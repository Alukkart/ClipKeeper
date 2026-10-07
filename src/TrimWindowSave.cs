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
    // The trim window: modes, the folder and the size estimate; saving and checking the result.
    partial class TrimWindow
    {
        // ── modes, folder, estimate ──
        TrimMode Mode { get { return modePrecise.IsChecked == true ? TrimMode.Precise : modeShare.IsChecked == true ? TrimMode.Share : TrimMode.Lossless; } }
        ShareTarget Target
        {
            get
            {
                return tgtNitro.IsChecked == true ? ShareTarget.Nitro : tgtTelegram.IsChecked == true ? ShareTarget.Telegram
                     : tgtCustom.IsChecked == true ? ShareTarget.Custom : tgtGif.IsChecked == true ? ShareTarget.Gif : ShareTarget.Discord;
            }
        }

        void UpdateMode()
        {
            F<StackPanel>("SharePanel").Visibility = Mode == TrimMode.Share ? Visibility.Visible : Visibility.Collapsed;
            F<TextBlock>("ShareHint").Text = Target == ShareTarget.Discord ? L.T("up to 10 MB — long ranges become 720p", "до 10 МБ — длинные отрезки станут 720p")
                                           : Target == ShareTarget.Nitro ? L.T("up to 500 MB", "до 500 МБ")
                                           : Target == ShareTarget.Custom ? L.T("up to the size you set — small sizes lower the resolution", "до заданного размера — при маленьком размере снизится разрешение")
                                           : Target == ShareTarget.Gif ? L.T("an animation without sound, plays by itself in any chat", "анимация без звука — сама играет в любом чате")
                                           : L.T("good quality up to 2 GB", "хорошее качество до 2 ГБ");
            F<StackPanel>("CustomRow").Visibility = Target == ShareTarget.Custom ? Visibility.Visible : Visibility.Collapsed;
            UpdateEstimate();
        }

        void UpdateFolder()
        {
            bool custom = !string.IsNullOrEmpty(cfg.TrimFolder);
            string dir = custom ? cfg.TrimFolder : Path.GetDirectoryName(source);
            string name = Path.GetFileName(dir.TrimEnd('\\'));
            folderText.Text = string.IsNullOrEmpty(name) ? dir : name;
            folderText.ToolTip = (custom ? L.T("Fixed folder for trims: ", "Постоянная папка для обрезок: ") : L.T("Next to the source: ", "Рядом с исходником: ")) + dir;
            F<Button>("BtnFolderSrc").Visibility = custom ? Visibility.Visible : Visibility.Collapsed;
            UpdateEstimate();
        }

        void PickFolder()
        {
            string start = !string.IsNullOrEmpty(cfg.TrimFolder) ? cfg.TrimFolder : Path.GetDirectoryName(source);
            string path = FolderPicker.Pick(W, L.T("Where to save trimmed clips", "Куда сохранять обрезанные клипы"), start);
            if (path == null) return;
            cfg.TrimFolder = path;
            SaveCfg();
            UpdateFolder();
        }

        TrimJob BuildJob()
        {
            return new TrimJob
            {
                Source = source, In = inT, Out = outT, Mode = Mode, Target = Target, CustomMb = cfg.ShareCustomMb, SourceInfo = info, Meta = meta,
                Title = nameBox.Text, OutputDir = string.IsNullOrEmpty(cfg.TrimFolder) ? null : cfg.TrimFolder,
                Loudness = cfg.ShareLoudness, GifFormat = cfg.GifFormat, GifWidth = cfg.GifWidth, GifFps = cfg.GifFps,
                MixIndex = mixIndex, RebuildMix = rebuildMix.IsChecked == true,
                Cuts = cuts.Count > 0 ? cuts.Select(c => new[] { c[0], c[1] }).ToList() : null,
                Tracks = rows.Select(r => new TrackPlan
                {
                    Source = r.Source, On = r.On.IsChecked == true, Gain = Math.Round(r.Gain.Value, 1), Title = r.Title,
                }).ToList(),
            };
        }

        void UpdateEstimate()
        {
            if (info == null) return;
            var j = BuildJob();
            int tracks = Mode == TrimMode.Share ? 1 : j.Tracks.Count(t => t.On);
            estSize.Text = Trimmer.Estimate(j);
            estDur.Text = Trimmer.Dur(j.Length);
            estTracks.Text = j.Gif ? L.T("no sound", "без звука") : L.N(tracks, "track", "tracks", "дорожка", "дорожки", "дорожек");
            UpdateShare(j);
            string file = Trimmer.OutputPath(j);
            estFile.Text = Path.GetFileName(file);
            estFile.ToolTip = file;
            int n = cuts.Count(c => c[1] > inT && c[0] < outT);
            F<StackPanel>("EstCutsBox").Visibility = n > 0 ? Visibility.Visible : Visibility.Collapsed;
            var est = F<TextBlock>("EstCuts");
            est.Text = L.N(n, "cut", "cuts", "вырез", "выреза", "вырезов") + " · −" + Trimmer.Dur(outT - inT - j.Length);
            est.ToolTip = Mode != TrimMode.Share
                ? L.T("With cuts the video is re-encoded on the GPU at \"Frame-exact\" quality — a piece can't be cut frame-exactly without re-encoding", "С вырезами видео пережимается на видеокарте в качестве «По кадру» — без пережатия кусок нельзя вырезать точно по кадру")
                : L.T("Cut pieces won't be in the file", "Вырезанные куски не попадут в файл");
        }

        // ── saving and checking ──
        void ShowExport()
        {
            F<FrameworkElement>("ExportPanel").Visibility = Visibility.Visible;
            F<FrameworkElement>("ResultPanel").Visibility = Visibility.Collapsed;
            UpdateMode();
        }

        void Save()
        {
            if (busy || info == null) return;
            var job = BuildJob();
            if (!job.Tracks.Any(t => t.On) && info.Audio.Count > 0)
            {
                audioStatus.Text = L.T("enable at least one track", "включи хотя бы одну дорожку");
                audioStatus.Foreground = Wpf.Br(Wpf.Bad, 255);
                return;
            }
            audioStatus.Foreground = Wpf.Res<Brush>("Muted");
            if (playing) Pause();
            busy = true;
            UpdateClipNav();   // no going to another clip while this one is being saved
            verified = false;
            lastJob = job;
            var state = Snap();   // what this save is made from: the edits count as saved only once it is verified
            steps.Clear();
            resultTitle.Text = L.T("Saving and checking…", "Сохраняю и проверяю…");
            resultTitle.Foreground = Wpf.Res<Brush>("Text");
            F<FrameworkElement>("ExportPanel").Visibility = Visibility.Collapsed;
            F<FrameworkElement>("ResultPanel").Visibility = Visibility.Visible;
            SetResultButtons(false);
            var fill = F<Border>("ProgressFill");
            fill.Width = 0;
            fill.Background = Wpf.Res<Brush>("Brand");
            W.UpdateLayout();
            double full = ((FrameworkElement)fill.Parent).ActualWidth;
            var token = cts.Token;
            var shown = new Dictionary<TrimStep, EventVm>();

            Task.Factory.StartNew(() => Trimmer.Run(job,
                p => W.Dispatcher.BeginInvoke(new Action(() => fill.Width = Math.Max(0, full * p))),
                st => W.Dispatcher.BeginInvoke(new Action(() =>
                {
                    var vm = StepVm(st);
                    EventVm old;
                    if (shown.TryGetValue(st, out old)) steps[steps.IndexOf(old)] = vm;
                    else steps.Add(vm);
                    shown[st] = vm;
                })), token)).ContinueWith(t => W.Dispatcher.BeginInvoke(new Action(() =>
            {
                busy = false;
                bool copy = copyWhenDone;
                copyWhenDone = false;
                if (t.IsCanceled || (t.IsFaulted && t.Exception.GetBaseException() is OperationCanceledException)) { AfterSave(); return; }
                if (t.IsFaulted)
                {
                    steps.Add(StepVm(new TrimStep { Text = L.T("Error: ", "Ошибка: ") + t.Exception.GetBaseException().Message, State = 2 }));
                    verified = false;
                }
                else verified = t.Result;
                if (verified) savedState = state;
                fill.Width = full;
                fill.Background = Wpf.Br(verified ? Wpf.Ok : Wpf.Bad, 255);
                resultTitle.Text = verified
                    ? L.T("✓ Done and verified — ", "✓ Готово и проверено — ") + Path.GetFileName(job.Output)
                    : L.T("The check failed — don't delete the source", "Проверка не пройдена — исходник не удаляй");
                resultTitle.Foreground = Wpf.Br(verified ? Wpf.Ok : Wpf.Bad, 255);
                if (copy && verified && Shell.CopyFile(job.Output))
                    steps.Add(StepVm(new TrimStep { Text = L.T("The file is copied — paste it into the chat (Ctrl+V)", "Файл скопирован — вставь его в чат (Ctrl+V)"), State = 1 }));
                SetResultButtons(true);
                if (app != null) app.ClipsChanged();
                AfterSave();
            })));
        }

        void SetResultButtons(bool done)
        {
            bool exists = lastJob != null && File.Exists(lastJob.Output ?? "");
            F<Button>("BtnShow").IsEnabled = done && exists;
            F<Button>("BtnCopy").IsEnabled = done && exists;
            F<Button>("BtnAgain").IsEnabled = done && !sourceDeleted;
            // the source can be deleted only after a successful check
            F<Button>("BtnDelSrc").Visibility = done && verified && !sourceDeleted ? Visibility.Visible : Visibility.Collapsed;
            delSrcText.Text = L.T("Delete the source", "Удалить исходник");
        }

        static EventVm StepVm(TrimStep s)
        {
            var c = s.State == 1 ? Wpf.Ok : s.State == 2 ? Wpf.Bad : s.State == 3 ? Wpf.Grey : Wpf.Accent;
            string g = s.State == 1 ? "" : s.State == 2 ? "" : s.State == 3 ? "" : "";
            return new EventVm { Text = s.Text, Glyph = g, GlyphBrush = Wpf.Br(c, 255) };
        }

        void DeleteSource()
        {
            if (!verified || sourceDeleted) return;
            if (delSrcText.Text == L.T("Delete the source", "Удалить исходник"))
            {
                delSrcText.Text = L.T("Sure? Press again", "Точно? Нажми ещё раз");
                var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
                t.Tick += (s, e) => { t.Stop(); if (!sourceDeleted) delSrcText.Text = L.T("Delete the source", "Удалить исходник"); };
                t.Start();
                return;
            }
            try { player.Stop(); player.Close(); player.Source = null; audio.Stop(); } catch { }   // the player keeps the file open
            if (Shell.Recycle(source))
            {
                sourceDeleted = true;
                steps.Add(StepVm(new TrimStep { Text = L.T("The source was moved to the Windows Recycle Bin — it can be restored from there", "Исходник перемещён в корзину Windows — оттуда его можно восстановить"), State = 3 }));
                F<Button>("BtnDelSrc").Visibility = Visibility.Collapsed;
                F<Button>("BtnDelClip").IsEnabled = false;
                F<Button>("BtnAgain").IsEnabled = false;
                if (app != null) app.ClipsChanged();
            }
            else steps.Add(StepVm(new TrimStep { Text = L.T("Could not delete the source (details in the log)", "Не удалось удалить исходник (подробности в журнале)"), State = 2 }));
        }
    }
}
