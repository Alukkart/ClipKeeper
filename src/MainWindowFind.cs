using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace DeviceGuard
{
    // Finding clips in the library: a search field, filters (favorites, not trimmed, older than a month) and the order
    // above the clips; clips sorted by date fall into days. A search from the library's home, or "all folders" from a game,
    // looks through Sources, Ready and the collection at once.
    partial class MainWindow
    {
        const string FindKey = "*find";
        const int SortNew = 0, SortOld = 1, SortLong = 2, SortBig = 3;
        TextBox clipsSearch;
        string findFrom;              // the view a search over all folders came from: back returns there
        bool fltFav, fltUncut, fltOld, findQuiet;
        int clipsSort = SortNew;
        DispatcherTimer findTimer;

        // what the background reading needs to know about the search and the filters
        class FindSpec
        {
            public string[] Words;
            public bool Fav, Uncut, Old;
            public int Sort;
            public bool Any { get { return Words.Length > 0 || Fav || Uncut || Old; } }
        }

        FindSpec CurrentFind()
        {
            return new FindSpec { Words = Words(clipsSearch != null ? clipsSearch.Text : ""), Fav = fltFav, Uncut = fltUncut, Old = fltOld, Sort = clipsSort };
        }

        // a search field like the one in the settings: an icon, a hint while empty, a cross to clear; Esc clears it
        Border SearchField(TextBox tb, string hint, string tip)
        {
            var box = new Border { Background = Wpf.Res<Brush>("Input"), BorderBrush = Wpf.Res<Brush>("LineHi"), BorderThickness = new Thickness(1),
                                   CornerRadius = new CornerRadius(7), Height = 34, ToolTip = tip };
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.Children.Add(new TextBlock { Style = S("Icon"), Text = "", FontSize = 13, Foreground = Wpf.Res<Brush>("Muted"), Margin = new Thickness(11, 0, 0, 0) });
            var hintText = new TextBlock { Text = hint, Foreground = Wpf.Res<Brush>("Muted"), FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis,
                                           Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
            Grid.SetColumn(hintText, 1);
            g.Children.Add(hintText);
            tb.Background = Brushes.Transparent;
            tb.BorderThickness = new Thickness(0);
            tb.Foreground = Wpf.Res<Brush>("Text");
            tb.CaretBrush = Wpf.Res<Brush>("Text");
            tb.SelectionBrush = Wpf.Br(Color.FromRgb(0x5A, 0x5C, 0x66), 255);
            tb.FontSize = 13;
            tb.VerticalAlignment = VerticalAlignment.Center;
            tb.Margin = new Thickness(6, 0, 0, 0);
            tb.FocusVisualStyle = null;
            Grid.SetColumn(tb, 1);
            g.Children.Add(tb);
            var clear = new Button { Style = S("BtnLink"), Content = new TextBlock { Style = S("Icon"), Text = "", FontSize = 10 }, Padding = new Thickness(10, 6, 10, 6), ToolTip = L.T("Clear", "Очистить") };
            clear.Click += (s, e) => { tb.Text = ""; tb.Focus(); };
            Grid.SetColumn(clear, 2);
            g.Children.Add(clear);
            box.Child = g;
            Action sync = () =>
            {
                bool empty = tb.Text.Length == 0;
                hintText.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
                clear.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
            };
            sync();
            box.Tag = hintText;   // the hint can change with the view
            tb.TextChanged += (s, e) => sync();
            tb.GotKeyboardFocus += (s, e) => box.BorderBrush = Wpf.Br(Color.FromRgb(0x6B, 0x6D, 0x76), 255);
            tb.LostKeyboardFocus += (s, e) => box.BorderBrush = Wpf.Res<Brush>("LineHi");
            tb.PreviewKeyDown += (s, e) => { if (e.Key == Key.Escape && tb.Text.Length > 0) { tb.Text = ""; e.Handled = true; } };
            return box;
        }

        void InitFind()
        {
            clipsSearch = new TextBox();
            var box = SearchField(clipsSearch, L.T("Search  (Ctrl+F)", "Поиск  (Ctrl+F)"), L.T("By name, game or date: \"bridge\", \"yesterday\", \"5 october\"",
                                                                                              "По названию, игре или дате: «мост», «вчера», «5 октября»"));
            F<Border>("ClipsSearchHost").Child = box;
            findTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            findTimer.Tick += (s, e) => { findTimer.Stop(); QueryChanged(); };
            clipsSearch.TextChanged += (s, e) => { if (findQuiet) return; findTimer.Stop(); findTimer.Start(); };
            clipsSearch.PreviewKeyDown += (s, e) =>
            {
                if (e.Key == Key.Enter) { findTimer.Stop(); QueryChanged(); e.Handled = true; }
                else if (e.Key == Key.Escape && clipsSearch.Text.Length == 0 && gameView == FindKey) { BackFromFind(); e.Handled = true; }
            };
            F<Button>("BtnClipsSort").Click += (s, e) => SortMenu((Button)s);
            F<Button>("BtnSearchAll").Click += (s, e) => { findFrom = gameView; OpenFind(); };
            BuildFilters();

            // Ctrl+F on the library page goes to the search
            W.PreviewKeyDown += (s, e) =>
            {
                if (e.Key != Key.F || Keyboard.Modifiers != ModifierKeys.Control || !pages[PageClips].IsVisible || CurrentRoot() == null) return;
                clipsSearch.Focus();
                clipsSearch.SelectAll();
                e.Handled = true;
            };

            // clips by date fall into days; a search over all folders — into folders
            var header = new FrameworkElementFactory(typeof(Grid));
            header.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 4, 12, 12));
            var c0 = new FrameworkElementFactory(typeof(ColumnDefinition)); c0.SetValue(ColumnDefinition.WidthProperty, GridLength.Auto);
            var c1 = new FrameworkElementFactory(typeof(ColumnDefinition)); c1.SetValue(ColumnDefinition.WidthProperty, GridLength.Auto);
            var c2 = new FrameworkElementFactory(typeof(ColumnDefinition));
            header.AppendChild(c0); header.AppendChild(c1); header.AppendChild(c2);
            var name = new FrameworkElementFactory(typeof(TextBlock));
            name.SetBinding(TextBlock.TextProperty, new Binding("Name"));
            name.SetValue(TextBlock.FontFamilyProperty, Wpf.Res<FontFamily>("DisplayFont"));
            name.SetValue(TextBlock.FontSizeProperty, 14.5);
            name.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
            var meta = new FrameworkElementFactory(typeof(TextBlock));
            var mb = new MultiBinding { Converter = new GroupMeta(day => { Tuple<int, double> t; return day != null && groupTotals != null && groupTotals.TryGetValue(day, out t) ? t : null; }) };
            mb.Bindings.Add(new Binding());
            mb.Bindings.Add(new Binding("ItemCount"));
            meta.SetBinding(TextBlock.TextProperty, mb);
            meta.SetValue(Grid.ColumnProperty, 1);
            meta.SetValue(TextBlock.ForegroundProperty, Wpf.Res<Brush>("Muted"));
            meta.SetValue(TextBlock.FontSizeProperty, 12.5);
            meta.SetValue(FrameworkElement.MarginProperty, new Thickness(10, 0, 0, 0));
            meta.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            var line = new FrameworkElementFactory(typeof(Border));
            line.SetValue(Grid.ColumnProperty, 2);
            line.SetValue(FrameworkElement.HeightProperty, 1.0);
            line.SetValue(Border.BackgroundProperty, Wpf.Res<Brush>("Line"));
            line.SetValue(FrameworkElement.MarginProperty, new Thickness(12, 0, 0, 0));
            line.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            header.AppendChild(name); header.AppendChild(meta); header.AppendChild(line);
            clipsList.GroupStyle.Add(new GroupStyle { HeaderTemplate = new DataTemplate { VisualTree = header } });
        }

        // "3 clips · 7 min" under a day or a folder
        // the whole day (or folder) as the reading counted it, not only the cards loaded so far; the loaded cards if unknown
        class GroupMeta : IMultiValueConverter
        {
            readonly Func<string, Tuple<int, double>> totals;
            public GroupMeta(Func<string, Tuple<int, double>> totals) { this.totals = totals; }

            public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
            {
                var g = values[0] as CollectionViewGroup;
                if (g == null) return "";
                var t = totals(g.Name as string);
                int n = t != null ? t.Item1 : g.Items.Count;
                double sec = t != null ? t.Item2 : g.Items.OfType<ClipVm>().Sum(c => c.Seconds);
                return L.N(n, "clip", "clips", "клип", "клипа", "клипов") + (sec > 0 ? " · " + Fmt.Duration(sec) : "");
            }
            public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) { throw new NotSupportedException(); }
        }

        // the search field always; filters and the order over a list of clips; "all folders" when a view found its share
        void ShowFindBar(bool home, bool flat, string view)
        {
            F<FrameworkElement>("ClipsTools").Visibility = Visibility.Visible;
            BuildFilters();
            F<FrameworkElement>("ClipSource").Visibility = view == FindKey ? Visibility.Collapsed : Visibility.Visible;
            var hint = (TextBlock)((Border)F<Border>("ClipsSearchHost").Child).Tag;
            hint.Text = home && !flat ? L.T("Search all folders  (Ctrl+F)", "Поиск по всем папкам  (Ctrl+F)")
                      : L.T("Search  (Ctrl+F)", "Поиск  (Ctrl+F)");
            string q = clipsSearch.Text.Trim();
            var all = F<Button>("BtnSearchAll");
            all.Content = L.T("Search all folders for \u201C" + q + "\u201D", "Искать «" + q + "» во всех папках");
            all.Visibility = q.Length > 0 && view != FindKey && (!home || flat) ? Visibility.Visible : Visibility.Collapsed;
        }

        void BuildFilters()
        {
            var p = F<Panel>("ClipsFilters");
            p.Children.Clear();
            // only the star has an icon: three chips, the search and the order fit one line of a window at its usual size
            Func<string, string, string, bool, Action<bool>, ToggleButton> chip = (glyph, text, tip, on, set) =>
            {
                var sp = new StackPanel { Orientation = Orientation.Horizontal };
                if (glyph != null) sp.Children.Add(new TextBlock { Style = S("Icon"), Text = glyph, FontSize = 11, Margin = new Thickness(0, 1, 6, 0) });
                sp.Children.Add(new TextBlock { Text = text });
                var b = new ToggleButton { Style = S("Chip"), Content = sp, IsChecked = on, ToolTip = tip, Margin = new Thickness(0, 0, 6, 6), Padding = new Thickness(9, 4, 9, 4) };
                b.Click += (s, e) => { set(b.IsChecked == true); clipsLimit = 30; LoadClips(true); };
                p.Children.Add(b);
                return b;
            };
            bool page = gameView != null || flatView;
            if (gameView != FavKey)
                chip("", L.T("Favorites", "Избранное"), L.T("Only clips with a star", "Только клипы со звёздочкой"), fltFav, v => fltFav = v);
            if (folderView == SrcObs)
                chip(null, L.T("Not trimmed", "Не обрезаны"), L.T("No trim was made from them in ClipKeeper yet", "Из них ещё не делали обрезку в ClipKeeper"), fltUncut, v => fltUncut = v);
            chip(null, L.T("Older than a month", "Старше месяца"), L.T("Saved more than 30 days ago — candidates for the Recycle Bin", "Сохранены больше 30 дней назад — кандидаты в корзину"), fltOld, v => fltOld = v);
            p.Visibility = page && gameView != FindKey ? Visibility.Visible : Visibility.Collapsed;
            var sortText = new[] { L.T("Newest first", "Сначала новые"), L.T("Oldest first", "Сначала старые"), L.T("Longest first", "Сначала длинные"), L.T("Largest first", "Сначала большие") };
            var sb = F<Button>("BtnClipsSort");
            var c = new StackPanel { Orientation = Orientation.Horizontal };
            c.Children.Add(new TextBlock { Style = S("Icon"), Text = "", FontSize = 12, Margin = new Thickness(0, 0, 8, 0) });
            c.Children.Add(new TextBlock { Text = sortText[clipsSort] });
            c.Children.Add(new TextBlock { Style = S("Icon"), Text = "", FontSize = 9, Margin = new Thickness(8, 1, 0, 0) });
            sb.Content = c;
            sb.Tag = sortText;
            sb.Visibility = page ? Visibility.Visible : Visibility.Collapsed;
        }

        void SortMenu(Button anchor)
        {
            var menu = BuildSortMenu((string[])anchor.Tag);
            menu.PlacementTarget = anchor;
            menu.Placement = PlacementMode.Bottom;
            menu.IsOpen = true;
        }

        // for previews: the order menu as it opens
        public ContextMenu PreviewSortMenu() { return BuildSortMenu((string[])F<Button>("BtnClipsSort").Tag); }

        ContextMenu BuildSortMenu(string[] names)
        {
            var menu = DarkMenu();
            for (int i = 0; i < names.Length; i++)
            {
                int k = i;
                var mi = Item((k == clipsSort ? "✓  " : "     ") + names[k], () => { clipsSort = k; clipsLimit = 30; BuildFilters(); LoadClips(true); });
                mi.MinWidth = 0;   // as wide as its words, not as the menus of folders
                menu.Items.Add(mi);
            }
            return menu;
        }

        // a typed search: from the library's home it looks through every folder, inside a view it narrows the view
        void QueryChanged()
        {
            string q = clipsSearch.Text.Trim();
            clipsLimit = 30;
            if (gameView == FindKey)
            {
                if (q.Length == 0) BackFromFind();
                else LoadClips(true);
                return;
            }
            if (gameView == null && !flatView && q.Length > 0) { findFrom = null; OpenFind(); return; }
            LoadClips(true);
        }

        void OpenFind()
        {
            gameView = FindKey;
            clipsLimit = 30;
            HideHero();
            LoadClips(true);
        }

        void BackFromFind()
        {
            string back = findFrom;
            findFrom = null;
            ResetFind();
            OpenGame(back);
        }

        // a new place in the library starts without a search and filters; the order stays
        void ResetFind()
        {
            findQuiet = true;
            if (clipsSearch != null) clipsSearch.Text = "";
            findQuiet = false;
            if (findTimer != null) findTimer.Stop();
            fltFav = fltUncut = fltOld = false;
        }

        // the search, the filters and the order applied to a view's clips (on the reading thread)
        static List<FileInfo> Refine(List<FileInfo> files, FindSpec f, Func<FileInfo, string> game, Func<FileInfo, string> title, Func<HashSet<string>> trimmed)
        {
            IEnumerable<FileInfo> r = files;
            if (f.Fav) r = r.Where(x => Favorites.Has(x.FullName));
            if (f.Old) { var edge = DateTime.Now.AddDays(-30); r = r.Where(x => x.LastWriteTime < edge); }
            if (f.Uncut) { var cut = trimmed(); r = r.Where(x => !cut.Contains(x.Name)); }
            if (f.Words.Length > 0) r = r.Where(x => Matches(x, game(x), title(x), f.Words));
            return Order(r, f.Sort).ToList();
        }

        static IEnumerable<FileInfo> Order(IEnumerable<FileInfo> files, int sort)
        {
            switch (sort)
            {
                case SortOld: return files.OrderBy(x => x.LastWriteTime);
                case SortLong: return files.OrderByDescending(x => ClipIndex.Duration(x)).ThenByDescending(x => x.LastWriteTime);
                case SortBig: return files.OrderByDescending(x => x.Length);
                default: return files.OrderByDescending(x => x.LastWriteTime);
            }
        }

        static string[] Words(string q)
        {
            return Norm(q).Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        }

        static string Norm(string s) { return (s ?? "").ToLowerInvariant().Replace('ё', 'е'); }

        // every word must be somewhere: the file name, its title, the game, the date ("5 october", "05.10", "yesterday")
        static bool Matches(FileInfo f, string game, string title, string[] words)
        {
            var when = f.LastWriteTime;
            var ru = CultureInfo.GetCultureInfo("ru-RU");
            var en = CultureInfo.GetCultureInfo("en-US");
            string hay = Norm(string.Join(" ", new[]
            {
                Path.GetFileNameWithoutExtension(f.Name), title, NoGame.Is(game) ? "" : Covers.Title(game) + " " + game,
                when.ToString("d MMMM", ru), when.ToString("d MMMM", en), when.ToString("MMMM yyyy", en), ru.DateTimeFormat.GetMonthName(when.Month),
                when.ToString("dd.MM"), when.ToString("d.MM"), when.ToString("dd.MM.yyyy"), when.ToString("yyyy-MM-dd"),
                when.Date == DateTime.Today ? "today сегодня" : when.Date == DateTime.Today.AddDays(-1) ? "yesterday вчера" : "",
            }));
            return words.All(w => hay.Contains(w));
        }

        // a ready clip's title lives in its data — only what is cached already: probing every unread file would make a search
        // take minutes, and the file name (searched anyway) is the title; a source's name is all it has
        static string TitleFor(FileInfo f, int kind) { return kind == SrcObs ? null : ClipIndex.CachedTitle(f); }

        // "Today", "Yesterday", "5 October", "5 October 2025"
        static string DayOf(DateTime when)
        {
            if (when.Date == DateTime.Today) return L.T("Today", "Сегодня");
            if (when.Date == DateTime.Today.AddDays(-1)) return L.T("Yesterday", "Вчера");
            return when.ToString(when.Year == DateTime.Now.Year ? "d MMMM" : "d MMMM yyyy", L.Culture);
        }

        // days only make sense while the clips go by date
        void ApplyGrouping(bool byGroup)
        {
            var g = clipsList.Items.GroupDescriptions;
            if (byGroup && g.Count == 0) g.Add(new PropertyGroupDescription("Group"));
            else if (!byGroup && g.Count > 0) g.Clear();
        }

        // for previews: a search over the given folders, shown as the app shows it
        public void PreviewFind(string query, params string[] roots)
        {
            findQuiet = true;
            clipsSearch.Text = query;
            findQuiet = false;
            gameView = FindKey;
            HideHero();
            bool more;
            string stats;
            Dictionary<string, Tuple<int, double>> totals;
            var page = FindIn(roots, new FindSpec { Words = Words(query), Sort = SortNew }, 9, null, out more, out stats, out totals);
            foreach (var vm in page)
            {
                var img = Thumbs.Load(vm.Path, 320, 180);
                if (img != null) vm.Thumb = new ImageBrush(img) { Stretch = Stretch.UniformToFill };
            }
            ClipIndex.Save();
            ShowLibrary(new LibData { Folder = roots[0], Stats = stats, Group = true, Page = page, More = more, Totals = totals }, FindKey);
        }

        // for the self-test: words, dates, the "older than a month" filter and the order on files made for it
        public static void TestFind(Action<bool, string> check)
        {
            string dir = Path.Combine(Path.GetTempPath(), "clipkeeper-find-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                Func<string, DateTime, int, FileInfo> make = (name, when, kb) =>
                {
                    string p = Path.Combine(dir, name);
                    File.WriteAllBytes(p, new byte[kb * 1024]);
                    File.SetLastWriteTime(p, when);
                    return new FileInfo(p);
                };
                var today = make("Hunt Showdown - Replay 2026-10-05 18-40-44.mp4", DateTime.Now, 3);
                var old = make("Replay 2026-08-01 10-00-00.mp4", DateTime.Now.AddDays(-40), 1);
                var day = make("Valorant - Clutch - 2026-10-05 21-00-00.mp4", new DateTime(DateTime.Now.Year, 10, 5, 21, 0, 0), 2);
                check(string.Join("|", Words("  Мост  ВЧЕРА ё ")) == "мост|вчера|е", "library search: words are lower case, ё is е");
                check(Matches(today, "Hunt Showdown", null, Words("hunt сегодня")) && !Matches(today, "Hunt Showdown", null, Words("hunt вчера")),
                      "library search: a game and today matches, yesterday does not");
                check(Matches(day, "Valorant", null, Words("5 октября")) && Matches(day, "Valorant", null, Words("05.10 clutch")) && Matches(day, "Valorant", null, Words("october")),
                      "library search: a date in words or digits, a word of the name");
                var all = new List<FileInfo> { today, old, day };
                Func<FileInfo, string> noGame = f => NoGame.Folder;
                Func<FileInfo, string> noTitle = f => null;
                Func<HashSet<string>> none = () => new HashSet<string>();
                var older = Refine(all, new FindSpec { Words = new string[0], Old = true }, noGame, noTitle, none);
                check(older.Count == 1 && older[0].Name == old.Name, "library filter: older than a month");
                var cut = Refine(all, new FindSpec { Words = new string[0], Uncut = true }, noGame, noTitle, () => new HashSet<string>(new[] { today.Name }, StringComparer.OrdinalIgnoreCase));
                check(cut.Count == 2 && !cut.Any(f => f.Name == today.Name), "library filter: not trimmed");
                var first = Refine(all, new FindSpec { Words = new string[0], Sort = SortOld }, noGame, noTitle, none);
                var big = Refine(all, new FindSpec { Words = new string[0], Sort = SortBig }, noGame, noTitle, none);
                check(first[0].Name == old.Name && big[0].Name == today.Name && big[2].Name == old.Name, "library order: oldest first, largest first");
                check(OwnTitle("Duel (discord)", "Duel") == "Duel" && OwnTitle("Duel (25 MB) 2", "Duel") == "Duel" && OwnTitle("Duel", "Duel") == "Duel"
                      && OwnTitle("Duel (best)", "Duel") == "Duel (best)" && OwnTitle("Bridge", "Duel") == "Bridge",
                      "ready clips: the editor's suffixes keep the saved title, a later name wins");
                check(DayOf(DateTime.Now) == L.T("Today", "Сегодня") && DayOf(DateTime.Now.AddDays(-1)) == L.T("Yesterday", "Вчера"), "library days: today, yesterday");
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        // one search over Sources, Ready and the collection; a clip shows in its own folder only (folders may be nested)
        List<ClipVm> FindEverywhere(FindSpec f, int limit, string lastClip, out bool more, out string stats, out Dictionary<string, Tuple<int, double>> totals)
        {
            return FindIn(new[] { RootOf(last), ReadyRoot, cfg.UseCollection ? CollectionRoot : null }, f, limit, lastClip, out more, out stats, out totals);
        }

        // the clips and length of each day (or folder) of a list, for the group headers
        static Dictionary<string, Tuple<int, double>> TotalsBy(IEnumerable<FileInfo> files, Func<FileInfo, string> group)
        {
            return files.GroupBy(group).ToDictionary(g => g.Key, g => Tuple.Create(g.Count(), g.Sum(f => ClipIndex.Duration(f))));
        }

        static List<ClipVm> FindIn(string[] roots, FindSpec f, int limit, string lastClip, out bool more, out string stats, out Dictionary<string, Tuple<int, double>> totals)
        {
            var found = new List<Tuple<FileInfo, int, string>>();
            for (int k = 0; k < roots.Length; k++)
            {
                string root = roots[k];
                if (root == null) continue;
                var files = ClipScanner.Scan(root, int.MaxValue);
                foreach (var other in roots)
                    if (other != null && other.Length > root.Length && other.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase))
                        files = files.Where(x => !x.FullName.StartsWith(other + "\\", StringComparison.OrdinalIgnoreCase)).ToList();
                int kind = k;
                foreach (var x in Order(files.Where(x => Matches(x, GameFor(x, root, kind), TitleFor(x, kind), f.Words)), f.Sort)) found.Add(Tuple.Create(x, kind, root));
            }
            more = found.Count > limit;
            stats = L.N(found.Count, "clip", "clips", "клип", "клипа", "клипов") + L.T(" in all folders", " во всех папках");
            var names = SrcNames;
            totals = found.GroupBy(t => names[t.Item2]).ToDictionary(g => g.Key, g => Tuple.Create(g.Count(), g.Sum(t => ClipIndex.Duration(t.Item1))));
            return found.Take(limit).Select(t =>
            {
                var vm = t.Item2 == SrcObs ? ClipScanner.Describe(t.Item1, t.Item3, lastClip) : DescribeNamed(t.Item1);
                vm.Fav = Favorites.Has(t.Item1.FullName);
                vm.Game = GameFor(t.Item1, t.Item3, t.Item2);
                vm.Group = names[t.Item2];
                return vm;
            }).ToList();
        }
    }
}
