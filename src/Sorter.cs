using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.Win32;

namespace DeviceGuard
{
    // A window seen in front: its process and title
    class WinInfo
    {
        public int Pid;
        public string Exe, Name, Title;   // full exe path (null if Windows does not tell), exe name without .exe, window title
        public bool Full;                 // the window covers its whole monitor (fullscreen or borderless)
    }

    static class Foreground
    {
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out int pid);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr hwnd, StringBuilder sb, int max);
        [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(int access, bool inherit, int pid);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool QueryFullProcessImageName(IntPtr h, int flags, StringBuilder sb, ref int size);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd, out RECT r);
        [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);
        [DllImport("user32.dll")] static extern bool GetMonitorInfo(IntPtr mon, ref MONITORINFO mi);

        [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] struct MONITORINFO { public int Size; public RECT Monitor, Work; public int Flags; }

        // a maximized window stops at the taskbar; a game covers the whole monitor
        static bool CoversMonitor(IntPtr h)
        {
            RECT r;
            var mi = new MONITORINFO { Size = Marshal.SizeOf(typeof(MONITORINFO)) };
            if (!GetWindowRect(h, out r) || !GetMonitorInfo(MonitorFromWindow(h, 2), ref mi)) return false;
            return r.Left <= mi.Monitor.Left && r.Top <= mi.Monitor.Top && r.Right >= mi.Monitor.Right && r.Bottom >= mi.Monitor.Bottom;
        }

        public static WinInfo Now()
        {
            var h = GetForegroundWindow();
            if (h == IntPtr.Zero) return null;
            int pid;
            GetWindowThreadProcessId(h, out pid);
            if (pid == 0) return null;
            var sb = new StringBuilder(512);
            GetWindowText(h, sb, sb.Capacity);
            string exe = ExeOf(pid), name = exe != null ? Path.GetFileNameWithoutExtension(exe) : null;
            if (name == null)
                try { using (var p = Process.GetProcessById(pid)) name = p.ProcessName; } catch { }   // anti-cheat may hide the path, not the name
            return new WinInfo { Pid = pid, Exe = exe, Name = name, Title = sb.ToString(), Full = CoversMonitor(h) };
        }

        // the limited query right is enough even for most protected game processes
        public static string ExeOf(int pid)
        {
            var h = OpenProcess(0x1000, false, pid);
            if (h == IntPtr.Zero) return null;
            try
            {
                var sb = new StringBuilder(1024);
                int size = sb.Capacity;
                return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString() : null;
            }
            finally { CloseHandle(h); }
        }

        public static bool Alive(WinInfo w)
        {
            if (w == null) return false;
            try
            {
                using (var p = Process.GetProcessById(w.Pid))
                    if (p.HasExited) return false;
            }
            catch { return false; }
            string exe = ExeOf(w.Pid);
            return w.Exe == null || exe == null || string.Equals(exe, w.Exe, StringComparison.OrdinalIgnoreCase);   // the pid was not reused
        }
    }

    // Is a process a game, and what is the game called: your own names → the folder clips of this exe already go to →
    // the store's name (Steam / Epic / GOG) → the name in the exe's properties → the exe name made readable
    static class Games
    {
        // not games: browsers, chats, launchers, players, tools — and everything in the Windows folder
        static readonly HashSet<string> NotGames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "explorer", "searchhost", "searchapp", "startmenuexperiencehost", "shellexperiencehost", "applicationframehost",
            "textinputhost", "lockapp", "systemsettings", "taskmgr", "cmd", "powershell", "pwsh", "windowsterminal", "conhost",
            "openconsole", "notepad", "notepad++", "mspaint", "snippingtool", "screenclippinghost", "photos", "microsoft.photos",
            "msedge", "chrome", "firefox", "zen", "opera", "opera_gx", "brave", "vivaldi", "browser", "yandex", "arc", "librewolf", "floorp", "waterfox",
            "discord", "discordcanary", "discordptb", "vesktop", "telegram", "ayugram", "kotatogram", "whatsapp", "slack", "teams", "ms-teams",
            "zoom", "skype", "viber", "signal", "element", "teamspeak", "ts3client_win64", "mumble",
            "spotify", "yandexmusic", "applemusic", "itunes", "foobar2000", "aimp", "vlc", "mpc-hc64", "mpc-be64", "potplayermini64", "mpv",
            "obs64", "obs32", "streamlabs obs", "clipkeeper", "deviceguard", "sharex", "lightshot", "nvidia share", "nvidia overlay", "nvidia app",
            "steam", "steamwebhelper", "epicgameslauncher", "galaxyclient", "battle.net", "riotclientservices", "riotclientux",
            "eadesktop", "origin", "ubisoftconnect", "upc", "playnite.desktopapp", "playnite.fullscreenapp", "xboxpcapp", "gamebar",
            "code", "devenv", "rider64", "idea64", "pycharm64", "webstorm64", "clion64", "sublime_text", "cursor", "windsurf",
            "adobe premiere pro", "afterfx", "photoshop", "illustrator", "resolve", "audacity", "blender", "davinci resolve",
            "crosshairx", "steelseriesgg", "steelseriesengine", "voicemeeter", "voicemeeterpro", "voicemeeter8", "voicemeeter8x64", "lghub", "razer synapse", "icue",
            "msiafterburner", "rtss", "radeonsoftware", "amdrsserv", "7zfm", "winrar", "totalcmd64", "everything", "keepass", "keepassxc",
            "1password", "bitwarden", "outlook", "winword", "excel", "powerpnt", "onenote", "notion", "obsidian", "figma",
            "claude", "chatgpt", "wallpaper64", "wallpaper32", "lively", "rainmeter", "qbittorrent", "utorrent", "anydesk", "teamviewer",
        };

        static readonly string WinDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows).TrimEnd('\\') + "\\";
        static readonly int OwnPid = Process.GetCurrentProcess().Id;

        // a game: from a game library, or fullscreen, or hidden by anti-cheat — so a maximized editor or chat
        // never becomes a "game" folder (windowed games outside libraries: add them to your names)
        public static bool IsGame(WinInfo w)
        {
            if (w == null || w.Pid == OwnPid) return false;
            if (string.IsNullOrEmpty(w.Name) && string.IsNullOrEmpty(w.Title)) return false;
            if (w.Name != null && NotGames.Contains(w.Name)) return false;
            if (w.Exe == null) return true;
            if (w.Exe.StartsWith(WinDir, StringComparison.OrdinalIgnoreCase)) return false;
            return w.Full || InLibrary(w.Exe);
        }

        // ── your own names: "HuntGame > Hunt", "+call duty > Call of Duty" (all words), "*minecraft* > Minecraft" (anywhere) ──
        public static string Custom(string lines, WinInfo w)
        {
            if (string.IsNullOrWhiteSpace(lines) || w == null) return null;
            string name = (w.Name ?? "").ToLowerInvariant(), title = (w.Title ?? "").ToLowerInvariant();
            foreach (var raw in lines.Split('\n'))
            {
                int gt = raw.LastIndexOf('>');
                if (gt <= 0) continue;
                string key = raw.Substring(0, gt).Trim().ToLowerInvariant(), folder = raw.Substring(gt + 1).Trim();
                if (key == "" || folder == "") continue;
                if (key.Length > 2 && key.StartsWith("*") && key.EndsWith("*"))
                {
                    string part = key.Trim('*').Trim();
                    if (part != "" && (name.Contains(part) || title.Contains(part))) return folder;
                }
                else if (key.StartsWith("+") || key.StartsWith("~"))
                {
                    var words = key.Substring(1).Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    if (words.Length > 0 && (words.All(name.Contains) || words.All(title.Contains))) return folder;
                }
                else
                {
                    string exe = Regex.Replace(key.Split('\\', '/').Last(), @"\.exe$", "");
                    if (exe == name) return folder;
                }
            }
            return null;
        }

        // ── the folder for a game ──
        public static string Folder(WinInfo w, string root)
        {
            var names = new List<string>();
            if (w.Exe != null)
            {
                names.Add(StoreName(w.Exe));
                names.Add(ProductName(w.Exe));
            }
            if (w.Name != null) { names.Add(Readable(w.Name)); names.Add(w.Name); }
            else names.Add(w.Title);
            names = names.Where(n => !string.IsNullOrWhiteSpace(n)).Select(Clean).Where(n => n != "").ToList();
            if (names.Count == 0) return null;

            string learned = w.Name != null ? Learned(root, w.Name) : null;
            if (learned != null) return learned;
            var dirs = Subdirs(root);
            foreach (var n in names)
            {
                var hit = dirs.FirstOrDefault(d => Norm(d) == Norm(n));
                if (hit != null) return hit;
            }
            return names[0];
        }

        public static string Clean(string s)
        {
            s = Regex.Replace(s ?? "", "[™®©]", "");
            return Trimmer.SafeName(s);
        }

        static string Norm(string s) { return Regex.Replace((s ?? "").ToLowerInvariant(), @"[^\p{L}\p{N}]", ""); }

        // "RimWorldWin64" → "RimWorld", "VALORANT-Win64-Shipping" → "VALORANT", "ShiftAtMidnight" → "Shift At Midnight"
        public static string Readable(string exe)
        {
            string s = Regex.Replace(exe, @"([-_ .]?(win64|win32|x64|x86|shipping|dx11|dx12))+$", "", RegexOptions.IgnoreCase);
            if (s == "") s = exe;
            if (!s.Contains(' ') && s.Any(char.IsLower)) s = Regex.Replace(s, @"(?<=\p{Ll})(?=\p{Lu})", " ");
            return Regex.Replace(s.Replace('_', ' '), @"\s+", " ").Trim();
        }

        // the name in the exe's properties, when it is a game's name and not an engine's
        static readonly string[] Generic = { "unreal engine", "unity", "bootstrappackagedgame", "microsoft", "java", "openjdk", "electron", "node.js", "chromium", "python", "launcher" };

        static string ProductName(string exe)
        {
            try
            {
                string p = (FileVersionInfo.GetVersionInfo(exe).ProductName ?? "").Trim();
                if (p.Length < 2 || p.Length > 60 || !p.Any(char.IsLetter)) return null;
                string low = p.ToLowerInvariant();
                return Generic.Any(low.Contains) ? null : p;
            }
            catch { return null; }
        }

        // ── stores: the game's real name by its install folder ──
        static readonly object storeLock = new object();
        static readonly Dictionary<string, Dictionary<string, string>> steam = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        static List<KeyValuePair<string, string>> installs;   // install folder → name (Epic, GOG)
        static DateTime installsAt = DateTime.MinValue;

        public static bool InLibrary(string exe)
        {
            if (exe == null) return false;
            if (exe.IndexOf(@"\steamapps\common\", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (Regex.IsMatch(exe, @"\\(Epic Games|GOG Games|GOG Galaxy\\Games|XboxGames|Riot Games)\\", RegexOptions.IgnoreCase)) return true;
            lock (storeLock) return Installs().Any(kv => exe.StartsWith(kv.Key, StringComparison.OrdinalIgnoreCase));
        }

        static string StoreName(string exe)
        {
            lock (storeLock)
            {
                var m = Regex.Match(exe, @"^(.*\\steamapps)\\common\\([^\\]+)\\", RegexOptions.IgnoreCase);
                if (m.Success)
                {
                    Dictionary<string, string> lib;
                    if (!steam.TryGetValue(m.Groups[1].Value, out lib)) steam[m.Groups[1].Value] = lib = SteamLibrary(m.Groups[1].Value);
                    string n;
                    if (lib.TryGetValue(m.Groups[2].Value, out n)) return n;
                    return null;
                }
                var hit = Installs().Where(kv => exe.StartsWith(kv.Key, StringComparison.OrdinalIgnoreCase)).OrderByDescending(kv => kv.Key.Length).FirstOrDefault();
                return hit.Value;
            }
        }

        // steamapps\appmanifest_*.acf: "installdir" → "name"
        static Dictionary<string, string> SteamLibrary(string steamapps)
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var f in Directory.GetFiles(steamapps, "appmanifest_*.acf"))
                {
                    string t = File.ReadAllText(f);
                    var dir = Regex.Match(t, "\"installdir\"\\s+\"([^\"]*)\"");
                    var name = Regex.Match(t, "\"name\"\\s+\"([^\"]*)\"");
                    if (dir.Success && name.Success) d[dir.Groups[1].Value] = name.Groups[1].Value;
                }
            }
            catch (Exception ex) { Log.Write("steam library " + steamapps + ": " + ex.Message); }
            return d;
        }

        static List<KeyValuePair<string, string>> Installs()
        {
            if (installs != null && (DateTime.Now - installsAt).TotalMinutes < 10) return installs;
            var l = new List<KeyValuePair<string, string>>();
            Action<string, string> add = (dir, name) =>
            {
                if (!string.IsNullOrEmpty(dir) && !string.IsNullOrEmpty(name))
                    l.Add(new KeyValuePair<string, string>(dir.Replace('/', '\\').TrimEnd('\\') + "\\", name));
            };
            try
            {
                string epic = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), @"Epic\EpicGamesLauncher\Data\Manifests");
                if (Directory.Exists(epic))
                    foreach (var f in Directory.GetFiles(epic, "*.item"))
                    {
                        var d = Json.Obj(Json.Parse(File.ReadAllText(f)));
                        add(Json.GetStr(d, "InstallLocation"), Json.GetStr(d, "DisplayName"));
                    }
            }
            catch (Exception ex) { Log.Write("epic manifests: " + ex.Message); }
            try
            {
                using (var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\GOG.com\Games"))
                    if (k != null)
                        foreach (var id in k.GetSubKeyNames())
                            using (var g = k.OpenSubKey(id))
                                if (g != null) add(g.GetValue("path") as string, g.GetValue("gameName") as string);
            }
            catch (Exception ex) { Log.Write("gog registry: " + ex.Message); }
            installs = l;
            installsAt = DateTime.Now;
            return l;
        }

        // ── folders that already exist: "HuntGame - Replay …" in "Hunt Showdown" means HuntGame goes there ──
        static readonly object learnLock = new object();
        static readonly Dictionary<string, Dictionary<string, string>> learned = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

        // the exe name in front of a saved file's name: "HuntGame - Replay 2026-08-07 04-50-47" → "HuntGame"
        public static string PrefixOf(string fileName)
        {
            var m = Regex.Match(Path.GetFileNameWithoutExtension(fileName), @"^(.+?) - (Replay|Screenshot|\d{4}-\d{2}-\d{2})");
            return m.Success ? m.Groups[1].Value : null;
        }

        static string Learned(string root, string exe)
        {
            lock (learnLock)
            {
                Dictionary<string, string> map;
                if (!learned.TryGetValue(root, out map)) learned[root] = map = Learn(root);
                string dir;
                return map.TryGetValue(exe, out dir) && Directory.Exists(Path.Combine(root, dir)) ? dir : null;
            }
        }

        static Dictionary<string, string> Learn(string root)
        {
            var counts = new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase);
            foreach (var dir in Subdirs(root))
            {
                try
                {
                    foreach (var f in Directory.EnumerateFiles(Path.Combine(root, dir), "*", SearchOption.AllDirectories).Take(5000))
                    {
                        string p = PrefixOf(f);
                        if (p == null) continue;
                        Dictionary<string, int> c;
                        if (!counts.TryGetValue(p, out c)) counts[p] = c = new Dictionary<string, int>();
                        int n;
                        c.TryGetValue(dir, out n);
                        c[dir] = n + 1;
                    }
                }
                catch { }
            }
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in counts) map[kv.Key] = kv.Value.OrderByDescending(x => x.Value).First().Key;
            return map;
        }

        static List<string> Subdirs(string root)
        {
            try { return Directory.GetDirectories(root).Select(Path.GetFileName).ToList(); }
            catch { return new List<string>(); }
        }
    }

    // Sorts what OBS saves into game folders, as the Smart Replay Mover script did: replays, recordings, screenshots.
    // The game is decided the moment OBS reports the save; the file is moved on a separate thread.
    class Sorter
    {
        public const string Replays = "Replays", Recordings = "Recordings", Screenshots = "Screenshots";

        readonly Settings cfg;
        readonly Action<string> ev;                     // a line for the event list (interface language)
        readonly Action<string, string> replayDone;     // the replay's final path and its game (null — no game)
        readonly BlockingCollection<Action> work = new BlockingCollection<Action>();
        Thread worker, tracker;
        volatile bool stopping;
        volatile WinInfo lastGame;                      // the last game seen in front

        // recording: the game is decided when it starts, the files are moved when they are finished
        WinInfo recFg, recLast;
        string recFile;
        DateTime recStart;

        public Sorter(Settings cfg, Action<string> ev, Action<string, string> replayDone)
        {
            this.cfg = cfg;
            this.ev = ev;
            this.replayDone = replayDone;
        }

        public void Start()
        {
            worker = new Thread(() => { foreach (var a in work.GetConsumingEnumerable()) try { a(); } catch (Exception ex) { Log.Write("sorter: " + ex); } })
                { IsBackground = true, Name = "ClipKeeper-sorter" };
            worker.Start();
            tracker = new Thread(Track) { IsBackground = true, Name = "ClipKeeper-foreground" };
            tracker.Start();
        }

        public void Stop()
        {
            stopping = true;
            work.CompleteAdding();
            if (worker != null) worker.Join(3000);
        }

        void Track()
        {
            while (!stopping)
            {
                if (cfg.SortClips)
                    try
                    {
                        var w = Foreground.Now();
                        if (Games.IsGame(w)) lastGame = w;
                    }
                    catch { }
                Thread.Sleep(1000);
            }
        }

        public bool Handles(string type)
        {
            return cfg.SortClips && (type == "ReplayBufferSaved" || type == "RecordStateChanged" || type == "RecordFileChanged" || type == "ScreenshotSaved");
        }

        // called on the WebSocket thread: only remembers what is in front and queues the rest
        public void OnEvent(string type, Dictionary<string, object> data)
        {
            WinInfo fg = null;
            try { fg = Foreground.Now(); } catch { }
            WinInfo last = lastGame;
            var at = DateTime.Now;
            if (type == "ReplayBufferSaved")
            {
                string p = Json.GetStr(data, "savedReplayPath");
                if (string.IsNullOrEmpty(p)) return;
                work.Add(() =>
                {
                    string game;
                    string to = Move(p, fg, last, Replays, at, out game);
                    replayDone(to ?? p.Replace('/', '\\'), game);
                });
            }
            else if (type == "ScreenshotSaved")
            {
                string p = Json.GetStr(data, "savedScreenshotPath");
                if (string.IsNullOrEmpty(p) || !cfg.SortScreenshots) return;
                work.Add(() => { string g; Move(p, fg, last, Screenshots, at, out g); });
            }
            else if (type == "RecordStateChanged")
            {
                string state = Json.GetStr(data, "outputState") ?? "", p = Json.GetStr(data, "outputPath");
                if (state.EndsWith("_STARTED"))
                    work.Add(() => { recFg = fg; recLast = last; recStart = at; recFile = p; });
                else if (state.EndsWith("_STOPPED") && !string.IsNullOrEmpty(p))
                    work.Add(() =>
                    {
                        string g;
                        if (cfg.SortRecordings) Move(p, recFg ?? fg, recFg != null ? recLast : last, Recordings, recStart != DateTime.MinValue ? recStart : at, out g);
                        recFg = recLast = null;
                        recFile = null;
                        recStart = DateTime.MinValue;
                    });
            }
            else if (type == "RecordFileChanged")
            {
                // automatic file splitting: the previous part is finished
                string next = Json.GetStr(data, "newOutputPath");
                work.Add(() =>
                {
                    string prev = recFile ?? PreviousPart(next);
                    recFile = next;
                    string g;
                    if (prev != null && cfg.SortRecordings) Move(prev, recFg ?? fg, recFg != null ? recLast : last, Recordings, recStart != DateTime.MinValue ? recStart : at, out g);
                });
            }
        }

        // the part OBS just closed, when its name was not reported: the newest video next to the new part
        string PreviousPart(string next)
        {
            if (string.IsNullOrEmpty(next)) return null;
            try
            {
                var fi = new FileInfo(next.Replace('/', '\\'));
                var hit = fi.Directory.GetFiles().Where(f => f.FullName != fi.FullName && Regex.IsMatch(f.Extension, @"^\.(mp4|mkv|mov|flv|ts|m3u8)$", RegexOptions.IgnoreCase) &&
                                                         (recStart == DateTime.MinValue || f.LastWriteTime >= recStart))
                    .OrderByDescending(f => f.LastWriteTime).FirstOrDefault();
                return hit == null ? null : hit.FullName;
            }
            catch { return null; }
        }

        // which folder: your names → the game in front → the last game if it still runs → the fallback
        public string Resolve(WinInfo fg, WinInfo last, string root, out bool fallback)
        {
            fallback = false;
            string custom = Games.Custom(cfg.SortNames, fg);
            if (custom != null) return Games.Clean(custom);
            var game = Games.IsGame(fg) ? fg : Foreground.Alive(last) ? last : null;
            if (game != null)
            {
                custom = Games.Custom(cfg.SortNames, game);
                string f = custom != null ? Games.Clean(custom) : Games.Folder(game, root);
                if (!string.IsNullOrEmpty(f)) return f;
            }
            fallback = true;
            return Fallback;
        }

        string Fallback { get { string f = Games.Clean(cfg.SortFallback); return f != "" ? f : "Desktop"; } }

        // for the settings page: what would be chosen right now
        public string Preview(string root)
        {
            var fg = Foreground.Now();
            bool fallback;
            string folder = Resolve(fg, lastGame, root ?? Path.GetTempPath(), out fallback);
            string seen = fg == null ? "?" : (fg.Name ?? fg.Title) + (fg.Exe != null ? ".exe" : "");
            return fallback ? L.T("in front: " + seen + " — no game, the clip would go to \"" + folder + "\"", "впереди: " + seen + " — игры нет, клип ушёл бы в «" + folder + "»")
                            : L.T("in front: " + seen + " — the clip would go to \"" + folder + "\"", "впереди: " + seen + " — клип ушёл бы в «" + folder + "»");
        }

        // {game}\{year}-{month} → "Hunt Showdown\2026-10"; each part is a safe folder name, empty parts are dropped
        public static string Expand(string template, string game, string type, DateTime t)
        {
            if (string.IsNullOrWhiteSpace(template)) template = "{game}";
            var parts = new List<string>();
            foreach (var seg in template.Split('/', '\\'))
            {
                string s = Regex.Replace(seg, @"\{(\w+)\}", m =>
                {
                    switch (m.Groups[1].Value.ToLowerInvariant())
                    {
                        case "game": return game;
                        case "type": return type;
                        case "year": return t.ToString("yyyy");
                        case "month": return t.ToString("MM");
                        case "day": return t.ToString("dd");
                        case "date": return t.ToString("yyyy-MM-dd");
                        case "yearmonth": return t.ToString("yyyy-MM");
                        case "hour": return t.ToString("HH");
                        case "min": return t.ToString("mm");
                        default: return m.Value;
                    }
                });
                s = Trimmer.SafeName(s);
                if (s != "" && s != "." && s != "..") parts.Add(s);
            }
            return string.Join("\\", parts);
        }

        public static string Unique(string path)
        {
            if (!File.Exists(path)) return path;
            string dir = Path.GetDirectoryName(path), name = Path.GetFileNameWithoutExtension(path), ext = Path.GetExtension(path);
            for (int i = 2; ; i++)
            {
                string p = Path.Combine(dir, name + " (" + i + ")" + ext);
                if (!File.Exists(p)) return p;
            }
        }

        // moves a saved file into its folder; returns the new path (null — left where it was)
        string Move(string path, WinInfo fg, WinInfo last, string type, DateTime at, out string game)
        {
            game = null;
            path = path.Replace('/', '\\');
            string root = Path.GetDirectoryName(path);
            bool fallback;
            string folder = Resolve(fg, last, root, out fallback);
            if (!fallback) game = folder;
            string rel = Expand(cfg.SortTemplate, folder, type, at);
            string dir = rel == "" ? root : Path.Combine(root, rel);
            string name = Path.GetFileName(path);
            if (cfg.SortPrefix && !fallback && !name.StartsWith(folder + " - ", StringComparison.OrdinalIgnoreCase)) name = folder + " - " + name;
            if (string.Equals(dir.TrimEnd('\\'), root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) && name == Path.GetFileName(path)) return path;

            // OBS or an antivirus may hold the file for a moment after the save
            for (int attempt = 0; ; attempt++)
            {
                if (!File.Exists(path))
                {
                    Log.Write("sorter: " + path + " is gone — moved by someone else?");
                    return null;
                }
                try
                {
                    Directory.CreateDirectory(dir);
                    string to = Unique(Path.Combine(dir, name));
                    File.Move(path, to);
                    ev(L.T("→ ", "→ ") + (type == Replays ? L.T("clip", "клип") : type == Recordings ? L.T("recording", "запись") : L.T("screenshot", "скриншот")) +
                       L.T(" moved to ", " перемещён в ") + rel);
                    return to;
                }
                catch (IOException ex)
                {
                    if (attempt >= 20) { ev(L.T("⚠ could not move ", "⚠ не удалось переместить ") + Path.GetFileName(path) + ": " + ex.Message); return null; }
                    Thread.Sleep(500);
                }
                catch (Exception ex)
                {
                    ev(L.T("⚠ could not move ", "⚠ не удалось переместить ") + Path.GetFileName(path) + ": " + ex.Message);
                    return null;
                }
            }
        }
    }

    // The Smart Replay Mover script in OBS: is it still connected (both would move the same files), its settings to take over,
    // and whether OBS has its own "Save Replay" hotkey (the script's hotkey goes away with it)
    static class SmartReplayMover
    {
        static string ObsDir { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "obs-studio"); } }

        // the script's settings, or null when it is not connected in any scene collection
        public static Dictionary<string, object> Find()
        {
            try
            {
                string dir = Path.Combine(ObsDir, @"basic\scenes");
                if (!Directory.Exists(dir)) return null;
                foreach (var f in Directory.GetFiles(dir, "*.json"))
                {
                    string text = File.ReadAllText(f, Encoding.UTF8);
                    if (text.IndexOf("replay_mover", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    var modules = Json.GetObj(Json.Obj(Json.Parse(text)), "modules");
                    foreach (var o in Json.GetArr(modules, "scripts-tool"))
                    {
                        var s = Json.Obj(o);
                        string path = Json.GetStr(s, "path") ?? "";
                        if (path.IndexOf("replay_mover", StringComparison.OrdinalIgnoreCase) >= 0)
                            return Json.GetObj(s, "settings") ?? new Dictionary<string, object>();
                    }
                }
            }
            catch (Exception ex) { Log.Write("smart replay mover lookup: " + ex.Message); }
            return null;
        }

        // the script's folders, names and switches (its defaults where they were never changed)
        public static void Import(Dictionary<string, object> s, Settings cfg)
        {
            cfg.SortTemplate = (Json.GetStr(s, "folder_template") ?? "{game}").Replace('/', '\\');
            cfg.SortPrefix = Json.GetBool(s, "add_game_prefix", true);
            cfg.SortFallback = Json.GetStr(s, "fallback_folder") ?? "Desktop";
            cfg.SortRecordings = Json.GetBool(s, "organize_recordings", true);
            cfg.SortScreenshots = Json.GetBool(s, "organize_screenshots", true);
            var names = Json.GetArr(s, "custom_names").Select(o => Json.GetStr(Json.Obj(o), "value")).Where(v => !string.IsNullOrWhiteSpace(v)).ToList();
            if (names.Count > 0) cfg.SortNames = string.Join("\n", names);
            Log.Write("smart replay mover settings taken over: " + cfg.SortTemplate + ", prefix " + cfg.SortPrefix + ", " + names.Count + " names");
        }

        // false — no OBS profile has a key for "Save Replay"
        public static bool SaveHotkeySet()
        {
            try
            {
                string dir = Path.Combine(ObsDir, @"basic\profiles");
                if (!Directory.Exists(dir)) return true;
                foreach (var ini in Directory.GetFiles(dir, "basic.ini", SearchOption.AllDirectories))
                    foreach (var line in File.ReadAllLines(ini, Encoding.UTF8))
                        if (line.StartsWith("ReplayBuffer=") && line.Contains("\"key\"")) return true;
                return false;
            }
            catch { return true; }
        }
    }
}
