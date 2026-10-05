using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace DeviceGuard
{
    // The trim window: one window for many clips. The list is what the library showed when "Trim" was pressed (otherwise the
    // clip's folder, newest first); ← / → in the title bar, Ctrl+← / Ctrl+→ and "Next clip" after saving go through it.
    // A clip opened from elsewhere comes into the same window. Unsaved edits are never dropped silently: the first press
    // only warns, an outside open asks.
    partial class TrimWindow
    {
        static readonly Regex Video = new Regex(@"\.(mp4|mkv|mov|flv)$", RegexOptions.IgnoreCase);

        List<string> queue = new List<string>();
        DateTime leaveArmed = DateTime.MinValue;
        Border askBar;
        Tuple<string, List<string>> openAfterSave;   // asked to open while saving: done right after

        public string Source { get { return source; } }
        public string NavText { get { return F<TextBlock>("ClipPos").Text + " " + F<TextBlock>("FileText").Text; } }   // for the self-test

        // edits that would be lost: in/out, cuts — unless exactly these were saved and verified (a failed save or
        // edits after "edit again" still count)
        EditState savedState;
        bool Dirty { get { return info != null && (undo.Count > 0 || cuts.Count > 0) && (savedState == null || !Same(savedState, Snap())); } }

        static bool Same(string a, string b) { return string.Equals(a, b, StringComparison.OrdinalIgnoreCase); }

        void InitClips()
        {
            F<Button>("BtnPrevClip").Click += (s, e) => GoClip(-1);
            F<Button>("BtnNextClip").Click += (s, e) => GoClip(1);
            F<Button>("BtnGoNext").Click += (s, e) => GoClip(1);
            F<Button>("BtnDelClip").Click += (s, e) => DeleteClip();
            SetQueue(source, null);
        }

        // the clip to the Recycle Bin without trimming it first: the first press only asks, the second (within 4 s) deletes;
        // then the next clip of the list opens (or the previous one), and with none left the window closes
        DateTime delArmed = DateTime.MinValue;

        void DeleteClip()
        {
            if (busy || closed || sourceDeleted) return;
            var text = F<TextBlock>("DelClipText");
            if ((DateTime.Now - delArmed).TotalSeconds > 4)
            {
                delArmed = DateTime.Now;
                text.Visibility = Visibility.Visible;
                F<TextBlock>("DelClipIcon").Foreground = Wpf.Res<Brush>("Bad");
                var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
                var armed = delArmed;
                t.Tick += (s, e) => { t.Stop(); if (delArmed == armed) DisarmDelete(); };
                t.Start();
                return;
            }
            DisarmDelete();
            string gone = source, next = Neighbour(1) ?? Neighbour(-1);
            try { player.Stop(); player.Close(); player.Source = null; audio.Stop(); } catch { }   // the player keeps the file open
            if (!Shell.Recycle(gone))
            {
                Switch(gone);   // the player was closed: load the clip again
                ShowNote(L.T("Could not delete the clip (details in the log)", "Не удалось удалить клип (подробности в журнале)"), true);
                return;
            }
            Log.Write("editor: recycled " + gone);
            queue.RemoveAll(p => Same(p, gone));
            if (app != null) app.ClipsChanged();
            if (next == null) { W.Close(); return; }
            Switch(next);
            ShowNote(L.T("The clip was moved to the Recycle Bin", "Клип перемещён в корзину"), true);
        }

        void DisarmDelete()
        {
            delArmed = DateTime.MinValue;
            F<TextBlock>("DelClipText").Visibility = Visibility.Collapsed;
            F<TextBlock>("DelClipIcon").ClearValue(TextBlock.ForegroundProperty);
        }

        static List<string> FolderClips(string path)
        {
            try
            {
                return new DirectoryInfo(Path.GetDirectoryName(path)).GetFiles().Where(f => Video.IsMatch(f.Name))
                    .OrderByDescending(f => f.LastWriteTime).Select(f => f.FullName).ToList();
            }
            catch { return new List<string> { path }; }
        }

        void SetQueue(string path, List<string> list)
        {
            queue = list != null && list.Count > 0 ? list.ToList() : FolderClips(path);
            if (!queue.Any(p => Same(p, path))) queue.Insert(0, path);
            UpdateClipNav();
        }

        int QueueIndex { get { return queue.FindIndex(p => Same(p, source)); } }

        // the neighbour that is still there (a source deleted after trimming is skipped)
        string Neighbour(int dir)
        {
            for (int i = QueueIndex + dir; i >= 0 && i < queue.Count; i += dir)
                if (File.Exists(queue[i])) return queue[i];
            return null;
        }

        void UpdateClipNav()
        {
            string prev = Neighbour(-1), next = Neighbour(1);
            var bp = F<Button>("BtnPrevClip");
            var bn = F<Button>("BtnNextClip");
            bp.IsEnabled = prev != null && !busy;
            bn.IsEnabled = next != null && !busy;
            F<Button>("BtnDelClip").IsEnabled = !busy && !sourceDeleted;
            bp.ToolTip = prev != null ? L.T("Previous clip · Ctrl+←\n", "Предыдущий клип · Ctrl+←\n") + Path.GetFileName(prev) : null;
            bn.ToolTip = next != null ? L.T("Next clip · Ctrl+→\n", "Следующий клип · Ctrl+→\n") + Path.GetFileName(next) : null;
            F<FrameworkElement>("ClipNav").Visibility = queue.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
            F<TextBlock>("ClipPos").Text = (QueueIndex + 1) + " / " + queue.Count;
            // after saving: the big "Next clip" — the way to go through a session of clips
            var go = F<Button>("BtnGoNext");
            go.Visibility = next != null ? Visibility.Visible : Visibility.Collapsed;
            go.IsEnabled = !busy;
            go.ToolTip = next != null ? Path.GetFileName(next) : null;
            int ni = next != null ? queue.FindIndex(p => Same(p, next)) : -1;
            F<TextBlock>("GoNextPos").Text = ni >= 0 ? (ni + 1) + " / " + queue.Count : "";
        }

        void GoClip(int dir)
        {
            string target = Neighbour(dir);
            if (target == null || busy || closed) return;
            if (Dirty && (DateTime.Now - leaveArmed).TotalSeconds > 4)
            {
                leaveArmed = DateTime.Now;
                ShowNote(L.T("The edits of this clip are not saved — press again to go on", "Правки этого клипа не сохранены — нажми ещё раз, чтобы перейти"), true);
                return;
            }
            leaveArmed = DateTime.MinValue;
            Switch(target);
        }

        // from outside (the library, a clip card, a hotkey): the clip comes into this window
        public void Open(string path, List<string> list)
        {
            if (W.WindowState == WindowState.Minimized) W.WindowState = WindowState.Normal;
            W.Activate();
            if (Same(path, source)) { if (list != null) SetQueue(path, list); return; }
            if (busy)
            {
                openAfterSave = Tuple.Create(path, list);
                ShowNote(L.T("Saving — ", "Сохраняю — ") + Path.GetFileName(path) + L.T(" opens right after", " откроется сразу после"), false);
                return;
            }
            if (Dirty) { Ask(path, list); return; }
            if (list != null) SetQueue(path, list);
            else if (!queue.Any(p => Same(p, path))) SetQueue(path, null);
            Switch(path);
        }

        // called when saving is over: a clip asked for meanwhile opens now
        void AfterSave()
        {
            if (closed) return;
            UpdateClipNav();
            var o = openAfterSave;
            openAfterSave = null;
            if (o != null) Open(o.Item1, o.Item2);
        }

        // a bar over the editor: open the other clip and lose the edits, or stay
        void Ask(string path, List<string> list)
        {
            HideAsk();
            var text = new TextBlock
            {
                Text = L.T("Open ", "Открыть ") + Path.GetFileName(path) + L.T("? The edits of this clip are not saved.", "? Правки этого клипа не сохранены."),
                VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 16, 0),
            };
            var open = new Button { Style = Wpf.Res<Style>("BtnPrimary"), Content = L.T("Open", "Открыть"), Margin = new Thickness(0, 0, 8, 0) };
            var stay = new Button { Style = Wpf.Res<Style>("BtnGhost"), Content = L.T("Stay", "Остаться") };
            open.Click += (s, e) =>
            {
                HideAsk();
                if (busy) return;
                if (list != null) SetQueue(path, list);
                else if (!queue.Any(p => Same(p, path))) SetQueue(path, null);
                Switch(path);
            };
            stay.Click += (s, e) => HideAsk();
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.Children.Add(text);
            Grid.SetColumn(open, 1);
            Grid.SetColumn(stay, 2);
            g.Children.Add(open);
            g.Children.Add(stay);
            askBar = new Border
            {
                Background = Wpf.Br("#1C1D21"), BorderBrush = Wpf.Res<Brush>("LineHi"), BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8), Padding = new Thickness(16, 10, 10, 10), Margin = new Thickness(20, 6, 20, 0),
                VerticalAlignment = VerticalAlignment.Top, Child = g,
            };
            Panel.SetZIndex(askBar, 50);
            Grid.SetRow(askBar, 1);
            F<Grid>("Root").Children.Add(askBar);
        }

        void HideAsk()
        {
            if (askBar == null) return;
            F<Grid>("Root").Children.Remove(askBar);
            askBar = null;
        }

        // the window takes another clip: everything of the old one stops and is forgotten, the new one loads like at the start
        void Switch(string path)
        {
            HideAsk();
            DisarmDelete();
            if (playing) Pause();
            playing = false;
            tick.Stop();
            audioDebounce.Stop();
            cts.Cancel();
            cts = new CancellationTokenSource();
            if (audioCts != null) audioCts.Cancel();
            audioGen++;
            clipGen++;
            try { player.Stop(); player.Close(); player.Source = null; audio.Stop(); audio.Close(); } catch { }
            string oldAudio = audioFile;
            if (oldAudio != null) Task.Factory.StartNew(() => { Thread.Sleep(1500); TryDelete(oldAudio); });
            audioFile = null;
            audioReady = false;
            player.IsMuted = false;

            source = path;
            info = null;
            meta = null;
            duration = 1;
            inT = outT = pos = 0;
            speed = 1;
            cuts.Clear();
            cutStart = null;
            undo.Clear();
            redo.Clear();
            RebuildCutVisuals();
            lastJob = null;
            savedState = null;
            verified = sourceDeleted = rebuildTouched = swapping = false;
            strip.Source = null;
            wave.Source = null;
            waveCaption.Text = "";
            steps.Clear();
            ShowExport();
            ShowNote(null, false);
            F<TextBlock>("FileText").Text = Path.GetFileName(source);
            playerMsg.Text = L.T("Loading…", "Загружаю…");
            playerMsg.Visibility = Visibility.Visible;
            UpdateFolder();
            UpdateClipNav();
            Log.Write("editor: " + path);
            Load();
        }
    }
}
