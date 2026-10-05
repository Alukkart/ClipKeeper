using System.Threading;

namespace DeviceGuard
{
    // Video encoders for re-encoding (frame-exact trims, cuts, sharing, the smooth preview copy):
    // NVIDIA NVENC, AMD AMF, Intel Quick Sync, or the CPU. "auto" picks the first hardware encoder
    // that really works on this PC (a one-frame test encode); every hardware encode still falls back to the CPU.
    static class Encoders
    {
        public const string Auto = "auto", Nvenc = "nvenc", Amf = "amf", Qsv = "qsv", Cpu = "cpu";
        public static readonly string[] All = { Auto, Nvenc, Amf, Qsv, Cpu };

        public static string Setting = Auto;   // from settings.json
        static string detected;
        static readonly object Sync = new object();

        // slow the first time (up to a few seconds) — call it off the UI thread
        public static string Detected
        {
            get
            {
                lock (Sync)
                {
                    if (detected == null) detected = Detect();
                    return detected;
                }
            }
        }

        public static bool IsDetected { get { lock (Sync) return detected != null; } }

        public static string Active { get { return Setting == Auto ? Detected : Setting; } }

        public static bool Hardware { get { return Active != Cpu; } }

        static string Detect()
        {
            if (!Ffmpeg.Available) return Cpu;
            foreach (var k in new[] { Nvenc, Amf, Qsv })
            {
                var r = Ffmpeg.Run("-v error -f lavfi -i color=c=black:s=256x256:r=30 -frames:v 3 -c:v h264_" + k + " -f null -", CancellationToken.None);
                if (r.Code == 0) { Log.Write("video encoder: " + k); return k; }
            }
            Log.Write("video encoder: no hardware encoder, using the CPU");
            return Cpu;
        }

        public static string Name(string k)
        {
            switch (k)
            {
                case Nvenc: return "NVIDIA NVENC";
                case Amf: return "AMD AMF";
                case Qsv: return "Intel Quick Sync";
                case Cpu: return L.T("CPU", "Процессор");
                default: return L.T("Auto", "Авто");
            }
        }

        static string Hw(string codec, string k)
        {
            return (codec == "h264" ? "h264_" : codec == "av1" ? "av1_" : "hevc_") + k;
        }

        // constant quality: q 18 is visually close to the source; null — use the CPU
        public static string Quality(string codec, int q)
        {
            switch (Active)
            {
                case Nvenc: return "-c:v " + Hw(codec, Nvenc) + " -preset p5 -tune hq -rc constqp -qp " + q;
                case Amf: return "-c:v " + Hw(codec, Amf) + " -quality quality -rc cqp -qp_i " + q + " -qp_p " + q + " -qp_b " + q;
                case Qsv: return "-c:v " + Hw(codec, Qsv) + " -preset slower -global_quality " + q;
                default: return null;
            }
        }

        public static string CpuQuality(string codec, int q)
        {
            return codec == "h264" ? "-c:v libx264 -crf " + q + " -preset fast" : "-c:v libx265 -crf " + q + " -preset fast";
        }

        // H.264 at a target bitrate (sharing); null — use the CPU
        public static string Bitrate(string rate, string buf)
        {
            string limits = " -b:v " + rate + " -maxrate " + rate + " -bufsize " + buf;
            switch (Active)
            {
                case Nvenc: return "-c:v h264_nvenc -preset p5 -rc vbr" + limits;
                case Amf: return "-c:v h264_amf -quality quality -rc vbr_peak" + limits;
                case Qsv: return "-c:v h264_qsv -preset slower" + limits;
                default: return null;
            }
        }

        // the light preview copy: speed matters more than quality; null — use the CPU
        public static string Fast()
        {
            switch (Active)
            {
                case Nvenc: return "-c:v h264_nvenc -preset p1 -rc vbr -b:v 8M -maxrate 12M -bufsize 16M";
                case Amf: return "-c:v h264_amf -quality speed -rc vbr_peak -b:v 8M -maxrate 12M -bufsize 16M";
                case Qsv: return "-c:v h264_qsv -preset veryfast -b:v 8M -maxrate 12M -bufsize 16M";
                default: return null;
            }
        }
    }
}
