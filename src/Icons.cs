using System;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DeviceGuard
{
    // The ClipKeeper icon — "a shield with a play button": guards the recording and keeps clips.
    // The exe icon, the logo in the window and the colored status icon in the tray.
    static class Icons
    {
        const string ShieldPath = "M20,2.5 L35,8.5 V19.5 C35,29 28.5,35 20,38 C11.5,35 5,29 5,19.5 V8.5 Z";
        const string PlayPath = "M16.5,13.2 L27.2,19.8 L16.5,26.4 Z";

        // a triangle with rounded corners (stroked in the same color)
        static void DrawPlay(DrawingContext dc, Brush b, double stroke)
        {
            dc.DrawGeometry(b, new Pen(b, stroke) { LineJoin = PenLineJoin.Round }, Geometry.Parse(PlayPath));
        }

        public static Brush AppBrush()
        {
            var b = new SolidColorBrush(Wpf.C("#EDEDEF"));
            b.Freeze();
            return b;
        }

        // the program icon (exe, taskbar): a white shield on a graphite tile
        public static BitmapSource RenderApp(int size)
        {
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                double k = size / 40.0;
                dc.DrawRoundedRectangle(new SolidColorBrush(Wpf.C("#16171A")), null, new Rect(0, 0, size, size), 9 * k, 9 * k);
                dc.PushTransform(new TranslateTransform(size * 0.14, size * 0.14));
                dc.PushTransform(new ScaleTransform(k * 0.72, k * 0.72));
                dc.DrawGeometry(AppBrush(), null, Geometry.Parse(ShieldPath));
                DrawPlay(dc, new SolidColorBrush(Wpf.C("#16171A")), size <= 20 ? 2.4 : 1.6);
                dc.Pop();
                dc.Pop();
            }
            var rtb = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(dv);
            return rtb;
        }

        public static BitmapSource Render(int size, Brush fill)
        {
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                dc.PushTransform(new ScaleTransform(size / 40.0, size / 40.0));
                dc.DrawGeometry(fill, null, Geometry.Parse(ShieldPath));
                DrawPlay(dc, Brushes.White, size <= 20 ? 2.4 : 1.6);
                dc.Pop();
            }
            var rtb = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(dv);
            return rtb;
        }

        static byte[] Png(BitmapSource b)
        {
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(b));
            using (var ms = new MemoryStream())
            {
                enc.Save(ms);
                return ms.ToArray();
            }
        }

        // ICO with PNG inside (supported since Windows Vista)
        public static byte[] Ico(int[] sizes, Brush fill)
        {
            return Ico(sizes, s => fill == null ? RenderApp(s) : Render(s, fill));
        }

        public static byte[] Ico(int[] sizes, Func<int, BitmapSource> render)
        {
            var images = new byte[sizes.Length][];
            for (int i = 0; i < sizes.Length; i++) images[i] = Png(render(sizes[i]));
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                w.Write((short)0); w.Write((short)1); w.Write((short)sizes.Length);
                int offset = 6 + 16 * sizes.Length;
                for (int i = 0; i < sizes.Length; i++)
                {
                    w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
                    w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
                    w.Write((byte)0); w.Write((byte)0);
                    w.Write((short)1); w.Write((short)32);
                    w.Write(images[i].Length);
                    w.Write(offset);
                    offset += images[i].Length;
                }
                foreach (var img in images) w.Write(img);
                w.Flush();
                return ms.ToArray();
            }
        }

        public static System.Drawing.Icon Tray(Color c)
        {
            var b = new SolidColorBrush(c);
            b.Freeze();
            var ico = Ico(new[] { 16, 20, 24, 32, 40, 48 }, b);
            return new System.Drawing.Icon(new MemoryStream(ico), System.Windows.Forms.SystemInformation.SmallIconSize);
        }

        public static ImageSource Window()
        {
            var ico = Ico(new[] { 16, 24, 32, 48, 64 }, (Brush)null);
            return BitmapFrame.Create(new MemoryStream(ico), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        }

        // ClipKeeper.exe --make-icon src\app.ico — the exe icon for build.cmd
        public static int MakeFile(string path)
        {
            File.WriteAllBytes(path, Ico(new[] { 16, 20, 24, 32, 40, 48, 64, 256 }, (Brush)null));
            return 0;
        }
    }
}
