using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace DeviceGuard
{
    // Statistics: how much was recorded, what became of clips (trimmed / kept / deleted) by month and game, how many are ready
    partial class MainWindow
    {
        // colors checked with the palette validator (dark card #131417): blue / teal / orange
        static readonly Charts.Series SCut = new Charts.Series { Name = L.T("trimmed", "обрезан"), Color = Wpf.C("#3987e5") };
        static readonly Charts.Series SKept = new Charts.Series { Name = L.T("kept", "лежит"), Color = Wpf.C("#199e70") };
        static readonly Charts.Series SGone = new Charts.Series { Name = L.T("deleted", "удалён"), Color = Wpf.C("#d95926") };
        static readonly Charts.Series SReady = new Charts.Series { Name = L.T("ready", "готовых"), Color = Wpf.C("#3987e5") };
        static CultureInfo Ru { get { return L.Culture; } }

        bool statsLoading;
        TextBlock statsSub;
        DateTime statsAt = DateTime.MinValue;

        public void LoadStats(bool force)
        {
            if (statsLoading || (!force && (DateTime.Now - statsAt).TotalMinutes < 2)) return;
            var panel = F<StackPanel>("StatsPanel");
            if (RootOf(last) == null)
            {
                StatsHeader(panel, L.T("the sources folder will be known after connecting to OBS — I'll count then", "папка исходников станет известна после подключения к OBS — тогда и посчитаю"));
                return;
            }
            statsLoading = true;
            if (panel.Children.Count == 0) StatsHeader(panel, L.T("counting…", "считаю…"));
            else statsSub.Text = L.T("counting…", "считаю…");
            string obs = RootOf(last), ready = ReadyRoot, coll = CollectionRoot;
            Task.Factory.StartNew(() => ClipStats.Compute(obs, ready, coll,
                s => W.Dispatcher.BeginInvoke(new Action(() => { if (statsSub != null) statsSub.Text = s; }))))
                .ContinueWith(t => W.Dispatcher.BeginInvoke(new Action(() =>
                {
                    statsLoading = false;
                    statsAt = DateTime.Now;
                    if (t.IsFaulted) { Log.Write("statistics: " + t.Exception); statsSub.Text = L.T("could not count — details in the log", "не получилось посчитать — подробности в журнале"); return; }
                    ShowStats(t.Result);
                })));
        }

        void StatsHeader(StackPanel p, string sub)
        {
            p.Children.Clear();
            var head = new Grid { Margin = new Thickness(0, 0, 0, 18) };
            var tx = new StackPanel();
            tx.Children.Add(new TextBlock { Text = L.T("Statistics", "Статистика"), Style = S("H1") });
            statsSub = new TextBlock { Text = sub, Style = S("SubText"), Margin = new Thickness(0, 4, 0, 0) };
            tx.Children.Add(statsSub);
            head.Children.Add(tx);
            var refresh = new Button { Style = S("BtnIcon"), Content = "", ToolTip = L.T("Recount", "Пересчитать"), HorizontalAlignment = HorizontalAlignment.Right,
                                       VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 0, 0) };
            refresh.Click += (o, e) => LoadStats(true);
            head.Children.Add(refresh);
            p.Children.Add(head);
        }

        static string Clips(int n) { return L.N(n, "clip", "clips", "клип", "клипа", "клипов"); }
        static string Pct(int n, int of) { return of == 0 ? "0%" : Math.Round(100.0 * n / of) + "%"; }

        void ShowStats(StatsResult r)
        {
            var p = F<StackPanel>("StatsPanel");
            var clips = r.Clips;
            int total = clips.Count, cut = clips.Count(c => c.Cut), gone = clips.Count(c => c.Gone), kept = total - cut - gone;
            StatsHeader(p, total == 0 ? L.T("no clips yet", "клипов пока нет")
                : L.T("sources, ready clips and the collection · updated ", "исходники, готовые клипы и коллекция · обновлено ") + DateTime.Now.ToString("HH:mm"));
            if (total == 0 && r.Ready.Count == 0) return;

            // the month recap — a picture to share
            AddRecap(p, r);

            // tiles
            var tiles = new UniformGrid { Columns = 4, Margin = new Thickness(-6, 0, -6, 6) };
            tiles.Children.Add(Tile(null, L.T("Recorded", "Записано"), Clips(total), Fmt.Duration(clips.Sum(c => c.Duration)) + " · " + Fmt.Size(clips.Sum(c => c.Size))));
            tiles.Children.Add(Tile(SCut, L.T("Trimmed", "Обрезано"), cut + " · " + Pct(cut, total), L.T("ready clips in total: ", "готовых клипов всего: ") + r.Ready.Count));
            tiles.Children.Add(Tile(SKept, L.T("Kept untrimmed", "Лежит без обрезки"), kept + " · " + Pct(kept, total),
                                    Fmt.Size(clips.Where(c => c.Present && !c.Cut).Sum(c => c.Size)) + L.T(" on disk", " на диске")));
            tiles.Children.Add(Tile(SGone, L.T("Deleted", "Удалено"), gone + " · " + Pct(gone, total), L.T("since ", "с ") + r.TrackedSince.ToString(L.IsRu ? "d MMMM" : "MMMM d", Ru)));
            p.Children.Add(tiles);

            var series = new[] { SCut, SKept, SGone };
            Func<IEnumerable<StatClip>, double[]> split = g =>
            {
                var l = g.ToList();
                return new double[] { l.Count(c => c.Cut), l.Count(c => !c.Cut && !c.Gone), l.Count(c => c.Gone) };
            };
            Func<IEnumerable<StatClip>, string> detail = g =>
            {
                var l = g.ToList();
                return SCut.Name + " " + l.Count(c => c.Cut) + " · " + SKept.Name + " " + l.Count(c => !c.Cut && !c.Gone) + " · " + SGone.Name + " " + l.Count(c => c.Gone) +
                       L.T("\ntotal ", "\nвсего ") + Clips(l.Count) + " · " + Fmt.Duration(l.Sum(c => c.Duration));
            };

            // by month: the last 12 months with anything in them
            var months = clips.GroupBy(c => new DateTime(c.Recorded.Year, c.Recorded.Month, 1)).OrderBy(g => g.Key).ToList();
            if (months.Count > 12) months = months.Skip(months.Count - 12).ToList();
            bool years = months.Select(g => g.Key.Year).Distinct().Count() > 1;
            var card = ChartCard(p, L.T("Clips by month", "Клипы по месяцам"), L.T("by recording date", "по дате записи"));
            card.Children.Add(Charts.Legend(series));
            card.Children.Add(Charts.Columns(months.Select(g => MonthLabel(g.Key, years)).ToList(), months.Select(g => split(g)).ToList(), series,
                                             i => MonthName(months[i].Key) + "\n" + detail(months[i]), 170));

            // by game: the 8 most frequent, the rest is "Other"
            var games = clips.GroupBy(c => Covers.Title(c.Game))
                             .OrderByDescending(g => g.Count()).ToList();
            var top = games.Take(8).Select(g => Tuple.Create(g.Key, g.ToList())).ToList();
            if (games.Count > 8) top.Add(Tuple.Create(L.T("Other (", "Другие (") + (games.Count - 8) + ")", games.Skip(8).SelectMany(g => g).ToList()));
            card = ChartCard(p, L.T("By game", "По играм"), L.T("how many clips were recorded and what became of them", "сколько клипов записано и что с ними стало"));
            card.Children.Add(Charts.Legend(series));
            card.Children.Add(Charts.Bars(top.Select(x => x.Item1).ToList(), top.Select(x => split(x.Item2)).ToList(), series,
                                          i => Clips(top[i].Item2.Count) + " · " + Fmt.Duration(top[i].Item2.Sum(c => c.Duration)),
                                          i => top[i].Item1 + "\n" + detail(top[i].Item2)));

            // ready clips by month — one series, no legend
            var made = r.Ready.GroupBy(x => new DateTime(x.Made.Year, x.Made.Month, 1)).OrderBy(g => g.Key).ToList();
            if (made.Count > 12) made = made.Skip(made.Count - 12).ToList();
            bool myears = made.Select(g => g.Key.Year).Distinct().Count() > 1;
            card = ChartCard(p, L.T("Ready clips by month", "Готовые клипы по месяцам"), L.T("Ready and the collection, by file date", "«Готовые» и коллекция, по дате файла"));
            card.Children.Add(Charts.Columns(made.Select(g => MonthLabel(g.Key, myears)).ToList(), made.Select(g => new double[] { g.Count() }).ToList(),
                                             new[] { SReady },
                                             i => MonthName(made[i].Key) + "\n" + L.T(made[i].Count() + " ready, " + made[i].Count(x => x.InCollection) + " of them in the collection", made[i].Count() + " готовых, из них в коллекции " + made[i].Count(x => x.InCollection)),
                                             150));

            p.Children.Add(new TextBlock
            {
                Style = S("SubText"), FontSize = 12.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 4, 0, 0),
                Text = L.T("How it is counted. \"Trimmed\" — a trim was made from the clip in ClipKeeper: trims from Premiere don't remember their source, so they can't be linked. " +
                           "\"Deleted\" — the clip is in neither Sources nor Ready: deleted clips are visible since " + r.TrackedSince.ToString("MMMM d, yyyy", Ru) +
                           ", when ClipKeeper started logging every saved clip. The history accumulates in clipstats.json.",
                           "Как считается. «Обрезан» — из клипа сделана обрезка в ClipKeeper: обрезки из Premiere не помнят свой исходник, поэтому их не связать. " +
                           "«Удалён» — клипа больше нет ни в исходниках, ни в готовых: удалённые видны с " + r.TrackedSince.ToString("d MMMM yyyy", Ru) +
                           ", с тех пор ClipKeeper записывает каждый сохранённый клип в журнал. История копится в clipstats.json."),
            });
        }

        static string MonthLabel(DateTime m, bool year)
        {
            string s = m.ToString("MMM", Ru).TrimEnd('.');
            return year ? s + " " + m.ToString("yy", Ru) : s;
        }

        static string MonthName(DateTime m)
        {
            string s = m.ToString("MMMM yyyy", Ru);   // "October 2026" / «октябрь 2026»
            return char.ToUpper(s[0]) + s.Substring(1);
        }

        StackPanel ChartCard(StackPanel p, string title, string sub)
        {
            var card = new Border { Style = S("CardBorder"), Margin = new Thickness(0, 12, 0, 0), Padding = new Thickness(20, 16, 20, 18) };
            var sp = new StackPanel();
            var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) };
            head.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, FontSize = 14.5 });
            head.Children.Add(new TextBlock { Text = sub, Foreground = Wpf.Res<Brush>("Muted"), FontSize = 12.5, Margin = new Thickness(10, 1, 0, 0),
                                              VerticalAlignment = VerticalAlignment.Center });
            sp.Children.Add(head);
            card.Child = sp;
            p.Children.Add(card);
            return sp;
        }

        // a total tile: a colored square ties it to the chart series, the text uses text color
        static Border Tile(Charts.Series s, string label, string value, string sub)
        {
            var b = new Border { Style = S("CardBorder"), Margin = new Thickness(6), Padding = new Thickness(16, 14, 16, 14) };
            var sp = new StackPanel();
            var head = new StackPanel { Orientation = Orientation.Horizontal };
            if (s != null) head.Children.Add(new Border { Width = 10, Height = 10, CornerRadius = new CornerRadius(2), Background = new SolidColorBrush(s.Color),
                                                          Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center });
            head.Children.Add(new TextBlock { Text = label, Foreground = Wpf.Res<Brush>("Sub"), FontSize = 12.5 });
            sp.Children.Add(head);
            sp.Children.Add(new TextBlock { Text = value, FontSize = 18, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 10, 0, 0) });
            sp.Children.Add(new TextBlock { Text = sub, Foreground = Wpf.Res<Brush>("Muted"), FontSize = 12, Margin = new Thickness(0, 3, 0, 0),
                                            TextTrimming = TextTrimming.CharacterEllipsis });
            b.Child = sp;
            return b;
        }

        // for the self-test: the page from ready numbers
        public void TestStats(StatsResult r) { statsAt = DateTime.Now; ShowStats(r); }

        // for previews: count over the real folders synchronously
        public void PreviewStats(string obs, string ready, string coll)
        {
            statsAt = DateTime.Now;   // so switching to the page does not start another count
            ShowStats(ClipStats.Compute(obs, ready, coll, s => { }));
        }
    }
}
