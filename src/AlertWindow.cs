using System;
using System.Media;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;

namespace DeviceGuard
{
    enum AlertKind { Alarm, Notice, Ok, Toast }

    // A card on top of all windows: does not steal focus from the game and is not recorded
    class AlertWindow : Window
    {
        readonly TrayApp app;
        readonly AlertKind kind;
        readonly int autoCloseSec;
        readonly Color accent;
        readonly DispatcherTimer timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        readonly DateTime opened = DateTime.Now;
        readonly TextBlock title = new TextBlock(), body = new TextBlock(), hint = new TextBlock();
        readonly Button btnRestart;
        Border card, progress;
        SolidColorBrush border;
        SoundPlayer player;
        DateTime lastSound;
        bool closing;

        public double TopY = -1;
        public Action Click;   // toast click

        public AlertWindow(TrayApp app, AlertKind kind, int autoCloseSec)
        {
            this.app = app;
            this.kind = kind;
            this.autoCloseSec = autoCloseSec;
            accent = kind == AlertKind.Alarm ? Wpf.Bad : kind == AlertKind.Notice ? Wpf.Warn : Wpf.Ok;

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

            btnRestart = new Button { Style = Wpf.Res<Style>("BtnGhost"), Content = L.T("Restart OBS", "Перезапустить OBS"), Margin = new Thickness(0, 0, 8, 0) };
            btnRestart.Click += (s, e) => { if (app != null) app.RequestRestartObs(); FadeClose(); };
            Content = kind == AlertKind.Toast ? BuildToast() : BuildCard();

            if ((kind == AlertKind.Alarm || kind == AlertKind.Notice) && app != null && app.Cfg.AlertSound)
                try { player = Alarm.Create(app.Cfg.SoundFile, app.Cfg.SoundVolume); } catch { player = null; }

            timer.Tick += OnTick;
            Closed += (s, e) => { timer.Stop(); if (player != null) player.Stop(); };
        }

        static Color Mix(Color a, Color b, double t)
        {
            return Color.FromRgb((byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));
        }

        UIElement BuildCard()
        {
            // a flat dark card lightly tinted with the status color
            var bg = new SolidColorBrush(Mix(Wpf.C("#131417"), accent, kind == AlertKind.Alarm ? 0.16 : 0.08));
            bg.Freeze();
            border = new SolidColorBrush(accent);
            card = new Border
            {
                Width = 540, CornerRadius = new CornerRadius(10), Background = bg, BorderThickness = new Thickness(kind == AlertKind.Alarm ? 2 : 1),
                BorderBrush = border, Padding = new Thickness(20, 18, 20, 16),
                Effect = new DropShadowEffect { Color = Colors.Black, BlurRadius = 24, ShadowDepth = 6, Opacity = 0.55 },
            };

            var g = new Grid();
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var head = new StackPanel { Orientation = Orientation.Horizontal };
            head.Children.Add(new TextBlock
            {
                Style = Wpf.Res<Style>("Icon"), FontSize = 16, Foreground = new SolidColorBrush(accent),
                Text = kind == AlertKind.Ok ? "" : "", Margin = new Thickness(0, 0, 12, 0),
            });
            title.Foreground = Wpf.Res<Brush>("Text");
            title.FontSize = 15.5;
            title.FontWeight = FontWeights.SemiBold;
            title.TextWrapping = TextWrapping.Wrap;
            title.MaxWidth = 470;
            title.VerticalAlignment = VerticalAlignment.Center;
            head.Children.Add(title);
            g.Children.Add(head);

            body.TextWrapping = TextWrapping.Wrap;
            body.FontSize = 13.5;
            body.LineHeight = 20;
            body.Foreground = Wpf.Br("#D4D4D8");
            body.Margin = new Thickness(28, 8, 0, 0);
            Grid.SetRow(body, 1);
            g.Children.Add(body);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
            buttons.Children.Add(btnRestart);
            var okBg = new SolidColorBrush(accent);
            okBg.Freeze();
            var ok = new Button
            {
                Style = Wpf.Res<Style>("BtnPrimary"), Content = kind == AlertKind.Alarm ? L.T("Got it", "Понял") : "OK", MinWidth = 96,
                Background = okBg, Foreground = kind == AlertKind.Alarm ? Brushes.White : Wpf.Br("#0B0C0E"),
            };
            ok.Click += (s, e) => FadeClose();
            buttons.Children.Add(ok);
            Grid.SetRow(buttons, 2);
            g.Children.Add(buttons);

            var outer = new Grid();
            outer.Children.Add(g);
            if (autoCloseSec > 0)
            {
                // a thin bar: time left until auto-close
                progress = new Border
                {
                    Height = 2, Background = new SolidColorBrush(accent), Opacity = 0.6,
                    VerticalAlignment = VerticalAlignment.Bottom, HorizontalAlignment = HorizontalAlignment.Left,
                    Margin = new Thickness(0, 0, 0, -9), Width = 498,
                };
                outer.Children.Add(progress);
            }
            card.Child = outer;
            return new Grid { Margin = new Thickness(24), Children = { card } };
        }

        UIElement BuildToast()
        {
            var pill = new Border
            {
                CornerRadius = new CornerRadius(9), Background = Wpf.Br("#17181B"), BorderBrush = Wpf.Br("#2E3036"),
                BorderThickness = new Thickness(1), Padding = new Thickness(14, 10, 16, 10), Cursor = System.Windows.Input.Cursors.Hand,
                Effect = new DropShadowEffect { Color = Colors.Black, BlurRadius = 20, ShadowDepth = 4, Opacity = 0.5 },
            };
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            sp.Children.Add(new TextBlock
            {
                Style = Wpf.Res<Style>("Icon"), Text = "", FontSize = 13, Foreground = new SolidColorBrush(Wpf.Ok),
            });
            title.Foreground = Wpf.Res<Brush>("Text");
            title.FontSize = 13.5;
            title.VerticalAlignment = VerticalAlignment.Center;
            title.Margin = new Thickness(10, 0, 0, 0);
            sp.Children.Add(title);
            hint.Foreground = Wpf.Res<Brush>("Muted");
            hint.FontSize = 12.5;
            hint.VerticalAlignment = VerticalAlignment.Center;
            hint.Margin = new Thickness(12, 0, 0, 0);
            sp.Children.Add(hint);
            pill.Child = sp;
            pill.MouseLeftButtonUp += (s, e) => { if (Click != null) Click(); FadeClose(); };
            pill.RenderTransform = new TranslateTransform(24, 0);
            return new Grid { Margin = new Thickness(18), Children = { pill } };
        }

        public void SetContent(string titleText, string bodyText, bool restartBtn, bool newProblem)
        {
            Fill(titleText, bodyText, restartBtn);
            if (!IsVisible)
            {
                Opacity = 0;
                Show();
                UpdateLayout();
                Place();
                Animate();
                timer.Start();
            }
            else
            {
                UpdateLayout();
                Place();
            }
            if (newProblem) Play();
        }

        void Fill(string titleText, string bodyText, bool restartBtn)
        {
            title.Text = titleText.TrimStart('✓', '⚠', ' ');   // these characters are drawn as an icon
            if (kind == AlertKind.Toast)
            {
                hint.Text = bodyText ?? "";
                hint.Visibility = string.IsNullOrEmpty(bodyText) ? Visibility.Collapsed : Visibility.Visible;
                return;
            }
            body.Text = bodyText ?? "";
            body.Visibility = string.IsNullOrEmpty(bodyText) ? Visibility.Collapsed : Visibility.Visible;
            btnRestart.Visibility = restartBtn ? Visibility.Visible : Visibility.Collapsed;
        }

        void Place()
        {
            var wa = SystemParameters.WorkArea;
            if (kind == AlertKind.Toast)
            {
                Left = wa.Right - ActualWidth - 4;
                Top = wa.Top + 4;
            }
            else
            {
                Left = wa.Left + (wa.Width - ActualWidth) / 2;
                Top = TopY >= 0 ? TopY : wa.Top + 12;
            }
        }

        void Animate()
        {
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)));
            if (kind == AlertKind.Toast)
            {
                var pill = (Border)((Grid)Content).Children[0];
                ((TranslateTransform)pill.RenderTransform).BeginAnimation(TranslateTransform.XProperty,
                    new DoubleAnimation(24, 0, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease });
            }
            if (progress != null)
                progress.BeginAnimation(WidthProperty, new DoubleAnimation(498, 0, TimeSpan.FromSeconds(autoCloseSec)));
            if (kind == AlertKind.Alarm && border != null)
                // the alarm blinks its border: noticeable, but without a "neon" glow
                border.BeginAnimation(SolidColorBrush.ColorProperty,
                    new ColorAnimation(accent, Mix(Wpf.C("#131417"), accent, 0.25), TimeSpan.FromMilliseconds(450))
                    { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
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

        void Play()
        {
            if (app == null || !app.Cfg.AlertSound || player == null) return;   // player null — volume 0
            lastSound = DateTime.Now;
            Alarm.AlarmAt = DateTime.Now;
            try { player.Play(); } catch (Exception ex) { Log.Write("alarm sound: " + ex.Message); }
        }

        public static void PlayOnce(Settings cfg)
        {
            Alarm.PlayOnce(cfg.SoundFile, cfg.SoundVolume, false);
        }

        void OnTick(object s, EventArgs e)
        {
            if (autoCloseSec > 0 && (DateTime.Now - opened).TotalSeconds >= autoCloseSec) { FadeClose(); return; }
            if (kind == AlertKind.Alarm && app != null && app.Cfg.SoundRepeatSec > 0 &&
                (DateTime.Now - lastSound).TotalSeconds >= app.Cfg.SoundRepeatSec)
                Play();
        }

        // for previews: the content without showing the window
        public FrameworkElement PreviewContent(string titleText, string bodyText, bool restartBtn)
        {
            Fill(titleText, bodyText, restartBtn);
            if (kind == AlertKind.Toast) ((TranslateTransform)((Border)((Grid)Content).Children[0]).RenderTransform).X = 0;
            var c = (FrameworkElement)Content;
            Content = null;
            return c;
        }
    }
}
