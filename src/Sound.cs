using System;
using System.IO;
using System.Media;
using System.Text;

namespace DeviceGuard
{
    // Alarm and "Clip saved" sounds with volume control: SoundPlayer has no volume,
    // so WAV samples are multiplied by a factor right in memory.
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

        // null — volume 0 (silence). chime: without a file — a short soft "ding" (clip saved) instead of the alarm beeps
        public static SoundPlayer Create(string file, int volume, bool chime = false)
        {
            if (volume <= 0) return null;
            double g = Gain(volume);
            byte[] wav = null;
            try
            {
                if (!string.IsNullOrEmpty(file) && File.Exists(file)) wav = Scale(File.ReadAllBytes(file), g);
            }
            catch { wav = null; }
            if (wav == null) wav = chime ? Chime(g) : Beep(g);   // no file or unsupported format — our own sound
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

        const int Rate = 44100;

        // a 16 bit mono WAV of the given length, each sample from wave(t)
        static byte[] Wav(double seconds, Func<double, double> wave)
        {
            var ms = new MemoryStream();
            var w = new BinaryWriter(ms);
            int samples = (int)(Rate * seconds);
            w.Write(Encoding.ASCII.GetBytes("RIFF")); w.Write(36 + samples * 2);
            w.Write(Encoding.ASCII.GetBytes("WAVEfmt ")); w.Write(16); w.Write((short)1); w.Write((short)1);
            w.Write(Rate); w.Write(Rate * 2); w.Write((short)2); w.Write((short)16);
            w.Write(Encoding.ASCII.GetBytes("data")); w.Write(samples * 2);
            for (int i = 0; i < samples; i++) w.Write((short)(Math.Max(-1, Math.Min(1, wave((double)i / Rate))) * short.MaxValue));
            w.Flush();
            return ms.ToArray();
        }

        // fallback alarm: three short two-tone beeps
        static byte[] Beep(double g)
        {
            return Wav(1.2, t =>
            {
                double slot = t % 0.4;
                if (slot >= 0.25) return 0;
                double f = slot < 0.125 ? 880 : 660;
                double env = Math.Min(1, Math.Min(slot, 0.25 - slot) * 200);   // no clicks
                return Math.Sin(2 * Math.PI * f * t) * env * 0.7 * g;
            });
        }

        // fallback "clip saved": two quick rising notes that ring out, soft and short — nothing like the alarm
        static byte[] Chime(double g)
        {
            Func<double, double, double, double> note = (t, start, f) =>
            {
                double x = t - start;
                if (x < 0) return 0;
                return Math.Sin(2 * Math.PI * f * x) * Math.Min(1, x * 400) * Math.Exp(-x * 9);
            };
            return Wav(0.5, t => (note(t, 0, 988) * 0.45 + note(t, 0.09, 1319) * 0.5) * g);   // B5, then E6
        }
    }
}
