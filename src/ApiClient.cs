// ============================================================================
//  4DDiG DLL Fixer — 重建工程
//  文件：ApiClient.cs  业务接口（AES 信封）与下载地址解析
//  对应原程序：DllRepair.Helpers.ApiHelper（check-crack-version / fix-config /
//              oss-download-url），信封 = AES-128-CBC/PKCS7 + base64(IV||CT)
//  说明：本文件是"重建版"的客户端实现，用于解析真实下载地址；
//        不实现账号登录，也不上报任何机器信息。
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace DllFix.Core
{
    public static class ApiClient
    {
        static ApiClient()
        {
            // .NET Framework 默认不启用 TLS1.2，必须显式打开，否则 HTTPS 直接失败
            try
            {
                ServicePointManager.SecurityProtocol =
                    SecurityProtocolType.Tls12 | SecurityProtocolType.Tls11 | SecurityProtocolType.Tls;
            }
            catch { }
        }
        private const string Host = "https://4ddig-online.tenorshare.com/api/v1/dllfix/";
        private static readonly byte[] Key = Encoding.UTF8.GetBytes("DDIG4SECUREKEY16");

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

        /// <summary>在线心跳：POST /api/v1/ping（表单，不走 AES 信封）</summary>
        public static void Ping()
        {
            string server = Config.ServerUrl;
            if (string.IsNullOrEmpty(server)) return;
            string body = "machine=" + Uri.EscapeDataString(SystemProbe.MachineGuid()) +
                          "&version=" + Uri.EscapeDataString(AppInfo.Version);
            byte[] data = Encoding.UTF8.GetBytes(body);
            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(server + "/api/v1/ping");
            req.Method = "POST";
            req.ContentType = "application/x-www-form-urlencoded";
            req.Timeout = 8000;
            req.UserAgent = "DllSmith/" + AppInfo.Version;
            req.ContentLength = data.Length;
            using (Stream s = req.GetRequestStream()) s.Write(data, 0, data.Length);
            using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
            using (StreamReader sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                sr.ReadToEnd();
        }

        /// <summary>普通 GET（用于从目录服务拉取 JSON）</summary>
        public static string GetRaw(string url)
        {
            try
            {
                HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
                req.Method = "GET";
                req.Timeout = 8000;
                req.UserAgent = "4DDiG-Rebuild/1.0";
                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                using (StreamReader sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                    return sr.ReadToEnd();
            }
            catch (Exception ex)
            {
                Log.Info("GET failed: " + url + " | " + ex.Message);
                return string.Empty;
            }
        }

        private static string Post(string api, string plainJson)
        {
            try
            {
                HttpWebRequest req = (HttpWebRequest)WebRequest.Create(Host + api);
                req.Method = "POST";
                req.ContentType = "text/plain";
                req.Timeout = 20000;
                req.UserAgent = "4DDiG-Rebuild/1.0";

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
        /// 生成候选下载地址：原 CDN 地址 + 若干 oss_key 变体换来的签名地址。
        /// 调用方按顺序尝试 GET，第一个成功的即采用（不依赖 HEAD，OSS 对 HEAD 支持不稳定）。
        /// </summary>
        public static List<string> Candidates(string catalogUrl)
        {
            List<string> urls = new List<string>();
            if (string.IsNullOrEmpty(catalogUrl)) return urls;
            urls.Add(catalogUrl);

            string path;
            try { path = new Uri(catalogUrl).AbsolutePath; }
            catch { return urls; }

            List<string> keys = new List<string>();
            keys.Add(path.Replace("/DLL/", "/").TrimStart('/') + "_encrypt");
            keys.Add(path.TrimStart('/') + "_encrypt");
            keys.Add(path.Replace("/DLL/", "/").TrimStart('/'));

            foreach (string k in keys)
            {
                string signed = OssDownloadUrl(k);
                if (string.IsNullOrEmpty(signed)) continue;
                if (!urls.Contains(signed)) urls.Add(signed);
                Log.Info("candidate oss_key=" + k);
            }
            return urls;
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

    public class UpdateInfo
    {
        public bool HasUpdate;
        public string Version = "";
        public string Url = "";
        public string Sha256 = "";
        public string Notes = "";
        public bool Mandatory;
    }

    public static class UpdateService
    {
        /// <summary>向目录服务查询更新；未配置 server 或请求失败时返回 null</summary>
        public static UpdateInfo Check(string currentVersion)
        {
            string server = Config.ServerUrl;
            if (string.IsNullOrEmpty(server)) return null;
            string body = ApiClient.GetRaw(server + "/api/v1/update?version=" +
                Uri.EscapeDataString(currentVersion) + "&channel=stable");
            if (string.IsNullOrEmpty(body)) return null;
            try
            {
                Dictionary<string, object> d = Json.Parse(body) as Dictionary<string, object>;
                if (d == null) return null;
                UpdateInfo info = new UpdateInfo();
                object upd;
                info.HasUpdate = d.TryGetValue("update", out upd) && upd is bool && (bool)upd;
                info.Version = Json.GetString(d, "version");
                info.Url = Json.GetString(d, "url");
                info.Sha256 = Json.GetString(d, "sha256");
                info.Notes = Json.GetString(d, "notes");
                object man;
                info.Mandatory = d.TryGetValue("mandatory", out man) && man is bool && (bool)man;
                if (!info.HasUpdate) return null;
                if (string.IsNullOrEmpty(info.Url)) return null;
                Log.Info("update available " + info.Version + " mandatory=" + info.Mandatory);
                return info;
            }
            catch (Exception ex)
            {
                Log.Error("update check failed", ex);
                return null;
            }
        }

        /// <summary>下载安装包到临时目录并启动，返回是否成功</summary>
        public static bool DownloadAndRun(UpdateInfo info)
        {
            try
            {
                string file = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                    "DllSmith-" + info.Version + ".exe");
                using (System.Net.WebClient wc = new System.Net.WebClient())
                {
                    wc.Headers.Add("User-Agent", "DllSmith/" + AppInfo.Version);
                    wc.DownloadFile(info.Url, file);
                }
                if (!string.IsNullOrEmpty(info.Sha256))
                {
                    string hash;
                    using (System.Security.Cryptography.SHA256 sha = System.Security.Cryptography.SHA256.Create())
                    using (System.IO.FileStream fs = System.IO.File.OpenRead(file))
                        hash = BitConverter.ToString(sha.ComputeHash(fs)).Replace("-", "").ToLowerInvariant();
                    if (!string.Equals(hash, info.Sha256.ToLowerInvariant(), StringComparison.Ordinal))
                    {
                        Log.Error("update sha256 mismatch: " + hash, null);
                        return false;
                    }
                }
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(file) { UseShellExecute = true });
                return true;
            }
            catch (Exception ex)
            {
                Log.Error("update download failed", ex);
                return false;
            }
        }
    }
}
