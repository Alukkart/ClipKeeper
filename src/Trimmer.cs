using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;

namespace DeviceGuard
{
    enum TrimMode { Lossless, Precise, Share }
    enum ShareTarget { Discord, Nitro, Telegram, Custom, Gif }

    // what to do with one audio track of the source
    class TrackPlan
    {
        public int Source;          // audio stream number in the source
        public bool On = true;      // false — don't keep the track
        public double Gain;         // dB, 0 — unchanged (copied as is)
        public string Title;
    }

    // where the clip comes from: game and recording time — written into the result's metadata
    class ClipMeta
    {
        public string Game, Source;
        public DateTime Recorded;
        public int Mix = -1;   // which audio track of a trim is the common mix (-1 — not written: trims before the join)
    }

    class TrimJob
    {
        public string Source, Output, OutputDir, Title;
        public double In, Out;
        public TrimMode Mode;
        public ShareTarget Target;
        public int CustomMb = 25;              // the "custom" target size
        // the size limit for sharing, MB (Telegram — 2 GB, its upload limit is 2000 MiB)
        public double LimitMb
        {
            get { return Target == ShareTarget.Discord ? 10 : Target == ShareTarget.Nitro ? 500 : Target == ShareTarget.Custom ? Math.Max(1, CustomMb) : 2000; }
        }
        public int ShareAudio;                 // for the "share" mode when Tracks are not set
        public List<TrackPlan> Tracks;         // null — all tracks as they are
        public int MixIndex = -1;              // which track is the common mix (-1 — none)
        public bool RebuildMix;                // rebuild the mix from the enabled tracks with their volume
        public ClipMeta Meta;
        public Ffmpeg.MediaInfo SourceInfo;
        public List<double[]> Cuts;            // cut pieces [start, end] inside In..Out (null — no cuts)
        public bool Loudness;                  // sharing: even loudness, -14 LUFS (two passes of loudnorm)
        public string Norm;                    // the second loudnorm pass, measured right before encoding
        public double LoudIn = double.NaN;     // what the first pass measured, LUFS
        public string GifFormat = "gif";       // the GIF target: gif or webp, the width and the frame rate
        public int GifWidth = 480, GifFps = 15;
        public bool Gif { get { return Mode == TrimMode.Share && Target == ShareTarget.Gif; } }

        // what remains after the cuts — pieces in order
        public List<double[]> Kept { get { return Trimmer.KeptSegments(In, Out, Cuts); } }
        public bool HasCuts { get { var k = Kept; return k.Count != 1 || k[0][0] > In + 1e-3 || k[0][1] < Out - 1e-3; } }
        public double Length { get { return Kept.Sum(k => k[1] - k[0]); } }
    }

    // A check step for the window: 0 — running, 1 — ok, 2 — error, 3 — for information
    class TrimStep
    {
        public string Text;
        public int State;
    }

    // Trimming plus a mandatory check of the result
    static partial class Trimmer
    {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        const double SilentDb = -60;        // quieter counts as silence
        const double ShareMaxKbps = 16000;  // higher makes no sense for 1080p60 H.264
        const string MetaTag = "DeviceGuard";

        // ── metadata ──
        public static ClipMeta MetaOf(string source, Ffmpeg.MediaInfo info, string clipsRoot)
        {
            // an already trimmed clip — take what was written last time
            var saved = ReadMeta(info);
            if (saved != null) return saved;

            var m = new ClipMeta { Source = Path.GetFileName(source) };
            string name = Path.GetFileNameWithoutExtension(source);
            var t = Regex.Match(name, @"(\d{4})-(\d{2})-(\d{2})[ _](\d{2})-(\d{2})-(\d{2})");
            DateTime when;
            m.Recorded = t.Success && DateTime.TryParseExact(t.Value.Replace('_', ' '), "yyyy-MM-dd HH-mm-ss", Inv, DateTimeStyles.None, out when)
                ? when : File.GetLastWriteTime(source);

            // the game is the sorting folder ("Game\YYYY-MM\…"), otherwise the file name prefix ("HuntGame - Replay …")
            if (!string.IsNullOrEmpty(clipsRoot) && source.StartsWith(clipsRoot.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
            {
                string g = ClipScanner.GameOf(new FileInfo(source), clipsRoot.TrimEnd('\\'));
                if (g != NoGame.Folder) m.Game = g;
            }
            if (m.Game == null)
            {
                var p = Regex.Match(name, @"^(.+?) - Replay");
                if (p.Success) m.Game = p.Groups[1].Value;
            }
            if (m.Game == null) m.Game = NoGame.Game;
            return m;
        }

        public static ClipMeta ReadMeta(Ffmpeg.MediaInfo info)
        {
            string c = null;
            if (info == null) return null;
            foreach (var key in new[] { "description", "comment" })   // comment — the format of the first versions
            {
                string v;
                if (info.Tags.TryGetValue(key, out v) && v != null && v.StartsWith(MetaTag + "|")) { c = v; break; }
            }
            if (c == null) return null;
            var m = new ClipMeta();
            foreach (var part in c.Split('|').Skip(1))
            {
                int eq = part.IndexOf('=');
                if (eq < 0) continue;
                string k = part.Substring(0, eq), v = part.Substring(eq + 1);
                if (k == "game") m.Game = v;
                else if (k == "source") m.Source = v;
                else if (k == "mix") { int x; if (int.TryParse(v, out x)) m.Mix = x; }
                else if (k == "recorded")
                {
                    DateTime d;
                    if (DateTime.TryParseExact(v, "yyyy-MM-ddTHH:mm:ss", Inv, DateTimeStyles.None, out d)) m.Recorded = d;
                }
            }
            return m.Game != null ? m : null;
        }

        static string Clean(string s) { return (s ?? "").Replace("\"", "”").Replace("|", "/").Trim(); }

        static string MetaArgs(TrimJob j)
        {
            var m = j.Meta;
            if (m == null) return "";
            // where the common mix ended up among the kept tracks: a join takes exactly that track
            var plan = Plan(j);
            int mix = j.MixIndex >= 0 ? plan.FindIndex(t => t.Source == j.MixIndex) : -1;
            if (mix < 0 && plan.Count == 1) mix = 0;
            string data = MetaTag + "|game=" + Clean(m.Game) + "|recorded=" + m.Recorded.ToString("yyyy-MM-ddTHH:mm:ss", Inv) +
                          "|source=" + Clean(m.Source) + (mix >= 0 ? "|mix=" + mix : "");
            string human = Clean(NoGame.Text(m.Game)) + L.T(" · clip from ", " · клип от ") + m.Recorded.ToString("dd.MM.yyyy HH:mm", Inv);
            return " -metadata title=\"" + Clean(j.Title) + "\" -metadata genre=\"" + Clean(m.Game) + "\"" +
                   " -metadata comment=\"" + human + "\" -metadata description=\"" + data + "\"" +
                   " -metadata date=" + m.Recorded.ToString("yyyy-MM-dd", Inv) +
                   " -metadata creation_time=" + m.Recorded.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss", Inv) + "Z";
        }

        public static string DefaultTitle(ClipMeta m)
        {
            return m == null ? L.T("Clip", "Клип") : NoGame.Text(m.Game) + " " + m.Recorded.ToString("dd.MM HH-mm", Inv);
        }

        // ── file name ──
        static string TrimSuffix { get { return L.T(" — trim", " — обрезка"); } }
        public static string SafeName(string s)
        {
            foreach (var ch in Path.GetInvalidFileNameChars()) s = s.Replace(ch, ' ');
            s = Regex.Replace(s, @"\s+", " ").Trim().TrimEnd('.');
            return s.Length > 120 ? s.Substring(0, 120).Trim() : s;
        }

        public static string OutputPath(TrimJob j)
        {
            string dir = !string.IsNullOrEmpty(j.OutputDir) ? j.OutputDir : Path.GetDirectoryName(j.Source);
            string name = SafeName(string.IsNullOrWhiteSpace(j.Title) ? Path.GetFileNameWithoutExtension(j.Source) + TrimSuffix : j.Title);
            if (name.Length == 0) name = L.T("Clip", "Клип");
            string ext = j.Gif ? (j.GifFormat == "webp" ? ".webp" : ".gif") : ".mp4";
            if (j.Mode == TrimMode.Share && !j.Gif)
                name += j.Target == ShareTarget.Discord ? " (discord)" : j.Target == ShareTarget.Nitro ? " (nitro)"
                      : j.Target == ShareTarget.Custom ? " (" + j.CustomMb + " MB)" : " (telegram)";
            string path = Path.Combine(dir, name + ext);
            for (int i = 2; File.Exists(path); i++) path = Path.Combine(dir, name + " " + i + ext);
            return path;
        }

        // ── tracks ──
        static List<TrackPlan> Plan(TrimJob j)
        {
            if (j.Gif) return new List<TrackPlan>();   // an animation has no sound
            var audio = j.SourceInfo.Audio;
            var plan = j.Tracks ?? Enumerable.Range(0, audio.Count).Select(i => new TrackPlan { Source = i, Title = audio[i].Title }).ToList();
            if (j.Mode == TrimMode.Share)
            {
                // one track — the final audio; for the check compare with the mix (or the first enabled one)
                var one = j.Tracks == null ? plan.FirstOrDefault(t => t.Source == j.ShareAudio)
                        : plan.FirstOrDefault(t => t.Source == j.MixIndex && t.On) ?? plan.FirstOrDefault(t => t.On);
                return one != null ? new List<TrackPlan> { one } : new List<TrackPlan>();
            }
            return plan.Where(t => t.On).ToList();
        }

        static string Gain(double db) { return db.ToString("0.0#", Inv) + "dB"; }

        // amix of the enabled tracks (except the mix itself) with their volume → [label]
        public static string MixGraph(IEnumerable<TrackPlan> sources, double mixGain, string label)
        {
            var src = sources.ToList();
            if (src.Count == 0) return null;
            string g = "";
            for (int i = 0; i < src.Count; i++) g += "[0:a:" + src[i].Source + "]volume=" + Gain(src[i].Gain) + "[s" + i + "];";
            for (int i = 0; i < src.Count; i++) g += "[s" + i + "]";
            return g + "amix=inputs=" + src.Count + ":normalize=0:dropout_transition=0,volume=" + Gain(mixGain) + "[" + label + "]";
        }

        static bool Rebuilt(TrimJob j, TrackPlan t) { return j.RebuildMix && j.MixIndex >= 0 && t.Source == j.MixIndex; }

        static string RebuildGraph(TrimJob j, TrackPlan mix)
        {
            var all = j.Tracks ?? new List<TrackPlan>();
            return MixGraph(all.Where(t => t.On && t.Source != j.MixIndex), mix.Gain, "mix");
        }

        static string AudioArgs(TrimJob j, List<TrackPlan> on)
        {
            string a = "";
            if (j.Mode == TrimMode.Share)
            {
                if (j.Gif) return " -an";
                string share = ShareGraph(j, on);
                if (share == null) return " -an";
                if (j.Norm == null) return " -filter_complex \"" + share + "\" -map \"[out]\" -c:a aac -b:a 128k -ac 2";
                // even loudness: the second loudnorm pass with what the first one measured; it works at 192 kHz — back to 48
                return " -filter_complex \"" + share + ";[out]" + j.Norm + ",aresample=48000[outn]\" -map \"[outn]\" -c:a aac -b:a 128k -ac 2";
            }
            var mix = on.FirstOrDefault(t => Rebuilt(j, t));
            string graph = mix != null ? RebuildGraph(j, mix) : null;
            if (graph == null) mix = null;
            if (graph != null) a += " -filter_complex \"" + graph + "\"";
            for (int k = 0; k < on.Count; k++) a += on[k] == mix ? " -map \"[mix]\"" : " -map 0:a:" + on[k].Source;

            for (int k = 0; k < on.Count; k++)
            {
                // without a volume change the track is copied byte for byte; a rebuilt mix is always encoded
                if (on[k] == mix) a += " -c:a:" + k + " aac -b:a:" + k + " 256k";
                else if (on[k].Gain == 0) a += " -c:a:" + k + " copy";
                else a += " -c:a:" + k + " aac -b:a:" + k + " 256k -filter:a:" + k + " volume=" + Gain(on[k].Gain);
                if (!string.IsNullOrEmpty(on[k].Title))
                    a += " -metadata:s:a:" + k + " handler_name=\"" + Clean(on[k].Title) + "\" -metadata:s:a:" + k + " title=\"" + Clean(on[k].Title) + "\"";
            }
            return a;
        }

        // the one track of a shared file: what is heard in the preview → [out]
        static string ShareGraph(TrimJob j, List<TrackPlan> on)
        {
            return j.Tracks == null ? MixGraph(on, 0, "out") : FinalGraph(j, "out");
        }

        // ── even loudness ──
        public const double LoudTarget = -14;   // LUFS, as YouTube and most chats play it

        // the first loudnorm pass over what goes into the file: the second one needs these numbers to land exactly
        static bool LoudnormPass(TrimJob j, string range, CancellationToken cancel)
        {
            string share = ShareGraph(j, Plan(j));
            if (share == null) return false;
            var r = Ffmpeg.Run("-nostats " + range + " -filter_complex \"" + share + ";[out]loudnorm=I=" + LoudTarget + ":TP=-1:LRA=11:print_format=json[o]\" -map \"[o]\" -vn -f null NUL", cancel);
            int a = r.Err.LastIndexOf('{'), b = r.Err.LastIndexOf('}');
            if (r.Code != 0 || a < 0 || b < a) return false;
            var d = Json.Obj(Json.Parse(r.Err.Substring(a, b - a + 1)));
            Func<string, string> v = k => Json.GetStr(d, k);
            double i;
            if (d == null || !double.TryParse(v("input_i"), NumberStyles.Float, Inv, out i) || double.IsInfinity(i)) return false;
            j.LoudIn = i;
            j.Norm = "loudnorm=I=" + LoudTarget + ":TP=-1:LRA=11:measured_I=" + v("input_i") + ":measured_TP=" + v("input_tp") + ":measured_LRA=" + v("input_lra") +
                     ":measured_thresh=" + v("input_thresh") + ":offset=" + v("target_offset") + ":linear=true";
            return true;
        }

        // for the editor: how loud the shared file would be now, LUFS (NaN — unknown)
        public static double MeasureLoudness(TrimJob j, CancellationToken cancel)
        {
            if (j.SourceInfo == null || j.Gif) return double.NaN;
            string share = ShareGraph(j, Plan(j));
            if (share == null) return double.NaN;
            var r = Ffmpeg.Run("-nostats -ss " + Ffmpeg.T(j.In) + " -to " + Ffmpeg.T(j.Out) + " -i " + Q(j.Source) + " -filter_complex \"" + share +
                               ";[out]ebur128[o]\" -map \"[o]\" -vn -f null NUL", cancel);
            return Ffmpeg.ParseLufs(r.Err);
        }

        // ── GIF and WebP: an animation for chats, no sound ──
        // bytes per pixel and frame, measured on game footage: a GIF with a dithered palette, a lossy WebP at quality 70
        public static double GifMb(TrimJob j)
        {
            double px = j.GifWidth * (j.GifWidth * 9.0 / 16) * j.GifFps * Math.Max(0, j.Length);
            return px * (j.GifFormat == "webp" ? 0.09 : 0.6) / 1048576;
        }

        static Ffmpeg.Result EncodeGif(TrimJob j, string range, Action<double> prog, CancellationToken cancel)
        {
            string vf = "fps=" + j.GifFps + ",scale=" + j.GifWidth + ":-2:flags=lanczos";
            if (j.GifFormat == "webp")
                return Encode(range + " -map 0:v:0 -vf \"" + vf + "\" -c:v libwebp_anim -lossless 0 -q:v 70 -loop 0 -an", j.Output, prog, cancel);
            // one palette for the whole animation, ordered dithering: it packs far better than error diffusion
            return Encode(range + " -filter_complex \"[0:v]" + vf + ",split[a][b];[a]palettegen=stats_mode=diff[p];[b][p]paletteuse=dither=bayer:bayer_scale=5:diff_mode=rectangle\" -an -loop 0",
                          j.Output, prog, cancel);
        }

        public static string FinalGraph(TrimJob j, string label)
        {
            var all = j.Tracks ?? new List<TrackPlan>();
            var mix = all.FirstOrDefault(t => t.Source == j.MixIndex && t.On);
            if (mix != null && j.RebuildMix)
            {
                var g = RebuildGraph(j, mix);
                return g == null ? null : g.Replace("[mix]", "[" + label + "]");
            }
            if (mix != null) return MixGraph(new[] { mix }, 0, label);
            return MixGraph(all.Where(t => t.On), 0, label);
        }

        // ── preview audio for the editor ──
        public static bool PreviewAudio(TrimJob j, ICollection<int> solo, string wav, CancellationToken cancel)
        {
            var all = j.Tracks ?? new List<TrackPlan>();
            string graph;
            if (solo != null && solo.Count > 0)
                graph = MixGraph(all.Where(t => solo.Contains(t.Source)), 0, "out");
            else graph = FinalGraph(j, "out");
            if (graph == null) return false;
            var r = Ffmpeg.Run("-v error -i " + Q(j.Source) + " -filter_complex \"" + graph + "\" -map \"[out]\" -vn -ac 2 -ar 48000 -c:a pcm_s16le " + Q(wav), cancel);
            return r.Code == 0 && File.Exists(wav);
        }

        // ── size estimate ──
        public static string Estimate(TrimJob j)
        {
            var src = j.SourceInfo;
            if (src == null || src.Duration <= 0) return "";
            double part = Math.Max(0, j.Length) / src.Duration;
            long bytes = (long)(src.Size * part);
            switch (j.Mode)
            {
                case TrimMode.Share:
                    if (j.Gif) return "≈ " + Size((long)(GifMb(j) * 1048576));
                    if (j.Target == ShareTarget.Telegram)
                    {
                        // constant quality, and only a range that won't fit is squeezed under the limit
                        long budget = (long)(ShareBudgetMb(j) * 1048576.0), q = (long)(bytes * 0.45);
                        return q > budget ? "≤ " + Size(budget) : "≈ " + Size(q);
                    }
                    double capped = (ShareMaxKbps + 128) * 1000 / 8 * j.Length, target = ShareBudgetMb(j) * 1048576.0;
                    return capped >= target ? "≤ " + Size((long)target) : "≈ " + Size((long)capped);
                default:
                    return "≈ " + Size(bytes);
            }
        }

        static string Q(string p) { return "\"" + p + "\""; }

        // constant-quality re-encode on the chosen video encoder, the CPU if it is missing or fails
        static Ffmpeg.Result EncodeQuality(string before, string codec, int q, string after, string output, Action<double> prog, CancellationToken cancel)
        {
            string hw = Encoders.Quality(codec, q);
            Ffmpeg.Result r = hw != null ? Encode(before + " " + hw + after, output, prog, cancel) : null;
            if (r == null || r.Code != 0) r = Encode(before + " " + Encoders.CpuQuality(codec, q) + after, output, prog, cancel);
            return r;
        }

        // ── cuts: the In..Out range minus the cut pieces ──
        public static List<double[]> KeptSegments(double a, double b, List<double[]> cuts)
        {
            var merged = new List<double[]>();
            if (cuts != null)
                foreach (var c in cuts.Select(c => new[] { Math.Max(a, Math.Min(c[0], c[1])), Math.Min(b, Math.Max(c[0], c[1])) })
                                      .Where(c => c[1] - c[0] > 0.01).OrderBy(c => c[0]))
                {
                    if (merged.Count > 0 && c[0] <= merged[merged.Count - 1][1]) merged[merged.Count - 1][1] = Math.Max(merged[merged.Count - 1][1], c[1]);
                    else merged.Add(c);
                }
            var kept = new List<double[]>();
            double from = a;
            foreach (var c in merged)
            {
                if (c[0] - from > 0.05) kept.Add(new[] { from, c[0] });
                from = c[1];
            }
            if (b - from > 0.05) kept.Add(new[] { from, b });
            return kept;
        }

        // joining the remaining pieces frame-exactly: the video is re-encoded (like "Frame-exact"), all audio tracks — AAC 320k,
        // a short 10 ms crossfade at the joints so they don't click
        static Ffmpeg.Result Assemble(TrimJob j, string output, Action<double> prog, CancellationToken cancel)
        {
            var kept = j.Kept;
            var src = j.SourceInfo;
            int n = kept.Count, na = src.Audio.Count;
            double t0 = kept[0][0], t1 = kept[n - 1][1];
            var g = new System.Text.StringBuilder();
            g.Append("[0:v]split=" + n);
            for (int i = 0; i < n; i++) g.Append("[vs" + i + "]");
            g.Append(";");
            for (int i = 0; i < n; i++)
                g.Append("[vs" + i + "]trim=start=" + Ffmpeg.T(kept[i][0] - t0) + ":end=" + Ffmpeg.T(kept[i][1] - t0) + ",setpts=PTS-STARTPTS[v" + i + "];");
            for (int k = 0; k < na; k++)
            {
                g.Append("[0:a:" + k + "]asplit=" + n);
                for (int i = 0; i < n; i++) g.Append("[as" + k + "_" + i + "]");
                g.Append(";");
                for (int i = 0; i < n; i++)
                {
                    double len = kept[i][1] - kept[i][0];
                    g.Append("[as" + k + "_" + i + "]atrim=start=" + Ffmpeg.T(kept[i][0] - t0) + ":end=" + Ffmpeg.T(kept[i][1] - t0) + ",asetpts=PTS-STARTPTS");
                    if (len > 0.05) g.Append(",afade=t=in:d=0.01,afade=t=out:st=" + Ffmpeg.T(len - 0.01) + ":d=0.01");
                    g.Append("[a" + k + "_" + i + "];");
                }
            }
            for (int i = 0; i < n; i++)
            {
                g.Append("[v" + i + "]");
                for (int k = 0; k < na; k++) g.Append("[a" + k + "_" + i + "]");
            }
            g.Append("concat=n=" + n + ":v=1:a=" + na + "[vout]");
            for (int k = 0; k < na; k++) g.Append("[aout" + k + "]");

            string args = "-ss " + Ffmpeg.T(t0) + " -t " + Ffmpeg.T(t1 - t0) + " -i " + Q(j.Source) + " -filter_complex \"" + g + "\" -map \"[vout]\"";
            for (int k = 0; k < na; k++)
            {
                args += " -map \"[aout" + k + "]\"";
                if (!string.IsNullOrEmpty(src.Audio[k].Title))
                    args += " -metadata:s:a:" + k + " handler_name=\"" + Clean(src.Audio[k].Title) + "\" -metadata:s:a:" + k + " title=\"" + Clean(src.Audio[k].Title) + "\"";
            }
            string codec = src.Streams.Where(x => x.Type == "video").Select(x => x.Codec).FirstOrDefault() ?? "hevc";
            string audio = na > 0 ? " -c:a aac -b:a 320k" : "";
            return EncodeQuality(args, codec, 18, audio, output, prog, cancel);
        }

        // with cuts: join the remaining pieces into a temp file, check the join, then the usual path from the joined file
        static bool RunWithCuts(TrimJob j, Action<double> progress, Action<TrimStep> step, CancellationToken cancel)
        {
            var src = j.SourceInfo;
            var kept = j.Kept;
            double cut = (j.Out - j.In) - j.Length;
            var s = new TrimStep { Text = L.T("Joining ", "Склеиваю ") + L.N(kept.Count, "piece", "pieces", "кусок", "куска", "кусков") + L.T(" without the cut parts…", " без вырезанного…") };
            step(s);
            string dir = Path.Combine(Path.GetTempPath(), "dg_trim");
            Directory.CreateDirectory(dir);
            string tmp = Path.Combine(dir, "assembled_" + Guid.NewGuid().ToString("N") + ".mp4");
            try
            {
                double total = j.Length;
                var r = Assemble(j, tmp, t => progress(Math.Min(0.5, 0.5 * t / Math.Max(0.1, total))), cancel);
                if (r.Code != 0 || !File.Exists(tmp))
                {
                    s.Text = L.T("Could not join the pieces: ", "Не удалось склеить куски: ") + Ffmpeg.LastLine(r.Err);
                    s.State = 2;
                    step(s);
                    return false;
                }
                var info = Ffmpeg.Info(tmp);
                for (int k = 0; k < info.Audio.Count && k < src.Audio.Count; k++)
                    if (string.IsNullOrEmpty(info.Audio[k].Title)) info.Audio[k].Title = src.Audio[k].Title;
                bool lenOk = Math.Abs(info.Duration - total) < 0.3;
                s.Text = L.T("Joined: " + Dur(info.Duration) + " of " + Dur(total) + ", cut " + Dur(cut), "Склеено: " + Dur(info.Duration) + " из " + Dur(total) + ", вырезано " + Dur(cut));
                s.State = lenOk ? 1 : 2;
                step(s);
                if (!lenOk) return false;

                // audio after joining: in every track that had it in the kept pieces
                var lost = new List<string>();
                for (int k = 0; k < src.Audio.Count && k < info.Audio.Count; k++)
                {
                    int track = k;
                    double before = kept.Max(x => Ffmpeg.PeakDb(j.Source, track, x[0], x[1], cancel));
                    double after = Ffmpeg.PeakDb(tmp, k, null, null, cancel);
                    if (before > SilentDb && after <= SilentDb) lost.Add(TrackName(src.Audio[k], k));
                }
                step(new TrimStep
                {
                    Text = lost.Count == 0 ? L.T("Audio is intact in all tracks after joining", "Звук после склейки на месте во всех дорожках") : L.T("AUDIO LOST while joining: ", "ЗВУК ПРОПАЛ при склейке: ") + string.Join(", ", lost),
                    State = lost.Count == 0 ? 1 : 2,
                });
                if (lost.Count > 0) return false;

                // then — a normal trim of the whole joined file; frame-exact is already done by the join, no second re-encode
                var j2 = new TrimJob
                {
                    Source = tmp, SourceInfo = info, In = 0, Out = info.Duration,
                    OutputDir = !string.IsNullOrEmpty(j.OutputDir) ? j.OutputDir : Path.GetDirectoryName(j.Source),
                    Title = string.IsNullOrWhiteSpace(j.Title) ? Path.GetFileNameWithoutExtension(j.Source) + TrimSuffix : j.Title,
                    Mode = j.Mode == TrimMode.Precise ? TrimMode.Lossless : j.Mode, Target = j.Target, ShareAudio = j.ShareAudio, CustomMb = j.CustomMb,
                    Tracks = j.Tracks, MixIndex = j.MixIndex, RebuildMix = j.RebuildMix, Meta = j.Meta,
                    Loudness = j.Loudness, GifFormat = j.GifFormat, GifWidth = j.GifWidth, GifFps = j.GifFps,
                };
                bool ok = Run(j2, p => progress(0.5 + 0.5 * p), step, cancel);
                j.Output = j2.Output;
                return ok;
            }
            finally { TryDelete(tmp); }
        }

        // returns true if the file passed all checks
        public static bool Run(TrimJob j, Action<double> progress, Action<TrimStep> step, CancellationToken cancel)
        {
            var src = j.SourceInfo ?? Ffmpeg.Info(j.Source);
            j.SourceInfo = src;
            if (j.HasCuts) return RunWithCuts(j, progress, step, cancel);
            if (!string.IsNullOrEmpty(j.OutputDir)) Directory.CreateDirectory(j.OutputDir);
            j.Output = OutputPath(j);
            var on = Plan(j);
            string range = "-ss " + Ffmpeg.T(j.In) + " -to " + Ffmpeg.T(j.Out) + " -i " + Q(j.Source);
            j.Norm = null;
            if (j.Mode == TrimMode.Share && !j.Gif && j.Loudness)
            {
                var ls = new TrimStep { Text = L.T("Measuring the loudness…", "Измеряю громкость…") };
                step(ls);
                bool measured = LoudnormPass(j, range, cancel);
                ls.Text = measured ? L.T("Loudness ", "Громкость ") + j.LoudIn.ToString("0.0", Inv) + " LUFS → " + LoudTarget.ToString("0", Inv) + " LUFS"
                                   : L.T("The loudness could not be measured — the sound stays as it is", "Громкость измерить не вышло — звук останется как есть");
                ls.State = measured ? 1 : 3;
                step(ls);
            }
            string audio = AudioArgs(j, on);
            string meta = " -map_metadata 0" + MetaArgs(j) + " -movflags +faststart";
            Action<double> prog = t => progress(Math.Min(1, t / Math.Max(0.1, j.Length)));

            var s = new TrimStep { Text = L.T("Saving…", "Сохраняю…") };
            step(s);
            string codec = src.Streams.Where(x => x.Type == "video").Select(x => x.Codec).FirstOrDefault() ?? "hevc";
            Ffmpeg.Result r;
            if (j.Mode == TrimMode.Lossless)
                r = Encode(range + " -map 0:v:0 -c:v copy" + audio + meta + " -avoid_negative_ts make_zero", j.Output, prog, cancel);
            else if (j.Mode == TrimMode.Precise)
                r = EncodeQuality(range + " -map 0:v:0", codec, 18, audio + meta, j.Output, prog, cancel);
            else if (j.Gif) r = EncodeGif(j, range, prog, cancel);
            else r = EncodeShare(j, range, audio + meta, prog, cancel);

            if (r.Code != 0 || !File.Exists(j.Output) || new FileInfo(j.Output).Length == 0)
            {
                s.Text = L.T("Could not save: ", "Не удалось сохранить: ") + Ffmpeg.LastLine(r.Err);
                s.State = 2;
                step(s);
                TryDelete(j.Output);
                return false;
            }
            long size = new FileInfo(j.Output).Length;
            s.Text = L.T("Saved: ", "Сохранено: ") + Path.GetFileName(j.Output) + " · " + Size(size);
            s.State = 1;
            step(s);
            progress(1);
            return Verify(j, size, step, cancel);
        }

        static Ffmpeg.Result Encode(string args, string output, Action<double> prog, CancellationToken cancel)
        {
            return Ffmpeg.Run(Ffmpeg.Exe, "-hide_banner -nostdin -y -v error -progress pipe:1 " + args + " " + Q(output), prog, cancel);
        }

        // what to aim for under the limit: a little headroom (Discord 10 → 9.5 MB, Nitro 500 → 480 MB)
        static double ShareBudgetMb(TrimJob j)
        {
            return j.LimitMb - Math.Max(0.5, j.LimitMb * 0.04);
        }

        // for sharing: H.264 (opens everywhere), one track, size under the limit
        static Ffmpeg.Result EncodeShare(TrimJob j, string range, string rest, Action<double> prog, CancellationToken cancel)
        {
            double limitMb = ShareBudgetMb(j);
            Ffmpeg.Result r = null;
            if (j.Target == ShareTarget.Telegram)
            {
                // good quality first; only a long range over 2 GB falls back to a bitrate under the limit
                r = EncodeQuality(range + " -map 0:v:0", "h264", 21, " -pix_fmt yuv420p" + rest, j.Output, prog, cancel);
                if (r.Code != 0 || new FileInfo(j.Output).Length <= limitMb * 1048576) return r;
            }
            for (double k = 0.93; k > 0.5; k -= 0.12)
            {
                double totalKbps = limitMb * 8 * 1024 * k / Math.Max(1, j.Length);
                // a long range into a small limit (minutes into Discord's 10 MB): the sound gives up half, the picture goes to 480p
                bool tight = totalKbps < 500;
                string tryRest = tight ? rest.Replace("-b:a 128k", "-b:a 64k") : rest;
                double videoKbps = Math.Min(ShareMaxKbps, Math.Max(90, totalKbps - (tight ? 64 : 128)));
                // at a low bitrate 1080p60 turns to mush — reduce the frame size and rate
                string vf = videoKbps < 600 ? "scale=-2:480,fps=30" : videoKbps < 1500 ? "scale=-2:720,fps=30" : videoKbps < 4500 ? "scale=-2:720" : null;
                string rate = ((int)videoKbps) + "k", buf = ((int)videoKbps * 2) + "k";
                string hw = Encoders.Bitrate(rate, buf), v = null;
                r = null;
                if (hw != null)
                {
                    v = " -map 0:v:0 " + hw + " -pix_fmt yuv420p" + (vf != null ? " -vf " + vf : "");
                    r = Encode(range + v + tryRest, j.Output, prog, cancel);
                }
                if (r == null || r.Code != 0)
                {
                    v = " -map 0:v:0 -c:v libx264 -preset veryfast -b:v " + rate + " -maxrate " + rate + " -bufsize " + buf + " -pix_fmt yuv420p" +
                        (vf != null ? " -vf " + vf : "");
                    r = Encode(range + v + tryRest, j.Output, prog, cancel);
                }
                if (r.Code != 0) return r;
                if (new FileInfo(j.Output).Length <= limitMb * 1048576) return r;   // fits
            }
            return r;
        }

        public static bool Verify(TrimJob j, long size, Action<TrimStep> step, CancellationToken cancel)
        {
            bool ok = true;
            Action<string, int> add = (text, state) =>
            {
                step(new TrimStep { Text = text, State = state });
                if (state == 2) ok = false;
            };

            if (j.Gif) return VerifyGif(j, size, add, cancel) && ok;

            Ffmpeg.MediaInfo dst;
            try { dst = Ffmpeg.Info(j.Output); }
            catch (Exception ex) { add(L.T("The file cannot be read: ", "Файл не читается: ") + ex.Message, 2); return false; }

            // length
            double expect = j.Length;
            if (j.Mode == TrimMode.Lossless)
            {
                // without re-encoding the start snaps to a keyframe — the file may be slightly longer
                bool good = dst.Duration >= expect - 0.3 && dst.Duration <= expect + 6;
                add(L.T("Length ", "Длина ") + Dur(dst.Duration) + (dst.Duration > expect + 0.3
                    ? L.T(" (the start snapped to a keyframe, +", " (начало встало на ключевой кадр, +") + (dst.Duration - expect).ToString("0.0", Inv) + L.T(" s)", " с)")
                    : ""), good ? 1 : 2);
            }
            else add(L.T("Length " + Dur(dst.Duration) + " of " + Dur(expect), "Длина " + Dur(dst.Duration) + " из " + Dur(expect)), Math.Abs(dst.Duration - expect) < 0.5 ? 1 : 2);

            // video
            if (dst.Video == 0) add(L.T("The file has no video", "В файле нет видео"), 2);
            else
            {
                string err = Ffmpeg.DecodeErrors(j.Output, dst.Duration, cancel);
                add(err == null ? L.T("The video reads from start to end", "Видео читается от начала до конца") : L.T("Video error: ", "Ошибка в видео: ") + err, err == null ? 1 : 2);
            }

            // audio: the number of tracks and sound in each (accounting for changed volume)
            var on = Plan(j);
            var srcAudio = j.SourceInfo.Audio;
            var dstAudio = dst.Audio;
            add(L.T("Audio tracks: " + dstAudio.Count + " of " + on.Count, "Звуковых дорожек: " + dstAudio.Count + " из " + on.Count), dstAudio.Count == on.Count ? 1 : 2);
            for (int i = 0; i < dstAudio.Count && i < on.Count; i++)
            {
                var plan = on[i];
                if (plan.Source >= srcAudio.Count) break;
                string name = TrackName(srcAudio[plan.Source], plan.Source) + (Rebuilt(j, plan) ? L.T(" (rebuilt)", " (пересобран)") : "") +
                              (plan.Gain != 0 ? " (" + (plan.Gain > 0 ? "+" : "") + plan.Gain.ToString("0.#", Inv) + L.T(" dB)", " дБ)") : "");
                double srcDb = Ffmpeg.PeakDb(j.Source, plan.Source, j.In, j.Out, cancel);
                double dstDb = Ffmpeg.PeakDb(j.Output, i, null, null, cancel);
                bool srcSound = srcDb + (Rebuilt(j, plan) ? 0 : plan.Gain) > SilentDb, dstSound = dstDb > SilentDb;
                if (dstSound) add(name + L.T(": has audio (", ": звук есть (") + Db(dstDb) + ")", 1);
                else if (!srcSound) add(name + L.T(": silence — the source is quiet on this range too", ": тишина — в исходнике на этом отрезке тоже тихо"), 3);
                else add(name + L.T(": AUDIO LOST — the source has it (", ": ЗВУК ПРОПАЛ — в исходнике он есть (") + Db(srcDb) + ")", 2);
            }

            // even loudness: where the file landed
            if (j.Norm != null && dstAudio.Count > 0)
            {
                double lufs = Ffmpeg.Lufs(j.Output, 0, cancel);
                bool near = Math.Abs(lufs - LoudTarget) <= 1.5;
                add(L.T("Loudness of the file: ", "Громкость файла: ") + (double.IsNaN(lufs) ? "?" : lufs.ToString("0.0", Inv)) + " LUFS" +
                    (near ? "" : L.T(" (the target is " + LoudTarget + ")", " (цель — " + LoudTarget + ")")), near ? 1 : 3);
            }

            if (j.Mode == TrimMode.Share)
            {
                double limit = j.LimitMb;
                double mb = size / 1048576.0;
                string where = j.Target == ShareTarget.Discord ? "Discord" : j.Target == ShareTarget.Nitro ? "Discord Nitro"
                             : j.Target == ShareTarget.Telegram ? "Telegram" : L.T(limit + " MB", limit + " МБ");
                add(L.T("Size ", "Размер ") + Size(size) + (mb <= limit ? L.T(" — fits ", " — пролезет в ") : L.T(" — over the limit of ", " — больше лимита ")) +
                    where, mb <= limit ? 1 : 2);
            }

            // metadata: game and recording time ("Sort by game" relies on them)
            if (j.Meta != null)
            {
                var m = ReadMeta(dst);
                add(m != null && m.Game == j.Meta.Game
                    ? L.T("Written to the file: \"" + NoGame.Text(m.Game) + "\", clip from ", "В файле записано: «" + NoGame.Text(m.Game) + "», клип от ") + m.Recorded.ToString("dd.MM.yyyy HH:mm", Inv)
                    : L.T("Metadata was not written — \"Sort by game\" will not recognize this file", "Метаданные не записались — «Разложить по играм» этот файл не узнает"), m != null ? 1 : 3);
            }
            return ok;
        }

        // a GIF or a WebP: it reads to the end, its length (GIF; ffprobe does not tell a WebP's), the size
        static bool VerifyGif(TrimJob j, long size, Action<string, int> add, CancellationToken cancel)
        {
            bool ok = true;
            var r = Ffmpeg.Run("-v error -i " + Q(j.Output) + " -f null NUL", cancel);
            bool reads = r.Code == 0 && string.IsNullOrWhiteSpace(r.Err);
            add(reads ? L.T("The animation reads from start to end", "Анимация читается от начала до конца") : L.T("Animation error: ", "Ошибка в анимации: ") + Ffmpeg.LastLine(r.Err), reads ? 1 : 2);
            ok &= reads;
            if (j.GifFormat != "webp")
            {
                double len = 0;
                try { len = Ffmpeg.Info(j.Output).Duration; } catch { }
                bool good = Math.Abs(len - j.Length) < 0.5;
                add(L.T("Length " + Dur(len) + " of " + Dur(j.Length), "Длина " + Dur(len) + " из " + Dur(j.Length)), good ? 1 : 2);
                ok &= good;
            }
            add(L.T("Size ", "Размер ") + Size(size) + " · " + j.GifWidth + L.T(" px wide, ", " пикс. в ширину, ") + j.GifFps + L.T(" fps, no sound", " к/с, без звука"), 1);
            return ok;
        }

        public static string TrackName(Ffmpeg.StreamInfo s, int index)
        {
            return L.T("Track ", "Дорожка ") + (index + 1) + (string.IsNullOrEmpty(s.Title) ? "" : " · " + s.Title);
        }

        static string Db(double db) { return (double.IsNegativeInfinity(db) ? "−∞" : db.ToString("0.#", Inv)) + L.T(" dB", " дБ"); }

        public static string Dur(double sec)
        {
            var t = TimeSpan.FromSeconds(Math.Max(0, sec));
            return (int)t.TotalMinutes + ":" + t.Seconds.ToString("00") + "." + (t.Milliseconds / 100);
        }

        public static string Size(long bytes)
        {
            double mb = bytes / 1048576.0;
            return mb >= 1024 ? (mb / 1024).ToString("0.00", Inv) + L.T(" GB", " ГБ") : mb.ToString(mb < 10 ? "0.0" : "0", Inv) + L.T(" MB", " МБ");
        }

        static void TryDelete(string p)
        {
            try { if (File.Exists(p)) File.Delete(p); } catch { }
        }

        // ── "To collection": ready clips → "collection\Game\" ──
        public class Move { public string From, To, Game; }

        public static List<Move> SortPlan(string folder, string collection, out List<string> unknown)
        {
            var moves = new List<Move>();
            unknown = new List<string>();
            foreach (var f in Directory.GetFiles(folder, "*.mp4"))
            {
                ClipMeta m = null;
                try { m = ReadMeta(Ffmpeg.Info(f)); } catch { }
                var mv = m != null ? MoveFor(f, m.Game, collection) : null;
                if (mv == null) unknown.Add(Path.GetFileName(f));
                else moves.Add(mv);
            }
            return moves;
        }

        // where to put one clip; game == "" — the collection root; null — the game is unknown
        public static Move MoveFor(string file, string game, string collection)
        {
            if (game == null || game == NoGame.Game) return null;
            string dir = game == "" ? collection : GameDir(collection, game);
            string to = Path.Combine(dir, Path.GetFileName(file));
            for (int i = 2; File.Exists(to); i++)
                to = Path.Combine(dir, Path.GetFileNameWithoutExtension(file) + " " + i + Path.GetExtension(file));
            return new Move { From = file, To = to, Game = game };
        }

        // the game folder: an existing one with the same name ("Hunt  Showdown" = "Hunt Showdown"), otherwise a new one
        public static string GameDir(string root, string game)
        {
            Func<string, string> norm = s => Regex.Replace(s.ToLowerInvariant(), @"[^\p{L}\p{N}]", "");
            try
            {
                var same = Directory.GetDirectories(root).FirstOrDefault(d => norm(Path.GetFileName(d)) == norm(game));
                if (same != null) return same;
            }
            catch { }
            return Path.Combine(root, SafeName(game));
        }
    }
}
