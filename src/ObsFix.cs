using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace DeviceGuard
{
    // What OBS needs so a key press becomes a clip — the WebSocket server, the replay buffer, a "Save Replay" key — read
    // from the files OBS keeps on this computer, and set there when the first-run check finds it missing.
    // OBS writes its settings back when it closes, so a fix is written only while OBS is closed: at once if it is,
    // otherwise it waits — for "Restart OBS" in ClipKeeper (Guard.LaunchObs) or for OBS to close by itself.
    // Every file is copied to backups\ before it is changed.
    static class ObsFix
    {
        public class Status
        {
            public bool Settings;             // OBS settings exist (OBS was started at least once)
            public bool WsOn;
            public string WsFile;
            public string Profile, ProfileIni;
            public bool RbOn;
            public string Key;                // "Ctrl+F8", null — none
        }

        static string ObsDir { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "obs-studio"); } }

        public static Status Read()
        {
            var s = new Status();
            var f = ObsFind.Read();
            s.Settings = f != null || Directory.Exists(Path.Combine(ObsDir, "basic"));
            s.WsOn = f != null && f.Enabled;
            s.WsFile = f != null ? f.From : null;
            s.ProfileIni = ProfileIni(out s.Profile);
            if (s.ProfileIni != null)
            {
                var ini = File.ReadAllLines(s.ProfileIni, Encoding.UTF8);
                s.RbOn = Get(ini, OutSection(ini), "RecRB") == "true";
                string hk = Get(ini, "Hotkeys", "ReplayBuffer");
                try
                {
                    var keys = hk == null ? new List<SaveKey.Combo>() : SaveKey.Parse(hk);
                    s.Key = keys.Count > 0 ? keys[0].ToString() : null;
                }
                catch (Exception ex) { Log.Write("OBS save key in " + s.ProfileIni + ": " + ex.Message); }
            }
            return s;
        }

        static string OutSection(string[] ini) { return Get(ini, "Output", "Mode") == "Advanced" ? "AdvOut" : "SimpleOutput"; }

        // the current profile: [Basic] ProfileDir of user.ini (OBS 31+) or global.ini, whichever is newer
        static string ProfileIni(out string name)
        {
            name = null;
            string best = null;
            DateTime at = DateTime.MinValue;
            foreach (var g in new[] { "user.ini", "global.ini" }.Select(n => Path.Combine(ObsDir, n)).Where(File.Exists))
            {
                var lines = File.ReadAllLines(g, Encoding.UTF8);
                string dir = Get(lines, "Basic", "ProfileDir");
                if (dir == null || File.GetLastWriteTime(g) < at) continue;
                string ini = Path.Combine(ObsDir, "basic", "profiles", dir, "basic.ini");
                if (!File.Exists(ini)) continue;
                at = File.GetLastWriteTime(g);
                best = ini;
                name = Get(lines, "Basic", "Profile") ?? dir;
            }
            return best;
        }

        public static string Get(string[] ini, string section, string key)
        {
            bool inSection = false;
            foreach (var raw in ini)
            {
                string line = raw.Trim();
                if (line.StartsWith("[")) { inSection = line.Equals("[" + section + "]", StringComparison.OrdinalIgnoreCase); continue; }
                int eq = line.IndexOf('=');
                if (inSection && eq > 0 && line.Substring(0, eq).Trim() == key) return line.Substring(eq + 1).Trim();
            }
            return null;
        }

        // key=value in a section: replaced where it is, added at the end of the section, or a new section at the end
        public static string[] Set(string[] ini, string section, string key, string value)
        {
            var lines = ini.ToList();
            int start = lines.FindIndex(l => l.Trim().Equals("[" + section + "]", StringComparison.OrdinalIgnoreCase));
            if (start < 0)
            {
                if (lines.Count > 0 && lines[lines.Count - 1].Trim().Length > 0) lines.Add("");
                lines.Add("[" + section + "]");
                lines.Add(key + "=" + value);
                return lines.ToArray();
            }
            int end = lines.FindIndex(start + 1, l => l.Trim().StartsWith("["));
            if (end < 0) end = lines.Count;
            for (int i = start + 1; i < end; i++)
            {
                int eq = lines[i].IndexOf('=');
                if (eq > 0 && lines[i].Substring(0, eq).Trim() == key) { lines[i] = key + "=" + value; return lines.ToArray(); }
            }
            int at = end;
            while (at > start + 1 && lines[at - 1].Trim().Length == 0) at--;   // before the blank line that ends the section
            lines.Insert(at, key + "=" + value);
            return lines.ToArray();
        }

        // ── the fixes ──
        static readonly List<Tuple<string, Func<string>>> pending = new List<Tuple<string, Func<string>>>();
        public static bool Pending { get { lock (pending) return pending.Count > 0; } }

        public static string EnableWebSocket(Status s)
        {
            if (s.WsFile == null) return L.T("OBS settings were not found — start OBS once", "Настройки OBS не найдены — запусти OBS один раз");
            string file = s.WsFile;
            return Fix("websocket", () => file.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? EditJson(file) : Edit(file, "OBSWebSocket", "ServerEnabled", "true"));
        }

        public static string EnableReplayBuffer(Status s)
        {
            if (s.ProfileIni == null) return L.T("The OBS profile was not found — start OBS once", "Профиль OBS не найден — запусти OBS один раз");
            string file = s.ProfileIni;
            return Fix("replay buffer", () => Edit(file, OutSection(File.ReadAllLines(file, Encoding.UTF8)), "RecRB", "true"));
        }

        // obsKey: "OBS_KEY_F8"; the start and stop keys of the buffer stay as they were
        public static string SetSaveKey(Status s, string obsKey, bool ctrl, bool shift, bool alt)
        {
            if (s.ProfileIni == null) return L.T("The OBS profile was not found — start OBS once", "Профиль OBS не найден — запусти OBS один раз");
            string file = s.ProfileIni;
            return Fix("save key", () =>
            {
                var ini = File.ReadAllLines(file, Encoding.UTF8);
                string old = Get(ini, "Hotkeys", "ReplayBuffer");
                var d = old != null ? Json.Obj(Json.Parse(old.Replace("\\n", "\n"))) ?? new Dictionary<string, object>() : new Dictionary<string, object>();
                var bind = Json.D("key", obsKey);
                if (ctrl) bind["control"] = true;
                if (shift) bind["shift"] = true;
                if (alt) bind["alt"] = true;
                d["ReplayBuffer.Save"] = new List<object> { bind };
                return Write(file, Set(ini, "Hotkeys", "ReplayBuffer", Json.Write(d, false).Replace("\n", "")));
            });
        }

        // now, if OBS is closed; otherwise it waits (WaitObs). null — done
        static string Fix(string what, Func<string> edit)
        {
            if (!ObsScript.ObsRunning())
            {
                string err = edit();
                Log.Write("OBS fix " + what + ": " + (err ?? "done"));
                return err;
            }
            lock (pending)
            {
                pending.RemoveAll(p => p.Item1 == what);
                pending.Add(Tuple.Create(what, edit));
            }
            Log.Write("OBS fix " + what + ": waits for OBS to close");
            return ObsScript.WaitObs;
        }

        // OBS is closed: what waited is written now (from Guard.LaunchObs, or a check that sees OBS gone)
        public static void ApplyPending()
        {
            List<Tuple<string, Func<string>>> todo;
            lock (pending)
            {
                if (pending.Count == 0 || ObsScript.ObsRunning()) return;
                todo = pending.ToList();
                pending.Clear();
            }
            foreach (var p in todo)
            {
                string err;
                try { err = p.Item2(); } catch (Exception ex) { err = ex.Message; }
                Log.Write("OBS fix " + p.Item1 + ": " + (err ?? "done"));
            }
        }

        static string Edit(string file, string section, string key, string value)
        {
            try { return Write(file, Set(File.ReadAllLines(file, Encoding.UTF8), section, key, value)); }
            catch (Exception ex) { return ex.Message; }
        }

        static string EditJson(string file)
        {
            try
            {
                var d = Json.Obj(Json.Parse(File.ReadAllText(file, Encoding.UTF8)));
                if (d == null) return L.T("can't read ", "не читается ") + file;
                d["server_enabled"] = true;
                Backup(file);
                File.WriteAllText(file, Json.Write(d, true), new UTF8Encoding(false));
                return null;
            }
            catch (Exception ex) { return ex.Message; }
        }

        // OBS writes its .ini files as UTF-8 with a BOM; so do we
        static string Write(string file, string[] lines)
        {
            try
            {
                Backup(file);
                File.WriteAllText(file, string.Join("\r\n", lines) + "\r\n", new UTF8Encoding(true));
                return null;
            }
            catch (Exception ex) { return ex.Message; }
        }

        static void Backup(string file)
        {
            string dir = Path.Combine(Program.Dir, "backups");
            Directory.CreateDirectory(dir);
            string name = Path.GetFileName(Path.GetDirectoryName(file)) + "-" + Path.GetFileName(file) + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
            File.Copy(file, Path.Combine(dir, name), true);
        }

        // a Windows key → its OBS name ("OBS_KEY_F8"), null — OBS has no such key
        public static string ObsKeyName(int vk)
        {
            var names = Enumerable.Range('A', 26).Select(c => ((char)c).ToString())
                .Concat(Enumerable.Range(0, 10).Select(n => n.ToString()))
                .Concat(Enumerable.Range(1, 24).Select(n => "F" + n))
                .Concat(Enumerable.Range(0, 10).Select(n => "NUM" + n))
                .Concat(new[] { "SPACE", "RETURN", "TAB", "BACKSPACE", "INSERT", "DELETE", "HOME", "END", "PAGEUP", "PAGEDOWN", "LEFT", "UP", "RIGHT", "DOWN",
                                "PRINT", "PAUSE", "SCROLLLOCK", "NUMASTERISK", "NUMPLUS", "NUMMINUS", "NUMPERIOD", "NUMSLASH", "MINUS", "EQUAL",
                                "BRACKETLEFT", "BRACKETRIGHT", "SEMICOLON", "APOSTROPHE", "QUOTELEFT", "COMMA", "PERIOD", "SLASH", "BACKSLASH" });
            return names.Select(n => "OBS_KEY_" + n).FirstOrDefault(n => SaveKey.Vk(n) == vk);
        }

        // for the self-test: reading and writing an OBS profile without touching OBS
        public static void Test(Action<bool, string> check)
        {
            var ini = new[] { "[General]", "Name=Main", "", "[Output]", "Mode=Advanced", "", "[AdvOut]", "RecRB=false", "RecRBTime=120", "", "[Video]", "BaseCX=1920" };
            var on = Set(ini, OutSection(ini), "RecRB", "true");
            check(Get(on, "AdvOut", "RecRB") == "true" && Get(on, "AdvOut", "RecRBTime") == "120" && on.Length == ini.Length,
                  "OBS check: the replay buffer is turned on in the advanced output, nothing else moves");
            var simple = Set(new[] { "[Output]", "Mode=Simple", "", "[SimpleOutput]", "FilePath=D:\\Sources", "", "[Video]" }, "SimpleOutput", "RecRB", "true");
            check(Get(simple, "SimpleOutput", "RecRB") == "true" && Array.IndexOf(simple, "RecRB=true") == 5, "OBS check: a missing key goes at the end of its section");
            var hk = Set(new[] { "[General]" }, "Hotkeys", "ReplayBuffer", "{\"ReplayBuffer.Save\":[{\"key\":\"OBS_KEY_F8\",\"control\":true}]}");
            var keys = SaveKey.Parse(Get(hk, "Hotkeys", "ReplayBuffer"));
            check(keys.Count == 1 && keys[0].Ctrl && keys[0].Vk == 0x77, "OBS check: a new section with the save key that SaveKey reads back");
            check(ObsKeyName(0x77) == "OBS_KEY_F8" && ObsKeyName('K') == "OBS_KEY_K" && ObsKeyName(0x6B) == "OBS_KEY_NUMPLUS" && ObsKeyName(0xFF) == null,
                  "OBS check: Windows keys get their OBS names");
        }
    }
}
