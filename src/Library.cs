using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace DeviceGuard
{
    // a game card in the library (also "Favorites" and "All clips")
    class GameCardVm : Vm
    {
        Brush cover;
        public string Key;            // game (folder) name or a service key: "*fav", "*all"
        public string Name, Stats, Tip, Glyph;
        public int Count;
        public Brush GlyphBrush { get; set; }
        public bool Special { get { return Key.StartsWith("*"); } }
        public string NameText { get { return Name; } }
        public string StatsText { get { return Stats; } }
        public string CountText { get { return Count.ToString(); } }
        public string InitialsText { get { return InitialsOf(Name); } }
        public string GlyphText { get { return Glyph; } }
        public Visibility GlyphVis { get { return Glyph != null ? Visibility.Visible : Visibility.Collapsed; } }
        // placeholder letters — only while there is no cover (otherwise a transparent logo lands on top of them)
        public Visibility InitialsVis { get { return Glyph == null && cover == null && coverMask == null ? Visibility.Visible : Visibility.Collapsed; } }
        public Visibility CoverBtnVis { get { return Special ? Visibility.Collapsed : Visibility.Visible; } }
        public Brush Cover { get { return cover; } set { Set(ref cover, value); Notify("InitialsVis"); } }
        // a dark logo on a transparent background is drawn white through this mask
        Brush coverMask;
        public Brush CoverMask { get { return coverMask; } set { Set(ref coverMask, value); Notify("CoverMaskVis", "InitialsVis"); } }
        public Visibility CoverMaskVis { get { return coverMask != null ? Visibility.Visible : Visibility.Collapsed; } }

        // the newest clips (for the mosaic and the backdrop)
        public List<string> Recent = new List<string>();
        // a blurred frame of the latest clip under a logo, a wide picture or the letters
        Brush backdrop;
        public Brush Backdrop { get { return backdrop; } set { Set(ref backdrop, value); Notify("BackdropVis", "InitialsBrush"); } }
        public Visibility BackdropVis { get { return backdrop != null && Glyph == null ? Visibility.Visible : Visibility.Collapsed; } }
        static readonly Brush InitialsDim = Wpf.Br("#3A3C44"), InitialsLight = Wpf.Br("#E4E4E7");
        public Brush InitialsBrush { get { return backdrop != null ? InitialsLight : InitialsDim; } }
        // Favorites, All clips and folders without a game: frames of the latest clips fanned out over the whole card,
        // the newest in front, on its own blurred copy
        public const int FanSize = 6;
        Brush[] mosaic;
        public Brush[] Mosaic
        {
            get { return mosaic; }
            set
            {
                Set(ref mosaic, value);
                Notify("MosaicVis", "NoMosaicVis", "Frame0", "Frame1", "Frame2", "Frame3", "Frame4", "Frame5",
                       "Frame0Vis", "Frame1Vis", "Frame2Vis", "Frame3Vis", "Frame4Vis", "Frame5Vis",
                       "Top0", "Top1", "Top2", "Top3", "Top4", "Top5");
            }
        }
        public Visibility MosaicVis { get { return mosaic != null && mosaic.Length > 0 ? Visibility.Visible : Visibility.Collapsed; } }
        // no frames yet (or none could be read): the icon alone, large, in the middle
        public Visibility NoMosaicVis { get { return MosaicVis == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible; } }
        Brush FrameAt(int i) { return mosaic != null && mosaic.Length > i ? mosaic[i] : null; }
        Visibility FrameVis(int i) { return FrameAt(i) != null ? Visibility.Visible : Visibility.Collapsed; }
        public Brush Frame0 { get { return FrameAt(0); } }
        public Brush Frame1 { get { return FrameAt(1); } }
        public Brush Frame2 { get { return FrameAt(2); } }
        public Brush Frame3 { get { return FrameAt(3); } }
        public Brush Frame4 { get { return FrameAt(4); } }
        public Brush Frame5 { get { return FrameAt(5); } }
        public Visibility Frame0Vis { get { return FrameVis(0); } }
        public Visibility Frame1Vis { get { return FrameVis(1); } }
        public Visibility Frame2Vis { get { return FrameVis(2); } }
        public Visibility Frame3Vis { get { return FrameVis(3); } }
        public Visibility Frame4Vis { get { return FrameVis(4); } }
        public Visibility Frame5Vis { get { return FrameVis(5); } }
        // where each frame of the fan stands: however many there are, they spread over the card and stay in its middle
        // (the card is 246 high, a frame 72; the count and the icon take the bottom)
        double FanTop(int i)
        {
            int n = mosaic != null ? Math.Max(1, mosaic.Length) : 1;
            double gap = n > 1 ? Math.Min(40, (196.0 - 72) / (n - 1)) : 0;
            double start = 6 + (196 - ((n - 1) * gap + 72)) / 2;
            return start + (n - 1 - i) * gap;
        }
        public double Top0 { get { return FanTop(0); } }
        public double Top1 { get { return FanTop(1); } }
        public double Top2 { get { return FanTop(2); } }
        public double Top3 { get { return FanTop(3); } }
        public double Top4 { get { return FanTop(4); } }
        public double Top5 { get { return FanTop(5); } }
        // a wide picture lies on its own blurred copy, not on a clip frame
        public bool OwnBackdrop;

        public static string InitialsOf(string name)
        {
            var words = Regex.Split(name ?? "", @"[^\p{L}\p{N}]+").Where(w => w.Length > 0).ToList();
            if (words.Count == 0) return "?";
            return (words.Count == 1 ? words[0].Substring(0, Math.Min(2, words[0].Length)) : words[0].Substring(0, 1) + words[1].Substring(0, 1)).ToUpperInvariant();
        }
    }

    // Game pictures. Cover (2:3): custom → cache → Steam → Wikipedia.
    // Banner above a game's clips: custom → cache → Steam (library_hero) → a frame from the latest clip.
    static class Covers
    {
        static string Dir { get { return Path.Combine(Program.Dir, "covers"); } }
        static readonly ConcurrentDictionary<string, ImageSource> mem = new ConcurrentDictionary<string, ImageSource>();
        static readonly ConcurrentDictionary<string, object> locks = new ConcurrentDictionary<string, object>();
        // folders sorting tools create for non-games — no pictures are looked up
        static readonly HashSet<string> NotGames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Desktop", "SearchHost", "explorer", NoGame.Game, NoGame.Folder, "Windows", "chrome", "firefox", "zen", "msedge",
            "Discord", "Telegram", "AyuGram", "obs64", "Spotify", "DeviceGuard", "ClipKeeper", "Adobe Premiere Pro", "Base Profile",
        };

        public static bool IsGame(string name) { return !NotGames.Contains(name ?? ""); }

        // false — pictures come only from your own files and clip frames, nothing is downloaded (settings: online covers)
        public static bool Online = true;

        static string Safe(string name) { return Trimmer.SafeName(name).ToLowerInvariant(); }
        static string P(string name, string suffix) { return Path.Combine(Dir, Safe(name) + suffix); }
        static string Custom(string name, string suffix) { return Path.Combine(Dir, "custom", Safe(name) + suffix); }

        // not found before — don't ask the internet more than once a week. A marker holds the version of the search that
        // missed: when the search gets better, older misses are tried again at once
        const string SearchVer = "2";
        static bool RecentlyMissed(string marker)
        {
            try { return File.Exists(marker) && (DateTime.Now - File.GetLastWriteTime(marker)).TotalDays < 7 && File.ReadAllText(marker).Trim().EndsWith("v" + SearchVer); }
            catch { return false; }
        }
        static void Missed(string marker) { Missed(marker, ""); }
        static void Missed(string marker, string prefix) { try { Directory.CreateDirectory(Dir); File.WriteAllText(marker, prefix + "v" + SearchVer); } catch { } }

        static ImageSource Load(string path, int width)
        {
            try
            {
                var b = new BitmapImage();
                b.BeginInit();
                b.CacheOption = BitmapCacheOption.OnLoad;
                b.DecodePixelWidth = width;
                b.UriSource = new Uri(path);
                b.EndInit();
                b.Freeze();
                return b;
            }
            catch { return null; }
        }

        // ── cover ──
        // the callback runs on the UI thread (or not at all if there is no cover)
        public static void Get(string game, Action<ImageSource> done)
        {
            ImageSource img;
            if (mem.TryGetValue(game, out img)) { done(img); return; }
            var disp = Dispatcher.CurrentDispatcher;
            Task.Factory.StartNew(() => Fetch(game)).ContinueWith(t =>
            {
                if (t.IsFaulted || t.Result == null) return;
                disp.BeginInvoke(new Action(() => done(t.Result)));
            });
        }

        // synchronously (background thread or preview)
        public static ImageSource Fetch(string game)
        {
            ImageSource img;
            if (mem.TryGetValue(game, out img)) return img;
            lock (locks.GetOrAdd(game + "|c", k => new object()))
            {
                if (mem.TryGetValue(game, out img)) return img;
                if (File.Exists(Custom(game, ".img"))) img = Load(Custom(game, ".img"), 400);
                else if (File.Exists(P(game, ".jpg"))) img = Load(P(game, ".jpg"), 400);
                else if (Online && IsGame(game) && !RecentlyMissed(P(game, ".none")))
                {
                    rateLimited = false;
                    if (Download(game, P(game, ".jpg"), "library_capsule_2x", "library_capsule", "header") || DownloadWiki(game)) img = Load(P(game, ".jpg"), 400);
                    else if (!rateLimited) Missed(P(game, ".none"));   // hit the rate limit — try next time, not in a week
                }
                if (img != null) mem[game] = img;
                return img;
            }
        }

        // ── banner ──
        public class Hero { public ImageSource Image; public bool FromClip; }

        public static Hero FetchHero(string game, string newestClip)
        {
            string k = game + "|hero";
            ImageSource img;
            lock (locks.GetOrAdd(k, x => new object()))
            {
                if (mem.TryGetValue(k, out img)) return new Hero { Image = img };
                if (File.Exists(Custom(game, ".hero.img"))) img = Load(Custom(game, ".hero.img"), 1920);
                else if (File.Exists(P(game, ".hero.jpg"))) img = Load(P(game, ".hero.jpg"), 1920);
                else if (Online && IsGame(game) && !RecentlyMissed(P(game, ".hero.none")))
                {
                    if (Download(game, P(game, ".hero.jpg"), "library_hero_2x", "library_hero")) img = Load(P(game, ".hero.jpg"), 1920);
                    else Missed(P(game, ".hero.none"));
                }
                if (img != null) { mem[k] = img; return new Hero { Image = img }; }
            }
            // no art — a frame from this game's latest clip
            if (newestClip == null || !Ffmpeg.Available) return null;
            string frame = P(game, ".frame.jpg");
            try
            {
                if (!File.Exists(frame))
                {
                    var info = Mp4.Read(newestClip);
                    double at = info != null && info.Duration > 0 ? info.Duration * 0.4 : 5;
                    Directory.CreateDirectory(Dir);
                    Ffmpeg.Run("-v error -ss " + Ffmpeg.T(at) + " -i \"" + newestClip + "\" -frames:v 1 -vf scale=1920:-2 -q:v 3 \"" + frame + "\"", CancellationToken.None);
                }
                img = File.Exists(frame) ? Load(frame, 1920) : null;
            }
            catch (Exception ex) { Log.Write("banner \"" + game + "\": " + ex.Message); }
            return img != null ? new Hero { Image = img, FromClip = true } : null;
        }

        public static void SetCustom(string game, string imagePath, bool hero)
        {
            Directory.CreateDirectory(Path.Combine(Dir, "custom"));
            File.Copy(imagePath, Custom(game, hero ? ".hero.img" : ".img"), true);
            ImageSource x;
            mem.TryRemove(hero ? game + "|hero" : game, out x);
        }

        // ── search ──
        static string Norm(string s) { return Regex.Replace((s ?? "").ToLowerInvariant(), @"[^\p{L}\p{N}]", ""); }

        // a folder named after the exe → a human name: "RimWorldWin64" → "RimWorld", "ShiftAtMidnight" → "Shift At Midnight"
        // the name to show: a readable game name, or "No game" / "No folder"
        public static string Title(string game)
        {
            return game == NoGame.Folder || game == NoGame.Game ? NoGame.Text(game) : SearchTerm(game);
        }

        public static string SearchTerm(string game)
        {
            string s = Regex.Replace(game, @"[-_ ]?(Win64|Win32|x64|Shipping)+$", "", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"(?<=\p{Ll})(?=\p{Lu})", " ");
            return Regex.Replace(s, @"\s+", " ").Trim();
        }

        // WebClient drops the User-Agent after the first request and Wikipedia answers 403 without it — set it on every request
        class UaClient : WebClient
        {
            protected override WebRequest GetWebRequest(Uri address)
            {
                var r = base.GetWebRequest(address) as HttpWebRequest;
                if (r != null)
                {
                    r.UserAgent = "ClipKeeper/1.0 (personal clip library)";
                    r.Timeout = 15000;
                }
                return r;
            }
        }

        static WebClient Client()
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            return new UaClient { Encoding = Encoding.UTF8 };
        }

        // what is looked up besides the name itself: "X Demo", "X Playtest", "X VR" → "X" — the full game has the pictures
        // (Roblox VR → Roblox on Wikipedia, Burglin' Gnomes Demo → Burglin' Gnomes on Steam)
        static readonly Regex Edition = new Regex(@"\s+(demo|playtest|prologue|vr|beta|early access)$", RegexOptions.IgnoreCase);

        public static string BaseTerm(string game)
        {
            string t = SearchTerm(game), b = Edition.Replace(t, "").TrimEnd(' ', '-', ':');
            return b.Length >= 3 ? b : t;
        }

        // a name as Steam has it, without "The" in front: "Headliners" ↔ "The Headliners"
        static string NormName(string s) { string n = Norm(s); return n.StartsWith("the") && n.Length > 6 ? n.Substring(3) : n; }

        // Steam appid (remembered; "0" — the game is not on Steam)
        static int FindApp(WebClient wc, string game)
        {
            string file = P(game, ".appid");
            int id;
            if (File.Exists(file) && int.TryParse(File.ReadAllText(file).Trim(), out id) && id > 0) return id;
            if (RecentlyMissed(file)) return 0;
            string term = SearchTerm(game), baseTerm = BaseTerm(game);
            // the full game first: a demo or a playtest has its own appid, mostly without library pictures
            id = SearchSteam(wc, baseTerm, baseTerm, false);
            if (id == 0 && baseTerm != term) id = SearchSteam(wc, term, term, false);
            // one word glued together from an .exe name ("Drivebeyondhorizons"): Steam finds it by the start of the name
            if (!term.Contains(' ') && term.Length >= 9)
                foreach (int k in new[] { 9, 6 })
                    if (id == 0) id = SearchSteam(wc, term.Substring(0, k), term, true);
            try { Directory.CreateDirectory(Dir); if (id > 0) File.WriteAllText(file, id.ToString()); else Missed(file, "0;"); } catch { }
            return id;
        }

        // the first result that is this game; a query cut from a glued name must match that whole name exactly
        static int SearchSteam(WebClient wc, string query, string term, bool exact)
        {
            string want = NormName(term);
            string json = wc.DownloadString("https://store.steampowered.com/api/storesearch/?l=english&cc=US&term=" + Uri.EscapeDataString(query));
            foreach (var o in Json.GetArr(Json.Obj(Json.Parse(json)), "items"))
            {
                var it = Json.Obj(o);
                string raw = Json.GetStr(it, "name") ?? "", n = NormName(raw);
                if (n == want) return Json.GetInt(it, "id", 0);
                if (exact) continue;
                // "Hunt Showdown" ↔ "Hunt: Showdown 1896", "PUBG" ↔ "PUBG: BATTLEGROUNDS" — the start matches,
                // but not on a random short word ("Desktop" must not become "Desktop Mate")
                bool prefix = n.StartsWith(want) && (want.Length >= 5 || raw.StartsWith(term + ":", StringComparison.OrdinalIgnoreCase));
                if (prefix || (n.Length >= 5 && want.StartsWith(n))) return Json.GetInt(it, "id", 0);
            }
            return 0;
        }

        // Steam pictures: new games have hashed URLs from IStoreBrowseService/GetItems; old ones have a direct path
        static List<string> SteamUrls(WebClient wc, int appid, string[] keys)
        {
            var urls = new List<string>();
            try
            {
                string input = "{\"ids\":[{\"appid\":" + appid + "}],\"context\":{\"language\":\"english\",\"country_code\":\"US\"},\"data_request\":{\"include_assets\":true}}";
                var resp = Json.GetObj(Json.Obj(Json.Parse(wc.DownloadString(
                    "https://api.steampowered.com/IStoreBrowseService/GetItems/v1/?input_json=" + Uri.EscapeDataString(input)))), "response");
                var assets = Json.GetObj(Json.Obj(Json.GetArr(resp, "store_items").FirstOrDefault()), "assets");
                string fmt = Json.GetStr(assets, "asset_url_format");
                if (fmt != null)
                    foreach (var key in keys)
                    {
                        string file = Json.GetStr(assets, key);
                        if (file != null) urls.Add("https://shared.akamai.steamstatic.com/store_item_assets/" + fmt.Replace("${FILENAME}", file));
                    }
            }
            catch (Exception ex) { Log.Write("Steam pictures " + appid + ": " + ex.Message); }
            foreach (var key in keys)
            {
                string old = key.StartsWith("library_capsule") ? "library_600x900.jpg" : key.StartsWith("library_hero") ? "library_hero.jpg" : "header.jpg";
                string u = "https://cdn.cloudflare.steamstatic.com/steam/apps/" + appid + "/" + old;
                if (!urls.Contains(u)) urls.Add(u);
            }
            return urls;
        }

        static bool Save(WebClient wc, string url, string to, string game, string source)
        {
            try
            {
                var bytes = Retry(() => wc.DownloadData(url));
                if (bytes.Length < 1000) return false;
                Directory.CreateDirectory(Dir);
                File.WriteAllBytes(to, bytes);
                Log.Write("picture \"" + game + "\": " + source + " (" + url.Substring(url.LastIndexOf('/') + 1).Split('?')[0] + ")");
                return true;
            }
            catch (Exception ex)
            {
                // Steam URL variants are tried in turn, 404 is normal there; log the rest
                if (!source.StartsWith("Steam")) Log.Write("picture \"" + game + "\": " + source + ": " + ex.Message);
                return false;
            }
        }

        // Wikipedia asks for at most one request a second and answers 429 when rushed — go one at a time, pause, and retry
        static readonly object wikiGate = new object();
        static DateTime wikiLast;
        [ThreadStatic] static bool rateLimited;

        static T Retry<T>(Func<T> call)
        {
            for (int i = 0; ; i++)
            {
                lock (wikiGate)
                {
                    double wait = 1.2 - (DateTime.Now - wikiLast).TotalSeconds;
                    if (wait > 0) Thread.Sleep(TimeSpan.FromSeconds(wait));
                    try { return call(); }
                    catch (WebException ex)
                    {
                        var r = ex.Response as HttpWebResponse;
                        bool limit = r != null && (int)r.StatusCode == 429;
                        if (limit) rateLimited = true;
                        if (i >= 2 || !limit) throw;
                    }
                    finally { wikiLast = DateTime.Now; }
                }
                Thread.Sleep(3000 * (i + 1));
            }
        }

        static bool Download(string game, string to, params string[] keys)
        {
            try
            {
                using (var wc = Client())
                {
                    int appid = FindApp(wc, game);
                    if (appid > 0)
                        foreach (var url in SteamUrls(wc, appid, keys))
                            if (Save(wc, url, to, game, "Steam " + appid)) return true;
                }
            }
            catch (Exception ex) { Log.Write("picture \"" + game + "\": Steam " + ex.Message); }
            return false;
        }

        // non-Steam games (Valorant, Genshin): a picture from the Wikipedia article — only if the article is about the game
        static bool DownloadWiki(string game)
        {
            try
            {
                using (var wc = Client())
                {
                    string term = BaseTerm(game), want = Norm(term);
                    var titles = new List<string> { term };   // a direct address: Wikipedia redirects by itself
                    var found = Json.GetObj(Json.Obj(Json.Parse(Retry(() => wc.DownloadString(
                        "https://en.wikipedia.org/w/api.php?action=query&list=search&format=json&srlimit=5&srsearch=" + Uri.EscapeDataString(term + " video game"))))), "query");
                    foreach (var o in Json.GetArr(found, "search"))
                    {
                        string t = Json.GetStr(Json.Obj(o), "title") ?? "";
                        if (Norm(Regex.Replace(t, @"\s*\(.*\)$", "")) == want && !titles.Contains(t)) titles.Add(t);
                    }
                    foreach (var t in titles)
                    {
                        Dictionary<string, object> sum;
                        try
                        {
                            string title = t;
                            sum = Json.Obj(Json.Parse(Retry(() => wc.DownloadString("https://en.wikipedia.org/api/rest_v1/page/summary/" +
                                                                                   Uri.EscapeDataString(title.Replace(' ', '_'))))));
                        }
                        catch (WebException ex)
                        {
                            var r = ex.Response as HttpWebResponse;
                            if (r == null || r.StatusCode != HttpStatusCode.NotFound) Log.Write("picture \"" + game + "\": Wikipedia \"" + t + "\": " + ex.Message);
                            continue;
                        }
                        string desc = (Json.GetStr(sum, "description") ?? "").ToLowerInvariant();
                        if (!desc.Contains("game")) continue;
                        string url = Json.GetStr(Json.GetObj(sum, "originalimage"), "source") ?? Json.GetStr(Json.GetObj(sum, "thumbnail"), "source");
                        if (url != null && Save(wc, url, P(game, ".jpg"), game, "Wikipedia \"" + Json.GetStr(sum, "title") + "\"")) return true;
                    }
                }
            }
            catch (Exception ex) { Log.Write("picture \"" + game + "\": Wikipedia " + ex.Message); }
            return false;
        }
    }

    // What is known about a clip: length (from the MP4 header) and ClipKeeper data (game, date, title — via ffprobe).
    // Everything is cached in clipcache.json until the file changes.
    static class ClipIndex
    {
        static string FilePath { get { return Path.Combine(Program.Dir, "clipcache.json"); } }
        static Dictionary<string, object> cache;
        static readonly object Sync = new object();
        static bool dirty;
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        static Dictionary<string, object> Entry(FileInfo f)
        {
            if (cache == null)
            {
                if (File.Exists(FilePath))
                    try { cache = Json.Obj(Json.Parse(File.ReadAllText(FilePath, Encoding.UTF8))); }
                    catch (Exception ex) { Log.Write("clipcache.json could not be read, clips are measured again: " + ex.Message); }
                if (cache == null) cache = new Dictionary<string, object>();
            }
            object v;
            string key = f.FullName.ToLowerInvariant();
            string stamp = f.Length + "|" + f.LastWriteTimeUtc.Ticks;
            var d = cache.TryGetValue(key, out v) ? Json.Obj(v) : null;
            if (d == null || Json.GetStr(d, "s") != stamp)
            {
                d = Json.D("s", stamp);
                cache[key] = d;
                dirty = true;
            }
            return d;
        }

        public static double Duration(FileInfo f)
        {
            lock (Sync)
            {
                var d = Entry(f);
                object v;
                if (d.TryGetValue("d", out v)) return Convert.ToDouble(v, Inv);
                double dur = 0;
                if (f.Extension.Equals(".mp4", StringComparison.OrdinalIgnoreCase) || f.Extension.Equals(".mov", StringComparison.OrdinalIgnoreCase))
                    try { var info = Mp4.Read(f.FullName); if (info != null && info.Duration > 0) dur = info.Duration; } catch { }
                d["d"] = dur;
                dirty = true;
                return dur;
            }
        }

        // ClipKeeper data from the file (null if none) and the title
        public static Tuple<ClipMeta, string> Meta(FileInfo f) { return Meta(f, false); }

        // strict: a file that can't be read throws instead of looking like "no data" (the cleanup relies on that)
        public static ClipMeta MetaStrict(FileInfo f) { return Meta(f, true).Item1; }

        static Tuple<ClipMeta, string> Meta(FileInfo f, bool strict)
        {
            lock (Sync)
            {
                var d = Entry(f);
                if (Json.GetInt(d, "m", 0) == 1)
                {
                    string g = Json.GetStr(d, "g"), r = Json.GetStr(d, "r");
                    DateTime when;
                    var m = g == null ? null : new ClipMeta
                    {
                        Game = g, Source = Json.GetStr(d, "src"),
                        Recorded = DateTime.TryParseExact(r ?? "", "yyyy-MM-ddTHH:mm:ss", Inv, DateTimeStyles.None, out when) ? when : f.LastWriteTime,
                    };
                    return Tuple.Create(m, Json.GetStr(d, "t"));
                }
            }
            // ffprobe runs outside the lock so others are not held up
            ClipMeta meta = null;
            string title = null;
            bool probed = false;
            if (Ffmpeg.Available)
                try
                {
                    var info = Ffmpeg.Info(f.FullName);
                    meta = Trimmer.ReadMeta(info);
                    info.Tags.TryGetValue("title", out title);
                    probed = true;
                }
                catch (Exception ex) { Log.Write("clip data of " + f.Name + ": " + ex.Message); }
            // remembered only when really read: "no data" without ffmpeg or after a failed probe would stick to the file
            // (the cleanup trusts a remembered "no data" to mean "not a trim")
            if (!probed)
            {
                if (strict) throw new IOException(L.T("can't read ", "не читается ") + f.Name);
                return Tuple.Create(meta, title);
            }
            lock (Sync)
            {
                var d = Entry(f);
                d["m"] = 1;
                d["g"] = meta != null ? meta.Game : null;
                d["src"] = meta != null ? meta.Source : null;
                d["r"] = meta != null ? meta.Recorded.ToString("yyyy-MM-ddTHH:mm:ss", Inv) : null;
                d["t"] = title;
                dirty = true;
            }
            return Tuple.Create(meta, title);
        }

        // the title if ffprobe has read this file before, null otherwise: a search does not probe hundreds of files
        // (a ready clip's file name is its title anyway)
        public static string CachedTitle(FileInfo f)
        {
            lock (Sync)
            {
                var d = Entry(f);
                return Json.GetInt(d, "m", 0) == 1 ? Json.GetStr(d, "t") : null;
            }
        }

        // whether ffprobe has read this file's data already (Meta then answers without running it)
        public static bool HasMeta(FileInfo f)
        {
            lock (Sync) return Json.GetInt(Entry(f), "m", 0) == 1;
        }

        // a renamed file keeps what is known about it (its length and time stay the same, so the entry stays valid)
        public static void Renamed(string from, string to)
        {
            lock (Sync)
            {
                object v;
                string k = from.ToLowerInvariant();
                if (cache == null || !cache.TryGetValue(k, out v)) return;
                cache.Remove(k);
                cache[to.ToLowerInvariant()] = v;
                dirty = true;
            }
        }

        public static void Save()
        {
            lock (Sync)
            {
                if (!dirty || cache == null) return;
                // don't keep records of files that are gone
                foreach (var k in cache.Keys.ToList()) if (!File.Exists(k)) cache.Remove(k);
                try { Json.WriteFile(FilePath, cache); dirty = false; } catch (Exception ex) { Log.Write("clipcache: " + ex.Message); }
            }
        }
    }

    // Favorites: by file name (it holds the date and time, so it is unique and survives moves)
    static class Favorites
    {
        static string FilePath { get { return Path.Combine(Program.Dir, "favorites.json"); } }
        static HashSet<string> set;
        static readonly object Sync = new object();

        static HashSet<string> Set()
        {
            if (set != null) return set;
            set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!File.Exists(FilePath)) return set;
            try { foreach (var o in (object[])Json.Parse(File.ReadAllText(FilePath, Encoding.UTF8))) set.Add(Convert.ToString(o)); }
            catch (Exception ex)
            {
                // keep the unreadable file aside: the next star would overwrite it with an almost empty list
                string aside = FilePath + ".bad";
                try { File.Copy(FilePath, aside, true); } catch { }
                Log.Write("favorites.json could not be read (" + ex.Message + "), a copy is kept as " + Path.GetFileName(aside));
            }
            return set;
        }

        public static bool Has(string path) { lock (Sync) return Set().Contains(Path.GetFileName(path)); }

        public static bool Toggle(string path)
        {
            lock (Sync)
            {
                string n = Path.GetFileName(path);
                bool now = !Set().Remove(n);
                if (now) set.Add(n);
                Save();
                return now;
            }
        }

        public static void Renamed(string from, string to)
        {
            lock (Sync) if (Set().Remove(Path.GetFileName(from))) { set.Add(Path.GetFileName(to)); Save(); }
        }

        static void Save()
        {
            try { Json.WriteFile(FilePath, set.OrderBy(x => x).ToList()); } catch (Exception ex) { Log.Write("favorites: " + ex.Message); }
        }
    }

    // Clips already looked at in "Go through new clips" (MainWindowTriage.cs): by file name, like favorites
    static class Reviewed
    {
        static string FilePath { get { return Path.Combine(Program.Dir, "reviewed.json"); } }
        static HashSet<string> set;
        static readonly object Sync = new object();

        static HashSet<string> Set()
        {
            if (set != null) return set;
            set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!File.Exists(FilePath)) return set;
            try { foreach (var o in (object[])Json.Parse(File.ReadAllText(FilePath, Encoding.UTF8))) set.Add(Convert.ToString(o)); }
            catch (Exception ex) { Log.Write("reviewed.json could not be read: " + ex.Message); }
            return set;
        }

        public static bool Has(string path) { lock (Sync) return Set().Contains(Path.GetFileName(path)); }

        public static void Mark(string path, bool on)
        {
            lock (Sync)
            {
                bool changed = on ? Set().Add(Path.GetFileName(path)) : Set().Remove(Path.GetFileName(path));
                if (changed) Save();
            }
        }

        public static void Renamed(string from, string to)
        {
            lock (Sync) if (Set().Remove(Path.GetFileName(from))) { set.Add(Path.GetFileName(to)); Save(); }
        }

        static void Save()
        {
            try { Json.WriteFile(FilePath, set.OrderBy(x => x).ToList()); } catch (Exception ex) { Log.Write("reviewed: " + ex.Message); }
        }
    }

    static class Fmt
    {
        public static string Duration(double sec)
        {
            var t = TimeSpan.FromSeconds(sec);
            if (t.TotalHours >= 1) return (int)t.TotalHours + L.T(" h ", " ч ") + t.Minutes + L.T(" min", " мин");
            if (t.TotalMinutes >= 1) return (int)t.TotalMinutes + L.T(" min", " мин");
            return (int)t.TotalSeconds + L.T(" s", " с");
        }

        public static string Size(long bytes)
        {
            double gb = bytes / 1073741824.0;
            return gb >= 1 ? gb.ToString("0.#") + L.T(" GB", " ГБ") : (bytes / 1048576.0).ToString("0") + L.T(" MB", " МБ");
        }
    }
}
