using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.ComponentModel;
using System.Windows.Controls;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;

namespace DeviceGuard
{
    // Shared WPF helpers
    static class Wpf
    {
        public static void LoadStyles(Application app)
        {
            app.Resources.MergedDictionaries.Add((ResourceDictionary)Load("Styles.xaml"));
        }

        // XAML is embedded in the exe as a resource (see build.cmd); "English¦Русский" values are resolved to the current language
        public static object Load(string name)
        {
            using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("DeviceGuard." + name))
            using (var r = new System.IO.StreamReader(s, System.Text.Encoding.UTF8))
                return XamlReader.Parse(L.Xaml(r.ReadToEnd()));
        }

        public static Color C(string hex) { return (Color)ColorConverter.ConvertFromString(hex); }

        public static SolidColorBrush Br(string hex)
        {
            var b = new SolidColorBrush(C(hex));
            b.Freeze();
            return b;
        }

        public static SolidColorBrush Br(Color c, byte alpha)
        {
            var b = new SolidColorBrush(Color.FromArgb(alpha, c.R, c.G, c.B));
            b.Freeze();
            return b;
        }

        public static T Res<T>(string key) { return (T)Application.Current.FindResource(key); }

        // status colors
        public static readonly Color Ok = C("#3FB950"), Warn = C("#D29922"), Bad = C("#F85149"),
                                     Grey = C("#6E7681"), Accent = C("#A1A1AA"), Text = C("#EDEDEF"),
                                     Brand = C("#EDEDEF");   // the same as the Brand brush in Styles.xaml

        public static Color LevelColor(int level)
        {
            return level == 0 ? Ok : level == 1 ? Warn : level == 2 ? Bad : Grey;
        }

        // ── Win32 ──
        [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr hwnd, int index);
        [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr hwnd, int index, int value);
        [DllImport("user32.dll", SetLastError = true)] static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        static IntPtr Handle(Window w) { return new WindowInteropHelper(w).Handle; }

        // a window over the game that does not take focus and stays off the taskbar
        public static void NoActivate(Window w)
        {
            var h = Handle(w);
            SetWindowLong(h, -20 /* GWL_EXSTYLE */, GetWindowLong(h, -20) | 0x08000000 /* NOACTIVATE */ | 0x80 /* TOOLWINDOW */);
        }

        // the window is visible to you but not to OBS screen capture (Windows 10 2004+)
        public static void ExcludeFromCapture(Window w)
        {
            Affinity(w, 0x11 /* WDA_EXCLUDEFROMCAPTURE */);
        }

        // ── window size and position between runs: "x;y;width;height;max" in WPF units ──
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public static string SaveBounds(Window w)
        {
            var r = w.WindowState == WindowState.Normal ? new Rect(w.Left, w.Top, w.ActualWidth, w.ActualHeight) : w.RestoreBounds;
            if (r.IsEmpty || r.Width < 100 || r.Height < 100) return null;
            return string.Join(";", new[] { r.Left, r.Top, r.Width, r.Height }.Select(v => Math.Round(v).ToString(Inv))) +
                   (w.WindowState == WindowState.Maximized ? ";max" : "");
        }

        // restore only if the title bar lands on some screen (the monitor may have been unplugged)
        public static void RestoreBounds(Window w, string s)
        {
            if (string.IsNullOrEmpty(s)) return;
            var p = s.Split(';');
            double x, y, wd, h;
            if (p.Length < 4 || !double.TryParse(p[0], NumberStyles.Float, Inv, out x) || !double.TryParse(p[1], NumberStyles.Float, Inv, out y) ||
                !double.TryParse(p[2], NumberStyles.Float, Inv, out wd) || !double.TryParse(p[3], NumberStyles.Float, Inv, out h)) return;
            var screen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                                  SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
            var caption = new Rect(x + 40, y, Math.Max(10, wd - 80), 40);
            if (!screen.IntersectsWith(caption)) return;
            w.WindowStartupLocation = WindowStartupLocation.Manual;
            w.Left = x;
            w.Top = y;
            w.Width = Math.Max(w.MinWidth, wd);
            w.Height = Math.Max(w.MinHeight, h);
            if (p.Length > 4 && p[4] == "max") w.WindowState = WindowState.Maximized;
        }

        // main window and editor: hiding them from capture is a setting (alerts and the tray menu are always hidden)
        public const string Capturable = "capturable";

        public static void SetCaptureHidden(Window w, bool hide)
        {
            Affinity(w, hide ? 0x11u : 0u /* WDA_NONE */);
        }

        // Windows older than 10 2004 refuses: the window then shows up in recordings — say so once in the log
        static bool affinityLogged;

        static void Affinity(Window w, uint value)
        {
            var h = Handle(w);
            if (h == IntPtr.Zero) return;   // not shown yet: SourceInitialized applies it
            string err = null;
            try { if (!SetWindowDisplayAffinity(h, value)) err = "error " + Marshal.GetLastWin32Error(); }
            catch (Exception ex) { err = ex.Message; }
            if (err == null || affinityLogged) return;
            affinityLogged = true;
            Log.Write("hiding windows from capture does not work here (" + err + ")");
        }

        // the setting changed: apply it to windows that are already open
        public static void ApplyCaptureSetting(bool hide)
        {
            foreach (Window w in Application.Current.Windows)
                if (Equals(w.Tag, Capturable)) SetCaptureHidden(w, hide);
        }

        // rounded corners and a dark frame (Windows 11)
        public static void ModernFrame(Window w)
        {
            try
            {
                var h = Handle(w);
                int round = 2, dark = 1;
                DwmSetWindowAttribute(h, 33 /* WINDOW_CORNER_PREFERENCE */, ref round, 4);
                DwmSetWindowAttribute(h, 20 /* USE_IMMERSIVE_DARK_MODE */, ref dark, 4);
            }
            catch { }
        }
    }

    // ── binding models ─────────────────────────────────────────────────────
    class Vm : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        protected void Set<T>(ref T field, T value, [CallerMemberName] string name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return;
            field = value;
            var h = PropertyChanged;
            if (h != null) h(this, new PropertyChangedEventArgs(name));
        }

        protected void Notify(params string[] names)
        {
            var h = PropertyChanged;
            if (h != null) foreach (var n in names) h(this, new PropertyChangedEventArgs(n));
        }
    }

    class TileVm : Vm
    {
        string icon, label, value, sub;
        Brush dot, valueBrush;
        public string Icon { get { return icon; } set { Set(ref icon, value); } }
        public string Label { get { return label; } set { Set(ref label, value); } }
        public string Value { get { return this.value; } set { Set(ref this.value, value); } }
        public string Sub { get { return sub; } set { Set(ref sub, value); } }
        public Brush Dot { get { return dot; } set { Set(ref dot, value); } }
        public Brush ValueBrush { get { return valueBrush; } set { Set(ref valueBrush, value); } }

        // tile click (e.g. "Last clip" → gallery)
        public Action Click { get; set; }
        public System.Windows.Input.Cursor Cursor { get { return Click != null ? System.Windows.Input.Cursors.Hand : null; } }
        public Visibility ArrowVis { get { return Click != null ? Visibility.Visible : Visibility.Collapsed; } }

        public TileVm(string icon, string label) { Icon = icon; Label = label; }

        public void Show(int level, string value, string sub)
        {
            Value = value;
            Sub = sub;
            Dot = Wpf.Br(Wpf.LevelColor(level), 255);
            ValueBrush = Wpf.Br(level == 2 ? Wpf.Bad : level == 3 ? Wpf.Grey : Wpf.Text, 255);
        }
    }

    // a clip in the gallery
    class ClipVm : Vm
    {
        Brush thumb;
        string deleteText = "";
        public string Path { get; set; }
        public string Title { get; set; }
        public string Sub { get; set; }
        public string Duration { get; set; }
        public Visibility DurationVis { get { return string.IsNullOrEmpty(Duration) ? Visibility.Collapsed : Visibility.Visible; } }
        public Visibility NewVis { get; set; }
        public Brush Thumb { get { return thumb; } set { Set(ref thumb, value); } }
        public string Group { get; set; }   // the day (or the folder in a search over all folders) it is listed under
        public double Seconds;

        // selected in the library (MainWindowSelect.cs): while any clip is, every card shows its circle
        bool selected, selecting;
        static readonly Brush RingOff = Wpf.Br("#BFFFFFFF"), FillOff = Wpf.Br("#730B0C0E"), Clear = Brushes.Transparent;
        public bool Selected
        {
            get { return selected; }
            set
            {
                if (value && !selected) SelectedAt = ++selectClock;   // the order clips were selected in: the order they are joined in
                selected = value;
                Notify("Selected", "CheckOpacity", "CheckRing", "CheckFill", "CheckMark");
            }
        }
        static long selectClock;
        public long SelectedAt;
        public bool Selecting { get { return selecting; } set { selecting = value; Notify("CheckOpacity"); } }
        public double CheckOpacity { get { return selected || selecting ? 1 : 0; } }
        public Brush CheckRing { get { return selected ? Wpf.Res<Brush>("Text") : RingOff; } }
        public Brush CheckFill { get { return selected ? Wpf.Res<Brush>("Text") : FillOff; } }
        public Brush CheckMark { get { return selected ? Wpf.Res<Brush>("Bg") : Clear; } }

        // its own name (ClipNames): a source is named in its file name, when and how big it is goes below
        public bool Named, Source;
        public string WhenText, SizeText;
        public string TitleTip { get { return Path + L.T("\nF2 or a double click — rename", "\nF2 или двойной клик — переименовать"); } }
        bool editing;
        string editText = "";
        public bool Editing { get { return editing; } set { editing = value; Notify("EditVis", "TitleVis", "NewFileName"); } }
        public Visibility EditVis { get { return editing ? Visibility.Visible : Visibility.Collapsed; } }
        public Visibility TitleVis { get { return editing ? Visibility.Collapsed : Visibility.Visible; } }
        public string EditText { get { return editText; } set { editText = value ?? ""; Notify("EditText", "NewFileName"); } }
        public string NewFileName { get { return editing ? ClipNames.NameFor(Path, editText, Source) ?? L.T("type a name", "введи название") : null; } }

        // frames under the mouse (MainWindowScrub.cs): the current frame and a progress line of the thumbnail width
        Brush scrub;
        double scrubWidth = -1;
        public Brush[] Frames;
        public Brush Scrub { get { return scrub; } set { Set(ref scrub, value); } }
        public double ScrubWidth { get { return Math.Max(0, scrubWidth); } set { scrubWidth = value; Notify("ScrubWidth", "ScrubVis"); } }
        public Visibility ScrubVis { get { return scrubWidth >= 0 ? Visibility.Visible : Visibility.Collapsed; } }
        public string DeleteText { get { return deleteText; } set { Set(ref deleteText, value); } }

        // favorites: the star is always shown on favorites, on hover for the rest
        bool fav;
        static readonly Brush StarOn = Wpf.Br(Wpf.Warn, 255), StarOff = Wpf.Br("#FFFFFF");
        public bool Fav { get { return fav; } set { fav = value; Notify("StarGlyph", "StarBrush", "StarOpacity", "StarTip"); } }
        public string StarGlyph { get { return fav ? "" : ""; } }
        public Brush StarBrush { get { return fav ? StarOn : StarOff; } }
        public double StarOpacity { get { return fav ? 1 : 0; } }
        public string StarTip { get { return fav ? L.T("Remove from favorites", "Убрать из избранного") : L.T("Add to favorites", "В избранное"); } }

        // a ready clip: "to collection" (known game — straight to its folder, otherwise a picker menu)
        public string Game { get; set; }
        public string ReturnTip { get; set; }
        public Visibility ReturnVis { get { return ReturnTip != null ? Visibility.Visible : Visibility.Collapsed; } }
    }

    class SourceVm : Vm
    {
        string status, device;
        Brush statusBrush, statusBg;
        List<string> tracks;
        public string Icon { get; set; }
        public string Name { get; set; }
        public Brush IconBg { get; set; }
        public Brush IconFg { get; set; }
        public string Device { get { return device; } set { Set(ref device, value); } }
        public List<string> Tracks { get { return tracks; } set { Set(ref tracks, value); } }
        public string Status { get { return status; } set { Set(ref status, value); } }
        public Brush StatusBrush { get { return statusBrush; } set { Set(ref statusBrush, value); } }
        public Brush StatusBg { get { return statusBg; } set { Set(ref statusBg, value); } }
    }

    class EventVm
    {
        public string Time { get; set; }
        public string Text { get; set; }
        public string Glyph { get; set; }
        public Brush GlyphBrush { get; set; }
        // a problem can lead to the setting or the place that deals with it
        public string LinkText { get; set; }
        public Action Link { get; set; }
        public Visibility LinkVis { get { return Link != null ? Visibility.Visible : Visibility.Collapsed; } }

        // "HH:mm:ss  ⚠ text" → time, icon and text
        public static EventVm Parse(string line)
        {
            var e = new EventVm { Time = line.Length >= 8 ? line.Substring(0, 8) : "", Text = line.Length > 10 ? line.Substring(10) : line };
            string t = e.Text;
            if (t.StartsWith("⚠")) { e.Glyph = ""; e.GlyphBrush = Wpf.Br(Wpf.Warn, 255); e.Text = t.Substring(1).Trim(); }
            else if (t.StartsWith("✓")) { e.Glyph = ""; e.GlyphBrush = Wpf.Br(Wpf.Ok, 255); e.Text = t.Substring(1).Trim(); }
            else if (t.StartsWith("ⓘ")) { e.Glyph = ""; e.GlyphBrush = Wpf.Br(Wpf.Accent, 255); e.Text = t.Substring(1).Trim(); }
            else { e.Glyph = ""; e.GlyphBrush = Wpf.Br(Wpf.Grey, 255); }
            e.Text = Cap(e.Text);
            return e;
        }

        public static string Cap(string t)
        {
            if (string.IsNullOrEmpty(t)) return t;
            char c = t[0];
            return char.IsLower(c) ? char.ToUpper(c) + t.Substring(1) : t;
        }

        public static EventVm Problem(string text, bool due)
        {
            return new EventVm { Text = text, Glyph = due ? "" : "", GlyphBrush = Wpf.Br(due ? Wpf.Bad : Wpf.Warn, 255) };
        }
    }
}
