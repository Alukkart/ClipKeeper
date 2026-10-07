using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;

namespace DeviceGuard
{
    // Several clips at once: Ctrl+click or the circle on a card selects it, Shift+click — everything up to it, Ctrl+A — all
    // shown, Esc — none. While any clip is selected a plain click on a card selects too, and a bar at the bottom offers
    // what to do with them: favorites, copy, to the collection, the Recycle Bin (on the second press).
    partial class MainWindow
    {
        ClipVm selAnchor;
        DispatcherTimer selDeleteTimer;
        bool selDeleteArmed;

        List<ClipVm> Selection { get { return clips.Where(c => c.Selected).ToList(); } }

        void InitSelect()
        {
            clipsList.AddHandler(UIElement.PreviewMouseLeftButtonDownEvent, new MouseButtonEventHandler((s, e) =>
            {
                var vm = ClipUnder(e.OriginalSource as DependencyObject);
                if (vm == null || vm.Editing) return;
                bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0, shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
                bool onButton = InButton(e.OriginalSource as DependencyObject);
                if (shift) SelectRange(vm);
                else if (ctrl || (Selection.Count > 0 && !onButton)) ToggleSelect(vm);
                else return;
                e.Handled = true;
            }));
            clips.CollectionChanged += (s, e) => { if (e.Action == NotifyCollectionChangedAction.Reset || e.Action == NotifyCollectionChangedAction.Remove) SelChanged(); };
            W.PreviewKeyDown += (s, e) =>
            {
                if (!pages[PageClips].IsVisible || Keyboard.FocusedElement is TextBox) return;
                if (e.Key == Key.A && Keyboard.Modifiers == ModifierKeys.Control && clips.Count > 0)
                {
                    foreach (var c in clips) c.Selected = true;
                    SelChanged();
                    e.Handled = true;
                }
                else if (e.Key == Key.Escape && Keyboard.Modifiers == ModifierKeys.None && Selection.Count > 0)
                {
                    ClearSelection();
                    e.Handled = true;
                }
            };
            selDeleteTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            selDeleteTimer.Tick += (s, e) => { selDeleteTimer.Stop(); selDeleteArmed = false; BuildSelBar(); };
        }

        // a click on a card's own button (open, trim, star…) does what the button does, not a selection
        bool InButton(DependencyObject d)
        {
            for (; d != null && d != clipsList; d = d is System.Windows.Media.Visual ? System.Windows.Media.VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
                if (d is ButtonBase) return true;
            return false;
        }

        // for previews: these cards selected, or none
        public void PreviewSelect(params int[] which)
        {
            if (which.Length == 0) { ClearSelection(); return; }
            foreach (int i in which) if (i < clips.Count) clips[i].Selected = true;
            SelChanged();
        }

        void ToggleSelect(ClipVm vm)
        {
            vm.Selected = !vm.Selected;
            selAnchor = vm;
            SelChanged();
        }

        // Shift+click: from the last clicked card to this one, as in Explorer
        void SelectRange(ClipVm vm)
        {
            int a = selAnchor != null ? clips.IndexOf(selAnchor) : -1, b = clips.IndexOf(vm);
            if (a < 0) { ToggleSelect(vm); return; }
            for (int i = Math.Min(a, b); i <= Math.Max(a, b); i++) clips[i].Selected = true;
            SelChanged();
        }

        void ClearSelection()
        {
            foreach (var c in clips) c.Selected = false;
            selAnchor = null;
            SelChanged();
        }

        // the circles on every card and the bar follow the selection
        void SelChanged()
        {
            var sel = Selection;
            bool any = sel.Count > 0;
            foreach (var c in clips) c.Selecting = any;
            selDeleteArmed = false;
            selDeleteTimer.Stop();
            F<Border>("SelBar").Visibility = any && pages[PageClips].Visibility == Visibility.Visible ? Visibility.Visible : Visibility.Collapsed;
            var body = F<StackPanel>("ClipsBody");
            var m = body.Margin;
            body.Margin = new Thickness(m.Left, m.Top, m.Right, any ? 96 : 30);   // the last row stays reachable above the bar
            if (any) BuildSelBar();
        }

        void BuildSelBar()
        {
            var sel = Selection;
            if (sel.Count == 0) return;
            F<TextBlock>("SelCount").Text = L.T("Selected: ", "Выбрано: ") + sel.Count;
            double sec = sel.Sum(c => c.Seconds);
            long bytes = sel.Sum(c => { try { return new FileInfo(c.Path).Length; } catch { return 0L; } });
            F<TextBlock>("SelMeta").Text = (sec > 0 ? Fmt.Duration(sec) + " · " : "") + Fmt.Size(bytes);
            var p = F<StackPanel>("SelActions");
            p.Children.Clear();
            bool allFav = sel.All(c => c.Fav);
            SelButton(p, allFav ? "" : "", allFav ? L.T("Unstar", "Убрать из избранного") : L.T("Favorite", "В избранное"), "BtnLink", () => SelFavorite(!allFav));
            SelButton(p, "", L.T("Copy", "Копировать"), "BtnLink", SelCopy);
            if (folderView == SrcReady && cfg.UseCollection) SelButton(p, "", L.T("To collection", "В коллекцию"), "BtnLink", SelToCollection);
            p.Children.Add(new Border { Width = 1, Height = 22, Background = Wpf.Res<System.Windows.Media.Brush>("LineHi"), Margin = new Thickness(6, 0, 6, 0) });
            var del = SelButton(p, "", selDeleteArmed ? L.T("Sure? To the Recycle Bin", "Точно? В корзину") : L.T("Recycle Bin", "В корзину"), "BtnLink", SelDelete);
            del.Foreground = Wpf.Br(Wpf.Bad, 255);
            var close = SelButton(p, "", null, "BtnLink", ClearSelection);
            close.ToolTip = L.T("Clear the selection (Esc)", "Снять выделение (Esc)");
        }

        Button SelButton(Panel p, string glyph, string text, string style, Action click)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            sp.Children.Add(new TextBlock { Style = S("Icon"), Text = glyph, FontSize = 13, VerticalAlignment = VerticalAlignment.Center });
            if (text != null) sp.Children.Add(new TextBlock { Text = text, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
            var b = new Button { Style = S(style), Content = sp, Margin = new Thickness(2, 0, 2, 0) };
            b.Click += (s, e) => click();
            p.Children.Add(b);
            return b;
        }

        void SelFavorite(bool on)
        {
            foreach (var c in Selection)
                if (c.Fav != on) c.Fav = Favorites.Toggle(c.Path);
            if (!on && gameView == FavKey) { foreach (var c in Selection) clips.Remove(c); }
            BuildSelBar();
        }

        void SelCopy()
        {
            var paths = new StringCollection();
            foreach (var c in Selection) paths.Add(c.Path);
            try
            {
                Clipboard.SetFileDropList(paths);
                if (app != null) app.ShowToast(L.T("✓ Copied: ", "✓ Скопировано: ") + L.N(paths.Count, "clip", "clips", "клип", "клипа", "клипов") +
                                               L.T(" — Ctrl+V into Discord or Telegram", " — Ctrl+V в Discord или Telegram"));
            }
            catch (Exception ex) { Log.Write("could not copy clips: " + ex.Message); }
        }

        // ready clips with a known game go to its folder in the collection; the rest are named so they can be moved one by one
        void SelToCollection()
        {
            string coll = CollectionRoot;
            if (coll == null) { PickFolder(SrcCollection); return; }
            var sel = Selection;
            var known = sel.Where(c => KnownGame(c.Game)).ToList();
            var unknown = sel.Where(c => !KnownGame(c.Game)).Select(c => Path.GetFileName(c.Path)).ToList();
            Task.Factory.StartNew(() =>
            {
                var errors = new List<string>();
                int moved = 0;
                foreach (var c in known)
                {
                    string err = DoMove(Trimmer.MoveFor(c.Path, c.Game, coll));
                    if (err == null) moved++;
                    else errors.Add(Path.GetFileName(c.Path) + ": " + err);
                }
                return Tuple.Create(moved, errors);
            }).ContinueWith(t => W.Dispatcher.BeginInvoke(new Action(() =>
            {
                if (app != null)
                {
                    if (t.Result.Item1 > 0) app.ShowToast(L.T("✓ Moved to the collection: ", "✓ В коллекцию перенесено: ") + t.Result.Item1);
                    var notes = t.Result.Item2.ToList();
                    if (unknown.Count > 0) notes.Add(L.T("No game known — move them one by one, picking the game: ", "Игра неизвестна — перенеси их по одному, выбрав игру: ") + string.Join(", ", unknown));
                    if (notes.Count > 0) app.ShowNotice(L.T("Not everything was moved", "Не всё перенеслось"), notes, false);
                }
                LoadClips(true);
            })));
        }

        void SelDelete()
        {
            if (!selDeleteArmed)
            {
                selDeleteArmed = true;
                BuildSelBar();
                selDeleteTimer.Stop();
                selDeleteTimer.Start();
                return;
            }
            selDeleteTimer.Stop();
            selDeleteArmed = false;
            int done = 0;
            var failed = new List<string>();
            foreach (var c in Selection)
            {
                if (Shell.Recycle(c.Path)) { clips.Remove(c); done++; }
                else failed.Add(Path.GetFileName(c.Path));
            }
            if (app != null)
            {
                if (done > 0) app.ShowToast(L.T("✓ To the Recycle Bin: ", "✓ В корзину: ") + L.N(done, "clip", "clips", "клип", "клипа", "клипов"));
                if (failed.Count > 0) app.ShowNotice(L.T("Not everything was deleted (details in the log)", "Удалилось не всё (подробности в журнале)"), failed, false);
            }
            SelChanged();
            if (clips.Count == 0) LoadClips(true);
        }
    }
}
