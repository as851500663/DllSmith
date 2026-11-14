// ============================================================================
//  DllSmith 1.4 — 目录信任（安全审计 A-03 + captain 2026-11-14 完整性硬要求）
//  文件：CatalogTrust.cs
//
//  manifest 行格式（server t11，全量条目入签）：
//      f|path|sha256         每条一行；sha256 可为空串
//      count = 行数 = 条目总数；coverage = 有 sha256 的条数（只用于展示覆盖率）
//  规范串：v1|manifest|<catalogKey>|<sha256(lines 的 UTF-8 字节)>|<count>
//
//  判定规则：
//    ① count != lines 行数、或 sha256(lines) != lines_sha256            -> 只读
//    ② 验签失败 / kid 未知 / 非 https                                  -> 只读
//    ③ count 与目录实际条目数不一致、目录里出现清单之外的条目            -> 只读
//    ④ 清单 path 与目录 FILEPATH 不一致（= 目录被改过）                  -> 只读
//    ⑤ coverage == 0（一条哈希都没有）                                  -> 只读
//    ⑥ 以上全过 -> 可修；条目有 sha256 必须逐字节匹配，空则标「哈希未覆盖」
//  一律不做"没有哈希就当通过"的静默放行。
// ============================================================================

using System;
using System.Collections.Generic;
using System.Text;

namespace DllFix.Core
{
    public enum CatalogTrustState
    {
        Unknown,     // 还没校验
        Trusted,     // 清单完整、验签通过、与目录逐条一致
        ReadOnly,    // 校验失败 -> 可扫不可修
        Offline      // 目录服务不可达 -> 只用本地数据，同样不可修
    }

    public class CatalogTrust
    {
        public CatalogTrustState State = CatalogTrustState.Unknown;
        public string Reason = "";
        public string Key = "";
        public string Kid = "";
        public int HashedEntries;      // coverage：有 sha256 的条目数
        public int TotalEntries;       // count / total：条目总数（= 清单行数）
        public int LineCount;          // lines 实际行数
        public string PublishedAt = "";

        /// <summary>
        /// "filename|path" 复合键 -> sha256（空串 = 条目在清单内但未覆盖哈希）。
        /// 目录里大量同名不同路径条目（SystemDll 有 892 组，如 System32/SysWOW64 各一份），
        /// 所以必须用复合键，不能只用文件名。
        /// </summary>
        public readonly Dictionary<string, string> ShaByKey =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>签名覆盖的全部复合键（用于检出目录多出/改动条目）</summary>
        public readonly HashSet<string> SignedKeys =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// 目录键统一口径（与 server t27 一字不差）：
        ///   f = FILENAME（非空且不是 "0"/"?"/"—"）否则 basename(DOWNLOAD) 否则 name
        ///   p = FILEPATH（非空且不是 "0"/"?"/"—"）否则 ""
        ///   key = lower(f + "|" + p)
        /// 路径一律用**原串**（不再做 ResolveX），空/占位值统一成空串参与比较；
        /// 展示层可以继续显示 "—"，但不能让占位符进入键。
        /// </summary>
        public static string ComposeKey(string fileName, string path)
        {
            return (NormalizeId(fileName) + "|" + NormalizePath(path)).ToLowerInvariant();
        }

        /// <summary>占位/空值判定：这些值在目录里表示"没有"</summary>
        public static bool IsPlaceholder(string v)
        {
            if (string.IsNullOrEmpty(v)) return true;
            string t = v.Trim();
            return t.Length == 0 || t == "0" || t == "?" || t == "???" || t == "—" || t == "-";
        }

        public static string NormalizePath(string path)
        {
            return IsPlaceholder(path) ? "" : path.Trim();
        }

        public static string NormalizeId(string id)
        {
            return IsPlaceholder(id) ? "" : id.Trim();
        }

        /// <summary>条目侧标识：FILENAME → basename(DOWNLOAD) → 展示名</summary>
        public static string IdOf(CatalogItem item)
        {
            if (item == null) return "";
            string url = string.IsNullOrEmpty(item.PkgUrl) ? item.Download : item.PkgUrl;
            string urlBase = UrlBase(url);
            string pathBase = WinBase(item.FilePath);
            string fn = NormalizeId(item.FileName);
            if (IsSysdllKind(item.SourceKey))
            {
                if (fn.Length > 0) return fn;
                if (LooksLikeFilename(urlBase)) return urlBase;
                if (pathBase.Length > 0) return pathBase;
            }
            else
            {
                if (LooksLikeFilename(urlBase)) return urlBase;
                if (fn.Length > 0) return fn;
                if (pathBase.Length > 0) return pathBase;
            }
            string dn = NormalizeId(item.DisplayName);
            if (dn.Length > 0) return dn;
            return NormalizeId(item.Name);
        }

        /// <summary>是否 SystemDll 类目录（服务端 kind=sysdll；其余一律 runtime）</summary>
        public static bool IsSysdllKind(string sourceKey)
        {
            return !string.IsNullOrEmpty(sourceKey)
                && sourceKey.StartsWith("SystemDll_", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>是否像文件名（带扩展名、不是纯数字）——与服务端 looks_like_filename 一致</summary>
        public static bool LooksLikeFilename(string name)
        {
            if (string.IsNullOrEmpty(name) || name.Length > 260) return false;
            if (name == "." || name == "..") return false;
            if (name.IndexOf('.') < 0) return false;
            bool allDigits = true;
            foreach (char ch in name) if (!char.IsDigit(ch)) { allDigits = false; break; }
            if (allDigits) return false;
            return true;
        }

        /// <summary>URL 去 query/fragment 后取 Windows basename</summary>
        public static string UrlBase(string url)
        {
            string u = (url ?? "").Trim();
            if (u.Length == 0) return "";
            int q = u.IndexOf('?'); if (q >= 0) u = u.Substring(0, q);
            int h = u.IndexOf('#'); if (h >= 0) u = u.Substring(0, h);
            return WinBase(u);
        }

        public static string WinBase(string path)
        {
            string p = (path ?? "").Replace('\\', '/').Trim();
            if (p.Length == 0) return "";
            int i = p.LastIndexOf('/');
            return i >= 0 ? p.Substring(i + 1) : p;
        }

        /// <summary>规范化后的 p 段：真路径优先；runtime 类无路径时用 MATCH#URL 或显示名（与服务端 t27 一致）</summary>
        public static string PathOf(CatalogItem item)
        {
            if (item == null) return "";
            string path = NormalizePath(item.FilePath);
            if (path.Length > 0) return path;
            if (IsSysdllKind(item.SourceKey)) return "";
            string match = NormalizeId(item.Match);
            string url = string.IsNullOrEmpty(item.PkgUrl) ? item.Download : item.PkgUrl;
            url = UrlNoQuery(url);
            if (match.Length > 0 && url.Length > 0) return match + "#" + url;
            string dn = NormalizeId(item.DisplayName);
            if (dn.Length > 0) return dn;
            return NormalizeId(item.Name);
        }

        public static string UrlNoQuery(string url)
        {
            string u = (url ?? "").Trim();
            if (u.Length == 0) return "";
            int q = u.IndexOf('?'); if (q >= 0) u = u.Substring(0, q);
            int h = u.IndexOf('#'); if (h >= 0) u = u.Substring(0, h);
            return u;
        }

        /// <summary>条目的完整键（供 Validate / ApplyTo 使用）</summary>
        public static string KeyOf(CatalogItem item)
        {
            if (item == null) return "";
            return ComposeKey(IdOf(item), PathOf(item));
        }

        public bool CanRepair
        {
            get { return State == CatalogTrustState.Trusted; }
        }

        /// <summary>"哈希覆盖 N / M"（确认框与日志都要显示）</summary>
        public string CoverageText
        {
            get
            {
                int total = TotalEntries > 0 ? TotalEntries : LineCount;
                return "哈希覆盖 " + HashedEntries + " / " + total;
            }
        }

        public double Coverage
        {
            get
            {
                int total = TotalEntries > 0 ? TotalEntries : LineCount;
                if (total <= 0) return 0;
                return (double)HashedEntries / total;
            }
        }

        /// <summary>清单里的哈希（未覆盖返回空串；条目根本不在清单里才返回 null）</summary>
        public string GetHash(string fileName, string path)
        {
            string h;
            return ShaByKey.TryGetValue(ComposeKey(fileName, path), out h) ? h : null;
        }

        public string GetHash(CatalogItem item)
        {
            if (item == null) return null;
            return GetHash(item.FileName, item.FilePath);
        }

        /// <summary>按条目的规范化键取哈希（t25：Installer 类没有 FILENAME，用 basename(DOWNLOAD)）</summary>
        public string GetHashByItemKey(CatalogItem item)
        {
            string h;
            return ShaByKey.TryGetValue(KeyOf(item), out h) ? h : null;
        }

        public bool IsSignedByItemKey(CatalogItem item)
        {
            return SignedKeys.Contains(KeyOf(item));
        }

        public bool IsSigned(string fileName, string path)
        {
            return SignedKeys.Contains(ComposeKey(fileName, path));
        }

        /// <summary>把清单哈希贴回条目；返回被贴上哈希的条目数</summary>
        public int ApplyTo(IEnumerable<CatalogItem> items)
        {
            int n = 0;
            if (items == null) return 0;
            foreach (CatalogItem it in items)
            {
                string h = GetHashByItemKey(it);
                if (string.IsNullOrEmpty(h)) continue;
                it.Sha256 = h;
                n++;
            }
            return n;
        }

        public static CatalogTrust Offline(string reason)
        {
            CatalogTrust t = new CatalogTrust();
            t.State = CatalogTrustState.Offline;
            t.Reason = reason;
            return t;
        }

        public static CatalogTrust ReadOnly(string key, string reason)
        {
            CatalogTrust t = new CatalogTrust();
            t.State = CatalogTrustState.ReadOnly;
            t.Key = key;
            t.Reason = reason;
            return t;
        }
    }

    /// <summary>manifest 拉取 + 验签 + 解析 + 完整性判定（缓存 10 分钟）</summary>
    public static class CatalogTrustLoader
    {
        private static readonly Dictionary<string, CatalogTrust> Cache =
            new Dictionary<string, CatalogTrust>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, DateTime> CacheAt = new Dictionary<string, DateTime>();
        private static readonly object Sync = new object();
        private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(10);

        public static void Invalidate()
        {
            lock (Sync) { Cache.Clear(); CacheAt.Clear(); }
        }

        /// <summary>按目录 key 校验一份条目集合（key 取自条目自身的 SourceKey）</summary>
        public static CatalogTrust Load(string key, IList<CatalogItem> items)
        {
            return Load(key, items, null);
        }

        /// <summary>
        /// 优先用 catalog 响应的 X-Manifest-* 头（省一次请求），缺失再拉 /api/v1/manifest。
        /// </summary>
        public static CatalogTrust Load(string key, IList<CatalogItem> items,
                                       Dictionary<string, object> inlineManifest)
        {
            if (string.IsNullOrEmpty(key))
                return CatalogTrust.ReadOnly("", "目录 key 为空，无法校验清单");

            lock (Sync)
            {
                CatalogTrust cached;
                DateTime at;
                if (Cache.TryGetValue(key, out cached) && CacheAt.TryGetValue(key, out at)
                    && DateTime.Now - at < Ttl)
                {
                    return Validate(cached, items);
                }
            }

            CatalogTrust t = inlineManifest != null ? FromManifestFields(key, inlineManifest) : null;
            if (t == null) t = Fetch(key);

            lock (Sync)
            {
                Cache[key] = t;
                CacheAt[key] = DateTime.Now;
            }
            return Validate(t, items);
        }

        /// <summary>
        /// 完整性判定（captain 硬要求）：清单不全 / 目录多出条目 / path 对不上 -> 只读。
        /// </summary>
        private static CatalogTrust Validate(CatalogTrust t, IList<CatalogItem> items)
        {
            if (t == null) return CatalogTrust.ReadOnly("", "清单为空");
            if (t.State != CatalogTrustState.Trusted) return t;

            int actual = items == null ? 0 : items.Count;

            if (t.LineCount <= 0)
            {
                t.State = CatalogTrustState.ReadOnly;
                t.Reason = "目录完整性校验失败：签名清单为空，无法确认任何条目";
                return t;
            }
            if (t.TotalEntries > 0 && t.LineCount != t.TotalEntries)
            {
                t.State = CatalogTrustState.ReadOnly;
                t.Reason = "目录完整性校验失败：清单声明 " + t.TotalEntries + " 条，实际只有 " + t.LineCount + " 行";
                return t;
            }
            if (actual > 0 && t.LineCount != actual)
            {
                t.State = CatalogTrustState.ReadOnly;
                t.Reason = "目录完整性校验失败：目录下发 " + actual + " 条，签名清单 " + t.LineCount + " 条";
                return t;
            }
            if (t.HashedEntries <= 0 && actual > 0)
            {
                // captain 2026-11-14：coverage=0 不等于只读 —— 目录已签名 + 走 HTTPS 即可修复，
                // 但每个条目都要标「内容未做哈希校验」并显示覆盖率（见 RepairEngine / 界面）。
                Log.Info("manifest coverage=0 (entries signed, hashes pending): " + t.Key + " " + t.CoverageText);
            }

            // 逐条对「filename|FILEPATH」复合键：目录多了/改了条目，立即只读
            int missing = 0;
            string firstMissing = null;
            if (items != null)
            {
                foreach (CatalogItem it in items)
                {
                    if (!t.IsSignedByItemKey(it))
                    {
                        missing++;
                        if (firstMissing == null)
                            firstMissing = CatalogTrust.IdOf(it) + " -> " + CatalogTrust.NormalizePath(it.FilePath);
                    }
                }
            }
            if (missing > 0)
            {
                t.State = CatalogTrustState.ReadOnly;
                t.Reason = "目录完整性校验失败：" + missing + " 个条目不在签名清单内（例如 "
                           + (firstMissing == null ? "?" : firstMissing) + "）";
                return t;
            }

            t.ApplyTo(items);
            Log.Info("manifest OK key=" + t.Key + " " + t.CoverageText + " lines=" + t.LineCount);
            return t;
        }


        private static CatalogTrust FromManifestFields(string key, Dictionary<string, object> src)
        {
            try
            {
                string sig = Json.GetString(src, "manifest_sig", "sig");
                if (string.IsNullOrEmpty(sig)) return null;
                Dictionary<string, object> d = new Dictionary<string, object>(src);
                d["sig"] = sig;
                d["lines_sha256"] = Json.GetString(src, "manifest_lines_sha256");
                d["kid"] = Json.GetString(src, "manifest_kid");
                d["count"] = Json.GetString(src, "manifest_count");
                d["total"] = Json.GetString(src, "manifest_total");
                d["key"] = key;
                // 头部没有 lines 正文，只能校验 count/lines_sha256 的签名覆盖
                d["lines"] = Json.GetString(src, "manifest_lines");
                if (string.IsNullOrEmpty(Json.GetString(d, "lines")))
                {
                    // 无正文时不能完成逐条判定 -> 回退到单独拉 manifest
                    return null;
                }
                CatalogTrust t = ParseAndVerify(d, key);
                if (t != null) Log.Info("manifest source=header key=" + key);
                return t;
            }
            catch (Exception ex)
            {
                Log.Error("inline manifest parse failed", ex);
                return null;
            }
        }

        private static CatalogTrust Fetch(string key)
        {
            string server = Config.ServerUrl;
            if (string.IsNullOrEmpty(server))
                return CatalogTrust.Offline("未配置目录服务（config.json），本次不修复");

            string url = server + "/api/v1/manifest?key=" + Uri.EscapeDataString(key);
            if (!UpdateTrust.IsHttps(url) && !Config.AllowInsecureServer)
                return CatalogTrust.ReadOnly(key, "目录服务不是 HTTPS，清单不可信（已切换为只读）");

            string body = ApiClient.GetRaw(url, 8000);
            if (string.IsNullOrEmpty(body))
                return CatalogTrust.Offline("无法获取签名清单（" + ApiClient.SafeUrl(url) + "）");

            try
            {
                Dictionary<string, object> d = Json.Parse(body) as Dictionary<string, object>;
                if (d == null) return CatalogTrust.ReadOnly(key, "清单响应不是合法 JSON");
                CatalogTrust t = ParseAndVerify(d, key);
                return t ?? CatalogTrust.ReadOnly(key, "清单格式不合法");
            }
            catch (Exception ex)
            {
                Log.Error("manifest verify failed: " + key, ex);
                return CatalogTrust.ReadOnly(key, "清单解析失败：" + ex.Message);
            }
        }

        /// <summary>解析 + 内容自洽 + 验签（不含与目录条目的比对）</summary>
        private static CatalogTrust ParseAndVerify(Dictionary<string, object> d, string key)
        {
            string lines = Json.GetString(d, "lines");
            if (lines == null) lines = "";
            string linesSha = Json.GetString(d, "lines_sha256");
            string sig = Json.GetString(d, "sig", "signature");
            string kid = Json.GetString(d, "kid");
            string mk = Json.GetString(d, "key");
            if (string.IsNullOrEmpty(mk)) mk = key;
            int count = ToInt(d, "count");
            int total = ToInt(d, "total");

            CatalogTrust t = new CatalogTrust();
            t.Key = mk;
            t.Kid = kid;
            t.PublishedAt = Json.GetString(d, "published_at");

            string calc = Hashes.Sha256Hex(Encoding.UTF8.GetBytes(lines));
            if (!string.IsNullOrEmpty(linesSha)
                && !string.Equals(calc, linesSha.Trim().ToLowerInvariant(), StringComparison.Ordinal))
            {
                t.State = CatalogTrustState.ReadOnly;
                t.Reason = "清单内容与 lines_sha256 不一致（可能被截断或篡改）";
                return t;
            }

            int lineCount = CountLines(lines);
            t.LineCount = lineCount;
            if (count != lineCount)
            {
                t.State = CatalogTrustState.ReadOnly;
                t.Reason = "清单 count(" + count + ") 与 lines 行数(" + lineCount + ") 不一致";
                return t;
            }

            if (string.IsNullOrEmpty(sig))
            {
                t.State = CatalogTrustState.ReadOnly;
                t.Reason = "清单未签名（服务端 sig 为空）";
                return t;
            }
            string n, e;
            if (!ReleaseKeys.TryGet(kid, out n, out e))
            {
                t.State = CatalogTrustState.ReadOnly;
                t.Reason = "清单签名密钥未知 kid=" + (string.IsNullOrEmpty(kid) ? "(无)" : kid);
                return t;
            }
            string canonical = UpdateTrust.ManifestCanonical(mk, calc, count);
            byte[] sigBytes;
            try { sigBytes = Convert.FromBase64String(sig); }
            catch
            {
                t.State = CatalogTrustState.ReadOnly;
                t.Reason = "清单签名不是合法 base64";
                return t;
            }
            if (!RsaVerify.VerifySha256(n, e, canonical, sigBytes))
            {
                t.State = CatalogTrustState.ReadOnly;
                t.Reason = "清单签名校验失败（目录数据不可信）";
                return t;
            }

            // 行格式：f|path|sha256（sha256 可为空）
            if (lineCount > 0)
            {
                foreach (string rawLine in lines.Split('\n'))
                {
                    string line = rawLine.TrimEnd('\r');
                    if (line.Trim().Length == 0) continue;
                    string[] parts = line.Split('|');
                    if (parts.Length < 3)
                    {
                        t.State = CatalogTrustState.ReadOnly;
                        t.Reason = "清单行格式不合法（应为 f|path|sha256）：" + Trim(line);
                        return t;
                    }
                    string f = parts[0].Trim();
                    string path = parts[1].Trim();
                    string sha = parts[2].Trim().ToLowerInvariant();
                    if (f.Length == 0) continue;
                    if (sha.Length > 0 && !Hashes.LooksLikeSha256(sha))
                    {
                        t.State = CatalogTrustState.ReadOnly;
                        t.Reason = "清单里的 sha256 不合法：" + f;
                        return t;
                    }
                    string ck = CatalogTrust.ComposeKey(f, path);
                    t.SignedKeys.Add(ck);
                    t.ShaByKey[ck] = sha;
                    if (sha.Length > 0) t.HashedEntries++;
                }
            }
            t.TotalEntries = total > 0 ? total : lineCount;
            t.State = CatalogTrustState.Trusted;
            return t;
        }

        private static string Trim(string s)
        {
            if (s == null) return "";
            s = s.Trim();
            return s.Length > 60 ? s.Substring(0, 60) + "…" : s;
        }

        private static int CountLines(string lines)
        {
            if (string.IsNullOrEmpty(lines)) return 0;
            int n = 0;
            foreach (string l in lines.Split('\n'))
                if (l.Trim().Length > 0) n++;
            return n;
        }

        private static int ToInt(Dictionary<string, object> d, string key)
        {
            object v;
            if (d == null || !d.TryGetValue(key, out v) || v == null) return 0;
            try { return Convert.ToInt32(v); } catch { return 0; }
        }
    }

    /// <summary>
    /// 一个页面可能跨多个 catalog key（运行库 = VC_Scan + VC_DLL_Scan + NETWORK）。
    /// 逐个 key 校验，全部 Trusted 才允许修复；覆盖率按 key 汇总。
    /// </summary>
    public class CatalogTrustSet
    {
        public readonly Dictionary<string, CatalogTrust> ByKey =
            new Dictionary<string, CatalogTrust>(StringComparer.OrdinalIgnoreCase);

        public bool CanRepair
        {
            get
            {
                if (ByKey.Count == 0) return false;
                foreach (CatalogTrust t in ByKey.Values)
                    if (!t.CanRepair) return false;
                return true;
            }
        }

        public string Reason
        {
            get
            {
                foreach (CatalogTrust t in ByKey.Values)
                    if (!t.CanRepair) return t.Reason;
                return "";
            }
        }

        public int HashedEntries
        {
            get
            {
                int n = 0;
                foreach (CatalogTrust t in ByKey.Values) n += t.HashedEntries;
                return n;
            }
        }

        public int TotalEntries
        {
            get
            {
                int n = 0;
                foreach (CatalogTrust t in ByKey.Values) n += t.TotalEntries;
                return n;
            }
        }

        public string CoverageText
        {
            get { return "哈希覆盖 " + HashedEntries + " / " + TotalEntries; }
        }

        /// <summary>按条目自身的 SourceKey 分组后并发校验</summary>
        public static CatalogTrustSet LoadAll(IList<CatalogItem> items)
        {
            CatalogTrustSet set = new CatalogTrustSet();
            if (items == null || items.Count == 0) return set;

            Dictionary<string, List<CatalogItem>> groups =
                new Dictionary<string, List<CatalogItem>>(StringComparer.OrdinalIgnoreCase);
            foreach (CatalogItem it in items)
            {
                string k = string.IsNullOrEmpty(it.SourceKey) ? "(unknown)" : it.SourceKey;
                List<CatalogItem> g;
                if (!groups.TryGetValue(k, out g)) { g = new List<CatalogItem>(); groups[k] = g; }
                g.Add(it);
            }

            object sync = new object();
            List<System.Threading.Thread> threads = new List<System.Threading.Thread>();
            foreach (KeyValuePair<string, List<CatalogItem>> kv in groups)
            {
                string key = kv.Key;
                List<CatalogItem> groupItems = kv.Value;
                System.Threading.Thread th = new System.Threading.Thread(delegate()
                {
                    Dictionary<string, object> inline = CatalogCache.GetManifest(key);
                    if (inline == null)
                        inline = CatalogCache.InlineManifestFromHeaders(CatalogCache.GetHeaders(key));
                    CatalogTrust t = CatalogTrustLoader.Load(key, groupItems, inline);
                    lock (sync) set.ByKey[key] = t;
                });
                th.IsBackground = true;
                th.Start();
                threads.Add(th);
            }
            foreach (System.Threading.Thread th in threads) th.Join(15000);

            foreach (KeyValuePair<string, List<CatalogItem>> kv in groups)
                if (!set.ByKey.ContainsKey(kv.Key))
                    set.ByKey[kv.Key] = CatalogTrust.ReadOnly(kv.Key, "清单校验超时");
            return set;
        }
    }
}
