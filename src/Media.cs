using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;

namespace DeviceGuard
{
    class Mp4Info
    {
        public double Duration = -1;   // seconds, -1 — could not be determined
        public int Video, Audio;
    }

    // Reads only the MP4 header (moov): length and the number of video/audio tracks. The video stream itself is not touched.
    static class Mp4
    {
        public static Mp4Info Read(string path)
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                long pos = 0, len = fs.Length;
                var hdr = new byte[16];
                while (pos + 8 <= len)
                {
                    fs.Position = pos;
                    if (fs.Read(hdr, 0, 16) < 8) break;
                    long size = U32(hdr, 0);
                    string type = System.Text.Encoding.ASCII.GetString(hdr, 4, 4);
                    int h = 8;
                    if (size == 1) { size = (long)U64(hdr, 8); h = 16; }
                    else if (size == 0) size = len - pos;
                    if (size < h) break;
                    if (type == "moov")
                    {
                        if (size > 256L * 1024 * 1024) return null;
                        var buf = new byte[size - h];
                        fs.Position = pos + h;
                        int read = 0;
                        while (read < buf.Length)
                        {
                            int n = fs.Read(buf, read, buf.Length - read);
                            if (n <= 0) return null;
                            read += n;
                        }
                        return ParseMoov(buf);
                    }
                    pos += size;
                }
            }
            return null;
        }

        static uint U32(byte[] b, int o) { return (uint)(b[o] << 24 | b[o + 1] << 16 | b[o + 2] << 8 | b[o + 3]); }
        static ulong U64(byte[] b, int o) { return (ulong)U32(b, o) << 32 | U32(b, o + 4); }

        // child boxes: (type, content start, end)
        static IEnumerable<Tuple<string, int, int>> Boxes(byte[] b, int start, int end)
        {
            int pos = start;
            while (pos + 8 <= end)
            {
                long size = U32(b, pos);
                string type = System.Text.Encoding.ASCII.GetString(b, pos + 4, 4);
                int h = 8;
                if (size == 1 && pos + 16 <= end) { size = (long)U64(b, pos + 8); h = 16; }
                else if (size == 0) size = end - pos;
                if (size < h || pos + size > end) yield break;
                yield return Tuple.Create(type, pos + h, (int)(pos + size));
                pos += (int)size;
            }
        }

        // mvhd/mdhd: (timescale, duration)
        static void TimeBox(byte[] b, int o, out uint scale, out ulong dur)
        {
            if (b[o] == 1) { scale = U32(b, o + 20); dur = U64(b, o + 24); }
            else { scale = U32(b, o + 12); dur = U32(b, o + 16); }
        }

        static Mp4Info ParseMoov(byte[] b)
        {
            var info = new Mp4Info();
            double movie = 0, fragment = 0, longestTrack = 0;
            uint movieScale = 0;
            foreach (var box in Boxes(b, 0, b.Length))
            {
                if (box.Item1 == "mvhd")
                {
                    ulong d;
                    TimeBox(b, box.Item2, out movieScale, out d);
                    if (movieScale > 0) movie = (double)d / movieScale;
                }
                else if (box.Item1 == "mvex")
                {
                    foreach (var x in Boxes(b, box.Item2, box.Item3))
                        if (x.Item1 == "mehd" && movieScale > 0)
                            fragment = (b[x.Item2] == 1 ? (double)U64(b, x.Item2 + 4) : U32(b, x.Item2 + 4)) / movieScale;
                }
                else if (box.Item1 == "trak")
                {
                    foreach (var t in Boxes(b, box.Item2, box.Item3))
                    {
                        if (t.Item1 != "mdia") continue;
                        foreach (var m in Boxes(b, t.Item2, t.Item3))
                        {
                            if (m.Item1 == "hdlr")
                            {
                                string handler = System.Text.Encoding.ASCII.GetString(b, m.Item2 + 8, 4);
                                if (handler == "vide") info.Video++;
                                else if (handler == "soun") info.Audio++;
                            }
                            else if (m.Item1 == "mdhd")
                            {
                                uint s;
                                ulong d;
                                TimeBox(b, m.Item2, out s, out d);
                                if (s > 0) longestTrack = Math.Max(longestTrack, (double)d / s);
                            }
                        }
                    }
                }
            }
            info.Duration = movie > 0 ? movie : fragment > 0 ? fragment : longestTrack > 0 ? longestTrack : -1;
            return info;
        }
    }

    // Monitors straight from Windows. Asking OBS for the monitor list blanks screen capture for 1–2 frames,
    // so the regular check uses only this list.
    static class Monitors
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct DISPLAY_DEVICE
        {
            public int cb;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
            public int StateFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern bool EnumDisplayDevices(string device, uint devNum, ref DISPLAY_DEVICE dd, uint flags);

        // active monitor paths in the same form as monitor_id in OBS: \\?\DISPLAY#MSI4CE2#...#{e6f07b5f-...}
        public static List<string> ActiveIds()
        {
            var res = new List<string>();
            for (uint i = 0; ; i++)
            {
                var ad = new DISPLAY_DEVICE();
                ad.cb = Marshal.SizeOf(ad);
                if (!EnumDisplayDevices(null, i, ref ad, 0)) break;
                if ((ad.StateFlags & 1) == 0) continue;   // the adapter is not attached to the desktop
                for (uint j = 0; ; j++)
                {
                    var md = new DISPLAY_DEVICE();
                    md.cb = Marshal.SizeOf(md);
                    if (!EnumDisplayDevices(ad.DeviceName, j, ref md, 1 /* EDD_GET_DEVICE_INTERFACE_NAME */)) break;
                    if ((md.StateFlags & 1) != 0 && !string.IsNullOrEmpty(md.DeviceID)) res.Add(md.DeviceID);
                }
            }
            return res;
        }
    }

    static class Pixels
    {
        public const string Black = "black", White = "white", Uniform = "uniform";

        // what OBS records, for messages: "black screen" / "чёрный экран"
        public static string ScreenText(string kind)
        {
            return kind == Black ? L.T("a black screen", "чёрный экран")
                 : kind == White ? L.T("a white screen", "белый экран")
                 : L.T("a single-color screen", "одноцветный экран");
        }

        // null — the picture is fine; otherwise Black / White / Uniform
        public static string UniformKind(string dataUrl)
        {
            if (string.IsNullOrEmpty(dataUrl)) return null;
            int comma = dataUrl.IndexOf(',');
            var bytes = Convert.FromBase64String(comma >= 0 ? dataUrl.Substring(comma + 1) : dataUrl);
            using (var ms = new MemoryStream(bytes))
            using (var bmp = new Bitmap(ms))
            {
                int minR = 255, minG = 255, minB = 255, maxR = 0, maxG = 0, maxB = 0;
                long lum = 0;
                for (int y = 0; y < bmp.Height; y++)
                    for (int x = 0; x < bmp.Width; x++)
                    {
                        var c = bmp.GetPixel(x, y);
                        int r = c.R * c.A / 255, g = c.G * c.A / 255, bl = c.B * c.A / 255;   // transparent = black
                        minR = Math.Min(minR, r); maxR = Math.Max(maxR, r);
                        minG = Math.Min(minG, g); maxG = Math.Max(maxG, g);
                        minB = Math.Min(minB, bl); maxB = Math.Max(maxB, bl);
                        lum += (r * 299 + g * 587 + bl * 114) / 1000;
                    }
                const int tolerance = 10;   // nearly identical pixels: a game with a HUD never looks like this
                if (maxR - minR > tolerance || maxG - minG > tolerance || maxB - minB > tolerance) return null;
                double avg = (double)lum / (bmp.Width * bmp.Height);
                return avg < 24 ? Black : avg > 232 ? White : Uniform;
            }
        }

        public static string Kind(Bitmap bmp)
        {
            int minR = 255, minG = 255, minB = 255, maxR = 0, maxG = 0, maxB = 0;
            long lum = 0;
            for (int y = 0; y < bmp.Height; y++)
                for (int x = 0; x < bmp.Width; x++)
                {
                    var c = bmp.GetPixel(x, y);
                    minR = Math.Min(minR, c.R); maxR = Math.Max(maxR, c.R);
                    minG = Math.Min(minG, c.G); maxG = Math.Max(maxG, c.G);
                    minB = Math.Min(minB, c.B); maxB = Math.Max(maxB, c.B);
                    lum += (c.R * 299 + c.G * 587 + c.B * 114) / 1000;
                }
            if (maxR - minR > 10 || maxG - minG > 10 || maxB - minB > 10) return null;
            double avg = (double)lum / (bmp.Width * bmp.Height);
            return avg < 24 ? Black : avg > 232 ? White : Uniform;
        }

        // what is really on the monitor now (a 64×36 copy, in memory, never saved)
        // bounds come from the OBS monitor name: "MSI MAG 275QF: 2560x1440 @ 0,0"
        public static string ScreenKind(string monitorName)
        {
            var m = System.Text.RegularExpressions.Regex.Match(monitorName ?? "", @"(\d+)\s*x\s*(\d+)\s*@\s*(-?\d+)\s*,\s*(-?\d+)");
            if (!m.Success) return "?";
            int w = int.Parse(m.Groups[1].Value), h = int.Parse(m.Groups[2].Value);
            int x0 = int.Parse(m.Groups[3].Value), y0 = int.Parse(m.Groups[4].Value);
            using (var bmp = new Bitmap(64, 36))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    IntPtr dst = g.GetHdc();
                    IntPtr src = GetDC(IntPtr.Zero);
                    try
                    {
                        SetStretchBltMode(dst, 3 /* COLORONCOLOR */);
                        if (!StretchBlt(dst, 0, 0, 64, 36, src, x0, y0, w, h, 0x00CC0020 /* SRCCOPY */)) return "?";
                    }
                    finally
                    {
                        ReleaseDC(IntPtr.Zero, src);
                        g.ReleaseHdc(dst);
                    }
                }
                return Kind(bmp);
            }
        }

        [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hwnd);
        [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
        [DllImport("gdi32.dll")] static extern int SetStretchBltMode(IntPtr hdc, int mode);
        [DllImport("gdi32.dll")]
        static extern bool StretchBlt(IntPtr dst, int xd, int yd, int wd, int hd, IntPtr src, int xs, int ys, int ws, int hs, uint rop);

        [StructLayout(LayoutKind.Sequential)]
        struct LASTINPUTINFO { public int cbSize; public uint dwTime; }

        [DllImport("user32.dll")]
        static extern bool GetLastInputInfo(ref LASTINPUTINFO info);

        // how many seconds the user has not touched anything (mouse/keyboard/gamepad via Windows input)
        public static double IdleSeconds()
        {
            var li = new LASTINPUTINFO { cbSize = Marshal.SizeOf(typeof(LASTINPUTINFO)) };
            if (!GetLastInputInfo(ref li)) return 0;
            return unchecked((uint)Environment.TickCount - li.dwTime) / 1000.0;
        }
    }
}
