using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace DeviceGuard
{
    // The search over everything: Ctrl+K (or the box at the top of the sidebar) opens one field for clips by name, game
    // or date in every folder, the games, and what ClipKeeper can do — save the replay now, trim the last clip, go through
    // new clips, open a page. ↑ ↓ choose, Enter does it, Esc closes. A found clip opens in the library with its panel.
    partial class MainWindow
    {
        class PalItem
        {
            public string Group, Title, Mark, Sub, Glyph;
            public Brush Thumb;
            public bool Cover;           // a 2:3 cover instead of a 16:9 frame
            public string[] Keys;
            public Action Run;
        }

        TextBox palInput;
        readonly List<PalItem> palItems = new List<PalItem>();
        readonly List<Border> palRows = new List<Border>();
        int palAt;
        DispatcherTimer palTimer;
        int palAsk;                      // the newest question: older answers are dropped
        string pendingPeek;              // a clip found in the search: its panel opens once the library shows it

        void InitPalette()
        {
            palInput = F<TextBox>("PaletteInput");
            palTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(160) };
            palTimer.Tick += (s, e) => { palTimer.Stop(); PaletteQuery(); };
            palInput.TextChanged += (s, e) =>
            {
                F<TextBlock>("PaletteHint").Visibility = palInput.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
                palTimer.Stop();
                palTimer.Start();
            };
            palInput.PreviewKeyDown += (s, e) =>
            {
                if (e.Key == Key.Escape) { ClosePalette(); e.Handled = true; }
                else if (e.Key == Key.Down) { PalMove(1); e.Handled = true; }
                else if (e.Key == Key.Up) { PalMove(-1); e.Handled = true; }
                else if (e.Key == Key.Enter) { palTimer.Stop(); PalRun(palAt); e.Handled = true; }
            };
            F<Border>("PaletteScrim").MouseLeftButtonDown += (s, e) => ClosePalette();
            W.PreviewKeyDown += (s, e) =>
            {
                if (e.Key == Key.K && Keyboard.Modifiers == ModifierKeys.Control) { OpenPalette(); e.Handled = true; }
            };
        }

        void OpenPalette()
        {
            if (F<FrameworkElement>("Palette").Visibility == Visibility.Visible) { palInput.SelectAll(); return; }
            F<FrameworkElement>("Palette").Visibility = Visibility.Visible;
            palInput.Text = "";
            PaletteQuery();
            W.Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => palInput.Focus()));
        }

        void ClosePalette()
        {
            palAsk++;
            F<FrameworkElement>("Palette").Visibility = Visibility.Collapsed;
        }

        // what the field holds now: actions and games at once, clips once the folders have been looked through
        void PaletteQuery()
        {
            string q = palInput.Text.Trim();
            var words = Words(q);
            int ask = ++palAsk;
            var known = PalStatic(words);
            PalShow(known, true);
            var roots = new[] { RootOf(last), ReadyRoot, cfg.UseCollection ? CollectionRoot : null };
            if (roots.All(r => r == null)) { PalShow(known, false); return; }
            string lastClip = last != null ? last.LastClipPath : null;
            Task.Factory.StartNew(() =>
            {
                bool more;
                string stats;
                Dictionary<string, Tuple<int, double>> totals;
                var found = FindIn(roots, new FindSpec { Words = words, Sort = SortNew }, words.Length > 0 ? 6 : 4, lastClip, out more, out stats, out totals);
                ClipIndex.Save();
                return found;
            }).ContinueWith(t => W.Dispatcher.BeginInvoke(new Action(() =>
            {
                if (ask != palAsk) return;
                var list = new List<PalItem>();
                if (!t.IsFaulted)
                {
                    var found = t.Result;
                    list.AddRange(found.Select(vm => PalClip(vm, q)));
                    if (words.Length > 0 && found.Count > 0) list.AddRange(PalClipActions(found[0]));
                }
                list.AddRange(known);
                PalShow(list, false);
            })));
        }

        PalItem PalClip(ClipVm vm, string q)
        {
            var it = new PalItem
            {
                Group = q.Length > 0 ? L.T("Clips", "Клипы") : L.T("Latest clips", "Последние клипы"),
                Title = vm.Named || !vm.Source ? vm.Title : (KnownGame(vm.Game) ? Covers.Title(vm.Game) : vm.Title), Mark = q,
                Sub = string.Join(" · ", new[] { vm.Named || !vm.Source ? (KnownGame(vm.Game) ? Covers.Title(vm.Game) : null) : null, vm.WhenText, vm.Group }.Where(x => !string.IsNullOrEmpty(x))),
                Keys = new[] { vm.Duration ?? "" },
            };
            Thumbs.Request(vm.Path, img => { if (img != null) { it.Thumb = Square(img); PalRefreshThumb(it); } });
            string path = vm.Path, query = q;
            it.Run = () => OpenFound(path, query);
            return it;
        }

        IEnumerable<PalItem> PalClipActions(ClipVm vm)
        {
            string name = vm.Named || !vm.Source ? vm.Title : (KnownGame(vm.Game) ? Covers.Title(vm.Game) + ", " : "") + vm.WhenText;
            string g = L.T("With the found clip", "С найденным клипом");
            yield return new PalItem { Group = g, Glyph = "", Title = L.T("Trim ", "Обрезать ") + "«" + name + "»", Run = () => { if (app != null) app.OpenTrim(vm.Path); } };
            yield return new PalItem { Group = g, Glyph = "", Title = L.T("Share: Discord, Telegram, GIF", "Поделиться: Discord, Telegram, GIF"), Run = () => { if (app != null) app.OpenShare(vm.Path, null); } };
            yield return new PalItem { Group = g, Glyph = "", Title = L.T("Copy the file", "Копировать файл"), Sub = L.T("then Ctrl+V into Discord or Telegram", "потом Ctrl+V в Discord или Telegram"),
                                       Run = () => { if (Shell.CopyFile(vm.Path) && app != null) app.ShowToast(L.T("✓ Clip copied", "✓ Клип скопирован")); } };
            yield return new PalItem { Group = g, Glyph = "", Title = L.T("Show in folder", "Показать в папке"), Run = () => Shell.Select(vm.Path) };
        }

        // games and actions: known right away, filtered by the words
        List<PalItem> PalStatic(string[] words)
        {
            var r = new List<PalItem>();
            Func<string, bool> fits = s => words.All(w => Norm(s).Contains(w));
            foreach (var g in sideGames.Where(x => fits(x.Name)).Take(words.Length > 0 ? 4 : 3))
            {
                var game = g;
                Brush cover;
                sideCovers.TryGetValue(g.Key, out cover);
                r.Add(new PalItem { Group = L.T("Games", "Игры"), Title = g.Name, Mark = string.Join(" ", words), Thumb = cover, Cover = true,
                                    Sub = L.N(g.Count, "clip", "clips", "клип", "клипа", "клипов"),
                                    Run = () => { if (currentPage != PageClips) ShowPage(PageClips); OpenGame(game.Key); } });
            }
            string ga = L.T("Actions", "Действия");
            var acts = new List<PalItem>();
            if (app != null && last != null && last.Connected && last.RbState == 1)
                acts.Add(new PalItem { Glyph = "", Title = L.T("Save the replay now", "Сохранить повтор сейчас"), Sub = L.T("like the OBS key", "как клавиша в OBS"),
                                       Run = () => app.Guard.RequestSaveReplay(err => app.ShowNotice(L.T("The replay was not saved", "Повтор не сохранён"), new List<string> { err }, false)) });
            string lastClip = last != null ? last.LastClipPath : null;
            if (lastClip != null && File.Exists(lastClip))
            {
                acts.Add(new PalItem { Glyph = "", Title = L.T("Trim the latest clip", "Обрезать последний клип"), Sub = Path.GetFileNameWithoutExtension(lastClip), Run = () => { if (app != null) app.OpenTrim(lastClip); } });
                acts.Add(new PalItem { Glyph = "", Title = L.T("Copy the latest clip", "Копировать последний клип"), Sub = L.T("then Ctrl+V into Discord or Telegram", "потом Ctrl+V в Discord или Telegram"),
                                       Run = () => { if (Shell.CopyFile(lastClip) && app != null) app.ShowToast(L.T("✓ Clip copied", "✓ Клип скопирован")); } });
            }
            var triage = F<Button>("BtnTriage");
            if (triage.Visibility == Visibility.Visible)
                acts.Add(new PalItem { Glyph = "", Title = L.T("Go through new clips", "Разобрать новые клипы"), Sub = (triage.Content as string) ?? "",
                                       Run = () => triage.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)) });
            if (RootOf(last) != null)
                acts.Add(new PalItem { Glyph = "", Title = L.T("Open the clip folder", "Открыть папку клипов"), Sub = RootOf(last), Run = () => Shell.Open(RootOf(last)) });
            if (PageOn(PageRecord))
            {
                acts.Add(new PalItem { Glyph = "", Title = L.T("Check the recording now", "Проверить запись сейчас"), Run = () => { if (app != null) app.Guard.RequestCheck(); ShowPage(PageRecord); } });
                acts.Add(new PalItem { Glyph = "", Title = L.T("Recording", "Запись"), Sub = L.T("page", "страница"), Run = () => ShowPage(PageRecord) });
            }
            if (PageOn(PageStats)) acts.Add(new PalItem { Glyph = "", Title = L.T("Statistics", "Статистика"), Sub = L.T("page", "страница"), Run = () => ShowPage(PageStats) });
            acts.Add(new PalItem { Glyph = "", Title = L.T("Settings", "Настройки"), Sub = L.T("page", "страница"), Run = () => ShowPage(PageSettings) });
            if (app != null) acts.Add(new PalItem { Glyph = "", Title = L.T("Test the alarm", "Проверить тревогу"), Run = app.TestAlert });
            foreach (var a in acts.Where(a => words.Length == 0 || fits(a.Title + " " + a.Sub)).Take(words.Length > 0 ? 6 : 5))
            {
                a.Group = ga;
                a.Mark = string.Join(" ", words);
                r.Add(a);
            }
            return r;
        }

        // the list: group names, rows; the first row of a new answer is chosen
        void PalShow(List<PalItem> items, bool reading)
        {
            var keep = palAt < palItems.Count ? palItems[palAt] : null;
            palItems.Clear();
            palItems.AddRange(items);
            palRows.Clear();
            var p = F<StackPanel>("PaletteList");
            p.Children.Clear();
            string group = null;
            for (int i = 0; i < palItems.Count; i++)
            {
                var it = palItems[i];
                if (it.Group != group)
                {
                    group = it.Group;
                    p.Children.Add(new TextBlock { Text = group, FontSize = 11.5, Foreground = Wpf.Br("#5F646D"), Margin = new Thickness(8, p.Children.Count > 0 ? 10 : 4, 0, 6) });
                }
                var row = PalRow(it, i);
                palRows.Add(row);
                p.Children.Add(row);
            }
            if (palItems.Count == 0)
                p.Children.Add(new TextBlock { Text = reading ? L.T("Looking…", "Ищу…") : L.T("Nothing found", "Ничего не нашлось"), Foreground = Wpf.Res<Brush>("Muted"), Margin = new Thickness(8, 10, 0, 10) });
            F<TextBlock>("PaletteStatus").Text = reading ? L.T("looking through the folders…", "смотрю по папкам…") : "";
            int k = keep != null ? palItems.FindIndex(x => x.Title == keep.Title && x.Group == keep.Group) : -1;
            PalSelect(k >= 0 ? k : 0);
        }

        Border PalRow(PalItem it, int i)
        {
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            FrameworkElement lead;
            if (it.Glyph != null)
                lead = new TextBlock { Style = S("Icon"), Text = it.Glyph, FontSize = 14, Foreground = Wpf.Res<Brush>("Sub"), Width = 20, Margin = new Thickness(2, 0, 10, 0) };
            else
                lead = new Border { Width = it.Cover ? 20 : 48, Height = 27, CornerRadius = new CornerRadius(it.Cover ? 3 : 4), Background = it.Thumb ?? Wpf.Res<Brush>("Card2"),
                                    BorderBrush = Wpf.Res<Brush>("Ring"), BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 10, 0), Tag = it };
            g.Children.Add(lead);
            var text = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 13.5, Foreground = Wpf.Br("#C4C7CD") };
            Highlight(text, it.Title, it.Mark);
            if (!string.IsNullOrEmpty(it.Sub)) text.Inlines.Add(new System.Windows.Documents.Run("   " + it.Sub) { Foreground = Wpf.Res<Brush>("Muted"), FontSize = 12 });
            Grid.SetColumn(text, 1);
            g.Children.Add(text);
            var keys = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
            if (it.Keys != null)
                foreach (var k in it.Keys.Where(k => k.Length > 0))
                    keys.Children.Add(new TextBlock { Text = k, FontSize = 12, Foreground = Wpf.Res<Brush>("Muted") });
            Grid.SetColumn(keys, 2);
            g.Children.Add(keys);
            var row = new Border { Height = 40, CornerRadius = new CornerRadius(6), Padding = new Thickness(8, 0, 10, 0), Child = g, Background = Brushes.Transparent, Cursor = Cursors.Hand };
            row.MouseEnter += (s, e) => PalSelect(i);
            row.MouseLeftButtonUp += (s, e) => PalRun(i);
            return row;
        }

        // the typed words lit in a name
        static void Highlight(TextBlock t, string text, string mark)
        {
            var words = Words(mark ?? "");
            string low = Norm(text);
            var lit = new bool[text.Length];
            foreach (var w in words)
                for (int at = low.IndexOf(w, StringComparison.Ordinal); at >= 0; at = low.IndexOf(w, at + 1, StringComparison.Ordinal))
                    for (int j = at; j < at + w.Length && j < lit.Length; j++) lit[j] = true;
            int i = 0;
            while (i < text.Length)
            {
                int j = i;
                while (j < text.Length && lit[j] == lit[i]) j++;
                var run = new System.Windows.Documents.Run(text.Substring(i, j - i));
                if (lit[i]) { run.Background = Wpf.Br("#40F0B232"); run.Foreground = Brushes.White; }
                t.Inlines.Add(run);
                i = j;
            }
        }

        void PalRefreshThumb(PalItem it)
        {
            foreach (var row in palRows)
            {
                var lead = ((Grid)row.Child).Children[0] as Border;
                if (lead != null && lead.Tag == it) lead.Background = it.Thumb;
            }
        }

        void PalSelect(int i)
        {
            if (palRows.Count == 0) { palAt = 0; return; }
            palAt = Math.Max(0, Math.Min(palRows.Count - 1, i));
            for (int k = 0; k < palRows.Count; k++) palRows[k].Background = k == palAt ? Wpf.Br("#12FFFFFF") : Brushes.Transparent;
            palRows[palAt].BringIntoView();
        }

        void PalMove(int d) { PalSelect(palAt + d); }

        void PalRun(int i)
        {
            if (i < 0 || i >= palItems.Count) return;
            var it = palItems[i];
            ClosePalette();
            if (it.Run != null) it.Run();
        }

        // a clip from the search: the library searches the same words in every folder and opens the clip's panel
        void OpenFound(string path, string query)
        {
            if (currentPage != PageClips) ShowPage(PageClips);
            pendingPeek = path;
            findQuiet = true;
            clipsSearch.Text = query.Length > 0 ? query : "";
            findQuiet = false;
            if (query.Length == 0)
            {
                // the latest clips: the folder's "All clips"
                var vm = clips.FirstOrDefault(c => string.Equals(c.Path, path, StringComparison.OrdinalIgnoreCase));
                if (vm != null) { pendingPeek = null; Peek(vm); return; }
                folderView = SrcObs;
                sourceChipsSig = null;
                ShowSourceChips();
                OpenGame(AllKey);
                return;
            }
            findFrom = gameView == FindKey ? findFrom : gameView;
            OpenFind();
        }

        // the library has shown its clips: the one found in the search opens
        void PeekPending()
        {
            if (pendingPeek == null) return;
            var vm = clips.FirstOrDefault(c => string.Equals(c.Path, pendingPeek, StringComparison.OrdinalIgnoreCase));
            pendingPeek = null;
            if (vm != null) W.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => Peek(vm)));
        }

        // for previews: the search open with these words and what it finds in the given folders
        public void PreviewPalette(string query, params string[] roots)
        {
            F<FrameworkElement>("Palette").Visibility = Visibility.Visible;
            palAsk++;
            palTimer.Stop();
            palInput.Text = query;
            palTimer.Stop();
            var words = Words(query);
            bool more;
            string stats;
            Dictionary<string, Tuple<int, double>> totals;
            var found = FindIn(roots, new FindSpec { Words = words, Sort = SortNew }, 4, null, out more, out stats, out totals);
            var list = found.Select(vm => PalClip(vm, query)).ToList();
            foreach (var it in list)
            {
                var vm = found[list.IndexOf(it)];
                var img = Thumbs.Load(vm.Path, 320, 180);
                if (img != null) it.Thumb = Square(img);
            }
            if (found.Count > 0) list.AddRange(PalClipActions(found[0]));
            list.AddRange(PalStatic(words));
            PalShow(list, false);
        }

        public void PreviewPaletteClose() { ClosePalette(); }
    }
}
