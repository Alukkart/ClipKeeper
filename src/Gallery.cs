using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace DeviceGuard
{
    // Markers for clips without a game. They are also stored in clip metadata and clipstats.json,
    // so the values stay as older versions wrote them; Text() gives the name to show.
    static class NoGame
    {
        public const string Folder = "Без папки", Game = "Без игры";

        public static bool Is(string g) { return g == null || g == Folder || g == Game; }

        public static string Text(string g)
        {
            return g == Folder ? L.T("No folder", "Без папки") : g == Game || g == null ? L.T("No game", "Без игры") : g;
        }
    }

    // Finds clips in the OBS recording folder (including game subfolders)
    static class ClipScanner
    {
        static readonly string[] Exts = { ".mp4", ".mkv", ".mov", ".flv" };

        public static List<FileInfo> Scan(string root, int max)
        {
            var files = new List<FileInfo>();
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return files;
            var stack = new Stack<DirectoryInfo>();
            stack.Push(new DirectoryInfo(root));
            while (stack.Count > 0)
            {
                var d = stack.Pop();
                try
                {
                    foreach (var f in d.EnumerateFiles())
                        if (Exts.Contains(f.Extension.ToLowerInvariant())) files.Add(f);
                    foreach (var s in d.EnumerateDirectories()) stack.Push(s);
                }
                catch { }   // no access to the folder — skip it
            }
            return files.OrderByDescending(f => f.LastWriteTime).Take(max).ToList();
        }

        // sorting puts clips into "Game\YYYY-MM\" — the first folder is the game.
        // In the OBS folder this is a setting (someone may sort clips by date); in Ready and the collection
        // (OwnRoots) the folders are made by ClipKeeper and are always games.
        public static bool SubfoldersAreGames = true;
        public static string[] OwnRoots = new string[0];

        public static string GameOf(FileInfo f, string root)
        {
            if (!SubfoldersAreGames && !OwnRoots.Any(r => string.Equals(r.TrimEnd('\\'), root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)))
                return NoGame.Folder;
            string rel = f.FullName.Length > root.Length ? f.FullName.Substring(root.Length).TrimStart('\\') : f.Name;
            var parts = rel.Split('\\');
            return parts.Length > 1 ? parts[0] : NoGame.Folder;
        }

        public static ClipVm Describe(FileInfo f, string root, string lastClip)
        {
            string game = GameOf(f, root);
            string own = ClipNames.TitleOf(f.Name);   // named in the library
            string title = own ?? (game == NoGame.Folder ? Path.GetFileNameWithoutExtension(f.Name) : game);
            string name = Path.GetFileNameWithoutExtension(f.Name);
            if (own == null && name.Contains(" — ")) title += " · " + name.Substring(name.LastIndexOf(" — ") + 3);   // trim / discord / …

            string duration = null;
            double sec = ClipIndex.Duration(f);
            if (sec > 0)
            {
                var t = TimeSpan.FromSeconds(Math.Round(sec));
                duration = t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
            }

            var when = f.LastWriteTime;
            string date = when.Date == DateTime.Today ? L.T("today", "сегодня") : when.Date == DateTime.Today.AddDays(-1) ? L.T("yesterday", "вчера")
                        : when.ToString(when.Year == DateTime.Now.Year ? "d MMM" : "d MMM yyyy", L.Culture);
            double mb = f.Length / 1048576.0;
            string size = mb >= 1024 ? (mb / 1024).ToString("0.0") + L.T(" GB", " ГБ") : mb.ToString("0") + L.T(" MB", " МБ");

            string at = date + ", " + when.ToString("HH:mm");
            return new ClipVm
            {
                Path = f.FullName,
                Title = title,
                // a named clip moved its game out of the title: it goes below, unless the clips are inside that game already
                Sub = (own != null && game != NoGame.Folder ? Covers.Title(game) + " · " : "") + at + " · " + size,
                Named = own != null, Source = true, WhenText = at, SizeText = size,
                Duration = duration,
                Seconds = sec,
                NewVis = string.Equals(f.FullName, lastClip, StringComparison.OrdinalIgnoreCase) ? Visibility.Visible : Visibility.Collapsed,
            };
        }
    }

    // Video thumbnails via Windows (like in Explorer). A separate STA thread so the window does not hang.
    static class Thumbs
    {
        static readonly BlockingCollection<Tuple<string, Action<ImageSource>>> queue =
            new BlockingCollection<Tuple<string, Action<ImageSource>>>();
        static readonly ConcurrentDictionary<string, ImageSource> cache = new ConcurrentDictionary<string, ImageSource>();
        static Thread worker;

        public static void Request(string path, Action<ImageSource> done)
        {
            ImageSource img;
            if (cache.TryGetValue(path, out img)) { done(img); return; }
            if (worker == null)
            {
                worker = new Thread(Run) { IsBackground = true, Name = "thumbs" };
                worker.SetApartmentState(ApartmentState.STA);
                worker.Start();
            }
            var disp = Dispatcher.CurrentDispatcher;
            queue.Add(Tuple.Create<string, Action<ImageSource>>(path, i => disp.BeginInvoke(new Action(() => done(i)))));
        }

        static void Run()
        {
            foreach (var job in queue.GetConsumingEnumerable())
            {
                ImageSource img = null;
                try { img = Load(job.Item1, 320, 180); } catch { }
                if (img != null) cache[job.Item1] = img;
                job.Item2(img);
            }
        }

        public static ImageSource Load(string path, int w, int h)
        {
            var iid = typeof(IShellItemImageFactory).GUID;
            IShellItemImageFactory f;
            if (SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out f) != 0 || f == null) return null;
            IntPtr hbmp = IntPtr.Zero;
            try
            {
                // 0x08 THUMBNAILONLY: only a frame from the video, no file icon
                if (f.GetImage(new SIZE { cx = w, cy = h }, 0x08, out hbmp) != 0 || hbmp == IntPtr.Zero) return null;
                var src = Imaging.CreateBitmapSourceFromHBitmap(hbmp, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                src.Freeze();
                return src;
            }
            finally
            {
                if (hbmp != IntPtr.Zero) DeleteObject(hbmp);
                Marshal.ReleaseComObject(f);
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        struct SIZE { public int cx, cy; }

        [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IShellItemImageFactory
        {
            [PreserveSig] int GetImage(SIZE size, int flags, out IntPtr phbm);
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        static extern int SHCreateItemFromParsingName(string path, IntPtr pbc, ref Guid riid,
                                                      [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory ppv);

        [DllImport("gdi32.dll")]
        static extern bool DeleteObject(IntPtr h);
    }

    // Frame strips: Count frames of a clip side by side in one picture, made by ffmpeg. The library scrolls through them
    // when the mouse moves over a card, the editor shows them under its timeline — one file serves both.
    // They are kept in "previews" next to the settings; the oldest go when there are too many.
    static class Filmstrips
    {
        public const int Count = 14, Height = 144;
        const int Keep = 600;
        static readonly object sync = new object();
        static string Dir { get { return Path.Combine(Program.Dir, "previews"); } }

        // the name follows the file's path, size and time: a changed or replaced clip gets a new strip
        static string FileFor(string clip)
        {
            var f = new FileInfo(clip);
            string key = f.FullName.ToLowerInvariant() + "|" + f.Length + "|" + f.LastWriteTimeUtc.Ticks;
            using (var md5 = MD5.Create())
                return Path.Combine(Dir, BitConverter.ToString(md5.ComputeHash(Encoding.UTF8.GetBytes(key))).Replace("-", "").ToLowerInvariant() + ".png");
        }

        public static bool Has(string clip)
        {
            try { return File.Exists(FileFor(clip)); } catch { return false; }
        }

        // ready or made now (seconds); null — no ffmpeg, no video or an error. duration ≤ 0 — found out here
        public static BitmapSource Get(string clip, double duration, CancellationToken cancel)
        {
            if (!Ffmpeg.Available || !File.Exists(clip)) return null;
            string png = FileFor(clip);
            lock (sync)   // one ffmpeg at a time; a second request for the same clip waits and takes the ready file
            {
                if (!File.Exists(png))
                {
                    if (duration <= 0) duration = ClipIndex.Duration(new FileInfo(clip));
                    if (duration <= 0) duration = Ffmpeg.Info(clip).Duration;
                    Directory.CreateDirectory(Dir);
                    string tmp = png + ".part.png";
                    if (!Ffmpeg.Filmstrip(clip, duration, Count, Height, tmp, cancel)) { TryDelete(tmp); return null; }
                    File.Move(tmp, png);
                    Prune();
                }
            }
            return Load(png);
        }

        // one brush per frame: a window into the strip
        public static Brush[] Frames(BitmapSource strip)
        {
            var r = new Brush[Count];
            for (int i = 0; i < Count; i++)
            {
                var b = new ImageBrush(strip)
                {
                    Viewbox = new Rect((double)i / Count, 0, 1.0 / Count, 1), ViewboxUnits = BrushMappingMode.RelativeToBoundingBox,
                    Stretch = Stretch.UniformToFill,
                };
                b.Freeze();
                r[i] = b;
            }
            return r;
        }

        static BitmapSource Load(string png)
        {
            try
            {
                using (var fs = File.OpenRead(png))
                {
                    var b = new BitmapImage();
                    b.BeginInit();
                    b.CacheOption = BitmapCacheOption.OnLoad;
                    b.StreamSource = fs;
                    b.EndInit();
                    b.Freeze();
                    return b;
                }
            }
            catch { return null; }
        }

        static void Prune()
        {
            try
            {
                var files = new DirectoryInfo(Dir).GetFiles("*.png");
                if (files.Length <= Keep) return;
                foreach (var f in files.OrderBy(f => f.LastWriteTime).Take(files.Length - Keep + 100)) TryDelete(f.FullName);
            }
            catch { }
        }

        static void TryDelete(string path)
        {
            try { File.Delete(path); } catch { }
        }
    }
}
