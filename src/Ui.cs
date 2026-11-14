// ============================================================================
//  4DDiG DLL Fixer — 重建工程（开源版 UI）
//  文件：Ui.cs
//  设计要点：去掉专业版/管理员/模拟模式等商业元素；表格改为
//            组件 / 架构 / 版本 / 状态 四列；加大留白与层级，主视觉更开阔。
//  约束：C# 5（csc 4.8），纯代码建 UI
// ============================================================================

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Text;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Data;
using DllFix.Core;

namespace DllFix.Ui
{
    /// <summary>扫描结果复用：总览里检查过的分类，详情页直接带数据显示，不需要再扫一遍</summary>
    public static class ScanStore
    {
        private static readonly Dictionary<int, List<CatalogItem>> Map = new Dictionary<int, List<CatalogItem>>();

        public static void Put(int page, List<CatalogItem> items)
        {
            if (page < 0 || items == null) return;
            Map[page] = new List<CatalogItem>(items);
        }

        public static List<CatalogItem> Get(int page)
        {
            List<CatalogItem> v;
            return Map.TryGetValue(page, out v) ? v : null;
        }

        public static void Drop(int page) { Map.Remove(page); }
    }

    /// <summary>页面进度上报接口（ScanPage / CleanPage 共用底部状态栏）</summary>
    public interface IProgressHost
    {
        event Action<string, int, bool> StatusChanged;
    }

    internal static class Theme
    {
        public static readonly Brush PageBg = Grad(Color.FromRgb(0xF3, 0xF7, 0xFE), Color.FromRgb(0xE9, 0xF1, 0xFC));
        public static readonly Brush Card = Brushes.White;
        public static readonly Brush CardLine = new SolidColorBrush(Color.FromRgb(0xE2, 0xEA, 0xF7));
        public static readonly Brush Primary = new SolidColorBrush(Color.FromRgb(0x1F, 0x6F, 0xE0));
        public static readonly Brush PrimarySoft = Grad(Color.FromRgb(0x3B, 0x8A, 0xF7), Color.FromRgb(0x1B, 0x62, 0xD6));
        public static readonly Brush TextStrong = new SolidColorBrush(Color.FromRgb(0x10, 0x22, 0x3A));
        public static readonly Brush TextBody = new SolidColorBrush(Color.FromRgb(0x33, 0x46, 0x5C));
        public static readonly Brush TextMuted = new SolidColorBrush(Color.FromRgb(0x6E, 0x80, 0x98));
        public static readonly Brush Ok = new SolidColorBrush(Color.FromRgb(0x18, 0x96, 0x52));
        public static readonly Brush OkBg = new SolidColorBrush(Color.FromRgb(0xE9, 0xF8, 0xF0));
        public static readonly Brush Warn = new SolidColorBrush(Color.FromRgb(0xD1, 0x3B, 0x3B));
        public static readonly Brush WarnBg = new SolidColorBrush(Color.FromRgb(0xFD, 0xEC, 0xEC));
        public static readonly Brush ArchBg = new SolidColorBrush(Color.FromRgb(0xEC, 0xF3, 0xFF));
        public static readonly Brush ArchFg = new SolidColorBrush(Color.FromRgb(0x2A, 0x6C, 0xD8));

        static Theme()
        {
            // 冻结静态画刷：不冻结时每次绘制都可能复制，且无法走硬件缓存
            Freeze(PageBg); Freeze(CardLine); Freeze(Primary); Freeze(PrimarySoft);
            Freeze(TextStrong); Freeze(TextBody); Freeze(TextMuted);
            Freeze(Ok); Freeze(OkBg); Freeze(Warn); Freeze(WarnBg);
            Freeze(ArchBg); Freeze(ArchFg);
        }

        private static void Freeze(Brush b)
        {
            try { if (b != null && b.CanFreeze) b.Freeze(); } catch { }
        }

        public static Brush Grad(Color a, Color b)
        {
            LinearGradientBrush g = new LinearGradientBrush();
            g.StartPoint = new System.Windows.Point(0, 0);
            g.EndPoint = new System.Windows.Point(1, 1);
            g.GradientStops.Add(new GradientStop(a, 0));
            g.GradientStops.Add(new GradientStop(b, 1));
            return g;
        }
    }

    public class MainWindow : Window
    {
        private readonly ContentControl _host = new ContentControl();
        private readonly TextBlock _status = new TextBlock();
        private readonly TextBlock _percent = new TextBlock();     // 下载百分比（状态栏内）
        private readonly Canvas _spin = new Canvas();              // 下载小圆圈
        private readonly RotateTransform _spinRot = new RotateTransform(0);
        private readonly ProgressBar _bar = new ProgressBar();
        private readonly List<Button> _navItems = new List<Button>();
        private readonly List<TextBlock> _navLabels = new List<TextBlock>();

        public MainWindow()
        {
            Title = AppInfo.ProductName + " · " + AppInfo.ChineseName + "  v" + AppInfo.Version;   // 标题保持品牌名 + 版本
            Width = 1220;
            Height = 820;
            MinWidth = 1080;
            MinHeight = 700;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Background = Theme.PageBg;
            // 主窗不使用 AllowsTransparency（保持硬件合成路径）
            AllowsTransparency = false;
            FontFamily = new FontFamily("Microsoft YaHei UI");
            FontSize = 13;
            UseLayoutRounding = true;

            BuildLayout();
            ApplyIcon();
            if (!Diag.Has("nopage")) ShowPage(0);
            CheckUpdateAsync();
            StartHeartbeat();
        }

        // ── 窗口/任务栏图标（取自 exe 的 win32 图标资源） ───────────────────
        private void ApplyIcon()
        {
            try
            {
                string exe = System.Reflection.Assembly.GetEntryAssembly().Location;
                using (System.Drawing.Icon ico = System.Drawing.Icon.ExtractAssociatedIcon(exe))
                {
                    if (ico != null)
                    {
                        Icon = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
                            ico.Handle, new Int32Rect(0, 0, ico.Width, ico.Height),
                            System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
                    }
                }
            }
            catch (Exception ex) { Log.Error("icon load failed", ex); }
        }

        // ── 在线心跳（每 60s 上报一次，后台据此统计在线人数） ───────────────
        private void StartHeartbeat()
        {
            Thread t = new Thread(delegate()
            {
                while (true)
                {
                    try { ApiClient.Ping(); } catch { }
                    Thread.Sleep(60000);
                }
            });
            t.IsBackground = true;
            t.Start();
        }

        // ── 启动时检查更新（目录服务 /api/v1/update） ────────────────────────
        private void CheckUpdateAsync()
        {
            Loaded += delegate
            {
                Thread t = new Thread(delegate()
                {
                    UpdateInfo info = UpdateService.Check(AppInfo.Version);
                    if (info == null) return;
                    Dispatcher.BeginInvoke(new Action(delegate { PromptUpdate(info); }));
                });
                t.IsBackground = true;
                t.Start();
            };
        }

        /// <summary>
        /// 更新提示（发布门禁）：
        ///   ① 记录未通过验签/HTTPS 校验 -> 只告知，不下载不安装；
        ///   ② 非强制 -> 用户点「取消」什么都不做（原实现丢弃 Dlg.Confirm 返回值，取消也装）；
        ///   ③ 强制 -> 只给「立即更新 / 退出程序」两个出口，不静默安装。
        /// </summary>
        private void PromptUpdate(UpdateInfo info)
        {
            if (info == null) return;

            if (!info.Trusted)
            {
                Log.Error("update rejected: " + info.RejectReason, null);
                Dlg.Error("更新记录未通过校验",
                    info.RejectReason + "\n\n为保证安全，本次更新已中止：不会下载、不会运行任何文件。\n详情见 Logs\\ 目录。");
                return;
            }

            string notes = string.IsNullOrEmpty(info.Notes) ? "（无更新说明）" : info.Notes;
            string body = "检测到新版本 " + info.Version + "（当前 v" + AppInfo.Version + "）。\n\n" + notes;

            if (info.Mandatory)
            {
                bool go = Dlg.Choose("必须更新",
                    body + "\n\n当前版本已停止支持，需更新后继续使用。",
                    "立即更新", "退出程序");
                if (!go)
                {
                    Log.Info("mandatory update declined -> exit");
                    Application.Current.Shutdown();
                    return;
                }
            }
            else
            {
                bool go = Dlg.Confirm("发现新版本", body + "\n\n现在更新吗？");
                if (!go)
                {
                    Log.Info("optional update cancelled by user");
                    return;                       // 取消 = 不下载、不安装、不退出
                }
            }

            RunUpdate(info);
        }

        /// <summary>下载（已验签 + 已过 https 检查）-> 校验 SHA256 -> 启动安装包</summary>
        private void RunUpdate(UpdateInfo info)
        {
            UpdateDialog dlg = new UpdateDialog(info);
            dlg.Owner = this;
            dlg.Show();

            CancellationTokenSource cts = new CancellationTokenSource();
            bool mandatory = info.Mandatory;

            Thread t = new Thread(delegate()
            {
                bool ok = false;
                try
                {
                    ok = UpdateService.Download(info, delegate(int pct, long got, long total)
                    {
                        try
                        {
                            dlg.Dispatcher.BeginInvoke(new Action(delegate { dlg.SetProgress(pct, got, total); }));
                        }
                        catch { }
                    }, cts.Token);
                }
                catch (Exception ex) { Log.Error("update download crashed", ex); }

                bool started = false;
                if (ok)
                {
                    try
                    {
                        dlg.Dispatcher.BeginInvoke(new Action(delegate
                        { dlg.SetStatus("下载完成，正在校验并启动安装程序…"); }));
                    }
                    catch { }
                    started = UpdateService.RunInstaller(info);
                }

                try
                {
                    dlg.Dispatcher.BeginInvoke(new Action(delegate
                    {
                        try { dlg.Close(); } catch { }
                        if (!ok || !started)
                        {
                            Dlg.Error("更新失败",
                                "更新包下载、签名/哈希校验或启动失败，已中止更新（未运行任何安装程序）。\n详情见 Logs\\ 目录。");
                            if (mandatory) Application.Current.Shutdown();
                            return;
                        }
                        Application.Current.Shutdown();
                    }));
                }
                catch { }
            });
            t.IsBackground = true;
            t.Start();
        }

        private void BuildLayout()
        {
            Grid root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition());
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.Background = Theme.PageBg;
            if (!Diag.Has("nobanner")) root.Children.Add(BuildBanner());

            Grid body = new Grid();
            body.Margin = new Thickness(20, 16, 20, 0);
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(224) });
            body.ColumnDefinitions.Add(new ColumnDefinition());
            if (!Diag.Has("nonav")) body.Children.Add(BuildNav());
            _host.Margin = new Thickness(16, 0, 0, 0);
            Grid.SetColumn(_host, 1);
            body.Children.Add(_host);
            Grid.SetRow(body, 1);
            root.Children.Add(body);

            if (!Diag.Has("nofooter")) root.Children.Add(BuildFooter());
            if (root.Children.Count > 2) Grid.SetRow(root.Children[root.Children.Count - 1], 2);
            Content = root;
        }

        // ── 顶部主视觉 ──────────────────────────────────────────────────────
        private UIElement BuildBanner()
        {
            Border banner = new Border();
            banner.Margin = new Thickness(20, 20, 20, 0);
            banner.CornerRadius = new CornerRadius(20);
            banner.Background = Theme.Grad(Color.FromRgb(0xE7, 0xF1, 0xFF), Color.FromRgb(0xF6, 0xFA, 0xFF));
            banner.BorderBrush = new SolidColorBrush(Color.FromRgb(0xDD, 0xE9, 0xFA));
            banner.BorderThickness = new Thickness(1);
            banner.Padding = new Thickness(26, 22, 26, 22);

            Grid g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            StackPanel left = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            Border logo = new Border();
            logo.Width = 60;
            logo.Height = 60;
            logo.CornerRadius = new CornerRadius(16);
            logo.Background = Theme.PrimarySoft;
            TextBlock glyph = new TextBlock();
            glyph.Text = "DS";
            glyph.FontFamily = new FontFamily("Segoe UI Semibold");
            glyph.FontWeight = FontWeights.Bold;
            glyph.FontSize = 22;
            glyph.Foreground = Brushes.White;
            glyph.HorizontalAlignment = HorizontalAlignment.Center;
            glyph.VerticalAlignment = VerticalAlignment.Center;
            logo.Child = glyph;

            StackPanel name = new StackPanel { Margin = new Thickness(16, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            TextBlock t1 = new TextBlock();
            t1.Text = AppInfo.ProductName + "  ·  " + AppInfo.ChineseName;
            t1.FontSize = 24;
            t1.FontWeight = FontWeights.Bold;
            t1.Foreground = Theme.TextStrong;
            TextBlock t2 = new TextBlock();
            t2.Text = AppInfo.Tagline;
            t2.FontSize = 12.5;
            t2.Foreground = Theme.TextMuted;
            t2.Margin = new Thickness(0, 4, 0, 0);
            name.Children.Add(t1);
            name.Children.Add(t2);
            left.Children.Add(logo);
            left.Children.Add(name);
            g.Children.Add(left);

            StackPanel mid = new StackPanel { Margin = new Thickness(48, 0, 20, 0), VerticalAlignment = VerticalAlignment.Center };
            TextBlock h = new TextBlock();
            h.Text = Str.MainTitle;
            h.FontSize = 27;
            h.FontWeight = FontWeights.Bold;
            h.Foreground = Theme.TextStrong;
            TextBlock d = new TextBlock();
            d.Text = Str.MainSubtitle;
            d.FontSize = 13;
            d.Foreground = Theme.TextMuted;
            d.Margin = new Thickness(0, 8, 0, 0);
            d.TextWrapping = TextWrapping.Wrap;
            d.LineHeight = 20;
            mid.Children.Add(h);
            mid.Children.Add(d);
            Grid.SetColumn(mid, 1);
            g.Children.Add(mid);

            // 右侧只保留项目信息（开源版：无商业化元素）
            StackPanel right = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
            TextBlock ver = new TextBlock();
            ver.Text = "v" + AppInfo.Version + " · " + AppInfo.License;
            ver.FontSize = 12.5;
            ver.Foreground = Theme.TextMuted;
            ver.HorizontalAlignment = HorizontalAlignment.Right;
            TextBlock repo = new TextBlock();
            repo.Text = Str.GitHubProject;
            repo.FontSize = 12;
            repo.Foreground = new SolidColorBrush(Color.FromRgb(0x8B, 0x9B, 0xB0));
            repo.Margin = new Thickness(0, 6, 0, 0);
            repo.HorizontalAlignment = HorizontalAlignment.Right;

            // 访问官网
            Button site = new Button();
            site.Content = Str.VisitSite;
            site.FontSize = 12.5;
            site.Height = 30;
            site.Padding = new Thickness(14, 0, 14, 0);
            site.Margin = new Thickness(0, 8, 0, 0);
            site.HorizontalAlignment = HorizontalAlignment.Right;
            site.Cursor = System.Windows.Input.Cursors.Hand;
            site.Foreground = new SolidColorBrush(Color.FromRgb(0x1F, 0x6F, 0xE0));
            site.Background = new SolidColorBrush(Color.FromRgb(0xEB, 0xF3, 0xFF));
            site.BorderBrush = new SolidColorBrush(Color.FromRgb(0xCF, 0xDF, 0xF7));
            site.BorderThickness = new Thickness(1);
            site.Template = BuildPillTemplate(new CornerRadius(9));
            site.Click += delegate { OpenSite(); };

            right.Children.Add(ver);
            right.Children.Add(repo);
            right.Children.Add(site);
            Grid.SetColumn(right, 2);
            g.Children.Add(right);

            banner.Child = g;
            return banner;
        }

        /// <summary>打开官网下载页</summary>
        private void OpenSite()
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(AppInfo.SiteUrl)
                {
                    UseShellExecute = true
                });
            }
            catch (Exception ex) { Log.Error("open site failed", ex); }
        }

        /// <summary>圆角按钮模板（无默认边框，hover 变色）</summary>
        private static ControlTemplate BuildPillTemplate(CornerRadius radius)
        {
            string xaml =
                "<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'" +
                " TargetType='Button'>" +
                "  <Border CornerRadius='" + radius.TopLeft + "' Background='{TemplateBinding Background}'" +
                "          BorderBrush='{TemplateBinding BorderBrush}' BorderThickness='{TemplateBinding BorderThickness}'>" +
                "    <ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center'/>" +
                "  </Border>" +
                "</ControlTemplate>";
            return (ControlTemplate)XamlReader.Parse(xaml);
        }

        // ── 左侧导航 ───────────────────────────────────────────────────────
        private UIElement BuildNav()
        {
            Border card = new Border();
            card.Background = Theme.Card;
            card.CornerRadius = new CornerRadius(18);
            card.BorderBrush = Theme.CardLine;
            card.BorderThickness = new Thickness(1);
            card.Padding = new Thickness(12, 16, 12, 16);

            StackPanel sp = new StackPanel();
            string[] labels = { Str.NavOverview, Str.NavRuntime, Str.NavDirectX, Str.NavSystemDll,
                                Str.NavDownLevel, Str.NavDownload, Str.NavClean };
            string[] glyphs = { "\uE80F", "\uE74C", "\uE7FC", "\uE8A5", "\uE8A5", "\uE896", "\uEDA2" };

            for (int i = 0; i < labels.Length; i++)
            {
                Button item = new Button();
                item.Height = 48;
                item.Margin = new Thickness(0, 0, 0, 6);
                item.Background = Brushes.Transparent;
                item.BorderThickness = new Thickness(0);
                item.HorizontalContentAlignment = HorizontalAlignment.Stretch;
                item.Padding = new Thickness(0);
                item.Cursor = System.Windows.Input.Cursors.Hand;
                item.Tag = i;

                Grid ig = new Grid();
                ig.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });
                ig.ColumnDefinitions.Add(new ColumnDefinition());

                TextBlock gl = new TextBlock();
                gl.Text = glyphs[i];
                gl.FontFamily = new FontFamily("Segoe MDL2 Assets");
                gl.FontSize = 17;
                gl.HorizontalAlignment = HorizontalAlignment.Center;
                gl.VerticalAlignment = VerticalAlignment.Center;
                gl.Foreground = Theme.TextMuted;
                ig.Children.Add(gl);

                TextBlock lb = new TextBlock();
                lb.Text = labels[i];
                lb.FontSize = 14;
                lb.VerticalAlignment = VerticalAlignment.Center;
                lb.Foreground = Theme.TextBody;
                Grid.SetColumn(lb, 1);
                ig.Children.Add(lb);

                item.Content = ig;
                _navItems.Add(item);
                _navLabels.Add(lb);
                item.Click += delegate(object s, RoutedEventArgs e)
                {
                    Button b = (Button)s;
                    ShowPage((int)b.Tag);
                };
                sp.Children.Add(item);
            }

            card.Child = sp;
            return card;
        }

        private void ShowPage(int index)
        {
            string[] glyphs = { "\uE80F", "\uE74C", "\uE7FC", "\uE8A5", "\uE896", "\uE896", "\uEDA2" };
            for (int i = 0; i < _navItems.Count; i++)
            {
                bool active = i == index;
                _navItems[i].Background = active ? Theme.PrimarySoft : Brushes.Transparent;
                _navLabels[i].Foreground = active ? Brushes.White : Theme.TextBody;
                _navLabels[i].FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal;
                TextBlock icon = (TextBlock)((Grid)_navItems[i].Content).Children[0];
                icon.Foreground = active ? Brushes.White : Theme.TextMuted;
            }

            UserControl page;
            switch (index)
            {
                case 1:
                    page = new ScanPage(Str.NavRuntime, glyphs[1], Str.DescRuntime,
                        delegate
                        {
                            List<CatalogItem> l = Catalog.LoadRuntime("运行库", "VC_Scan", Environment.Is64BitOperatingSystem);
                            l.AddRange(Catalog.LoadRuntime("运行库", "VC_DLL_Scan", Environment.Is64BitOperatingSystem));
                            l.AddRange(Catalog.LoadRuntime("运行库", "NETWORK", Environment.Is64BitOperatingSystem));
                            return l;
                        }, false, 1);
                    break;
                case 2:
                    page = new ScanPage(Str.NavDirectX, glyphs[2], Str.DescDirectX,
                        delegate
                        {
                            List<CatalogItem> l = Catalog.LoadRuntime("DirectX", "DirectX", Environment.Is64BitOperatingSystem);
                            l.AddRange(Catalog.LoadRuntime("DownLevel", "downlevel", Environment.Is64BitOperatingSystem));
                            return l;
                        }, false, 2);
                    break;
                case 3:
                    page = new ScanPage(Str.NavSystemDll, glyphs[3], Str.DescSystemDll,
                        delegate { return Catalog.LoadSystemDll(Environment.Is64BitOperatingSystem); }, false, 3);
                    break;
                case 4:
                    page = new ScanPage(Str.NavDownLevel, glyphs[4], Str.DescDownLevel,
                        delegate { return Catalog.LoadRuntime("DownLevel", "downlevel", Environment.Is64BitOperatingSystem); }, false, 4);
                    break;
                case 5:
                    page = new ScanPage(Str.NavDownload, glyphs[5], Str.DescDownload,
                        delegate { return Catalog.LoadSystemDll(Environment.Is64BitOperatingSystem); }, true, 5);
                    break;
                case 6:
                    page = new CleanPage();
                    break;
                case 0:
                default:
                    OverviewPage ov = new OverviewPage();
                    ov.Navigate += delegate(int target) { ShowPage(target); };
                    page = ov;
                    break;
            }

            IProgressHost host = page as IProgressHost;
            if (host != null) host.StatusChanged += delegate(string s, int percent, bool busy)
            {
                if (!Dispatcher.CheckAccess())
                {
                    Dispatcher.BeginInvoke(new Action(delegate { UpdateStatus(s, percent, busy); }));
                    return;
                }
                UpdateStatus(s, percent, busy);
            };
            _host.Content = page;
        }

        private void UpdateStatus(string s, int percent, bool busy)
        {
            _status.Text = s;
            bool downloading = busy && percent > 0 && percent < 100;
            _spin.Visibility = downloading ? Visibility.Visible : Visibility.Collapsed;
            _percent.Visibility = downloading ? Visibility.Visible : Visibility.Collapsed;
            _percent.Text = percent + "%";
        }

        private static List<CatalogItem> LoadAll()
        {
            List<CatalogItem> all = new List<CatalogItem>();
            all.AddRange(Catalog.LoadSystemDll(Environment.Is64BitOperatingSystem));
            all.AddRange(Catalog.LoadRuntime("运行库", "VC_Scan", Environment.Is64BitOperatingSystem));
            all.AddRange(Catalog.LoadRuntime("运行库", "VC_DLL_Scan", Environment.Is64BitOperatingSystem));
            all.AddRange(Catalog.LoadRuntime("DirectX", "DirectX", Environment.Is64BitOperatingSystem));
            all.AddRange(Catalog.LoadRuntime("DownLevel", "downlevel", Environment.Is64BitOperatingSystem));
            all.AddRange(Catalog.LoadRuntime("Network", "NETWORK", Environment.Is64BitOperatingSystem));
            return all;
        }

        // ── 底部状态栏（状态 + 下载圆圈 + 百分比） ─────────────────────────
        private UIElement BuildFooter()
        {
            Border footer = new Border();
            footer.Background = Brushes.White;
            footer.BorderBrush = Theme.CardLine;
            footer.BorderThickness = new Thickness(0, 1, 0, 0);
            footer.Padding = new Thickness(24, 11, 24, 11);

            StackPanel left = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            TextBlock info = new TextBlock();
            info.Text = "\uE946";
            info.FontFamily = new FontFamily("Segoe MDL2 Assets");
            info.FontSize = 13;
            info.Foreground = Theme.Primary;
            info.Margin = new Thickness(0, 0, 8, 0);
            info.VerticalAlignment = VerticalAlignment.Center;
            _status.Text = Str.Ready;
            _status.FontSize = 13;
            _status.Foreground = Theme.TextBody;
            _status.VerticalAlignment = VerticalAlignment.Center;
            left.Children.Add(info);
            left.Children.Add(_status);

            // 下载时转圈的小圆圈
            _spin.Width = 15;
            _spin.Height = 15;
            _spin.Margin = new Thickness(12, 0, 0, 0);
            _spin.VerticalAlignment = VerticalAlignment.Center;
            _spin.Visibility = Visibility.Collapsed;
            System.Windows.Shapes.Path arc = new System.Windows.Shapes.Path();
            arc.Stroke = Theme.Primary;
            arc.StrokeThickness = 2;
            arc.StrokeStartLineCap = PenLineCap.Round;
            arc.StrokeEndLineCap = PenLineCap.Round;
            arc.Data = Geometry.Parse("M 7.5,1.5 A 6,6 0 0 1 13.5,7.5");
            arc.RenderTransformOrigin = new Point(0.5, 0.5);
            arc.RenderTransform = _spinRot;
            _spin.Children.Add(arc);
            if (!Diag.Has("nospin"))
            {
                DoubleAnimation spinAnim = new DoubleAnimation(0, 360, new Duration(TimeSpan.FromMilliseconds(900)));
                spinAnim.RepeatBehavior = RepeatBehavior.Forever;
                _spinRot.BeginAnimation(RotateTransform.AngleProperty, spinAnim);
            }
            left.Children.Add(_spin);

            _percent.Text = "0%";
            _percent.FontSize = 12.5;
            _percent.Foreground = Theme.Primary;
            _percent.Margin = new Thickness(8, 0, 0, 0);
            _percent.VerticalAlignment = VerticalAlignment.Center;
            _percent.Visibility = Visibility.Collapsed;
            left.Children.Add(_percent);

            // 右侧：还原被替换的文件（F1 评审 blocker：回滚必须有界面入口，与 --rollback 共用同一套逻辑）
            Button rollback = new Button();
            rollback.Content = Str.BtnRollback;
            rollback.FontSize = 12.5;
            rollback.Height = 30;
            rollback.Padding = new Thickness(14, 0, 14, 0);
            rollback.Cursor = System.Windows.Input.Cursors.Hand;
            rollback.Foreground = Theme.Primary;
            rollback.Background = new SolidColorBrush(Color.FromRgb(0xEB, 0xF3, 0xFF));
            rollback.BorderBrush = new SolidColorBrush(Color.FromRgb(0xCF, 0xDF, 0xF7));
            rollback.BorderThickness = new Thickness(1);
            rollback.ToolTip = Str.RollbackHint;
            rollback.Template = BuildPillTemplate(new CornerRadius(9));
            rollback.Click += delegate { RollbackDialog.Show(this); };

            Grid footerGrid = new Grid();
            footerGrid.ColumnDefinitions.Add(new ColumnDefinition());
            footerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            footerGrid.Children.Add(left);
            Grid.SetColumn(rollback, 1);
            rollback.VerticalAlignment = VerticalAlignment.Center;
            footerGrid.Children.Add(rollback);

            footer.Child = footerGrid;
            return footer;
        }
    }

    // ── 更新下载进度窗 ─────────────────────────────────────────────────────
    public class UpdateDialog : Window
    {
        private readonly ProgressBar _bar = new ProgressBar();
        private readonly TextBlock _pct = new TextBlock();
        private readonly TextBlock _detail = new TextBlock();
        private readonly TextBlock _status = new TextBlock();

        public UpdateDialog(UpdateInfo info)
        {
            Title = "正在更新 DllSmith";
            Width = 520;
            Height = 236;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            // 性能：不要用 AllowsTransparency（会把整个窗口变成 layered window、强制软件合成）。
            // 自绘标题栏改用 WindowChrome，圆角交给内部卡片 Border。
            AllowsTransparency = false;
            Background = new SolidColorBrush(Color.FromRgb(0xF3, 0xF7, 0xFE));
            System.Windows.Shell.WindowChrome.SetWindowChrome(this, new System.Windows.Shell.WindowChrome
            {
                CaptionHeight = 0,
                ResizeBorderThickness = new Thickness(0),
                GlassFrameThickness = new Thickness(0),
                CornerRadius = new CornerRadius(0),
                UseAeroCaptionButtons = false
            });
            ShowInTaskbar = false;
            Topmost = true;

            Border card = new Border();
            card.Background = Brushes.White;
            card.CornerRadius = new CornerRadius(18);
            card.BorderBrush = new SolidColorBrush(Color.FromRgb(0xE2, 0xEA, 0xF7));
            card.BorderThickness = new Thickness(1);
            card.Padding = new Thickness(28, 24, 28, 22);

            StackPanel root = new StackPanel();

            // 标题行
            StackPanel head = new StackPanel { Orientation = Orientation.Horizontal };
            Border logo = new Border();
            logo.Width = 36;
            logo.Height = 36;
            logo.CornerRadius = new CornerRadius(11);
            logo.Background = new LinearGradientBrush(Color.FromRgb(0x42, 0x8E, 0xF8), Color.FromRgb(0x18, 0x5C, 0xD0), 90);
            TextBlock lt = new TextBlock();
            lt.Text = "DS";
            lt.Foreground = Brushes.White;
            lt.FontSize = 15;
            lt.FontWeight = FontWeights.Bold;
            lt.HorizontalAlignment = HorizontalAlignment.Center;
            lt.VerticalAlignment = VerticalAlignment.Center;
            logo.Child = lt;

            StackPanel tt = new StackPanel { Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            TextBlock t1 = new TextBlock();
            t1.Text = "正在下载更新 v" + info.Version;
            t1.FontSize = 15.5;
            t1.FontWeight = FontWeights.SemiBold;
            t1.Foreground = new SolidColorBrush(Color.FromRgb(0x12, 0x23, 0x3D));
            TextBlock t2 = new TextBlock();
            t2.Text = "请不要关闭窗口，下载完成后会自动启动安装程序";
            t2.FontSize = 11.5;
            t2.Margin = new Thickness(0, 4, 0, 0);
            t2.Foreground = new SolidColorBrush(Color.FromRgb(0x7A, 0x8C, 0xA4));
            tt.Children.Add(t1);
            tt.Children.Add(t2);

            head.Children.Add(logo);
            head.Children.Add(tt);
            root.Children.Add(head);

            // 进度条
            _bar.Height = 9;
            _bar.Minimum = 0;
            _bar.Maximum = 100;
            _bar.Value = 0;
            _bar.Margin = new Thickness(0, 22, 0, 0);
            _bar.Foreground = new LinearGradientBrush(Color.FromRgb(0x5A, 0xA0, 0xFA), Color.FromRgb(0x1F, 0x6F, 0xE0), 0);
            _bar.Background = new SolidColorBrush(Color.FromRgb(0xE9, 0xEF, 0xF9));
            _bar.BorderThickness = new Thickness(0);
            root.Children.Add(_bar);

            // 百分比 + 已下载/总量
            Grid infoRow = new Grid { Margin = new Thickness(0, 10, 0, 0) };
            infoRow.ColumnDefinitions.Add(new ColumnDefinition());
            infoRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            _detail.Text = "正在连接…";
            _detail.FontSize = 11.5;
            _detail.Foreground = new SolidColorBrush(Color.FromRgb(0x8A, 0x9B, 0xB2));
            _detail.VerticalAlignment = VerticalAlignment.Center;
            infoRow.Children.Add(_detail);

            _pct.Text = "0%";
            _pct.FontSize = 13;
            _pct.FontWeight = FontWeights.SemiBold;
            _pct.Foreground = new SolidColorBrush(Color.FromRgb(0x1F, 0x6F, 0xE0));
            Grid.SetColumn(_pct, 1);
            infoRow.Children.Add(_pct);
            root.Children.Add(infoRow);

            _status.Text = "";
            _status.FontSize = 11.5;
            _status.Margin = new Thickness(0, 14, 0, 0);
            _status.Foreground = new SolidColorBrush(Color.FromRgb(0x9A, 0xA9, 0xBC));
            root.Children.Add(_status);

            card.Child = root;
            Content = card;
        }

        public void SetProgress(int percent, long got, long total)
        {
            if (percent < 0) percent = 0;
            if (percent > 100) percent = 100;
            _bar.Value = percent;
            _pct.Text = percent + "%";
            if (total > 0)
                _detail.Text = string.Format("{0:0.0} MB / {1:0.0} MB",
                    got / 1048576.0, total / 1048576.0);
            else if (got > 0)
                _detail.Text = string.Format("{0:0.0} MB", got / 1048576.0);
        }

        public void SetStatus(string text)
        {
            _status.Text = text;
            if (text != null && text.IndexOf("安装程序", StringComparison.Ordinal) >= 0)
            {
                _bar.Value = 100;
                _pct.Text = "100%";
            }
        }
    }

    public static class RepairEngineHolder
    {
        public static bool DryRun { get; set; }
    }

    // ── 扫描 / 修复页 ───────────────────────────────────────────────────────
    public class ScanPage : UserControl, IProgressHost
    {
        private readonly ObservableCollection<CatalogItem> _items = new ObservableCollection<CatalogItem>();
        private readonly Func<List<CatalogItem>> _loader;
        private readonly bool _downloadOnly;
        private readonly string _title;

        private readonly ListBox _list = new ListBox();
        private TextBlock _placeholder;
        private readonly TextBox _logBox = new TextBox();
        private readonly TextBlock _logLast = new TextBlock();
        private readonly Button _logToggle = new Button();
        private Border _logCard;
        private bool _logExpanded;
        private readonly Button _btnScan = new Button();
        private readonly Button _btnRepair = new Button();
        private readonly Button _btnStop = new Button();
        private readonly Border _allCheck = new Border();
        private readonly TextBlock _allCheckGlyph = new TextBlock();

        private TextBlock _cardTotal;
        private TextBlock _cardMissing;
        private TextBlock _cardOk;

        private CancellationTokenSource _cts;
        private bool _busy;
        private readonly int _page = -1;
        private CatalogTrustSet _trust = new CatalogTrustSet();
        private ListCollectionView _view;

        public event Action<string, int, bool> StatusChanged;

        public ScanPage(string title, string glyph, string desc, Func<List<CatalogItem>> loader, bool downloadOnly)
            : this(title, glyph, desc, loader, downloadOnly, -1) { }

        public ScanPage(string title, string glyph, string desc, Func<List<CatalogItem>> loader, bool downloadOnly, int pageIndex)
        {
            _title = title;
            _loader = loader;
            _downloadOnly = downloadOnly;
            _page = pageIndex;

            Build(title, glyph, desc);
            Log.LineWritten += OnLogLine;
            // 不再自动扫描：由用户点「开始检查」触发

            // 总览里已经检查过 → 直接复用结果，不用再扫一遍
            List<CatalogItem> cached = ScanStore.Get(_page);
            if (cached != null && cached.Count > 0)
            {
                foreach (CatalogItem it in cached) _items.Add(it);
                ApplySort();
                UpdateCards();
                SetButtonText(_btnScan, Str.BtnRescan);
                if (_placeholder != null) _placeholder.Visibility = Visibility.Collapsed;
                // 载入缓存的页面同样要跑一次信任态刷新：否则"目录未校验"时会给出空白原因（F3）
                if (_trust == null || _trust.ByKey.Count == 0)
                    _trust = CatalogTrustSet.LoadAll(cached);
                ApplyTrustUi();
                Report(string.Format("已载入总览的检查结果：{0} 项", cached.Count), 0, false);
            }

            Unloaded += delegate { Log.LineWritten -= OnLogLine; };
        }

        private void Build(string title, string glyph, string desc)
        {
            Grid root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition());
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // 页头
            StackPanel head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(2, 0, 0, 16) };
            Border tile = new Border();
            tile.Width = 50;
            tile.Height = 50;
            tile.CornerRadius = new CornerRadius(14);
            tile.Background = new SolidColorBrush(Color.FromRgb(0xE9, 0xF2, 0xFF));
            TextBlock tg = new TextBlock();
            tg.Text = glyph;
            tg.FontFamily = new FontFamily("Segoe MDL2 Assets");
            tg.FontSize = 22;
            tg.Foreground = Theme.Primary;
            tg.HorizontalAlignment = HorizontalAlignment.Center;
            tg.VerticalAlignment = VerticalAlignment.Center;
            tile.Child = tg;

            StackPanel ht = new StackPanel { Margin = new Thickness(14, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            TextBlock h = new TextBlock();
            h.Text = title;
            h.FontSize = 23;
            h.FontWeight = FontWeights.Bold;
            h.Foreground = Theme.TextStrong;
            TextBlock d = new TextBlock();
            d.Text = desc;
            d.FontSize = 12.5;
            d.Foreground = Theme.TextMuted;
            d.Margin = new Thickness(0, 5, 0, 0);
            ht.Children.Add(h);
            ht.Children.Add(d);
            head.Children.Add(tile);
            head.Children.Add(ht);
            root.Children.Add(head);

            // 统计卡 + 操作
            Grid mid = new Grid();
            mid.Margin = new Thickness(0, 0, 0, 14);
            mid.ColumnDefinitions.Add(new ColumnDefinition());
            mid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            Grid cards = new Grid();
            for (int i = 0; i < 3; i++) cards.ColumnDefinitions.Add(new ColumnDefinition());
            _cardTotal = new TextBlock();
            _cardMissing = new TextBlock();
            _cardOk = new TextBlock();
            cards.Children.Add(StatCard(0, "\uE73E", Theme.Ok, Theme.OkBg, "检测完成", _cardTotal, "0 项"));
            cards.Children.Add(StatCard(1, "\uE7BA", new SolidColorBrush(Color.FromRgb(0xEF, 0x8E, 0x1F)),
                new SolidColorBrush(Color.FromRgb(0xFF, 0xF3, 0xE2)), "缺失 / 待修复", _cardMissing, "0 项"));
            cards.Children.Add(StatCard(2, "\uE930", Theme.Ok, Theme.OkBg, "正常", _cardOk, "0 项"));
            mid.Children.Add(cards);

            StackPanel actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            StyleButton(_btnScan, Str.BtnScan, false, "\uE72C");
            _btnScan.Click += delegate { BeginScan(); };
            StyleButton(_btnRepair, _downloadOnly ? Str.BtnDownloadOnly : Str.BtnRepair, true, "\uE8B0");
            _btnRepair.Click += delegate { BeginRepair(); };
            StyleButton(_btnStop, Str.BtnStop, false, "\uE71A");
            _btnStop.Margin = new Thickness(8, 0, 0, 0);
            _btnStop.Visibility = Visibility.Collapsed;
            _btnStop.Click += delegate
            {
                if (_cts != null) _cts.Cancel();
                _btnStop.IsEnabled = false;
                Report("正在停止…", 0, true);
            };
            actions.Children.Add(_btnScan);
            actions.Children.Add(_btnRepair);
            actions.Children.Add(_btnStop);
            Grid.SetColumn(actions, 1);
            mid.Children.Add(actions);
            root.Children.Add(mid);
            Grid.SetRow(mid, 1);

            // 表格
            Border tableCard = new Border();
            tableCard.Background = Theme.Card;
            tableCard.CornerRadius = new CornerRadius(18);
            tableCard.BorderBrush = Theme.CardLine;
            tableCard.BorderThickness = new Thickness(1);
            tableCard.ClipToBounds = true;

            Grid table = new Grid();
            table.RowDefinitions.Add(new RowDefinition { Height = new GridLength(46) });
            // 最小高度：日志面板展开时列表不能被压成 0（P1：1080x700 下 0 高度）
            table.RowDefinitions.Add(new RowDefinition { MinHeight = 180 });

            Grid header = new Grid();
            header.Background = new SolidColorBrush(Color.FromRgb(0xF5, 0xF9, 0xFF));
            BuildColumns(header);
            AddCell(header, 0, AllCheckBox());
            AddText(header, 1, _title == "系统 DLL 修复" || _title == "系统文件下载" ? "系统文件" : "运行库组件",
                    12.5, Theme.TextBody, true, new Thickness(8, 0, 0, 0));
            AddText(header, 2, "架构", 12.5, Theme.TextBody, true, new Thickness(8, 0, 0, 0));
            AddText(header, 3, IsSystemPage() ? "版本 / 目标路径" : "版本", 12.5, Theme.TextBody, true, new Thickness(8, 0, 0, 0));
            AddText(header, 4, "状态", 12.5, Theme.TextBody, true, new Thickness(8, 0, 0, 0));
            AddText(header, 5, Str.ColumnProgress, 12.5, Theme.TextBody, true, new Thickness(8, 0, 0, 0));
            AddText(header, 6, Str.ColumnCheck, 12.5, Theme.TextBody, true, new Thickness(8, 0, 0, 0));
            table.Children.Add(header);

            _list.ItemsSource = _items;
            ApplySort();
            _list.BorderThickness = new Thickness(0);
            _list.Background = Brushes.White;
            _list.HorizontalContentAlignment = HorizontalAlignment.Stretch;
            _list.ItemTemplate = BuildRowTemplate();
            _list.ItemContainerStyle = BuildRowStyle();
            _list.Resources.Add(typeof(ScrollBar), BuildScrollBarStyle(false));
            // 性能：容器回收 + 像素滚动（虚拟化本身没坏，但 Standard 模式会不停重建容器）
            VirtualizingPanel.SetIsVirtualizing(_list, true);
            VirtualizingPanel.SetVirtualizationMode(_list, VirtualizationMode.Recycling);
            VirtualizingPanel.SetScrollUnit(_list, ScrollUnit.Pixel);
            ScrollViewer.SetCanContentScroll(_list, true);
            Grid.SetRow(_list, 1);
            table.Children.Add(_list);

            _placeholder = new TextBlock();
            _placeholder.Text = Str.EmptyScan;
            _placeholder.FontSize = 13;
            _placeholder.Foreground = Theme.TextMuted;
            _placeholder.HorizontalAlignment = HorizontalAlignment.Center;
            _placeholder.VerticalAlignment = VerticalAlignment.Center;
            _placeholder.Margin = new Thickness(0, 24, 0, 24);
            Grid.SetRow(_placeholder, 1);
            table.Children.Add(_placeholder);

            tableCard.Child = table;
            root.Children.Add(tableCard);
            Grid.SetRow(tableCard, 2);

            root.Children.Add(BuildLogPanel());
            Grid.SetRow(root.Children[3], 3);

            Content = root;
        }

        private UIElement AllCheckBox()
        {
            _allCheckGlyph.Text = "\uE73A";
            _allCheckGlyph.FontFamily = new FontFamily("Segoe MDL2 Assets");
            _allCheckGlyph.FontSize = 14;
            _allCheckGlyph.Foreground = Theme.Primary;
            _allCheckGlyph.HorizontalAlignment = HorizontalAlignment.Center;
            _allCheckGlyph.VerticalAlignment = VerticalAlignment.Center;
            _allCheck.Background = Brushes.Transparent;
            _allCheck.Cursor = System.Windows.Input.Cursors.Hand;
            _allCheck.Child = _allCheckGlyph;
            _allCheck.MouseLeftButtonUp += delegate
            {
                bool selectAll = _items.Any(delegate(CatalogItem i) { return !i.Selected; });
                foreach (CatalogItem i in _items) i.Selected = selectAll;
                _allCheckGlyph.Text = selectAll ? "\uE73A" : "\uE739";
            };
            return _allCheck;
        }

        private static void BuildColumns(Grid g)
        {
            // 必须与行模板列数一致（原来 5 列 vs 行 6 列 -> 表头与内容错位，P1）
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(48) });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(104) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
        }

        private static void AddCell(Grid g, int col, UIElement el)
        {
            Grid.SetColumn(el, col);
            g.Children.Add(el);
        }

        private static void AddText(Grid g, int col, string text, double size, Brush fg, bool bold, Thickness margin)
        {
            TextBlock t = new TextBlock();
            t.Text = text;
            t.FontSize = size;
            t.Foreground = fg;
            t.FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal;
            t.VerticalAlignment = VerticalAlignment.Center;
            t.Margin = margin;
            AddCell(g, col, t);
        }

        private UIElement StatCard(int col, string glyph, Brush fg, Brush bg, string label, TextBlock valueBlock, string init)
        {
            Border card = new Border();
            card.Background = Theme.Card;
            card.CornerRadius = new CornerRadius(14);
            card.BorderBrush = Theme.CardLine;
            card.BorderThickness = new Thickness(1);
            card.Padding = new Thickness(20, 14, 20, 14);
            card.Margin = new Thickness(col == 0 ? 0 : 10, 0, col == 2 ? 0 : 10, 0);

            Grid g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(46) });
            g.ColumnDefinitions.Add(new ColumnDefinition());

            Border icon = new Border();
            icon.Width = 36;
            icon.Height = 36;
            icon.CornerRadius = new CornerRadius(18);
            icon.Background = bg;
            icon.VerticalAlignment = VerticalAlignment.Center;
            TextBlock gl = new TextBlock();
            gl.Text = glyph;
            gl.FontFamily = new FontFamily("Segoe MDL2 Assets");
            gl.FontSize = 16;
            gl.Foreground = fg;
            gl.HorizontalAlignment = HorizontalAlignment.Center;
            gl.VerticalAlignment = VerticalAlignment.Center;
            icon.Child = gl;
            g.Children.Add(icon);

            StackPanel sp = new StackPanel { Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            TextBlock lb = new TextBlock();
            lb.Text = label;
            lb.FontSize = 12;
            lb.Foreground = Theme.TextMuted;
            valueBlock.Text = init;
            valueBlock.FontSize = 22;
            valueBlock.FontWeight = FontWeights.Bold;
            valueBlock.Foreground = Theme.TextStrong;
            valueBlock.Margin = new Thickness(0, 3, 0, 0);
            sp.Children.Add(lb);
            sp.Children.Add(valueBlock);
            Grid.SetColumn(sp, 1);
            g.Children.Add(sp);

            card.Child = g;
            Grid.SetColumn(card, col);
            return card;
        }

        /// <summary>
        /// 原地排序（缺失置顶 -> 正常 -> 未判定，其次按名称）。
        /// 用 ListCollectionView.CustomSort，不再每次 new 一个 List 赋给 ItemsSource：
        /// 换源会重建全部容器，扫描中列表还会空白（性能审计 §3）。
        /// </summary>
        private void ApplySort()
        {
            if (_view == null)
            {
                _view = CollectionViewSource.GetDefaultView(_items) as ListCollectionView;
                if (_view != null) _view.CustomSort = new CatalogComparer();
            }
            if (_view != null) _view.Refresh();
        }

        private sealed class CatalogComparer : System.Collections.IComparer
        {
            public int Compare(object x, object y)
            {
                CatalogItem a = x as CatalogItem;
                CatalogItem b = y as CatalogItem;
                if (a == null || b == null) return 0;
                int ra = StateRank(a), rb = StateRank(b);
                if (ra != rb) return ra.CompareTo(rb);
                return string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase);
            }
        }

        /// <summary>本页是否系统文件类（决定第四列显示版本还是目标路径）</summary>
        private bool IsSystemPage()
        {
            return _title != null && (_title.IndexOf("DLL", StringComparison.Ordinal) >= 0
                || _title.IndexOf("系统文件", StringComparison.Ordinal) >= 0);
        }

        /// <summary>按目录信任状态刷新界面：只读时禁用修复并写明原因</summary>
        private void ApplyTrustUi()
        {
            if (_btnRepair == null) return;
            bool can = _trust != null && _trust.CanRepair;
            _btnRepair.IsEnabled = can && !_busy;
            if (!can && _trust != null)
            {
                SetButtonText(_btnRepair, "只读：目录未通过校验");
                string reason = _trust.Reason;
                if (string.IsNullOrEmpty(reason)) reason = Str.RecheckForTrust;   // 空原因兜底，绝不给空白
                Report(Str.ReadOnlyBanner + reason, 0, false);
            }
            else
            {
                SetButtonText(_btnRepair, _downloadOnly ? "一键下载" : "一键修复");
            }
        }

        /// <summary>把多 key 的信任集合适配成 RepairEngine 认的单一对象</summary>
        private CatalogTrust MultiTrustAdapter()
        {
            if (_trust == null || _trust.ByKey.Count == 0) return null;
            if (!_trust.CanRepair)
            {
                foreach (CatalogTrust x in _trust.ByKey.Values)
                    if (!x.CanRepair) return x;
            }
            CatalogTrust probe = new CatalogTrust();
            probe.Key = "(page)";
            probe.State = CatalogTrustState.Trusted;
            probe.HashedEntries = _trust.HashedEntries;
            probe.TotalEntries = _trust.TotalEntries;
            probe.LineCount = _trust.TotalEntries;
            return probe;
        }

        private static int StateRank(CatalogItem i)
        {
            if (i.State == ItemState.Absent) return 0;      // 缺失置顶
            if (i.State == ItemState.Unknown) return 2;     // 未判定垫底
            return 1;                                       // 正常
        }

        /// <summary>忙碌时禁用按钮，并换成浅底深字的可读配色</summary>
        private void EnterBusy(bool on)
        {
            _busy = on;
            if (_btnStop != null)
            {
                _btnStop.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
                _btnStop.IsEnabled = on;
            }
            _btnScan.IsEnabled = !on;
            _btnRepair.IsEnabled = !on;
            PaintButton(_btnScan, false, on);
            PaintButton(_btnRepair, true, on);
        }

        private static void PaintButton(Button b, bool primary, bool disabled)
        {
            if (disabled)
            {
                b.Background = new SolidColorBrush(Color.FromRgb(0xEC, 0xF1, 0xF9));
                b.Foreground = new SolidColorBrush(Color.FromRgb(0x7E, 0x90, 0xA9));
                b.BorderBrush = new SolidColorBrush(Color.FromRgb(0xD8, 0xE3, 0xF2));
            }
            else
            {
                b.Background = primary ? Theme.PrimarySoft : Brushes.White;
                b.Foreground = primary ? Brushes.White : Theme.Primary;
                b.BorderBrush = Theme.Primary;
            }
        }

        private void StyleButton(Button b, string text, bool primary, string glyph)
        {
            b.Height = 44;
            b.MinWidth = 132;
            b.Margin = new Thickness(10, 0, 0, 0);
            b.Padding = new Thickness(18, 0, 18, 0);
            b.FontSize = 13.5;
            b.FontWeight = FontWeights.SemiBold;
            b.BorderThickness = new Thickness(primary ? 0 : 1);
            b.BorderBrush = Theme.Primary;
            b.Foreground = primary ? Brushes.White : Theme.Primary;
            b.Background = primary ? Theme.PrimarySoft : Brushes.White;
            b.Cursor = System.Windows.Input.Cursors.Hand;

            StackPanel sp = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
            TextBlock g = new TextBlock();
            g.Text = glyph;
            g.FontFamily = new FontFamily("Segoe MDL2 Assets");
            g.FontSize = 14;
            g.VerticalAlignment = VerticalAlignment.Center;
            g.Margin = new Thickness(0, 0, 8, 0);
            g.SetBinding(TextBlock.ForegroundProperty, new Binding("Foreground") { Source = b });
            TextBlock t = new TextBlock();
            t.Text = text;
            t.VerticalAlignment = VerticalAlignment.Center;
            t.SetBinding(TextBlock.ForegroundProperty, new Binding("Foreground") { Source = b });
            sp.Children.Add(g);
            sp.Children.Add(t);
            b.Content = sp;
        }

        /// <summary>改按钮上的文字（StackPanel: 图标 + 文字）</summary>
        private static void SetButtonText(Button b, string text)
        {
            StackPanel sp = b.Content as StackPanel;
            if (sp == null || sp.Children.Count < 2) return;
            TextBlock t = sp.Children[1] as TextBlock;
            if (t != null) t.Text = text;
        }

        private Style BuildRowStyle()
        {
            Style s = new Style(typeof(ListBoxItem));
            s.Setters.Add(new Setter(ListBoxItem.PaddingProperty, new Thickness(0)));
            s.Setters.Add(new Setter(ListBoxItem.MarginProperty, new Thickness(0)));
            s.Setters.Add(new Setter(ListBoxItem.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
            s.Setters.Add(new Setter(ListBoxItem.BackgroundProperty, Brushes.White));
            Trigger sel = new Trigger();
            sel.Property = ListBoxItem.IsSelectedProperty;
            sel.Value = true;
            sel.Setters.Add(new Setter(ListBoxItem.BackgroundProperty, new SolidColorBrush(Color.FromRgb(0xF2, 0xF7, 0xFF))));
            s.Triggers.Add(sel);
            return s;
        }

        /// <summary>统一滚动条样式（细、圆角，贴合卡片风格）</summary>
        private static Style BuildScrollBarStyle(bool dark)
        {
            string thumb = dark ? "#3C4B61" : "#C9D6E8";
            string xaml =
                "<Style xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'" +
                "       xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='ScrollBar'>" +
                "  <Setter Property='Width' Value='9'/>" +
                "  <Setter Property='Background' Value='Transparent'/>" +
                "  <Setter Property='Template'>" +
                "    <Setter.Value>" +
                "      <ControlTemplate TargetType='ScrollBar'>" +
                "        <Grid Background='Transparent'>" +
                "          <Track x:Name='PART_Track' IsDirectionReversed='true'>" +
                "            <Track.Thumb>" +
                "              <Thumb>" +
                "                <Thumb.Template>" +
                "                  <ControlTemplate TargetType='Thumb'>" +
                "                    <Border CornerRadius='5' Background='" + thumb + "' Margin='1,0'/>" +
                "                  </ControlTemplate>" +
                "                </Thumb.Template>" +
                "              </Thumb>" +
                "            </Track.Thumb>" +
                "          </Track>" +
                "        </Grid>" +
                "      </ControlTemplate>" +
                "    </Setter.Value>" +
                "  </Setter>" +
                "</Style>";
            return (Style)XamlReader.Parse(xaml);
        }

        private DataTemplate BuildRowTemplate()
        {
            string xaml =
                "<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'>" +
                "  <Border BorderBrush='#F0F4FA' BorderThickness='0,0,0,1' Padding='0,9'>" +
                "    <Grid>" +
                "      <Grid.ColumnDefinitions>" +
                "        <ColumnDefinition Width='48'/>" +
                "        <ColumnDefinition Width='*'/>" +
                "        <ColumnDefinition Width='100'/>" +
                "        <ColumnDefinition Width='150'/>" +
                "        <ColumnDefinition Width='120'/>" +
                "        <ColumnDefinition Width='104'/>" +
                "        <ColumnDefinition Width='96'/>" +
                "      </Grid.ColumnDefinitions>" +
                "      <CheckBox IsChecked='{Binding Selected, Mode=TwoWay}' HorizontalAlignment='Center' VerticalAlignment='Center'/>" +
                "      <StackPanel Grid.Column='1' Orientation='Horizontal' Margin='8,0,12,0'>" +
                "        <TextBlock Text='&#xE74C;' FontFamily='Segoe MDL2 Assets' FontSize='15' Foreground='#7A4DFF' VerticalAlignment='Center' Margin='0,0,10,0'/>" +
                "        <TextBlock Text='{Binding Name}' FontSize='13.5' Foreground='#1B2B42' VerticalAlignment='Center' TextTrimming='CharacterEllipsis'/>" +
                "      </StackPanel>" +
                "      <Border Grid.Column='2' HorizontalAlignment='Left' VerticalAlignment='Center' CornerRadius='9' Padding='9,2'" +
                "              Background='#ECF3FF' BorderBrush='#D8E7FF' BorderThickness='1'>" +
                "        <TextBlock Text='{Binding Arch}' FontSize='11.5' Foreground='#2A6CD8'/>" +
                "      </Border>" +
                "      <TextBlock Grid.Column='3' Text='{Binding InfoText}' FontFamily='Consolas' FontSize='12.5'" +
                "                 Foreground='#5A6B80' VerticalAlignment='Center' Margin='8,0,0,0'/>" +
                "      <StackPanel Grid.Column='5' Orientation='Horizontal' VerticalAlignment='Center'" +
                "                  Visibility='{Binding HasPercent}'>" +
                "        <ProgressBar Width='54' Height='5' Minimum='0' Maximum='100' Value='{Binding Percent}'" +
                "                     Foreground='#2A6CD8' Background='#E6ECF6' BorderThickness='0'" +
                "                     VerticalAlignment='Center'/>" +
                "        <TextBlock Text='{Binding PercentText}' FontSize='11' Foreground='#5A6B80'" +
                "                   Margin='7,0,0,0' VerticalAlignment='Center'/>" +
                "      </StackPanel>" +
                "      <Border Grid.Column='4' HorizontalAlignment='Left' VerticalAlignment='Center' CornerRadius='10' Padding='11,3'" +
                "              Background='#E9F8F0' BorderBrush='#CFEBD9' BorderThickness='1'>" +
                "        <Border.Style>" +
                "          <Style TargetType='Border'>" +
                "            <Style.Triggers>" +
                "              <DataTrigger Binding='{Binding IsMissing}' Value='True'>" +
                "                <Setter Property='Background' Value='#FDECEC'/>" +
                "                <Setter Property='BorderBrush' Value='#F5C7C7'/>" +
                "              </DataTrigger>" +
                "              <DataTrigger Binding='{Binding StateKey}' Value='downloading'>" +
                "                <Setter Property='Background' Value='#FFF3E2'/>" +
                "                <Setter Property='BorderBrush' Value='#F7DCB5'/>" +
                "              </DataTrigger>" +
                "              <DataTrigger Binding='{Binding StateKey}' Value='installing'>" +
                "                <Setter Property='Background' Value='#FFF3E2'/>" +
                "                <Setter Property='BorderBrush' Value='#F7DCB5'/>" +
                "              </DataTrigger>" +
                "            </Style.Triggers>" +
                "          </Style>" +
                "        </Border.Style>" +
                "        <TextBlock Text='{Binding StateText}' FontSize='12'>" +
                "          <TextBlock.Style>" +
                "            <Style TargetType='TextBlock'>" +
                "              <Setter Property='Foreground' Value='#189652'/>" +
                "              <Style.Triggers>" +
                "                <DataTrigger Binding='{Binding IsMissing}' Value='True'>" +
                "                  <Setter Property='Foreground' Value='#D13B3B'/>" +
                "                </DataTrigger>" +
                "                <DataTrigger Binding='{Binding StateKey}' Value='downloading'>" +
                "                  <Setter Property='Foreground' Value='#B26A00'/>" +
                "                </DataTrigger>" +
                "                <DataTrigger Binding='{Binding StateKey}' Value='installing'>" +
                "                  <Setter Property='Foreground' Value='#B26A00'/>" +
                "                </DataTrigger>" +
                "              </Style.Triggers>" +
                "            </Style>" +
                "          </TextBlock.Style>" +
                "        </TextBlock>" +
                "      </Border>" +
                "      <Border Grid.Column='6' HorizontalAlignment='Left' VerticalAlignment='Center' CornerRadius='9' Padding='9,3'" +
                "              BorderThickness='1' BorderBrush='#CFEBD9' Background='#E9F8F0'>" +
                "        <Border.Style>" +
                "          <Style TargetType='Border'>" +
                "            <Style.Triggers>" +
                "              <DataTrigger Binding='{Binding HashCovered}' Value='False'>" +
                "                <Setter Property='Background' Value='#FFF7E8'/>" +
                "                <Setter Property='BorderBrush' Value='#F0D9A8'/>" +
                "              </DataTrigger>" +
                "            </Style.Triggers>" +
                "          </Style>" +
                "        </Border.Style>" +
                "        <TextBlock Text='{Binding HashText}' FontSize='11'>" +
                "          <TextBlock.Style>" +
                "            <Style TargetType='TextBlock'>" +
                "              <Setter Property='Foreground' Value='#189652'/>" +
                "              <Style.Triggers>" +
                "                <DataTrigger Binding='{Binding HashCovered}' Value='False'>" +
                "                  <Setter Property='Foreground' Value='#B26A00'/>" +
                "                </DataTrigger>" +
                "              </Style.Triggers>" +
                "            </Style>" +
                "          </TextBlock.Style>" +
                "        </TextBlock>" +
                "      </Border>" +
                "    </Grid>" +
                "  </Border>" +
                "</DataTemplate>";
            return (DataTemplate)XamlReader.Parse(xaml);
        }

        private UIElement BuildLogPanel()
        {
            Border card = new Border();
            card.Margin = new Thickness(0, 14, 0, 0);
            card.CornerRadius = new CornerRadius(16);
            card.Background = new SolidColorBrush(Color.FromRgb(0x0F, 0x1B, 0x2D));
            card.Padding = new Thickness(16, 10, 16, 10);
            card.Height = 46;                     // 默认收起：一行高度
            _logCard = card;

            Grid g = new Grid();
            g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(24) });
            g.RowDefinitions.Add(new RowDefinition());

            Grid hd = new Grid();
            hd.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            hd.ColumnDefinitions.Add(new ColumnDefinition());
            hd.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            StackPanel hl = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            TextBlock gl = new TextBlock();
            gl.Text = "\uE756";
            gl.FontFamily = new FontFamily("Segoe MDL2 Assets");
            gl.FontSize = 12;
            gl.Foreground = new SolidColorBrush(Color.FromRgb(0x8F, 0xB4, 0xE8));
            gl.VerticalAlignment = VerticalAlignment.Center;
            gl.Margin = new Thickness(0, 0, 8, 0);
            TextBlock ht = new TextBlock();
            ht.Text = "执行日志";
            ht.FontSize = 12.5;
            ht.Foreground = new SolidColorBrush(Color.FromRgb(0xC9, 0xD8, 0xEA));
            ht.VerticalAlignment = VerticalAlignment.Center;
            hl.Children.Add(gl);
            hl.Children.Add(ht);
            hd.Children.Add(hl);

            // 收起时显示最近一条日志
            _logLast.Text = "";
            _logLast.FontFamily = new FontFamily("Consolas");
            _logLast.FontSize = 11.5;
            _logLast.Foreground = new SolidColorBrush(Color.FromRgb(0x9F, 0xB3, 0xCB));
            _logLast.VerticalAlignment = VerticalAlignment.Center;
            _logLast.Margin = new Thickness(14, 0, 8, 0);
            _logLast.TextTrimming = TextTrimming.CharacterEllipsis;
            Grid.SetColumn(_logLast, 1);
            hd.Children.Add(_logLast);

            StackPanel hr = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            _logToggle.Content = "展开";
            _logToggle.FontSize = 11.5;
            _logToggle.Foreground = new SolidColorBrush(Color.FromRgb(0xB9, 0xC8, 0xDA));
            _logToggle.Background = new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF));
            _logToggle.BorderBrush = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));
            _logToggle.BorderThickness = new Thickness(1);
            _logToggle.Padding = new Thickness(12, 3, 12, 3);
            _logToggle.Cursor = System.Windows.Input.Cursors.Hand;
            _logToggle.Click += delegate { ToggleLog(); };
            hr.Children.Add(_logToggle);

            Button clear = new Button();
            clear.Content = "清空";
            clear.FontSize = 11.5;
            clear.Margin = new Thickness(8, 0, 0, 0);
            clear.Foreground = new SolidColorBrush(Color.FromRgb(0xB9, 0xC8, 0xDA));
            clear.Background = new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF));
            clear.BorderBrush = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));
            clear.BorderThickness = new Thickness(1);
            clear.Padding = new Thickness(12, 3, 12, 3);
            clear.Cursor = System.Windows.Input.Cursors.Hand;
            clear.Click += delegate { _logBox.Clear(); _logLast.Text = ""; };
            hr.Children.Add(clear);

            Grid.SetColumn(hr, 2);
            hd.Children.Add(hr);
            g.Children.Add(hd);

            _logBox.Background = Brushes.Transparent;
            _logBox.Foreground = new SolidColorBrush(Color.FromRgb(0xB7, 0xC6, 0xDA));
            _logBox.BorderThickness = new Thickness(0);
            _logBox.FontFamily = new FontFamily("Consolas");
            _logBox.FontSize = 11.5;
            _logBox.IsReadOnly = true;
            _logBox.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            _logBox.Margin = new Thickness(0, 6, 0, 0);
            _logBox.Resources.Add(typeof(ScrollBar), BuildScrollBarStyle(true));
            _logBox.Visibility = Visibility.Collapsed;   // 默认收起
            Grid.SetRow(_logBox, 1);
            g.Children.Add(_logBox);

            card.Child = g;
            return card;
        }

        /// <summary>展开 / 收起执行日志</summary>
        private void ToggleLog()
        {
            _logExpanded = !_logExpanded;
            _logCard.Height = _logExpanded ? 176 : 46;
            _logBox.Visibility = _logExpanded ? Visibility.Visible : Visibility.Collapsed;
            _logLast.Visibility = _logExpanded ? Visibility.Collapsed : Visibility.Visible;
            _logToggle.Content = _logExpanded ? "收起" : "展开";
            if (_logExpanded) _logBox.ScrollToEnd();
        }

        // ── 扫描 ────────────────────────────────────────────────────────────
        private void BeginScan()
        {
            if (_busy) return;
            _busy = true;
            _items.Clear();
            _btnScan.IsEnabled = false;
            _btnRepair.IsEnabled = false;
            Report("正在加载目录…", 0, true);

            Thread t = new Thread(delegate()
            {
                bool wow64Off = false;
                try
                {
                    wow64Off = Wow64.Disable();
                    List<CatalogItem> raw = _loader();
                    List<CatalogItem> dedup = new List<CatalogItem>();
                    HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (CatalogItem it in raw)
                    {
                        // P0：原来按 Category|Title|FilePath 去重，把 VC_DLL 的 239 条折叠成 25 条。
                        // 现在只折叠"完全同一条"（文件名 + 下载地址 + 目标路径都相同）。
                        string key = it.Category + "|" + it.FileName + "|" + it.Download + "|" + it.FilePath;
                        if (seen.Add(key)) dedup.Add(it);
                    }

                    Dispatcher.BeginInvoke(new Action(delegate
                    {
                        foreach (CatalogItem it in dedup) _items.Add(it);
                        Report(string.Format("正在校验目录签名…（{0} 条）", dedup.Count), 0, true);
                    }));

                    // 目录信任：签名 manifest 完整性校验 + 逐条覆盖判定（页面据此决定能否修复）
                    CatalogTrustSet trust = CatalogTrustSet.LoadAll(dedup);
                    _trust = trust;

                    // 批量回填检测结果：每 100 条一次封送（原来每条一次，3324 次 BeginInvoke）
                    List<CatalogItem> pending = new List<CatalogItem>();
                    List<bool> pendingMiss = new List<bool>();
                    int done = 0, absent = 0;
                    foreach (CatalogItem it in dedup)
                    {
                        bool miss = Scanner.IsAbsent(it);
                        if (miss) absent++;
                        pending.Add(it);
                        pendingMiss.Add(miss);
                        done++;
                        if (pending.Count >= 100)
                        {
                            // 模型写入 + 进度合并成一次封送：后台线程不直接改绑定对象
                            List<CatalogItem> batch = new List<CatalogItem>(pending);
                            List<bool> batchMiss = new List<bool>(pendingMiss);
                            pending.Clear();
                            pendingMiss.Clear();
                            int d0 = done, a0 = absent, total0 = dedup.Count;
                            Dispatcher.BeginInvoke(new Action(delegate
                            {
                                for (int bi = 0; bi < batch.Count; bi++)
                                {
                                    batch[bi].State = batchMiss[bi] ? ItemState.Absent : ItemState.Present;
                                    batch[bi].Detail = batchMiss[bi] ? "缺失" : "已安装";
                                }
                                Report(string.Format("检测中 {0}/{1}，缺失 {2}", d0, total0, a0),
                                       total0 == 0 ? 0 : d0 * 100 / total0, false);
                            }));
                        }
                    }
                    // 末尾不足 100 条的尾批：同样封送，不靠后面的 ApplySort/Refresh 兜底
                    if (pending.Count > 0)
                    {
                        List<CatalogItem> tail = new List<CatalogItem>(pending);
                        List<bool> tailMiss = new List<bool>(pendingMiss);
                        pending.Clear();
                        pendingMiss.Clear();
                        Dispatcher.BeginInvoke(new Action(delegate
                        {
                            for (int bi = 0; bi < tail.Count; bi++)
                            {
                                tail[bi].State = tailMiss[bi] ? ItemState.Absent : ItemState.Present;
                                tail[bi].Detail = tailMiss[bi] ? "缺失" : "已安装";
                            }
                        }));
                    }

                    int totalItems = dedup.Count;
                    int absentFinal = absent;
                    Dispatcher.BeginInvoke(new Action(delegate
                    {
                        // 细节文案（源缺失 / 驱动 / 已安装版本）在 UI 线程统一回填
                        foreach (CatalogItem it in _items)
                        {
                            if (it.SourceDead) { it.Detail = "源缺失，暂不支持自动修复"; continue; }
                            if (it.IsDriver) { it.Detail = "驱动文件，不在修复范围"; continue; }
                            if (it.State != ItemState.Present) continue;
                            if (it.Kind == ItemKind.SystemDll) continue;
                            if (it.Version != null && it.Version != "—") continue;
                            string dv = string.IsNullOrEmpty(it.DisplayName) ? it.DisplayVersion : it.DisplayName;
                            string v = SystemProbe.FindInstalledVersion(dv);
                            if (!string.IsNullOrEmpty(v)) it.Version = v;
                        }
                        EnterBusy(false);
                        ApplySort();
                        UpdateCards();
                        ScanStore.Put(_page, new List<CatalogItem>(_items));
                        SetButtonText(_btnScan, "重新检查");
                        if (_placeholder != null)
                            _placeholder.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                        ApplyTrustUi();
                        string tail = trust.CanRepair ? "" : "（只读：目录未通过完整性校验）";
                        Report(string.Format("扫描完成：共 {0} 项，缺失/待修复 {1} 项{2}",
                            totalItems, absentFinal, tail), 100, false);
                    }));
                }
                catch (Exception ex)
                {
                    Log.Error("scan failed", ex);
                    Dispatcher.BeginInvoke(new Action(delegate
                    {
                        EnterBusy(false);
                        ApplySort();
                        Report("扫描失败：" + ex.Message, 0, false);
                    }));
                }
                finally
                {
                    if (wow64Off) Wow64.Enable();
                }
            });
            t.IsBackground = true;
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
        }

        private void UpdateCards()
        {
            int total = _items.Count;
            int missing = _items.Count(delegate(CatalogItem i) { return i.State == ItemState.Absent; });
            if (_cardTotal != null) _cardTotal.Text = total + " 项";
            if (_cardMissing != null) _cardMissing.Text = missing + " 项";
            if (_cardOk != null) _cardOk.Text = (total - missing) + " 项";
        }

        // ── 修复 / 下载 ─────────────────────────────────────────────────────
        private void BeginRepair()
        {
            if (_busy)
            {
                Dlg.Info("提示", "正在检测或处理中，请稍候…");
                return;
            }

            // 目录信任门禁（发布硬门禁）：manifest 未通过完整性校验 -> 只读，可扫不可修
            if (_trust != null && !_trust.CanRepair)
            {
                Dlg.Error("目录未通过完整性校验，已切换为只读",
                    "本次不执行修复。\n\n原因：" + _trust.Reason +
                    "\n\n目录可能被篡改、清单覆盖不全，或当前不是 HTTPS 连接。\n" +
                    "可以继续扫描查看状态；请稍后重试或联系发布方。");
                return;
            }

            // 只修复"确认为缺失"的项：正常/已修复/未检测一律不动；
            // 源缺失（download_status=dead*）与驱动文件不进修复队列（captain 定案）。
            List<CatalogItem> picked = _items.Where(delegate(CatalogItem i)
            {
                return i.Selected && i.State == ItemState.Absent && !i.SourceDead && !i.IsDriver;
            }).ToList();
            int deadCount = _items.Count(delegate(CatalogItem i)
            { return i.Selected && i.State == ItemState.Absent && i.SourceDead; });

            if (picked.Count == 0)
            {
                string extra = deadCount > 0
                    ? string.Format("另有 {0} 项「源缺失」，暂不支持自动修复。", deadCount) : "";
                Dlg.Info("提示", "没有可以自动修复的项：当前结果里没有可下载的缺失组件。\n" + extra);
                return;
            }

            int normalCount = _items.Count(delegate(CatalogItem i) { return i.State == ItemState.Present; });
            int uncovered = picked.Count(delegate(CatalogItem i) { return !i.HashCovered; });
            StringBuilder ask = new StringBuilder();
            ask.AppendFormat("将修复 {0} 项缺失组件，自动跳过 {1} 项正常组件。", picked.Count, normalCount);
            if (deadCount > 0)
                ask.AppendFormat("\n其中 {0} 项「源缺失」已跳过（暂不支持自动修复）。", deadCount);
            ask.Append("\n\n目录完整性：已通过签名校验（HTTPS）。");
            if (_trust != null) ask.Append("\n" + _trust.CoverageText);
            if (uncovered > 0)
                ask.AppendFormat("\n注意：{0} 项内容未做哈希校验（目录已签名、走 HTTPS）。", uncovered);
            ask.Append("\n\n继续？");
            if (!Dlg.Confirm("确认修复", ask.ToString())) return;

            if (!RepairEngineHolder.DryRun && !SystemProbe.IsElevated && picked.Any(delegate(CatalogItem i)
                { return i.Kind == ItemKind.SystemDll; }))
            {
                if (Dlg.Confirm("需要管理员权限",
                    "覆盖系统目录文件需要管理员权限。\n\n确定 = 以管理员身份重新启动\n取消 = 只处理不需要提权的项"))
                {
                    RelaunchElevated();
                    return;
                }
                picked = picked.Where(delegate(CatalogItem i) { return i.Kind != ItemKind.SystemDll; }).ToList();
                if (picked.Count == 0) return;
            }

            EnterBusy(true);
            _cts = new CancellationTokenSource();

            RepairEngine engine = new RepairEngine();
            engine.DryRun = RepairEngineHolder.DryRun;
            engine.DownloadOnly = _downloadOnly;
            engine.Trust = MultiTrustAdapter();
            // 所有模型写操作统一封送到 UI 线程（后台线程不直接碰绑定对象）
            engine.Post = delegate(Action a)
            {
                if (Dispatcher.CheckAccess()) a();
                else Dispatcher.Invoke(a);
            };

            engine.Overall += delegate(int d, int total)
            {
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    Report(string.Format("处理中 {0}/{1}", d, total),
                           total == 0 ? 0 : d * 100 / total, false);
                }));
            };
            engine.ItemProgress += delegate(CatalogItem item, int pct, string msg)
            {
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    item.Percent = pct;
                    Report(string.Format("{0}：{1}", item.Title, msg), pct, false);
                }));
            };

            CancellationToken token = _cts.Token;
            Thread t = new Thread(delegate()
            {
                try
                {
                    engine.Run(picked, token);
                    Dispatcher.BeginInvoke(new Action(delegate
                    {
                        EnterBusy(false);
                        ApplySort();
                        UpdateCards();
                        Report(string.Format("完成：成功 {0}，失败 {1}", engine.Success, engine.Failed), 100, false);
                        if (engine.Failed > 0)
                        {
                            // 把失败原因带回界面（原来只有"失败"两个字，用户无从诊断）
                            StringBuilder det = new StringBuilder();
                            int shown = 0;
                            foreach (CatalogItem it in picked)
                            {
                                if (it.State != ItemState.Failed) continue;
                                if (shown++ >= 5) break;
                                det.AppendLine("· " + it.Title + "：" + (it.Detail == null ? "未知原因" : it.Detail));
                            }
                            if (shown < engine.Failed) det.AppendLine("… 共 " + engine.Failed + " 项，见执行日志");
                            Dlg.Error(Str.RepairFailedTitle, Str.F(Str.RepairFailedBody, engine.Failed, det.ToString()));
                        }
                    }));
                }
                catch (Exception ex)
                {
                    Log.Error("repair thread crashed", ex);
                    Dispatcher.BeginInvoke(new Action(delegate
                    {
                        EnterBusy(false);
                        ApplySort();
                        Report("修复异常：" + ex.Message, 0, false);
                    }));
                }
            });
            t.IsBackground = true;
            t.Start();
        }

        private void RelaunchElevated()
        {
            try
            {
                string exe = System.Reflection.Assembly.GetEntryAssembly().Location;
                System.Diagnostics.ProcessStartInfo psi = new System.Diagnostics.ProcessStartInfo(exe);
                psi.UseShellExecute = true;
                psi.Verb = "runas";
                System.Diagnostics.Process.Start(psi);
                Application.Current.Shutdown();
            }
            catch (Exception ex)
            {
                Log.Error("elevate failed", ex);
                Dlg.Error("提权失败", ex.Message);
            }
        }

        private void OnLogLine(string line)
        {
            // 后台线程（Log.Write）会直接回调到这里：统一封送到 UI 线程，绝不跨线程碰控件
            if (line == null) return;
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(delegate { OnLogLine(line); }));
                return;
            }
            _logLast.Text = line.Trim();
            _logBox.AppendText(line + Environment.NewLine);
            _logBox.ScrollToEnd();
        }

        private void Report(string text, int percent, bool busy)
        {
            Action<string, int, bool> h = StatusChanged;
            if (h != null) h(text, percent, busy);
        }
    }
}
