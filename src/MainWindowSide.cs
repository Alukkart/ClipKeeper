using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace DeviceGuard
{
    // The sidebar under the pages: the games of the folder the library shows, newest first, each with a tiny cover and its
    // clip count — one click opens a game from any page. The search box above the pages opens the search over everything.
    partial class MainWindow
    {
        // a game in the sidebar: what the reading of a folder found (LibData.Side)
        class SideGame
        {
            public string Key, Name, Newest;
            public int Count;
        }

        const int SideGamesMax = 12;
        bool sideAsked;
        List<SideGame> sideGames = new List<SideGame>();
        readonly Dictionary<string, Brush> sideCovers = new Dictionary<string, Brush>();

        void InitSide()
        {
            F<Button>("SideSearch").Click += (s, e) => OpenPalette();
            var box = F<Border>("SideSearchBox");
            var search = F<Button>("SideSearch");
            search.MouseEnter += (s, e) => box.Background = Wpf.Br("#141518");
            search.MouseLeave += (s, e) => box.Background = Brushes.Transparent;
            var status = F<Border>("SideStatus");
            status.MouseEnter += (s, e) => status.Background = Wpf.Br("#141518");
            status.MouseLeave += (s, e) => status.Background = Brushes.Transparent;
        }

        // the games of a folder, newest first (on the reading thread)
        static List<SideGame> SideGamesOf(List<System.IO.FileInfo> all, Func<System.IO.FileInfo, string> game)
        {
            return all.GroupBy(game).Where(g => KnownGame(g.Key) && Covers.IsGame(g.Key))
                      .OrderByDescending(g => g.Max(f => f.LastWriteTime))
                      .Take(SideGamesMax)
                      .Select(g => new SideGame { Key = g.Key, Name = Covers.Title(g.Key), Count = g.Count(), Newest = g.OrderByDescending(f => f.LastWriteTime).First().FullName })
                      .ToList();
        }

        void ShowSideGames(List<SideGame> list, int total)
        {
            if (list != null) sideGames = list;
            F<TextBlock>("NavLibraryCount").Text = total > 0 ? total.ToString() : "";
            var p = F<StackPanel>("SideGames");
            p.Children.Clear();
            foreach (var g in sideGames) p.Children.Add(SideGameRow(g));
            F<FrameworkElement>("SideGamesBox").Visibility = sideGames.Count > 0 ? Visibility.Visible : Visibility.Hidden;
        }

        // the chosen game follows the library: the row of the open game is lit
        void MarkSideGame()
        {
            foreach (var b in F<StackPanel>("SideGames").Children.OfType<Button>())
            {
                bool on = currentPage == PageClips && Equals(b.Tag, gameView);
                b.Background = on ? Wpf.Br("#1B1C20") : Brushes.Transparent;
                b.Foreground = on ? Wpf.Br("#F4F5F7") : Wpf.Res<Brush>("Sub");
            }
        }

        Button SideGameRow(SideGame g)
        {
            var cover = new Border { Width = 16, Height = 16, CornerRadius = new CornerRadius(4), Background = Wpf.Res<Brush>("Card2"), Margin = new Thickness(0, 0, 9, 0) };
            Brush known;
            if (sideCovers.TryGetValue(g.Key, out known)) cover.Background = known;
            else
            {
                string key = g.Key;
                // a frame of the newest clip until the cover comes; no cover at all — the frame stays
                Thumbs.Request(g.Newest, img => { if (img != null && !sideCovers.ContainsKey(key)) cover.Background = Square(img); });
                Covers.Get(key, img => { var b = Square(img); sideCovers[key] = b; cover.Background = b; });
            }
            var row = new DockPanel();
            row.Children.Add(cover);
            var count = new TextBlock { Text = g.Count.ToString(), Foreground = Wpf.Br("#5F646D"), FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
            DockPanel.SetDock(count, Dock.Right);
            row.Children.Add(count);
            row.Children.Add(new TextBlock { Text = g.Name, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
            var b2 = new Button { Style = S("SideRow"), Content = row, Tag = g.Key, ToolTip = g.Name };
            b2.Click += (s, e) =>
            {
                if (currentPage != PageClips) ShowPage(PageClips);
                OpenGame(g.Key);
            };
            return b2;
        }

        // for previews: the sidebar games with their covers at hand and the tab counts, without waiting for downloads
        void PreviewSide(List<System.IO.FileInfo> all, Func<System.IO.FileInfo, string> game, string[] roots)
        {
            var list = SideGamesOf(all, game);
            foreach (var g in list)
            {
                var img = Covers.Fetch(g.Key) ?? Thumbs.Load(g.Newest, 320, 180);
                if (img != null) sideCovers[g.Key] = Square(img);
            }
            ShowSideGames(list, all.Count);
            ShowCounts(CountAll(roots));
        }

        // the middle of a picture as a small square: a cover, or the frame of a clip
        static Brush Square(ImageSource img)
        {
            var b = new ImageBrush(img) { Stretch = Stretch.UniformToFill, AlignmentY = AlignmentY.Top };
            b.Freeze();
            return b;
        }
    }
}
