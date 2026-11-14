// ============================================================================
//  4DDiG DLL Fixer — 重建工程
//  文件：ApiClient.cs  业务接口（AES 信封）/ 目录下载候选链 / 更新链路
//  对应原程序：DllRepair.Helpers.ApiHelper（check-crack-version / fix-config /
//              oss-download-url），信封 = AES-128-CBC/PKCS7 + base64(IV||CT)
//
//  1.4.0 变更（安全审计 A-01 / A-02 / A-03 / A-13 / A-09）：
//    · 更新记录必须验签（RSA-3072 / PKCS#1 v1.5 / SHA-256，kid=ds1）+ 强制 HTTPS，
//      验签或哈希任一失败立即中止，绝不 Process.Start（见 UpdateSecurity.cs）
//    · 下载候选顺序反转：oss_key 解析优先，catalog 的 DOWNLOAD 仅兜底，全部短超时
//    · 只接受 http/https，禁 file:/UNC，拒私有地址（SSRF / NTLM 外带）
//    · 心跳改成加盐哈希，日志脱敏（不再出现 MachineGuid 与带签名的 URL）
// ============================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace DllFix.Core
{
    public static class ApiClient
    {
        static ApiClient()
        {
            // .NET Framework 默认不启用 TLS1.2，必须显式打开，否则 HTTPS 直接失败
            try
            {
                ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            }
            catch { }
        }
        private const string Host = "https://4ddig-online.tenorshare.com/api/v1/dllfix/";
        private static readonly byte[] Key = Encoding.UTF8.GetBytes("DDIG4SECUREKEY16");

        /// <summary>元数据请求超时（审计 A-02：原来 8s + 串行，断网时白等 12s）</summary>
        public const int MetaTimeoutMs = 4000;

        /// <summary>单个候选的首字节超时（404/死链必须快速失败）</summary>
        public const int ProbeTimeoutMs = 5000;

        private static string Encrypt(string json)
        {
            using (Aes aes = Aes.Create())
            {
                aes.Key = Key;
                aes.GenerateIV();
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.PKCS7;

                using (MemoryStream ms = new MemoryStream())
                {
                    ms.Write(aes.IV, 0, aes.IV.Length);
                    using (CryptoStream cs = new CryptoStream(ms, aes.CreateEncryptor(), CryptoStreamMode.Write))
                    using (StreamWriter sw = new StreamWriter(cs))
                    {
                        sw.Write(json);
                    }
                    return Convert.ToBase64String(ms.ToArray());
                }
            }
        }

        private static string Decrypt(string b64)
        {
            byte[] raw = Convert.FromBase64String(b64);
            byte[] iv = new byte[16];
            byte[] ct = new byte[raw.Length - 16];
            Array.Copy(raw, 0, iv, 0, 16);
            Array.Copy(raw, 16, ct, 0, ct.Length);

            using (Aes aes = Aes.Create())
            {
                aes.Key = Key;
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.PKCS7;
                aes.IV = iv;
                using (MemoryStream ms = new MemoryStream(ct))
                using (CryptoStream cs = new CryptoStream(ms, aes.CreateDecryptor(), CryptoStreamMode.Read))
                using (StreamReader sr = new StreamReader(cs))
                {
                    return sr.ReadToEnd();
                }
            }
        }

        /// <summary>
        /// 在线心跳：POST /api/v1/ping（表单，不走 AES 信封）。
        /// 1.4.0：只上报加盐哈希后的安装标识（A-09），不上报原始 MachineGuid；
        ///        config.json 里 "telemetry": false 可完全关闭。
        /// </summary>
        public static void Ping()
        {
            if (!Config.Telemetry) return;
            string server = Config.ServerUrl;
            if (string.IsNullOrEmpty(server)) return;
            string body = "machine=" + Uri.EscapeDataString(InstallId.Salted(false)) +
                          "&version=" + Uri.EscapeDataString(AppInfo.Version) +
                          "&anon=1";
            byte[] data = Encoding.UTF8.GetBytes(body);
            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(server + "/api/v1/ping");
            req.Method = "POST";
            req.ContentType = "application/x-www-form-urlencoded";
            req.Timeout = MetaTimeoutMs;
            req.ReadWriteTimeout = MetaTimeoutMs;
            req.UserAgent = "DllSmith/" + AppInfo.Version;
            req.ContentLength = data.Length;
            req.Proxy = null;
            using (Stream s = req.GetRequestStream()) s.Write(data, 0, data.Length);
            using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
            using (StreamReader sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                sr.ReadToEnd();
        }

        /// <summary>普通 GET（用于从目录服务拉取 JSON）；失败返回空串，不抛异常</summary>
        public static string GetRaw(string url)
        {
            return GetRaw(url, MetaTimeoutMs);
        }

        public static string GetRaw(string url, int timeoutMs)
        {
            if (!IsFetchableUrl(url))
            {
                Log.Info("GET rejected (scheme/host policy): " + SafeUrl(url));
                return string.Empty;
            }
            try
            {
                HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
                req.Method = "GET";
                req.Timeout = timeoutMs;
                req.ReadWriteTimeout = timeoutMs;
                req.UserAgent = "DllSmith/" + AppInfo.Version;
                req.Proxy = null;                    // 不吃系统代理，避免被中间人劫持
                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                using (StreamReader sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                    return sr.ReadToEnd();
            }
            catch (Exception ex)
            {
                Log.Info("GET failed: " + SafeUrl(url) + " | " + ex.Message);
                return string.Empty;
            }
        }

        /// <summary>带响应头的 GET（目录响应头里带 X-Manifest-*，可省一次 manifest 往返）</summary>
        public static string GetRawWithHeaders(string url, int timeoutMs, out Dictionary<string, string> headers)
        {
            headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (!IsFetchableUrl(url)) return string.Empty;
            try
            {
                HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
                req.Method = "GET";
                req.Timeout = timeoutMs;
                req.ReadWriteTimeout = timeoutMs;
                req.UserAgent = "DllSmith/" + AppInfo.Version;
                req.Proxy = null;
                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                {
                    foreach (string k in resp.Headers.AllKeys)
                    {
                        if (k == null) continue;
                        try { headers[k] = resp.Headers[k]; } catch { }
                    }
                    using (StreamReader sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                        return sr.ReadToEnd();
                }
            }
            catch (Exception ex)
            {
                Log.Info("GET(header) failed: " + SafeUrl(url) + " | " + ex.Message);
                return string.Empty;
            }
        }

        /// <summary>只允许 http/https；file:/UNC（\\host\share）一律拒绝（A-13）</summary>
        public static bool IsFetchableUrl(string url)
        {
            if (string.IsNullOrEmpty(url)) return false;
            if (url.StartsWith("\\\\", StringComparison.Ordinal)) return false;
            Uri u;
            if (!Uri.TryCreate(url, UriKind.Absolute, out u)) return false;
            if (!string.Equals(u.Scheme, "http", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(u.Scheme, "https", StringComparison.OrdinalIgnoreCase)) return false;
            return !IsPrivateHost(u.Host);
        }

        /// <summary>私有/保留地址拦截（SSRF 面；目录数据不可信）</summary>
        public static bool IsPrivateHost(string host)
        {
            if (string.IsNullOrEmpty(host)) return true;
            if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return true;
            IPAddress ip;
            if (IPAddress.TryParse(host, out ip))
            {
                byte[] b = ip.GetAddressBytes();
                if (b.Length == 4)
                {
                    if (b[0] == 10) return true;
                    if (b[0] == 127) return true;
                    if (b[0] == 169 && b[1] == 254) return true;
                    if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return true;
                    if (b[0] == 192 && b[1] == 168) return true;
                    if (b[0] == 0) return true;
                }
                else if (b.Length == 16)
                {
                    if (ip.Equals(IPAddress.IPv6Loopback)) return true;
                    if (ip.IsIPv6LinkLocal) return true;
                }
            }
            return false;
        }

        /// <summary>日志脱敏：去掉 query（OSS 签名参数可被复用）</summary>
        public static string SafeUrl(string url)
        {
            if (string.IsNullOrEmpty(url)) return "";
            int q = url.IndexOf('?');
            string s = q > 0 ? url.Substring(0, q) + "?<redacted>" : url;
            return s.Length > 200 ? s.Substring(0, 200) + "…" : s;
        }

        private static string Post(string api, string plainJson)
        {
            try
            {
                HttpWebRequest req = (HttpWebRequest)WebRequest.Create(Host + api);
                req.Method = "POST";
                req.ContentType = "text/plain";
                req.Timeout = 20000;
                req.UserAgent = "DllSmith/" + AppInfo.Version;

                byte[] body = Encoding.UTF8.GetBytes(Encrypt(plainJson));
                req.ContentLength = body.Length;
                using (Stream s = req.GetRequestStream()) s.Write(body, 0, body.Length);

                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                using (StreamReader sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                {
                    return Decrypt(sr.ReadToEnd());
                }
            }
            catch (Exception ex)
            {
                Log.Info("api " + api + " failed: " + ex.Message);
                return string.Empty;
            }
        }

        /// <summary>用 oss_key 换取 OSS 签名地址（对应逆向报告 §6.2）</summary>
        public static string OssDownloadUrl(string ossKey)
        {
            if (string.IsNullOrEmpty(ossKey)) return string.Empty;
            string json = "{\"oss_key\":" + Quote(ossKey) + "}";
            string plain = Post("oss-download-url", json);
            if (string.IsNullOrEmpty(plain)) return string.Empty;
            Dictionary<string, object> d = Json.Parse(plain) as Dictionary<string, object>;
            if (d == null) return string.Empty;
            return Json.GetString(d, "download_url", "DownloadUrl");
        }

        /// <summary>
        /// 生成候选下载地址（1.4.0 顺序，A-03）：
        ///   ① 条目自带的 oss_key（服务端下发）
        ///   ② 由 DOWNLOAD 路径推导的 oss_key（厂商 OSS 接口，实测可用）
        ///   ③ 服务端代理 /api/v1/oss-url（服务端上线后）
        ///   ④ catalog 的 DOWNLOAD —— 仅历史兜底（实测 CDN 已 404）
        /// </summary>
        public static List<string> Candidates(CatalogItem item)
        {
            List<string> urls = new List<string>();
            if (item == null) return urls;

            List<string> keys = new List<string>();
            if (!string.IsNullOrEmpty(item.OssKey)) keys.Add(item.OssKey.Trim());
            foreach (string k in DeriveOssKeys(item.Download))
                if (!keys.Contains(k)) keys.Add(k);

            // ③ 服务端代理（还没上线时返回空，不影响后面兜底）
            string server = Config.ServerUrl;
            if (!string.IsNullOrEmpty(server))
            {
                foreach (string k in keys)
                {
                    if (urls.Count >= 2) break;
                    try
                    {
                        string body = GetRaw(server + "/api/v1/oss-url?key=" + Uri.EscapeDataString(k), MetaTimeoutMs);
                        if (string.IsNullOrEmpty(body)) continue;
                        Dictionary<string, object> d = Json.Parse(body) as Dictionary<string, object>;
                        string u = d == null ? null : Json.GetString(d, "url", "download_url");
                        if (!string.IsNullOrEmpty(u) && IsFetchableUrl(u) && !urls.Contains(u)) urls.Add(u);
                    }
                    catch { }
                }
            }

            // ② 厂商 OSS 直连（当前实测唯一有效通道）
            foreach (string k in keys)
            {
                try
                {
                    string signed = OssDownloadUrl(k);
                    if (!string.IsNullOrEmpty(signed) && IsFetchableUrl(signed) && !urls.Contains(signed))
                        urls.Add(signed);
                }
                catch (Exception ex) { Log.Info("oss key failed: " + k + " | " + ex.Message); }
            }

            // ④ 历史 DOWNLOAD（放最后：它现在多半 404，但保留兜底不丢功能）
            if (!string.IsNullOrEmpty(item.Download) && IsFetchableUrl(item.Download) && !urls.Contains(item.Download))
                urls.Add(item.Download);

            return urls;
        }

        /// <summary>兼容旧调用（--selftest 用）：从一个 URL 推导候选</summary>
        public static List<string> Candidates(string catalogUrl)
        {
            CatalogItem fake = new CatalogItem();
            fake.Download = catalogUrl;
            return Candidates(fake);
        }

        private static List<string> DeriveOssKeys(string download)
        {
            List<string> keys = new List<string>();
            if (string.IsNullOrEmpty(download)) return keys;
            string path;
            try { path = new Uri(download).AbsolutePath; }
            catch { return keys; }

            // 已经是 _encrypt 对象时不要再拼一次（会产生 x_encrypt_encrypt 这种必 404 的候选）
            string basePath = path.Replace("/DLL/", "/").TrimStart('/');
            string a = basePath.EndsWith("_encrypt", StringComparison.OrdinalIgnoreCase)
                ? basePath : basePath + "_encrypt";
            string b = path.TrimStart('/').EndsWith("_encrypt", StringComparison.OrdinalIgnoreCase)
                ? path.TrimStart('/') : path.TrimStart('/') + "_encrypt";
            string c = basePath;
            keys.Add(a);
            if (b != a) keys.Add(b);
            if (c != a && c != b) keys.Add(c);
            return keys;
        }

        private static string Quote(string s)
        {
            StringBuilder sb = new StringBuilder("\"");
            foreach (char c in s)
            {
                if (c == '"' || c == '\\') sb.Append('\\');
                sb.Append(c);
            }
            sb.Append('"');
            return sb.ToString();
        }
    }

    /// <summary>安装标识（加盐哈希，A-09）：不回传原始 MachineGuid</summary>
    public static class InstallId
    {
        private const string Salt = "dllsmith-1.4-anon";

        public static string Salted(bool full)
        {
            try
            {
                string raw = SystemProbe.MachineGuid() + "|" + Salt;
                string hex = Hashes.Sha256Hex(Encoding.UTF8.GetBytes(raw));
                return full ? hex : hex.Substring(0, 16);
            }
            catch { return "unknown"; }
        }
    }

    public class UpdateInfo
    {
        public bool HasUpdate;
        public string Version = "";
        public string Url = "";
        public string Sha256 = "";
        public string Notes = "";
        public string Channel = "stable";
        public string Sig = "";
        public string Kid = "";
        public string PublishedAt = "";
        public bool Mandatory;

        /// <summary>被拒绝的原因（界面要原样展示，不许"只提示然后照样装"）</summary>
        public string RejectReason = "";

        public bool Trusted
        {
            get { return string.IsNullOrEmpty(RejectReason); }
        }
    }

    /// <summary>
    /// 更新检查 / 下载 / 启动安装包。
    /// 硬规则（发布门禁）：验签 -> HTTPS -> SHA256 -> 用户确认，任何一步不过都不落地执行。
    /// </summary>
    public static class UpdateService
    {
        /// <summary>向目录服务查询更新；失败/被拒返回 null，原因写进 UpdateCheckResult</summary>
        public static UpdateInfo Check(string currentVersion)
        {
            string reason;
            return CheckEx(currentVersion, out reason);
        }

        public static UpdateInfo CheckEx(string currentVersion, out string rejectReason)
        {
            rejectReason = "";
            string server = Config.ServerUrl;
            if (string.IsNullOrEmpty(server))
            {
                rejectReason = "未配置目录服务";
                return null;
            }
            if (!UpdateTrust.IsHttps(server) && !Config.AllowInsecureServer)
            {
                rejectReason = "目录服务地址不是 HTTPS（config.json），已进入离线模式";
                Log.Info("update skipped: insecure server " + ApiClient.SafeUrl(server));
                return null;
            }

            string body = ApiClient.GetRaw(server + "/api/v1/update?version=" +
                Uri.EscapeDataString(currentVersion) + "&channel=" + Uri.EscapeDataString(Config.Channel));
            if (string.IsNullOrEmpty(body))
            {
                rejectReason = "无法联系更新服务器";
                return null;
            }
            try
            {
                Dictionary<string, object> d = Json.Parse(body) as Dictionary<string, object>;
                if (d == null) { rejectReason = "更新响应不是合法 JSON"; return null; }

                UpdateInfo info = new UpdateInfo();
                object upd;
                info.HasUpdate = d.TryGetValue("update", out upd) && upd is bool && (bool)upd;
                info.Version = Json.GetString(d, "version");
                info.Url = Json.GetString(d, "url");
                info.Sha256 = Json.GetString(d, "sha256");
                info.Notes = Json.GetString(d, "notes");
                info.Channel = Json.GetString(d, "channel");
                if (string.IsNullOrEmpty(info.Channel)) info.Channel = Config.Channel;
                info.Sig = Json.GetString(d, "sig", "signature");
                info.Kid = Json.GetString(d, "kid");
                info.PublishedAt = Json.GetString(d, "published_at");
                object man;
                info.Mandatory = d.TryGetValue("mandatory", out man) && man is bool && (bool)man;

                if (!info.HasUpdate) return null;

                TrustResult tr = UpdateTrust.VerifyUpdateRecord(info);
                if (!tr.Ok)
                {
                    info.RejectReason = tr.Reason;
                    rejectReason = tr.Reason;
                    Log.Error("update record rejected: " + tr.Reason, null);
                    return info;          // 返回对象，让界面能显示"为什么拒绝"
                }
                Log.Info("update available v" + info.Version + " mandatory=" + info.Mandatory +
                         " kid=" + info.Kid);
                return info;
            }
            catch (Exception ex)
            {
                Log.Error("update check failed", ex);
                rejectReason = "更新响应解析失败";
                return null;
            }
        }

        /// <summary>下载到临时目录（强制 HTTPS + 强制 SHA256），返回是否通过校验</summary>
        public static bool Download(UpdateInfo info, Action<int, long, long> progress)
        {
            return Download(info, progress, CancellationToken.None);
        }

        public static bool Download(UpdateInfo info, Action<int, long, long> progress, CancellationToken token)
        {
            if (info == null || !info.Trusted)
            {
                Log.Error("download refused: " + (info == null ? "null record" : info.RejectReason), null);
                return false;
            }
            if (!UpdateTrust.IsHttps(info.Url))
            {
                Log.Error("download refused: non-https url " + ApiClient.SafeUrl(info.Url), null);
                return false;
            }
            if (!Hashes.LooksLikeSha256(info.Sha256))
            {
                Log.Error("download refused: invalid sha256", null);
                return false;
            }

            try
            {
                string file = TempPackagePath(info);
                HttpWebRequest req = (HttpWebRequest)WebRequest.Create(info.Url);
                req.UserAgent = "DllSmith/" + AppInfo.Version;
                req.Timeout = 15000;
                req.ReadWriteTimeout = 60000;
                req.AllowAutoRedirect = false;        // 不跟随重定向，避免被导到明文/他域
                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                using (Stream net = resp.GetResponseStream())
                using (FileStream fs = File.Create(file))
                {
                    long total = resp.ContentLength;
                    byte[] buf = new byte[65536];
                    long got = 0;
                    int n;
                    while ((n = net.Read(buf, 0, buf.Length)) > 0)
                    {
                        if (token.IsCancellationRequested) { fs.Close(); TryDelete(file); return false; }
                        fs.Write(buf, 0, n);
                        got += n;
                        if (progress != null)
                        {
                            int pct = total > 0 ? (int)(got * 100L / total) : 0;
                            progress(pct, got, total);
                        }
                    }
                }
                if (progress != null) progress(100, 0, 0);

                string hash = Hashes.Sha256HexOfFile(file);
                if (!string.Equals(hash, info.Sha256.ToLowerInvariant(), StringComparison.Ordinal))
                {
                    Log.Error("update sha256 mismatch: got " + hash + " want " + info.Sha256, null);
                    TryDelete(file);
                    return false;
                }
                Log.Info("update downloaded & verified: " + Path.GetFileName(file));
                return true;
            }
            catch (Exception ex)
            {
                Log.Error("update download failed", ex);
                return false;
            }
        }

        private static string TempPackagePath(UpdateInfo info)
        {
            string name = "DllSmith-" + SafeVersion(info.Version) + ".exe";
            return Path.Combine(Path.GetTempPath(), name);
        }

        private static string SafeVersion(string v)
        {
            if (string.IsNullOrEmpty(v)) return "unknown";
            StringBuilder sb = new StringBuilder();
            foreach (char c in v)
                sb.Append(char.IsLetterOrDigit(c) || c == '.' ? c : '_');
            return sb.ToString();
        }

        /// <summary>启动下载好的安装包（调用前必须已经验签 + 哈希通过 + 用户确认）</summary>
        public static bool RunInstaller(UpdateInfo info)
        {
            if (info == null || !info.Trusted)
            {
                Log.Error("installer refused: " + (info == null ? "null record" : info.RejectReason), null);
                return false;
            }
            try
            {
                string file = TempPackagePath(info);
                if (!File.Exists(file))
                {
                    Log.Error("run installer: file missing", null);
                    return false;
                }
                string hash = Hashes.Sha256HexOfFile(file);
                if (!string.Equals(hash, info.Sha256.ToLowerInvariant(), StringComparison.Ordinal))
                {
                    Log.Error("run installer refused: file changed after verify", null);
                    return false;
                }
                Log.Info("launch installer: " + Path.GetFileName(file) + " verified sha256=" + hash);
                Process.Start(new ProcessStartInfo(file) { UseShellExecute = true });
                return true;
            }
            catch (Exception ex)
            {
                Log.Error("update run failed", ex);
                return false;
            }
        }

        public static void TryDelete(string file)
        {
            try { if (File.Exists(file)) File.Delete(file); } catch { }
        }
    }
}
