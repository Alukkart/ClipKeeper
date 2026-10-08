using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Ellipse = System.Windows.Shapes.Ellipse;
using Hyperlink = System.Windows.Documents.Hyperlink;
using Run = System.Windows.Documents.Run;

namespace DeviceGuard
{
    // Settings: on the left the title, the search and the sections in groups; the chosen section scrolls on the right.
    // A section opens with a banner (its name, what it is for, a picture) and is made of cards: a caption and rows split by thin lines.
    // The search goes over every row of every section.
    partial class MainWindow
    {
        public const string TabGeneral = "general", TabAbout = "about", TabObs = "obs", TabObsApp = "obs-app", TabReplay = "replay",
                            TabChecks = "checks", TabAlarm = "alarm", TabClipSaved = "clip-saved", TabHotkeys = "hotkeys", TabSorting = "sorting",
                            TabFolders = "folders", TabCovers = "covers", TabCleanup = "cleanup", TabEncoder = "encoder", TabEditorKeys = "editor-keys";
        // rows other places link to (a problem on the Recording page, a tile)
        public const string RowConnection = "connection", RowPassword = "password", RowObsProgram = "obs-program",
                            RowReplayBuffer = "replay-buffer", RowDisk = "disk", RowFrames = "frames", RowUpdates = "updates", RowClipSound = "clip-sound", RowObsScript = "obs-script";

        class SettingsTab
        {
            public string Key, Title, Group;
            public RadioButton Button;
            public StackPanel Panel;
            public FrameworkElement Banner;   // the name, what the section is for and its picture: hidden in search results
            public TextBlock Heading;         // the section name: only shown in search results, the banner says it otherwise
        }

        class SettingsCard
        {
            public SettingsTab Tab;
            public TextBlock Caption;
            public Border Box;
            public StackPanel Rows;
        }

        class SettingsItem
        {
            public FrameworkElement Row;
            public SettingsCard Card;
            public string Words;        // search words that are not on screen
            public bool OwnText;        // the text on screen counts too (not for the key map: every action name would match)
        }

        readonly List<SettingsTab> settingsTabs = new List<SettingsTab>();
        readonly List<SettingsCard> settingsCards = new List<SettingsCard>();
        readonly List<SettingsItem> settingsItems = new List<SettingsItem>();
        readonly Dictionary<string, FrameworkElement> settingsRows = new Dictionary<string, FrameworkElement>();
        StackPanel settingsNav;
        string buildGroup;
        SettingsTab buildTab;
        SettingsCard buildCard;
        string settingsTab = TabGeneral;   // survives rebuilds, so a switched toggle does not throw you to the first section
        TextBox settingsSearch;
        TextBlock settingsNothing;
        bool syncingTabs;

        public IEnumerable<string> SettingsTabKeys { get { return settingsTabs.Select(t => t.Key).ToList(); } }

        void BuildSettings()
        {
            var head = F<StackPanel>("SettingsHead");
            settingsNav = F<StackPanel>("SettingsNav");
            var p = F<StackPanel>("SettingsPanel");
            string query = settingsSearch != null ? settingsSearch.Text : "";
            head.Children.Clear();
            settingsNav.Children.Clear();
            p.Children.Clear();
            settingsTabs.Clear();
            settingsCards.Clear();
            settingsItems.Clear();
            settingsRows.Clear();

            head.Children.Add(SearchBox(query));

            Group(L.T("Basics", "Основное"));
            Tab(p, TabGeneral, "", L.T("General", "Общие"), L.T("Language, startup and which parts of ClipKeeper are on", "Язык, автозапуск и какие части ClipKeeper включены"));
            BuildGeneral();
            Tab(p, TabAbout, "", L.T("About", "О программе"), L.T("The version, updates and how to report a problem", "Версия, обновления и как сообщить о проблеме"));
            BuildAbout();

            Group("OBS");
            Tab(p, TabObs, "", L.T("Connection", "Подключение"), L.T("How ClipKeeper reaches OBS over WebSocket", "Как ClipKeeper связывается с OBS по WebSocket"));
            BuildConnection();
            Tab(p, TabObsApp, "", L.T("OBS program", "Программа OBS"), L.T("Where OBS is installed, starting together and getting back up after a crash",
                                                                                   "Где установлен OBS, запуск вместе с ним и восстановление после падения"));
            BuildObsProgram();
            if (cfg.GuardEnabled)
            {
                Tab(p, TabReplay, "", L.T("Replay buffer", "Буфер повтора"), L.T("If you save clips with the replay buffer rather than regular recording",
                                                                                       "Если сохраняешь клипы буфером повтора, а не обычной записью"));
                BuildReplayBuffer();

                Group(L.T("Monitoring", "Наблюдение"));
                Tab(p, TabChecks, "", L.T("Checks", "Проверки"), L.T("What ClipKeeper checks in the recording besides devices", "Что ClipKeeper проверяет в записи помимо устройств"));
                BuildChecks();
                Tab(p, TabAlarm, "", L.T("Alarm", "Тревога"), L.T("How ClipKeeper calls you when something is wrong with recording", "Как ClipKeeper зовёт тебя, если с записью что-то не так"));
                BuildAlarm();
            }

            Group(L.T("Clips", "Клипы"));
            if (cfg.GuardEnabled)
            {
                Tab(p, TabClipSaved, "", L.T("Clip saved", "Клип сохранён"), L.T("What happens the moment you save a clip", "Что происходит в момент, когда ты сохранил клип"));
                BuildClipSaved();
            }
            if (cfg.LibraryEnabled)
            {
                Tab(p, TabHotkeys, "", L.T("Hotkeys in game", "Клавиши в игре"), L.T("Do things with the last clip without leaving the game", "Действия с последним клипом, не выходя из игры"));
                BuildHotkeys();
            }
            // sorting works without the library: it is done while recording
            Tab(p, TabSorting, "", L.T("Sorting by game", "Раскладка по играм"), L.T("Saved clips go into the folder of the game you are playing — no OBS script needed",
                                                                                             "Сохранённые клипы попадают в папку игры, в которую ты играешь, — скрипт в OBS не нужен"));
            BuildSorting();

            if (cfg.LibraryEnabled)
            {
                Group(L.T("Library", "Библиотека"));
                Tab(p, TabFolders, "", L.T("Folders", "Папки"), L.T("Where ClipKeeper looks for clips. The same three folders are the buttons on top of the library",
                                                                         "Где ClipKeeper ищет клипы. Те же три папки — кнопки вверху библиотеки"));
                BuildFolders();
                Tab(p, TabCovers, "", L.T("Games and covers", "Игры и обложки"), L.T("How the library tells which game a clip is from and what to show on it",
                                                                                          "Как библиотека понимает, из какой игры клип, и что показать на обложке"));
                BuildCovers();
                Tab(p, TabCleanup, "", L.T("Cleanup", "Уборка"), L.T("Old source clips go to the Recycle Bin; nothing you kept is touched",
                                                                          "Старые исходники уходят в корзину; то, что ты оставил, не трогается"));
                BuildCleanup();

                Group(L.T("Editor", "Редактор"));
                Tab(p, TabEncoder, "", L.T("Video encoder", "Кодировщик"), L.T("What re-encodes trims and cuts", "Чем пережимаются обрезки и вырезы"));
                BuildEncoder();
                Tab(p, TabEditorKeys, "", L.T("Editor keys", "Клавиши редактора"), L.T("Like in Premiere, and any key can be changed", "Как в Premiere, и любую можно поменять"));
                BuildEditorKeys();
            }

            settingsNothing = new TextBlock { Style = S("SubText"), Margin = new Thickness(2, 24, 0, 0), Visibility = Visibility.Collapsed };
            p.Children.Add(settingsNothing);

            if (!settingsTabs.Any(t => t.Key == settingsTab)) settingsTab = TabGeneral;   // its module was turned off
            ApplySettingsSearch();
        }

        // ── building blocks ──
        // a group caption in the list; the sections after it belong to it
        void Group(string name)
        {
            buildGroup = name;
            settingsNav.Children.Add(new TextBlock { Text = name.ToUpperInvariant(), Style = S("Caption"),
                                                     Margin = new Thickness(10, settingsNav.Children.Count == 0 ? 8 : 14, 0, 3) });
        }

        void Tab(StackPanel p, string key, string glyph, string title, string sub)
        {
            var t = new SettingsTab { Key = key, Title = title, Group = buildGroup, Panel = new StackPanel() };
            t.Banner = Banner(key, title, sub);
            t.Panel.Children.Add(t.Banner);
            t.Heading = new TextBlock { Text = title, FontFamily = Wpf.Res<FontFamily>("DisplayFont"), FontSize = 20, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 26, 0, 0) };
            t.Panel.Children.Add(t.Heading);
            p.Children.Add(t.Panel);

            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            sp.Children.Add(new TextBlock { Style = S("Icon"), Text = glyph, FontSize = 14, Width = 26, VerticalAlignment = VerticalAlignment.Center });
            sp.Children.Add(new TextBlock { Text = title });
            t.Button = new RadioButton { Style = S("SubNav"), GroupName = "settingsTabs", Content = sp };
            t.Button.Checked += (s, e) => { if (!syncingTabs) ShowSettingsTab(key); };
            settingsNav.Children.Add(t.Button);

            settingsTabs.Add(t);
            buildTab = t;
        }

        // the top of a section: its name and what it is for on the left, its picture on the right
        static FrameworkElement Banner(string key, string title, string sub)
        {
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var tx = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 20, 0) };
            tx.Children.Add(new TextBlock { Text = title, Style = S("H1"), FontSize = 21 });
            tx.Children.Add(new TextBlock { Text = sub, Style = S("SubText"), Margin = new Thickness(0, 6, 0, 0) });
            g.Children.Add(tx);
            var art = SettingsArt(key);
            Grid.SetColumn(art, 1);
            g.Children.Add(art);
            return new Border
            {
                Background = Wpf.Res<Brush>("Card"), BorderBrush = Wpf.Res<Brush>("Line"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12),
                Padding = new Thickness(22, 8, 12, 8), Margin = new Thickness(0, 2, 0, 4), Child = g,
            };
        }

        // a new card in the category being built; rows go into it until the next card
        void Card(string caption)
        {
            var c = new SettingsCard { Tab = buildTab, Rows = new StackPanel() };
            if (caption != null)
            {
                c.Caption = Section(caption);
                buildTab.Panel.Children.Add(c.Caption);
            }
            c.Box = new Border { Style = S("CardBorder"), Padding = new Thickness(0), Child = c.Rows, Margin = new Thickness(0, caption != null ? 0 : 20, 0, 0) };
            buildTab.Panel.Children.Add(c.Box);
            settingsCards.Add(c);
            buildCard = c;
        }

        // a row of the current card; words are extra search terms (synonyms, the other language)
        T Add<T>(T row, string words = null, bool ownText = true, string id = null) where T : FrameworkElement
        {
            buildCard.Rows.Children.Add(row);
            if (id != null) settingsRows[id] = row;
            settingsItems.Add(new SettingsItem { Row = row, Card = buildCard, Words = words ?? "", OwnText = ownText });
            return row;
        }

        // a button on the right of a row
        Button RowButton(string glyph, string text, string style, Action click)
        {
            var b = Btn(glyph, text, style);
            b.Click += (s, e) => click();
            return b;
        }

        FrameworkElement SearchBox(string query)
        {
            settingsSearch = new TextBox { Text = query };
            var box = SearchField(settingsSearch, L.T("Search  (Ctrl+F)", "Поиск  (Ctrl+F)"), L.T("Find a setting (Ctrl+F)", "Найти настройку (Ctrl+F)"));
            box.Margin = new Thickness(0, 0, 0, 4);
            settingsSearch.TextChanged += (s, e) => { ApplySettingsSearch(); ScrollOf(PageSettings).ScrollToTop(); };
            return box;
        }

        // Ctrl+F on the settings page goes to the search; Esc or the arrow on top goes back to the page you came from.
        // Esc is caught after the controls: the search and a hotkey field being set use it themselves
        void InitSettingsKeys()
        {
            F<Button>("SettingsBack").Click += (s, e) => LeaveSettings();
            W.KeyDown += (s, e) =>
            {
                if (e.Key == Key.Escape && Keyboard.Modifiers == ModifierKeys.None && pages[PageSettings].IsVisible) { LeaveSettings(); e.Handled = true; }
            };
            W.PreviewKeyDown += (s, e) =>
            {
                if (e.Key != Key.F || Keyboard.Modifiers != ModifierKeys.Control || !pages[PageSettings].IsVisible || settingsSearch == null) return;
                settingsSearch.Focus();
                settingsSearch.SelectAll();
                e.Handled = true;
            };
        }

        public void LeaveSettings()
        {
            ShowPage(PageOn(pageBeforeSettings) ? pageBeforeSettings : PageClips);   // ShowPage falls back further if the library is off
        }

        public void ShowSettingsTab(string key)
        {
            settingsTab = key;
            if (settingsSearch != null && settingsSearch.Text.Length > 0) settingsSearch.Text = "";   // TextChanged applies it
            else ApplySettingsSearch();
            ScrollOf(PageSettings).ScrollToTop();
            var t = settingsTabs.FirstOrDefault(x => x.Key == key);
            if (t != null) t.Button.BringIntoView();   // the list scrolls when the window is low
        }

        // opens the section the row is in and scrolls to the row, which lights up for a moment;
        // a row that is not there (its feature is off) — just the given section
        public void ShowSettingsRow(string tab, string row)
        {
            ShowPage(PageSettings);
            FrameworkElement el;
            var owner = settingsRows.TryGetValue(row, out el) ? settingsTabs.FirstOrDefault(t => t.Panel.IsAncestorOf(el)) : null;
            if (owner != null) tab = owner.Key;
            else if (!settingsTabs.Any(t => t.Key == tab)) tab = TabGeneral;
            ShowSettingsTab(tab);
            if (owner == null) return;
            W.Dispatcher.BeginInvoke(new Action(() =>
            {
                var sv = ScrollOf(PageSettings);
                double y = el.TranslatePoint(new Point(0, 0), (UIElement)sv.Content).Y;
                sv.ScrollToVerticalOffset(Math.Max(0, y - 60));
                Flash(el as Border);
            }), DispatcherPriority.Loaded);
        }

        static void Flash(Border b)
        {
            if (b == null) return;
            var br = new SolidColorBrush(Color.FromRgb(0x2A, 0x2B, 0x31));
            b.Background = br;
            var a = new System.Windows.Media.Animation.ColorAnimation(Color.FromArgb(0, 0x2A, 0x2B, 0x31), TimeSpan.FromSeconds(1.6))
            {
                BeginTime = TimeSpan.FromSeconds(0.5),
            };
            a.Completed += (s, e) => b.Background = null;
            br.BeginAnimation(SolidColorBrush.ColorProperty, a);
        }

        // for the self-test: type into the search; returns the rows and the tabs that are shown
        public int[] SearchSettingsForTest(string q)
        {
            settingsSearch.Text = q;
            int rows = settingsItems.Count(i => i.Row.Visibility == Visibility.Visible && i.Card.Box.Visibility == Visibility.Visible &&
                                                i.Card.Tab.Panel.Visibility == Visibility.Visible);
            return new[] { rows, settingsTabs.Count(t => t.Panel.Visibility == Visibility.Visible) };
        }

        public string ShownSettingsTab { get { return settingsTabs.Where(t => t.Panel.Visibility == Visibility.Visible).Select(t => t.Key).FirstOrDefault(); } }

        void ClearSettingsSearch()
        {
            if (settingsSearch != null && settingsSearch.Text.Length > 0) settingsSearch.Text = "";
        }

        // no search: only the chosen section. A search: matching rows from every section, under section names;
        // in the list, sections without matches fade
        void ApplySettingsSearch()
        {
            string q = settingsSearch != null ? settingsSearch.Text.Trim().ToLowerInvariant() : "";
            var words = q.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            bool searching = words.Length > 0;

            foreach (var it in settingsItems)
            {
                bool show = true;
                if (searching)
                {
                    string hay = (it.Card.Tab.Group + " " + it.Card.Tab.Title + " " + (it.Card.Caption != null ? it.Card.Caption.Text : "") + " " + it.Words + " " + (it.OwnText ? TextOf(it.Row) : "")).ToLowerInvariant();
                    show = words.All(w => hay.Contains(w));
                }
                it.Row.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            }
            foreach (var c in settingsCards)
            {
                bool any = Divide(c.Rows);
                c.Box.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
                if (c.Caption != null) c.Caption.Visibility = c.Box.Visibility;
            }
            bool found = false;
            syncingTabs = true;
            foreach (var t in settingsTabs)
            {
                bool show = searching ? settingsCards.Any(c => c.Tab == t && c.Box.Visibility == Visibility.Visible) : t.Key == settingsTab;
                found |= show;
                t.Panel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
                t.Heading.Visibility = searching ? Visibility.Visible : Visibility.Collapsed;
                // the banner explains the section; in search results its name is enough
                t.Banner.Visibility = searching ? Visibility.Collapsed : Visibility.Visible;
                t.Button.IsChecked = !searching && t.Key == settingsTab;
                t.Button.Opacity = searching && !show ? 0.4 : 1;
            }
            syncingTabs = false;
            settingsNothing.Text = L.T("Nothing found for “", "Ничего не нашлось по запросу «") + q + L.T("”", "»");
            settingsNothing.Visibility = searching && !found ? Visibility.Visible : Visibility.Collapsed;
        }

        // a thin line between the visible rows of a card; returns whether any row is visible
        static bool Divide(StackPanel rows)
        {
            bool first = true;
            foreach (FrameworkElement r in rows.Children)
            {
                if (r.Visibility != Visibility.Visible) continue;
                var b = r as Border;
                if (b != null) b.BorderThickness = new Thickness(0, first ? 0 : 1, 0, 0);
                first = false;
            }
            return !first;
        }

        // all the text in a row: title, description, button captions — for the search
        static string TextOf(object o)
        {
            var sb = new StringBuilder();
            CollectText(o, sb);
            return sb.ToString();
        }

        static void CollectText(object o, StringBuilder sb)
        {
            if (o is string) { sb.Append((string)o).Append(' '); return; }
            var tb = o as TextBlock;
            if (tb != null) { sb.Append(tb.Text).Append(' '); return; }
            var d = o as DependencyObject;
            if (d == null) return;
            foreach (var c in LogicalTreeHelper.GetChildren(d)) CollectText(c, sb);
        }

        // a module or a mode was switched: rebuild the settings page (tabs and rows appear and disappear) and the menu
        void RebuildSettings()
        {
            W.Dispatcher.BeginInvoke(new Action(() =>
            {
                var sv = ScrollOf(PageSettings);
                double offset = sv.VerticalOffset;
                bool focused = settingsSearch != null && settingsSearch.IsKeyboardFocusWithin;
                BuildSettings();
                sv.UpdateLayout();
                sv.ScrollToVerticalOffset(offset);
                if (focused) { settingsSearch.Focus(); settingsSearch.CaretIndex = settingsSearch.Text.Length; }
            }), DispatcherPriority.Background);
        }

        // which pages exist: the library and statistics belong to the library module, Recording to the guard
        bool PageOn(int page)
        {
            if (page == PageClips || page == PageStats) return cfg.LibraryEnabled;
            if (page == PageRecord) return cfg.GuardEnabled;
            return true;
        }

        public void SyncModules()
        {
            for (int i = 0; i < navs.Length; i++) navs[i].Visibility = PageOn(i) ? Visibility.Visible : Visibility.Collapsed;
            if (currentPage >= 0 && !PageOn(currentPage)) ShowPage(currentPage);
            sourceChipsSig = null;
            clipsLoadedAt = DateTime.MinValue;
            if (app != null) app.ModulesChanged();
        }

        // a setting that changes what exists was switched: apply it everywhere
        void Changed(bool rebuild)
        {
            Save();
            cfg.Apply();
            SyncModules();
            if (rebuild) RebuildSettings();
        }

        const string ReleasesUrl = "https://github.com/Alukkart/ClipKeeper/releases";

        // ── General: language, startup, modules ─────────────────────────────
        // the language chips go under the description: on the right they would squeeze it in a narrow window
        void BuildGeneral()
        {
            Card(L.T("Interface", "Интерфейс"));
            // the title is in both languages so it can be found in either
            var chips = new StackPanel { Orientation = Orientation.Horizontal };
            var options = new[] { new[] { L.Auto, L.T("As in Windows", "Как в Windows") }, new[] { L.En, "English" }, new[] { L.Ru, "Русский" } };
            foreach (var o in options)
            {
                string code = o[0];
                var b = Btn(null, o[1], code == cfg.Language ? "BtnPrimary" : "BtnGhost");
                b.Margin = new Thickness(0, 0, 6, 0);
                b.Click += (s, e) =>
                {
                    if (code == cfg.Language) return;
                    cfg.Language = code;
                    Save();
                    Log.Write("language: " + code);
                    if (app != null) app.Restart();
                };
                chips.Children.Add(b);
            }
            var langDesc = new StackPanel();
            langDesc.Children.Add(new TextBlock { Style = S("SubText"), FontSize = 12.5, Margin = new Thickness(0, 2, 0, 10),
                Text = L.T("ClipKeeper restarts to apply it. As in Windows: Russian on a Russian Windows, English otherwise",
                           "Чтобы применить, ClipKeeper перезапустится. Как в Windows: русский на русской Windows, иначе английский") });
            langDesc.Children.Add(chips);
            Add(Row("\uF2B7", "Language · Язык", langDesc, null), "language язык english русский");
            Add(Row("\uE890", L.T("Hide ClipKeeper windows from recordings and screenshots", "Скрывать окна ClipKeeper из записи и скриншотов"),
                L.T("The main window and the editor stay out of OBS, screenshots and screen sharing. Alarms, notifications and the tray menu are always hidden", "Главное окно и редактор не попадают в OBS, скриншоты и демонстрацию экрана. Тревоги, уведомления и меню трея скрыты всегда"),
                Toggle(cfg.HideFromCapture, v => { cfg.HideFromCapture = v; Wpf.ApplyCaptureSetting(v); })),
                "capture stream privacy захват стрим");

            var autoToggle = new CheckBox { Style = S("Toggle"), IsChecked = Autostart.IsOn() };
            BindAutostart(autoToggle);
            Add(Row("\uE7E8", L.T("Start with Windows", "Запускать вместе с Windows"), L.T("ClipKeeper starts minimized to the tray", "ClipKeeper стартует свёрнутым в трей"), autoToggle),
                "autostart startup boot tray автозапуск трей");

            Card(L.T("Features", "Возможности"));
            CheckBox guard = null, library = null;
            guard = Toggle(cfg.GuardEnabled, v =>
            {
                if (!v && !cfg.LibraryEnabled) { guard.IsChecked = true; return; }   // at least one module stays on
                cfg.GuardEnabled = v;
                Changed(true);
            });
            library = Toggle(cfg.LibraryEnabled, v =>
            {
                if (!v && !cfg.GuardEnabled) { library.IsChecked = true; return; }
                cfg.LibraryEnabled = v;
                Changed(true);
            });
            Add(Row("\uE7C8", L.T("Recording guard", "Наблюдение за записью"),
                L.T("Keeps OBS devices, the replay buffer, audio, picture and frames in check and raises an alarm when something breaks. Off — the Recording page, Checks and Alarm disappear",
                    "Следит за устройствами OBS, буфером повтора, звуком, картинкой и кадрами и бьёт тревогу, если что-то сломалось. Выключено — пропадут страница «Запись», «Проверки» и «Тревога»"), guard),
                "module модуль");
            Add(Row("\uE8B9", L.T("Clip library and editor", "Библиотека клипов и редактор"),
                L.T("Clips by game with covers, trimming and cutting, statistics. Off — the Library and Statistics pages disappear",
                    "Клипы по играм с обложками, обрезка и вырезы, статистика. Выключено — пропадут «Библиотека» и «Статистика»"), library),
                "module модуль");
            Add(Row("\uE768", L.T("First-time setup", "Первая настройка"),
                L.T("Features, OBS, devices and folders step by step", "Возможности, OBS, устройства и папки по шагам"),
                RowButton(null, L.T("Run again", "Пройти заново"), "BtnGhost", ShowSetup)),
                "wizard мастер");
        }

        // ── About: the version and updates, reporting a problem ─────────────
        void BuildAbout()
        {
            Card(null);
            updateText = new TextBlock { Style = S("SubText"), FontSize = 12.5 };
            updateButton = new Button { Style = S("BtnGhost") };
            updateButton.Click += (s, e) => UpdateButtonClick();
            var buttons = new StackPanel { Orientation = Orientation.Horizontal };
            buttons.Children.Add(updateButton);
            var log = Btn("\uE8A5", null, "BtnIcon");
            log.ToolTip = L.T("Open the log", "Открыть журнал");
            log.Margin = new Thickness(6, 0, 0, 0);
            log.Click += (s, e) => Shell.Open(Log.FilePath);
            buttons.Children.Add(log);
            Add(Row("\uE946", "ClipKeeper " + Program.Version, updateText, buttons),
                "about version update log journal о программе версия обновление журнал", true, RowUpdates);
            Add(Row("\uEBE8", L.T("Report a problem", "Сообщить о проблеме"),
                L.T("Opens a GitHub form with your ClipKeeper and Windows versions filled in, and shows ClipKeeper.log — drag it into the form. Nothing is sent by itself",
                    "Открывает форму на GitHub с уже подставленными версиями ClipKeeper и Windows и показывает ClipKeeper.log — перетащи его в форму. Само ничего не отправляется"),
                RowButton(null, L.T("Report…", "Сообщить…"), "BtnGhost", () => Report.Open(null, null))),
                "bug report problem issue github ошибка проблема сообщить");
            Add(Row("\uE895", L.T("Check for updates", "Проверять обновления"),
                L.T("Once a day on GitHub. A new version is installed only when you press \"Update\"",
                    "Раз в день на GitHub. Новая версия ставится, только когда нажмёшь «Обновить»"),
                Toggle(cfg.UpdateCheck, v => cfg.UpdateCheck = v)), "update обновление");
            UpdatesChanged();
        }

        // ── updates: the About row shows what Updates knows and offers the next step ──
        TextBlock updateText;
        Button updateButton;
        string updateError;   // the last install attempt failed

        public void UpdatesChanged()
        {
            if (updateText == null) return;
            bool busy = app != null && app.UpdateBusy;
            var r = Updates.Newer;
            updateText.Inlines.Clear();
            updateText.Foreground = Wpf.Res<Brush>("Sub");
            string label;
            if (r != null)
            {
                updateText.Inlines.Add(new Run(busy ? L.T("Downloading version ", "Скачиваю версию ") + r.Version + "…"
                                                    : L.T("Version ", "Вышла версия ") + r.Version + L.T(" is out · ", " · ")));
                if (!busy) updateText.Inlines.Add(Link(L.T("what's new", "что нового"), r.Page ?? ReleasesUrl));
                if (!busy && r.Version == cfg.UpdateFailed)
                    updateText.Inlines.Add(new Run(L.T("\nIt crashed on start here last time and was rolled back", "\nВ прошлый раз она упала при запуске, и ClipKeeper откатился"))
                                           { Foreground = Wpf.Br(Wpf.Warn, 255) });
                if (updateError != null && !busy)
                {
                    updateText.Inlines.Add(new Run(L.T("\nDid not update: ", "\nНе обновилось: ") + updateError) { Foreground = Wpf.Br(Wpf.Bad, 255) });
                }
                label = L.T("Update to ", "Обновить до ") + r.Version;
                updateButton.Style = S("BtnPrimary");
            }
            else
            {
                string state = busy ? L.T("Checking for updates…", "Проверяю обновления…")
                             : Updates.Error != null ? L.T("Could not check for updates: ", "Не получилось проверить обновления: ") + Updates.Error
                             : Updates.CheckedAt != DateTime.MinValue ? L.T("This is the latest version (checked at ", "Это последняя версия (проверено в ") + Updates.CheckedAt.ToString("HH:mm") + ")"
                             : Updates.LocalBuild ? L.T("A local build: updates are checked only by the button", "Локальная сборка: обновления проверяются только кнопкой")
                             : L.T("Updates have not been checked yet", "Обновления ещё не проверялись");
                updateText.Inlines.Add(new Run(state + " · "));
                updateText.Inlines.Add(Link(L.T("all versions", "все версии"), ReleasesUrl));
                label = L.T("Check now", "Проверить сейчас");
                updateButton.Style = S("BtnGhost");
            }
            updateButton.Content = label;
            updateButton.IsEnabled = !busy && app != null;
        }

        void UpdateButtonClick()
        {
            if (app == null) return;
            updateError = null;
            if (Updates.Newer != null) app.InstallUpdate(err => { updateError = err; UpdatesChanged(); });
            else app.CheckUpdates(true);
        }

        static Hyperlink Link(string text, string url)
        {
            var link = new Hyperlink(new Run(text)) { Foreground = Wpf.Res<Brush>("Sub") };
            link.Click += (s, e) => { try { System.Diagnostics.Process.Start(url); } catch (Exception ex) { Log.Write("link: " + ex.Message); } };
            return link;
        }

        // the toggle goes back if Windows refused, so it never shows "on" while autostart is off
        void BindAutostart(CheckBox c)
        {
            bool reverting = false;
            RoutedEventHandler changed = (s, e) =>
            {
                if (reverting) return;
                bool on = c.IsChecked == true;
                string err;
                if (Autostart.TrySet(on, out err)) return;
                reverting = true;
                c.IsChecked = !on;
                reverting = false;
                if (app != null) app.ShowToast(L.T("Could not change \"Start with Windows\": ", "Не получилось изменить «Запускать вместе с Windows»: ") + err);
            };
            c.Checked += changed;
            c.Unchecked += changed;
        }

        // ── OBS: the connection ─────────────────────────────────────────────
        void BuildConnection()
        {
            // the connection status at the top: the first thing to look at when something does not work
            Card(null);
            var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(18, 14, 18, 14) };
            connDot = new Ellipse { Width = 8, Height = 8, Fill = Wpf.Br(Wpf.Grey, 255), VerticalAlignment = VerticalAlignment.Center };
            var tx = new StackPanel { Margin = new Thickness(14, 0, 0, 0) };
            connTitle = new TextBlock { Text = L.T("Connecting…", "Подключение…"), FontWeight = FontWeights.SemiBold };
            connSub = new TextBlock { Style = S("SubText"), FontSize = 12.5, Margin = new Thickness(0, 2, 0, 0), FontFamily = Wpf.Res<FontFamily>("MonoFont") };
            tx.Children.Add(connTitle);
            tx.Children.Add(connSub);
            sp.Children.Add(connDot);
            sp.Children.Add(tx);
            Add(new Border { Child = sp, BorderBrush = Wpf.Res<Brush>("Line") }, "status connection статус подключение связь", true, RowConnection);

            Card(L.T("WebSocket connection", "Подключение по WebSocket"));
            host = new TextBox { Style = S("Field"), Width = 220, Text = cfg.Host };
            port = new TextBox { Style = S("Field"), Width = 110, Text = cfg.Port.ToString() };
            pw = new PasswordBox { Style = S("PwField"), Width = 220 };
            pwState = new TextBlock { Style = S("SubText"), FontSize = 12.5 };
            UpdatePwState();
            Add(Row("\uE774", L.T("Address", "Адрес"), L.T("If OBS is on this computer — 127.0.0.1", "Если OBS на этом же компьютере — 127.0.0.1"), host), "websocket host ip");
            Add(Row("\uE968", L.T("Port", "Порт"), L.T("4455 by default", "По умолчанию 4455"), port), "websocket");
            Add(Row("\uE72E", L.T("Password", "Пароль"), pwState, pw), "websocket", true, RowPassword);
            Add(Row("\uE946", L.T("Where to find them", "Где их взять"),
                L.T("OBS → Tools → WebSocket Server Settings → Show Connect Info", "OBS → Сервис → Настройки сервера WebSocket → Показать сведения о подключении"),
                RowButton(null, L.T("Save and reconnect", "Сохранить и переподключиться"), "BtnPrimary", SaveConnection)),
                "websocket save reconnect сохранить переподключиться");

        }

        // ── OBS program: where it is, starting together, restarting after a crash, backups ──
        void BuildObsProgram()
        {
            Card(null);
            obsExe = new TextBlock { Style = S("SubText"), FontSize = 12, FontFamily = Wpf.Res<FontFamily>("MonoFont"), TextTrimming = TextTrimming.CharacterEllipsis };
            UpdateObsExe();
            var exeButtons = new StackPanel { Orientation = Orientation.Horizontal };
            var pickExe = Btn(null, L.T("Choose…", "Выбрать…"), "BtnGhost");
            pickExe.Click += (s, e) => PickObsExe();
            var autoExe = Btn("\uE72C", null, "BtnIcon");
            autoExe.ToolTip = L.T("Find it automatically", "Искать автоматически");
            autoExe.Margin = new Thickness(6, 0, 0, 0);
            autoExe.Click += (s, e) => { cfg.ObsPath = ""; Save(); UpdateObsExe(); };
            exeButtons.Children.Add(pickExe);
            exeButtons.Children.Add(autoExe);
            Add(Row("\uE7FC", L.T("Where OBS is installed", "Где установлен OBS"), obsExe, exeButtons), "obs64.exe path путь", true, RowObsProgram);
            BuildObsScriptRow();
            if (cfg.GuardEnabled)
            {
                Add(Row("\uE777", L.T("Restart OBS if it crashes", "Перезапускать OBS, если он упал"),
                    (cfg.UseReplayBuffer ? L.T("With the replay buffer and without the safe mode question", "С буфером повтора и без вопроса про безопасный режим")
                                         : L.T("Without the safe mode question", "Без вопроса про безопасный режим")) +
                    L.T("; also when it lost the graphics card after a driver update", "; и когда он потерял видеокарту после обновления драйвера"),
                    Toggle(cfg.RestartObsOnCrash, v => cfg.RestartObsOnCrash = v)), "crash краш");
                Add(Row("\uE74E", L.T("OBS settings backup", "Резервная копия настроек OBS"),
                    L.T("Once a day into the backups folder, the last 7 copies are kept", "Раз в день в папку backups, хранятся 7 последних копий"),
                    Toggle(cfg.Backup, v => cfg.Backup = v)), "backup бэкап");
            }
        }

        // ── Replay buffer (with the guard only) ─────────────────────────────
        void BuildReplayBuffer()
        {
            Card(null);
            Add(Row("\uE916", L.T("I record with the replay buffer", "Записываю буфером повтора"),
                L.T("Off — for regular recording: no buffer checks, OBS starts without the buffer", "Выключи, если пишешь обычной записью: буфер не проверяется, OBS запускается без него"),
                Toggle(cfg.UseReplayBuffer, v => { cfg.UseReplayBuffer = v; Changed(true); })), "replay buffer", true, RowReplayBuffer);
            if (cfg.UseReplayBuffer)
            {
                Add(Row("\uE7C8", L.T("The replay buffer must always run", "Буфер повтора должен работать всегда"),
                    L.T("Alarm if it is not running a minute after OBS starts", "Тревога, если он не запущен через минуту после старта OBS"),
                    Toggle(cfg.ReplayBufferMustRun, v => cfg.ReplayBufferMustRun = v)), "replay buffer");
                Add(Row("\uE72C", L.T("Restart the buffer after an error", "Перезапускать буфер после ошибки"),
                    L.T("Up to 5 attempts; 3 crashes in 10 minutes — alarm right away", "До 5 попыток; если падает 3 раза за 10 минут — сразу тревога"),
                    Toggle(cfg.RbAutoRestart, v => cfg.RbAutoRestart = v)), "replay buffer");
            }
        }

        TextBlock obsExe;

        // ── "Start ClipKeeper together with OBS" (ObsScript): the toggle, where it stands, and "Restart OBS" when it waits ──
        TextBlock obsScriptStatus;
        Button obsScriptRestart;

        void BuildObsScriptRow()
        {
            var desc = new StackPanel();
            desc.Children.Add(new TextBlock
            {
                Style = S("SubText"), FontSize = 12.5,
                Text = L.T("However OBS is started — a shortcut, Steam, autostart — ClipKeeper starts too. A small script is added to OBS " +
                           "(you see it in OBS → Tools → Scripts); turning this off removes it",
                           "Как бы ни запустили OBS — с ярлыка, из Steam, автозапуском — ClipKeeper запустится тоже. В OBS добавляется маленький скрипт " +
                           "(он виден в OBS → Сервис → Скрипты); если выключить — он уберётся"),
            });
            obsScriptStatus = new TextBlock { Style = S("SubText"), FontSize = 12.5, Margin = new Thickness(0, 6, 0, 0) };
            desc.Children.Add(obsScriptStatus);
            obsScriptRestart = Btn("\uE72C", L.T("Restart OBS now", "Перезапустить OBS сейчас"), "BtnGhost");
            obsScriptRestart.HorizontalAlignment = HorizontalAlignment.Left;
            obsScriptRestart.Margin = new Thickness(0, 8, 0, 0);
            obsScriptRestart.ToolTip = L.T("OBS closes and starts again by itself; the replay buffer starts over", "OBS закроется и сам запустится снова; буфер повтора начнётся заново");
            ConfirmClick(obsScriptRestart, L.T("The replay buffer starts over — press again", "Буфер повтора начнётся заново — нажми ещё раз"), () =>
            {
                if (app == null) return;
                app.Guard.RequestRestartObs();   // the script is added between OBS closing and starting (Guard.LaunchObs)
                obsScriptStatus.Text = L.T("Restarting OBS…", "Перезапускаю OBS…");
                obsScriptRestart.Visibility = Visibility.Collapsed;
            });
            desc.Children.Add(obsScriptRestart);
            Add(Row("\uE768", L.T("Start ClipKeeper together with OBS", "Запускать ClipKeeper вместе с OBS"), desc,
                Toggle(cfg.ObsStartScript, v =>
                {
                    cfg.ObsStartScript = v;
                    Log.Write("start with OBS: " + (v ? "on" : "off"));
                    obsScriptStatus.Text = v ? L.T("Adding…", "Добавляю…") : L.T("Removing…", "Убираю…");
                    if (app != null) app.SyncObsScript();
                })), "obs script start launch together скрипт запуск вместе", true, RowObsScript);
            ObsScriptChanged();
        }

        public void ObsScriptChanged()
        {
            if (obsScriptStatus == null) return;
            var st = app != null ? app.ObsScriptState : ObsScript.State.Off;
            bool on = cfg.ObsStartScript;
            string text = null;
            Color? color = null;
            bool restart = false;
            switch (st)
            {
                case ObsScript.State.On:
                    text = L.T("✓ Added — from now on ClipKeeper starts whenever OBS starts", "✓ Добавлен — теперь ClipKeeper запускается вместе с OBS");
                    color = Wpf.Ok;
                    break;
                case ObsScript.State.Pending:
                    text = on ? L.T("Waits for OBS to close — OBS keeps its files while it runs. It will be added by itself the moment OBS closes, or restart OBS now:",
                                    "Ждёт закрытия OBS — пока OBS открыт, его файлы трогать нельзя. Скрипт добавится сам, как только OBS закроется, или перезапусти OBS сейчас:")
                              : L.T("Will be removed by itself the moment OBS closes, or restart OBS now:",
                                    "Уберётся сам, как только OBS закроется, или перезапусти OBS сейчас:");
                    color = Wpf.Warn;
                    restart = app != null && app.Guard.ObsExe() != null;
                    break;
                case ObsScript.State.NoObs:
                    if (on) { text = L.T("OBS settings were not found — start OBS once, then it is added", "Настройки OBS не найдены — запусти OBS один раз, и скрипт добавится"); color = Wpf.Warn; }
                    break;
                case ObsScript.State.Error:
                    text = L.T("Could not change the OBS files: ", "Не получилось изменить файлы OBS: ") + (app != null ? app.ObsScriptError : "") + L.T(" (details in the log)", " (подробности в журнале)");
                    color = Wpf.Bad;
                    break;
            }
            obsScriptStatus.Text = text ?? "";
            obsScriptStatus.Visibility = text != null ? Visibility.Visible : Visibility.Collapsed;
            obsScriptStatus.Foreground = color != null ? Wpf.Br(color.Value, 255) : Wpf.Res<Brush>("Sub");
            obsScriptRestart.Visibility = restart ? Visibility.Visible : Visibility.Collapsed;
        }

        void UpdateObsExe()
        {
            string found = app != null ? app.Guard.ObsExe() : null;
            obsExe.Text = !string.IsNullOrEmpty(cfg.ObsPath) ? cfg.ObsPath
                        : found != null ? L.T("found automatically: ", "найдена автоматически: ") + found
                        : L.T("not found — choose obs64.exe so ClipKeeper can start OBS", "не найдена — укажи obs64.exe, чтобы ClipKeeper мог запускать OBS");
        }

        void PickObsExe()
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "OBS (obs64.exe)|obs64.exe|" + L.T("Programs", "Программы") + " (*.exe)|*.exe", Title = L.T("OBS program", "Программа OBS") };
            try { dlg.InitialDirectory = @"C:\Program Files\obs-studio\bin\64bit"; } catch { }
            if (dlg.ShowDialog(W) != true) return;
            cfg.ObsPath = dlg.FileName;
            Save();
            UpdateObsExe();
        }

        void UpdatePwState()
        {
            pwState.Text = cfg.HasPassword ? L.T("Saved encrypted. Enter a new one to replace it", "Сохранён в зашифрованном виде. Введи новый, чтобы заменить") : L.T("Not set", "Не задан");
            pwState.Foreground = Wpf.Res<Brush>("Sub");
        }

        void SaveConnection()
        {
            cfg.Host = host.Text.Trim().Length > 0 ? host.Text.Trim() : "127.0.0.1";
            int n;
            if (int.TryParse(port.Text.Trim(), out n) && n > 0 && n < 65536) cfg.Port = n;
            if (pw.Password.Length > 0) { cfg.SetPassword(pw.Password); pw.Clear(); }
            Save();
            UpdatePwState();
            if (app != null) { app.Guard.Reconnect(); app.ShowToast(L.T("✓ Connection settings saved", "✓ Настройки подключения сохранены")); }
        }

        // ── Checks ──────────────────────────────────────────────────────────
        void BuildChecks()
        {
            Card(L.T("Recording", "Запись"));
            Add(Row("\uE767", L.T("Audio reaches OBS", "Звук доходит до OBS"),
                L.T("If Windows plays audio on the device but OBS gets nothing — restart the source; if that fails — alarm", "Если в Windows на устройстве звук есть, а OBS его не получает — перезапустить источник, не помогло — тревога"),
                Toggle(cfg.AudioStallCheck, v => cfg.AudioStallCheck = v)), "sound звук");
            Add(Row("\uE7F4", L.T("Picture in capture", "Картинка в захвате"),
                L.T("A black, white or single-color frame while the monitor shows a picture", "Чёрный, белый или одноцветный кадр, когда на мониторе есть изображение"),
                Toggle(cfg.ScreenCheck, v => cfg.ScreenCheck = v)), "black screen чёрный экран");
            Add(Row("\uE72C", L.T("Restart capture when monitors change", "Перезапускать захват при смене мониторов"),
                L.T("When a monitor is reconnected or moved to another port", "Когда монитор переподключили или поменяли порт"),
                Toggle(cfg.KickMonitor, v => cfg.KickMonitor = v)), "display дисплей");
            Add(Row("\uE713", L.T("Tracks, volume and mute", "Дорожки, громкость и «выкл. звук»"),
                L.T("Tracks are restored automatically; volume and mute only get a warning", "Дорожки возвращаются сами; о громкости и выключенном звуке — только предупреждение"),
                Toggle(cfg.GuardMixer, v => cfg.GuardMixer = v)), "mixer микшер");
            Add(Row("\uE9D9", L.T("Dropped frames", "Потерянные кадры"),
                L.T("Warn if OBS can't keep up rendering or encoding and drops frames (clips stutter); the clip check shows how many were lost", "Предупредить, если OBS не успевает рендерить или кодировать и теряет кадры (клипы дёргаются); в проверке клипа — сколько кадров пропало"),
                Toggle(cfg.PerfCheck, v => cfg.PerfCheck = v)), "fps lag лаги", true, RowFrames);
            Add(Row("\uE7BA", L.T("Graphics driver failure", "Сбой драйвера видеокарты"),
                L.T("Warn when Windows reports a driver reset or install (after the program restarts)", "Предупредить, когда Windows сообщает о сбросе или установке драйвера (после перезапуска программы)"),
                Toggle(cfg.DriverWatch, v => cfg.DriverWatch = v)), "gpu видеокарта");

            Card(L.T("Clips", "Клипы"));
            Add(Row("\uE714", L.T("Check saved clips", "Проверять сохранённые клипы"),
                L.T("Length and track count; tells you if the clip was cut short by the memory limit", "Длина и число дорожек; подскажет, если клип обрезан лимитом памяти"),
                Toggle(cfg.ClipCheck, v => cfg.ClipCheck = v)));
            Add(Row("\uEDA2", L.T("Free disk space", "Свободное место на диске"),
                L.T("Alarm if less is left for clips", "Тревога, если для клипов осталось меньше"),
                SliderBox(0, 100, cfg.MinFreeGB, 5, v => v == 0 ? L.T("off", "выкл.") : v + L.T(" GB", " ГБ"), v => cfg.MinFreeGB = v, null)), "disk диск", true, RowDisk);

        }

        // ── "Clip saved": the card, the sound, the instant answer to the key ──
        void BuildClipSaved()
        {
            Card(null);
            Add(Row("\uE73E", L.T("Show the card", "Показывать карточку"),
                L.T("A card in the top right corner: a frame, the game, length and size, the folder it went to; open, trim, copy. A clip with a problem is always shown",
                    "Карточка в правом верхнем углу: кадр, игра, длина и размер, в какую папку попал; открыть, обрезать, скопировать. Клип с проблемой показывается всегда"),
                Toggle(cfg.ClipToast, v => cfg.ClipToast = v)), "clip saved notification toast клип сохранён уведомление");
            Add(Row("\uE767", L.T("Sound", "Звук"),
                L.T("A short sound when a clip is saved, with or without the card — not the alarm one. A clip with a problem sounds like the alarm",
                    "Короткий звук, когда клип сохранён, — с карточкой или без неё и не тот, что у тревоги. Клип с проблемой звучит как тревога"),
                Toggle(cfg.ClipSound, v => cfg.ClipSound = v)), "clip saved sound клип сохранён звук", true, RowClipSound);
            string obsKey = app != null ? app.Guard.SaveKeyText : null;
            Add(Row("\uE945", L.T("Confirm the press at once", "Подтверждать нажатие сразу"),
                L.T("OBS reports a clip only once the file is written — a second or more. ClipKeeper watches the same key and answers right away: the sound and \"Saving the clip…\". Nothing is taken from OBS. ",
                    "OBS сообщает о клипе, только когда допишет файл, — секунду и больше. ClipKeeper следит за той же клавишей и отвечает сразу: звук и «Сохраняю клип…». У OBS ничего не отнимается. ") +
                (obsKey != null ? L.T("The key in OBS: ", "Клавиша в OBS: ") + obsKey
                                : L.T("The key is not found yet: it is read from the current OBS profile after connecting (OBS → Settings → Hotkeys → Replay Buffer → Save Replay)",
                                      "Клавиша пока не найдена: она читается из текущего профиля OBS после подключения (OBS → Настройки → Горячие клавиши → Буфер повтора → Сохранить повтор)")),
                Toggle(cfg.ClipInstant, v => cfg.ClipInstant = v)), "instant fast delay key press сразу быстро задержка клавиша нажатие");
            Card(L.T("Sound", "Звук"));
            SoundRows(() => cfg.ClipSoundFile, f => cfg.ClipSoundFile = f, () => cfg.ClipSoundVolume, v => cfg.ClipSoundVolume = v, true,
                      L.T("\"Clip saved\" sound", "Звук «Клип сохранён»"), "clip saved клип сохранён");
        }

        // ── Alarm ───────────────────────────────────────────────────────────
        void BuildAlarm()
        {
            Card(L.T("When", "Когда"));
            Add(Row("\uE916", L.T("Wait before the alarm", "Ждать перед тревогой"),
                L.T("How long a failure must last: audio software (SteelSeries GG, Voicemeeter…) may drop devices for a couple of seconds while updating", "Сколько сбой должен продержаться: звуковые программы (SteelSeries GG, Voicemeeter…) при обновлении теряют устройства на пару секунд"),
                SliderBox(0, 60, cfg.GraceSec, 1, v => v + L.T(" s", " с"), v => cfg.GraceSec = v, null)), "delay grace задержка");
            Add(Row("\uE73E", L.T("Tell me when things fixed themselves", "Сообщать, когда всё починилось само"),
                L.T("A green window for 7 seconds", "Зелёное окно на 7 секунд"),
                Toggle(cfg.NotifyFixed, v => cfg.NotifyFixed = v)), "recovery восстановление");

            Card(L.T("How", "Как"));
            Add(Row("\uE7BA", L.T("A window on top of everything", "Окно поверх всех окон"),
                L.T("Does not steal focus from the game and is not recorded", "Не забирает фокус у игры и не попадает в запись"),
                Toggle(cfg.AlertWindow, v => cfg.AlertWindow = v)), "popup окно");
            Add(Row("\uE767", L.T("Sound", "Звук"), L.T("Audible even in a fullscreen game where the window is not visible", "Слышно и в полноэкранной игре, где окно не видно"),
                Toggle(cfg.AlertSound, v => cfg.AlertSound = v)), "audio");

            SoundRows(() => cfg.SoundFile, f => cfg.SoundFile = f, () => cfg.SoundVolume, v => cfg.SoundVolume = v, false,
                      L.T("Alarm sound", "Звук тревоги"), "alarm тревога");
            Add(Row("\uE72C", L.T("Repeat the sound", "Повторять звук"), L.T("Until you press \"Got it\"", "Пока не нажмёшь «Понял»"),
                SliderBox(0, 60, cfg.SoundRepeatSec, 5, v => v == 0 ? L.T("once", "один раз") : v + L.T(" s", " с"), v => cfg.SoundRepeatSec = v, null)));

            Card(null);
            Add(Row("\uEA8F", L.T("Try it", "Проверить"), L.T("Shows the alarm and plays the sound as for a real failure", "Покажет тревогу и сыграет звук, как при настоящем сбое"),
                RowButton(null, L.T("Test alarm", "Тест тревоги"), "BtnPrimary", () => { if (app != null) app.TestAlert(); })), "test тест");
        }

        // the sound (built-in ones, the user's own, "Upload…") and its volume with "Listen" — the same rows for the alarm and for "Clip saved".
        // A click on a sound picks it and plays it
        void SoundRows(Func<string> file, Action<string> setFile, Func<int> volume, Action<int> setVolume, bool chime, string title, string words)
        {
            Slider slider = null;
            Func<int> vol = () => slider != null ? (int)slider.Value : volume();
            var chips = new WrapPanel { Margin = new Thickness(0, 0, 0, 4) };
            var note = new TextBlock { Style = S("SubText"), FontSize = 12.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 10) };
            string hint = Ffmpeg.Available
                ? L.T("Your own: any audio file (WAV, MP3, OGG, M4A…), the first " + Alarm.MaxSec + " s are kept. Right click on it — delete",
                      "Свой: любой аудиофайл (WAV, MP3, OGG, M4A…), берутся первые " + Alarm.MaxSec + " с. Правый клик по нему — удалить")
                : L.T("Your own: a WAV file (other formats need ffmpeg). Right click on it — delete",
                      "Свой: файл WAV (другие форматы — с ffmpeg). Правый клик по нему — удалить");
            Action fill = null;
            Action<string> choose = f =>
            {
                setFile(f);
                Save();
                fill();
                Alarm.Preview(f, vol(), chime);
            };
            Action upload = () =>
            {
                var dlg = new Microsoft.Win32.OpenFileDialog
                {
                    Title = title,
                    Filter = Ffmpeg.Available ? L.T("Audio", "Аудио") + "|*.wav;*.mp3;*.ogg;*.opus;*.flac;*.m4a;*.aac;*.wma|" + L.T("All files", "Все файлы") + "|*.*"
                                              : "WAV (*.wav)|*.wav",
                };
                if (dlg.ShowDialog(W) != true) return;
                string err, dst;
                Mouse.OverrideCursor = Cursors.Wait;
                try { dst = Alarm.Import(dlg.FileName, out err); }
                finally { Mouse.OverrideCursor = null; }
                if (dst != null) choose(dst);
                else note.Text = L.T("Not added: ", "Не добавлен: ") + err;
            };
            fill = () =>
            {
                chips.Children.Clear();
                string cur = file();
                bool found = false;
                Func<string, string, string, System.Windows.Controls.Primitives.ToggleButton> chip = (value, text, tip) =>
                {
                    bool on = string.Equals(value, cur, StringComparison.OrdinalIgnoreCase);
                    found |= on;
                    var b = new System.Windows.Controls.Primitives.ToggleButton { Style = S("Chip"), Content = text, IsChecked = on, ToolTip = tip, Margin = new Thickness(0, 0, 6, 6) };
                    b.Click += (s, e) => choose(value);
                    chips.Children.Add(b);
                    return b;
                };
                foreach (var id in chime ? Alarm.ClipSounds : Alarm.AlarmSounds) chip(Alarm.Builtin + id, Alarm.Name(id), null);
                var own = Alarm.Uploaded().ToList();
                if (!Alarm.IsBuiltin(cur) && File.Exists(cur) && !Alarm.InFolder(cur)) own.Insert(0, cur);   // a file chosen before uploads existed
                foreach (var f in own)
                {
                    string path = f;
                    var b = chip(path, Path.GetFileNameWithoutExtension(path), path);
                    if (!Alarm.InFolder(path)) continue;
                    var menu = DarkMenu();
                    menu.Items.Add(Item(L.T("Delete", "Удалить"), () => DeleteSound(path)));
                    b.ContextMenu = menu;
                }
                if (!found) ((System.Windows.Controls.Primitives.ToggleButton)chips.Children[0]).IsChecked = true;   // what actually plays: the default one
                var addSp = new StackPanel { Orientation = Orientation.Horizontal };
                addSp.Children.Add(new TextBlock { Style = S("Icon"), Text = "", FontSize = 11, Margin = new Thickness(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center });
                addSp.Children.Add(new TextBlock { Text = L.T("Upload…", "Загрузить…") });
                // looks like the sounds, but is a button: it never stays pressed
                var add = new System.Windows.Controls.Primitives.ToggleButton { Style = S("Chip"), Content = addSp, Margin = new Thickness(0, 0, 6, 6) };
                add.Click += (s, e) => { add.IsChecked = false; upload(); };
                chips.Children.Add(add);
                note.Text = !found && !string.IsNullOrEmpty(cur) && !Alarm.IsBuiltin(cur)
                    ? L.T("The file is not found: ", "Файл не найден: ") + Path.GetFileName(cur) + L.T(" — the built-in one plays", " — играет встроенный")
                    : hint;
            };
            fill();
            var desc = new StackPanel();
            desc.Children.Add(note);
            desc.Children.Add(chips);
            Add(Row("", title, desc, null), "sound file wav mp3 upload звук файл загрузить свой " +
                string.Join(" ", (chime ? Alarm.ClipSounds : Alarm.AlarmSounds).Select(Alarm.Name)) + " " + words);

            var listen = Btn("", null, "BtnIcon");
            listen.Margin = new Thickness(10, 0, 0, 0);
            listen.ToolTip = L.T("Listen", "Прослушать");
            listen.Click += (s, e) => Alarm.Preview(file(), vol(), chime);
            var volBox = SliderBox(0, 100, volume(), 5, v => v + "%", setVolume, listen);
            slider = (Slider)volBox.Tag;
            Add(Row("", L.T("Volume", "Громкость"), null, volBox), "sound звук " + words);
        }

        // an uploaded sound is deleted; whatever used it goes back to its default
        void DeleteSound(string path)
        {
            try { File.Delete(path); }
            catch (Exception ex) { Log.Write("sound not deleted: " + ex.Message); return; }
            if (string.Equals(cfg.SoundFile, path, StringComparison.OrdinalIgnoreCase)) cfg.SoundFile = Alarm.DefaultAlarm;
            if (string.Equals(cfg.ClipSoundFile, path, StringComparison.OrdinalIgnoreCase)) cfg.ClipSoundFile = Alarm.DefaultClip;
            Log.Write("sound deleted: " + Path.GetFileName(path));
            Save();
            RebuildSettings();
        }

        // ── Library folders: sources, ready, collection ─────────────────────
        void BuildFolders()
        {
            Card(null);
            Func<TextBlock> path = () => new TextBlock { Style = S("SubText"), FontSize = 12, FontFamily = Wpf.Res<FontFamily>("MonoFont"), TextTrimming = TextTrimming.CharacterEllipsis };
            obsPath = path();
            readyPath = path();
            collPath = path();
            UpdateFolderTexts();
            Add(Row("\uE714", L.T("Sources", "Исходники"), obsPath, null), "folder папка obs");
            Add(Row("\uE8C6", L.T("Ready — the editor saves here too", "Готовые — сюда же сохраняет редактор"), readyPath,
                RowButton(null, L.T("Choose…", "Выбрать…"), "BtnGhost", () => PickSettingsFolder(SrcReady))), "folder папка trim");
            Add(Row("\uE8F1", L.T("Collection", "Коллекция"),
                L.T("A third folder for clips that made it into a video", "Третья папка — для клипов, вошедших в выпуск"),
                Toggle(cfg.UseCollection, v => { cfg.UseCollection = v; Changed(true); })), "folder папка");
            if (cfg.UseCollection)
                Add(Row("\uE8B7", L.T("Collection folder", "Папка коллекции"), collPath,
                    RowButton(null, L.T("Choose…", "Выбрать…"), "BtnGhost", () => PickSettingsFolder(SrcCollection))), "folder папка");

        }

        // ── hotkeys in game: what to do with the last clip ──────────────────
        void BuildHotkeys()
        {
            Card(null);
            hotkeyShows.Clear();
            Add(Row("\uE946", L.T("How to set", "Как задать"),
                L.T("Click a field and press the keys: two modifiers (Ctrl+Shift+K) or one with an F key (Ctrl+F9). Esc — cancel, Backspace — clear",
                    "Нажми на поле и затем клавиши: два модификатора (Ctrl+Shift+K) или один с F-клавишей (Ctrl+F9). Esc — отмена, Backspace — очистить"), null),
                "hotkey горячие клавиши");
            HotkeyRow("\uE8C6", L.T("Trim the last clip", "Обрезать последний клип"), L.T("Opens it in the editor", "Открывает его в редакторе"), "HotkeyTrim",
                      () => cfg.HotkeyTrim, v => cfg.HotkeyTrim = v);
            HotkeyRow("\uE734", L.T("Last clip to favorites", "Последний клип в избранное"), L.T("Press again to take it out", "Повторное нажатие убирает"), "HotkeyFav",
                      () => cfg.HotkeyFav, v => cfg.HotkeyFav = v);
            HotkeyRow("\uE8C8", L.T("Copy the last clip", "Скопировать последний клип"), L.T("Then Ctrl+V into Discord or Telegram", "Потом Ctrl+V в Discord или Telegram"), "HotkeyCopy",
                      () => cfg.HotkeyCopy, v => cfg.HotkeyCopy = v);
            HotkeyRow("\uE714", L.T("Show the last clip again", "Показать последний клип ещё раз"), L.T("Its \"Clip saved\" card", "Его карточку «Клип сохранён»"), "HotkeyCard",
                      () => cfg.HotkeyCard, v => cfg.HotkeyCard = v);

        }

        // ── games and covers in the library ─────────────────────────────────
        void BuildCovers()
        {
            Card(null);
            Add(Row("\uE8B7", L.T("Subfolders of the OBS folder are games", "Подпапки в папке OBS — это игры"),
                L.T("Sorting by game puts clips into Game\\Month folders. Turn it off if you sort clips another way (by date…) — then the game comes from the file name",
                    "Раскладка по играм кладёт клипы в папки Игра\\Месяц. Выключи, если раскладываешь иначе (по датам…) — тогда игра берётся из имени файла"),
                Toggle(cfg.SubfoldersAreGames, v => { cfg.SubfoldersAreGames = v; Changed(false); })), "game игра folder папка");
            Add(Row("\uE774", L.T("Covers from the internet", "Обложки из интернета"),
                L.T("Game covers and banners from Steam and Wikipedia. Off — only your own pictures and frames from clips",
                    "Обложки и баннеры игр из Steam и Википедии. Выключено — только свои картинки и кадры из клипов"),
                Toggle(cfg.OnlineCovers, v => { cfg.OnlineCovers = v; Changed(false); })), "steam art арт");
        }

        // ── cleanup of old source clips (Cleanup): first "Check" shows what would go, then a double press moves it ──
        Cleanup.Plan cleanupPlan;

        void BuildCleanup()
        {
            Card(null);
            var status = new TextBlock { Style = S("SubText"), FontSize = 12.5 };
            var check = Btn(null, L.T("Check", "Проверить"), "BtnGhost");
            var move = Btn("\uE74D", L.T("To the Recycle Bin", "В корзину"), "BtnPrimary");
            move.Margin = new Thickness(6, 0, 0, 0);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal };
            buttons.Children.Add(check);
            buttons.Children.Add(move);
            Action<string, Hyperlink> show = (text, link) =>
            {
                status.Inlines.Clear();
                status.Inlines.Add(new Run(text));
                if (link != null) { status.Inlines.Add(new Run(" · ")); status.Inlines.Add(link); }
                move.Visibility = cleanupPlan != null && cleanupPlan.Files.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            };
            Action reset = () =>
            {
                cleanupPlan = null;
                show(L.T("Nothing is moved until you press \"To the Recycle Bin\" — first see what would go", "Ничего не перемещается, пока не нажмёшь «В корзину», — сначала посмотри, что уйдёт"), null);
            };

            Add(Row("\uE787", L.T("Keep clips for", "Хранить клипы"),
                L.T("Counted from when the clip was saved or copied into the folder, whichever is later", "Считается от сохранения клипа или его копирования в папку — что позже"),
                SliderBox(7, 365, cfg.CleanupDays, 1, v => v + L.T(" days", " дн."), v => { cfg.CleanupDays = v; reset(); }, null)),
                "cleanup delete old days уборка удалить старые дни");
            Add(Row("\uE8C6", L.T("Keep sources of trimmed clips", "Не трогать исходники обрезанных клипов"),
                L.T("A clip you have trimmed stays, so you can trim it again", "Клип, из которого ты уже сделал обрезку, остаётся — чтобы обрезать заново"),
                Toggle(cfg.CleanupKeepTrimmed, v => { cfg.CleanupKeepTrimmed = v; reset(); })), "cleanup trim уборка обрезка");
            Add(Row("\uE916", L.T("Clean up by itself once a day", "Убирать самому раз в день"),
                L.T("Only to the Recycle Bin and only old clips in the OBS folder. Never: favorites, trims, the Ready and Collection folders. " +
                    "If more than " + Cleanup.AutoMaxFiles + " clips or half of the folder would go, it stops and asks you",
                    "Только в корзину и только старые клипы в папке OBS. Никогда: избранное, обрезки, папки «Готовые» и «Коллекция». " +
                    "Если уйти должно больше " + Cleanup.AutoMaxFiles + " клипов или половина папки — остановится и спросит"),
                Toggle(cfg.CleanupAuto, v =>
                {
                    cfg.CleanupAuto = v;
                    if (v) cfg.CleanupLastRun = DateTime.Now.ToString("yyyy-MM-dd HH:mm");   // the first run — in a day, not right now
                    Log.Write("auto cleanup " + (v ? "on, " + cfg.CleanupDays + " days" : "off"));
                })), "cleanup auto уборка автоматически");
            Add(Row("\uE74D", L.T("Clean up now", "Убрать сейчас"), status, buttons), "cleanup recycle bin delete уборка корзина удалить");
            reset();

            check.Click += (s, e) =>
            {
                if (app == null) return;
                check.IsEnabled = false;
                cleanupPlan = null;
                show(L.T("Looking through the folder…", "Просматриваю папку…"), null);
                System.Threading.Tasks.Task.Factory.StartNew(() => app.PlanCleanup()).ContinueWith(t => W.Dispatcher.BeginInvoke(new Action(() =>
                {
                    check.IsEnabled = true;
                    if (t.Exception != null) { show(t.Exception.InnerException.Message, null); return; }
                    var p = t.Result;
                    if (p.Refusal != null) { show(L.T("Can't clean up: ", "Убрать нельзя: ") + p.Refusal, null); return; }
                    cleanupPlan = p;
                    if (p.Files.Count == 0) { show(L.T("Nothing to clean up: ", "Убирать нечего: ") + p.Total + L.T(" clips, none is old enough or all are kept", " клипов, старых нет или все под защитой"), null); return; }
                    try { Cleanup.WriteList(p); } catch (Exception ex) { Log.Write("cleanup list: " + ex.Message); }
                    show(p.Files.Count + L.T(" of ", " из ") + p.Total + L.T(" clips (", " клипов (") + Fmt.Size(p.Bytes) + L.T(") would go to the Recycle Bin", ") уйдут в корзину"),
                         Link(L.T("see the list", "посмотреть список"), Cleanup.ListPath));
                })));
            };
            ConfirmClick(move, L.T("Sure? Press again", "Точно? Нажми ещё раз"), () =>
            {
                var plan = cleanupPlan;
                if (app == null || plan == null) return;
                move.IsEnabled = check.IsEnabled = false;
                show(L.T("Moving to the Recycle Bin…", "Перемещаю в корзину…"), null);
                System.Threading.Tasks.Task.Factory.StartNew(() => Cleanup.Run(plan, Favorites.Has, false)).ContinueWith(t => W.Dispatcher.BeginInvoke(new Action(() =>
                {
                    move.IsEnabled = check.IsEnabled = true;
                    cleanupPlan = null;
                    if (t.Exception != null) { show(t.Exception.InnerException.Message, null); return; }
                    var r = t.Result;
                    show(r.Moved + L.T(" clips (", " клипов (") + Fmt.Size(r.Bytes) + L.T(") are in the Recycle Bin", ") в корзине") +
                         (r.Kept > 0 ? L.T(", kept: ", ", оставлено: ") + r.Kept : "") +
                         (r.Stopped ? L.T(". Stopped: Windows would delete for good, not into the Recycle Bin", ". Остановлено: Windows удалила бы насовсем, а не в корзину") : ""),
                         Link(L.T("what went", "что ушло"), Cleanup.LogPath));
                    if (app != null) app.ClipsChanged();
                })));
            });
        }

        // ── hotkey fields: click, press the combination (with Ctrl, Alt, Shift or Win); Esc — cancel, Backspace — clear ──
        readonly List<Action> hotkeyShows = new List<Action>();

        void HotkeyRow(string glyph, string title, string desc, string setting, Func<string> get, Action<string> set)
        {
            var status = new TextBlock { Style = S("SubText"), FontSize = 12.5 };
            var field = new Button { Style = S("BtnGhost"), MinWidth = 160 };
            bool capturing = false;
            string hint = null;
            Action show = () =>
            {
                string err = null;
                if (!capturing && app != null && app.Keys != null) app.Keys.Errors.TryGetValue(setting, out err);
                string text = hint ?? err;
                status.Text = desc + (text != null ? " — " + text : "");
                status.Foreground = text != null ? Wpf.Br(hint != null ? Wpf.Warn : Wpf.Bad, 255) : Wpf.Res<Brush>("Sub");
                field.Content = capturing ? L.T("Press the keys…", "Нажми клавиши…") : (get() == "" ? L.T("Not set", "Не задана") : get());
            };
            Action<bool, string> finish = (changed, value) =>
            {
                capturing = false;
                hint = null;
                if (changed) { set(value); Save(); Log.Write("hotkey " + setting + ": " + (value == "" ? "none" : value)); }
                if (app != null) app.ApplyHotkeys();
                foreach (var a in hotkeyShows) a();
            };
            field.Click += (s, e) =>
            {
                if (capturing) return;
                capturing = true;
                hint = L.T("Esc — cancel, Backspace — clear", "Esc — отмена, Backspace — очистить");
                if (app != null) app.ApplyHotkeys(true);   // the current ones must not fire while you press
                show();
            };
            field.LostKeyboardFocus += (s, e) => { if (capturing) finish(false, null); };
            field.PreviewKeyDown += (s, e) =>
            {
                if (!capturing) return;
                e.Handled = true;
                var key = e.Key == Key.System ? e.SystemKey : e.Key;
                var mods = Keyboard.Modifiers;
                if (mods == ModifierKeys.None && key == Key.Escape) { finish(false, null); return; }
                if (mods == ModifierKeys.None && (key == Key.Back || key == Key.Delete)) { finish(true, ""); return; }
                if (Hotkey.IsModifier(key)) return;   // wait for the key itself
                var hk = new Hotkey { Mods = mods, Key = key };
                string why = hk.Refusal();
                if (why != null) { hint = why; show(); return; }
                string combo = hk.ToString();
                var others = new[] { cfg.HotkeyTrim, cfg.HotkeyFav, cfg.HotkeyCopy, cfg.HotkeyCard };
                if (combo != get() && others.Contains(combo))
                {
                    hint = combo + L.T(" is already used for another action", " уже стоит на другом действии");
                    show();
                    return;
                }
                finish(true, combo);
            };
            hotkeyShows.Add(show);
            show();
            Add(Row(glyph, title, status, field), "hotkey shortcut key горячие клавиши сочетание");
        }

        void UpdateFolderTexts()
        {
            if (obsPath == null) return;
            string obs = RootOf(last);
            obsPath.Text = obs != null ? obs + L.T("   (recording folder from OBS settings)", "   (папка записи из настроек OBS)") : L.T("known after connecting to OBS", "станет известна после подключения к OBS");
            readyPath.Text = string.IsNullOrEmpty(cfg.TrimFolder) ? L.T("not chosen — trims are saved next to the source", "не выбрана — обрезки сохраняются рядом с исходником") : cfg.TrimFolder;
            collPath.Text = string.IsNullOrEmpty(cfg.CollectionFolder) ? L.T("not chosen", "не выбрана") : cfg.CollectionFolder;
        }

        void PickSettingsFolder(int kind)
        {
            string cur = kind == SrcReady ? cfg.TrimFolder : cfg.CollectionFolder;
            string chosen = FolderPicker.Pick(W, kind == SrcReady ? L.T("Ready clips folder (the editor saves here too)", "Папка готовых клипов (сюда же сохраняет редактор)") : L.T("Clip collection folder", "Папка коллекции клипов"),
                                              Existing(cur) ?? RootOf(last));
            if (chosen == null) return;
            if (kind == SrcReady) cfg.TrimFolder = chosen;
            else cfg.CollectionFolder = chosen;
            Save();
            Log.Write("folder " + kind + ": " + chosen);
            UpdateFolderTexts();
            sourceChipsSig = null;
            ShowSourceChips();
            clipsLoadedAt = DateTime.MinValue;   // the library rereads the folder the next time it opens
        }

        // ── Editor: the video encoder ───────────────────────────────────────
        void BuildEncoder()
        {
            Card(null);
            var chips = new WrapPanel();
            foreach (var k in Encoders.All)
            {
                string code = k;
                var b = Btn(null, Encoders.Name(k), code == cfg.Encoder ? "BtnPrimary" : "BtnGhost");
                b.Margin = new Thickness(0, 0, 6, 4);
                b.Click += (s, e) =>
                {
                    if (code == cfg.Encoder) return;
                    cfg.Encoder = code;
                    Changed(true);
                };
                chips.Children.Add(b);
            }
            var found = new TextBlock { Style = S("SubText"), FontSize = 12.5, TextWrapping = TextWrapping.Wrap };
            Action show = () => found.Text = L.T("Frame-exact trims, cuts, sharing and the smooth preview. Found on this PC: ",
                                                 "Обрезка «По кадру», вырезы, отправка и плавный просмотр. На этом ПК найден: ") + Encoders.Name(Encoders.Detected) +
                                             L.T(". If the chosen one fails, the CPU takes over.", ". Если выбранный не сработает, пережмёт процессор.");
            if (Encoders.IsDetected) show();
            else
            {
                found.Text = L.T("Looking for a video encoder on this PC…", "Ищу видеокодировщик на этом ПК…");
                System.Threading.Tasks.Task.Factory.StartNew(() => Encoders.Detected).ContinueWith(t => W.Dispatcher.BeginInvoke(show));
            }
            found.Margin = new Thickness(0, 2, 0, 10);
            var desc = new StackPanel();
            desc.Children.Add(found);
            desc.Children.Add(chips);
            Add(Row("\uE7F4", L.T("Re-encode trims with", "Пережимать обрезки через"), desc, null), "encoder nvenc amf qsv gpu cpu кодек");

        }

        // ── Editor keys: the same map as in the editor on "?" ───────────────
        void BuildEditorKeys()
        {
            Card(null);
            var keys = new Border { Padding = new Thickness(22, 16, 22, 18), BorderBrush = Wpf.Res<Brush>("Line") };
            var keysBox = new StackPanel();
            keysBox.Children.Add(new TextBlock { Style = S("SubText"), FontSize = 12.5, Margin = new Thickness(0, 0, 0, 14),
                Text = L.T("Like in Premiere, and any key can be changed. In the editor the map opens with \"?\" or F1",
                           "Как в Premiere, любую можно поменять. В самом редакторе карта открывается по «?» или F1") });
            keysBox.Children.Add(new Border());
            keys.Child = keysBox;
            Action changed = null;
            changed = () =>
            {
                cfg.KeyBinds = KeyMap.Save();
                Save();
                ((Border)keysBox.Children[1]).Child = KeyMap.Build(changed);
            };
            ((Border)keysBox.Children[1]).Child = KeyMap.Build(changed);
            Add(keys, "keys hotkeys shortcuts keyboard premiere клавиши горячие сочетания клавиатура", false);
        }
    }
}
