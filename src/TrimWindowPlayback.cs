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
    // The trim window: playback, keys (like in Premiere) and the light copy for watching heavy video.
    partial class TrimWindow
    {
        // ── playback ──
        void Seek(double t)
        {
            pos = Math.Max(0, Math.Min(duration, t));
            try
            {
                player.Position = TimeSpan.FromSeconds(pos);
                if (audioReady) audio.Position = TimeSpan.FromSeconds(pos);
            }
            catch { }
            HoldSync();
            Follow(false);
            Layout();
        }

        // frame stepping (like ←/→ in Premiere): snap to the frame grid
        void StepFrames(int n)
        {
            if (playing) Pause();
            Seek(Math.Round(pos * fps + n) / fps);
        }

        // edit points: start and end of the clip, the range and the cuts
        void JumpEdit(int dir)
        {
            var pts = new List<double> { 0, inT, outT, duration };
            foreach (var c in cuts) { pts.Add(c[0]); pts.Add(c[1]); }
            double eps = 0.5 / fps;
            var cand = dir < 0 ? pts.Where(p => p < pos - eps).ToList() : pts.Where(p => p > pos + eps).ToList();
            if (cand.Count == 0) return;
            Seek(dir < 0 ? cand.Max() : cand.Min());
        }

        void HoldSync()
        {
            holdPos = pos;
            holdAt = DateTime.Now;
            syncHold = holdAt.AddSeconds(1.5);
            driftTicks = 0;
        }

        void TogglePlay(bool selection)
        {
            if (playing && (!selection || loopSel)) Pause();
            else StartPlay(selection, 1);
        }

        void StartPlay(bool selection, double rate)
        {
            if (selection || pos >= duration - 0.05) Seek(selection ? inT : 0);
            loopSel = selection;
            speed = rate;
            player.Play();
            player.SpeedRatio = speed;
            if (audioReady) { audio.Position = TimeSpan.FromSeconds(pos); audio.Play(); audio.SpeedRatio = speed; }
            HoldSync();
            playing = true;
            F<Button>("BtnPlay").Content = "";
            Layout();
        }

        void Pause()
        {
            player.Pause();
            audio.Pause();
            playing = false;
            loopSel = false;
            speed = 1;
            F<Button>("BtnPlay").Content = "";
            Layout();
        }

        // L: play, again — 2×, 4×
        void Faster()
        {
            if (!playing) { StartPlay(false, 1); return; }
            speed = Math.Min(4, speed * 2);
            player.SpeedRatio = speed;
            audio.SpeedRatio = speed;
            HoldSync();
            Layout();
        }

        void OnTick()
        {
            if (!playing) return;
            bool hold = DateTime.Now < syncHold;
            // the clock: right after a seek — by time (players still report the old position), then by audio, without it — by video
            double v = player.Position.TotalSeconds;
            double a = audioReady ? audio.Position.TotalSeconds : -1, run = (DateTime.Now - holdAt).TotalSeconds * speed;
            if (!hold) pos = audioReady ? a : v;
            // right after a seek the player may stand still for a second: the playhead waits for audio instead of running ahead
            else if (audioReady) pos = a >= holdPos - 0.05 && a <= holdPos + run + 0.5 ? Math.Max(holdPos, a) : holdPos;
            else pos = Math.Min(duration, holdPos + run);
            if (cutStart == null)
            {
                var skip = cuts.FirstOrDefault(c => pos >= c[0] && pos < c[1] - 0.03);
                if (skip != null) { Seek(skip[1]); return; }   // cut parts are skipped while watching
            }
            if (loopSel && pos >= outT) { Seek(inT); return; }   // loop the range
            if (pos >= duration - 0.02) { Pause(); return; }
            // audio is a separate player. If the video drifts from the audio noticeably and steadily (not just for a moment after a seek),
            // pull the video to the audio: a picture jump is less noticeable than repeated audio
            if (audioReady && !hold)
            {
                if (Math.Abs(v - pos) > 0.25 * speed)
                {
                    if (++driftTicks >= 5)
                    {
                        player.Position = TimeSpan.FromSeconds(pos);
                        player.Play();   // if the video stopped (e.g. after a source swap) — let it go on
                        player.SpeedRatio = speed;
                        HoldSync();
                    }
                }
                else driftTicks = 0;
            }
            Follow(true);
            Layout();
        }

        // ── keys (like in Premiere) ──
        // the key map is shared with the main window (KeyMap)
        void BuildKeys() { F<Grid>("KeysList").Children.Add(KeyMap.Build()); }

        void ShowKeys(bool show) { F<Grid>("KeysOverlay").Visibility = show ? Visibility.Visible : Visibility.Collapsed; }

        void OnKey(object sender, KeyEventArgs e)
        {
            bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0, shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            // the cheat sheet closes on any key
            if (F<Grid>("KeysOverlay").Visibility == Visibility.Visible)
            {
                if (key != Key.LeftShift && key != Key.RightShift && key != Key.LeftCtrl && key != Key.RightCtrl) { ShowKeys(false); e.Handled = true; }
                return;
            }
            if (busy) return;
            if (key == Key.S && ctrl) { Save(); e.Handled = true; return; }
            if (e.OriginalSource is TextBox) return;   // keys work as usual in the name field
            // fixed keys: undo / redo, Esc, "?"; everything else comes from the user's key map (KeyMap)
            if (ctrl)
            {
                if (key == Key.Left) GoClip(-1);         // the previous / next clip of the list (TrimWindowClips.cs)
                else if (key == Key.Right) GoClip(1);
                else if (key == Key.Z) { if (shift) Redo(); else Undo(); }
                else if (key == Key.Y) Redo();
                else return;
                e.Handled = true;
                return;
            }
            if (key == Key.Escape) { if (cutStart == null) return; CancelPendingCut(); e.Handled = true; return; }
            if (key == Key.OemQuestion && shift) { ShowKeys(true); e.Handled = true; return; }
            var act = KeyMap.ActOf(key);
            if (act == null) return;
            switch (act.Value)
            {
                case KeyMap.Act.Play: TogglePlay(false); break;
                case KeyMap.Act.Loop: TogglePlay(true); break;
                case KeyMap.Act.Stop: if (playing) Pause(); break;
                case KeyMap.Act.Faster: Faster(); break;
                case KeyMap.Act.Back5: if (playing && speed > 1) { speed = 1; player.SpeedRatio = 1; audio.SpeedRatio = 1; } Seek(pos - 5); break;
                case KeyMap.Act.FrameBack: if (shift) Seek(pos - 1); else StepFrames(-1); break;
                case KeyMap.Act.FrameFwd: if (shift) Seek(pos + 1); else StepFrames(1); break;
                case KeyMap.Act.PrevEdit: JumpEdit(-1); break;
                case KeyMap.Act.NextEdit: JumpEdit(1); break;
                case KeyMap.Act.Start: Seek(0); break;
                case KeyMap.Act.End: Seek(duration); break;
                case KeyMap.Act.MarkIn: if (shift) Seek(inT); else MarkIn(); break;
                case KeyMap.Act.MarkOut: if (shift) Seek(outT); else MarkOut(); break;
                case KeyMap.Act.Cut: ToggleCut(); break;
                case KeyMap.Act.Restore:
                    int under = cuts.FindIndex(c => pos >= c[0] && pos <= c[1]);
                    if (under < 0) return;
                    RemoveCut(under);
                    break;
                case KeyMap.Act.ZoomIn: ZoomAt(ZoomAnchor(), 1.6); break;
                case KeyMap.Act.ZoomOut: ZoomAt(ZoomAnchor(), 1 / 1.6); break;
                case KeyMap.Act.ZoomFit: ZoomFit(); break;
                case KeyMap.Act.Mute: muted = !muted; ApplyVolume(); break;
                case KeyMap.Act.Lanes: SetLanes(!lanesOn); break;
                case KeyMap.Act.Keys: ShowKeys(true); break;
                default: return;
            }
            e.Handled = true;
        }

        // ── a light copy for watching: heavy video (HEVC, high bitrate) lags in the WPF player ──
        static string ProxyDir { get { return Path.Combine(Path.GetTempPath(), "dg_trim", "proxy"); } }

        string ProxyPath()
        {
            var f = new FileInfo(source);
            string key = f.FullName.ToLowerInvariant() + "|" + f.Length + "|" + f.LastWriteTimeUtc.Ticks;
            using (var md5 = System.Security.Cryptography.MD5.Create())
                return Path.Combine(ProxyDir, BitConverter.ToString(md5.ComputeHash(System.Text.Encoding.UTF8.GetBytes(key))).Replace("-", "").ToLowerInvariant() + ".mp4");
        }

        bool NeedsProxy()
        {
            string codec = info.Streams.Where(x => x.Type == "video").Select(x => x.Codec).FirstOrDefault();
            double mbps = info.Duration > 0 ? info.Size * 8 / info.Duration / 1e6 : 0;
            return codec != "h264" || mbps > 20;
        }

        // while the copy is made (~10 s on the GPU) the original plays; then the player switches seamlessly
        void MakeProxy(string path)
        {
            ShowNote(L.T("preparing smooth preview…", "готовлю плавный просмотр…"), false);
            var token = cts.Token;
            string src = source;   // the copy is made of this clip even if another one is opened meanwhile
            int gen = clipGen;
            Task.Factory.StartNew(() =>
            {
                Directory.CreateDirectory(ProxyDir);
                // don't hoard old copies: no older than 3 days and no more than 8
                var olds = new DirectoryInfo(ProxyDir).GetFiles().OrderByDescending(x => x.LastWriteTime).ToList();
                for (int i = 0; i < olds.Count; i++)
                    if (i >= 8 || (DateTime.Now - olds[i].LastWriteTime).TotalDays > 3) TryDelete(olds[i].FullName);
                string tmp = path + ".part.mp4";
                try
                {
                    string common = "-v error -hwaccel auto -i \"" + src + "\" -map 0:v:0 -an -vf scale=-2:720 -g 30 -bf 0 -pix_fmt yuv420p ";
                    string hw = Encoders.Fast();
                    var r = hw != null ? Ffmpeg.Run(common + hw + " \"" + tmp + "\"", token) : null;
                    if (r == null || r.Code != 0) r = Ffmpeg.Run(common + "-c:v libx264 -preset ultrafast -crf 23 \"" + tmp + "\"", token);
                    if (r.Code != 0 || !File.Exists(tmp)) { TryDelete(tmp); return false; }
                    TryDelete(path);
                    File.Move(tmp, path);
                    return true;
                }
                catch { TryDelete(tmp); throw; }
            }).ContinueWith(t => W.Dispatcher.BeginInvoke(new Action(() =>
            {
                if (closed || gen != clipGen) return;
                if (t.IsFaulted || t.IsCanceled || !t.Result)
                {
                    ShowNote(null, false);
                    if (t.IsFaulted) Log.Write("preview copy: " + t.Exception.GetBaseException().Message);
                    return;
                }
                swapping = true;
                try { player.Source = new Uri(path); player.Play(); if (!playing) player.Pause(); }
                catch (Exception ex) { swapping = false; Log.Write("preview copy: " + ex.Message); }
            })));
        }

        void ShowNote(string text, bool fade)
        {
            noteTimer.Stop();
            F<Border>("PreviewNote").Visibility = text == null ? Visibility.Collapsed : Visibility.Visible;
            if (text != null) F<TextBlock>("PreviewNoteText").Text = text;
            if (fade) noteTimer.Start();
        }
    }
}
