// ============================================================================
//  DllSmith 1.4 — 更新链路信任（文档 A-01 / C-01）
//  文件：UpdateSecurity.cs
//  内容：
//    ReleaseKeys  —— 内置发布公钥表（kid -> RSA-3072），只认编译进 exe 的钥匙
//    RsaVerify    —— RSASSA-PKCS1-v1_5 + SHA-256 验签（纯 BCL：RSACng + RSAParameters）
//    UpdateTrust  —— 发布记录校验：签名 -> 哈希 -> HTTPS，任何一步失败即拒绝
//    TlsPin       —— 自签根证书 pinning（Pin 根的 RSA 模数，不 pin 叶子指纹）
//  规范串（与 _team\reports\client\contract-server-1.4.0.md 一致，UTF-8、竖线分隔、无尾随换行）：
//      v1|<version>|<channel>|<sha256 小写 hex>|<url>
//  约束：C# 5（csc 4.8 / .NET Framework 4.8），不引第三方 DLL。
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;

namespace DllFix.Core
{
    /// <summary>
    /// 内置发布公钥。**只从这里取公钥**：绝不从网络、磁盘或配置文件读取信任根，
    /// 否则等于没有签名（攻击者能连公钥一起换掉）。
    /// </summary>
    public static class ReleaseKeys
    {
        /// <summary>正式发布公钥 kid（契约固定 ds1）</summary>
        public const string ProductionKid = "ds1";

        /// <summary>
        /// 正式发布公钥（RSA-3072，base64 大端 modulus，去前导 0x00）。
        /// 来源：server-engineer 2026-11-14 生成，`_team\reports\server\update_pubkey.pem`。
        /// 已用 _team\reports\client\tools\release-record-1.3.1.json 对表通过
        /// （客户端 .NET 与 Python 标准库、服务端 openssl 三方一致）。
        /// </summary>
        public const string Ds1ModulusB64 =
            "sR0bLX3B5Je+qGrrsnmSWTpYR5+XCYx46C/L/oswwACPtqjH8fRYzDZOdj8Dx11oKEPJdgeNzPMXd1Y4DD5dB7efhfvKhjijt0K5szV9+Bf34pbVbRw4MQ+dFU2Uus3eptbIQB7Mx4tV8au8rtuQEAsgGTtz0K09OnC4T2/eIDnLRaqw59L9rgsenqL9NqF3gPTtfF6vy63/bhU+ji8cBWcHJQNSBCZ2kXWTBuygBcG1MKhmW9j3bZp8qBYgL8YWeI4a8pdibQPjas9P0quQ7/aNwzSicyUZQlEDlURFWQIlqyES1FE0fHZxXRuBos42yb0WAEEGoQjaR4htUeY8aInAVAZtXZe8/HrQl1+/ZdWZwzuwMIwdjxbROZTOtGE2GKQOvJBZYfeZqrZ3HpT2lTGSC+MHkEfw38jShjcW0OLyWXEue4/43oXRmpJic5+EL+DfI0P5403JFc1hqKXePwvHZZ64i3Vcid9n4JaWDEu/75a4bHM8oJUhd1wVa1XN";

        /// <summary>公开指数（恒为 AQAB = 65537）</summary>
        public const string Ds1ExponentB64 = "AQAB";

        // ── 安全硬约束（QA t6 P0-1 整改）───────────────────────────────────
        // 1.4.0 早期版本把一把"测试钥匙"(kid=ds1-test) 连同模数编进了生产 exe，而它的
        // 私钥躺在 _team\reports\client\tools\testvector-rs256.json 里 → 任何拿到该文件的人
        // 都能给全量客户端下发任意安装包（验签/HTTPS/哈希三道门全部"通过"）。
        // 现在：**生产构建只认 ProductionKid**，测试钥匙彻底删除，绝不参与生产路径。
        // 自检一律用真发布记录（--verify-release）或直接用 openssl/python 对表。

        /// <summary>
        /// 生产构建里唯一允许的 kid（硬白名单）。
        /// 只有显式 /define:DS_TESTKEYS 的**测试构建**才会额外接受 TestKid，
        /// 且该构建的产物文件名与版本号都带 -test（见 build.ps1），不可能误发。
        /// </summary>
        public static bool IsProductionKid(string kid)
        {
            if (string.Equals(kid, ProductionKid, StringComparison.Ordinal)) return true;
#if DS_TESTKEYS
            if (string.Equals(kid, TestKid, StringComparison.Ordinal)) return true;
#endif
            return false;
        }

        /// <summary>当前构建允许的 kid 列表（--selfcheck-keys 原样输出，生产必须恰好 [ds1]）</summary>
        public static string[] KnownKids()
        {
#if DS_TESTKEYS
            return new string[] { ProductionKid, TestKid };
#else
            return new string[] { ProductionKid };
#endif
        }

#if DS_TESTKEYS
        /// <summary>
        /// 仅测试构建存在。这里只有**公钥**：测试记录的私钥不落库、不进产物，
        /// 需要时用 sigprobe.exe sign 现生成一对（每次都是新的，用完即弃）。
        /// </summary>
        public const string TestKid = "ds1-test";
        public const string TestModulusB64 =
            "r+xBT0Yct9/YzXPs25m23SZ+mOxl3cUxonJG4Z0Je/3FcGFfQkY2XgizV/OEvkjRyg8Neupze4V5E42rgvqWln1jDq/zzBLIszGIF2aWpgdh6pxDDzdDgIXUPxQfvW3eUDQW0uxZxI8r42vfbKUnCSheSwsD9Ctr0XY+vCttxWs35QzDruVgZTNESKa+noyvWWGy6iLHx9EYSjSmGNfFz6tDc/5mlYX/q4u2nJcM/kktu27WAr8hAbAWFhAZBi4piS7tYn7dcmu9hZ3v9k+r+tMuYWBI8DNNGXJPg1N+/qcM2FRQCowLy0VjQMAjU1wcgl9wvmoalCVYTaaRVE5MRDEaTc6Npn/rAHU49IfyDa4iqVQwdEpHvY8hJATf+lDFKflk2/eoZvUME4VYdM6gHcl4vpxLuhZUAJ15CdZjBZf6853pZJAGETyJIn8n4rtpcbIrrJOqlmxFLykNaQVfp2n37+m0d7k+5rTKwWnln9qOwiNdJKsSbS/xIJNHSu6t";
#endif

        /// <summary>kid -> (modulus, exponent)；只认生产 kid，其它一律 false</summary>
        public static bool TryGet(string kid, out string modulusB64, out string exponentB64)
        {
            modulusB64 = null;
            exponentB64 = null;
            if (string.IsNullOrEmpty(kid)) kid = ProductionKid;
            if (!IsProductionKid(kid)) return false;                 // 未知名 -> 直接拒绝
            if (string.Equals(kid, ProductionKid, StringComparison.Ordinal))
            {
                if (string.IsNullOrEmpty(Ds1ModulusB64)) return false;   // 未内置 -> 不可用
                modulusB64 = Ds1ModulusB64;
                exponentB64 = Ds1ExponentB64;
                return true;
            }
#if DS_TESTKEYS
            if (string.Equals(kid, TestKid, StringComparison.Ordinal))
            {
                modulusB64 = TestModulusB64;
                exponentB64 = "AQAB";
                return true;
            }
#endif
            return false;
        }

        /// <summary>正式公钥是否已内置（未内置时更新通道禁用，界面要明确提示）</summary>
        public static bool ProductionKeyAvailable
        {
            get { return !string.IsNullOrEmpty(Ds1ModulusB64); }
        }
    }

    /// <summary>RSASSA-PKCS1-v1_5 / SHA-256 验签（纯 BCL）</summary>
    public static class RsaVerify
    {
        public static bool VerifySha256(string modulusB64, string exponentB64,
                                        string message, byte[] signature)
        {
            if (signature == null || signature.Length == 0) return false;
            if (string.IsNullOrEmpty(modulusB64) || string.IsNullOrEmpty(exponentB64)) return false;
            try
            {
                RSAParameters p = new RSAParameters();
                p.Modulus = Convert.FromBase64String(modulusB64);
                p.Exponent = Convert.FromBase64String(exponentB64);
                byte[] data = Encoding.UTF8.GetBytes(message);
                using (RSACng rsa = new RSACng())
                {
                    rsa.ImportParameters(p);
                    return rsa.VerifyData(data, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                }
            }
            catch (Exception ex)
            {
                // 签名长度不对 / 数据非法都会抛 CryptographicException：按"验签失败"处理，不是崩溃
                Log.Error("signature verify threw", ex);
                return false;
            }
        }
    }

    public static class Hashes
    {
        public static string Sha256Hex(byte[] data)
        {
            using (SHA256 sha = SHA256.Create())
                return ToHex(sha.ComputeHash(data));
        }

        public static string Sha256HexOfFile(string path)
        {
            using (SHA256 sha = SHA256.Create())
            using (FileStream fs = File.OpenRead(path))
                return ToHex(sha.ComputeHash(fs));
        }

        private static string ToHex(byte[] hash)
        {
            StringBuilder sb = new StringBuilder(hash.Length * 2);
            foreach (byte b in hash) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }

        /// <summary>严格校验 16 进制串：长度 64、只含 0-9a-fA-F</summary>
        public static bool LooksLikeSha256(string s)
        {
            if (string.IsNullOrEmpty(s) || s.Length != 64) return false;
            foreach (char c in s)
            {
                bool ok = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
                if (!ok) return false;
            }
            return true;
        }
    }

    /// <summary>发布记录校验结果</summary>
    public class TrustResult
    {
        public bool Ok;
        public string Reason = "";
        public string Canonical = "";

        public static TrustResult Fail(string reason)
        {
            TrustResult r = new TrustResult();
            r.Ok = false;
            r.Reason = reason;
            return r;
        }

        public static TrustResult Pass(string canonical)
        {
            TrustResult r = new TrustResult();
            r.Ok = true;
            r.Canonical = canonical;
            return r;
        }
    }

    /// <summary>更新记录 / manifest 的信任判定（契约 §2，权威实现在这里）</summary>
    public static class UpdateTrust
    {
        /// <summary>规范串：v1|version|channel|sha256|url（小写 hex，无尾随换行）</summary>
        public static string UpdateCanonical(string version, string channel, string sha256, string url)
        {
            return "v1|" + (version == null ? "" : version.Trim())
                       + "|" + (channel == null ? "" : channel.Trim())
                       + "|" + (sha256 == null ? "" : sha256.Trim().ToLowerInvariant())
                       + "|" + (url == null ? "" : url.Trim());
        }

        /// <summary>manifest 规范串：v1|manifest|key|sha256(lines)|count（契约 §4-b）</summary>
        public static string ManifestCanonical(string key, string linesSha256, int count)
        {
            return "v1|manifest|" + (key == null ? "" : key)
                                 + "|" + (linesSha256 == null ? "" : linesSha256.ToLowerInvariant())
                                 + "|" + count.ToString();
        }

        public static bool IsHttps(string url)
        {
            if (string.IsNullOrEmpty(url)) return false;
            try { return string.Equals(new Uri(url).Scheme, "https", StringComparison.OrdinalIgnoreCase); }
            catch { return false; }
        }

        /// <summary>校验一条更新记录。失败即拒绝更新，绝不"只提示然后照样装"。</summary>
        public static TrustResult VerifyUpdateRecord(UpdateInfo info)
        {
            return Verify(info, true);
        }

        /// <summary>只验签名 + 字段合法性，不管传输策略（对表 / 老版本 http 分支自检用）</summary>
        public static TrustResult VerifySignatureOnly(UpdateInfo info)
        {
            return Verify(info, false);
        }

        private static TrustResult Verify(UpdateInfo info, bool requireHttps)
        {
            if (info == null) return TrustResult.Fail("没有更新记录");
            if (!info.HasUpdate) return TrustResult.Fail("没有可用更新");
            if (string.IsNullOrEmpty(info.Version)) return TrustResult.Fail("更新记录缺少 version");
            if (string.IsNullOrEmpty(info.Url)) return TrustResult.Fail("更新记录缺少 url");
            if (!Hashes.LooksLikeSha256(info.Sha256))
                return TrustResult.Fail("更新记录的 sha256 非法（必须是 64 位十六进制）");

            // 1) 下载地址强制 HTTPS（本代只接受签名 + TLS 记录）
            if (requireHttps && !IsHttps(info.Url))
                return TrustResult.Fail("更新地址不是 HTTPS：" + Short(info.Url));

            // 2) 必须是已签名记录
            if (string.IsNullOrEmpty(info.Sig))
                return TrustResult.Fail("更新记录未签名（服务端 sig 为空），已拒绝更新");

            // 3) kid 必须是生产 kid（硬白名单）且钥匙已内置
            string effKid = string.IsNullOrEmpty(info.Kid) ? ReleaseKeys.ProductionKid : info.Kid;
            if (!ReleaseKeys.IsProductionKid(effKid))
                return TrustResult.Fail("未知的更新签名密钥 kid=" + effKid);

            string n, e;
            if (!ReleaseKeys.TryGet(effKid, out n, out e))
            {
                return TrustResult.Fail("本机未内置正式发布公钥（kid=" + effKid + "），更新通道已禁用");
            }

            // 4) 验签
            string canonical = UpdateCanonical(info.Version, info.Channel, info.Sha256, info.Url);
            byte[] sig;
            try { sig = Convert.FromBase64String(info.Sig); }
            catch { return TrustResult.Fail("签名不是合法 base64"); }

            if (!RsaVerify.VerifySha256(n, e, canonical, sig))
                return TrustResult.Fail("更新记录签名校验失败（记录可能被篡改）");

            Log.Info("update record signature OK kid=" + info.Kid + " v=" + info.Version);
            return TrustResult.Pass(canonical);
        }

        private static string Short(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Length > 90 ? s.Substring(0, 90) + "…" : s;
        }
    }

    /// <summary>
    /// TLS pinning：对目录服务主机做「自签根 CA 的 RSA 模数」pin。
    /// 不 pin 叶子指纹（叶子每年滚动，pin 叶子等于每次换证都要发版）。
    /// 未配置 pin 时：仅对已登记主机拒绝连接，其它主机回落到系统默认校验。
    /// </summary>
    public static class TlsPin
    {
        /// <summary>
        /// 正式自签根 CA 的 RSA 模数（base64）。来源 `_team\reports\server\dllsmith-release-ca.pem`
        /// （CN=dllsmith-release-ca / RSA-3072 / 2026-11-14 → 2036-11-11）。
        /// 客户端 pin 这个模数：根 10 年不动，叶子每年滚动不需要发版。
        /// </summary>
        public const string RootModulusB64 =
            "qKIcndRir0W1w4wrEcA3E3Yz/FXgICm+s6W7Fge1jiEZ6db1TUOPElb4aes7365MzmBm6y1UYUlUE+6IXieJyv+P19YJDxEOs0JNfC0tRr5BRqR0EUGrv2QPcIqZV9EgEjGVIRILZ+mQb07XjCkxFz4Ede7Oq8dKWwg1pa5flH0k2XYrNE8W+ti2kU83U2/VpFT4g5Wg+rE57S4guMR6f+8PrwYyEb9jAiWNOwWHTCOymFGxZHnzth8mNvQ8TFdxR5QgdRP2br9Nb9CIaN7CqtUWphM6fQrS5LAVczkwGAj3Ea4KegvCFUvaATwj63IE9ALGDjiEgH1xxcilye2cEx5+Y/4Fwdxr0iTttW9WkZyQYDAVvhPnSi3VPQJEE0EAfg3oiI7WFFXslsmUmMd+VKDe/ZaAO7ogPvafADUcGE4dlI/tkTyEzhbgF0GhuyMlA5DQx84Sbb639y7CA3uAv12wRUCaP6xU1ZLCRlJx38WBaqzBQ1Qbe+Msu1EET259";

        /// <summary>
        /// 额外可接受的叶子模数（轮换期备用）。
        /// 当前叶子 CN=123.57.94.141 / RSA-2048 / 2027-12-16 到期，由上面的根签发，
        /// 所以正常情况下靠"链到内置根"即可放行；这里不 pin 叶子指纹。
        /// </summary>
        public static readonly string[] ExtraLeafModulusB64 = new string[0];

        /// <summary>允许的目标主机（SAN 断言用；命中其一才接受 pin 通道）</summary>
        public static readonly string[] AllowedHosts = new string[] { "123.57.94.141", "127.0.0.1", "localhost" };

        private static bool _installed;

        /// <summary>仅供自检程序模拟"pin 配错"（不来自配置/网络，正式路径不可改）</summary>
        internal static string SelfTestPinOverride;

        public static bool PinningConfigured
        {
            get { return !string.IsNullOrEmpty(ExpectedRootB64); }
        }

        private static string ExpectedRootB64
        {
            get { return SelfTestPinOverride ?? RootModulusB64; }
        }

        /// <summary>安装全局证书校验回调（只影响本进程）</summary>
        public static void Install()
        {
            if (_installed) return;
            _installed = true;
            ServicePointManager.ServerCertificateValidationCallback = Validate;
            Log.Info("TLS pin installed, configured=" + PinningConfigured);
        }

        /// <summary>
        /// 返回 true 才允许握手。
        /// 规则：①系统默认校验通过 -> 放行（普通公网 HTTPS）；
        ///       ②否则仅当目标是 pin 主机、且证书有效期正常、链到内置根、SAN 命中时才放行；
        ///       ③其余一律 false —— 绝不无条件 true。
        /// </summary>
        private static bool Validate(object sender, X509Certificate certificate, X509Chain chain,
                                     SslPolicyErrors errors)
        {
            if (errors == SslPolicyErrors.None) return true;

            X509Certificate2 cert = certificate as X509Certificate2;
            if (cert == null) return false;

            HttpWebRequest req = sender as HttpWebRequest;
            string host = req != null ? req.RequestUri.Host : null;

            if (!PinningConfigured)
            {
                Log.Error("TLS rejected (no pin configured): " + host + " errors=" + errors, null);
                return false;
            }

            try
            {
                // 1) 有效期
                DateTime now = DateTime.Now;
                if (now < cert.NotBefore || now > cert.NotAfter)
                {
                    Log.Error("TLS rejected: certificate expired/not yet valid", null);
                    return false;
                }

                // 2) 模数 pin：叶子或内置根命中即视为可信链
                byte[] leafMod = Modulus(cert);
                if (leafMod == null)
                {
                    Log.Error("TLS rejected: non-RSA certificate", null);
                    return false;
                }

                // 3) SAN 显式断言（不依赖 .NET 的 name-mismatch 回调语义）
                if (!SanMatches(cert, host))
                {
                    Log.Error("TLS rejected: SAN does not contain host " + host, null);
                    return false;
                }

                if (Matches(leafMod, ExpectedRootB64)) return true;
                foreach (string extra in ExtraLeafModulusB64)
                    if (Matches(leafMod, extra)) return true;

                // 3) 叶子由内置根签发：用 ExtraStore 装内置根 + 允许未知根，链尾模数必须是 pin 根
                X509Certificate2 root = FindPinnedRoot(chain, leafMod);
                if (root == null)
                {
                    Log.Error("TLS rejected: chain does not terminate at pinned root (" + host + ")", null);
                    return false;
                }
                if (!ChainsTo(cert, root))
                {
                    Log.Error("TLS rejected: chain build to pinned root failed", null);
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                Log.Error("TLS validate threw", ex);
                return false;
            }
        }

        /// <summary>证书 SAN 是否包含目标主机（IP 或域名，字面比对）</summary>
        private static bool SanMatches(X509Certificate2 cert, string host)
        {
            if (string.IsNullOrEmpty(host)) return false;
            try
            {
                foreach (X509Extension ext in cert.Extensions)
                {
                    if (ext.Oid == null || ext.Oid.Value != "2.5.29.17") continue;
                    string text = ext.Format(false);
                    if (text == null) continue;
                    foreach (string token in text.Split(new char[] { ',', ';' }))
                    {
                        string t = token.Trim();
                        int eq = t.IndexOf('=');
                        if (eq >= 0) t = t.Substring(eq + 1).Trim();
                        if (string.Equals(t, host, StringComparison.OrdinalIgnoreCase)) return true;
                    }
                }
            }
            catch (Exception ex) { Log.Error("SAN parse failed", ex); }
            return false;
        }

        private static X509Certificate2 FindPinnedRoot(X509Chain chain, byte[] leafMod)
        {
            if (chain == null) return null;
            // 叶子模数已单独比对；这里只需确认链尾（自签根）的模数等于 pin
            if (chain.ChainElements.Count > 0)
            {
                X509Certificate2 last = chain.ChainElements[chain.ChainElements.Count - 1].Certificate;
                if (last != null)
                {
                    byte[] mod = Modulus(last);
                    if (mod != null && Matches(mod, ExpectedRootB64)) return last;
                }
            }
            return null;
        }

        /// <summary>用内置根作为唯一信任锚重建链（明确不使用系统根）</summary>
        private static bool ChainsTo(X509Certificate2 leaf, X509Certificate2 root)
        {
            using (X509Chain c = new X509Chain())
            {
                c.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;       // 内网自签根无 CRL
                c.ChainPolicy.VerificationFlags = X509VerificationFlags.AllowUnknownCertificateAuthority;
                c.ChainPolicy.ExtraStore.Add(root);
                c.Build(leaf);
                if (c.ChainElements.Count == 0) return false;
                X509Certificate2 top = c.ChainElements[c.ChainElements.Count - 1].Certificate;
                return top != null && string.Equals(top.Thumbprint, root.Thumbprint, StringComparison.OrdinalIgnoreCase);
            }
        }

        private static byte[] Modulus(X509Certificate2 cert)
        {
            try
            {
                using (RSA rsa = cert.GetRSAPublicKey())
                {
                    if (rsa == null) return null;
                    RSAParameters p = rsa.ExportParameters(false);
                    return p.Modulus;
                }
            }
            catch (Exception ex)
            {
                Log.Error("read certificate modulus failed", ex);
                return null;
            }
        }

        private static bool Matches(byte[] modulus, string expectedB64)
        {
            if (modulus == null || string.IsNullOrEmpty(expectedB64)) return false;
            byte[] want;
            try { want = Convert.FromBase64String(expectedB64); }
            catch { return false; }
            byte[] a = Trim(modulus), b = Trim(want);
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        private static byte[] Trim(byte[] v)
        {
            int i = 0;
            while (i < v.Length - 1 && v[i] == 0) i++;
            byte[] r = new byte[v.Length - i];
            Array.Copy(v, i, r, 0, r.Length);
            return r;
        }
    }
}
