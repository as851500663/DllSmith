// ============================================================================
//  DllSmith 1.4 — 覆盖回滚（安全审计 A-04：.rebuild.bak 只写不还）
//  文件：Rollback.cs
//  内容：BackupStore（记录 / 扫描 / 还原 *.rebuild.bak）+ 回滚对话框
//  要点：每次覆盖系统文件前把原文件另存为 <target>.rebuild.bak 并登记到
//        Logs\rebuild-backups.json；界面与 `--rollback` 都能一键还原。
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace DllFix.Core
{
    public class BackupEntry
    {
        public string Target = "";
        public string Backup = "";
        public long Bytes;
        public string Sha256 = "";
        public DateTime Time = DateTime.MinValue;

        public string SizeText
        {
            get { return CleanSize(Bytes); }
        }

        public string TimeText
        {
            get { return Time == DateTime.MinValue ? "—" : Time.ToString("yyyy-MM-dd HH:mm"); }
        }

        private static string CleanSize(long b)
        {
            if (b >= 1048576L) return string.Format("{0:0.0} MB", b / 1048576.0);
            if (b >= 1024L) return string.Format("{0:0} KB", b / 1024.0);
            return b + " B";
        }
    }

    public static class BackupStore
    {
        public const string Suffix = ".rebuild.bak";

        private static string IndexFile
        {
            get { return Path.Combine(Paths.LogDir, "rebuild-backups.json"); }
        }

        /// <summary>覆盖前留档并登记（失败不阻断修复，只记录）</summary>
        public static string Create(string target)
        {
            if (string.IsNullOrEmpty(target) || !File.Exists(target)) return null;
            string backup = target + Suffix;
            try
            {
                if (!File.Exists(backup))
                {
                    File.Copy(target, backup, true);
                    Log.Info("backup created: " + backup);
                }
                Record(target, backup);
                return backup;
            }
            catch (Exception ex)
            {
                Log.Error("backup failed: " + target, ex);
                return null;
            }
        }

        private static void Record(string target, string backup)
        {
            try
            {
                long size = 0;
                try { size = new FileInfo(backup).Length; } catch { }
                string hash = "";
                try { hash = Hashes.Sha256HexOfFile(backup); } catch { }

                List<BackupEntry> list = ReadIndex();
                for (int i = list.Count - 1; i >= 0; i--)
                    if (string.Equals(list[i].Target, target, StringComparison.OrdinalIgnoreCase))
                        list.RemoveAt(i);
                BackupEntry e = new BackupEntry();
                e.Target = target;
                e.Backup = backup;
                e.Bytes = size;
                e.Sha256 = hash;
                e.Time = DateTime.Now;
                list.Insert(0, e);

                StringBuilder sb = new StringBuilder();
                sb.AppendLine("[");
                for (int i = 0; i < list.Count && i < 200; i++)
                {
                    BackupEntry x = list[i];
                    sb.Append("  {\"target\":\"").Append(Esc(x.Target))
                      .Append("\",\"backup\":\"").Append(Esc(x.Backup))
                      .Append("\",\"bytes\":").Append(x.Bytes)
                      .Append(",\"sha256\":\"").Append(x.Sha256)
                      .Append("\",\"time\":\"").Append(x.Time.ToString("s")).Append("\"}")
                      .AppendLine(i + 1 < list.Count && i + 1 < 200 ? "," : "");
                }
                sb.AppendLine("]");
                Paths.EnsureDir(Paths.LogDir);
                File.WriteAllText(IndexFile, sb.ToString(), Encoding.UTF8);
            }
            catch (Exception ex) { Log.Error("backup index write failed", ex); }
        }

        private static string Esc(string s)
        {
            return (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        private static List<BackupEntry> ReadIndex()
        {
            List<BackupEntry> list = new List<BackupEntry>();
            try
            {
                if (!File.Exists(IndexFile)) return list;
                object parsed = Json.Parse(File.ReadAllText(IndexFile, Encoding.UTF8));
                object[] arr = parsed as object[];
                if (arr == null) return list;
                foreach (object o in arr)
                {
                    Dictionary<string, object> d = o as Dictionary<string, object>;
                    if (d == null) continue;
                    BackupEntry e = new BackupEntry();
                    e.Target = Json.GetString(d, "target");
                    e.Backup = Json.GetString(d, "backup");
                    e.Sha256 = Json.GetString(d, "sha256");
                    object bytes;
                    if (d.TryGetValue("bytes", out bytes) && bytes != null)
                    {
                        try { e.Bytes = Convert.ToInt64(bytes); } catch { }
                    }
                    DateTime t;
                    if (DateTime.TryParse(Json.GetString(d, "time"), out t)) e.Time = t;
                    if (!string.IsNullOrEmpty(e.Target)) list.Add(e);
                }
            }
            catch (Exception ex) { Log.Error("backup index read failed", ex); }
            return list;
        }

        /// <summary>可还原项 = 索引里有、且 .bak 仍然存在的</summary>
        public static List<BackupEntry> Scan()
        {
            List<BackupEntry> idx = ReadIndex();
            List<BackupEntry> ok = new List<BackupEntry>();
            foreach (BackupEntry e in idx)
            {
                try
                {
                    if (!File.Exists(e.Backup)) continue;
                    e.Bytes = new FileInfo(e.Backup).Length;
                    ok.Add(e);
                }
                catch { }
            }
            // 兜底：索引之外、系统目录里遗留的 .rebuild.bak（旧版本留下的）
            foreach (string dir in LegacyBackupDirs())
            {
                try
                {
                    if (!Directory.Exists(dir)) continue;
                    foreach (string bak in Directory.GetFiles(dir, "*" + Suffix))
                    {
                        string target = bak.Substring(0, bak.Length - Suffix.Length);
                        bool dup = false;
                        foreach (BackupEntry e in ok)
                            if (string.Equals(e.Backup, bak, StringComparison.OrdinalIgnoreCase)) { dup = true; break; }
                        if (dup) continue;
                        BackupEntry e2 = new BackupEntry();
                        e2.Target = target;
                        e2.Backup = bak;
                        e2.Bytes = new FileInfo(bak).Length;
                        e2.Time = File.GetLastWriteTime(bak);
                        ok.Add(e2);
                    }
                }
                catch { }
            }
            return ok;
        }

        private static IEnumerable<string> LegacyBackupDirs()
        {
            string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            List<string> dirs = new List<string>();
            dirs.Add(Path.Combine(win, "System32"));
            dirs.Add(Path.Combine(win, "SysWOW64"));
            return dirs;
        }

        /// <summary>还原：把 .bak 覆盖回原位置。返回 null 表示成功。</summary>
        public static string Restore(BackupEntry e)
        {
            if (e == null) return "没有选择条目";
            if (!File.Exists(e.Backup)) return "备份文件不存在：" + e.Backup;
            if (!SystemProbe.IsElevated) return "还原系统文件需要管理员权限";
            try
            {
                string dir = Path.GetDirectoryName(e.Target);
                if (!string.IsNullOrEmpty(dir)) Paths.EnsureDir(dir);
                if (File.Exists(e.Target))
                {
                    try { File.SetAttributes(e.Target, FileAttributes.Normal); } catch { }
                }
                File.Copy(e.Backup, e.Target, true);
                Log.Info("rolled back: " + e.Target + " <- " + e.Backup);
                return null;
            }
            catch (Exception ex)
            {
                Log.Error("rollback failed: " + e.Target, ex);
                return ex.Message;
            }
        }
    }
}

namespace DllFix.Ui
{
    using DllFix.Core;

    /// <summary>回滚对话框：列出 .rebuild.bak，可单条还原</summary>
    public static class RollbackDialog
    {
        public static void Show(Window owner)
        {
            List<BackupEntry> list = BackupStore.Scan();
            if (list.Count == 0)
            {
                Dlg.Info("没有可还原的备份",
                    "本机没有找到 .rebuild.bak 备份文件。\n\n覆盖系统文件时会自动留档，路径写进 Logs\\rebuild-backups.json。");
                return;
            }
            if (!SystemProbe.IsElevated)
            {
                Dlg.Error("需要管理员权限", "还原系统目录文件需要以管理员身份运行本程序。");
                return;
            }

            Window w = new Window();
            w.Title = "还原被替换的系统文件";
            w.Width = 760;
            w.Height = 460;
            w.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            w.Background = new SolidColorBrush(Color.FromRgb(0xF3, 0xF7, 0xFE));
            if (owner != null) w.Owner = owner;

            Grid root = new Grid();
            root.Margin = new Thickness(16);
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition());
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            TextBlock tip = new TextBlock();
            tip.Text = "以下 " + list.Count + " 个文件曾被本工具替换，原始版本仍保留在 .rebuild.bak 中。\n选择条目后点「还原」，文件会回到原版本。";
            tip.TextWrapping = TextWrapping.Wrap;
            tip.Foreground = new SolidColorBrush(Color.FromRgb(0x33, 0x46, 0x5C));
            tip.Margin = new Thickness(0, 0, 0, 12);
            root.Children.Add(tip);

            ListBox lb = new ListBox();
            lb.ItemsSource = list;
            lb.BorderThickness = new Thickness(1);
            lb.Background = Brushes.White;
            string tpl =
                "<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'>" +
                "  <Grid Height='44'>" +
                "    <Grid.ColumnDefinitions><ColumnDefinition Width='*'/><ColumnDefinition Width='110'/><ColumnDefinition Width='140'/></Grid.ColumnDefinitions>" +
                "    <TextBlock Text='{Binding Target}' FontSize='12' Foreground='#22344F' VerticalAlignment='Center' TextTrimming='CharacterEllipsis'/>" +
                "    <TextBlock Grid.Column='1' Text='{Binding SizeText}' FontSize='12' Foreground='#8B9BB0' VerticalAlignment='Center'/>" +
                "    <TextBlock Grid.Column='2' Text='{Binding TimeText}' FontSize='12' Foreground='#8B9BB0' VerticalAlignment='Center'/>" +
                "  </Grid>" +
                "</DataTemplate>";
            lb.ItemTemplate = (DataTemplate)System.Windows.Markup.XamlReader.Parse(tpl);
            Grid.SetRow(lb, 1);
            root.Children.Add(lb);

            StackPanel btns = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
            Button undo = new Button();
            undo.Content = "还原选中";
            undo.Width = 108;
            undo.Height = 34;
            undo.Margin = new Thickness(0, 0, 8, 0);
            Button close = new Button();
            close.Content = "关闭";
            close.Width = 88;
            close.Height = 34;
            close.Click += delegate { w.Close(); };
            undo.Click += delegate
            {
                BackupEntry e = lb.SelectedItem as BackupEntry;
                if (e == null) { Dlg.Info("提示", "请先在上方列表里选择一个条目。"); return; }
                if (!Dlg.Confirm("确认还原", "将用备份覆盖：\n" + e.Target + "\n\n继续？")) return;
                string err = BackupStore.Restore(e);
                if (err == null)
                {
                    Dlg.Info("还原完成", "已还原：\n" + e.Target);
                    w.Close();
                    RollbackDialog.Show(owner);
                }
                else Dlg.Error("还原失败", err);
            };
            btns.Children.Add(undo);
            btns.Children.Add(close);
            Grid.SetRow(btns, 2);
            root.Children.Add(btns);

            w.Content = root;
            w.ShowDialog();
        }
    }
}
