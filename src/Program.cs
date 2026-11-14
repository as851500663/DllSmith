// ============================================================================
//  4DDiG DLL Fixer — 重建工程
//  文件：Program.cs  入口 / 单实例 / 全局异常 / 授权初始化
//  对应原程序：<PrivateImplementationDetails>::Main → App.OnStartup
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Threading;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using DllFix.Core;
using DllFix.Ui;

namespace DllFix
{
    public static class Program
    {
        private static Mutex _mutex;

        [STAThread]
        public static int Main(string[] args)
        {
            bool created;
            _mutex = new Mutex(true, "4DDiG_DLLFixer_Rebuild_SingleInstance", out created);
            if (!created)
            {
                MessageBox.Show("程序已在运行。", AppInfo.ProductName, MessageBoxButton.OK, MessageBoxImage.Information);
                return 0;
            }

            Paths.EnsureDir(Paths.LogDir);
            Log.Init();

            // 自检：--selftest <catalogUrl> 解析并下载单个对象，结果写入 selftest.txt
            if (args != null && args.Length >= 2 && args[0] == "--selftest")
            {
                return SelfTest(args[1]);
            }

            // 预览：--preview-update 单独显示更新下载进度窗（UI 核对用）
            if (args != null && args.Length >= 1 && args[0] == "--preview-update")
            {
                Application pv = new Application();
                pv.ShutdownMode = ShutdownMode.OnMainWindowClose;
                UpdateInfo fake = new UpdateInfo();
                fake.HasUpdate = true;
                fake.Version = "1.0.4";
                UpdateDialog dlg = new UpdateDialog(fake);
                dlg.SetProgress(38, 1084000, 2854000);
                dlg.Show();
                return pv.Run(dlg);
            }

            // 自检：--clean-cancel-test 验证清理能中断（沙盒目录，不动真实数据）
            if (args != null && args.Length >= 1 && args[0] == "--clean-cancel-test")
            {
                string sandbox = Path.Combine(Path.GetTempPath(), "DllSmithCancelTest");
                try { Directory.Delete(sandbox, true); } catch { }
                Directory.CreateDirectory(sandbox);
                for (int i = 0; i < 40; i++)
                {
                    string sub = Path.Combine(sandbox, "d" + i);
                    Directory.CreateDirectory(sub);
                    for (int j = 0; j < 200; j++)
                        File.WriteAllText(Path.Combine(sub, "f" + j + ".tmp"), new string('x', 4096));
                }
                int before = Directory.GetFiles(sandbox, "*", SearchOption.AllDirectories).Length;

                CleanItem it = new CleanItem();
                it.Name = "沙盒取消测试";
                it.Path = sandbox;
                it.Files = before;
                it.Selected = true;

                CleanEngine.CancelRequested = false;
                Thread t2 = new Thread(delegate()
                {
                    Thread.Sleep(120);
                    CleanEngine.CancelRequested = true;
                });
                t2.IsBackground = true;
                t2.Start();

                List<CleanItem> one = new List<CleanItem>();
                one.Add(it);
                long freed = CleanEngine.CleanCaches(one, null, null);
                bool cancelled = CleanEngine.CancelRequested;
                CleanEngine.CancelRequested = false;

                int after = 0;
                try { after = Directory.GetFiles(sandbox, "*", SearchOption.AllDirectories).Length; } catch { }

                string msg = string.Format("before={0} after={1} freed={2}KB cancelFlag={3} stoppedEarly={4}",
                    before, after, freed / 1024, cancelled, after > 0);
                Console.WriteLine(msg);
                File.WriteAllText(Path.Combine(Paths.LogDir, "clean-cancel-test.txt"), msg);
                try { Directory.Delete(sandbox, true); } catch { }
                return 0;
            }

            // 自检：--scan-report <outFile> 扫描并输出每个条目的判定（核对"哪些会被修复"）
            if (args != null && args.Length >= 2 && args[0] == "--scan-report")
            {
                return ScanReport(args[1]);
            }

            Paths.EnsureDir(Paths.LogDir);
            Paths.EnsureDir(Paths.DownloadDir);
            Log.Init();
            Log.Info("license=" + License.BadgeText + " machine=" + License.Current.Machine);

            Application app = new Application();
            app.ShutdownMode = ShutdownMode.OnMainWindowClose;
            app.DispatcherUnhandledException += delegate(object s, DispatcherUnhandledExceptionEventArgs e)
            {
                Log.Error("dispatcher unhandled", e.Exception);
                MessageBox.Show("发生未处理异常：" + e.Exception.Message + "\n\n详细信息已写入 Logs\\。",
                    AppInfo.ProductName, MessageBoxButton.OK, MessageBoxImage.Error);
                e.Handled = true;
            };
            AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e)
            {
                Log.Error("appdomain unhandled", e.ExceptionObject as Exception);
            };

            MainWindow window = new MainWindow();
            return app.Run(window);
        }

        /// <summary>
        /// 自检：--scan-report &lt;outFile&gt;
        /// 跑一遍完整扫描，把每个条目的判定写进文件并输出统计，用于核对"哪些项会被修复"。
        /// </summary>
        private static int ScanReport(string outFile)
        {
            List<CatalogItem> all = new List<CatalogItem>();
            all.AddRange(Catalog.LoadSystemDll(Environment.Is64BitOperatingSystem));
            all.AddRange(Catalog.LoadRuntime("运行库", "VC_Scan", Environment.Is64BitOperatingSystem));
            all.AddRange(Catalog.LoadRuntime("运行库", "VC_DLL_Scan", Environment.Is64BitOperatingSystem));
            all.AddRange(Catalog.LoadRuntime("DirectX", "DirectX", Environment.Is64BitOperatingSystem));
            all.AddRange(Catalog.LoadRuntime("DownLevel", "downlevel", Environment.Is64BitOperatingSystem));
            all.AddRange(Catalog.LoadRuntime("Network", "NETWORK", Environment.Is64BitOperatingSystem));

            bool wow = Wow64.Disable();
            try { Scanner.Scan(all, null); }
            finally { if (wow) Wow64.Enable(); }

            StringBuilder sb = new StringBuilder();
            StringBuilder head = new StringBuilder();
            int absent = 0, present = 0;
            Dictionary<string, int[]> perCat = new Dictionary<string, int[]>();
            foreach (CatalogItem it in all)
            {
                bool miss = it.State == ItemState.Absent;
                if (miss) absent++; else present++;
                if (!perCat.ContainsKey(it.Category)) perCat[it.Category] = new int[2];
                perCat[it.Category][miss ? 0 : 1]++;
                if (miss)
                    sb.AppendLine(string.Format("[缺失] {0,-8} {1,-30} arch={2} path={3}",
                        it.Category, it.Name, it.Arch, it.Where));
            }
            head.AppendLine("total=" + all.Count + " absent=" + absent + " present=" + present);
            foreach (KeyValuePair<string, int[]> kv in perCat)
                head.AppendLine("  " + kv.Key + ": absent=" + kv.Value[0] + " present=" + kv.Value[1]);
            head.AppendLine("---- 缺失明细 ----");
            File.WriteAllText(outFile, head.ToString() + sb.ToString(), Encoding.UTF8);
            Console.WriteLine(head.ToString());
            return 0;
        }

        /// <summary>
        /// 自检：--selftest <catalogUrl>
        /// 解析真实下载地址、下载到临时目录、识别文件头，结果写入 selftest.txt（winexe 无控制台）。
        /// </summary>
        private static int SelfTest(string catalogUrl)
        {
            StringBuilder sb = new StringBuilder();
            try
            {
                List<string> urls = ApiClient.Candidates(catalogUrl);
                sb.AppendLine("catalog : " + catalogUrl);
                sb.AppendLine("candidates: " + urls.Count);
                string tmp = Path.Combine(Path.GetTempPath(), "rebuild-selftest.bin");
                string used = null;
                Exception last = null;
                foreach (string u in urls)
                {
                    try
                    {
                        using (WebClient wc = new WebClient())
                        {
                            wc.Headers.Add("User-Agent", "4DDiG-Rebuild/1.0");
                            wc.DownloadFile(u, tmp);
                        }
                        used = u;
                        break;
                    }
                    catch (Exception ex) { last = ex; sb.AppendLine("fail    : " + u + " | " + ex.Message); }
                }
                if (used == null) throw last ?? new InvalidOperationException("no candidate worked");
                sb.AppendLine("used    : " + used);
                if (Payload.Normalize(tmp))
                {
                    sb.AppendLine("decrypt : ok (key=DDIG4SECUREKEY16, CBC, IV=前16字节)");
                }
                FileInfo fi = new FileInfo(tmp);
                sb.AppendLine("bytes   : " + fi.Length);
                using (FileStream fs = File.OpenRead(tmp))
                {
                    byte[] head = new byte[8];
                    fs.Read(head, 0, 8);
                    sb.AppendLine("header  : " + BitConverter.ToString(head));
                    bool pe = head[0] == 0x4D && head[1] == 0x5A;
                    bool sevenZip = head[0] == 0x37 && head[1] == 0x7A;
                    bool zip = head[0] == 0x50 && head[1] == 0x4B;
                    sb.AppendLine("format  : " + (pe ? "PE/MZ" : sevenZip ? "7z" : zip ? "zip" : "encrypted-or-unknown"));
                }
                File.WriteAllText(Path.Combine(Paths.BaseDir, "selftest.txt"), sb.ToString(), Encoding.UTF8);
                return 0;
            }
            catch (Exception ex)
            {
                sb.AppendLine("ERROR: " + ex.Message);
                try { File.WriteAllText(Path.Combine(Paths.BaseDir, "selftest.txt"), sb.ToString(), Encoding.UTF8); }
                catch { }
                return 1;
            }
        }
    }
}
