using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DeviceGuard
{
    // ClipKeeper.exe --showcase <folder> [--lang en|ru] — the README GIF from the current interface: a few screens with a caption
    // under each, cross-faded and looped. Writes slide-N.png and showcase.gif into the folder (showcase.cmd copies it to docs\).
    // Like --preview it shows real things: settings.json, devices.json, clips from D:\Sources, covers; needs ffmpeg\.
    static partial class Preview
    {
        const double SlideW = 960, SlideH = 680;   // the GIF size
        const double ScreenW = 912, ScreenH = 576;  // the screen on a slide; the caption takes the rest
        const double HoldSec = 2.2, FadeSec = 0.25;   // fade frames change every pixel: most of the GIF's weight
        const int GifFps = 12;

        public static int Showcase(string dir)
        {
            dir = Path.GetFullPath(dir);   // pictures are loaded by URI
            Directory.CreateDirectory(dir);
            try { return MakeShowcase(dir); }
            catch (Exception ex) { File.WriteAllText(Path.Combine(dir, "showcase-error.txt"), ex.ToString()); return 1; }
        }

        static int MakeShowcase(string dir)
        {
            if (!Ffmpeg.Available) { File.WriteAllText(Path.Combine(dir, "showcase-error.txt"), "ffmpeg is not found in " + Ffmpeg.Dir); return 1; }
            const string clipsRoot = @"D:\Sources", game = "Hunt Showdown";
            var cfg = Settings.Load(Path.Combine(Program.Dir, "settings.json"));   // read only
            cfg.Apply();
            cfg.CollectionFolder = @"D:\Clips";   // for the picture only, not saved
            string dummy;
            var refs = RefStore.Load(Path.Combine(Program.Dir, "devices.json"), new string[0], out dummy);

            // every screen is rendered at the slide's aspect, so nothing is cropped
            double mainH = Math.Round(1078 * ScreenH / ScreenW);
            var slides = new List<KeyValuePair<BitmapSource, string>>();
            Action<BitmapSource, string> add = (pic, caption) => slides.Add(new KeyValuePair<BitmapSource, string>(pic, caption));

            var clips = ClipScanner.Scan(Path.Combine(clipsRoot, game), 9).Select(f => ClipScanner.Describe(f, clipsRoot, null)).ToList();
            if (clips.Count == 0) { File.WriteAllText(Path.Combine(dir, "showcase-error.txt"), "no clips in " + Path.Combine(clipsRoot, game)); return 1; }
            string clip = clips[0].Path;
            var info = Ffmpeg.Info(clip);

            // 1. a moment of the game and the "Clip saved" card over it, where it really shows: the top right corner
            var over = new Grid { Width = 1078, Height = mainH, Background = Brushes.Black };
            var shot = Frame(clip, info.Duration * 0.4, dir);
            if (shot != null) over.Children.Add(new Image { Source = shot, Stretch = Stretch.UniformToFill });
            var card = ClipCard.PreviewContent(new ClipInfo { Path = clip, Game = game, Duration = info.Duration, Tracks = info.Audio.Count,
                                                              Bytes = new FileInfo(clip).Length, Today = 3 });
            card.HorizontalAlignment = HorizontalAlignment.Right;
            card.VerticalAlignment = VerticalAlignment.Top;
            card.Margin = new Thickness(0, 8, 8, 0);
            over.Children.Add(card);
            add(Shot(over, 1078, mainH), L.T("Press the OBS hotkey — the clip is saved and checked", "Жмёшь хоткей OBS — клип сохранён и проверен"));

            var mw = new MainWindow(null, cfg);
            var root = (FrameworkElement)mw.W.Content;
            mw.ShowPage(MainWindow.PageRecord);
            mw.Update(Fake(refs, 0));
            add(Shot(root, 1078, mainH), L.T("It watches OBS and fixes what breaks", "Следит за OBS и сам чинит, что сломалось"));

            mw.ShowPage(MainWindow.PageClips);
            mw.PreviewLibrary(clipsRoot, 3, 0);
            add(Shot(root, 1078, mainH), L.T("Every clip sorted by game, with covers", "Все клипы по играм, с обложками"));

            for (int i = 0; i < clips.Count; i++)
            {
                var img = Thumbs.Load(clips[i].Path, 320, 180);
                if (img != null) clips[i].Thumb = new ImageBrush(img) { Stretch = Stretch.UniformToFill };
            }
            clips[0].NewVis = Visibility.Visible;
            if (clips.Count > 1) clips[1].Fav = true;
            clips.ForEach(MainWindow.GameViewTitle);
            mw.PreviewClips(clips, clipsRoot, game);
            add(Shot(root, 1078, mainH), L.T("All clips of a game in one place", "Клипы одной игры — в одном месте"));

            // the editor: the same clip, a range with two cuts, the frame under the playhead in the player
            const double a = 54.5, b = 84.5, at = 61;
            string sp = Path.Combine(dir, "_strip.png"), wp = Path.Combine(dir, "_wave.png");
            Ffmpeg.Filmstrip(clip, info.Duration, 14, 108, sp, CancellationToken.None);
            Ffmpeg.Waveform(clip, info.Audio.Count - 1, 2400, 120, "0xC4C8D0", wp, CancellationToken.None);
            var frame = Frame(clip, Math.Min(at, info.Duration / 2), dir);
            double trimH = Math.Round(1400 * ScreenH / ScreenW);
            string[] names = { "Discord", "Game", "Mic", "Mix" };
            for (int i = 0; i < info.Audio.Count && i < names.Length; i++) info.Audio[i].Title = names[i];
            string realTrims = cfg.TrimFolder;
            cfg.TrimFolder = L.T(@"D:\Clips\Trims", @"D:\Clips\Обрезки");   // in the GIF's language, for the picture only, not saved
            Func<TrimWindow> editor = () =>
            {
                var t = new TrimWindow(null, cfg, clip, info.Audio.Count - 1);
                t.PreviewLoad(info, Png(sp), Png(wp), a, b, at, clipsRoot);
                t.PreviewFrame(frame);
                return t;
            };
            var tw = editor();
            tw.PreviewGain(0, -6, true, false);
            tw.PreviewCuts(new[] { new[] { 62.0, 66.5 }, new[] { 74.0, 77.0 } });
            add(Shot((FrameworkElement)tw.W.Content, 1400, trimH), L.T("Trim to the frame, cut out the boring parts", "Обрезка до кадра, лишнее — вырезать"));
            tw.PreviewShare(true);
            add(Shot((FrameworkElement)tw.W.Content, 1400, trimH), L.T("Fit for Discord in one click — then Ctrl+V", "Под Discord в один клик — и Ctrl+V в чат"));

            var tw2 = editor();
            string name = Path.GetFileNameWithoutExtension(clip);
            tw2.PreviewResult(new[]
            {
                new TrimStep { State = 1, Text = L.T("Saved: " + name + " — trim.mp4 · 451 MB", "Сохранено: " + name + " — обрезка.mp4 · 451 МБ") },
                new TrimStep { State = 1, Text = L.T("Length 0:30.5 (the start snapped to a keyframe, +0.6 s)", "Длина 0:30.5 (начало встало на ключевой кадр, +0.6 с)") },
                new TrimStep { State = 1, Text = L.T("The video reads from start to end", "Видео читается от начала до конца") },
                new TrimStep { State = 1, Text = L.T("Audio tracks: 4 of 4", "Звуковых дорожек: 4 из 4") },
                new TrimStep { State = 1, Text = L.T("Track 1 · Discord: has audio (-39.3 dB)", "Дорожка 1 · Discord: звук есть (-39.3 дБ)") },
                new TrimStep { State = 1, Text = L.T("Track 2 · Game: has audio (-14.1 dB)", "Дорожка 2 · Game: звук есть (-14.1 дБ)") },
                new TrimStep { State = 3, Text = L.T("Track 3 · Mic: silence — the source is quiet on this range too", "Дорожка 3 · Mic: тишина — в исходнике на этом отрезке тоже тихо") },
                new TrimStep { State = 1, Text = L.T("Track 4 · Mix: has audio (-14.1 dB)", "Дорожка 4 · Mix: звук есть (-14.1 дБ)") },
            }, true, Path.Combine(Path.GetDirectoryName(clip), name + L.T(" — trim.mp4", " — обрезка.mp4")));
            add(Shot((FrameworkElement)tw2.W.Content, 1400, trimH), L.T("Every trim is checked: video and all audio tracks", "Каждая обрезка проверяется: видео и все дорожки"));
            File.Delete(sp);
            File.Delete(wp);
            cfg.TrimFolder = realTrims;

            mw.PreviewStats(clipsRoot, Directory.Exists(cfg.TrimFolder) ? cfg.TrimFolder : null, cfg.CollectionFolder);
            mw.ShowPage(MainWindow.PageStats);
            add(Shot(root, 1078, mainH), L.T("And a monthly recap to show your friends", "И итоги месяца — показать друзьям"));

            // the slides, then the GIF: each slide holds, fades into the next, the last one fades back into the first
            var logo = Icons.RenderApp(64);
            var files = new List<string>();
            for (int i = 0; i < slides.Count; i++)
            {
                string f = Path.Combine(dir, "slide-" + (i + 1) + ".png");
                File.WriteAllBytes(f, PngOf(Slide(slides[i].Key, slides[i].Value, logo)));
                files.Add(f);
            }
            return Gif(files, Path.Combine(dir, "showcase.gif"));
        }

        // a screen in a rounded frame on top, the logo and the caption under it
        static BitmapSource Slide(BitmapSource screen, string caption, BitmapSource logo)
        {
            var g = new Grid { Width = SlideW, Height = SlideH, Background = Wpf.Res<Brush>("Bg") };
            var frame = new Border
            {
                Width = ScreenW, Height = ScreenH, Margin = new Thickness(0, 24, 0, 0), VerticalAlignment = VerticalAlignment.Top,
                CornerRadius = new CornerRadius(10), BorderBrush = Wpf.Br("#26FFFFFF"), BorderThickness = new Thickness(1),
                Background = new ImageBrush(screen) { Stretch = Stretch.UniformToFill, AlignmentY = AlignmentY.Top },
            };
            RenderOptions.SetBitmapScalingMode(frame, BitmapScalingMode.HighQuality);
            g.Children.Add(frame);

            var bottom = new Grid { Height = SlideH - ScreenH - 24, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(30, 0, 30, 0) };
            var brand = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
            var icon = new Image { Source = logo, Width = 22, Height = 22, VerticalAlignment = VerticalAlignment.Center };
            RenderOptions.SetBitmapScalingMode(icon, BitmapScalingMode.HighQuality);
            brand.Children.Add(icon);
            brand.Children.Add(new TextBlock { Text = "ClipKeeper", Margin = new Thickness(10, 0, 0, 0), FontSize = 13.5, VerticalAlignment = VerticalAlignment.Center,
                                               Foreground = Wpf.Res<Brush>("Muted"), FontFamily = Wpf.Res<FontFamily>("UiFont") });
            bottom.Children.Add(brand);
            bottom.Children.Add(new TextBlock
            {
                Text = caption, FontSize = 19, FontWeight = FontWeights.SemiBold, Foreground = Wpf.Res<Brush>("Text"), FontFamily = Wpf.Res<FontFamily>("DisplayFont"),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            });
            g.Children.Add(bottom);
            return Shot(g, SlideW, SlideH, 1.5);   // a sharper picture; ffmpeg scales it down to the GIF size
        }

        static BitmapSource Shot(FrameworkElement el, double w, double h, double scale = 2)
        {
            el.Measure(new Size(w, h));
            el.Arrange(new Rect(0, 0, w, h));
            el.UpdateLayout();
            Pump();
            el.UpdateLayout();
            return Snap(el, w, h, Wpf.Res<Brush>("Bg"), scale);
        }

        // one frame of a video as a picture (ffmpeg), or null
        static BitmapSource Frame(string clip, double at, string dir)
        {
            string png = Path.Combine(dir, "_frame.png");
            var r = Ffmpeg.Run("-y -ss " + Ffmpeg.T(at) + " -i \"" + clip + "\" -frames:v 1 \"" + png + "\"", CancellationToken.None);
            var b = r.Code == 0 ? Png(png) : null;
            try { File.Delete(png); } catch { }
            return b;
        }

        static int Gif(List<string> slides, string gif)
        {
            var args = new StringBuilder("-y");
            string len = Ffmpeg.T(HoldSec + FadeSec);
            foreach (var f in slides) args.Append(" -loop 1 -framerate " + GifFps + " -t " + len + " -i \"" + f + "\"");
            args.Append(" -loop 1 -framerate " + GifFps + " -t " + Ffmpeg.T(FadeSec) + " -i \"" + slides[0] + "\"");   // back to the start
            var fc = new StringBuilder();
            string last = "[0]";
            for (int i = 1; i <= slides.Count; i++)
            {
                string label = i == slides.Count ? "[v]" : "[x" + i + "]";
                fc.Append(last + "[" + i + "]xfade=transition=fade:duration=" + Ffmpeg.T(FadeSec) + ":offset=" + Ffmpeg.T(i * HoldSec) + label + ";");
                last = label;
            }
            fc.Append("[v]scale=" + SlideW + ":-1:flags=lanczos,fps=" + GifFps + ",split[a][b];[a]palettegen=stats_mode=diff[p];" +
                      "[b][p]paletteuse=dither=bayer:bayer_scale=5:diff_mode=rectangle");   // looks the same as error diffusion, packs better
            args.Append(" -filter_complex \"" + fc + "\" \"" + gif + "\"");
            var r = Ffmpeg.Run(args.ToString(), CancellationToken.None);
            if (r.Code == 0) return 0;
            File.WriteAllText(Path.Combine(Path.GetDirectoryName(gif), "showcase-error.txt"), "ffmpeg " + args + Environment.NewLine + r.Err);
            return 1;
        }
    }
}
