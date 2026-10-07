using System;
using System.Collections.Generic;
using System.IO;
using System.Media;
using System.Text;

namespace DeviceGuard
{
    // Alarm and "Clip saved" sounds with volume control: SoundPlayer has no volume,
    // so WAV samples are multiplied by a factor right in memory. Built-in sounds are synthesized, the user's own are WAV files.
    // Windows plays one such sound at a time per program: a new one cuts the previous one off.
    static class Alarm
    {
        static SoundPlayer preview, onceAlarm, onceClip;

        // Windows plays one sound per program: a new one cuts off the one playing. The clip sound waits out a fresh alarm
        public static DateTime AlarmAt;

        // a quadratic scale is closer to how we hear: 50% ≈ −12 dB
        public static double Gain(int volume)
        {
            double v = Math.Max(0, Math.Min(100, volume)) / 100.0;
            return v * v;
        }

        // ── built-in sounds: "builtin:<id>" in the settings instead of a file path ──
        public const string Builtin = "builtin:";
        public static readonly string[] ClipSounds = { "marimba", "glass", "shutter", "drop" };
        public static readonly string[] AlarmSounds = { "sweep", "motif", "monitor", "bell" };
        public const string DefaultClip = Builtin + "marimba", DefaultAlarm = Builtin + "sweep";

        public static bool IsBuiltin(string file) { return file != null && file.StartsWith(Builtin, StringComparison.Ordinal); }

        public static string Name(string id)
        {
            switch (id)
            {
                case "marimba": return L.T("Marimba", "Маримба");
                case "glass": return L.T("Glass", "Стекло");
                case "shutter": return L.T("Shutter", "Затвор");
                case "drop": return L.T("Drop", "Капля");
                case "sweep": return L.T("Fall", "Спад");
                case "motif": return L.T("Motif", "Мотив");
                case "monitor": return L.T("Monitor", "Монитор");
                case "bell": return L.T("Bell", "Колокол");
            }
            return id;
        }

        // the built-in sound that plays for this setting: its own, or the default when the file is gone or unreadable
        static string BuiltinId(string file, bool chime)
        {
            if (IsBuiltin(file))
            {
                string id = file.Substring(Builtin.Length);
                if (Array.IndexOf(chime ? ClipSounds : AlarmSounds, id) >= 0) return id;
            }
            return (chime ? DefaultClip : DefaultAlarm).Substring(Builtin.Length);
        }

        // ── the user's own sounds: copied into the sounds folder next to settings.json ──
        public static string Folder { get { return Path.Combine(Program.Dir, "sounds"); } }

        public static string[] Uploaded()
        {
            try
            {
                if (!Directory.Exists(Folder)) return new string[0];
                var files = Directory.GetFiles(Folder, "*.wav");
                Array.Sort(files, StringComparer.OrdinalIgnoreCase);
                return files;
            }
            catch { return new string[0]; }
        }

        public static bool InFolder(string file)
        {
            return !string.IsNullOrEmpty(file) && !IsBuiltin(file) &&
                   string.Equals(Path.GetDirectoryName(Path.GetFullPath(file)).TrimEnd('\\'), Folder.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
        }

        // any audio file → a WAV in the sounds folder: ffmpeg converts mp3/ogg/m4a… and cuts it to MaxSec;
        // without ffmpeg only a WAV we can play is taken as is. Returns the new path, or null and the reason
        public const int MaxSec = 10;
        public static string Import(string src, out string error)
        {
            error = null;
            try
            {
                Directory.CreateDirectory(Folder);
                string dst = Path.Combine(Folder, Path.GetFileNameWithoutExtension(src) + ".wav");
                if (string.Equals(Path.GetFullPath(src), Path.GetFullPath(dst), StringComparison.OrdinalIgnoreCase)) return dst;
                if (Ffmpeg.Available)
                {
                    string tmp = dst + ".tmp.wav";
                    var r = Ffmpeg.Run("-i \"" + src + "\" -vn -t " + MaxSec + " -ar 44100 -c:a pcm_s16le \"" + tmp + "\"", System.Threading.CancellationToken.None);
                    if (r.Code != 0 || !File.Exists(tmp) || Scale(File.ReadAllBytes(tmp), 1) == null)
                    {
                        try { File.Delete(tmp); } catch { }
                        error = L.T("ffmpeg could not read the sound from this file", "ffmpeg не смог прочитать звук из этого файла");
                        Log.Write("sound import " + src + ": " + Ffmpeg.LastLine(r.Err));
                        return null;
                    }
                    if (File.Exists(dst)) File.Delete(dst);
                    File.Move(tmp, dst);
                }
                else
                {
                    if (Scale(File.ReadAllBytes(src), 1) == null)
                    {
                        error = L.T("This WAV format is not supported, and without ffmpeg other formats can't be converted",
                                    "Такой формат WAV не поддерживается, а без ffmpeg другие форматы не перевести");
                        return null;
                    }
                    File.Copy(src, dst, true);
                }
                Log.Write("sound imported: " + Path.GetFileName(dst));
                return dst;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                Log.Write("sound import " + src + ": " + ex.Message);
                return null;
            }
        }

        // null — volume 0 (silence). chime: the "Clip saved" sound — its built-in fallback is a short note, not the alarm
        public static SoundPlayer Create(string file, int volume, bool chime = false)
        {
            if (volume <= 0) return null;
            double g = Gain(volume);
            byte[] wav = null;
            try
            {
                if (!IsBuiltin(file) && !string.IsNullOrEmpty(file) && File.Exists(file)) wav = Scale(File.ReadAllBytes(file), g);
            }
            catch { wav = null; }
            if (wav == null) wav = Wav(Synth(BuiltinId(file, chime)), g);   // built-in, or no file / unsupported format
            var p = new SoundPlayer(new MemoryStream(wav));
            p.Load();
            return p;
        }

        public static void Preview(string file, int volume, bool chime = false)
        {
            if (preview != null) { preview.Stop(); preview.Dispose(); }
            preview = Create(file, volume, chime);
            if (preview != null) preview.Play();
        }

        // plays without waiting; the player is kept until the next one so it is not collected mid-sound
        public static void PlayOnce(string file, int volume, bool chime)
        {
            try
            {
                var p = Create(file, volume, chime);
                if (p == null) return;
                if (chime)
                {
                    if (onceClip != null) onceClip.Dispose();
                    onceClip = p;
                }
                else
                {
                    if (onceAlarm != null) onceAlarm.Dispose();
                    onceAlarm = p;
                    AlarmAt = DateTime.Now;
                }
                p.Play();
            }
            catch (Exception ex) { Log.Write((chime ? "clip" : "alarm") + " sound: " + ex.Message); }
        }

        public static void StopPreview()
        {
            if (preview != null) preview.Stop();
        }

        static string Id(byte[] b, int pos) { return Encoding.ASCII.GetString(b, pos, 4); }

        public static byte[] Scale(byte[] src, double g)
        {
            if (src.Length < 44 || Id(src, 0) != "RIFF" || Id(src, 8) != "WAVE") return null;
            var b = (byte[])src.Clone();
            int tag = 0, bits = 0, pos = 12;
            bool scaled = false;
            while (pos + 8 <= b.Length)
            {
                string id = Id(b, pos);
                int size = BitConverter.ToInt32(b, pos + 4);
                int data = pos + 8;
                if (size < 0 || data + size > b.Length) size = b.Length - data;
                if (id == "fmt " && size >= 16)
                {
                    tag = BitConverter.ToUInt16(b, data);
                    bits = BitConverter.ToUInt16(b, data + 14);
                    if (tag == 0xFFFE && size >= 26) tag = BitConverter.ToUInt16(b, data + 24);   // WAVE_FORMAT_EXTENSIBLE
                }
                else if (id == "data")
                {
                    if (!ScaleData(b, data, size, tag, bits, g)) return null;
                    scaled = true;
                }
                pos = data + size + (size & 1);
            }
            return scaled ? b : null;
        }

        static int Clamp(double v, int min, int max)
        {
            return v < min ? min : v > max ? max : (int)Math.Round(v);
        }

        static bool ScaleData(byte[] b, int off, int len, int tag, int bits, double g)
        {
            int end = off + len;
            if (tag == 1 && bits == 16)
            {
                for (int i = off; i + 1 < end; i += 2)
                {
                    int s = Clamp((short)(b[i] | b[i + 1] << 8) * g, short.MinValue, short.MaxValue);
                    b[i] = (byte)s;
                    b[i + 1] = (byte)(s >> 8);
                }
            }
            else if (tag == 1 && bits == 8)
            {
                for (int i = off; i < end; i++) b[i] = (byte)Clamp((b[i] - 128) * g + 128, 0, 255);
            }
            else if (tag == 1 && bits == 24)
            {
                for (int i = off; i + 2 < end; i += 3)
                {
                    int s = Clamp((b[i] | b[i + 1] << 8 | (sbyte)b[i + 2] << 16) * g, -8388608, 8388607);
                    b[i] = (byte)s;
                    b[i + 1] = (byte)(s >> 8);
                    b[i + 2] = (byte)(s >> 16);
                }
            }
            else if (tag == 1 && bits == 32)
            {
                for (int i = off; i + 3 < end; i += 4)
                {
                    long v = (long)Math.Round(BitConverter.ToInt32(b, i) * g);
                    int s = (int)Math.Max(int.MinValue, Math.Min(int.MaxValue, v));
                    Buffer.BlockCopy(BitConverter.GetBytes(s), 0, b, i, 4);
                }
            }
            else if (tag == 3 && bits == 32)
            {
                for (int i = off; i + 3 < end; i += 4)
                    Buffer.BlockCopy(BitConverter.GetBytes((float)(BitConverter.ToSingle(b, i) * g)), 0, b, i, 4);
            }
            else return false;
            return true;
        }

        // ── the built-in sounds are synthesized here: a few instruments and a small room ──
        const int Rate = 44100;
        const double Tau = 2 * Math.PI;
        static readonly Dictionary<string, double[]> synthCache = new Dictionary<string, double[]>();

        // samples −1…1 of a built-in sound, rendered once
        static double[] Synth(string id)
        {
            lock (synthCache)
            {
                double[] s;
                if (synthCache.TryGetValue(id, out s)) return s;
                switch (id)
                {
                    case "glass": s = ClipGlass(); break;
                    case "shutter": s = ClipShutter(); break;
                    case "drop": s = ClipDrop(); break;
                    case "motif": s = AlarmMotif(); break;
                    case "monitor": s = AlarmMonitor(); break;
                    case "bell": s = AlarmBell(); break;
                    case "sweep": s = AlarmSweep(); break;
                    default: s = ClipMarimba(); break;
                }
                synthCache[id] = s;
                return s;
            }
        }

        // a 16 bit mono WAV of the samples at gain g
        static byte[] Wav(double[] s, double g)
        {
            var ms = new MemoryStream();
            var w = new BinaryWriter(ms);
            w.Write(Encoding.ASCII.GetBytes("RIFF")); w.Write(36 + s.Length * 2);
            w.Write(Encoding.ASCII.GetBytes("WAVEfmt ")); w.Write(16); w.Write((short)1); w.Write((short)1);
            w.Write(Rate); w.Write(Rate * 2); w.Write((short)2); w.Write((short)16);
            w.Write(Encoding.ASCII.GetBytes("data")); w.Write(s.Length * 2);
            foreach (double v in s) w.Write((short)(Math.Max(-1, Math.Min(1, v * g)) * short.MaxValue));
            w.Flush();
            return ms.ToArray();
        }

        static double[] Buf(double sec) { return new double[(int)(Rate * sec)]; }

        // adds f(x) from start for dur seconds; x — seconds since the voice started
        static void Add(double[] b, double start, double dur, Func<double, double> f)
        {
            int s = (int)(start * Rate), n = (int)(dur * Rate);
            for (int i = 0; i < n && s + i < b.Length; i++) b[s + i] += f((double)i / Rate);
        }

        static double Env(double x, double attack, double decay)
        {
            return x < attack ? x / attack : Math.Exp(-(x - attack) * decay);
        }

        // a struck note; partials as {ratio, amp, decay}
        static void Strike(double[] b, double start, double f, double amp, double[,] partials, double dur)
        {
            Add(b, start, dur, x =>
            {
                double v = 0;
                for (int p = 0; p < partials.GetLength(0); p++)
                    v += Math.Sin(Tau * f * partials[p, 0] * x) * partials[p, 1] * Env(x, 0.0015, partials[p, 2]);
                return v * amp;
            });
        }

        // an FM bell: inharmonic, glassy; the brightness fades faster than the note
        static void Bell(double[] b, double start, double f, double amp, double ratio, double index, double decay, double dur)
        {
            Add(b, start, dur, x => Math.Sin(Tau * f * x + index * Math.Exp(-x * decay * 1.5) * Math.Sin(Tau * f * ratio * x)) * Env(x, 0.002, decay) * amp);
        }

        // a held tone with harmonics 1…n falling off as 1/k^rolloff; a 30 ms release after len
        static void Tone(double[] b, double start, double f, double amp, double len, int harmonics, double rolloff)
        {
            Add(b, start, len + 0.03, x =>
            {
                double e = x < 0.006 ? x / 0.006 : x < len ? 1 - (x - 0.006) / len * 0.35 : Math.Max(0, 0.65 * (1 - (x - len) / 0.03));
                double v = 0;
                for (int k = 1; k <= harmonics; k++) v += Math.Sin(Tau * f * k * x) / Math.Pow(k, rolloff);
                return v * e * amp;
            });
        }

        static uint seed;
        static double Noise() { seed = seed * 1664525 + 1013904223; return (seed >> 8) / 8388608.0 - 1; }

        static void LowPass(double[] b, double cutoff)
        {
            double a = 1 - Math.Exp(-Tau * cutoff / Rate), y = 0;
            for (int i = 0; i < b.Length; i++) { y += a * (b[i] - y); b[i] = y; }
        }

        static void HighPass(double[] b, double cutoff)
        {
            double a = 1 - Math.Exp(-Tau * cutoff / Rate), y = 0;
            for (int i = 0; i < b.Length; i++) { y += a * (b[i] - y); b[i] -= y; }
        }

        // a small room: four combs and two allpasses (a shortened Freeverb)
        static void Reverb(double[] b, double wet, double feedback)
        {
            int[] combs = { 1116, 1188, 1277, 1356 }, alls = { 556, 441 };
            var o = new double[b.Length];
            foreach (int d in combs)
            {
                var line = new double[d];
                double lp = 0;
                for (int i = 0, p = 0; i < b.Length; i++, p = (p + 1) % d)
                {
                    double y = line[p];
                    lp = y * 0.7 + lp * 0.3;
                    line[p] = b[i] + lp * feedback;
                    o[i] += y / combs.Length;
                }
            }
            foreach (int d in alls)
            {
                var line = new double[d];
                for (int i = 0, p = 0; i < b.Length; i++, p = (p + 1) % d)
                {
                    double y = line[p];
                    line[p] = o[i] + y * 0.5;
                    o[i] = y - o[i] * 0.5;
                }
            }
            for (int i = 0; i < b.Length; i++) b[i] = b[i] * (1 - wet * 0.5) + o[i] * wet;
        }

        // to the given peak, with a 20 ms fade at the end
        static double[] Finish(double[] b, double peak)
        {
            double m = 0;
            foreach (double v in b) m = Math.Max(m, Math.Abs(v));
            int fade = Rate / 50;
            for (int i = 0; i < b.Length; i++)
            {
                b[i] = b[i] / m * peak;
                int left = b.Length - 1 - i;
                if (left < fade) b[i] *= (double)left / fade;
            }
            return b;
        }

        // "Clip saved": short, soft and rising — nothing like the alarm
        static double[] ClipMarimba()
        {
            var b = Buf(0.7);
            var mar = new double[,] { { 1, 1, 11 }, { 3.93, 0.3, 45 }, { 9.2, 0.08, 90 } };
            Strike(b, 0, 587.3, 0.6, mar, 0.6);       // D5
            Strike(b, 0.08, 880, 0.65, mar, 0.6);     // A5
            Reverb(b, 0.2, 0.75);
            return Finish(b, 0.8);
        }

        static double[] ClipGlass()
        {
            var b = Buf(0.9);
            Bell(b, 0, 1046.5, 0.5, 3.5, 1.2, 7, 0.8);       // C6
            Bell(b, 0.075, 1568, 0.55, 3.5, 1.0, 6, 0.8);    // G6
            Reverb(b, 0.35, 0.8);
            return Finish(b, 0.8);
        }

        // a camera shutter: two filtered clicks with a thump, and a faint glint after them
        static double[] ClipShutter()
        {
            var b = Buf(0.45);
            seed = 12345;
            Add(b, 0, 0.018, x => Noise() * Env(x, 0.0005, 260));
            Add(b, 0.062, 0.025, x => Noise() * 0.8 * Env(x, 0.0005, 200));
            HighPass(b, 1500);
            LowPass(b, 6000);
            Add(b, 0, 0.06, x => Math.Sin(Tau * 160 * x) * Env(x, 0.001, 70) * 0.5);
            Add(b, 0.062, 0.06, x => Math.Sin(Tau * 130 * x) * Env(x, 0.001, 70) * 0.4);
            Bell(b, 0.07, 2093, 0.12, 2, 0.4, 14, 0.35);
            Reverb(b, 0.18, 0.7);
            return Finish(b, 0.75);
        }

        // two drops: a sine gliding up from f0 to f1
        static double[] ClipDrop()
        {
            var b = Buf(0.5);
            Func<double, double, double, double, Func<double, double>> drop = (f0, f1, glide, decay) =>
            {
                double phase = 0;
                return x =>
                {
                    phase += Tau * (f1 + (f0 - f1) * Math.Exp(-x / glide)) / Rate;
                    return Math.Sin(phase) * Env(x, 0.002, decay);
                };
            };
            Add(b, 0, 0.4, drop(420, 1250, 0.018, 16));
            var second = drop(620, 1650, 0.015, 20);
            Add(b, 0.085, 0.35, x => second(x) * 0.55);
            Reverb(b, 0.22, 0.72);
            return Finish(b, 0.8);
        }

        // the alarm: it must cut through the game sound. Three falling tones, like a siren
        static double[] AlarmSweep()
        {
            var b = Buf(1.45);
            for (int g = 0; g < 3; g++)
            {
                double phase = 0;
                Add(b, g * 0.46, 0.38, x =>
                {
                    phase += Tau * (1150 - 450 * (x / 0.38)) / Rate;
                    double e = Math.Min(1, x / 0.006) * Math.Min(1, (0.38 - x) / 0.04);
                    return (Math.Sin(phase) + Math.Sin(2 * phase) * 0.35 + Math.Sin(3 * phase) * 0.18) * e * 0.45;
                });
            }
            LowPass(b, 5000);
            Reverb(b, 0.12, 0.65);
            return Finish(b, 0.85);
        }

        // three notes down, twice: C6 A5 F5
        static double[] AlarmMotif()
        {
            var b = Buf(1.55);
            double[] notes = { 1046.5, 880, 698.5 };
            for (int g = 0; g < 2; g++)
                for (int k = 0; k < 3; k++)
                    Tone(b, g * 0.62 + k * 0.15, notes[k], 0.5, 0.11, 7, 1.3);
            LowPass(b, 5000);
            Reverb(b, 0.15, 0.7);
            return Finish(b, 0.85);
        }

        // the IEC 60601-1-8 high priority pattern of medical monitors: C E G — G C', twice
        static double[] AlarmMonitor()
        {
            var b = Buf(1.75);
            double[] f = { 523.25, 659.25, 784, 784, 1046.5 }, at = { 0, 0.12, 0.24, 0.48, 0.60 };
            for (int g = 0; g < 2; g++)
                for (int k = 0; k < 5; k++)
                    Tone(b, g * 0.92 + at[k], f[k], 0.5, 0.075, 6, 1.1);
            LowPass(b, 5500);
            Reverb(b, 0.12, 0.65);
            return Finish(b, 0.85);
        }

        // four bell strikes a tritone apart: E6 and A#5
        static double[] AlarmBell()
        {
            var b = Buf(1.7);
            double[] f = { 1318.5, 932.3, 1318.5, 932.3 };
            for (int k = 0; k < 4; k++) Bell(b, k * 0.2, f[k], 0.5, 1.4, 2.2, 5, 1.2);
            Reverb(b, 0.25, 0.78);
            return Finish(b, 0.85);
        }
    }
}
