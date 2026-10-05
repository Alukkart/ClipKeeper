using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using Path = System.IO.Path;

namespace DeviceGuard
{
    // "Month recap" on top of Statistics: clips, the game of the month, the best day — a card made to be shared:
    // "Copy picture" puts it on the clipboard (Ctrl+V into Discord), "Save PNG" — into a file. The picture is the card itself.
    partial class MainWindow
    {
        DateTime recapMonth;   // the month shown; kept between recounts

        const string RepoLink = "github.com/Alukkart/ClipKeeper";

        static List<DateTime> RecapMonths(StatsResult r)
        {
            return r.Clips.Select(c => new DateTime(c.Recorded.Year, c.Recorded.Month, 1)).Distinct().OrderBy(m => m).ToList();
        }

        void AddRecap(StackPanel p, StatsResult r)
        {
            var months = RecapMonths(r);
            if (months.Count == 0) return;
            if (!months.Contains(recapMonth)) recapMonth = months.Last();

            var row = new Grid { Margin = new Thickness(0, 0, 0, 12) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            var slot = new ContentControl { Focusable = false };
            row.Children.Add(slot);

            var side = new StackPanel { Margin = new Thickness(20, 2, 0, 0) };
            Grid.SetColumn(side, 1);
            var sw = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 16) };
            var prev = new Button { Style = S("BtnIcon"), Content = "", ToolTip = L.T("Previous month", "Предыдущий месяц") };
            var next = new Button { Style = S("BtnIcon"), Content = "", ToolTip = L.T("Next month", "Следующий месяц"), Margin = new Thickness(6, 0, 0, 0) };
            var label = new TextBlock { Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Foreground = Wpf.Res<Brush>("Sub") };
            sw.Children.Add(prev);
            sw.Children.Add(next);
            sw.Children.Add(label);
            side.Children.Add(sw);
            var copy = Btn("", L.T("Copy picture", "Копировать картинку"), "BtnPrimary");
            copy.HorizontalAlignment = HorizontalAlignment.Left;
            var save = Btn("", L.T("Save PNG", "Сохранить PNG"), "BtnGhost");
            save.HorizontalAlignment = HorizontalAlignment.Left;
            save.Margin = new Thickness(0, 8, 0, 0);
            side.Children.Add(copy);
            side.Children.Add(save);
            side.Children.Add(new TextBlock
            {
                Text = L.T("Then Ctrl+V into Discord or Telegram — show your friends what the month was like.",
                           "Потом Ctrl+V в Discord или Telegram — покажи друзьям, каким был месяц."),
                Style = S("SubText"), FontSize = 12.5, MaxWidth = 220, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 12, 0, 0),
            });
            row.Children.Add(side);

            FrameworkElement card = null;
            Action show = () =>
            {
                int i = months.IndexOf(recapMonth);
                card = RecapCard(r, recapMonth);
                slot.Content = card;
                label.Text = MonthName(recapMonth);
                prev.IsEnabled = i > 0;
                next.IsEnabled = i < months.Count - 1;
            };
            prev.Click += (s, e) => { int i = months.IndexOf(recapMonth); if (i > 0) { recapMonth = months[i - 1]; show(); } };
            next.Click += (s, e) => { int i = months.IndexOf(recapMonth); if (i < months.Count - 1) { recapMonth = months[i + 1]; show(); } };
            copy.Click += (s, e) =>
            {
                string png = WriteRecap(card, Path.Combine(Program.Dir, "recap", RecapFileName(recapMonth)));
                if (png == null) return;
                try
                {
                    // a picture and a file: chats take either
                    var data = new DataObject();
                    data.SetImage(LoadPicture(png));
                    data.SetFileDropList(new StringCollection { png });
                    Clipboard.SetDataObject(data, true);
                    if (app != null) app.ShowToast(L.T("✓ Picture copied — paste it into the chat (Ctrl+V)", "✓ Картинка скопирована — вставь её в чат (Ctrl+V)"));
                }
                catch (Exception ex) { Log.Write("recap not copied: " + ex.Message); }
            };
            save.Click += (s, e) =>
            {
                var dlg = new Microsoft.Win32.SaveFileDialog
                {
                    FileName = RecapFileName(recapMonth), Filter = "PNG|*.png", DefaultExt = ".png",
                    InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
                };
                if (dlg.ShowDialog(W) != true) return;
                string png = WriteRecap(card, dlg.FileName);
                if (png != null && app != null) app.ShowToast(L.T("✓ Saved", "✓ Сохранено"), L.T("show", "показать"), () => Shell.Select(png));
            };
            show();
            p.Children.Add(row);
        }

        // for the self-test: the recap of the newest month as a picture; the file path or null
        public string TestRecapPicture(StatsResult r, string png)
        {
            var card = RecapCard(r, RecapMonths(r).Last());
            card.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            card.Arrange(new Rect(card.DesiredSize));
            card.UpdateLayout();
            return WriteRecap(card, png);
        }

        static string RecapFileName(DateTime m)
        {
            return "ClipKeeper " + m.ToString("yyyy-MM") + ".png";
        }

        // the card on a page-colored margin, at double resolution; null — did not work (in the log)
        static string WriteRecap(FrameworkElement card, string path)
        {
            try
            {
                const double pad = 20, scale = 2;
                double w = card.ActualWidth, h = card.ActualHeight;
                var dv = new DrawingVisual();
                using (var dc = dv.RenderOpen())
                {
                    dc.DrawRectangle(Wpf.Res<Brush>("Bg"), null, new Rect(0, 0, w + pad * 2, h + pad * 2));
                    dc.DrawRectangle(new VisualBrush(card), null, new Rect(pad, pad, w, h));
                }
                var rtb = new RenderTargetBitmap((int)Math.Ceiling((w + pad * 2) * scale), (int)Math.Ceiling((h + pad * 2) * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
                rtb.Render(dv);
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(rtb));
                using (var fs = File.Create(path)) enc.Save(fs);
                return path;
            }
            catch (Exception ex) { Log.Write("recap picture: " + ex.Message); return null; }
        }

        static BitmapSource LoadPicture(string png)
        {
            using (var fs = File.OpenRead(png))
            {
                var b = new BitmapImage();
                b.BeginInit();
                b.CacheOption = BitmapCacheOption.OnLoad;
                b.StreamSource = fs;
                b.EndInit();
                b.Freeze();
                return b;
            }
        }

        static string MonthGen(DateTime m)
        {
            // "October 2026 recap" / «Итоги октября 2026» — Russian takes the genitive
            return L.IsRu ? L.Culture.DateTimeFormat.MonthGenitiveNames[m.Month - 1] + " " + m.Year : m.ToString("MMMM yyyy", L.Culture);
        }

        FrameworkElement RecapCard(StatsResult r, DateTime month)
        {
            const double W0 = 540, H0 = 300;
            var list = r.Clips.Where(c => c.Recorded.Year == month.Year && c.Recorded.Month == month.Month).ToList();
            var pm = month.AddMonths(-1);
            int prevN = r.Clips.Count(c => c.Recorded.Year == pm.Year && c.Recorded.Month == pm.Month);
            var top = list.Where(c => !NoGame.Is(c.Game)).GroupBy(c => c.Game)
                          .OrderByDescending(x => x.Count()).ThenByDescending(x => x.Sum(c => c.Duration)).FirstOrDefault();
            var best = list.GroupBy(c => c.Recorded.Date).OrderByDescending(x => x.Count()).ThenBy(x => x.Key).First();
            int days = list.Select(c => c.Recorded.Date).Distinct().Count(), cut = list.Count(c => c.Cut);
            Brush white = Brushes.White, soft = Wpf.Br("#A1A1AA"), faint = Wpf.Br("#71717A");

            var g = new Grid { Width = W0, Height = H0, Clip = new RectangleGeometry(new Rect(0, 0, W0, H0), 16, 16) };
            g.Children.Add(new Border { Background = Wpf.Br("#131417") });

            // the game of the month: its cover on the right
            bool hasCover = top != null && Covers.IsGame(top.Key);
            if (hasCover)
            {
                var cover = new Grid
                {
                    Width = 128, Height = 192, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
                    Margin = new Thickness(0, 30, 30, 0), RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new RotateTransform(4),
                    Effect = new DropShadowEffect { BlurRadius = 24, ShadowDepth = 6, Opacity = 0.5, Color = Colors.Black },
                };
                var back = new Border { CornerRadius = new CornerRadius(10), Background = Wpf.Br("#26FFFFFF"), BorderBrush = Wpf.Br("#40FFFFFF"), BorderThickness = new Thickness(1) };
                var art = new Border { CornerRadius = new CornerRadius(10) };
                var initials = new TextBlock
                {
                    Text = GameCardVm.InitialsOf(Covers.Title(top.Key)), FontFamily = Wpf.Res<FontFamily>("DisplayFont"), FontSize = 38, FontWeight = FontWeights.SemiBold,
                    Foreground = soft, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                };
                cover.Children.Add(back);
                cover.Children.Add(initials);
                cover.Children.Add(art);
                cover.Children.Add(new TextBlock
                {
                    Text = L.T("GAME OF THE MONTH", "ИГРА МЕСЯЦА"), FontSize = 10, FontWeight = FontWeights.SemiBold, Foreground = soft,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, -20),
                });
                g.Children.Add(cover);
                Covers.Get(top.Key, img =>
                {
                    if (img == null) return;
                    var b = CoverBrush(img);
                    bool wide = img.Width > img.Height * 0.9;
                    if (wide && DarkArt(img as BitmapSource)) { art.Background = Brushes.White; art.OpacityMask = b; }
                    else art.Background = b;
                    initials.Visibility = Visibility.Collapsed;
                });
            }

            var body = new DockPanel { Margin = new Thickness(28, 24, hasCover ? 180 : 28, 22), LastChildFill = false };
            // the brand line on top
            var head = new StackPanel { Orientation = Orientation.Horizontal };
            head.Children.Add(new Viewbox
            {
                Width = 16, Height = 16, Margin = new Thickness(0, 0, 8, 0),
                Child = new System.Windows.Shapes.Path { Fill = Wpf.Res<Brush>("Brand"), Data = Geometry.Parse("M20,2.5 L35,8.5 V19.5 C35,29 28.5,35 20,38 C11.5,35 5,29 5,19.5 V8.5 Z") },
            });
            head.Children.Add(new TextBlock { Text = "ClipKeeper", FontWeight = FontWeights.SemiBold, FontSize = 12.5, Foreground = white, VerticalAlignment = VerticalAlignment.Center });
            head.Children.Add(new TextBlock
            {
                Text = "  ·  " + (L.IsRu ? "Итоги " + MonthGen(month) : MonthGen(month) + " recap"), FontSize = 12.5, Foreground = soft,
                VerticalAlignment = VerticalAlignment.Center,
            });
            DockPanel.SetDock(head, Dock.Top);
            body.Children.Add(head);

            // the big number
            var big = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 16, 0, 0) };
            big.Children.Add(new TextBlock { Text = list.Count.ToString(), FontFamily = Wpf.Res<FontFamily>("DisplayFont"), FontSize = 60, FontWeight = FontWeights.Bold, Foreground = white });
            string word = L.N(list.Count, "clip", "clips", "клип", "клипа", "клипов");
            word = word.Substring(word.IndexOf(' ') + 1);
            big.Children.Add(new TextBlock { Text = word, FontSize = 20, FontWeight = FontWeights.SemiBold, Foreground = soft, Margin = new Thickness(10, 0, 0, 12), VerticalAlignment = VerticalAlignment.Bottom });
            DockPanel.SetDock(big, Dock.Top);
            body.Children.Add(big);
            var sub = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, -2, 0, 0) };
            sub.Children.Add(new TextBlock
            {
                Text = Fmt.Duration(list.Sum(c => c.Duration)) + L.T(" recorded · ", " записано · ") + L.N(days, "day", "days", "день", "дня", "дней") + L.T(" with clips", " с клипами"),
                Foreground = soft, FontSize = 13, VerticalAlignment = VerticalAlignment.Center,
            });
            if (prevN > 0)
            {
                int d = (int)Math.Round(100.0 * (list.Count - prevN) / prevN);
                sub.Children.Add(new Border
                {
                    CornerRadius = new CornerRadius(9), Padding = new Thickness(7, 1, 7, 2), Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
                    Background = Wpf.Br(d >= 0 ? "#333FB950" : "#33F85149"),
                    Child = new TextBlock
                    {
                        Text = (d >= 0 ? "+" : "−") + Math.Abs(d) + "%", FontSize = 11.5, FontWeight = FontWeights.SemiBold,
                        Foreground = Wpf.Br(d >= 0 ? "#7EE2A8" : "#FF9B95"), ToolTip = L.T("compared to ", "по сравнению с ") + MonthName(pm),
                    },
                });
            }
            DockPanel.SetDock(sub, Dock.Top);
            body.Children.Add(sub);

            // the link — the picture travels, the program with it
            var link = new TextBlock { Text = RepoLink, FontSize = 11, Foreground = faint };
            DockPanel.SetDock(link, Dock.Bottom);
            body.Children.Add(link);

            var facts = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
            if (top != null) facts.Children.Add(Fact("", L.T("Most clipped", "Чаще всего"), Covers.Title(top.Key) + " · " + top.Count(), soft, white));
            facts.Children.Add(Fact("", L.T("Best day", "Лучший день"),
                                    best.Key.ToString(L.IsRu ? "d MMMM" : "MMMM d", L.Culture) + " · " + L.N(best.Count(), "clip", "clips", "клип", "клипа", "клипов"), soft, white));
            facts.Children.Add(Fact("", L.T("Trimmed", "Обрезано"), cut + L.T(" of ", " из ") + list.Count, soft, white));
            DockPanel.SetDock(facts, Dock.Bottom);
            body.Children.Add(facts);
            g.Children.Add(body);
            return g;
        }

        static FrameworkElement Fact(string glyph, string label, string value, Brush soft, Brush white)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 5, 0, 0) };
            sp.Children.Add(new Border
            {
                Width = 22, Height = 22, CornerRadius = new CornerRadius(11), Background = Wpf.Br("#26FFFFFF"), Margin = new Thickness(0, 0, 10, 0),
                Child = new TextBlock { Style = S("Icon"), Text = glyph, FontSize = 10.5, Foreground = white, HorizontalAlignment = HorizontalAlignment.Center },
            });
            sp.Children.Add(new TextBlock { Text = label, Foreground = soft, FontSize = 12.5, Width = 104, VerticalAlignment = VerticalAlignment.Center });
            sp.Children.Add(new TextBlock { Text = value, Foreground = white, FontWeight = FontWeights.SemiBold, FontSize = 13, VerticalAlignment = VerticalAlignment.Center,
                                            TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 190 });
            return sp;
        }
    }
}
