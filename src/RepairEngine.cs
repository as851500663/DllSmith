// ============================================================================
// 4DDiG DLL Fixer — 重建工程
//  文件：RepairEngine.cs  下载 / 校验 / 解压 / 安装 / 覆盖 DLL（对应逆向报告 §8.3）
//
//  1.4.0 安全收口（审计 A-03 / A-04 / A-05 / A-08 / A-13）：
//    · 目录必须来自签名 manifest（Trusted）才允许修复；否则整页只读
//    · 下载：oss_key 优先、短超时快速失败、只允许 http/https、禁私有地址
//    · 解密后按 manifest 明文 SHA256 逐字节校验；无哈希条目允许修复但标记「哈希未覆盖」
//    · 缓存不再按文件名信任：命中缓存也要重算哈希，不匹配即丢弃重下
//    · FILEPATH 走 PathGuard 白名单 + 重解析点检查；覆盖前留档 .rebuild.bak 并登记
//    · WOW64 重定向改成实例级 using 作用域（消除静态 _old 并发竞态）
//    · 覆盖前校验 PE 头与架构（.dll 才落盘，架构不符直接拒绝）
// ============================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Threading;

namespace DllFix.Core
{
    public class RepairEngine
    {
        private const int InstallTimeoutMs = 15 * 60 * 1000;

        public bool DryRun { get; set; }

        /// <summary>只下载不安装（对应"系统文件下载"页）</summary>
        public bool DownloadOnly { get; set; }

        /// <summary>目录信任状态；CanRepair == false 时整个引擎拒绝工作</summary>
        public CatalogTrust Trust { get; set; }

        /// <summary>
        /// 演练目标目录（--payload-rehearse 用）：非空时 ReplaceDll 改为把明文写到该目录，
        /// 不碰系统盘、不要求提权，也不走 PathGuard 目标白名单——只验证"下载→解密→落盘"链路。
        /// </summary>
        public string RehearseDir { get; set; }

        /// <summary>UI 线程封送（页面注入；不注入则原地执行，测试用）</summary>
        public Action<Action> Post { get; set; }

        public int Success { get; private set; }
        public int Failed { get; private set; }
        public int Skipped { get; private set; }

        /// <summary>单项进度：item, 百分比, 描述</summary>
        public event Action<CatalogItem, int, string> ItemProgress;

        /// <summary>整体进度：已完成项数, 总项数</summary>
        public event Action<int, int> Overall;

        public void Run(IList<CatalogItem> items, CancellationToken token)
        {
            Success = 0;
            Failed = 0;
            Skipped = 0;

            if (Trust != null && !Trust.CanRepair)
            {
                Log.Error("repair refused: catalog not trusted (" + Trust.Reason + ")", null);
                throw new InvalidOperationException(
                    "目录数据未通过签名校验，当前为只读模式：可以扫描，但不能修复。\n原因：" + Trust.Reason);
            }

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
                    SetItem(item, ItemState.Unknown, "已取消", 0);
                    Skipped++;
                    break;
                }
                catch (Exception ex)
                {
                    Failed++;
                    SetItem(item, ItemState.Failed, ex.Message, 0);
                    Log.Error("repair failed: " + item.Title, ex);
                }
                if (Overall != null) Overall(index, items.Count);
            }
        }

        private void RepairOne(CatalogItem item, CancellationToken token)
        {
            Log.Info("repair [" + item.Category + "] " + item.Title
                     + " hash=" + (item.HashCovered ? item.Sha256.Substring(0, 12) + "…" : "(未覆盖)")
                     + " <- " + ApiClient.SafeUrl(item.Download));

            if (DryRun)
            {
                SetItem(item, ItemState.Done, "模拟模式：未实际下载/安装", 100);
                return;
            }

            // 目标校验放在最前面：指向 %TEMP% / 非白名单 / 系统保留目录的条目
            // 必须在**发出任何下载请求之前**被拒绝（QA t6：不要先下 3MB 再拒）。
            if (!DownloadOnly && item.Kind == ItemKind.SystemDll)
            {
                string err0 = PathGuard.CheckDllTarget(Paths.ResolveX(item.FilePath));
                if (err0 != null) throw new InvalidOperationException("目标被拒绝：" + err0);
            }

            if (string.IsNullOrEmpty(item.Download) && string.IsNullOrEmpty(item.OssKey))
                throw new InvalidOperationException("条目没有下载地址");

            string local = CachePathFor(item);
            bool cacheUsable = false;
            if (File.Exists(local) && new FileInfo(local).Length > 0)
            {
                if (item.HashCovered)
                {
                    string h = SafeHash(local);
                    if (string.Equals(h, item.Sha256, StringComparison.OrdinalIgnoreCase))
                    {
                        cacheUsable = true;
                        Report(item, 100, "命中缓存且哈希一致");
                    }
                    else
                    {
                        Log.Info("cache hash mismatch, discard: " + Path.GetFileName(local));
                        try { File.Delete(local); } catch { }
                    }
                }
                else
                {
                    // 没有哈希就无法确认缓存内容可信 -> 不复用（A-03）
                    Log.Info("cache ignored (no hash available): " + Path.GetFileName(local));
                    try { File.Delete(local); } catch { }
                }
            }

            if (!cacheUsable)
            {
                DownloadWithFallback(item, local, token);
            }

            // t21（数据损坏级）：格式判定失败必须中止该项。
            // 旧代码 `Payload.Normalize(local);` 忽略返回值 → 把 AES 密文当明文覆盖进 System32\*.msc。
            EnsurePayloadUsable(local, item.FilePath);

            token.ThrowIfCancellationRequested();

            // 明文口径校验（与 manifest 一致）
            if (item.HashCovered)
            {
                string got = SafeHash(local);
                if (!string.Equals(got, item.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    try { File.Delete(local); } catch { }
                    throw new InvalidOperationException(
                        "下载内容哈希不匹配（期望 " + item.Sha256.Substring(0, 12) + "…，实际 "
                        + (got.Length >= 12 ? got.Substring(0, 12) + "…" : got) + "），已拒绝落地");
                }
                Report(item, 100, "SHA256 校验通过");
            }
            else
            {
                Report(item, 100, "哈希未覆盖：本次未做内容校验");
            }

            if (DownloadOnly)
            {
                SetItem(item, ItemState.Done, "已下载：" + Path.GetFileName(local), 100);
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

            SetItem(item, ItemState.Done, item.HashCovered ? "修复完成（已校验）" : "修复完成（哈希未覆盖）", 100);
        }

        private static string CachePathFor(CatalogItem item)
        {
            string seed = string.IsNullOrEmpty(item.Download) ? item.FileName : item.Download;
            string name = SafeFileName(seed);
            if (string.IsNullOrEmpty(name)) name = SafeFileName(item.FileName);
            // 用哈希前缀区分同名不同内容的条目，避免互相覆盖
            string prefix = item.HashCovered ? item.Sha256.Substring(0, 8) + "_" : "";
            return Path.Combine(Paths.DownloadDir, prefix + name);
        }

        private static string SafeHash(string file)
        {
            try { return Hashes.Sha256HexOfFile(file); }
            catch { return ""; }
        }

        // ── 下载：逐块读 + 可取消 + 短超时 + 候选回退 ───────────────────────
        private void DownloadWithFallback(CatalogItem item, string local, CancellationToken token)
        {
            List<string> urls = ApiClient.Candidates(item);
            if (urls.Count == 0)
                throw new InvalidOperationException("没有可用的下载地址（oss_key 与 DOWNLOAD 均不可用）");

            Exception last = null;
            foreach (string u in urls)
            {
                if (token.IsCancellationRequested) throw new OperationCanceledException();
                try
                {
                    Log.Info("download try: " + ApiClient.SafeUrl(u));
                    Download(item, u, local, token);
                    return;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    last = ex;
                    Log.Info("download failed (" + ex.Message + "): " + ApiClient.SafeUrl(u));
                }
            }
            if (last != null) throw last;
            throw new InvalidOperationException("没有可用的下载地址");
        }

        private void Download(CatalogItem item, string url, string local, CancellationToken token)
        {
            if (!ApiClient.IsFetchableUrl(url))
                throw new InvalidOperationException("下载地址被拒绝（只允许 http/https，且不允许私有地址）");

            SetItem(item, ItemState.Downloading, "下载中", 0);
            Report(item, 0, "开始下载");

            string tmp = local + ".part";
            try
            {
                HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
                req.UserAgent = "DllSmith/" + AppInfo.Version;
                req.Timeout = ApiClient.ProbeTimeoutMs;         // 首字节快速失败
                req.ReadWriteTimeout = 30000;
                req.AllowAutoRedirect = true;

                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                using (Stream net = resp.GetResponseStream())
                using (FileStream fs = File.Create(tmp))
                {
                    long total = resp.ContentLength;
                    byte[] buf = new byte[65536];
                    long got = 0;
                    int n;
                    while ((n = net.Read(buf, 0, buf.Length)) > 0)
                    {
                        if (token.IsCancellationRequested) throw new OperationCanceledException();
                        fs.Write(buf, 0, n);
                        got += n;
                        if (total > 0)
                        {
                            int pct = (int)(got * 100L / total);
                            Report(item, pct, string.Format("下载中 {0:0.0}/{1:0.0} MB",
                                got / 1048576.0, total / 1048576.0));
                        }
                    }
                    // 注意：ReadWriteTimeout 必须在 GetResponse 之前设置。
                    // 请求提交后再赋值会抛 net_reqsubmitted（"无法在提交请求后执行此操作"），
                    // 导致每次修复下载都在读完数据后失败（QA t6 P0-2）。
                    // 见上方 req 初始化处的 req.ReadWriteTimeout = 30000;
                }

                if (File.Exists(local)) File.Delete(local);
                File.Move(tmp, local);
            }
            finally
            {
                if (File.Exists(tmp))
                {
                    try
                    {
                        if (new FileInfo(tmp).Length == 0 || token.IsCancellationRequested) File.Delete(tmp);
                        else { if (File.Exists(local)) File.Delete(local); File.Move(tmp, local); }
                    }
                    catch { }
                }
            }
            token.ThrowIfCancellationRequested();
        }

        /// <summary>
        /// 载荷可用性守卫（t21）：Normalize 结果不是"可用明文"就抛异常，调用方据此中止该项。
        /// RepairOne 与 --payload-selftest 共用这一个判定，避免两处逻辑漂移。
        /// </summary>
        public static void EnsurePayloadUsable(string local, string targetPath)
        {
            PayloadResult payload = Payload.Normalize(local, targetPath);
            if (payload == PayloadResult.UnknownFormat)
            {
                throw new InvalidOperationException(
                    "未知格式，已跳过（解密后既不是 PE/压缩包，也不在目标扩展名白名单内："
                    + (targetPath == null ? "?" : Path.GetFileName(targetPath)) + "）");
            }
            if (payload == PayloadResult.NotEncryptable || payload == PayloadResult.Failed)
            {
                throw new InvalidOperationException(
                    "载荷不可用（不是明文，也不是可解密的 _encrypt 信封）：" + Path.GetFileName(local));
            }
        }
        // ── 覆盖系统 DLL（管理员 + 白名单 + 留档回滚 + PE/架构校验） ─────────
        private void ReplaceDll(CatalogItem item, string local)
        {
            SetItem(item, ItemState.Installing, "安装中", 100);

            string target = Paths.ResolveX(item.FilePath);

            // 演练模式：写到 RehearseDir（临时目录），不写系统盘
            if (!string.IsNullOrEmpty(RehearseDir))
            {
                string name = Path.GetFileName(target);
                if (string.IsNullOrEmpty(name)) name = Path.GetFileName(local);
                Paths.EnsureDir(RehearseDir);
                string dst = Path.Combine(RehearseDir, name);
                File.Copy(local, dst, true);
                // 演练同样走审计行（result=rehearsed），这样"非 .dll 覆盖写"的日志格式有真实样例可核对
                if (!string.Equals(Path.GetExtension(target), ".dll", StringComparison.OrdinalIgnoreCase))
                    AuditNonDllWrite(item, dst, local, "rehearsed");
                Log.Info("rehearsal wrote: " + dst);
                return;
            }

            string denied = PathGuard.CheckDllTarget(target);
            if (denied != null) throw new InvalidOperationException("目标路径被拒绝：" + denied);

            if (!SystemProbe.IsElevated)
                throw new InvalidOperationException("覆盖 " + target + " 需要管理员权限（请以管理员身份重新启动）");

            string peError = PeCheck(local, item.Arch);
            if (peError != null) throw new InvalidOperationException("下载内容不可用：" + peError);

            string dir = Path.GetDirectoryName(target);
            Paths.EnsureDir(dir);
            // Program Files / AppData 下的目标必须是"签名清单登记过的目录"：
            // 条目的 FILEPATH 已由签名 manifest 校验过，这里把通过的目录登记进白名单。
            PathGuard.Register(dir);

            using (new Wow64Scope())
            {
                string backup = null;
                if (File.Exists(target))
                {
                    backup = BackupStore.Create(target);
                    try { File.SetAttributes(target, FileAttributes.Normal); } catch { }
                }
                // 非 .dll 目标（.msc/.dls/.mui/.cpl/.com/.rll）覆盖写留审计行，便于事后追溯（t23）
                bool nonDll = !string.Equals(Path.GetExtension(target), ".dll", StringComparison.OrdinalIgnoreCase);
                try
                {
                    File.Copy(local, target, true);
                    if (nonDll) AuditNonDllWrite(item, target, local, "ok");
                    Log.Info("dll replaced: " + target + (backup == null ? "" : "  (backup: " + backup + ")"));
                }
                catch (Exception ex)
                {
                    if (nonDll) AuditNonDllWrite(item, target, local, "failed: " + ex.GetType().Name);
                    throw;
                }
            }
        }

        /// <summary>
        /// 非 .dll 覆盖写审计（t23）：目标路径 / 来源 catalog / 明文 sha256 前 12 位 / 结果，
        /// 一行一条写进日志，事后可按文件追溯"谁在什么时候往系统目录写了非 DLL 文件"。
        /// </summary>
        private static void AuditNonDllWrite(CatalogItem item, string target, string local, string result)
        {
            string sha12 = "";
            try
            {
                string full = Hashes.Sha256HexOfFile(local);
                sha12 = full.Length >= 12 ? full.Substring(0, 12) : full;
            }
            catch { sha12 = "(hash-error)"; }
            string src = (item == null ? "?" : item.Category + "/" + (item.SourceKey ?? "-"));
            Log.Info("AUDIT non-dll write | target=" + target
                     + " | catalog=" + src
                     + " | sha256=" + sha12
                     + " | result=" + result);
        }

        /// <summary>PE 头 + 架构校验：拒绝非 PE、架构不符、0 字节</summary>
        private static string PeCheck(string file, string expectedArch)
        {
            try
            {
                FileInfo fi = new FileInfo(file);
                if (fi.Length < 0x40) return "文件过小，不是有效 PE";
                using (FileStream fs = File.OpenRead(file))
                {
                    byte[] head = new byte[0x200];
                    int n = fs.Read(head, 0, head.Length);
                    if (n < 0x40 || head[0] != 0x4D || head[1] != 0x5A) return "不是 PE/MZ 文件";
                    int peoff = BitConverter.ToInt32(head, 0x3C);
                    if (peoff <= 0 || peoff + 6 >= n) return "PE 头偏移非法";
                    if (!(head[peoff] == 0x50 && head[peoff + 1] == 0x45)) return "缺少 PE\\0\\0 签名";
                    int machine = BitConverter.ToUInt16(head, peoff + 4);
                    string arch = machine == 0x8664 ? "x64" : (machine == 0x14C ? "x86" : (machine == 0xAA64 ? "arm64" : "0x" + machine.ToString("X4")));
                    if (!string.IsNullOrEmpty(expectedArch) && expectedArch != "—"
                        && (arch == "x64" || arch == "x86") && expectedArch != arch)
                        return "架构不符（条目 " + expectedArch + "，文件 " + arch + "）";
                }
                return null;
            }
            catch (Exception ex) { return ex.Message; }
        }

        // ── 运行安装包（对应 SetUpExe） ────────────────────────────────────
        private void RunInstaller(CatalogItem item, string local)
        {
            SetItem(item, ItemState.Installing, "安装中", 100);
            string args = string.IsNullOrEmpty(item.CommandLine) ? "/q /norestart" : item.CommandLine;
            Report(item, 100, "安装中：" + Path.GetFileName(local));

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
                if (code != 0 && code != 1641 && code != 3010)
                    throw new InvalidOperationException("安装器返回 " + code);
            }
        }

        // ── 解压（调用随包 7z.exe，绝对路径 + 参数数组，不经 cmd 拼接） ────
        private void Extract(CatalogItem item, string local)
        {
            SetItem(item, ItemState.Installing, "解压中", 100);
            string outDir = Path.Combine(Paths.DownloadDir, Path.GetFileNameWithoutExtension(local));
            Paths.EnsureDir(outDir);
            Report(item, 100, "解压到 " + outDir);

            bool ok = SevenZip.Extract(local, outDir);
            if (!ok && local.ToLowerInvariant().EndsWith(".zip"))
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

        // ── UI 封送：所有模型写操作都过 Post（页面注入 Dispatch） ────────────
        private void SetItem(CatalogItem item, ItemState state, string detail, int percent)
        {
            Action a = delegate()
            {
                item.State = state;
                item.Detail = detail;
                if (percent > 0) item.Percent = percent;
                else item.Percent = 0;
            };
            RunOnUi(a);
        }

        private void Report(CatalogItem item, int percent, string message)
        {
            Action a = delegate()
            {
                item.Percent = percent;
                item.Detail = message;
                Action<CatalogItem, int, string> h = ItemProgress;
                if (h != null) { try { h(item, percent, message); } catch { } }
            };
            RunOnUi(a);
        }

        private void RunOnUi(Action a)
        {
            Action<Action> post = Post;
            if (post == null) { try { a(); } catch { } return; }
            try { post(a); } catch { }
        }

        private static string SafeFileName(string url)
        {
            string name;
            try { name = Path.GetFileName(new Uri(url).LocalPath); }
            catch { name = Path.GetFileName(url); }
            if (string.IsNullOrEmpty(name)) name = "download.bin";
            foreach (char c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            return name;
        }
    }

    // ── 7z 封装（绝对路径，防 PATH 劫持） ──────────────────────────────────
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
                ProcessStartInfo psi = new ProcessStartInfo(Paths.SevenZipExe);
                psi.Arguments = "x " + ProcArgs.Quote(archive) + " -o" + ProcArgs.Quote(outDir) + " -y";
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
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

    // ── WOW64 文件系统重定向（线程级状态，消除静态 _old 并发竞态） ──────────
    public static class Wow64
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool Wow64DisableWow64FsRedirection(out IntPtr oldValue);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool Wow64RevertWow64FsRedirection(IntPtr oldValue);

        // ThreadStatic：每个线程各存各的旧值，A 线程的 Disable 不会被 B 线程覆盖
        [ThreadStatic]
        private static IntPtr _old;

        /// <summary>兼容旧调用点：返回 true 表示确实关闭了重定向（需配对 Enable）</summary>
        public static bool Disable()
        {
            if (!Environment.Is64BitOperatingSystem) return false;
            IntPtr old;
            try
            {
                if (!Wow64DisableWow64FsRedirection(out old)) return false;
            }
            catch { return false; }
            _old = old;
            return true;
        }

        public static void Enable()
        {
            IntPtr old = _old;
            _old = IntPtr.Zero;
            if (old == IntPtr.Zero) return;
            try { Wow64RevertWow64FsRedirection(old); } catch { }
        }

        internal static bool DisableNative(out IntPtr old)
        {
            old = IntPtr.Zero;
            if (!Environment.Is64BitOperatingSystem) return false;
            try { return Wow64DisableWow64FsRedirection(out old); }
            catch { return false; }
        }

        internal static void RevertNative(IntPtr old)
        {
            if (old == IntPtr.Zero) return;
            try { Wow64RevertWow64FsRedirection(old); } catch { }
        }
    }

    /// <summary>
    /// 实例级 WOW64 重定向作用域：using 结束即还原，不存在静态 _old 被并发覆盖的问题。
    /// </summary>
    public sealed class Wow64Scope : IDisposable
    {
        private readonly IntPtr _old;
        private readonly bool _active;

        public Wow64Scope()
        {
            IntPtr old;
            _active = Wow64.DisableNative(out old);
            _old = old;
        }

        public bool Active { get { return _active; } }

        public void Dispose()
        {
            if (_active) Wow64.RevertNative(_old);
        }
    }
}
