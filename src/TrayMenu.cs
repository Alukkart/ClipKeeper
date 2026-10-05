using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using Ellipse = System.Windows.Shapes.Ellipse;

namespace DeviceGuard
{
    // The tray icon menu in the ClipKeeper window style: a dark card, Fluent icons, recording state on top
    class TrayMenu : Window
    {
        const double Pad = 22;   // margin around the card for the shadow

        readonly TrayApp app;
        readonly Ellipse dot = new Ellipse { Width = 7, Height = 7, VerticalAlignment = VerticalAlignment.Center };
        readonly TextBlock status = new TextBlock();
        readonly DispatcherTimer outside = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        TextBlock restartText;
        bool confirmRestart;

        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hwnd);
        [DllImport("user32.dll")] static extern short GetAsyncKeyState(int key);

        public TrayMenu(TrayApp app)
        {
            this.app = app;
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            Topmost = true;
            ShowInTaskbar = false;
            ResizeMode = ResizeMode.NoResize;
            SizeToContent = SizeToContent.WidthAndHeight;
            FontFamily = Wpf.Res<FontFamily>("UiFont");
            Foreground = Wpf.Res<Brush>("Text");
            UseLayoutRounding = true;
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
            SourceInitialized += (s, e) => Wpf.ExcludeFromCapture(this);
            Content = Build();

            // close: a click outside, Esc, switching to another window
            Deactivated += (s, e) => Close2();
            PreviewKeyDown += (s, e) => { if (e.Key == Key.Escape) Close2(); };
            // fallback if Windows did not give the menu focus: a mouse press outside the card
            outside.Tick += (s, e) =>
            {
                bool down = (GetAsyncKeyState(0x01) & 0x8000) != 0 || (GetAsyncKeyState(0x02) & 0x8000) != 0;
                if (down && !IsMouseOver) Close2();
            };
        }

        UIElement Build()
        {
            var list = new StackPanel();

            // header: logo, name, status
            var head = new Grid { Margin = new Thickness(10, 8, 10, 10) };
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            head.ColumnDefinitions.Add(new ColumnDefinition());
            head.Children.Add(new Image { Source = Icons.RenderApp(64), Width = 30, Height = 30, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 1, 12, 0) });
            var info = new StackPanel();
            info.Children.Add(new TextBlock { Text = "ClipKeeper", FontFamily = Wpf.Res<FontFamily>("DisplayFont"), FontSize = 14.5, FontWeight = FontWeights.SemiBold });
            var line = new Grid { Margin = new Thickness(0, 3, 0, 0) };
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            line.ColumnDefinitions.Add(new ColumnDefinition());

            dot.VerticalAlignment = VerticalAlignment.Top;
            dot.Margin = new Thickness(0, 6, 8, 0);
            line.Children.Add(dot);
            status.FontSize = 12;
            status.Foreground = Wpf.Res<Brush>("Sub");
            status.TextWrapping = TextWrapping.Wrap;
            status.TextTrimming = TextTrimming.CharacterEllipsis;
            status.MaxHeight = 34;
            Grid.SetColumn(status, 1);
            line.Children.Add(status);
            info.Children.Add(line);
            Grid.SetColumn(info, 1);
            head.Children.Add(info);
            list.Children.Add(head);

            bool guardOn = app == null || app.Cfg.GuardEnabled, libraryOn = app == null || app.Cfg.LibraryEnabled;
            list.Children.Add(Sep());
            if (libraryOn) list.Children.Add(Item("", L.T("Library", "Библиотека"), () => app.ShowMain(false, MainWindow.PageClips)));
            if (guardOn) list.Children.Add(Item("", L.T("Recording", "Запись"), () => app.ShowMain(false, MainWindow.PageRecord)));
            list.Children.Add(Item("", L.T("Settings", "Настройки"), () => app.ShowMain(false, MainWindow.PageSettings)));
            // recording actions — only with the recording guard on
            if (guardOn)
            {
                list.Children.Add(Sep());
                list.Children.Add(Item("", L.T("Remember devices", "Запомнить устройства"), () => { app.Guard.RequestSave(); app.ShowToast(L.T("✓ Remembering current devices", "✓ Запоминаю текущие устройства")); }));
                list.Children.Add(Item("", L.T("Check now", "Проверить сейчас"), () => app.Guard.RequestCheck()));
                list.Children.Add(Item("", L.T("Test alarm", "Тест тревоги"), () => app.TestAlert()));

                // OBS restart on the second press, like in the window (no system "Are you sure?")
                var restart = Item("", L.T("Restart OBS", "Перезапустить OBS"), null);
                restartText = (TextBlock)((StackPanel)restart.Content).Children[1];
                restart.Click += (s, e) =>
                {
                    if (!confirmRestart)
                    {
                        confirmRestart = true;
                        restartText.Text = L.T("Press again — OBS will restart", "Нажми ещё раз — OBS перезапустится");
                        restartText.Foreground = Wpf.Br(Wpf.Bad, 255);
                        return;
                    }
                    Close2();
                    app.RequestRestartObs();
                };
                list.Children.Add(restart);
            }
            list.Children.Add(Sep());
            if (app != null && Updates.Newer != null)
                list.Children.Add(Item("\uE896", L.T("Update to ", "Обновить до ") + Updates.Newer.Version, () => app.ShowUpdates()));
            list.Children.Add(Item("", L.T("Exit", "Выход"), () => app.Exit()));

            var card = new Border
            {
                Width = 284, CornerRadius = new CornerRadius(10), Background = Wpf.Res<Brush>("Card"),
                BorderBrush = Wpf.Res<Brush>("LineHi"), BorderThickness = new Thickness(1), Padding = new Thickness(5),
                Effect = new DropShadowEffect { Color = Colors.Black, BlurRadius = 22, ShadowDepth = 4, Opacity = 0.55 },
                Child = list,
            };
            return new Grid { Margin = new Thickness(Pad), Children = { card } };
        }

        static Border Sep()
        {
            return new Border { Height = 1, Background = Wpf.Res<Brush>("Line"), Margin = new Thickness(6, 4, 6, 4) };
        }

        Button Item(string glyph, string text, Action act)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            sp.Children.Add(new TextBlock { Style = Wpf.Res<Style>("Icon"), Text = glyph, FontSize = 14, Width = 26, Foreground = Wpf.Res<Brush>("Sub"), VerticalAlignment = VerticalAlignment.Center });
            sp.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
            var b = new Button { Style = Wpf.Res<Style>("MenuRow"), Content = sp };
            if (act != null) b.Click += (s, e) => { Close2(); act(); };
            return b;
        }

        public void SetStatus(Snapshot s)
        {
            dot.Fill = Wpf.Br(Wpf.LevelColor(s.Level), 255);
            string first = s.Due.Concat(s.Pending).Concat(s.Notes).FirstOrDefault();
            switch (s.Level)
            {
                case 0: status.Text = L.T("All good", "Всё в порядке") + (s.PerfValue != null && s.PerfLevel >= 0 ? " · " + s.PerfValue : ""); break;
                case 1: status.Text = first != null ? EventVm.Cap(first) : (s.Status ?? "").TrimStart('✓', '⚠', ' '); break;
                case 2: status.Text = L.T("Problem: ", "Проблема: ") + (first ?? L.T("details in the window", "подробности в окне")); break;
                default: status.Text = s.ObsStatus ?? L.T("OBS is not running", "OBS не запущен"); break;
            }
            status.ToolTip = (s.Status ?? status.Text).TrimStart('✓', '⚠', ' ');
        }

        // at the cursor, above the taskbar; never past the edge of the work area
        public void Open(Snapshot s)
        {
            SetStatus(s);
            confirmRestart = false;
            if (restartText != null) restartText.Text = L.T("Restart OBS", "Перезапустить OBS");
            if (restartText != null) restartText.Foreground = Wpf.Res<Brush>("Text");

            var cur = System.Windows.Forms.Cursor.Position;   // physical pixels
            Left = -20000;
            Top = -20000;
            Show();
            UpdateLayout();
            var src = PresentationSource.FromVisual(this);
            var p = src != null ? src.CompositionTarget.TransformFromDevice.Transform(new Point(cur.X, cur.Y)) : new Point(cur.X, cur.Y);
            var wa = SystemParameters.WorkArea;
            double w = ActualWidth, h = ActualHeight;
            double x = p.X - w + Pad, y = p.Y - h + Pad;      // the tray is usually bottom right — the menu goes up and left of the cursor
            if (y < wa.Top - Pad) y = p.Y - Pad;              // taskbar on top — go down
            if (x < wa.Left - Pad) x = p.X - Pad;             // taskbar on the left — go right
            Left = Math.Max(wa.Left - Pad, Math.Min(x, wa.Right - w + Pad));
            Top = Math.Max(wa.Top - Pad, Math.Min(y, wa.Bottom - h + Pad));

            // focus is needed so a click outside closes the menu; clicking the tray icon gives the program that right
            try { SetForegroundWindow(new WindowInteropHelper(this).Handle); } catch { }
            Activate();
            outside.Start();
        }

        public void Close2()
        {
            outside.Stop();
            if (IsVisible) Hide();
        }

        // for previews: the card without a window
        public FrameworkElement PreviewContent(Snapshot s)
        {
            SetStatus(s);
            return (FrameworkElement)Content;
        }
    }
}
