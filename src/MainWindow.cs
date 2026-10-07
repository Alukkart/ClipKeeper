using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Ellipse = System.Windows.Shapes.Ellipse;

namespace DeviceGuard
{
    // The ClipKeeper main window: Library / Recording / Settings. Markup is ui/MainWindow.xaml; settings are in MainWindowSettings.cs,
    // sources are in MainWindowSources.cs, the library is in MainWindowClips.cs.
    partial class MainWindow
    {
        public readonly Window W;
        readonly TrayApp app;      // null in preview mode
        readonly Settings cfg;

        public const int PageClips = 0, PageRecord = 1, PageSettings = 2, PageStats = 3;

        readonly TileVm tObs = new TileVm("\uE703", "OBS"), tRb = new TileVm("\uE7C8", L.T("Replay buffer", "Буфер повтора")),
                        tAudio = new TileVm("\uE767", L.T("Audio", "Звук")), tScreen = new TileVm("\uE7F4", L.T("Screen", "Экран")),
                        tDisk = new TileVm("\uEDA2", L.T("Clip disk", "Диск для клипов")), tClip = new TileVm("\uE714", L.T("Last clip", "Последний клип"));
        readonly DispatcherTimer saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };

        readonly FrameworkElement[] pages;   // ScrollViewers, except Settings: a grid with its own scroll under fixed tabs
        readonly RadioButton[] navs;
        readonly Border heroStripe, problemsCard;
        readonly Ellipse heroDot, heroHalo, sideDot, sideHalo, navRecordDot;
        readonly TextBlock heroLabel, heroTitle, heroDetail, sideConn, sideSub, eventsEmpty;
        readonly ItemsControl problemsList, eventsList;
        TextBlock readyPath, collPath, obsPath;
        TextBlock connTitle, connSub, pwState;
        Ellipse connDot;
        TextBox host, port;
        PasswordBox pw;
        int lastLevel = -1;
        string lastEvents;
        Storyboard pulse;
        Snapshot last;

        T F<T>(string name) where T : class { return (T)W.FindName(name); }
        static Style S(string key) { return Wpf.Res<Style>(key); }

        public MainWindow(TrayApp app, Settings cfg)
        {
            this.app = app;
            this.cfg = cfg;
            W = (Window)Wpf.Load("MainWindow.xaml");

            pages = new[] { "PageClips", "PageRecord", "PageSettings", "PageStats" }.Select(n => F<FrameworkElement>(n)).ToArray();
            navs = new[] { "NavLibrary", "NavRecord", "NavSettings", "NavStats" }.Select(n => F<RadioButton>(n)).ToArray();
            for (int i = 0; i < navs.Length; i++)
            {
                int page = i;
                navs[i].Checked += (s, e) => ShowPage(page);
            }

            heroStripe = F<Border>("HeroStripe");
            problemsCard = F<Border>("ProblemsCard");
            heroDot = F<Ellipse>("HeroDot");
            heroHalo = F<Ellipse>("HeroHalo");
            F<Button>("BtnSourcesFold").Click += (s, e) => FoldSources(F<StackPanel>("SourcesBody").Visibility == Visibility.Visible);
            sideDot = F<Ellipse>("SideDot");
            sideHalo = F<Ellipse>("SideHalo");
            var sideStatus = F<Border>("SideStatus");
            sideStatus.MouseLeftButtonUp += (s, e) => { if (PageOn(PageRecord)) ShowPage(PageRecord); };
            sideStatus.MouseEnter += (s, e) => sideStatus.BorderBrush = Wpf.Res<Brush>("LineHi");
            sideStatus.MouseLeave += (s, e) => sideStatus.BorderBrush = Wpf.Res<Brush>("Line");
            navRecordDot = F<Ellipse>("NavRecordDot");
            heroLabel = F<TextBlock>("HeroLabel");
            heroTitle = F<TextBlock>("HeroTitle");
            heroDetail = F<TextBlock>("HeroDetail");
            sideConn = F<TextBlock>("SideConn");
            sideSub = F<TextBlock>("SideSub");
            eventsEmpty = F<TextBlock>("EventsEmpty");
            problemsList = F<ItemsControl>("ProblemsList");
            eventsList = F<ItemsControl>("EventsList");

            var tiles = F<ItemsControl>("Tiles");
            tClip.Click = () => ShowPage(PageClips);
            tAudio.Click = ShowSources;
            tScreen.Click = ShowSources;
            tObs.Click = () => ShowSettingsRow(TabObs, RowConnection);
            tRb.Click = () => ShowSettingsRow(TabReplay, RowReplayBuffer);
            tDisk.Click = () => ShowSettingsRow(TabChecks, RowDisk);
            problemsList.AddHandler(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, new RoutedEventHandler((s, e) =>
            {
                var fe = e.OriginalSource as FrameworkElement;
                var vm = fe != null ? fe.DataContext as EventVm : null;
                if (vm != null && vm.Link != null) vm.Link();
            }));
            tiles.ItemsSource = new[] { tObs, tRb, tAudio, tScreen, tDisk, tClip };
            tiles.MouseLeftButtonUp += (s, e) =>
            {
                var fe = e.OriginalSource as FrameworkElement;
                var vm = fe != null ? fe.DataContext as TileVm : null;
                if (vm != null && vm.Click != null) vm.Click();
            };

            F<Button>("BtnMin").Click += (s, e) => W.WindowState = WindowState.Minimized;
            F<Button>("BtnMax").Click += (s, e) => W.WindowState = W.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            F<Button>("BtnClose").Click += (s, e) => W.Close();   // Closing remembers the size and hides the window
            W.StateChanged += (s, e) =>
            {
                // a window without the system frame overflows the screen by the frame width when maximized — compensate
                bool max = W.WindowState == WindowState.Maximized;
                F<Grid>("Root").Margin = max ? new Thickness(7) : new Thickness(0);
                F<Button>("BtnMax").Content = max ? "" : "";
            };
            F<Button>("BtnCheck").Click += (s, e) => { if (app != null) app.Guard.RequestCheck(); };
            F<Button>("BtnSave").Click += (s, e) => SaveReference();
            F<Button>("BtnTest").Click += (s, e) => { if (app != null) app.TestAlert(); };
            ConfirmClick(F<Button>("BtnRestart"), L.T("Sure? Press again", "Точно? Нажми ещё раз"), () => { if (app != null) app.Guard.RequestRestartObs(); });
            F<Button>("BtnOpenLog").Click += (s, e) => Shell.Open(Log.FilePath);

            saveTimer.Tick += (s, e) => { saveTimer.Stop(); Save(); };
            W.Tag = Wpf.Capturable;
            W.SourceInitialized += (s, e) => { Wpf.ModernFrame(W); Wpf.SetCaptureHidden(W, cfg.HideFromCapture); };
            Wpf.RestoreBounds(W, cfg.MainBounds);
            if (W.WindowState == WindowState.Maximized)
            {
                F<Grid>("Root").Margin = new Thickness(7);
                F<Button>("BtnMax").Content = "";
            }
            W.Closing += (s, e) =>
            {
                RememberBounds();
                if (app != null && app.Exiting) return;
                e.Cancel = true;
                W.Hide();
            };

            InitClips();
            BuildSettings();
            InitSettingsKeys();
            for (int i = 0; i < navs.Length; i++) navs[i].Visibility = PageOn(i) ? Visibility.Visible : Visibility.Collapsed;
        }

        int currentPage = -1;
        int pageBeforeSettings = PageClips;   // where "back" on the settings page goes
        public int CurrentPage { get { return currentPage; } }

        public void ShowPage(int i)
        {
            if (triageOn) CloseTriage(false);   // another page: the going through ends, nothing undecided is lost
            if (!PageOn(i)) i = PageOn(PageClips) ? PageClips : PageOn(PageRecord) ? PageRecord : PageSettings;
            for (int k = 0; k < pages.Length; k++)
            {
                bool show = k == i;
                // the library remembers where you were; other pages open at the top
                if (show && k != currentPage && k != PageClips) ScrollOf(k).ScrollToTop();
                if (show && k != currentPage && k == PageSettings) ClearSettingsSearch();
                pages[k].Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            }
            if (i == PageSettings && currentPage >= 0 && currentPage != PageSettings) pageBeforeSettings = currentPage;
            // on the settings page the sidebar is the list of its sections, with a way back
            F<FrameworkElement>("MainSide").Visibility = i == PageSettings ? Visibility.Collapsed : Visibility.Visible;
            F<FrameworkElement>("SettingsSide").Visibility = i == PageSettings ? Visibility.Visible : Visibility.Collapsed;
            F<FrameworkElement>("SelBar").Visibility = i == PageClips && clips.Any(c => c.Selected) ? Visibility.Visible : Visibility.Collapsed;
            currentPage = i;
            if (navs[i].IsChecked != true) navs[i].IsChecked = true;
            if (i == PageClips) LoadClips(false);
            if (i == PageStats) LoadStats(false);
        }

        ScrollViewer ScrollOf(int page)
        {
            return (ScrollViewer)pages[page];
        }

        public void FocusPassword()
        {
            ShowSettingsRow(TabObs, RowPassword);
            pwState.Text = cfg.HasPassword
                ? L.T("The password did not work — enter it again and press \"Save and reconnect\"", "Пароль не подошёл — введи заново и нажми «Сохранить и переподключиться»")
                : L.T("Enter the password from the OBS WebSocket settings", "Введи пароль из настроек WebSocket в OBS");
            pwState.Foreground = Wpf.Br(Wpf.Bad, 255);
            W.Dispatcher.BeginInvoke(new Action(() => { pw.BringIntoView(); pw.Focus(); }), DispatcherPriority.Input);
        }

        // size and position are remembered when the window hides or the program closes
        public void RememberBounds()
        {
            string b = Wpf.SaveBounds(W);
            if (b == null || b == cfg.MainBounds) return;
            cfg.MainBounds = b;
            Save();
        }

        void SaveReference()
        {
            if (app == null) return;
            app.Guard.RequestSave();
            app.ShowToast(L.T("✓ Remembering the current OBS settings", "✓ Запоминаю текущие настройки OBS"));
        }

        // a double press instead of an "Are you sure?" popup
        static void ConfirmClick(Button b, string confirmText, Action act)
        {
            var label = b.Content as TextBlock ?? ((Panel)b.Content).Children.OfType<TextBlock>().Last();
            string normal = label.Text;
            var reset = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
            reset.Tick += (s, e) => { reset.Stop(); label.Text = normal; };
            b.Click += (s, e) =>
            {
                if (label.Text == normal) { label.Text = confirmText; reset.Start(); return; }
                reset.Stop();
                label.Text = normal;
                act();
            };
        }

        void Save()
        {
            try { cfg.Save(); } catch (Exception ex) { Log.Write("settings not saved: " + ex.Message); }
        }

        void SaveSoon() { saveTimer.Stop(); saveTimer.Start(); }

        // ── page building blocks ────────────────────────────────────────────
        static void Header(StackPanel p, string title, string sub)
        {
            p.Children.Add(new TextBlock { Text = title, Style = S("H1") });
            p.Children.Add(new TextBlock { Text = sub, Style = S("SubText"), Margin = new Thickness(0, 4, 0, 6) });
        }

        static TextBlock Section(string text)
        {
            return new TextBlock { Text = text.ToUpperInvariant(), Style = S("Caption") };
        }

        // a line of a settings card: icon, title, description, a control on the right. The line above it is drawn by Divide
        static Border Row(string glyph, string title, object desc, UIElement control)
        {
            var card = new Border { Padding = new Thickness(16, 13, 16, 13), BorderBrush = Wpf.Res<Brush>("Line") };
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.Children.Add(new TextBlock
            {
                Style = S("Icon"), Text = glyph, FontSize = 15, Foreground = Wpf.Res<Brush>("Muted"),
                Width = 22, VerticalAlignment = VerticalAlignment.Center,
            });
            var tx = new StackPanel { Margin = new Thickness(12, 0, 20, 0), VerticalAlignment = VerticalAlignment.Center };
            tx.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold });
            var d = desc as TextBlock;
            if (d == null && desc is string)
                d = new TextBlock { Text = (string)desc, Style = S("SubText"), FontSize = 12.5 };
            if (d != null) { d.Margin = new Thickness(0, 2, 0, 0); tx.Children.Add(d); }
            else if (desc is FrameworkElement) tx.Children.Add((FrameworkElement)desc);   // a description with controls in it
            Grid.SetColumn(tx, 1);
            g.Children.Add(tx);
            if (control != null)
            {
                var fe = control as FrameworkElement;
                if (fe != null) fe.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetColumn(control, 2);
                g.Children.Add(control);
            }
            card.Child = g;
            return card;
        }

        CheckBox Toggle(bool on, Action<bool> set)
        {
            var c = new CheckBox { Style = S("Toggle"), IsChecked = on };
            c.Checked += (s, e) => { set(true); Save(); };
            c.Unchecked += (s, e) => { set(false); Save(); };
            return c;
        }

        static Slider MakeSlider(double min, double max, double value, double step, double width)
        {
            return new Slider
            {
                Style = S("Slide"), Minimum = min, Maximum = max, Value = Math.Max(min, Math.Min(max, value)), Width = width,
                IsSnapToTickEnabled = true, TickFrequency = step, SmallChange = step, LargeChange = step,
            };
        }

        FrameworkElement SliderBox(int min, int max, int value, int step, Func<int, string> fmt, Action<int> set, UIElement extra)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            var sl = MakeSlider(min, max, value, step, 160);
            var lbl = new TextBlock
            {
                Text = fmt((int)sl.Value), Width = 58, Margin = new Thickness(10, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Right, Foreground = Wpf.Res<Brush>("Sub"),
            };
            sl.ValueChanged += (s, e) =>
            {
                int v = (int)Math.Round(sl.Value);
                lbl.Text = fmt(v);
                set(v);
                SaveSoon();
            };
            sp.Children.Add(sl);
            sp.Children.Add(lbl);
            if (extra != null) sp.Children.Add(extra);
            sp.Tag = sl;
            return sp;
        }

        static Button Btn(string glyph, string text, string style)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            if (glyph != null)
                sp.Children.Add(new TextBlock { Style = S("Icon"), Text = glyph, FontSize = 12, Margin = new Thickness(0, 0, text != null ? 8 : 0, 0) });
            if (text != null) sp.Children.Add(new TextBlock { Text = text });
            return new Button { Style = S(style), Content = sp };
        }

        // ── updates from the state snapshot (once a second while the window is open) ──
        public static string ShortDevice(string name)
        {
            if (string.IsNullOrEmpty(name)) return "?";
            var m = Regex.Match(name, @" - (.+?) \(");
            if (m.Success) return m.Groups[1].Value;
            return Regex.Replace(name, @"\s*\([^()]*\)\s*$", "");
        }

        static string Model(string monitor)
        {
            if (string.IsNullOrEmpty(monitor)) return "?";
            int i = monitor.IndexOf(':');
            return i > 0 ? monitor.Substring(0, i) : monitor;
        }

        static string When(DateTime t)
        {
            if (t == DateTime.MinValue) return "";
            return (t.Date == DateTime.Today ? L.T("today ", "сегодня ") : t.ToString("dd.MM ")) + t.ToString("HH:mm");
        }

        public void Update(Snapshot s)
        {
            last = s;
            UpdateSetup(s);
            var br = Wpf.Br(Wpf.LevelColor(s.Level), 255);
            heroStripe.Background = br;
            // recording is live — the dot is "REC" red and breathes
            heroDot.Fill = Recording(s) ? Wpf.Br(Wpf.Bad, 255) : br;
            Pulse(heroHalo, Recording(s));
            heroLabel.Foreground = br;
            heroLabel.Text = s.Level == 0 ? L.T("RUNNING", "РАБОТАЕТ") : s.Level == 1 ? L.T("ATTENTION", "ВНИМАНИЕ") : s.Level == 2 ? L.T("PROBLEM", "ПРОБЛЕМА") : L.T("WAITING", "ОЖИДАНИЕ");
            bool noLink = s.ObsStatus != null && s.ObsStatus.StartsWith(Guard.NoLinkPrefix);
            switch (s.Level)
            {
                case 0:
                    heroTitle.Text = L.T("All good", "Всё в порядке");
                    heroDetail.Text = L.T("Recording, devices in place", "Запись идёт, устройства на месте") +
                                      (s.LastCheck != DateTime.MinValue ? L.T(" · checked at ", " · проверка в ") + s.LastCheck.ToString("HH:mm:ss") : "");
                    break;
                case 1:
                    heroTitle.Text = L.T("Needs attention", "Нужно внимание");
                    heroDetail.Text = s.Pending.Count > 0 ? L.T("Glitch, waiting for recovery — details below", "Сбой, жду восстановления — подробности ниже") : L.T("Details below", "Подробности ниже");
                    break;
                case 2:
                    heroTitle.Text = L.T("Recording problem", "Проблема с записью");
                    heroDetail.Text = L.T("Details below. ClipKeeper is already trying to fix it", "Подробности ниже. ClipKeeper уже пытается починить");
                    break;
                default:
                    heroTitle.Text = noLink ? L.T("No connection to OBS", "Нет связи с OBS") : L.T("OBS is not running", "OBS не запущен");
                    heroDetail.Text = noLink ? s.ObsStatus.Substring(Guard.NoLinkPrefix.Length).Trim() : L.T("ClipKeeper is waiting for OBS to start", "ClipKeeper ждёт, когда OBS запустится");
                    break;
            }
            if (s.Level != lastLevel)
            {
                if (pulse != null) { pulse.Stop(); pulse = null; heroStripe.Opacity = 1; }
                if (s.Level == 2)
                {
                    var a = new DoubleAnimation(1, 0.2, TimeSpan.FromSeconds(0.7)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever };
                    Storyboard.SetTarget(a, heroStripe);
                    Storyboard.SetTargetProperty(a, new PropertyPath("Opacity"));
                    pulse = new Storyboard();
                    pulse.Children.Add(a);
                    pulse.Begin();
                }
                lastLevel = s.Level;
            }

            var probs = s.Due.Select((m, i) => Linked(EventVm.Problem(m, true), KeyAt(s.DueKeys, i)))
                         .Concat(s.Pending.Select((m, i) => Linked(EventVm.Problem(m, false), KeyAt(s.PendingKeys, i))))
                         .Concat(s.Notes.Select((m, i) => Linked(new EventVm { Text = EventVm.Cap(m), Glyph = "\uE946", GlyphBrush = Wpf.Br(Wpf.Warn, 255) }, KeyAt(s.NotesKeys, i))))
                         .ToList();
            problemsList.ItemsSource = probs;
            problemsCard.Visibility = probs.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

            UpdateTiles(s);
            UpdateSources(s);
            UpdateSourcesSummary(s);
            UpdateClipsHeader(s);
            UpdateFolderTexts();

            // a dot on "Recording" in the menu when something is wrong with recording (visible from any page)
            bool warn = s.Level == 1 || s.Level == 2;
            navRecordDot.Visibility = warn ? Visibility.Visible : Visibility.Collapsed;
            if (warn) navRecordDot.Fill = Wpf.Br(Wpf.LevelColor(s.Level), 255);

            var link = s.Connected ? Wpf.Ok : s.ProblemKeys.Contains("ws") || s.ProblemKeys.Contains("obs") ? Wpf.Bad : Wpf.Grey;
            // the buffer records — a "REC" dot that breathes, visible from any page
            bool rec = Recording(s);
            sideDot.Fill = Wpf.Br(rec ? Wpf.Bad : link, 255);
            Pulse(sideHalo, rec);
            sideConn.Text = rec ? L.T("Recording", "Идёт запись") : s.Connected ? L.T("OBS connected", "OBS подключён") : (s.ObsStatus ?? L.T("OBS is not running", "OBS не запущен"));
            sideSub.Text = rec ? L.T("replay buffer · OBS", "буфер повтора · OBS") : s.Connected ? s.Endpoint : "";
            F<Border>("SideStatus").Cursor = PageOn(PageRecord) ? System.Windows.Input.Cursors.Hand : null;
            if (connTitle != null)
            {
                connDot.Fill = Wpf.Br(link, 255);
                connTitle.Text = s.Connected ? L.T("Connected to OBS", "Подключено к OBS") : (s.ObsStatus ?? L.T("No connection", "Нет связи"));
                connSub.Text = s.Endpoint;
            }

            string joined = string.Join("\n", s.Events);
            if (joined != lastEvents)
            {
                lastEvents = joined;
                var ev = s.Events.Select(EventVm.Parse).ToList();
                eventsList.ItemsSource = ev;
                eventsEmpty.Visibility = ev.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        static bool Recording(Snapshot s) { return s.Connected && s.RbState == 1 && s.Level == 0; }

        // a halo that grows from a dot and fades — "recording is live"; off — gone
        static void Pulse(Ellipse halo, bool on)
        {
            if (on == Equals(halo.Tag, "on")) return;
            halo.Tag = on ? "on" : null;
            var sc = (ScaleTransform)halo.RenderTransform;
            if (!on)
            {
                halo.BeginAnimation(UIElement.OpacityProperty, null);
                sc.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                sc.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                halo.Opacity = 0;
                return;
            }
            var t = TimeSpan.FromSeconds(1.6);
            var grow = new DoubleAnimation(1, 2.8, t) { RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } };
            halo.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0.55, 0, t) { RepeatBehavior = RepeatBehavior.Forever });
            sc.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
            sc.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
        }

        static string KeyAt(List<string> keys, int i) { return i < keys.Count ? keys[i] : null; }

        void ShowSources()
        {
            FoldSources(false);
            F<Grid>("SourcesHeader").BringIntoView();
        }

        // the sources list is needed when something broke: folded while all is fine
        bool sourcesAutoOpened;

        void FoldSources(bool fold)
        {
            F<StackPanel>("SourcesBody").Visibility = fold ? Visibility.Collapsed : Visibility.Visible;
            F<TextBlock>("SourcesChevron").Text = fold ? "\uE76C" : "\uE70D";
        }

        void UpdateSourcesSummary(Snapshot s)
        {
            var bad = s.Refs.Where(r => HasKey(s, r.Key, "src:", "audio:", "screen:")).Select(r => r.Key).ToList();
            var sum = F<TextBlock>("SourcesSummary");
            if (s.Refs.Count == 0) sum.Text = L.T("the reference is empty", "эталон пуст");
            else if (bad.Count > 0) sum.Text = L.T("problem: ", "проблема: ") + string.Join(", ", bad);
            else sum.Text = L.N(s.Refs.Count, "source", "sources", "источник", "источника", "источников") + (s.Connected ? L.T(" · all fine", " · всё в норме") : "");
            sum.Foreground = bad.Count > 0 ? Wpf.Br(Wpf.Bad, 255) : Wpf.Res<Brush>("Muted");
            // opens by itself once per trouble; folding it back by hand is respected until the next one
            bool trouble = bad.Count > 0 || s.Refs.Count == 0 && s.Connected;
            if (trouble && !sourcesAutoOpened) FoldSources(false);
            sourcesAutoOpened = trouble;
        }

        // a problem → where it is dealt with: a device — its source card below; the rest — the setting that governs it
        EventVm Linked(EventVm vm, string key)
        {
            if (key == null) return vm;
            string settings = L.T("Settings", "Настройки");
            if (key.StartsWith("src:") || key.StartsWith("audio:") || key.StartsWith("screen:") || key == "rectracks")
            {
                vm.LinkText = L.T("Source", "Источник");
                vm.Link = ShowSources;
            }
            else if (key == "ws") { vm.LinkText = settings; vm.Link = () => ShowSettingsRow(TabObs, RowConnection); }
            else if (key == "obs" || key == "hang") { vm.LinkText = settings; vm.Link = () => ShowSettingsRow(TabObsApp, RowObsProgram); }
            else if (key == "rb") { vm.LinkText = settings; vm.Link = () => ShowSettingsRow(TabReplay, RowReplayBuffer); }
            else if (key == "disk") { vm.LinkText = settings; vm.Link = () => ShowSettingsRow(TabChecks, RowDisk); }
            else if (key == "perf") { vm.LinkText = settings; vm.Link = () => ShowSettingsRow(TabChecks, RowFrames); }
            return vm;
        }

        static bool HasKey(Snapshot s, string name, params string[] prefixes)
        {
            return prefixes.Any(p => s.ProblemKeys.Contains(p + name));
        }

        void UpdateTiles(Snapshot s)
        {
            // connected: how many frames OBS delivers and whether it drops any
            if (s.Connected && s.PerfLevel >= 0) tObs.Show(s.PerfLevel, s.PerfValue, s.PerfSub);
            else if (s.Connected) tObs.Show(0, L.T("Connected", "Подключён"), s.Endpoint);
            else
            {
                bool bad = s.ProblemKeys.Contains("obs") || s.ProblemKeys.Contains("ws") || s.ProblemKeys.Contains("hang");
                bool noLink = s.ObsStatus != null && s.ObsStatus.StartsWith(Guard.NoLinkPrefix);
                tObs.Show(bad ? 2 : 3, noLink ? L.T("No connection", "Нет связи") : L.T("Not running", "Не запущен"), s.ObsStatus ?? "");
            }

            if (s.ProblemKeys.Contains("rb")) tRb.Show(2, L.T("Failure", "Сбой"), L.T("details above", "подробности выше"));
            else switch (s.RbState)
            {
                case -2: tRb.Show(3, L.T("Not used", "Не используется"), L.T("regular recording", "обычная запись")); break;
                case 1: tRb.Show(0, L.T("Recording", "Записывает"), s.RbInfo ?? ""); break;
                case 0: tRb.Show(1, L.T("Off", "Выключен"), L.T("stopped by hand", "остановлен вручную")); break;
                case 2: tRb.Show(2, L.T("Stopped", "Остановился"), L.T("error — restarting", "ошибка — перезапускаю")); break;
                default: tRb.Show(3, s.Connected ? L.T("Not set up", "Не настроен") : "—", s.Connected ? L.T("enable the buffer in OBS output settings", "включи буфер в настройках вывода OBS") : L.T("OBS is not connected", "OBS не подключён")); break;
            }

            var audio = s.Refs.Where(r => Matcher.IsAudio(r.Value.Type)).ToList();
            var audioBad = audio.Where(r => HasKey(s, r.Key, "src:", "audio:")).Select(r => r.Key).ToList();
            var audioWarn = audio.Where(r => s.NoteKeys.Contains("mute:" + r.Key) || s.NoteKeys.Contains("vol:" + r.Key)).Select(r => r.Key).ToList();
            string names = string.Join(" · ", audio.Select(r => ShortDevice(r.Value.LastName ?? r.Value.Display)));
            if (audio.Count == 0) tAudio.Show(3, L.T("No reference", "Нет эталона"), L.T("press \"Remember devices\"", "нажми «Запомнить устройства»"));
            else if (audioBad.Count > 0) tAudio.Show(2, L.T("Problem", "Проблема"), string.Join(", ", audioBad));
            else if (audioWarn.Count > 0) tAudio.Show(1, L.T("Attention", "Внимание"), string.Join(", ", audioWarn));
            else if (!s.Connected) tAudio.Show(3, "—", names);
            else tAudio.Show(0, L.T(L.N(audio.Count, "source", "sources", "", "", "") + " OK", L.N(audio.Count, "", "", "источник", "источника", "источников") + " в норме"), names);

            var mons = s.Refs.Where(r => Matcher.IsMonitor(r.Value.Type)).ToList();
            string model = mons.Count > 0 ? Model(mons[0].Value.LastName ?? mons[0].Value.Display) : "";
            if (mons.Count == 0) tScreen.Show(3, L.T("No reference", "Нет эталона"), L.T("press \"Remember devices\"", "нажми «Запомнить устройства»"));
            else if (mons.Any(r => HasKey(s, r.Key, "src:", "screen:"))) tScreen.Show(2, L.T("Capture failure", "Сбой захвата"), model);
            else if (!s.Connected) tScreen.Show(3, "—", model);
            else tScreen.Show(0, L.T("Picture OK", "Картинка есть"), model);

            if (s.DiskFreeGb < 0) tDisk.Show(3, "—", L.T("no data", "нет данных"));
            else
            {
                int lvl = s.ProblemKeys.Contains("disk") ? 2 : s.DiskFreeGb < cfg.MinFreeGB * 2 ? 1 : 0;
                tDisk.Show(lvl, s.DiskFreeGb.ToString("0") + L.T(" GB free", " ГБ свободно"),
                           (s.DiskRoot ?? "") + (cfg.MinFreeGB > 0 ? L.T(" · threshold ", " · порог ") + cfg.MinFreeGB + L.T(" GB", " ГБ") : ""));
            }

            // last clip: which folder it went to (sorting puts it into the game's folder)
            string where = null;
            if (s.LastClipPath != null)
            {
                string root = RootOf(s);
                string dir = Path.GetDirectoryName(s.LastClipPath);
                where = root != null && dir.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                    ? dir.Substring(root.Length).TrimStart('\\') : dir;
                if (where == "") where = Path.GetFileName(dir);
            }
            if (s.ClipText == null && s.LastClipPath == null) tClip.Show(3, L.T("None yet", "Ещё не было"), L.T("open the clip gallery", "открыть галерею клипов"));
            else tClip.Show(s.ClipText == null || s.ClipOk ? 0 : 1, s.ClipText ?? L.T("Saved", "Сохранён"),
                            (where != null ? where + " · " : "") + When(s.ClipAt));
        }
    }
}
