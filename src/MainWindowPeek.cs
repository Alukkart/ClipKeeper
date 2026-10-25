using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Ellipse = System.Windows.Shapes.Ellipse;

namespace DeviceGuard
{
    // A clip's panel on the right of the library. A click on a clip's frame opens it: the clip plays right there,
    // its name changes in place (the file is renamed), the check says whether the video and every audio track are there,
    // each track shows how loud it is, and every action is a button. A double click opens the clip in the player;
    // ↑ ↓ ← → go to the next clip, Space plays, Esc closes.
    partial class MainWindow
    {
        const double PeekWidth = 392;
        ClipVm peekVm;
        MediaElement peekPlayer;
        Border peekPoster, peekPlayBtn, peekFill;
        TextBlock peekTime, peekCheck, peekSize;
        Ellipse peekCheckDot;
        StackPanel peekTracks;
        bool peekPlaying, peekLoaded, peekDeleteArmed;
        readonly DispatcherTimer peekTick = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        CancellationTokenSource peekCts;

        void InitPeek()
        {
            peekTick.Tick += (s, e) => PeekShowTime();
            // a click on a frame (or a row of the list) opens the clip's panel, a double click — the player (a selection takes the click first)
            clipsList.AddHandler(UIElement.PreviewMouseLeftButtonDownEvent, new MouseButtonEventHandler((s, e) =>
            {
                if (e.Handled || Keyboard.Modifiers != ModifierKeys.None || Selection.Count > 0) return;
                var src = e.OriginalSource as DependencyObject;
                if (InButton(src) || ThumbUnder(src) == null && !RowUnder(src)) return;
                var vm = ClipUnder(src);
                if (vm == null || vm.Editing) return;
                if (e.ClickCount == 2) { PeekStop(); Shell.Open(vm.Path); }
                else Peek(vm);
                e.Handled = true;
            }));
            W.PreviewKeyDown += (s, e) =>
            {
                if (peekVm == null || F<Grid>("PeekPanel").Visibility != Visibility.Visible || Keyboard.FocusedElement is TextBox) return;
                if (e.Key == Key.Escape && Selection.Count == 0) { ClosePeek(); e.Handled = true; }
                else if (e.Key == Key.Space) { PeekPlay(); e.Handled = true; }
                else if (e.Key == Key.Right || e.Key == Key.Down) { PeekStep(1); e.Handled = true; }
                else if (e.Key == Key.Left || e.Key == Key.Up) { PeekStep(-1); e.Handled = true; }
            };
            clips.CollectionChanged += (s, e) =>
            {
                // the clip went away (deleted, moved, the view changed): the panel goes with it
                if (peekVm != null && !clips.Contains(peekVm)) ClosePeek();
            };
        }

        void PeekStep(int d)
        {
            int i = clips.IndexOf(peekVm) + d;
            if (i >= 0 && i < clips.Count) Peek(clips[i]);
        }

        public void Peek(ClipVm vm)
        {
            if (peekVm != null) peekVm.Peeked = false;
            PeekStop();
            peekVm = vm;
            vm.Peeked = true;
            peekDeleteArmed = false;
            var panel = F<Grid>("PeekPanel");
            panel.Visibility = Visibility.Visible;
            var page = F<ScrollViewer>("PageClips");
            page.Margin = new Thickness(page.Margin.Left, page.Margin.Top, PeekWidth - 12, page.Margin.Bottom);
            BuildPeek(vm);
            PeekReadInfo(vm);
        }

        void ClosePeek()
        {
            PeekStop();
            if (peekCts != null) peekCts.Cancel();
            if (peekVm != null) peekVm.Peeked = false;
            peekVm = null;
            F<Grid>("PeekPanel").Visibility = Visibility.Collapsed;
            var page = F<ScrollViewer>("PageClips");
            page.Margin = new Thickness(page.Margin.Left, page.Margin.Top, 0, page.Margin.Bottom);
        }

        void BuildPeek(ClipVm vm)
        {
            var body = F<StackPanel>("PeekBody");
            body.Children.Clear();

            // ── the name (a click on the pencil or on the name renames), where it is from, close ──
            var head = new Grid();
            head.ColumnDefinitions.Add(new ColumnDefinition());
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var names = new StackPanel();
            var title = new TextBlock { Text = vm.Title, FontFamily = Wpf.Res<FontFamily>("DisplayFont"), FontSize = 17, FontWeight = FontWeights.SemiBold,
                                        TextWrapping = TextWrapping.Wrap, Cursor = Cursors.IBeam, ToolTip = L.T("Click to rename (the file gets the name)", "Нажми, чтобы переименовать (файл получит это имя)") };
            var titleRow = new StackPanel { Orientation = Orientation.Horizontal };
            titleRow.Children.Add(title);
            var pencil = new TextBlock { Style = S("Icon"), Text = "", FontSize = 12, Foreground = Wpf.Res<Brush>("Muted"), Margin = new Thickness(8, 3, 0, 0), Cursor = Cursors.Hand };
            titleRow.Children.Add(pencil);
            names.Children.Add(titleRow);
            var edit = new TextBox { Style = S("Field"), Text = vm.Named || !vm.Source ? vm.Title : "", FontSize = 15, FontWeight = FontWeights.SemiBold, MaxLength = 120,
                                     Visibility = Visibility.Collapsed, Padding = new Thickness(8, 4, 8, 4) };
            names.Children.Add(edit);
            var hint = new TextBlock { Text = L.T("Enter — save · Esc — cancel", "Enter — сохранить · Esc — отмена"), FontSize = 11.5, Foreground = Wpf.Res<Brush>("Muted"),
                                       Margin = new Thickness(0, 4, 0, 0), Visibility = Visibility.Collapsed };
            names.Children.Add(hint);
            // the game and when — unless the name already is the date (an unnamed clip inside its game), then the size
            string when = EventVm.Cap(vm.WhenText ?? File.GetLastWriteTime(vm.Path).ToString("dd.MM HH:mm"));
            bool dated = vm.Source && !vm.Named && vm.Title != null && vm.Title.StartsWith(when.Split(',')[0], StringComparison.OrdinalIgnoreCase);
            string where = string.Join(" · ", new[] { KnownGame(vm.Game) ? Covers.Title(vm.Game) : null, dated ? vm.SizeText : when }.Where(x => !string.IsNullOrEmpty(x)));
            names.Children.Add(new TextBlock { Text = where, Foreground = Wpf.Res<Brush>("Muted"), FontSize = 12.5, Margin = new Thickness(0, 3, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis });
            head.Children.Add(names);
            var close = new Button { Style = S("BtnLink"), Content = new TextBlock { Style = S("Icon"), Text = "", FontSize = 12 }, Padding = new Thickness(8), Margin = new Thickness(8, -4, -8, 0),
                                     VerticalAlignment = VerticalAlignment.Top, ToolTip = L.T("Close (Esc)", "Закрыть (Esc)") };
            close.Click += (s, e) => ClosePeek();
            Grid.SetColumn(close, 1);
            head.Children.Add(close);
            body.Children.Add(head);
            Action startEdit = () =>
            {
                titleRow.Visibility = Visibility.Collapsed;
                edit.Visibility = hint.Visibility = Visibility.Visible;
                edit.Focus();
                edit.SelectAll();
            };
            title.MouseLeftButtonUp += (s, e) => startEdit();
            pencil.MouseLeftButtonUp += (s, e) => startEdit();
            edit.PreviewTextInput += (s, e) => { if (e.Text.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) e.Handled = true; };
            edit.PreviewKeyDown += (s, e) =>
            {
                if (e.Key == Key.Escape) { titleRow.Visibility = Visibility.Visible; edit.Visibility = hint.Visibility = Visibility.Collapsed; e.Handled = true; }
                else if (e.Key == Key.Enter)
                {
                    e.Handled = true;
                    PeekStop();   // the player holds the file open: it can't be renamed under it
                    var nv = RenameClip(vm, edit.Text);
                    if (nv != null) Peek(nv);
                    else { titleRow.Visibility = Visibility.Visible; edit.Visibility = hint.Visibility = Visibility.Collapsed; }
                }
            };

            // ── the clip itself: its frame until Play, then the video; a line under it to jump ──
            var stage = new Grid { Height = (PeekWidth - 12 - 36) * 9 / 16, Margin = new Thickness(0, 14, 0, 0), Background = Brushes.Transparent, Cursor = Cursors.Hand };
            stage.Clip = new RectangleGeometry(new Rect(0, 0, PeekWidth - 12 - 36, stage.Height), 6, 6);
            stage.Children.Add(new Border { Background = Brushes.Black, CornerRadius = new CornerRadius(6) });
            peekPoster = new Border { Background = vm.Thumb, CornerRadius = new CornerRadius(6) };
            stage.Children.Add(peekPoster);
            peekPlayer = new MediaElement { LoadedBehavior = MediaState.Manual, UnloadedBehavior = MediaState.Manual, ScrubbingEnabled = true, Stretch = Stretch.Uniform, Visibility = Visibility.Hidden };
            peekPlayer.MediaOpened += (s, e) => { peekLoaded = true; PeekShowTime(); };
            peekPlayer.MediaEnded += (s, e) => { peekPlayer.Pause(); peekPlayer.Position = TimeSpan.Zero; peekPlaying = false; peekPlayBtn.Visibility = Visibility.Visible; peekTick.Stop(); PeekShowTime(); };
            stage.Children.Add(peekPlayer);
            stage.Children.Add(new Border { CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1), BorderBrush = Wpf.Res<Brush>("Ring") });
            peekPlayBtn = new Border
            {
                Width = 48, Height = 48, CornerRadius = new CornerRadius(24), Background = Wpf.Br("#A6000000"), BorderBrush = Wpf.Br("#40FFFFFF"), BorderThickness = new Thickness(1),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock { Style = S("Icon"), Text = "", FontSize = 16, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(3, 0, 0, 0) },
            };
            stage.Children.Add(peekPlayBtn);
            peekTime = new TextBlock { FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White };
            stage.Children.Add(new Border { CornerRadius = new CornerRadius(4), Background = Wpf.Res<Brush>("Plate"), Padding = new Thickness(6, 1, 6, 1), Margin = new Thickness(7),
                                            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Child = peekTime });
            stage.MouseLeftButtonUp += (s, e) => PeekPlay();
            body.Children.Add(stage);
            var seek = new Grid { Height = 14, Background = Brushes.Transparent, Cursor = Cursors.Hand, Margin = new Thickness(0, 4, 0, 0) };
            seek.Children.Add(new Border { Height = 4, CornerRadius = new CornerRadius(2), Background = Wpf.Br("#1AFFFFFF") });
            peekFill = new Border { Height = 4, CornerRadius = new CornerRadius(2), Background = Wpf.Res<Brush>("Brand"), HorizontalAlignment = HorizontalAlignment.Left, Width = 0 };
            seek.Children.Add(peekFill);
            MouseButtonEventHandler down = (s, e) => { seek.CaptureMouse(); PeekSeek(e.GetPosition(seek).X / Math.Max(1, seek.ActualWidth)); };
            seek.MouseLeftButtonDown += down;
            seek.MouseMove += (s, e) => { if (seek.IsMouseCaptured) PeekSeek(e.GetPosition(seek).X / Math.Max(1, seek.ActualWidth)); };
            seek.MouseLeftButtonUp += (s, e) => seek.ReleaseMouseCapture();
            body.Children.Add(seek);
            PeekShowTime();

            // ── what it is ──
            var kv = new Grid { Margin = new Thickness(0, 12, 0, 0) };
            kv.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
            kv.ColumnDefinitions.Add(new ColumnDefinition());
            long bytes = 0;
            try { bytes = new FileInfo(vm.Path).Length; } catch { }
            PeekRow(kv, L.T("File", "Файл"), new TextBlock { Text = Path.GetFileName(vm.Path), TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = vm.Path });
            peekSize = new TextBlock { Text = Fmt.Size(bytes), TextTrimming = TextTrimming.CharacterEllipsis };
            PeekRow(kv, L.T("Size", "Размер"), peekSize);
            var check = new StackPanel { Orientation = Orientation.Horizontal };
            peekCheckDot = new Ellipse { Width = 7, Height = 7, Fill = Wpf.Res<Brush>("Muted"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 1, 8, 0) };
            check.Children.Add(peekCheckDot);
            peekCheck = new TextBlock { Text = Ffmpeg.Available ? L.T("checking…", "проверяю…") : L.T("no ffmpeg — not checked", "нет ffmpeg — не проверить"), TextWrapping = TextWrapping.Wrap };
            check.Children.Add(peekCheck);
            PeekRow(kv, L.T("Check", "Проверка"), check);
            body.Children.Add(kv);

            body.Children.Add(new TextBlock { Text = L.T("Tracks", "Дорожки"), Foreground = Wpf.Br("#5F646D"), FontSize = 11.5, Margin = new Thickness(0, 16, 0, 6) });
            peekTracks = new StackPanel();
            peekTracks.Children.Add(new TextBlock { Text = Ffmpeg.Available ? L.T("reading…", "читаю…") : "—", Foreground = Wpf.Res<Brush>("Muted"), FontSize = 12.5 });
            body.Children.Add(peekTracks);

            // ── what to do with it ──
            var acts = new UniformGrid { Columns = 3, Margin = new Thickness(-3, 16, -3, 0) };
            acts.Children.Add(PeekAct("", L.T("Trim", "Обрезать"), true, () => { PeekStop(); if (app != null) app.OpenTrim(vm.Path, clips.Select(c => c.Path).ToList()); }));
            acts.Children.Add(PeekAct("", L.T("Share", "Поделиться"), false, () => { PeekStop(); if (app != null) app.OpenShare(vm.Path, clips.Select(c => c.Path).ToList()); },
                                      L.T("The editor on \"Share\": Discord 10 MB, Nitro, Telegram or GIF", "Редактор на «Поделиться»: Discord 10 МБ, Nitro, Telegram или GIF")));
            acts.Children.Add(PeekAct("", L.T("Copy", "Копировать"), false, () =>
            {
                if (Shell.CopyFile(vm.Path) && app != null) app.ShowToast(L.T("✓ Clip copied — paste it into Discord or Telegram (Ctrl+V)", "✓ Клип скопирован — вставь его в Discord или Telegram (Ctrl+V)"));
            }));
            acts.Children.Add(PeekAct(vm.Fav ? "" : "", L.T("Favorite", "Избранное"), false, () =>
            {
                vm.Fav = Favorites.Toggle(vm.Path);
                if (!vm.Fav && gameView == FavKey) { clips.Remove(vm); return; }
                BuildPeek(vm);
                PeekReadInfo(vm);
            }, vm.Fav ? L.T("Remove from favorites", "Убрать из избранного") : L.T("Add to favorites", "В избранное")));
            acts.Children.Add(PeekAct("", L.T("Folder", "Папка"), false, () => Shell.Select(vm.Path)));
            if (vm.ReturnTip != null)
            {
                Button anchor = null;
                anchor = PeekAct("", L.T("Collection", "В коллекцию"), false, () => ToCollection(vm, anchor), vm.ReturnTip);
                acts.Children.Add(anchor);
            }
            Button del = null;
            del = PeekAct("", L.T("Delete", "Удалить"), false, () =>
            {
                if (!peekDeleteArmed)
                {
                    peekDeleteArmed = true;
                    ((TextBlock)((StackPanel)del.Content).Children[1]).Text = L.T("Sure?", "Точно?");
                    var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
                    t.Tick += (s, e) => { t.Stop(); peekDeleteArmed = false; ((TextBlock)((StackPanel)del.Content).Children[1]).Text = L.T("Delete", "Удалить"); };
                    t.Start();
                    return;
                }
                PeekStop();
                if (Shell.Recycle(vm.Path))
                {
                    int i = clips.IndexOf(vm);
                    clips.Remove(vm);
                    if (app != null) app.ShowToast(L.T("✓ Clip moved to the Recycle Bin", "✓ Клип перемещён в корзину"));
                    if (clips.Count > 0) Peek(clips[Math.Min(i, clips.Count - 1)]);   // the next one, like a list in Explorer
                }
            }, L.T("Move to the Recycle Bin (press twice)", "В корзину (нажми два раза)"));
            del.Foreground = Wpf.Br("#FF8A80");
            acts.Children.Add(del);
            body.Children.Add(acts);
            body.Children.Add(new TextBlock { Text = L.T("Double click on a frame — the player · ← → — the next clip", "Двойной клик по кадру — плеер · ← → — следующий клип"),
                                              Foreground = Wpf.Br("#5F646D"), FontSize = 11.5, Margin = new Thickness(0, 14, 0, 0), TextWrapping = TextWrapping.Wrap });
        }

        static void PeekRow(Grid g, string label, FrameworkElement value)
        {
            int r = g.RowDefinitions.Count;
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var l = new TextBlock { Text = label, Foreground = Wpf.Res<Brush>("Muted"), Margin = new Thickness(0, 0, 0, 8) };
            Grid.SetRow(l, r);
            g.Children.Add(l);
            value.Margin = new Thickness(0, 0, 0, 8);
            Grid.SetRow(value, r);
            Grid.SetColumn(value, 1);
            g.Children.Add(value);
        }

        Button PeekAct(string glyph, string text, bool primary, Action click, string tip = null)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            sp.Children.Add(new TextBlock { Style = S("Icon"), Text = glyph, FontSize = 12, Margin = new Thickness(0, 0, 7, 0) });
            sp.Children.Add(new TextBlock { Text = text, TextTrimming = TextTrimming.CharacterEllipsis });
            var b = new Button { Style = S(primary ? "BtnPrimary" : "BtnGhost"), Content = sp, Margin = new Thickness(3), Padding = new Thickness(8, 6, 8, 6), ToolTip = tip };
            b.Click += (s, e) => click();
            return b;
        }

        // ── the player ──
        void PeekPlay()
        {
            if (peekPlayer == null || peekVm == null) return;
            if (!peekLoaded && peekPlayer.Source == null)
            {
                peekPlayer.Source = new Uri(peekVm.Path);
                peekPlayer.Visibility = Visibility.Visible;
            }
            if (peekPlaying) { peekPlayer.Pause(); peekPlaying = false; peekPlayBtn.Visibility = Visibility.Visible; peekTick.Stop(); }
            else
            {
                peekPlayer.Play();
                peekPlaying = true;
                peekPlayBtn.Visibility = Visibility.Collapsed;
                peekPoster.Visibility = Visibility.Hidden;
                peekPlayer.Visibility = Visibility.Visible;
                peekTick.Start();
            }
            PeekShowTime();
        }

        void PeekSeek(double at)
        {
            if (peekPlayer == null || peekVm == null) return;
            at = Math.Max(0, Math.Min(1, at));
            if (peekPlayer.Source == null)
            {
                peekPlayer.Source = new Uri(peekVm.Path);
                peekPlayer.Visibility = Visibility.Visible;
                peekPoster.Visibility = Visibility.Hidden;
                peekPlayer.Pause();   // opens the file and shows the frame, without sound
            }
            peekPlayer.Position = TimeSpan.FromSeconds(at * Math.Max(0, peekVm.Seconds));
            peekPoster.Visibility = Visibility.Hidden;
            PeekShowTime();
        }

        void PeekShowTime()
        {
            if (peekTime == null || peekVm == null) return;
            double len = peekLoaded && peekPlayer.NaturalDuration.HasTimeSpan ? peekPlayer.NaturalDuration.TimeSpan.TotalSeconds : peekVm.Seconds;
            double now = peekPlayer != null && peekPlayer.Source != null ? peekPlayer.Position.TotalSeconds : 0;
            peekTime.Text = (now > 0 || peekPlaying ? Clock(now) + " / " : "") + Clock(len);
            var track = peekFill.Parent as Grid;
            peekFill.Width = len > 0 && track != null ? Math.Min(1, now / len) * track.ActualWidth : 0;
        }

        static string Clock(double sec)
        {
            var t = TimeSpan.FromSeconds(Math.Max(0, Math.Round(sec)));
            return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
        }

        // the player lets the file go: it can be renamed, trimmed or deleted
        void PeekStop()
        {
            peekTick.Stop();
            peekPlaying = false;
            peekLoaded = false;
            if (peekPlayer == null) return;
            peekPlayer.Stop();
            peekPlayer.Close();
            peekPlayer.Source = null;
        }

        // ── what ffprobe and the level meter say: the picture, the tracks, how loud each is ──
        void PeekReadInfo(ClipVm vm)
        {
            if (!Ffmpeg.Available) return;
            if (peekCts != null) peekCts.Cancel();
            peekCts = new CancellationTokenSource();
            var token = peekCts.Token;
            string path = vm.Path;
            Task.Factory.StartNew(() => Ffmpeg.Info(path), token).ContinueWith(t => W.Dispatcher.BeginInvoke(new Action(() =>
            {
                if (peekVm != vm || token.IsCancellationRequested) return;
                if (t.IsFaulted) { PeekVerdict(2, L.T("the file does not read: ", "файл не читается: ") + t.Exception.GetBaseException().Message); peekTracks.Children.Clear(); return; }
                var info = t.Result;
                var v = info.Streams.FirstOrDefault(x => x.Type == "video");
                long bytes = info.Size;
                if (bytes <= 0) try { bytes = new FileInfo(path).Length; } catch { }
                peekSize.Text = Fmt.Size(bytes) + (v != null && v.Width > 0 ? " · " + v.Width + "×" + v.Height : "") + (v != null && v.Fps > 1 ? " · " + Math.Round(v.Fps) + L.T(" fps", " к/с") : "");
                if (vm.Seconds <= 0) vm.Seconds = info.Duration;
                PeekShowTime();
                var audio = info.Audio;
                peekTracks.Children.Clear();
                if (audio.Count == 0) peekTracks.Children.Add(new TextBlock { Text = L.T("no audio tracks", "звуковых дорожек нет"), Foreground = Wpf.Res<Brush>("Muted") });
                var meters = audio.Select((a, i) => PeekTrackRow(a.Title, i)).ToList();
                foreach (var m in meters) peekTracks.Children.Add(m.Item1);
                if (v == null) { PeekVerdict(2, L.T("no video in the file", "в файле нет видео")); return; }
                PeekVerdict(-1, L.T("measuring the tracks…", "меряю дорожки…"));
                PeekMeasure(vm, path, audio.Count, meters.Select(m => m.Item2).ToList(), token);
            })), TaskScheduler.Default);
        }

        // a track: an icon by its name, the name, a bar of how loud it is and the number
        Tuple<FrameworkElement, Action<double>> PeekTrackRow(string name, int i)
        {
            string n = (name ?? "").ToLowerInvariant();
            string glyph = n.Contains("mic") || n.Contains("микр") || n.Contains("voice") ? ""
                         : n.Contains("discord") || n.Contains("chat") || n.Contains("чат") || n.Contains("teamspeak") ? "" : "";
            var g = new Grid { Margin = new Thickness(0, 0, 0, 7) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(62) });
            g.Children.Add(new TextBlock { Style = S("Icon"), Text = glyph, FontSize = 13, Foreground = Wpf.Res<Brush>("Muted") });
            var label = new TextBlock { Text = string.IsNullOrEmpty(name) ? L.T("Track ", "Дорожка ") + (i + 1) : name, Foreground = Wpf.Res<Brush>("Sub"), TextTrimming = TextTrimming.CharacterEllipsis,
                                        VerticalAlignment = VerticalAlignment.Center, ToolTip = L.T("Track ", "Дорожка ") + (i + 1) };
            Grid.SetColumn(label, 1);
            g.Children.Add(label);
            var track = new Grid { Height = 6, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 8, 0) };
            track.Children.Add(new Border { CornerRadius = new CornerRadius(3), Background = Wpf.Br("#12FFFFFF") });
            var fill = new Border { CornerRadius = new CornerRadius(3), HorizontalAlignment = HorizontalAlignment.Left, Width = 0, Background = Wpf.Res<Brush>("Ok") };
            track.Children.Add(fill);
            Grid.SetColumn(track, 2);
            g.Children.Add(track);
            var db = new TextBlock { Text = "…", Foreground = Wpf.Res<Brush>("Muted"), FontSize = 12, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(db, 3);
            g.Children.Add(db);
            Action<double> show = mean =>
            {
                bool silent = double.IsNegativeInfinity(mean) || mean < SilentDb;
                db.Text = silent ? L.T("silence", "тишина") : "−" + Math.Abs(mean).ToString("0") + L.T(" dB", " дБ");
                db.Foreground = silent ? Wpf.Res<Brush>("Warn") : Wpf.Res<Brush>("Muted");
                // −60 dB and below is empty, −10 dB is full
                double k = silent ? 0 : Math.Max(0.04, Math.Min(1, (mean + 60) / 50));
                fill.Width = k * Math.Max(0, track.ActualWidth > 0 ? track.ActualWidth : 160);
                fill.Background = Wpf.Res<Brush>(silent ? "Warn" : "Ok");
            };
            return Tuple.Create((FrameworkElement)g, show);
        }

        // the average level below which a track counts as silent (an untouched mic still hisses around −70)
        const double SilentDb = -60;

        void PeekMeasure(ClipVm vm, string path, int n, List<Action<double>> shows, CancellationToken token)
        {
            var levels = new double[n];
            Task.Factory.StartNew(() =>
            {
                for (int i = 0; i < n && !token.IsCancellationRequested; i++)
                {
                    int k = i;
                    double mean = double.NegativeInfinity;
                    try { mean = Ffmpeg.MeanDb(path, k, token); } catch { }
                    levels[k] = mean;
                    W.Dispatcher.BeginInvoke(new Action(() => { if (peekVm == vm && !token.IsCancellationRequested) shows[k](mean); }));
                }
            }, token).ContinueWith(t => W.Dispatcher.BeginInvoke(new Action(() =>
            {
                if (peekVm != vm || token.IsCancellationRequested) return;
                var silent = Enumerable.Range(0, n).Where(i => double.IsNegativeInfinity(levels[i]) || levels[i] < SilentDb).Select(i => (i + 1).ToString()).ToList();
                if (n == 0) PeekVerdict(1, L.T("video only, no sound", "только видео, без звука"));
                else if (silent.Count == 0) PeekVerdict(0, L.T("all in place: the video and ", "всё на месте: видео и ") + L.N(n, "track", "tracks", "дорожка", "дорожки", "дорожек") + L.T(" with sound", " со звуком"));
                else PeekVerdict(1, (silent.Count == 1 ? L.T("silence in track ", "тишина в дорожке ") : L.T("silence in tracks ", "тишина в дорожках ")) + string.Join(", ", silent));
            })), TaskScheduler.Default);
        }

        void PeekVerdict(int level, string text)
        {
            peekCheck.Text = text;
            peekCheckDot.Fill = level < 0 ? Wpf.Res<Brush>("Muted") : Wpf.Br(Wpf.LevelColor(level), 255);
        }

        // for previews: the panel of a clip with what reading it would show
        public void PreviewPeek(int index)
        {
            if (index < 0) { ClosePeek(); return; }
            if (index >= clips.Count) return;
            var vm = clips[index];
            Peek(vm);
            if (peekCts != null) peekCts.Cancel();
            var info = Ffmpeg.Available ? Ffmpeg.Info(vm.Path) : null;
            if (info == null) return;
            var v = info.Streams.FirstOrDefault(x => x.Type == "video");
            peekSize.Text = Fmt.Size(info.Size) + (v != null ? " · " + v.Width + "×" + v.Height + " · " + Math.Round(v.Fps) + L.T(" fps", " к/с") : "");
            peekTracks.Children.Clear();
            var rows = info.Audio.Select((a, i) => PeekTrackRow(a.Title, i)).ToList();
            foreach (var r in rows) peekTracks.Children.Add(r.Item1);
            Preview.Pump();
            var fake = new[] { -21.0, -34.0, -73.0, -19.0 };
            for (int i = 0; i < rows.Count; i++) rows[i].Item2(fake[i % fake.Length]);
            PeekVerdict(1, L.T("silence in track 3", "тишина в дорожке 3"));
        }
    }
}
