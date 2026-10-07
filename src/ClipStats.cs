using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace DeviceGuard
{
    // One recorded clip (a source) and what happened to it
    class StatClip
    {
        public string Name, Game;
        public DateTime Recorded;
        public double Duration;
        public long Size;
        public bool Present;   // is in the sources (or moved whole into Ready / the collection)
        public bool Cut;       // a trim was made from it in ClipKeeper
        public DateTime? GoneAt;
        public bool Gone { get { return !Present && !Cut; } }   // deleted without a trim
    }

    // A ready clip (a trim): when it was made, which game, where it is
    class StatReady
    {
        public DateTime Made;
        public string Game;
        public bool InCollection;
    }

    class StatsResult
    {
        public List<StatClip> Clips = new List<StatClip>();
        public List<StatReady> Ready = new List<StatReady>();
        public DateTime TrackedSince;   // from this day deleted clips are visible too
    }

    // Clip history for the statistics. Sources:
    //  · ClipKeeper logs — a line for every saved clip (name, length, size), even if it was deleted later;
    //  · the sources folder — what is there now;
    //  · Ready and the collection — ClipKeeper trims remember which source they came from.
    // Everything accumulates in clipstats.json so the history survives log cleanup.
    static class ClipStats
    {
        static string FilePath { get { return Path.Combine(Program.Dir, "clipstats.json"); } }
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        static readonly Regex LogClip = new Regex(@"^(\d{4}-\d\d-\d\d \d\d:\d\d:\d\d)  (?:clip|клип) (.+?\.(?:mp4|mkv|mov|flv)): (\d+) (?:s|с)(?:.*?(\d+) (?:MB|МБ))?", RegexOptions.Compiled);   // English since the English UI, Russian before
        static readonly Regex NameDate = new Regex(@"(\d{4})-(\d{2})-(\d{2})[ _](\d{2})-(\d{2})-(\d{2})", RegexOptions.Compiled);
        static readonly Regex NameGame = new Regex(@"^(.+?) - Replay", RegexOptions.Compiled);
        static readonly object Sync = new object();

        public static StatsResult Compute(string obsRoot, string readyRoot, string collRoot, Action<string> progress)
        {
            // without the sources folder "deleted" cannot be told from "not visible" — count nothing, mark nothing
            if (string.IsNullOrEmpty(obsRoot) || !Directory.Exists(obsRoot))
                throw new InvalidOperationException(L.T("the sources folder is unknown", "папка исходников неизвестна"));
            lock (Sync)
            {
                var hist = Load();
                var now = DateTime.Now;
                DateTime trackedSince = DateTime.MaxValue;

                // 1. logs: every saved clip
                progress(L.T("reading logs…", "читаю журналы…"));
                foreach (var log in new[] { "DeviceGuard.log", "ClipKeeper.log" }.Select(n => Path.Combine(Program.Dir, n)).Where(File.Exists))
                    foreach (var line in ReadLines(log))
                    {
                        var m = LogClip.Match(line);
                        if (!m.Success) continue;
                        DateTime at;
                        if (!DateTime.TryParseExact(m.Groups[1].Value, "yyyy-MM-dd HH:mm:ss", Inv, DateTimeStyles.None, out at)) continue;
                        if (at < trackedSince) trackedSince = at;
                        string name = m.Groups[2].Value;
                        var c = Get(hist, name);
                        if (c.Recorded == DateTime.MinValue) c.Recorded = DateOf(name, at);
                        if (c.Game == null) c.Game = GameOf(name);
                        if (c.Duration <= 0) c.Duration = double.Parse(m.Groups[3].Value, Inv);
                        if (c.Size <= 0 && m.Groups[4].Success) c.Size = long.Parse(m.Groups[4].Value, Inv) * 1048576;
                    }

                // 2. what is in the sources now
                progress(L.T("scanning sources…", "смотрю исходники…"));
                var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var f in Without(ClipScanner.Scan(obsRoot, int.MaxValue), readyRoot, collRoot))
                    {
                        present.Add(f.Name);
                        var c = Get(hist, f.Name);
                        c.Game = GameIn(f, obsRoot);
                        c.Recorded = DateOf(f.Name, f.LastWriteTime);
                        c.Duration = ClipIndex.Duration(f);
                        c.Size = f.Length;
                    }

                // 3. ready clips and the collection: which sources were trimmed, which were moved whole
                var cutFrom = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var result = new StatsResult();
                foreach (var root in new[] { readyRoot, collRoot })
                {
                    if (root == null) continue;
                    bool coll = root == collRoot;
                    progress(coll ? L.T("reading the collection…", "читаю коллекцию…") : L.T("reading ready clips…", "читаю готовые клипы…"));
                    foreach (var f in ClipScanner.Scan(root, int.MaxValue))
                    {
                        // only ClipKeeper writes source data — files older than the logs don't have it, so skip ffprobe
                        ClipMeta meta = null;
                        if (f.LastWriteTime >= trackedSince.AddDays(-1)) meta = ClipIndex.Meta(f).Item1;
                        if (meta != null && !string.IsNullOrEmpty(meta.Source)) cutFrom.Add(meta.Source);
                        if (hist.ContainsKey(f.Name.ToLowerInvariant())) present.Add(f.Name);   // the source was moved whole
                        string game = meta != null ? meta.Game : ClipScanner.GameOf(f, root);
                        if (game == NoGame.Folder) game = NoGame.Game;
                        result.Ready.Add(new StatReady { Made = f.LastWriteTime, Game = game, InCollection = coll });
                    }
                }
                ClipIndex.Save();

                // 4. the outcome for each clip
                foreach (var c in hist.Values)
                {
                    c.Present = present.Contains(c.Name);
                    c.Cut = cutFrom.Contains(c.Name);
                    if (c.Present || c.Cut) c.GoneAt = null;
                    else if (c.GoneAt == null) c.GoneAt = now;
                    if (c.Game == null) c.Game = NoGame.Game;
                }
                Save(hist);
                result.Clips = hist.Values.Where(c => c.Recorded != DateTime.MinValue).ToList();
                result.TrackedSince = trackedSince == DateTime.MaxValue ? now : trackedSince;
                return result;
            }
        }

        // names of the sources a trim was made from (ClipKeeper writes the source into every trim) — the "not trimmed" filter
        public static HashSet<string> TrimmedSources(string readyRoot, string collRoot)
        {
            var cut = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var root in new[] { readyRoot, collRoot })
                if (root != null)
                    foreach (var f in ClipScanner.Scan(root, int.MaxValue))
                    {
                        var m = ClipIndex.Meta(f).Item1;
                        if (m != null && !string.IsNullOrEmpty(m.Source)) cut.Add(m.Source);
                    }
            ClipIndex.Save();
            return cut;
        }

        // the log may be open for writing by the program — read with shared access
        static IEnumerable<string> ReadLines(string path)
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var r = new StreamReader(fs, Encoding.UTF8))
            {
                string line;
                while ((line = r.ReadLine()) != null) yield return line;
            }
        }

        static IEnumerable<FileInfo> Without(IEnumerable<FileInfo> files, params string[] roots)
        {
            var skip = roots.Where(r => r != null).Select(r => r.TrimEnd('\\') + "\\").ToList();
            return files.Where(f => !skip.Any(r => f.FullName.StartsWith(r, StringComparison.OrdinalIgnoreCase)));
        }

        static string GameOf(string name)
        {
            var m = NameGame.Match(Path.GetFileNameWithoutExtension(name));
            return m.Success ? m.Groups[1].Value : null;
        }

        static string GameIn(FileInfo f, string root)
        {
            string g = ClipScanner.GameOf(f, root);
            return g != NoGame.Folder ? g : GameOf(f.Name) ?? NoGame.Game;
        }

        static DateTime DateOf(string name, DateTime fallback)
        {
            var m = NameDate.Match(name);
            DateTime d;
            return m.Success && DateTime.TryParseExact(m.Value.Replace('_', ' '), "yyyy-MM-dd HH-mm-ss", Inv, DateTimeStyles.None, out d) ? d : fallback;
        }

        static StatClip Get(Dictionary<string, StatClip> hist, string name)
        {
            StatClip c;
            if (!hist.TryGetValue(name.ToLowerInvariant(), out c)) hist[name.ToLowerInvariant()] = c = new StatClip { Name = name };
            return c;
        }

        static Dictionary<string, StatClip> Load()
        {
            var hist = new Dictionary<string, StatClip>();
            try
            {
                var d = Json.Obj(Json.Parse(File.ReadAllText(FilePath, Encoding.UTF8)));
                foreach (var o in Json.GetArr(d, "clips"))
                {
                    var x = Json.Obj(o);
                    var c = new StatClip
                    {
                        Name = Json.GetStr(x, "name"), Game = Json.GetStr(x, "game"),
                        Duration = ToD(Json.GetStr(x, "dur")), Size = (long)ToD(Json.GetStr(x, "size")),
                    };
                    DateTime t;
                    if (DateTime.TryParseExact(Json.GetStr(x, "rec") ?? "", "yyyy-MM-ddTHH:mm:ss", Inv, DateTimeStyles.None, out t)) c.Recorded = t;
                    if (DateTime.TryParseExact(Json.GetStr(x, "gone") ?? "", "yyyy-MM-ddTHH:mm:ss", Inv, DateTimeStyles.None, out t)) c.GoneAt = t;
                    if (c.Name != null) hist[c.Name.ToLowerInvariant()] = c;
                }
            }
            catch { }
            return hist;
        }

        static double ToD(string s) { double v; return double.TryParse(s ?? "", NumberStyles.Float, Inv, out v) ? v : 0; }

        static void Save(Dictionary<string, StatClip> hist)
        {
            try
            {
                var list = hist.Values.OrderBy(c => c.Recorded).Select(c => Json.D(
                    "name", c.Name, "game", c.Game, "rec", c.Recorded.ToString("yyyy-MM-ddTHH:mm:ss", Inv),
                    "dur", c.Duration.ToString("0.#", Inv), "size", c.Size.ToString(Inv),
                    "gone", c.GoneAt.HasValue ? c.GoneAt.Value.ToString("yyyy-MM-ddTHH:mm:ss", Inv) : null)).ToList();
                Json.WriteFile(FilePath, Json.D("clips", list));
            }
            catch (Exception ex) { Log.Write("clipstats: " + ex.Message); }
        }
    }
}
