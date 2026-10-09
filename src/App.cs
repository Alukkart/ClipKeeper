using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using WF = System.Windows.Forms;

namespace DeviceGuard
{
    // The tray icon and all windows. Everything here runs on the UI thread (WPF Dispatcher).
    class TrayApp
    {
        readonly Dispatcher disp = Dispatcher.CurrentDispatcher;
        readonly WF.NotifyIcon tray;
        TrayMenu trayMenu;
        readonly System.Drawing.Icon[] icons;
        readonly DispatcherTimer refresh = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        // the first update check a couple of minutes after start (not to slow it down), then once a day
        readonly DispatcherTimer updateTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(2) };
        // the automatic cleanup looks once an hour whether a day has passed since its last run
        readonly DispatcherTimer cleanupTimer = new DispatcherTimer { Interval = TimeSpan.FromHours(1) };
        // "Start ClipKeeper together with OBS": while OBS is open the change waits — looked at every 5 s until OBS closes
        readonly DispatcherTimer obsScriptTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        public ObsScript.State ObsScriptState = ObsScript.State.Off;
        public string ObsScriptError;
        bool obsScriptBusy;
        bool cleanupBusy;
        string updateToasted;      // the version the "update available" toast was shown for
        bool updateBusy;
        public readonly Settings Cfg;
        public readonly Guard Guard;
        public bool Exiting;
        public Hotkeys Keys;
        AlertWindow alert;
        MainWindow main;
        int lastLevel = -1;

        public TrayApp(string[] args)
        {
            Cfg = Settings.Load(Path.Combine(Program.Dir, "settings.json"));
            Cfg.Apply();
            // find the video encoder in the background so the first trim does not wait for it
            System.Threading.Tasks.Task.Factory.StartNew(() => Encoders.Detected);
            icons = new[] { Wpf.Ok, Wpf.Warn, Wpf.Bad, Wpf.Grey }.Select(Icons.Tray).ToArray();

            // the icon menu is our own ClipKeeper-styled window (TrayMenu), not the standard Windows menu
            tray = new WF.NotifyIcon { Icon = icons[3], Text = "ClipKeeper", Visible = true };
            tray.MouseUp += (s, e) =>
            {
                if (e.Button == WF.MouseButtons.Right) ShowTrayMenu();
                else if (e.Button == WF.MouseButtons.Left) ShowMain(false);
            };
            ListenShowRequests();

            Guard = new Guard(Program.Dir, Cfg, this);
            SystemEvents.DisplaySettingsChanged += OnDisplayChanged;
            SystemEvents.SessionEnding += OnSessionEnding;

            try { Keys = new Hotkeys(); ApplyHotkeys(); } catch (Exception ex) { Log.Write("hotkeys: " + ex.Message); }
            refresh.Tick += (s, e) => RefreshUi();
            refresh.Start();
            updateTimer.Tick += (s, e) =>
            {
                updateTimer.Interval = TimeSpan.FromHours(24);
                if (Cfg.UpdateCheck && !Updates.LocalBuild) CheckUpdates(false);
            };
            updateTimer.Start();
            cleanupTimer.Tick += (s, e) => AutoCleanup();
            cleanupTimer.Start();
            obsScriptTimer.Tick += (s, e) => SyncObsScript();
            if (Cfg.ObsStartScript || File.Exists(ObsScript.ScriptPath)) SyncObsScript();   // the exe path in the script, a change still waiting
            // a minute without a crash: the update (if there was one) is kept
            var confirm = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
            confirm.Tick += (s, e) => { confirm.Stop(); Updates.Confirm(); };
            confirm.Start();
            string failed = Updates.TakeFailed();
            if (failed != null)
            {
                Cfg.UpdateFailed = failed;
                try { Cfg.Save(); } catch (Exception ex) { Log.Write("settings not saved: " + ex.Message); }
                ShowNotice(L.T("The update did not start", "Обновление не запустилось"), new List<string>
                {
                    L.T("Version ", "Версия ") + failed + L.T(" crashed on start, so ClipKeeper went back to ", " упала при запуске, поэтому ClipKeeper вернулся к ") + Program.Version + ".",
                    L.T("Details are in ClipKeeper.log. This version won't be offered by itself again.", "Подробности в ClipKeeper.log. Сама эта версия больше предлагаться не будет."),
                }, false);
            }
            Guard.Start();

            if (!Cfg.SetupDone || !args.Contains("--tray")) ShowMain(false);
        }

        void ShowTrayMenu()
        {
            if (trayMenu == null) trayMenu = new TrayMenu(this);
            trayMenu.Open(Guard.GetSnapshot());
        }

        // launching the exe again does not say "already running" but asks this copy to open its window
        public const string ShowEventName = @"Local\ClipKeeperShow";

        // "ClipKeeper.exe --exit" (the setup, the uninstaller) asks this copy to exit
        public const string ExitEventName = @"Local\ClipKeeperExit";

        void ListenShowRequests()
        {
            EventWaitHandle ev, exit;
            try
            {
                ev = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
                exit = new EventWaitHandle(false, EventResetMode.AutoReset, ExitEventName);
            }
            catch (Exception ex) { Log.Write("\"open window\" signal: " + ex.Message); return; }
            new Thread(() =>
            {
                while (ev.WaitOne()) Post(() => ShowMain(false));
            }) { IsBackground = true, Name = "ClipKeeper-show" }.Start();
            new Thread(() =>
            {
                exit.WaitOne();
                Log.Write("asked to exit (setup or uninstall)");
                Post(Exit);
            }) { IsBackground = true, Name = "ClipKeeper-exit" }.Start();
        }

        void OnDisplayChanged(object s, EventArgs e) { Guard.NotifyChange(); }
        void OnSessionEnding(object s, SessionEndingEventArgs e)
        {
            Updates.Confirm();   // the PC shuts down within the first minute after an update — that is not a crash
            Guard.SessionEnding();
        }

        public void Post(Action a)
        {
            try { disp.BeginInvoke(a); } catch { }
        }

        void RefreshUi()
        {
            var snap = Guard.GetSnapshot();
            if (snap.Level != lastLevel) { tray.Icon = icons[snap.Level]; lastLevel = snap.Level; }
            string t = "ClipKeeper: " + snap.Status;
            tray.Text = t.Length > 63 ? t.Substring(0, 60) + "…" : t;
            if (trayMenu != null && trayMenu.IsVisible) trayMenu.SetStatus(snap);
            if (main != null && main.W.IsVisible) main.Update(snap);
        }

        public void ShowMain(bool focusPassword, int page = -1)
        {
            if (main == null)
            {
                main = new MainWindow(this, Cfg);
                try { main.W.Icon = Icons.Window(); } catch { }
            }
            var snap = Guard.GetSnapshot();
            main.Update(snap);
            // the library opens; if recording has a problem — straight to "Recording"
            if (page >= 0) main.ShowPage(page);
            else if (snap.Level == 2) main.ShowPage(MainWindow.PageRecord);
            else if (main.CurrentPage < 0) main.ShowPage(MainWindow.PageClips);
            main.W.Show();
            if (main.W.WindowState == WindowState.Minimized) main.W.WindowState = WindowState.Normal;
            main.W.Activate();
            if (!Cfg.SetupDone) main.ShowSetup();
            else if (focusPassword && !main.SetupVisible) main.FocusPassword();
        }

        // ── windows over the game ───────────────────────────────────────────
        public void ShowAlert(string title, string body, bool restartBtn, bool isNew)
        {
            if (isNew)
            {
                string tip = body.Split('\n')[0];
                tray.ShowBalloonTip(10000, title, tip.Length > 200 ? tip.Substring(0, 197) + "…" : tip, WF.ToolTipIcon.Warning);
            }
            if (!Cfg.AlertWindow)
            {
                if (isNew && Cfg.AlertSound) AlertWindow.PlayOnce(Cfg);
                return;
            }
            if (alert == null)
            {
                if (!isNew) return;   // the window was closed with "Got it" — don't show it until a new problem
                var a = new AlertWindow(this, AlertKind.Alarm, 0);
                a.Closed += (s, e) => { if (alert == a) alert = null; };
                alert = a;
            }
            alert.SetContent(title, body, restartBtn, isNew);
        }

        public void CloseAlert()
        {
            if (alert != null) alert.FadeClose();
            alert = null;
        }

        // the next card goes under the alarm window (windows have 28 px margins for the shadow)
        double BelowAlert()
        {
            if (alert != null && alert.IsVisible) return alert.Top + alert.ActualHeight - 46;
            return -1;
        }

        static string Bullets(List<string> lines) { return string.Join("\n", lines.Select(l => "• " + l)); }

        public void ShowOk(string title, List<string> lines)
        {
            new AlertWindow(this, AlertKind.Ok, 7) { TopY = BelowAlert() }.SetContent(title, Bullets(lines), false, false);
        }

        public void ShowNotice(string title, List<string> lines, bool sound)
        {
            new AlertWindow(this, AlertKind.Notice, 15) { TopY = BelowAlert() }.SetContent(title, Bullets(lines), false, sound);
        }

        public void ShowToast(string text)
        {
            ShowToast(text, null, null);
        }

        public void ShowToast(string text, string hint, Action click)
        {
            new AlertWindow(this, AlertKind.Toast, click != null ? 6 : 4) { Click = click }.SetContent(text, hint, false, false);
        }

        // clip saved: the card (ClipCard) and the sound, each by its own setting; a clip with a problem is always shown
        // (and sounds like the alarm). The gallery updates itself from the state snapshot
        public void ClipSaved(ClipInfo clip)
        {
            // the sound already played at the press; each press answers for one clip (two quick saves — two clips)
            bool confirmed = pressesWaiting > 0 && (DateTime.Now - pressedAt).TotalSeconds < 60;
            if (pressesWaiting > 0) pressesWaiting--;
            if (pressesWaiting == 0) pressedAt = DateTime.MinValue;
            if (!clip.Ok || Cfg.ClipToast) ClipCard.Pop(this, clip);
            if (clip.Ok && !confirmed) ClipSound();
            if (main != null) main.SetupClipEvent(true, clip);   // the test clip of the first-run setup
        }

        // the OBS save key was pressed (SaveKey): the sound and a "Saving the clip…" card right away; the clip card replaces it
        // once OBS has written the file. Nothing from OBS in 30 s — the card says so
        DateTime pressedAt = DateTime.MinValue;   // the last press
        int pressesWaiting;                       // presses whose clip has not come yet

        public void SavePressed()
        {
            if ((DateTime.Now - pressedAt).TotalSeconds < 1) return;   // one press, not a key repeat
            var at = pressedAt = DateTime.Now;
            pressesWaiting++;
            ClipSound();
            if (Cfg.ClipToast) ClipCard.Saving(this);
            if (main != null) main.SetupClipEvent(false, null);
            var wait = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
            wait.Tick += (s, e) =>
            {
                wait.Stop();
                if (pressedAt != at) return;   // the clips came (or a newer press)
                pressedAt = DateTime.MinValue;
                pressesWaiting = 0;
                Log.Write("save key pressed, but OBS reported no clip in 30 s");
                ClipCard.NotSaved();
            };
            wait.Start();
        }

        // the "Clip saved" sound; not while an alarm is on screen or just sounded — Windows would cut the alarm sound off
        public void ClipSound()
        {
            if (!Cfg.ClipSound || (alert != null && alert.IsVisible) || (DateTime.Now - Alarm.AlarmAt).TotalSeconds < 6) return;
            Alarm.PlayOnce(Cfg.ClipSoundFile, Cfg.ClipSoundVolume, true);
        }

        public void TestAlert()
        {
            new AlertWindow(this, AlertKind.Alarm, 10).SetContent(L.T("Alarm test", "Тест тревоги"),
                L.T("• This is what an alarm looks like: it blinks, sounds and does not steal focus from the game.\n" +
                    "• This window is not captured in the OBS recording.\n• It closes by itself in 10 seconds.",
                    "• Так выглядит тревога: мигает, звучит и не забирает фокус у игры.\n" +
                    "• В запись OBS это окно не попадает.\n• Закроется само через 10 секунд."), false, true);
            // what "Clip saved" looks like: the newest clip if there is one
            string root = Guard.ClipsRoot;
            var newest = string.IsNullOrEmpty(root) ? null : ClipScanner.Scan(root, 1).FirstOrDefault();
            if (newest != null)
            {
                string g = ClipScanner.GameOf(newest, root.TrimEnd('\\'));
                var c = ClipInfo.Of(newest.FullName, NoGame.Is(g) || !Covers.IsGame(g) ? null : g);
                c.Duration = ClipIndex.Duration(newest);
                ClipCard.Pop(this, c);   // the card even if it is off; no clip sound — the test sounds like the alarm
            }
            else ShowToast(L.T("✓ This is what \"Clip saved\" looks like", "✓ Так выглядит «Клип сохранён»"));
        }

        public void RequestRestartObs() { Guard.RequestRestartObs(); }

        public void OpenTrim(string path) { OpenTrim(path, null); }

        // one editor window for all clips: an open one takes the clip (list — what the library shows, to go through with ← →)
        TrimWindow trim;

        public void OpenTrim(string path, List<string> list)
        {
            if (!Ffmpeg.Available)
            {
                ShowNotice(L.T("No ffmpeg", "Нет ffmpeg"), new List<string> { L.T("Put ffmpeg.exe and ffprobe.exe into the ffmpeg folder next to ClipKeeper.exe", "Положи ffmpeg.exe и ffprobe.exe в папку ffmpeg рядом с ClipKeeper.exe") }, false);
                return;
            }
            if (trim != null && trim.W.IsLoaded) { trim.Open(path, list); return; }
            var w = new TrimWindow(this, Cfg, path, Guard.MixAudioIndex());
            w.W.Closed += (s, e) => { if (trim == w) trim = null; };
            trim = w;
            if (list != null) w.Open(path, list);
            w.W.Show();
        }

        public void ClipsChanged()
        {
            if (main != null) main.LoadClips(true);
        }

        // ── global hotkeys: the last clip from the game ─────────────────────
        // (re)registers from the settings; paused while a combination is being set in Settings
        public void ApplyHotkeys(bool paused = false)
        {
            if (Keys == null) return;
            Keys.Clear();
            if (paused || !Cfg.LibraryEnabled) return;
            Keys.Add("HotkeyTrim", Cfg.HotkeyTrim, () => WithLastClip(OpenTrim));
            Keys.Add("HotkeyFav", Cfg.HotkeyFav, () => WithLastClip(p =>
            {
                bool on = Favorites.Toggle(p);
                ShowToast((on ? L.T("★ In favorites: ", "★ В избранном: ") : L.T("☆ Not in favorites: ", "☆ Убран из избранного: ")) + Path.GetFileName(p));
                ClipsChanged();
            }));
            Keys.Add("HotkeyCopy", Cfg.HotkeyCopy, () => WithLastClip(p =>
            {
                if (Shell.CopyFile(p)) ShowToast(L.T("✓ Copied — Ctrl+V into Discord or Telegram", "✓ Скопирован — Ctrl+V в Discord или Telegram"));
            }));
            Keys.Add("HotkeyCard", Cfg.HotkeyCard, () => WithLastClip(p =>
            {
                string root = Guard.ClipsRoot;
                string g = root != null ? ClipScanner.GameOf(new FileInfo(p), root.TrimEnd('\\')) : null;
                var c = ClipInfo.Of(p, g == null || NoGame.Is(g) || !Covers.IsGame(g) ? null : g);
                c.Duration = ClipIndex.Duration(new FileInfo(p));
                ClipCard.Pop(this, c);
            }));
        }

        // the clip saved last: the one ClipKeeper saw being saved, otherwise the newest in the recording folder
        void WithLastClip(Action<string> act)
        {
            string p = Guard.LastClipPath;
            if (p == null || !File.Exists(p))
            {
                string root = Guard.ClipsRoot;
                var newest = string.IsNullOrEmpty(root) ? null : ClipScanner.Scan(root, 1).FirstOrDefault();
                p = newest != null ? newest.FullName : null;
            }
            if (p == null) { ShowToast(L.T("No clips yet", "Клипов пока нет")); return; }
            act(p);
        }

        // ── "Start ClipKeeper together with OBS" ────────────────────────────
        // brings OBS in line with the setting (in the background: it reads the scene collections) and refreshes the settings row
        public void SyncObsScript()
        {
            if (obsScriptBusy) return;
            obsScriptBusy = true;
            bool on = Cfg.ObsStartScript;
            System.Threading.Tasks.Task.Factory.StartNew(() =>
            {
                string err = ObsScript.Sync(on);
                return Tuple.Create(err, ObsScript.Status(on));
            }).ContinueWith(t => Post(() =>
            {
                obsScriptBusy = false;
                if (t.Exception != null) { ObsScriptState = ObsScript.State.Error; ObsScriptError = t.Exception.InnerException.Message; }
                else
                {
                    string err = t.Result.Item1;
                    ObsScriptError = err != null && err != ObsScript.WaitObs ? err : null;
                    ObsScriptState = ObsScriptError != null ? ObsScript.State.Error : t.Result.Item2;
                }
                if (ObsScriptState == ObsScript.State.Pending) obsScriptTimer.Start(); else obsScriptTimer.Stop();
                if (on != Cfg.ObsStartScript) { SyncObsScript(); return; }   // switched again meanwhile
                if (main != null) main.ObsScriptChanged();
            }));
        }

        // ── cleanup of old clips ────────────────────────────────────────────
        // off the UI thread: walks the folder and reads clip data
        public Cleanup.Plan PlanCleanup()
        {
            return Cleanup.Make(Guard.ClipsRoot, Cfg.CleanupDays, Cfg.CleanupKeepTrimmed, new[] { Cfg.TrimFolder, Cfg.CollectionFolder },
                                ClipIndex.MetaStrict, f => Trimmer.ReadMeta(Ffmpeg.Info(f.FullName)), Favorites.Has, Ffmpeg.Available, DateTime.Now);
        }

        public static DateTime ParseRun(string s)
        {
            DateTime t;
            return DateTime.TryParseExact(s ?? "", "yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture,
                                          System.Globalization.DateTimeStyles.None, out t) ? t : DateTime.MinValue;
        }

        void AutoCleanup()
        {
            if (!Cfg.CleanupAuto || !Cfg.LibraryEnabled || cleanupBusy) return;
            if ((DateTime.Now - ParseRun(Cfg.CleanupLastRun)).TotalHours < 24) return;
            cleanupBusy = true;
            bool ran = false;   // a refused plan (OBS not connected yet…) does not use up the day: the next hour tries again
            System.Threading.Tasks.Task.Factory.StartNew(() =>
            {
                var plan = PlanCleanup();
                if (plan.Refusal != null) { Log.Write("auto cleanup skipped: " + plan.Refusal); return null; }
                ran = true;
                if (plan.Files.Count == 0) return null;
                if (Cleanup.TooMuchForAuto(plan))
                {
                    Log.Write("auto cleanup stopped: " + plan.Files.Count + " of " + plan.Total + " clips would go");
                    Post(() => ShowNotice(L.T("Cleanup stopped", "Уборка остановлена"), new List<string>
                    {
                        plan.Files.Count + L.T(" of ", " из ") + plan.Total + L.T(" clips would go to the Recycle Bin — that is too many to do by itself.",
                                                                                  " клипов ушли бы в корзину — слишком много, чтобы делать это самому."),
                        L.T("Nothing was moved. Check the list in Settings → Library → Cleanup.", "Ничего не перемещено. Проверь список в «Настройки → Библиотека → Уборка»."),
                    }, false));
                    return null;
                }
                return Cleanup.Run(plan, Favorites.Has, true);
            }).ContinueWith(t => Post(() =>
            {
                cleanupBusy = false;
                if (ran)
                {
                    Cfg.CleanupLastRun = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
                    try { Cfg.Save(); } catch (Exception ex) { Log.Write("settings not saved: " + ex.Message); }
                }
                var r = t.Exception == null ? t.Result : null;
                if (t.Exception != null) Log.Write("auto cleanup: " + t.Exception.InnerException.Message);
                if (r != null && r.Stopped)
                    ShowNotice(L.T("Cleanup stopped", "Уборка остановлена"), new List<string>
                    {
                        L.T("Windows would delete clips for good instead of putting them in the Recycle Bin (it is full or turned off), so the rest stay.",
                            "Windows удалила бы клипы насовсем, а не в корзину (она полна или выключена), поэтому остальные остались."),
                    }, false);
                if (r == null || r.Moved == 0) return;
                ShowToast(L.T("🗑 ", "🗑 ") + r.Moved + L.T(" old clips (", " старых клипов (") + Fmt.Size(r.Bytes) + L.T(") went to the Recycle Bin", ") ушли в корзину"),
                          L.T("list", "список"), () => Shell.Open(Cleanup.LogPath));
                ClipsChanged();
            }));
        }

        // ── updates ─────────────────────────────────────────────────────────
        public bool UpdateBusy { get { return updateBusy; } }

        // manual: from the button in Settings — the result is shown there, no toast
        public void CheckUpdates(bool manual)
        {
            if (updateBusy) return;
            updateBusy = true;
            UpdatesChanged();
            System.Threading.Tasks.Task.Factory.StartNew(() => Updates.Check()).ContinueWith(t => Post(() =>
            {
                updateBusy = false;
                var r = Updates.Newer;
                if (!manual && r != null && updateToasted != r.Version && r.Version != Cfg.UpdateFailed)
                {
                    updateToasted = r.Version;
                    ShowToast(L.T("ClipKeeper ", "Вышел ClipKeeper ") + r.Version + L.T(" is out", ""), L.T("update", "обновить"), ShowUpdates);
                }
                UpdatesChanged();
            }));
        }

        // download, check, swap the exe and restart; done(error) on the UI thread when it did not work
        public void InstallUpdate(Action<string> done)
        {
            var r = Updates.Newer;
            if (updateBusy || r == null) return;
            updateBusy = true;
            UpdatesChanged();
            System.Threading.Tasks.Task.Factory.StartNew(() => Updates.Install(r)).ContinueWith(t => Post(() =>
            {
                updateBusy = false;
                string err = t.Result;
                // the new exe is already in place; if it can't be started, say so instead of doing nothing
                if (err == null && !Restart())
                    err = L.T("the new version is in place but did not start (details in ClipKeeper.log) — exit ClipKeeper and start it again",
                              "новая версия на месте, но не запустилась (подробности в ClipKeeper.log) — выйди из ClipKeeper и запусти его снова");
                if (err == null) return;
                UpdatesChanged();
                done(err);
            }));
        }

        public void ShowUpdates()
        {
            ShowMain(false, MainWindow.PageSettings);
            main.ShowSettingsRow(MainWindow.TabAbout, MainWindow.RowUpdates);
        }

        void UpdatesChanged()
        {
            if (trayMenu != null && !trayMenu.IsVisible) { trayMenu.Close(); trayMenu = null; }   // the menu gets an "Update" item
            if (main != null) main.UpdatesChanged();
        }

        // a module was turned on or off: the tray menu is rebuilt with the right items on its next opening
        public void ModulesChanged()
        {
            if (trayMenu != null) { trayMenu.Close(); trayMenu = null; }
            ApplyHotkeys();   // the clip hotkeys belong to the library
            Guard.RequestCheck();
        }

        // restart to apply the interface language or an update: the new copy waits for this one to exit (--after in Program).
        // false — the new copy did not start, this one keeps running
        public bool Restart()
        {
            try
            {
                using (var me = System.Diagnostics.Process.GetCurrentProcess())
                    System.Diagnostics.Process.Start(System.Windows.Forms.Application.ExecutablePath, "--after " + me.Id);
            }
            catch (Exception ex) { Log.Write("restart: " + ex.Message); return false; }
            Exit();
            return true;
        }

        public void Exit()
        {
            Exiting = true;
            Updates.Confirm();   // a normal exit: the new version (if just updated) works
            if (Keys != null) Keys.Dispose();
            refresh.Stop();
            updateTimer.Stop();
            cleanupTimer.Stop();
            obsScriptTimer.Stop();
            SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;
            SystemEvents.SessionEnding -= OnSessionEnding;
            Guard.Stop();
            CloseAlert();
            if (trayMenu != null) trayMenu.Close();
            if (main != null) main.W.Close();
            tray.Visible = false;
            tray.Dispose();
            Application.Current.Shutdown();
        }
    }
}
