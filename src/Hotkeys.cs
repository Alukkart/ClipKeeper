using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;

namespace DeviceGuard
{
    // A key combination as it is stored and shown: "Ctrl+Alt+F9". A global hotkey takes the combination from every program,
    // so only ones nobody types: two modifiers (Ctrl+Shift+K), one modifier with an F key (Ctrl+F9), or Win with a key.
    // Not: a plain key, Shift+letter (capitals), Ctrl+C/V and the like, Alt+F4, Ctrl+Alt+key without an F key (that is AltGr:
    // @, €, ł… on many keyboards)
    class Hotkey
    {
        public ModifierKeys Mods;
        public Key Key;

        static readonly ModifierKeys[] Order = { ModifierKeys.Control, ModifierKeys.Alt, ModifierKeys.Shift, ModifierKeys.Windows };
        static string ModName(ModifierKeys m) { return m == ModifierKeys.Control ? "Ctrl" : m == ModifierKeys.Windows ? "Win" : m.ToString(); }

        static string KeyName(Key k)
        {
            if (k >= Key.D0 && k <= Key.D9) return ((int)(k - Key.D0)).ToString();
            return k.ToString();
        }

        public override string ToString()
        {
            return string.Join("+", Order.Where(m => (Mods & m) != 0).Select(ModName).Concat(new[] { KeyName(Key) }));
        }

        // null — empty or not a valid combination
        public static Hotkey Parse(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            var parts = s.Split('+').Select(p => p.Trim()).Where(p => p != "").ToList();
            if (parts.Count < 2) return null;
            var h = new Hotkey();
            foreach (var p in parts.Take(parts.Count - 1))
            {
                var m = Order.FirstOrDefault(x => string.Equals(ModName(x), p, StringComparison.OrdinalIgnoreCase));
                if (m == ModifierKeys.None) return null;
                h.Mods |= m;
            }
            string last = parts[parts.Count - 1];
            Key k;
            if (last.Length == 1 && char.IsDigit(last[0])) k = Key.D0 + (last[0] - '0');
            else if (char.IsDigit(last[0]) || !Enum.TryParse(last, true, out k) || !Enum.IsDefined(typeof(Key), k) || IsModifier(k)) return null;   // "65" would parse as a number
            h.Key = k;
            return h;
        }

        // null — allowed; otherwise why not
        public string Refusal()
        {
            int mods = Order.Count(m => (Mods & m) != 0);
            bool fkey = Key >= Key.F1 && Key <= Key.F24;
            if (mods == 0) return L.T("add Ctrl, Alt or Win — a plain key would be taken from games", "добавь Ctrl, Alt или Win — простую клавишу отнимем у игр");
            if (Mods == ModifierKeys.Alt && Key == Key.F4) return L.T("Alt+F4 closes windows", "Alt+F4 закрывает окна");
            if ((Mods & (ModifierKeys.Control | ModifierKeys.Alt)) == (ModifierKeys.Control | ModifierKeys.Alt) && (Mods & ModifierKeys.Windows) == 0 && !fkey)
                return L.T("Ctrl+Alt is AltGr on many keyboards — it types @, € and other characters; try Ctrl+Shift+", "Ctrl+Alt — это AltGr на многих клавиатурах, им набирают @, € и другие символы; попробуй Ctrl+Shift+") + KeyName(Key);
            if (mods >= 2 || (Mods & ModifierKeys.Windows) != 0 || fkey) return null;
            return L.T("with one modifier only an F key — ", "с одним модификатором — только F-клавиша: ") + ToString() +
                   L.T(" would be taken from typing in every program; try Ctrl+Shift+", " отнимется у набора текста во всех программах; попробуй Ctrl+Shift+") + KeyName(Key);
        }

        public static bool IsModifier(Key k)
        {
            return k == Key.LeftCtrl || k == Key.RightCtrl || k == Key.LeftAlt || k == Key.RightAlt || k == Key.LeftShift ||
                   k == Key.RightShift || k == Key.LWin || k == Key.RWin || k == Key.System || k == Key.None;
        }
    }

    // Global hotkeys: they work while a game is in front. Windows RegisterHotKey on a hidden message window;
    // a combination another program already holds is reported, not taken
    class Hotkeys : IDisposable
    {
        [DllImport("user32.dll", SetLastError = true)] static extern bool RegisterHotKey(IntPtr hwnd, int id, uint mods, uint vk);
        [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hwnd, int id);
        const int WmHotkey = 0x0312;
        const uint ModNoRepeat = 0x4000;

        readonly HwndSource window;
        readonly Dictionary<int, Action> actions = new Dictionary<int, Action>();
        public readonly Dictionary<string, string> Errors = new Dictionary<string, string>();   // setting name → why it did not register

        public Hotkeys()
        {
            window = new HwndSource(new HwndSourceParameters("ClipKeeperHotkeys") { Width = 0, Height = 0, WindowStyle = 0 });
            window.AddHook((IntPtr h, int msg, IntPtr w, IntPtr l, ref bool handled) =>
            {
                Action a;
                if (msg == WmHotkey && actions.TryGetValue(w.ToInt32(), out a))
                {
                    handled = true;
                    try { a(); } catch (Exception ex) { Log.Write("hotkey: " + ex.Message); }
                }
                return IntPtr.Zero;
            });
        }

        public void Clear()
        {
            foreach (int id in actions.Keys) UnregisterHotKey(window.Handle, id);
            actions.Clear();
            Errors.Clear();
        }

        // name — the setting it came from (for Errors); an empty or broken combination is skipped
        public void Add(string name, string combo, Action act)
        {
            var k = Hotkey.Parse(combo);
            if (k == null) return;
            string why = k.Refusal();
            if (why != null) { Errors[name] = why; Log.Write("hotkey " + combo + " not registered: " + why); return; }
            uint mods = ModNoRepeat | ((k.Mods & ModifierKeys.Alt) != 0 ? 1u : 0) | ((k.Mods & ModifierKeys.Control) != 0 ? 2u : 0) |
                        ((k.Mods & ModifierKeys.Shift) != 0 ? 4u : 0) | ((k.Mods & ModifierKeys.Windows) != 0 ? 8u : 0);
            int id = actions.Count + 1;
            if (RegisterHotKey(window.Handle, id, mods, (uint)KeyInterop.VirtualKeyFromKey(k.Key)))
            {
                actions[id] = act;
                return;
            }
            int err = Marshal.GetLastWin32Error();
            Errors[name] = err == 1409 ? L.T("already taken by another program", "уже занята другой программой") : L.T("Windows refused (error ", "Windows отказала (ошибка ") + err + ")";
            Log.Write("hotkey " + combo + " not registered: error " + err);
        }

        public void Dispose()
        {
            Clear();
            window.Dispose();
        }
    }
}
