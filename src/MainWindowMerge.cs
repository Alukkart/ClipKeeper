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
using System.Windows.Media.Imaging;

namespace DeviceGuard
{
    // "Join": the clips selected in the library become one, in the order they were selected (dragging a card or its
    // ‹ › changes it), each whole or only a part of it (a click on a card opens its frames with two handles),
    // back to back or with a fade, kept at their quality or fitted for sharing (Trimmer.Merge), saved where the user says.
    // The card covers the library until it is done; Esc or Cancel closes it, and stops a join that is running.
    partial class MainWindow
    {
        class MergeItem
        {
            public string Path, Title, Time, Game;
            public Brush Thumb;
            public Ffmpeg.MediaInfo Info;
            public double[] Range;        // the part that goes into the join, null — the whole clip
            public BitmapSource Strip;    // frames along the clip, for choosing the part
            public bool StripAsked;
        }

        const int SaveReady = 0, SaveBesideFirst = 1, SaveFolder = 2;
        Grid merge;
        List<MergeItem> mergeItems;
        bool mergeFade, mergeShare, mergeRunning;
        ShareTarget mergeTarget = ShareTarget.Discord;
        string mergeTitle, mergeDir;
        int mergeWhere = SaveReady, mergeOpen = -1;   // where it is saved; the card whose part is being chosen
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
            mergeOpen = -1;
            if (mergeWhere == SaveReady && ReadyRoot == null) mergeWhere = SaveBesideFirst;
            merge = new Grid { Background = Wpf.Br("#E60B0C0E"), Margin = new Thickness(0, 44, 0, 0) };
            Grid.SetColumnSpan(merge, 2);
            Panel.SetZIndex(merge, 40);
            F<Grid>("Root").Children.Add(merge);
            BuildMerge();
            // what each clip is: its length, frame and sound (ffprobe) — for the warning, the estimate and the parts
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

        string MergeFolder
        {
            get
            {
                if (mergeWhere == SaveReady && ReadyRoot != null) return ReadyRoot;
                if (mergeWhere == SaveFolder && Existing(mergeDir) != null) return mergeDir;
                return Path.GetDirectoryName(mergeItems[0].Path);
            }
        }

        MergeJob MergeJobNow()
        {
            return new MergeJob
            {
                Clips = mergeItems.Select(i => i.Path).ToList(), Ranges = mergeItems.Select(i => i.Range).ToList(),
                Fade = mergeFade, Mode = mergeShare ? TrimMode.Share : TrimMode.Precise, Target = mergeTarget,
                CustomMb = cfg.ShareCustomMb, Loudness = mergeShare && cfg.ShareLoudness, MixIndex = app != null ? app.Guard.MixAudioIndex() : -1,
                Title = mergeTitle, OutputDir = MergeFolder,
            };
        }

        Border MergeCard(UIElement body)
        {
            var card = new Border
            {
                Style = S("CardBorder"), Width = 780, Padding = new Thickness(26, 22, 26, 22),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(24, 50, 24, 24),
                Child = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 640 },
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
            double total = known ? Trimmer.MergedLength(mergeItems.Select((i, k) => { var r = Trimmer.RangeOf(job, k, i.Info); return r[1] - r[0]; }).ToList(), mergeFade) : 0;

            // the title and what comes out
            var head = new Grid { Margin = new Thickness(0, 0, 0, 10) };
            head.ColumnDefinitions.Add(new ColumnDefinition());
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            head.Children.Add(new TextBlock { Text = L.T("Join ", "Склеить ") + L.N(mergeItems.Count, "clip", "clips", "клип", "клипа", "клипов"),
                                              FontFamily = Wpf.Res<FontFamily>("DisplayFont"), FontSize = 20, FontWeight = FontWeights.SemiBold });
            var stat = new TextBlock { Foreground = Wpf.Res<Brush>("Muted"), FontSize = 13, VerticalAlignment = VerticalAlignment.Center,
                                       Text = known ? Trimmer.Dur(total) + " · " + Trimmer.MergeEstimate(job, mergeItems.Select(i => i.Info).ToList()) : L.T("reading the clips…", "читаю клипы…") };
            Grid.SetColumn(stat, 1);
            head.Children.Add(stat);
            p.Children.Add(head);

            // how the order changes: said before the cards, not under them
            var hint = new Grid { Margin = new Thickness(0, 0, 0, 10) };
            hint.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            hint.ColumnDefinitions.Add(new ColumnDefinition());
            hint.Children.Add(new TextBlock { Style = S("Icon"), Text = "", FontSize = 13, Foreground = Wpf.Res<Brush>("Sub"), Margin = new Thickness(0, 2, 0, 0) });
            var hintText = new TextBlock { Style = S("SubText"), FontSize = 12.5, Margin = new Thickness(8, 0, 0, 0), TextWrapping = TextWrapping.Wrap,
                Text = L.T("The order: drag a card or press ‹ › on it. A click on a card takes only a part of the clip.",
                           "Порядок: перетащи карточку или нажми на ней ‹ ›. Клик по карточке — взять из клипа только кусок.") };
            Grid.SetColumn(hintText, 1);
            hint.Children.Add(hintText);
            p.Children.Add(hint);

            p.Children.Add(MergeStrip());
            if (mergeOpen >= 0 && mergeOpen < mergeItems.Count) p.Children.Add(MergeRange(mergeOpen));

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

            // transition, saving, where, title
            var kv = new Grid { Margin = new Thickness(0, 18, 0, 0) };
            kv.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) });
            kv.ColumnDefinitions.Add(new ColumnDefinition());
            int row = 0;
            Action<string, UIElement> add = (label, el) =>
            {
                kv.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var l = new TextBlock { Text = label, Foreground = Wpf.Res<Brush>("Sub"), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 5, 0, 10) };
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
                add(L.T("For", "Для"), Choice(new[] { "Discord 10 MB", "Nitro 500 MB", "Telegram 2 GB" }.Select(t => L.IsRu ? t.Replace("MB", "МБ").Replace("GB", "ГБ") : t).ToArray(),
                                              Array.IndexOf(targets, mergeTarget), i => { mergeTarget = targets[i]; BuildMerge(); }));
                var loud = new CheckBox { Style = S("Toggle"), IsChecked = cfg.ShareLoudness, HorizontalAlignment = HorizontalAlignment.Left };
                loud.Click += (s, e) => { cfg.ShareLoudness = loud.IsChecked == true; Save(); };
                add(L.T("Even loudness", "Ровная громкость"), loud);
            }
            add(L.T("Save to", "Куда"), MergeWhere());
            var name = new TextBox { Style = S("Field"), Text = mergeTitle };
            name.TextChanged += (s, e) => mergeTitle = name.Text;
            add(L.T("Title", "Название"), name);
            p.Children.Add(kv);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
            var cancel = Btn(null, L.T("Cancel", "Отмена"), "BtnGhost");
            cancel.Margin = new Thickness(0, 0, 8, 0);
            cancel.Click += (s, e) => CloseMerge();
            var go = Btn("", L.T("Join", "Склеить"), "BtnPrimary");
            go.IsEnabled = known;
            go.Click += (s, e) => RunMerge();
            buttons.Children.Add(cancel);
            buttons.Children.Add(go);
            p.Children.Add(buttons);
        }

        // where the join is saved: Ready, next to the first clip, or a folder of one's own; the path is shown under the choice
        FrameworkElement MergeWhere()
        {
            var sp = new StackPanel();
            var options = new List<Tuple<int, string>>();
            if (ReadyRoot != null) options.Add(Tuple.Create(SaveReady, L.T("Ready", "Готовые")));
            options.Add(Tuple.Create(SaveBesideFirst, L.T("Next to the first clip", "Рядом с первым клипом")));
            options.Add(Tuple.Create(SaveFolder, Existing(mergeDir) != null ? L.T("Folder: ", "Папка: ") + Path.GetFileName(mergeDir.TrimEnd('\\')) : L.T("Choose a folder…", "Выбрать папку…")));
            sp.Children.Add(Choice(options.Select(o => o.Item2).ToArray(), Math.Max(0, options.FindIndex(o => o.Item1 == mergeWhere)), i =>
            {
                int where = options[i].Item1;
                if (where == SaveFolder)
                {
                    // a click on "Folder" always lets choose another one
                    string picked = FolderPicker.Pick(W, L.T("Where to save the join", "Куда сохранить склейку"), Existing(mergeDir) ?? MergeFolder);
                    if (picked != null) { mergeDir = picked; mergeWhere = SaveFolder; }
                }
                else mergeWhere = where;
                BuildMerge();
            }));
            string dir = MergeFolder;
            sp.Children.Add(new TextBlock { Text = dir, ToolTip = dir, FontFamily = Wpf.Res<FontFamily>("MonoFont"), FontSize = 11.5, Foreground = Wpf.Res<Brush>("Muted"),
                                            TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(2, 6, 0, 0) });
            return sp;
        }

        // chips that work as "one of"
        FrameworkElement Choice(string[] texts, int on, Action<int> pick)
        {
            var sp = new WrapPanel();
            for (int i = 0; i < texts.Length; i++)
            {
                int k = i;
                var b = new ToggleButton { Style = S("Chip"), Content = texts[i], IsChecked = i == on, Padding = new Thickness(12, 5, 12, 5) };
                b.Click += (s, e) => pick(k);
                sp.Children.Add(b);
            }
            return sp;
        }

        void MoveMergeItem(int from, int to)
        {
            if (to < 0 || to >= mergeItems.Count || from == to) return;
            var moved = mergeItems[from];
            mergeItems.RemoveAt(from);
            mergeItems.Insert(to, moved);
            if (mergeOpen == from) mergeOpen = to;
            else if (mergeOpen >= 0) mergeOpen = mergeItems.IndexOf(mergeItems[mergeOpen]);
            BuildMerge();
        }

        // the clips in order: each card has a grip, ‹ › on hover, lifts under the mouse; one is dragged onto another's place,
        // a click opens its part
        FrameworkElement MergeStrip()
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
            Point down = new Point();
            bool dragged = false;
            for (int i = 0; i < mergeItems.Count; i++)
            {
                int k = i;
                var it = mergeItems[i];
                bool open = k == mergeOpen;
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
                // the grip: what can be grabbed, always in sight
                thumb.Children.Add(new Border
                {
                    CornerRadius = new CornerRadius(5), Background = Wpf.Br("#CC0B0C0E"), Padding = new Thickness(5, 3, 5, 3), Margin = new Thickness(6),
                    HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
                    Child = new TextBlock { Style = S("Icon"), Text = "", FontSize = 12, Foreground = Brushes.White },
                });
                string time = it.Range != null ? Trimmer.Dur(it.Range[0]) + "–" + Trimmer.Dur(it.Range[1]) : it.Time;
                if (time != null)
                    thumb.Children.Add(new Border
                    {
                        CornerRadius = new CornerRadius(5), Background = it.Range != null ? Wpf.Res<Brush>("Text") : Wpf.Br("#CC0B0C0E"), Padding = new Thickness(6, 1, 6, 1), Margin = new Thickness(6),
                        HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom,
                        Child = new TextBlock { Text = time, FontFamily = Wpf.Res<FontFamily>("MonoFont"), FontSize = 11, Foreground = it.Range != null ? Wpf.Res<Brush>("Bg") : Brushes.White },
                    });
                // ‹ › — the same without dragging
                var arrows = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom,
                                              Margin = new Thickness(6), Opacity = 0 };
                Func<string, string, int, Button> arrow = (glyph, tip, to) =>
                {
                    var b = new Button { Style = S("BtnBare"), ToolTip = tip, IsEnabled = to >= 0 && to < mergeItems.Count, Margin = new Thickness(0, 0, 4, 0),
                                         Content = new Border { Width = 24, Height = 22, CornerRadius = new CornerRadius(5), Background = Wpf.Br("#E60B0C0E"),
                                                                Child = new TextBlock { Style = S("Icon"), Text = glyph, FontSize = 10, Foreground = Brushes.White,
                                                                                        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } } };
                    if (!b.IsEnabled) b.Opacity = 0.35;
                    b.Click += (s, e) => { e.Handled = true; MoveMergeItem(k, to); };
                    b.PreviewMouseLeftButtonUp += (s, e) => dragged = true;   // not a click on the card
                    return b;
                };
                arrows.Children.Add(arrow("", L.T("Earlier", "Раньше"), k - 1));
                arrows.Children.Add(arrow("", L.T("Later", "Позже"), k + 1));
                thumb.Children.Add(arrows);

                var label = new TextBlock { Text = it.Title, FontSize = 12.5, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = Path.GetFileName(it.Path), Margin = new Thickness(3, 7, 3, 1) };
                var box = new StackPanel();
                box.Children.Add(thumb);
                box.Children.Add(label);
                var lift = new TranslateTransform();
                var piece = new Border
                {
                    Width = 158, Background = Wpf.Res<Brush>("Card2"), BorderBrush = open ? Wpf.Res<Brush>("Text") : Wpf.Res<Brush>("Line"),
                    BorderThickness = new Thickness(open ? 2 : 1), CornerRadius = new CornerRadius(9), Padding = new Thickness(open ? 4 : 5),
                    Child = box, AllowDrop = true, Cursor = Cursors.SizeAll, RenderTransform = lift,
                    ToolTip = L.T("Drag to move · click to take a part", "Перетащи, чтобы передвинуть · клик — выбрать кусок"),
                };
                var rest = piece.BorderBrush;
                piece.MouseEnter += (s, e) => { arrows.Opacity = 1; lift.Y = -3; if (!open) piece.BorderBrush = Wpf.Res<Brush>("Sub"); };
                piece.MouseLeave += (s, e) => { arrows.Opacity = 0; lift.Y = 0; piece.BorderBrush = rest; };
                piece.PreviewMouseLeftButtonDown += (s, e) => { down = e.GetPosition(sp); dragged = false; };
                piece.MouseLeftButtonUp += (s, e) =>
                {
                    if (dragged || mergeRunning) return;
                    mergeOpen = open ? -1 : k;   // a click opens the clip's part, a second one closes it
                    BuildMerge();
                };
                piece.MouseMove += (s, e) =>
                {
                    if (e.LeftButton != MouseButtonState.Pressed || mergeRunning) return;
                    var at = e.GetPosition(sp);
                    if (Math.Abs(at.X - down.X) < 6 && Math.Abs(at.Y - down.Y) < 6) return;
                    dragged = true;
                    piece.Opacity = 0.5;
                    DragDrop.DoDragDrop(piece, k.ToString(), DragDropEffects.Move);
                    piece.Opacity = 1;
                };
                piece.DragOver += (s, e) => { piece.BorderBrush = Wpf.Res<Brush>("Text"); e.Effects = DragDropEffects.Move; e.Handled = true; };
                piece.DragLeave += (s, e) => piece.BorderBrush = rest;
                piece.Drop += (s, e) =>
                {
                    int from;
                    if (!int.TryParse(e.Data.GetData(DataFormats.StringFormat) as string, out from) || from == k) { piece.BorderBrush = rest; return; }
                    W.Dispatcher.BeginInvoke(new Action(() => MoveMergeItem(from, k)));   // after the drag is over
                };
                sp.Children.Add(piece);
            }
            return new ScrollViewer { Content = sp, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, Padding = new Thickness(0, 0, 0, 4) };
        }

        // the part of one clip: its frames along the whole length, two handles for the start and the end
        FrameworkElement MergeRange(int k)
        {
            var it = mergeItems[k];
            var box = new Border { Style = S("CardBorder"), Background = Wpf.Res<Brush>("Input"), Margin = new Thickness(0, 12, 0, 0), Padding = new Thickness(14) };
            var p = new StackPanel();
            box.Child = p;
            var head = new Grid { Margin = new Thickness(0, 0, 0, 10) };
            head.ColumnDefinitions.Add(new ColumnDefinition());
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            head.Children.Add(new TextBlock { Text = L.T("Part of clip ", "Кусок клипа ") + (k + 1) + " · " + it.Title, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
            var whole = new Button { Style = S("BtnLink"), Content = L.T("Whole clip", "Весь клип"), IsEnabled = it.Range != null };
            whole.Click += (s, e) => { it.Range = null; BuildMerge(); };
            Grid.SetColumn(whole, 1);
            head.Children.Add(whole);
            p.Children.Add(head);
            if (it.Info == null || it.Info.Duration <= 0) { p.Children.Add(new TextBlock { Text = L.T("Reading the clip…", "Читаю клип…"), Style = S("SubText") }); return box; }

            double len = it.Info.Duration, w = 700, h = 54;
            double a = it.Range != null ? it.Range[0] : 0, b = it.Range != null ? it.Range[1] : len;
            var track = new Canvas { Width = w, Height = h, Background = it.Strip != null ? (Brush)new ImageBrush(it.Strip) { Stretch = Stretch.Fill } : Wpf.Res<Brush>("Card2"), ClipToBounds = true };
            var dimL = new Border { Height = h, Background = Wpf.Br("#B30B0C0E") };
            var dimR = new Border { Height = h, Background = Wpf.Br("#B30B0C0E") };
            var frame = new Border { Height = h, BorderBrush = Wpf.Res<Brush>("Text"), BorderThickness = new Thickness(0, 2, 0, 2) };
            Func<Border> handle = () => new Border { Width = 10, Height = h, Background = Wpf.Res<Brush>("Text"), CornerRadius = new CornerRadius(3), Cursor = Cursors.SizeWE,
                                                      Child = new Border { Width = 2, Height = 18, Background = Wpf.Res<Brush>("Bg"), CornerRadius = new CornerRadius(1) } };
            var hA = handle();
            var hB = handle();
            foreach (var el in new UIElement[] { dimL, dimR, frame, hA, hB }) track.Children.Add(el);
            var times = new TextBlock { FontFamily = Wpf.Res<FontFamily>("MonoFont"), FontSize = 12, Foreground = Wpf.Res<Brush>("Sub"), Margin = new Thickness(0, 8, 0, 0) };
            Action place = () =>
            {
                double xa = a / len * w, xb = b / len * w;
                dimL.Width = Math.Max(0, xa);
                Canvas.SetLeft(dimR, xb);
                dimR.Width = Math.Max(0, w - xb);
                Canvas.SetLeft(frame, xa);
                frame.Width = Math.Max(0, xb - xa);
                Canvas.SetLeft(hA, Math.Max(0, xa - 5));
                Canvas.SetLeft(hB, Math.Min(w - 10, xb - 5));
                times.Text = L.T("from ", "с ") + Trimmer.Dur(a) + L.T(" to ", " до ") + Trimmer.Dur(b) + " · " + Trimmer.Dur(b - a) + L.T(" of ", " из ") + Trimmer.Dur(len);
            };
            place();
            // a handle is dragged; the part changes when it is let go (the estimate and the cards follow)
            Action<Border, bool> drag = (hd, start) =>
            {
                hd.MouseLeftButtonDown += (s, e) => { hd.CaptureMouse(); e.Handled = true; };
                hd.MouseMove += (s, e) =>
                {
                    if (!hd.IsMouseCaptured) return;
                    double t = Math.Max(0, Math.Min(len, e.GetPosition(track).X / w * len));
                    if (start) a = Math.Min(t, b - 1); else b = Math.Max(t, a + 1);
                    place();
                };
                hd.MouseLeftButtonUp += (s, e) =>
                {
                    if (!hd.IsMouseCaptured) return;
                    hd.ReleaseMouseCapture();
                    it.Range = a < 0.05 && b > len - 0.05 ? null : new[] { Math.Round(a, 2), Math.Round(b, 2) };
                    BuildMerge();
                };
            };
            drag(hA, true);
            drag(hB, false);
            p.Children.Add(track);
            p.Children.Add(times);
            if (it.Strip == null && !it.StripAsked)
            {
                it.StripAsked = true;
                string png = Path.Combine(Path.GetTempPath(), "dg_trim", "joinstrip_" + Guid.NewGuid().ToString("N") + ".png");
                Task.Factory.StartNew(() =>
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(png));
                    if (!Ffmpeg.Filmstrip(it.Path, len, 14, (int)h, png, CancellationToken.None) || !File.Exists(png)) return null;
                    var img = new BitmapImage();
                    img.BeginInit();
                    img.CacheOption = BitmapCacheOption.OnLoad;
                    img.UriSource = new Uri(png);
                    img.EndInit();
                    img.Freeze();
                    try { File.Delete(png); } catch { }
                    return (BitmapSource)img;
                }).ContinueWith(t => W.Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (t.IsFaulted || t.Result == null) return;
                    it.Strip = t.Result;
                    if (merge != null && !mergeRunning && mergeOpen >= 0 && mergeItems[mergeOpen] == it) BuildMerge();
                })));
            }
            return box;
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

        // for previews: the join card for these clips, read at once; open — the card whose part is shown, with a part chosen
        public void PreviewMerge(List<ClipVm> list, int open = -1)
        {
            if (list == null) { CloseMerge(); return; }
            ShowMerge(list);
            foreach (var i in mergeItems) try { i.Info = Ffmpeg.Info(i.Path); } catch { }
            if (open >= 0 && open < mergeItems.Count && mergeItems[open].Info != null)
            {
                var it = mergeItems[open];
                it.Range = new[] { it.Info.Duration * 0.35, it.Info.Duration * 0.7 };
                it.StripAsked = true;
                string png = Path.Combine(Path.GetTempPath(), "dg_trim", "joinstrip_preview.png");
                Directory.CreateDirectory(Path.GetDirectoryName(png));
                if (Ffmpeg.Filmstrip(it.Path, it.Info.Duration, 14, 54, png, CancellationToken.None))
                {
                    var img = new BitmapImage();
                    img.BeginInit(); img.CacheOption = BitmapCacheOption.OnLoad; img.UriSource = new Uri(png); img.EndInit(); img.Freeze();
                    it.Strip = img;
                    try { File.Delete(png); } catch { }
                }
                mergeOpen = open;
            }
            BuildMerge();
        }
    }
}
