using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace DeviceGuard
{
    // Several whole clips joined into one, in the given order, back to back or with a short fade.
    // Every clip is brought to the first one's frame size and rate (a smaller one gets bars, not stretching) and gives its
    // common mix as the one audio track. The join goes into a temporary file at "Frame-exact" quality and is checked;
    // then the usual path saves it — copied as is, or fitted for sharing — and checks the result.
    class MergeJob
    {
        public List<string> Clips = new List<string>();
        public List<double[]> Ranges;      // per clip [from, to] in seconds, null (or a null entry) — the whole clip
        public bool Fade;                  // 0.3 s cross-fade instead of back to back
        public TrimMode Mode = TrimMode.Precise;   // Precise — keep the quality, Share — fit a target
        public ShareTarget Target;
        public int CustomMb = 25;
        public bool Loudness;
        public int MixIndex = -1;          // the common mix track of OBS clips; a clip with fewer tracks gives its first one
        public string Title, OutputDir, Output;
        public ClipMeta Meta;
    }

    static partial class Trimmer
    {
        public const double FadeSec = 0.3;

        // the frame and rate everything is brought to: the first clip's
        public class MergeShape { public int W = 1920, H = 1080; public double Fps = 60; }

        public static MergeShape ShapeOf(Ffmpeg.MediaInfo info)
        {
            var v = info.Streams.FirstOrDefault(s => s.Type == "video");
            var sh = new MergeShape();
            if (v != null && v.Width > 0 && v.Height > 0) { sh.W = v.Width; sh.H = v.Height; }
            if (v != null && v.Fps > 1) sh.Fps = Math.Round(v.Fps, 3);
            return sh;
        }

        public static double MergedLength(IList<double> durations, bool fade)
        {
            return durations.Sum() - (fade ? FadeSec * Math.Max(0, durations.Count - 1) : 0);
        }

        // which audio track of each clip goes into the join (-1 — the clip has none, silence takes its place):
        // a trim names its common mix; an OBS clip has it where the current OBS layout has it; a single track is the one;
        // otherwise the loudest track — the common mix carries everything, so it is the loudest on average
        public static int[] AudioPicks(IList<Ffmpeg.MediaInfo> infos, IList<string> files, int mixIndex, Action<TrimStep> step, CancellationToken cancel)
        {
            var picks = new int[infos.Count];
            for (int i = 0; i < infos.Count; i++)
            {
                int na = infos[i].Audio.Count;
                var meta = ReadMeta(infos[i]);
                if (na == 0) picks[i] = -1;
                else if (meta != null && meta.Mix >= 0 && meta.Mix < na) picks[i] = meta.Mix;
                else if (meta == null && mixIndex >= 0 && mixIndex < na) picks[i] = mixIndex;
                else if (na == 1) picks[i] = 0;
                else
                {
                    int k = i;
                    picks[i] = Enumerable.Range(0, na).OrderByDescending(t => Ffmpeg.MeanDb(files[k], t, cancel)).First();
                    step(new TrimStep { State = 3, Text = "«" + Path.GetFileNameWithoutExtension(files[i]) + L.T("»: the file does not say which track is the common mix — the loudest one is taken (track ",
                                                                                                              "»: в файле не записано, какая дорожка — общий микс; взята самая громкая (дорожка ") + (picks[i] + 1) + ")" });
                }
            }
            return picks;
        }

        // a clip's part in the join: its range, or the whole clip
        public static double[] RangeOf(MergeJob m, int i, Ffmpeg.MediaInfo info)
        {
            var r = m.Ranges != null && i < m.Ranges.Count ? m.Ranges[i] : null;
            if (r == null) return new[] { 0.0, info.Duration };
            // kept inside the clip and at least half a second long, whatever was asked
            double a = Math.Max(0, Math.Min(r[0], info.Duration - 0.5));
            return new[] { a, Math.Max(a + 0.5, Math.Min(info.Duration, r[1])) };
        }

        // the filter graph that joins the inputs 0..n-1 (each already cut to its range, so lengths are the ranges'),
        // each with its audio track from AudioPicks
        public static string MergeGraph(IList<Ffmpeg.MediaInfo> infos, IList<double> lengths, MergeShape sh, int[] picks, bool fade)
        {
            var g = new StringBuilder();
            string fps = sh.Fps.ToString("0.###", Inv);
            for (int i = 0; i < infos.Count; i++)
            {
                g.Append("[" + i + ":v:0]scale=" + sh.W + ":" + sh.H + ":force_original_aspect_ratio=decrease,pad=" + sh.W + ":" + sh.H +
                         ":(ow-iw)/2:(oh-ih)/2,setsar=1,fps=" + fps + ",format=yuv420p,settb=AVTB[v" + i + "];");
                if (picks[i] < 0) g.Append("anullsrc=r=48000:cl=stereo,atrim=duration=" + Ffmpeg.T(lengths[i]) + "[a" + i + "];");
                else g.Append("[" + i + ":a:" + picks[i] + "]aresample=48000,aformat=channel_layouts=stereo,asetpts=PTS-STARTPTS[a" + i + "];");
            }
            int n = infos.Count;
            if (!fade)
            {
                for (int i = 0; i < n; i++) g.Append("[v" + i + "][a" + i + "]");
                g.Append("concat=n=" + n + ":v=1:a=1[vout][aout]");
                return g.ToString();
            }
            // a chain of cross-fades: each next clip starts FadeSec before the previous one ends
            string v = "[v0]", a = "[a0]";
            double at = 0;
            for (int i = 1; i < n; i++)
            {
                at += lengths[i - 1] - FadeSec;
                string vo = i == n - 1 ? "[vout]" : "[vx" + i + "]", ao = i == n - 1 ? "[aout]" : "[ax" + i + "]";
                g.Append(v + "[v" + i + "]xfade=transition=fade:duration=" + Ffmpeg.T(FadeSec) + ":offset=" + Ffmpeg.T(at) + vo + ";");
                g.Append(a + "[a" + i + "]acrossfade=d=" + Ffmpeg.T(FadeSec) + ao + (i == n - 1 ? "" : ";"));
                v = vo;
                a = ao;
            }
            return g.ToString();
        }

        public static bool Merge(MergeJob m, Action<double> progress, Action<TrimStep> step, CancellationToken cancel)
        {
            var read = new TrimStep { Text = L.T("Reading the clips…", "Читаю клипы…") };
            step(read);
            var infos = m.Clips.Select(Ffmpeg.Info).ToList();
            var sh = ShapeOf(infos[0]);
            var ranges = infos.Select((info, i) => RangeOf(m, i, info)).ToList();
            var lengths = ranges.Select(r => r[1] - r[0]).ToList();
            double total = MergedLength(lengths, m.Fade);
            read.Text = L.N(m.Clips.Count, "clip", "clips", "клип", "клипа", "клипов") + " · " + Dur(total) + " · " + sh.W + "×" + sh.H + ", " + sh.Fps.ToString("0.##", Inv) + L.T(" fps", " к/с");
            read.State = 1;
            step(read);

            var s = new TrimStep { Text = m.Fade ? L.T("Joining with fades…", "Склеиваю с затуханием…") : L.T("Joining…", "Склеиваю…") };
            step(s);
            string dir = Path.Combine(Path.GetTempPath(), "dg_trim");
            Directory.CreateDirectory(dir);
            string tmp = Path.Combine(dir, "merged_" + Guid.NewGuid().ToString("N") + ".mp4");
            try
            {
                var picks = AudioPicks(infos, m.Clips, m.MixIndex, step, cancel);
                // each input cut to its range before decoding: seeking on the input is exact when the video is re-encoded
                string inputs = string.Join(" ", m.Clips.Select((c, i) => (ranges[i][0] > 0.01 || ranges[i][1] < infos[i].Duration - 0.01
                    ? "-ss " + Ffmpeg.T(ranges[i][0]) + " -to " + Ffmpeg.T(ranges[i][1]) + " " : "") + "-i " + Q(c)));
                string args = inputs + " -filter_complex \"" + MergeGraph(infos, lengths, sh, picks, m.Fade) + "\" -map \"[vout]\" -map \"[aout]\"";
                string codec = infos[0].Streams.Where(x => x.Type == "video").Select(x => x.Codec).FirstOrDefault() ?? "hevc";
                var r = EncodeQuality(args, codec, 18, " -c:a aac -b:a 320k", tmp, t => progress(Math.Min(0.5, 0.5 * t / Math.Max(0.1, total))), cancel);
                if (r.Code != 0 || !File.Exists(tmp))
                {
                    s.Text = L.T("Could not join the clips: ", "Не удалось склеить клипы: ") + Ffmpeg.LastLine(r.Err);
                    s.State = 2;
                    step(s);
                    return false;
                }
                var info = Ffmpeg.Info(tmp);
                bool lenOk = Math.Abs(info.Duration - total) < 0.5 + 0.05 * m.Clips.Count;
                s.Text = L.T("Joined: " + Dur(info.Duration) + " of " + Dur(total), "Склеено: " + Dur(info.Duration) + " из " + Dur(total));
                s.State = lenOk ? 1 : 2;
                step(s);
                if (!lenOk) return false;
                double peak = Ffmpeg.PeakDb(tmp, 0, null, null, cancel);
                bool sound = peak > SilentDb || infos.All(i => i.Audio.Count == 0);
                step(new TrimStep { Text = sound ? L.T("The joined clip has sound", "Звук в склейке есть") : L.T("NO SOUND in the joined clip", "В склейке НЕТ ЗВУКА"), State = sound ? 1 : 2 });
                if (!sound) return false;

                // then the usual path from the joined file: copied as is (no second re-encode) or fitted for sharing
                var j = new TrimJob
                {
                    Source = tmp, SourceInfo = info, In = 0, Out = info.Duration, OutputDir = m.OutputDir ?? Path.GetDirectoryName(m.Clips[0]),
                    Title = m.Title, Mode = m.Mode == TrimMode.Share ? TrimMode.Share : TrimMode.Lossless, Target = m.Target, CustomMb = m.CustomMb,
                    ShareAudio = 0, Loudness = m.Loudness, Meta = m.Meta,
                };
                bool ok = Run(j, p => progress(0.5 + 0.5 * p), step, cancel);
                m.Output = j.Output;
                return ok;
            }
            finally { TryDelete(tmp); }
        }

        // what the joined file may weigh before it is made
        public static string MergeEstimate(MergeJob m, IList<Ffmpeg.MediaInfo> infos)
        {
            if (infos == null || infos.Count == 0) return "";
            // a part of a clip weighs its share of the file
            long bytes = (long)infos.Select((info, i) => { var r = RangeOf(m, i, info); return info.Duration > 0 ? info.Size * (r[1] - r[0]) / info.Duration : 0; }).Sum();
            double len = MergedLength(infos.Select((info, i) => { var r = RangeOf(m, i, info); return r[1] - r[0]; }).ToList(), m.Fade);
            if (m.Mode != TrimMode.Share) return "≈ " + Size(bytes);
            var j = new TrimJob { Mode = TrimMode.Share, Target = m.Target, CustomMb = m.CustomMb, In = 0, Out = len,
                                  SourceInfo = new Ffmpeg.MediaInfo { Duration = len, Size = bytes } };
            return Estimate(j);
        }

        // for the self-test: the graph of two clips, back to back and faded
        public static void TestMerge(Action<bool, string> check)
        {
            Func<double, int, Ffmpeg.MediaInfo> clip = (dur, audio) =>
            {
                var i = new Ffmpeg.MediaInfo { Duration = dur };
                i.Streams.Add(new Ffmpeg.StreamInfo { Type = "video", Width = 2560, Height = 1440, Fps = 60 });
                for (int k = 0; k < audio; k++) i.Streams.Add(new Ffmpeg.StreamInfo { Type = "audio" });
                return i;
            };
            var two = new List<Ffmpeg.MediaInfo> { clip(10, 4), clip(5, 1), clip(8, 0) };
            var trim = clip(6, 3);
            trim.Tags["description"] = MetaTag + "|game=Hunt|recorded=2026-10-05T19:56:05|source=a.mp4|mix=2";   // a trim that names its mix
            two.Add(trim);
            var picks = AudioPicks(two, new[] { "a", "b", "c", "d" }, 3, s => { }, CancellationToken.None);
            check(string.Join(",", picks) == "3,0,-1,2", "merge: the OBS mix, a single track, silence, the mix a trim names (" + string.Join(",", picks) + ")");
            two.RemoveAt(3);
            var sh = ShapeOf(two[0]);
            var lens = new[] { 10.0, 5, 8 };
            string flat = MergeGraph(two, lens, sh, new[] { 3, 0, -1 }, false), faded = MergeGraph(two, lens, sh, new[] { 3, 0, -1 }, true);
            check(sh.W == 2560 && flat.Contains("[0:a:3]") && flat.Contains("[1:a:0]") && flat.Contains("anullsrc") && flat.EndsWith("concat=n=3:v=1:a=1[vout][aout]"),
                  "merge: each clip's track (or silence), back to back");
            check(faded.Contains("offset=9.7[vx1]") && faded.Contains("offset=14.4[vout]") && faded.Contains("acrossfade=d=0.3[aout]") && !faded.EndsWith(";"),
                  "merge: fades start 0.3 s before each clip ends");
            check(Math.Abs(MergedLength(new[] { 10.0, 5, 8 }, true) - 22.4) < 1e-9, "merge: the length with fades");
            var part = new MergeJob { Ranges = new List<double[]> { new[] { 2.0, 6.0 }, null, new[] { -1.0, 99.0 }, new[] { 30.0, 40.0 } } };
            var r0 = RangeOf(part, 0, two[0]); var r1 = RangeOf(part, 1, two[1]); var r2 = RangeOf(part, 2, two[2]); var r3 = RangeOf(part, 3, two[0]);
            check(r0[0] == 2 && r0[1] == 6 && r1[0] == 0 && r1[1] == 5 && r2[0] == 0 && r2[1] == 8 && r3[0] == 9.5 && r3[1] == 10,
                  "merge: a clip's range, the whole clip, a range kept inside the clip, one past its end");
        }
    }
}
