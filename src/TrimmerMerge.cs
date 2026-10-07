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

        // the filter graph that joins the inputs 0..n-1
        public static string MergeGraph(IList<Ffmpeg.MediaInfo> infos, MergeShape sh, int mixIndex, bool fade)
        {
            var g = new StringBuilder();
            string fps = sh.Fps.ToString("0.###", Inv);
            for (int i = 0; i < infos.Count; i++)
            {
                g.Append("[" + i + ":v:0]scale=" + sh.W + ":" + sh.H + ":force_original_aspect_ratio=decrease,pad=" + sh.W + ":" + sh.H +
                         ":(ow-iw)/2:(oh-ih)/2,setsar=1,fps=" + fps + ",format=yuv420p,settb=AVTB[v" + i + "];");
                int na = infos[i].Audio.Count;
                if (na == 0) g.Append("anullsrc=r=48000:cl=stereo,atrim=duration=" + Ffmpeg.T(infos[i].Duration) + "[a" + i + "];");
                else g.Append("[" + i + ":a:" + (mixIndex >= 0 && mixIndex < na ? mixIndex : 0) + "]aresample=48000,aformat=channel_layouts=stereo,asetpts=PTS-STARTPTS[a" + i + "];");
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
                at += infos[i - 1].Duration - FadeSec;
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
            double total = MergedLength(infos.Select(i => i.Duration).ToList(), m.Fade);
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
                string args = string.Join(" ", m.Clips.Select(c => "-i " + Q(c))) + " -filter_complex \"" + MergeGraph(infos, sh, m.MixIndex, m.Fade) + "\" -map \"[vout]\" -map \"[aout]\"";
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
            if (m.Mode != TrimMode.Share) return "≈ " + Size(infos.Sum(i => i.Size));
            var j = new TrimJob { Mode = TrimMode.Share, Target = m.Target, CustomMb = m.CustomMb, In = 0, Out = MergedLength(infos.Select(i => i.Duration).ToList(), m.Fade),
                                  SourceInfo = new Ffmpeg.MediaInfo { Duration = MergedLength(infos.Select(i => i.Duration).ToList(), m.Fade), Size = infos.Sum(i => i.Size) } };
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
            var sh = ShapeOf(two[0]);
            string flat = MergeGraph(two, sh, 3, false), faded = MergeGraph(two, sh, 3, true);
            check(sh.W == 2560 && flat.Contains("[0:a:3]") && flat.Contains("[1:a:0]") && flat.Contains("anullsrc") && flat.EndsWith("concat=n=3:v=1:a=1[vout][aout]"),
                  "merge: the mix of each clip (or its only track, or silence), back to back");
            check(faded.Contains("offset=9.7[vx1]") && faded.Contains("offset=14.4[vout]") && faded.Contains("acrossfade=d=0.3[aout]") && !faded.EndsWith(";"),
                  "merge: fades start 0.3 s before each clip ends");
            check(Math.Abs(MergedLength(new[] { 10.0, 5, 8 }, true) - 22.4) < 1e-9, "merge: the length with fades");
        }
    }
}
