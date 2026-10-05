using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Ellipse = System.Windows.Shapes.Ellipse;

namespace DeviceGuard
{
    // The Sources section: the reference plus editing right here (applied in OBS and remembered at once)
    partial class MainWindow
    {
        class SourceCard
        {
            public string Name, Type;
            public Border Card, Editor;
            public TextBlock Device, Status, EditLabel;
            public Ellipse Dot;
            public WrapPanel Chips;
            public bool Editing;
        }

        readonly List<SourceCard> sourceCards = new List<SourceCard>();

        static string TypeGlyph(string type)
        {
            return Matcher.IsMonitor(type) ? "" : type == "wasapi_input_capture" ? "" : "";
        }

        static Border Chip(string text)
        {
            return new Border
            {
                CornerRadius = new CornerRadius(5), BorderBrush = Wpf.Res<Brush>("LineHi"), BorderThickness = new Thickness(1),
                Padding = new Thickness(7, 1, 7, 2), Margin = new Thickness(0, 0, 6, 0),
                Child = new TextBlock { Text = text, FontSize = 11.5, Foreground = Wpf.Res<Brush>("Sub") },
            };
        }

        SourceCard BuildSourceCard(string name, string type)
        {
            var c = new SourceCard { Name = name, Type = type };
            c.Card = new Border { Style = S("CardBorder"), Padding = new Thickness(0), Margin = new Thickness(0, 0, 0, 8) };
            var outer = new StackPanel();

            var head = new Grid { Margin = new Thickness(18, 14, 16, 14) };
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            head.ColumnDefinitions.Add(new ColumnDefinition());
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            head.Children.Add(new TextBlock
            {
                Style = S("Icon"), Text = TypeGlyph(type), FontSize = 17, Foreground = Wpf.Res<Brush>("Muted"),
                Width = 28, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 0, 0),
            });
            var tx = new StackPanel { Margin = new Thickness(10, 0, 16, 0) };
            tx.Children.Add(new TextBlock { Text = name, FontSize = 14.5, FontWeight = FontWeights.SemiBold });
            c.Device = new TextBlock { Foreground = Wpf.Res<Brush>("Sub"), FontSize = 12.5, Margin = new Thickness(0, 2, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
            tx.Children.Add(c.Device);
            c.Chips = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
            tx.Children.Add(c.Chips);
            Grid.SetColumn(tx, 1);
            head.Children.Add(tx);

            var status = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 14, 0) };
            c.Dot = new Ellipse { Width = 7, Height = 7, VerticalAlignment = VerticalAlignment.Center };
            c.Status = new TextBlock { FontSize = 12.5, Foreground = Wpf.Res<Brush>("Sub"), Margin = new Thickness(8, 0, 0, 0) };
            status.Children.Add(c.Dot);
            status.Children.Add(c.Status);
            Grid.SetColumn(status, 2);
            head.Children.Add(status);

            c.EditLabel = new TextBlock { Text = L.T("Edit", "Изменить") };
            var edit = new Button { Style = S("BtnGhost"), Content = c.EditLabel, VerticalAlignment = VerticalAlignment.Center };
            edit.Click += (s, e) => ToggleEditor(c);
            Grid.SetColumn(edit, 3);
            head.Children.Add(edit);
            outer.Children.Add(head);

            c.Editor = new Border
            {
                Visibility = Visibility.Collapsed, BorderBrush = Wpf.Res<Brush>("Line"), BorderThickness = new Thickness(0, 1, 0, 0),
                Padding = new Thickness(56, 4, 18, 16),
            };
            outer.Children.Add(c.Editor);
            c.Card.Child = outer;
            return c;
        }

        void UpdateSources(Snapshot s)
        {
            var panel = F<StackPanel>("SourcesPanel");
            var names = s.Refs.Select(r => r.Key).ToList();
            if (!names.SequenceEqual(sourceCards.Select(c => c.Name)))
            {
                if (sourceCards.Any(c => c.Editing)) return;   // don't reset an open editor
                panel.Children.Clear();
                sourceCards.Clear();
                foreach (var r in s.Refs)
                {
                    var c = BuildSourceCard(r.Key, r.Value.Type);
                    sourceCards.Add(c);
                    panel.Children.Add(c.Card);
                }
            }
            F<TextBlock>("SourcesEmpty").Visibility = names.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            for (int i = 0; i < s.Refs.Count; i++)
            {
                string name = s.Refs[i].Key;
                var r = s.Refs[i].Value;
                var c = sourceCards[i];
                c.Device.Text = string.IsNullOrEmpty(r.Display) ? L.T("device not remembered", "устройство не запомнено") : (r.LastName ?? r.Display);

                var chips = new List<string>();
                if (!string.IsNullOrEmpty(r.Tracks)) chips.Add(L.T("tracks ", "дорожки ") + r.Tracks.Replace(",", ", "));
                if (r.VolumeDb.HasValue && Math.Abs(r.VolumeDb.Value) >= 0.05) chips.Add(Db(r.VolumeDb.Value));
                if (r.Muted == true) chips.Add(L.T("muted", "звук выключен"));
                string sig = string.Join("|", chips);
                if ((c.Chips.Tag as string) != sig)
                {
                    c.Chips.Tag = sig;
                    c.Chips.Children.Clear();
                    foreach (var t in chips) c.Chips.Children.Add(Chip(t));
                    c.Chips.Visibility = chips.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
                }

                int lvl;
                string text;
                if (HasKey(s, name, "src:", "audio:", "screen:")) { lvl = 2; text = L.T("Problem", "Проблема"); }
                else if (s.NoteKeys.Contains("mute:" + name)) { lvl = 1; text = L.T("Muted", "Звук выключен"); }
                else if (s.NoteKeys.Contains("vol:" + name)) { lvl = 1; text = L.T("Volume changed", "Громкость изменена"); }
                else if (!s.Connected) { lvl = 3; text = L.T("No connection", "Нет связи"); }
                else { lvl = 0; text = L.T("OK", "В норме"); }
                c.Dot.Fill = Wpf.Br(Wpf.LevelColor(lvl), 255);
                c.Status.Text = text;
            }
        }

        static string Db(double db)
        {
            return (db > 0 ? "+" : db < 0 ? "−" : "") + Math.Abs(db).ToString("0.#", CultureInfo.InvariantCulture) + L.T(" dB", " дБ");
        }

        void ToggleEditor(SourceCard c)
        {
            if (c.Editing)
            {
                c.Editing = false;
                c.Editor.Visibility = Visibility.Collapsed;
                c.EditLabel.Text = L.T("Edit", "Изменить");
                return;
            }
            c.Editing = true;
            c.EditLabel.Text = L.T("Hide", "Скрыть");
            c.Editor.Visibility = Visibility.Visible;
            c.Editor.Child = new TextBlock
            {
                Text = Matcher.IsMonitor(c.Type) ? L.T("Asking OBS for the monitor list…", "Спрашиваю у OBS список мониторов…") : L.T("Loading the device list…", "Загружаю список устройств…"),
                Foreground = Wpf.Res<Brush>("Muted"), Margin = new Thickness(0, 12, 0, 0),
            };
            if (app == null) return;
            app.Guard.RequestState(c.Name, st => FillEditor(c, st), err =>
            {
                c.Editor.Child = new TextBlock { Text = L.T("Failed: ", "Не получилось: ") + err, Foreground = Wpf.Res<Brush>("Bad"), Margin = new Thickness(0, 12, 0, 0) };
            });
        }

        // for previews
        public void PreviewEditor(int index, Guard.SourceState st)
        {
            FoldSources(false);
            var c = sourceCards[index];
            c.Editing = true;
            c.EditLabel.Text = L.T("Hide", "Скрыть");
            c.Editor.Visibility = Visibility.Visible;
            FillEditor(c, st);
        }

        static TextBlock EditorCaption(string text)
        {
            return new TextBlock
            {
                Text = text.ToUpperInvariant(), Foreground = Wpf.Res<Brush>("Muted"), FontSize = 11.5,
                FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 14, 0, 8),
            };
        }

        void FillEditor(SourceCard c, Guard.SourceState st)
        {
            var root = new StackPanel();

            root.Children.Add(EditorCaption(Matcher.IsMonitor(st.Type) ? L.T("Monitor", "Монитор") : L.T("Device", "Устройство")));
            var options = new StackPanel { MaxWidth = 640, HorizontalAlignment = HorizontalAlignment.Left };
            foreach (var o in st.Options)
            {
                options.Children.Add(new RadioButton
                {
                    Style = S("Option"), GroupName = "dev_" + c.Name, Tag = o, IsChecked = o.Value == st.Value,
                    Content = new TextBlock { Text = o.Name, TextTrimming = TextTrimming.CharacterEllipsis },
                });
            }
            if (!st.Options.Any(o => o.Value == st.Value))
                options.Children.Add(new TextBlock
                {
                    Text = L.T("The device selected in OBS is not in the system — pick one from the list.", "Устройство, выбранное сейчас в OBS, в системе не найдено — выбери одно из списка."),
                    Foreground = Wpf.Res<Brush>("Warn"), FontSize = 12.5, Margin = new Thickness(2, 2, 0, 0), TextWrapping = TextWrapping.Wrap,
                });
            root.Children.Add(options);

            var chips = new List<ToggleButton>();
            Slider vol = null;
            CheckBox mute = null;
            bool volTouched = false;
            if (Matcher.IsAudio(st.Type))
            {
                root.Children.Add(EditorCaption(L.T("Recording tracks", "Дорожки записи")));
                var on = new HashSet<string>((st.Tracks ?? "").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries));
                var row = new StackPanel { Orientation = Orientation.Horizontal };
                for (int i = 1; i <= 6; i++)
                {
                    var t = new ToggleButton { Style = S("Chip"), Content = i.ToString(), IsChecked = on.Contains(i.ToString()) };
                    chips.Add(t);
                    row.Children.Add(t);
                }
                root.Children.Add(row);

                root.Children.Add(EditorCaption(L.T("OBS volume", "Громкость в OBS")));
                var vrow = new StackPanel { Orientation = Orientation.Horizontal };
                vol = MakeSlider(-30, 10, Math.Round(st.VolumeDb * 2) / 2, 0.5, 260);
                var vlbl = new TextBlock
                {
                    Text = Db(vol.Value), Width = 70, Margin = new Thickness(12, 0, 0, 0), Foreground = Wpf.Res<Brush>("Sub"),
                    VerticalAlignment = VerticalAlignment.Center,
                };
                var v = vol;
                vol.ValueChanged += (s, e) => { vlbl.Text = Db(v.Value); volTouched = true; };
                mute = new CheckBox { Style = S("Toggle"), IsChecked = st.Muted, Margin = new Thickness(28, 0, 10, 0) };
                vrow.Children.Add(vol);
                vrow.Children.Add(vlbl);
                vrow.Children.Add(mute);
                vrow.Children.Add(new TextBlock { Text = L.T("muted", "звук выключен"), Foreground = Wpf.Res<Brush>("Sub"), VerticalAlignment = VerticalAlignment.Center });
                root.Children.Add(vrow);
            }

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 18, 0, 0) };
            var apply = new Button { Style = S("BtnPrimary"), Content = L.T("Apply and remember", "Применить и запомнить") };
            var cancel = new Button { Style = S("BtnLink"), Content = L.T("Cancel", "Отмена"), Margin = new Thickness(8, 0, 0, 0) };
            buttons.Children.Add(apply);
            buttons.Children.Add(cancel);
            root.Children.Add(buttons);
            root.Children.Add(new TextBlock
            {
                Text = L.T("Changes go to OBS right away and become the reference — ClipKeeper will not roll them back.", "Изменения сразу уйдут в OBS и станут эталоном — ClipKeeper не будет их откатывать."),
                Foreground = Wpf.Res<Brush>("Muted"), FontSize = 12, Margin = new Thickness(2, 8, 0, 0),
            });

            cancel.Click += (s, e) => ToggleEditor(c);
            apply.Click += (s, e) =>
            {
                var edit = new Guard.SourceEdit();
                var sel = options.Children.OfType<RadioButton>().FirstOrDefault(r => r.IsChecked == true);
                var item = sel != null ? (DevItem)sel.Tag : null;
                // send the device only if it changed: setting the monitor again restarts capture
                if (item != null && item.Value != st.Value) { edit.Value = item.Value; edit.Name = item.Name; }
                if (Matcher.IsAudio(st.Type))
                {
                    edit.Tracks = string.Join(",", Enumerable.Range(0, 6).Where(i => chips[i].IsChecked == true).Select(i => (i + 1).ToString()));
                    edit.VolumeDb = volTouched ? Math.Round(vol.Value, 1) : st.VolumeDb;
                    edit.Muted = mute.IsChecked == true;
                }
                ToggleEditor(c);
                if (app == null) return;
                app.Guard.RequestApply(c.Name, edit, err => app.ShowNotice(L.T("Could not apply", "Не удалось применить"), new List<string> { c.Name + ": " + err }, false));
            };

            c.Editor.Child = root;
        }
    }
}
