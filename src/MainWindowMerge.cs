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

namespace DeviceGuard
{
    // "Join": the clips selected in the library become one, in the order they were selected (dragging changes it),
    // back to back or with a fade, kept at their quality or fitted for sharing (Trimmer.Merge). The card covers the library
    // until it is done; Esc or Cancel closes it, and stops a join that is running.
    partial class MainWindow
    {
        class MergeItem { public string Path, Title, Time, Game; public Brush Thumb; public Ffmpeg.MediaInfo Info; }

        Grid merge;
        List<MergeItem> mergeItems;
        bool mergeFade, mergeShare, mergeRunning;
        ShareTarget mergeTarget = ShareTarget.Discord;
        string mergeTitle;
        CancellationTokenSource mergeCts;

        void ShowMerge(List<ClipVm> list)
        {
            if (merge != null || list.Count < 2) return;
            mergeItems = list.Select(c => new MergeItem
            {
                Path = c.Path, Title = c.Named || !c.Source ? c.Title : EventVm.Cap(c.WhenText ?? c.Title), Time = c.Duration, Game = c.Game, Thumb = c.Thumb,
            }).ToList();
            var games = mergeItems.Select(i => i.Game).Distinct().ToList();
            var newest = mergeItems.Max(i => File.GetLastWriteTime(i.Path));
            mergeTitle = games.Count == 1 && KnownGame(games[0])
                ? Covers.Title(games[0]) + L.T(" — best of ", " — лучшее за ") + newest.ToString("d MMMM", L.Culture)
                : L.T("Montage ", "Склейка ") + newest.ToString("d MMMM", L.Culture);
            mergeFade = false;
            mergeShare = false;
            mergeRunning = false;
            merge = new Grid { Background = Wpf.Br("#E60B0C0E"), Margin = new Thickness(0, 44, 0, 0) };
            Grid.SetColumnSpan(merge, 2);
            Panel.SetZIndex(merge, 40);
            F<Grid>("Root").Children.Add(merge);
            BuildMerge();
            // what each clip is: its length, frame and sound (ffprobe) — for the warning and the estimate
            var items = mergeItems.ToList();
            Task.Factory.StartNew(() => { foreach (var i in items) try { i.Info = Ffmpeg.Info(i.Path); } catch (Exception ex) { Log.Write("join: " + ex.Message); } })
                .ContinueWith(t => W.Dispatcher.BeginInvoke(new Action(() => { if (merge != null && !mergeRunning) BuildMerge(); })));
        }

        void CloseMerge()
        {
            if (merge == null) return;
            if (mergeCts != null) mergeCts.Cancel();
            F<Grid>("Root").Children.Remove(merge);
            merge = null;
        }

        MergeJob MergeJobNow()
        {
            return new MergeJob
            {
                Clips = mergeItems.Select(i => i.Path).ToList(), Fade = mergeFade, Mode = mergeShare ? TrimMode.Share : TrimMode.Precise, Target = mergeTarget,
                CustomMb = cfg.ShareCustomMb, Loudness = mergeShare && cfg.ShareLoudness, MixIndex = app != null ? app.Guard.MixAudioIndex() : -1,
                Title = mergeTitle, OutputDir = ReadyRoot,
            };
        }

        Border MergeCard(UIElement body)
        {
            var card = new Border
            {
                Style = S("CardBorder"), Width = 780, Padding = new Thickness(26, 22, 26, 22),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(24, 50, 24, 24), Child = body,
            };
            merge.Children.Clear();
            merge.Children.Add(card);
            return card;
        }

        void BuildMerge()
        {
            var p = new StackPanel();
            MergeCard(p);
            bool known = mergeItems.All(i => i.Info != null);
            var job = MergeJobNow();
            double total = known ? Trimmer.MergedLength(mergeItems.Select(i => i.Info.Duration).ToList(), mergeFade) : 0;

            // the title and what comes out
            var head = new Grid { Margin = new Thickness(0, 0, 0, 16) };
            head.ColumnDefinitions.Add(new ColumnDefinition());
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            head.Children.Add(new TextBlock { Text = L.T("Join ", "Склеить ") + L.N(mergeItems.Count, "clip", "clips", "клип", "клипа", "клипов"),
                                              FontFamily = Wpf.Res<FontFamily>("DisplayFont"), FontSize = 20, FontWeight = FontWeights.SemiBold });
            var stat = new TextBlock { Foreground = Wpf.Res<Brush>("Muted"), FontSize = 13, VerticalAlignment = VerticalAlignment.Center,
                                       Text = known ? Trimmer.Dur(total) + " · " + Trimmer.MergeEstimate(job, mergeItems.Select(i => i.Info).ToList()) : L.T("reading the clips…", "читаю клипы…") };
            Grid.SetColumn(stat, 1);
            head.Children.Add(stat);
            p.Children.Add(head);

            p.Children.Add(MergeStrip());
            p.Children.Add(new TextBlock { Text = L.T("The order is the order you selected them in — drag a clip to move it.", "Порядок — как выделял; перетащи клип, чтобы его передвинуть."),
                                           Style = S("SubText"), FontSize = 12, Margin = new Thickness(2, 8, 0, 0) });

            // a different frame or rate: brought to the first clip's
            if (known)
            {
                var sh = Trimmer.ShapeOf(mergeItems[0].Info);
                var odd = mergeItems.Skip(1).Where(i => { var o = Trimmer.ShapeOf(i.Info); return o.W != sh.W || o.H != sh.H || Math.Abs(o.Fps - sh.Fps) > 0.5; }).ToList();
                string warn = null;
                if (odd.Count > 0)
                    warn = string.Join(", ", odd.Select(i => "«" + i.Title + "» — " + Trimmer.ShapeOf(i.Info).H + "p " + Math.Round(Trimmer.ShapeOf(i.Info).Fps) + L.T(" fps", " к/с"))) +
                           L.T(". Everything is brought to the first clip: ", ". Всё будет приведено к первому клипу: ") + sh.W + "×" + sh.H + ", " + Math.Round(sh.Fps) + L.T(" fps.", " к/с.");
                if (mergeShare && mergeTarget == ShareTarget.Discord && total > 180)
                    warn = (warn != null ? warn + "\n" : "") + Trimmer.Dur(total) + L.T(" into 10 MB — the picture goes down to 480p. Nitro or Telegram suit a long join better.",
                                                                                       " в 10 МБ — картинка опустится до 480p. Для длинной склейки лучше Nitro или Telegram.");
                if (warn != null)
                    p.Children.Add(new TextBlock { Text = warn, Foreground = Wpf.Br(Wpf.Warn, 255), FontSize = 12.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 12, 0, 0) });
            }

            // transition, saving, title
            var kv = new Grid { Margin = new Thickness(0, 18, 0, 0) };
            kv.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) });
            kv.ColumnDefinitions.Add(new ColumnDefinition());
            int row = 0;
            Action<string, UIElement> add = (label, el) =>
            {
                kv.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var l = new TextBlock { Text = label, Foreground = Wpf.Res<Brush>("Sub"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 0, 10) };
                Grid.SetRow(l, row);
                kv.Children.Add(l);
                var fe = (FrameworkElement)el;
                fe.Margin = new Thickness(0, 0, 0, 10);
                Grid.SetRow(fe, row);
                Grid.SetColumn(fe, 1);
                kv.Children.Add(fe);
                row++;
            };
            add(L.T("Transition", "Переход"), Choice(new[] { L.T("Back to back", "Встык"), L.T("Fade 0.3 s", "Затухание 0,3 с") }, mergeFade ? 1 : 0, i => { mergeFade = i == 1; BuildMerge(); }));
            add(L.T("Save", "Сохранить"), Choice(new[] { L.T("Frame-exact", "По кадру"), L.T("Share", "Отправить") }, mergeShare ? 1 : 0, i => { mergeShare = i == 1; BuildMerge(); }));
            if (mergeShare)
            {
                var targets = new[] { ShareTarget.Discord, ShareTarget.Nitro, ShareTarget.Telegram };
                add(L.T("For", "Куда"), Choice(new[] { "Discord 10 MB", "Nitro 500 MB", "Telegram 2 GB" }.Select(t => L.IsRu ? t.Replace("MB", "МБ").Replace("GB", "ГБ") : t).ToArray(),
                                               Array.IndexOf(targets, mergeTarget), i => { mergeTarget = targets[i]; BuildMerge(); }));
                var loud = new CheckBox { Style = S("Toggle"), IsChecked = cfg.ShareLoudness, HorizontalAlignment = HorizontalAlignment.Left };
                loud.Click += (s, e) => { cfg.ShareLoudness = loud.IsChecked == true; Save(); };
                add(L.T("Even loudness", "Ровная громкость"), loud);
            }
            var name = new TextBox { Style = S("Field"), Text = mergeTitle };
            name.TextChanged += (s, e) => mergeTitle = name.Text;
            add(L.T("Title", "Название"), name);
            p.Children.Add(kv);

            var foot = new Grid { Margin = new Thickness(0, 8, 0, 0) };
            foot.ColumnDefinitions.Add(new ColumnDefinition());
            foot.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            foot.Children.Add(new TextBlock { Style = S("SubText"), FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center,
                Text = ReadyRoot != null ? L.T("It goes to Ready", "Сохранится в «Готовые»") : L.T("It goes next to the first clip (choose Ready in the library to keep joins there)", "Сохранится рядом с первым клипом (выбери «Готовые» в библиотеке, чтобы склейки лежали там)") });
            var buttons = new StackPanel { Orientation = Orientation.Horizontal };
            var cancel = Btn(null, L.T("Cancel", "Отмена"), "BtnGhost");
            cancel.Margin = new Thickness(0, 0, 8, 0);
            cancel.Click += (s, e) => CloseMerge();
            var go = Btn("", L.T("Join", "Склеить"), "BtnPrimary");
            go.IsEnabled = known;
            go.Click += (s, e) => RunMerge();
            buttons.Children.Add(cancel);
            buttons.Children.Add(go);
            Grid.SetColumn(buttons, 1);
            foot.Children.Add(buttons);
            p.Children.Add(foot);
        }

        // chips that work as "one of"
        FrameworkElement Choice(string[] texts, int on, Action<int> pick)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            for (int i = 0; i < texts.Length; i++)
            {
                int k = i;
                var b = new ToggleButton { Style = S("Chip"), Content = texts[i], IsChecked = i == on, Padding = new Thickness(12, 5, 12, 5) };
                b.Click += (s, e) => pick(k);
                sp.Children.Add(b);
            }
            return sp;
        }

        // the clips in order; one is dragged onto another's place
        FrameworkElement MergeStrip()
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            Point down = new Point();
            for (int i = 0; i < mergeItems.Count; i++)
            {
                int k = i;
                var it = mergeItems[i];
                if (i > 0) sp.Children.Add(new TextBlock { Style = S("Icon"), Text = mergeFade ? "" : "", FontSize = 12, Foreground = Wpf.Res<Brush>("Muted"),
                                                           VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 6, 24),
                                                           ToolTip = mergeFade ? L.T("a 0.3 s fade", "затухание 0,3 с") : L.T("back to back", "встык") });
                var thumb = new Grid { Height = 90 };
                thumb.Children.Add(new Border { CornerRadius = new CornerRadius(6), Background = it.Thumb ?? Wpf.Res<Brush>("Input") });
                thumb.Children.Add(new Border
                {
                    Width = 22, Height = 22, CornerRadius = new CornerRadius(11), Background = Wpf.Res<Brush>("Text"), Margin = new Thickness(6),
                    HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
                    Child = new TextBlock { Text = (i + 1).ToString(), FontWeight = FontWeights.SemiBold, FontSize = 12, Foreground = Wpf.Res<Brush>("Bg"),
                                            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
                });
                if (it.Time != null)
                    thumb.Children.Add(new Border
                    {
                        CornerRadius = new CornerRadius(5), Background = Wpf.Br("#CC0B0C0E"), Padding = new Thickness(6, 1, 6, 1), Margin = new Thickness(6),
                        HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom,
                        Child = new TextBlock { Text = it.Time, FontFamily = Wpf.Res<FontFamily>("MonoFont"), FontSize = 11, Foreground = Brushes.White },
                    });
                var label = new Grid { Margin = new Thickness(3, 7, 3, 1) };
                label.ColumnDefinitions.Add(new ColumnDefinition());
                label.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                label.Children.Add(new TextBlock { Text = it.Title, FontSize = 12.5, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = Path.GetFileName(it.Path) });
                var grip = new TextBlock { Style = S("Icon"), Text = "", FontSize = 12, Foreground = Wpf.Res<Brush>("Muted"), Margin = new Thickness(6, 0, 0, 0) };
                Grid.SetColumn(grip, 1);
                label.Children.Add(grip);
                var box = new StackPanel();
                box.Children.Add(thumb);
                box.Children.Add(label);
                var piece = new Border
                {
                    Width = 158, Background = Wpf.Res<Brush>("Card2"), BorderBrush = Wpf.Res<Brush>("Line"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(9),
                    Padding = new Thickness(5), Child = box, AllowDrop = true, Cursor = Cursors.SizeAll, Tag = k,
                    ToolTip = L.T("Drag to change the order", "Перетащи, чтобы поменять порядок"),
                };
                piece.PreviewMouseLeftButtonDown += (s, e) => down = e.GetPosition(sp);
                piece.MouseMove += (s, e) =>
                {
                    if (e.LeftButton != MouseButtonState.Pressed || mergeRunning) return;
                    var at = e.GetPosition(sp);
                    if (Math.Abs(at.X - down.X) < 6 && Math.Abs(at.Y - down.Y) < 6) return;
                    piece.Opacity = 0.5;
                    DragDrop.DoDragDrop(piece, k.ToString(), DragDropEffects.Move);
                    piece.Opacity = 1;
                };
                piece.DragOver += (s, e) => { piece.BorderBrush = Wpf.Res<Brush>("Text"); e.Effects = DragDropEffects.Move; e.Handled = true; };
                piece.DragLeave += (s, e) => piece.BorderBrush = Wpf.Res<Brush>("Line");
                piece.Drop += (s, e) =>
                {
                    int from;
                    if (!int.TryParse(e.Data.GetData(DataFormats.StringFormat) as string, out from) || from == k) { piece.BorderBrush = Wpf.Res<Brush>("Line"); return; }
                    var moved = mergeItems[from];
                    mergeItems.RemoveAt(from);
                    mergeItems.Insert(k, moved);
                    W.Dispatcher.BeginInvoke(new Action(BuildMerge));   // after the drag is over
                };
                sp.Children.Add(piece);
            }
            return new ScrollViewer { Content = sp, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, Padding = new Thickness(0, 0, 0, 4) };
        }

        // the join running: progress and the check step by step; then the file
        void RunMerge()
        {
            var job = MergeJobNow();
            var first = mergeItems[0];
            if (first.Info != null) job.Meta = Trimmer.MetaOf(first.Path, first.Info, RootOf(last));
            mergeRunning = true;
            var p = new StackPanel();
            MergeCard(p);
            var title = new TextBlock { Text = L.T("Joining and checking…", "Склеиваю и проверяю…"), FontFamily = Wpf.Res<FontFamily>("DisplayFont"), FontSize = 18, FontWeight = FontWeights.SemiBold };
            p.Children.Add(title);
            var bar = new Grid { Height = 4, Margin = new Thickness(0, 12, 0, 12) };
            bar.Children.Add(new Border { CornerRadius = new CornerRadius(2), Background = Wpf.Br("#26272C") });
            var fill = new Border { CornerRadius = new CornerRadius(2), Background = Wpf.Res<Brush>("Brand"), HorizontalAlignment = HorizontalAlignment.Left, Width = 0 };
            bar.Children.Add(fill);
            p.Children.Add(bar);
            // the same step list as the editor's result (EventVm.Of): a step that changes replaces its line
            var steps = new System.Collections.ObjectModel.ObservableCollection<EventVm>();
            p.Children.Add(new ItemsControl { ItemsSource = steps, ItemTemplate = (DataTemplate)W.FindResource("EventTpl") });
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
            p.Children.Add(buttons);
            var stop = Btn(null, L.T("Stop", "Остановить"), "BtnGhost");
            stop.Click += (s, e) => { if (mergeCts != null) mergeCts.Cancel(); };
            buttons.Children.Add(stop);
            var shown = new Dictionary<TrimStep, EventVm>();
            Action<TrimStep> show = st =>
            {
                var vm = EventVm.Of(st);
                EventVm old;
                if (shown.TryGetValue(st, out old)) steps[steps.IndexOf(old)] = vm;
                else steps.Add(vm);
                shown[st] = vm;
            };
            mergeCts = new CancellationTokenSource();
            var token = mergeCts.Token;
            W.UpdateLayout();
            double full = bar.ActualWidth;
            Task.Factory.StartNew(() => Trimmer.Merge(job, f => W.Dispatcher.BeginInvoke(new Action(() => fill.Width = Math.Max(0, full * f))),
                                                     st => W.Dispatcher.BeginInvoke(new Action(() => show(st))), token), token)
                .ContinueWith(t => W.Dispatcher.BeginInvoke(new Action(() =>
                {
                    mergeRunning = false;
                    if (merge == null) return;   // closed meanwhile
                    bool canceled = t.IsCanceled || (t.IsFaulted && t.Exception.GetBaseException() is OperationCanceledException);
                    bool ok = !t.IsFaulted && !t.IsCanceled && t.Result;
                    if (t.IsFaulted && !canceled) show(new TrimStep { Text = L.T("Error: ", "Ошибка: ") + t.Exception.GetBaseException().Message, State = 2 });
                    fill.Width = full;
                    fill.Background = Wpf.Br(ok ? Wpf.Ok : Wpf.Bad, 255);
                    title.Text = ok ? L.T("✓ Joined and checked — ", "✓ Склеено и проверено — ") + Path.GetFileName(job.Output)
                               : canceled ? L.T("Stopped", "Остановлено") : L.T("The check failed", "Проверка не пройдена");
                    title.Foreground = Wpf.Br(ok ? Wpf.Ok : canceled ? Wpf.Text : Wpf.Bad, 255);
                    buttons.Children.Clear();
                    if (ok && File.Exists(job.Output ?? ""))
                    {
                        var folder = Btn("", L.T("Show in folder", "Показать в папке"), "BtnGhost");
                        folder.Margin = new Thickness(0, 0, 8, 0);
                        folder.Click += (s, e) => Shell.Select(job.Output);
                        var copy = Btn("", L.T("Copy file", "Копировать файл"), "BtnGhost");
                        copy.Margin = new Thickness(0, 0, 8, 0);
                        copy.Click += (s, e) => { if (Shell.CopyFile(job.Output) && app != null) app.ShowToast(L.T("✓ Copied — Ctrl+V into the chat", "✓ Скопировано — Ctrl+V в чат")); };
                        buttons.Children.Add(folder);
                        buttons.Children.Add(copy);
                    }
                    var done = Btn(null, ok ? L.T("Done", "Готово") : L.T("Back", "Назад"), ok ? "BtnPrimary" : "BtnGhost");
                    done.Click += (s, e) =>
                    {
                        if (ok) { CloseMerge(); ClearSelection(); LoadClips(true); if (app != null) app.ClipsChanged(); }
                        else BuildMerge();
                    };
                    buttons.Children.Add(done);
                })));
        }

        // for previews: the join card for these clips, read at once
        public void PreviewMerge(List<ClipVm> list)
        {
            if (list == null) { CloseMerge(); return; }
            ShowMerge(list);
            foreach (var i in mergeItems) try { i.Info = Ffmpeg.Info(i.Path); } catch { }
            BuildMerge();
        }
    }
}
