using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Ellipse = System.Windows.Shapes.Ellipse;

namespace DeviceGuard
{
    // The first-run setup: a card over the main window that walks through language and features, the OBS link,
    // devices and folders. It shows until finished or skipped (settings.json: SetupDone) and can be run again
    // from Settings → Features.
    partial class MainWindow
    {
        Grid setup;
        int setupStep;
        List<Action<StackPanel>> setupSteps;
        Action<Snapshot> setupLive;   // the current step's live part (connection, remembered devices)

        public bool SetupVisible { get { return setup != null; } }

        public void ShowSetup()
        {
            if (setup != null) return;
            setup = new Grid { Background = Wpf.Br("#E60B0C0E"), Margin = new Thickness(0, 44, 0, 0) };
            Grid.SetColumnSpan(setup, 2);
            Panel.SetZIndex(setup, 50);
            F<Grid>("Root").Children.Add(setup);
            setupStep = 0;
            ShowSetupStep();
        }

        void CloseSetup(bool done)
        {
            if (setup == null) return;
            F<Grid>("Root").Children.Remove(setup);
            setup = null;
            setupLive = null;
            if (!cfg.SetupDone)
            {
                cfg.SetupDone = true;
                Save();
            }
            Log.Write(done ? "setup finished" : "setup skipped");
            cfg.Apply();
            SyncModules();
            RebuildSettings();
        }

        // the steps depend on the features chosen on the first one
        List<Action<StackPanel>> SetupSteps()
        {
            var steps = new List<Action<StackPanel>> { StepWelcome, StepObs, StepCheck };
            if (cfg.GuardEnabled) steps.Add(StepDevices);
            if (cfg.LibraryEnabled) steps.Add(StepFolders);
            // a test clip needs the guard (it hears OBS save) and the replay buffer (the key); otherwise the plain end
            steps.Add(cfg.GuardEnabled && cfg.UseReplayBuffer ? (Action<StackPanel>)StepTestClip : StepDone);
            return steps;
        }

        public int SetupStepCount { get { return SetupSteps().Count; }
        }

        void ShowSetupStep()
        {
            setupSteps = SetupSteps();
            setupStep = Math.Max(0, Math.Min(setupStep, setupSteps.Count - 1));
            setupLive = null;
            setupClip = null;
            setup.Children.Clear();

            var card = new Border
            {
                Style = S("CardBorder"), Width = 640, Padding = new Thickness(34, 28, 34, 24),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(24, 56, 24, 24),
            };
            var dock = new DockPanel();

            // progress: one dot per step
            var dots = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 18) };
            for (int i = 0; i < setupSteps.Count; i++)
                dots.Children.Add(new Border
                {
                    Width = i == setupStep ? 22 : 8, Height = 8, CornerRadius = new CornerRadius(4), Margin = new Thickness(0, 0, 6, 0),
                    Background = i <= setupStep ? Wpf.Res<Brush>("Brand") : Wpf.Res<Brush>("Line"),
                });
            DockPanel.SetDock(dots, Dock.Top);
            dock.Children.Add(dots);

            // buttons: skip on the left, back / next on the right
            var nav = new Grid { Margin = new Thickness(0, 24, 0, 0) };
            nav.ColumnDefinitions.Add(new ColumnDefinition());
            nav.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            bool lastStep = setupStep == setupSteps.Count - 1;
            if (!lastStep)
            {
                var skip = new Button { Style = S("BtnLink"), Content = L.T("Skip setup", "Пропустить настройку"), HorizontalAlignment = HorizontalAlignment.Left };
                skip.Click += (s, e) => CloseSetup(false);
                nav.Children.Add(skip);
            }
            var right = new StackPanel { Orientation = Orientation.Horizontal };
            Grid.SetColumn(right, 1);
            if (setupStep > 0)
            {
                var back = Btn(null, L.T("Back", "Назад"), "BtnGhost");
                back.Margin = new Thickness(0, 0, 8, 0);
                back.Click += (s, e) => { setupStep--; ShowSetupStep(); };
                right.Children.Add(back);
            }
            var next = Btn(null, lastStep ? L.T("Finish", "Готово") : L.T("Next", "Далее"), "BtnPrimary");
            next.MinWidth = 110;
            next.Click += (s, e) =>
            {
                if (lastStep) { CloseSetup(true); return; }
                setupStep++;
                ShowSetupStep();
            };
            right.Children.Add(next);
            nav.Children.Add(right);
            DockPanel.SetDock(nav, Dock.Bottom);
            dock.Children.Add(nav);

            var body = new StackPanel();
            setupSteps[setupStep](body);
            dock.Children.Add(new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 560 });
            card.Child = dock;
            setup.Children.Add(card);
            if (last != null && setupLive != null) setupLive(last);
        }

        // called from Update once a second
        void UpdateSetup(Snapshot s)
        {
            if (setup != null && setupLive != null) setupLive(s);
        }

        static void StepTitle(StackPanel p, string title, string sub)
        {
            p.Children.Add(new TextBlock { Text = title, FontFamily = Wpf.Res<FontFamily>("DisplayFont"), FontSize = 24, FontWeight = FontWeights.SemiBold });
            p.Children.Add(new TextBlock { Text = sub, Style = S("SubText"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 18) });
        }

        // a compact toggle row without the card look (the setup card is already a card)
        Grid SetupToggle(string title, string desc, bool on, Action<bool> set)
        {
            var g = new Grid { Margin = new Thickness(0, 0, 0, 14) };
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var tx = new StackPanel { Margin = new Thickness(0, 0, 20, 0) };
            tx.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold });
            if (desc != null) tx.Children.Add(new TextBlock { Text = desc, Style = S("SubText"), FontSize = 12.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) });
            g.Children.Add(tx);
            var t = new CheckBox { Style = S("Toggle"), IsChecked = on, VerticalAlignment = VerticalAlignment.Center };
            t.Checked += (s, e) => { set(true); Save(); };
            t.Unchecked += (s, e) => { set(false); Save(); };
            Grid.SetColumn(t, 1);
            g.Children.Add(t);
            return g;
        }

        // ── 1. language and features ──
        void StepWelcome(StackPanel p)
        {
            p.Children.Add(WhatYouGet());
            StepTitle(p, L.T("Welcome to ClipKeeper", "Добро пожаловать в ClipKeeper"),
                L.T("It keeps your OBS recording from breaking and keeps your clips. A few steps — and everything can be changed later in Settings.",
                    "Он следит, чтобы запись OBS не сломалась, и хранит клипы. Несколько шагов — и всё это потом можно поменять в настройках."));

            p.Children.Add(new TextBlock { Text = "LANGUAGE · ЯЗЫК", Style = S("Caption") });
            var langs = new WrapPanel { Margin = new Thickness(0, 0, 0, 18) };
            foreach (var o in new[] { new[] { L.Auto, L.T("As in Windows", "Как в Windows") }, new[] { L.En, "English" }, new[] { L.Ru, "Русский" } })
            {
                string code = o[0];
                var b = Btn(null, o[1], code == cfg.Language ? "BtnPrimary" : "BtnGhost");
                b.Margin = new Thickness(0, 0, 8, 0);
                b.Click += (s, e) =>
                {
                    if (code == cfg.Language) return;
                    cfg.Language = code;
                    Save();
                    if (app != null) app.Restart();   // the setup opens again in the new language
                };
                langs.Children.Add(b);
            }
            p.Children.Add(langs);

            p.Children.Add(new TextBlock { Text = L.T("WHAT DO YOU NEED", "ЧТО НУЖНО"), Style = S("Caption") });
            CheckBox guard = null, library = null;
            var g1 = SetupToggle(L.T("Recording guard", "Наблюдение за записью"),
                L.T("Devices, replay buffer, audio, picture and frames — with an alarm when something breaks", "Устройства, буфер повтора, звук, картинка и кадры — с тревогой, если что-то сломалось"),
                cfg.GuardEnabled, v => { if (!v && !cfg.LibraryEnabled) { guard.IsChecked = true; return; } cfg.GuardEnabled = v; ShowSetupStep(); });
            guard = (CheckBox)g1.Children[1];
            p.Children.Add(g1);
            var g2 = SetupToggle(L.T("Clip library and editor", "Библиотека клипов и редактор"),
                L.T("Clips by game, trimming, cutting and sharing, statistics", "Клипы по играм, обрезка, вырезы и отправка, статистика"),
                cfg.LibraryEnabled, v => { if (!v && !cfg.GuardEnabled) { library.IsChecked = true; return; } cfg.LibraryEnabled = v; ShowSetupStep(); });
            library = (CheckBox)g2.Children[1];
            p.Children.Add(g2);
            if (cfg.GuardEnabled)
                p.Children.Add(SetupToggle(L.T("I record with the replay buffer", "Записываю буфером повтора"),
                    L.T("Off — for regular recording", "Выключи, если пишешь обычной записью"), cfg.UseReplayBuffer, v => cfg.UseReplayBuffer = v));
        }

        // ── 2. the OBS link: one click finds the server settings OBS keeps on this computer; by hand below ──
        void StepObs(StackPanel p)
        {
            StepTitle(p, L.T("Connect to OBS", "Подключение к OBS"),
                L.T("Start OBS and press the button: ClipKeeper takes the port and the password from the OBS settings on this computer.",
                    "Запусти OBS и нажми кнопку: ClipKeeper возьмёт порт и пароль из настроек OBS на этом компьютере."));

            var hostBox = new TextBox { Style = S("Field"), Width = 200, Text = cfg.Host };
            var portBox = new TextBox { Style = S("Field"), Width = 90, Text = cfg.Port.ToString(), Margin = new Thickness(8, 0, 0, 0) };
            var pwBox = new PasswordBox { Style = S("PwField"), Width = 298 };
            Action connectNow = () =>
            {
                Save();
                if (host != null) { host.Text = cfg.Host; port.Text = cfg.Port.ToString(); UpdatePwState(); }
                if (app != null) app.Guard.Reconnect();
            };

            // the status: a big badge that pops into a green check when the link comes up
            var badge = new Grid { Width = 46, Height = 46, RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new ScaleTransform(1, 1) };
            var halo = new Ellipse { Fill = Wpf.Br(Wpf.Ok, 255), Opacity = 0, RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new ScaleTransform(1, 1) };
            var disc = new Ellipse { Fill = Wpf.Res<Brush>("Card2"), Stroke = Wpf.Res<Brush>("LineHi"), StrokeThickness = 1 };
            var mark = new TextBlock { Style = S("Icon"), Text = "\uE703", FontSize = 18, HorizontalAlignment = HorizontalAlignment.Center, Foreground = Wpf.Res<Brush>("Muted") };
            badge.Children.Add(halo);
            badge.Children.Add(disc);
            badge.Children.Add(mark);
            var stTitle = new TextBlock { FontFamily = Wpf.Res<FontFamily>("DisplayFont"), FontSize = 17, FontWeight = FontWeights.SemiBold };
            var stSub = new TextBlock { Style = S("SubText"), FontSize = 12.5, TextWrapping = TextWrapping.Wrap, MaxWidth = 420, Margin = new Thickness(0, 2, 0, 0) };
            var find = Btn("\uE721", L.T("Find OBS", "Найти OBS"), "BtnPrimary");
            find.Padding = new Thickness(18, 9, 18, 9);
            var statusRow = new Grid { Margin = new Thickness(0, 0, 0, 6) };
            statusRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            statusRow.ColumnDefinitions.Add(new ColumnDefinition());
            statusRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var stText = new StackPanel { Margin = new Thickness(16, 0, 16, 0), VerticalAlignment = VerticalAlignment.Center };
            stText.Children.Add(stTitle);
            stText.Children.Add(stSub);
            Grid.SetColumn(stText, 1);
            find.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(find, 2);
            statusRow.Children.Add(badge);
            statusRow.Children.Add(stText);
            statusRow.Children.Add(find);
            p.Children.Add(new Border { Style = S("CardBorder"), Background = Wpf.Res<Brush>("Input"), Padding = new Thickness(16, 14, 16, 14), Child = statusRow });
            var findText = new TextBlock { Style = S("SubText"), FontSize = 12.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 10, 0, 0), Visibility = Visibility.Collapsed };
            p.Children.Add(findText);

            find.Click += (s, e) =>
            {
                findText.Visibility = Visibility.Visible;
                findText.Foreground = Wpf.Res<Brush>("Sub");
                var f = ObsFind.Read();
                if (f == null)
                {
                    findText.Text = L.T("No OBS settings on this computer. Start OBS once — or enter the connection by hand below.",
                                        "Настроек OBS на этом компьютере нет. Запусти OBS хотя бы раз — или введи подключение вручную ниже.");
                    return;
                }
                if (!f.Enabled)
                {
                    findText.Foreground = Wpf.Br(Wpf.Warn, 255);
                    findText.Text = L.T("OBS is found, but its WebSocket server is off: in OBS open Tools → WebSocket Server Settings, turn on \"Enable WebSocket server\" and press the button again.",
                                        "OBS найден, но сервер WebSocket в нём выключен: в OBS открой «Сервис → Настройки сервера WebSocket», включи «Включить сервер WebSocket» и нажми кнопку ещё раз.");
                    return;
                }
                cfg.Host = "127.0.0.1";
                cfg.Port = f.Port;
                cfg.SetPassword(f.Auth ? f.Password : "");
                hostBox.Text = cfg.Host;
                portBox.Text = cfg.Port.ToString();
                Log.Write("OBS found: port " + f.Port + (f.Auth ? ", with a password" : ", no password") + " (" + f.From + ")");
                findText.Text = L.T("Found: port " + f.Port + (f.Auth ? ", the password is taken from OBS" : ", no password") + ". Connecting…",
                                    "Нашёл: порт " + f.Port + (f.Auth ? ", пароль взят из OBS" : ", без пароля") + ". Подключаюсь…");
                connectNow();
            };

            // by hand: another computer, a portable OBS
            p.Children.Add(new TextBlock { Text = L.T("OR BY HAND", "ИЛИ ВРУЧНУЮ"), Style = S("Caption"), Margin = new Thickness(2, 22, 0, 4) });
            p.Children.Add(new TextBlock
            {
                Text = L.T("In OBS: Tools → WebSocket Server Settings → Show Connect Info.", "В OBS: «Сервис → Настройки сервера WebSocket → Показать сведения о подключении»."),
                Style = S("SubText"), FontSize = 12.5, Margin = new Thickness(0, 0, 0, 10),
            });
            var addr = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
            addr.Children.Add(hostBox);
            addr.Children.Add(portBox);
            p.Children.Add(addr);
            var pwRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
            pwRow.Children.Add(pwBox);
            var connect = Btn(null, L.T("Connect", "Подключиться"), "BtnGhost");
            connect.Margin = new Thickness(8, 0, 0, 0);
            pwRow.Children.Add(connect);
            p.Children.Add(pwRow);
            p.Children.Add(new TextBlock
            {
                Text = cfg.HasPassword ? L.T("A password is already saved — leave the field empty to keep it.", "Пароль уже сохранён — оставь поле пустым, чтобы его не менять.")
                                       : L.T("The password is stored encrypted: only your Windows account can read it.", "Пароль хранится зашифрованным: прочитать его может только твоя учётная запись Windows."),
                Style = S("SubText"), FontSize = 12, TextWrapping = TextWrapping.Wrap,
            });
            connect.Click += (s, e) =>
            {
                cfg.Host = hostBox.Text.Trim().Length > 0 ? hostBox.Text.Trim() : "127.0.0.1";
                int n;
                if (int.TryParse(portBox.Text.Trim(), out n) && n > 0 && n < 65536) cfg.Port = n;
                if (pwBox.Password.Length > 0) { cfg.SetPassword(pwBox.Password); pwBox.Clear(); }
                stSub.Text = L.T("Connecting…", "Подключаюсь…");
                connectNow();
            };

            bool? was = null;
            setupLive = s =>
            {
                bool on = s.Connected;
                stTitle.Text = on ? L.T("Connected to OBS", "Подключено к OBS") : L.T("Not connected yet", "Пока не подключено");
                stSub.Text = on ? L.T("Everything is ready — press \"Next\"", "Всё готово — жми «Далее»") + " · " + s.Endpoint
                                : (s.ObsStatus ?? L.T("Start OBS, then press \"Find OBS\"", "Запусти OBS и нажми «Найти OBS»"));
                find.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
                if (on == was) return;
                disc.Fill = on ? Wpf.Br(Wpf.Ok, 255) : Wpf.Res<Brush>("Card2");
                disc.StrokeThickness = on ? 0 : 1;
                mark.Text = on ? "\uE73E" : "\uE703";
                mark.Foreground = on ? Brushes.White : Wpf.Res<Brush>("Muted");
                mark.FontSize = on ? 20 : 18;
                if (on && was == false) Celebrate(badge, halo);   // only the moment it connects, not when the step opens connected
                if (on) findText.Visibility = Visibility.Collapsed;
                was = on;
            };
        }

        // the check pops in with a bounce and a green ring runs out of it once
        static void Celebrate(FrameworkElement badge, Ellipse halo)
        {
            var bs = (ScaleTransform)badge.RenderTransform;
            var pop = new DoubleAnimation(0.3, 1, TimeSpan.FromSeconds(0.55)) { EasingFunction = new BackEase { Amplitude = 0.6, EasingMode = EasingMode.EaseOut } };
            bs.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
            bs.BeginAnimation(ScaleTransform.ScaleYProperty, pop);
            var hs = (ScaleTransform)halo.RenderTransform;
            var t = TimeSpan.FromSeconds(0.9);
            var grow = new DoubleAnimation(1, 2.4, t) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } };
            hs.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
            hs.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
            halo.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0.6, 0, t));
        }

        // ── 3. devices (the recording guard) ──
        void StepDevices(StackPanel p)
        {
            StepTitle(p, L.T("Remember your devices", "Запомни устройства"),
                L.T("Set up the audio and screen capture sources in OBS the way the recording should be, then press the button. " +
                    "ClipKeeper keeps OBS on exactly these devices and brings them back when Windows or a driver swaps them.",
                    "Настрой в OBS источники звука и захвата экрана так, как должна идти запись, и нажми кнопку. " +
                    "ClipKeeper держит OBS именно на этих устройствах и возвращает их, если Windows или драйвер их подменит."));
            var remember = Btn("", L.T("Remember current devices", "Запомнить текущие устройства"), "BtnPrimary");
            remember.HorizontalAlignment = HorizontalAlignment.Left;
            remember.Click += (s, e) => SaveReference();
            p.Children.Add(remember);
            var list = new TextBlock { Style = S("SubText"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 14, 0, 0) };
            p.Children.Add(list);
            setupLive = s =>
            {
                remember.IsEnabled = s.Connected;
                list.Text = !s.Connected ? L.T("Connect to OBS first (the previous step).", "Сначала подключись к OBS (предыдущий шаг).")
                          : s.Refs.Count == 0 ? L.T("Nothing remembered yet.", "Пока ничего не запомнено.")
                          : L.T("Remembered: ", "Запомнено: ") + string.Join(", ", s.Refs.Select(r => r.Key));
            };
        }

        // ── 4. folders (the library) ──
        void StepFolders(StackPanel p)
        {
            StepTitle(p, L.T("Clip folders", "Папки для клипов"),
                L.T("Sources is the OBS recording folder — ClipKeeper reads it from OBS. Ready is where the editor saves trims.",
                    "Исходники — папка записи OBS, ClipKeeper берёт её из OBS. Готовые — куда редактор сохраняет обрезки."));

            Func<string, string, string, Action, FrameworkElement> folder = (title, value, empty, pick) =>
            {
                var g = new Grid { Margin = new Thickness(0, 0, 0, 12) };
                g.ColumnDefinitions.Add(new ColumnDefinition());
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var tx = new StackPanel { Margin = new Thickness(0, 0, 16, 0) };
                tx.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold });
                tx.Children.Add(new TextBlock { Text = value ?? empty, Style = S("SubText"), FontSize = 12, FontFamily = Wpf.Res<FontFamily>("MonoFont"), TextTrimming = TextTrimming.CharacterEllipsis });
                g.Children.Add(tx);
                if (pick != null)
                {
                    var b = Btn(null, L.T("Choose…", "Выбрать…"), "BtnGhost");
                    b.VerticalAlignment = VerticalAlignment.Center;
                    b.Click += (s, e) => { pick(); ShowSetupStep(); };
                    Grid.SetColumn(b, 1);
                    g.Children.Add(b);
                }
                return g;
            };
            p.Children.Add(folder(L.T("Sources", "Исходники"), RootOf(last), L.T("known after connecting to OBS", "станет известна после подключения к OBS"), null));
            p.Children.Add(folder(L.T("Ready", "Готовые"), Existing(cfg.TrimFolder), L.T("not chosen — trims are saved next to the source", "не выбрана — обрезки сохраняются рядом с исходником"),
                                  () => PickSettingsFolder(SrcReady)));
            p.Children.Add(SetupToggle(L.T("Collection", "Коллекция"), L.T("A third folder for clips that made it into a video", "Третья папка — для клипов, вошедших в выпуск"),
                                       cfg.UseCollection, v => { cfg.UseCollection = v; ShowSetupStep(); }));
            if (cfg.UseCollection)
                p.Children.Add(folder(L.T("Collection folder", "Папка коллекции"), Existing(cfg.CollectionFolder), L.T("not chosen", "не выбрана"),
                                      () => PickSettingsFolder(SrcCollection)));
            p.Children.Add(SetupToggle(L.T("Sort clips by game", "Раскладывать клипы по играм"),
                L.T("Each saved clip goes into the folder of the game you are playing — no OBS script needed",
                    "Каждый сохранённый клип — в папку игры, в которую играешь, без скриптов в OBS"),
                cfg.SortClips, v => { SetSorting(v); ShowSetupStep(); }));
            if (!cfg.SortClips)
                p.Children.Add(SetupToggle(L.T("Subfolders of the OBS folder are games", "Подпапки в папке OBS — это игры"),
                    L.T("On if clips are in Game\\Month folders. Off if you sort clips another way.", "Включено, если клипы лежат в папках Игра\\Месяц. Выключи, если раскладываешь клипы иначе."),
                    cfg.SubfoldersAreGames, v => cfg.SubfoldersAreGames = v));
            p.Children.Add(SetupToggle(L.T("Covers from the internet", "Обложки из интернета"), L.T("Steam and Wikipedia", "Steam и Википедия"),
                                       cfg.OnlineCovers, v => cfg.OnlineCovers = v));
        }

        // ── 5. done ──
        void StepDone(StackPanel p)
        {
            StepTitle(p, L.T("All set", "Готово"),
                L.T("ClipKeeper lives in the tray. Click the icon to open this window, right click for quick actions.",
                    "ClipKeeper живёт в трее. Клик по значку открывает это окно, правый клик — быстрые действия."));
            var autoRow = SetupToggle(L.T("Start with Windows", "Запускать вместе с Windows"), L.T("Minimized to the tray", "Свёрнутым в трей"), Autostart.IsOn(), v => { });
            BindAutostart(autoRow.Children.OfType<CheckBox>().First());
            p.Children.Add(autoRow);
            if (cfg.GuardEnabled)
            {
                var test = Btn(null, L.T("Test alarm", "Тест тревоги"), "BtnGhost");
                test.HorizontalAlignment = HorizontalAlignment.Left;
                test.Click += (s, e) => { if (app != null) app.TestAlert(); };
                p.Children.Add(new TextBlock { Text = L.T("See what an alarm looks like:", "Посмотри, как выглядит тревога:"), Style = S("SubText"), Margin = new Thickness(0, 4, 0, 8) });
                p.Children.Add(test);
            }
        }

        // for previews: the setup card at a given step (-1 — hide it); nothing is saved
        public void PreviewSetup(int step)
        {
            if (step < 0)
            {
                if (setup != null) F<Grid>("Root").Children.Remove(setup);
                setup = null;
                setupLive = null;
                return;
            }
            if (setup == null) ShowSetup();
            setupStep = step;
            ShowSetupStep();
        }
    }
}
