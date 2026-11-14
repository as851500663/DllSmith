// ============================================================================
//  DllSmith 1.4 — 路径安全（安全审计 A-03 / A-05 / A-13）
//  文件：PathGuard.cs
//  内容：统一先 Path.GetFullPath 规范化，再做**前缀精确匹配**（不是子串 Contains）；
//        根目录与每一级都判 ReparsePoint（防 junction/符号链接把写/删重定向到别处）；
//        清理与迁移额外拒绝 System32 / WinSxS / DriverStore。
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;

namespace DllFix.Core
{
    /// <summary>
    /// 外部进程参数封装：所有拼进命令行的路径都必须过 Quote()。
    /// 含双引号/换行的路径直接拒绝（历史上就是尾部反斜杠 + 引号把 robocopy 参数搞坏，exit=16）。
    /// </summary>
    public static class ProcArgs
    {
        /// <summary>拒绝可疑路径：含双引号、单引号、换行</summary>
        public static string Quote(string value)
        {
            string v = value == null ? "" : value.Trim();
            if (v.Length == 0) throw new ArgumentException("空路径不能作为进程参数");
            if (v.IndexOf('"') >= 0 || v.IndexOf('\'') >= 0
                || v.IndexOf('\n') >= 0 || v.IndexOf('\r') >= 0)
                throw new ArgumentException("路径含非法字符（引号/换行），已拒绝：" + v);
            // 去掉尾部反斜杠后再包引号：否则 "C:\a\" 会把结尾引号转义掉
            v = v.TrimEnd('\\');
            return "\"" + v + "\"";
        }
    }

    public static class PathGuard
    {
        /// <summary>允许被覆盖的目标扩展名（唯一权威来源；普查、自检、文档都引这个）</summary>
        public static readonly string[] AllowedExtensions =
            new string[] { ".dll", ".msc", ".dls", ".mui", ".cpl", ".com", ".rll" };

        private static readonly HashSet<string> AllowedExtSet =
            new HashSet<string>(AllowedExtensions, StringComparer.OrdinalIgnoreCase);

        public static bool IsAllowedTargetExtension(string ext)
        {
            return !string.IsNullOrEmpty(ext) && AllowedExtSet.Contains(ext);
        }

        public static string AllowedExtensionText()
        {
            return string.Join(" / ", AllowedExtensions);
        }

        private static readonly HashSet<string> Registered =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly object RegSync = new object();

        /// <summary>登记一个"签名清单里出现过"的目标目录（Program Files / AppData 下必需）</summary>
        public static void Register(string dir)
        {
            string d = TrimEnd(Normalize(dir));
            if (d.Length == 0) return;
            lock (RegSync) Registered.Add(d);
        }

        public static bool IsRegisteredDir(string dir)
        {
            string d = TrimEnd(Normalize(dir));
            if (d.Length == 0) return false;
            lock (RegSync)
            {
                if (Registered.Contains(d)) return true;
                foreach (string r in Registered)
                    if (d.StartsWith(r + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(d, r, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        public static int RegisteredCount
        {
            get { lock (RegSync) return Registered.Count; }
        }

        /// <summary>规范化：绝对是绝对路径、去掉 ..、统一分隔符；失败返回空串</summary>
        public static string Normalize(string path)
        {
            if (string.IsNullOrEmpty(path)) return string.Empty;
            try { return Path.GetFullPath(path); }
            catch { return string.Empty; }
        }

        /// <summary>
        /// 前缀精确匹配：path 必须在 root 之下（含 root 本身？不含 —— 写/删操作都不允许落到根本身）。
        /// 用 "root + 分隔符" 做 StartsWith，避免 C:\Windows\System32x 这类子串误判。
        /// </summary>
        public static bool IsUnder(string path, string root, bool allowRootItself)
        {
            string p = TrimEnd(Normalize(path));
            string r = TrimEnd(Normalize(root));
            if (p.Length == 0 || r.Length == 0) return false;
            if (string.Equals(p, r, StringComparison.OrdinalIgnoreCase)) return allowRootItself;
            return p.StartsWith(r + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }

        private static string TrimEnd(string p)
        {
            if (string.IsNullOrEmpty(p)) return p;
            return p.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        /// <summary>目标自身或任一父目录是重解析点（junction / 符号链接）</summary>
        public static bool HasReparseInChain(string path, bool includeSelf)
        {
            string p = Normalize(path);
            if (p.Length == 0) return true;
            try
            {
                if (includeSelf)
                {
                    FileAttributes a = File.GetAttributes(p);
                    if ((a & FileAttributes.ReparsePoint) == FileAttributes.ReparsePoint) return true;
                }
            }
            catch { /* 文件不存在是正常的 */ }

            string cur = p;
            for (int guard = 0; guard < 64; guard++)
            {
                cur = Path.GetDirectoryName(cur);
                if (string.IsNullOrEmpty(cur)) return false;
                try
                {
                    FileAttributes a = File.GetAttributes(cur);
                    if ((a & FileAttributes.ReparsePoint) == FileAttributes.ReparsePoint) return true;
                }
                catch { return false; }
            }
            return false;
        }

        /// <summary>系统关键目录（清理/迁移一律拒绝）</summary>
        public static bool IsForbiddenZone(string path)
        {
            string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            string[] zones = new string[]
            {
                Path.Combine(win, "System32"),
                Path.Combine(win, "SysWOW64"),
                Path.Combine(win, "WinSxS"),
                Path.Combine(win, "System32", "DriverStore"),
                Path.Combine(win, "System32", "config"),
                Path.Combine(win, "servicing"),
                Path.Combine(win, "assembly")
            };
            foreach (string z in zones)
                if (IsUnder(path, z, true)) return true;
            return false;
        }

        /// <summary>
        /// 系统 DLL 覆盖目标校验（A-03）。
        /// 白名单：%WINDIR%\System32、%WINDIR%\SysWOW64、%WINDIR%\WinSxS、Program Files、用户 AppData。
        /// 额外要求：必须是文件、扩展名 .dll、路径上没有重解析点、不在 config/CatRoot 等禁区。
        /// 返回 null = 通过；否则返回拒绝原因。
        /// </summary>
        public static string CheckDllTarget(string target)
        {
            string p = Normalize(target);
            if (p.Length == 0) return "目标路径非法";

            string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string roam = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

            List<string> allowed = new List<string>();
            if (!string.IsNullOrEmpty(win))
            {
                allowed.Add(Path.Combine(win, "System32"));
                allowed.Add(Path.Combine(win, "SysWOW64"));
                allowed.Add(Path.Combine(win, "WinSxS"));
            }
            if (!string.IsNullOrEmpty(pf)) allowed.Add(pf);
            if (!string.IsNullOrEmpty(pf86)) allowed.Add(pf86);
            if (!string.IsNullOrEmpty(local)) allowed.Add(local);
            if (!string.IsNullOrEmpty(roam)) allowed.Add(roam);

            bool ok = false;
            foreach (string root in allowed)
                if (!string.IsNullOrEmpty(root) && IsUnder(p, root, false)) { ok = true; break; }
            if (!ok) return "目标路径不在允许的目录内（System32/SysWOW64/WinSxS/Program Files/AppData）";

            // 目标扩展名白名单（t23 收敛）：只放行目录里**真实存在**的系统文件类型。
            //   .dll  系统组件      .msc  MMC 控制台 XML      .dls  DirectMusic 音色库（RIFF）
            //   .mui  多语言资源    .cpl  控制面板项          .com  兼容命令
            //   .rll  资源库
            // 已移除 .xml：全目录普查 0 个 .xml 目标，属于多余的可写面。
            string tex = Path.GetExtension(p).ToLowerInvariant();
            if (!IsAllowedTargetExtension(tex))
                return "只允许覆盖 " + AllowedExtensionText() + " 文件（当前："
                     + (tex.Length == 0 ? "(无扩展名)" : tex) + "）";

            if (IsUnder(p, Path.Combine(win, "System32", "config"), true)
                || IsUnder(p, Path.Combine(win, "System32", "CatRoot"), true)
                || IsUnder(p, Path.Combine(win, "System32", "DriverStore"), true))
                return "目标位于系统保留目录（config / CatRoot / DriverStore），拒绝写入";

            // 临时目录一律拒绝：%TEMP% 是任意用户可写落点，目录数据指向这里等于给了
            // 一个"以管理员权限往临时目录写任意文件"的入口（QA t6 P0-2 同源问题）。
            // AppData 其它位置仍按白名单放行。
            string tmp = TrimEnd(Normalize(Path.GetTempPath()));
            if (tmp.Length > 0 && IsUnder(p, tmp, true))
                return "目标位于临时目录（%TEMP%），拒绝写入";
            if (!string.IsNullOrEmpty(local))
            {
                string localTemp = Path.Combine(local, "Temp");
                if (IsUnder(p, localTemp, true))
                    return "目标位于 AppData\\Local\\Temp，拒绝写入";
                string packages = Path.Combine(local, "Packages");
                if (IsUnder(p, packages, true) && p.IndexOf("\\TempState", StringComparison.OrdinalIgnoreCase) >= 0)
                    return "目标位于 AppData\\Local\\Packages\\*\\TempState，拒绝写入";
            }

            if (HasReparseInChain(p, true))
                return "目标路径上存在目录联接/符号链接，拒绝写入（可能被重定向到系统外）";

            // Program Files / AppData 这两片区域太大，单靠"在目录内"不足以说明是我们登记过的组件。
            // 额外要求：目标目录必须由**签名清单**里的条目登记过（PathGuard.Register）。
            // 未登记（例如直接调引擎、或目录数据被替换）→ 拒绝，而不是放行。
            bool wideArea = (!string.IsNullOrEmpty(pf) && IsUnder(p, pf, false))
                         || (!string.IsNullOrEmpty(pf86) && IsUnder(p, pf86, false))
                         || (!string.IsNullOrEmpty(local) && IsUnder(p, local, false))
                         || (!string.IsNullOrEmpty(roam) && IsUnder(p, roam, false));
            if (wideArea)
            {
                string dir = TrimEnd(Path.GetDirectoryName(p));
                if (!IsRegisteredDir(dir))
                    return "目标目录未登记（Program Files / AppData 下只允许签名清单登记过的目录）：" + dir;
            }

            return null;
        }

        /// <summary>清理/迁移的根目录校验：白名单前缀 + 非重解析点 + 非系统关键目录</summary>
        public static string CheckCleanRoot(string path)
        {
            string p = Normalize(path);
            if (p.Length == 0) return "路径非法";
            if (IsForbiddenZone(p)) return "系统关键目录，拒绝操作：" + p;
            if (HasReparseInChain(p, true)) return "路径含目录联接/符号链接，拒绝操作：" + p;
            if (p.Length <= 3 && p.EndsWith(":\\", StringComparison.Ordinal)) return "不允许对磁盘根目录操作";

            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string roam = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            string temp = Path.GetTempPath();

            List<string> allowed = new List<string>();
            allowed.Add(temp);
            allowed.Add(Path.Combine(win, "Temp"));
            allowed.Add(Path.Combine(win, "Logs"));
            allowed.Add(Path.Combine(win, "Panther"));
            allowed.Add(Path.Combine(win, "Prefetch"));
            allowed.Add(Path.Combine(win, "SoftwareDistribution"));
            allowed.Add(Path.Combine(local, "CrashDumps"));
            allowed.Add(Path.Combine(local, "Microsoft", "Windows", "Explorer"));
            allowed.Add(Path.Combine(local, "Microsoft", "Edge", "User Data"));
            allowed.Add(Path.Combine(local, "Google", "Chrome", "User Data"));
            allowed.Add(Path.Combine(roam, "Tencent"));
            allowed.Add(Path.Combine(local, "Tencent"));
            if (!string.IsNullOrEmpty(local)) allowed.Add(local);
            if (!string.IsNullOrEmpty(roam)) allowed.Add(roam);

            foreach (string root in allowed)
            {
                if (string.IsNullOrEmpty(root)) continue;
                if (IsUnder(p, root, false) || string.Equals(TrimEnd(p), TrimEnd(Normalize(root)), StringComparison.OrdinalIgnoreCase))
                    return null;
            }
            return "不在清理白名单内：" + p;
        }
    }
}
