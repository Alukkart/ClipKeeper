using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace DeviceGuard
{
    // Settings → Sorting: where saved replays, recordings and screenshots go (Sorter)
    partial class MainWindow
    {
        // turned on: the folders it makes are games; the first time Smart Replay Mover's settings are taken over
        void SetSorting(bool on)
        {
            cfg.SortClips = on;
            if (!on) return;
            cfg.SubfoldersAreGames = true;
            if (cfg.SortImported) return;
            var srm = SmartReplayMover.Find();
            if (srm != null) SmartReplayMover.Import(srm, cfg);
            cfg.SortImported = true;
        }

        // a row of warning text in its own card, above the rest
        void Warn(string text)
        {
            Card(null);
            Add(new Border
            {
                Padding = new Thickness(16, 13, 16, 13), BorderBrush = Wpf.Res<Brush>("Line"),
                Child = new TextBlock { Text = text, Foreground = Wpf.Res<Brush>("Warn"), TextWrapping = TextWrapping.Wrap, FontSize = 12.5 },
            });
        }

        void BuildSorting()
        {
            if (cfg.SortClips && SmartReplayMover.Find() != null)
                Warn(L.T(
                    "Smart Replay Mover is still connected in OBS — it and ClipKeeper would move the same files. Remove it: OBS → Tools → Scripts → select it → \"−\". " +
                    "Its \"Smart Save\" hotkey goes away with it: set the key in OBS → Settings → Hotkeys → Replay Buffer → \"Save Replay\".",
                    "Smart Replay Mover всё ещё подключён в OBS — он и ClipKeeper будут перемещать одни и те же файлы. Убери его: OBS → Сервис → Скрипты → выдели его → «−». " +
                    "Вместе с ним пропадёт его горячая клавиша «Smart Save»: назначь клавишу в OBS → Настройки → Горячие клавиши → Буфер повтора → «Сохранить повтор»."));
            else if (cfg.SortClips && cfg.UseReplayBuffer && !SmartReplayMover.SaveHotkeySet())
                Warn(L.T(
                    "OBS has no key for saving a replay. Set it in OBS → Settings → Hotkeys → Replay Buffer → \"Save Replay\".",
                    "В OBS не назначена клавиша сохранения повтора. Назначь её в OBS → Настройки → Горячие клавиши → Буфер повтора → «Сохранить повтор»."));

            Card(null);
            Add(Row("\uE8CB", L.T("Sort clips by game", "Раскладывать клипы по играм"),
                L.T("Into the folder of the game you are playing, like Smart Replay Mover — its settings are taken over",
                    "В папку игры, в которую играешь, как Smart Replay Mover — его настройки переносятся"),
                Toggle(cfg.SortClips, v => { SetSorting(v); Changed(true); })), "smart replay mover srm");
            if (!cfg.SortClips) return;

            Card(L.T("Folders", "Папки"));

            var example = new TextBlock { Style = S("SubText"), FontSize = 12.5, TextWrapping = TextWrapping.Wrap };
            Action showExample = () =>
            {
                string rel = Sorter.Expand(cfg.SortTemplate, "Hunt Showdown", Sorter.Replays, DateTime.Now);
                string name = (cfg.SortPrefix ? "Hunt Showdown - " : "") + "Replay " + DateTime.Now.ToString("yyyy-MM-dd HH-mm-ss") + ".mp4";
                example.Text = L.T("{game} {type} {year} {month} {day} {date} {yearmonth}. Example: ", "{game} {type} {year} {month} {day} {date} {yearmonth}. Пример: ") +
                               (rel == "" ? "" : rel + "\\") + name;
            };
            showExample();
            var tpl = new TextBox { Style = S("Field"), Width = 240, Text = cfg.SortTemplate };
            tpl.TextChanged += (s, e) => { cfg.SortTemplate = tpl.Text.Trim(); showExample(); SaveSoon(); };
            Add(Row("\uE8B7", L.T("Folder template", "Шаблон папок"), example, tpl), "template шаблон");
            Add(Row("", L.T("Game name in the file name", "Название игры в имени файла"),
                L.T("\"Hunt Showdown - Replay …\" — the game is known even if the file is moved", "«Hunt Showdown - Replay …» — игра видна, даже если файл перенесли"),
                Toggle(cfg.SortPrefix, v => { cfg.SortPrefix = v; showExample(); })));
            var fallback = new TextBox { Style = S("Field"), Width = 160, Text = cfg.SortFallback };
            fallback.TextChanged += (s, e) => { cfg.SortFallback = fallback.Text.Trim(); SaveSoon(); };
            Add(Row("", L.T("When there is no game", "Когда игры нет"),
                L.T("Desktop, a browser, Discord in front and no game running", "Впереди рабочий стол, браузер или Discord, и никакая игра не запущена"), fallback));

            Card(L.T("What to sort", "Что раскладывать"));
            Add(Row("", L.T("Replays", "Повторы"), L.T("Always, when sorting is on", "Всегда, когда раскладка включена"), null));
            Add(Row("", L.T("Recordings", "Записи"),
                L.T("Regular recording — by the game at the start; split parts too", "Обычная запись — по игре на момент старта; разбитые на части тоже"),
                Toggle(cfg.SortRecordings, v => cfg.SortRecordings = v)));
            Add(Row("", L.T("Screenshots", "Скриншоты"), L.T("OBS screenshots (the library shows only videos)", "Скриншоты OBS (в библиотеке видны только видео)"),
                Toggle(cfg.SortScreenshots, v => cfg.SortScreenshots = v)));

            Card(L.T("How the game is found", "Как определяется игра"));
            var names = new TextBox
            {
                Style = S("Field"), Text = (cfg.SortNames ?? "").Replace("\n", Environment.NewLine), AcceptsReturn = true, MinLines = 3,
                FontFamily = Wpf.Res<FontFamily>("MonoFont"), FontSize = 12.5, Margin = new Thickness(0, 10, 0, 0),
            };
            names.TextChanged += (s, e) => { cfg.SortNames = names.Text.Replace("\r\n", "\n").Trim(); SaveSoon(); };
            var namesDesc = new StackPanel();
            namesDesc.Children.Add(new TextBlock
            {
                Style = S("SubText"), FontSize = 12.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0),
                Text = L.T("The window in front when you save; if it's Discord or the desktop — the last game you played, if it still runs. " +
                           "The name comes from Steam, Epic or GOG, and an existing game folder is reused. Your own names, one per line: " +
                           "HuntGame > Hunt · +call duty > Call of Duty (all words) · *minecraft* > Minecraft (anywhere in the name or window title)",
                           "Окно впереди в момент сохранения; если это Discord или рабочий стол — последняя игра, если она ещё запущена. " +
                           "Название берётся из Steam, Epic или GOG, а уже существующая папка игры используется дальше. Свои названия, по одному в строке: " +
                           "HuntGame > Hunt · +call duty > Call of Duty (все слова) · *minecraft* > Minecraft (где угодно в имени или заголовке окна)"),
            });
            namesDesc.Children.Add(names);
            Add(Row("", L.T("Your names", "Свои названия"), namesDesc, null));

            var result = new TextBlock { Style = S("SubText"), FontSize = 12.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0),
                                         Text = L.T("Press the button and switch to the game within 5 seconds", "Нажми кнопку и за 5 секунд переключись в игру") };
            var check = Btn(null, L.T("Check", "Проверить"), "BtnGhost");
            check.Click += (s, e) =>
            {
                if (app == null) return;
                check.IsEnabled = false;
                result.Text = L.T("Switch to the game… 5 s", "Переключись в игру… 5 с");
                string root = RootOf(last);
                Task.Delay(5000).ContinueWith(t =>
                {
                    string r;
                    try { r = app.Guard.Sorter.Preview(root); } catch (Exception ex) { r = ex.Message; }
                    W.Dispatcher.BeginInvoke(new Action(() => { result.Text = r; check.IsEnabled = true; }));
                });
            };
            Add(Row("", L.T("Which game is it now", "Какая игра сейчас"), result, check));
        }
    }
}
