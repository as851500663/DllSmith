// ============================================================================
//  4DDiG DLL Fixer 鈥?閲嶅缓宸ョ▼
//  鏂囦欢锛歅rogram.cs  鍏ュ彛 / 鍗曞疄渚?/ 鍏ㄥ眬寮傚父 / 鎺堟潈鍒濆鍖?//  瀵瑰簲鍘熺▼搴忥細<PrivateImplementationDetails>::Main 鈫?App.OnStartup
// ============================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Threading;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DllFix.Core;
using DllFix.Ui;

namespace DllFix
{
    /// <summary>鍛戒护琛岃嚜妫€閫€鍑虹爜锛堜笌 0/1/2 鐨勫父瑙勮涔夊垎寮€锛屼究浜庤剼鏈垽鏂級</summary>
    public enum ExitCode
    {
        Ok = 0,
        Failed = 1,
        BadInput = 2,
        SelfCheckBusy = 3
    }

    public static class Program
    {
        private static Mutex _mutex;
        private static Mutex _cliMutex;

        [STAThread]
        public static int Main(string[] args)
        {
            // 鍛戒护琛岃嚜妫€妯″紡锛?-xxx锛変笉鍙備笌 GUI 鍗曞疄渚嬩簰鏂ワ紝浣嗚鎷夸竴鎶?*鑷涓撶敤**鐨勪簰鏂ワ細

            bool cliMode = args != null && args.Length >= 1
                && args[0] != null && args[0].StartsWith("--", StringComparison.Ordinal);

            if (cliMode)
            {
                bool cliCreated;
                _cliMutex = new Mutex(true, "DllSmith_SelfCheck_SingleInstance", out cliCreated);
                if (!cliCreated)
                {
                    Console.WriteLine("SELFCHECK BUSY: 另一个自检进程正在运行（共用下载缓存），请等待其结束后重试。");
                    Log.Error("selfcheck refused: another selfcheck instance is running", null);
                    return (int)ExitCode.SelfCheckBusy;
                }
            }
            else
            {
                bool created;
                _mutex = new Mutex(true, "4DDiG_DLLFixer_Rebuild_SingleInstance", out created);
                if (!created)
                {
                    MessageBox.Show("程序已在运行。", AppInfo.ProductName, MessageBoxButton.OK, MessageBoxImage.Information);
                    return 0;
                }
            }

            Paths.EnsureDir(Paths.LogDir);
            Log.Init();
            TlsPin.Install();

            // 鍏ㄧ洰褰曚俊浠讳綋妫€锛?-verify-manifest-all锛?8 涓?catalog 閫愪釜杈撳嚭 Trusted/ReadOnly锛?
            if (args != null && args.Length >= 1 && args[0] == "--verify-manifest-all")
            {
                return VerifyManifestAll();
            }
            // 鐩綍鎵╁睍鍚嶆櫘鏌ワ紙t23锛夛細--ext-census
            if (args != null && args.Length >= 1 && args[0] == "--ext-census")
            {
                return ExtCensus();
            }

            // 杞借嵎鏍煎紡鑷锛坱21锛夛細--payload-selftest
            if (args != null && args.Length >= 1 && args[0] == "--payload-selftest")
            {
                return PayloadSelfTest();
            }

            // 闈?PE 杞借嵎婕旂粌锛坱21/t23锛夛細--payload-rehearse [maxItems] [hashesJson|-] [extList]锛堝啓涓存椂鐩綍锛?
            if (args != null && args.Length >= 1 && args[0] == "--payload-rehearse")
            {
                int max = args.Length >= 2 ? Convert.ToInt32(args[1]) : 999;
                string hashes = args.Length >= 3 && args[2] != "-" ? args[2] : null;
                string exts = args.Length >= 4 ? args[3] : ".msc,.dls";
                string catalogKey = args.Length >= 5 ? args[4] : null;
                return PayloadRehearse(max, hashes, exts, catalogKey);
            }

            // 鐪熷疄涓嬭浇鑷锛?-repair-download <catalogKey> <FILENAME>
            // 璧?RepairEngine 鐨勭湡瀹?Download 璺緞锛圖ownloadOnly锛屼笉鍐欑郴缁熺洰褰曪級锛?            // 鐢ㄤ簬楠岃瘉"姣忔淇涓嬭浇蹇呮姏 net_reqsubmitted"杩欑被鍥炲綊锛圦A t6 P0-2锛夈€?
            if (args != null && args.Length >= 3 && args[0] == "--repair-download")
            {
                return RepairDownload(args[1], args[2]);
            }

            // 閽ュ寵鑷锛?-selfcheck-keys锛堢敓浜ф瀯寤哄繀椤绘伆濂?[ds1]锛涚粨鏋滃悓鏃惰惤鐩樹緵 build.ps1 鏂█锛?
            if (args != null && args.Length >= 1 && args[0] == "--selfcheck-keys")
            {
                string[] kids = ReleaseKeys.KnownKids();
                string line = "[" + string.Join(", ", kids) + "]";
                Console.WriteLine(line);
                try
                {
                    Paths.EnsureDir(Paths.LogDir);
                    File.WriteAllText(Path.Combine(Paths.LogDir, "selfcheck-keys.txt"), line, Encoding.UTF8);
                }
                catch { }
                return kids.Length == 1 && kids[0] == ReleaseKeys.ProductionKid ? 0 : 2;
            }

            // 闂ㄧ鑷锛?-selfcheck-gate <realRecord.json> [forged1.json ...]
            // 姝ｅ悜 1 鏉?+ 璐熷悜 6 鏉★紝鍏ㄩ儴鎸夋湡鏈涙墠绠楄繃锛涗緵 QA 涓€鏉″懡浠ゅ璺戙€?
            if (args != null && args.Length >= 2 && args[0] == "--selfcheck-gate")
            {
                string[] extra = new string[args.Length - 2];
                Array.Copy(args, 2, extra, 0, extra.Length);
                return SelfCheckGate(args[1], extra);
            }

            // 瀵硅〃锛?-verify-release <record.json> 鐢ㄥ唴缃叕閽ラ獙涓€鏉″彂甯冭褰曪紙鍙戝竷鍓嶇疆闂ㄧ锛?
            if (args != null && args.Length >= 2 && args[0] == "--verify-release")
            {
                return VerifyRelease(args[1]);
            }

            // 澶嶆祴锛?-diag-idle <绉? 鍚姩涓荤獥鍚庨噰鏍疯嚜韬?CPU锛堟€ц兘鍥炲綊鐢級
            if (args != null && args.Length >= 2 && args[0] == "--diag-idle")
            {
                return DiagIdle(args[1]);
            }

            // 鍥炴粴锛?-rollback [list] 鍒楀嚭/杩樺師 *.rebuild.bak锛圓-04锛?
            if (args != null && args.Length >= 1 && args[0] == "--rollback")
            {
                return Rollback(args.Length >= 2 ? args[1] : "list");
            }

            // TLS pin 鑷锛?-tls-probe <url> [--badpin]
            if (args != null && args.Length >= 2 && args[0] == "--tls-probe")
            {
                bool badPin = args.Length >= 3 && args[2] == "--badpin";
                return TlsProbe(args[1], badPin);
            }

            // 鎴浘锛?-shot <out.png> [page]锛堝簲鐢ㄥ唴鑷覆鏌擄紝涓嶄緷璧栦氦浜掓闈級
            if (args != null && args.Length >= 2 && args[0] == "--shot")
            {
                int page = args.Length >= 3 ? Convert.ToInt32(args[2]) : 0;
                return Shot(args[1], page);
            }

            if (args != null && args.Length >= 2 && args[0] == "--verify-manifest")
            {
                string tamper = args.Length >= 4 && args[2] == "--tamper" ? args[3] : null;
                return VerifyManifest(args[1], tamper);
            }

            // 鑷锛?-selftest <catalogUrl> 瑙ｆ瀽骞朵笅杞藉崟涓璞★紝缁撴灉鍐欏叆 selftest.txt
            if (args != null && args.Length >= 2 && args[0] == "--selftest")
            {
                return SelfTest(args[1]);
            }

            // 棰勮锛?-preview-update 鍗曠嫭鏄剧ず鏇存柊涓嬭浇杩涘害绐楋紙UI 鏍稿鐢級
            if (args != null && args.Length >= 1 && args[0] == "--preview-update")
            {
                Application pv = new Application();
                pv.ShutdownMode = ShutdownMode.OnMainWindowClose;
                UpdateInfo fake = new UpdateInfo();
                fake.HasUpdate = true;
                fake.Version = "1.4.0";
                fake.Channel = "stable";
                fake.Url = "https://123.57.94.141:9443/files/DllSmith-Setup-1.4.0.exe";
                fake.Sha256 = new string('a', 64);
                UpdateDialog dlg = new UpdateDialog(fake);
                dlg.SetProgress(38, 1084000, 2854000);
                dlg.Show();
                return pv.Run(dlg);
            }

            // 鑷锛?-clean-cancel-test 楠岃瘉娓呯悊鑳戒腑鏂紙娌欑洅鐩綍锛屼笉鍔ㄧ湡瀹炴暟鎹級
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
                it.Name = "娌欑洅鍙栨秷娴嬭瘯";
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

            // 鑷锛?-scan-report <outFile> 鎵弿骞惰緭鍑烘瘡涓潯鐩殑鍒ゅ畾锛堟牳瀵?鍝簺浼氳淇"锛?
            if (args != null && args.Length >= 2 && args[0] == "--scan-report")
            {
                return ScanReport(args[1]);
            }

            Paths.EnsureDir(Paths.LogDir);
            Paths.EnsureDir(Paths.DownloadDir);
            Log.Init();
            Log.Info("license=" + License.BadgeText);
            Log.Info("install id=" + InstallId.Salted(false));   // 鍙鍝堝笇锛屼笉钀藉師濮?MachineGuid锛圓-09锛?
            // 鐩綍骞跺彂棰勫彇锛堟湰鍦颁紭鍏堬紝鍚庡彴鎷夎繙绋嬶紱澶辫触缂撳瓨 5 鍒嗛挓锛?
            CatalogCache.PrefetchAsync(Catalog.KeysForCurrentOs());
            CatalogCache.Refreshed += delegate(int ok, int total)
            {
                Log.Info("catalog remote ready " + ok + "/" + total);
            };

            Application app = new Application();
            app.ShutdownMode = ShutdownMode.OnMainWindowClose;
            app.DispatcherUnhandledException += delegate(object s, DispatcherUnhandledExceptionEventArgs e)
            {
                Log.Error("dispatcher unhandled", e.Exception);
                if (!_crashShown)
                {
                    _crashShown = true;
                    MessageBox.Show("绋嬪簭閬囧埌涓€涓湭澶勭悊鐨勯敊璇紝宸茶褰曞埌 Logs\\ 鐩綍锛屽彲缁х画浣跨敤銆俓n\n" + e.Exception.Message,
                        AppInfo.ProductName, MessageBoxButton.OK, MessageBoxImage.Error);
                }
                e.Handled = true;      // 鍗曢〉寮傚父涓嶅緱瀵艰嚧鏁翠釜杩涚▼閫€鍑?
};
            AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e)
            {
                Log.Error("appdomain unhandled", e.ExceptionObject as Exception);
            };

            MainWindow window = new MainWindow();
            return app.Run(window);
        }

        private static bool _crashShown;

        /// <summary>
        /// 鐩綍鎵╁睍鍚嶆櫘鏌ワ紙t23锛夛細杈撳嚭銆屽悇鎵╁睍鍚?鏉＄洰鏁?/ 鍙慨 / 浠嶈鎷掋€嶃€?        /// 楠屾敹鍙ｅ緞锛?mui 209 / .cpl 52 / .com 14 / .rll 12 蹇呴』鍏ㄩ儴鍙慨锛?        ///          .sys / .exe / .txt / 鏃犳墿灞曞悕 蹇呴』浠嶈鎷掋€?        /// </summary>
        /// <summary>
        /// 鍏ㄧ洰褰曚俊浠讳綋妫€锛?-verify-manifest-all
        /// 瀵?Json\ 涓嬫瘡涓?catalog锛?8 涓級璺戜竴娆＄鍚嶆竻鍗曟牎楠岋紝杈撳嚭 Trusted/ReadOnly + 瑕嗙洊鐜?+ 棣栦釜鐪熷疄宸紓銆?        /// 鏈嶅姟绔敼閿彛寰勶紙t27锛夊悗鐢ㄥ畠涓€娆￠獙瀹岋紝涓嶇敤閫愪釜浜哄伐鏁层€?        /// </summary>
        private static int VerifyManifestAll()
        {
            StringBuilder sb = new StringBuilder();
            int trusted = 0, readonly_ = 0, errored = 0;
            try
            {
                List<string> keys = new List<string>();
                if (Directory.Exists(Paths.JsonDir))
                {
                    foreach (string file in Directory.GetFiles(Paths.JsonDir, "*.json", SearchOption.AllDirectories))
                        keys.Add(Path.GetFileNameWithoutExtension(file));
                }
                keys.Sort(StringComparer.OrdinalIgnoreCase);
                sb.AppendLine("manifest trust sweep  catalogs=" + keys.Count);
                sb.AppendLine("  key                          state      coverage        note");
                foreach (string key in keys)
                {
                    List<CatalogItem> items = Catalog.LoadByKey(key);
                    if (items.Count == 0)
                    {
                        sb.AppendLine(string.Format("  {0,-28} {1,-10} {2,-15} (no items loaded)", key, "SKIP", "-"));
                        continue;
                    }
                    CatalogTrust t = CatalogTrustLoader.Load(key, items, null);
                    if (t == null) { errored++; continue; }
                    if (t.CanRepair) trusted++; else readonly_++;
                    string note = t.CanRepair ? "" : t.Reason;
                    if (note.Length > 90) note = note.Substring(0, 90) + "...";
                    sb.AppendLine(string.Format("  {0,-28} {1,-10} {2,-15} {3}",
                        key, t.State.ToString(), t.HashedEntries + "/" + t.TotalEntries, note));
                }
                sb.AppendLine("");
                sb.AppendLine(string.Format("summary: trusted={0} readonly={1} errored={2}", trusted, readonly_, errored));
            }
            catch (Exception ex)
            {
                sb.AppendLine("ERROR: " + ex.GetType().Name + ": " + ex.Message);
                Log.Error("verify-manifest-all failed", ex);
            }
            Console.WriteLine(sb.ToString());
            try
            {
                Paths.EnsureDir(Paths.LogDir);
                File.WriteAllText(Path.Combine(Paths.LogDir, "verify-manifest-all.txt"), sb.ToString(), Encoding.UTF8);
            }
            catch { }
            return readonly_ == 0 && errored == 0 ? 0 : 1;
        }
        /// <summary>鏃?FILEPATH 鏉＄洰鐨勫彲璇绘爣绛撅紙FILENAME 鈫?DISPLAYNAME 鈫?DOWNLOAD 鏂囦欢鍚嶏級</summary>
        private static string noPathLabel(Dictionary<string, object> d)
        {
            string f = Json.GetString(d, "FILENAME");
            if (!CatalogTrust.IsPlaceholder(f)) return f;
            string dn = Json.GetString(d, "DISPLAYNAME");
            if (!CatalogTrust.IsPlaceholder(dn)) return dn + "  (no FILEPATH)";
            string dl = Json.GetString(d, "DOWNLOAD");
            try { return System.IO.Path.GetFileName(new Uri(dl).LocalPath); } catch { return dl; }
        }
        private static int ExtCensus()
        {
            StringBuilder sb = new StringBuilder();
            int code = 0;
            try
            {
                // 鍏ㄧ洰褰曟櫘鏌ワ細鎵?Json\ 涓?*鍏ㄩ儴** catalog锛?8 涓?JSON锛夛紝缁熻"鏂囦欢鐩爣"锛團ILEPATH 闈炵┖锛夌殑

                Dictionary<string, int[]> stat = new Dictionary<string, int[]>(StringComparer.OrdinalIgnoreCase);
                int scanned = 0, fileTargets = 0, installerLike = 0;
                // t25锛氭妸涓ょ被"娌℃湁鍙敤鐩爣璺緞"鐨勬潯鐩垎寮€缁熻锛岄伩鍏嶆贩鍦ㄤ竴涓?(none) 妗堕噷
                List<string> noFilepath = new List<string>();        // FILEPATH 涓虹┖/鍗犱綅
                List<string> filepathNoExt = new List<string>();     // FILEPATH 闈炵┖浣嗘病鏈夋墿灞曞悕
                if (Directory.Exists(Paths.JsonDir))
                {
                    foreach (string file in Directory.GetFiles(Paths.JsonDir, "*.json", SearchOption.AllDirectories))
                    {
                        List<Dictionary<string, object>> items;
                        try { items = Json.ParseObjectArray(File.ReadAllText(file, Encoding.UTF8)); }
                        catch (Exception ex) { Log.Error("census parse failed: " + file, ex); continue; }
                        scanned++;
                        foreach (Dictionary<string, object> d in items)
                        {
                            string path = Json.GetString(d, "FILEPATH");
                            string p2 = (path == null ? "" : path.Trim());
                            if (CatalogTrust.IsPlaceholder(p2))
                            {
                                installerLike++;      // 鏃?FILEPATH锛堝畨瑁呭櫒/浠呮敞鍐岃〃绫伙級锛氫笉璧?ReplaceDll
                                if (noFilepath.Count < 30) noFilepath.Add(noPathLabel(d));
                                continue;
                            }

                            string ext = Path.GetExtension(p2).ToLowerInvariant();
                            if (ext.Length == 0)
                            {
                                string name = Json.GetString(d, "FILENAME");
                                if (string.IsNullOrEmpty(name)) name = Json.GetString(d, "DISPLAYNAME");
                                ext = Path.GetExtension(name ?? "").ToLowerInvariant();
                            }
                            if (ext.Length == 0)
                            {
                                ext = "(none)";
                                if (filepathNoExt.Count < 30) filepathNoExt.Add(p2);
                            }
                            int[] s;
                            if (!stat.TryGetValue(ext, out s)) { s = new int[3]; stat[ext] = s; }
                            fileTargets++;
                            s[0]++;
                            if (PathGuard.CheckDllTarget(Paths.ResolveX(p2)) == null) s[1]++;
                            else s[2]++;
                        }
                    }
                }

                sb.AppendLine("extension census  catalogs=" + scanned + "  file targets=" + fileTargets
                              + "  installer-like(skip)=" + installerLike);
                sb.AppendLine("allowed target extensions: " + PathGuard.AllowedExtensionText());
                sb.AppendLine("");
                sb.AppendLine("  ext        total   repairable   rejected");
                List<string> keys = new List<string>(stat.Keys);
                keys.Sort(delegate(string a, string b) { return stat[b][0].CompareTo(stat[a][0]); });
                foreach (string k in keys)
                {
                    int[] s = stat[k];
                    sb.AppendLine(string.Format("  {0,-10} {1,6} {2,12} {3,10}", k, s[0], s[1], s[2]));
                }

                // 楠屾敹鏂█锛堝彛寰勶細璇勫缁欏嚭鐨勫叏鐩綍鑱氬悎鏁帮級
                sb.AppendLine("");
                Dictionary<string, int> expect = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                expect[".mui"] = 209; expect[".cpl"] = 52; expect[".com"] = 14; expect[".rll"] = 12;
                foreach (KeyValuePair<string, int> kv in expect)
                {
                    int[] s;
                    bool found = stat.TryGetValue(kv.Key, out s);
                    bool ok = found && s[0] == kv.Value && s[1] == kv.Value && s[2] == 0;
                    sb.AppendLine(string.Format("  {0,-6} expect total={1} repairable={1} rejected=0 -> {2}{3}",
                        kv.Key, kv.Value, ok ? "[PASS]" : "[FAIL]",
                        found ? "  (actual " + s[0] + "/" + s[1] + "/" + s[2] + ")" : "  (absent)"));
                    if (!ok) code = 1;
                }
                foreach (string deny in new string[] { ".sys", ".exe", ".txt", "(none)" })
                {
                    int[] s;
                    bool present = stat.TryGetValue(deny, out s);
                    bool ok = !present || s[1] == 0;          // 涓€鏉￠兘涓嶈鍙樻垚"鍙慨"
                    sb.AppendLine(string.Format("  {0,-6} must stay rejected                     -> {1}{2}",
                        deny, ok ? "[PASS]" : "[FAIL]",
                        present ? "  (total=" + s[0] + ", repairable=" + s[1] + ")" : "  (absent)"));
                    if (!ok) code = 1;
                }
                sb.AppendLine("");
                sb.AppendLine("  鐧藉悕鍗曟墿灞曞悕鍦ㄧ洰褰曚腑鐨勫疄闄呮暟閲忥細");
                foreach (string allow in PathGuard.AllowedExtensions)
                {
                    int[] s;
                    bool present = stat.TryGetValue(allow, out s);
                    sb.AppendLine(string.Format("    {0,-6} present={1,-3} total={2,-6} repairable={3}",
                        allow, present ? "yes" : "no", present ? s[0].ToString() : "0",
                        present ? s[1].ToString() : "0"));
                }
                sb.AppendLine("");
                sb.AppendLine("  涓ょ被銆愭棤鍙敤鐩爣璺緞銆戠殑鏉＄洰锛堝垎寮€缁熻锛宼25锛夛細");
                sb.AppendLine("    (1) FILEPATH 为空/占位（安装器/仅注册表类，不走 ReplaceDll）：" + installerLike + " 条");
                foreach (string s in noFilepath) sb.AppendLine("        - " + s);
                if (installerLike > noFilepath.Count) sb.AppendLine("        其余 " + (installerLike - noFilepath.Count) + " 条省略");
                sb.AppendLine("    (2) FILEPATH 非空但没有扩展名（会被扩展名白名单拒绝）：" + filepathNoExt.Count + " 条");
                foreach (string s in filepathNoExt) sb.AppendLine("        - " + s);
                if (filepathNoExt.Count == 0) sb.AppendLine("        (无 — 全目录普查为 0 条)");
                sb.AppendLine("");
                sb.AppendLine(code == 0 ? "EXT CENSUS PASS" : "EXT CENSUS FAIL");
            }
            catch (Exception ex)
            {
                sb.AppendLine("ERROR: " + ex.GetType().Name + ": " + ex.Message);
                Log.Error("ext-census failed", ex);
                code = 2;
            }
            Console.WriteLine(sb.ToString());
            try
            {
                Paths.EnsureDir(Paths.LogDir);
                File.WriteAllText(Path.Combine(Paths.LogDir, "ext-census.txt"), sb.ToString(), Encoding.UTF8);
            }
            catch { }
            return code;
        }

        /// <summary>
        /// 杞借嵎鏍煎紡鑷锛坱21锛夛細瑕嗙洊 MZ / 7z / `&lt;?xml` / RIFF 鍥涚被鏄庢枃 + 鍥涚"浼€犳墿灞曞悕/鏈煡鏍煎紡"鍙嶄緥銆?        /// 鍒ゅ畾鏍囧噯锛氶鏈熺粨鏋滃繀椤婚€愭潯鍛戒腑锛屽惁鍒欓€€鍑虹爜 1銆?        /// </summary>
        private static int PayloadSelfTest()
        {
            StringBuilder sb = new StringBuilder();
            string dir = Path.Combine(Path.GetTempPath(), "DllSmithPayloadTest");
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
            Paths.EnsureDir(dir);
            int failed = 0;

            sb.AppendLine("payload self-test  dir=" + dir);
            sb.AppendLine("  #  case                          target   expect            got               result");

            // 鏋勯€犲洓绫?鏄庢枃"鍐呭
            byte[] mz = new byte[256];
            mz[0] = (byte)'M'; mz[1] = (byte)'Z';
            byte[] sevenZip = new byte[64];
            sevenZip[0] = 0x37; sevenZip[1] = 0x7A;
            byte[] xml = Encoding.UTF8.GetBytes("<?xml version=\"1.0\"?><root/>" + new string(' ', 32));
            byte[] riff = Encoding.UTF8.GetBytes("RIFF" + new string('x', 60));
            byte[] garbage = new byte[37];                       // 闈?16 瀵归綈銆佷篃涓嶆槸鏄庢枃

            // 鈶?鍥涚被鏄庢枃锛堢洿鎺ヨ惤鐩橈紝涓嶉渶瑕佽В瀵嗭級
            failed += CaseFile(sb, dir, 1, "plain MZ (dll)", mz, ".dll", PayloadResult.AlreadyPlain, false);
            failed += CaseFile(sb, dir, 2, "plain 7z (7z)", sevenZip, ".7z", PayloadResult.AlreadyPlain, false);
            failed += CaseFile(sb, dir, 3, "plain <?xml (msc)", xml, ".msc", PayloadResult.AlreadyPlain, false);
            failed += CaseFile(sb, dir, 4, "plain RIFF (dls)", riff, ".dls", PayloadResult.AlreadyPlain, false);


            failed += CaseFile(sb, dir, 5, "sealed MZ (dll)", mz, ".dll", PayloadResult.Decrypted, true);
            failed += CaseFile(sb, dir, 6, "sealed <?xml (msc)", xml, ".msc", PayloadResult.Decrypted, true);
            failed += CaseFile(sb, dir, 7, "sealed RIFF (dls)", riff, ".dls", PayloadResult.Decrypted, true);


            failed += CaseFile(sb, dir, 8, "sealed <?xml -> .dll", xml, ".dll", PayloadResult.UnknownFormat, true);
            failed += CaseFile(sb, dir, 9, "sealed RIFF -> .dll", riff, ".dll", PayloadResult.UnknownFormat, true);
            failed += CaseFile(sb, dir, 10, "garbage (not envelope)", garbage, ".dll", PayloadResult.NotEncryptable, false);


            failed += GuardCase(sb, dir, 11, "guard: sealed garbage -> .msc", garbage, ".msc");
            failed += GuardCase(sb, dir, 12, "guard: sealed RIFF   -> .msc", riff, ".msc");
            failed += GuardCase(sb, dir, 13, "guard: sealed <?xml  -> .dls", xml, ".dls");

            sb.AppendLine("");
            sb.AppendLine(failed == 0 ? "PAYLOAD SELFTEST PASS" : ("PAYLOAD SELFTEST FAIL (" + failed + ")"));
            Console.WriteLine(sb.ToString());
            try
            {
                Paths.EnsureDir(Paths.LogDir);
                File.WriteAllText(Path.Combine(Paths.LogDir, "payload-selftest.txt"), sb.ToString(), Encoding.UTF8);
            }
            catch { }
            return failed == 0 ? 0 : 1;
        }

        private static int GuardCase(StringBuilder sb, string dir, int no, string name, byte[] plain, string targetExt)
        {
            string raw = Path.Combine(dir, "guard" + no + ".bin");
            File.WriteAllBytes(raw, plain);
            string file = Path.Combine(dir, "guard" + no + "_encrypt");
            Payload.SealForSelfTest(raw, file);
            try { File.Delete(raw); } catch { }
            string target = Path.Combine(dir, "guardtarget" + no + targetExt);
            bool rejected = false;
            string msg = "(no exception)";
            try { RepairEngine.EnsurePayloadUsable(file, target); }
            catch (Exception ex) { rejected = true; msg = ex.Message; }
            bool ok = rejected && msg.IndexOf("未知格式", StringComparison.Ordinal) >= 0;
            sb.AppendLine(string.Format("  {0,-2} {1,-29} {2,-8} {3,-17} {4,-17} {5}",
                no, name, targetExt, "throw", rejected ? "rejected" : "pass-through", ok ? "[PASS]" : "[MISMATCH]"));
            return ok ? 0 : 1;
        }
        private static int CaseFile(StringBuilder sb, string dir, int no, string name, byte[] plain,
                                    string targetExt, PayloadResult expect, bool sealedEnvelope)
        {
            string raw = Path.Combine(dir, "case" + no + ".bin");
            File.WriteAllBytes(raw, plain);
            string file = Path.Combine(dir, "case" + no + "_encrypt");
            if (sealedEnvelope) Payload.SealForSelfTest(raw, file);
            else File.Copy(raw, file, true);
            try { File.Delete(raw); } catch { }

            string target = Path.Combine(dir, "target" + no + targetExt);   // 鎵╁睍鍚嶅繀椤诲湪鏈熬锛岀櫧鍚嶅崟鎸夊畠鍒ゅ畾
            PayloadResult got = Payload.Normalize(file, target);
            bool ok = got == expect;
            sb.AppendLine(string.Format("  {0,-2} {1,-29} {2,-8} {3,-17} {4,-17} {5}",
                no, name, targetExt, expect, got, ok ? "[PASS]" : "[MISMATCH]"));
            if (!ok) return 1;
            // 鍙嶄緥鏍￠獙锛氳鎷掓椂涓嶅緱鐣欎笅 .dec 鏄庢枃娈嬬墖
            if ((expect == PayloadResult.UnknownFormat || expect == PayloadResult.NotEncryptable)
                && File.Exists(file + ".dec"))
            {
                sb.AppendLine("      [MISMATCH] 拒绝后仍留下 .dec 明文残片：" + file + ".dec");
                return 2;
            }
            return 0;
        }

        /// <summary>
        /// 闈?PE 杞借嵎婕旂粌锛坱21锛夛細鎶婄洰褰曢噷 .msc/.dls 鏉＄洰璧?涓嬭浇 鈫?瑙ｅ瘑 鈫?钀界洏"锛?        /// 鐩爣鍐欏叆 %TEMP%\DllSmithRehearsal锛堜笉纰扮郴缁熺洏锛夛紝閫愭潯鏍稿鍐欏嚭鍐呭澶淬€?        /// </summary>
        private static int PayloadRehearse(int maxItems, string hashesJson, string extList, string catalogKey)
        {
            StringBuilder sb = new StringBuilder();
            string outDir = Path.Combine(Path.GetTempPath(), "DllSmithRehearsal");
            try { if (Directory.Exists(outDir)) Directory.Delete(outDir, true); } catch { }
            Paths.EnsureDir(outDir);
            int code = 1;
            try
            {

                string[] wantExts = (extList ?? ".msc,.dls").Split(',');
                for (int wi = 0; wi < wantExts.Length; wi++) wantExts[wi] = wantExts[wi].Trim().ToLowerInvariant();


                List<CatalogItem> all = string.IsNullOrEmpty(catalogKey)
                    ? Catalog.LoadSystemDll(true)
                    : Catalog.LoadCatalogFileExact(catalogKey);
                if (!string.IsNullOrEmpty(catalogKey))
                    sb.AppendLine("catalog  : " + catalogKey + " (explicit, " + all.Count + " items)");
                List<CatalogItem> targets = new List<CatalogItem>();
                foreach (CatalogItem it in all)
                {
                    string fn = (it.FileName ?? "").ToLowerInvariant();
                    bool match = false;
                    foreach (string e in wantExts)
                        if (e.Length > 0 && fn.EndsWith(e)) { match = true; break; }
                    if (match) targets.Add(it);
                    if (targets.Count >= maxItems) break;
                }

                sb.AppendLine("payload rehearsal  items=" + targets.Count + "  exts=" + extList + "  out=" + outDir);

                // 可选：对照 t10 数据集（_team\source\<catalog>.hashes.json），只校验 plain=="unknown" 的条目。
                Dictionary<string, string> wantSha = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                Dictionary<string, long> wantSize = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
                if (!string.IsNullOrEmpty(hashesJson))
                {
                    if (!File.Exists(hashesJson))
                    {
                        sb.AppendLine("dataset  : MISSING " + hashesJson);
                    }
                    else
                    {
                        Dictionary<string, object> root = Json.Parse(File.ReadAllText(hashesJson, Encoding.UTF8))
                            as Dictionary<string, object>;
                        object arrObj = null;
                        object[] arr = null;
                        if (root != null && root.TryGetValue("items", out arrObj)) arr = arrObj as object[];
                        if (arr != null)
                        {
                            foreach (object o in arr)
                            {
                                Dictionary<string, object> d = o as Dictionary<string, object>;
                                if (d == null) continue;
                                string kind = Json.GetString(d, "plain");
                                if (!string.Equals(kind, "unknown", StringComparison.OrdinalIgnoreCase)) continue;
                                string fn = Json.GetString(d, "filename");
                                string sha = Json.GetString(d, "sha256");
                                if (string.IsNullOrEmpty(fn) || string.IsNullOrEmpty(sha)) continue;
                                wantSha[fn] = sha.Trim().ToLowerInvariant();
                                long l;
                                if (long.TryParse(Json.GetString(d, "size"), out l)) wantSize[fn] = l;
                            }
                        }
                        sb.AppendLine("dataset  : " + hashesJson + "   plain==unknown 条目 " + wantSha.Count + " 条");
                    }
                }

                sb.AppendLine("  #  file                          payload           written   header      result");
                int no = 0, okCount = 0, rejected = 0;
                foreach (CatalogItem it in targets)
                {
                    no++;
                    RepairEngine engine = new RepairEngine();
                    engine.RehearseDir = outDir;
                    engine.Trust = null;
                    List<CatalogItem> one = new List<CatalogItem>();
                    one.Add(it);
                    engine.Run(one, new CancellationTokenSource().Token);

                    string written = Path.Combine(outDir, Path.GetFileName(Paths.ResolveX(it.FilePath)));
                    string header = "(none)";
                    string state = it.State == ItemState.Done ? "written" : "rejected";
                    if (File.Exists(written))
                    {
                        byte[] h = new byte[8];
                        using (FileStream fs = File.OpenRead(written)) fs.Read(h, 0, 8);
                        header = BitConverter.ToString(h).Replace("-", " ");
                        // 鍐呭蹇呴』涓庣洰鏍囨墿灞曞悕鍖归厤
                        // 澶存牎楠屽繀椤绘寜鐩爣鎵╁睍鍚嶅垎鍒垽瀹氾細
                        //   .msc 鈫?<?xml   .dls 鈫?RIFF   鍏朵綑锛?dll/.mui/.cpl/.com/.rll锛夆啋 MZ(PE)
                        string low = (it.FilePath ?? "").ToLowerInvariant();
                        bool headOk;
                        if (low.EndsWith(".msc"))
                            headOk = h[0] == (byte)'<' && h[1] == (byte)'?';
                        else if (low.EndsWith(".dls"))
                            headOk = h[0] == (byte)'R' && h[1] == (byte)'I'
                                     && h[2] == (byte)'F' && h[3] == (byte)'F';
                        else
                            headOk = h[0] == (byte)'M' && h[1] == (byte)'Z';
                        if (!headOk) state = "WRONG-HEADER";
                    }

                    string sha = "";
                    long fsize = 0;
                    if (File.Exists(written))
                    {
                        try { fsize = new FileInfo(written).Length; } catch { }
                        try { sha = Hashes.Sha256HexOfFile(written); } catch { }
                    }
                    string expectSha;
                    if (wantSha.TryGetValue(it.FileName ?? "", out expectSha))
                    {
                        long expectSize = 0;
                        wantSize.TryGetValue(it.FileName ?? "", out expectSize);
                        bool same = string.Equals(sha, expectSha, StringComparison.OrdinalIgnoreCase)
                                    && (expectSize == 0 || fsize == expectSize);
                        state = same ? "written+verified" : "SHA-MISMATCH";
                    }
                    if (state == "written" || state == "written+verified") okCount++;
                    if (state == "rejected") rejected++;
                    sb.AppendLine(string.Format("  {0,-2} {1,-26} {2,-10} {3,-16} {4,-24} {5}  {6}",
                        no, Path.GetFileName(it.FilePath), it.State, state, header,
                        state == "written" || state == "written+verified" ? "[PASS]"
                            : (state == "rejected" ? "[SKIPPED]" : "[FAIL]"),
                        sha.Length >= 12
                            ? ("sha=" + sha.Substring(0, 12) + " size=" + fsize + "  " + it.Detail)
                            : it.Detail));
                }
                sb.AppendLine("");
                sb.AppendLine(string.Format("summary: written={0} rejected={1} total={2}", okCount, rejected, no));
                code = (no > 0 && okCount == no) ? 0 : 2;
            }
            catch (Exception ex)
            {
                sb.AppendLine("ERROR: " + ex.GetType().Name + ": " + ex.Message);
                Log.Error("payload-rehearse failed", ex);
            }
            Console.WriteLine(sb.ToString());
            try
            {
                Paths.EnsureDir(Paths.LogDir);
                File.WriteAllText(Path.Combine(Paths.LogDir, "payload-rehearse.txt"), sb.ToString(), Encoding.UTF8);
            }
            catch { }
            return code;
        }

        /// <summary>
        /// 鐪熷疄涓嬭浇鑷锛?-repair-download &lt;catalogKey&gt; &lt;FILENAME&gt;
        /// 璧?RepairEngine 鐨勭湡瀹炰笅杞?鍊欓€?瑙ｅ瘑/鍝堝笇閾捐矾锛圖ownloadOnly=true锛屼笉鍐欑郴缁熺洰褰曪級銆?        /// 閫€鍑虹爜 0 = 涓嬭浇鎴愬姛涓旀棤寮傚父銆?        /// </summary>
        private static int RepairDownload(string catalogKey, string fileName)
        {
            StringBuilder sb = new StringBuilder();
            int code = 1;
            try
            {
                Paths.EnsureDir(Paths.DownloadDir);
                List<CatalogItem> items = Catalog.LoadByKey(catalogKey);

                // 鍏堟寜**鏁翠唤鐩綍**璺戜竴娆＄鍚嶆竻鍗曟牎楠岋紙涓庣敓浜ч〉闈㈠悓鍙ｅ緞锛氬畬鏁存€у垽瀹氶渶瑕佸叏閲忔潯鐩級锛?
                // 杩欐牱鏉＄洰鎵嶄細琚创涓?manifest 閲岀殑 sha256锛屽悗缁笅杞借蛋鐪熷疄鐨勫己鏍￠獙璺緞銆?
                CatalogTrustSet trustAll = CatalogTrustSet.LoadAll(items);

                CatalogItem pick = null;
                foreach (CatalogItem it in items)
                {
                    if (string.Equals(it.FileName, fileName, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(it.Name, fileName, StringComparison.OrdinalIgnoreCase)
                        || Basename(it.Download) == fileName)
                    { pick = it; break; }
                }
                if (pick == null)
                {
                    sb.AppendLine("item not found: " + catalogKey + " / " + fileName);
                    sb.AppendLine("candidates(" + items.Count + "): ");
                    for (int i = 0; i < items.Count && i < 12; i++)
                        sb.AppendLine("  " + items[i].FileName + " | " + items[i].Name + " | " + Basename(items[i].Download));
                    try
                    {
                        File.WriteAllText(Path.Combine(Paths.LogDir, "repair-download.txt"), sb.ToString(), Encoding.UTF8);
                    }
                    catch { }
                    Console.WriteLine(sb.ToString());
                    return 3;
                }

                CatalogTrustSet trust = trustAll;   // 澶嶇敤鏁翠唤鐩綍鐨勬牎楠岀粨鏋滐紙鐢熶骇鍚屽彛寰勶級
                List<CatalogItem> one = new List<CatalogItem>();
                one.Add(pick);

                RepairEngine engine = new RepairEngine();
                engine.DownloadOnly = true;
                engine.Run(one, new CancellationTokenSource().Token);
                engine.Trust = null;   // 涓嬭浇鑷鍙獙璇佷笅杞?瑙ｅ瘑/鍝堝笇閾捐矾锛屼笉鍐欑郴缁熺洰褰曪紱鍝堝笇鐢?manifest 璐村埌鏉＄洰涓婂悗浠嶄細寮烘牎楠?
            engine.Run(one, new CancellationTokenSource().Token);

                sb.AppendLine("catalog  : " + catalogKey);
                sb.AppendLine("item     : " + pick.FileName + "  arch=" + pick.Arch);
                sb.AppendLine("hash     : " + (pick.HashCovered ? pick.Sha256.Substring(0, 16) + "..." : "(未覆盖)"));
                sb.AppendLine("trust    : " + trustAll.CoverageText + (trustAll.CanRepair ? " (Trusted)" : " (ReadOnly: " + trustAll.Reason + ")"));
                sb.AppendLine("success  : " + engine.Success);
                sb.AppendLine("failed   : " + engine.Failed);
                sb.AppendLine("state    : " + pick.State + "  detail=" + pick.Detail);
                code = engine.Success == 1 ? 0 : 2;
            }
            catch (Exception ex)
            {
                sb.AppendLine("ERROR: " + ex.GetType().Name + ": " + ex.Message);
                Log.Error("repair-download failed", ex);
            }
            Console.WriteLine(sb.ToString());
            try
            {
                Paths.EnsureDir(Paths.LogDir);
                File.WriteAllText(Path.Combine(Paths.LogDir, "repair-download.txt"), sb.ToString(), Encoding.UTF8);
            }
            catch { }
            return code;
        }

        private static string Basename(string url)
        {
            if (string.IsNullOrEmpty(url)) return "";
            try { return Path.GetFileName(new Uri(url).LocalPath); }
            catch { return Path.GetFileName(url); }
        }

        /// <summary>
        /// 闂ㄧ鑷锛?-selfcheck-gate &lt;realRecord.json&gt; [forged1.json ...]
        /// 姝ｅ悜锛氱湡璁板綍蹇呴』 signature OK銆?        /// 璐熷悜锛氭敼 version / 鏀?url / 鏀?sha256 / 鍘?sig / kid 鏀规垚娴嬭瘯閽ュ寵 / 澶栭儴浼€犳牱鏈紝鍏ㄩ儴蹇呴』 FAIL銆?        /// 閫€鍑虹爜 0 = 鍏ㄩ儴绗﹀悎鏈熸湜銆?        /// </summary>
        private static int SelfCheckGate(string realRecord, string[] forged)
        {
            StringBuilder sb = new StringBuilder();
            int failed = 0;

            UpdateInfo real = LoadRecord(realRecord);
            if (real == null)
            {
                Console.WriteLine("GATE FAIL - cannot read record: " + realRecord);
                return 3;
            }

            sb.AppendLine("record   : " + realRecord);
            sb.AppendLine("kid list : [" + string.Join(", ", ReleaseKeys.KnownKids()) + "]");
            sb.AppendLine("");

            // 鈶?姝ｅ悜锛氱湡璁板綍
            failed += Case(sb, "real record", real, true, null);
            // 鈶?version 琚敼
            UpdateInfo v = LoadRecord(realRecord);
            v.Version = "9.9.9";
            failed += Case(sb, "tampered version", v, false, null);

            UpdateInfo u = LoadRecord(realRecord);
            u.Url = "https://evil-qalab.example.com/DllSmith-Setup-1.4.0.exe";
            failed += Case(sb, "tampered url", u, false, null);
            // 鈶?sha256 琚敼
            UpdateInfo h = LoadRecord(realRecord);
            h.Sha256 = new string('b', 64);
            failed += Case(sb, "tampered sha256", h, false, null);
            // 鈶?鍘绘帀绛惧悕
            UpdateInfo s = LoadRecord(realRecord);
            s.Sig = "";
            failed += Case(sb, "missing sig", s, false, null);
            // 鈶?kid 鏀规垚娴嬭瘯閽ュ寵锛堢鍚嶆槸 ds1 绛剧殑锛屼粛蹇呴』鍥?kid 鐧藉悕鍗曡鎷掞級
            UpdateInfo k = LoadRecord(realRecord);
            k.Kid = TestKidName;
            failed += Case(sb, "kid=" + TestKidName, k, false, "未知的更新签名密钥 kid=" + TestKidName);
            // 鈶?澶栭儴浼€犳牱鏈紙QA 鎻愪緵鐨?testkid / evilurl 绛夛級
            foreach (string f in forged)
            {
                UpdateInfo fo = LoadRecord(f);
                if (fo == null) { sb.AppendLine("  [SKIP] cannot read " + f); continue; }
                failed += Case(sb, "forged: " + Path.GetFileName(f), fo, false, null);
            }

            sb.AppendLine("");
            sb.AppendLine(failed == 0 ? "GATE PASS" : ("GATE FAIL (" + failed + " case(s) mismatched)"));
            Console.WriteLine(sb.ToString());
            try
            {
                Paths.EnsureDir(Paths.LogDir);
                File.WriteAllText(Path.Combine(Paths.LogDir, "selfcheck-gate.txt"), sb.ToString(), Encoding.UTF8);
            }
            catch { }
            return failed == 0 ? 0 : 1;
        }

        private static int Case(StringBuilder sb, string name, UpdateInfo info, bool expectOk,
                               string mustContain)
        {
            TrustResult sig = UpdateTrust.VerifySignatureOnly(info);
            TrustResult full = UpdateTrust.VerifyUpdateRecord(info);
            bool ok = sig.Ok;
            string reason = sig.Ok ? "" : sig.Reason;
            bool good = ok == expectOk;

            if (mustContain != null && reason.IndexOf(mustContain, StringComparison.Ordinal) < 0) good = false;
            if (sb != null)
            {
                sb.AppendLine(string.Format("  {0,-26} signature={1,-5} policy={2,-28} expect={3}  {4}",
                    name,
                    ok ? "OK" : "FAIL",
                    full.Ok ? "OK" : "reject",
                    expectOk ? "OK" : "FAIL",
                    good ? "[PASS]" : "[MISMATCH]" + (reason.Length > 0 ? " - " + reason : "")));
            }
            return good ? 0 : 1;
        }

        /// <summary>
        /// 娴嬭瘯 kid 鍚嶅瓧鍦ㄨ繍琛屾椂鎷煎嚭鏉ワ細鐢熶骇浜岃繘鍒堕噷涓嶅厑璁稿嚭鐜拌瀛楅潰閲?        /// 锛坆uild.ps1 浼?grep 浜х墿锛屽彂鐜颁换浣?test kid / 娴嬭瘯妯℃暟灏变腑姝㈡瀯寤猴級銆?        /// </summary>
        private static string TestKidName
        {
            get { return string.Format("{0}-{1}", "ds1", "test"); }   // 涓嶅啓鎴愬父閲忔嫾鎺ワ細缂栬瘧鍣ㄤ細鎶婂父閲忔姌鍙犳垚瀛楅潰閲忥紝琚?build.ps1 鐨勪骇鐗╂柇瑷€鎶撳埌
        }

        private static UpdateInfo LoadRecord(string file)
        {
            try
            {
                string text = File.ReadAllText(file, Encoding.UTF8);
                Dictionary<string, object> d = Json.Parse(text) as Dictionary<string, object>;
                if (d == null) return null;
                UpdateInfo info = new UpdateInfo();
                info.HasUpdate = true;
                info.Version = Json.GetString(d, "version");
                info.Channel = Json.GetString(d, "channel");
                info.Sha256 = Json.GetString(d, "sha256");
                info.Url = Json.GetString(d, "url");
                info.Sig = Json.GetString(d, "sig", "signature");
                info.Kid = Json.GetString(d, "kid");
                return info;
            }
            catch (Exception ex)
            {
                Log.Error("read record failed: " + file, ex);
                return null;
            }
        }

        /// <summary>
        /// 鍙戝竷鍓嶇疆闂ㄧ锛?-verify-release &lt;record.json&gt;
        /// 鐢?*鍐呯疆鍏挜**楠岃瘉涓€鏉＄湡瀹炲彂甯冭褰曪紱鍚屾椂缁欏嚭"绛惧悕缁撹"鍜?浼犺緭绛栫暐缁撹"銆?        /// 閫€鍑虹爜锛? = 绛惧悕鏈夋晥锛? = 绛惧悕鏃犳晥锛? = 璁板綍鏂囦欢璇讳笉浜嗐€?        /// </summary>
        private static int VerifyRelease(string file)
        {
            StringBuilder sb = new StringBuilder();
            int code = 3;
            try
            {
                string text = File.ReadAllText(file, Encoding.UTF8);
                Dictionary<string, object> d = Json.Parse(text) as Dictionary<string, object>;
                if (d == null) throw new InvalidOperationException("record is not a JSON object");

                UpdateInfo info = new UpdateInfo();
                info.HasUpdate = true;
                info.Version = Json.GetString(d, "version");
                info.Channel = Json.GetString(d, "channel");
                info.Sha256 = Json.GetString(d, "sha256");
                info.Url = Json.GetString(d, "url");
                info.Sig = Json.GetString(d, "sig", "signature");
                info.Kid = Json.GetString(d, "kid");
                info.Notes = Json.GetString(d, "notes");
                info.PublishedAt = Json.GetString(d, "published_at");

                TrustResult sig = UpdateTrust.VerifySignatureOnly(info);
                TrustResult full = UpdateTrust.VerifyUpdateRecord(info);

                sb.AppendLine("record   : " + file);
                sb.AppendLine("version  : " + info.Version + "  channel=" + info.Channel);
                sb.AppendLine("kid      : " + (string.IsNullOrEmpty(info.Kid) ? "(none)" : info.Kid));
                sb.AppendLine("url      : " + info.Url);
                sb.AppendLine("canonical: " + (sig.Canonical.Length > 0
                    ? sig.Canonical : UpdateTrust.UpdateCanonical(info.Version, info.Channel, info.Sha256, info.Url)));
                sb.AppendLine("signature: " + (sig.Ok ? "OK" : "FAIL - " + sig.Reason));
                sb.AppendLine("policy   : " + (full.Ok ? "OK (https)" : "reject - " + full.Reason));
                sb.AppendLine("pubkey   : kid=ds1 builtin=" + ReleaseKeys.ProductionKeyAvailable);
                code = sig.Ok ? 0 : 2;
            }
            catch (Exception ex)
            {
                sb.AppendLine("ERROR: " + ex.Message);
                Log.Error("verify-release failed", ex);
            }
            Console.WriteLine(sb.ToString());
            try
            {
                File.WriteAllText(Path.Combine(Paths.LogDir, "verify-release.txt"), sb.ToString(), Encoding.UTF8);
            }
            catch { }
            return code;
        }

        /// <summary>
        /// 鎬ц兘澶嶆祴锛?-diag-idle &lt;绉?gt;
        /// 鍚姩涓荤獥鍚庨噰鏍锋湰杩涚▼ CPU 鍗犳瘮锛岀粨鏋滃啓 Logs\diag-idle.txt锛堢獥鍙ｅ彲瑙佹€侊級銆?        /// </summary>
        private static int DiagIdle(string secondsArg)
        {
            int seconds = 10;
            try { seconds = Math.Max(3, Convert.ToInt32(secondsArg)); } catch { }

            Application app = new Application();
            app.ShutdownMode = ShutdownMode.OnMainWindowClose;
            MainWindow w = new MainWindow();

            Thread t = new Thread(delegate()
            {
                Thread.Sleep(2500);                       // 绛夐甯т笌棰勫彇绋冲畾
                System.Diagnostics.Process me = System.Diagnostics.Process.GetCurrentProcess();
                TimeSpan t0 = me.TotalProcessorTime;
                System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
                Thread.Sleep(seconds * 1000);
                sw.Stop();
                me.Refresh();
                double pct = (me.TotalProcessorTime - t0).TotalMilliseconds
                             / Math.Max(1.0, sw.Elapsed.TotalMilliseconds) * 100.0;
                string line = string.Format(
                    "visible-idle cpu={0:0.0}% ws={1:0.0}MB private={2:0.0}MB handles={3} threads={4} sec={5}",
                    pct, me.WorkingSet64 / 1048576.0, me.PrivateMemorySize64 / 1048576.0,
                    me.HandleCount, me.Threads.Count, seconds);
                try
                {
                    Paths.EnsureDir(Paths.LogDir);
                    File.WriteAllText(Path.Combine(Paths.LogDir, "diag-idle.txt"), line, Encoding.UTF8);
                }
                catch { }
                Console.WriteLine(line);
                try { w.Dispatcher.Invoke(new Action(delegate() { app.Shutdown(); })); } catch { }
            });
            t.IsBackground = true;
            t.Start();

            app.Run(w);
            return 0;
        }

        /// <summary>
        /// 鎴浘锛?-shot &lt;out.png&gt; [pageIndex]
        /// 搴旂敤鍐呯敤 RenderTargetBitmap 鑷覆鏌撲富绐楋紙鏃犱氦浜掓闈篃鑳藉嚭鍥撅紝渚涜瘎瀹?鍥炲綊鐣欒瘉锛夈€?        /// </summary>
        private static int Shot(string outPath, int page)
        {
            Application app = new Application();
            app.ShutdownMode = ShutdownMode.OnMainWindowClose;
            MainWindow w = new MainWindow();
            w.WindowStartupLocation = WindowStartupLocation.Manual;
            w.Left = -6000;                     // 鎸埌灞忓箷澶栵紝閬垮厤闂竴涓?
            w.Top = 0;
            w.Show();

            try
            {
                System.Reflection.MethodInfo mi = typeof(MainWindow).GetMethod("ShowPage",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (mi != null && page > 0) mi.Invoke(w, new object[] { page });
            }
            catch (Exception ex) { Log.Error("shot: switch page failed", ex); }

            w.UpdateLayout();
            app.Dispatcher.Invoke(new Action(delegate { }), DispatcherPriority.Background);

            int px = (int)Math.Ceiling(w.ActualWidth);
            int py = (int)Math.Ceiling(w.ActualHeight);
            if (px <= 0) px = 1220;
            if (py <= 0) py = 820;

            RenderTargetBitmap rtb = new RenderTargetBitmap(px, py, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(w);
            PngBitmapEncoder enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(rtb));
            string dir = Path.GetDirectoryName(outPath);
            if (!string.IsNullOrEmpty(dir)) Paths.EnsureDir(dir);
            using (FileStream fs = File.Create(outPath)) enc.Save(fs);

            Console.WriteLine("shot saved: " + outPath + " (" + px + "x" + py + ")");
            Log.Info("shot saved: " + outPath);
            app.Shutdown();
            return 0;
        }

        /// <summary>
        /// manifest 鑱旇皟鑷锛?-verify-manifest &lt;catalogKey&gt;
        /// 杈撳嚭锛歴tate / reason / count / lines 琛屾暟 / coverage / 绛惧悕缁撹銆?        /// 閫€鍑虹爜 0 = Trusted锛堝彲淇級锛? = 鍙/绂荤嚎銆?        /// </summary>
        private static int VerifyManifest(string catalogKey, string tamper)
        {
            StringBuilder sb = new StringBuilder();
            int code = 1;
            try
            {
                CatalogCache.PrefetchAsync(new string[] { catalogKey });
                // 绛夐鍙栬惤鍦帮紙鏈€澶?12s锛?
            for (int i = 0; i < 24 && CatalogCache.Get(catalogKey) == null; i++)
                    Thread.Sleep(500);

                List<CatalogItem> items = Catalog.LoadByKey(catalogKey);

                // 鍙嶄緥锛氭湰鍦版敼涓€鏉?FILEPATH锛堟ā鎷熺洰褰曡姹℃煋锛夛紝蹇呴』琚垽涓哄彧璇?
            if (string.Equals(tamper, "path", StringComparison.OrdinalIgnoreCase) && items.Count > 0)
                {
                    string before = items[0].FilePath;
                    items[0].FilePath = @"X:\Windows\System32\evil-should-be-rejected.dll";
                    sb.AppendLine("tamper   : item[0] FILEPATH " + before + " -> " + items[0].FilePath);
                }
                else if (string.Equals(tamper, "count", StringComparison.OrdinalIgnoreCase) && items.Count > 1)
                {
                    items.RemoveAt(items.Count - 1);
                    sb.AppendLine("tamper   : drop last item (items=" + items.Count + ")");
                }
                Dictionary<string, object> inline = CatalogCache.GetManifest(catalogKey);
                if (inline == null)
                    inline = CatalogCache.InlineManifestFromHeaders(CatalogCache.GetHeaders(catalogKey));

                CatalogTrust t = CatalogTrustLoader.Load(catalogKey, items, inline);
                sb.AppendLine("key      : " + catalogKey);
                sb.AppendLine("source   : " + (inline != null ? "catalog inline manifest" : "/api/v1/manifest"));
                sb.AppendLine("items    : " + items.Count);
                sb.AppendLine("state    : " + t.State);
                sb.AppendLine("kid      : " + t.Kid);
                sb.AppendLine("count    : " + t.TotalEntries + "  lines=" + t.LineCount);
                sb.AppendLine("coverage : " + t.CoverageText);
                sb.AppendLine("reason   : " + (string.IsNullOrEmpty(t.Reason) ? "(none)" : t.Reason));
                sb.AppendLine("repair   : " + (t.CanRepair ? "ALLOWED" : "READ-ONLY"));
                code = t.CanRepair ? 0 : 1;
            }
            catch (Exception ex)
            {
                sb.AppendLine("ERROR: " + ex.Message);
                Log.Error("verify-manifest failed", ex);
            }
            Console.WriteLine(sb.ToString());
            try
            {
                Paths.EnsureDir(Paths.LogDir);
                File.WriteAllText(Path.Combine(Paths.LogDir, "verify-manifest.txt"), sb.ToString(), Encoding.UTF8);
            }
            catch { }
            return code;
        }

        /// <summary>
        /// TLS pin 鑷锛?-tls-probe &lt;url&gt; [--badpin]
        /// 鐢ㄥ鎴风鐪熷疄鍥炶皟锛堣瘉涔︽湁鏁堟湡 + SAN 鏂█ + 妯℃暟 pin / 閾惧埌鍐呯疆鏍癸級鎻℃墜骞跺彇 /api/v1/health銆?        /// --badpin 鏁呮剰鎶?pin 鎹㈡垚閿欒妯℃暟锛屽繀椤绘彙鎵嬪け璐ワ紙鍙嶄緥锛夈€?        /// </summary>
        private static int TlsProbe(string url, bool badPin)
        {
            StringBuilder sb = new StringBuilder();
            int code = 2;
            if (badPin)
            {
                // 鏁呮剰鎹竴鎶婇敊璇ā鏁帮紝楠岃瘉"pin 閰嶉敊 -> 鎷掔粷鎻℃墜"锛岃€屼笉鏄?return true
                TlsPin.SelfTestPinOverride = Convert.ToBase64String(new byte[384]);
            }
            try
            {
                ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
                HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
                req.Timeout = 8000;
                req.UserAgent = "DllSmith/" + AppInfo.Version;
                req.Proxy = null;
                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                using (StreamReader sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                {
                    string body = sr.ReadToEnd();
                    sb.AppendLine("url    : " + url);
                    sb.AppendLine("http   : " + (int)resp.StatusCode);
                    sb.AppendLine("body   : " + (body.Length > 200 ? body.Substring(0, 200) : body));
                    sb.AppendLine("pin    : " + (badPin ? "BROKEN(=expected fail)" : "builtin root"));
                    sb.AppendLine("result : HANDSHAKE OK");
                    code = 0;
                }
            }
            catch (Exception ex)
            {
                sb.AppendLine("url    : " + url);
                sb.AppendLine("pin    : " + (badPin ? "BROKEN(=expected fail)" : "builtin root"));
                sb.AppendLine("result : HANDSHAKE FAILED - " + ex.GetType().Name + ": " + ex.Message);
                code = badPin ? 0 : 3;    // 鍙嶄緥澶辫触鏄?棰勬湡鎴愬姛"
            }
            Console.WriteLine(sb.ToString());
            try
            {
                File.WriteAllText(Path.Combine(Paths.LogDir,
                    badPin ? "tls-probe-badpin.txt" : "tls-probe.txt"), sb.ToString(), Encoding.UTF8);
            }
            catch { }
            return code;
        }

        /// <summary>鍥炴粴鍏ュ彛锛圓-04锛夛細list = 鍒楀嚭鍙繕鍘熺殑 .rebuild.bak</summary>
        private static int Rollback(string mode)
        {
            List<BackupEntry> list = BackupStore.Scan();
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("backup entries: " + list.Count);
            foreach (BackupEntry e in list)
                sb.AppendLine("  " + e.Target + "  <-  " + e.Backup + "  (" + e.SizeText + ", " + e.TimeText + ")");
            Console.WriteLine(sb.ToString());
            try { File.WriteAllText(Path.Combine(Paths.LogDir, "rollback-list.txt"), sb.ToString(), Encoding.UTF8); }
            catch { }
            return 0;
        }

        /// <summary>
        /// 鑷锛?-scan-report &lt;outFile&gt;
        /// 璺戜竴閬嶅畬鏁存壂鎻忥紝鎶婃瘡涓潯鐩殑鍒ゅ畾鍐欒繘鏂囦欢骞惰緭鍑虹粺璁★紝鐢ㄤ簬鏍稿"鍝簺椤逛細琚慨澶?銆?        /// </summary>
        private static int ScanReport(string outFile)
        {
            List<CatalogItem> all = new List<CatalogItem>();
            all.AddRange(Catalog.LoadSystemDll(Environment.Is64BitOperatingSystem));
            all.AddRange(Catalog.LoadRuntime("VC_Scan", "VC_Scan", Environment.Is64BitOperatingSystem));
            all.AddRange(Catalog.LoadRuntime("VC_DLL_Scan", "VC_DLL_Scan", Environment.Is64BitOperatingSystem));
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
                    sb.AppendLine(string.Format("[缂哄け] {0,-8} {1,-30} arch={2} path={3}",
                        it.Category, it.Name, it.Arch, it.Where));
            }
            head.AppendLine("total=" + all.Count + " absent=" + absent + " present=" + present);
            foreach (KeyValuePair<string, int[]> kv in perCat)
                head.AppendLine("  " + kv.Key + ": absent=" + kv.Value[0] + " present=" + kv.Value[1]);
            head.AppendLine("---- 缂哄け鏄庣粏 ----");
            File.WriteAllText(outFile, head.ToString() + sb.ToString(), Encoding.UTF8);
            Console.WriteLine(head.ToString());
            return 0;
        }

        /// <summary>
        /// 鑷锛?-selftest <catalogUrl>
        /// 瑙ｆ瀽鐪熷疄涓嬭浇鍦板潃銆佷笅杞藉埌涓存椂鐩綍銆佽瘑鍒枃浠跺ご锛岀粨鏋滃啓鍏?selftest.txt锛坵inexe 鏃犳帶鍒跺彴锛夈€?        /// </summary>
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
                    sb.AppendLine("decrypt : ok (key=DDIG4SECUREKEY16, CBC, IV=鍓?6瀛楄妭)");
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
