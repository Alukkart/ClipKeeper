using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Win32;

namespace DeviceGuard
{
    class Snapshot
    {
        public int Level;                // 0 ok, 1 attention, 2 alarm, 3 OBS not running
        public string Status;            // one line — for the tray icon
        public bool Connected;
        public string ObsStatus, Endpoint;
        public List<string> Due = new List<string>(), Pending = new List<string>(), Notes = new List<string>();
        public HashSet<string> ProblemKeys = new HashSet<string>(), NoteKeys = new HashSet<string>();
        public List<string> DueKeys = new List<string>(), PendingKeys = new List<string>(), NotesKeys = new List<string>();   // same order as Due, Pending, Notes
        public List<KeyValuePair<string, RefEntry>> Refs = new List<KeyValuePair<string, RefEntry>>();
        public List<string> Events = new List<string>();
        public DateTime LastCheck;
        public int RbState = -1;         // -1 unknown, 0 turned off by hand, 1 recording, 2 crashed
        public string RbInfo, DiskRoot, ClipText, ClipNote, ClipsRoot, LastClipPath;
        public double DiskFreeGb = -1;
        public DateTime ClipAt;
        public bool ClipOk;
        public int PerfLevel = -1;       // -1 no data, 0 no dropped frames, 1 dropping
        public string PerfValue, PerfSub;
    }
    // All the watching logic. Runs on its own thread and reaches the UI only via app.Post.
    // Events (Ev) are shown in the window, so they are in the interface language; log-only lines are English.
    partial class Guard
    {
        class Problem { public DateTime Since; public string Msg; public bool Urgent; }

        const int ReqTimeout = 5000;

        public readonly Settings Cfg;
        public readonly Sorter Sorter;
        readonly TrayApp app;
        readonly string refsPath;
        readonly RefStore refs;

        Thread worker;
        readonly AutoResetEvent trigger = new AutoResetEvent(false);
        volatile bool stopping, deviceChanged, pendingSave, pendingRestart, reconnect, sessionEnding;
        readonly ConcurrentQueue<KeyValuePair<string, Dictionary<string, object>>> obsEvents =
            new ConcurrentQueue<KeyValuePair<string, Dictionary<string, object>>>();

        ObsClient obs;
        Process obsProc;
        AudioDevices audio;

        // OBS state
        bool exitStarted, plannedExit, lostUnexpected, authFailed, authPrompted;
        DateTime lostAt, connectedAt, lastConnectTry = DateTime.MinValue, lastLaunch = DateTime.MinValue,
                 obsSeenSince = DateTime.MinValue;
        string wsError, lastObsExe;
        volatile string restartWhy;
        int timeouts;
        readonly List<DateTime> launches = new List<DateTime>();

        // replay buffer
        bool rbStopRequested, rbCrashed, rbManualOff;
        DateTime rbCrashAt, rbLastTry = DateTime.MinValue;
        int rbTries;
        readonly List<DateTime> rbCrashes = new List<DateTime>();

        // monitors
        string monSig;
        bool monKick;
        readonly HashSet<string> missingLogged = new HashSet<string>();

        // problems and notifications
        readonly object st = new object();
        readonly Dictionary<string, Problem> problems = new Dictionary<string, Problem>();
        readonly List<string> events = new List<string>();
        DateTime lastCheck = DateTime.MinValue;
        string obsStatus = L.T("starting…", "запуск…");
        volatile bool connectedNow;
        // for the window: buffer state, disk, last clip
        volatile int uiRb = -1;
        string uiRbInfo, uiDiskRoot, uiClipText, uiClipNote;
        double uiDiskFree = -1;
        DateTime uiClipAt;
        bool uiClipOk;
        HashSet<string> alertKeys = new HashSet<string>();
        string alertBody = "";

        public Guard(string dir, Settings cfg, TrayApp app)
        {
            Cfg = cfg;
            this.app = app;
            refsPath = Path.Combine(dir, "devices.json");
            string imported = null;
            try
            {
                string parent = Path.GetDirectoryName(dir.TrimEnd('\\'));
                refs = RefStore.Load(refsPath, new[] {
                    Path.Combine(parent, "device_guard_config.json"),
                    Path.Combine(parent, "audio_device_guard_config.json") }, out imported);
            }
            catch (Exception ex)
            {
                refs = new RefStore();
                Ev(L.T("⚠ reference could not be read: ", "⚠ эталон не прочитан: ") + ex.Message);
            }
            if (imported != null)
            {
                Ev(L.T("reference imported from " + Path.GetFileName(imported) + " (the old file is unchanged)", "эталон импортирован из " + Path.GetFileName(imported) + " (старый файл не изменён)"));
                SaveRefs();
            }
            foreach (var kv in refs.Items) Log.Write("  reference: " + kv.Key + " -> " + kv.Value.Display);
            // the sorter reports a moved replay back here — the clip check needs its final path
            Sorter = new Sorter(cfg, Ev, (path, game) =>
            {
                obsEvents.Enqueue(new KeyValuePair<string, Dictionary<string, object>>("ClipSorted", Json.D("savedReplayPath", path, "game", game)));
                trigger.Set();
            });
        }

        // ── control from the UI ─────────────────────────────────────────────
        public void Start()
        {
            worker = new Thread(Run) { IsBackground = true, Name = "ClipKeeper" };
            worker.SetApartmentState(ApartmentState.MTA);
            worker.Start();
            StartExtras();
            Sorter.Start();
        }

        public void Stop()
        {
            stopping = true;
            trigger.Set();
            if (worker != null) worker.Join(5000);
            StopExtras();
            Sorter.Stop();
        }

        public void RequestSave() { pendingSave = true; trigger.Set(); }
        public void RequestCheck() { trigger.Set(); }
        public void RequestRestartObs() { restartWhy = null; pendingRestart = true; trigger.Set(); }
        public void Reconnect() { reconnect = true; trigger.Set(); }
        public void NotifyChange() { deviceChanged = true; trigger.Set(); }
        public void SessionEnding() { sessionEnding = true; }

        public Snapshot GetSnapshot()
        {
            lock (st)
            {
                var now = DateTime.Now;
                var s = new Snapshot
                {
                    Connected = connectedNow, ObsStatus = obsStatus, Endpoint = Cfg.Host + ":" + Cfg.Port,
                    LastCheck = lastCheck, RbState = !Cfg.UseReplayBuffer ? -2 : connectedNow ? uiRb : -1, RbInfo = uiRbInfo,
                    DiskFreeGb = uiDiskFree, DiskRoot = uiDiskRoot,
                    ClipText = uiClipText, ClipNote = uiClipNote, ClipAt = uiClipAt, ClipOk = uiClipOk,
                    ClipsRoot = ClipsRoot, LastClipPath = LastClipPath,
                    PerfLevel = connectedNow ? uiPerfLevel : -1, PerfValue = uiPerfValue, PerfSub = uiPerfSub,
                };
                foreach (var kv in problems)
                {
                    bool due = IsDue(kv.Value, now);
                    (due ? s.Due : s.Pending).Add(kv.Value.Msg);
                    (due ? s.DueKeys : s.PendingKeys).Add(kv.Key);
                    s.ProblemKeys.Add(kv.Key);
                }
                foreach (var kv in notes) { s.Notes.Add(kv.Value); s.NoteKeys.Add(kv.Key); s.NotesKeys.Add(kv.Key); }
                foreach (var kv in refs.Items)
                    s.Refs.Add(new KeyValuePair<string, RefEntry>(kv.Key, new RefEntry
                    {
                        Type = kv.Value.Type, Prop = kv.Value.Prop, Value = kv.Value.Value, Display = kv.Value.Display,
                        LastName = kv.Value.LastName, Tracks = kv.Value.Tracks, Muted = kv.Value.Muted, VolumeDb = kv.Value.VolumeDb,
                    }));
                s.Events = events.ToList();

                // the recording guard is turned off: ClipKeeper only talks to OBS for the library (the clip folder)
                if (!Cfg.GuardEnabled)
                {
                    s.Level = 3;
                    s.Status = L.T("Recording guard is off", "Наблюдение за записью выключено") +
                               (connectedNow ? L.T(" · OBS connected", " · OBS подключён") : "");
                    s.Due.Clear(); s.Pending.Clear(); s.Notes.Clear(); s.ProblemKeys.Clear(); s.NoteKeys.Clear();
                    s.DueKeys.Clear(); s.PendingKeys.Clear(); s.NotesKeys.Clear();
                }
                else if (s.Due.Count > 0) { s.Level = 2; s.Status = L.T("⚠ PROBLEM: ", "⚠ ПРОБЛЕМА: ") + string.Join("; ", s.Due.Concat(s.Pending)); }
                else if (s.Pending.Count > 0) { s.Level = 1; s.Status = L.T("Glitch, waiting for recovery: ", "Сбой, жду восстановления: ") + string.Join("; ", s.Pending); }
                else if (!connectedNow) { s.Level = 3; s.Status = obsStatus; }
                else if (refs.Items.Count == 0) { s.Level = 1; s.Status = L.T("The reference is empty — press \"Remember current devices\"", "Эталон пуст — нажмите «Запомнить текущие устройства»"); }
                else if (s.Notes.Count > 0) { s.Level = 1; s.Status = L.T("Attention: ", "Внимание: ") + string.Join("; ", s.Notes); }
                else if (rbManualOff) { s.Level = 1; s.Status = L.T("Replay buffer turned off by hand", "Буфер повтора выключен вручную"); }
                else
                {
                    s.Level = 0;
                    s.Status = L.T("✓ All good", "✓ Всё в порядке") +
                               (lastCheck != DateTime.MinValue ? L.T(" (checked ", " (проверка ") + lastCheck.ToString("HH:mm:ss") + ")" : "");
                }
                return s;
            }
        }
        bool IsDue(Problem p, DateTime now) { return p.Urgent || (now - p.Since).TotalSeconds >= Cfg.GraceSec; }

        // ObsStatus starts with this while the WebSocket is unreachable (the window shows a password hint then)
        public static string NoLinkPrefix { get { return L.T("No connection to OBS: ", "Нет связи с OBS: "); } }

        void Ev(string msg)
        {
            Log.Write(msg);
            lock (st)
            {
                events.Insert(0, DateTime.Now.ToString("HH:mm:ss") + "  " + msg);
                if (events.Count > 15) events.RemoveAt(events.Count - 1);
            }
        }

        void SaveRefs()
        {
            try { lock (st) refs.Save(refsPath); }
            catch (Exception ex) { Log.Write("devices.json not saved: " + ex.Message); }
        }

        // ── main loop ───────────────────────────────────────────────────────
        void Run()
        {
            try
            {
                audio = new AudioDevices(true);
                audio.Changed += NotifyChange;
            }
            catch (Exception ex)
            {
                audio = null;
                Ev(L.T("⚠ could not connect to the Windows audio system: ", "⚠ не удалось подключиться к аудиосистеме Windows: ") + ex.Message);
            }

            while (!stopping)
            {
                try { Tick(); }
                catch (Exception ex) { Ev(L.T("check error: ", "ошибка проверки: ") + ex.Message); Log.Write(ex.ToString()); }

                int wait = Math.Max(1, Cfg.IntervalSec) * 1000;
                if (obs == null) wait = Math.Min(wait, 3000);
                wait = Math.Min(wait, ClipWaitMs());
                trigger.WaitOne(wait);
                if (deviceChanged && !stopping)
                {
                    // GG toggles devices in bursts while updating — give it a second to finish
                    deviceChanged = false;
                    Thread.Sleep(1500);
                }
            }

            if (obs != null) obs.Dispose();
            if (audio != null) audio.Dispose();
        }

        void Tick()
        {
            var now = DateTime.Now;
            var cur = new Dictionary<string, string>();
            var urgent = new HashSet<string>();
            var fixedMsgs = new List<string>();

            if (pendingRestart) { pendingRestart = false; RestartObs(restartWhy); restartWhy = null; }
            if (reconnect)
            {
                reconnect = false;
                if (obs != null) { obs.Dispose(); obs = null; }
                lastConnectTry = DateTime.MinValue;
                authFailed = false;
                authPrompted = false;
                wsError = null;
            }

            var notesCur = new Dictionary<string, string>();
            bool guard = Cfg.GuardEnabled;
            if (guard) RunAlways(now);
            DrainEvents(now);
            bool connected = EnsureObs(now, cur, urgent, fixedMsgs);
            connectedNow = connected;
            if (!connected || exitStarted) RunUiRequests(false);
            if (connected && !exitStarted)
            {
                try
                {
                    RunUiRequests(true);
                    if (pendingSave) { pendingSave = false; SaveReference(); }
                    if (guard)
                    {
                        CheckClips(now);   // a saved clip first — the checks below can take a while (screenshots…)
                        CheckSaveKey(now);
                        CheckSources(cur, fixedMsgs);
                        DrainEvents(now);
                        CheckRender(now, cur, urgent, fixedMsgs);   // before the buffer: a buffer OBS can't feed is not restarted
                        CheckReplayBuffer(now, cur, urgent, fixedMsgs);
                        RunExtras(now, cur, urgent, fixedMsgs, notesCur);
                    }
                    else CheckDisk(now, new Dictionary<string, string>());   // only to learn the clip folder for the library
                    timeouts = 0;
                }
                catch (ObsException ex)
                {
                    if (ex.Code != 207) Ev(ex.Message);   // 207 — OBS is still loading or already closing
                    CarryOver(cur, urgent);
                }
                catch (TimeoutException ex)
                {
                    timeouts++;
                    Ev(L.T("OBS did not answer in time (", "OBS не ответил вовремя (") + timeouts + "): " + ex.Message);
                    CarryOver(cur, urgent);
                    if (timeouts >= 3) { cur["hang"] = L.T("OBS does not answer requests — it seems to be frozen", "OBS не отвечает на запросы — похоже, завис"); urgent.Add("hang"); }
                }
                catch (IOException)
                {
                    CarryOver(cur, urgent);   // the link dropped mid-check — sort it out on the next tick
                    trigger.Set();
                }
            }
            if (!guard) { cur.Clear(); urgent.Clear(); fixedMsgs.Clear(); notesCur.Clear(); }   // no alarms and notes with the guard off
            ProcessResults(now, cur, urgent, fixedMsgs);
            ProcessNotes(notesCur);
        }

        void CarryOver(Dictionary<string, string> cur, HashSet<string> urgent)
        {
            lock (st)
                foreach (var kv in problems)
                    if (!cur.ContainsKey(kv.Key))
                    {
                        cur[kv.Key] = kv.Value.Msg;
                        if (kv.Value.Urgent) urgent.Add(kv.Key);
                    }
        }

        void OnObsEvent(string type, Dictionary<string, object> data)
        {
            if (type == "InputVolumeMeters") { OnMeters(data); return; }   // ~20 times a second — no queue
            if (Sorter.Handles(type))
            {
                Sorter.OnEvent(type, data);
                if (type == "ReplayBufferSaved") return;   // comes back as ClipSorted once the file is in its folder
            }
            if (type == "ExitStarted" || type == "ReplayBufferStateChanged" || type == "ReplayBufferSaved")
            {
                obsEvents.Enqueue(new KeyValuePair<string, Dictionary<string, object>>(type, data));
                trigger.Set();
            }
        }

        void DrainEvents(DateTime now)
        {
            KeyValuePair<string, Dictionary<string, object>> e;
            while (obsEvents.TryDequeue(out e))
            {
                if (e.Key == "ExitStarted")
                {
                    exitStarted = true;
                    Ev(L.T("OBS is closing", "OBS закрывается"));
                    continue;
                }
                if (e.Key == "ReplayBufferSaved" || e.Key == "ClipSorted") { OnClipSaved(e.Value, now, e.Key == "ClipSorted"); continue; }
                string state = Json.GetStr(e.Value, "outputState") ?? "";
                if (state.EndsWith("STOPPING")) rbStopRequested = true;
                else if (state.EndsWith("STARTING") || state.EndsWith("STARTED"))
                {
                    rbStopRequested = false;
                    rbManualOff = false;
                    if (state.EndsWith("STARTED")) rbStartedAt = now;
                }
                else if (state.EndsWith("STOPPED"))
                {
                    // a failed encoder (NVENC after a driver update) also comes with STOPPING — OBS stops the output
                    // itself; only its log tells the two apart
                    string encErr = rbStopRequested && !exitStarted && !plannedExit ? ObsLog.ReplayStopError() : null;
                    if (encErr != null) Log.Write("OBS log: " + encErr);
                    if (rbStopRequested && encErr == null) { rbManualOff = true; Ev(L.T("replay buffer turned off by hand", "буфер повтора выключен вручную")); }
                    else if (!exitStarted && !plannedExit)
                    {
                        // without STOPPING it was not the user who stopped the buffer but an error (e.g. NVENC)
                        rbCrashed = true;
                        rbStopRequested = false;
                        rbCrashAt = now;
                        rbTries = 0;
                        rbLastTry = DateTime.MinValue;
                        rbCrashes.Add(now);
                        Ev(encErr != null ? L.T("⚠ the replay buffer stopped: the OBS encoder failed", "⚠ буфер повтора остановился: сбой кодировщика OBS")
                                          : L.T("⚠ the replay buffer stopped by itself", "⚠ буфер повтора остановился сам"));
                    }
                }
            }
        }

        // ── OBS: connection, crash, restart ─────────────────────────────────
        Process FindObs()
        {
            var all = System.Diagnostics.Process.GetProcessesByName("obs64");
            for (int i = 1; i < all.Length; i++) all[i].Dispose();
            return all.Length > 0 ? all[0] : null;
        }

        bool EnsureObs(DateTime now, Dictionary<string, string> cur, HashSet<string> urgent, List<string> fixedMsgs)
        {
            if (obs != null && obs.IsOpen) return true;
            if (obs != null)
            {
                obs.Dispose();
                obs = null;
                DrainEvents(now);
                bool clean = exitStarted || plannedExit || sessionEnding;
                if (!clean && obsProc != null)
                {
                    // fallback sign of a normal exit: the process ended with code 0
                    try { clean = obsProc.WaitForExit(3000) && obsProc.ExitCode == 0; } catch { }
                }
                if (obsProc != null) { obsProc.Dispose(); obsProc = null; }
                if (clean) Ev(L.T("OBS closed", "OBS закрыт"));
                else
                {
                    lostUnexpected = true;
                    lostAt = now;
                    Ev(L.T("⚠ the link to OBS dropped, but OBS was not closing", "⚠ связь с OBS оборвалась, а OBS не закрывался"));
                }
            }

            bool running;
            using (var p = FindObs())
            {
                running = p != null;
                if (running)
                {
                    try { lastObsExe = p.MainModule.FileName; } catch { }
                    if (obsSeenSince == DateTime.MinValue) obsSeenSince = now;
                }
                else obsSeenSince = DateTime.MinValue;
            }

            if (lostUnexpected && !sessionEnding)
            {
                bool recentLaunch = (now - lastLaunch).TotalSeconds < 90;
                if (!running)
                {
                    string msg = L.T("OBS crashed", "OBS аварийно закрылся");
                    if (Cfg.RestartObsOnCrash && Cfg.GuardEnabled)
                    {
                        launches.RemoveAll(t => (now - t).TotalMinutes > 15);
                        if (launches.Count >= 3) msg += L.T(". Auto restart failed 3 times — start OBS by hand", ". Автоперезапуск не помог 3 раза — запустите OBS вручную");
                        else if ((now - lastLaunch).TotalSeconds >= 30) { LaunchObs(L.T("after a crash", "после падения")); msg += L.T(", restarting…", ", перезапускаю…"); }
                        else msg += L.T(", restarting…", ", перезапускаю…");
                    }
                    cur["obs"] = msg;
                    urgent.Add("obs");
                }
                else if (recentLaunch)
                {
                    cur["obs"] = L.T("OBS crashed, restarting…", "OBS аварийно закрылся, перезапускаю…");
                    urgent.Add("obs");
                }
                else if ((now - lostAt).TotalSeconds >= 20)
                {
                    cur["obs"] = L.T("OBS is not responding — it crashed with an error window or froze", "OBS не отвечает — упал с окном ошибки или завис");
                    urgent.Add("obs");
                }
            }

            if (!running)
            {
                lock (st) obsStatus = lostUnexpected ? L.T("OBS crashed", "OBS упал") : L.T("OBS is not running", "OBS не запущен");
                return false;
            }

            if ((now - lastConnectTry).TotalSeconds >= 3)
            {
                lastConnectTry = now;
                string err;
                bool authFail;
                var c = ObsClient.Connect(Cfg.Host, Cfg.Port, Cfg.GetPassword(), out err, out authFail);
                if (c != null)
                {
                    KeyValuePair<string, Dictionary<string, object>> stale;
                    while (obsEvents.TryDequeue(out stale)) { }   // events from the previous connection are not needed
                    obs = c;
                    obs.EventReceived += OnObsEvent;
                    if (obsProc != null) obsProc.Dispose();
                    obsProc = FindObs();
                    try { if (obsProc != null) { var h = obsProc.Handle; } } catch { }   // the handle is needed to read the exit code later
                    bool hadProblem;
                    lock (st) hadProblem = problems.ContainsKey("obs");
                    if (hadProblem) fixedMsgs.Add(L.T("OBS is working again", "OBS снова работает"));
                    lostUnexpected = exitStarted = plannedExit = authFailed = authPrompted = false;
                    wsError = null;
                    timeouts = 0;
                    connectedAt = now;
                    rbCrashed = rbStopRequested = rbManualOff = false;
                    rbTries = 0;
                    rbCrashes.Clear();
                    monSig = null;
                    monKick = false;
                    missingLogged.Clear();
                    ResetExtras();
                    lock (st) obsStatus = L.T("connected to OBS", "подключено к OBS");
                    Ev(L.T("connected to OBS", "подключено к OBS"));
                    return true;
                }
                authFailed = authFail;
                if (authFail && lostUnexpected)
                {
                    // OBS answered the connection — it is alive, the WebSocket password just changed
                    lostUnexpected = false;
                    cur.Remove("obs");
                    urgent.Remove("obs");
                }
                if (wsError != err) Log.Write("OBS connection: " + err);
                wsError = err;
                if (authFail && !authPrompted)
                {
                    authPrompted = true;
                    app.Post(() => app.ShowMain(true));
                }
            }

            // the password was never entered — a window with a hint is enough, no alarm
            if (wsError != null && (authFailed ? Cfg.HasPassword : (now - obsSeenSince).TotalSeconds >= 60))
                cur["ws"] = authFailed
                    ? L.T("No access to OBS: " + wsError + ". Open the ClipKeeper window and enter the password", "Нет доступа к OBS: " + wsError + ". Откройте окно ClipKeeper и введите пароль")
                    : L.T("Cannot connect to the OBS WebSocket (" + wsError + "). Is the WebSocket server enabled in OBS?", "Не могу подключиться к WebSocket OBS (" + wsError + "). Включён ли сервер WebSocket в OBS?");
            lock (st) obsStatus = wsError != null ? NoLinkPrefix + wsError : L.T("OBS is running, connecting…", "OBS запущен, подключаюсь…");
            return false;
        }

        // obs64.exe: the path from settings, the last one seen running, the registry or the default install folder
        public string ObsExe()
        {
            if (!string.IsNullOrEmpty(Cfg.ObsPath) && File.Exists(Cfg.ObsPath)) return Cfg.ObsPath;
            if (lastObsExe != null && File.Exists(lastObsExe)) return lastObsExe;
            try
            {
                using (var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\OBS Studio"))
                {
                    var d = k == null ? null : k.GetValue("") as string;
                    if (d != null)
                    {
                        var e = Path.Combine(d, @"bin\64bit\obs64.exe");
                        if (File.Exists(e)) return e;
                    }
                }
            }
            catch { }
            const string def = @"C:\Program Files\obs-studio\bin\64bit\obs64.exe";
            return File.Exists(def) ? def : null;
        }

        void LaunchObs(string why)
        {
            string exe = ObsExe();
            if (exe == null) { Ev(L.T("⚠ obs64.exe not found — choose it in Settings → Connection", "⚠ не найден obs64.exe — укажите его в «Настройках → Подключение»")); return; }
            // a process that was just ended takes a moment to disappear; OBS files are only touched once it has
            for (int i = 0; i < 40 && ObsScript.ObsRunning(); i++) Thread.Sleep(250);
            // OBS is closed right now: the moment to add or remove the "start ClipKeeper with OBS" script (ObsScript)
            string scriptErr = ObsScript.Sync(Cfg.ObsStartScript);
            if (scriptErr != null && scriptErr != ObsScript.WaitObs) Log.Write("OBS start script before launch: " + scriptErr);
            app.Post(app.SyncObsScript);   // the settings row shows the new state
            ObsFix.ApplyPending();         // what the first-run check fixed while OBS was open (WebSocket, replay buffer, save key)
            // --disable-shutdown-check: no "OBS crashed, start in safe mode?" question
            string args = (Cfg.UseReplayBuffer ? "--startreplaybuffer " : "") + "--disable-shutdown-check" + (Cfg.ObsMinimized ? " --minimize-to-tray" : "");
            try
            {
                System.Diagnostics.Process.Start(new ProcessStartInfo(exe, args)
                {
                    WorkingDirectory = Path.GetDirectoryName(exe),
                    UseShellExecute = true,
                });
                lastLaunch = DateTime.Now;
                launches.Add(lastLaunch);
                Ev(L.T("starting OBS (", "запускаю OBS (") + why + ")");
            }
            catch (Exception ex) { Ev(L.T("⚠ could not start OBS: ", "⚠ не удалось запустить OBS: ") + ex.Message); }
        }

        // why — for the log and the window when ClipKeeper restarts OBS by itself; null — from the button
        void RestartObs(string why)
        {
            plannedExit = true;
            lostUnexpected = false;
            StopReplayBufferForExit();
            using (var p = FindObs())
            {
                if (p != null)
                {
                    Ev(why != null ? L.T("restarting OBS: ", "перезапускаю OBS: ") + why : L.T("restarting OBS from the button", "перезапуск OBS по кнопке"));
                    bool asked = false;
                    try { asked = p.CloseMainWindow(); } catch { }
                    // OBS that hides to the tray, or waits on a question, ignores the close and keeps serving WebSocket;
                    // one that really closes drops the connection at once — so after 4 s with the link still up, stop waiting
                    var started = DateTime.Now;
                    bool exited = false;
                    while ((DateTime.Now - started).TotalSeconds < (asked ? 20 : 3))
                    {
                        if (p.WaitForExit(250)) { exited = true; break; }
                        if ((DateTime.Now - started).TotalSeconds > 4 && obs != null && obs.IsOpen) break;
                    }
                    if (!exited)
                    {
                        Ev(L.T("OBS did not close by itself — ending the process", "OBS не закрылся сам — завершаю процесс"));
                        try { p.Kill(); p.WaitForExit(10000); } catch { }
                    }
                }
            }
            if (obs != null) { obs.Dispose(); obs = null; }
            LaunchObs(why ?? L.T("from the button", "по кнопке"));
        }

        // OBS asks "exit while recording?" when the buffer is active — so stop it first
        void StopReplayBufferForExit()
        {
            if (obs == null || !obs.IsOpen) return;
            try
            {
                if (!Json.GetBool(obs.Request("GetReplayBufferStatus", null, ReqTimeout), "outputActive", false)) return;
                rbStopRequested = true;
                obs.Request("StopReplayBuffer", null, ReqTimeout);
                for (int i = 0; i < 20; i++)
                {
                    Thread.Sleep(500);
                    if (!Json.GetBool(obs.Request("GetReplayBufferStatus", null, ReqTimeout), "outputActive", false)) return;
                }
            }
            catch (Exception ex) { Log.Write("stopping the buffer before restart: " + ex.Message); }
        }

        // ── devices ─────────────────────────────────────────────────────────
        List<DevItem> Items(string type, string inputName, Dictionary<string, List<DevItem>> cache)
        {
            List<DevItem> l;
            if (cache.TryGetValue(type, out l)) return l;
            if (Matcher.IsAudio(type))
            {
                try
                {
                    l = audio == null ? null : audio.List(type == "wasapi_input_capture");
                    if (l != null) l.Insert(0, new DevItem(L.T("Default", "По умолчанию"), "default"));
                }
                catch (Exception ex) { Ev(L.T("audio device list error: ", "ошибка списка аудиоустройств: ") + ex.Message); l = null; }
            }
            else
            {
                try
                {
                    var r = obs.Request("GetInputPropertiesListPropertyItems",
                                        Json.D("inputName", inputName, "propertyName", RefStore.Watched[type]), ReqTimeout);
                    l = new List<DevItem>();
                    foreach (var o in Json.GetArr(r, "propertyItems"))
                    {
                        var d = Json.Obj(o);
                        if (d == null || !Json.GetBool(d, "itemEnabled", true)) continue;
                        string n = Json.GetStr(d, "itemName"), v = Json.GetStr(d, "itemValue");
                        if (!string.IsNullOrEmpty(n) && !string.IsNullOrEmpty(v)) l.Add(new DevItem(n, v));
                    }
                }
                catch (ObsException ex) { Ev(ex.Message); l = null; }
            }
            cache[type] = l;
            return l;
        }

        static string CurrentValue(Dictionary<string, object> resp, string type, string prop)
        {
            string v = Json.GetStr(Json.GetObj(resp, "inputSettings"), prop);
            // OBS does not send the default value
            return v ?? (Matcher.IsAudio(type) ? "default" : "");
        }

        void CheckSources(Dictionary<string, string> cur, List<string> fixedMsgs)
        {
            List<KeyValuePair<string, RefEntry>> list;
            lock (st) list = refs.Items.ToList();
            if (list.Count == 0) return;

            var cache = new Dictionary<string, List<DevItem>>();
            var monInputs = new List<string>();
            var nativeMons = Monitors.ActiveIds();
            bool changed = false;

            foreach (var kv in list)
            {
                string name = kv.Key;
                RefEntry r = kv.Value;
                Dictionary<string, object> resp;
                try { resp = obs.Request("GetInputSettings", Json.D("inputName", name), ReqTimeout); }
                catch (ObsException ex)
                {
                    if (ex.Code == 207) return;
                    if (ex.Code != 600) Ev(ex.Message);
                    else if (missingLogged.Add(name)) Ev(L.T("⚠ source \"" + name + "\" from the reference is not in OBS (renamed/removed?)", "⚠ источник «" + name + "» из эталона не найден в OBS (переименован/удалён?)"));
                    continue;
                }
                missingLogged.Remove(name);
                if (!(Json.GetStr(resp, "inputKind") ?? "").StartsWith(r.Type)) continue;

                string curVal = CurrentValue(resp, r.Type, r.Prop);
                if (Matcher.IsMonitor(r.Type))
                {
                    monInputs.Add(name);
                    // the monitor is in place — don't ask OBS for the list (that request blanks capture for 1–2 frames)
                    if (curVal == r.Value && nativeMons.Any(i => string.Equals(i, curVal, StringComparison.OrdinalIgnoreCase)))
                        continue;
                }
                var res = Matcher.Resolve(r.Type, curVal, r, Items(r.Type, name, cache));
                if (res.Kind == "ok")
                {
                    if (r.Value != curVal || r.LastName != res.Name)
                    {
                        lock (st) { r.Value = curVal; r.LastName = res.Name; }
                        changed = true;
                    }
                }
                else if (res.Kind == "fix")
                {
                    obs.Request("SetInputSettings", Json.D("inputName", name, "inputSettings",
                                Json.D(r.Prop, res.Value), "overlay", true), ReqTimeout);
                    lock (st) { r.Value = res.Value; r.LastName = res.Name; }
                    changed = true;
                    fixedMsgs.Add(name + L.T(": restored \"" + res.Name + "\" (", ": вернул «" + res.Name + "» (") + res.How + ")");
                }
                else cur["src:" + name] = name + ": " + res.How;
            }

            // monitors were reconnected — restart screen capture once the set of monitors
            // stops changing. Only IDs are compared: a resolution change does not count.
            if (monInputs.Count > 0 && nativeMons.Count > 0)
            {
                string sig = string.Join("|", nativeMons.Select(v => v.ToUpperInvariant()).OrderBy(v => v, StringComparer.Ordinal));
                if (monSig != null && monSig != sig)
                {
                    monKick = true;
                    Ev(L.T("the monitor list changed", "список мониторов изменился"));
                }
                else if (monKick)
                {
                    monKick = false;
                    if (Cfg.KickMonitor)
                        foreach (var n in monInputs.Where(n => !cur.ContainsKey("src:" + n)))
                        {
                            try
                            {
                                obs.Request("SetInputSettings", Json.D("inputName", n, "inputSettings",
                                            new Dictionary<string, object>(), "overlay", true), ReqTimeout);
                                Ev(n + L.T(": screen capture restarted after the monitor change", ": захват экрана перезапущен после смены мониторов"));
                            }
                            catch (ObsException ex) { Ev(ex.Message); }
                        }
                }
                monSig = sig;
            }

            if (changed) SaveRefs();
        }

        void SaveReference()
        {
            var resp = obs.Request("GetInputList", null, ReqTimeout);
            Dictionary<string, RefEntry> old;
            lock (st) old = new Dictionary<string, RefEntry>(refs.Items);
            var fresh = new Dictionary<string, RefEntry>();
            var notes = new List<string>();
            var cache = new Dictionary<string, List<DevItem>>();

            foreach (var o in Json.GetArr(resp, "inputs"))
            {
                var d = Json.Obj(o);
                string name = Json.GetStr(d, "inputName");
                string kind = Json.GetStr(d, "unversionedInputKind") ?? Json.GetStr(d, "inputKind");
                if (name == null || kind == null || !RefStore.Watched.ContainsKey(kind)) continue;
                string prop = RefStore.Watched[kind];
                string val = CurrentValue(obs.Request("GetInputSettings", Json.D("inputName", name), ReqTimeout), kind, prop);
                var items = Items(kind, name, cache);
                string display = items == null ? null : items.Where(i => i.Value == val).Select(i => i.Name).FirstOrDefault();
                RefEntry prev;
                old.TryGetValue(name, out prev);
                if (display == null && prev != null && prev.Value == val && !string.IsNullOrEmpty(prev.Display))
                {
                    display = prev.Display;
                    notes.Add(name + L.T(": the device is unavailable now, the previous name is kept", ": устройство сейчас недоступно, оставлено прежнее имя"));
                }
                else if (display == null)
                {
                    display = "";
                    notes.Add(name + L.T(": the device is unavailable now — restoring by name will not work", ": устройство сейчас недоступно — восстановление по имени не сработает"));
                }
                fresh[name] = new RefEntry { Type = kind, Prop = prop, Value = val, Display = display, LastName = display };
                if (Matcher.IsAudio(kind)) ReadMixer(name, fresh[name]);
            }

            string recTracks = ProfileParam(OutSection(), "RecTracks");
            lock (st)
            {
                refs.Items = fresh;
                refs.RecTracks = recTracks;
                problems.Clear();
                notes.Clear();
            }
            missingLogged.Clear();
            SaveRefs();
            alertKeys.Clear();
            alertBody = "";
            app.Post(app.CloseAlert);
            Ev(L.T("reference saved: ", "эталон сохранён: ") + L.N(fresh.Count, "source", "sources", "источник", "источника", "источников"));
            foreach (var kv in fresh) Log.Write("  " + kv.Key + " (" + kv.Value.Type + ") -> " + kv.Value.Display);
            foreach (var n in notes) Ev("⚠ " + n);
        }

        // ── replay buffer ───────────────────────────────────────────────────
        void CheckReplayBuffer(DateTime now, Dictionary<string, string> cur, HashSet<string> urgent, List<string> fixedMsgs)
        {
            if (!Cfg.WatchReplayBuffer || !Cfg.UseReplayBuffer) return;
            Dictionary<string, object> r;
            try { r = obs.Request("GetReplayBufferStatus", null, ReqTimeout); }
            catch (ObsException) { uiRb = -1; return; }   // the replay buffer is not enabled in OBS output settings
            bool active = Json.GetBool(r, "outputActive", false);
            uiRb = active ? 1 : rbCrashed ? 2 : 0;

            // crashes again and again — restarts are useless, alarm right away until it is stable for 10 minutes
            rbCrashes.RemoveAll(t => (now - t).TotalMinutes > 10);
            if (rbCrashes.Count >= 3)
            {
                cur["rb"] = L.T("The replay buffer crashed " + rbCrashes.Count + " times in 10 minutes — looks like a driver/encoder " +
                                "failure. Restart OBS (if that does not help, reboot the PC)",
                                "Буфер повтора упал " + rbCrashes.Count + " раза за 10 минут — похоже на сбой " +
                                "драйвера/энкодера. Перезапустите OBS (не поможет — перезагрузите ПК)");
                urgent.Add("rb");
                if (active) rbCrashed = false;
                return;
            }

            if (active)
            {
                if (rbCrashed) fixedMsgs.Add(L.T("the replay buffer is recording again", "буфер повтора снова записывает"));
                rbCrashed = rbManualOff = false;
                rbTries = 0;
                return;
            }

            if (rbCrashed)
            {
                // OBS can't draw: the buffer won't start (OBS only shows an error window) — the "gpu" alarm says what to do
                if (renderFailSince != DateTime.MinValue)
                {
                    cur["rb"] = L.T("The replay buffer stopped: OBS lost the graphics card", "Буфер повтора остановился: OBS потерял видеокарту");
                    return;
                }
                if (Cfg.RbAutoRestart && rbTries < 5 && (now - rbCrashAt).TotalSeconds >= 3 &&
                    (now - rbLastTry).TotalSeconds >= 15)
                    StartReplayBuffer(now);
                cur["rb"] = L.T("The replay buffer stopped because of an error", "Буфер повтора остановился из-за ошибки") +
                            (rbTries >= 5 || !Cfg.RbAutoRestart ? L.T(". Auto start did not help — restart OBS", ". Автозапуск не помог — перезапустите OBS")
                                                               : L.T(", restarting…", ", перезапускаю…"));
                return;
            }

            if (rbStopRequested) { rbManualOff = true; return; }   // turned off by the user — no alarm

            if (Cfg.ReplayBufferMustRun && (now - connectedAt).TotalSeconds >= 60)
            {
                if (Cfg.RbAutoRestart && rbTries < 3 && (now - rbLastTry).TotalSeconds >= 15) StartReplayBuffer(now);
                cur["rb"] = L.T("The replay buffer is not running", "Буфер повтора не запущен");
            }
        }

        void StartReplayBuffer(DateTime now)
        {
            rbTries++;
            rbLastTry = now;
            Ev(L.T("starting the replay buffer (attempt ", "запускаю буфер повтора (попытка ") + rbTries + ")");
            try { obs.Request("StartReplayBuffer", null, ReqTimeout); }
            catch (ObsException ex) { Ev(ex.Message); }
        }

        // ── problems → notifications ────────────────────────────────────────
        void ProcessResults(DateTime now, Dictionary<string, string> cur, HashSet<string> urgent, List<string> fixedMsgs)
        {
            var due = new Dictionary<string, string>();
            var fresh = new List<string>();
            lock (st)
            {
                foreach (var k in problems.Keys.ToList())
                    if (!cur.ContainsKey(k)) problems.Remove(k);
                foreach (var kv in cur)
                {
                    Problem p;
                    if (!problems.TryGetValue(kv.Key, out p))
                    {
                        p = new Problem { Since = now, Msg = kv.Value };
                        problems[kv.Key] = p;
                        fresh.Add(kv.Value);
                    }
                    p.Msg = kv.Value;
                    if (urgent.Contains(kv.Key)) p.Urgent = true;
                }
                foreach (var kv in problems)
                    if (IsDue(kv.Value, now)) due[kv.Key] = kv.Value.Msg;
                if (connectedNow) lastCheck = now;
            }
            foreach (var m in fresh) Ev("⚠ " + m);
            foreach (var m in fixedMsgs) Ev("✓ " + m);
            UpdateAlerts(due, fixedMsgs);
        }

        static string Body(Dictionary<string, string> due)
        {
            var lines = due.Values.Select(m => "• " + m).ToList();
            if (due.Keys.Any(k => k.StartsWith("src:")))
            {
                lines.Add("");
                lines.Add(L.T("Check the device connections and your audio software (SteelSeries GG, Voicemeeter…). If the device now has a different name, " +
                              "pick it in OBS and press \"Remember current devices\" in ClipKeeper.",
                              "Проверьте подключение устройств и звуковые программы (SteelSeries GG, Voicemeeter…). Если устройство теперь называется " +
                              "иначе — выберите его в OBS и нажмите «Запомнить текущие устройства» в ClipKeeper."));
            }
            if (due.ContainsKey("rb") || due.ContainsKey("obs") || due.ContainsKey("hang") || due.ContainsKey("gpu"))
            {
                lines.Add("");
                lines.Add(L.T("\"Restart OBS\" closes OBS and starts it again with the replay buffer.", "«Перезапустить OBS» закроет OBS и запустит его снова с буфером повтора."));
            }
            return string.Join("\n", lines);
        }

        void UpdateAlerts(Dictionary<string, string> due, List<string> fixedMsgs)
        {
            if (due.Count > 0)
            {
                string body = Body(due);
                bool restartBtn = due.ContainsKey("rb") || due.ContainsKey("obs") || due.ContainsKey("hang") || due.ContainsKey("gpu");
                bool isNew = due.Keys.Any(k => !alertKeys.Contains(k));
                if (isNew || body != alertBody)
                    app.Post(() => app.ShowAlert(L.T("OBS: recording is not going right!", "OBS: запись идёт не так, как надо!"), body, restartBtn, isNew));
                alertKeys = new HashSet<string>(due.Keys);
                alertBody = body;
                return;
            }
            if (alertKeys.Count > 0)
            {
                alertKeys.Clear();
                alertBody = "";
                var lines = fixedMsgs.Count > 0 ? fixedMsgs.ToList() : new List<string> { L.T("The problem is fixed", "Проблема устранена") };
                app.Post(() =>
                {
                    app.CloseAlert();
                    if (Cfg.NotifyFixed) app.ShowOk(L.T("OBS: everything works again", "OBS: всё снова работает"), lines);
                });
            }
            else if (fixedMsgs.Count > 0 && Cfg.NotifyFixed)
            {
                var lines = fixedMsgs.ToList();
                app.Post(() => app.ShowOk(L.T("OBS: restored", "OBS: восстановлено"), lines));
            }
        }
    }
}
