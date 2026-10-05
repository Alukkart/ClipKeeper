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
    // The trim window: --playtest and --keytest (offscreen checks with a real clip) and previews.
    partial class TrimWindow
    {
        // ── ClipKeeper.exe --playtest <clip> <report>: offscreen window, volume 0; plays, seeks,
        //    changes a track on the fly and closes right after the second edit — video and audio positions go to the report ──
        public void PlayTest(string report, Action done)
        {
            var sb = new System.Text.StringBuilder();
            testRun = true;
            W.WindowStartupLocation = WindowStartupLocation.Manual;
            W.Left = -4000; W.Top = 0;
            W.ShowActivated = false;
            muted = true;
            DateTime t0 = DateTime.MinValue, closedAt = DateTime.MinValue;
            int step = 0;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            timer.Tick += (s, e) =>
            {
                var now = DateTime.Now;
                if (t0 == DateTime.MinValue)
                {
                    if (audioReady && audio.NaturalDuration.HasTimeSpan) { t0 = now; ApplyVolume(); sb.AppendLine("audio ready: " + audioFile); }
                    else return;
                }
                double t = (now - t0).TotalSeconds;
                if (closedAt == DateTime.MinValue)
                    sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "{0,5:0.0}  clock {1,6:0.00}  video {2,6:0.00}  audio {3,6:0.00}{4}",
                        t, pos, player.Position.TotalSeconds, audio.Position.TotalSeconds, (playing ? "" : "  (paused)") +
                        (player.Source != null && Path.GetDirectoryName(player.Source.LocalPath).EndsWith("proxy") ? "  [720p copy]" : "  [original]")));
                else
                    sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "{0,5:0.0}  after closing: audio source {1}, position {2:0.00}",
                        t, audio.Source == null ? "none" : audio.Source.ToString(), audio.Position.TotalSeconds));
                if (step == 0 && t >= 0) { step++; sb.AppendLine("— play from 0"); TogglePlay(false); }
                else if (step == 1 && t >= 3) { step++; sb.AppendLine("— seek to 60"); Seek(60); }
                else if (step == 2 && t >= 7) { step++; sb.AppendLine("— seek to 20"); Seek(20); }
                else if (step == 3 && t >= 11) { step++; sb.AppendLine("— track 1: −6 dB (audio rebuilds on the fly)"); rows[0].Gain.Value = -6; }
                else if (step == 4 && t >= 16) { step++; sb.AppendLine("— seek to 100"); Seek(100); }
                else if (step == 5 && t >= 20)
                {
                    step++;
                    sb.AppendLine("— track 1: −12 dB and close the window right away");
                    rows[0].Gain.Value = -12;
                    W.Dispatcher.BeginInvoke(new Action(() => W.Close()), DispatcherPriority.Background);
                    closedAt = now;
                }
                else if (step == 6 && (now - closedAt).TotalSeconds >= 4)
                {
                    timer.Stop();
                    File.WriteAllText(report, sb.ToString(), System.Text.Encoding.UTF8);
                    done();
                }
            };
            W.Loaded += (s, e) => timer.Start();
            W.Show();
        }

        // ── ClipKeeper.exe --keytest <clip> <report>: offscreen window, audio off; real key presses ──
        public void KeyTest(string report, Action done)
        {
            var sb = new System.Text.StringBuilder();
            int fails = 0;
            Action<bool, string> check = (ok, what) => { sb.AppendLine((ok ? "PASS " : "FAIL ") + what); if (!ok) fails++; };
            Action<Key> press = k =>
            {
                var src = PresentationSource.FromVisual(W);
                W.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, src, 0, k) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
            };
            Func<double, string> f = v => v.ToString("0.000", Inv);
            testRun = true;
            KeyMap.Reset();   // the test presses the default keys
            W.WindowStartupLocation = WindowStartupLocation.Manual;
            W.Left = -4000; W.Top = 0;
            W.ShowActivated = false;
            muted = true;
            int step = 0;
            double mark = 0;
            DateTime at = DateTime.MinValue;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            timer.Tick += (s, e) =>
            {
                if (!audioReady)
                {
                    File.WriteAllText(report, "waiting for audio… file read: " + (info != null) + ", message: \"" + playerMsg.Text + "\", audio status: \"" +
                                      audioStatus.Text + "\", ffmpeg: " + Ffmpeg.Available + ", window loaded: " + W.IsLoaded, System.Text.Encoding.UTF8);
                    return;
                }
                if ((DateTime.Now - at).TotalSeconds < 0) return;
                ApplyVolume();
                double wait = 0;
                switch (step)
                {
                    case 0: Seek(30); wait = 0.6; break;
                    case 1: mark = pos; press(Key.L); wait = 1.0; break;
                    case 2: check(playing && speed == 1, "L: play 1× (" + f(pos - mark) + " s in 1 s)"); mark = pos; press(Key.L); wait = 1.0; break;
                    case 3: check(playing && speed == 2 && pos - mark > 1.4, "L again: 2× (" + f(pos - mark) + " s in 1 s)"); press(Key.K); wait = 0.3; break;
                    case 4: check(!playing && speed == 1, "K: stop"); Seek(40); wait = 0.4; break;
                    case 5: mark = pos; press(Key.Right); press(Key.Right); press(Key.Right); wait = 0.2; break;
                    case 6: check(Math.Abs((pos - mark) * fps - 3) < 0.01, "→ ×3: exactly 3 frames (" + f(pos - mark) + " s at " + fps.ToString("0.##", Inv) + " fps)"); press(Key.Left); wait = 0.2; break;
                    case 7: check(Math.Abs((pos - mark) * fps - 2) < 0.01, "←: one frame back"); press(Key.J); wait = 0.2; break;
                    case 8: check(Math.Abs(pos - (mark + 2 / fps - 5)) < 0.01, "J: −5 s"); Seek(20); press(Key.I); Seek(50); press(Key.O); wait = 0.2; break;
                    case 9: check(Math.Abs(inT - 20) < 1e-6 && Math.Abs(outT - 50) < 1e-6, "I / O: range 20 → 50"); Seek(30); press(Key.X); Seek(34); press(Key.X); wait = 0.2; break;
                    case 10: check(cuts.Count == 1 && Math.Abs(cuts[0][0] - 30) < 1e-6 && Math.Abs(cuts[0][1] - 34) < 1e-6, "X, X: cut 30 → 34"); Seek(25); press(Key.Down); wait = 0.2; break;
                    case 11: check(Math.Abs(pos - 30) < 1e-6, "↓: to the next edit point (cut start, " + f(pos) + ")"); press(Key.Down); press(Key.Down); wait = 0.2; break;
                    case 12: check(Math.Abs(pos - 50) < 1e-6, "↓ ×2: cut end, then range end (" + f(pos) + ")"); press(Key.Up); wait = 0.2; break;
                    case 13: check(Math.Abs(pos - 34) < 1e-6, "↑: back to the cut end (" + f(pos) + ")"); Undo(); wait = 0.2; break;
                    case 14: check(cuts.Count == 0 && Math.Abs(outT - 50) < 1e-6, "Ctrl+Z: cut undone, range intact"); Undo(); wait = 0.2; break;
                    case 15: check(Math.Abs(outT - duration) < 1e-6 && Math.Abs(inT - 20) < 1e-6, "Ctrl+Z again: range end undone"); Redo(); Redo(); wait = 0.2; break;
                    case 16: check(cuts.Count == 1 && Math.Abs(outT - 50) < 1e-6, "Ctrl+Shift+Z ×2: everything is back"); Seek(40); press(Key.OemPlus); wait = 0.2; break;
                    case 17: check(Zoomed && Math.Abs(duration / viewLen - 1.6) < 0.01 && pos >= viewStart && pos <= viewStart + viewLen, "=: zoom 1.6×, playhead in view"); press(Key.Oem5); wait = 0.2; break;
                    case 18: check(!Zoomed, "backslash: whole timeline"); muted = false; ApplyVolume(); press(Key.M); wait = 0.2; break;   // paused — nothing audible
                    case 19: check(audio.Volume == 0 && player.Volume == 0, "M: preview audio off"); press(Key.F1); wait = 0.2; break;
                    case 20: check(F<Grid>("KeysOverlay").Visibility == Visibility.Visible, "F1: cheat sheet opened"); press(Key.A); wait = 0.2; break;
                    case 21: check(F<Grid>("KeysOverlay").Visibility != Visibility.Visible, "any key: cheat sheet closed"); press(Key.End); wait = 0.2; break;
                    case 22: check(Math.Abs(pos - duration) < 1e-6, "End: clip end"); press(Key.Home); wait = 0.2; break;
                    case 23:
                        check(pos == 0, "Home: clip start");
                        if (info.Audio.Count > 1) { SetLanes(false); press(Key.T); wait = 6; } break;
                    case 24:
                        if (info.Audio.Count > 1)
                        {
                            check(lanesOn && Math.Abs(timeline.ActualHeight - (WaveTop + info.Audio.Count * LaneH)) < 1 && wave.Visibility != Visibility.Visible,
                                  "T: track lanes instead of the combined waveform, timeline " + timeline.ActualHeight + " px");
                            check(laneImgs.All(x => x.Source != null), "lanes: a waveform for each of " + laneImgs.Count + " tracks");
                            press(Key.T);
                        }
                        wait = 0.3; break;
                    case 25:
                        if (info.Audio.Count > 1) check(!lanesOn && Math.Abs(timeline.ActualHeight - BaseH) < 1 && wave.Visibility == Visibility.Visible,
                                                        "T again: lanes hidden, combined waveform is back");
                        double alive = WaveAlive();
                        check(wave.Source != null && alive > 0.2, "waveform over all enabled tracks: visible on " + Math.Round(alive * 100) + "% of the width · " +
                              waveCaption.Text + " · tracks in the clip: " + info.Audio.Count + (mixIndex < info.Audio.Count ? "" : " (no mix)"));
                        timer.Stop();
                        sb.AppendLine(fails == 0 ? "RESULT: ALL PASSED" : "RESULT: " + fails + " FAILED");
                        File.WriteAllText(report, sb.ToString(), System.Text.Encoding.UTF8);
                        W.Close();
                        done();
                        return;
                }
                step++;
                at = DateTime.Now.AddSeconds(wait);
                File.WriteAllText(report, sb.ToString() + "… step " + step, System.Text.Encoding.UTF8);   // if it hangs, this shows where
            };
            W.Loaded += (s, e) => timer.Start();
            W.Show();
        }

        // the share of waveform columns noticeably above a flat line (for --keytest)
        double WaveAlive()
        {
            var src = wave.Source as BitmapSource;
            if (src == null) return 0;
            var bmp = new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
            int w = bmp.PixelWidth, h = bmp.PixelHeight, stride = w * 4;
            var px = new byte[stride * h];
            bmp.CopyPixels(px, stride, 0);
            int alive = 0;
            for (int x = 0; x < w; x++)
            {
                int filled = 0;
                for (int y = 0; y < h; y++) if (px[y * stride + x * 4 + 3] > 40) filled++;
                if (filled > h * 0.1) alive++;
            }
            return (double)alive / w;
        }

        // ── for previews ──
        public void PreviewLoad(Ffmpeg.MediaInfo i, BitmapSource stripImg, BitmapSource waveImg, double a, double b, double p, string clipsRoot)
        {
            testRun = true;   // previews draw pictures — settings are not touched
            info = i;
            duration = i.Duration;
            fps = i.Fps;
            viewStart = 0;
            viewLen = duration;
            meta = Trimmer.MetaOf(source, info, clipsRoot);
            nameBox.Text = Trimmer.DefaultTitle(meta);
            BuildTracks();
            inT = a; outT = b; pos = p;
            strip.Source = stripImg;
            wave.Source = waveImg;
            waveCaption.Text = WaveText;
            BuildLanes();
            playerMsg.Text = L.T("(video here)", "(здесь видео)");
            audioStatus.Text = HearText;
            UpdateMode();
        }

        public void PreviewCuts(IEnumerable<double[]> list)
        {
            foreach (var c in list) cuts.Add(c);
            NormalizeCuts();
            RebuildCutVisuals();
            UpdateEstimate();
        }

        // zoom and the time hint under the cursor — for the picture
        public void PreviewZoom(double from, double to, double hoverAt)
        {
            viewStart = from;
            viewLen = to - from;
            ClampView();
            W.Dispatcher.BeginInvoke(new Action(() => { Layout(); ShowHover(X(hoverAt)); }), DispatcherPriority.Loaded);
        }

        public void PreviewKeys() { ShowKeys(true); }
        public void PreviewLanes(bool on) { if (on && !lanesLoaded) LoadLanes(true); SetLanes(on); }
        public void PreviewShare(bool on) { modeLossless.IsChecked = !on; modeShare.IsChecked = on; UpdateMode(); }

        public void PreviewGain(int track, double db, bool on, bool solo)
        {
            rows[track].Gain.Value = db;
            rows[track].On.IsChecked = on;
            rows[track].Solo.IsChecked = solo;
            RefreshRow(rows[track]);
            AudioChanged();
            audioDebounce.Stop();
        }

        public void PreviewResult(IEnumerable<TrimStep> list, bool ok, string output)
        {
            verified = ok;
            lastJob = new TrimJob { Output = output };
            F<FrameworkElement>("ExportPanel").Visibility = Visibility.Collapsed;
            F<FrameworkElement>("ResultPanel").Visibility = Visibility.Visible;
            foreach (var s in list) steps.Add(StepVm(s));
            resultTitle.Text = ok ? L.T("✓ Done and verified — ", "✓ Готово и проверено — ") + Path.GetFileName(output) : L.T("The check failed — don't delete the source", "Проверка не пройдена — исходник не удаляй");
            resultTitle.Foreground = Wpf.Br(ok ? Wpf.Ok : Wpf.Bad, 255);
            F<Border>("ProgressFill").Width = 2000;
            F<Border>("ProgressFill").Background = Wpf.Br(ok ? Wpf.Ok : Wpf.Bad, 255);
            F<Button>("BtnDelSrc").Visibility = ok ? Visibility.Visible : Visibility.Collapsed;
        }
    }
}
