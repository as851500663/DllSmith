// ============================================================================
//  4DDiG DLL Fixer — 重建工程
//  文件：RepairEngine.cs  下载 / 解压 / 安装 / 覆盖 DLL（对应逆向报告 §8.3）
//  对应原程序：RepairHelper.bqEo9H2KhP（队列 worker）、Decompress、SetUpExe
// ============================================================================

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Threading;

namespace DllFix.Core
{
    public class RepairEngine
    {
        private const int MaxDownloadMinutes = 30;
        private const int InstallTimeoutMs = 15 * 60 * 1000;

        public bool DryRun { get; set; }

        /// <summary>只下载不安装（对应"系统文件下载"页）</summary>
        public bool DownloadOnly { get; set; }

        public int Success { get; private set; }
        public int Failed { get; private set; }

        /// <summary>单项进度：item, 百分比, 描述</summary>
        public event Action<CatalogItem, int, string> ItemProgress;

        /// <summary>整体进度：已完成项数, 总项数</summary>
        public event Action<int, int> Overall;

        public void Run(IList<CatalogItem> items, CancellationToken token)
        {
            Success = 0;
            Failed = 0;
            Paths.EnsureDir(Paths.DownloadDir);

            int index = 0;
            foreach (CatalogItem item in items)
            {
                if (token.IsCancellationRequested) break;
                index++;
                try
                {
                    RepairOne(item, token);
                    Success++;
                }
                catch (OperationCanceledException)
                {
                    item.State = ItemState.Unknown;
                    item.Detail = "已取消";
                    break;
                }
                catch (Exception ex)
                {
                    Failed++;
                    item.State = ItemState.Failed;
                    item.Detail = ex.Message;
                    Log.Error("repair failed: " + item.Title, ex);
                }
                if (Overall != null) Overall(index, items.Count);
            }
        }

        private void RepairOne(CatalogItem item, CancellationToken token)
        {
            Log.Info("repair [" + item.Category + "] " + item.Title + " <- " + item.Download);

            if (DryRun)
            {
                item.State = ItemState.Done;
                item.Detail = "模拟模式：未实际下载/安装";
                Report(item, 100, item.Detail);
                return;
            }

            if (string.IsNullOrEmpty(item.Download))
                throw new InvalidOperationException("条目没有下载地址");

            string local = Path.Combine(Paths.DownloadDir, SafeFileName(item.Download));
            if (!File.Exists(local) || new FileInfo(local).Length == 0)
            {
                DownloadWithFallback(item, local, token);
                Payload.Normalize(local);
            }
            else
                Report(item, 100, "已存在缓存：" + Path.GetFileName(local));

            token.ThrowIfCancellationRequested();

            if (DownloadOnly)
            {
                item.State = ItemState.Done;
                item.Detail = "已下载：" + Path.GetFileName(local);
                Report(item, 100, item.Detail);
                return;
            }

            switch (item.Kind)
            {
                case ItemKind.SystemDll:
                    ReplaceDll(item, local);
                    break;
                case ItemKind.Installer:
                    RunInstaller(item, local);
                    break;
                case ItemKind.Archive:
                    Extract(item, local);
                    break;
                default:
                    throw new InvalidOperationException("未知条目类型");
            }

            item.State = ItemState.Done;
            item.Detail = "修复完成";
            Report(item, 100, item.Detail);
        }

        // ── 下载（对应 TS.Downloader 多线程下载；此处用 WebClient 分块进度） ──
        private void Download(CatalogItem item, string url, string local, CancellationToken token)
        {
            item.State = ItemState.Downloading;
            item.Detail = "下载中";
            Report(item, 0, "开始下载");

            using (WebClient wc = new WebClient())
            {
                wc.Headers.Add("User-Agent", "Mozilla/5.0 4DDiG-Rebuild/1.0");
                DateTime start = DateTime.UtcNow;

                wc.DownloadProgressChanged += delegate(object s, DownloadProgressChangedEventArgs e)
                {
                    if ((DateTime.UtcNow - start).TotalMinutes > MaxDownloadMinutes)
                    {
                        try { wc.CancelAsync(); } catch { }
                    }
                    double mb = e.BytesReceived / 1048576.0;
                    double total = e.TotalBytesToReceive > 0 ? e.TotalBytesToReceive / 1048576.0 : 0;
                    Report(item, e.ProgressPercentage,
                        string.Format("下载中 {0:F1}/{1:F1} MB", mb, total));
                };

                string tmp = local + ".part";
                try
                {
                    wc.DownloadFile(new Uri(url), tmp);
                }
                finally
                {
                    if (File.Exists(tmp))
                    {
                        if (new FileInfo(tmp).Length > 0)
                        {
                            if (File.Exists(local)) File.Delete(local);
                            File.Move(tmp, local);
                        }
                        else
                        {
                            try { File.Delete(tmp); } catch { }
                        }
                    }
                }
            }
            token.ThrowIfCancellationRequested();
        }

        // 依次尝试候选地址（原 CDN → OSS 签名地址），首个成功的即采用
        private void DownloadWithFallback(CatalogItem item, string local, CancellationToken token)
        {
            List<string> urls = ApiClient.Candidates(item.Download);
            Exception last = null;
            foreach (string u in urls)
            {
                if (token.IsCancellationRequested) throw new OperationCanceledException();
                try
                {
                    Log.Info("download try: " + u);
                    Download(item, u, local, token);
                    return;
                }
                catch (Exception ex)
                {
                    last = ex;
                    Log.Info("download failed (" + ex.Message + "): " + u);
                }
            }
            if (last != null) throw last;
            throw new InvalidOperationException("没有可用的下载地址");
        }
        // ── 覆盖系统 DLL（需要管理员 + WOW64 重定向处理） ──────────────────
        private void ReplaceDll(CatalogItem item, string local)
        {
            item.State = ItemState.Installing;
            Report(item, 100, "安装中");

            string target = Paths.ResolveX(item.FilePath);
            if (string.IsNullOrEmpty(target))
                throw new InvalidOperationException("条目缺少目标路径");

            string dir = Path.GetDirectoryName(target);
            Paths.EnsureDir(dir);

            if (!SystemProbe.IsElevated)
                throw new InvalidOperationException("覆盖 " + target + " 需要管理员权限（请以管理员身份重新启动）");

            bool disabled = false;
            try
            {
                disabled = Wow64.Disable();
                if (File.Exists(target))
                {
                    string backup = target + ".rebuild.bak";
                    if (!File.Exists(backup))
                    {
                        try { File.Copy(target, backup, true); }
                        catch (Exception ex) { Log.Error("backup failed: " + target, ex); }
                    }
                    try { File.SetAttributes(target, FileAttributes.Normal); } catch { }
                }
                File.Copy(local, target, true);
                Log.Info("dll replaced: " + target);
            }
            finally
            {
                if (disabled) Wow64.Enable();
            }
        }

        // ── 运行安装包（对应 SetUpExe） ────────────────────────────────────
        private void RunInstaller(CatalogItem item, string local)
        {
            item.State = ItemState.Installing;
            string args = string.IsNullOrEmpty(item.CommandLine) ? "/q /norestart" : item.CommandLine;
            Report(item, 100, "安装中：" + Path.GetFileName(local) + " " + args);

            ProcessStartInfo psi = new ProcessStartInfo(local, args);
            psi.CreateNoWindow = true;
            psi.UseShellExecute = false;
            psi.WindowStyle = ProcessWindowStyle.Hidden;

            using (Process p = Process.Start(psi))
            {
                if (p == null) throw new InvalidOperationException("安装器启动失败");
                if (!p.WaitForExit(InstallTimeoutMs))
                {
                    try { p.Kill(); } catch { }
                    throw new TimeoutException("安装超时：" + Path.GetFileName(local));
                }
                int code = p.ExitCode;
                Log.Info("installer exit=" + code + " " + Path.GetFileName(local));
                // 0=成功 1641=已启动重启 3010=需重启
                if (code != 0 && code != 1641 && code != 3010)
                    throw new InvalidOperationException("安装器返回 " + code);
            }
        }

        // ── 解压（对应 Decompress：调用随包 7z.exe） ───────────────────────
        private void Extract(CatalogItem item, string local)
        {
            item.State = ItemState.Installing;
            string outDir = Path.Combine(Paths.DownloadDir,
                Path.GetFileNameWithoutExtension(local));
            Paths.EnsureDir(outDir);
            Report(item, 100, "解压到 " + outDir);

            bool ok = SevenZip.Extract(local, outDir);
            if (!ok)
            {
                // 兜底：.zip 用 BCL 解
                if (local.ToLowerInvariant().EndsWith(".zip"))
                {
                    try
                    {
                        System.IO.Compression.ZipFile.ExtractToDirectory(local, outDir);
                        ok = true;
                    }
                    catch (Exception ex) { Log.Error("zip extract failed", ex); }
                }
                if (!ok) throw new InvalidOperationException("解压失败：" + Path.GetFileName(local));
            }
        }

        private void Report(CatalogItem item, int percent, string message)
        {
            item.Percent = percent;
            item.Detail = message;
            Action<CatalogItem, int, string> h = ItemProgress;
            if (h != null) { try { h(item, percent, message); } catch { } }
        }

        private static string SafeFileName(string url)
        {
            string name;
            try { name = Path.GetFileName(new Uri(url).LocalPath); }
            catch { name = Path.GetFileName(url); }
            foreach (char c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            return name;
        }
    }

    // ── 7z 封装 ────────────────────────────────────────────────────────────
    public static class SevenZip
    {
        public static bool Available
        {
            get { return File.Exists(Paths.SevenZipExe); }
        }

        public static bool Extract(string archive, string outDir)
        {
            if (!Available)
            {
                Log.Info("7z.exe 不存在，跳过解压：" + archive);
                return false;
            }
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(Paths.SevenZipExe,
                    "x \"" + archive + "\" -o\"" + outDir + "\" -y");
                psi.CreateNoWindow = true;
                psi.UseShellExecute = false;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;

                using (Process p = Process.Start(psi))
                {
                    string err = p.StandardError.ReadToEnd();
                    p.StandardOutput.ReadToEnd();
                    p.WaitForExit(10 * 60 * 1000);
                    if (p.ExitCode != 0) Log.Info("7z exit=" + p.ExitCode + " " + err);
                    return p.ExitCode == 0;
                }
            }
            catch (Exception ex)
            {
                Log.Error("7z failure", ex);
                return false;
            }
        }
    }

    // ── WOW64 文件系统重定向（对应 SystemManageHelper.DisableWow64FsRedirection） ──
    public static class Wow64
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool Wow64DisableWow64FsRedirection(out IntPtr oldValue);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool Wow64RevertWow64FsRedirection(IntPtr oldValue);

        [DllImport("kernel32.dll")]
        private static extern bool IsWow64Process(IntPtr hProcess, out bool wow64);

        private static IntPtr _old = IntPtr.Zero;

        public static bool Disable()
        {
            if (!Environment.Is64BitOperatingSystem) return false;
            try { return Wow64DisableWow64FsRedirection(out _old); }
            catch { return false; }
        }

        public static void Enable()
        {
            try { Wow64RevertWow64FsRedirection(_old); }
            catch { }
        }
    }
}
