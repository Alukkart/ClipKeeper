using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace DeviceGuard
{
    static class Program
    {
        public static string Dir;      // ClipKeeper's data: next to the exe (portable), or %LOCALAPPDATA%\ClipKeeper if that folder is read-only
        public static string ExeDir;   // the exe folder: ffmpeg\ comes with it
        public static string Installer;   // "winget" / "scoop" — that package manager owns the exe folder; null — unpacked by hand

        // winget unpacks the zip into ...\WinGet\Packages\Alukkart.ClipKeeper_<source>\, Scoop into
        // <scoop>\apps\clipkeeper\<version or current>\ (next to <scoop>\shims). Both replace that folder on an update
        public static string InstalledBy(string exeDir, Func<string, bool> dirExists)
        {
            string d = (exeDir ?? "").TrimEnd('\\');
            if (d.IndexOf(@"\WinGet\Packages\", StringComparison.OrdinalIgnoreCase) >= 0) return "winget";
            var m = System.Text.RegularExpressions.Regex.Match(d, @"^(.+)\\apps\\clipkeeper\\[^\\]+$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (m.Success && dirExists(Path.Combine(m.Groups[1].Value, "shims"))) return "scoop";
            return null;
        }

        // a folder like Program Files can't be written without admin rights: settings, the log and the cache would silently
        // not be saved; a package manager's folder is replaced on its update. Then the data lives in %LOCALAPPDATA%\ClipKeeper;
        // what was already next to the exe is copied there once
        static string DataDir(string exeDir)
        {
            if (Installer == null && Writable(exeDir)) return exeDir;
            string local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClipKeeper");
            try
            {
                Directory.CreateDirectory(local);
                if (!File.Exists(Path.Combine(local, "settings.json")))
                {
                    foreach (var name in new[] { "settings.json", "devices.json", "favorites.json", "reviewed.json", "clipstats.json", "clipcache.json" })
                        if (File.Exists(Path.Combine(exeDir, name))) File.Copy(Path.Combine(exeDir, name), Path.Combine(local, name));
                    string covers = Path.Combine(exeDir, "covers");
                    if (Directory.Exists(covers))
                    {
                        Directory.CreateDirectory(Path.Combine(local, "covers"));
                        foreach (var f in Directory.GetFiles(covers)) File.Copy(f, Path.Combine(local, "covers", Path.GetFileName(f)), true);
                    }
                }
            }
            catch { }
            return local;
        }

        public static bool Writable(string dir)
        {
            try
            {
                string probe = Path.Combine(dir, "write-test-" + Guid.NewGuid().ToString("N") + ".tmp");
                File.WriteAllText(probe, "");
                File.Delete(probe);
                return true;
            }
            catch { return false; }
        }

        [DllImport("user32.dll")]
        static extern bool SetProcessDPIAware();

        // service modes that need WPF (no tray icon)
        static int RunWpf(Func<int> body)
        {
            var wpf = new System.Windows.Application { ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown };
            Wpf.LoadStyles(wpf);
            return body();
        }

        // version from the release tag (src/AssemblyInfo.cs); a local build is "0.0.0-dev"
        public static string Version
        {
            get
            {
                var a = (System.Reflection.AssemblyInformationalVersionAttribute)Attribute.GetCustomAttribute(
                    typeof(Program).Assembly, typeof(System.Reflection.AssemblyInformationalVersionAttribute));
                return a != null ? a.InformationalVersion : "?";
            }
        }

        [STAThread]
        static int Main(string[] args)
        {
            ExeDir = AppDomain.CurrentDomain.BaseDirectory;
            Installer = InstalledBy(ExeDir, Directory.Exists);
            Dir = DataDir(ExeDir);
            Log.FilePath = Path.Combine(Dir, "ClipKeeper.log");
            if (!string.Equals(Dir.TrimEnd('\\'), ExeDir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                Log.Write((Installer != null ? "installed with " + Installer : "the program folder is read-only") + " — data is kept in " + Dir);

            // --lang en|ru picks the language for previews and tests; --after <pid> waits for the old copy to exit (restart)
            string lang = TakeArg(ref args, "--lang"), after = TakeArg(ref args, "--after");
            bool selftest = args.Length > 0 && args[0] == "--selftest";
            L.Init(lang ?? (selftest ? L.En : Settings.Load(Path.Combine(Dir, "settings.json")).Language));
            int pid;
            if (after != null && int.TryParse(after, out pid))
                try { using (var old = System.Diagnostics.Process.GetProcessById(pid)) old.WaitForExit(15000); } catch { }

            if (selftest)   // with WPF: the windows are built too
                return RunWpf(() => SelfTest.Run(args.Length > 1 ? args[1] : Path.Combine(Dir, "selftest.txt")));
            if (args.Length > 0 && args[0] == "--shot")
                return SelfTest.Shot(args.Length > 1 ? args[1] : Path.Combine(Dir, "shot.txt"));
            if (args.Length > 0 && args[0] == "--make-icon")
                return RunWpf(() => Icons.MakeFile(args.Length > 1 ? args[1] : Path.Combine(Dir, "app.ico")));
            if (args.Length > 0 && args[0] == "--preview-settings")
                return RunWpf(() => Preview.SettingsScreens(args.Length > 1 ? args[1] : Path.Combine(Dir, "preview")));
            if (args.Length > 0 && args[0] == "--showcase")
                return RunWpf(() => Preview.Showcase(args.Length > 1 ? args[1] : Path.Combine(Dir, "preview", "showcase")));
            if (args.Length > 0 && args[0] == "--preview")
                return RunWpf(() => Preview.Run(args.Length > 1 ? args[1] : Path.Combine(Dir, "preview")));
            if (args.Length > 4 && args[0] == "--mergetest")
                return SelfTest.MergeTest(new[] { args[1], args[2] }, args[3], args[4]);
            if (args.Length > 3 && args[0] == "--trimtest")
                return SelfTest.TrimTest(args[1], args[2], args[3]);
            if (args.Length > 2 && args[0] == "--playtest")
            {
                var wpf = new System.Windows.Application { ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown };
                wpf.DispatcherUnhandledException += (s, e) =>
                {
                    File.AppendAllText(args[2], Environment.NewLine + "EXCEPTION: " + e.Exception, Encoding.UTF8);
                    e.Handled = true;
                    wpf.Shutdown();
                };
                Wpf.LoadStyles(wpf);
                var tw = new TrimWindow(null, Settings.Load(Path.Combine(Dir, "settings.json")), args[1], 3);
                tw.PlayTest(args[2], () => wpf.Shutdown());
                wpf.Run();
                return 0;
            }
            if (args.Length > 2 && args[0] == "--keytest")
            {
                var wpf = new System.Windows.Application { ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown };
                wpf.DispatcherUnhandledException += (s, e) =>
                {
                    File.AppendAllText(args[2], Environment.NewLine + "EXCEPTION: " + e.Exception, Encoding.UTF8);
                    e.Handled = true;
                    wpf.Shutdown();
                };
                Wpf.LoadStyles(wpf);
                var tw = new TrimWindow(null, Settings.Load(Path.Combine(Dir, "settings.json")), args[1], 3);
                tw.KeyTest(args[2], () => wpf.Shutdown());
                wpf.Run();
                return 0;
            }
            if (args.Length > 0 && args[0] == "--probe")
                return SelfTest.Probe(args.Length > 1 ? args[1] : Path.Combine(Dir, "probe.txt"));

            bool created;
            using (var mutex = new Mutex(true, @"Local\OBSDeviceGuard", out created))
            {
                if (args.Contains("--exit")) return created ? 0 : ExitRunning(mutex);
                if (!created)
                {
                    if (args.Contains("--ensure")) return 0;   // started by OBS (ObsScript) while already running — nothing to do
                    // already running — ask that copy to open its window (instead of a plain "already running")
                    try { using (var ev = EventWaitHandle.OpenExisting(TrayApp.ShowEventName)) ev.Set(); }
                    catch { MessageBox.Show(L.T("ClipKeeper is already running — see the tray icon.", "ClipKeeper уже запущен — значок в трее."), "ClipKeeper"); }
                    return 0;
                }
                try { SetProcessDPIAware(); } catch { }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.ThreadException += (s, e) => Log.Write("UI error: " + e.Exception);
                AppDomain.CurrentDomain.UnhandledException += (s, e) =>
                {
                    Log.Write("fatal: " + e.ExceptionObject);
                    Report.Crashed(e.ExceptionObject as Exception);   // instead of disappearing without a word
                };
                var wpf = new System.Windows.Application { ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown };
                wpf.DispatcherUnhandledException += (s, e) => { Log.Write("UI error: " + e.Exception); e.Handled = true; };
                Wpf.LoadStyles(wpf);
                Log.Write("=== ClipKeeper " + Version + " started (" + L.Code + ") ===");
                try { Autostart.Migrate(); } catch (Exception ex) { Log.Write("autostart: " + ex.Message); }
                if (Updates.OnStart(args)) return 0;   // the new version crashed last time — the old one is starting instead
                new TrayApp(args);
                wpf.Run();
                Log.Write("=== ClipKeeper closed ===");
            }
            return 0;
        }

        // ClipKeeper.exe --exit — the setup and its uninstaller close the running copy before they touch its files: that copy
        // exits the way "Exit" in the tray does. 0 — it is gone (or was not running), 1 — it is still there after 15 s
        static int ExitRunning(Mutex mine)
        {
            try { using (var ev = EventWaitHandle.OpenExisting(TrayApp.ExitEventName)) ev.Set(); }
            catch { return 1; }   // a copy too old to know the signal
            mine.Dispose();   // our handle keeps the mutex alive too
            for (int i = 0; i < 75; i++)
            {
                Mutex m;
                if (!Mutex.TryOpenExisting(@"Local\OBSDeviceGuard", out m)) return 0;
                m.Dispose();
                Thread.Sleep(200);
            }
            return 1;
        }

        // removes "--name value" from the arguments and returns the value
        static string TakeArg(ref string[] args, string name)
        {
            int i = Array.IndexOf(args, name);
            if (i < 0 || i + 1 >= args.Length) return null;
            string v = args[i + 1];
            args = args.Take(i).Concat(args.Skip(i + 2)).ToArray();
            return v;
        }
    }

    // ClipKeeper.exe --selftest [file] — logic checks without OBS (changes nothing)
    // ClipKeeper.exe --probe [file]    — checks that the OBS WebSocket answers (no password)
    static class SelfTest
    {
        public static int Run(string outPath)
        {
            var sb = new StringBuilder();
            int fails = 0;
            Action<bool, string> check = (ok, what) =>
            {
                sb.AppendLine((ok ? "PASS " : "FAIL ") + what);
                if (!ok) fails++;
            };

            const string GAME = "SteelSeries Sonar - Gaming (SteelSeries Sonar Virtual Audio Device)";
            const string MON = "MSI MAG 275QF: 2560x1440 @ 0,0 (Основной монитор)";
            const string OLD_MON = @"\\?\DISPLAY#MSI4CE2#5&1476aa08&0&UID4355#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}";
            var game = new RefEntry { Type = "wasapi_output_capture", Prop = "device_id", Value = "{old}", Display = GAME, LastName = GAME };
            Func<string, string, List<DevItem>> outs = (g, extraName) =>
            {
                var l = new List<DevItem> { new DevItem("По умолчанию", "default"), new DevItem("Динамики (Realtek(R) Audio)", "{rt}") };
                if (g != null) l.Add(new DevItem(extraName ?? GAME, g));
                return l;
            };

            var r = Matcher.Resolve(game.Type, "{old}", game, outs("{old}", null));
            check(r.Kind == "ok", "device present → ok");
            r = Matcher.Resolve(game.Type, "{old}", game, outs("{new}", null));
            check(r.Kind == "fix" && r.Value == "{new}", "GG changed the GUID → found by name");
            r = Matcher.Resolve(game.Type, "{old}", game, outs("{new}", "2- " + GAME));
            check(r.Kind == "fix" && r.Value == "{new}", "name with a \"2- \" prefix → found");
            r = Matcher.Resolve(game.Type, "{old}", game, outs(null, null));
            check(r.Kind == "missing", "Sonar is gone → alarm, not Default/Realtek (" + r.How + ")");
            r = Matcher.Resolve(game.Type, "default", game, outs("{old}", null));
            check(r.Kind == "fix" && r.Value == "{old}", "OBS was set to Default → restored by ID");
            var two = outs(null, null);
            two.Add(new DevItem("2- " + GAME, "{a}"));
            two.Add(new DevItem("3- " + GAME, "{b}"));
            r = Matcher.Resolve(game.Type, "{old}", game, two);
            check(r.Kind == "missing" && r.How.Contains("several"), "two similar devices → no guessing");
            var ph = outs(null, null);
            ph.Add(new DevItem("[Устройство не подключено]", "{old}"));
            r = Matcher.Resolve(game.Type, "{old}", game, ph);
            check(r.Kind == "missing", "a \"[...]\" placeholder is not a live device");

            var mon = new RefEntry { Type = "monitor_capture", Prop = "monitor_id", Value = OLD_MON, Display = MON, LastName = MON };
            const string NEW_MON = @"\\?\DISPLAY#MSI4CE2#7&dead&0&UID9#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}";
            r = Matcher.Resolve(mon.Type, OLD_MON, mon, new List<DevItem> {
                new DevItem("MSI MAG 275QF: 1920x1080 @ 0,0 (Основной монитор)", NEW_MON),
                new DevItem("DELL U2419: 1920x1080 @ 1920,0", @"\\?\DISPLAY#DEL1234#1&1&0&UID1#{x}") });
            check(r.Kind == "fix" && r.Value == NEW_MON, "monitor on another port with another resolution → found");

            // JSON and importing the old config
            string parent = Path.GetDirectoryName(Program.Dir.TrimEnd('\\'));
            string legacy = Path.Combine(parent, "audio_device_guard_config.json");
            if (File.Exists(legacy))
            {
                string imported;
                var store = RefStore.Load(Path.Combine(Path.GetTempPath(), "dg_nonexistent.json"), new[] { legacy }, out imported);
                check(store.Items.Count == 4 && imported != null, "old config imports (" + store.Items.Count + " sources)");
                string tmp = Path.Combine(Path.GetTempPath(), "dg_selftest.json");
                store.Save(tmp);
                string dummy;
                var back = RefStore.Load(tmp, new string[0], out dummy);
                check(back.Items.Count == store.Items.Count && back.Items["game"].Display == store.Items["game"].Display,
                      "devices.json: write and read back (Cyrillic, monitor paths)");
                File.Delete(tmp);

                // Core Audio: do we see the same IDs as in the reference
                try
                {
                    using (var audio = new AudioDevices(false))
                    {
                        var render = audio.List(false);
                        var capture = audio.List(true);
                        check(render.Count > 0, "Core Audio: " + render.Count + " output devices, " + capture.Count + " input");
                        foreach (var kv in store.Items.Where(x => Matcher.IsAudio(x.Value.Type)))
                        {
                            var list = kv.Value.Type == "wasapi_input_capture" ? capture : render;
                            var hit = list.FirstOrDefault(d => d.Value == kv.Value.Value);
                            sb.AppendLine("     " + kv.Key + ": " + (hit != null ? "ID present, name \"" + hit.Name + "\""
                                                                     : "reference ID is not in the system now"));
                        }
                        sb.AppendLine("     All active devices:");
                        foreach (var d in render) sb.AppendLine("       [out] " + d.Name);
                        foreach (var d in capture) sb.AppendLine("       [in]  " + d.Name);
                    }
                }
                catch (Exception ex) { check(false, "Core Audio: " + ex.Message); }
            }

            // volume: signal peak after scaling
            const string alarm = @"C:\Windows\Media\Alarm01.wav";
            if (File.Exists(alarm))
            {
                var src = File.ReadAllBytes(alarm);
                Func<byte[], int> peak = w =>
                {
                    int p = 0;
                    for (int i = 44; i + 1 < w.Length; i += 2) p = Math.Max(p, Math.Abs((int)(short)(w[i] | w[i + 1] << 8)));
                    return p;
                };
                var quiet = Alarm.Scale(src, Alarm.Gain(40));
                double ratio = quiet == null ? 0 : (double)peak(quiet) / peak(src);
                check(quiet != null && Math.Abs(ratio - 0.16) < 0.01,
                      "volume 40% → peak ×" + ratio.ToString("0.000") + " (expected 0.160)");
                check(Alarm.Create(alarm, 0) == null, "volume 0% → silence");
                check(Alarm.Create(@"C:\nonexistent.wav", 50) != null, "no file → built-in beep");
                check(Alarm.Create(@"C:\nonexistent.wav", 50, true) != null && Alarm.Create(@"C:\nonexistent.wav", 0, true) == null,
                      "clip sound: no file → built-in chime, volume 0% → silence");
            }

            // built-in sounds: each renders, the volume scales it, an unknown one falls back to the default
            {
                Func<string, int, bool, byte[]> bytes = (f, v, chime) =>
                {
                    var p = Alarm.Create(f, v, chime);
                    var ms = new MemoryStream();
                    p.Stream.Position = 0;
                    p.Stream.CopyTo(ms);
                    return ms.ToArray();
                };
                Func<byte[], int> peak = w =>
                {
                    int p = 0;
                    for (int i = 44; i + 1 < w.Length; i += 2) p = Math.Max(p, Math.Abs((int)(short)(w[i] | w[i + 1] << 8)));
                    return p;
                };
                foreach (var id in Alarm.ClipSounds.Concat(Alarm.AlarmSounds))
                {
                    bool chime = Alarm.ClipSounds.Contains(id);
                    var full = bytes(Alarm.Builtin + id, 100, chime);
                    double sec = (full.Length - 44) / 88200.0, top = peak(full) / 32767.0;
                    check(sec > 0.3 && sec < 2 && top > 0.7 && top < 0.9, "built-in sound " + id + ": " + sec.ToString("0.00") + " s, peak " + top.ToString("0.00"));
                }
                double half = (double)peak(bytes(Alarm.DefaultAlarm, 50, false)) / peak(bytes(Alarm.DefaultAlarm, 100, false));
                check(Math.Abs(half - 0.25) < 0.01, "built-in sound at 50% → peak ×" + half.ToString("0.000") + " (expected 0.250)");
                check(bytes(Alarm.Builtin + "nope", 100, true).SequenceEqual(bytes(Alarm.DefaultClip, 100, true)) &&
                      bytes(Alarm.Builtin + "marimba", 100, false).SequenceEqual(bytes(Alarm.DefaultAlarm, 100, false)),
                      "unknown or the other kind's built-in sound → the default one");

                string tmp = Path.Combine(Path.GetTempPath(), "ck-sound-settings.json");
                File.WriteAllText(tmp, "{\"SoundFile\": \"C:\\\\Windows\\\\Media\\\\Alarm01.wav\", \"ClipSoundFile\": \"D:\\\\my.wav\"}");
                var st = Settings.Load(tmp);
                File.Delete(tmp);
                check(st.SoundFile == Alarm.DefaultAlarm && st.ClipSoundFile == @"D:\my.wav", "settings: the old Windows default → built-in, an own file stays");
            }

            // monitors from Windows in the OBS format
            {
                var ids = Monitors.ActiveIds();
                string parentDir = Path.GetDirectoryName(Program.Dir.TrimEnd('\\'));
                string dv;
                var cur = RefStore.Load(Path.Combine(Program.Dir, "devices.json"),
                                        new[] { Path.Combine(parentDir, "audio_device_guard_config.json") }, out dv);
                foreach (var kv in cur.Items.Where(x => Matcher.IsMonitor(x.Value.Type)))
                    check(ids.Any(i => string.Equals(i, kv.Value.Value, StringComparison.OrdinalIgnoreCase)),
                          "reference monitor is visible in Windows without asking OBS (" + ids.Count + " active)");
            }

            // screen: a single-color frame
            Func<Action<System.Drawing.Graphics>, string> shot = draw =>
            {
                using (var bmp = new System.Drawing.Bitmap(64, 36))
                using (var ms = new MemoryStream())
                {
                    using (var g = System.Drawing.Graphics.FromImage(bmp)) draw(g);
                    bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                    return Pixels.UniformKind("data:image/png;base64," + Convert.ToBase64String(ms.ToArray()));
                }
            };
            check(shot(g => g.Clear(System.Drawing.Color.Black)) == Pixels.Black, "frame: black");
            check(shot(g => g.Clear(System.Drawing.Color.White)) == Pixels.White, "frame: white");
            check(shot(g => g.Clear(System.Drawing.Color.FromArgb(90, 90, 200))) == Pixels.Uniform, "frame: filled with one color");
            check(shot(g => g.Clear(System.Drawing.Color.Transparent)) == Pixels.Black, "frame: transparent (capture dropped) = black");
            check(shot(g => { g.Clear(System.Drawing.Color.Black); g.FillRectangle(System.Drawing.Brushes.White, 2, 30, 10, 3); }) == null,
                  "frame: dark scene with a HUD — fine");
            check(shot(g => { g.Clear(System.Drawing.Color.White); g.FillRectangle(System.Drawing.Brushes.DimGray, 10, 10, 20, 2); }) == null,
                  "frame: white page with text — fine");

            // MP4: real clips
            try
            {
                var mp4s = new DirectoryInfo(@"D:\Sources").EnumerateFiles("*.mp4", SearchOption.AllDirectories)
                    .OrderByDescending(f => f.LastWriteTime).Take(5).ToList();
                foreach (var f in mp4s)
                {
                    var info = Mp4.Read(f.FullName);
                    check(info != null && info.Duration > 0 && info.Video == 1 && info.Audio >= 1,
                          "clip " + f.Name + ": " + (info == null ? "not read"
                          : info.Duration.ToString("0.0") + " s, video " + info.Video + ", audio " + info.Audio + ", " + f.Length / 1048576 + " MB"));
                }
            }
            catch (Exception ex) { sb.AppendLine("     clips not checked: " + ex.Message); }

            // Windows level meters
            try
            {
                using (var a = new AudioDevices(false))
                    foreach (var d in a.List(false).Concat(a.List(true)).Where(x => x.Name.Contains("Sonar")))
                    {
                        float p = a.Peak(d.Value);
                        check(p >= 0, "level meter: " + d.Name + " = " + p.ToString("0.000"));
                    }
            }
            catch (Exception ex) { check(false, "level meter: " + ex.Message); }

            string hash = ObsClient.AuthString("supersecretpassword", "lM1GncleQOaCu9lT1yeUZhFYnqhsLLP1G5lAGo3ixaI=",
                                               "+IxH4CnCiqpX1rM9scsNynZzbOe4KhDeYcTNS3PDaeY=");
            check(hash.Length == 44, "obs-websocket auth hash");

            // cuts: what remains of the 10..40 range
            Func<List<double[]>, string> segs = l => string.Join(" ", l.Select(x => Ffmpeg.T(x[0]) + "-" + Ffmpeg.T(x[1])));
            var kept = Trimmer.KeptSegments(10, 40, null);
            check(segs(kept) == "10-40", "cuts: no cuts — the whole range (" + segs(kept) + ")");
            kept = Trimmer.KeptSegments(10, 40, new List<double[]> { new[] { 20.0, 25.0 } });
            check(segs(kept) == "10-20 25-40", "cuts: one in the middle (" + segs(kept) + ")");
            kept = Trimmer.KeptSegments(10, 40, new List<double[]> { new[] { 30.0, 35.0 }, new[] { 18.0, 22.0 }, new[] { 21.0, 24.0 } });
            check(segs(kept) == "10-18 24-30 35-40", "cuts: overlapping ones merge, any order (" + segs(kept) + ")");
            kept = Trimmer.KeptSegments(10, 40, new List<double[]> { new[] { 5.0, 12.0 }, new[] { 38.0, 50.0 } });
            check(segs(kept) == "12-38", "cuts: ones past the edges are clipped to the range (" + segs(kept) + ")");
            kept = Trimmer.KeptSegments(10, 40, new List<double[]> { new[] { 10.0, 40.0 } });
            check(kept.Count == 0, "cuts: everything cut — no pieces");
            var cj = new TrimJob { In = 10, Out = 40, Cuts = new List<double[]> { new[] { 20.0, 25.0 } } };
            check(cj.HasCuts && Math.Abs(cj.Length - 25) < 1e-9, "cuts: result is 25 s of 30");
            cj.Cuts = new List<double[]> { new[] { 50.0, 60.0 } };
            check(!cj.HasCuts && Math.Abs(cj.Length - 30) < 1e-9, "cuts: a cut outside the range changes nothing");
            sb.AppendLine("     auth hash: " + hash);

            // dropped frames: a sample every 5 s, 60 fps
            var t0 = new DateTime(2026, 10, 4, 20, 0, 0);
            var ps = new List<PerfSample>();
            long rs = 0, os = 0;
            for (int i = 0; i <= 36; i++)   // 3 minutes
            {
                if (i > 24) rs += 12;        // last minute: the GPU drops 12 frames per sample (4%)
                if (i == 10) os += 3;        // a one-off encoder hiccup
                ps.Add(new PerfSample { At = t0.AddSeconds(i * 5), RenderTotal = i * 300, RenderSkip = rs, OutTotal = i * 300, OutSkip = os });
            }
            var tEnd = t0.AddSeconds(180);
            var lm = FrameStats.Between(ps, tEnd.AddSeconds(-60), tEnd);
            check(lm != null && lm.RenderSkip == 144 && lm.RenderTotal == 3600 && lm.OutSkip == 0, "frames: counter difference over a minute");
            check(lm != null && FrameStats.Noticeable(lm, 1.0, 20) && lm.RenderSide && Math.Abs(lm.Pct - 4) < 0.01,
                  "frames: 4% over a minute — warning, the GPU is to blame (" + (lm != null ? FrameStats.PctText(lm.Pct) : "-") + ")");
            var calm = FrameStats.Between(ps, t0.AddSeconds(30), t0.AddSeconds(90));
            check(calm != null && calm.Lost == 3 && !FrameStats.Noticeable(calm, 1.0, 20), "frames: 3 frames a minute — no alarm");
            var early = FrameStats.Between(ps, t0.AddSeconds(-140), t0.AddSeconds(20));
            check(early != null && early.RenderTotal == 1200, "frames: less history than the clip length — use what there is");
            check(FrameStats.Between(ps.Take(1).ToList(), t0, t0.AddSeconds(60)) == null, "frames: a single sample — no data");

            // the OBS log tells a failed encoder from a stop by hand (both come as STOPPING → STOPPED)
            const string rbStart = "18:00:00.000: ==== Replay Buffer Start ===========================================\n";
            const string rbStop = "21:58:28.035: ==== Replay Buffer Stop ============================================\n";
            const string encFail = "21:58:28.032: [obs-nvenc] d3d11_encode: nv.nvEncMapInputResource(enc->session, &map) failed: 8 (NV_ENC_ERR_INVALID_PARAM)\n" +
                                   "21:58:28.033: Error encoding with encoder 'advanced_video_recording'\n" +
                                   "21:58:28.033: Output 'Буфер повтора': stopping\n" +
                                   "21:58:28.033: Output 'Буфер повтора': Total frames output: 2638337\n";
            const string handStop = "21:58:28.033: Output 'Буфер повтора': stopping\n21:58:28.033: Output 'Буфер повтора': Total frames output: 2638337\n";
            bool logged;
            string encErr = ObsLog.StopError(rbStart + encFail + rbStop, out logged);
            check(logged && encErr != null && encErr.Contains("advanced_video_recording"), "OBS log: the encoder failed → the buffer crashed, not stopped by hand");
            check(ObsLog.StopError(rbStart + handStop + rbStop, out logged) == null && logged, "OBS log: a stop by hand → by hand");
            check(ObsLog.StopError("17:00:00.000: Error encoding with encoder 'x'\n" + rbStart + handStop + rbStop, out logged) == null,
                  "OBS log: an encoder error from before the buffer started does not count");
            ObsLog.StopError(rbStart + encFail + rbStop + rbStart, out logged);
            check(!logged, "OBS log: this stop is not written yet (a start after the last stop) → wait for it");

            // editor keys: overrides survive a save/load, a taken key swaps with its owner
            KeyMap.Load("");
            check(KeyMap.ActOf(System.Windows.Input.Key.X) == KeyMap.Act.Cut && KeyMap.Save() == "", "keys: defaults, nothing saved");
            KeyMap.Set(KeyMap.Act.Cut, System.Windows.Input.Key.C);
            check(KeyMap.ActOf(System.Windows.Input.Key.C) == KeyMap.Act.Cut && KeyMap.ActOf(System.Windows.Input.Key.X) == null, "keys: Cut moved to C, X is free");
            KeyMap.Set(KeyMap.Act.Mute, System.Windows.Input.Key.C);
            check(KeyMap.KeyOf(KeyMap.Act.Mute) == System.Windows.Input.Key.C && KeyMap.KeyOf(KeyMap.Act.Cut) == System.Windows.Input.Key.M,
                  "keys: a taken key swaps (Mute ↔ Cut): " + KeyMap.Save());
            string saved = KeyMap.Save();
            KeyMap.Load(saved);
            check(KeyMap.KeyOf(KeyMap.Act.Mute) == System.Windows.Input.Key.C && KeyMap.KeyOf(KeyMap.Act.Cut) == System.Windows.Input.Key.M, "keys: load back \"" + saved + "\"");
            KeyMap.Load("Cut=LeftShift;Nonsense=Q");
            check(KeyMap.KeyOf(KeyMap.Act.Cut) == System.Windows.Input.Key.X, "keys: a modifier or an unknown action is ignored");
            KeyMap.Load("");

            // video encoder: the CPU setting means no hardware arguments
            string encWas = Encoders.Setting;
            Encoders.Setting = Encoders.Cpu;
            check(Encoders.Quality("hevc", 18) == null && Encoders.Bitrate("1000k", "2000k") == null && Encoders.Fast() == null, "encoder: CPU → no hardware arguments");
            Encoders.Setting = Encoders.Amf;
            check(Encoders.Quality("hevc", 18).Contains("hevc_amf") && Encoders.Bitrate("1000k", "2000k").Contains("h264_amf"), "encoder: AMD → hevc_amf / h264_amf");
            Encoders.Setting = encWas;

            // share limits: Discord / Nitro / custom / none
            check(new TrimJob { Target = ShareTarget.Discord }.LimitMb == 10 && new TrimJob { Target = ShareTarget.Nitro }.LimitMb == 500 &&
                  new TrimJob { Target = ShareTarget.Custom, CustomMb = 25 }.LimitMb == 25 && new TrimJob { Target = ShareTarget.Telegram }.LimitMb == 2000,
                  "share: limits 10 / 500 / custom 25 / none");

            // game folders: subfolders of the OBS folder are games only when the setting says so; Ready / collection always
            var clipFile = new FileInfo(@"D:\Rec\2026-10\Replay 2026-10-05 12-00-00.mp4");
            ClipScanner.OwnRoots = new[] { @"D:\Clips" };
            ClipScanner.SubfoldersAreGames = true;
            check(ClipScanner.GameOf(clipFile, @"D:\Rec") == "2026-10", "folders: subfolder as game");
            ClipScanner.SubfoldersAreGames = false;
            check(ClipScanner.GameOf(clipFile, @"D:\Rec") == NoGame.Folder, "folders: subfolders are not games in the OBS folder");
            check(ClipScanner.GameOf(new FileInfo(@"D:\Clips\Hunt\a.mp4"), @"D:\Clips") == "Hunt", "folders: the collection's subfolders are always games");
            ClipScanner.SubfoldersAreGames = true;
            ClipScanner.OwnRoots = new string[0];

            // sorting by game: folder template, your names, readable exe names, the exe in front of old file names
            var at = new DateTime(2026, 10, 5, 12, 0, 0);
            check(Sorter.Expand(@"{game}\{year}-{month}", "Hunt Showdown", Sorter.Replays, at) == @"Hunt Showdown\2026-10", "sort: {game}\\{year}-{month}");
            check(Sorter.Expand("{game}/{type}", "A:B", Sorter.Screenshots, at) == @"A B\Screenshots", "sort: / works, bad characters are dropped");
            check(Sorter.Expand(@"{date}\..\{game}", "X", Sorter.Replays, at) == @"2026-10-05\X", "sort: the template cannot leave the folder");
            check(Sorter.Expand("", "X", Sorter.Replays, at) == "X", "sort: an empty template means {game}");
            const string names = "AyuGram > Desktop\n+call duty > Call of Duty\n*minecraft* > Minecraft\nC:\\Games\\HuntGame.exe > Hunt";
            check(Games.Custom(names, new WinInfo { Name = "AyuGram", Title = "Chat" }) == "Desktop", "sort names: exact exe");
            check(Games.Custom(names, new WinInfo { Name = "cod", Title = "Call of Duty® HQ" }) == "Call of Duty", "sort names: all words in the title");
            check(Games.Custom(names, new WinInfo { Name = "javaw", Title = "Minecraft 1.21" }) == "Minecraft", "sort names: anywhere");
            check(Games.Custom(names, new WinInfo { Name = "huntgame", Title = "" }) == "Hunt", "sort names: a full path, case and .exe ignored");
            check(Games.Custom(names, new WinInfo { Name = "chrome", Title = "Call me maybe" }) == null, "sort names: no match");
            check(Games.Readable("RimWorldWin64") == "Rim World" && Games.Readable("VALORANT-Win64-Shipping") == "VALORANT" && Games.Readable("ShiftAtMidnight") == "Shift At Midnight",
                  "sort: readable exe names");
            check(Games.PrefixOf("HuntGame - Replay 2026-08-07 04-50-47.mp4") == "HuntGame" && Games.PrefixOf("cod - 2026-09-05 14-24-01.mkv") == "cod" &&
                  Games.PrefixOf("Replay 2026-09-08 05-37-37.mp4") == null && Games.PrefixOf("зомби.mp4") == null, "sort: the exe in front of a file name");
            check(!Games.IsGame(new WinInfo { Pid = 1, Name = "Discord" }) && !Games.IsGame(new WinInfo { Pid = 1, Name = "x", Exe = @"C:\Windows\explorer2.exe" }) &&
                  Games.IsGame(new WinInfo { Pid = 1, Name = "HuntGame", Exe = @"D:\SteamLibrary\steamapps\common\Hunt Showdown 1896\bin\win_x64\HuntGame.exe" }),
                  "sort: Discord and Windows are not games, a Steam exe is");
            check(!Games.IsGame(new WinInfo { Pid = 1, Name = "Figma2", Exe = @"C:\Apps\Figma2.exe" }) &&
                  Games.IsGame(new WinInfo { Pid = 1, Name = "GenshinImpact", Exe = @"C:\HoYoPlay\games\GenshinImpact.exe", Full = true }) &&
                  Games.IsGame(new WinInfo { Pid = 1, Name = "VALORANT-Win64-Shipping" }),
                  "sort: a windowed app is not a game; fullscreen or hidden by anti-cheat is");

            // cleanup: only an old plain source in the OBS folder may go; everything a person might care about stays
            string croot = Path.Combine(Path.GetTempPath(), "clipkeeper-cleanup-" + Guid.NewGuid().ToString("N"));
            try
            {
                string ready = Path.Combine(croot, "Ready");
                Directory.CreateDirectory(ready);
                var old = DateTime.Now.AddDays(-60);
                Action<string, DateTime, DateTime> file = (relPath, written, created) =>
                {
                    string fp = Path.Combine(croot, relPath);
                    File.WriteAllText(fp, "x");
                    File.SetCreationTime(fp, created);
                    File.SetLastWriteTime(fp, written);
                };
                file("old.mp4", old, old);                                         // goes
                file("young.mp4", DateTime.Now.AddDays(-2), DateTime.Now.AddDays(-2));
                file("fav.mp4", old, old);                                         // a favorite
                file("trimmedsrc.mp4", old, old);                                  // has a trim in Ready
                file("trim next to source.mp4", old, old);                         // a trim saved next to its source
                file("copied.mp4", old, DateTime.Now);                             // old, but copied in today
                file("unreadable.mp4", old, old);                                  // its data can't be read
                file("notes.txt", old, old);                                       // not a video
                file(@"Ready\trim.mp4", old, old);                                 // the Ready folder is never touched
                Func<FileInfo, ClipMeta> meta = f =>
                    f.Name == "trim.mp4" ? new ClipMeta { Game = "G", Source = "trimmedsrc.mp4" } :
                    f.Name == "trim next to source.mp4" ? new ClipMeta { Game = "G", Source = "elsewhere.mp4" } : null;
                Func<FileInfo, ClipMeta> strict = f => { if (f.Name == "unreadable.mp4") throw new IOException("broken"); return meta(f); };
                Func<string, bool> fav = p => Path.GetFileName(p) == "fav.mp4";
                var plan = Cleanup.Make(croot, 30, true, new[] { ready }, meta, strict, fav, true, DateTime.Now);
                string gone = string.Join(",", plan.Files.Select(f => f.Name));
                check(plan.Refusal == null && gone == "old.mp4" && plan.Total == 7,
                      "cleanup: only the old plain source goes — favorite, young, trimmed, trim, copied-in, unreadable, Ready stay (" + (plan.Refusal ?? gone + ", of " + plan.Total) + ")");
                var noKeep = Cleanup.Make(croot, 30, false, new[] { ready }, meta, strict, fav, true, DateTime.Now);
                check(string.Join(",", noKeep.Files.Select(f => f.Name).OrderBy(n => n)) == "old.mp4,trimmedsrc.mp4", "cleanup: \"keep trimmed\" off — the trimmed source may go too");
                check(Cleanup.Make(croot, 30, true, new[] { ready }, meta, strict, fav, false, DateTime.Now).Refusal != null, "cleanup: without ffmpeg it refuses");
                check(!Cleanup.Make(croot, 1, true, new[] { ready }, meta, strict, fav, true, DateTime.Now).Files.Any(f => f.Name == "young.mp4"),
                      "cleanup: less than 7 days is raised to 7 (a 2-day-old clip stays)");
                Func<FileInfo, ClipMeta> brokenTrim = f => { if (f.Name == "trim.mp4") throw new IOException("locked"); return meta(f); };
                var refused = Cleanup.Make(croot, 30, true, new[] { ready }, brokenTrim, strict, fav, true, DateTime.Now);
                check(refused.Refusal != null && refused.Files.Count == 0, "cleanup: a trim that can't be read stops the whole cleanup (its source would lose protection)");
                var gone2 = Cleanup.Make(croot, 30, true, new[] { ready, Path.Combine(croot, "unplugged") }, meta, strict, fav, true, DateTime.Now);
                check(gone2.Refusal != null && gone2.Files.Count == 0, "cleanup: a Ready / Collection folder that is not there stops the cleanup (its trims can't be seen)");
                check(!Cleanup.TooMuchForAuto(plan) && Cleanup.TooMuchForAuto(new Cleanup.Plan { Total = 10, Files = Enumerable.Repeat<FileInfo>(null, 6).ToList() }),
                      "cleanup: the automatic run stops when half of the folder would go");
            }
            catch (Exception ex) { check(false, "cleanup: " + ex.Message); }
            finally { try { Directory.Delete(croot, true); } catch { } }

            // "Start ClipKeeper together with OBS": the script entry in a scene collection, and nothing else of OBS changes
            string sp1 = @"C:\Users\Ян\AppData\Roaming\ClipKeeper\clipkeeper_start.lua", sp2 = @"D:\Moved\ClipKeeper\clipkeeper_start.lua";
            var coll = Json.Obj(Json.Parse("{\"name\":\"Main\",\"modules\":{\"scripts-tool\":[{\"path\":\"C:/x/other.lua\",\"settings\":{\"a\":1}}]}}"));
            bool added = ObsScript.Apply(coll, true, sp1), again = ObsScript.Apply(coll, true, sp1);
            var tools = Json.GetArr(Json.GetObj(coll, "modules"), "scripts-tool");
            check(added && !again && ObsScript.Has(coll) && tools.Length == 2 && Json.GetStr(Json.Obj(tools[1]), "path") == sp1.Replace('\\', '/'),
                  "OBS script: added once, next to the person's own script, with OBS-style slashes");
            check(ObsScript.Apply(coll, true, sp2) && Json.GetStr(Json.Obj(Json.GetArr(Json.GetObj(coll, "modules"), "scripts-tool")[1]), "path") == sp2.Replace('\\', '/'),
                  "OBS script: an old path is pointed at the current file");
            bool removed = ObsScript.Apply(coll, false, sp2);
            tools = Json.GetArr(Json.GetObj(coll, "modules"), "scripts-tool");
            check(removed && !ObsScript.Has(coll) && tools.Length == 1 && Json.GetStr(Json.Obj(tools[0]), "path") == "C:/x/other.lua",
                  "OBS script: turning it off removes only it — the person's own script stays");
            var bare = Json.Obj(Json.Parse("{\"name\":\"Empty\"}"));
            check(ObsScript.Apply(bare, true, sp1) && ObsScript.Has(bare) && !ObsScript.Apply(Json.Obj(Json.Parse("{\"name\":\"E\"}")), false, sp1),
                  "OBS script: a collection without scripts gets the list; nothing to remove — nothing changes");
            string lua = ObsScript.Script(@"C:\Игры\Clip ""K""\ClipKeeper.exe");
            check(lua.Contains("local exe = \"C:\\\\Игры\\\\Clip \\\"K\\\"\\\\ClipKeeper.exe\"") && lua.Contains("--ensure --tray") && lua.Contains("script_description"),
                  "OBS script: the exe path is a proper Lua string (backslashes, quotes, Cyrillic)");
            string round = Json.Write(Json.Parse("{\"a\":1.0,\"b\":0.25,\"c\":12345678901234,\"d\":\"Сцена\",\"e\":[true,null]}"), false);
            check(round == "{\"a\":1.0,\"b\":0.25,\"c\":12345678901234,\"d\":\"Сцена\",\"e\":[true,null]}",
                  "OBS script: OBS files keep their numbers and text when written back (" + round + ")");

            // the OBS save key, as basic.ini keeps it ("\n" as two characters)
            var sk = SaveKey.Parse("{\\n    \"ReplayBuffer.Save\": [\\n        {\\n            \"control\": true,\\n            \"key\": \"OBS_KEY_F9\"\\n        },\\n" +
                                   "        {\\n            \"key\": \"OBS_KEY_MOUSE4\"\\n        },\\n        {\\n            \"key\": \"OBS_KEY_HENKAN\"\\n        }\\n    ]\\n}");
            check(sk.Count == 2 && sk[0].Vk == 0x78 && sk[0].Ctrl && !sk[0].Shift && sk[0].ToString() == "Ctrl+F9" && sk[1].Vk == 0x05,
                  "OBS save key: Ctrl+F9 and mouse 4 read from basic.ini, an unknown key skipped (" + string.Join(", ", sk.Select(c => c.ToString())) + ")");
            check(SaveKey.Vk("OBS_KEY_A") == 0x41 && SaveKey.Vk("OBS_KEY_NUM5") == 0x65 && SaveKey.Vk("OBS_KEY_F13") == 0x7C && SaveKey.Vk("nonsense") == null,
                  "OBS save key: key names → Windows keys");

            // global hotkeys: stored as text, a modifier is required
            var hk = Hotkey.Parse("Ctrl+Alt+F9");
            check(hk != null && hk.Key == System.Windows.Input.Key.F9 && hk.ToString() == "Ctrl+Alt+F9", "hotkeys: Ctrl+Alt+F9 reads and writes back");
            check(Hotkey.Parse("shift+ctrl+5").ToString() == "Ctrl+Shift+5", "hotkeys: any case and order, digits");
            check(Hotkey.Parse("F9") == null && Hotkey.Parse("Ctrl+") == null && Hotkey.Parse("Ctrl+Alt") == null && Hotkey.Parse("Hyper+F9") == null && Hotkey.Parse("") == null,
                  "hotkeys: a plain key, a modifier alone, nonsense — not a hotkey");
            check(Hotkey.Parse("Ctrl+Shift+K").Refusal() == null && Hotkey.Parse("Ctrl+F9").Refusal() == null && Hotkey.Parse("Ctrl+Shift+5").Refusal() == null &&
                  Hotkey.Parse("Ctrl+Alt+F9").Refusal() == null && Hotkey.Parse("Win+Ctrl+Alt+K").Refusal() == null,
                  "hotkeys: two modifiers, or one with an F key, are allowed");
            check(Hotkey.Parse("Ctrl+Alt+Q").Refusal() != null && Hotkey.Parse("Ctrl+Alt+Shift+7").Refusal() != null,
                  "hotkeys: Ctrl+Alt with a letter or digit is refused — it is AltGr (@, {) on many keyboards");
            check(Hotkey.Parse("Ctrl+C").Refusal() != null && Hotkey.Parse("Shift+A").Refusal() != null && Hotkey.Parse("Alt+F4").Refusal() != null,
                  "hotkeys: Ctrl+C, Shift+A (capitals), Alt+F4 are refused — they would be taken from every program");

            // updates: version order, the GitHub answer, checksums
            check(Updates.Compare("1.2.10", "1.2.9") > 0 && Updates.Compare("v1.0.0", "1.0.0") == 0 && Updates.Compare("1.1.0", "1.0.9") > 0,
                  "updates: versions compare by number, \"v\" ignored");
            check(Updates.Compare("1.2.0", "1.2.0-beta.1") > 0 && Updates.Compare("1.2.0-beta.2", "1.2.0-beta.1") > 0 && Updates.Compare("1.0.0", "0.0.0-dev") > 0,
                  "updates: a pre-release is older than its version, a local build is older than any release");
            check(Updates.Compare("1.2.0-beta.10", "1.2.0-beta.9") > 0 && Updates.Compare("1.2.0-rc.1", "1.2.0-beta.3") > 0 && Updates.Compare("1.2.0-beta.1", "1.2.0-beta") > 0,
                  "updates: pre-release parts compare as numbers (beta.10 > beta.9)");
            check(Updates.Compare("nonsense", "0.0.1") < 0 && Updates.Compare("1.0.0", "") > 0, "updates: something that is not a version is the oldest");
            var rel = Updates.Parse("{\"tag_name\":\"v1.3.0\",\"html_url\":\"https://github.com/x/releases/tag/v1.3.0\",\"assets\":[" +
                                    "{\"name\":\"ClipKeeper-1.3.0.zip\",\"browser_download_url\":\"https://x/zip\"}," +
                                    "{\"name\":\"ClipKeeper.exe\",\"browser_download_url\":\"https://x/exe\"}," +
                                    "{\"name\":\"SHA256SUMS.txt\",\"browser_download_url\":\"https://x/sums\"}]}");
            check(rel != null && rel.Version == "1.3.0" && rel.ExeUrl == "https://x/exe" && rel.SumsUrl == "https://x/sums", "updates: the release answer is read");
            check(Updates.Parse("{\"message\":\"Not Found\"}") == null, "updates: no release — nothing to offer");
            const string sums = "0a1b  ClipKeeper-1.3.0.zip\r\nAABBCC  ClipKeeper.exe\r\n";
            check(Updates.HashOf(sums, "ClipKeeper.exe") == "aabbcc" && Updates.HashOf(sums, "other.exe") == null, "updates: the exe hash from SHA256SUMS.txt");
            Func<string, bool> scoopRoot = d => string.Equals(d, @"D:\Tools\scoop\shims", StringComparison.OrdinalIgnoreCase);
            check(Program.InstalledBy(@"C:\Users\a\AppData\Local\Microsoft\WinGet\Packages\Alukkart.ClipKeeper_Microsoft.Winget.Source_8wekyb3d8bbwe\ClipKeeper\", scoopRoot) == "winget" &&
                  Program.InstalledBy(@"D:\Tools\scoop\apps\clipkeeper\current\", scoopRoot) == "scoop" &&
                  Program.InstalledBy(@"D:\Tools\scoop\apps\clipkeeper\1.2.0", scoopRoot) == "scoop",
                  "updates: a copy installed with winget or Scoop is recognized by its folder");
            check(Program.InstalledBy(@"D:\Apps\ClipKeeper\", scoopRoot) == null && Program.InstalledBy(@"D:\Games\apps\clipkeeper\current\", scoopRoot) == null &&
                  Program.InstalledBy(@"D:\Tools\scoop\apps\clipkeeper\", scoopRoot) == null,
                  "updates: an unpacked copy (even in a folder named apps\\clipkeeper without Scoop) updates itself");
            check(Updates.ManagerCommand("winget") == "winget upgrade Alukkart.ClipKeeper" && Updates.ManagerCommand("scoop") == "scoop update clipkeeper" &&
                  Updates.ManagerCommand(null) == null, "updates: a package manager's copy is updated with its command");
            check(Updates.SameFolder(@"C:\Users\a\AppData\Local\Programs\ClipKeeper\", @"c:\users\a\appdata\local\programs\clipkeeper") &&
                  Updates.SameFolder("\"D:\\Apps\\ClipKeeper\"", @"D:\Apps\ClipKeeper\") && !Updates.SameFolder(@"D:\Apps\ClipKeeper", @"D:\Apps\ClipKeeper2") &&
                  !Updates.SameFolder(null, @"D:\Apps"), "updates: the setup's entry is matched to this copy by its folder");

            string obsIni = Path.Combine(Path.GetTempPath(), "clipkeeper-obs-" + Guid.NewGuid().ToString("N") + ".ini");
            try
            {
                File.WriteAllText(obsIni, "[General]\r\nServerPort=1\r\n[OBSWebSocket]\r\nFirstLoad=false\r\nServerEnabled=true\r\nServerPort=4456\r\nAuthRequired=true\r\nServerPassword=abc=12\r\n[Video]\r\n");
                var oi = ObsFind.FromIni(obsIni);
                check(oi != null && oi.Enabled && oi.Auth && oi.Port == 4456 && oi.Password == "abc=12", "find OBS: the [OBSWebSocket] section of global.ini / user.ini");
                File.WriteAllText(obsIni, "{\"alerts_enabled\":false,\"auth_required\":false,\"first_load\":false,\"server_enabled\":true,\"server_password\":\"x\",\"server_port\":4455}");
                var oj = ObsFind.FromJson(obsIni);
                check(oj != null && oj.Enabled && !oj.Auth && oj.Port == 4455, "find OBS: obs-websocket config.json");
                File.WriteAllText(obsIni, "[General]\r\nLanguage=ru-RU\r\n");
                check(ObsFind.FromIni(obsIni) == null, "find OBS: no section — nothing found");
            }
            finally { try { File.Delete(obsIni); } catch { } }

            MainWindow.TestFind(check);
            ClipNames.Test(check);
            MainWindow.TestTriage(check);
            ObsFix.Test(check);
            Trimmer.TestMerge(check);

            Windows(check);

            sb.AppendLine(fails == 0 ? "RESULT: ALL PASSED" : "RESULT: " + fails + " FAILED");
            File.WriteAllText(outPath, sb.ToString(), Encoding.UTF8);
            return fails == 0 ? 0 : 1;
        }

        // Windows: the UI is built from code and XAML at run time, so a missing style or a bad template only shows up there.
        // Every screen is built with default settings and laid out off screen (nothing is shown, nothing is saved)
        static void Windows(Action<bool, string> check)
        {
            // an error in a deferred UI call would end the process without a report — catch it and blame the step
            Exception deferred = null;
            System.Windows.Application.Current.DispatcherUnhandledException += (s, e) => { if (deferred == null) deferred = e.Exception; e.Handled = true; };
            Func<string, Action, bool> run = (what, act) =>
            {
                deferred = null;
                try { act(); }
                catch (Exception ex) { deferred = deferred ?? ex; }
                if (deferred == null) { check(true, "window: " + what); return true; }
                check(false, "window: " + what + " — " + deferred.GetType().Name + ": " + deferred.Message);
                return false;
            };
            Action<System.Windows.FrameworkElement> layout = el =>
            {
                el.Measure(new System.Windows.Size(1078, 758));
                el.Arrange(new System.Windows.Rect(0, 0, 1078, 758));
                el.UpdateLayout();
                Preview.Pump();
                el.UpdateLayout();
            };
            // a settings file that does not exist: defaults, never written
            Func<Settings> fresh = () => Settings.Load(Path.Combine(Path.GetTempPath(), "clipkeeper-selftest-" + Guid.NewGuid().ToString("N") + ".json"));
            string dummy;
            var refs = RefStore.Load(Path.Combine(Path.GetTempPath(), "clipkeeper-selftest-none.json"), new string[0], out dummy);

            var cfg = fresh();
            cfg.SortClips = true;   // so the sorting cards are built too
            MainWindow mw = null;
            if (!run("the main window builds", () => mw = new MainWindow(null, cfg))) return;
            var root = (System.Windows.FrameworkElement)mw.W.Content;
            foreach (var page in new[] { MainWindow.PageRecord, MainWindow.PageClips, MainWindow.PageStats, MainWindow.PageSettings })
                run("page " + page + " lays out", () => { mw.ShowPage(page); mw.Update(Preview.Fake(refs, page == MainWindow.PageRecord ? 2 : 0)); layout(root); });

            run("library: game cards (mosaic, logo on a clip frame, letters) and a clip card with frames", () =>
            {
                mw.ShowPage(MainWindow.PageClips);
                mw.TestLibraryCards();
                layout(root);
            });
            var stats = new StatsResult { TrackedSince = DateTime.Today.AddMonths(-2) };
            for (int i = 0; i < 12; i++)
                stats.Clips.Add(new StatClip
                {
                    Name = "c" + i, Game = i % 3 == 0 ? NoGame.Folder : "Hunt Showdown", Recorded = DateTime.Now.AddDays(-i * 4), Duration = 60 + i,
                    Size = 1048576, Present = i % 2 == 0, Cut = i % 4 == 1,
                });
            run("statistics with the month recap", () =>
            {
                mw.ShowPage(MainWindow.PageStats);
                mw.TestStats(stats);
                layout(root);
            });
            string recapPng = Path.Combine(Path.GetTempPath(), "clipkeeper-recap-" + Guid.NewGuid().ToString("N") + ".png");
            run("month recap picture", () =>
            {
                string made = mw.TestRecapPicture(stats, recapPng);
                if (made == null || new FileInfo(made).Length < 1000) throw new Exception("no picture");
            });
            try { File.Delete(recapPng); } catch { }

            var tabs = mw.SettingsTabKeys.ToList();
            check(string.Join(",", tabs) == "general,about,obs,obs-app,replay,checks,alarm,clip-saved,hotkeys,sorting,folders,covers,cleanup,encoder,editor-keys",
                  "settings: all sections with every feature on (" + string.Join(",", tabs) + ")");
            foreach (var tab in tabs)
                run("settings section " + tab, () =>
                {
                    mw.ShowSettingsTab(tab);
                    layout(root);
                    if (mw.ShownSettingsTab != tab) throw new Exception("shown: " + mw.ShownSettingsTab);
                });
            int[] hit = null, none = null, back = null;
            run("settings search", () =>
            {
                hit = mw.SearchSettingsForTest("replay");
                layout(root);
                none = mw.SearchSettingsForTest("qqqzzzxx");
                back = mw.SearchSettingsForTest("");
            });
            if (hit != null)
            {
                check(hit[0] >= 3 && hit[1] >= 2, "settings search: \"replay\" finds rows in several sections (" + hit[0] + " rows, " + hit[1] + " sections)");
                check(none[0] == 0 && none[1] == 0, "settings search: nonsense finds nothing");
                check(back[1] == 1, "settings search: clearing it shows one section again");
            }
            run("a problem link opens its setting", () =>
            {
                mw.ShowSettingsRow(MainWindow.TabChecks, MainWindow.RowDisk);
                layout(root);
                if (mw.ShownSettingsTab != MainWindow.TabChecks) throw new Exception("shown: " + mw.ShownSettingsTab);
                // the row decides the section, the given one is only for a row that is not there
                mw.ShowSettingsRow(MainWindow.TabGeneral, MainWindow.RowReplayBuffer);
                layout(root);
                if (mw.ShownSettingsTab != MainWindow.TabReplay) throw new Exception("shown: " + mw.ShownSettingsTab);
            });
            run("settings: the sidebar turns into its sections, back returns to the page before", () =>
            {
                mw.ShowPage(MainWindow.PageStats);
                mw.ShowPage(MainWindow.PageSettings);
                layout(root);
                if (((System.Windows.FrameworkElement)root.FindName("MainSide")).Visibility == System.Windows.Visibility.Visible) throw new Exception("the page list is still shown");
                mw.LeaveSettings();
                layout(root);
                if (mw.CurrentPage != MainWindow.PageStats) throw new Exception("back to page " + mw.CurrentPage);
                if (((System.Windows.FrameworkElement)root.FindName("MainSide")).Visibility != System.Windows.Visibility.Visible) throw new Exception("the page list is not back");
            });
            for (int i = 0; i < 5; i++)
            {
                int step = i;
                run("setup step " + (step + 1), () => { mw.PreviewSetup(step); layout(root); });
            }
            mw.PreviewSetup(-1);

            // a feature turned off takes its sections away
            var noGuard = fresh();
            noGuard.GuardEnabled = false;
            run("settings without the recording guard", () =>
            {
                var keys = new MainWindow(null, noGuard).SettingsTabKeys.ToList();
                if (keys.Contains(MainWindow.TabChecks) || keys.Contains(MainWindow.TabAlarm) || keys.Contains(MainWindow.TabReplay) || keys.Contains(MainWindow.TabClipSaved)) throw new Exception(string.Join(",", keys));
            });
            var noLibrary = fresh();
            noLibrary.LibraryEnabled = false;
            run("settings without the library", () =>
            {
                var keys = new MainWindow(null, noLibrary).SettingsTabKeys.ToList();
                if (keys.Contains(MainWindow.TabFolders) || keys.Contains(MainWindow.TabHotkeys) || keys.Contains(MainWindow.TabEncoder) || !keys.Contains(MainWindow.TabSorting)) throw new Exception(string.Join(",", keys));
            });

            Action<System.Windows.FrameworkElement> fit = el =>
            {
                el.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
                el.Arrange(new System.Windows.Rect(el.DesiredSize));
                el.UpdateLayout();
            };
            run("alarm", () => fit(new AlertWindow(null, AlertKind.Alarm, 0).PreviewContent("Alarm", "• one\n• two", true)));
            run("toast", () => fit(new AlertWindow(null, AlertKind.Toast, 4).PreviewContent("✓ Saved", null, false)));
            run("clip card", () => fit(ClipCard.PreviewContent(new ClipInfo { Path = @"C:\none\Replay.mp4", Game = "Game", Duration = 30, Tracks = 2, Bytes = 1048576 })));
            run("clip card asking to delete", () => fit(ClipCard.PreviewContent(new ClipInfo { Path = @"C:\none\Replay.mp4", Game = "Game", Duration = 30, Tracks = 2, Bytes = 1048576 }, true)));
            run("clip card with a problem", () => fit(ClipCard.PreviewContent(new ClipInfo { Path = @"C:\none\Replay.mp4", Duration = 5, Issues = new List<string> { "short" } })));
            run("tray menu", () => fit(new TrayMenu(null).PreviewContent(Preview.Fake(refs, 2))));
            run("\"Saving the clip…\" card", () => fit(ClipCard.PreviewSaving()));
            run("\"Saving the clip…\" turns into the clip card", () => fit(ClipCard.PreviewBecome(new ClipInfo { Path = @"C:\none\Replay.mp4", Game = "Game", Duration = 30, Tracks = 2, Bytes = 1048576 })));

            // the editor: one window goes through the clips of a list (no clip is really read — no ffmpeg needed)
            string tdir = Path.Combine(Path.GetTempPath(), "clipkeeper-editor-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(tdir);
                var paths = new[] { "c.mp4", "b.mp4", "a.mp4" }.Select((n, i) =>
                {
                    string fp = Path.Combine(tdir, n);
                    File.WriteAllText(fp, "x");
                    File.SetLastWriteTime(fp, DateTime.Now.AddMinutes(-i));   // c newest
                    return fp;
                }).ToList();
                TrimWindow tw = null;
                string first = null, second = null;
                run("editor: builds for a clip", () => { tw = new TrimWindow(null, fresh(), paths[0], 3); layout((System.Windows.FrameworkElement)tw.W.Content); first = tw.NavText; });
                if (tw != null)
                {
                    run("editor: another clip comes into the same window", () => { tw.Open(paths[1], null); Preview.Pump(); second = tw.NavText; });
                    check(first == "1 / 3 c.mp4" && second == "2 / 3 b.mp4", "editor: the folder is the list, newest first (" + first + " → " + second + ")");
                    tw.W.Close();
                }
            }
            catch (Exception ex) { check(false, "editor list: " + ex.Message); }
            finally { try { Directory.Delete(tdir, true); } catch { } }
        }

        // screen check diagnostics: pixel stats of a source snapshot (the picture itself is not saved)
        public static int Shot(string outPath)
        {
            var cfg = Settings.Load(Path.Combine(Program.Dir, "settings.json"));
            string err;
            bool authFail;
            var c = ObsClient.Connect(cfg.Host, cfg.Port, cfg.GetPassword(), out err, out authFail);
            var sb = new StringBuilder();
            if (c == null) { File.WriteAllText(outPath, "no connection: " + err, Encoding.UTF8); return 1; }
            using (c)
            {
                string dummy;
                var refs = RefStore.Load(Path.Combine(Program.Dir, "devices.json"), new string[0], out dummy);
                foreach (var kv in refs.Items.Where(x => Matcher.IsMonitor(x.Value.Type)))
                    foreach (var fmt in new[] { "png", "jpg", "bmp" })
                    {
                        try
                        {
                            var r = c.Request("GetSourceScreenshot", Json.D("sourceName", kv.Key, "imageFormat", fmt,
                                              "imageWidth", 64, "imageHeight", 36), 5000);
                            string data = Json.GetStr(r, "imageData") ?? "";
                            var bytes = Convert.FromBase64String(data.Substring(data.IndexOf(',') + 1));
                            using (var ms = new MemoryStream(bytes))
                            using (var bmp = new System.Drawing.Bitmap(ms))
                            {
                                int minA = 255, maxA = 0, minR = 255, maxR = 0, minG = 255, maxG = 0, minB = 255, maxB = 0;
                                for (int y = 0; y < bmp.Height; y++)
                                    for (int x = 0; x < bmp.Width; x++)
                                    {
                                        var p = bmp.GetPixel(x, y);
                                        minA = Math.Min(minA, p.A); maxA = Math.Max(maxA, p.A);
                                        minR = Math.Min(minR, p.R); maxR = Math.Max(maxR, p.R);
                                        minG = Math.Min(minG, p.G); maxG = Math.Max(maxG, p.G);
                                        minB = Math.Min(minB, p.B); maxB = Math.Max(maxB, p.B);
                                    }
                                sb.AppendLine(kv.Key + " [" + fmt + "] " + bmp.Width + "x" + bmp.Height + " " + bmp.PixelFormat +
                                              "  A " + minA + ".." + maxA + "  R " + minR + ".." + maxR + "  G " + minG + ".." + maxG +
                                              "  B " + minB + ".." + maxB + "  → " + (Pixels.UniformKind(data) ?? "fine") +
                                              ";  monitor: " + (Pixels.ScreenKind(kv.Value.LastName ?? kv.Value.Display) ?? "has a picture"));
                            }
                        }
                        catch (Exception ex) { sb.AppendLine(kv.Key + " [" + fmt + "] error: " + ex.Message); }
                    }
            }
            File.WriteAllText(outPath, sb.ToString(), Encoding.UTF8);
            return 0;
        }

        // --mergetest <clip1> <clip2> <folder> <report>: two clips joined with a fade (kept quality), then back to back for Discord
        public static int MergeTest(string[] clips, string outDir, string report)
        {
            var sb = new StringBuilder();
            Directory.CreateDirectory(outDir);
            int fails = 0;
            foreach (var m in new[]
            {
                new MergeJob { Clips = clips.ToList(), Fade = true, Mode = TrimMode.Precise, MixIndex = 3, OutputDir = outDir, Title = "Merge test fade" },
                new MergeJob { Clips = clips.ToList(), Ranges = new List<double[]> { new[] { 10.0, 20.0 }, new[] { 5.0, 15.5 } }, Fade = true, Mode = TrimMode.Precise, MixIndex = 3,
                               OutputDir = outDir, Title = "Merge test parts" },
                new MergeJob { Clips = clips.ToList(), Fade = false, Mode = TrimMode.Share, Target = ShareTarget.Discord, MixIndex = 3, OutputDir = outDir, Title = "Merge test discord" },
            })
            {
                sb.AppendLine("== merge, " + (m.Fade ? "fade" : "back to back") + ", " + m.Mode + (m.Mode == TrimMode.Share ? " " + m.Target : ""));
                var sw = Stopwatch.StartNew();
                bool ok = Trimmer.Merge(m, p => { }, st => sb.AppendLine("   [" + "…✓✗i"[st.State] + "] " + st.Text), CancellationToken.None);
                sb.AppendLine("   result: " + (ok ? "VERIFIED" : "FAILED") + " in " + sw.Elapsed.TotalSeconds.ToString("0.0") + " s");
                sb.AppendLine();
                if (!ok) fails++;
            }
            sb.AppendLine(fails == 0 ? "RESULT: ALL PASSED" : "RESULT: " + fails + " FAILED");
            File.WriteAllText(report, sb.ToString(), Encoding.UTF8);
            return fails == 0 ? 0 : 1;
        }

        public static int TrimTest(string src, string outDir, string report)
        {
            var sb = new StringBuilder();
            Directory.CreateDirectory(outDir);
            var info = Ffmpeg.Info(src);
            sb.AppendLine("source: " + Path.GetFileName(src) + " · " + Trimmer.Dur(info.Duration) + " · video " + info.Video +
                          " · audio " + info.Audio.Count + " (" + string.Join(", ", info.Audio.Select((a, i) => Trimmer.TrackName(a, i))) + ")");
            double mid = info.Duration / 2;
            var cases = new[]
            {
                new TrimJob { Source = src, OutputDir = outDir, In = mid - 15, Out = mid + 15, Mode = TrimMode.Lossless },
                new TrimJob { Source = src, OutputDir = outDir, In = mid - 5, Out = mid + 5, Mode = TrimMode.Precise },
                new TrimJob { Source = src, OutputDir = outDir, In = mid - 10, Out = mid + 10, Mode = TrimMode.Share, Target = ShareTarget.Discord, ShareAudio = info.Audio.Count - 1 },
                new TrimJob { Source = src, OutputDir = outDir, In = mid - 10, Out = mid + 10, Mode = TrimMode.Share, Target = ShareTarget.Discord, ShareAudio = info.Audio.Count - 1,
                              Loudness = true, Title = "Loudness test" },
                new TrimJob { Source = src, OutputDir = outDir, In = mid - 3, Out = mid + 3, Mode = TrimMode.Share, Target = ShareTarget.Gif, Title = "Gif test" },
                new TrimJob { Source = src, OutputDir = outDir, In = mid - 3, Out = mid + 3, Mode = TrimMode.Share, Target = ShareTarget.Gif, GifFormat = "webp", Title = "Webp test" },
            };
            int fails = 0;
            foreach (var j in cases)
            {
                j.SourceInfo = info;
                sb.AppendLine();
                sb.AppendLine("== " + j.Mode + (j.Mode == TrimMode.Share ? " " + j.Target : "") + ": " + Trimmer.Dur(j.In) + " → " + Trimmer.Dur(j.Out));
                var sw = Stopwatch.StartNew();
                bool ok = Trimmer.Run(j, p => { }, st => sb.AppendLine("   [" + "…✓✗i"[st.State] + "] " + st.Text), CancellationToken.None);
                sb.AppendLine("   result: " + (ok ? "VERIFIED" : "FAILED") + " in " + sw.Elapsed.TotalSeconds.ToString("0.0") + " s");
                if (!ok) fails++;
            }

            // volume, a disabled track, title and metadata → then the "Sort by game" plan
            if (info.Audio.Count >= 2)
            {
                var j = new TrimJob
                {
                    Source = src, OutputDir = outDir, In = mid - 10, Out = mid + 10, Mode = TrimMode.Lossless, SourceInfo = info,
                    Title = "Test: double kill", Meta = Trimmer.MetaOf(src, info, @"D:\Sources"),
                    Tracks = info.Audio.Select((a, i) => new TrackPlan { Source = i, On = i != 1, Gain = i == 0 ? -6 : 0 }).ToList(),
                };
                sb.AppendLine();
                sb.AppendLine("== Volume −6 dB on track 1, track 2 off, title and metadata (game: " + j.Meta.Game + ", recorded " + j.Meta.Recorded + ")");
                bool ok = Trimmer.Run(j, p => { }, st => sb.AppendLine("   [" + "…✓✗i"[st.State] + "] " + st.Text), CancellationToken.None);
                sb.AppendLine("   result: " + (ok ? "VERIFIED" : "FAILED"));
                if (!ok) fails++;
                List<string> unknown;
                var plan = Trimmer.SortPlan(outDir, @"D:\Clips", out unknown);
                sb.AppendLine("   sort plan (nothing moved): " + string.Join("; ", plan.Select(m => Path.GetFileName(m.From) + " → " + Path.GetDirectoryName(m.To))));
                sb.AppendLine("   no game data: " + string.Join(", ", unknown));
                if (!plan.Any(m => Path.GetFileName(m.From).StartsWith("Test"))) fails++;
            }

            // mix rebuild: Discord −20 dB, mic removed; the mix is rebuilt; plus preview audio and the "share" variant
            if (info.Audio.Count >= 4)
            {
                int mixI = info.Audio.Count - 1;
                Func<TrimMode, TrimJob> mk = mode => new TrimJob
                {
                    Source = src, OutputDir = outDir, In = mid - 10, Out = mid + 10, Mode = mode, Target = ShareTarget.Discord, SourceInfo = info,
                    Title = "Mix test " + mode, Meta = Trimmer.MetaOf(src, info, @"D:\Sources"), MixIndex = mixI, RebuildMix = true,
                    Tracks = info.Audio.Select((a, i) => new TrackPlan { Source = i, On = i != 2, Gain = i == 0 ? -20 : 0 }).ToList(),
                };
                foreach (var mode in new[] { TrimMode.Lossless, TrimMode.Share })
                {
                    var j = mk(mode);
                    sb.AppendLine();
                    sb.AppendLine("== Mix rebuild (" + mode + "): track 1 −20 dB, track 3 removed");
                    bool ok = Trimmer.Run(j, p => { }, st => sb.AppendLine("   [" + "…✓✗i"[st.State] + "] " + st.Text), CancellationToken.None);
                    sb.AppendLine("   result: " + (ok ? "VERIFIED" : "FAILED"));
                    if (!ok) fails++;
                }
                var sw2 = Stopwatch.StartNew();
                string wav = Path.Combine(outDir, "preview.wav");
                bool pa = Trimmer.PreviewAudio(mk(TrimMode.Lossless), new int[0], wav, CancellationToken.None);
                sb.AppendLine("   preview audio (whole clip): " + (pa ? new FileInfo(wav).Length / 1048576 + " MB in " + sw2.Elapsed.TotalSeconds.ToString("0.0") + " s" : "NOT BUILT"));
                if (!pa) fails++;
                bool solo = Trimmer.PreviewAudio(mk(TrimMode.Lossless), new[] { 0 }, Path.Combine(outDir, "solo.wav"), CancellationToken.None);
                sb.AppendLine("   track 1 solo: " + (solo ? "ok" : "NOT BUILT"));
                if (!solo) fails++;
            }

            // cuts: two pieces from the middle, all three modes; length = sum of kept pieces, audio intact
            foreach (var mode in new[] { TrimMode.Lossless, TrimMode.Precise, TrimMode.Share })
            {
                var j = new TrimJob
                {
                    Source = src, OutputDir = outDir, In = mid - 15, Out = mid + 15, Mode = mode, Target = ShareTarget.Discord, SourceInfo = info,
                    Title = "Cuts test " + mode, Meta = Trimmer.MetaOf(src, info, @"D:\Sources"), MixIndex = info.Audio.Count - 1,
                    Cuts = new List<double[]> { new[] { mid - 8, mid - 5 }, new[] { mid + 2, mid + 6.5 } },
                };
                sb.AppendLine();
                sb.AppendLine("== Cuts (" + mode + "): 30 s, cut 3 + 4.5 s → expecting " + Trimmer.Dur(j.Length));
                var sw = Stopwatch.StartNew();
                bool ok = Trimmer.Run(j, p => { }, st => sb.AppendLine("   [" + "…✓✗i"[st.State] + "] " + st.Text), CancellationToken.None);
                double got = ok ? Ffmpeg.Info(j.Output).Duration : -1;
                bool lenOk = Math.Abs(got - j.Length) < (mode == TrimMode.Lossless ? 0.35 : 0.25);
                sb.AppendLine("   file length " + Trimmer.Dur(got) + " · result: " + (ok && lenOk ? "VERIFIED" : "FAILED") +
                              " in " + sw.Elapsed.TotalSeconds.ToString("0.0") + " s");
                if (!ok || !lenOk) fails++;
            }

            // simulate "the trim lost audio": a copy with one track silenced
            if (info.Audio.Count > 0)
            {
                var j = new TrimJob { Source = src, OutputDir = outDir, In = mid - 15, Out = mid + 15, Mode = TrimMode.Lossless, SourceInfo = info };
                int loud = -1;
                for (int i = 0; i < info.Audio.Count && loud < 0; i++)
                    if (Ffmpeg.PeakDb(src, i, j.In, j.Out, CancellationToken.None) > -60) loud = i;
                if (loud >= 0)
                {
                    j.Output = Path.Combine(outDir, "broken.mp4");
                    Ffmpeg.Run("-v error -ss " + Ffmpeg.T(j.In) + " -to " + Ffmpeg.T(j.Out) + " -i \"" + src + "\" -map 0 -c copy -c:a:" + loud +
                               " aac -filter:a:" + loud + " volume=0 \"" + j.Output + "\"", CancellationToken.None);
                    sb.AppendLine();
                    sb.AppendLine("== DELIBERATELY BROKEN file: silenced " + Trimmer.TrackName(info.Audio[loud], loud));
                    bool ok = Trimmer.Verify(j, new FileInfo(j.Output).Length, st => sb.AppendLine("   [" + "…✓✗i"[st.State] + "] " + st.Text), CancellationToken.None);
                    sb.AppendLine("   result: " + (ok ? "THE CHECK MISSED THE DAMAGE — BAD" : "damage caught — good"));
                    if (ok) fails++;
                }
            }
            File.WriteAllText(report, sb.ToString(), Encoding.UTF8);
            return fails;
        }

        public static int Probe(string outPath)
        {
            var cfg = Settings.Load(Path.Combine(Program.Dir, "settings.json"));
            string err;
            bool authFail;
            // the password is deliberately not sent: we only check that the server is alive and asks for auth
            var c = ObsClient.Connect(cfg.Host, cfg.Port, "", out err, out authFail);
            string res = c != null ? "connected without a password (OBS auth is off)"
                       : authFail ? "OBS WebSocket answers and asks for a password (" + err + ")"
                       : "OBS WebSocket is unreachable: " + err;
            if (c != null) c.Dispose();
            File.WriteAllText(outPath, cfg.Host + ":" + cfg.Port + " — " + res + Environment.NewLine, Encoding.UTF8);
            return c != null || authFail ? 0 : 1;
        }
    }
}
