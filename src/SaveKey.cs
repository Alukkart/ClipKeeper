using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace DeviceGuard
{
    // The key OBS saves a replay with, watched by ClipKeeper too: OBS reports a clip only once the file is written
    // (a second or more for a big buffer), so the press itself is confirmed at once. The binding is read from the current
    // OBS profile; the keyboard is polled the way OBS does it (GetAsyncKeyState) — no hook, nothing is taken from OBS.
    class SaveKey
    {
        public class Combo
        {
            public int Vk;
            public bool Ctrl, Shift, Alt, Win;
            public string Name;

            public override string ToString()
            {
                var parts = new List<string>();
                if (Ctrl) parts.Add("Ctrl");
                if (Alt) parts.Add("Alt");
                if (Shift) parts.Add("Shift");
                if (Win) parts.Add("Win");
                parts.Add(Name);
                return string.Join("+", parts);
            }
        }

        static string ObsDir { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "obs-studio"); } }

        // OBS key names (obs-hotkeys.h) → Windows virtual keys; null — not supported
        public static int? Vk(string obsKey)
        {
            if (string.IsNullOrEmpty(obsKey) || !obsKey.StartsWith("OBS_KEY_")) return null;
            string k = obsKey.Substring(8);
            int n;
            if (k.Length == 1 && k[0] >= 'A' && k[0] <= 'Z') return k[0];
            if (k.Length == 1 && k[0] >= '0' && k[0] <= '9') return k[0];
            if (k.StartsWith("F") && int.TryParse(k.Substring(1), out n) && n >= 1 && n <= 24) return 0x70 + n - 1;
            if (k.StartsWith("NUM") && k.Length == 4 && char.IsDigit(k[3])) return 0x60 + (k[3] - '0');
            if (k.StartsWith("MOUSE") && int.TryParse(k.Substring(5), out n))
                return n == 1 ? 0x01 : n == 2 ? 0x02 : n == 3 ? 0x04 : n == 4 ? 0x05 : n == 5 ? 0x06 : (int?)null;
            switch (k)
            {
                case "SPACE": return 0x20;
                case "RETURN": case "ENTER": return 0x0D;
                case "ESCAPE": return 0x1B;
                case "TAB": return 0x09;
                case "BACKSPACE": return 0x08;
                case "INSERT": return 0x2D;
                case "DELETE": return 0x2E;
                case "HOME": return 0x24;
                case "END": return 0x23;
                case "PAGEUP": return 0x21;
                case "PAGEDOWN": return 0x22;
                case "LEFT": return 0x25;
                case "UP": return 0x26;
                case "RIGHT": return 0x27;
                case "DOWN": return 0x28;
                case "PRINT": return 0x2C;
                case "PAUSE": return 0x13;
                case "SCROLLLOCK": return 0x91;
                case "CAPSLOCK": return 0x14;
                case "NUMLOCK": return 0x90;
                case "NUMASTERISK": return 0x6A;
                case "NUMPLUS": return 0x6B;
                case "NUMMINUS": return 0x6D;
                case "NUMPERIOD": return 0x6E;
                case "NUMSLASH": return 0x6F;
                case "MINUS": return 0xBD;
                case "EQUAL": return 0xBB;
                case "BRACKETLEFT": return 0xDB;
                case "BRACKETRIGHT": return 0xDD;
                case "SEMICOLON": return 0xBA;
                case "APOSTROPHE": return 0xDE;
                case "ASCIITILDE": case "QUOTELEFT": return 0xC0;
                case "COMMA": return 0xBC;
                case "PERIOD": return 0xBE;
                case "SLASH": return 0xBF;
                case "BACKSLASH": return 0xDC;
            }
            return null;
        }

        static string Pretty(string obsKey)
        {
            string k = obsKey.Substring(8);
            if (k.StartsWith("MOUSE")) return L.T("Mouse ", "Мышь ") + k.Substring(5);
            if (k.StartsWith("NUM") && k.Length == 4) return "Num " + k[3];
            return k.Length > 1 ? k.Substring(0, 1) + k.Substring(1).ToLowerInvariant() : k;
        }

        // the "ReplayBuffer=" value of a profile's basic.ini: JSON with "\n" written as two characters
        public static List<Combo> Parse(string iniValue)
        {
            var list = new List<Combo>();
            var d = Json.Obj(Json.Parse(iniValue.Replace("\\n", "\n")));
            foreach (var o in Json.GetArr(d, "ReplayBuffer.Save"))
            {
                var b = Json.Obj(o);
                string key = Json.GetStr(b, "key");
                var vk = Vk(key);
                if (vk == null) { Log.Write("OBS save key " + key + " is not supported for the instant confirmation"); continue; }
                list.Add(new Combo
                {
                    Vk = vk.Value, Name = Pretty(key), Ctrl = Json.GetBool(b, "control", false), Shift = Json.GetBool(b, "shift", false),
                    Alt = Json.GetBool(b, "alt", false), Win = Json.GetBool(b, "command", false),
                });
            }
            return list;
        }

        // the bindings of the profile with this name (OBS knows the current one); empty — none or not found
        public static List<Combo> Read(string profile)
        {
            try
            {
                string dir = Path.Combine(ObsDir, @"basic\profiles");
                if (string.IsNullOrEmpty(profile) || !Directory.Exists(dir)) return new List<Combo>();
                foreach (var ini in Directory.GetFiles(dir, "basic.ini", SearchOption.AllDirectories))
                {
                    var lines = File.ReadAllLines(ini, Encoding.UTF8);
                    if (!lines.Any(l => l.Trim() == "Name=" + profile)) continue;
                    var rb = lines.FirstOrDefault(l => l.StartsWith("ReplayBuffer="));
                    return rb == null ? new List<Combo>() : Parse(rb.Substring("ReplayBuffer=".Length));
                }
            }
            catch (Exception ex) { Log.Write("OBS save key: " + ex.Message); }
            return new List<Combo>();
        }

        // ── watching ──
        [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);
        static bool Down(int vk) { return (GetAsyncKeyState(vk) & 0x8000) != 0; }

        volatile List<Combo> combos = new List<Combo>();
        volatile bool stopping;
        Thread thread;
        readonly Action pressed;

        public SaveKey(Action pressed) { this.pressed = pressed; }

        public List<Combo> Combos { get { return combos; } }

        public void Set(List<Combo> list)
        {
            combos = list ?? new List<Combo>();
            if (combos.Count > 0 && thread == null)
            {
                thread = new Thread(Watch) { IsBackground = true, Name = "ClipKeeper-savekey" };
                thread.Start();
            }
        }

        public void Stop() { stopping = true; }

        void Watch()
        {
            // held combinations by value: the list is read again every 30 s as new objects, a held key must not fire again
            var was = new HashSet<string>();
            while (!stopping)
            {
                Thread.Sleep(15);
                var list = combos;
                if (list.Count == 0) { was.Clear(); continue; }
                bool ctrl = Down(0x11), shift = Down(0x10), alt = Down(0x12), win = Down(0x5B) || Down(0x5C);
                var held = new HashSet<string>();
                foreach (var c in list)
                {
                    // like OBS: the modifiers must match exactly
                    if (!(Down(c.Vk) && c.Ctrl == ctrl && c.Shift == shift && c.Alt == alt && c.Win == win)) continue;
                    string id = c.Vk + ":" + c;
                    held.Add(id);
                    if (!was.Contains(id))
                        try { pressed(); } catch (Exception ex) { Log.Write("save key: " + ex.Message); }
                }
                was = held;
            }
        }
    }
}
