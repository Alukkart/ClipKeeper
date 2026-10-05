using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;

namespace DeviceGuard
{
    // What is known about a just-saved clip (the clip check fills what it could read)
    class ClipInfo
    {
        public string Path, Game;               // Game: null — no game (the fallback folder or no sorting)
        public double Duration = -1;            // seconds
        public int Tracks = -1;                 // audio tracks
        public long Bytes = -1;
        public int Today;                       // clips saved today in the same folder, this one included
        public string Note;                     // a remark that is not a problem (a few dropped frames)
        public List<string> Issues = new List<string>();
        public bool Ok { get { return Issues.Count == 0; } }

        static readonly Regex Video = new Regex(@"\.(mp4|mkv|mov|flv)$", RegexOptions.IgnoreCase);

        public static ClipInfo Of(string path, string game)
        {
            var c = new ClipInfo { Path = path, Game = game };
            try
            {
                var fi = new FileInfo(path);
                c.Bytes = fi.Length;
                c.Today = fi.Directory.GetFiles().Count(f => Video.IsMatch(f.Name) && f.LastWriteTime.Date == DateTime.Today);
            }
            catch { }
            return c;
        }
    }

    // "Clip saved": a card in the top right corner — a frame of the clip, the game, length / tracks / size, where it went,
    // and buttons to open, trim, copy and show it. A clip with a problem gets the same card in orange with the problems listed.
    // Like the alarm it does not take focus from the game and is not recorded; hovering it keeps it open.
    class ClipCard : Window
    {
        const double CardWidth = 452, ThumbW = 160, ThumbH = 90;

        static ClipCard current;   // one card at a time: a new clip replaces the previous card

        readonly TrayApp app;
        ClipInfo clip;     // null — the waiting card ("Saving the clip…"); Become turns it into the clip card
        int seconds;
        Color accent;
        readonly DispatcherTimer timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        DateTime closeAt;
        Border card, thumb, bar;
        Button delButton;
        bool closing;

        // the save key was pressed, OBS is still writing the file: a small card until the real one replaces it
        public static void Saving(TrayApp app)
        {
            if (current != null) current.FadeClose();
            current = new ClipCard(app, null);
            current.Open();
        }

        // no clip came after the press: the waiting card (if it is still there) says so and goes away
        public static void NotSaved()
        {
            var c = current;
            if (c == null || c.clip != null || c.closing) return;
            c.notSaved = true;
            c.title.Text = L.T("OBS has not reported a clip", "OBS не сообщил о клипе");
            c.title.Foreground = Wpf.Br(Wpf.Warn, 255);
            c.sub.Text = L.T("Check that the replay buffer is recording — the Recording page shows it", "Проверь, что буфер повтора пишет, — это видно на странице «Запись»");
            c.bar.BeginAnimation(WidthProperty, null);
            c.bar.Background = new SolidColorBrush(Wpf.Warn);
            c.Countdown();
        }

        public static void Pop(TrayApp app, ClipInfo c)
        {
            var w = current;
            if (w != null && w.clip == null && !w.closing && w.IsVisible) w.Become(c);   // "Saving the clip…" turns into the clip
            else
            {
                if (w != null) w.FadeClose();
                current = new ClipCard(app, c);
                current.Open();
            }
            if (!c.Ok && app != null && app.Cfg.AlertSound) AlertWindow.PlayOnce(app.Cfg);
        }

        ClipCard(TrayApp app, ClipInfo clip)   // clip null — "Saving the clip…"
        {
            this.app = app;
            this.clip = clip;
            seconds = clip == null ? 30 : clip.Ok ? 8 : 20;
            accent = clip == null || clip.Ok ? Wpf.Ok : Wpf.Warn;

            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            Topmost = true;
            ShowInTaskbar = false;
            ShowActivated = false;
            ResizeMode = ResizeMode.NoResize;
            SizeToContent = SizeToContent.WidthAndHeight;
            FontFamily = Wpf.Res<FontFamily>("UiFont");
            Foreground = Wpf.Res<Brush>("Text");
            UseLayoutRounding = true;
            SourceInitialized += (s, e) => { Wpf.NoActivate(this); Wpf.ExcludeFromCapture(this); };
            Content = clip == null ? BuildSaving() : Build();
            timer.Tick += (s, e) => { if (DateTime.Now >= closeAt && !IsMouseOver) FadeClose(); };
            Closed += (s, e) => { timer.Stop(); if (current == this) current = null; };
        }

        // ── layout ──
        UIElement Build()
        {
            card = new Border
            {
                Width = CardWidth, CornerRadius = new CornerRadius(10), Background = Wpf.Br("#17181B"),
                BorderBrush = clip.Ok ? Wpf.Br("#2E3036") : new SolidColorBrush(accent), BorderThickness = new Thickness(1),
                Effect = new DropShadowEffect { Color = Colors.Black, BlurRadius = 22, ShadowDepth = 5, Opacity = 0.55 },
                RenderTransform = new TranslateTransform(28, 0),
            };
            var stack = new StackPanel();

            var top = new Grid { Margin = new Thickness(12, 12, 14, 0) };
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            top.ColumnDefinitions.Add(new ColumnDefinition());
            top.Children.Add(BuildThumb());
            var info = BuildInfo();
            Grid.SetColumn(info, 1);
            top.Children.Add(info);
            stack.Children.Add(top);

            if (!clip.Ok)
            {
                var issues = new StackPanel { Margin = new Thickness(14, 10, 14, 0) };
                foreach (var i in clip.Issues)
                    issues.Children.Add(new TextBlock { Text = "• " + i, Foreground = new SolidColorBrush(accent), FontSize = 12.5, TextWrapping = TextWrapping.Wrap, LineHeight = 18 });
                stack.Children.Add(issues);
            }

            stack.Children.Add(BuildButtons());

            // a thin bar: time left until the card goes away (stops while the mouse is over it)
            bar = new Border { Height = 2, Background = new SolidColorBrush(accent), Opacity = 0.7, HorizontalAlignment = HorizontalAlignment.Left, Width = CardWidth - 2 };
            stack.Children.Add(new Border { CornerRadius = new CornerRadius(0, 0, 10, 10), ClipToBounds = true, Child = bar });

            card.Child = stack;
            card.MouseEnter += (s, e) => { bar.BeginAnimation(WidthProperty, null); bar.Width = CardWidth - 2; };
            card.MouseLeave += (s, e) => Countdown();
            return new Grid { Margin = new Thickness(18), Children = { card } };
        }

        TextBlock title, sub;
        bool notSaved;   // the waiting card turned into "OBS has not reported a clip"

        // the waiting card: the same place and width, a record dot, the text and a bar running back and forth
        UIElement BuildSaving()
        {
            card = new Border
            {
                Width = CardWidth, CornerRadius = new CornerRadius(10), Background = Wpf.Br("#17181B"),
                BorderBrush = Wpf.Br("#2E3036"), BorderThickness = new Thickness(1),
                Effect = new DropShadowEffect { Color = Colors.Black, BlurRadius = 22, ShadowDepth = 5, Opacity = 0.55 },
                RenderTransform = new TranslateTransform(28, 0),
            };
            var row = new Grid { Margin = new Thickness(16, 14, 16, 14) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            var dot = new System.Windows.Shapes.Ellipse { Width = 10, Height = 10, Fill = new SolidColorBrush(Wpf.Bad), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 14, 0) };
            dot.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0.25, TimeSpan.FromSeconds(0.6)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
            row.Children.Add(dot);
            var text = new StackPanel();
            title = new TextBlock { Text = L.T("Saving the clip…", "Сохраняю клип…"), FontSize = 14, FontWeight = FontWeights.SemiBold };
            sub = new TextBlock { Text = L.T("OBS is writing the file — the card will show it in a moment", "OBS записывает файл — карточка покажет его через мгновение"),
                                  Foreground = Wpf.Res<Brush>("Sub"), FontSize = 12.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) };
            text.Children.Add(title);
            text.Children.Add(sub);
            Grid.SetColumn(text, 1);
            row.Children.Add(text);
            var stack = new StackPanel();
            stack.Children.Add(row);
            bar = new Border { Height = 2, Background = new SolidColorBrush(accent), Opacity = 0.7, HorizontalAlignment = HorizontalAlignment.Left, Width = CardWidth - 2 };
            stack.Children.Add(new Border { CornerRadius = new CornerRadius(0, 0, 10, 10), ClipToBounds = true, Child = bar });
            card.Child = stack;
            return new Grid { Margin = new Thickness(18), Children = { card } };
        }

        FrameworkElement BuildThumb()
        {
            thumb = new Border
            {
                Width = ThumbW, Height = ThumbH, CornerRadius = new CornerRadius(6), Background = Wpf.Br("#0E0F11"),
                Cursor = Cursors.Hand, ToolTip = L.T("Play", "Воспроизвести"), VerticalAlignment = VerticalAlignment.Top,
            };
            var g = new Grid();
            g.Children.Add(new TextBlock
            {
                Style = Wpf.Res<Style>("Icon"), Text = "\uE714", FontSize = 22, Foreground = Wpf.Res<Brush>("Muted"),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            });
            if (clip.Duration >= 0)
                g.Children.Add(new Border
                {
                    Background = Wpf.Br("#CC0B0C0E"), CornerRadius = new CornerRadius(4), Padding = new Thickness(6, 1, 6, 2),
                    HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 5, 5),
                    Child = new TextBlock { Text = Clock(clip.Duration), FontSize = 11.5, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White },
                });
            thumb.Child = g;
            thumb.MouseLeftButtonUp += (s, e) => { Shell.Open(clip.Path); FadeClose(); };
            if (app != null)
                Thumbs.Request(clip.Path, img =>
                {
                    if (img == null) return;
                    thumb.Background = new ImageBrush(img) { Stretch = Stretch.UniformToFill };
                    ((TextBlock)g.Children[0]).Visibility = Visibility.Collapsed;
                });
            return thumb;
        }

        FrameworkElement BuildInfo()
        {
            var sp = new StackPanel { Margin = new Thickness(14, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };

            var head = new StackPanel { Orientation = Orientation.Horizontal };
            head.Children.Add(new TextBlock
            {
                Style = Wpf.Res<Style>("Icon"), Text = clip.Ok ? "\uE73E" : "\uE7BA", FontSize = 11.5, Foreground = new SolidColorBrush(accent),
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0),
            });
            head.Children.Add(new TextBlock
            {
                Text = (clip.Ok ? L.T("Clip saved", "Клип сохранён") : L.T("Clip saved with a problem", "Клип сохранён с проблемой")).ToUpperInvariant(),
                FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(accent), VerticalAlignment = VerticalAlignment.Center,
            });
            sp.Children.Add(head);

            string name = clip.Game != null ? Covers.Title(clip.Game) : Path.GetFileNameWithoutExtension(clip.Path);
            sp.Children.Add(new TextBlock
            {
                Text = name, FontFamily = Wpf.Res<FontFamily>("DisplayFont"), FontSize = 17, FontWeight = FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 3, 0, 0), ToolTip = name,
            });

            var facts = new List<string>();
            if (clip.Duration >= 0) facts.Add(Fmt.Duration(clip.Duration));
            if (clip.Tracks >= 0) facts.Add(L.N(clip.Tracks, "track", "tracks", "дорожка", "дорожки", "дорожек"));
            if (clip.Bytes >= 0) facts.Add(Fmt.Size(clip.Bytes));
            if (facts.Count > 0)
                sp.Children.Add(new TextBlock { Text = string.Join(" · ", facts), Foreground = Wpf.Res<Brush>("Sub"), FontSize = 12.5, Margin = new Thickness(0, 3, 0, 0) });

            var extra = new List<string>();
            if (clip.Today > 1) extra.Add(L.T("today in this folder: ", "сегодня в этой папке: ") + clip.Today);
            if (clip.Note != null) extra.Add(clip.Note);
            if (extra.Count > 0)
                sp.Children.Add(new TextBlock { Text = string.Join(" · ", extra), Foreground = Wpf.Res<Brush>("Muted"), FontSize = 12, Margin = new Thickness(0, 2, 0, 0),
                                                TextTrimming = TextTrimming.CharacterEllipsis });

            string dir = Path.GetDirectoryName(clip.Path);
            var where = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0), ToolTip = clip.Path, Cursor = Cursors.Hand };
            where.Children.Add(new TextBlock { Style = Wpf.Res<Style>("Icon"), Text = "\uE838", FontSize = 11.5, Foreground = Wpf.Res<Brush>("Muted"),
                                               VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
            where.Children.Add(new TextBlock { Text = ShortPath(dir), FontFamily = Wpf.Res<FontFamily>("MonoFont"), FontSize = 11.5,
                                               Foreground = Wpf.Res<Brush>("Sub"), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 240 });
            where.MouseLeftButtonUp += (s, e) => { Shell.Select(clip.Path); FadeClose(); };
            sp.Children.Add(where);
            return sp;
        }

        FrameworkElement BuildButtons()
        {
            var row = new WrapPanel { Margin = new Thickness(12, 12, 12, 12) };
            Func<string, string, Action, bool, Button> add = (glyph, text, act, close) =>
            {
                var content = new StackPanel { Orientation = Orientation.Horizontal };
                content.Children.Add(new TextBlock { Style = Wpf.Res<Style>("Icon"), Text = glyph, FontSize = 12, Margin = new Thickness(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center });
                var label = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center };
                content.Children.Add(label);
                var b = new Button { Style = Wpf.Res<Style>("BtnGhost"), Content = content, FontSize = 12.5, Padding = new Thickness(11, 5, 11, 5), Margin = new Thickness(0, 0, 6, 0), Tag = label };
                b.Click += (s, e) => { act(); if (close) FadeClose(); };
                row.Children.Add(b);
                return b;
            };
            add("\uE768", L.T("Open", "Открыть"), () => Shell.Open(clip.Path), true);
            if (app == null || (app.Cfg.LibraryEnabled && Ffmpeg.Available))
                add("\uE8C6", L.T("Trim", "Обрезать"), () => { if (app != null) app.OpenTrim(clip.Path); }, true);
            Button copy = null;
            copy = add("\uE8C8", L.T("Copy", "Копировать"), () =>
            {
                if (!Shell.CopyFile(clip.Path)) return;
                ((TextBlock)copy.Tag).Text = L.T("Copied — Ctrl+V", "Скопирован — Ctrl+V");
                closeAt = DateTime.Now.AddSeconds(4);
            }, false);
            copy.ToolTip = L.T("Then Ctrl+V into Discord or Telegram", "Потом Ctrl+V в Discord или Telegram");
            add("\uE838", L.T("Folder", "Папка"), () => Shell.Select(clip.Path), true);

            // delete, like in the library: the first click asks, the second within 3 s moves the clip to the Recycle Bin.
            // While it asks, the other buttons give way to the question (same height — the card does not jump)
            var delLabel = new TextBlock { Text = L.T("Delete", "Удалить"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(7, 0, 0, 0),
                                           Visibility = Visibility.Collapsed };
            var ask = new TextBlock { Text = L.T("The clip goes to the Recycle Bin — click again", "Клип уйдёт в корзину — нажми ещё раз"),
                                      Foreground = Wpf.Res<Brush>("Bad"), FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center,
                                      TextTrimming = TextTrimming.CharacterEllipsis, Visibility = Visibility.Collapsed };
            var delContent = new StackPanel { Orientation = Orientation.Horizontal };
            delContent.Children.Add(new TextBlock { Style = Wpf.Res<Style>("Icon"), Text = "\uE74D", FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
            delContent.Children.Add(delLabel);
            var del = new Button { Style = Wpf.Res<Style>("BtnGhost"), Content = delContent, FontSize = 12.5, Padding = new Thickness(9, 5, 9, 5),
                                   VerticalAlignment = VerticalAlignment.Top, ToolTip = L.T("Delete the clip (to the Recycle Bin)", "Удалить клип (в корзину)") };
            var disarm = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            Action<bool> armed = on =>
            {
                delLabel.Visibility = ask.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
                row.Visibility = on ? Visibility.Hidden : Visibility.Visible;
                if (on) del.Foreground = Wpf.Res<Brush>("Bad"); else del.ClearValue(ForegroundProperty);
            };
            disarm.Tick += (s, e) => { disarm.Stop(); armed(false); };
            del.Click += (s, e) =>
            {
                if (delLabel.Visibility != Visibility.Visible)
                {
                    armed(true);
                    if (closeAt < DateTime.Now.AddSeconds(4)) closeAt = DateTime.Now.AddSeconds(4);   // the card waits for the second click
                    disarm.Start();
                    return;
                }
                disarm.Stop();
                if (Shell.Recycle(clip.Path))
                {
                    Log.Write("clip card: " + clip.Path + " moved to the Recycle Bin");
                    if (app != null)
                    {
                        app.ShowToast(L.T("✓ Clip moved to the Recycle Bin", "✓ Клип перемещён в корзину"));
                        app.ClipsChanged();
                    }
                    FadeClose();
                }
                else if (app != null) app.ShowToast(L.T("Could not delete the clip (details in the log)", "Не удалось удалить клип (подробности в журнале)"));
            };

            // the four actions on the left, delete apart on the right
            row.Margin = new Thickness(0);
            var g = new Grid { Margin = new Thickness(12, 12, 12, 12) };
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumnSpan(row, 2);   // its width does not depend on the delete button growing while it asks
            g.Children.Add(row);
            g.Children.Add(ask);
            Grid.SetColumn(del, 1);
            g.Children.Add(del);
            delButton = del;
            return g;
        }

        // ── behaviour ──
        // the waiting card becomes the clip card in the same window and place (it grows down from the same top),
        // instead of a second card appearing over it
        void Become(ClipInfo c)
        {
            clip = c;
            seconds = c.Ok ? 8 : 20;
            accent = c.Ok ? Wpf.Ok : Wpf.Warn;
            notSaved = false;
            bar.BeginAnimation(MarginProperty, null);
            bar.BeginAnimation(WidthProperty, null);
            Content = Build();
            ((TranslateTransform)card.RenderTransform).X = 0;
            card.BeginAnimation(OpacityProperty, new DoubleAnimation(0.3, 1, TimeSpan.FromMilliseconds(200)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            Countdown();
        }

        void Open()
        {
            Opacity = 0;
            Show();
            UpdateLayout();
            var wa = SystemParameters.WorkArea;
            Left = wa.Right - ActualWidth - 4;
            Top = wa.Top + 4;
            BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)));
            ((TranslateTransform)card.RenderTransform).BeginAnimation(TranslateTransform.XProperty,
                new DoubleAnimation(28, 0, TimeSpan.FromMilliseconds(240)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            Countdown();
            timer.Start();
        }

        void Countdown()
        {
            if (clip == null && !notSaved)
            {
                // waiting: no countdown, a bar running across instead (the 30 s timeout is in TrayApp)
                closeAt = DateTime.MaxValue;
                bar.Width = 90;
                var run = new ThicknessAnimation(new Thickness(-90, 0, 0, 0), new Thickness(CardWidth, 0, 0, 0), TimeSpan.FromSeconds(1.1)) { RepeatBehavior = RepeatBehavior.Forever };
                bar.BeginAnimation(MarginProperty, run);
                return;
            }
            int secs = seconds;
            if (clip == null) { bar.BeginAnimation(MarginProperty, null); bar.Margin = new Thickness(0); secs = 10; }
            closeAt = DateTime.Now.AddSeconds(secs);
            bar.BeginAnimation(WidthProperty, new DoubleAnimation(CardWidth - 2, 0, TimeSpan.FromSeconds(secs)));
        }

        public void FadeClose()
        {
            if (closing) return;
            closing = true;
            timer.Stop();
            var a = new DoubleAnimation(Opacity, 0, TimeSpan.FromMilliseconds(140));
            a.Completed += (s, e) => Close();
            BeginAnimation(OpacityProperty, a);
        }

        // ── text helpers ──
        // 140 → "2:20", 3725 → "1:02:05"
        public static string Clock(double sec)
        {
            var t = TimeSpan.FromSeconds(Math.Round(sec));
            return t.TotalHours >= 1 ? string.Format("{0}:{1:00}:{2:00}", (int)t.TotalHours, t.Minutes, t.Seconds) : string.Format("{0}:{1:00}", (int)t.TotalMinutes, t.Seconds);
        }

        // the end of a long path matters most: "D:\…\Sources\Hunt Showdown\2026-10"
        public static string ShortPath(string dir)
        {
            if (string.IsNullOrEmpty(dir) || dir.Length <= 40) return dir;
            string root = Path.GetPathRoot(dir) ?? "";
            var parts = dir.Substring(root.Length).Split('\\');
            string tail = parts[parts.Length - 1];
            for (int i = parts.Length - 2; i >= 0 && tail.Length + parts[i].Length + 1 <= 34; i--) tail = parts[i] + "\\" + tail;
            return tail.Length + 1 >= dir.Length - root.Length ? dir : root + "…\\" + tail;
        }

        // for previews: the waiting card without showing the window
        public static FrameworkElement PreviewSaving()
        {
            var w = new ClipCard(null, null);
            ((TranslateTransform)w.card.RenderTransform).X = 0;
            var content = (FrameworkElement)w.Content;
            w.Content = null;
            return content;
        }

        // for the self-test: the waiting card turned into the clip card, without showing the window
        public static FrameworkElement PreviewBecome(ClipInfo c)
        {
            var w = new ClipCard(null, null);
            w.Become(c);
            var content = (FrameworkElement)w.Content;
            w.Content = null;
            return content;
        }

        // for previews: the card without showing the window; asking — after the first click on "delete"
        public static FrameworkElement PreviewContent(ClipInfo c, bool asking = false)
        {
            var w = new ClipCard(null, c);
            if (asking) w.delButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            ((TranslateTransform)w.card.RenderTransform).X = 0;
            var img = File.Exists(c.Path) ? Thumbs.Load(c.Path, 320, 180) : null;
            if (img != null)
            {
                w.thumb.Background = new ImageBrush(img) { Stretch = Stretch.UniformToFill };
                ((Grid)w.thumb.Child).Children[0].Visibility = Visibility.Collapsed;
            }
            var content = (FrameworkElement)w.Content;
            w.Content = null;
            // out of the window the text no longer inherits its color and font
            content.SetValue(System.Windows.Documents.TextElement.ForegroundProperty, w.Foreground);
            content.SetValue(System.Windows.Documents.TextElement.FontFamilyProperty, w.FontFamily);
            return content;
        }
    }
}
