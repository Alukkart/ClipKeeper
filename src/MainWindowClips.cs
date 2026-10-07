using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace DeviceGuard
{
    // File actions: open, show in Explorer, copy, move to the Recycle Bin
    static class Shell
    {
        public static void Open(string path)
        {
            try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
            catch (Exception ex) { Log.Write("could not open " + path + ": " + ex.Message); }
        }

        public static void Select(string path)
        {
            try { Process.Start("explorer.exe", "/select,\"" + path + "\""); }
            catch (Exception ex) { Log.Write("could not show " + path + ": " + ex.Message); }
        }

        // the file goes to the clipboard — then Ctrl+V straight into Discord / Telegram / Explorer
        public static bool CopyFile(string path)
        {
            try
            {
                Clipboard.SetFileDropList(new StringCollection { path });
                return true;
            }
            catch (Exception ex) { Log.Write("could not copy " + path + ": " + ex.Message); return false; }
        }

        public static bool Recycle(string path)
        {
            try
            {
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(path, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                                                                   Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                return true;
            }
            catch (Exception ex) { Log.Write("could not delete " + path + ": " + ex.Message); return false; }
        }
    }

    // The clip library. Three folders follow a clip's path:
    //   Sources (OBS recordings) → Ready (trims waiting for a video) → Collection (made it into a video).
    // Each has a home screen with game cards (cover, clip count, total length); inside a game — a banner and clips.
    partial class MainWindow
    {
        const string FavKey = "*fav", AllKey = "*all";
        const int SrcObs = 0, SrcReady = 1, SrcCollection = 2;
        static string[] SrcNames { get { return new[] { L.T("Sources", "Исходники"), L.T("Ready", "Готовые"), L.T("Collection", "Коллекция") }; } }
        static readonly string[] SrcGlyphs = { "", "", "" };
        readonly ObservableCollection<ClipVm> clips = new ObservableCollection<ClipVm>();
        readonly ObservableCollection<GameCardVm> games = new ObservableCollection<GameCardVm>();
        static readonly Brush GlyphGrey = Wpf.Br("#55575F"), GlyphStar = Wpf.Br(Wpf.Warn, 255);
        ItemsControl clipsList, gamesList;
        TextBlock clipsFolder, clipsEmpty;
        Button clipsMore;
        string clipsRoot, lastClipSeen;
        string gameView;              // null — the library; otherwise a game, FavKey or AllKey
        int folderView = SrcObs;
        double homeScroll;
        int clipsLimit = 30;
        bool clipsLoading, clipsReload, flatView;
        DateTime clipsLoadedAt = DateTime.MinValue;
        List<Trimmer.Move> sortPlan;

        class LibData
        {
            public List<GameCardVm> Cards;
            public List<ClipVm> Page;
            public bool More, Flat, Group;
            public string Stats, Folder, Newest;
        }

        void InitClips()
        {
            clipsList = F<ItemsControl>("ClipsList");
            gamesList = F<ItemsControl>("GamesList");
            clipsFolder = F<TextBlock>("ClipsFolder");
            clipsEmpty = F<TextBlock>("ClipsEmpty");
            clipsMore = F<Button>("BtnClipsMore");
            clipsList.ItemsSource = clips;
            gamesList.ItemsSource = games;
            clipsList.AddHandler(Button.ClickEvent, new RoutedEventHandler(OnClipButton));
            InitScrub();
            InitFind();
            gamesList.AddHandler(Button.ClickEvent, new RoutedEventHandler(OnGameButton));
            F<Button>("BtnClipsBack").Click += (s, e) => GoBack();
            F<Button>("BtnClipsRefresh").Click += (s, e) => LoadClips(true);
            F<Button>("BtnClipsFolder").Click += (s, e) =>
            {
                string r = clipsFolder.Text;
                if (!Directory.Exists(r)) r = CurrentRoot();
                if (r != null) Shell.Open(r);
            };
            clipsMore.Click += (s, e) => { clipsLimit += 30; LoadClips(true); };
            F<Button>("BtnSortClips").Click += (s, e) => PlanSort();
            F<Button>("BtnSortGo").Click += (s, e) => RunSort();
            F<Button>("BtnSortCancel").Click += (s, e) => F<Border>("SortCard").Visibility = Visibility.Collapsed;

            var heroBox = F<Grid>("HeroBox");
            var heroBtn = F<Button>("BtnHero");
            heroBox.MouseEnter += (s, e) => heroBtn.Opacity = 1;
            heroBox.MouseLeave += (s, e) => heroBtn.Opacity = 0;
            heroBtn.Click += (s, e) => PickImage(gameView, true);

            // back to the library: Backspace or the mouse back button
            W.PreviewKeyDown += (s, e) =>
            {
                if (e.Key == Key.Back && gameView != null && pages[PageClips].IsVisible && !(Keyboard.FocusedElement is TextBox))
                {
                    GoBack();
                    e.Handled = true;
                }
            };
            W.PreviewMouseDown += (s, e) =>
            {
                if (e.ChangedButton == MouseButton.XButton1 && gameView != null && pages[PageClips].IsVisible)
                {
                    GoBack();
                    e.Handled = true;
                }
            };
        }

        // the recording folder from OBS settings, or the last clip's folder if OBS has not answered yet
        static string RootOf(Snapshot s)
        {
            if (s == null) return null;
            if (!string.IsNullOrEmpty(s.ClipsRoot)) return s.ClipsRoot.TrimEnd('\\');
            if (!string.IsNullOrEmpty(s.LastClipPath)) return Path.GetDirectoryName(s.LastClipPath);
            return null;
        }

        static string Existing(string path) { return string.IsNullOrEmpty(path) || !Directory.Exists(path) ? null : path.TrimEnd('\\'); }
        string ReadyRoot { get { return Existing(cfg.TrimFolder); } }
        string CollectionRoot { get { return Existing(cfg.CollectionFolder); } }
        string RootFor(int kind) { return kind == SrcObs ? RootOf(last) : kind == SrcReady ? ReadyRoot : CollectionRoot; }
        string CurrentRoot() { return RootFor(folderView); }

        void UpdateClipsHeader(Snapshot s)
        {
            if (clipsRoot == null) clipsFolder.Text = CurrentRoot() ?? L.T("the clip folder appears once ClipKeeper connects to OBS", "папка клипов появится, когда ClipKeeper подключится к OBS");
            ShowSourceChips();
            if (s.LastClipPath != lastClipSeen)
            {
                lastClipSeen = s.LastClipPath;
                if (pages[PageClips].IsVisible) LoadClips(true);
                else clipsLoadedAt = DateTime.MinValue;
            }
        }

        // Sources / Ready / Collection; a folder not chosen yet is a button with "+"
        string sourceChipsSig;
        void ShowSourceChips()
        {
            if (!cfg.UseCollection && folderView == SrcCollection) folderView = SrcObs;   // the collection was turned off in settings
            string sig = RootOf(last) + "|" + ReadyRoot + "|" + CollectionRoot + "|" + folderView + "|" + cfg.UseCollection;
            if (sig == sourceChipsSig) return;
            sourceChipsSig = sig;
            var panel = F<StackPanel>("ClipSource");
            panel.Children.Clear();
            for (int k = 0; k < (cfg.UseCollection ? 3 : 2); k++) panel.Children.Add(SourceChip(k));
            F<Button>("BtnSortClips").Visibility = folderView == SrcReady && cfg.UseCollection ? Visibility.Visible : Visibility.Collapsed;
        }

        ToggleButton SourceChip(int kind)
        {
            string root = RootFor(kind);
            bool missing = root == null && kind != SrcObs;
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            sp.Children.Add(new TextBlock { Style = (Style)W.FindResource("Icon"), Text = missing ? "" : SrcGlyphs[kind], FontSize = 12, Margin = new Thickness(0, 0, 7, 0) });
            sp.Children.Add(new TextBlock { Text = SrcNames[kind] });
            string tip = missing
                ? (kind == SrcReady ? L.T("Choose the ready clips folder — the editor saves trims there too", "Выбрать папку готовых клипов — туда же редактор сохраняет обрезки") : L.T("Choose the collection folder — clips that made it into a video", "Выбрать папку коллекции — клипы, которые вошли в выпуск"))
                : (root ?? L.T("the OBS recording folder will be known after connecting", "папка записи OBS станет известна после подключения")) + (kind != SrcObs ? L.T("\nRight click — change the folder", "\nПравый клик — сменить папку") : "");
            var b = new ToggleButton { Style = S("Chip"), Content = sp, IsChecked = folderView == kind && !missing, ToolTip = tip, Margin = new Thickness(0, 0, 6, 0) };
            if (missing) b.Opacity = 0.7;
            b.Click += (s, e) =>
            {
                if (missing) PickFolder(kind);
                else SwitchView(kind);
                sourceChipsSig = null;
                ShowSourceChips();
            };
            if (kind != SrcObs && !missing)
            {
                var menu = DarkMenu();
                menu.Items.Add(Item(L.T("Open in Explorer", "Открыть в Проводнике"), () => Shell.Open(root)));
                menu.Items.Add(Item(L.T("Change folder…", "Сменить папку…"), () => PickFolder(kind)));
                b.ContextMenu = menu;
            }
            return b;
        }

        void PickFolder(int kind)
        {
            string cur = kind == SrcReady ? cfg.TrimFolder : cfg.CollectionFolder;
            string path = FolderPicker.Pick(W, kind == SrcReady ? L.T("Ready clips folder (the editor saves here too)", "Папка готовых клипов (сюда же сохраняет редактор)") : L.T("Clip collection folder", "Папка коллекции клипов"),
                                            Existing(cur) ?? RootOf(last));
            if (path == null) return;
            if (kind == SrcReady) cfg.TrimFolder = path;
            else cfg.CollectionFolder = path;
            try { cfg.Save(); } catch (Exception ex) { Log.Write("settings not saved: " + ex.Message); }
            Log.Write("folder " + kind + ": " + path);
            SwitchView(kind);
        }

        ContextMenu DarkMenu() { return new ContextMenu { Style = S2<ContextMenu>("DarkMenu") }; }

        MenuItem Item(string text, Action click)
        {
            var mi = new MenuItem { Header = text, Style = S2<MenuItem>("DarkItem") };
            if (click != null) mi.Click += (s, e) => click();
            else mi.IsEnabled = false;
            return mi;
        }

        Style S2<T>(string key) { return (Style)W.FindResource(key); }

        void SwitchView(int kind)
        {
            folderView = kind;
            gameView = null;
            findFrom = null;
            ResetFind();
            homeScroll = 0;
            clipsLimit = 30;
            sourceChipsSig = null;
            ShowSourceChips();
            F<Border>("SortCard").Visibility = Visibility.Collapsed;
            LoadClips(true);
        }

        void OpenGame(string key)
        {
            var sv = F<ScrollViewer>("PageClips");
            if (gameView == null && key != null) homeScroll = sv.VerticalOffset;
            if (key != FindKey) ResetFind();
            gameView = key;
            clipsLimit = 30;
            // reserve the banner space right away so clips don't jump down when the picture loads
            if (key != null && !key.StartsWith("*") && Covers.IsGame(key)) ShowHero(null, true);
            else HideHero();
            LoadClips(true);
        }

        // back: from a search over all folders to where it started, from a game to the library
        void GoBack()
        {
            if (gameView == FindKey) BackFromFind();
            else OpenGame(null);
        }

        // a clip's game: subfolder (sorting / collection) → ClipKeeper data in the file → "Game - Replay …" in the name
        static string GameFor(FileInfo f, string root, int kind)
        {
            string g = ClipScanner.GameOf(f, root);
            if (g != NoGame.Folder) return g;
            if (kind != SrcObs)
            {
                var m = MetaOf(f).Item1;
                if (m != null) return m.Game;
            }
            var p = Regex.Match(Path.GetFileNameWithoutExtension(f.Name), @"^(.+?) - Replay");
            if (p.Success) return p.Groups[1].Value;
            return kind == SrcReady ? NoGame.Game : NoGame.Folder;
        }

        static bool KnownGame(string g) { return !NoGame.Is(g); }

        static string Summary(List<FileInfo> files)
        {
            if (files.Count == 0) return "";
            double dur = files.Sum(f => ClipIndex.Duration(f));
            return L.N(files.Count, "clip", "clips", "клип", "клипа", "клипов") +
                   (dur > 0 ? " · " + Fmt.Duration(dur) : "") + " · " + Fmt.Size(files.Sum(f => f.Length));
        }

        static GameCardVm Card(string key, string name, List<FileInfo> files, string glyph, Brush glyphBrush)
        {
            double dur = files.Sum(f => ClipIndex.Duration(f));
            var latest = files.Max(f => f.LastWriteTime);
            return new GameCardVm
            {
                Key = key, Name = name, Count = files.Count, Glyph = glyph, GlyphBrush = glyphBrush,
                Recent = files.OrderByDescending(f => f.LastWriteTime).Take(glyph != null ? GameCardVm.FanSize : 1).Select(f => f.FullName).ToList(),
                Stats = (dur > 0 ? Fmt.Duration(dur) + " · " : "") + Fmt.Size(files.Sum(f => f.Length)),
                Tip = L.N(files.Count, "clip", "clips", "клип", "клипа", "клипов") + L.T(", latest ", ", последний — ") + latest.ToString("dd.MM.yyyy HH:mm"),
            };
        }

        static List<GameCardVm> BuildCards(List<FileInfo> all, Func<FileInfo, string> game)
        {
            var cards = new List<GameCardVm>();
            if (all.Count == 0) return cards;
            var fav = all.Where(f => Favorites.Has(f.FullName)).ToList();
            if (fav.Count > 0) cards.Add(Card(FavKey, L.T("Favorites", "Избранное"), fav, "", GlyphStar));
            cards.Add(Card(AllKey, L.T("All clips", "Все клипы"), all, "", GlyphGrey));
            // games first, by their latest clip, then Desktop and the rest
            foreach (var g in all.GroupBy(game).OrderBy(g => Covers.IsGame(g.Key) ? 0 : 1).ThenByDescending(g => g.Max(f => f.LastWriteTime)))
            {
                string glyph = Covers.IsGame(g.Key) ? null : g.Key == "Desktop" ? "" : "";
                cards.Add(Card(g.Key, Covers.Title(g.Key), g.ToList(), glyph, GlyphGrey));
            }
            return cards;
        }

        public void LoadClips(bool force)
        {
            string root = CurrentRoot();
            if (root == null)
            {
                games.Clear();
                clips.Clear();
                HideHero();
                F<FrameworkElement>("ClipsTools").Visibility = Visibility.Collapsed;
                F<FrameworkElement>("BtnSearchAll").Visibility = Visibility.Collapsed;
                clipsEmpty.Text = folderView == SrcObs ? L.T("The clip folder is unknown yet — ClipKeeper learns it from OBS settings after connecting.", "Папка клипов пока неизвестна — ClipKeeper узнает её из настроек OBS после подключения.")
                                                       : L.T("No folder chosen — press the \"", "Папка не выбрана — нажми на кнопку «") + SrcNames[folderView] + L.T("\" button above.", "» выше.");
                clipsEmpty.Visibility = Visibility.Visible;
                return;
            }
            if (clipsLoading) { clipsReload |= force; return; }   // read again once this reading is done: the view or the search changed
            if (!force && root == clipsRoot && (DateTime.Now - clipsLoadedAt).TotalSeconds < 30) return;
            clipsLoading = true;
            clipsRoot = root;
            F<TextBlock>("ClipsStats").Text = L.T("reading the folder…", "читаю папку…");
            string lastClip = last != null ? last.LastClipPath : null;
            string obsRoot = RootOf(last), readyRoot = ReadyRoot, collRoot = CollectionRoot;
            int limit = clipsLimit, kind = folderView;
            string view = gameView;
            var find = CurrentFind();
            Task.Factory.StartNew(() =>
            {
                if (view == FindKey)
                {
                    bool more;
                    string stats;
                    var found = new LibData { Folder = root, Group = true };
                    found.Page = FindEverywhere(find, limit, lastClip, out more, out stats);
                    found.More = more;
                    found.Stats = stats;
                    ClipIndex.Save();
                    return found;
                }
                var all = ClipScanner.Scan(root, int.MaxValue);
                // folders may be nested — a clip is shown only in its own one
                foreach (var other in new[] { obsRoot, readyRoot, collRoot })
                    if (other != null && other.Length > root.Length && other.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase))
                        all = all.Where(f => !f.FullName.StartsWith(other + "\\", StringComparison.OrdinalIgnoreCase)).ToList();
                Func<FileInfo, string> game = f => GameFor(f, root, kind);
                HashSet<string> cut = null;
                Func<HashSet<string>> trimmed = () => cut ?? (cut = ClipStats.TrimmedSources(readyRoot, collRoot));
                bool byDay = find.Sort == SortNew || find.Sort == SortOld;
                var d = new LibData { Folder = root, Group = byDay };
                Action<List<FileInfo>, string> fillPage = (list, v) =>
                {
                    var shown = Refine(list, find, game, f => TitleFor(f, kind), trimmed);
                    d.Stats = Summary(shown);
                    d.Page = shown.Take(limit).Select(f =>
                    {
                        var vm = kind == SrcObs ? ClipScanner.Describe(f, root, lastClip) : DescribeNamed(f);
                        vm.Fav = Favorites.Has(f.FullName);
                        vm.Game = game(f);
                        if (byDay) vm.Group = DayOf(f.LastWriteTime);
                        if (kind == SrcObs && v != AllKey && v != FavKey) GameViewTitle(vm);
                        if (kind == SrcReady && cfg.UseCollection) vm.ReturnTip = collRoot == null ? L.T("To collection — choose its folder first", "В коллекцию — сначала выбери её папку")
                            : KnownGame(vm.Game) ? L.T("To collection: ", "В коллекцию: ") + Trimmer.GameDir(collRoot, vm.Game) : L.T("To collection — pick a game", "В коллекцию — выбрать игру");
                        return vm;
                    }).ToList();
                    d.More = shown.Count > limit;
                };
                if (view == null)
                {
                    d.Cards = BuildCards(all, game);
                    d.Stats = Summary(all);
                    // a single group (e.g. all ready clips without game data) — cards add nothing, show clips right away
                    if (all.Count > 0 && d.Cards.Count(c => !c.Special) <= 1 && !d.Cards.Any(c => c.Key == FavKey))
                    {
                        d.Flat = true;
                        fillPage(all, AllKey);
                    }
                    else d.Group = false;
                }
                else
                {
                    var shown = view == AllKey ? all : view == FavKey ? all.Where(f => Favorites.Has(f.FullName)).ToList()
                              : all.Where(f => game(f) == view).ToList();
                    d.Newest = shown.Count > 0 ? shown[0].FullName : null;
                    if (view != AllKey && view != FavKey && Directory.Exists(Path.Combine(root, view))) d.Folder = Path.Combine(root, view);
                    fillPage(shown, view);
                }
                ClipIndex.Save();
                return d;
            }).ContinueWith(t => W.Dispatcher.BeginInvoke(new Action(() =>
            {
                clipsLoading = false;
                clipsLoadedAt = DateTime.Now;
                if (clipsReload || view != gameView || kind != folderView) { clipsReload = false; LoadClips(true); return; }   // changed while reading
                if (t.IsFaulted) { Log.Write("gallery: " + t.Exception); F<TextBlock>("ClipsStats").Text = ""; return; }
                ShowLibrary(t.Result, view);
            })));
        }

        void ShowLibrary(LibData d, string view)
        {
            bool home = view == null;
            flatView = home && d.Flat;
            F<Button>("BtnClipsBack").Visibility = home ? Visibility.Collapsed : Visibility.Visible;
            F<TextBlock>("ClipsTitle").Text = home ? SrcNames[folderView] : view == FavKey ? L.T("Favorites", "Избранное") : view == AllKey ? L.T("All clips", "Все клипы")
                                            : view == FindKey ? L.T("Search", "Поиск") : Covers.Title(view);
            F<TextBlock>("ClipsStats").Text = d.Stats ?? "";
            clipsFolder.Text = d.Folder;
            games.Clear();
            clips.Clear();
            ApplyGrouping(d.Group);
            ShowFindBar(home, d.Flat, view);
            var sv = F<ScrollViewer>("PageClips");
            if (home && d.Flat)
            {
                HideHero();
                ShowClips(d.Page, d.More);
                if (clipsLimit == 30) sv.ScrollToTop();
            }
            else if (home)
            {
                HideHero();
                foreach (var c in d.Cards) games.Add(c);
                LoadCovers();
                clipsEmpty.Text = folderView == SrcObs ? L.T("No clips yet.", "Клипов пока нет.") : L.T("This folder has no clips yet.", "В этой папке пока нет клипов.");
                clipsEmpty.Visibility = d.Cards.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                clipsMore.Visibility = Visibility.Collapsed;
                double y = homeScroll;
                W.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => sv.ScrollToVerticalOffset(y)));
            }
            else
            {
                ShowClips(d.Page, d.More);
                if (clipsLimit == 30) sv.ScrollToTop();
                if (!view.StartsWith("*") && Covers.IsGame(view)) LoadHero(view, d.Newest);
                else HideHero();
            }
        }

        // ── game banner ──
        void LoadHero(string game, string newest)
        {
            Task.Factory.StartNew(() => Covers.FetchHero(game, newest)).ContinueWith(t => W.Dispatcher.BeginInvoke(new Action(() =>
            {
                if (gameView != game) return;
                if (t.IsFaulted || t.Result == null) HideHero();
                else ShowHero(t.Result, false);
            })));
        }

        void ShowHero(Covers.Hero hero, bool placeholder)
        {
            F<Grid>("HeroBox").Visibility = Visibility.Visible;
            F<ScrollViewer>("PageClips").Margin = new Thickness(0, -40, 0, 0);   // the banner goes under the window buttons
            F<StackPanel>("ClipsBody").Margin = new Thickness(34, 196, 22, 30);
            if (placeholder) { F<Border>("HeroImage").Background = null; return; }
            var b = new ImageBrush(hero.Image) { Stretch = Stretch.UniformToFill, AlignmentY = AlignmentY.Center };
            b.Freeze();
            var img = F<Border>("HeroImage");
            img.Background = b;
            // a clip frame is busy (game UI, small text) — blur and darken it; Steam art is made for text on top
            img.Effect = hero.FromClip ? new System.Windows.Media.Effects.BlurEffect { Radius = 28 } : null;
            img.RenderTransform = hero.FromClip ? new ScaleTransform(1.12, 1.12) : null;   // blurred edges are hidden
            img.RenderTransformOrigin = new Point(0.5, 0.5);
            F<Border>("HeroDim").Background = new SolidColorBrush(Color.FromArgb((byte)(hero.FromClip ? 0x60 : 0x10), 0, 0, 0));
        }

        void HideHero()
        {
            F<Grid>("HeroBox").Visibility = Visibility.Collapsed;
            F<ScrollViewer>("PageClips").Margin = new Thickness(0);
            F<StackPanel>("ClipsBody").Margin = new Thickness(34, 0, 22, 30);
        }

        void LoadCovers()
        {
            foreach (var c in games)
            {
                var card = c;
                // Favorites, All clips, Desktop: a mosaic of the latest clips; games: a clip frame under the cover
                if (card.Glyph != null) LoadMosaic(card, Thumbs.Request);
                else if (card.Recent.Count > 0) Thumbs.Request(card.Recent[0], img => { if (img != null && !card.OwnBackdrop) card.Backdrop = Fill(img); });
                if (!c.Special && Covers.IsGame(c.Key)) Covers.Get(c.Key, img => SetCover(card, img));
            }
        }

        // request(path, done) — Thumbs.Request in the window, a synchronous load for previews
        static void LoadMosaic(GameCardVm card, Action<string, Action<ImageSource>> request)
        {
            var paths = card.Recent.ToList();
            var got = new Brush[paths.Count];
            int left = paths.Count;
            for (int i = 0; i < paths.Count; i++)
            {
                int k = i;
                request(paths[k], img =>
                {
                    got[k] = img != null ? Fill(img) : null;
                    if (--left == 0) card.Mosaic = got.Where(b => b != null).ToArray();
                });
            }
        }

        static Brush Fill(ImageSource img)
        {
            var b = new ImageBrush(img) { Stretch = Stretch.UniformToFill };
            b.Freeze();
            return b;
        }

        static void SetCover(GameCardVm card, ImageSource img)
        {
            var b = CoverBrush(img);
            bool logo = HasAlpha(img as BitmapSource), wide = Wide(img);
            // a dark logo on a transparent background would be lost on the dark card — it is drawn white instead
            bool silhouette = wide && logo && DarkArt(img as BitmapSource);
            card.CoverMask = silhouette ? b : null;
            card.Cover = silhouette ? null : b;
            // a wide picture (a Steam header, a Wikipedia image): its own blurred copy above and below it
            if (wide && !logo) { card.OwnBackdrop = true; card.Backdrop = Fill(img); }
        }

        static bool HasAlpha(BitmapSource src)
        {
            if (src == null) return false;
            var f = src.Format;
            return f == PixelFormats.Bgra32 || f == PixelFormats.Pbgra32 || f == PixelFormats.Rgba64 || f == PixelFormats.Prgba64
                || f == PixelFormats.Rgba128Float || f == PixelFormats.Prgba128Float;
        }

        // a logo is shown whole from a bit wider than square; a picture fills the card unless it is clearly wide
        // (box art is often almost square — cropping its sides is better than a small picture in the middle)
        static bool Wide(ImageSource img) { return img.Width > img.Height * (HasAlpha(img as BitmapSource) ? 0.9 : 1.15); }

        // average brightness of opaque pixels: a dark logo on a transparent background gets lost on a dark card
        static bool DarkArt(BitmapSource src)
        {
            if (src == null) return false;
            try
            {
                var bmp = new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
                int w = bmp.PixelWidth, h = bmp.PixelHeight, stride = w * 4;
                var px = new byte[stride * h];
                bmp.CopyPixels(px, stride, 0);
                double sum = 0;
                long n = 0, transparent = 0;
                for (int i = 0; i < px.Length; i += 16)   // every fourth pixel is enough
                {
                    if (px[i + 3] < 128) { transparent++; continue; }
                    sum += 0.114 * px[i] + 0.587 * px[i + 1] + 0.299 * px[i + 2];
                    n++;
                }
                return n > 0 && transparent > n / 4 && sum / n < 90;
            }
            catch { return false; }
        }

        // a portrait cover fills the card; a logo is shown whole with margins, a wide picture across the whole width
        static Brush CoverBrush(ImageSource img)
        {
            bool wide = Wide(img), logo = HasAlpha(img as BitmapSource);
            var b = new ImageBrush(img) { Stretch = wide ? Stretch.Uniform : Stretch.UniformToFill, AlignmentY = wide ? AlignmentY.Center : AlignmentY.Top };
            if (wide && logo)
            {
                b.Viewport = new Rect(0.1, 0.1, 0.8, 0.8);
                b.ViewportUnits = BrushMappingMode.RelativeToBoundingBox;
            }
            b.Freeze();
            return b;
        }

        // ClipKeeper data from the file (cached in clipcache.json): game, recording date, title
        static Tuple<ClipMeta, string> MetaOf(FileInfo f) { return ClipIndex.Meta(f); }

        // inside a game its name on every card is redundant: the title is when it was recorded, below — the size
        public static void GameViewTitle(ClipVm vm)
        {
            int dot = vm.Sub.LastIndexOf(" · ");
            if (dot < 0) return;
            string when = EventVm.Cap(vm.Sub.Substring(0, dot));
            int tag = vm.Title.IndexOf(" · ");
            vm.Title = when + (tag >= 0 ? vm.Title.Substring(tag) : "");
            vm.Sub = vm.Sub.Substring(dot + 3);
        }

        // ready clips and the collection: the title is the clip name (from the file or file name), below — where it came from
        static ClipVm DescribeNamed(FileInfo f)
        {
            var vm = ClipScanner.Describe(f, f.DirectoryName, null);
            var mt = MetaOf(f);
            vm.Title = !string.IsNullOrEmpty(mt.Item2) ? mt.Item2 : Path.GetFileNameWithoutExtension(f.Name);
            if (mt.Item1 != null)
                vm.Sub = Covers.Title(mt.Item1.Game) + L.T(" · clip from ", " · клип от ") + mt.Item1.Recorded.ToString("dd.MM HH:mm") + " · " + vm.Sub.Substring(vm.Sub.LastIndexOf('·') + 2);
            return vm;
        }

        void ShowClips(List<ClipVm> list, bool more)
        {
            ScrubEnd();
            clips.Clear();
            foreach (var vm in list)
            {
                clips.Add(vm);
                var v = vm;
                Thumbs.Request(vm.Path, img =>
                {
                    if (img == null) return;
                    var b = new ImageBrush(img) { Stretch = Stretch.UniformToFill };
                    b.Freeze();
                    v.Thumb = b;
                });
            }
            clipsEmpty.Text = CurrentFind().Any || gameView == FindKey ? L.T("Nothing found.", "Ничего не нашлось.")
                            : gameView == FavKey ? L.T("Favorites are empty — star a clip on its preview.", "В избранном пусто — отметь клип звёздочкой на превью.")
                            : L.T("No clips here yet.", "Здесь пока нет клипов.");
            clipsEmpty.Visibility = list.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            clipsMore.Visibility = more ? Visibility.Visible : Visibility.Collapsed;
        }

        // ── for previews ──
        public void PreviewClips(List<ClipVm> list, string root, string title)
        {
            clipsRoot = root;
            gameView = title;
            string dir = Path.Combine(root, title);
            var files = ClipScanner.Scan(dir, int.MaxValue);
            ShowLibrary(new LibData { Cards = new List<GameCardVm>(), Folder = dir, Stats = Summary(files) }, null);
            gameView = title;
            F<Button>("BtnClipsBack").Visibility = Visibility.Visible;
            F<TextBlock>("ClipsTitle").Text = Covers.Title(title);
            clipsFolder.Text = dir;
            games.Clear();
            ShowFindBar(false, false, title);
            ApplyGrouping(true);
            foreach (var vm in list)
            {
                vm.Group = DayOf(File.GetLastWriteTime(vm.Path));
                clips.Add(vm);
            }
            clipsEmpty.Visibility = Visibility.Collapsed;
            var hero = Covers.FetchHero(title, files.Count > 0 ? files[0].FullName : null);
            if (hero != null) ShowHero(hero, false);
        }

        // a library with real games and covers; favorites are for the picture only and are not saved
        public void PreviewLibrary(string root, int fakeFav, int kind)
        {
            folderView = kind;
            sourceChipsSig = null;
            ShowSourceChips();
            clipsRoot = root;
            gameView = null;
            var all = ClipScanner.Scan(root, int.MaxValue);
            var cards = BuildCards(all, f => GameFor(f, root, kind));
            if (fakeFav == 0 && cards.Count(c => !c.Special) <= 1)
            {
                var page = all.Take(9).Select(f =>
                {
                    var vm = DescribeNamed(f);
                    vm.Game = GameFor(f, root, kind);
                    if (kind == SrcReady && cfg.UseCollection) vm.ReturnTip = KnownGame(vm.Game) ? L.T("To collection", "В коллекцию") : L.T("To collection — pick a game", "В коллекцию — выбрать игру");
                    var img = Thumbs.Load(f.FullName, 320, 180);
                    if (img != null) vm.Thumb = new ImageBrush(img) { Stretch = Stretch.UniformToFill };
                    return vm;
                }).ToList();
                ClipIndex.Save();
                ShowLibrary(new LibData { Flat = true, Page = page, Folder = root, Stats = Summary(all) }, null);
                return;
            }
            if (fakeFav > 0 && !cards.Any(c => c.Key == FavKey))
                cards.Insert(0, Card(FavKey, L.T("Favorites", "Избранное"), all.Take(fakeFav).ToList(), "", GlyphStar));
            Action<string, Action<ImageSource>> load = (path, done) => done(Thumbs.Load(path, 320, 180));
            foreach (var c in cards)
            {
                if (c.Glyph != null) LoadMosaic(c, load);
                else if (c.Recent.Count > 0) load(c.Recent[0], img => { if (img != null && !c.OwnBackdrop) c.Backdrop = Fill(img); });
                if (!c.Special && Covers.IsGame(c.Key))
                {
                    var img = Covers.Fetch(c.Key);
                    if (img != null) SetCover(c, img);
                }
            }
            ClipIndex.Save();
            ShowLibrary(new LibData { Cards = new List<GameCardVm>(), Folder = root, Stats = Summary(all) }, null);
            foreach (var c in cards) games.Add(c);
            clipsEmpty.Visibility = Visibility.Collapsed;
        }

        // for the self-test: every look of a game card and a clip card with frames under the mouse
        public void TestLibraryCards()
        {
            var frame = Wpf.Br("#3B2F8F");
            var glyph = new GameCardVm { Key = AllKey, Name = "All", Count = 3, Stats = "1 min", Glyph = "\uE8F1", GlyphBrush = GlyphGrey };
            glyph.Mosaic = new Brush[] { frame, frame, frame };
            var logo = new GameCardVm { Key = "Logo Game", Name = "Logo Game", Count = 2, Stats = "1 min", GlyphBrush = GlyphGrey };
            logo.Backdrop = frame;
            logo.CoverMask = frame;
            var letters = new GameCardVm { Key = "No Cover", Name = "No Cover", Count = 1, Stats = "1 min", GlyphBrush = GlyphGrey };
            letters.Backdrop = frame;
            games.Clear();
            foreach (var c in new[] { glyph, logo, letters }) games.Add(c);
            var vm = new ClipVm { Path = @"C:\none\Replay.mp4", Title = "Replay", Sub = "today · 10 MB", Duration = "0:30", NewVis = Visibility.Visible };
            vm.Frames = new Brush[] { frame };
            vm.Scrub = frame;
            vm.ScrubWidth = 40;
            clips.Clear();
            clips.Add(vm);
        }

        // ── "All to collection" ──
        void PlanSort()
        {
            string ready = ReadyRoot, dest = CollectionRoot;
            if (ready == null) return;
            if (dest == null) { PickFolder(SrcCollection); return; }
            var card = F<Border>("SortCard");
            F<TextBlock>("SortTitle").Text = L.T("Looking at what is in Ready…", "Смотрю, что лежит в готовых…");
            F<ItemsControl>("SortList").ItemsSource = null;
            F<Button>("BtnSortGo").Visibility = Visibility.Collapsed;
            card.Visibility = Visibility.Visible;
            Task.Factory.StartNew(() =>
            {
                List<string> unknown;
                var plan = Trimmer.SortPlan(ready, dest, out unknown);
                return Tuple.Create(plan, unknown);
            }).ContinueWith(t => W.Dispatcher.BeginInvoke(new Action(() =>
            {
                if (t.IsFaulted) { F<TextBlock>("SortTitle").Text = L.T("Failed: ", "Не получилось: ") + t.Exception.GetBaseException().Message; return; }
                sortPlan = t.Result.Item1;
                var lines = sortPlan.GroupBy(m => Path.GetDirectoryName(m.To))
                                    .Select(g => g.Count() + " → " + g.Key + "   (" + string.Join(", ", g.Select(m => Path.GetFileNameWithoutExtension(m.From)).Take(4)) +
                                                 (g.Count() > 4 ? ", …" : "") + ")")
                                    .ToList();
                if (t.Result.Item2.Count > 0)
                    lines.Add(L.T("No game data — these stay put; move them one by one choosing a game: ", "Без данных об игре — останутся на месте, их можно перенести по одному с выбором игры: ") +
                              string.Join(", ", t.Result.Item2.Take(5)) + (t.Result.Item2.Count > 5 ? ", …" : ""));
                F<TextBlock>("SortTitle").Text = sortPlan.Count == 0 ? L.T("Nothing to move", "Переносить нечего")
                    : L.T("Move to the collection: ", "Перенести в коллекцию ") + L.N(sortPlan.Count, "clip", "clips", "клип", "клипа", "клипов") + ":";
                F<ItemsControl>("SortList").ItemsSource = lines;
                F<Button>("BtnSortGo").Visibility = sortPlan.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            })));
        }

        void RunSort()
        {
            var plan = sortPlan;
            if (plan == null || plan.Count == 0) return;
            F<Button>("BtnSortGo").Visibility = Visibility.Collapsed;
            F<TextBlock>("SortTitle").Text = L.T("Moving…", "Переношу…");
            Task.Factory.StartNew(() =>
            {
                int moved = 0;
                var errors = new List<string>();
                foreach (var m in plan)
                {
                    string err = DoMove(m);
                    if (err == null) moved++;
                    else errors.Add(Path.GetFileName(m.From) + ": " + err);
                }
                return Tuple.Create(moved, errors);
            }).ContinueWith(t => W.Dispatcher.BeginInvoke(new Action(() =>
            {
                sortPlan = null;
                F<Border>("SortCard").Visibility = Visibility.Collapsed;
                if (app != null)
                {
                    app.ShowToast(L.T("✓ Moved to the collection: ", "✓ В коллекцию перенесено: ") + t.Result.Item1);
                    if (t.Result.Item2.Count > 0) app.ShowNotice(L.T("Not everything was moved", "Не всё перенеслось"), t.Result.Item2, false);
                }
                LoadClips(true);
            })));
        }

        // a move that keeps the star; null — success, otherwise the error text (across drives File.Move copies)
        static string DoMove(Trimmer.Move m)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(m.To));
                File.Move(m.From, m.To);
                Favorites.Renamed(m.From, m.To);
                Log.Write("moved: " + m.From + " → " + m.To);
                return null;
            }
            catch (Exception ex) { return ex.Message; }
        }

        // one ready clip → the collection; unknown game — a menu with the collection's game folders
        void ToCollection(ClipVm vm, Button anchor)
        {
            string coll = CollectionRoot;
            if (coll == null) { PickFolder(SrcCollection); return; }
            if (KnownGame(vm.Game)) { MoveClip(vm, vm.Game); return; }
            var menu = DarkMenu();
            menu.Items.Add(Item(L.T("Which game?", "В какую игру?"), null));
            List<string> dirs;
            try { dirs = new DirectoryInfo(coll).GetDirectories().OrderByDescending(d => d.LastWriteTime).Select(d => d.Name).ToList(); }
            catch { dirs = new List<string>(); }
            foreach (var name in dirs)
            {
                string n = name;
                menu.Items.Add(Item(Covers.Title(n), () => MoveClip(vm, n)));
            }
            menu.Items.Add(Item(L.T("To the collection root", "В корень коллекции"), () => MoveClip(vm, "")));
            menu.PlacementTarget = anchor;
            menu.Placement = PlacementMode.Bottom;
            menu.IsOpen = true;
        }

        void MoveClip(ClipVm vm, string game)
        {
            string coll = CollectionRoot;
            if (coll == null) return;
            Task.Factory.StartNew(() =>
            {
                var mv = Trimmer.MoveFor(vm.Path, game, coll);
                return Tuple.Create(mv, DoMove(mv));
            }).ContinueWith(t => W.Dispatcher.BeginInvoke(new Action(() =>
            {
                if (t.IsFaulted || t.Result.Item2 != null)
                {
                    string err = t.IsFaulted ? t.Exception.GetBaseException().Message : t.Result.Item2;
                    if (app != null) app.ShowNotice(L.T("The clip was not moved", "Клип не перенесён"), new List<string> { Path.GetFileName(vm.Path) + ": " + err }, false);
                    return;
                }
                clips.Remove(vm);
                string to = t.Result.Item1.To;
                if (app != null)
                    app.ShowToast(L.T("✓ \"" + vm.Title + "\" is in the collection", "✓ «" + vm.Title + "» в коллекции") + (game != "" ? " · " + Covers.Title(game) : ""), L.T("show", "показать"), () => Shell.Select(to));
                if (clips.Count == 0) LoadClips(true);
            })));
        }

        void OnGameButton(object sender, RoutedEventArgs e)
        {
            var b = e.OriginalSource as Button;
            var card = b != null ? b.DataContext as GameCardVm : null;
            if (card == null) return;
            e.Handled = true;
            if ((b.Tag as string) == "cover") PickImage(card.Key, false, card);
            else OpenGame(card.Key);
        }

        void PickImage(string game, bool hero, GameCardVm card = null)
        {
            if (game == null || game.StartsWith("*")) return;
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = (hero ? L.T("Banner for ", "Баннер для ") : L.T("Cover for ", "Обложка для ")) + Covers.Title(game),
                Filter = L.T("Pictures", "Картинки") + "|*.jpg;*.jpeg;*.png;*.webp;*.bmp|" + L.T("All files", "Все файлы") + "|*.*",
            };
            if (dlg.ShowDialog(W) != true) return;
            try
            {
                Covers.SetCustom(game, dlg.FileName, hero);
                if (hero) LoadHero(game, null);
                else if (card != null) Covers.Get(game, img => SetCover(card, img));
            }
            catch (Exception ex)
            {
                if (app != null) app.ShowNotice(L.T("The picture was not set", "Картинка не поставилась"), new List<string> { ex.Message }, false);
            }
        }

        void OnClipButton(object sender, RoutedEventArgs e)
        {
            var b = e.OriginalSource as Button;
            var vm = b != null ? b.DataContext as ClipVm : null;
            if (vm == null) return;
            e.Handled = true;
            switch (b.Tag as string)
            {
                case "open": Shell.Open(vm.Path); break;
                case "trim": if (app != null) app.OpenTrim(vm.Path, clips.Select(c => c.Path).ToList()); break;   // ← → in the editor go through this list
                case "folder": Shell.Select(vm.Path); break;
                case "fav":
                    vm.Fav = Favorites.Toggle(vm.Path);
                    if (!vm.Fav && gameView == FavKey) clips.Remove(vm);
                    break;
                case "return": ToCollection(vm, b); break;
                case "copy":
                    if (Shell.CopyFile(vm.Path) && app != null)
                        app.ShowToast(L.T("✓ Clip copied — paste it into Discord or Telegram (Ctrl+V)", "✓ Клип скопирован — вставь его в Discord или Telegram (Ctrl+V)"));
                    break;
                case "delete":
                    if (vm.DeleteText == "")
                    {
                        vm.DeleteText = L.T("Delete?", "Удалить?");
                        var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
                        t.Tick += (s, a) => { t.Stop(); vm.DeleteText = ""; };
                        t.Start();
                        break;
                    }
                    if (Shell.Recycle(vm.Path))
                    {
                        clips.Remove(vm);
                        if (app != null) app.ShowToast(L.T("✓ Clip moved to the Recycle Bin", "✓ Клип перемещён в корзину"));
                    }
                    break;
            }
        }
    }
}
