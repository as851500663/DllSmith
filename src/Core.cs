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
using System.Windows;
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

    // ── 诊断开关（只用于性能/回归定位，生产环境不设 DLLSMITH_DIAG 即全部关闭） ──
    // 用法：set DLLSMITH_DIAG=nobanner,nofooter  然后启动 exe 复测空闲 CPU
    public static class Diag
    {
        private static readonly HashSet<string> On = Load();

        private static HashSet<string> Load()
        {
            HashSet<string> s = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                string v = Environment.GetEnvironmentVariable("DLLSMITH_DIAG");
                if (!string.IsNullOrEmpty(v))
                    foreach (string p in v.Split(','))
                        if (p.Trim().Length > 0) s.Add(p.Trim());
            }
            catch { }
            return s;
        }

        public static bool Has(string name) { return On.Contains(name); }
        public static bool Any { get { return On.Count > 0; } }
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
#if DS_TESTBUILD
        public const string Version = "1.4.0-test";     // 测试构建：名字/版本都带 -test，防止误发
#else
        public const string Version = "1.4.0";
#endif
        public const string License = "GPL-3.0";
        public const string SiteUrl  = "https://123.57.94.141:9443/download";
        public const string BuildTag = "opensource";
    }

    // ── 客户端配置（config.json：{"server":"https://host:port","channel":"stable"}） ──
    public static class Config
    {
        private static bool _loaded;
        private static string _server;
        private static string _channel = "stable";
        private static bool _telemetry = true;
        private static bool _allowInsecure;

        /// <summary>目录服务地址（1.4.0 起默认 https；http 只作为"离线模式"判据，不做明文信任）</summary>
        public static string ServerUrl
        {
            get
            {
                EnsureLoaded();
                return _server;
            }
        }

        /// <summary>更新通道：stable / beta</summary>
        public static string Channel
        {
            get { EnsureLoaded(); return _channel; }
        }

        /// <summary>是否参与匿名在线统计（默认开启，可在 config.json 里 "telemetry": false 关闭）</summary>
        public static bool Telemetry
        {
            get { EnsureLoaded(); return _telemetry; }
        }

        /// <summary>允许非 https 目录服务（仅供本机联调；正式包默认 false）</summary>
        public static bool AllowInsecureServer
        {
            get { EnsureLoaded(); return _allowInsecure; }
        }

        /// <summary>目录服务是否可用于"可信"场景（https 或显式放行）</summary>
        public static bool ServerUsable
        {
            get
            {
                string s = ServerUrl;
                if (string.IsNullOrEmpty(s)) return false;
                return UpdateTrust.IsHttps(s) || AllowInsecureServer;
            }
        }

        private static void EnsureLoaded()
        {
            if (_loaded) return;
            _loaded = true;
            try
            {
                string path = Path.Combine(Paths.BaseDir, "config.json");
                if (File.Exists(path))
                {
                    Dictionary<string, object> d = Json.Parse(File.ReadAllText(path, Encoding.UTF8))
                        as Dictionary<string, object>;
                    if (d != null)
                    {
                        _server = Json.GetString(d, "server").TrimEnd('/');
                        string ch = Json.GetString(d, "channel");
                        if (!string.IsNullOrEmpty(ch)) _channel = ch.Trim();
                        object tel;
                        if (d.TryGetValue("telemetry", out tel) && tel is bool) _telemetry = (bool)tel;
                        object ins;
                        if (d.TryGetValue("allow_insecure_server", out ins) && ins is bool) _allowInsecure = (bool)ins;
                    }
                }
            }
            catch (Exception ex) { Log.Error("config.json parse failed", ex); }

            // 1.3.1 的 config.json 写的是 http://…:8787；1.4.0 起迁移到 https://…:9443
            // （不改用户文件，只在内存里升级；未知端口映射不到就不升级，交由 ServerUsable 判离线）
            if (!string.IsNullOrEmpty(_server) && _server.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                && !_allowInsecure)
            {
                string upgraded = UpgradeToHttps(_server);
                if (upgraded != null)
                {
                    Log.Info("config server upgraded to https: " + ApiClient.SafeUrl(upgraded));
                    _server = upgraded;
                }
                else
                {
                    Log.Info("config server kept as http (no https mapping): " + ApiClient.SafeUrl(_server));
                }
            }
            if (!string.IsNullOrEmpty(_server)) Log.Info("server catalog: " + ApiClient.SafeUrl(_server)
                + " channel=" + _channel + " telemetry=" + _telemetry);
        }

        /// <summary>明文端口到 TLS 端口的映射（服务端 8787 明文 / 9443 TLS）</summary>
        public static string UpgradeToHttps(string httpUrl)
        {
            try
            {
                Uri u = new Uri(httpUrl);
                int port = u.IsDefaultPort ? 8787 : u.Port;
                int tlsPort = port == 8787 ? 9443 : (port == 80 ? 443 : port);
                if (tlsPort == port && port != 9443) { /* 端口无映射时仍按同端口尝试 */ }
                UriBuilder b = new UriBuilder("https", u.Host, tlsPort);
                b.Path = u.AbsolutePath;
                return b.Uri.ToString().TrimEnd('/');
            }
            catch { return null; }
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
        public string PkgUrl { get; set; }   // 目录里原始 DOWNLOAD（未被子文件地址覆盖）
        public string DownloadDll { get; set; }

        /// <summary>服务端下载源状态（catalog 的 download_status）：ok / dead:… / dead_source …</summary>
        public string DownloadStatus { get; set; }

        /// <summary>下载源已失效：允许扫描展示，但不进修复队列（captain 2026-11-14 定案）</summary>
        public bool SourceDead
        {
            get
            {
                return !string.IsNullOrEmpty(DownloadStatus)
                    && DownloadStatus.StartsWith("dead", StringComparison.OrdinalIgnoreCase);
            }
        }

        /// <summary>是否驱动文件（.sys/.drv）：留在列表里但默认不勾选、不修复</summary>
        public bool IsDriver { get; set; }

        /// <summary>条目所属目录 key（用于按 key 校验签名 manifest）</summary>
        public string SourceKey { get; set; }

        /// <summary>条目自带 oss_key（服务端 1.4.0 新增；优先用它换签名地址）</summary>
        public string OssKey { get; set; }

        private string _sha256 = string.Empty;

        /// <summary>条目级下载哈希（明文口径，来自签名 manifest 或条目 SHA256 字段）；空 = 未被哈希覆盖</summary>
        public string Sha256
        {
            get { return _sha256; }
            set
            {
                _sha256 = value == null ? string.Empty : value.Trim().ToLowerInvariant();
                Raise("Sha256");
                Raise("HashCovered");
                Raise("HashText");
            }
        }

        /// <summary>该条目是否有哈希可用（true 才做逐字节校验）</summary>
        public bool HashCovered
        {
            get { return _sha256.Length > 0; }
        }

        /// <summary>列表展示用：有哈希 / 哈希未覆盖</summary>
        public string HashText
        {
            get { return _sha256.Length > 0 ? "已校验" : "哈希未覆盖"; }
        }

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
            set { _state = value; Raise("State"); Raise("StateText"); Raise("IsMissing"); Raise("StateKey"); }
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
            set { _percent = value; Raise("Percent"); Raise("PercentText"); Raise("HasPercent"); }
        }

        /// <summary>行内进度文案：0 表示未开始</summary>
        public string PercentText
        {
            get { return _percent <= 0 ? "" : (_percent >= 100 ? "完成" : _percent + "%"); }
        }

        public Visibility HasPercent
        {
            get { return _percent > 0 ? Visibility.Visible : Visibility.Collapsed; }
        }

        public string StateText
        {
            get
            {
                if (SourceDead) return "源缺失";
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

        /// <summary>
        /// 状态稳定键（ASCII）：DataTemplate 的 DataTrigger 绑这个而不是中文 StateText，
        /// 这样文案可以自由本地化而不会把触发器打散。
        /// </summary>
        public string StateKey
        {
            get
            {
                if (SourceDead) return "dead";
                switch (State)
                {
                    case ItemState.Absent: return "absent";
                    case ItemState.Present: return "present";
                    case ItemState.Downloading: return "downloading";
                    case ItemState.Installing: return "installing";
                    case ItemState.Done: return "done";
                    case ItemState.Failed: return "failed";
                    default: return "unknown";
                }
            }
        }

        /// <summary>
        /// 排序键（配合 CollectionViewSource 原地排序，不换 ItemsSource）：
        /// 缺失置顶 -> 正常 -> 未判定垫底。
        /// </summary>
        public int SortRank
        {
            get
            {
                if (State == ItemState.Absent || State == ItemState.Failed) return 0;
                if (State == ItemState.Unknown) return 2;
                return 1;
            }
        }

        /// <summary>第四列：运行库显示版本，系统 DLL 显示目标路径（P1：系统 DLL 页缺路径列）</summary>
        public string InfoText
        {
            get
            {
                if (Kind == ItemKind.SystemDll && !string.IsNullOrEmpty(FilePath))
                {
                    string p = Paths.ResolveX(FilePath);
                    string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                    if (!string.IsNullOrEmpty(win) && p.StartsWith(win, StringComparison.OrdinalIgnoreCase))
                        return "…" + p.Substring(win.Length);
                    return p;
                }
                return string.IsNullOrEmpty(Version) ? "—" : Version;
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

    // ── 目录缓存（本地优先 + 并发预取 + 失败缓存 5 分钟） ────────────────────
    //  性能审计 §1：原实现"远程优先且串行"，断网时 6 个 JSON 各等 2s，扫描墙钟 12.8s。
    //  现在：扫描永远读本地（0ms），启动时 6 个 key 并发拉远程，成功才替换；
    //        失败结果记 5 分钟，避免每次进页面都去撞超时。
    public static class CatalogCache
    {
        private static readonly Dictionary<string, List<Dictionary<string, object>>> Ok =
            new Dictionary<string, List<Dictionary<string, object>>>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, DateTime> FailUntil =
            new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, DateTime> OkAt =
            new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, Dictionary<string, string>> Headers =
            new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, Dictionary<string, object>> Manifests =
            new Dictionary<string, Dictionary<string, object>>(StringComparer.OrdinalIgnoreCase);
        private static readonly object Sync = new object();
        private static readonly HashSet<string> InFlight = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>单次失败的冷却时间</summary>
        public static readonly TimeSpan FailCooldown = TimeSpan.FromMinutes(5);

        /// <summary>调度完成（在做 UI 线程上触发；用于"目录已更新"提示）</summary>
        public static event Action<int, int> Refreshed;

        public static List<Dictionary<string, object>> Get(string key)
        {
            lock (Sync)
            {
                List<Dictionary<string, object>> v;
                if (Ok.TryGetValue(key, out v)) return v;
                return null;
            }
        }

        /// <summary>目录响应头（X-Manifest-Count / -Lines-Sha256 / -Sig / -Kid …）</summary>
        public static Dictionary<string, string> GetHeaders(string key)
        {
            lock (Sync)
            {
                Dictionary<string, string> h;
                if (key != null && Headers.TryGetValue(key, out h)) return h;
                return null;
            }
        }

        /// <summary>catalog 响应内联的 manifest 对象（?with_manifest=1 信封），没有则 null</summary>
        public static Dictionary<string, object> GetManifest(string key)
        {
            lock (Sync)
            {
                Dictionary<string, object> m;
                if (key != null && Manifests.TryGetValue(key, out m)) return m;
                return null;
            }
        }

        public static DateTime? LastSuccess(string key)
        {
            lock (Sync)
            {
                DateTime t;
                if (OkAt.TryGetValue(key, out t)) return t;
            }
            return null;
        }

        /// <summary>并发预取（每个 key 一个后台线程，最多 6 个；同一 key 不重复拉）</summary>
        public static void PrefetchAsync(IEnumerable<string> keys)
        {
            if (!Config.ServerUsable) return;      // 离线模式：不碰网络
            foreach (string k in keys)
            {
                if (string.IsNullOrEmpty(k)) continue;
                lock (Sync)
                {
                    if (InFlight.Contains(k)) continue;
                    DateTime until;
                    if (FailUntil.TryGetValue(k, out until) && until > DateTime.Now) continue;
                    if (Ok.ContainsKey(k)) continue;
                    InFlight.Add(k);
                }
                string key = k;
                Thread t = new Thread(delegate() { Fetch(key); });
                t.IsBackground = true;
                t.Start();
            }
        }

        /// <summary>强制刷新（用户点「重新检查」时只清失败缓存，不重复拉已成功的）</summary>
        public static void ClearFailureCache()
        {
            lock (Sync) FailUntil.Clear();
        }

        private static void Fetch(string key)
        {
            string body = string.Empty;
            try
            {
                string url = Config.ServerUrl + "/api/v1/catalog?key=" + Uri.EscapeDataString(key);
                Dictionary<string, string> headers;
                // 一次请求同时拿 items + 内联 manifest（?with_manifest=1）；不支持则回退纯数组
                body = ApiClient.GetRawWithHeaders(url + "&with_manifest=1", ApiClient.MetaTimeoutMs, out headers);
                Dictionary<string, object> manifest = null;
                List<Dictionary<string, object>> items = ParseEnvelope(body, out manifest);
                if (items == null || items.Count == 0)
                {
                    body = ApiClient.GetRawWithHeaders(url, ApiClient.MetaTimeoutMs, out headers);
                    items = ParseItems(body);
                }
                lock (Sync)
                {
                    InFlight.Remove(key);
                    if (items != null && items.Count > 0)
                    {
                        Ok[key] = items;
                        OkAt[key] = DateTime.Now;
                        if (headers != null) Headers[key] = headers;
                        if (manifest != null) Manifests[key] = manifest;
                        FailUntil.Remove(key);
                    }
                    else
                    {
                        FailUntil[key] = DateTime.Now.Add(FailCooldown);
                    }
                }
                if (items != null && items.Count > 0) RaiseRefreshed();
                else Log.Info("catalog remote unavailable (cached 5min): " + key);
            }
            catch (Exception ex)
            {
                lock (Sync)
                {
                    InFlight.Remove(key);
                    FailUntil[key] = DateTime.Now.Add(FailCooldown);
                }
                Log.Error("catalog remote failed: " + key, ex);
            }
        }

        /// <summary>
        /// 信封形态：{"items":[...],"manifest":{...}}（?with_manifest=1）。
        /// 解析成功返回 items 并输出 manifest 字典；不是信封则返回 null。
        /// </summary>
        private static List<Dictionary<string, object>> ParseEnvelope(string body,
            out Dictionary<string, object> manifest)
        {
            manifest = null;
            if (string.IsNullOrEmpty(body)) return null;
            string trimmed = body.Trim();
            if (!trimmed.StartsWith("{", StringComparison.Ordinal)) return null;
            Dictionary<string, object> d = Json.Parse(trimmed) as Dictionary<string, object>;
            if (d == null) return null;
            object itemsObj, manObj;
            if (!d.TryGetValue("items", out itemsObj) || !(itemsObj is object[])) return null;
            if (d.TryGetValue("manifest", out manObj)) manifest = manObj as Dictionary<string, object>;
            return Json.ParseObjectArray(Json.Stringify(itemsObj));
        }

        /// <summary>兼容两种下发形态：JSON 数组，或 `{"items":[...],"manifest":{...}}` 信封。</summary>
        private static List<Dictionary<string, object>> ParseItems(string body)
        {
            if (string.IsNullOrEmpty(body)) return null;
            string trimmed = body.Trim();
            if (trimmed.StartsWith("[", StringComparison.Ordinal))
                return Json.ParseObjectArray(trimmed);
            if (trimmed.StartsWith("{", StringComparison.Ordinal))
            {
                Dictionary<string, object> d = Json.Parse(trimmed) as Dictionary<string, object>;
                if (d == null) return null;
                object items;
                if (d.TryGetValue("items", out items) && items is object[])
                    return Json.ParseObjectArray(body);
            }
            return null;
        }

        /// <summary>从 catalog 响应头构造内联 manifest 字段（省一次 /api/v1/manifest 往返）</summary>
        public static Dictionary<string, object> InlineManifestFromHeaders(Dictionary<string, string> headers)
        {
            if (headers == null) return null;
            string sig = GetHeader(headers, "X-Manifest-Sig");
            if (string.IsNullOrEmpty(sig)) return null;
            Dictionary<string, object> d = new Dictionary<string, object>();
            d["manifest_sig"] = sig;
            d["manifest_lines_sha256"] = GetHeader(headers, "X-Manifest-Lines-Sha256");
            d["manifest_entries_sha256"] = GetHeader(headers, "X-Manifest-Entries-Sha256");
            d["manifest_count"] = GetHeader(headers, "X-Manifest-Count");
            d["manifest_kid"] = GetHeader(headers, "X-Manifest-Kid");
            d["manifest_alg"] = GetHeader(headers, "X-Manifest-Alg");
            return d;
        }

        private static string GetHeader(Dictionary<string, string> headers, string name)
        {
            string v;
            return headers.TryGetValue(name, out v) ? (v == null ? "" : v.Trim()) : "";
        }

        private static void RaiseRefreshed()
        {
            Action<int, int> h = Refreshed;
            if (h == null) return;
            int ok, total;
            lock (Sync) { ok = Ok.Count; total = Ok.Count + FailUntil.Count; }
            try { h(ok, total); } catch { }
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
            string srcKey = Path.GetFileNameWithoutExtension(file);
            int sysSkipped = 0;
            foreach (Dictionary<string, object> d in ReadArray(file))
            {
                CatalogItem it = new CatalogItem();
                it.Category = "系统DLL";
                it.SourceKey = srcKey;
                it.FileName = Json.GetString(d, "FILENAME");
                // P1：系统 DLL 页混入 .sys/.drv 驱动。驱动**保留在列表里**（否则条目数与签名清单
                // 2634 对不上，目录完整性断言会失败），但标记为驱动、默认不勾选、不可修复。
                string ext = Path.GetExtension(it.FileName == null ? "" : it.FileName);
                if (string.Equals(ext, ".sys", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(ext, ".drv", StringComparison.OrdinalIgnoreCase))
                {
                    sysSkipped++;
                    it.IsDriver = true;
                    it.Selected = false;
                }
                it.FilePath = Json.GetString(d, "FILEPATH");
                string fp = (it.FilePath == null ? "" : it.FilePath.Trim());
                if (fp == "0" || fp == "?" || fp == "???") it.FilePath = "";
                it.Download = Json.GetString(d, "DOWNLOAD");
                it.OssKey = Json.GetString(d, "OSS_KEY", "oss_key");
                it.DownloadStatus = Json.GetString(d, "download_status", "DOWNLOAD_STATUS");
                string sha = Json.GetString(d, "SHA256", "sha256");
                if (Hashes.LooksLikeSha256(sha)) it.Sha256 = sha;
                it.Check = CheckKind.FileExists;
                it.Kind = ItemKind.SystemDll;
                it.Arch = is64 ? "x64" : "x86";
                it.Name = it.FileName;
                it.Version = "—";
                list.Add(it);
            }
            Log.Info("catalog SystemDll " + Path.GetFileName(file) + " items=" + list.Count
                     + " (skipped .sys=" + sysSkipped + ")");
            return list;
        }

        public static List<CatalogItem> LoadRuntime(string category, string jsonFile, bool is64)
        {
            string path = Path.Combine(Path.Combine(Paths.JsonDir, "VCDirectxJson"),
                jsonFile + "_" + (is64 ? "64" : "32") + ".json");
            List<CatalogItem> list = new List<CatalogItem>();
            string srcKey2 = Path.GetFileNameWithoutExtension(path);
            foreach (Dictionary<string, object> d in ReadArray(path))
            {
                CatalogItem it = new CatalogItem();
                it.Category = category;
                it.SourceKey = srcKey2;
                it.DisplayName = Json.GetString(d, "DISPLAYNAME");
                string dn = (it.DisplayName == null ? "" : it.DisplayName.Trim());
                if (dn == "0" || dn == "???" || dn == "?" ) it.DisplayName = "";
                it.DisplayVersion = Json.GetString(d, "DISPLAYVERIONS", "DISPLAYVERSION");
                it.FileName = Json.GetString(d, "FILENAME");
                it.FilePath = Json.GetString(d, "FILEPATH");
                string fp2 = (it.FilePath == null ? "" : it.FilePath.Trim());
                if (fp2 == "0" || fp2 == "?" || fp2 == "???") it.FilePath = "";
                it.Download = Json.GetString(d, "DOWNLOAD");
                it.CommandLine = Json.GetString(d, "COMMANDLINE");
                it.Wow6432Node = Json.GetString(d, "WOW6432NODE", "WOW6434NODE");
                it.Match = Json.GetString(d, "MATCH");
                it.DownloadDll = Json.GetString(d, "DOWNLOADDLL", "DOWNLOAD_DLL");
                it.OssKey = Json.GetString(d, "OSS_KEY", "oss_key");
                it.DownloadStatus = Json.GetString(d, "download_status", "DOWNLOAD_STATUS");
                string sha2 = Json.GetString(d, "SHA256", "sha256");
                if (Hashes.LooksLikeSha256(sha2)) it.Sha256 = sha2;
                if (!string.IsNullOrEmpty(it.Match)) it.Check = CheckKind.MatchPattern;
                else if (!string.IsNullOrEmpty(it.FilePath)) it.Check = CheckKind.FileExists;
                else if (!string.IsNullOrEmpty(it.DisplayName)) it.Check = CheckKind.RegistryDisplayName;
                else it.Check = CheckKind.Unknown;

                if (!string.IsNullOrEmpty(it.DownloadDll))
                {
                    it.Kind = ItemKind.SystemDll;     // 单文件下载，直接覆盖
                    it.PkgUrl = it.Download;          // 保留目录原始包地址（manifest 键用它，不能用单文件地址）
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
            // 1) 服务端缓存优先（由 CatalogCache 在启动时并发预取；失败缓存 5 分钟）
            string key = Path.GetFileNameWithoutExtension(file);
            List<Dictionary<string, object>> remote = CatalogCache.Get(key);
            if (remote != null && remote.Count > 0)
            {
                Log.Info("catalog source=remote " + key + " items=" + remote.Count);
                return remote;
            }

            // 2) 本地兜底（立即返回，绝不为网络阻塞 UI / 扫描）
            try
            {
                if (!File.Exists(file)) { Log.Info("catalog missing: " + file); return new List<Dictionary<string, object>>(); }
                List<Dictionary<string, object>> local = Json.ParseObjectArray(File.ReadAllText(file, Encoding.UTF8));
                Log.Info("catalog source=local " + key + " items=" + local.Count);
                return local;
            }
            catch (Exception ex)
            {
                Log.Error("catalog parse failed: " + file, ex);
                return new List<Dictionary<string, object>>();
            }
        }

        /// <summary>
        /// 按 catalog key **精确**加载对应 JSON（不做 OS 架构映射）。
        /// 用于自检/普查要访问非当前系统目录（例如 .com 只存在于 SystemDll_win7_64）。
        /// </summary>
        public static List<CatalogItem> LoadCatalogFileExact(string key)
        {
            List<CatalogItem> list = new List<CatalogItem>();
            if (string.IsNullOrEmpty(key) || !Directory.Exists(Paths.JsonDir)) return list;
            string found = null;
            foreach (string f in Directory.GetFiles(Paths.JsonDir, key + ".json", SearchOption.AllDirectories))
            { found = f; break; }
            if (found == null) { Log.Info("catalog file not found: " + key); return list; }
            foreach (Dictionary<string, object> d in Json.ParseObjectArray(File.ReadAllText(found, Encoding.UTF8)))
            {
                CatalogItem it = new CatalogItem();
                it.SourceKey = key;
                it.Category = "census";
                it.FileName = Json.GetString(d, "FILENAME");
                it.DisplayName = Json.GetString(d, "DISPLAYNAME");
                it.FilePath = Json.GetString(d, "FILEPATH");
                it.Download = Json.GetString(d, "DOWNLOAD");
                it.OssKey = Json.GetString(d, "OSS_KEY", "oss_key");
                it.DownloadStatus = Json.GetString(d, "download_status", "DOWNLOAD_STATUS");
                string sha = Json.GetString(d, "SHA256", "sha256");
                if (Hashes.LooksLikeSha256(sha)) it.Sha256 = sha;
                it.Kind = ItemKind.SystemDll;
                it.Name = string.IsNullOrEmpty(it.DisplayName) ? it.FileName : it.DisplayName;
                list.Add(it);
            }
            return list;
        }
        /// <summary>按 catalog key 直接加载（--verify-manifest 自检用）</summary>
        public static List<CatalogItem> LoadByKey(string key)
        {
            if (string.IsNullOrEmpty(key)) return new List<CatalogItem>();
            bool is64 = Environment.Is64BitOperatingSystem;
            string k = key.Trim();
            if (k.StartsWith("SystemDll_", StringComparison.OrdinalIgnoreCase))
                return LoadSystemDll(is64);
            if (k.StartsWith("VC_Scan", StringComparison.OrdinalIgnoreCase))
                return LoadRuntime("运行库", "VC_Scan", is64);
            if (k.StartsWith("VC_DLL_Scan", StringComparison.OrdinalIgnoreCase))
                return LoadRuntime("运行库", "VC_DLL_Scan", is64);
            if (k.StartsWith("DirectX", StringComparison.OrdinalIgnoreCase))
                return LoadRuntime("DirectX", "DirectX", is64);
            if (k.StartsWith("downlevel", StringComparison.OrdinalIgnoreCase))
                return LoadRuntime("DownLevel", "downlevel", is64);
            if (k.StartsWith("NETWORK", StringComparison.OrdinalIgnoreCase))
                return LoadRuntime("Network", "NETWORK", is64);
            Log.Info("unknown catalog key: " + key);
            return new List<CatalogItem>();
        }

        /// <summary>当前系统需要的 6 个目录 key（启动时并发预取用）</summary>
        public static List<string> KeysForCurrentOs()
        {
            bool is64 = Environment.Is64BitOperatingSystem;
            string tag = OsTag();
            List<string> keys = new List<string>();
            keys.Add("SystemDll_" + tag + "_" + (is64 ? "64" : "32"));
            keys.Add("VC_Scan_" + (is64 ? "64" : "32"));
            keys.Add("VC_DLL_Scan_" + (is64 ? "64" : "32"));
            keys.Add("DirectX_" + (is64 ? "64" : "32"));
            keys.Add("downlevel_" + (is64 ? "64" : "32"));
            keys.Add("NETWORK_" + (is64 ? "64" : "32"));
            return keys;
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
                // 通配路径：先看 System32 / SysWOW64 有没有同名文件
                string leaf = Path.GetFileName(p);
                string win = Path.Combine(Paths.SystemDrive, "Windows");
                found = File.Exists(Path.Combine(win, "System32", leaf)) ||
                        File.Exists(Path.Combine(win, "SysWOW64", leaf));

                if (!found && _sxsDirs != null && _sxsDirs.Count > 0)
                {
                    // WinSxS 目录名形如 amd64_microsoft.vc80.atl_1fc8b3b...，
                    // 用"字母前缀 + 版本数字"两个 token 同时命中才算存在（atl80 -> atl + 80）
                    string stem = Path.GetFileNameWithoutExtension(leaf).ToLowerInvariant();
                    string alpha = new string(stem.TakeWhile(delegate(char c) { return c < '0' || c > '9'; }).ToArray());
                    string digits = new string(stem.Where(delegate(char c) { return c >= '0' && c <= '9'; }).ToArray());
                    if (alpha.Length == 0) alpha = stem;

                    foreach (string d in _sxsDirs)
                    {
                        bool ok = d.IndexOf(alpha, StringComparison.Ordinal) >= 0;
                        if (ok && digits.Length > 0) ok = d.IndexOf(digits, StringComparison.Ordinal) >= 0;
                        if (ok) { found = true; break; }
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
