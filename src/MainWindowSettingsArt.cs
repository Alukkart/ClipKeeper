using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace DeviceGuard
{
    // The pictures on the settings section banners: what the section is about at a glance, drawn in code (no files)
    // in the greys of the interface; colour only where it means something (connected, alarm).
    // Coordinates are on a 160×90 grid and scaled here, not by a Viewbox: scaled text drawn for the pixel grid gets blurry.
    partial class MainWindow
    {
        sealed class Sketch
        {
            // greys from dark to light, the fills of shapes, and the two states
            public const string G0 = "#2E3036", G1 = "#3A3C44", G2 = "#4A4D57", G3 = "#71717A", G4 = "#A1A1AA", G5 = "#EDEDEF",
                                Fill = "#16171B", Fill2 = "#1A1B1F", Dark = "#0B0C0E", Ok = "#3FB950", OkSoft = "#12261A", Bad = "#F85149", BadSoft = "#2A1416";

            public const double K = 1.7;   // grid units → pixels
            public readonly Canvas C = new Canvas { Width = 160 * K, Height = 90 * K, ClipToBounds = true };

            static Brush B(string hex) { return hex == null ? null : Wpf.Br(hex); }

            // frames of rectangles keep their 1px to stay sharp; lines and circles thicken a little with the scale
            T Put<T>(T s, string fill, string stroke, double th) where T : Shape
            {
                s.Fill = B(fill);
                s.Stroke = B(stroke);
                s.StrokeThickness = s is Rectangle ? th : System.Math.Round(th * 1.4 * 2) / 2;
                s.StrokeLineJoin = PenLineJoin.Round;
                s.StrokeStartLineCap = s.StrokeEndLineCap = PenLineCap.Round;
                C.Children.Add(s);
                return s;
            }

            public Shape Rect(double x, double y, double w, double h, double r, string fill, string stroke, double th = 1)
            {
                // on whole pixels, so a 1px frame stays sharp
                double l = System.Math.Round(x * K), t = System.Math.Round(y * K);
                var s = Put(new Rectangle { Width = System.Math.Round((x + w) * K) - l, Height = System.Math.Round((y + h) * K) - t, RadiusX = r * K, RadiusY = r * K },
                            fill, stroke, th);
                Canvas.SetLeft(s, l);
                Canvas.SetTop(s, t);
                return s;
            }

            public Shape Circle(double cx, double cy, double r, string fill, string stroke, double th = 1)
            {
                var s = Put(new Ellipse { Width = r * 2 * K, Height = r * 2 * K }, fill, stroke, th);
                Canvas.SetLeft(s, (cx - r) * K);
                Canvas.SetTop(s, (cy - r) * K);
                return s;
            }

            // SVG-like path data; numbers are formatted invariantly so a Russian Windows does not turn 1.5 into 1,5
            public Shape Path(string fill, string stroke, double th, string data, params object[] args)
            {
                var s = Put(new System.Windows.Shapes.Path(), fill, stroke, th);
                var g = Geometry.Parse(string.Format(CultureInfo.InvariantCulture, data, args)).Clone();
                g.Transform = new ScaleTransform(K, K);   // the stroke is not scaled with it
                s.SetValue(System.Windows.Shapes.Path.DataProperty, g);
                return s;
            }

            public Shape Dashed(Shape s)
            {
                s.StrokeDashArray = new DoubleCollection { 3, 3 };
                s.StrokeDashCap = PenLineCap.Flat;
                return s;
            }

            public static void Rotate(UIElement e, double angle)
            {
                e.RenderTransformOrigin = new Point(0.5, 0.5);
                e.RenderTransform = new RotateTransform(angle);
            }

            // text centred on x with its baseline near y; never smaller than 11px, or it is hard to read
            public TextBlock Text(double x, double y, string text, double size, string color, bool mono = false)
            {
                double fs = System.Math.Max(11, System.Math.Round(size * K * 2) / 2);
                var t = new TextBlock
                {
                    Text = text, FontSize = fs, Foreground = B(color), Width = 150 * K, TextAlignment = TextAlignment.Center,
                    FontFamily = Wpf.Res<FontFamily>(mono ? "MonoFont" : "UiFont"), FontWeight = FontWeights.SemiBold,
                };
                Canvas.SetLeft(t, System.Math.Round((x - 75) * K));
                Canvas.SetTop(t, System.Math.Round(y * K - fs * 1.1));
                C.Children.Add(t);
                return t;
            }

            // a window with a title bar
            public void Window(double x, double y, double w, double h)
            {
                Rect(x, y, w, h, 5, Fill, G1);
                Path(null, G1, 1, "M{0} {1}h{2}", x, y + 9, w);
                Circle(x + 6, y + 4.5, 1.5, G2, null);
                Circle(x + 11, y + 4.5, 1.5, G2, null);
            }

            // a keyboard key with a label
            public void Key(double x, double y, double w, string label)
            {
                Rect(x, y + 3, w, 19, 4, G1, null);
                Rect(x, y, w, 19, 4, Fill2, G2);
                Text(x + w / 2, y + 13, label, 7.5, G5);
            }

            public void Folder(double x, double y, string stroke, string label)
            {
                Path(Fill2, stroke, 1, "M{0} {1}v20a3 3 0 0 0 3 3h30a3 3 0 0 0 3 -3V{2}a3 3 0 0 0 -3 -3H{3}l-3 -4H{4}a3 3 0 0 0 -3 3z",
                     x, y + 4, y + 7, x + 16, x + 3);
                if (label != null) Text(x + 18, y + 40, label, 7, G4);
            }

            // a game screen with hills, for the alarm and the clip card that appear over a game
            public void Screen()
            {
                Rect(10, 12, 140, 68, 5, Fill2, G1);
                Path(null, G2, 1, "M10 66l34 -22 22 14 30 -24 54 34");
            }
        }

        static FrameworkElement SettingsArt(string key)
        {
            var s = new Sketch();
            const string G0 = Sketch.G0, G1 = Sketch.G1, G2 = Sketch.G2, G3 = Sketch.G3, G4 = Sketch.G4, G5 = Sketch.G5, F2 = Sketch.Fill2;
            switch (key)
            {
                case TabGeneral:   // a window with the language chips and a switch
                    s.Window(30, 14, 100, 62);
                    s.Rect(40, 31, 26, 13, 3, G5, null);
                    s.Text(53, 40.5, "Aa", 7.5, Sketch.Dark);
                    s.Rect(70, 31, 26, 13, 3, null, G2);
                    s.Text(83, 40.5, "Я", 7.5, G4);
                    s.Rect(40, 55, 44, 4, 2, G2, null);
                    s.Rect(100, 51, 21, 11, 5.5, G5, null);
                    s.Circle(115.5, 56.5, 3.5, Sketch.Dark, null);
                    break;
                case TabAbout:   // the logo, the version and an arrow down
                    s.Path(F2, G3, 1, "M58 22l20 -8 20 8v17c0 14 -9 22 -20 27 -11 -5 -20 -13 -20 -27z");
                    s.Path(G5, null, 0, "M72 31l14 8 -14 8z");
                    double pill = 12 + Program.Version.Length * 4.6;   // a dev build has a long version
                    s.Rect(154 - pill, 20, pill, 15, 7.5, Sketch.OkSoft, Sketch.Ok);
                    s.Text(154 - pill / 2, 30.5, Program.Version, 7, Sketch.Ok);
                    s.Path(null, G4, 1.5, "M127 43v15m-5 -5 5 5 5 -5");
                    break;
                case TabObs:   // OBS and ClipKeeper linked, the dot in the middle is green
                    s.Rect(16, 26, 44, 36, 7, F2, G2);
                    s.Circle(38, 44, 10, null, G4, 1.5);
                    s.Circle(38, 44, 3.5, G4, null);
                    s.Rect(100, 26, 44, 36, 7, F2, G2);
                    s.Path(null, G5, 1.2, "M113 37l9 -4 9 4v7c0 6 -4 9 -9 11 -5 -2 -9 -5 -9 -11z");
                    s.Dashed(s.Path(null, G3, 1, "M62 44h36"));
                    s.Circle(80, 44, 4, Sketch.Ok, null);
                    s.Text(80, 79, "127.0.0.1:4455", 7, G3, true);
                    break;
                case TabObsApp:   // the OBS window starts ClipKeeper through a small script
                    s.Window(16, 16, 72, 58);
                    s.Circle(52, 48, 13, null, G4, 1.5);
                    s.Circle(52, 48, 4, G4, null);
                    s.Path(null, G3, 1, "M95 45h15m-4 -4 4 4 -4 4");
                    s.Rect(117, 26, 28, 38, 3, F2, G2);
                    s.Path(null, G2, 1, "M123 35h16M123 41h12M123 47h14");
                    s.Path(G5, null, 0, "M127 53l5 3.5 -5 3.5z");
                    break;
                case TabReplay:   // the buffer keeps the last minutes
                    s.Rect(16, 40, 128, 12, 6, F2, G1);
                    s.Rect(74, 40, 70, 12, 6, G2, null);
                    s.Path(null, G5, 1.5, "M74 33v26");
                    s.Text(109, 31, L.T("last 2 minutes", "последние 2 мин"), 7, G4);
                    s.Circle(139, 46, 2.5, Sketch.Bad, null);
                    s.Path(null, G3, 1.5, "M22 70a8 8 0 1 0 3 -6");
                    s.Path(null, G3, 1.5, "M22 60v5h5");
                    break;
                case TabChecks:   // a monitor with the sound wave, and a check mark
                    s.Rect(14, 18, 72, 46, 4, F2, G2);
                    s.Path(null, G2, 1, "M42 70h16M50 64v6");
                    s.Path(null, G4, 1.5, "M22 44h8l4 -12 6 22 6 -16 5 10 4 -4h13");
                    s.Circle(120, 41, 20, Sketch.OkSoft, Sketch.Ok);
                    s.Path(null, Sketch.Ok, 2, "M110 41l7 7 13 -14");
                    break;
                case TabAlarm:   // the red alarm window over a game, and a bell
                    s.Screen();
                    s.Rect(86, 18, 58, 31, 4, Sketch.BadSoft, Sketch.Bad);
                    s.Circle(96, 28, 3, Sketch.Bad, null);
                    s.Rect(103, 26, 35, 4, 2, G4, null);
                    s.Rect(93, 37, 45, 3, 1.5, G2, null);
                    s.Path(null, G3, 1.2, "M30 30a8 8 0 0 1 16 0v6l3 4H27l3 -4zM35 44a3 3 0 0 0 6 0");
                    break;
                case TabClipSaved:   // the "Clip saved" card over a game, with a sound
                    s.Screen();
                    s.Rect(80, 17, 65, 31, 4, "#131417", G3);
                    s.Rect(84, 21, 23, 23, 3, G1, null);
                    s.Path(G5, null, 0, "M92 28l7 4.5 -7 4.5z");
                    s.Rect(111, 23, 29, 4, 2, G5, null);
                    s.Rect(111, 31, 20, 3, 1.5, G3, null);
                    s.Circle(113, 40, 2, Sketch.Ok, null);
                    s.Path(null, G3, 1.2, "M22 30c3 3 3 7 0 10M28 26c6 5 6 13 0 18");
                    break;
                case TabHotkeys:   // Ctrl + Shift + K, pressed in a game
                    s.Key(16, 30, 36, "Ctrl");
                    s.Text(60, 44, "+", 11, G3);
                    s.Key(68, 30, 36, "Shift");
                    s.Text(112, 44, "+", 11, G3);
                    s.Key(120, 30, 24, "K");
                    s.Rect(36, 64, 88, 13, 6.5, F2, G2);
                    s.Text(80, 73.5, L.T("in a game, no Alt+Tab", "в игре, без Alt+Tab"), 6.5, G4);
                    break;
                case TabSorting:   // a clip goes into Game\Month
                    s.Rect(10, 20, 38, 27, 4, F2, G2);
                    s.Path(G5, null, 0, "M24 28l9 5 -9 5z");
                    s.Path(null, G3, 1, "M53 33h14m-4 -4 4 4 -4 4");
                    s.Folder(74, 16, G4, null);
                    s.Text(92, 54, "Cyberpunk 2077", 7, G5);
                    s.Path(null, G2, 1, "M92 58v12h12");
                    s.Folder(108, 56, G2, null);
                    s.Text(126, 77, "2026-10", 6.5, G4);
                    break;
                case TabFolders:   // the three folders of the library
                    s.Folder(14, 22, G3, L.T("Sources", "Исходники"));
                    s.Folder(62, 22, G4, L.T("Ready", "Готовые"));
                    s.Folder(110, 22, G2, L.T("Collection", "Коллекция"));
                    s.Dashed(s.Path(null, G2, 1, "M52 36h8M100 36h8"));
                    break;
                case TabCovers:   // covers fanned out, the middle one a picture
                    Sketch.Rotate(s.Rect(33, 20, 34, 50, 4, "#24262C", G2), -8);
                    Sketch.Rotate(s.Rect(93, 20, 34, 50, 4, "#24262C", G2), 8);
                    s.Rect(62, 13, 36, 54, 4, "#33353D", G3);
                    s.Path(null, G4, 1.2, "M66 56l9 -10 7 7 5 -5 7 8");
                    s.Circle(88, 25, 3, G4, null);
                    s.Rect(64, 72, 32, 11, 5.5, "#131417", G2);
                    s.Text(80, 80.5, "Steam", 6, G4);
                    break;
                case TabCleanup:   // old clips go to the Recycle Bin
                    s.Rect(16, 20, 28, 17, 3, F2, G2);
                    s.Rect(24, 43, 28, 17, 3, F2, G2);
                    s.Rect(58, 29, 28, 17, 3, F2, G3).Opacity = 0.7;
                    s.Dashed(s.Path(null, G3, 1, "M91 38h14"));
                    s.Path(null, G4, 1.5, "M112 28h30M123 28v-4h8v4M115 28l3 40h18l3 -40");
                    s.Path(null, G2, 1, "M124 36v24M130 36v24");
                    s.Text(40, 77, L.T("older than 30 days", "старше 30 дней"), 7, G3);
                    break;
                case TabEncoder:   // a chip re-encodes the trim
                    s.Rect(56, 21, 48, 48, 6, F2, G4);
                    s.Rect(66, 31, 28, 28, 3, null, G2);
                    s.Text(80, 48, "GPU", 8, G5);
                    s.Path(null, G2, 1, "M64 21v-6M72 21v-6M80 21v-6M88 21v-6M96 21v-6M64 75v-6M72 75v-6M80 75v-6M88 75v-6M96 75v-6");
                    s.Path(null, G3, 1, "M14 45h34m-5 -4 5 4 -5 4M112 45h34m-5 -4 5 4 -5 4");
                    break;
                case TabEditorKeys:   // the timeline with a selection, and J K L
                    s.Rect(12, 14, 136, 24, 4, F2, G1);
                    s.Rect(42, 18, 58, 16, 2, G2, null);
                    s.Path(null, G5, 1.5, "M42 12v30M100 12v30");
                    s.Key(30, 52, 22, "J");
                    s.Key(57, 52, 22, "K");
                    s.Key(84, 52, 22, "L");
                    s.Key(116, 52, 22, "I");
                    break;
                default:
                    s.Rect(40, 20, 80, 50, 6, F2, G0);
                    break;
            }
            return s.C;
        }
    }
}
