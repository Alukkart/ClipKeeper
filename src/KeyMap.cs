using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace DeviceGuard
{
    // The editor key map: a keyboard with bound keys colored by group, and a list below it.
    // The same block is shown in the editor ("?" / F1, read only) and in the main window
    // (Settings → Editor keys), where every action can be given another key.
    // Fixed keys: Ctrl+Z / Ctrl+Shift+Z / Ctrl+Y, Ctrl+S, Esc and Shift+? — they work the same everywhere.
    static class KeyMap
    {
        // groups use colors from the validated palette (keep the slot order: that keeps them distinguishable with color blindness)
        public class Group { public string Name; public Color Color; }
        public static readonly Group Play = new Group { Name = L.T("Playback", "Воспроизведение"), Color = Wpf.C("#3987e5") };
        public static readonly Group Edit = new Group { Name = L.T("Range and cuts", "Отрезок и вырезы"), Color = Wpf.C("#d95926") };
        public static readonly Group Nav = new Group { Name = L.T("Navigation and timeline", "Перемещение и шкала"), Color = Wpf.C("#199e70") };
        public static readonly Group Misc = new Group { Name = L.T("Other", "Прочее"), Color = Wpf.C("#c98500") };
        static readonly Group[] Groups = { Play, Edit, Nav, Misc };

        // ── actions and their keys ──
        public enum Act { Play, Loop, Stop, Faster, Back5, FrameBack, FrameFwd, PrevEdit, NextEdit, Start, End, MarkIn, MarkOut,
                          Cut, Restore, ZoomIn, ZoomOut, ZoomFit, Mute, Lanes, Keys }

        static readonly Dictionary<Act, Key> Defaults = new Dictionary<Act, Key>
        {
            { Act.Play, Key.Space }, { Act.Loop, Key.Return }, { Act.Stop, Key.K }, { Act.Faster, Key.L }, { Act.Back5, Key.J },
            { Act.FrameBack, Key.Left }, { Act.FrameFwd, Key.Right }, { Act.PrevEdit, Key.Up }, { Act.NextEdit, Key.Down },
            { Act.Start, Key.Home }, { Act.End, Key.End }, { Act.MarkIn, Key.I }, { Act.MarkOut, Key.O },
            { Act.Cut, Key.X }, { Act.Restore, Key.Delete }, { Act.ZoomIn, Key.OemPlus }, { Act.ZoomOut, Key.OemMinus },
            { Act.ZoomFit, Key.Oem5 }, { Act.Mute, Key.M }, { Act.Lanes, Key.T }, { Act.Keys, Key.F1 },
        };

        static readonly Dictionary<Act, Key> current = new Dictionary<Act, Key>(Defaults);
        static readonly object Sync = new object();

        // settings.json keeps only the changed keys: "Cut=C;Mute=N"
        public static void Load(string s)
        {
            lock (Sync)
            {
                current.Clear();
                foreach (var kv in Defaults) current[kv.Key] = kv.Value;
                foreach (var part in (s ?? "").Split(';'))
                {
                    int eq = part.IndexOf('=');
                    if (eq < 0) continue;
                    Act a;
                    Key k;
                    if (Enum.TryParse(part.Substring(0, eq).Trim(), out a) && Enum.TryParse(part.Substring(eq + 1).Trim(), out k) && CanBind(k))
                        Assign(a, k);
                }
            }
        }

        public static string Save()
        {
            lock (Sync)
                return string.Join(";", current.Where(kv => Defaults[kv.Key] != kv.Value).Select(kv => kv.Key + "=" + kv.Value));
        }

        public static Key KeyOf(Act a) { lock (Sync) return current[a]; }

        public static bool IsDefault { get { lock (Sync) return current.All(kv => Defaults[kv.Key] == kv.Value); } }

        public static Act? ActOf(Key k)
        {
            lock (Sync)
            {
                foreach (var kv in current) if (kv.Value == k) return kv.Key;
                // numpad and the second backslash key work for zoom while zoom keeps its default keys
                if (k == Key.Add && current[Act.ZoomIn] == Key.OemPlus) return Act.ZoomIn;
                if (k == Key.Subtract && current[Act.ZoomOut] == Key.OemMinus) return Act.ZoomOut;
                if (k == Key.OemBackslash && current[Act.ZoomFit] == Key.Oem5) return Act.ZoomFit;
                return null;
            }
        }

        public static bool CanBind(Key k)
        {
            switch (k)
            {
                case Key.None: case Key.Escape: case Key.System: case Key.LeftShift: case Key.RightShift: case Key.LeftCtrl: case Key.RightCtrl:
                case Key.LeftAlt: case Key.RightAlt: case Key.LWin: case Key.RWin: case Key.Apps: case Key.Capital: case Key.NumLock:
                case Key.Scroll: case Key.ImeProcessed: case Key.DeadCharProcessed: case Key.Sleep: case Key.PrintScreen:
                    return false;
                default:
                    return true;
            }
        }

        // a key already used by another action swaps with it, so no action is left without a key
        public static void Set(Act a, Key k)
        {
            lock (Sync) Assign(a, k);
        }

        static void Assign(Act a, Key k)
        {
            foreach (var other in current.Where(kv => kv.Value == k && kv.Key != a).Select(kv => kv.Key).ToList())
                current[other] = current[a];
            current[a] = k;
        }

        public static void Reset() { Load(""); }

        public static string Label(Key k)
        {
            switch (k)
            {
                case Key.Space: return L.T("Space", "Пробел");
                case Key.Return: return "Enter";
                case Key.Left: return "←";
                case Key.Right: return "→";
                case Key.Up: return "↑";
                case Key.Down: return "↓";
                case Key.Back: return "Backspace";
                case Key.Prior: return "PgUp";
                case Key.Next: return "PgDn";
                case Key.Insert: return "Ins";
                case Key.OemPlus: return "=";
                case Key.OemMinus: return "−";
                case Key.Oem5: case Key.OemBackslash: return "\\";
                case Key.OemQuestion: return "/";
                case Key.OemComma: return ",";
                case Key.OemPeriod: return ".";
                case Key.Oem1: return ";";
                case Key.OemQuotes: return "'";
                case Key.OemOpenBrackets: return "[";
                case Key.Oem6: return "]";
                case Key.Oem3: return "`";
                case Key.Add: return "Num +";
                case Key.Subtract: return "Num −";
                case Key.Multiply: return "Num *";
                case Key.Divide: return "Num /";
            }
            if (k >= Key.D0 && k <= Key.D9) return ((int)(k - Key.D0)).ToString();
            if (k >= Key.NumPad0 && k <= Key.NumPad9) return "Num " + (int)(k - Key.NumPad0);
            return k.ToString();
        }

        // what each action does (one line per action in the settings list)
        static string Desc(Act a)
        {
            switch (a)
            {
                case Act.Play: return L.T("play / pause", "пуск / пауза");
                case Act.Loop: return L.T("loop the range", "отрезок по кругу");
                case Act.Stop: return L.T("stop", "стоп");
                case Act.Faster: return L.T("play; again — 2×, 4×", "пуск; ещё раз — 2×, 4×");
                case Act.Back5: return L.T("back 5 seconds", "назад 5 секунд");
                case Act.FrameBack: return L.T("frame back; with Shift — a second", "кадр назад; с Shift — секунда");
                case Act.FrameFwd: return L.T("frame forward; with Shift — a second", "кадр вперёд; с Shift — секунда");
                case Act.PrevEdit: return L.T("previous edit point", "предыдущая точка монтажа");
                case Act.NextEdit: return L.T("next edit point", "следующая точка монтажа");
                case Act.Start: return L.T("clip start", "начало клипа");
                case Act.End: return L.T("clip end", "конец клипа");
                case Act.MarkIn: return L.T("range start here; with Shift — go to it", "начало отрезка здесь; с Shift — перейти к нему");
                case Act.MarkOut: return L.T("range end here; with Shift — go to it", "конец отрезка здесь; с Shift — перейти к нему");
                case Act.Cut: return L.T("cut a piece: at its start and again at its end", "вырезать кусок: в начале и ещё раз в конце");
                case Act.Restore: return L.T("restore the cut under the playhead", "вернуть вырез под бегунком");
                case Act.ZoomIn: return L.T("zoom in", "приблизить");
                case Act.ZoomOut: return L.T("zoom out", "отдалить");
                case Act.ZoomFit: return L.T("whole timeline", "вся шкала");
                case Act.Mute: return L.T("preview audio on / off", "звук просмотра вкл / выкл");
                case Act.Lanes: return L.T("separate track lanes on the timeline", "дорожки по отдельности на шкале");
                default: return L.T("key map", "карта клавиш");
            }
        }

        static Group GroupOf(Act a)
        {
            switch (a)
            {
                case Act.Play: case Act.Loop: case Act.Stop: case Act.Faster: case Act.Back5: return Play;
                case Act.MarkIn: case Act.MarkOut: case Act.Cut: case Act.Restore: return Edit;
                case Act.Mute: case Act.Keys: return Misc;
                default: return Nav;
            }
        }

        // ── the list: actions combined as in Premiere's cheat sheet, plus the fixed keys ──
        public class Bind
        {
            public string Fixed, Desc;
            public string[] FixedIds;
            public Act[] Acts = new Act[0];
            public string Prefix = "";
            public Group Group;

            public string Keys { get { return Fixed ?? Prefix + string.Join("   ", Acts.Select(a => Label(KeyOf(a)))); } }

            public string[] Ids
            {
                get
                {
                    if (FixedIds != null) return FixedIds;
                    var ids = Acts.Select(a => Id(KeyOf(a))).ToList();
                    if (Prefix.StartsWith("Shift")) ids.Insert(0, "Shift");
                    if (Prefix.StartsWith("?")) ids.Insert(0, "OemQuestion");
                    return ids.ToArray();
                }
            }
        }

        static string Id(Key k) { return k == Key.OemBackslash ? "Oem5" : k.ToString(); }

        static Bind A(Group g, string desc, params Act[] acts) { return new Bind { Group = g, Desc = desc, Acts = acts }; }
        static Bind F(Group g, string keys, string desc, params string[] ids) { return new Bind { Group = g, Fixed = keys, Desc = desc, FixedIds = ids }; }

        public static Bind[] Binds()
        {
            return new[]
            {
                A(Play, Desc(Act.Play), Act.Play),
                A(Play, Desc(Act.Back5), Act.Back5),
                A(Play, Desc(Act.Stop), Act.Stop),
                A(Play, Desc(Act.Faster), Act.Faster),
                A(Play, Desc(Act.Loop), Act.Loop),

                A(Edit, L.T("range start / end here", "начало / конец отрезка здесь"), Act.MarkIn, Act.MarkOut),
                A(Edit, Desc(Act.Cut), Act.Cut),
                A(Edit, Desc(Act.Restore), Act.Restore),
                F(Edit, "Esc", L.T("cancel an unfinished cut", "отменить незаконченный вырез"), "Escape"),
                F(Edit, "Ctrl + Z", L.T("undo a range or cut edit", "отменить правку отрезка или выреза"), "Ctrl", "Z"),
                F(Edit, "Ctrl + Shift + Z", L.T("redo", "повторить"), "Ctrl", "Shift", "Z"),

                A(Nav, L.T("frame back / forward", "кадр назад / вперёд"), Act.FrameBack, Act.FrameFwd),
                new Bind { Group = Nav, Desc = L.T("second back / forward", "секунда назад / вперёд"), Acts = new[] { Act.FrameBack, Act.FrameFwd }, Prefix = "Shift + " },
                A(Nav, L.T("to the previous / next edit point", "к предыдущей / следующей точке монтажа"), Act.PrevEdit, Act.NextEdit),
                A(Nav, L.T("clip start / end", "начало / конец клипа"), Act.Start, Act.End),
                new Bind { Group = Nav, Desc = L.T("go to the range start / end", "перейти к началу / концу отрезка"), Acts = new[] { Act.MarkIn, Act.MarkOut }, Prefix = "Shift + " },
                A(Nav, L.T("zoom in / out / whole timeline", "приблизить / отдалить / вся шкала"), Act.ZoomIn, Act.ZoomOut, Act.ZoomFit),
                F(Nav, L.T("Ctrl + wheel", "Ctrl + колесо"), L.T("zoom at the cursor; wheel — scroll", "приблизить у курсора; колесо — прокрутка"), "Ctrl"),
                A(Nav, Desc(Act.Lanes), Act.Lanes),

                A(Misc, Desc(Act.Mute), Act.Mute),
                F(Misc, "Ctrl + S", L.T("save", "сохранить"), "Ctrl", "S"),
                new Bind { Group = Misc, Desc = Desc(Act.Keys), Acts = new[] { Act.Keys }, Prefix = "?   " },
            };
        }

        // layout: label, id (a Key name), width in keys; id = null — an empty slot
        static readonly object[][][] Rows =
        {
            new[] { K("Esc", "Escape"), Gap(0.6), K("F1", "F1"), K("F2", "F2"), K("F3", "F3"), K("F4", "F4"), Gap(0.3),
                    K("F5", "F5"), K("F6", "F6"), K("F7", "F7"), K("F8", "F8"), Gap(0.3), K("F9", "F9"), K("F10", "F10"), K("F11", "F11"), K("F12", "F12") },
            new[] { K("`", "Oem3"), K("1", "D1"), K("2", "D2"), K("3", "D3"), K("4", "D4"), K("5", "D5"), K("6", "D6"), K("7", "D7"),
                    K("8", "D8"), K("9", "D9"), K("0", "D0"), K("−", "OemMinus"), K("=", "OemPlus"), K("⌫", "Back", 2) },
            new[] { K("Tab", "Tab", 1.5), K("Q", "Q"), K("W", "W"), K("E", "E"), K("R", "R"), K("T", "T"), K("Y", "Y"), K("U", "U"),
                    K("I", "I"), K("O", "O"), K("P", "P"), K("[", "OemOpenBrackets"), K("]", "Oem6"), K("\\", "Oem5", 1.5) },
            new[] { K("Caps", null, 1.75), K("A", "A"), K("S", "S"), K("D", "D"), K("F", "F"), K("G", "G"), K("H", "H"), K("J", "J"),
                    K("K", "K"), K("L", "L"), K(";", "Oem1"), K("'", "OemQuotes"), K("Enter", "Return", 2.25) },
            new[] { K("Shift", "Shift", 2.25), K("Z", "Z"), K("X", "X"), K("C", "C"), K("V", "V"), K("B", "B"), K("N", "N"), K("M", "M"),
                    K(",", "OemComma"), K(".", "OemPeriod"), K("/ ?", "OemQuestion"), K("Shift", "Shift", 2.75) },
            new[] { K("Ctrl", "Ctrl", 1.5), K("Win", null, 1.25), K("Alt", null, 1.25), K(L.T("Space", "Пробел"), "Space", 6.25), K("Alt", null, 1.25),
                    K("Ctrl", "Ctrl", 2.5) },
        };

        static object[] K(string label, string id, double w = 1) { return new object[] { label, id, w }; }
        static object[] Gap(double w) { return new object[] { null, null, w }; }

        const double U = 38, G = 4;   // key size and gap

        // editable: the settings version, where each action gets a button to change its key; changed() is called after every change
        public static FrameworkElement Build(Action changed = null)
        {
            var root = new StackPanel();
            var binds = Binds();
            root.Children.Add(Keyboard(binds));
            root.Children.Add(Legend());
            root.Children.Add(changed != null ? Editor(changed) : List(binds));
            return root;
        }

        // keyboard: the main block + Ins/Home/PgUp, Del/End/PgDn and the arrows on the right
        static FrameworkElement Keyboard(Bind[] binds)
        {
            var c = new Canvas();
            double y = 0, maxX = 0;
            for (int r = 0; r < Rows.Length; r++)
            {
                double x = 0;
                foreach (var k in Rows[r])
                {
                    double w = (double)k[2] * U + ((double)k[2] - 1) * G;
                    if (k[0] != null) Place(c, binds, (string)k[0], (string)k[1], x, y, w);
                    x += w + G;
                }
                maxX = Math.Max(maxX, x);
                y += U + G + (r == 0 ? 8 : 0);   // the Esc/F1 row sits slightly apart, like on a real keyboard
            }
            double nx = maxX + 18, top = U + G + 8;
            Place(c, binds, "Ins", "Insert", nx, top, U);
            Place(c, binds, "Home", "Home", nx + U + G, top, U);
            Place(c, binds, "PgUp", "Prior", nx + 2 * (U + G), top, U);
            Place(c, binds, "Del", "Delete", nx, top + U + G, U);
            Place(c, binds, "End", "End", nx + U + G, top + U + G, U);
            Place(c, binds, "PgDn", "Next", nx + 2 * (U + G), top + U + G, U);
            Place(c, binds, "↑", "Up", nx + U + G, y - 2 * (U + G), U);
            Place(c, binds, "←", "Left", nx, y - (U + G), U);
            Place(c, binds, "↓", "Down", nx + U + G, y - (U + G), U);
            Place(c, binds, "→", "Right", nx + 2 * (U + G), y - (U + G), U);
            c.Width = nx + 3 * (U + G);
            c.Height = y;
            return new Viewbox { Child = c, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, HorizontalAlignment = HorizontalAlignment.Left };
        }

        static void Place(Canvas c, Bind[] all, string label, string id, double x, double y, double w)
        {
            var binds = id == null ? new Bind[0] : all.Where(b => b.Ids.Contains(id)).ToArray();
            // modifiers are not a group but a "helper": a light outline without fill
            bool modifier = id == "Ctrl" || id == "Shift";
            var own = binds.Where(b => !modifier).Select(b => b.Group).FirstOrDefault();
            var key = new Border
            {
                Width = w, Height = U, CornerRadius = new CornerRadius(5), BorderThickness = new Thickness(1),
                Background = own != null ? Wpf.Br(own.Color, 0x47) : Wpf.Res<Brush>("Card2"),
                BorderBrush = own != null ? new SolidColorBrush(own.Color) : modifier ? Wpf.Br("#8A8D96") : Wpf.Res<Brush>("Line"),
                Child = new TextBlock
                {
                    Text = label, FontSize = label.Length > 3 ? 11 : 13, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                    Foreground = own != null || modifier ? Wpf.Res<Brush>("Text") : Wpf.Res<Brush>("Muted"),
                    FontWeight = own != null ? FontWeights.SemiBold : FontWeights.Normal,
                },
            };
            if (binds.Length > 0) key.ToolTip = string.Join("\n", binds.Select(b => b.Keys + " — " + b.Desc));
            Canvas.SetLeft(key, x);
            Canvas.SetTop(key, y);
            c.Children.Add(key);
        }

        static FrameworkElement Legend()
        {
            var p = new WrapPanel { Margin = new Thickness(0, 14, 0, 4) };
            foreach (var g in Groups) p.Children.Add(Swatch(g));
            var mod = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 18, 6) };
            mod.Children.Add(new Border { Width = 12, Height = 12, CornerRadius = new CornerRadius(3), BorderThickness = new Thickness(1),
                                          BorderBrush = Wpf.Br("#8A8D96"), Margin = new Thickness(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center });
            mod.Children.Add(new TextBlock { Text = L.T("Ctrl / Shift — together with another key", "Ctrl / Shift — вместе с другой клавишей"), Foreground = Wpf.Res<Brush>("Sub"), FontSize = 12.5 });
            p.Children.Add(mod);
            return p;
        }

        static FrameworkElement Swatch(Group g)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 18, 6) };
            sp.Children.Add(new Border { Width = 12, Height = 12, CornerRadius = new CornerRadius(3), Background = new SolidColorBrush(g.Color),
                                         Margin = new Thickness(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center });
            sp.Children.Add(new TextBlock { Text = g.Name, Foreground = Wpf.Res<Brush>("Sub"), FontSize = 12.5 });
            return sp;
        }

        static StackPanel GroupHead(Group g, bool first)
        {
            var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, first ? 0 : 12, 0, 4) };
            head.Children.Add(new Border { Width = 3, Height = 13, CornerRadius = new CornerRadius(1.5), Background = new SolidColorBrush(g.Color),
                                           Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center });
            head.Children.Add(new TextBlock { Text = g.Name.ToUpperInvariant(), Foreground = Wpf.Res<Brush>("Muted"), FontSize = 11.5, FontWeight = FontWeights.SemiBold });
            return head;
        }

        static Grid TwoColumns(out StackPanel left, out StackPanel right)
        {
            var grid = new Grid { Margin = new Thickness(0, 10, 0, 0) };
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            left = new StackPanel();
            right = new StackPanel();
            Grid.SetColumn(right, 2);
            grid.Children.Add(left);
            grid.Children.Add(right);
            return grid;
        }

        // the list by group in two columns
        static FrameworkElement List(Bind[] binds)
        {
            StackPanel left, right;
            var grid = TwoColumns(out left, out right);
            for (int i = 0; i < Groups.Length; i++)
            {
                var col = i < 2 ? left : right;
                var g = Groups[i];
                col.Children.Add(GroupHead(g, col.Children.Count == 0));
                foreach (var b in binds.Where(x => x.Group == g))
                {
                    var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(132) });
                    row.ColumnDefinitions.Add(new ColumnDefinition());
                    row.Children.Add(new TextBlock { Text = b.Keys, FontFamily = Wpf.Res<FontFamily>("MonoFont"), FontSize = 12 });
                    var d = new TextBlock { Text = b.Desc, Foreground = Wpf.Res<Brush>("Sub"), FontSize = 12.5, TextWrapping = TextWrapping.Wrap };
                    Grid.SetColumn(d, 1);
                    row.Children.Add(d);
                    col.Children.Add(row);
                }
            }
            return grid;
        }

        // the settings list: one row per action with a key button — click it and press the new key (Esc — cancel)
        static FrameworkElement Editor(Action changed)
        {
            var root = new StackPanel();
            StackPanel left, right;
            var grid = TwoColumns(out left, out right);
            var acts = (Act[])Enum.GetValues(typeof(Act));
            for (int i = 0; i < Groups.Length; i++)
            {
                var col = i < 2 ? left : right;
                var g = Groups[i];
                col.Children.Add(GroupHead(g, col.Children.Count == 0));
                foreach (var a in acts.Where(x => GroupOf(x) == g))
                {
                    var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(132) });
                    row.ColumnDefinitions.Add(new ColumnDefinition());
                    var btn = KeyButton(a, changed);
                    row.Children.Add(btn);
                    var d = new TextBlock { Text = Desc(a), Foreground = Wpf.Res<Brush>("Sub"), FontSize = 12.5, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
                    Grid.SetColumn(d, 1);
                    row.Children.Add(d);
                    col.Children.Add(row);
                }
            }
            root.Children.Add(grid);

            var foot = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 14, 0, 0) };
            foot.Children.Add(new TextBlock
            {
                Text = L.T("Click a key and press a new one. Ctrl + Z / Ctrl + S / Esc / ? stay as they are.",
                           "Нажми на клавишу в списке и затем новую. Ctrl + Z / Ctrl + S / Esc / ? не меняются."),
                Foreground = Wpf.Res<Brush>("Muted"), FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, MaxWidth = 520,
            });
            if (!IsDefault)
            {
                var reset = new Button { Style = Wpf.Res<Style>("BtnLink"), Content = L.T("Reset to defaults", "Вернуть как было"), Margin = new Thickness(14, 0, 0, 0) };
                reset.Click += (s, e) => { Reset(); changed(); };
                foot.Children.Add(reset);
            }
            root.Children.Add(foot);
            return root;
        }

        static Button KeyButton(Act a, Action changed)
        {
            string normal = Label(KeyOf(a));
            var btn = new Button
            {
                Style = Wpf.Res<Style>("BtnGhost"), Content = normal, HorizontalAlignment = HorizontalAlignment.Left, MinWidth = 64,
                Padding = new Thickness(10, 3, 10, 3), FontFamily = Wpf.Res<FontFamily>("MonoFont"), FontSize = 12, Margin = new Thickness(0, 0, 12, 0),
                ToolTip = L.T("Click and press a new key", "Нажми и затем новую клавишу"),
            };
            bool listening = false;
            btn.Click += (s, e) =>
            {
                if (listening) return;
                listening = true;
                btn.Content = L.T("press a key…", "нажми клавишу…");
                btn.Focus();
            };
            btn.LostKeyboardFocus += (s, e) => { if (listening) { listening = false; btn.Content = normal; } };
            btn.PreviewKeyDown += (s, e) =>
            {
                if (!listening) return;
                e.Handled = true;
                var k = e.Key == Key.System ? e.SystemKey : e.Key;
                if (k == Key.Escape) { listening = false; btn.Content = normal; return; }
                if (!CanBind(k)) return;
                listening = false;
                Set(a, k);
                changed();
            };
            return btn;
        }
    }
}
