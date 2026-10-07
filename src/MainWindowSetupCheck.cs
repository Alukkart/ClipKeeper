using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace DeviceGuard
{
    // Two steps of the first-run setup: "Check OBS" lists what OBS needs so a key press becomes a clip, each missing thing
    // with a button that fixes it (ObsFix); "Save a test clip" waits for the save key and shows the card that every clip gets.
    partial class MainWindow
    {
        // a line of the check: an icon, what it is and how it stands, a button that fixes it
        class CheckRow
        {
            public Grid Box;
            public TextBlock Icon, Title, Desc;
            public Button Fix;

            public void Show(int state, string title, string desc, string fix)   // 0 good, 1 needs attention, 2 missing
            {
                Icon.Text = state == 0 ? "" : state == 1 ? "" : "";
                Icon.Foreground = Wpf.Br(state == 0 ? Wpf.Ok : state == 1 ? Wpf.Warn : Wpf.Bad, 255);
                Title.Text = title;
                Desc.Text = desc ?? "";
                Desc.Visibility = desc == null ? Visibility.Collapsed : Visibility.Visible;
                Fix.Visibility = fix == null ? Visibility.Collapsed : Visibility.Visible;
                if (fix != null) Fix.Content = fix;
            }
        }

        CheckRow AddCheckRow(StackPanel p, Action fix)
        {
            var r = new CheckRow { Box = new Grid { Margin = new Thickness(0, 0, 0, 0) } };
            r.Box.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
            r.Box.ColumnDefinitions.Add(new ColumnDefinition());
            r.Box.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            r.Icon = new TextBlock { Style = S("Icon"), FontSize = 14, VerticalAlignment = VerticalAlignment.Center };
            var tx = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 14, 0) };
            r.Title = new TextBlock { FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
            r.Desc = new TextBlock { Style = S("SubText"), FontSize = 12.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) };
            tx.Children.Add(r.Title);
            tx.Children.Add(r.Desc);
            Grid.SetColumn(tx, 1);
            r.Fix = new Button { Style = S("BtnPrimary"), VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
            r.Fix.Click += (s, e) => fix();
            Grid.SetColumn(r.Fix, 2);
            r.Box.Children.Add(r.Icon);
            r.Box.Children.Add(tx);
            r.Box.Children.Add(r.Fix);
            p.Children.Add(new Border { BorderBrush = Wpf.Res<Brush>("Line"), BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(0, 11, 0, 11), Child = r.Box });
            return r;
        }

        // ── "Check OBS" ──
        void StepCheck(StackPanel p)
        {
            StepTitle(p, L.T("Check OBS", "Проверка OBS"),
                L.T("For clips to be saved, OBS needs these turned on. What is missing can be fixed right here.",
                    "Чтобы клипы сохранялись, в OBS должно быть включено вот это. Чего не хватает — можно исправить прямо здесь."));
            ObsFix.Status st = null;
            DateTime readAt = DateTime.MinValue;
            var waiting = new HashSet<string>();   // fixed while OBS is open: waits for it to close
            string capture = null;                 // the save key being pressed: what to show meanwhile
            Snapshot shown = last;
            Action refresh = null;

            Action<string, string> fixDone = (what, err) =>
            {
                if (err == ObsScript.WaitObs) waiting.Add(what);
                else if (err != null && app != null) app.ShowNotice(L.T("Not fixed", "Не исправилось"), new List<string> { err }, false);
                readAt = DateTime.MinValue;
                refresh();
            };

            var rows = new StackPanel();
            p.Children.Add(rows);
            var obsRow = AddCheckRow(rows, PickObsExe);
            var wsRow = AddCheckRow(rows, () => fixDone("ws", ObsFix.EnableWebSocket(st)));
            CheckRow rbRow = null, keyRow = null;
            if (cfg.UseReplayBuffer)
            {
                rbRow = AddCheckRow(rows, () => fixDone("rb", ObsFix.EnableReplayBuffer(st)));
                keyRow = AddCheckRow(rows, () =>
                {
                    capture = L.T("Press the key…  (Esc — cancel)", "Нажми клавишу…  (Esc — отмена)");
                    refresh();
                    KeyEventHandler grab = null;
                    grab = (s, e) =>
                    {
                        var k = e.Key == Key.System ? e.SystemKey : e.Key;
                        if (k == Key.LeftCtrl || k == Key.RightCtrl || k == Key.LeftShift || k == Key.RightShift || k == Key.LeftAlt || k == Key.RightAlt || k == Key.LWin || k == Key.RWin) return;
                        e.Handled = true;
                        W.PreviewKeyDown -= grab;
                        capture = null;
                        if (k == Key.Escape) { refresh(); return; }
                        string name = ObsFix.ObsKeyName(KeyInterop.VirtualKeyFromKey(k));
                        if (name == null) { fixDone("key", L.T("OBS has no such key — choose another one", "В OBS нет такой клавиши — выбери другую")); return; }
                        var m = Keyboard.Modifiers;
                        fixDone("key", ObsFix.SetSaveKey(st, name, (m & ModifierKeys.Control) != 0, (m & ModifierKeys.Shift) != 0, (m & ModifierKeys.Alt) != 0));
                    };
                    W.PreviewKeyDown += grab;
                });
            }
            CheckRow devRow = cfg.GuardEnabled ? AddCheckRow(rows, () => { }) : null;

            // OBS is open: what was fixed waits, because OBS writes its files back when it closes
            var waitBox = new Border { Style = S("CardBorder"), Background = Wpf.Res<Brush>("Input"), Margin = new Thickness(0, 10, 0, 0), Visibility = Visibility.Collapsed };
            var waitPanel = new StackPanel();
            waitPanel.Children.Add(new TextBlock { Style = S("SubText"), FontSize = 12.5, TextWrapping = TextWrapping.Wrap,
                Text = L.T("OBS is open and writes its settings back when it closes, so the fix waits: it is made the moment OBS closes — or restart OBS now.",
                           "OBS открыт и при закрытии перезапишет свои настройки, поэтому исправление ждёт: оно запишется, как только OBS закроется, — или перезапусти OBS сейчас.") });
            var restart = Btn("", L.T("Restart OBS now", "Перезапустить OBS сейчас"), "BtnGhost");
            restart.HorizontalAlignment = HorizontalAlignment.Left;
            restart.Margin = new Thickness(0, 10, 0, 0);
            restart.Click += (s, e) => { if (app != null) app.Guard.RequestRestartObs(); restart.IsEnabled = false; };
            waitPanel.Children.Add(restart);
            waitBox.Child = waitPanel;
            p.Children.Add(waitBox);

            refresh = () =>
            {
                var s = shown;
                if (st == null || (DateTime.Now - readAt).TotalSeconds > 3)
                {
                    ObsFix.ApplyPending();   // OBS closed by itself: what waited is written now
                    st = ObsFix.Read();
                    readAt = DateTime.Now;
                    if (!ObsFix.Pending) waiting.Clear();
                }
                string exe = app != null ? app.Guard.ObsExe() : null;
                if (exe != null) obsRow.Show(0, L.T("OBS is found", "OBS найден"), exe, null);
                else obsRow.Show(2, L.T("OBS is not found", "OBS не найден"), L.T("Choose obs64.exe so ClipKeeper can start it", "Укажи obs64.exe, чтобы ClipKeeper мог его запускать"), L.T("Choose…", "Выбрать…"));

                bool connected = s != null && s.Connected;
                if (connected) wsRow.Show(0, L.T("WebSocket is on", "WebSocket включён"), L.T("connected · ", "подключено · ") + s.Endpoint, null);
                else if (waiting.Contains("ws")) wsRow.Show(1, L.T("WebSocket will be turned on", "WebSocket включится"), L.T("once OBS closes", "как только OBS закроется"), null);
                else if (st.WsFile != null && !st.WsOn) wsRow.Show(2, L.T("The WebSocket server is off", "Сервер WebSocket выключен"),
                    L.T("Without it ClipKeeper can't see OBS", "Без него ClipKeeper не видит OBS"), L.T("Turn on", "Включить"));
                else if (!st.Settings) wsRow.Show(1, L.T("OBS was never started", "OBS ещё не запускался"), L.T("Start OBS once, then come back", "Запусти OBS один раз и вернись сюда"), null);
                else wsRow.Show(1, L.T("Not connected yet", "Пока не подключено"), L.T("Go back a step and press \"Find OBS\"", "Вернись на шаг назад и нажми «Найти OBS»"), null);

                if (rbRow != null)
                {
                    if ((s != null && s.RbState == 1) || st.RbOn) rbRow.Show(0, L.T("The replay buffer is on", "Буфер повтора включён"), s != null && s.RbState == 1 ? s.RbInfo : null, null);
                    else if (waiting.Contains("rb")) rbRow.Show(1, L.T("The replay buffer will be turned on", "Буфер повтора включится"), L.T("once OBS closes", "как только OBS закроется"), null);
                    else rbRow.Show(2, L.T("The replay buffer is off", "Буфер повтора выключен"), L.T("Without it the key has nothing to save", "Без него клавише нечего сохранять"),
                                    st.ProfileIni != null ? L.T("Turn on", "Включить") : null);
                }
                if (keyRow != null)
                {
                    string key = st.Key ?? (app != null ? app.Guard.SaveKeyText : null);
                    if (capture != null) keyRow.Show(1, L.T("The \"Save Replay\" key", "Клавиша «Сохранить повтор»"), capture, null);
                    else if (waiting.Contains("key")) keyRow.Show(1, L.T("The key will be set", "Клавиша назначится"), L.T("once OBS closes", "как только OBS закроется"), null);
                    else if (key != null) keyRow.Show(0, L.T("Save Replay: ", "Сохранить повтор: ") + key, L.T("the key that saves a clip", "клавиша, которая сохраняет клип"), L.T("Change…", "Сменить…"));
                    else keyRow.Show(1, L.T("No \"Save Replay\" key", "Нет клавиши «Сохранить повтор»"), L.T("Press the key you will save clips with", "Нажми клавишу, которой будешь сохранять клипы"),
                                     st.ProfileIni != null ? L.T("Set…", "Назначить…") : null);
                    if (key != null && capture == null && !waiting.Contains("key")) keyRow.Fix.Style = S("BtnGhost");
                    else keyRow.Fix.Style = S("BtnPrimary");
                }
                if (devRow != null)
                {
                    int n = s != null ? s.Refs.Count : 0;
                    if (n > 0) devRow.Show(0, L.T("Devices are remembered", "Устройства запомнены"), string.Join(", ", s.Refs.Select(r => r.Key)), null);
                    else devRow.Show(1, L.T("Devices are not remembered yet", "Устройства ещё не запомнены"), L.T("That is the next step", "Это следующий шаг"), null);
                }
                waitBox.Visibility = waiting.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
                restart.IsEnabled = app != null && app.Guard.ObsExe() != null;
            };
            setupLive = s => { shown = s; refresh(); };
            refresh();
        }

        // ── "Save a test clip": the last step when clips are saved with the replay buffer ──
        Action<bool, ClipInfo> setupClip;   // the step that waits for a clip: pressed (false) or saved (true)

        public void SetupClipEvent(bool saved, ClipInfo clip)
        {
            if (setup != null && setupClip != null) setupClip(saved, clip);
        }

        void StepTestClip(StackPanel p)
        {
            StepTitle(p, L.T("Save a test clip", "Сохрани пробный клип"),
                L.T("Press your key, as you would in a game. ClipKeeper shows what you get.", "Нажми свою клавишу, как в игре. ClipKeeper покажет, что получилось."));
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 16) };
            var keyText = new TextBlock { FontFamily = Wpf.Res<FontFamily>("MonoFont"), FontSize = 19, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
            row.Children.Add(new Border
            {
                MinWidth = 58, Height = 54, Padding = new Thickness(14, 0, 14, 0), CornerRadius = new CornerRadius(9), BorderBrush = Wpf.Res<Brush>("LineHi"),
                BorderThickness = new Thickness(1, 1, 1, 4), Background = Wpf.Res<Brush>("Card2"), Child = keyText,
            });
            keyText.HorizontalAlignment = HorizontalAlignment.Center;
            var state = new TextBlock { Style = S("SubText"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(18, 0, 0, 0), TextWrapping = TextWrapping.Wrap, MaxWidth = 440 };
            row.Children.Add(state);
            p.Children.Add(row);
            var cardHost = new ContentControl();
            p.Children.Add(cardHost);
            var after = new TextBlock { Style = S("SubText"), FontSize = 12.5, Margin = new Thickness(0, 10, 0, 0), TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed,
                                        Text = L.T("This is what every saved clip looks like. The card never takes focus from the game.",
                                                   "Так будет после каждого сохранения. Карточка не забирает фокус у игры.") };
            p.Children.Add(after);

            var waitPulse = new DoubleAnimation(1, 0.35, TimeSpan.FromSeconds(0.8)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever };
            Action<string, bool> say = (text, pulse) =>
            {
                state.Text = text;
                state.BeginAnimation(UIElement.OpacityProperty, pulse ? waitPulse : null);
                if (!pulse) state.Opacity = 1;
            };
            say(L.T("Waiting for the press…", "Жду нажатия…"), true);
            setupClip = (saved, clip) =>
            {
                if (!saved) { say(L.T("Saving the clip…", "Сохраняю клип…"), true); return; }
                say(clip.Ok ? L.T("✓ The clip is saved and checked", "✓ Клип сохранён и проверен") : L.T("The clip is saved, but with a problem — see the card", "Клип сохранён, но с проблемой — смотри карточку"), false);
                var card = ClipCard.PreviewContent(clip);
                card.HorizontalAlignment = HorizontalAlignment.Left;
                cardHost.Content = card;
                after.Visibility = Visibility.Visible;
            };

            // what else is worth setting before the end (it used to be the last step on its own)
            p.Children.Add(new Border { Height = 1, Background = Wpf.Res<Brush>("Line"), Margin = new Thickness(0, 20, 0, 16) });
            var autoRow = SetupToggle(L.T("Start with Windows", "Запускать вместе с Windows"), L.T("Minimized to the tray. Click the tray icon to open this window", "Свёрнутым в трей. Клик по значку в трее открывает это окно"),
                                      Autostart.IsOn(), v => { });
            BindAutostart(autoRow.Children.OfType<CheckBox>().First());
            p.Children.Add(autoRow);
            var test = Btn(null, L.T("Test alarm", "Тест тревоги"), "BtnGhost");
            test.HorizontalAlignment = HorizontalAlignment.Left;
            test.Click += (s, e) => { if (app != null) app.TestAlert(); };
            p.Children.Add(test);

            bool noKey = false;
            setupLive = s =>
            {
                string key = app != null ? app.Guard.SaveKeyText : null;
                keyText.Text = key ?? "?";
                if (cardHost.Content != null || (key == null) == noKey) return;
                noKey = key == null;
                if (noKey)
                    say(L.T("The save key is not known yet — set it in OBS (Settings → Hotkeys → Replay Buffer → Save Replay) or on the \"Check OBS\" step",
                            "Клавиша сохранения пока неизвестна — назначь её в OBS («Настройки → Горячие клавиши → Буфер повтора → Сохранить повтор») или на шаге «Проверка OBS»"), false);
                else say(L.T("Waiting for the press…", "Жду нажатия…"), true);
            };
        }

        // for previews: the test clip step with the card it shows
        public void PreviewTestClip(ClipInfo clip)
        {
            if (setupClip != null && clip != null) setupClip(true, clip);
        }
    }
}
