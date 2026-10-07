using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace DeviceGuard
{
    // A wrapper over ffmpeg/ffprobe from the ffmpeg folder next to the exe
    static class Ffmpeg
    {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public static string Dir { get { return Path.Combine(Program.ExeDir ?? Program.Dir, "ffmpeg"); } }   // it comes with the exe
        public static string Exe { get { return Path.Combine(Dir, "ffmpeg.exe"); } }
        public static string Probe { get { return Path.Combine(Dir, "ffprobe.exe"); } }
        public static bool Available { get { return File.Exists(Exe) && File.Exists(Probe); } }

        public static string T(double sec) { return sec.ToString("0.###", Inv); }

        public class Result { public int Code; public string Out, Err; }

        // run reading stdout/stderr; onProgress gets the seconds of processed video (-progress pipe:1)
        public static Result Run(string exe, string args, Action<double> onProgress, CancellationToken cancel)
        {
            var psi = new ProcessStartInfo(exe, args)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            };
            var outSb = new StringBuilder();
            var errSb = new StringBuilder();
            using (var p = new Process { StartInfo = psi })
            {
                p.OutputDataReceived += (s, e) =>
                {
                    if (e.Data == null) return;
                    outSb.AppendLine(e.Data);
                    if (onProgress != null && e.Data.StartsWith("out_time_us="))
                    {
                        long us;
                        if (long.TryParse(e.Data.Substring(12), out us) && us > 0) onProgress(us / 1e6);
                    }
                };
                p.ErrorDataReceived += (s, e) => { if (e.Data != null) errSb.AppendLine(e.Data); };
                p.Start();
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                while (!p.WaitForExit(200))
                {
                    if (cancel.IsCancellationRequested)
                    {
                        try { p.Kill(); } catch { }
                        p.WaitForExit(3000);
                        throw new OperationCanceledException();
                    }
                }
                p.WaitForExit();
                return new Result { Code = p.ExitCode, Out = outSb.ToString(), Err = errSb.ToString() };
            }
        }

        public static Result Run(string args, CancellationToken cancel)
        {
            return Run(Exe, "-hide_banner -nostdin -y " + args, null, cancel);
        }

        static string Q(string path) { return "\"" + path + "\""; }

        // ── file analysis ──
        public class StreamInfo { public string Type, Codec, Title; public int Index, Width, Height; public double Fps; }

        public class MediaInfo
        {
            public double Duration;
            public long Size;
            public Dictionary<string, string> Tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            public List<StreamInfo> Streams = new List<StreamInfo>();
            public int Video { get { return Streams.Count(s => s.Type == "video"); } }
            // frames per second of the first video stream (for frame stepping); 60 if unknown
            public double Fps { get { var v = Streams.FirstOrDefault(s => s.Type == "video" && s.Fps > 1); return v != null ? v.Fps : 60; } }
            public List<StreamInfo> Audio { get { return Streams.Where(s => s.Type == "audio").ToList(); } }
        }

        public static MediaInfo Info(string file)
        {
            var r = Run(Probe, "-v error -show_entries format=duration,size:format_tags:stream=index,codec_type,codec_name,avg_frame_rate,width,height:stream_tags=title,handler_name -of json " + Q(file),
                        null, CancellationToken.None);
            if (r.Code != 0) throw new IOException("ffprobe: " + LastLine(r.Err));
            var d = Json.Obj(Json.Parse(r.Out));
            var info = new MediaInfo();
            double dur;
            var fmt = Json.GetObj(d, "format");
            if (double.TryParse(Json.GetStr(fmt, "duration"), NumberStyles.Float, Inv, out dur)) info.Duration = dur;
            long size;
            if (long.TryParse(Json.GetStr(fmt, "size"), out size)) info.Size = size;
            var ftags = Json.GetObj(fmt, "tags");
            if (ftags != null) foreach (var kv in ftags) info.Tags[kv.Key] = Convert.ToString(kv.Value, Inv);
            foreach (var o in Json.GetArr(d, "streams"))
            {
                var s = Json.Obj(o);
                var tags = Json.GetObj(s, "tags");
                string title = Json.GetStr(tags, "title");
                string handler = Json.GetStr(tags, "handler_name");
                // OBS writes the track name into handler_name; service "SoundHandler" names are hidden
                if (string.IsNullOrEmpty(title) && handler != null && !handler.EndsWith("Handler")) title = handler;
                info.Streams.Add(new StreamInfo
                {
                    Index = Json.GetInt(s, "index", 0), Type = Json.GetStr(s, "codec_type"), Codec = Json.GetStr(s, "codec_name"), Title = title,
                    Fps = Rate(Json.GetStr(s, "avg_frame_rate")), Width = Json.GetInt(s, "width", 0), Height = Json.GetInt(s, "height", 0),
                });
            }
            return info;
        }

        // "60/1", "30000/1001" → frames per second
        static double Rate(string r)
        {
            if (string.IsNullOrEmpty(r)) return 0;
            var p = r.Split('/');
            double a, b = 1;
            if (!double.TryParse(p[0], NumberStyles.Float, Inv, out a)) return 0;
            if (p.Length > 1 && (!double.TryParse(p[1], NumberStyles.Float, Inv, out b) || b == 0)) return 0;
            return a / b;
        }

        // peak level of an audio track in dB (−∞ = silence); from/to — a source range or null
        public static double PeakDb(string file, int audioIndex, double? from, double? to, CancellationToken cancel)
        {
            string range = (from.HasValue ? "-ss " + T(from.Value) + " " : "") + (to.HasValue ? "-to " + T(to.Value) + " " : "");
            var r = Run("-nostats " + range + "-i " + Q(file) + " -map 0:a:" + audioIndex + " -af volumedetect -vn -sn -dn -f null NUL", cancel);
            var m = Regex.Match(r.Err, @"max_volume:\s*(-?[\d.]+|-inf) dB");
            if (!m.Success) return double.NegativeInfinity;
            return m.Groups[1].Value == "-inf" ? double.NegativeInfinity : double.Parse(m.Groups[1].Value, Inv);
        }

        // integrated loudness of an audio track, LUFS (EBU R128); NaN — not measured
        public static double Lufs(string file, int audioIndex, CancellationToken cancel)
        {
            var r = Run("-nostats -i " + Q(file) + " -map 0:a:" + audioIndex + " -af ebur128 -vn -sn -dn -f null NUL", cancel);
            return ParseLufs(r.Err);
        }

        // the summary ebur128 prints at the end: "Integrated loudness: I: -23.0 LUFS"
        public static double ParseLufs(string err)
        {
            var all = Regex.Matches(err ?? "", @"I:\s+(-?[\d.]+|-inf) LUFS");
            if (all.Count == 0) return double.NaN;
            string v = all[all.Count - 1].Groups[1].Value;
            return v == "-inf" ? double.NegativeInfinity : double.Parse(v, Inv);
        }

        // the video is readable: decode the start and end, decoder errors = a broken file
        public static string DecodeErrors(string file, double duration, CancellationToken cancel)
        {
            var head = Run("-v error -t 2 -i " + Q(file) + " -map 0:v:0 -f null NUL", cancel);
            var tail = Run("-v error -ss " + T(Math.Max(0, duration - 2)) + " -i " + Q(file) + " -map 0:v:0 -f null NUL", cancel);
            string err = (head.Err + tail.Err).Trim();
            return err.Length > 0 ? LastLine(err) : null;
        }

        public static bool HasEncoder(string name)
        {
            try
            {
                var r = Run("-v error -f lavfi -i color=c=black:s=256x144:d=0.1 -c:v " + name + " -f null NUL", CancellationToken.None);
                return r.Code == 0;
            }
            catch { return false; }
        }

        public static string LastLine(string s)
        {
            var lines = (s ?? "").Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            return lines.Length > 0 ? lines[lines.Length - 1].Trim() : "";
        }

        // ── pictures for the editor ──
        // filmstrip: count frames of height h joined into one picture
        public static bool Filmstrip(string file, double duration, int count, int h, string outPng, CancellationToken cancel)
        {
            double fps = count / Math.Max(1, duration);
            var r = Run("-v error -skip_frame nokey -i " + Q(file) + " -map 0:v:0 -vf \"fps=" + fps.ToString("0.#####", Inv) +
                        ",scale=-2:" + h + ",tile=" + count + "x1\" -frames:v 1 -update 1 " + Q(outPng), cancel);
            return r.Code == 0 && File.Exists(outPng);
        }

        // waveform of one audio track
        public static bool Waveform(string file, int audioIndex, int w, int h, string color, string outPng, CancellationToken cancel)
        {
            var r = Run("-v error -i " + Q(file) + " -filter_complex \"[0:a:" + audioIndex + "]aformat=channel_layouts=mono,dynaudnorm=f=250:g=15,showwavespic=s=" +
                        w + "x" + h + ":colors=" + color + ":scale=sqrt:draw=full\" -frames:v 1 -update 1 " + Q(outPng), cancel);
            return r.Code == 0 && File.Exists(outPng);
        }
    }
}
