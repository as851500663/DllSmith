// ============================================================================
//  DllSmith — 统一风格对话框（替代系统 MessageBox）
//  文件：Dialog.cs
// ============================================================================
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace DllFix.Ui
{
    public static class Dlg
    {
        public static void Info(string title, string message)
        {
            Show(title, message, 0, false);
        }

        public static void Error(string title, string message)
        {
            Show(title, message, 2, false);
        }

        public static bool Confirm(string title, string message)
        {
            return Show(title, message, 1, true, "确定", "取消");
        }

        /// <summary>
        /// 双出口选择（强制更新用）：只有 primary / cancel 两个按钮，关闭窗口 = cancel。
        /// 返回 true 表示用户选择了 primary。
        /// </summary>
        public static bool Choose(string title, string message, string primaryText, string cancelText)
        {
            return Show(title, message, 1, true, primaryText, cancelText);
        }

        private static bool Show(string title, string message, int kind, bool cancel)
        {
            return Show(title, message, kind, cancel, "确定", "取消");
        }

        private static bool Show(string title, string message, int kind, bool cancel,
                                 string primaryText, string cancelText)
        {
            Window owner = Application.Current != null ? Application.Current.MainWindow : null;
            Window w = new Window();
            w.WindowStyle = WindowStyle.None;
            w.AllowsTransparency = true;          // 临时小窗：弹出即关，渲染代价可忽略
            w.Background = Brushes.Transparent;
            w.ResizeMode = ResizeMode.NoResize;
            w.SizeToContent = SizeToContent.Height;
            w.Width = 468;
            w.ShowInTaskbar = false;
            w.WindowStartupLocation = owner != null
                ? WindowStartupLocation.CenterOwner
                : WindowStartupLocation.CenterScreen;
            if (owner != null && owner.IsVisible) w.Owner = owner;

            Border card = new Border();
            card.Background = Brushes.White;
            card.CornerRadius = new CornerRadius(18);
            card.BorderBrush = new SolidColorBrush(Color.FromRgb(0xE2, 0xEA, 0xF7));
            card.BorderThickness = new Thickness(1);
            card.Padding = new Thickness(26, 22, 26, 18);
            card.Effect = new DropShadowEffect
            {
                BlurRadius = 28,
                ShadowDepth = 6,
                Direction = 270,
                Opacity = 0.16,
                Color = Color.FromRgb(0x1F, 0x2F, 0x4A)
            };
            card.MouseLeftButtonDown += delegate { try { w.DragMove(); } catch { } };

            StackPanel root = new StackPanel();

            // 图标 + 标题
            Grid head = new Grid();
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            head.ColumnDefinitions.Add(new ColumnDefinition());

            Color accent = kind == 2 ? Color.FromRgb(0xD0, 0x4A, 0x4A)
                          : kind == 1 ? Color.FromRgb(0xE0, 0x8A, 0x1F)
                          : Color.FromRgb(0x1F, 0x6F, 0xE0);
            Color bg = kind == 2 ? Color.FromRgb(0xFD, 0xEC, 0xEC)
                      : kind == 1 ? Color.FromRgb(0xFF, 0xF3, 0xE2)
                      : Color.FromRgb(0xEA, 0xF2, 0xFF);

            Border icon = new Border();
            icon.Width = 38;
            icon.Height = 38;
            icon.CornerRadius = new CornerRadius(11);
            icon.Background = new SolidColorBrush(bg);
            TextBlock gi = new TextBlock();
            gi.Text = kind == 2 ? "\uEA39" : (kind == 1 ? "\uE7BA" : "\uE946");
            gi.FontFamily = new FontFamily("Segoe MDL2 Assets");
            gi.FontSize = 17;
            gi.Foreground = new SolidColorBrush(accent);
            gi.HorizontalAlignment = HorizontalAlignment.Center;
            gi.VerticalAlignment = VerticalAlignment.Center;
            icon.Child = gi;
            head.Children.Add(icon);

            TextBlock t = new TextBlock();
            t.Text = title;
            t.FontSize = 15.5;
            t.FontWeight = FontWeights.SemiBold;
            t.Foreground = new SolidColorBrush(Color.FromRgb(0x12, 0x23, 0x3D));
            t.VerticalAlignment = VerticalAlignment.Center;
            t.Margin = new Thickness(12, 0, 0, 0);
            t.TextWrapping = TextWrapping.Wrap;
            Grid.SetColumn(t, 1);
            head.Children.Add(t);
            root.Children.Add(head);

            // 正文
            TextBlock body = new TextBlock();
            body.Text = message;
            body.FontSize = 12.8;
            body.LineHeight = 22;
            body.Foreground = new SolidColorBrush(Color.FromRgb(0x5A, 0x6B, 0x82));
            body.TextWrapping = TextWrapping.Wrap;
            body.Margin = new Thickness(0, 14, 0, 0);
            body.MaxHeight = 300;

            ScrollViewer sv = new ScrollViewer();
            sv.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            sv.Content = body;
            sv.Margin = new Thickness(0);
            root.Children.Add(sv);

            // 按钮
            StackPanel btns = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 20, 0, 0)
            };

            if (cancel)
            {
                Button no = MakeButton(cancelText, false);
                no.Click += delegate { w.DialogResult = false; w.Close(); };
                btns.Children.Add(no);
                no.IsCancel = true;
            }

            Button ok = MakeButton(cancel ? primaryText : "我知道了", true);
            ok.Click += delegate
            {
                w.DialogResult = cancel ? (bool?)true : null;
                w.Close();
            };
            btns.Children.Add(ok);
            root.Children.Add(btns);

            card.Child = root;
            w.Content = card;
            w.Loaded += delegate { ok.Focus(); };

            bool? r = w.ShowDialog();
            return r == true;
        }

        private static Button MakeButton(string text, bool primary)
        {
            Button b = new Button();
            b.Content = text;
            b.Height = 36;
            b.MinWidth = 96;
            b.Margin = new Thickness(10, 0, 0, 0);
            b.Padding = new Thickness(18, 0, 18, 0);
            b.FontSize = 13;
            b.FontWeight = FontWeights.SemiBold;
            b.Cursor = Cursors.Hand;
            b.BorderThickness = new Thickness(primary ? 0 : 1);
            b.BorderBrush = new SolidColorBrush(Color.FromRgb(0xD6, 0xE3, 0xF7));
            b.Foreground = primary ? Brushes.White : new SolidColorBrush(Color.FromRgb(0x1F, 0x6F, 0xE0));
            b.Background = primary
                ? (Brush)new LinearGradientBrush(Color.FromRgb(0x42, 0x8E, 0xF8), Color.FromRgb(0x1F, 0x6F, 0xE0), 90)
                : Brushes.White;

            string xaml =
                "<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' TargetType='Button'>" +
                "<Border CornerRadius='10' Background='{TemplateBinding Background}'" +
                "        BorderBrush='{TemplateBinding BorderBrush}' BorderThickness='{TemplateBinding BorderThickness}'>" +
                "<ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center'/></Border></ControlTemplate>";
            b.Template = (ControlTemplate)XamlReader.Parse(xaml);
            return b;
        }
    }
}
