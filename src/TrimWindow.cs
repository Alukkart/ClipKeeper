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
    // The trim window. Left — video, controls and the timeline (ruler, frames, waveform, zoom); right — tracks and saving.
    // The video plays muted; audio is a separate file built from the chosen tracks with your volume.
    // Keys are like in Premiere (cheat sheet: "?" or F1).
    // This file: fields, building the window and loading the clip; the rest is in TrimWindow*.cs by topic.
    partial class TrimWindow
    {
        public readonly Window W;
        readonly TrayApp app;
        readonly Settings cfg;
        string source;                   // the clip in the window; the next one comes into the same window (TrimWindowClips.cs)
        int clipGen;                     // grows with every clip: late answers for the previous one are dropped
        readonly int mixIndex;
        readonly string tempDir = Path.Combine(Path.GetTempPath(), "dg_trim", Guid.NewGuid().ToString("N"));
        readonly MediaElement player;
        readonly MediaPlayer audio = new MediaPlayer { Volume = 0.85 };
        readonly Grid timeline, overview;
        readonly Canvas canvas = new Canvas(), ruler = new Canvas { IsHitTestVisible = false };
        readonly Image strip = new Image { Stretch = Stretch.Fill }, wave = new Image { Stretch = Stretch.Fill, Opacity = 0.9 };
        readonly Rectangle maskL = new Rectangle(), maskR = new Rectangle(), selBox = new Rectangle(), playhead = new Rectangle(),
                           pendingBox = new Rectangle(), hoverLine = new Rectangle();
        readonly Border hoverTag = new Border(), overviewThumb;
        readonly TextBlock hoverText = new TextBlock();
        // the caption in the waveform corner: what exactly it shows (the result or "solo")
        readonly TextBlock waveCaption = new TextBlock { FontSize = 10.5, IsHitTestVisible = false };
        readonly TextBlock timeText, totalText, speedText, selText, zoomText, playerMsg, resultTitle, delSrcText, folderText, audioStatus,
                           estSize, estDur, estTracks, estFile;
        readonly TextBox nameBox;
        readonly Slider volume;
        readonly Button muteBtn;
        readonly ToggleButton modeLossless, modePrecise, modeShare, tgtDiscord, tgtNitro, tgtTelegram, tgtCustom, rebuildMix;
        readonly ObservableCollection<EventVm> steps = new ObservableCollection<EventVm>();
        readonly DispatcherTimer tick = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
        readonly DispatcherTimer audioDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        readonly DispatcherTimer noteTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        CancellationTokenSource cts = new CancellationTokenSource();   // the background work of the current clip
        readonly List<TrackRow> rows = new List<TrackRow>();
        Border handleL, handleR;

        // timeline heights: ruler, filmstrip, waveform; track lanes replace the waveform when on
        const double RulerH = 20, StripH = 58, WaveTop = RulerH + StripH + 4, BaseH = 128, LaneH = 34;
        // track colors come from the validated palette, in its order (so adjacent lanes stay distinguishable with color blindness)
        static readonly string[] LaneColors = { "#3987e5", "#d95926", "#199e70", "#c98500", "#d55181", "#008300" };
        readonly List<Image> laneImgs = new List<Image>();
        readonly List<Border> laneTags = new List<Border>();
        readonly List<Rectangle> laneSeps = new List<Rectangle>();
        ToggleButton lanesBtn;
        bool lanesOn, lanesLoaded;

        Ffmpeg.MediaInfo info;
        ClipMeta meta;
        double duration = 1, inT, outT, pos, fps = 60;
        double viewStart, viewLen = 1;   // the visible part of the timeline (zoom)
        double speed = 1;                // J/K/L: 1×, 2×, 4×
        bool playing, loopSel, busy, verified, sourceDeleted, audioReady, rebuildTouched, closed, muted, swapping;
        bool copyWhenDone;   // a one-click share: the verified file goes to the clipboard by itself
        bool testRun;                    // --playtest / --keytest: the window is offscreen, settings are not touched
        // after a seek the video reports the old position for a while as it rolls to a keyframe —
        // leave the audio alone meanwhile, otherwise it "stutters" on the same piece
        DateTime syncHold, holdAt;
        double holdPos;
        int driftTicks;
        string drag;                     // "in" | "out" | "head" | "c0:N" | "c1:N"
        EditState dragBefore;
        bool overviewDrag;
        double overviewGrab;
        string rulerSig;
        string audioFile;
        int audioGen;
        CancellationTokenSource audioCts;
        TrimJob lastJob;
        // cuts: pieces [start, end] that won't be in the file; cutStart — where X was pressed first
        readonly List<double[]> cuts = new List<double[]>();
        double? cutStart;
        readonly List<Rectangle> cutBoxes = new List<Rectangle>();
        readonly List<Border> cutX = new List<Border>();
        // undo and redo: start, end, cuts
        class EditState { public double In, Out; public List<double[]> Cuts; }
        readonly List<EditState> undo = new List<EditState>(), redo = new List<EditState>();

        class TrackRow
        {
            public int Source;
            public string Title;
            public ToggleButton On, Solo;
            public Slider Gain;
            public TextBlock Value, Name;
        }

        T F<T>(string name) where T : class { return (T)W.FindName(name); }
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public TrimWindow(TrayApp app, Settings cfg, string source, int mixIndex)
        {
            this.app = app;
            this.cfg = cfg;
            this.source = source;
            this.mixIndex = mixIndex;
            cfg.Apply();
            W = (Window)Wpf.Load("TrimWindow.xaml");
            try { W.Icon = Icons.Window(); } catch { }

            player = F<MediaElement>("Player");
            timeline = F<Grid>("Timeline");
            overview = F<Grid>("Overview");
            overviewThumb = F<Border>("OverviewThumb");
            timeText = F<TextBlock>("TimeText");
            totalText = F<TextBlock>("TotalText");
            speedText = F<TextBlock>("SpeedText");
            selText = F<TextBlock>("SelText");
            zoomText = F<TextBlock>("ZoomText");
            playerMsg = F<TextBlock>("PlayerMsg");
            resultTitle = F<TextBlock>("ResultTitle");
            delSrcText = F<TextBlock>("DelSrcText");
            folderText = F<TextBlock>("FolderText");
            audioStatus = F<TextBlock>("AudioStatus");
            estSize = F<TextBlock>("EstSize");
            estDur = F<TextBlock>("EstDur");
            estTracks = F<TextBlock>("EstTracks");
            estFile = F<TextBlock>("EstFile");
            nameBox = F<TextBox>("NameBox");
            volume = F<Slider>("Volume");
            muteBtn = F<Button>("BtnMute");
            modeLossless = F<ToggleButton>("ModeLossless");
            modePrecise = F<ToggleButton>("ModePrecise");
            modeShare = F<ToggleButton>("ModeShare");
            tgtDiscord = F<ToggleButton>("TgtDiscord");
            tgtNitro = F<ToggleButton>("TgtNitro");
            tgtTelegram = F<ToggleButton>("TgtTelegram");
            tgtCustom = F<ToggleButton>("TgtCustom");
            tgtGif = F<ToggleButton>("TgtGif");
            var customMb = F<TextBox>("CustomMb");
            customMb.Text = cfg.ShareCustomMb.ToString();
            customMb.TextChanged += (s, e) =>
            {
                int mb;
                if (!int.TryParse(customMb.Text.Trim(), out mb) || mb < 1 || mb > 100000) return;
                cfg.ShareCustomMb = mb;
                SaveCfg();
                UpdateMode();
            };
            rebuildMix = F<ToggleButton>("RebuildMix");
            F<TextBlock>("FileText").Text = Path.GetFileName(source);
            F<ItemsControl>("Steps").ItemsSource = steps;
            Directory.CreateDirectory(tempDir);

            // mode and share target — as last time
            modeLossless.IsChecked = cfg.TrimMode != "precise" && cfg.TrimMode != "share";
            modePrecise.IsChecked = cfg.TrimMode == "precise";
            modeShare.IsChecked = cfg.TrimMode == "share";
            tgtDiscord.IsChecked = cfg.TrimTarget != "nitro" && cfg.TrimTarget != "telegram" && cfg.TrimTarget != "custom" && cfg.TrimTarget != "gif";
            tgtCustom.IsChecked = cfg.TrimTarget == "custom";
            tgtGif.IsChecked = cfg.TrimTarget == "gif";
            tgtNitro.IsChecked = cfg.TrimTarget == "nitro";
            tgtTelegram.IsChecked = cfg.TrimTarget == "telegram";
            // the toggles work as "one of"
            Group(new[] { modeLossless, modePrecise, modeShare }, () =>
            {
                cfg.TrimMode = Mode == TrimMode.Precise ? "precise" : Mode == TrimMode.Share ? "share" : "lossless";
                SaveCfg();
                UpdateMode();
            });
            Group(new[] { tgtDiscord, tgtNitro, tgtTelegram, tgtCustom, tgtGif }, () =>
            {
                cfg.TrimTarget = Target == ShareTarget.Nitro ? "nitro" : Target == ShareTarget.Telegram ? "telegram" : Target == ShareTarget.Custom ? "custom"
                               : Target == ShareTarget.Gif ? "gif" : "discord";
                SaveCfg();
                UpdateMode();
            });
            InitShare();
            rebuildMix.Click += (s, e) => { rebuildTouched = true; AudioChanged(); };

            F<Button>("BtnMin").Click += (s, e) => W.WindowState = WindowState.Minimized;
            F<Button>("BtnMax").Click += (s, e) => W.WindowState = W.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            F<Button>("BtnClose").Click += (s, e) => W.Close();
            F<Button>("BtnKeys").Click += (s, e) => ShowKeys(F<Grid>("KeysOverlay").Visibility != Visibility.Visible);
            F<Grid>("KeysOverlay").MouseLeftButtonDown += (s, e) => ShowKeys(false);
            W.StateChanged += (s, e) =>
            {
                // a window without the system frame overflows the screen by the frame width when maximized — compensate
                bool max = W.WindowState == WindowState.Maximized;
                F<Grid>("Root").Margin = max ? new Thickness(7) : new Thickness(0);
                F<Button>("BtnMax").Content = max ? "" : "";
            };

            // controls
            F<Button>("BtnPlay").Click += (s, e) => TogglePlay(false);
            F<Button>("BtnPlaySel").Click += (s, e) => TogglePlay(true);
            F<Button>("BtnBack").Click += (s, e) => Seek(pos - 5);
            F<Button>("BtnFwd").Click += (s, e) => Seek(pos + 5);
            F<Button>("BtnFrameBack").Click += (s, e) => StepFrames(-1);
            F<Button>("BtnFrameFwd").Click += (s, e) => StepFrames(1);
            F<Button>("BtnIn").Click += (s, e) => MarkIn();
            F<Button>("BtnOut").Click += (s, e) => MarkOut();
            F<Button>("BtnCut").Click += (s, e) => ToggleCut();
            F<Button>("BtnZoomIn").Click += (s, e) => ZoomAt(ZoomAnchor(), 1.6);
            F<Button>("BtnZoomOut").Click += (s, e) => ZoomAt(ZoomAnchor(), 1 / 1.6);
            F<Button>("BtnZoomFit").Click += (s, e) => ZoomFit();
            lanesBtn = F<ToggleButton>("BtnLanes");
            lanesBtn.Click += (s, e) => SetLanes(lanesBtn.IsChecked == true);
            muteBtn.Click += (s, e) => { muted = !muted; ApplyVolume(); };
            volume.Value = Math.Max(0, Math.Min(100, cfg.TrimVolume));
            volume.ValueChanged += (s, e) =>
            {
                cfg.TrimVolume = (int)Math.Round(volume.Value);
                if (volume.Value > 0) muted = false;
                ApplyVolume();
            };
            ApplyVolume();
            noteTimer.Tick += (s, e) => ShowNote(null, false);

            // saving
            F<Button>("BtnSave").Click += (s, e) => Save();
            F<Button>("BtnFolder").Click += (s, e) => PickFolder();
            F<Button>("BtnFolderSrc").Click += (s, e) => { cfg.TrimFolder = ""; SaveCfg(); UpdateFolder(); };
            F<Button>("BtnShow").Click += (s, e) => { if (lastJob != null) Shell.Select(lastJob.Output); };
            F<Button>("BtnCopy").Click += (s, e) =>
            {
                if (lastJob != null && Shell.CopyFile(lastJob.Output) && app != null)
                    app.ShowToast(L.T("✓ File copied — paste it into Discord or Telegram (Ctrl+V)", "✓ Файл скопирован — вставь его в Discord или Telegram (Ctrl+V)"));
            };
            F<Button>("BtnAgain").Click += (s, e) => ShowExport();
            QuickShare(F<Button>("QuickDiscord"), tgtDiscord);
            QuickShare(F<Button>("QuickNitro"), tgtNitro);
            QuickShare(F<Button>("QuickTelegram"), tgtTelegram);
            F<Button>("BtnDelSrc").Click += (s, e) => DeleteSource();
            nameBox.TextChanged += (s, e) => UpdateEstimate();

            BuildTimeline();
            BuildOverview();
            BuildKeys();
            player.MediaOpened += (s, e) =>
            {
                playerMsg.Visibility = Visibility.Collapsed;
                if (swapping)
                {
                    // switched to the light copy — continue from the same place
                    swapping = false;
                    player.Position = TimeSpan.FromSeconds(pos);
                    if (playing) { player.Play(); player.SpeedRatio = speed; } else player.Pause();
                    HoldSync();
                    ShowNote(L.T("smooth preview · 720p", "плавный просмотр · 720p"), true);
                    return;
                }
                player.Pause();
                player.Position = TimeSpan.Zero;
            };
            player.MediaFailed += (s, e) =>
            {
                playerMsg.Text = L.T("Preview unavailable (", "Предпросмотр недоступен (") + e.ErrorException.Message + L.T("). You can still trim — using the frame strip.", "). Обрезать всё равно можно — по шкале кадров.");
                playerMsg.Visibility = Visibility.Visible;
            };
            audio.MediaOpened += (s, e) =>
            {
                if (closed) { audio.Close(); return; }
                audio.Position = TimeSpan.FromSeconds(pos);
                HoldSync();
                if (playing) { audio.Play(); audio.SpeedRatio = speed; }
            };
            audioDebounce.Tick += (s, e) => { audioDebounce.Stop(); BuildAudio(); };
            tick.Tick += (s, e) => OnTick();
            W.PreviewKeyDown += OnKey;
            W.Tag = Wpf.Capturable;
            W.SourceInitialized += (s, e) => { Wpf.ModernFrame(W); Wpf.SetCaptureHidden(W, cfg.HideFromCapture); };
            Wpf.RestoreBounds(W, cfg.TrimBounds);
            if (W.WindowState == WindowState.Maximized)
            {
                F<Grid>("Root").Margin = new Thickness(7);
                F<Button>("BtnMax").Content = "";
            }
            W.Closing += (s, e) =>
            {
                if (!testRun)
                {
                    string b = Wpf.SaveBounds(W);
                    if (b != null) cfg.TrimBounds = b;
                    SaveCfg();   // window size and preview volume
                }
                // audio may still be building — after closing it must neither open nor play
                closed = true;
                playing = false;
                audioDebounce.Stop();
                cts.Cancel();
                if (audioCts != null) audioCts.Cancel();
                tick.Stop();
                try { player.Stop(); player.Close(); audio.Stop(); audio.Close(); } catch { }
            };
            W.Closed += (s, e) => { try { Directory.Delete(tempDir, true); } catch { } };
            W.Loaded += (s, e) => Load();
            UpdateFolder();
            InitClips();
        }

        // a share tile: "Share" mode with this target (remembered like a choice by hand), save, then copy the file
        void QuickShare(Button b, ToggleButton target)
        {
            b.Click += (s, e) =>
            {
                if (busy || info == null) return;
                foreach (var m in new[] { modeLossless, modePrecise, modeShare }) m.IsChecked = m == modeShare;
                foreach (var t in new[] { tgtDiscord, tgtNitro, tgtTelegram, tgtCustom, tgtGif }) t.IsChecked = t == target;
                cfg.TrimMode = "share";
                cfg.TrimTarget = Target == ShareTarget.Nitro ? "nitro" : Target == ShareTarget.Telegram ? "telegram" : "discord";
                SaveCfg();
                UpdateMode();
                copyWhenDone = true;
                Save();
                if (!busy) copyWhenDone = false;   // did not start (no tracks enabled)
            };
        }

        static void Group(ToggleButton[] items, Action changed)
        {
            foreach (var t in items)
            {
                var me = t;
                me.Click += (s, e) =>
                {
                    foreach (var o in items) o.IsChecked = o == me;
                    changed();
                };
            }
        }

        void SaveCfg()
        {
            if (testRun) return;   // test runs and previews don't change the real settings
            try { cfg.Save(); } catch (Exception ex) { Log.Write("settings not saved: " + ex.Message); }
        }

        static string WaveText { get { return L.T("waveform: what goes into the file", "волна: то, что будет в файле"); } }
        static string HearText { get { return L.T("you hear what goes into the file", "слышно то, что будет в файле"); } }

        // ── loading ──
        void Load()
        {
            string src = source;
            int gen = clipGen;
            Task.Factory.StartNew(() => Ffmpeg.Info(src)).ContinueWith(t => W.Dispatcher.BeginInvoke(new Action(() =>
            {
                if (closed || gen != clipGen) return;   // another clip was opened meanwhile
                if (t.IsFaulted)
                {
                    playerMsg.Text = L.T("Could not read the file: ", "Не удалось прочитать файл: ") + t.Exception.GetBaseException().Message;
                    return;
                }
                Ready(t.Result);
                // heavy video is watched via a light copy (if it already exists — right away)
                string proxy = null;
                try { proxy = ProxyPath(); } catch { }
                bool cached = proxy != null && File.Exists(proxy);
                try { player.Source = new Uri(cached ? proxy : src); player.Play(); player.Pause(); } catch { }
                if (cached) ShowNote(L.T("smooth preview · 720p", "плавный просмотр · 720p"), true);
                tick.Start();
                LoadPictures();
                BuildAudio();
                if (!cached && proxy != null && NeedsProxy()) MakeProxy(proxy);
            })));
        }

        void Ready(Ffmpeg.MediaInfo i)
        {
            info = i;
            var titles = app != null ? app.Guard.TrackTitles : null;
            var a = info.Audio;
            for (int k = 0; titles != null && k < a.Count && k < titles.Length; k++)
                if (string.IsNullOrEmpty(a[k].Title) && !string.IsNullOrEmpty(titles[k])) a[k].Title = titles[k];
            duration = Math.Max(0.5, info.Duration);
            fps = info.Fps > 1 ? info.Fps : 60;
            inT = 0;
            outT = duration;
            viewStart = 0;
            viewLen = duration;
            meta = Trimmer.MetaOf(source, info, app != null ? app.Guard.ClipsRoot : null);
            nameBox.Text = Trimmer.DefaultTitle(meta);
            BuildTracks();
            BuildLanes();
            UpdateMode();
            if (cfg.TrimLanes) SetLanes(true);
            Layout();
        }
    }
}
