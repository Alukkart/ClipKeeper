using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Ellipse = System.Windows.Shapes.Ellipse;

namespace DeviceGuard
{
    // The recording card: a click on the status at the bottom of the sidebar shows, from any page, everything the guard
    // watches — OBS, the replay buffer, the picture, the disk, each audio source with its device — and the problems;
    // the replay can be saved right there. "More" opens the Recording page.
    partial class MainWindow
    {
        void InitRecPop()
        {
            var status = F<Border>("SideStatus");
            status.MouseLeftButtonUp += (s, e) =>
            {
                if (!PageOn(PageRecord)) return;
                if (F<Grid>("RecPop").Visibility == Visibility.Visible) CloseRecPop();
                else OpenRecPop();
                e.Handled = true;
            };
            F<Border>("RecPopCatch").MouseLeftButtonDown += (s, e) => CloseRecPop();
            W.PreviewKeyDown += (s, e) =>
            {
                if (e.Key == Key.Escape && F<Grid>("RecPop").Visibility == Visibility.Visible) { CloseRecPop(); e.Handled = true; }
            };
        }

        void OpenRecPop()
        {
            F<Grid>("RecPop").Visibility = Visibility.Visible;
            BuildRecPop();
        }

        void CloseRecPop() { F<Grid>("RecPop").Visibility = Visibility.Collapsed; }

        // the snapshot comes once a second: an open card follows it
        void UpdateRecPop()
        {
            if (F<Grid>("RecPop").Visibility == Visibility.Visible) BuildRecPop();
        }

        void BuildRecPop()
        {
            var s = last;
            var p = F<StackPanel>("RecPopBody");
            p.Children.Clear();
            if (s == null) { p.Children.Add(new TextBlock { Text = L.T("Connecting to OBS…", "Подключаюсь к OBS…"), Foreground = Wpf.Res<Brush>("Muted") }); return; }

            bool rec = Recording(s);
            var head = new Grid();
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            head.ColumnDefinitions.Add(new ColumnDefinition());
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            head.Children.Add(new Ellipse { Width = 9, Height = 9, Fill = Wpf.Br(rec ? Wpf.Bad : Wpf.LevelColor(s.Level), 255), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 11, 0) });
            var names = new StackPanel();
            names.Children.Add(new TextBlock { Text = rec ? L.T("Recording", "Запись идёт") : s.Connected ? L.T("OBS connected", "OBS подключён") : (s.ObsStatus ?? L.T("OBS is not running", "OBS не запущен")),
                                               FontSize = 15, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
            names.Children.Add(new TextBlock { Text = s.Connected ? "OBS · " + s.Endpoint + (s.LastCheck != DateTime.MinValue ? L.T(" · checked at ", " · проверка в ") + s.LastCheck.ToString("HH:mm:ss") : "")
                                                                  : L.T("ClipKeeper is waiting for OBS", "ClipKeeper ждёт OBS"), FontSize = 12, Foreground = Wpf.Res<Brush>("Muted"), TextTrimming = TextTrimming.CharacterEllipsis });
            Grid.SetColumn(names, 1);
            head.Children.Add(names);
            var tag = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
            var lc = Wpf.LevelColor(s.Level);
            tag.Children.Add(new Ellipse { Width = 7, Height = 7, Fill = Wpf.Br(lc, 255), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 1, 6, 0) });
            tag.Children.Add(new TextBlock { Text = s.Level == 0 ? L.T("all good", "всё в порядке") : s.Level == 1 ? L.T("attention", "внимание") : s.Level == 2 ? L.T("problem", "проблема") : L.T("waiting", "ожидание"),
                                             FontSize = 12, Foreground = Wpf.Br(lc, 255) });
            Grid.SetColumn(tag, 2);
            head.Children.Add(tag);
            p.Children.Add(head);
            p.Children.Add(new Border { Height = 1, Background = Wpf.Br("#14FFFFFF"), Margin = new Thickness(0, 12, 0, 10) });

            // what is wrong comes first
            var probs = s.Due.Concat(s.Pending).Concat(s.Notes).Take(3).ToList();
            foreach (var m in probs)
                p.Children.Add(new TextBlock { Text = EventVm.Cap(m), TextWrapping = TextWrapping.Wrap, FontSize = 12.5, Foreground = Wpf.Res<Brush>(s.Due.Contains(m) ? "Bad" : "Warn"), Margin = new Thickness(0, 0, 0, 8) });

            RecRow(p, "", L.T("Replay buffer", "Буфер повтора"), tRb);
            RecRow(p, "", L.T("Picture", "Картинка"), tScreen);
            RecRow(p, "", L.T("Frames", "Кадры"), tObs);
            RecRow(p, "", L.T("Clip disk", "Диск для клипов"), tDisk);

            var audio = s.Refs.Where(r => Matcher.IsAudio(r.Value.Type)).ToList();
            if (audio.Count > 0)
            {
                p.Children.Add(new TextBlock { Text = L.T("Audio sources", "Источники звука"), FontSize = 11.5, Foreground = Wpf.Br("#5F646D"), Margin = new Thickness(0, 6, 0, 6) });
                foreach (var r in audio)
                {
                    bool bad = HasKey(s, r.Key, "src:", "audio:");
                    bool warn = s.NoteKeys.Contains("mute:" + r.Key) || s.NoteKeys.Contains("vol:" + r.Key);
                    string state = !s.Connected ? "—" : bad ? L.T("problem", "проблема") : warn ? L.T("check", "проверь") : L.T("in place", "на месте");
                    var c = !s.Connected ? Wpf.Grey : bad ? Wpf.Bad : warn ? Wpf.Warn : Wpf.Ok;
                    string n = r.Key.ToLowerInvariant();
                    string glyph = n.Contains("mic") || n.Contains("микр") ? "" : n.Contains("chat") || n.Contains("discord") || n.Contains("чат") ? "" : "";
                    RecLine(p, glyph, r.Key, ShortDevice(r.Value.LastName ?? r.Value.Display), state, Wpf.Br(c, 255));
                }
            }

            var bt = new Grid { Margin = new Thickness(0, 10, 0, 0) };
            bt.ColumnDefinitions.Add(new ColumnDefinition());
            bt.ColumnDefinitions.Add(new ColumnDefinition());
            bt.ColumnDefinitions.Add(new ColumnDefinition());
            Action<int, string, string, string, Action> add = (col, glyph, text, style, act) =>
            {
                var sp = new StackPanel { Orientation = Orientation.Horizontal };
                sp.Children.Add(new TextBlock { Style = S("Icon"), Text = glyph, FontSize = 11, Margin = new Thickness(0, 0, 7, 0) });
                sp.Children.Add(new TextBlock { Text = text, TextTrimming = TextTrimming.CharacterEllipsis });
                var b = new Button { Style = S(style), Content = sp, Margin = new Thickness(col == 0 ? 0 : 3, 0, col == 2 ? 0 : 3, 0), Padding = new Thickness(6, 6, 6, 6) };
                b.Click += (o, e) => act();
                Grid.SetColumn(b, col);
                bt.Children.Add(b);
            };
            if (s.Connected && s.RbState == 1 && app != null)
                add(0, "", L.T("Save replay", "Сохранить повтор"), "BtnPrimary", () =>
                {
                    app.Guard.RequestSaveReplay(err => app.ShowNotice(L.T("The replay was not saved", "Повтор не сохранён"), new List<string> { err }, false));
                    CloseRecPop();
                });
            else add(0, "", L.T("Check", "Проверить"), "BtnPrimary", () => { if (app != null) app.Guard.RequestCheck(); });
            add(1, "", L.T("Test alarm", "Тест тревоги"), "BtnGhost", () => { if (app != null) app.TestAlert(); });
            add(2, "", L.T("More", "Подробнее"), "BtnGhost", () => { CloseRecPop(); ShowPage(PageRecord); });
            p.Children.Add(bt);
        }

        // a line of what is watched: the tile's value and its note, the tile's dot
        static void RecRow(Panel p, string glyph, string name, TileVm t)
        {
            RecLine(p, glyph, name, t.Sub, t.Value, t.Dot);
        }

        static void RecLine(Panel p, string glyph, string name, string sub, string value, Brush dot)
        {
            var g = new Grid { Margin = new Thickness(0, 0, 0, 9) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.Children.Add(new TextBlock { Style = S("Icon"), Text = glyph, FontSize = 13, Foreground = Wpf.Res<Brush>("Muted"), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 0, 0) });
            var tx = new StackPanel();
            tx.Children.Add(new TextBlock { Text = name, Foreground = Wpf.Br("#C4C7CD"), TextTrimming = TextTrimming.CharacterEllipsis });
            if (!string.IsNullOrEmpty(sub)) tx.Children.Add(new TextBlock { Text = sub, FontSize = 12, Foreground = Wpf.Res<Brush>("Muted"), TextTrimming = TextTrimming.CharacterEllipsis });
            Grid.SetColumn(tx, 1);
            g.Children.Add(tx);
            var v = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(10, 1, 0, 0) };
            v.Children.Add(new TextBlock { Text = value ?? "", FontSize = 12.5, Foreground = Wpf.Res<Brush>("Sub"), MaxWidth = 140, TextTrimming = TextTrimming.CharacterEllipsis });
            v.Children.Add(new Ellipse { Width = 7, Height = 7, Fill = dot, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 1, 0, 0) });
            Grid.SetColumn(v, 2);
            g.Children.Add(v);
            p.Children.Add(g);
        }

        // for previews: the card open over the library
        public void PreviewRecPop(bool open)
        {
            if (open) OpenRecPop();
            else CloseRecPop();
        }
    }
}
