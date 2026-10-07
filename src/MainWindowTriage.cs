using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace DeviceGuard
{
    // a clip in "Go through new clips" and what was decided about it
    class TriageVm : Vm
    {
        public const int None = 0, Keep = 1, Trim = 2, Delete = 3, Skip = 4;
        Brush thumb;
        int decision;
        bool current;
        public string Path;
        public bool Starred;   // the star was put here, so undo takes it away
        public Brush Thumb { get { return thumb; } set { Set(ref thumb, value); } }
        public int Decision { get { return decision; } set { decision = value; Notify("Mark", "MarkVis", "MarkBrush", "Dim"); } }
        public bool Current { get { return current; } set { current = value; Notify("Ring", "Dim"); } }
        public string Tip { get { return System.IO.Path.GetFileName(Path); } }
        public string Mark { get { return decision == Keep ? "" : decision == Trim ? "" : decision == Delete ? "" : decision == Skip ? "" : ""; } }
        public Visibility MarkVis { get { return decision == None ? Visibility.Collapsed : Visibility.Visible; } }
        public Brush MarkBrush { get { return Wpf.Br(decision == Keep ? Wpf.Warn : decision == Delete ? Wpf.Bad : Wpf.Text, 255); } }
        public Brush Ring { get { return current ? Wpf.Res<Brush>("Text") : Brushes.Transparent; } }
        public double Dim { get { return current ? 1 : decision != None ? 0.45 : 0.8; } }
    }

    // "Go through new clips": the clips saved lately that nobody looked at, one at a time, playing by themselves.
    // F keeps (a star), X — trim later, Delete — to the Recycle Bin, → — skip, Z — undo, Space — pause.
    // Nothing is deleted on the way: at the end the clips marked for the bin go there with one press, the ones to trim open in the editor.
    partial class MainWindow
    {
        readonly ObservableCollection<TriageVm> triage = new ObservableCollection<TriageVm>();
        readonly Stack<int> triageDone = new Stack<int>();
        List<string> triageFresh = new List<string>();
        string triageName;
        int triageAt;
        bool triageOn, triageBinArmed;
        DispatcherTimer triageClock;

        void InitTriage()
        {
            F<ItemsControl>("TriageQueue").ItemsSource = triage;
            F<ItemsControl>("TriageQueue").AddHandler(Button.ClickEvent, new RoutedEventHandler((s, e) =>
            {
                var vm = ((FrameworkElement)e.OriginalSource).DataContext as TriageVm;
                if (vm == null) return;
                e.Handled = true;
                ShowTriage(triage.IndexOf(vm));
            }));
            F<Button>("BtnTriage").Click += (s, e) => StartTriage(triageFresh, triageName);
            F<Button>("BtnTriageClose").Click += (s, e) => TriageEsc();
            var player = F<MediaElement>("TriagePlayer");
            player.MediaOpened += (s, e) => F<TextBlock>("TriageMsg").Text = "";
            player.MediaEnded += (s, e) => { player.Position = TimeSpan.Zero; player.Play(); };   // a short clip plays round
            player.MediaFailed += (s, e) => F<TextBlock>("TriageMsg").Text = L.T("Can't show this clip here (", "Этот клип здесь не показать (") + e.ErrorException.Message +
                                                                               L.T("). Decide by its name, or open it: Enter.", "). Реши по названию или открой его: Enter.");
            var seek = F<Grid>("TriageSeek");
            seek.MouseLeftButtonDown += (s, e) =>
            {
                if (!player.NaturalDuration.HasTimeSpan || seek.ActualWidth <= 0) return;
                player.Position = TimeSpan.FromSeconds(player.NaturalDuration.TimeSpan.TotalSeconds * e.GetPosition(seek).X / seek.ActualWidth);
            };
            triageClock = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            triageClock.Tick += (s, e) => TriageTick();
            ((FrameworkElement)F<Border>("TriageProgress").Parent).SizeChanged += (s, e) => TriageStats();
            W.PreviewKeyDown += (s, e) =>
            {
                // a decision is a bare key: Ctrl+F out of habit must not star a clip; Ctrl+Z undoes like Z
                var mods = Keyboard.Modifiers;
                if (!triageOn || Keyboard.FocusedElement is TextBox || (mods != ModifierKeys.None && !(e.Key == Key.Z && mods == ModifierKeys.Control))) return;
                bool review = F<Border>("TriageStage").Visibility == Visibility.Visible;
                switch (e.Key)
                {
                    case Key.F: if (review) Decide(TriageVm.Keep); break;
                    case Key.X: if (review) Decide(TriageVm.Trim); break;
                    case Key.Delete: if (review) Decide(TriageVm.Delete); break;
                    case Key.Right: if (review) Decide(TriageVm.Skip); break;
                    case Key.Z: UndoTriage(); break;
                    case Key.Space: if (review) TogglePlay(); break;
                    case Key.Enter: if (review && triageAt < triage.Count) Shell.Open(triage[triageAt].Path); break;
                    case Key.Escape: TriageEsc(); break;
                    default: return;
                }
                e.Handled = true;
            };
        }

        // which clips wait to be gone through: saved in the last two weeks, without a star, not trimmed, not looked at yet
        static List<string> FreshClips(IEnumerable<FileInfo> files, Func<DateTime, HashSet<string>> trimmedSince)
        {
            var edge = DateTime.Now.AddDays(-14);
            var fresh = files.Where(f => f.LastWriteTime > edge && !Favorites.Has(f.FullName) && !Reviewed.Has(f.Name)).ToList();
            if (fresh.Count == 0) return new List<string>();
            var cut = trimmedSince(edge);   // only trims of these two weeks can be made from these clips
            return fresh.Where(f => !cut.Contains(f.Name)).OrderBy(f => f.LastWriteTime).Select(f => f.FullName).ToList();   // in the order they were played
        }

        // for the self-test: what counts as new (files made for it — no star, never looked at)
        public static void TestTriage(Action<bool, string> check)
        {
            string dir = Path.Combine(Path.GetTempPath(), "clipkeeper-triage-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                Func<string, int, FileInfo> make = (name, daysAgo) =>
                {
                    string p = Path.Combine(dir, name);
                    File.WriteAllBytes(p, new byte[16]);
                    File.SetLastWriteTime(p, DateTime.Now.AddDays(-daysAgo).AddMinutes(-1));
                    return new FileInfo(p);
                };
                var files = new List<FileInfo> { make("Replay 2026-10-05 18-00-00.mp4", 1), make("Replay 2026-10-04 18-00-00.mp4", 2),
                                                 make("Replay 2026-09-01 18-00-00.mp4", 30), make("Replay 2026-10-03 18-00-00.mp4", 3) };
                DateTime asked = DateTime.MaxValue;
                var fresh = FreshClips(files, since => { asked = since; return new HashSet<string>(new[] { "Replay 2026-10-03 18-00-00.mp4" }, StringComparer.OrdinalIgnoreCase); });
                check(Math.Abs((DateTime.Now.AddDays(-14) - asked).TotalMinutes) < 1, "going through: only trims of the last two weeks are read");
                check(fresh.Count == 2 && Path.GetFileName(fresh[0]) == "Replay 2026-10-04 18-00-00.mp4",
                      "going through: new = the last two weeks, not trimmed, oldest first (" + string.Join(", ", fresh.Select(Path.GetFileName)) + ")");
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        void ShowTriageButton(List<string> fresh, string name)
        {
            triageFresh = fresh ?? new List<string>();
            triageName = name;
            var b = F<Button>("BtnTriage");
            b.Visibility = triageFresh.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            sp.Children.Add(new TextBlock { Style = S("Icon"), Text = "", FontSize = 12, Margin = new Thickness(0, 0, 8, 0) });
            sp.Children.Add(new TextBlock { Text = L.T("Go through ", "Разобрать ") + L.N(triageFresh.Count, "new clip", "new clips", "новый", "новых", "новых") });
            b.Content = sp;
        }

        void StartTriage(List<string> paths, string name)
        {
            if (paths.Count == 0) return;
            ClearSelection();
            triage.Clear();
            triageDone.Clear();
            foreach (var p in paths)
            {
                var vm = new TriageVm { Path = p };
                triage.Add(vm);
                Thumbs.Request(p, img => { if (img != null) vm.Thumb = Fill(img); });
            }
            triageOn = true;
            F<TextBlock>("TriageTitle").Text = L.T("Going through · ", "Разбор · ") + name;
            pages[PageClips].Visibility = Visibility.Collapsed;
            F<FrameworkElement>("SelBar").Visibility = Visibility.Collapsed;
            F<FrameworkElement>("TriagePage").Visibility = Visibility.Visible;
            BuildTriageKeys();
            ShowTriage(0);
            triageClock.Start();
        }

        void BuildTriageKeys()
        {
            var p = F<UniformGrid>("TriageKeys");
            p.Children.Clear();
            Action<string, string, string, Action, bool> key = (k, glyph, text, act, main) =>
            {
                var sp = new StackPanel { Orientation = Orientation.Horizontal };
                sp.Children.Add(new Border
                {
                    BorderBrush = main ? Wpf.Res<Brush>("Bg") : Wpf.Res<Brush>("LineHi"), BorderThickness = new Thickness(1, 1, 1, 2), CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(6, 0, 6, 0), Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center,
                    Child = new TextBlock { Text = k, FontFamily = Wpf.Res<FontFamily>("MonoFont"), FontSize = 12 },
                });
                if (glyph != null) sp.Children.Add(new TextBlock { Style = S("Icon"), Text = glyph, FontSize = 13, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center });
                sp.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
                var b = new Button { Style = S(main ? "BtnPrimary" : "BtnGhost"), Content = sp, Margin = new Thickness(0, 0, 10, 0), Padding = new Thickness(12, 9, 12, 9),
                                     HorizontalContentAlignment = HorizontalAlignment.Left };
                b.Click += (s, e) => act();
                p.Children.Add(b);
            };
            key("F", "", L.T("Keep", "Оставить"), () => Decide(TriageVm.Keep), false);
            key("X", "", L.T("Trim later", "Обрезать потом"), () => Decide(TriageVm.Trim), false);
            key("Del", "", L.T("Recycle Bin", "В корзину"), () => Decide(TriageVm.Delete), false);
            key("→", null, L.T("Next", "Дальше"), () => Decide(TriageVm.Skip), true);
        }

        void ShowTriage(int i)
        {
            if (i < 0 || triage.Count == 0) return;
            if (i >= triage.Count) { FinishTriage(); return; }
            if (!File.Exists(triage[i].Path))   // deleted or moved meanwhile: nothing to decide, on to the next one
            {
                triage[i].Decision = TriageVm.Skip;
                int after = NextUndecided(i);
                ShowTriage(after >= 0 ? after : triage.Count);
                return;
            }
            triageAt = i;
            for (int k = 0; k < triage.Count; k++) triage[k].Current = k == i;
            F<Border>("TriageDone").Visibility = Visibility.Collapsed;
            F<Border>("TriageStage").Visibility = Visibility.Visible;
            F<UniformGrid>("TriageKeys").Visibility = Visibility.Visible;
            var f = new FileInfo(triage[i].Path);
            var vm = ClipScanner.Describe(f, RootOf(last) ?? f.DirectoryName, null);
            F<TextBlock>("TriageInfo").Text = (vm.Named ? vm.Title + " · " : "") + EventVm.Cap(vm.WhenText) + " · " + vm.SizeText;
            F<TextBlock>("TriageMsg").Text = L.T("Loading…", "Загружаю…");
            var player = F<MediaElement>("TriagePlayer");
            player.Stop();
            player.Source = new Uri(f.FullName);
            player.Play();
            TriageStats();
            W.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
            {
                var c = F<ItemsControl>("TriageQueue").ItemContainerGenerator.ContainerFromIndex(i) as FrameworkElement;
                if (c != null) c.BringIntoView();
            }));
        }

        void TriageStats()
        {
            if (triage.Count == 0) return;
            int done = triage.Count(t => t.Decision != TriageVm.None);
            F<TextBlock>("TriageStats").Text = (triageAt + 1) + L.T(" of ", " из ") + triage.Count + " · " +
                L.T("kept ", "оставлено ") + triage.Count(t => t.Decision == TriageVm.Keep) + " · " +
                L.T("to trim ", "обрезать ") + triage.Count(t => t.Decision == TriageVm.Trim) + " · " +
                L.T("to the bin ", "в корзину ") + triage.Count(t => t.Decision == TriageVm.Delete);
            var bar = F<Border>("TriageProgress");
            bar.Width = ((FrameworkElement)bar.Parent).ActualWidth * done / triage.Count;
        }

        void TriageTick()
        {
            var player = F<MediaElement>("TriagePlayer");
            if (!player.NaturalDuration.HasTimeSpan) { F<TextBlock>("TriageTime").Text = ""; return; }
            double len = player.NaturalDuration.TimeSpan.TotalSeconds, at = player.Position.TotalSeconds;
            F<TextBlock>("TriageTime").Text = TimeSpan.FromSeconds(at).ToString(@"m\:ss") + " / " + TimeSpan.FromSeconds(len).ToString(@"m\:ss");
            var seek = F<Grid>("TriageSeek");
            F<Border>("TriageSeekFill").Width = len > 0 ? seek.ActualWidth * Math.Min(1, at / len) : 0;
        }

        bool triagePaused;
        void TogglePlay()
        {
            var player = F<MediaElement>("TriagePlayer");
            if (triagePaused) player.Play(); else player.Pause();
            triagePaused = !triagePaused;
        }

        void Decide(int what)
        {
            if (triageAt >= triage.Count) return;
            var vm = triage[triageAt];
            Revert(vm);
            vm.Decision = what;
            if (what == TriageVm.Keep && !Favorites.Has(vm.Path)) { Favorites.Toggle(vm.Path); vm.Starred = true; }
            if (what != TriageVm.Delete) Reviewed.Mark(vm.Path, true);   // a clip for the bin counts once it is gone
            triageDone.Push(triageAt);
            triagePaused = false;
            int next = NextUndecided(triageAt);
            ShowTriage(next >= 0 ? next : triage.Count);
        }

        // the next clip nobody decided about yet: after this one, then the ones skipped by clicking ahead in the queue; -1 — none
        int NextUndecided(int after)
        {
            for (int k = after + 1; k < triage.Count; k++) if (triage[k].Decision == TriageVm.None) return k;
            for (int k = 0; k < after && k < triage.Count; k++) if (triage[k].Decision == TriageVm.None) return k;
            return -1;
        }

        // what a decision did to a clip is undone (the star it put, "looked at")
        static void Revert(TriageVm vm)
        {
            if (vm.Starred && Favorites.Has(vm.Path)) Favorites.Toggle(vm.Path);
            vm.Starred = false;
            if (vm.Decision != TriageVm.None) Reviewed.Mark(vm.Path, false);
            vm.Decision = TriageVm.None;
        }

        void UndoTriage()
        {
            if (triageDone.Count == 0) return;
            int i = triageDone.Pop();
            Revert(triage[i]);
            ShowTriage(i);
        }

        void TriageEsc()
        {
            if (F<Border>("TriageDone").Visibility == Visibility.Visible || triage.All(t => t.Decision == TriageVm.None)) CloseTriage(true);
            else FinishTriage();
        }

        // the end: what was decided, the bin with one press, the trims in the editor
        void FinishTriage()
        {
            var player = F<MediaElement>("TriagePlayer");
            player.Stop();
            player.Source = null;
            triageAt = triage.Count;
            foreach (var t in triage) t.Current = false;
            F<Border>("TriageStage").Visibility = Visibility.Collapsed;
            F<UniformGrid>("TriageKeys").Visibility = Visibility.Collapsed;
            F<Border>("TriageDone").Visibility = Visibility.Visible;
            F<TextBlock>("TriageStats").Text = "";
            TriageStats();
            F<TextBlock>("TriageStats").Text = L.N(triage.Count(t => t.Decision != TriageVm.None), "clip", "clips", "клип", "клипа", "клипов") + L.T(" gone through", " разобрано");
            triageBinArmed = false;
            BuildTriageSummary();
        }

        void BuildTriageSummary()
        {
            var p = F<StackPanel>("TriageSummary");
            p.Children.Clear();
            var bin = triage.Where(t => t.Decision == TriageVm.Delete).ToList();
            var trims = triage.Where(t => t.Decision == TriageVm.Trim).Select(t => t.Path).ToList();
            int kept = triage.Count(t => t.Decision == TriageVm.Keep), skipped = triage.Count(t => t.Decision == TriageVm.Skip), left = triage.Count(t => t.Decision == TriageVm.None);
            p.Children.Add(new TextBlock { Text = L.T("Done", "Готово"), Style = S("H1"), FontSize = 21 });
            var lines = new List<string>();
            if (kept > 0) lines.Add(L.T("kept with a star: ", "оставлено со звёздочкой: ") + kept);
            if (trims.Count > 0) lines.Add(L.T("to trim: ", "обрезать: ") + trims.Count);
            if (bin.Count > 0) lines.Add(L.T("for the Recycle Bin: ", "в корзину: ") + bin.Count);
            if (skipped > 0) lines.Add(L.T("skipped: ", "пропущено: ") + skipped);
            if (left > 0) lines.Add(L.T("not decided: ", "без решения: ") + left + L.T(" — they come back next time", " — вернутся в следующий раз"));
            p.Children.Add(new TextBlock { Text = string.Join(" · ", lines), Style = S("SubText"), Margin = new Thickness(0, 8, 0, 16), TextWrapping = TextWrapping.Wrap });
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            if (bin.Count > 0)
            {
                var del = new Button { Style = S("BtnPrimary"), Margin = new Thickness(0, 0, 8, 0),
                                       Content = triageBinArmed ? L.T("Sure? ", "Точно? ") + L.N(bin.Count, "clip", "clips", "клип", "клипа", "клипов") + L.T(" to the bin", " в корзину")
                                                                : L.T("To the Recycle Bin: ", "В корзину: ") + L.N(bin.Count, "clip", "clips", "клип", "клипа", "клипов") };
                del.Click += (s, e) =>
                {
                    if (!triageBinArmed) { triageBinArmed = true; BuildTriageSummary(); return; }
                    var failed = new List<string>();
                    foreach (var t in bin)
                    {
                        if (Shell.Recycle(t.Path)) triage.Remove(t);
                        else failed.Add(Path.GetFileName(t.Path));
                    }
                    if (app != null && failed.Count > 0) app.ShowNotice(L.T("Not everything was deleted (details in the log)", "Удалилось не всё (подробности в журнале)"), failed, false);
                    if (app != null && bin.Count > failed.Count) app.ShowToast(L.T("✓ To the Recycle Bin: ", "✓ В корзину: ") + L.N(bin.Count - failed.Count, "clip", "clips", "клип", "клипа", "клипов"));
                    triageBinArmed = false;
                    BuildTriageSummary();
                };
                row.Children.Add(del);
            }
            if (trims.Count > 0)
            {
                var trim = new Button { Style = S(bin.Count > 0 ? "BtnGhost" : "BtnPrimary"), Margin = new Thickness(0, 0, 8, 0),
                                        Content = L.T("Trim: ", "Обрезать: ") + L.N(trims.Count, "clip", "clips", "клип", "клипа", "клипов") };
                trim.Click += (s, e) => { if (app != null) app.OpenTrim(trims[0], trims); CloseTriage(true); };   // ← → in the editor go through them
                row.Children.Add(trim);
            }
            var done = new Button { Style = S(bin.Count == 0 && trims.Count == 0 ? "BtnPrimary" : "BtnGhost"), Content = L.T("Back to the library", "К библиотеке") };
            done.Click += (s, e) => CloseTriage(true);
            row.Children.Add(done);
            p.Children.Add(row);
            if (bin.Count > 0) p.Children.Add(new TextBlock { Text = L.T("Nothing is deleted until you press it. A clip in the queue can be opened again — click it.",
                                                                         "Пока не нажмёшь, ничего не удаляется. Клип в очереди можно открыть снова — кликни по нему."),
                                                              Style = S("SubText"), FontSize = 12.5, Margin = new Thickness(0, 12, 0, 0), TextWrapping = TextWrapping.Wrap });
        }

        void CloseTriage(bool reload)
        {
            if (!triageOn) return;
            triageOn = false;
            triageClock.Stop();
            var player = F<MediaElement>("TriagePlayer");
            player.Stop();
            player.Source = null;
            triage.Clear();
            F<FrameworkElement>("TriagePage").Visibility = Visibility.Collapsed;
            if (currentPage == PageClips) pages[PageClips].Visibility = Visibility.Visible;
            if (reload) LoadClips(true);
        }

        // for previews: the triage screen on these clips, the first one decided
        public void PreviewTriage(List<string> paths, string name)
        {
            StartTriage(paths, name);
            triageClock.Stop();
            var player = F<MediaElement>("TriagePlayer");
            player.Source = null;
            foreach (var t in triage)
            {
                var img = Thumbs.Load(t.Path, 320, 180);
                if (img != null) t.Thumb = Fill(img);
            }
            triage[0].Decision = TriageVm.Keep;
            ShowTriage(1);
            player.Source = null;
            var frame = Thumbs.Load(triage[1].Path, 1280, 720);
            F<Border>("TriageStage").Background = frame != null ? (Brush)new ImageBrush(frame) { Stretch = Stretch.Uniform } : Brushes.Black;
            F<TextBlock>("TriageMsg").Text = "";
            F<TextBlock>("TriageTime").Text = "0:42 / 2:20";
        }
    }
}
