using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;

namespace DeviceGuard
{
    // "Share": the GIF target (format, width, frame rate, a warning when it comes out big) and even loudness —
    // when it is on, the window measures how loud the shared file would be and shows where it lands.
    partial class TrimWindow
    {
        ToggleButton tgtGif;
        CheckBox loudToggle;
        DispatcherTimer loudTimer;
        CancellationTokenSource loudCts;
        string loudKey;   // what the shown loudness was measured for

        void InitShare()
        {
            // format, width and frame rate: three rows of chips, each "one of"
            var g = F<Grid>("GifRow");
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(92) });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            Action<int, string, string[], string[], Func<string>, Action<string>> row = (r, label, values, texts, get, set) =>
            {
                g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var l = new TextBlock { Text = label, Foreground = Wpf.Res<System.Windows.Media.Brush>("Sub"), FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center };
                Grid.SetRow(l, r);
                g.Children.Add(l);
                var p = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
                var chips = values.Select((v, i) => new ToggleButton { Style = (Style)W.FindResource("Chip"), Content = texts[i], Padding = new Thickness(10, 3, 10, 3), IsChecked = get() == v, Tag = v }).ToArray();
                foreach (var c in chips) p.Children.Add(c);
                Group(chips, () => { set((string)chips.First(c => c.IsChecked == true).Tag); SaveCfg(); UpdateEstimate(); });
                Grid.SetRow(p, r);
                Grid.SetColumn(p, 1);
                g.Children.Add(p);
            };
            row(0, L.T("Format", "Формат"), new[] { "gif", "webp" }, new[] { "GIF", "WebP" }, () => cfg.GifFormat, v => cfg.GifFormat = v);
            row(1, L.T("Width", "Ширина"), new[] { "360", "480", "720" }, new[] { "360", "480", "720" }, () => cfg.GifWidth.ToString(), v => cfg.GifWidth = int.Parse(v));
            row(2, L.T("Frames/s", "Кадров/с"), new[] { "10", "15", "24" }, new[] { "10", "15", "24" }, () => cfg.GifFps.ToString(), v => cfg.GifFps = int.Parse(v));

            loudToggle = F<CheckBox>("LoudToggle");
            loudToggle.IsChecked = cfg.ShareLoudness;
            RoutedEventHandler loud = (s, e) => { cfg.ShareLoudness = loudToggle.IsChecked == true; SaveCfg(); loudKey = null; UpdateEstimate(); };
            loudToggle.Checked += loud;
            loudToggle.Unchecked += loud;
            loudTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
            loudTimer.Tick += (s, e) => { loudTimer.Stop(); MeasureLoudness(); };
            W.Closing += (s, e) => { loudTimer.Stop(); if (loudCts != null) loudCts.Cancel(); };
        }

        // the share panel follows the target: GIF settings or loudness, the warning, the hint
        void UpdateShare(TrimJob j)
        {
            bool share = Mode == TrimMode.Share, gif = share && Target == ShareTarget.Gif;
            F<Grid>("GifRow").Visibility = gif ? Visibility.Visible : Visibility.Collapsed;
            F<Grid>("LoudRow").Visibility = share && !gif ? Visibility.Visible : Visibility.Collapsed;
            var warn = F<TextBlock>("GifWarn");
            double mb = gif ? Trimmer.GifMb(j) : 0;
            bool big = gif && (j.Length > 10.5 || mb > 25);
            warn.Visibility = big ? Visibility.Visible : Visibility.Collapsed;
            if (big)
            {
                var other = BuildJob();
                other.GifFormat = cfg.GifFormat == "webp" ? "gif" : "webp";
                warn.Text = L.T("The range is " + Math.Round(j.Length) + " s — long for an animation: about " + Trimmer.Size((long)(mb * 1048576)) + ".",
                                "Отрезок " + Math.Round(j.Length) + " с — для анимации длинно: около " + Trimmer.Size((long)(mb * 1048576)) + ".") +
                            (cfg.GifFormat == "webp" ? L.T(" Better up to 10 s, or a lower width and frame rate.", " Лучше до 10 с или меньше ширина и кадров/с.")
                                                     : L.T(" Better up to 10 s, or WebP: about ", " Лучше до 10 с или WebP: около ") + Trimmer.Size((long)(Trimmer.GifMb(other) * 1048576)) + ".");
            }
            var lt = F<TextBlock>("LoudText");
            bool loud = share && !gif && cfg.ShareLoudness;
            lt.Visibility = loud ? Visibility.Visible : Visibility.Collapsed;
            if (!loud) { loudTimer.Stop(); return; }
            string key = j.In.ToString("0.00") + "|" + j.Out.ToString("0.00") + "|" + j.MixIndex + "|" + j.RebuildMix + "|" +
                         string.Join(",", j.Tracks.Select(t => t.Source + ":" + t.On + ":" + t.Gain));
            if (key == loudKey) return;
            loudKey = key;
            lt.Text = L.T("measuring…", "измеряю…");
            loudTimer.Stop();
            loudTimer.Start();   // after the range stops moving
        }

        // for previews: the GIF target, or Discord with even loudness and a measured line
        public void PreviewShareTarget(bool gif)
        {
            modeShare.IsChecked = true; modeLossless.IsChecked = false; modePrecise.IsChecked = false;
            foreach (var t in new[] { tgtDiscord, tgtNitro, tgtTelegram, tgtCustom, tgtGif }) t.IsChecked = gif ? t == tgtGif : t == tgtDiscord;
            loudToggle.IsChecked = !gif;
            UpdateMode();
            loudTimer.Stop();
            if (!gif) F<TextBlock>("LoudText").Text = "-27.2 → -14 LUFS · +13.2 " + L.T("dB", "дБ");
        }

        void MeasureLoudness()
        {
            if (info == null) return;
            if (loudCts != null) loudCts.Cancel();
            loudCts = new CancellationTokenSource();
            var token = loudCts.Token;
            var j = BuildJob();
            string key = loudKey;
            Task.Factory.StartNew(() => Trimmer.MeasureLoudness(j, token), token).ContinueWith(t => W.Dispatcher.BeginInvoke(new Action(() =>
            {
                if (t.IsCanceled || key != loudKey) return;
                double now = t.IsFaulted ? double.NaN : t.Result;
                var lt = F<TextBlock>("LoudText");
                if (double.IsNaN(now) || double.IsInfinity(now)) { lt.Text = L.T("silence — nothing to even out", "тишина — выравнивать нечего"); return; }
                double gain = Trimmer.LoudTarget - now;
                lt.Text = now.ToString("0.0") + " → " + Trimmer.LoudTarget.ToString("0") + " LUFS · " + (gain >= 0 ? "+" : "−") + Math.Abs(gain).ToString("0.0") + L.T(" dB", " дБ");
            })));
        }
    }
}
