using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace DeviceGuard
{
    // "Start ClipKeeper together with OBS": a small Lua script in OBS starts ClipKeeper whenever OBS starts — from a shortcut,
    // Steam or anywhere. Done in the open, only when the person turns it on:
    //   • the script is a normal OBS script, visible in OBS → Tools → Scripts with a description of what it does;
    //   • OBS files (the scene collections — scripts belong to them) are changed only while OBS is closed, because OBS writes
    //     them back on exit; asked while OBS runs, it is done the moment OBS closes (or by "Restart OBS");
    //   • each file is copied to backups\ before the first change; turning the setting off removes the script the same way.
    // The script lives in %APPDATA%\ClipKeeper, so moving the ClipKeeper folder does not break it: the exe path inside is
    // rewritten on every start.
    static class ObsScript
    {
        public const string FileName = "clipkeeper_start.lua";

        public static string Dir { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClipKeeper"); } }
        public static string ScriptPath { get { return Path.Combine(Dir, FileName); } }
        static string ScenesDir { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"obs-studio\basic\scenes"); } }

        public enum State { Off, On, Pending, NoObs, Error }
        public const string WaitObs = "obs-open";   // Sync: OBS is open — it is done once OBS closes

        public static bool ObsRunning()
        {
            var all = Process.GetProcessesByName("obs64").Concat(Process.GetProcessesByName("obs32")).Concat(Process.GetProcessesByName("obs")).ToList();
            bool any = all.Count > 0;
            foreach (var p in all) p.Dispose();
            return any;
        }

        // a Lua string literal: backslashes and quotes escaped, the rest (Cyrillic) stays UTF-8
        static string LuaString(string s)
        {
            return "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        }

        // OBS has LuaJIT with ffi: ShellExecuteW starts the exe without a console window flashing (os.execute would show one).
        // "--ensure": a ClipKeeper that already runs stays as it is — no second copy, no window
        public static string Script(string exe)
        {
            return string.Join("\n", new[]
            {
                "-- ClipKeeper: starts ClipKeeper together with OBS.",
                "-- Added by ClipKeeper (Settings → OBS → \"Start ClipKeeper together with OBS\"); turn it off there to remove it.",
                "local ffi = require(\"ffi\")",
                "local declared = pcall(ffi.cdef, [[",
                "int MultiByteToWideChar(unsigned int cp, unsigned long flags, const char* s, int n, wchar_t* out, int outn);",
                "void* ShellExecuteW(void* hwnd, const wchar_t* op, const wchar_t* file, const wchar_t* params, const wchar_t* dir, int show);",
                "]])",
                "local exe = " + LuaString(exe),
                "",
                "local function wide(s)",
                "  local n = ffi.C.MultiByteToWideChar(65001, 0, s, -1, nil, 0)",
                "  local b = ffi.new(\"wchar_t[?]\", n)",
                "  ffi.C.MultiByteToWideChar(65001, 0, s, -1, b, n)",
                "  return b",
                "end",
                "",
                "function script_description()",
                "  return \"Starts ClipKeeper together with OBS, so the recording guard and the clip card are always on.<br>\" ..",
                "         \"Added by ClipKeeper: Settings → OBS → Start ClipKeeper together with OBS. Turn it off there to remove it.<br><br>\" ..",
                "         \"Запускает ClipKeeper вместе с OBS. Добавлен ClipKeeper: Настройки → OBS. Выключи там, чтобы убрать.\"",
                "end",
                "",
                "function script_load(settings)",
                "  if not declared then print(\"ClipKeeper: this OBS has no Windows functions for Lua, ClipKeeper is not started\") return end",
                "  local ok, err = pcall(function()",
                "    ffi.load(\"shell32\").ShellExecuteW(nil, wide(\"open\"), wide(exe), wide(\"--ensure --tray\"), nil, 1)",
                "  end)",
                "  if not ok then print(\"ClipKeeper: could not start \" .. exe .. \": \" .. tostring(err)) end",
                "end",
                "",
            });
        }

        // the script file with the current exe (called on every start while the setting is on)
        public static void WriteScript()
        {
            Directory.CreateDirectory(Dir);
            string text = Script(System.Windows.Forms.Application.ExecutablePath);
            if (File.Exists(ScriptPath) && File.ReadAllText(ScriptPath, Encoding.UTF8) == text) return;
            File.WriteAllText(ScriptPath, text, new UTF8Encoding(false));
        }

        static string ObsPathOf(string p) { return p.Replace('\\', '/'); }   // OBS keeps script paths with forward slashes

        static bool IsOurs(Dictionary<string, object> s)
        {
            string p = Json.GetStr(s, "path") ?? "";
            return p.Replace('\\', '/').EndsWith("/" + FileName, StringComparison.OrdinalIgnoreCase);
        }

        // is the script in this collection (pure, for the self-test too)
        public static bool Has(Dictionary<string, object> collection)
        {
            var modules = Json.GetObj(collection, "modules");
            return Json.GetArr(modules, "scripts-tool").Select(Json.Obj).Any(s => s != null && IsOurs(s));
        }

        // adds or removes the script in a parsed collection; true — changed
        public static bool Apply(Dictionary<string, object> collection, bool on, string scriptPath)
        {
            var modules = Json.GetObj(collection, "modules");
            if (modules == null) { modules = new Dictionary<string, object>(); collection["modules"] = modules; }
            var list = Json.GetArr(modules, "scripts-tool").ToList();
            bool had = list.Select(Json.Obj).Any(s => s != null && IsOurs(s));
            if (on == had)
            {
                if (!on) return false;
                // there, but maybe with an old path — keep it pointing at the current file
                foreach (var s in list.Select(Json.Obj).Where(s => s != null && IsOurs(s)))
                    if (Json.GetStr(s, "path") != ObsPathOf(scriptPath)) { s["path"] = ObsPathOf(scriptPath); return true; }
                return false;
            }
            if (on) list.Add(Json.D("path", ObsPathOf(scriptPath), "settings", new Dictionary<string, object>()));
            else list = list.Where(o => { var s = Json.Obj(o); return s == null || !IsOurs(s); }).ToList();
            modules["scripts-tool"] = list.ToArray();
            return true;
        }

        // where things stand, for the settings row
        public static State Status(bool on)
        {
            try
            {
                if (!Directory.Exists(ScenesDir) || Directory.GetFiles(ScenesDir, "*.json").Length == 0) return State.NoObs;
                bool all = true, none = true;
                foreach (var f in Collections())
                {
                    var d = Json.Obj(Json.Parse(File.ReadAllText(f, Encoding.UTF8)));
                    if (d == null) continue;
                    bool has = Has(d);
                    all &= has;
                    none &= !has;
                }
                return (on ? all : none) ? (on ? State.On : State.Off) : State.Pending;
            }
            catch { return State.Error; }
        }

        static IEnumerable<string> Collections()
        {
            return Directory.GetFiles(ScenesDir, "*.json").Where(f => !f.EndsWith(".bak", StringComparison.OrdinalIgnoreCase));
        }

        // brings OBS in line with the setting — only while OBS is closed; null — done (or nothing to do), otherwise why not
        public static string Sync(bool on)
        {
            try
            {
                if (!on && !File.Exists(ScriptPath)) return null;   // never turned on (or already removed): nothing to look for
                if (on) WriteScript();
                if (!Directory.Exists(ScenesDir)) return null;
                var files = Collections().ToList();
                if (files.Count == 0) return null;
                var changes = new List<KeyValuePair<string, Dictionary<string, object>>>();
                foreach (var f in files)
                {
                    var d = Json.Obj(Json.Parse(File.ReadAllText(f, Encoding.UTF8)));
                    if (d != null && Apply(d, on, ScriptPath)) changes.Add(new KeyValuePair<string, Dictionary<string, object>>(f, d));
                }
                if (changes.Count == 0) return null;
                if (ObsRunning()) return WaitObs;   // it would write its own copy back on exit
                string backup = Path.Combine(Program.Dir, "backups", "obs-scenes-" + DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss"));
                Directory.CreateDirectory(backup);
                // the last 5 such copies are enough (the daily OBS backup keeps its own)
                foreach (var d in new DirectoryInfo(Path.GetDirectoryName(backup)).GetDirectories("obs-scenes-*").OrderByDescending(x => x.Name).Skip(5))
                    try { d.Delete(true); } catch { }
                foreach (var c in changes)
                {
                    File.Copy(c.Key, Path.Combine(backup, Path.GetFileName(c.Key)), true);
                    Json.WriteFile(c.Key, c.Value);
                }
                if (!on) try { File.Delete(ScriptPath); } catch { }
                Log.Write("OBS start script " + (on ? "added to " : "removed from ") + changes.Count + " scene collection(s); copies in " + backup);
                return null;
            }
            catch (Exception ex)
            {
                Log.Write("OBS start script: " + ex.Message);
                return ex.Message;
            }
        }
    }
}
