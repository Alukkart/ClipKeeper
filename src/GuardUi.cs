using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace DeviceGuard
{
    // Requests from the window: read a source and apply changes in OBS so they become the reference right away
    partial class Guard
    {
        public class SourceState
        {
            public string Type, Value, Tracks;
            public List<DevItem> Options = new List<DevItem>();
            public double VolumeDb;
            public bool Muted;
        }

        public class SourceEdit
        {
            public string Value, Name, Tracks;
            public double? VolumeDb;
            public bool? Muted;
        }

        class UiRequest { public Action Work; public Action<string> Fail; }

        readonly ConcurrentQueue<UiRequest> uiRequests = new ConcurrentQueue<UiRequest>();

        // for the gallery: where OBS saves clips and where the last one is
        public volatile string ClipsRoot, LastClipPath;
        public volatile string[] TrackTitles;

        void Enqueue(Action work, Action<string> fail)
        {
            uiRequests.Enqueue(new UiRequest { Work = work, Fail = fail });
            trigger.Set();
        }

        // runs on the worker thread at the start of a tick
        void RunUiRequests(bool connected)
        {
            UiRequest r;
            while (uiRequests.TryDequeue(out r))
            {
                if (!connected) { Fail(r, L.T("OBS is not connected", "OBS не подключён")); continue; }
                try { r.Work(); }
                catch (Exception ex)
                {
                    Log.Write("window request: " + ex);
                    Fail(r, ObsClient.Flatten(ex));
                }
            }
        }

        void Fail(UiRequest r, string msg)
        {
            var f = r.Fail;
            if (f != null) app.Post(() => f(msg));
        }

        // the common mix = the track all audio sources go to; returns the stream number in the file (0…) or -1
        public int MixAudioIndex()
        {
            lock (st)
            {
                var audio = refs.Items.Values.Where(r => Matcher.IsAudio(r.Type) && !string.IsNullOrEmpty(r.Tracks)).ToList();
                if (audio.Count < 2) return -1;
                var common = new HashSet<string>(audio[0].Tracks.Split(','));
                foreach (var r in audio.Skip(1)) common.IntersectWith(r.Tracks.Split(','));
                if (common.Count == 0) return -1;
                int mix = common.Select(int.Parse).Min();
                int mask;
                if (!int.TryParse(refs.RecTracks, out mask)) mask = 0x3F;
                if ((mask & (1 << (mix - 1))) == 0) return -1;
                int idx = 0;
                for (int t = 1; t < mix; t++) if ((mask & (1 << (t - 1))) != 0) idx++;
                return idx;
            }
        }

        RefEntry RefOf(string name)
        {
            RefEntry r;
            lock (st) refs.Items.TryGetValue(name, out r);
            return r;
        }

        public void RequestState(string name, Action<SourceState> done, Action<string> fail)
        {
            Enqueue(() =>
            {
                var r = RefOf(name);
                if (r == null) throw new InvalidOperationException(L.T("the source is not in the reference", "источника нет в эталоне"));
                var s = new SourceState { Type = r.Type };
                s.Value = CurrentValue(obs.Request("GetInputSettings", Json.D("inputName", name), ReqTimeout), r.Type, r.Prop);
                s.Options = Items(r.Type, name, new Dictionary<string, List<DevItem>>()) ?? new List<DevItem>();
                if (Matcher.IsAudio(r.Type))
                {
                    s.Tracks = TracksStr(Json.GetObj(obs.Request("GetInputAudioTracks", Json.D("inputName", name), ReqTimeout), "inputAudioTracks"));
                    s.Muted = Json.GetBool(obs.Request("GetInputMute", Json.D("inputName", name), ReqTimeout), "inputMuted", false);
                    s.VolumeDb = VolumeDb(obs.Request("GetInputVolume", Json.D("inputName", name), ReqTimeout));
                }
                app.Post(() => done(s));
            }, fail);
        }

        public void RequestApply(string name, SourceEdit e, Action<string> fail)
        {
            Enqueue(() =>
            {
                var r = RefOf(name);
                if (r == null) throw new InvalidOperationException(L.T("the source is not in the reference", "источника нет в эталоне"));
                var changes = new List<string>();
                if (e.Value != null)
                {
                    obs.Request("SetInputSettings", Json.D("inputName", name, "inputSettings", Json.D(r.Prop, e.Value), "overlay", true), ReqTimeout);
                    if (e.Value != r.Value) changes.Add(L.T("device", "устройство"));
                }
                if (e.Tracks != null)
                {
                    obs.Request("SetInputAudioTracks", Json.D("inputName", name, "inputAudioTracks", TracksObj(e.Tracks)), ReqTimeout);
                    if (e.Tracks != r.Tracks) changes.Add(L.T("tracks", "дорожки"));
                }
                if (e.VolumeDb.HasValue)
                {
                    obs.Request("SetInputVolume", Json.D("inputName", name, "inputVolumeDb", e.VolumeDb.Value), ReqTimeout);
                    if (!r.VolumeDb.HasValue || Math.Abs(r.VolumeDb.Value - e.VolumeDb.Value) > 0.05) changes.Add(L.T("volume", "громкость"));
                }
                if (e.Muted.HasValue)
                {
                    obs.Request("SetInputMute", Json.D("inputName", name, "inputMuted", e.Muted.Value), ReqTimeout);
                    if (r.Muted != e.Muted) changes.Add(e.Muted.Value ? L.T("muted", "звук выключен") : L.T("unmuted", "звук включён"));
                }
                lock (st)
                {
                    if (e.Value != null) { r.Value = e.Value; r.Display = r.LastName = e.Name ?? r.Display; }
                    if (e.Tracks != null) r.Tracks = e.Tracks;
                    if (e.VolumeDb.HasValue) r.VolumeDb = e.VolumeDb;
                    if (e.Muted.HasValue) r.Muted = e.Muted;
                    foreach (var k in problems.Keys.Where(k => k.EndsWith(":" + name)).ToList()) problems.Remove(k);
                }
                SaveRefs();
                string what = changes.Count > 0 ? string.Join(", ", changes) : L.T("no changes", "без изменений");
                Ev("✓ " + name + L.T(": changed in ClipKeeper and remembered (", ": изменено в ClipKeeper и запомнено (") + what + ")");
                app.Post(() => app.ShowToast("✓ " + name + L.T(": applied in OBS and remembered", ": применено в OBS и запомнено")));
            }, fail);
        }
    }
}
