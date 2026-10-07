using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;
using Microsoft.Win32;

namespace DeviceGuard
{
    // ── JSON: parsing via JavaScriptSerializer, writing is our own (indented) ──
    static class Json
    {
        static readonly JavaScriptSerializer Ser = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };

        public static object Parse(string s) { return Ser.DeserializeObject(s); }

        public static Dictionary<string, object> Obj(object o) { return o as Dictionary<string, object>; }

        public static Dictionary<string, object> D(params object[] kv)
        {
            var d = new Dictionary<string, object>();
            for (int i = 0; i + 1 < kv.Length; i += 2) d[(string)kv[i]] = kv[i + 1];
            return d;
        }

        public static string GetStr(Dictionary<string, object> d, string key)
        {
            object v;
            if (d == null || !d.TryGetValue(key, out v) || v == null) return null;
            return Convert.ToString(v, CultureInfo.InvariantCulture);
        }

        public static int GetInt(Dictionary<string, object> d, string key, int def)
        {
            object v;
            if (d == null || !d.TryGetValue(key, out v) || v == null) return def;
            try { return Convert.ToInt32(v, CultureInfo.InvariantCulture); } catch { return def; }
        }

        public static bool GetBool(Dictionary<string, object> d, string key, bool def)
        {
            object v;
            if (d == null || !d.TryGetValue(key, out v) || !(v is bool)) return def;
            return (bool)v;
        }

        public static Dictionary<string, object> GetObj(Dictionary<string, object> d, string key)
        {
            object v;
            if (d == null || !d.TryGetValue(key, out v)) return null;
            return v as Dictionary<string, object>;
        }

        public static object[] GetArr(Dictionary<string, object> d, string key)
        {
            object v;
            if (d == null || !d.TryGetValue(key, out v)) return new object[0];
            return (v as object[]) ?? new object[0];
        }

        public static string Write(object o, bool pretty)
        {
            var sb = new StringBuilder();
            W(sb, o, pretty, 0);
            return sb.ToString();
        }

        static void W(StringBuilder sb, object o, bool pretty, int ind)
        {
            if (o == null) { sb.Append("null"); return; }
            if (o is string) { Q(sb, (string)o); return; }
            if (o is bool) { sb.Append((bool)o ? "true" : "false"); return; }
            if (o is int || o is long || o is double || o is decimal || o is float)
            {
                sb.Append(Convert.ToString(o, CultureInfo.InvariantCulture));
                return;
            }
            var dict = o as IDictionary;
            if (dict != null)
            {
                if (dict.Count == 0) { sb.Append("{}"); return; }
                sb.Append('{');
                int i = 0;
                foreach (DictionaryEntry e in dict)
                {
                    NewLine(sb, pretty, ind + 1);
                    Q(sb, Convert.ToString(e.Key, CultureInfo.InvariantCulture));
                    sb.Append(pretty ? ": " : ":");
                    W(sb, e.Value, pretty, ind + 1);
                    if (++i < dict.Count) sb.Append(',');
                }
                NewLine(sb, pretty, ind);
                sb.Append('}');
                return;
            }
            var list = o as IEnumerable;
            if (list != null)
            {
                var items = new List<object>();
                foreach (var x in list) items.Add(x);
                if (items.Count == 0) { sb.Append("[]"); return; }
                sb.Append('[');
                for (int i = 0; i < items.Count; i++)
                {
                    NewLine(sb, pretty, ind + 1);
                    W(sb, items[i], pretty, ind + 1);
                    if (i + 1 < items.Count) sb.Append(',');
                }
                NewLine(sb, pretty, ind);
                sb.Append(']');
                return;
            }
            Q(sb, o.ToString());
        }

        static void NewLine(StringBuilder sb, bool pretty, int ind)
        {
            if (!pretty) return;
            sb.Append('\n');
            sb.Append(' ', ind * 2);
        }

        static void Q(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }

        public static void WriteFile(string path, object o)
        {
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, Write(o, true), new UTF8Encoding(false));
            if (File.Exists(path)) File.Replace(tmp, path, null);
            else File.Move(tmp, path);
        }
    }

    // ── file log ─────────────────────────────────────────────────────────────
    static class Log
    {
        static readonly object Sync = new object();
        public static string FilePath;

        public static void Write(string msg)
        {
            lock (Sync)
            {
                try
                {
                    var fi = new FileInfo(FilePath);
                    if (fi.Exists && fi.Length > 1024 * 1024)
                    {
                        File.Copy(FilePath, FilePath + ".old", true);
                        File.Delete(FilePath);
                    }
                    File.AppendAllText(FilePath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + msg +
                                       Environment.NewLine, Encoding.UTF8);
                }
                catch { }
            }
        }
    }

    // ── program settings (settings.json next to the exe) ─────────────────────
    class Settings
    {
        public string Host = "127.0.0.1";
        public int Port = 4455;
        public string PasswordDpapi = "";   // password encrypted with DPAPI for the current Windows user
        public int IntervalSec = 5, GraceSec = 10, SoundRepeatSec = 15, SoundVolume = 40;
        public bool RestartObsOnCrash = true, WatchReplayBuffer = true, RbAutoRestart = true,
                    ReplayBufferMustRun = true, KickMonitor = true, AlertWindow = true,
                    AlertSound = true, NotifyFixed = true, ObsMinimized = false,
                    AudioStallCheck = true, ScreenCheck = true, ClipCheck = true, ClipToast = true,
                    GuardMixer = true, DriverWatch = true, Backup = true, PerfCheck = true, HideFromCapture = true;
        public int MinFreeGB = 10, ScreenIntervalSec = 15;
        public string SoundFile = @"C:\Windows\Media\Alarm01.wav", ObsPath = "", TrimFolder = "", CollectionFolder = "",
                      MainBounds = "", TrimBounds = "",                // window size and position: "x;y;w;h;max"
                      TrimMode = "lossless", TrimTarget = "discord",   // editor: last mode and share target
                      Language = L.Auto;                               // interface: auto (Windows language), en, ru
        public int TrimVolume = 85;                             // editor: preview volume, %
        public bool TrimLanes;                                  // editor: separate per-track lanes on the timeline
        public int ShareCustomMb = 25;                          // editor: the "custom" share target size
        public string Encoder = "auto", KeyBinds = "";          // video encoder (Encoders); editor key overrides (KeyMap)
        // features: the recording guard and the library can be turned off separately; the rest tunes them to the user's setup
        public bool GuardEnabled = true, LibraryEnabled = true, UseReplayBuffer = true, SubfoldersAreGames = true,
                    OnlineCovers = true, UseCollection = true;
        public bool SetupDone = true;                           // the first-run setup was shown (false only for a new settings.json)
        // sorting saved files into game folders (Sorter)
        public bool SortClips, SortPrefix = true, SortRecordings = true, SortScreenshots = true, SortImported;
        public string SortTemplate = @"{game}\{year}-{month}", SortFallback = "Desktop", SortNames = "";
        public bool UpdateCheck = true;                         // ask GitHub for a new version once a day (Updates)
        public string UpdateFailed = "";                        // a version that crashed here and was rolled back: not offered by itself
        // "Clip saved" sound: its own file and volume, set like the alarm one
        public bool ClipSound = true, ClipInstant = true;   // ClipInstant: confirm the OBS save key press at once (SaveKey)
        public int ClipSoundVolume = 30;
        public string ClipSoundFile = @"C:\Windows\Media\Windows Notify System Generic.wav";
        // global hotkeys for the last clip ("Ctrl+Alt+F9"; empty — none): editor, favorite, copy, show the card again
        public string HotkeyTrim = "", HotkeyFav = "", HotkeyCopy = "", HotkeyCard = "";
        // cleanup of old source clips (Cleanup): off by default
        public bool CleanupAuto, CleanupKeepTrimmed = true;
        public bool ObsStartScript;                             // "Start ClipKeeper together with OBS" (ObsScript), off by default
        public int CleanupDays = 30;
        public string CleanupLastRun = "";                      // "yyyy-MM-dd HH:mm" — the automatic run waits a day after it

        public bool IsNew;
        string path;
        static readonly byte[] Entropy = Encoding.UTF8.GetBytes("OBSDeviceGuard");

        public static Settings Load(string path)
        {
            var s = new Settings { path = path };
            if (!File.Exists(path)) { s.IsNew = true; s.SetupDone = false; return s; }
            try
            {
                var d = Json.Obj(Json.Parse(File.ReadAllText(path, Encoding.UTF8)));
                s.Host = Json.GetStr(d, "Host") ?? s.Host;
                s.Port = Json.GetInt(d, "Port", s.Port);
                s.PasswordDpapi = Json.GetStr(d, "PasswordDpapi") ?? "";
                s.IntervalSec = Json.GetInt(d, "IntervalSec", s.IntervalSec);
                s.GraceSec = Json.GetInt(d, "GraceSec", s.GraceSec);
                s.SoundRepeatSec = Json.GetInt(d, "SoundRepeatSec", s.SoundRepeatSec);
                s.SoundVolume = Json.GetInt(d, "SoundVolume", s.SoundVolume);
                s.RestartObsOnCrash = Json.GetBool(d, "RestartObsOnCrash", s.RestartObsOnCrash);
                s.WatchReplayBuffer = Json.GetBool(d, "WatchReplayBuffer", s.WatchReplayBuffer);
                s.RbAutoRestart = Json.GetBool(d, "RbAutoRestart", s.RbAutoRestart);
                s.ReplayBufferMustRun = Json.GetBool(d, "ReplayBufferMustRun", s.ReplayBufferMustRun);
                s.KickMonitor = Json.GetBool(d, "KickMonitor", s.KickMonitor);
                s.AlertWindow = Json.GetBool(d, "AlertWindow", s.AlertWindow);
                s.AlertSound = Json.GetBool(d, "AlertSound", s.AlertSound);
                s.NotifyFixed = Json.GetBool(d, "NotifyFixed", s.NotifyFixed);
                s.ObsMinimized = Json.GetBool(d, "ObsMinimized", s.ObsMinimized);
                s.AudioStallCheck = Json.GetBool(d, "AudioStallCheck", s.AudioStallCheck);
                s.ScreenCheck = Json.GetBool(d, "ScreenCheck", s.ScreenCheck);
                s.ClipCheck = Json.GetBool(d, "ClipCheck", s.ClipCheck);
                s.ClipToast = Json.GetBool(d, "ClipToast", s.ClipToast);
                s.GuardMixer = Json.GetBool(d, "GuardMixer", s.GuardMixer);
                s.DriverWatch = Json.GetBool(d, "DriverWatch", s.DriverWatch);
                s.Backup = Json.GetBool(d, "Backup", s.Backup);
                s.PerfCheck = Json.GetBool(d, "PerfCheck", s.PerfCheck);
                s.HideFromCapture = Json.GetBool(d, "HideFromCapture", s.HideFromCapture);
                s.MinFreeGB = Json.GetInt(d, "MinFreeGB", s.MinFreeGB);
                s.ScreenIntervalSec = Json.GetInt(d, "ScreenIntervalSec", s.ScreenIntervalSec);
                s.SoundFile = Json.GetStr(d, "SoundFile") ?? s.SoundFile;
                s.ObsPath = Json.GetStr(d, "ObsPath") ?? "";
                s.TrimFolder = Json.GetStr(d, "TrimFolder") ?? "";
                s.CollectionFolder = Json.GetStr(d, "CollectionFolder") ?? "";
                s.MainBounds = Json.GetStr(d, "MainBounds") ?? "";
                s.TrimBounds = Json.GetStr(d, "TrimBounds") ?? "";
                s.TrimMode = Json.GetStr(d, "TrimMode") ?? s.TrimMode;
                s.TrimTarget = Json.GetStr(d, "TrimTarget") ?? s.TrimTarget;
                s.TrimVolume = Json.GetInt(d, "TrimVolume", s.TrimVolume);
                s.TrimLanes = Json.GetBool(d, "TrimLanes", s.TrimLanes);
                s.Language = Json.GetStr(d, "Language") ?? s.Language;
                s.ShareCustomMb = Json.GetInt(d, "ShareCustomMb", s.ShareCustomMb);
                s.Encoder = Json.GetStr(d, "Encoder") ?? s.Encoder;
                s.KeyBinds = Json.GetStr(d, "KeyBinds") ?? "";
                s.GuardEnabled = Json.GetBool(d, "GuardEnabled", s.GuardEnabled);
                s.LibraryEnabled = Json.GetBool(d, "LibraryEnabled", s.LibraryEnabled);
                s.UseReplayBuffer = Json.GetBool(d, "UseReplayBuffer", s.UseReplayBuffer);
                s.SubfoldersAreGames = Json.GetBool(d, "SubfoldersAreGames", s.SubfoldersAreGames);
                s.OnlineCovers = Json.GetBool(d, "OnlineCovers", s.OnlineCovers);
                s.UseCollection = Json.GetBool(d, "UseCollection", s.UseCollection);
                s.SortClips = Json.GetBool(d, "SortClips", s.SortClips);
                s.SortPrefix = Json.GetBool(d, "SortPrefix", s.SortPrefix);
                s.SortRecordings = Json.GetBool(d, "SortRecordings", s.SortRecordings);
                s.SortScreenshots = Json.GetBool(d, "SortScreenshots", s.SortScreenshots);
                s.SortImported = Json.GetBool(d, "SortImported", s.SortImported);
                s.SortTemplate = Json.GetStr(d, "SortTemplate") ?? s.SortTemplate;
                s.SortFallback = Json.GetStr(d, "SortFallback") ?? s.SortFallback;
                s.SortNames = Json.GetStr(d, "SortNames") ?? "";
                s.UpdateCheck = Json.GetBool(d, "UpdateCheck", s.UpdateCheck);
                s.UpdateFailed = Json.GetStr(d, "UpdateFailed") ?? "";
                s.ClipSound = Json.GetBool(d, "ClipSound", s.ClipSound);
                s.ClipInstant = Json.GetBool(d, "ClipInstant", s.ClipInstant);
                s.ClipSoundVolume = Json.GetInt(d, "ClipSoundVolume", s.ClipSoundVolume);
                s.ClipSoundFile = Json.GetStr(d, "ClipSoundFile") ?? s.ClipSoundFile;
                s.HotkeyTrim = Json.GetStr(d, "HotkeyTrim") ?? "";
                s.HotkeyFav = Json.GetStr(d, "HotkeyFav") ?? "";
                s.HotkeyCopy = Json.GetStr(d, "HotkeyCopy") ?? "";
                s.HotkeyCard = Json.GetStr(d, "HotkeyCard") ?? "";
                s.CleanupAuto = Json.GetBool(d, "CleanupAuto", s.CleanupAuto);
                s.ObsStartScript = Json.GetBool(d, "ObsStartScript", s.ObsStartScript);
                s.CleanupKeepTrimmed = Json.GetBool(d, "CleanupKeepTrimmed", s.CleanupKeepTrimmed);
                s.CleanupDays = Math.Max(7, Json.GetInt(d, "CleanupDays", s.CleanupDays));
                s.CleanupLastRun = Json.GetStr(d, "CleanupLastRun") ?? "";
                s.SetupDone = Json.GetBool(d, "SetupDone", true);   // older settings files: the user is already set up
            }
            catch (Exception ex) { Log.Write("settings.json could not be read: " + ex.Message); }
            return s;
        }

        public void Save()
        {
            Json.WriteFile(path, Json.D(
                "Host", Host, "Port", Port, "PasswordDpapi", PasswordDpapi,
                "IntervalSec", IntervalSec, "GraceSec", GraceSec, "SoundRepeatSec", SoundRepeatSec, "SoundVolume", SoundVolume,
                "RestartObsOnCrash", RestartObsOnCrash, "WatchReplayBuffer", WatchReplayBuffer,
                "RbAutoRestart", RbAutoRestart, "ReplayBufferMustRun", ReplayBufferMustRun,
                "KickMonitor", KickMonitor, "AlertWindow", AlertWindow, "AlertSound", AlertSound,
                "NotifyFixed", NotifyFixed, "ObsMinimized", ObsMinimized,
                "AudioStallCheck", AudioStallCheck, "ScreenCheck", ScreenCheck, "ClipCheck", ClipCheck,
                "ClipToast", ClipToast, "GuardMixer", GuardMixer, "DriverWatch", DriverWatch, "Backup", Backup, "PerfCheck", PerfCheck, "HideFromCapture", HideFromCapture,
                "MinFreeGB", MinFreeGB, "ScreenIntervalSec", ScreenIntervalSec,
                "SoundFile", SoundFile, "ObsPath", ObsPath, "TrimFolder", TrimFolder,
                "CollectionFolder", CollectionFolder, "MainBounds", MainBounds, "TrimBounds", TrimBounds,
                "TrimMode", TrimMode, "TrimTarget", TrimTarget, "TrimVolume", TrimVolume, "TrimLanes", TrimLanes, "Language", Language,
                "ShareCustomMb", ShareCustomMb, "Encoder", Encoder, "KeyBinds", KeyBinds,
                "GuardEnabled", GuardEnabled, "LibraryEnabled", LibraryEnabled, "UseReplayBuffer", UseReplayBuffer,
                "SubfoldersAreGames", SubfoldersAreGames, "OnlineCovers", OnlineCovers, "UseCollection", UseCollection,
                "SortClips", SortClips, "SortPrefix", SortPrefix, "SortRecordings", SortRecordings, "SortScreenshots", SortScreenshots,
                "SortImported", SortImported, "SortTemplate", SortTemplate, "SortFallback", SortFallback, "SortNames", SortNames,
                "UpdateCheck", UpdateCheck, "UpdateFailed", UpdateFailed, "ClipSound", ClipSound, "ClipInstant", ClipInstant, "ClipSoundVolume", ClipSoundVolume, "ClipSoundFile", ClipSoundFile,
                "HotkeyTrim", HotkeyTrim, "HotkeyFav", HotkeyFav, "HotkeyCopy", HotkeyCopy, "HotkeyCard", HotkeyCard,
                "CleanupAuto", CleanupAuto, "ObsStartScript", ObsStartScript, "CleanupKeepTrimmed", CleanupKeepTrimmed, "CleanupDays", CleanupDays, "CleanupLastRun", CleanupLastRun,
                "SetupDone", SetupDone));
            IsNew = false;
        }

        // switches that other parts read without a Settings reference
        public void Apply()
        {
            Encoders.Setting = Encoder;
            Covers.Online = OnlineCovers;
            ClipScanner.SubfoldersAreGames = SubfoldersAreGames;
            ClipScanner.OwnRoots = new[] { TrimFolder, CollectionFolder }.Where(x => !string.IsNullOrEmpty(x)).ToArray();
            KeyMap.Load(KeyBinds);
        }

        public bool HasPassword { get { return !string.IsNullOrEmpty(PasswordDpapi); } }

        public string GetPassword()
        {
            if (!HasPassword) return "";
            try
            {
                return Encoding.UTF8.GetString(ProtectedData.Unprotect(
                    Convert.FromBase64String(PasswordDpapi), Entropy, DataProtectionScope.CurrentUser));
            }
            catch { return ""; }
        }

        public void SetPassword(string pw)
        {
            PasswordDpapi = string.IsNullOrEmpty(pw) ? "" : Convert.ToBase64String(ProtectedData.Protect(
                Encoding.UTF8.GetBytes(pw), Entropy, DataProtectionScope.CurrentUser));
        }
    }

    // ── device reference (format compatible with device_guard.py v4) ─────────
    class RefEntry
    {
        public string Type, Prop, Value, ValType = "string", Display, LastName;
        // mixer (audio only; null — not remembered yet)
        public string Tracks;       // «1,3»
        public bool? Muted;
        public double? VolumeDb;
    }

    class RefStore
    {
        public static readonly Dictionary<string, string> Watched = new Dictionary<string, string>
        {
            { "wasapi_input_capture", "device_id" },
            { "wasapi_output_capture", "device_id" },
            { "monitor_capture", "monitor_id" },
        };

        public Dictionary<string, RefEntry> Items = new Dictionary<string, RefEntry>();
        public string RecTracks;   // which tracks are enabled in recording settings (OBS bitmask)

        public static RefStore Load(string path, string[] legacy, out string importedFrom)
        {
            importedFrom = null;
            var store = new RefStore();
            string src = path;
            if (!File.Exists(src))
            {
                src = null;
                foreach (var l in legacy) if (File.Exists(l)) { src = l; importedFrom = l; break; }
                if (src == null) return store;
            }
            var d = Json.Obj(Json.Parse(File.ReadAllText(src, Encoding.UTF8)));
            var raw = Json.GetInt(d, "version", 0) == 2 ? Json.GetObj(d, "sources") : d;
            if (raw == null) return store;
            store.RecTracks = Json.GetStr(d, "rec_tracks");
            foreach (var kv in raw)
            {
                var r = Json.Obj(kv.Value);
                string type = Json.GetStr(r, "type");
                if (r == null || type == null || !Watched.ContainsKey(type)) continue;
                string display = Json.GetStr(r, "display") ?? "";
                store.Items[kv.Key] = new RefEntry
                {
                    Type = type,
                    Prop = Watched[type],
                    Value = Json.GetStr(r, "value") ?? "",
                    ValType = Json.GetStr(r, "val_type") ?? "string",
                    Display = display,
                    LastName = Json.GetStr(r, "last_name") ?? display,
                    Tracks = Json.GetStr(r, "tracks"),
                };
                object mv, vv;
                if (r.TryGetValue("muted", out mv) && mv is bool) store.Items[kv.Key].Muted = (bool)mv;
                if (r.TryGetValue("volume_db", out vv) && vv != null)
                    store.Items[kv.Key].VolumeDb = Convert.ToDouble(vv, CultureInfo.InvariantCulture);
            }
            return store;
        }

        public void Save(string path)
        {
            var sources = new Dictionary<string, object>();
            foreach (var kv in Items)
                sources[kv.Key] = Json.D("type", kv.Value.Type, "prop", kv.Value.Prop, "value", kv.Value.Value,
                                         "val_type", kv.Value.ValType, "display", kv.Value.Display,
                                         "last_name", kv.Value.LastName, "tracks", kv.Value.Tracks,
                                         "muted", kv.Value.Muted, "volume_db", kv.Value.VolumeDb);
            Json.WriteFile(path, Json.D("version", 2, "saved_at", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                                        "rec_tracks", RecTracks, "sources", sources));
        }
    }

    // ── start with Windows ───────────────────────────────────────────────────
    static class Autostart
    {
        const string Key = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string Name = "ClipKeeper";
        const string OldName = "OBS Device Guard";   // name before the program was renamed

        public static string Command { get { return "\"" + System.Windows.Forms.Application.ExecutablePath + "\" --tray"; } }

        // rename: autostart was enabled under the old name — move it to the new name and exe
        public static void Migrate()
        {
            using (var k = Registry.CurrentUser.OpenSubKey(Key, true))
            {
                if (k == null) return;
                if (k.GetValue(OldName) != null)
                {
                    k.DeleteValue(OldName);
                    k.SetValue(Name, Command);
                    Log.Write("autostart moved: \"" + OldName + "\" → \"" + Name + "\"");
                    return;
                }
                // the program folder was moved — autostart is on but points to the old path
                var cur = k.GetValue(Name) as string;
                if (cur != null && !string.Equals(cur, Command, StringComparison.OrdinalIgnoreCase))
                {
                    k.SetValue(Name, Command);
                    Log.Write("autostart: path updated to " + Command);
                }
            }
        }

        public static bool IsOn()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(Key))
                    return k != null && string.Equals(k.GetValue(Name) as string, Command, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex) { Log.Write("autostart: " + ex.Message); return false; }
        }

        // false when the registry refuses (a policy, an antivirus) — the reason goes to the log and to error
        public static bool TrySet(bool on, out string error)
        {
            error = null;
            try { Set(on); return true; }
            catch (Exception ex)
            {
                error = ex.Message;
                Log.Write("autostart " + (on ? "on" : "off") + " failed: " + ex.Message);
                return false;
            }
        }

        public static void Set(bool on)
        {
            using (var k = Registry.CurrentUser.CreateSubKey(Key))
            {
                if (on) k.SetValue(Name, Command);
                else if (k.GetValue(Name) != null) k.DeleteValue(Name);
            }
        }
    }
}
