// ============================================================================
//  4DDiG DLL Fixer — 重建工程（Clean-room Rebuild）
//  文件：Core.cs   基础设施：路径 / 日志 / JSON / 目录模型 / 系统探测 / 授权
//  说明：本工程依据《4DDiG_DLL_Fixer_完整逆向报告》的行为规格重写，
//        不是原始源码；类名与方法职责尽量对齐逆向结论，便于对照。
//  语言约束：C# 5（csc.exe 4.8），不使用 C# 6+ 语法。
// ============================================================================

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Management;
using System.Net;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using Microsoft.Win32;

namespace DllFix.Core
{
    // ── 路径 ────────────────────────────────────────────────────────────────
    public static class Paths
    {
        public static string BaseDir
        {
            get { return AppDomain.CurrentDomain.BaseDirectory; }
        }

        public static string JsonDir
        {
            get { return Path.Combine(BaseDir, "Json"); }
        }

        public static string SevenZipExe
        {
            get { return Path.Combine(Path.Combine(BaseDir, "7z"), "7z.exe"); }
        }

        public static string UserDataDir
        {
            get
            {
                string docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                return Path.Combine(docs, "DllRepair");
            }
        }

        public static string DownloadDir
        {
            get { return Path.Combine(UserDataDir, "Download"); }
        }

        public static string LogDir
        {
            get { return Path.Combine(BaseDir, "Logs"); }
        }

        public static string LicenseFile
        {
            get { return Path.Combine(BaseDir, "license.json"); }
        }

        public static string ProductFile
        {
            get { return Path.Combine(BaseDir, "register.json"); }
        }

        public static string SystemDrive
        {
            get
            {
                string root = Path.GetPathRoot(Environment.SystemDirectory);
                return string.IsNullOrEmpty(root) ? "C:\\" : root;
            }
        }

        /// <summary>把目录里的 X:\ 占位盘符换成系统盘</summary>
        public static string ResolveX(string path)
        {
            if (string.IsNullOrEmpty(path)) return path;
            if (path.Length > 1 && (path[0] == 'X' || path[0] == 'x') && path[1] == ':')
                return SystemDrive + path.Substring(3);
            return path;
        }

        public static void EnsureDir(string dir)
        {
            try { if (!Directory.Exists(dir)) Directory.CreateDirectory(dir); }
            catch { }
        }
    }

    // ── 日志（对齐原程序的 Logs\ 滚动日志行为） ──────────────────────────────
    public static class Log
    {
        private static readonly object Sync = new object();
        private static string _file;

        public static event Action<string> LineWritten;

        public static void Init()
        {
            Paths.EnsureDir(Paths.LogDir);
            _file = Path.Combine(Paths.LogDir, "rebuild-" + DateTime.Now.ToString("yyyyMMdd") + ".log");
            Info("==================================================");
            Info("4DDiG DLL Fixer Rebuild start, version " + AppInfo.Version);
            Info("base=" + Paths.BaseDir);
            Info("os=" + Environment.OSVersion + " 64bit=" + Environment.Is64BitOperatingSystem
                 + " elevated=" + SystemProbe.IsElevated);
        }

        public static void Info(string message)
        {
            Write("INFO ", message);
        }

        public static void Error(string message, Exception ex)
        {
            Write("ERROR", message + (ex == null ? "" : " | " + ex.GetType().Name + ": " + ex.Message));
        }

        private static void Write(string level, string message)
        {
            string line = DateTime.Now.ToString("HH:mm:ss.fff") + " | " + level + " | " + message;
            lock (Sync)
            {
                try
                {
                    if (_file != null) File.AppendAllText(_file, line + Environment.NewLine, Encoding.UTF8);
                }
                catch { }
            }
            Action<string> h = LineWritten;
            if (h != null) { try { h(line); } catch { } }
        }
    }

    public static class AppInfo
    {
        public const string ProductName = "DllSmith";
        public const string ChineseName = "DLL 修复匠";
        public const string Tagline = "开源运行库与 DLL 修复工具";
        public const string Version = "1.0.3";
        public const string License = "GPL-3.0";
        public const string SiteUrl  = "http://123.57.94.141:8787/download";
        public const string BuildTag = "opensource";
    }

    // ── 客户端配置（config.json：{"server":"http://host:port"}） ────────────
    public static class Config
    {
        private static bool _loaded;
        private static string _server;

        public static string ServerUrl
        {
            get
            {
                if (_loaded) return _server;
                _loaded = true;
                try
                {
                    string path = Path.Combine(Paths.BaseDir, "config.json");
                    if (File.Exists(path))
                    {
                        Dictionary<string, object> d = Json.Parse(File.ReadAllText(path, Encoding.UTF8))
                            as Dictionary<string, object>;
                        if (d != null) _server = Json.GetString(d, "server").TrimEnd('/');
                    }
                }
                catch (Exception ex) { Log.Error("config.json parse failed", ex); }
                if (!string.IsNullOrEmpty(_server)) Log.Info("server catalog: " + _server);
                return _server;
            }
        }
    }

    // ── JSON（用 BCL 的 JavaScriptSerializer，避免第三方依赖） ────────────────
    public static class Json
    {
        private static readonly JavaScriptSerializer Ser = CreateSerializer();

        private static JavaScriptSerializer CreateSerializer()
        {
            JavaScriptSerializer s = new JavaScriptSerializer();
            s.MaxJsonLength = int.MaxValue;
            s.RecursionLimit = 200;
            return s;
        }

        public static object Parse(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            return Ser.DeserializeObject(text);
        }

        public static string Stringify(object value)
        {
            return Ser.Serialize(value);
        }

        /// <summary>把 [{"A":"1"}] 解析成 List&lt;Dictionary&lt;string,object&gt;&gt;</summary>
        public static List<Dictionary<string, object>> ParseObjectArray(string text)
        {
            List<Dictionary<string, object>> result = new List<Dictionary<string, object>>();
            object[] arr = Parse(text) as object[];
            if (arr == null) return result;
            foreach (object o in arr)
            {
                Dictionary<string, object> d = o as Dictionary<string, object>;
                if (d != null) result.Add(d);
            }
            return result;
        }

        public static string GetString(Dictionary<string, object> d, params string[] keys)
        {
            if (d == null) return string.Empty;
            foreach (string k in keys)
            {
                object v;
                if (d.TryGetValue(k, out v) && v != null) return Convert.ToString(v, CultureInfo.InvariantCulture);
                // 大小写不敏感兜底（不同 JSON 里 key 大小写不一致）
                foreach (KeyValuePair<string, object> kv in d)
                {
                    if (string.Equals(kv.Key, k, StringComparison.OrdinalIgnoreCase) && kv.Value != null)
                        return Convert.ToString(kv.Value, CultureInfo.InvariantCulture);
                }
            }
            return string.Empty;
        }
    }

    // ── 目录模型 ────────────────────────────────────────────────────────────
    public enum CheckKind
    {
        FileExists,
        RegistryDisplayName,
        Both,
        MatchPattern,   // MATCH 通配（VC_DLL 目录：在 WinSxS 里按文件名找）
        Unknown         // 既无路径也无名称，无法判定：既不算正常也不算缺失
    }

    public enum ItemKind
    {
        SystemDll,   // 目标是一个 .dll，下载后覆盖到 FILEPATH
        Installer,   // 目标是一个安装包（.exe/.msi），带 COMMANDLINE
        Archive      // 目标是一个压缩包（.7z/.zip），解压到下载目录
    }

    public enum ItemState
    {
        Unknown,
        Absent,
        Present,
        Downloading,
        Installing,
        Done,
        Failed
    }

    public class CatalogItem : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        private bool _selected = true;
        private ItemState _state = ItemState.Unknown;
        private string _detail = string.Empty;
        private int _percent;

        public string Category { get; set; }
        public string FileName { get; set; }

        /// <summary>展示名（去掉尾部 x64/x86 与括号）</summary>
        public string Name { get; set; }

        /// <summary>架构：x64 / x86 / —</summary>
        public string Arch { get; set; }

        /// <summary>已安装版本号（扫描时从卸载表取，未安装为 —）</summary>
        public string Version { get; set; }
        public string FilePath { get; set; }
        public string Download { get; set; }
        public string CommandLine { get; set; }
        public string DisplayName { get; set; }
        public string DisplayVersion { get; set; }
        public string Wow6432Node { get; set; }
        public string Match { get; set; }
        public string DownloadDll { get; set; }
        public CheckKind Check { get; set; }
        public ItemKind Kind { get; set; }

        public bool Selected
        {
            get { return _selected; }
            set { _selected = value; Raise("Selected"); }
        }

        public ItemState State
        {
            get { return _state; }
            set { _state = value; Raise("State"); Raise("StateText"); Raise("IsMissing"); }
        }

        public bool IsMissing
        {
            get { return State == ItemState.Absent || State == ItemState.Failed; }
        }

        public string Detail
        {
            get { return _detail; }
            set { _detail = value; Raise("Detail"); }
        }

        public int Percent
        {
            get { return _percent; }
            set { _percent = value; Raise("Percent"); }
        }

        public string StateText
        {
            get
            {
                switch (State)
                {
                    case ItemState.Absent: return "缺失";
                    case ItemState.Present: return "正常";
                    case ItemState.Downloading: return "下载中";
                    case ItemState.Installing: return "安装中";
                    case ItemState.Done: return "已修复";
                    case ItemState.Failed: return "失败";
                    default: return "待检测";
                }
            }
        }

        public string Title
        {
            get
            {
                if (!string.IsNullOrEmpty(DisplayVersion)) return DisplayVersion;
                if (!string.IsNullOrEmpty(DisplayName)) return DisplayName;
                return FileName;
            }
        }

        /// <summary>下载地址（表格里显示用）</summary>
        public string Url
        {
            get { return Download; }
        }

        public string Where
        {
            get
            {
                if (!string.IsNullOrEmpty(FilePath)) return Paths.ResolveX(FilePath);
                return Download;
            }
        }

        private void Raise(string name)
        {
            PropertyChangedEventHandler h = PropertyChanged;
            if (h != null) h(this, new PropertyChangedEventArgs(name));
        }
    }

    // ── 目录加载（对应逆向报告 §8.1 的 18 个 JSON） ──────────────────────────
    public static class Catalog
    {
        public static List<CatalogItem> LoadSystemDll(bool is64)
        {
            string file = Path.Combine(Path.Combine(Paths.JsonDir, "SystemDllJson"),
                "SystemDll_" + OsTag() + "_" + (is64 ? "64" : "32") + ".json");
            List<CatalogItem> list = new List<CatalogItem>();
            foreach (Dictionary<string, object> d in ReadArray(file))
            {
                CatalogItem it = new CatalogItem();
                it.Category = "系统DLL";
                it.FileName = Json.GetString(d, "FILENAME");
                it.FilePath = Json.GetString(d, "FILEPATH");
                string fp = (it.FilePath == null ? "" : it.FilePath.Trim());
                if (fp == "0" || fp == "?" || fp == "???") it.FilePath = "";
                it.Download = Json.GetString(d, "DOWNLOAD");
                it.Check = CheckKind.FileExists;
                it.Kind = ItemKind.SystemDll;
                it.Arch = is64 ? "x64" : "x86";
                it.Name = it.FileName;
                it.Version = "—";
                list.Add(it);
            }
            Log.Info("catalog SystemDll " + Path.GetFileName(file) + " items=" + list.Count);
            return list;
        }

        public static List<CatalogItem> LoadRuntime(string category, string jsonFile, bool is64)
        {
            string path = Path.Combine(Path.Combine(Paths.JsonDir, "VCDirectxJson"),
                jsonFile + "_" + (is64 ? "64" : "32") + ".json");
            List<CatalogItem> list = new List<CatalogItem>();
            foreach (Dictionary<string, object> d in ReadArray(path))
            {
                CatalogItem it = new CatalogItem();
                it.Category = category;
                it.DisplayName = Json.GetString(d, "DISPLAYNAME");
                string dn = (it.DisplayName == null ? "" : it.DisplayName.Trim());
                if (dn == "0" || dn == "???" || dn == "?" ) it.DisplayName = "";
                it.DisplayVersion = Json.GetString(d, "DISPLAYVERIONS", "DISPLAYVERSION");
                it.FileName = Json.GetString(d, "FILENAME");
                it.FilePath = Json.GetString(d, "FILEPATH");
                it.Download = Json.GetString(d, "DOWNLOAD");
                it.CommandLine = Json.GetString(d, "COMMANDLINE");
                it.Wow6432Node = Json.GetString(d, "WOW6432NODE", "WOW6434NODE");
                it.Match = Json.GetString(d, "MATCH");
                it.DownloadDll = Json.GetString(d, "DOWNLOADDLL", "DOWNLOAD_DLL");
                if (!string.IsNullOrEmpty(it.Match)) it.Check = CheckKind.MatchPattern;
                else if (!string.IsNullOrEmpty(it.FilePath)) it.Check = CheckKind.FileExists;
                else if (!string.IsNullOrEmpty(it.DisplayName)) it.Check = CheckKind.RegistryDisplayName;
                else it.Check = CheckKind.Unknown;

                if (!string.IsNullOrEmpty(it.DownloadDll))
                {
                    it.Kind = ItemKind.SystemDll;     // 单文件下载，直接覆盖
                    it.Download = it.DownloadDll;
                }
                else
                {
                    it.Kind = KindOf(it.Download);
                }
                if (string.IsNullOrEmpty(it.FileName)) it.FileName = Path.GetFileName(SafeName(it.Download));
                it.Arch = SystemProbe.ArchOfName(it.DisplayVersion + " " + it.DisplayName);
                if (it.Arch.Length == 0) it.Arch = is64 ? "x64" : "x86";
                it.Name = CleanName(it.DisplayVersion);
                if (it.Name.Length == 0) it.Name = CleanName(it.DisplayName);
                it.Version = "—";
                list.Add(it);
            }
            Log.Info("catalog " + category + " " + Path.GetFileName(path) + " items=" + list.Count);
            return list;
        }

        private static string CleanName(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            string r = s;
            r = Regex.Replace(r, @"\s*\((x64|x86)\)\s*$", string.Empty, RegexOptions.IgnoreCase);
            r = Regex.Replace(r, @"\s*(x64|x86)\s*$", string.Empty, RegexOptions.IgnoreCase);
            r = Regex.Replace(r, @"\s+", " ");
            return r.Trim();
        }

        private static ItemKind KindOf(string url)
        {
            string name = SafeName(url).ToLowerInvariant();
            if (name.EndsWith(".7z") || name.EndsWith(".zip") || name.EndsWith(".rar")) return ItemKind.Archive;
            if (name.EndsWith(".exe") || name.EndsWith(".msi")) return ItemKind.Installer;
            return ItemKind.SystemDll;
        }

        private static string SafeName(string url)
        {
            try { return new Uri(url).LocalPath; }
            catch { return url; }
        }

        private static List<Dictionary<string, object>> ReadArray(string file)
        {
            // 1) 服务端优先：/api/v1/catalog?key=<文件名去扩展名>
            string server = Config.ServerUrl;
            if (!string.IsNullOrEmpty(server))
            {
                string key = Path.GetFileNameWithoutExtension(file);
                string body = ApiClient.GetRaw(server + "/api/v1/catalog?key=" + Uri.EscapeDataString(key));
                if (!string.IsNullOrEmpty(body))
                {
                    try
                    {
                        List<Dictionary<string, object>> remote = Json.ParseObjectArray(body);
                        if (remote.Count > 0)
                        {
                            Log.Info("catalog remote " + key + " items=" + remote.Count);
                            return remote;
                        }
                    }
                    catch (Exception ex) { Log.Error("remote catalog parse failed: " + key, ex); }
                }
            }

            // 2) 本地兜底
            try
            {
                if (!File.Exists(file)) { Log.Info("catalog missing: " + file); return new List<Dictionary<string, object>>(); }
                return Json.ParseObjectArray(File.ReadAllText(file, Encoding.UTF8));
            }
            catch (Exception ex)
            {
                Log.Error("catalog parse failed: " + file, ex);
                return new List<Dictionary<string, object>>();
            }
        }

        /// <summary>对应原程序 SystemManageHelper.GetOsVersionInfo 的粗分类</summary>
        [System.Runtime.InteropServices.DllImport("ntdll.dll", SetLastError = true)]
        private static extern int RtlGetVersion(ref OsVersionInfo versionInfo);

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct OsVersionInfo
        {
            public int dwOSVersionInfoSize;
            public int dwMajorVersion;
            public int dwMinorVersion;
            public int dwBuildNumber;
            public int dwPlatformId;
            [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 128)]
            public string szCSDVersion;
        }

        /// <summary>真实的 OS 版本号（不受 manifest/兼容层影响）</summary>
        public static Version RealOsVersion()
        {
            OsVersionInfo info = new OsVersionInfo();
            info.dwOSVersionInfoSize = System.Runtime.InteropServices.Marshal.SizeOf(info);
            try
            {
                if (RtlGetVersion(ref info) == 0)
                    return new Version(info.dwMajorVersion, info.dwMinorVersion, info.dwBuildNumber);
            }
            catch { }
            return Environment.OSVersion.Version;
        }

        public static string OsTag()
        {
            Version v = RealOsVersion();
            if (v.Major >= 10)
            {
                // Win11 与 Win10 同为 10.x，用 build 号区分
                if (v.Build >= 22000) return "win11";
                return "win10";
            }
            if (v.Major == 6 && v.Minor >= 3) return "win8";
            if (v.Major == 6 && v.Minor == 2) return "win8";
            if (v.Major == 6 && v.Minor == 1) return "win7";
            return "win10";
        }
    }

    // ── 系统探测 ────────────────────────────────────────────────────────────
    public static class SystemProbe
    {
        private static List<string> _displayNames;
        private static List<KeyValuePair<string, string>> _uninstall;

        public static bool IsElevated
        {
            get
            {
                try
                {
                    WindowsIdentity id = WindowsIdentity.GetCurrent();
                    WindowsPrincipal p = new WindowsPrincipal(id);
                    return p.IsInRole(WindowsBuiltInRole.Administrator);
                }
                catch { return false; }
            }
        }

        /// <summary>
        /// 对应 SystemManageHelper.CheckRegisetrIsHasInstall / CheckIsAllRegistExist。
        /// 三级匹配：①通配正则 ②归一化后互相包含 ③产品族（Visual C++ / .NET + 年份 + 架构）。
        /// 目录里的名字和实际卸载项经常不一致（例："Visual C++ 2015-2022 x64" vs
        /// "Visual C++ 2022 X64 Additional Runtime - 14.42.34438"），只靠精确/通配匹配会漏。
        /// </summary>
        public static bool IsInstalledByName(string pattern)
        {
            if (string.IsNullOrWhiteSpace(pattern)) return false;
            List<string> names = UninstallDisplayNames();
            Regex re = WildcardToRegex(pattern);
            string want = Normalize(pattern);
            string wantArch = ArchOf(pattern);

            foreach (string n in names)
            {
                if (string.IsNullOrEmpty(n)) continue;
                if (re.IsMatch(n)) return true;

                string got = Normalize(n);
                if (want.Length > 0 && (got.Contains(want) || want.Contains(got))) return true;

                if (SameFamily(want, got, wantArch)) return true;
            }
            return false;
        }

        /// <summary>产品族匹配：同一产品（Visual C++ / .NET Desktop / .NET Runtime）+ 年份有交集 + 架构一致</summary>
        private static bool SameFamily(string want, string got, string wantArch)
        {
            string gotArch = ArchOf(got);
            if (wantArch.Length > 0 && gotArch.Length > 0 && wantArch != gotArch) return false;

            string[] products = { "visualc++", "netdesktopruntime", "netruntime", "netframework" };
            string wp = null, gp = null;
            foreach (string p in products)
            {
                if (wp == null && want.IndexOf(p, StringComparison.Ordinal) >= 0) wp = p;
                if (gp == null && got.IndexOf(p, StringComparison.Ordinal) >= 0) gp = p;
            }
            if (wp == null || gp == null || wp != gp) return false;

            List<int> wy = Years(want);
            List<int> gy = Years(got);
            if (wy.Count == 0 || gy.Count == 0) return true;   // 没年份可比，产品+架构一致即认

            foreach (int a in wy)
                foreach (int b in gy)
                    if (a == b) return true;
            return false;
        }

        private static List<int> Years(string normalized)
        {
            List<int> years = new List<int>();
            foreach (Match m in Regex.Matches(normalized, "(?<!\\d)(20[0-9]{2})(?!\\d)"))
            {
                int y = Convert.ToInt32(m.Groups[1].Value);
                if (!years.Contains(y)) years.Add(y);
            }
            // "2015-2022" 这类区间：补全中间年份
            foreach (Match m in Regex.Matches(normalized, "(20\\d{2})-(20\\d{2})"))
            {
                int a = Convert.ToInt32(m.Groups[1].Value);
                int b = Convert.ToInt32(m.Groups[2].Value);
                for (int y = a; y <= b && y - a < 20; y++)
                    if (!years.Contains(y)) years.Add(y);
            }
            return years;
        }

        private static string Normalize(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            string r = s.ToLowerInvariant();
            r = Regex.Replace(r, @"\b\d+(\.\d+)+\b", " ");     // 去掉 11.0.61030 这类版本号
            r = Regex.Replace(r, @"[\(\)\[\]\{\}\-–—_,\.:]", " ");
            r = Regex.Replace(r, @"\s+", " ");
            return r.Trim();
        }

        private static Regex WildcardToRegex(string pattern)
        {
            string rx = Regex.Escape(pattern.Trim()).Replace(@"\*", ".*").Replace(@"\?", ".");
            return new Regex("^" + rx + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        private static List<string> UninstallDisplayNames()
        {
            if (_displayNames != null) return _displayNames;
            List<string> list = new List<string>();
            string root = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

            // 注意：本进程是 x86，直接 Registry.LocalMachine.OpenSubKey("SOFTWARE\\...")
            // 会被重定向到 WOW6432Node，只能看到 32 位安装项 —— 必须显式指定 64/32 两种视图。
            RegistryView[] views = new RegistryView[] { RegistryView.Registry64, RegistryView.Registry32 };
            foreach (RegistryView view in views)
            {
                try
                {
                    using (RegistryKey baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view))
                    using (RegistryKey k = baseKey.OpenSubKey(root))
                    {
                        if (k == null) continue;
                        foreach (string sub in k.GetSubKeyNames())
                        {
                            try
                            {
                                using (RegistryKey sk = k.OpenSubKey(sub))
                                {
                                    if (sk == null) continue;
                                    object v = sk.GetValue("DisplayName");
                                    if (v != null) list.Add(Convert.ToString(v));
                                }
                            }
                            catch { }
                        }
                    }
                }
                catch (Exception ex) { Log.Error("enumerate uninstall " + view, ex); }
            }
            _displayNames = list;
            Log.Info("uninstall entries=" + list.Count + " (x64+x86 views)");
            return list;
        }

        /// <summary>已安装项里匹配到的 DisplayVersion（未安装返回空）</summary>
        public static string FindInstalledVersion(string pattern)
        {
            if (string.IsNullOrWhiteSpace(pattern)) return string.Empty;
            List<string> names = UninstallDisplayNames();
            Regex re = WildcardToRegex(pattern);
            string want = Normalize(pattern);
            string wantArch = ArchOf(pattern);
            foreach (string n in names)
            {
                if (string.IsNullOrEmpty(n)) continue;
                string got = Normalize(n);
                bool hit = re.IsMatch(n) || (want.Length > 0 && (got.Contains(want) || want.Contains(got))) || SameFamily(want, got, wantArch);
                if (!hit) continue;
                string ver = VersionFromUninstall(n);
                if (!string.IsNullOrEmpty(ver)) return ver;
            }
            return string.Empty;
        }

        private static string VersionFromUninstall(string displayName)
        {
            Match m = Regex.Match(displayName, "(?<!\\d)(\\d+\\.\\d+\\.\\d+(\\.\\d+)?)(?!\\d)");
            return m.Success ? m.Groups[1].Value : string.Empty;
        }

        /// <summary>从 "… Visual C++ 2012 x64" 里取架构</summary>
        public static string ArchOfName(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            if (Regex.IsMatch(s, "x64|amd64", RegexOptions.IgnoreCase)) return "x64";
            if (Regex.IsMatch(s, "x86|win32", RegexOptions.IgnoreCase)) return "x86";
            return string.Empty;
        }

        public static string ArchOf(string s)
        {
            return ArchOfName(s);
        }

        public static string MachineGuid()
        {
            try
            {
                using (RegistryKey k = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
                                                    .OpenSubKey(@"SOFTWARE\Microsoft\Cryptography"))
                {
                    if (k != null)
                    {
                        object v = k.GetValue("MachineGuid");
                        if (v != null) return Convert.ToString(v);
                    }
                }
            }
            catch { }
            return "unknown";
        }

        public static string OsCaption()
        {
            try
            {
                using (ManagementObjectSearcher s = new ManagementObjectSearcher("SELECT Caption FROM Win32_OperatingSystem"))
                {
                    foreach (ManagementObject o in s.Get())
                    {
                        object c = o["Caption"];
                        if (c != null) return Convert.ToString(c);
                    }
                }
            }
            catch { }
            return Environment.OSVersion.ToString();
        }
    }

    // ── 扫描（对应 ScanHelper / SystemManageHelper） ─────────────────────────
    public static class Scanner
    {
        public static void Scan(IEnumerable<CatalogItem> items, Action<CatalogItem> onScanned)
        {
            Prepare(items);
            foreach (CatalogItem it in items)
            {
                it.State = IsAbsent(it) ? ItemState.Absent : ItemState.Present;
                it.Detail = it.State == ItemState.Present ? "已安装" : "缺失";
                if (it.State == ItemState.Present && it.Kind != ItemKind.SystemDll)
                {
                    string v = SystemProbe.FindInstalledVersion(
                        string.IsNullOrEmpty(it.DisplayName) ? it.DisplayVersion : it.DisplayName);
                    if (!string.IsNullOrEmpty(v)) it.Version = v;
                }
                if (onScanned != null) onScanned(it);
            }
        }

        /// <summary>
        /// 该条目的文件名是否是"构建专属别名"（如 69fe178f-…-eventlogservice.dll）。
        /// 这类名字每个 Windows 构建都可能不同，按名字判存在性只会产生假缺失。
        /// </summary>
        private static bool IsBuildSpecific(string fileName)
        {
            if (string.IsNullOrEmpty(fileName)) return false;
            return Regex.IsMatch(fileName, @"^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}_");
        }

        private static readonly Dictionary<string, bool> MatchCache =
            new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        private static List<string> _sxsDirs;      // WinSxS 顶层目录名（小写）

        /// <summary>扫描前预处理：一次性拿到 WinSxS 顶层目录名，供通配匹配使用</summary>
        public static void Prepare(IEnumerable<CatalogItem> items)
        {
            bool need = false;
            foreach (CatalogItem it in items)
            {
                if (it.Check == CheckKind.MatchPattern && it.Match != null && it.Match.IndexOf('*') >= 0)
                {
                    need = true;
                    break;
                }
            }
            if (!need) return;

            List<string> dirs = new List<string>();
            try
            {
                string root = Path.Combine(Path.Combine(Paths.SystemDrive, "Windows"), "WinSxS");
                if (Directory.Exists(root))
                {
                    foreach (string d in Directory.GetDirectories(root))
                        dirs.Add(Path.GetFileName(d).ToLowerInvariant());
                }
            }
            catch (Exception ex) { Log.Error("WinSxS index failed", ex); }
            _sxsDirs = dirs;
            Log.Info("WinSxS dirs=" + dirs.Count);
        }

        /// <summary>MATCH 形如 *:\WINDOWS\SysWOW64\VCRUNTIME140.DLL 或 *:\WINDOWS\WINSXS\*\ATL80.DLL</summary>
        private static bool MatchExists(string pattern)
        {
            if (string.IsNullOrEmpty(pattern)) return true;
            string p = pattern;
            if (p.Length > 1 && p[1] == ':') p = Paths.SystemDrive + p.Substring(2);   // *:\... -> C:\...
            p = Paths.ResolveX(p);

            bool cached;
            if (MatchCache.TryGetValue(p, out cached)) return cached;

            bool found;
            if (p.IndexOf('*') < 0 && p.IndexOf('?') < 0)
            {
                found = File.Exists(p);                       // 普通路径，直接判
            }
            else
            {
                string leaf = Path.GetFileName(p);
                string stem = Path.GetFileNameWithoutExtension(leaf).ToLowerInvariant();
                if (_sxsDirs == null || _sxsDirs.Count == 0)
                {
                    found = true;                              // 没有索引：按"存在"处理，避免误报缺失
                }
                else
                {
                    found = false;
                    foreach (string d in _sxsDirs)
                    {
                        if (d.IndexOf(stem, StringComparison.Ordinal) >= 0) { found = true; break; }
                    }
                }
            }

            MatchCache[p] = found;
            return found;
        }

        public static bool IsAbsent(CatalogItem it)
        {
            if (IsBuildSpecific(it.FileName)) return false;
            if (it.Check == CheckKind.Unknown) return false;
            if (it.Check == CheckKind.MatchPattern) return !MatchExists(it.Match);
            bool fileMissing = false, nameMissing = false;
            if (it.Check == CheckKind.FileExists || it.Check == CheckKind.Both)
            {
                string p = Paths.ResolveX(it.FilePath);
                fileMissing = string.IsNullOrEmpty(p) || !File.Exists(p);
            }
            if (it.Check == CheckKind.RegistryDisplayName || it.Check == CheckKind.Both)
            {
                nameMissing = !SystemProbe.IsInstalledByName(it.DisplayName);
            }
            if (it.Check == CheckKind.FileExists) return fileMissing;
            if (it.Check == CheckKind.RegistryDisplayName) return nameMissing;
            return fileMissing && nameMissing;
        }
    }

    // ── 授权（重建版：本地 license.json，不联网、不做设备绑定） ───────────────
    public class LicenseInfo
    {
        public string Plan { get; set; }
        public string Email { get; set; }
        public string Expire { get; set; }
        public string Machine { get; set; }
        public string Note { get; set; }
    }

    public static class License
    {
        private static LicenseInfo _current;

        public static LicenseInfo Current
        {
            get
            {
                if (_current == null) _current = Load();
                return _current;
            }
        }

        public static bool IsPro
        {
            get { return Current != null && string.Equals(Current.Plan, "Professional", StringComparison.OrdinalIgnoreCase); }
        }

        public static string BadgeText
        {
            get { return IsPro ? "专业版 · 重建" : "免费版 · 重建"; }
        }

        private static LicenseInfo Load()
        {
            try
            {
                if (File.Exists(Paths.LicenseFile))
                {
                    LicenseInfo li = JsonConvert<LicenseInfo>(File.ReadAllText(Paths.LicenseFile, Encoding.UTF8));
                    if (li != null) return li;
                }
            }
            catch (Exception ex) { Log.Error("license read failed", ex); }

            LicenseInfo fresh = new LicenseInfo();
            fresh.Plan = "Professional";
            fresh.Email = "rebuild@local";
            fresh.Expire = "lifetime";
            fresh.Machine = SystemProbe.MachineGuid();
            fresh.Note = "clean-room rebuild, no activation server";
            try
            {
                File.WriteAllText(Paths.LicenseFile, Json.Stringify(fresh), Encoding.UTF8);
                Log.Info("license.json created");
            }
            catch (Exception ex) { Log.Error("license write failed", ex); }
            return fresh;
        }

        private static T JsonConvert<T>(string text) where T : class
        {
            // JavaScriptSerializer 反序列化为字典再回填，保持零依赖
            Dictionary<string, object> d = Json.Parse(text) as Dictionary<string, object>;
            if (d == null) return null;
            T obj = Activator.CreateInstance<T>();
            foreach (System.Reflection.PropertyInfo p in typeof(T).GetProperties())
            {
                object v;
                if (d.TryGetValue(p.Name, out v) && v != null)
                    p.SetValue(obj, Convert.ToString(v), null);
            }
            return obj;
        }
    }
}
