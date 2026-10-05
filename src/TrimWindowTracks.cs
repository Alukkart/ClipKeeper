using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
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
using System.Windows.Threading;
using Rectangle = System.Windows.Shapes.Rectangle;

namespace DeviceGuard
{
    // The trim window: separate per-track lanes and the track list (on/off, "solo", volume).
    partial class TrimWindow
    {
        // ── separate tracks: each has its own lane with a waveform ──
        void BuildLanes()
        {
            foreach (var x in laneImgs) canvas.Children.Remove(x);
            foreach (var x in laneTags) canvas.Children.Remove(x);
            foreach (var x in laneSeps) canvas.Children.Remove(x);
            laneImgs.Clear(); laneTags.Clear(); laneSeps.Clear();
            lanesLoaded = false;
            lanesBtn.Visibility = info.Audio.Count > 1 ? Visibility.Visible : Visibility.Collapsed;   // one track — nothing to show
            int at = canvas.Children.IndexOf(wave) + 1;
            for (int i = 0; i < info.Audio.Count; i++)
            {
                var color = Wpf.C(LaneColors[i % LaneColors.Length]);
                var sep = new Rectangle { Height = 1, Fill = Wpf.Br("#26282E"), IsHitTestVisible = false, Visibility = Visibility.Collapsed };
                var img = new Image { Stretch = Stretch.Fill, IsHitTestVisible = false, Visibility = Visibility.Collapsed };
                var tag = new StackPanel { Orientation = Orientation.Horizontal };
                tag.Children.Add(new Border { Width = 8, Height = 8, CornerRadius = new CornerRadius(2), Background = new SolidColorBrush(color),
                                              Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center });
                string title = info.Audio[i].Title;
                tag.Children.Add(new TextBlock { Text = (i + 1) + (string.IsNullOrEmpty(title) ? "" : " · " + title), FontSize = 10.5,
                                                 Foreground = Wpf.Res<Brush>("Sub") });
                var label = new Border { Background = Wpf.Br("#D9141518"), CornerRadius = new CornerRadius(3), Padding = new Thickness(5, 1, 6, 1),
                                         Child = tag, IsHitTestVisible = false, Visibility = Visibility.Collapsed };
                canvas.Children.Insert(at++, sep);
                canvas.Children.Insert(at++, img);
                canvas.Children.Insert(at++, label);
                laneSeps.Add(sep); laneImgs.Add(img); laneTags.Add(label);
            }
            UpdateLaneStates();
        }

        void SetLanes(bool on)
        {
            if (info == null) return;
            on = on && info.Audio.Count > 1;
            lanesOn = on;
            lanesBtn.IsChecked = on;
            if (cfg.TrimLanes != on) { cfg.TrimLanes = on; SaveCfg(); }
            // lanes take the place of the combined waveform: it is the sum of the same tracks, together they duplicate each other
            timeline.Height = on ? WaveTop + info.Audio.Count * LaneH : BaseH;
            wave.Visibility = waveCaption.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
            foreach (var x in laneImgs) x.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            foreach (var x in laneTags) x.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            foreach (var x in laneSeps) x.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            if (on && !lanesLoaded) LoadLanes(false);
            rulerSig = null;
            Layout();
        }

        // a waveform for every source track in its own color; built once, on first use
        void LoadLanes(bool sync)
        {
            lanesLoaded = true;
            var token = cts.Token;
            string src = source;
            int gen = clipGen;
            int n = info.Audio.Count;
            Func<BitmapSource[]> make = () =>
            {
                var res = new BitmapSource[n];
                for (int i = 0; i < n; i++)
                {
                    string png = Path.Combine(tempDir, "lane" + i + ".png");
                    if (Ffmpeg.Waveform(src, i, 2400, 60, "0x" + LaneColors[i % LaneColors.Length].Substring(1), png, token)) res[i] = LoadPng(png);
                    TryDelete(png);
                }
                return res;
            };
            if (sync) { var r = make(); for (int i = 0; i < n; i++) laneImgs[i].Source = r[i]; return; }
            Task.Factory.StartNew(make).ContinueWith(t => W.Dispatcher.BeginInvoke(new Action(() =>
            {
                if (closed || gen != clipGen || t.IsFaulted || t.IsCanceled || t.Result.Length != laneImgs.Count) return;
                for (int i = 0; i < t.Result.Length; i++) laneImgs[i].Source = t.Result[i];
            })));
        }

        // a disabled track is pale: it won't be in the file
        void UpdateLaneStates()
        {
            for (int i = 0; i < laneImgs.Count; i++)
            {
                bool on = i >= rows.Count || rows[i].On.IsChecked == true;
                laneImgs[i].Opacity = on ? 0.95 : 0.22;
                laneTags[i].Opacity = on ? 1 : 0.5;
            }
        }

        void LoadPictures()
        {
            var token = cts.Token;
            string src = source;
            double len = duration;
            int gen = clipGen;
            // the same strip the library shows on hover: made once, kept in "previews"
            Task.Factory.StartNew(() => Filmstrips.Get(src, len, token)).ContinueWith(t => W.Dispatcher.BeginInvoke(new Action(() =>
            {
                if (!closed && gen == clipGen && !t.IsFaulted && !t.IsCanceled) strip.Source = t.Result;
            })));
        }

        // The waveform comes from the same audio you hear in the preview: all enabled tracks with their volume (or "solo").
        // So old clips without a mix track have one too, and it shows right away what goes into the file.
        void LoadWave(string wav, int gen, string caption)
        {
            string wp = Path.Combine(tempDir, "wave" + gen + ".png");
            var token = cts.Token;
            Task.Factory.StartNew(() => Ffmpeg.Waveform(wav, 0, 2400, 120, "0xC4C8D0", wp, token) ? LoadPng(wp) : null)
                .ContinueWith(t => W.Dispatcher.BeginInvoke(new Action(() =>
                {
                    TryDelete(wp);
                    if (closed || gen != audioGen || t.IsFaulted || t.IsCanceled || t.Result == null) return;
                    wave.Source = t.Result;
                    waveCaption.Text = caption;
                    Layout();
                })));
        }

        public static BitmapSource LoadPng(string path)
        {
            if (!File.Exists(path)) return null;
            var b = new BitmapImage();
            b.BeginInit();
            b.CacheOption = BitmapCacheOption.OnLoad;
            b.UriSource = new Uri(path);
            b.EndInit();
            b.Freeze();
            return b;
        }

        // ── tracks: on/off, "solo", volume ──
        static string DbText(double db) { return (db > 0 ? "+" : db < 0 ? "−" : "") + Math.Abs(db).ToString("0.#", Inv) + L.T(" dB", " дБ"); }

        bool IsMix(TrackRow r) { return r.Source == mixIndex; }

        void BuildTracks()
        {
            var panel = F<StackPanel>("TracksPanel");
            panel.Children.Clear();
            rows.Clear();
            var a = info.Audio;
            for (int i = 0; i < a.Count; i++)
            {
                var r = new TrackRow { Source = i, Title = a[i].Title };
                var g = new Grid { Margin = new Thickness(0, 0, 0, 6) };
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                g.ColumnDefinitions.Add(new ColumnDefinition());
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(92) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(50) });

                r.On = new ToggleButton { Style = Wpf.Res<Style>("IconToggle"), Content = "", IsChecked = true, ToolTip = L.T("Track is in the file — click to remove", "Дорожка в файле — нажми, чтобы убрать") };
                r.Solo = new ToggleButton { Style = Wpf.Res<Style>("IconToggle"), Content = "", Margin = new Thickness(6, 0, 0, 0), ToolTip = L.T("Listen to this track only (does not affect the file)", "Слушать только эту дорожку (на файл не влияет)") };
                Grid.SetColumn(r.Solo, 1);
                bool mixInTitle = r.Title != null && (r.Title.IndexOf("mix", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                                     r.Title.IndexOf("микс", StringComparison.OrdinalIgnoreCase) >= 0);
                r.Name = new TextBlock
                {
                    Text = (i + 1) + (string.IsNullOrEmpty(r.Title) ? "" : " · " + r.Title) + (IsMix(r) && !mixInTitle ? L.T(" · mix", " · микс") : ""),
                    Margin = new Thickness(10, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
                    ToolTip = IsMix(r) ? L.T("Common mix: OBS mixes all the other tracks into this one", "Общий микс: на эту дорожку OBS сводит все остальные") : r.Title,
                };
                Grid.SetColumn(r.Name, 2);
                r.Gain = new Slider
                {
                    Style = Wpf.Res<Style>("Slide"), Minimum = -20, Maximum = 20, Value = 0, IsSnapToTickEnabled = true, Focusable = false,
                    TickFrequency = 0.5, SmallChange = 0.5, LargeChange = 1, ToolTip = L.T("Volume · double click — 0 dB", "Громкость · двойной клик — 0 дБ"),
                };
                Grid.SetColumn(r.Gain, 3);
                r.Value = new TextBlock { Text = L.T("0 dB", "0 дБ"), Foreground = Wpf.Res<Brush>("Sub"), TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
                Grid.SetColumn(r.Value, 4);
                g.Children.Add(r.On);
                g.Children.Add(r.Solo);
                g.Children.Add(r.Name);
                g.Children.Add(r.Gain);
                g.Children.Add(r.Value);

                var row = r;
                r.Gain.ValueChanged += (s, e) => { row.Value.Text = DbText(row.Gain.Value); AudioChanged(); };
                r.Gain.MouseDoubleClick += (s, e) => row.Gain.Value = 0;
                r.On.Click += (s, e) => { RefreshRow(row); AudioChanged(); };
                r.Solo.Click += (s, e) => AudioChanged();
                rows.Add(r);
                panel.Children.Add(g);
            }
            if (a.Count == 0) panel.Children.Add(new TextBlock { Text = L.T("The file has no audio", "В файле нет звука"), Foreground = Wpf.Res<Brush>("Muted") });
            F<StackPanel>("RebuildRow").Visibility = rows.Any(IsMix) && rows.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        }

        void RefreshRow(TrackRow r)
        {
            bool on = r.On.IsChecked == true;
            r.On.Content = on ? "" : "";
            r.On.ToolTip = on ? L.T("Track is in the file — click to remove", "Дорожка в файле — нажми, чтобы убрать") : L.T("Track removed from the file — click to bring it back", "Дорожка убрана из файла — нажми, чтобы вернуть");
            r.Gain.IsEnabled = on;
            r.Name.Opacity = r.Value.Opacity = on ? 1 : 0.4;
            UpdateLaneStates();
        }

        // any track change: the mix rebuilds itself (until edited by hand), preview audio is rebuilt
        void AudioChanged()
        {
            if (!rebuildTouched && rows.Any(IsMix))
                rebuildMix.IsChecked = rows.Any(r => !IsMix(r) && (r.On.IsChecked != true || Math.Abs(r.Gain.Value) > 0.01));
            UpdateEstimate();
            audioDebounce.Stop();
            audioDebounce.Start();
        }

        void BuildAudio()
        {
            if (closed || info == null || info.Audio.Count == 0 || !Ffmpeg.Available) return;
            if (audioCts != null) audioCts.Cancel();
            audioCts = new CancellationTokenSource();
            var token = audioCts.Token;
            int gen = ++audioGen;
            var job = BuildJob();
            var solo = rows.Where(r => r.Solo.IsChecked == true).Select(r => r.Source).ToList();
            string wav = Path.Combine(tempDir, "audio" + gen + ".wav");
            audioStatus.Text = L.T("building audio…", "собираю звук…");
            Task.Factory.StartNew(() => Trimmer.PreviewAudio(job, solo, wav, token)).ContinueWith(t => W.Dispatcher.BeginInvoke(new Action(() =>
            {
                if (closed || gen != audioGen || t.IsFaulted || t.IsCanceled || !t.Result) { TryDelete(wav); return; }
                string old = audioFile;
                audioFile = wav;
                HoldSync();                 // while the file opens its position is 0; the clock runs by time
                audio.Open(new Uri(wav));   // position and start happen in MediaOpened
                player.IsMuted = true;
                audioReady = true;
                ApplyVolume();
                audioStatus.Text = solo.Count > 0 ? L.T("solo: ", "слушаешь отдельно: ") + string.Join(", ", solo.Select(x => x + 1)) : HearText;
                LoadWave(wav, gen, solo.Count > 0 ? L.T("waveform: ", "волна: ") + L.W(solo.Count, "track", "tracks", "дорожка", "дорожки", "дорожки") + " " + string.Join(", ", solo.Select(x => x + 1)) + L.T(" (solo)", " (соло)")
                                                  : WaveText);
                if (old != null) Task.Factory.StartNew(() => { Thread.Sleep(1500); TryDelete(old); });
            })));
        }

        static void TryDelete(string p) { try { if (File.Exists(p)) File.Delete(p); } catch { } }

        // preview volume (does not affect the file)
        void ApplyVolume()
        {
            double v = muted ? 0 : volume.Value / 100.0;
            audio.Volume = v;
            player.Volume = v;
            muteBtn.Content = muted || volume.Value <= 0 ? "" : volume.Value < 40 ? "" : "";
            muteBtn.Foreground = muted ? Wpf.Br(Wpf.Bad, 255) : Wpf.Res<Brush>("Sub");
        }
    }
}
