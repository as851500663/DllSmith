// ============================================================================
//  DllSmith — 全面体检（分类总览）
//  文件：Overview.cs
//  目的：不再一进页面就全量扫描。按分类（运行库 / DirectX / 系统 DLL / 系统文件 /
//        C 盘清理）分块展示，点哪个「检查」就只查那一类；结果就地展开。
// ============================================================================
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Shapes;
using DllFix.Core;

namespace DllFix.Ui
{
    public class OverviewPage : UserControl, IProgressHost
    {
        // 单个分类
        private class Section
        {
            public string Title;
            public string Glyph;
            public string Desc;
            public Func<List<CatalogItem>> Loader;   // null = C 盘清理（特殊处理）
            public int PageIndex = -1;               // 「详情」跳转的页面序号

            public Border Card;
            public TextBlock Status;
            public Button CheckBtn;
            public Button GoBtn;
            public ListBox List;
            public bool Busy;
            public int Missing;
            public int Total;
            public long CleanBytes;
        }

        private readonly List<Section> _sections = new List<Section>();
        private readonly Button _all = new Button();
        private bool _busyAll;

        public event Action<string, int, bool> StatusChanged;
        public event Action<int> Navigate;

        public OverviewPage()
        {
            Build();
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
        }

        // ── 布局 ────────────────────────────────────────────────────────────
        private void Build()
        {
            Grid root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition());

            // 页头
            Grid head = new Grid();
            head.ColumnDefinitions.Add(new ColumnDefinition());
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            StackPanel hl = new StackPanel { Orientation = Orientation.Horizontal };
            Border tile = new Border();
            tile.Width = 42; tile.Height = 42;
            tile.CornerRadius = new CornerRadius(12);
            tile.Background = Theme.PrimarySoft;
            TextBlock tg = new TextBlock();
            tg.Text = "\uE80F";
            tg.FontFamily = new FontFamily("Segoe MDL2 Assets");
            tg.FontSize = 19;
            tg.Foreground = Brushes.White;
            tg.HorizontalAlignment = HorizontalAlignment.Center;
            tg.VerticalAlignment = VerticalAlignment.Center;
            tile.Child = tg;

            StackPanel ht = new StackPanel { Margin = new Thickness(14, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            TextBlock h1 = new TextBlock();
            h1.Text = "全面体检";
            h1.FontSize = 21;
            h1.FontWeight = FontWeights.SemiBold;
            h1.Foreground = Theme.TextStrong;
            TextBlock h2 = new TextBlock();
            h2.Text = "按分类检查：点哪一类的「检查」就只扫这一类，避免一进页面就全量扫描。";
            h2.FontSize = 12.5;
            h2.Margin = new Thickness(0, 6, 0, 0);
            h2.Foreground = Theme.TextMuted;
            ht.Children.Add(h1);
            ht.Children.Add(h2);
            hl.Children.Add(tile);
            hl.Children.Add(ht);
            head.Children.Add(hl);

            StyleBtn(_all, "全部检查", true);
            _all.Height = 36;
            _all.Margin = new Thickness(0, 0, 2, 0);
            _all.Click += delegate { CheckAll(); };
            Grid.SetColumn(_all, 1);
            head.Children.Add(_all);
            head.Margin = new Thickness(0, 0, 0, 14);
            root.Children.Add(head);

            // 分类列表
            ScrollViewer sv = new ScrollViewer();
            sv.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            sv.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
            StackPanel host = new StackPanel();
            sv.Content = host;

            AddSection(host, "运行库", "\uE74C",
                "Microsoft Visual C++ 2005–2022 与 .NET 运行库", 1,
                delegate
                {
                    List<CatalogItem> l = Catalog.LoadRuntime("运行库", "VC_Scan", Environment.Is64BitOperatingSystem);
                    l.AddRange(Catalog.LoadRuntime("运行库", "VC_DLL_Scan", Environment.Is64BitOperatingSystem));
                    l.AddRange(Catalog.LoadRuntime("运行库", "NETWORK", Environment.Is64BitOperatingSystem));
                    return l;
                });

            AddSection(host, "DirectX", "\uE7FC",
                "DirectX 组件与 d3dcompiler 系列", 2,
                delegate { return Catalog.LoadRuntime("DirectX", "DirectX", Environment.Is64BitOperatingSystem); });

            AddSection(host, "系统 DLL", "\uE8A5",
                "System32 下常见系统组件完整性", 3,
                delegate { return Catalog.LoadSystemDll(Environment.Is64BitOperatingSystem); });

            AddSection(host, "系统文件", "\uE896",
                "DownLevel API 集（api-ms-win-*）", 4,
                delegate { return Catalog.LoadRuntime("DownLevel", "downlevel", Environment.Is64BitOperatingSystem); });

            AddSection(host, "C 盘清理", "\uEDA2",
                "临时文件、更新缓存、日志、回收站等可释放空间", 5, null);

            Grid.SetRow(sv, 1);
            root.Children.Add(sv);

            Content = root;
        }

        private void AddSection(StackPanel host, string title, string glyph, string desc, int pageIndex,
                                 Func<List<CatalogItem>> loader)
        {
            Section s = new Section();
            s.Title = title;
            s.Glyph = glyph;
            s.Desc = desc;
            s.Loader = loader;
            s.PageIndex = pageIndex;

            Border card = new Border();
            card.Background = Theme.Card;
            card.CornerRadius = new CornerRadius(18);
            card.BorderBrush = Theme.CardLine;
            card.BorderThickness = new Thickness(1);
            card.Padding = new Thickness(18, 14, 18, 14);
            card.Margin = new Thickness(0, 0, 0, 12);
            s.Card = card;

            StackPanel sp = new StackPanel();

            Grid row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            Border g = new Border();
            g.Width = 34; g.Height = 34;
            g.CornerRadius = new CornerRadius(10);
            g.Background = new SolidColorBrush(Color.FromRgb(0xEA, 0xF2, 0xFF));
            TextBlock gi = new TextBlock();
            gi.Text = glyph;
            gi.FontFamily = new FontFamily("Segoe MDL2 Assets");
            gi.FontSize = 15;
            gi.Foreground = Theme.Primary;
            gi.HorizontalAlignment = HorizontalAlignment.Center;
            gi.VerticalAlignment = VerticalAlignment.Center;
            g.Child = gi;
            row.Children.Add(g);

            StackPanel mid = new StackPanel { Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            TextBlock t = new TextBlock();
            t.Text = title;
            t.FontSize = 14.5;
            t.FontWeight = FontWeights.SemiBold;
            t.Foreground = Theme.TextStrong;
            TextBlock d = new TextBlock();
            d.Text = desc;
            d.FontSize = 12;
            d.Margin = new Thickness(0, 4, 0, 0);
            d.Foreground = Theme.TextMuted;
            mid.Children.Add(t);
            mid.Children.Add(d);
            Grid.SetColumn(mid, 1);
            row.Children.Add(mid);

            StackPanel right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            s.Status = new TextBlock();
            s.Status.Text = "未检查";
            s.Status.FontSize = 12.5;
            s.Status.VerticalAlignment = VerticalAlignment.Center;
            s.Status.Margin = new Thickness(0, 0, 12, 0);
            s.Status.Foreground = Theme.TextMuted;

            s.CheckBtn = new Button();
            StyleBtn(s.CheckBtn, loader == null ? "检查空间" : "检查", false);
            s.CheckBtn.Height = 32;
            s.CheckBtn.MinWidth = 84;
            s.CheckBtn.Click += delegate { CheckSection(s); };

            s.GoBtn = new Button();
            StyleBtn(s.GoBtn, loader == null ? "去清理" : "详情", false);
            s.GoBtn.Height = 32;
            s.GoBtn.MinWidth = 72;
            s.GoBtn.Margin = new Thickness(8, 0, 0, 0);
            s.GoBtn.Click += delegate
            {
                Action<int> n = Navigate;
                if (n != null) n(s.PageIndex);
            };

            right.Children.Add(s.Status);
            right.Children.Add(s.CheckBtn);
            right.Children.Add(s.GoBtn);
            Grid.SetColumn(right, 2);
            row.Children.Add(right);

            sp.Children.Add(row);

            s.List = new ListBox();
            s.List.BorderThickness = new Thickness(0);
            s.List.Background = Brushes.White;
            s.List.Margin = new Thickness(0, 10, 0, 0);
            s.List.Height = 0;
            s.List.Visibility = Visibility.Collapsed;
            s.List.HorizontalContentAlignment = HorizontalAlignment.Stretch;
            s.List.ItemTemplate = RowTemplate();
            s.List.ItemContainerStyle = RowStyle();
            s.List.Resources.Add(typeof(System.Windows.Controls.Primitives.ScrollBar), ScrollBarStyle());
            sp.Children.Add(s.List);

            card.Child = sp;
            host.Children.Add(card);
            _sections.Add(s);
        }

        private void StyleBtn(Button b, string text, bool primary)
        {
            b.Content = text;
            b.Padding = new Thickness(14, 0, 14, 0);
            b.FontSize = 12.5;
            b.FontWeight = FontWeights.SemiBold;
            b.Cursor = System.Windows.Input.Cursors.Hand;
            b.BorderThickness = new Thickness(primary ? 0 : 1);
            b.BorderBrush = Theme.Primary;
            b.Foreground = primary ? Brushes.White : Theme.Primary;
            b.Background = primary ? Theme.PrimarySoft : Brushes.White;
        }

        private DataTemplate RowTemplate()
        {
            string xaml = @"
<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'>
  <Grid Height='34' Background='Transparent'>
    <Grid.ColumnDefinitions>
      <ColumnDefinition Width='*'/><ColumnDefinition Width='80'/><ColumnDefinition Width='90'/>
    </Grid.ColumnDefinitions>
    <TextBlock Text='{Binding Name}' FontSize='12.5' Foreground='#22344F' VerticalAlignment='Center'
               TextTrimming='CharacterEllipsis'/>
    <TextBlock Grid.Column='1' Text='{Binding Arch}' FontSize='11.5' Foreground='#8B9BB0'
               VerticalAlignment='Center'/>
    <TextBlock Grid.Column='2' Text='{Binding StateText}' FontSize='11.5' VerticalAlignment='Center'/>
  </Grid>
</DataTemplate>";
            return (DataTemplate)XamlReader.Parse(xaml);
        }

        private Style RowStyle()
        {
            Style s = new Style(typeof(ListBoxItem));
            s.Setters.Add(new Setter(ListBoxItem.PaddingProperty, new Thickness(0)));
            s.Setters.Add(new Setter(ListBoxItem.MarginProperty, new Thickness(0)));
            s.Setters.Add(new Setter(ListBoxItem.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
            s.Setters.Add(new Setter(ListBoxItem.BackgroundProperty, Brushes.White));
            return s;
        }

        private Style ScrollBarStyle()
        {
            string xaml =
                "<Style xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' TargetType='ScrollBar'>" +
                "<Setter Property='Width' Value='8'/><Setter Property='Template'><Setter.Value>" +
                "<ControlTemplate TargetType='ScrollBar'><Grid Background='Transparent'><Track IsDirectionReversed='true'>" +
                "<Track.Thumb><Thumb><Thumb.Template><ControlTemplate TargetType='Thumb'>" +
                "<Border CornerRadius='4' Background='#C9D6E8' Margin='1,0'/></ControlTemplate></Thumb.Template></Thumb>" +
                "</Track.Thumb></Track></Grid></ControlTemplate></Setter.Value></Setter></Style>";
            return (Style)XamlReader.Parse(xaml);
        }

        // ── 检查 ────────────────────────────────────────────────────────────
        private void CheckAll()
        {
            if (_busyAll) return;
            _busyAll = true;
            _all.IsEnabled = false;
            Thread t = new Thread(delegate()
            {
                foreach (Section s in _sections)
                {
                    RunCheck(s);
                }
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    _busyAll = false;
                    _all.IsEnabled = true;
                    Report("全部检查完成", 0, false);
                }));
            });
            t.IsBackground = true;
            t.Start();
        }

        private void CheckSection(Section s)
        {
            if (s.Busy) return;
            Thread t = new Thread(delegate() { RunCheck(s); });
            t.IsBackground = true;
            t.Start();
        }

        private void RunCheck(Section s)
        {
            s.Busy = true;
            Dispatcher.BeginInvoke(new Action(delegate
            {
                s.Status.Text = "检查中…";
                s.Status.Foreground = Theme.Primary;
                s.CheckBtn.IsEnabled = false;
                Report("正在检查：" + s.Title, 0, true);
            }));

            try
            {
                if (s.Loader == null)
                {
                    // C 盘清理：只做空间统计，不删任何东西
                    List<CleanItem> caches = CleanEngine.ScanCaches(null);
                    long bytes = 0;
                    foreach (CleanItem it in caches) bytes += it.Bytes;
                    s.CleanBytes = bytes;
                    s.Missing = 0;
                    s.Total = caches.Count;

                    Dispatcher.BeginInvoke(new Action(delegate
                    {
                        s.Status.Text = "可释放 " + CleanEngine.FormatSize(bytes);
                        s.Status.Foreground = new SolidColorBrush(Color.FromRgb(0x1E, 0x9A, 0x5A));
                        s.List.ItemsSource = caches;
                        s.List.Visibility = Visibility.Visible;
                        s.List.Height = 150;
                        s.CheckBtn.IsEnabled = true;
                        s.Busy = false;
                    }));
                    return;
                }

                bool wow = Wow64.Disable();
                List<CatalogItem> items;
                try
                {
                    items = s.Loader();
                    Scanner.Scan(items, null);
                }
                finally { if (wow) Wow64.Enable(); }

                int missing = 0;
                List<CatalogItem> preview = new List<CatalogItem>();
                foreach (CatalogItem it in items)
                {
                    if (it.State == ItemState.Absent)
                    {
                        missing++;
                        if (preview.Count < 40) preview.Add(it);
                    }
                }
                if (preview.Count == 0)
                {
                    int take = 0;
                    foreach (CatalogItem it in items)
                    {
                        if (take++ >= 12) break;
                        preview.Add(it);
                    }
                }

                int total = items.Count;
                int miss = missing;
                List<CatalogItem> show = preview;

                Dispatcher.BeginInvoke(new Action(delegate
                {
                    s.Missing = miss;
                    s.Total = total;
                    if (miss > 0)
                    {
                        s.Status.Text = string.Format("共 {0} 项，缺失 {1} 项", total, miss);
                        s.Status.Foreground = new SolidColorBrush(Color.FromRgb(0xD0, 0x5A, 0x5A));
                    }
                    else
                    {
                        s.Status.Text = string.Format("共 {0} 项，全部正常", total);
                        s.Status.Foreground = new SolidColorBrush(Color.FromRgb(0x1E, 0x9A, 0x5A));
                    }
                    s.List.ItemsSource = show;
                    s.List.Visibility = Visibility.Visible;
                    s.List.Height = 150;
                    s.CheckBtn.IsEnabled = true;
                    s.Busy = false;
                    Report(s.Title + " 检查完成", 0, false);
                }));
            }
            catch (Exception ex)
            {
                Log.Error("overview check failed: " + s.Title, ex);
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    s.Status.Text = "检查失败";
                    s.Status.Foreground = new SolidColorBrush(Color.FromRgb(0xD0, 0x5A, 0x5A));
                    s.CheckBtn.IsEnabled = true;
                    s.Busy = false;
                }));
            }
        }
    }
}
