// ============================================================================
//  DllSmith — C 盘清理 / 瘦身
//  文件：Clean.cs
//  内容：扫描可清理缓存、扫描 AppData 大文件夹、清理、迁移+目录联接(Junction)
//  说明：全部动作走白名单目录；删除失败（占用）自动跳过；迁移前检查目标盘空间，
//        迁移后自动建立 Junction，并写回滚清单到 Logs\clean-<时间>.json
// ============================================================================
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Markup;
using Path = System.IO.Path;
using DllFix.Core;

namespace DllFix.Ui
{
    // ── 数据模型 ────────────────────────────────────────────────────────────
    public enum CleanKind { Cache, BigFolder }

    public class CleanItem : System.ComponentModel.INotifyPropertyChanged
    {
        public event System.ComponentModel.PropertyChangedEventHandler PropertyChanged;

        private int _percent;
        private string _status = "";

        private void Raise(string name)
        {
            System.ComponentModel.PropertyChangedEventHandler h = PropertyChanged;
            if (h != null) h(this, new System.ComponentModel.PropertyChangedEventArgs(name));
        }

        /// <summary>单项进度 0-100（0 = 未开始）</summary>
        public int Percent
        {
            get { return _percent; }
            set { _percent = value; Raise("Percent"); Raise("PercentText"); Raise("HasPercent"); }
        }

        public string PercentText
        {
            get { return _percent <= 0 ? "" : (_percent >= 100 ? "完成" : _percent + "%"); }
        }

        public Visibility HasPercent
        {
            get { return _percent > 0 ? Visibility.Visible : Visibility.Collapsed; }
        }

        public string Name { get; set; }
        public string Detail { get; set; }
        public string Path { get; set; }
        public long Bytes { get; set; }
        public int Files { get; set; }
        public bool Selected { get; set; }
        public CleanKind Kind { get; set; }
        /// <summary>所属磁盘根目录（回收站项用，如 C:\）</summary>
        public string Drive { get; set; }

        public string Status
        {
            get { return _status; }
            set { _status = value; Raise("Status"); }
        }

        public string SizeText
        {
            get
            {
                if (Bytes <= 0) return "—";
                return CleanEngine.FormatSize(Bytes);
            }
        }

        public string CountText
        {
            get { return Files > 0 ? (Files + " 个文件") : ""; }
        }
    }

    // ── 引擎 ────────────────────────────────────────────────────────────────
    public static class CleanEngine
    {
        /// <summary>取消标志（清理 / 迁移共用）</summary>
        public static volatile bool CancelRequested;

        public static string FormatSize(long bytes)
        {
            if (bytes >= 1073741824L) return string.Format("{0:0.00} GB", bytes / 1073741824.0);
            if (bytes >= 1048576L) return string.Format("{0:0.0} MB", bytes / 1048576.0);
            if (bytes >= 1024L) return string.Format("{0:0} KB", bytes / 1024.0);
            return bytes + " B";
        }

        /// <summary>可清理的缓存目录（白名单）</summary>
        public static List<CleanItem> ScanCaches(Action<string> log)
        {
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            string roam = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

            List<CleanItem> list = new List<CleanItem>();

            Add(list, log, "用户临时文件", "%TEMP%", new string[] { Path.GetTempPath() }, true);
            Add(list, log, "系统临时文件", "%WINDIR%\\Temp", new string[] { Path.Combine(win, "Temp") }, true);
            Add(list, log, "Windows 更新缓存", "SoftwareDistribution\\Download",
                new string[] { Path.Combine(win, "SoftwareDistribution", "Download") }, true);
            Add(list, log, "传递优化缓存", "DeliveryOptimization",
                new string[] { Path.Combine(win, "SoftwareDistribution", "DeliveryOptimization") }, true);
            Add(list, log, "预读取文件", "Prefetch",
                new string[] { Path.Combine(win, "Prefetch") }, true);
            Add(list, log, "Windows 日志", "Logs / Panther",
                new string[] { Path.Combine(win, "Logs"), Path.Combine(win, "Panther") }, false);
            Add(list, log, "崩溃转储", "CrashDumps",
                new string[] { Path.Combine(local, "CrashDumps") }, false);
            Add(list, log, "系统内存转储", "MEMORY.DMP",
                new string[] { Path.Combine(win, "MEMORY.DMP") }, false);
            Add(list, log, "缩略图 / 图标缓存", "Explorer\\thumbcache",
                new string[] { Path.Combine(local, "Microsoft", "Windows", "Explorer") }, true);
            Add(list, log, "Edge 缓存", "Edge\\User Data\\*\\Cache",
                new string[] { Path.Combine(local, "Microsoft", "Edge", "User Data") }, false);
            Add(list, log, "Chrome 缓存", "Chrome\\User Data\\*\\Cache",
                new string[] { Path.Combine(local, "Google", "Chrome", "User Data") }, false);
            Add(list, log, "微信 / QQ 缓存", "Tencent 缓存",
                new string[] { Path.Combine(roam, "Tencent"), Path.Combine(local, "Tencent") }, false);

            // 回收站：按磁盘分别统计（SHQueryRecycleBin(null) 是全部磁盘合计，容易误判）
            foreach (DriveInfo d in DriveInfo.GetDrives())
            {
                if (!d.IsReady) continue;
                if (d.DriveType != DriveType.Fixed) continue;
                long rb;
                try { rb = RecycleBinSize(d.Name); } catch { continue; }
                if (rb <= 0) continue;

                CleanItem it = new CleanItem();
                it.Name = "回收站 (" + d.Name.TrimEnd('\\') + ")";
                it.Detail = "清空后不可恢复";
                it.Path = "recycle:" + d.Name;
                it.Drive = d.Name;
                it.Bytes = rb;
                it.Kind = CleanKind.Cache;
                it.Selected = false;   // 默认不勾选，避免误清空
                list.Add(it);
                if (log != null) log(it.Name + " " + FormatSize(rb));
            }

            // 按大小排序，大的在前
            list.Sort(delegate(CleanItem a, CleanItem b) { return b.Bytes.CompareTo(a.Bytes); });
            return list;
        }

        private static void Add(List<CleanItem> list, Action<string> log, string name, string detail, string[] roots, bool recommended)
        {
            long bytes = 0;
            int files = 0;
            foreach (string root in roots)
            {
                if (!Directory.Exists(root) && !File.Exists(root)) continue;
                if (File.Exists(root))
                {
                    try { bytes += new FileInfo(root).Length; files++; } catch { }
                    continue;
                }
                MeasureDir(root, ref bytes, ref files, 0);
            }
            if (bytes <= 0 && files == 0) return;

            CleanItem it = new CleanItem();
            it.Name = name;
            it.Detail = detail;
            it.Path = string.Join(" ; ", roots);
            it.Bytes = bytes;
            it.Files = files;
            it.Kind = CleanKind.Cache;
            it.Selected = recommended;
            list.Add(it);
            if (log != null) log(string.Format("{0}  {1}  ({2} 个文件)", name, FormatSize(bytes), files));
        }

        private static void MeasureDir(string dir, ref long bytes, ref int files, int depth)
        {
            Stack<string> stack = new Stack<string>();
            stack.Push(dir);
            int guard = 0;
            while (stack.Count > 0 && guard++ < 200000)
            {
                string cur = stack.Pop();
                try
                {
                    foreach (string f in Directory.EnumerateFiles(cur))
                    {
                        try { bytes += new FileInfo(f).Length; files++; } catch { }
                    }
                    foreach (string d in Directory.EnumerateDirectories(cur))
                    {
                        if (IsJunction(d)) continue;     // 不跟随目录联接，避免死循环
                        stack.Push(d);
                    }
                }
                catch { }
            }
        }

        /// <summary>删除选中项的缓存内容，返回释放字节数</summary>
        public static long CleanCaches(IEnumerable<CleanItem> items, Action<int, string> progress, Action<string> log)
        {
            List<CleanItem> list = new List<CleanItem>(items);
            long freed = 0;
            int idx = 0;
            foreach (CleanItem it in list)
            {
                if (!it.Selected) continue;
                if (CancelRequested)
                {
                    if (log != null) log("已停止清理（剩余项未处理）");
                    break;
                }
                idx++;
                if (progress != null) progress(0, "正在清理：" + it.Name);

                if (it.Name != null && it.Name.StartsWith("回收站"))
                {
                    string root = string.IsNullOrEmpty(it.Drive) ? null : it.Drive;
                    long freeBefore = DriveFree(root);
                    long sizeBefore = RecycleBinSize(root);
                    EmptyRecycleBin(root);
                    long sizeAfter = RecycleBinSize(root);
                    long freeAfter = DriveFree(root);

                    long claimed = Math.Max(0, sizeBefore - sizeAfter);
                    long real = Math.Max(0, freeAfter - freeBefore);
                    long d = real > 0 ? real : claimed;   // 以真实可用空间为准
                    freed += d;
                    it.Percent = 100;
                    it.Status = string.Format("已清空 {0}（可用 +{1}）", FormatSize(claimed), FormatSize(real));
                    if (log != null)
                        log(string.Format("{0}：回收站 {1} → 可用空间 {2} → {3}（实际 +{4}）",
                            it.Name, FormatSize(sizeBefore), FormatSize(freeBefore),
                            FormatSize(freeAfter), FormatSize(real)));
                    continue;
                }

                it.Percent = 5;
                long got = 0;
                int totalFiles = Math.Max(1, it.Files);
                int doneFiles = 0;
                foreach (string root in (it.Path ?? "").Split(new string[] { " ; " }, StringSplitOptions.RemoveEmptyEntries))
                {
                    try
                    {
                        got += PurgePath(root, log, delegate(int n)
                        {
                            doneFiles += n;
                            int pct = 5 + (int)(90L * doneFiles / totalFiles);
                            if (pct > 95) pct = 95;
                            it.Percent = pct;
                        });
                    }
                    catch (Exception ex) { if (log != null) log("清理异常（已跳过）：" + it.Name + " - " + ex.Message); }
                }
                freed += got;
                it.Percent = 100;
                it.Status = "已释放 " + FormatSize(got);
                if (log != null) log(string.Format("{0}：释放 {1}", it.Name, FormatSize(got)));
                if (progress != null) progress((int)(idx * 100L / Math.Max(1, list.Count)), "已清理 " + it.Name);
            }
            return freed;
        }

        private static long PurgePath(string path, Action<string> log, Action<int> onFiles)
        {
            long freed = 0;
            try
            {
                if (File.Exists(path))
                {
                    long len = 0;
                    try { len = new FileInfo(path).Length; } catch { }
                    try
                    {
                        File.SetAttributes(path, FileAttributes.Normal);
                        File.Delete(path);
                        freed += len;
                    }
                    catch (Exception ex) { if (log != null) log("跳过（占用）：" + Path.GetFileName(path)); }
                    return freed;
                }
                if (!Directory.Exists(path)) return 0;

                // 大目录（如 Edge/Chrome 的 User Data）只清缓存子目录，避免误删配置
                string low = path.ToLowerInvariant();
                if (low.EndsWith("\\user data"))
                {
                    foreach (string profile in SafeDirs(path))
                    {
                        foreach (string sub in new string[] { "Cache", "Code Cache", "GPUCache", "Service Worker", "CacheStorage" })
                        {
                            string p = Path.Combine(profile, sub);
                            if (Directory.Exists(p)) freed += DeleteTree(p, log, onFiles);
                        }
                    }
                    return freed;
                }
                if (low.EndsWith("\\tencent"))
                {
                    foreach (string sub in new string[] { "temp", "Temp", "Crash", "crash", "log", "Log" })
                    {
                        string p = Path.Combine(path, sub);
                        if (Directory.Exists(p)) freed += DeleteTree(p, log, onFiles);
                    }
                    return freed;
                }

                freed += DeleteTreeContent(path, log, onFiles);
            }
            catch (Exception ex) { if (log != null) log("清理失败：" + ex.Message); }
            return freed;
        }

        private static IEnumerable<string> SafeDirs(string dir)
        {
            try { return Directory.GetDirectories(dir); }
            catch { return new string[0]; }
        }

        private static long DeleteTree(string dir, Action<string> log, Action<int> onFiles)
        {
            long freed = DeleteTreeContent(dir, log, onFiles);
            try { Directory.Delete(dir, true); } catch { }
            return freed;
        }

        /// <summary>白名单校验：只允许删除这些根之下的内容</summary>
        private static readonly string[] AllowedRoots = new string[]
        {
            "\\temp\\", "\\windows\\temp", "\\windows\\logs", "\\windows\\panther",
            "\\windows\\prefetch", "\\softwaredistribution", "\\crashdumps",
            "\\microsoft\\windows\\explorer", "\\microsoft\\edge\\user data",
            "\\google\\chrome\\user data", "\\tencent", "\\appdata\\local\\temp"
        };

        private static bool IsAllowed(string path)
        {
            string p = (path ?? "").ToLowerInvariant();
            foreach (string r in AllowedRoots) if (p.Contains(r)) return true;
            return false;
        }

        /// <summary>删除目录内容（保留目录本身）：迭代遍历、跳过联接、单文件失败只记录不中断</summary>
        private static long DeleteTreeContent(string dir, Action<string> log, Action<int> onFiles)
        {
            if (!IsAllowed(dir))
            {
                if (log != null) log("拒绝清理（不在白名单）：" + dir);
                return 0;
            }

            long freed = 0;
            Stack<string> pending = new Stack<string>();
            List<string> dirs = new List<string>();
            pending.Push(dir);
            int guard = 0;

            while (pending.Count > 0 && guard++ < 200000)
            {
                string cur = pending.Pop();
                dirs.Add(cur);

                List<string> files = new List<string>();
                try { files.AddRange(Directory.GetFiles(cur)); } catch { }
                if (CancelRequested) return freed;
                foreach (string f in files)
                {
                    if (CancelRequested) return freed;
                    try
                    {
                        long len = new FileInfo(f).Length;
                        File.SetAttributes(f, FileAttributes.Normal);
                        File.Delete(f);
                        freed += len;
                        if (onFiles != null) onFiles(1);
                    }
                    catch { }               // 占用 / 权限不足：跳过
                }

                List<string> subs = new List<string>();
                try { subs.AddRange(Directory.GetDirectories(cur)); } catch { }
                foreach (string d in subs)
                {
                    if (IsJunction(d)) continue;
                    pending.Push(d);
                }
            }

            // 由深到浅删空目录
            for (int i = dirs.Count - 1; i >= 0; i--)
            {
                if (string.Equals(dirs[i], dir, StringComparison.OrdinalIgnoreCase)) continue;
                try { Directory.Delete(dirs[i], false); } catch { }
            }
            return freed;
        }

        // ── AppData 大文件夹 ────────────────────────────────────────────────
        public static List<CleanItem> ScanBigFolders(Action<string> log, long minBytes)
        {
            List<CleanItem> list = new List<CleanItem>();
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string roam = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

            foreach (string root in new string[] { local, roam })
            {
                foreach (string dir in SafeDirs(root))
                {
                    string name = Path.GetFileName(dir);
                    if (name.Equals("Temp", StringComparison.OrdinalIgnoreCase)) continue;
                    if (name.Equals("Packages", StringComparison.OrdinalIgnoreCase)) continue;
                    if (IsJunction(dir)) continue;
                    long bytes = 0; int files = 0;
                    MeasureDir(dir, ref bytes, ref files, 0);
                    if (bytes < minBytes) continue;

                    CleanItem it = new CleanItem();
                    it.Name = name;
                    it.Detail = Path.GetFileName(root) + "\\" + name;
                    it.Path = dir;
                    it.Bytes = bytes;
                    it.Files = files;
                    it.Kind = CleanKind.BigFolder;
                    it.Selected = false;
                    list.Add(it);
                    if (log != null) log("大文件夹 " + name + "  " + FormatSize(bytes));
                }
            }
            list.Sort(delegate(CleanItem a, CleanItem b) { return b.Bytes.CompareTo(a.Bytes); });
            return list;
        }

        public static bool IsJunction(string dir)
        {
            try
            {
                DirectoryInfo di = new DirectoryInfo(dir);
                return (di.Attributes & FileAttributes.ReparsePoint) == FileAttributes.ReparsePoint;
            }
            catch { return false; }
        }

        /// <summary>
        /// 迁移文件夹到目标根目录并创建 Junction。
        /// 返回 null 表示成功，否则返回错误信息。
        /// </summary>
        public static string Migrate(CleanItem item, string targetRoot, Action<int, string> progress, Action<string> log)
        {
            string src = item.Path;
            if (!Directory.Exists(src)) return "源目录不存在";
            if (IsJunction(src)) return "该目录已经是目录联接，无需迁移";
            if (string.IsNullOrEmpty(targetRoot)) return "请先选择目标目录";

            try
            {
                string drive = Path.GetPathRoot(Path.GetFullPath(targetRoot));
                string srcDrive = Path.GetPathRoot(Path.GetFullPath(src));
                if (string.Equals(drive, srcDrive, StringComparison.OrdinalIgnoreCase))
                    return "目标目录不能在同一磁盘";

                DriveInfo di = new DriveInfo(drive);
                if (!di.IsReady) return "目标磁盘不可用";
                if (di.AvailableFreeSpace < item.Bytes + 200L * 1048576L)
                    return "目标磁盘空间不足（需要 " + FormatSize(item.Bytes + 200L * 1048576L) + "）";

                string dst = Path.Combine(targetRoot, Path.GetFileName(src));
                if (Directory.Exists(dst) || File.Exists(dst))
                    dst = Path.Combine(targetRoot, Path.GetFileName(src) + "_" + DateTime.Now.ToString("MMddHHmm"));

                Directory.CreateDirectory(targetRoot);
                if (progress != null) progress(5, "正在复制：" + Path.GetFileName(src));

                string robolog = RunRobocopy(src + "\\", dst + "\\", "/E /MOVE /R:1 /W:1 /NFL /NDL /NJH /NJS /NP",
                    item.Bytes, 5, 85, progress);
                if (CancelRequested)
                    return "已取消（目标目录可能残留部分文件，请手动确认）";
                if (!Directory.Exists(dst))
                    return "复制失败：" + Short(robolog);

                if (progress != null) progress(90, "正在创建目录联接…");
                if (Directory.Exists(src) && Directory.GetFileSystemEntries(src).Length > 0)
                    return "源目录仍有残留文件，未创建联接";

                string linkLog = RunCmd("mklink /J \"" + src + "\" \"" + dst + "\"");
                if (!Directory.Exists(src))
                {
                    // 尝试回滚
                    RunRobocopy(dst + "\\", src + "\\", "/E /MOVE /R:1 /W:1 /NFL /NDL /NJH /NJS /NP", 0, 0, 0, null);
                    return "创建联接失败，已尝试回滚：" + Short(linkLog);
                }

                if (progress != null) progress(100, "完成");
                if (log != null) log("迁移完成：" + src + "  ->  " + dst);
                WriteRollback(src, dst);
                item.Path = src;
                item.Detail = "已迁移 -> " + dst;
                return null;
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        }

        private static string RunRobocopy(string src, string dst, string args, long totalBytes, int from, int to,
                                          Action<int, string> progress)
        {
            ProcessStartInfo psi = new ProcessStartInfo("robocopy", "\"" + src + "\" \"" + dst + "\" " + args);
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;

            StringBuilder sb = new StringBuilder();
            using (Process p = Process.Start(psi))
            {
                Thread tOut = new Thread(delegate()
                {
                    try
                    {
                        string line;
                        while ((line = p.StandardOutput.ReadLine()) != null) sb.AppendLine(line);
                    }
                    catch { }
                });
                tOut.IsBackground = true;
                tOut.Start();

                long total = totalBytes > 0 ? totalBytes : 1;
                while (!p.HasExited)
                {
                    if (CancelRequested)
                    {
                        try { p.Kill(); } catch { }
                        break;
                    }
                    long cur = DirSize(dst);
                    int pct = from + (int)((to - from) * (double)cur / total);
                    if (pct > to) pct = to;
                    if (progress != null)
                        progress(pct, "复制中 " + FormatSize(cur) + " / " + FormatSize(totalBytes));
                    Thread.Sleep(600);
                }
                try { p.WaitForExit(8000); } catch { }
            }
            if (progress != null) progress(to, "复制完成");
            return sb.ToString();
        }

        /// <summary>快速统计目录大小（用于迁移进度）</summary>
        private static long DirSize(string dir)
        {
            long total = 0;
            try
            {
                Stack<string> st = new Stack<string>();
                st.Push(dir);
                int guard = 0;
                while (st.Count > 0 && guard++ < 20000)
                {
                    string cur = st.Pop();
                    try
                    {
                        foreach (string f in Directory.EnumerateFiles(cur))
                        {
                            try { total += new FileInfo(f).Length; } catch { }
                        }
                        foreach (string d in Directory.EnumerateDirectories(cur))
                        {
                            if (IsJunction(d)) continue;
                            st.Push(d);
                        }
                    }
                    catch { }
                }
            }
            catch { }
            return total;
        }

        private static string RunCmd(string args)
        {
            ProcessStartInfo psi = new ProcessStartInfo("cmd.exe", "/c " + args);
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            using (Process p = Process.Start(psi))
            {
                string o = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
                p.WaitForExit(30000);
                return o;
            }
        }

        private static string Short(string s)
        {
            if (string.IsNullOrEmpty(s)) return "未知错误";
            s = s.Trim();
            return s.Length > 200 ? s.Substring(0, 200) : s;
        }

        private static void WriteRollback(string src, string dst)
        {
            try
            {
                string file = Path.Combine(Paths.LogDir,
                    "clean-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".json");
                string json = "{\"type\":\"junction\",\"link\":\"" + Escape(src) + "\",\"target\":\"" +
                              Escape(dst) + "\",\"time\":\"" + DateTime.Now.ToString("s") + "\"}";
                File.AppendAllText(file, json + Environment.NewLine, Encoding.UTF8);
            }
            catch { }
        }

        private static string Escape(string s)
        {
            return (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        // ── 回收站 ──────────────────────────────────────────────────────────
        [StructLayout(LayoutKind.Sequential)]
        private struct SHQUERYRBINFO
        {
            public int cbSize;
            public long i64Size;
            public long i64NumItems;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHQueryRecycleBin(string pszRootPath, ref SHQUERYRBINFO pSHQueryRBInfo);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHEmptyRecycleBin(IntPtr hwnd, string pszRootPath, uint dwFlags);

        /// <summary>指定磁盘的回收站大小；root 为 null 时是全部磁盘合计</summary>
        public static long RecycleBinSize(string root)
        {
            try
            {
                SHQUERYRBINFO info = new SHQUERYRBINFO();
                info.cbSize = Marshal.SizeOf(typeof(SHQUERYRBINFO));
                int hr = SHQueryRecycleBin(root, ref info);
                if (hr == 0) return info.i64Size;
            }
            catch { }
            return 0;
        }

        public static void EmptyRecycleBin(string root)
        {
            try { SHEmptyRecycleBin(IntPtr.Zero, root, 0x1 | 0x2 | 0x4); } catch { }
        }

        public static long DriveFree(string root)
        {
            try { return string.IsNullOrEmpty(root) ? 0 : new DriveInfo(root).AvailableFreeSpace; }
            catch { return 0; }
        }

        public static string DiskText()
        {
            try
            {
                DriveInfo c = new DriveInfo(Path.GetPathRoot(Environment.SystemDirectory));
                return string.Format("{0} 可用 {1} / 共 {2}",
                    c.Name.TrimEnd('\\'), FormatSize(c.AvailableFreeSpace), FormatSize(c.TotalSize));
            }
            catch { return ""; }
        }

        public static long DiskFree()
        {
            try
            {
                DriveInfo c = new DriveInfo(Path.GetPathRoot(Environment.SystemDirectory));
                return c.AvailableFreeSpace;
            }
            catch { return 0; }
        }

        public static long DiskTotal()
        {
            try
            {
                DriveInfo c = new DriveInfo(Path.GetPathRoot(Environment.SystemDirectory));
                return c.TotalSize;
            }
            catch { return 0; }
        }

        public static string SuggestTarget()
        {
            try
            {
                foreach (DriveInfo d in DriveInfo.GetDrives())
                {
                    if (!d.IsReady) continue;
                    if (d.DriveType != DriveType.Fixed) continue;
                    if (string.Equals(d.Name, Path.GetPathRoot(Environment.SystemDirectory), StringComparison.OrdinalIgnoreCase)) continue;
                    if (d.AvailableFreeSpace < 5L * 1073741824L) continue;
                    return Path.Combine(d.Name, "DllSmithMove");
                }
            }
            catch { }
            return "";
        }
    }
    // ── C 盘清理页 ──────────────────────────────────────────────────────────
    public class CleanPage : UserControl, IProgressHost
    {
        private readonly ObservableCollection<CleanItem> _items = new ObservableCollection<CleanItem>();
        private readonly ListBox _list = new ListBox();
        private readonly TextBlock _logLast = new TextBlock();
        private readonly Button _logToggle = new Button();
        private readonly TextBox _logBox = new TextBox();
        private Border _logCard;
        private bool _logExpanded;

        private readonly Button _btnCache = new Button();
        private readonly Button _btnFolder = new Button();
        private readonly Button _btnScan = new Button();
        private readonly Button _btnAction = new Button();
        private readonly Button _btnStop = new Button();
        private readonly TextBox _target = new TextBox();
        private readonly TextBlock _targetLabel = new TextBlock();
        private readonly TextBlock _statFree = new TextBlock();
        private readonly TextBlock _statUsed = new TextBlock();
        private readonly TextBlock _statSel = new TextBlock();

        private bool _busy;
        private long freeBeforeAction;
        private int _mode;      // 0 = 缓存清理，1 = 大文件夹迁移

        public event Action<string, int, bool> StatusChanged;

        public CleanPage()
        {
            Build();   // 不自动扫描：由「检查可用空间」按钮触发
        }

        private void Report(string text, int percent, bool busy)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(delegate { Report(text, percent, busy); }));
                return;
            }
            Action<string, int, bool> h = StatusChanged;
            if (h != null) h(text, percent, busy);

            // 页内进度条：忙碌时显示，跑完 100% 后稍后隐藏
            if (busy || percent > 0)
            {
                _barHost.Visibility = Visibility.Visible;
                _bar.IsIndeterminate = busy && percent <= 0;
                if (percent > 0) _bar.Value = percent;
                _barPct.Text = percent > 0 ? percent + "%" : "…";
                if (text != null) _barText.Text = text;
            }
            else
            {
                _bar.Value = 100;
                _barPct.Text = "100%";
                _barText.Text = text == null ? "完成" : text;
            }
        }

        private void LogLine(string line)
        {
            if (line == null) return;
            _logLast.Text = line.Trim();
            Dispatcher.BeginInvoke(new Action(delegate { _logBox.AppendText(line + Environment.NewLine); _logBox.ScrollToEnd(); }));
        }

        // ── 布局 ────────────────────────────────────────────────────────────
        private void Build()
        {
            Grid root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition());
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // 页头
            StackPanel head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(2, 0, 0, 16) };
            Border tile = new Border();
            tile.Width = 42; tile.Height = 42;
            tile.CornerRadius = new CornerRadius(12);
            tile.Background = Theme.PrimarySoft;
            TextBlock tg = new TextBlock();
            tg.Text = "\uEDA2";
            tg.FontFamily = new FontFamily("Segoe MDL2 Assets");
            tg.FontSize = 19;
            tg.Foreground = Brushes.White;
            tg.HorizontalAlignment = HorizontalAlignment.Center;
            tg.VerticalAlignment = VerticalAlignment.Center;
            tile.Child = tg;
            StackPanel ht = new StackPanel { Margin = new Thickness(14, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            TextBlock h1 = new TextBlock();
            h1.Text = "C 盘清理";
            h1.FontSize = 21;
            h1.FontWeight = FontWeights.SemiBold;
            h1.Foreground = Theme.TextStrong;
            TextBlock h2 = new TextBlock();
            h2.Text = "清理系统缓存 / 迁移大文件夹并建立目录联接，不改分区也能腾出空间。";
            h2.FontSize = 12.5;
            h2.Margin = new Thickness(0, 6, 0, 0);
            h2.Foreground = Theme.TextMuted;
            ht.Children.Add(h1);
            ht.Children.Add(h2);
            head.Children.Add(tile);
            head.Children.Add(ht);
            root.Children.Add(head);

            // 统计卡
            StackPanel cards = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 14) };
            cards.Children.Add(StatCard("C 盘可用", _statFree, Color.FromRgb(0x1E, 0xA8, 0x5A)));
            cards.Children.Add(StatCard("可清理合计", _statUsed, Color.FromRgb(0x1F, 0x6F, 0xE0)));
            cards.Children.Add(StatCard("已选择", _statSel, Color.FromRgb(0xE0, 0x8A, 0x1F)));
            Grid.SetRow(cards, 1);
            root.Children.Add(cards);

            // 中间：工具条 + 列表
            Border card = new Border();
            card.Background = Theme.Card;
            card.CornerRadius = new CornerRadius(18);
            card.BorderBrush = Theme.CardLine;
            card.BorderThickness = new Thickness(1);
            card.Padding = new Thickness(16, 14, 16, 8);

            Grid g = new Grid();
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // 工具条
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // 进度条
            g.RowDefinitions.Add(new RowDefinition());                              // 列表

            Grid bar = new Grid();
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            bar.ColumnDefinitions.Add(new ColumnDefinition());
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            StackPanel modes = new StackPanel { Orientation = Orientation.Horizontal };
            StylePill(_btnCache, "缓存清理", true);
            StylePill(_btnFolder, "大文件夹迁移", false);
            _btnCache.Margin = new Thickness(0, 0, 8, 0);
            _btnCache.Click += delegate { SwitchMode(0); };
            _btnFolder.Click += delegate { SwitchMode(1); };
            modes.Children.Add(_btnCache);
            modes.Children.Add(_btnFolder);
            bar.Children.Add(modes);

            StackPanel targetBox = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(14, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            _targetLabel.Text = "迁移到";
            _targetLabel.FontSize = 12.5;
            _targetLabel.Foreground = Theme.TextMuted;
            _targetLabel.VerticalAlignment = VerticalAlignment.Center;
            _target.Width = 230;
            _target.Height = 30;
            _target.FontSize = 12.5;
            _target.Padding = new Thickness(8, 0, 8, 0);
            _target.Text = CleanEngine.SuggestTarget();
            _target.VerticalContentAlignment = VerticalAlignment.Center;
            _target.Margin = new Thickness(8, 0, 0, 0);
            targetBox.Children.Add(_targetLabel);
            targetBox.Children.Add(_target);
            Grid.SetColumn(targetBox, 1);
            bar.Children.Add(targetBox);
            _targetBox = targetBox;

            StackPanel acts = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            StyleBtn(_btnScan, "检查可用空间", false);
            StyleBtn(_btnAction, "立即清理", true);
            _btnScan.Click += delegate { BeginScan(); };
            _btnAction.Click += delegate { DoAction(); };
            StyleBtn(_btnStop, "停止", false);
            _btnStop.Visibility = Visibility.Collapsed;
            _btnStop.Click += delegate
            {
                CleanEngine.CancelRequested = true;
                _btnStop.IsEnabled = false;
                Report("正在停止…", 0, true);
            };
            acts.Children.Add(_btnScan);
            acts.Children.Add(_btnAction);
            acts.Children.Add(_btnStop);
            Grid.SetColumn(acts, 2);
            bar.Children.Add(acts);
            g.Children.Add(bar);

            // 页内进度条（检查 / 清理 / 迁移共用）
            _barHost.Background = new SolidColorBrush(Color.FromRgb(0xF7, 0xFA, 0xFF));
            _barHost.BorderBrush = new SolidColorBrush(Color.FromRgb(0xE7, 0xEF, 0xFB));
            _barHost.BorderThickness = new Thickness(1);
            _barHost.CornerRadius = new CornerRadius(12);
            _barHost.Padding = new Thickness(14, 8, 14, 8);
            _barHost.Margin = new Thickness(0, 10, 0, 0);
            _barHost.Visibility = Visibility.Collapsed;

            Grid pg = new Grid();
            pg.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            pg.ColumnDefinitions.Add(new ColumnDefinition());
            pg.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            _barText.Text = "准备中…";
            _barText.FontSize = 12;
            _barText.Foreground = Theme.TextBody;
            _barText.VerticalAlignment = VerticalAlignment.Center;
            _barText.Margin = new Thickness(0, 0, 12, 0);
            pg.Children.Add(_barText);

            _bar.Height = 8;
            _bar.Minimum = 0;
            _bar.Maximum = 100;
            _bar.Value = 0;
            _bar.Foreground = Theme.Primary;
            _bar.Background = new SolidColorBrush(Color.FromRgb(0xE6, 0xEC, 0xF6));
            _bar.BorderThickness = new Thickness(0);
            _bar.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(_bar, 1);
            pg.Children.Add(_bar);

            _barPct.Text = "0%";
            _barPct.FontSize = 12;
            _barPct.Foreground = Theme.Primary;
            _barPct.VerticalAlignment = VerticalAlignment.Center;
            _barPct.Margin = new Thickness(12, 0, 0, 0);
            Grid.SetColumn(_barPct, 2);
            pg.Children.Add(_barPct);

            _barHost.Child = pg;
            Grid.SetRow(_barHost, 1);
            g.Children.Add(_barHost);

            _list.ItemsSource = _items;
            _list.BorderThickness = new Thickness(0);
            _list.Background = Brushes.White;
            _list.Margin = new Thickness(0, 10, 0, 0);
            _list.HorizontalContentAlignment = HorizontalAlignment.Stretch;
            _list.ItemTemplate = BuildRow();
            _list.ItemContainerStyle = BuildRowStyle();
            _list.Resources.Add(typeof(ScrollBar), BuildScrollBar());
            Grid.SetRow(_list, 2);
            g.Children.Add(_list);

            _placeholder = new TextBlock();
            _placeholder.Text = _mode == 0
                ? "尚未检查：点击右上角「检查可用空间」统计可清理项。"
                : "尚未检查：点击右上角「检查可用空间」扫描 AppData 大文件夹。";
            _placeholder.FontSize = 13;
            _placeholder.Foreground = Theme.TextMuted;
            _placeholder.HorizontalAlignment = HorizontalAlignment.Center;
            _placeholder.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetRow(_placeholder, 2);
            g.Children.Add(_placeholder);

            card.Child = g;
            Grid.SetRow(card, 2);
            root.Children.Add(card);

            // 日志（默认一行）
            Border log = new Border();
            log.Margin = new Thickness(0, 14, 0, 0);
            log.CornerRadius = new CornerRadius(16);
            log.Background = new SolidColorBrush(Color.FromRgb(0x0F, 0x1B, 0x2D));
            log.Padding = new Thickness(16, 10, 16, 10);
            log.Height = 46;
            _logCard = log;

            Grid lg = new Grid();
            lg.RowDefinitions.Add(new RowDefinition { Height = new GridLength(24) });
            lg.RowDefinitions.Add(new RowDefinition());

            Grid lh = new Grid();
            lh.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            lh.ColumnDefinitions.Add(new ColumnDefinition());
            lh.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            StackPanel lhl = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            TextBlock lgi = new TextBlock();
            lgi.Text = "\uE756";
            lgi.FontFamily = new FontFamily("Segoe MDL2 Assets");
            lgi.FontSize = 12;
            lgi.Foreground = new SolidColorBrush(Color.FromRgb(0x8F, 0xB4, 0xE8));
            lgi.Margin = new Thickness(0, 0, 8, 0);
            TextBlock lgt = new TextBlock();
            lgt.Text = "执行日志";
            lgt.FontSize = 12.5;
            lgt.Foreground = new SolidColorBrush(Color.FromRgb(0xC9, 0xD8, 0xEA));
            lhl.Children.Add(lgi);
            lhl.Children.Add(lgt);
            lh.Children.Add(lhl);

            _logLast.Text = "";
            _logLast.FontFamily = new FontFamily("Consolas");
            _logLast.FontSize = 11.5;
            _logLast.Foreground = new SolidColorBrush(Color.FromRgb(0x9F, 0xB3, 0xCB));
            _logLast.Margin = new Thickness(14, 0, 8, 0);
            _logLast.VerticalAlignment = VerticalAlignment.Center;
            _logLast.TextTrimming = TextTrimming.CharacterEllipsis;
            Grid.SetColumn(_logLast, 1);
            lh.Children.Add(_logLast);

            _logToggle.Content = "展开";
            _logToggle.FontSize = 11.5;
            _logToggle.Foreground = new SolidColorBrush(Color.FromRgb(0xB9, 0xC8, 0xDA));
            _logToggle.Background = new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF));
            _logToggle.BorderBrush = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));
            _logToggle.BorderThickness = new Thickness(1);
            _logToggle.Padding = new Thickness(12, 3, 12, 3);
            _logToggle.Cursor = System.Windows.Input.Cursors.Hand;
            _logToggle.Click += delegate
            {
                _logExpanded = !_logExpanded;
                _logCard.Height = _logExpanded ? 168 : 46;
                _logBox.Visibility = _logExpanded ? Visibility.Visible : Visibility.Collapsed;
                _logLast.Visibility = _logExpanded ? Visibility.Collapsed : Visibility.Visible;
                _logToggle.Content = _logExpanded ? "收起" : "展开";
            };
            Grid.SetColumn(_logToggle, 2);
            lh.Children.Add(_logToggle);
            lg.Children.Add(lh);

            _logBox.Background = Brushes.Transparent;
            _logBox.Foreground = new SolidColorBrush(Color.FromRgb(0xB7, 0xC6, 0xDA));
            _logBox.BorderThickness = new Thickness(0);
            _logBox.FontFamily = new FontFamily("Consolas");
            _logBox.FontSize = 11.5;
            _logBox.IsReadOnly = true;
            _logBox.Margin = new Thickness(0, 6, 0, 0);
            _logBox.Visibility = Visibility.Collapsed;
            _logBox.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            Grid.SetRow(_logBox, 1);
            lg.Children.Add(_logBox);

            log.Child = lg;
            Grid.SetRow(log, 3);
            root.Children.Add(log);

            Content = root;
            SwitchMode(0);
        }

        private StackPanel _targetBox;
        private readonly Border _barHost = new Border();
        private readonly ProgressBar _bar = new ProgressBar();
        private readonly TextBlock _barText = new TextBlock();
        private readonly TextBlock _barPct = new TextBlock();
        private TextBlock _placeholder;

        private Border StatCard(string title, TextBlock value, Color accent)
        {
            Border card = new Border();
            card.Width = 194;
            card.Height = 82;
            card.Margin = new Thickness(0, 0, 12, 0);
            card.Background = Theme.Card;
            card.CornerRadius = new CornerRadius(18);
            card.BorderBrush = Theme.CardLine;
            card.BorderThickness = new Thickness(1);
            card.Padding = new Thickness(18, 12, 18, 12);

            StackPanel sp = new StackPanel();
            TextBlock t = new TextBlock();
            t.Text = title;
            t.FontSize = 12;
            t.Foreground = Theme.TextMuted;
            value.Text = "—";
            value.FontSize = 20;
            value.FontWeight = FontWeights.SemiBold;
            value.Foreground = new SolidColorBrush(accent);
            value.Margin = new Thickness(0, 6, 0, 0);
            sp.Children.Add(t);
            sp.Children.Add(value);
            card.Child = sp;
            return card;
        }

        private void StylePill(Button b, string text, bool active)
        {
            b.Content = text;
            b.Height = 30;
            b.Padding = new Thickness(16, 0, 16, 0);
            b.FontSize = 12.5;
            b.Cursor = System.Windows.Input.Cursors.Hand;
            b.BorderThickness = new Thickness(1);
            b.Foreground = active ? Brushes.White : Theme.TextBody;
            b.Background = active ? Theme.PrimarySoft : Brushes.White;
            b.BorderBrush = active ? Theme.PrimarySoft : Theme.CardLine;
        }

        private void StyleBtn(Button b, string text, bool primary)
        {
            b.Content = text;
            b.Height = 34;
            b.MinWidth = 104;
            b.Margin = new Thickness(8, 0, 0, 0);
            b.Padding = new Thickness(14, 0, 14, 0);
            b.FontSize = 12.5;
            b.FontWeight = FontWeights.SemiBold;
            b.Cursor = System.Windows.Input.Cursors.Hand;
            b.BorderThickness = new Thickness(primary ? 0 : 1);
            b.Foreground = primary ? Brushes.White : Theme.Primary;
            b.Background = primary ? Theme.PrimarySoft : Brushes.White;
            b.BorderBrush = Theme.Primary;
        }

        private DataTemplate BuildRow()
        {
            string xaml = @"
<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'
              xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'>
  <Grid Height='48' Background='Transparent'>
    <Grid.ColumnDefinitions>
      <ColumnDefinition Width='48'/><ColumnDefinition Width='*'/><ColumnDefinition Width='120'/>
      <ColumnDefinition Width='110'/><ColumnDefinition Width='120'/><ColumnDefinition Width='104'/>
    </Grid.ColumnDefinitions>
    <CheckBox IsChecked='{Binding Selected, Mode=TwoWay}' HorizontalAlignment='Center'
              VerticalAlignment='Center'/>
    <StackPanel Grid.Column='1' VerticalAlignment='Center'>
      <TextBlock Text='{Binding Name}' FontSize='13' Foreground='#22344F'/>
      <TextBlock Text='{Binding CountText}' FontSize='11' Foreground='#8B9BB0' Margin='0,3,0,0'/>
    </StackPanel>
    <TextBlock Grid.Column='2' Text='{Binding SizeText}' FontSize='12.5'
               Foreground='#1F6FE0' VerticalAlignment='Center'/>
    <TextBlock Grid.Column='3' Text='{Binding Detail}' FontSize='11.5'
               Foreground='#8B9BB0' VerticalAlignment='Center' TextTrimming='CharacterEllipsis'/>
    <TextBlock Grid.Column='4' Text='{Binding Status}' FontSize='11.5'
               Foreground='#1E9A5A' VerticalAlignment='Center'/>
    <StackPanel Grid.Column='5' Orientation='Horizontal' VerticalAlignment='Center'
                Visibility='{Binding HasPercent}'>
      <ProgressBar Width='54' Height='5' Minimum='0' Maximum='100' Value='{Binding Percent}'
                   Foreground='#2A6CD8' Background='#E6ECF6' BorderThickness='0'/>
      <TextBlock Text='{Binding PercentText}' FontSize='11' Foreground='#5A6B80' Margin='7,0,0,0'/>
    </StackPanel>
  </Grid>
</DataTemplate>";
            return (DataTemplate)XamlReader.Parse(xaml);
        }

        private Style BuildRowStyle()
        {
            Style s = new Style(typeof(ListBoxItem));
            s.Setters.Add(new Setter(ListBoxItem.PaddingProperty, new Thickness(0)));
            s.Setters.Add(new Setter(ListBoxItem.MarginProperty, new Thickness(0)));
            s.Setters.Add(new Setter(ListBoxItem.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
            s.Setters.Add(new Setter(ListBoxItem.BackgroundProperty, Brushes.White));
            return s;
        }

        private Style BuildScrollBar()
        {
            string xaml =
                "<Style xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' TargetType='ScrollBar'>" +
                "<Setter Property='Width' Value='9'/><Setter Property='Background' Value='Transparent'/>" +
                "<Setter Property='Template'><Setter.Value>" +
                "<ControlTemplate TargetType='ScrollBar'><Grid Background='Transparent'>" +
                "<Track x:Name='PART_Track' IsDirectionReversed='true'><Track.Thumb><Thumb>" +
                "<Thumb.Template><ControlTemplate TargetType='Thumb'>" +
                "<Border CornerRadius='5' Background='#C9D6E8' Margin='1,0'/></ControlTemplate></Thumb.Template>" +
                "</Thumb></Track.Thumb></Track></Grid></ControlTemplate></Setter.Value></Setter></Style>";
            xaml = xaml.Replace("<Track x:Name='PART_Track'", "<Track");
            return (Style)XamlReader.Parse(xaml);
        }

        // ── 模式切换 ────────────────────────────────────────────────────────
        private void SwitchMode(int mode)
        {
            _mode = mode;
            StylePill(_btnCache, "缓存清理", mode == 0);
            StylePill(_btnFolder, "大文件夹迁移", mode == 1);
            _targetBox.Visibility = mode == 1 ? Visibility.Visible : Visibility.Collapsed;
            _btnAction.Content = mode == 0 ? "立即清理" : "迁移选中";
            BeginScan();
        }

        // ── 扫描 ────────────────────────────────────────────────────────────
        private void BeginScan()
        {
            if (_busy) return;
            EnterBusy(true);
            _items.Clear();
            Report("正在扫描…", 0, true);
            int mode = _mode;

            Thread t = new Thread(delegate()
            {
                List<CleanItem> found = mode == 0
                    ? CleanEngine.ScanCaches(delegate(string s) { Dispatcher.BeginInvoke(new Action(delegate { LogLine(s); })); })
                    : CleanEngine.ScanBigFolders(delegate(string s) { Dispatcher.BeginInvoke(new Action(delegate { LogLine(s); })); }, 100L * 1048576L);

                Dispatcher.BeginInvoke(new Action(delegate
                {
                    foreach (CleanItem it in found) _items.Add(it);
                    UpdateStats();
                    EnterBusy(false);
                    if (_placeholder != null) _placeholder.Visibility = found.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                    Report(string.Format("扫描完成：{0} 项，共 {1}", found.Count,
                        CleanEngine.FormatSize(found.Sum(delegate(CleanItem x) { return x.Bytes; }))), 0, false);
                }));
            });
            t.IsBackground = true;
            t.Start();
        }

        private void UpdateStats()
        {
            long total = 0, sel = 0;
            foreach (CleanItem it in _items)
            {
                total += it.Bytes;
                if (it.Selected) sel += it.Bytes;
            }
            _statFree.Text = CleanEngine.FormatSize(CleanEngine.DiskFree());
            _statUsed.Text = CleanEngine.FormatSize(total);
            _statSel.Text = CleanEngine.FormatSize(sel);
        }

        private void EnterBusy(bool on)
        {
            _busy = on;
            _btnScan.IsEnabled = !on;
            _btnAction.IsEnabled = !on;
            _btnCache.IsEnabled = !on;
            _btnFolder.IsEnabled = !on;
            if (_btnStop != null)
            {
                _btnStop.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
                _btnStop.IsEnabled = on;
            }
        }

        // ── 执行 ────────────────────────────────────────────────────────────
        private void DoAction()
        {
            if (_busy) return;
            List<CleanItem> picked = _items.Where(delegate(CleanItem i) { return i.Selected; }).ToList();
            if (picked.Count == 0)
            {
                MessageBox.Show("请先勾选要处理的项目。", "提示");
                return;
            }

            if (_mode == 0)
            {
                long bytes = picked.Sum(delegate(CleanItem x) { return x.Bytes; });
                MessageBoxResult r = MessageBox.Show(
                    string.Format("将清理 {0} 项缓存，预计释放 {1}。\n\n清理只涉及白名单目录（临时文件、更新缓存、日志、回收站等），被占用的文件会自动跳过。\n\n继续？",
                        picked.Count, CleanEngine.FormatSize(bytes)),
                    "确认清理", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (r != MessageBoxResult.Yes) return;

                CleanEngine.CancelRequested = false;
                freeBeforeAction = CleanEngine.DiskFree();
                EnterBusy(true);
                Report("正在清理…", 0, true);
                Thread t = new Thread(delegate()
                {
                    long freed = 0;
                    string err = null;
                    try
                    {
                        freed = CleanEngine.CleanCaches(picked,
                            delegate(int pct, string msg) { Report(msg, pct, true); },
                            delegate(string s) { Dispatcher.BeginInvoke(new Action(delegate { LogLine(s); })); });
                    }
                    catch (Exception ex)
                    {
                        err = ex.ToString();
                        Log.Error("clean crashed", ex);
                    }
                    long freedFinal = freed;
                    string errFinal = err;
                    Dispatcher.BeginInvoke(new Action(delegate
                    {
                        EnterBusy(false);
                        UpdateStats();
                        if (errFinal != null)
                        {
                            Report("清理中断：" + errFinal.Split('\n')[0], 0, false);
                            MessageBox.Show("清理过程中出现异常，已停止：\n" + errFinal, "清理失败");
                        }
                        else
                        {
                            long freeNow = CleanEngine.DiskFree();
                            long delta = freeNow - freeBeforeAction;
                            Report("清理完成，释放 " + CleanEngine.FormatSize(freedFinal), 0, false);
                            MessageBox.Show(string.Format("清理完成。\n\n统计释放：{0}\nC 盘可用：{1} → {2}（实际 +{3}）\n\n详情见日志面板。",
                                CleanEngine.FormatSize(freedFinal),
                                CleanEngine.FormatSize(freeBeforeAction),
                                CleanEngine.FormatSize(freeNow),
                                CleanEngine.FormatSize(delta < 0 ? 0 : delta)), "完成");
                            // 保留列表：每行保留「完成」与已释放大小，便于核对
                        }
                    }));
                });
                t.IsBackground = true;
                t.Start();
                return;
            }

            // 迁移
            string target = (_target.Text ?? "").Trim();
            if (target.Length == 0)
            {
                MessageBox.Show("请先填写目标目录（例如 D:\\DllSmithMove）。", "提示");
                return;
            }
            long total = picked.Sum(delegate(CleanItem x) { return x.Bytes; });
            MessageBoxResult r2 = MessageBox.Show(
                string.Format("将把 {0} 个文件夹（共 {1}）迁移到：\n{2}\n\n迁移完成后会在原位置创建目录联接（Junction），软件照常使用。\n\n继续？",
                    picked.Count, CleanEngine.FormatSize(total), target),
                "确认迁移", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (r2 != MessageBoxResult.Yes) return;

            CleanEngine.CancelRequested = false;
            EnterBusy(true);
            Report("正在迁移…", 0, true);
            Thread t2 = new Thread(delegate()
            {
                int ok = 0, fail = 0;
                foreach (CleanItem it in picked)
                {
                    Report("正在迁移：" + it.Name, 10, true);
                    string err = CleanEngine.Migrate(it, target,
                        delegate(int pct, string msg) { Report(msg, pct, true); },
                        delegate(string s) { Dispatcher.BeginInvoke(new Action(delegate { LogLine(s); })); });
                    if (err == null) { ok++; it.Status = "已迁移"; }
                    else { fail++; it.Status = "失败：" + err; Dispatcher.BeginInvoke(new Action(delegate { LogLine("迁移失败 " + it.Name + "：" + err); })); }
                }
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    EnterBusy(false);
                    UpdateStats();
                    Report(string.Format("迁移结束：成功 {0}，失败 {1}", ok, fail), 0, false);
                    MessageBox.Show(string.Format("迁移结束。\n\n成功 {0} 个，失败 {1} 个。\n明细见日志，回滚清单在 Logs\\ 目录。", ok, fail), "完成");
                    BeginScan();
                }));
            });
            t2.IsBackground = true;
            t2.Start();
        }
    }
}