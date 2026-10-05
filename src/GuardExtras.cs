using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;

namespace DeviceGuard
{
    // Extra checks: silence in OBS, single-color screen, mixer, clips, disk, driver, backup
    partial class Guard
    {
        // ── common ──────────────────────────────────────────────────────────
        readonly Dictionary<string, string> notes = new Dictionary<string, string>();   // yellow warnings, under st
        Thread sampler;
        EventLogWatcher drvWatcher;

        // ── the OBS save key: confirm the press at once (SaveKey) ───────────
        SaveKey saveKey;
        DateTime saveKeyRead = DateTime.MinValue;
        string saveKeyProfile;
        public volatile string SaveKeyText;   // "Ctrl+F9" — what OBS saves with; null — not found or not supported

        // the OBS profile can change and its keys too: read again every 30 s (a file read and one request)
        void CheckSaveKey(DateTime now)
        {
            if ((now - saveKeyRead).TotalSeconds < 30) return;
            saveKeyRead = now;
            string profile = null;
            try { profile = Json.GetStr(obs.Request("GetProfileList", null, ReqTimeout), "currentProfileName"); }
            catch (ObsException) { }
            var list = SaveKey.Read(profile);
            string text = list.Count > 0 ? string.Join(", ", list.Select(c => c.ToString())) : null;
            if (profile != saveKeyProfile || text != SaveKeyText) Log.Write("OBS save key (" + profile + "): " + (text ?? "none"));
            saveKeyProfile = profile;
            SaveKeyText = text;
            saveKey.Set(list);
        }

        // the polling thread: the press counts only while the replay buffer is recording — otherwise OBS saves nothing
        void SaveKeyPressed()
        {
            if (!connectedNow || uiRb != 1 || !Cfg.GuardEnabled || !Cfg.UseReplayBuffer || !Cfg.ClipInstant) return;
            app.Post(app.SavePressed);
        }

        void StartExtras()
        {
            saveKey = new SaveKey(SaveKeyPressed);
            sampler = new Thread(SamplerRun) { IsBackground = true, Name = "ClipKeeper-meters" };
            sampler.SetApartmentState(ApartmentState.MTA);
            sampler.Start();
            StartDriverWatch();
        }

        void StopExtras()
        {
            if (saveKey != null) saveKey.Stop();
            if (sampler != null) sampler.Join(2000);
            if (drvWatcher != null) try { drvWatcher.Enabled = false; drvWatcher.Dispose(); } catch { }
        }

        void ResetExtras()
        {
            saveKeyRead = DateTime.MinValue;   // a new connection: the profile may be another one
            audioWatch.Clear();
            screenWatch.Clear();
            lastScreenCheck = DateTime.Now;   // in the first seconds after OBS starts the capture may still be empty
            lastRecTracksCheck = lastDiskCheck = DateTime.MinValue;
            recTracksNote = diskMsg = null;
            rbStartedAt = DateTime.MinValue;
            resetMeters = true;
            ResetPerf();
        }

        // checks that do not need OBS
        void RunAlways(DateTime now)
        {
            if (driverEvent)
            {
                driverEvent = false;
                if ((now - lastDriverNotice).TotalMinutes >= 2)
                {
                    lastDriverNotice = now;
                    lastScreenCheck = DateTime.MinValue;   // check the picture right away
                    Ev(L.T("⚠ Windows: the graphics driver failed or restarted", "⚠ Windows: сбой или перезапуск драйвера видеокарты"));
                    var lines = new List<string> {
                        L.T("Windows reports that the graphics driver failed or restarted.", "Windows сообщает, что драйвер видеокарты сбоил или перезапускался."),
                        L.T("Checking the recording. If something broke, a separate alarm will follow.", "Проверяю запись. Если что-то сломалось, придёт отдельная тревога.") };
                    app.Post(() => app.ShowNotice(L.T("Graphics driver failure", "Сбой драйвера видеокарты"), lines, true));
                }
            }
            DailyBackup(now);
        }

        void RunExtras(DateTime now, Dictionary<string, string> cur, HashSet<string> urgent,
                       List<string> fixedMsgs, Dictionary<string, string> notesCur)
        {
            CheckMixer(now, fixedMsgs, notesCur);
            CheckAudioStall(now, cur, urgent, fixedMsgs);
            CheckScreens(now, cur, urgent, fixedMsgs);
            CheckDisk(now, cur);
            CheckPerf(now, notesCur);
        }

        void ProcessNotes(Dictionary<string, string> cur)
        {
            List<string> fresh;
            lock (st)
            {
                fresh = cur.Where(kv => !notes.ContainsKey(kv.Key)).Select(kv => kv.Value).ToList();
                notes.Clear();
                foreach (var kv in cur) notes[kv.Key] = kv.Value;
            }
            foreach (var m in fresh) Ev("ⓘ " + m);
            if (fresh.Count > 0) app.Post(() => app.ShowNotice(L.T("OBS: heads up", "OBS: обрати внимание"), fresh, false));
        }

        static double ToD(object o)
        {
            try { return o == null ? 0 : Convert.ToDouble(o, CultureInfo.InvariantCulture); } catch { return 0; }
        }

        string OutSection()
        {
            return ProfileParam("Output", "Mode") == "Advanced" ? "AdvOut" : "SimpleOutput";
        }

        string ProfileParam(string category, string name)
        {
            try
            {
                var r = obs.Request("GetProfileParameter", Json.D("parameterCategory", category, "parameterName", name), ReqTimeout);
                return Json.GetStr(r, "parameterValue") ?? Json.GetStr(r, "defaultParameterValue");
            }
            catch (ObsException) { return null; }
        }

        // ── 1. audio: Windows hears it, OBS does not ────────────────────────
        class AudioWatch
        {
            public string DeviceId;
            public DateTime ObsLast = DateTime.MinValue, StallSince = DateTime.MinValue, KickedAt = DateTime.MinValue;
            public int Mismatch;
            public bool Muted, Kicked, Reported;
        }

        const float WinThreshold = 0.01f;     // −40 dB: something is definitely playing in Windows
        const double ObsThreshold = 0.0005;   // −66 dB: OBS gets at least some signal
        readonly ConcurrentDictionary<string, AudioWatch> audioWatch = new ConcurrentDictionary<string, AudioWatch>();
        long lastMeterTicks;
        volatile bool resetMeters;

        // source levels from OBS (~20 times a second, WebSocket receive thread)
        void OnMeters(Dictionary<string, object> data)
        {
            var now = DateTime.Now;
            Interlocked.Exchange(ref lastMeterTicks, now.Ticks);
            foreach (var o in Json.GetArr(data, "inputs"))
            {
                var d = Json.Obj(o);
                string name = Json.GetStr(d, "inputName");
                AudioWatch w;
                if (name == null || !audioWatch.TryGetValue(name, out w)) continue;
                double peak = 0;
                foreach (var ch in Json.GetArr(d, "inputLevelsMul"))
                {
                    var arr = ch as object[];
                    if (arr != null && arr.Length >= 3) peak = Math.Max(peak, ToD(arr[2]));   // peak before the mixer volume
                }
                if (peak > ObsThreshold) lock (w) w.ObsLast = now;
            }
        }

        // Windows device levels, 4 times a second
        void SamplerRun()
        {
            AudioDevices meters;
            try { meters = new AudioDevices(false); }
            catch (Exception ex) { Log.Write("Windows audio meters unavailable: " + ex.Message); return; }
            try
            {
                while (!stopping)
                {
                    Thread.Sleep(250);
                    if (!Cfg.AudioStallCheck || !connectedNow) continue;
                    if (resetMeters) { resetMeters = false; meters.ResetMeters(); }
                    var now = DateTime.Now;
                    bool meterFresh = (now - new DateTime(Interlocked.Read(ref lastMeterTicks))).TotalSeconds < 2;
                    foreach (var w in audioWatch.Values)
                    {
                        float p;
                        try { p = meters.Peak(w.DeviceId); } catch { p = -1; }
                        lock (w)
                        {
                            if (!meterFresh || w.Muted || (now - w.ObsLast).TotalSeconds < 1.5)
                            {
                                w.StallSince = DateTime.MinValue;
                                w.Mismatch = 0;
                            }
                            else if (p > WinThreshold)
                            {
                                if (w.StallSince == DateTime.MinValue) w.StallSince = now;
                                w.Mismatch++;
                            }
                        }
                    }
                }
            }
            finally { meters.Dispose(); }
        }

        void SyncAudioWatch()
        {
            var want = new Dictionary<string, string>();
            lock (st)
                foreach (var kv in refs.Items)
                    if (Matcher.IsAudio(kv.Value.Type) && !string.IsNullOrEmpty(kv.Value.Value) && kv.Value.Value != "default")
                        want[kv.Key] = kv.Value.Value;
            AudioWatch tmp;
            foreach (var k in audioWatch.Keys.Where(k => !want.ContainsKey(k)).ToList()) audioWatch.TryRemove(k, out tmp);
            foreach (var kv in want)
            {
                AudioWatch w;
                if (!audioWatch.TryGetValue(kv.Key, out w) || w.DeviceId != kv.Value)
                    audioWatch[kv.Key] = new AudioWatch { DeviceId = kv.Value };
            }
        }

        void CheckAudioStall(DateTime now, Dictionary<string, string> cur, HashSet<string> urgent, List<string> fixedMsgs)
        {
            SyncAudioWatch();
            if (!Cfg.AudioStallCheck) return;
            foreach (var kv in audioWatch)
            {
                string name = kv.Key;
                var w = kv.Value;
                bool stalled, recovered;
                lock (w)
                {
                    stalled = w.StallSince != DateTime.MinValue && (now - w.StallSince).TotalSeconds >= 8 && w.Mismatch >= 12;
                    recovered = w.Kicked && w.ObsLast > w.KickedAt.AddSeconds(1);
                }
                if (recovered)
                {
                    fixedMsgs.Add(name + L.T(": OBS stopped getting audio — restarted the source, audio is back", ": OBS перестал получать звук — перезапустил источник, звук снова идёт"));
                    lock (w) { w.Kicked = false; w.Reported = false; }
                    continue;
                }
                if (stalled && !w.Kicked)
                {
                    // the most common way to "wake" a source is to select the device again
                    Ev("⚠ " + name + L.T(": Windows has audio but OBS is not getting it — restarting the source", ": в Windows звук есть, а OBS его не получает — перезапускаю источник"));
                    KickAudio(name, w.DeviceId);
                    lock (w)
                    {
                        w.Kicked = true;
                        w.KickedAt = now;
                        w.StallSince = DateTime.MinValue;
                        w.Mismatch = 0;
                    }
                }
                else if (stalled) w.Reported = true;
                else if (w.Kicked && !w.Reported && (now - w.KickedAt).TotalMinutes > 10) w.Kicked = false;

                if (w.Reported)
                {
                    cur["audio:" + name] = name + L.T(": Windows is playing audio but OBS is not getting it — the source is stuck. " +
                                         "Reselect the device in OBS or restart OBS",
                                         ": в Windows звук идёт, а OBS его не получает — источник завис. " +
                                         "Перевыберите устройство в OBS или перезапустите OBS");
                    urgent.Add("audio:" + name);
                }
            }
        }

        void KickAudio(string name, string deviceId)
        {
            obs.Request("SetInputSettings", Json.D("inputName", name, "inputSettings", Json.D("device_id", "default"), "overlay", true), ReqTimeout);
            Thread.Sleep(300);
            obs.Request("SetInputSettings", Json.D("inputName", name, "inputSettings", Json.D("device_id", deviceId), "overlay", true), ReqTimeout);
        }

        // ── 2. screen: black / white / single color ─────────────────────────
        class ScreenWatch
        {
            public DateTime Since = DateTime.MinValue;
            public string Kind;
            public bool Kicked, Reported;
        }

        readonly Dictionary<string, ScreenWatch> screenWatch = new Dictionary<string, ScreenWatch>();
        DateTime lastScreenCheck = DateTime.MinValue;

        void CheckScreens(DateTime now, Dictionary<string, string> cur, HashSet<string> urgent, List<string> fixedMsgs)
        {
            if (!Cfg.ScreenCheck) { screenWatch.Clear(); return; }
            if ((now - lastScreenCheck).TotalSeconds >= Math.Max(5, Cfg.ScreenIntervalSec))
            {
                lastScreenCheck = now;
                // the screen is locked / the monitor slept while you are away — that is not a failure
                bool idle = Pixels.IdleSeconds() > 120;
                Dictionary<string, string> monitors;   // source -> monitor name ("MSI…: 2560x1440 @ 0,0")
                lock (st) monitors = refs.Items.Where(kv => Matcher.IsMonitor(kv.Value.Type))
                                               .ToDictionary(kv => kv.Key, kv => kv.Value.LastName ?? kv.Value.Display);
                foreach (var k in screenWatch.Keys.Except(monitors.Keys).ToList()) screenWatch.Remove(k);

                foreach (var mon in monitors)
                {
                    string name = mon.Key;
                    string kind;
                    try
                    {
                        var r = obs.Request("GetSourceScreenshot", Json.D("sourceName", name, "imageFormat", "png",
                                            "imageWidth", 64, "imageHeight", 36), ReqTimeout);
                        kind = Pixels.UniformKind(Json.GetStr(r, "imageData"));
                    }
                    catch (ObsException ex)
                    {
                        if (ex.Code != 600 && ex.Code != 207) Log.Write("screenshot: " + ex.Message);
                        continue;
                    }
                    catch (TimeoutException) { throw; }
                    catch (IOException) { throw; }
                    catch (Exception ex) { Log.Write("screenshot: " + ex.Message); continue; }

                    ScreenWatch w;
                    if (!screenWatch.TryGetValue(name, out w)) screenWatch[name] = w = new ScreenWatch();

                    // compare with the monitor: if it also shows a solid color (loading, a dark scene),
                    // OBS records it honestly — capture is not broken
                    if (kind != null && !idle)
                    {
                        string real;
                        try { real = Pixels.ScreenKind(mon.Value); } catch { real = "?"; }
                        if (real != null && real != "?")
                        {
                            if (w.Since == DateTime.MinValue || w.Kind != null)
                                Log.Write(name + ": the screen really is " + real + " — capture is fine");
                            kind = null;
                        }
                    }

                    if (kind == null || idle)
                    {
                        if (kind == null && (w.Kicked || w.Reported)) fixedMsgs.Add(name + L.T(": the recording has a picture again", ": картинка в записи снова есть"));
                        w.Since = DateTime.MinValue;
                        w.Kicked = w.Reported = false;
                        w.Kind = null;
                        continue;
                    }
                    if (w.Since == DateTime.MinValue) { w.Since = now; Ev(name + L.T(": the monitor has a picture, but OBS records ", ": на мониторе есть картинка, а OBS пишет ") + Pixels.ScreenText(kind)); }
                    w.Kind = kind;
                    double secs = (now - w.Since).TotalSeconds;
                    if (secs >= 30 && !w.Kicked)
                    {
                        w.Kicked = true;
                        Ev("⚠ " + name + ": " + Pixels.ScreenText(kind) + L.T(" for " + (int)secs + " s — restarting capture", " уже " + (int)secs + " с — перезапускаю захват"));
                        try
                        {
                            obs.Request("SetInputSettings", Json.D("inputName", name, "inputSettings",
                                        new Dictionary<string, object>(), "overlay", true), ReqTimeout);
                        }
                        catch (ObsException ex) { Log.Write(ex.Message); }
                    }
                    if (secs >= 60) w.Reported = true;
                }
            }

            foreach (var kv in screenWatch.Where(x => x.Value.Reported))
            {
                int min = (int)Math.Max(1, (now - kv.Value.Since).TotalMinutes);
                cur["screen:" + kv.Key] = kv.Key + L.T(": the recording shows " + Pixels.ScreenText(kv.Value.Kind) + " for " + min +
                                                " min — screen capture is broken. Restart OBS",
                                                ": в записи " + Pixels.ScreenText(kv.Value.Kind) + " уже " + min +
                                                " мин — захват экрана сломался. Перезапустите OBS");
                urgent.Add("screen:" + kv.Key);
            }
        }

        // ── 3. mixer: tracks, mute, volume ──────────────────────────────────
        DateTime lastRecTracksCheck = DateTime.MinValue;
        string recTracksNote;

        static string TracksStr(Dictionary<string, object> tracks)
        {
            if (tracks == null) return null;
            return string.Join(",", Enumerable.Range(1, 6).Where(i => Json.GetBool(tracks, i.ToString(), false)));
        }

        static Dictionary<string, object> TracksObj(string s)
        {
            var on = new HashSet<string>((s ?? "").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries));
            var d = new Dictionary<string, object>();
            for (int i = 1; i <= 6; i++) d[i.ToString()] = on.Contains(i.ToString());
            return d;
        }

        static string PrettyTracks(string s) { return string.IsNullOrEmpty(s) ? L.T("none", "никаких") : s.Replace(",", ", "); }

        static string MaskTracks(string mask)
        {
            int m;
            if (!int.TryParse(mask, out m)) return mask;
            return PrettyTracks(string.Join(",", Enumerable.Range(1, 6).Where(i => (m & (1 << (i - 1))) != 0)));
        }

        static string Db(double db) { return db <= -99 ? "−∞" : db.ToString("0.#", CultureInfo.InvariantCulture); }

        static double VolumeDb(Dictionary<string, object> r)
        {
            object v;
            return r != null && r.TryGetValue("inputVolumeDb", out v) && v != null ? ToD(v) : -100;
        }

        // remember the source's mixer (on "Remember current devices")
        void ReadMixer(string name, RefEntry e)
        {
            try
            {
                e.Tracks = TracksStr(Json.GetObj(obs.Request("GetInputAudioTracks", Json.D("inputName", name), ReqTimeout), "inputAudioTracks"));
                e.Muted = Json.GetBool(obs.Request("GetInputMute", Json.D("inputName", name), ReqTimeout), "inputMuted", false);
                double db = VolumeDb(obs.Request("GetInputVolume", Json.D("inputName", name), ReqTimeout));
                e.VolumeDb = db <= -99 ? (double?)null : db;
            }
            catch (ObsException ex) { Log.Write("mixer " + name + ": " + ex.Message); }
        }

        void CheckMixer(DateTime now, List<string> fixedMsgs, Dictionary<string, string> notesCur)
        {
            if (!Cfg.GuardMixer) return;
            List<KeyValuePair<string, RefEntry>> list;
            lock (st) list = refs.Items.Where(kv => Matcher.IsAudio(kv.Value.Type)).ToList();
            foreach (var kv in list)
            {
                string name = kv.Key;
                var r = kv.Value;
                try
                {
                    // tracks are restored automatically: an accidentally cleared checkbox = an empty track in the clip
                    if (r.Tracks != null)
                    {
                        string curTracks = TracksStr(Json.GetObj(obs.Request("GetInputAudioTracks",
                                                     Json.D("inputName", name), ReqTimeout), "inputAudioTracks"));
                        if (curTracks != null && curTracks != r.Tracks)
                        {
                            obs.Request("SetInputAudioTracks", Json.D("inputName", name, "inputAudioTracks", TracksObj(r.Tracks)), ReqTimeout);
                            fixedMsgs.Add(name + L.T(": restored tracks " + PrettyTracks(r.Tracks) + " (were " + PrettyTracks(curTracks) + ")", ": вернул дорожки " + PrettyTracks(r.Tracks) + " (были " + PrettyTracks(curTracks) + ")"));
                        }
                    }
                    // volume and mute are left alone — they are often changed on purpose; only warn
                    bool muted = Json.GetBool(obs.Request("GetInputMute", Json.D("inputName", name), ReqTimeout), "inputMuted", false);
                    AudioWatch w;
                    if (audioWatch.TryGetValue(name, out w)) lock (w) w.Muted = muted;
                    if (muted && r.Muted == false) notesCur["mute:" + name] = name + L.T(": muted in the OBS mixer", ": звук выключен в микшере OBS");
                    if (r.VolumeDb.HasValue)
                    {
                        double db = VolumeDb(obs.Request("GetInputVolume", Json.D("inputName", name), ReqTimeout));
                        if (Math.Abs(db - r.VolumeDb.Value) >= 6)
                            notesCur["vol:" + name] = name + L.T(": OBS volume " + Db(db) + " dB (reference " + Db(r.VolumeDb.Value) + ")", ": громкость в OBS " + Db(db) + " дБ (в эталоне " + Db(r.VolumeDb.Value) + ")");
                    }
                }
                catch (ObsException ex)
                {
                    if (ex.Code == 207) return;
                    if (ex.Code != 600) Log.Write("mixer " + name + ": " + ex.Message);
                }
            }

            if (refs.RecTracks != null && (now - lastRecTracksCheck).TotalSeconds >= 30)
            {
                lastRecTracksCheck = now;
                string rt = ProfileParam(OutSection(), "RecTracks");
                recTracksNote = rt != null && rt != refs.RecTracks
                    ? L.T("recording settings have tracks " + MaskTracks(rt) + " enabled, the reference has " + MaskTracks(refs.RecTracks), "в настройках записи включены дорожки " + MaskTracks(rt) + ", а в эталоне " + MaskTracks(refs.RecTracks))
                    : null;
            }
            if (recTracksNote != null) notesCur["rectracks"] = recTracksNote;
        }

        // ── 4. disk space ───────────────────────────────────────────────────
        DateTime lastDiskCheck = DateTime.MinValue;
        string diskMsg;

        void CheckDisk(DateTime now, Dictionary<string, string> cur)
        {
            if ((now - lastDiskCheck).TotalSeconds >= 60)
            {
                lastDiskCheck = now;
                diskMsg = null;
                try
                {
                    string sec = OutSection();
                    string path = ProfileParam(sec, sec == "AdvOut" ? "RecFilePath" : "FilePath");
                    if (!string.IsNullOrEmpty(path)) ClipsRoot = path.Replace('/', '\\');
                    string rbt = ProfileParam(sec, "RecRBTime"), rbs = ProfileParam(sec, "RecRBSize");
                    if (rbt != null && rbs != null) uiRbInfo = L.T("up to " + rbt + " s · memory up to " + rbs + " MB", "до " + rbt + " с · память до " + rbs + " МБ");
                    int mask;
                    if (sec == "AdvOut" && int.TryParse(ProfileParam(sec, "RecTracks"), out mask))
                    {
                        var titles = new List<string>();
                        for (int t = 1; t <= 6; t++)
                            if ((mask & (1 << (t - 1))) != 0) titles.Add(ProfileParam(sec, "Track" + t + "Name") ?? "");
                        TrackTitles = titles.ToArray();
                    }
                    if (!string.IsNullOrEmpty(path) && Cfg.MinFreeGB > 0)
                    {
                        string root = Path.GetPathRoot(path.Replace('/', '\\'));
                        double gb = new DriveInfo(root).AvailableFreeSpace / 1073741824.0;
                        uiDiskFree = gb;
                        uiDiskRoot = root.TrimEnd('\\');
                        if (gb < Cfg.MinFreeGB)
                            diskMsg = L.T("Only " + gb.ToString("0.#") + " GB left on " + root.TrimEnd('\\') + " — clips will soon stop saving",
                              "На диске " + root.TrimEnd('\\') + " осталось " + gb.ToString("0.#") + " ГБ — клипы скоро перестанут сохраняться");
                    }
                }
                catch (ObsException) { throw; }
                catch (Exception ex) { Log.Write("disk check: " + ex.Message); }
            }
            if (diskMsg != null) cur["disk"] = diskMsg;
        }

        // ── 5. checking saved clips ─────────────────────────────────────────
        class PendingClip { public string Path, Game; public DateTime SavedAt, DueAt; public int Tries; }

        readonly List<PendingClip> clips = new List<PendingClip>();
        DateTime rbStartedAt = DateTime.MinValue;

        void OnClipSaved(Dictionary<string, object> data, DateTime now, bool sorted)
        {
            string p = Json.GetStr(data, "savedReplayPath");
            if (string.IsNullOrEmpty(p)) return;
            if (!Cfg.GuardEnabled || (!Cfg.ClipCheck && !Cfg.ClipToast && !Cfg.ClipSound)) return;
            // sorted by ClipKeeper — the file is already in place; otherwise Smart Replay Mover may still be moving it
            // (only if it is there: without it every clip would wait for nothing)
            clips.Add(new PendingClip { Path = p.Replace('/', '\\'), Game = Json.GetStr(data, "game"), SavedAt = now,
                                        DueAt = sorted || !SrmPresent(now) ? now : now.AddSeconds(2.5) });
        }

        DateTime srmCheckedAt = DateTime.MinValue;
        bool srmPresent;

        bool SrmPresent(DateTime now)
        {
            if ((now - srmCheckedAt).TotalMinutes >= 1) { srmPresent = SmartReplayMover.Find() != null; srmCheckedAt = now; }
            return srmPresent;
        }

        int ClipWaitMs()
        {
            if (clips.Count == 0) return int.MaxValue;
            double ms = (clips.Min(c => c.DueAt) - DateTime.Now).TotalMilliseconds;
            return (int)Math.Max(100, Math.Min(60000, ms));
        }

        static string FindClip(string path)
        {
            if (File.Exists(path)) return path;
            try
            {
                string name = Path.GetFileName(path), root = Path.GetDirectoryName(path);
                var hit = new DirectoryInfo(root).EnumerateFiles("*" + name, SearchOption.AllDirectories)
                    .Where(f => (DateTime.Now - f.LastWriteTime).TotalMinutes < 10)
                    .OrderByDescending(f => f.LastWriteTime).FirstOrDefault();
                return hit == null ? null : hit.FullName;
            }
            catch { return null; }
        }

        void CheckClips(DateTime now)
        {
            foreach (var c in clips.Where(c => c.DueAt <= now).ToList())
            {
                string file = FindClip(c.Path);
                if (file == null && ++c.Tries < 4) { c.DueAt = now.AddSeconds(2.5); continue; }
                clips.Remove(c);
                if (file == null)
                {
                    Ev(L.T("clip saved, but the file was not found for checking: ", "клип сохранён, но файл не найден для проверки: ") + Path.GetFileName(c.Path));
                    app.Post(() =>
                    {
                        if (Cfg.ClipToast) app.ShowToast(L.T("✓ Clip saved", "✓ Клип сохранён") + (c.Game != null ? " → " + c.Game : ""));
                        app.ClipSound();
                    });
                    continue;
                }
                LastClipPath = file;
                AnalyzeClip(file, c);
            }
        }

        // how many seconds the buffer has really been recording (if it restarted, the clip is legitimately shorter)
        double ReplayRunningSeconds()
        {
            try
            {
                foreach (var o in Json.GetArr(obs.Request("GetOutputList", null, ReqTimeout), "outputs"))
                {
                    var d = Json.Obj(o);
                    if (Json.GetStr(d, "outputKind") != "replay_buffer") continue;
                    var s = obs.Request("GetOutputStatus", Json.D("outputName", Json.GetStr(d, "outputName")), ReqTimeout);
                    object dur;
                    s.TryGetValue("outputDuration", out dur);
                    return ToD(dur) / 1000.0;
                }
            }
            catch (ObsException) { }
            return -1;
        }

        static string Secs(double s) { return ((int)Math.Round(s)) + L.T(" s", " с"); }

        void AnalyzeClip(string file, PendingClip c)
        {
            var card = ClipInfo.Of(file, GameOfClip(file, c));
            if (!Cfg.ClipCheck)
            {
                app.Post(() => app.ClipSaved(card));
                return;
            }
            Mp4Info info = null;
            try { info = Mp4.Read(file); } catch (Exception ex) { Log.Write("clip parse: " + ex.Message); }
            long sizeMb = 0;
            try { sizeMb = new FileInfo(file).Length / 1048576; } catch { }

            string sec = OutSection();
            int rbTime, rbSize, mask;
            int.TryParse(ProfileParam(sec, "RecRBTime"), out rbTime);
            int.TryParse(ProfileParam(sec, "RecRBSize"), out rbSize);
            int.TryParse(ProfileParam(sec, "RecTracks"), out mask);
            int wantTracks = Enumerable.Range(0, 6).Count(i => (mask & (1 << i)) != 0);

            double expect = rbTime;
            double running = ReplayRunningSeconds();
            if (running < 0 && rbStartedAt != DateTime.MinValue) running = (c.SavedAt - rbStartedAt).TotalSeconds;
            if (running >= 0 && running < expect) expect = running;

            var issues = new List<string>();
            if (info == null) issues.Add(L.T("could not read the file — it may be damaged", "не удалось прочитать файл — возможно, он повреждён"));
            else
            {
                if (info.Duration >= 0 && expect > 0 && info.Duration < expect - 6)
                {
                    string why = rbSize > 0 && sizeMb >= rbSize * 0.85
                        ? L.T(" — hit the \"Maximum memory\" of " + rbSize + " MB, raise it in the replay buffer settings", " — упёрся в «Максимум памяти» " + rbSize + " МБ, увеличь его в настройках буфера повтора")
                        : "";
                    issues.Add(L.T("length " + Secs(info.Duration) + " of " + Secs(expect), "длина " + Secs(info.Duration) + " из " + Secs(expect)) + why);
                }
                if (wantTracks > 0 && info.Audio != wantTracks) issues.Add(L.T(info.Audio + " of " + wantTracks + " audio tracks", "звуковых дорожек " + info.Audio + " из " + wantTracks));
                if (info.Video == 0) issues.Add(L.T("the file has no video", "в файле нет видео"));
            }

            // dropped frames during the clip: noticeable — a problem, a few — just mention in the summary
            string lossNote = null;
            var loss = ClipLoss(c.SavedAt, info != null && info.Duration > 0 ? info.Duration : expect);
            if (loss != null)
            {
                Log.Write("clip: render skipped " + loss.RenderSkip + " of " + loss.RenderTotal + ", encoder " + loss.OutSkip +
                          " in " + (int)loss.Seconds + " s");
                if (FrameStats.Noticeable(loss, 2.0, 15))
                    issues.Add(L.T(loss.Lost + " frames dropped (" + FrameStats.PctText(loss.Pct) + ") — the video stutters in places: ", "потеряно " + loss.Lost + " кадров (" + FrameStats.PctText(loss.Pct) + ") — видео местами дёргается: ") + FrameStats.Cause(loss));
                else if (FrameStats.Noticeable(loss, 0.5, 5))
                    lossNote = L.T(FrameStats.PctText(loss.Pct) + " frames dropped", "потеряно " + FrameStats.PctText(loss.Pct) + " кадров");
            }

            string summary = (info != null && info.Duration >= 0 ? Secs(info.Duration) : "?" + L.T(" s", " с")) +
                             (info != null ? L.T(" · tracks: ", " · дорожек: ") + info.Audio : "") + " · " + sizeMb + L.T(" MB", " МБ") +
                             (lossNote != null ? " · " + lossNote : "");
            // ClipStats reads this line back for the statistics: "clip <file>: <seconds> s ... <size> MB"
            Log.Write("clip " + Path.GetFileName(file) + ": " + (info != null && info.Duration >= 0 ? (int)Math.Round(info.Duration) + " s" : "? s") +
                      (info != null ? " · tracks: " + info.Audio : "") + " · " + sizeMb + " MB" +
                      (issues.Count > 0 ? " — " + string.Join("; ", issues) : ""));
            uiClipText = (info != null && info.Duration >= 0 ? Secs(info.Duration) : "?" + L.T(" s", " с")) +
                         (info != null ? " · " + L.N(info.Audio, "track", "tracks", "дорожка", "дорожки", "дорожек") : "");
            uiClipNote = issues.Count > 0 ? string.Join("; ", issues) : sizeMb + L.T(" MB", " МБ");
            uiClipOk = issues.Count == 0;
            uiClipAt = DateTime.Now;
            if (info != null && info.Duration >= 0) card.Duration = info.Duration;
            if (info != null) card.Tracks = info.Audio;
            card.Note = lossNote;
            card.Issues = issues;
            if (issues.Count == 0) Ev(L.T("✓ clip saved: ", "✓ клип сохранён: ") + summary);
            else Ev(L.T("⚠ clip with a problem: ", "⚠ клип с проблемой: ") + string.Join("; ", issues));
            app.Post(() => app.ClipSaved(card));
        }

        // the game for the card: what sorting decided, or the game folder the clip is in (Smart Replay Mover)
        string GameOfClip(string file, PendingClip c)
        {
            if (c.Game != null) return c.Game;
            string root = ClipsRoot;
            if (string.IsNullOrEmpty(root) || !ClipScanner.SubfoldersAreGames) return null;
            string g = ClipScanner.GameOf(new FileInfo(file), root.TrimEnd('\\'));
            return NoGame.Is(g) || !Covers.IsGame(g) ? null : g;
        }

        // ── 6. graphics driver failure (Windows event log) ──────────────────
        volatile bool driverEvent;
        DateTime lastDriverNotice = DateTime.MinValue;

        void StartDriverWatch()
        {
            if (!Cfg.DriverWatch) return;
            try
            {
                // 4101 — "Display driver stopped responding and has recovered"; errors of the NVIDIA driver itself
                var q = new EventLogQuery("System", PathType.LogName,
                    "*[System[(Provider[@Name='Display'] and EventID=4101) or (Provider[@Name='nvlddmkm'] and (Level=1 or Level=2))]]");
                drvWatcher = new EventLogWatcher(q);
                drvWatcher.EventRecordWritten += (s, e) =>
                {
                    if (e.EventRecord == null) return;
                    try { Log.Write("Windows event log: " + e.EventRecord.ProviderName + " #" + e.EventRecord.Id); } catch { }
                    e.EventRecord.Dispose();
                    driverEvent = true;
                    trigger.Set();
                };
                drvWatcher.Enabled = true;
            }
            catch (Exception ex) { Log.Write("could not watch the Windows event log: " + ex.Message); }
        }

        // ── 7. daily backup of OBS settings ─────────────────────────────────
        DateTime lastBackupDay = DateTime.MinValue;

        void DailyBackup(DateTime now)
        {
            if (!Cfg.Backup || now.Date == lastBackupDay) return;
            lastBackupDay = now.Date;
            try
            {
                string src = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "obs-studio", "basic");
                if (!Directory.Exists(src)) return;
                string root = Path.Combine(Program.Dir, "backups");
                string dst = Path.Combine(root, now.ToString("yyyy-MM-dd"));
                if (Directory.Exists(dst)) return;
                CopyDir(src, dst);
                // only its own dated folders: other copies there (obs-scenes-… from ObsScript) are not counted or touched
                foreach (var d in new DirectoryInfo(root).GetDirectories().Where(x => System.Text.RegularExpressions.Regex.IsMatch(x.Name, @"^\d{4}-\d{2}-\d{2}$"))
                                                                          .OrderByDescending(d => d.Name).Skip(7))
                    d.Delete(true);
                Ev(L.T("OBS settings backup: backups\\", "резервная копия настроек OBS: backups\\") + now.ToString("yyyy-MM-dd"));
            }
            catch (Exception ex) { Log.Write("OBS settings backup: " + ex.Message); }
        }

        static void CopyDir(string src, string dst)
        {
            Directory.CreateDirectory(dst);
            foreach (var f in Directory.GetFiles(src))
            {
                var fi = new FileInfo(f);
                if (fi.Length < 50L * 1024 * 1024) File.Copy(f, Path.Combine(dst, fi.Name), true);
            }
            foreach (var d in Directory.GetDirectories(src)) CopyDir(d, Path.Combine(dst, Path.GetFileName(d)));
        }
    }
}
