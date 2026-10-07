using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace DeviceGuard
{
    // ClipKeeper.exe --preview <folder> [--lang en|ru] — renders every screen to PNG without showing windows (for design checks)
    static partial class Preview
    {
        public static int Run(string dir)
        {
            Directory.CreateDirectory(dir);
            var cfg = Settings.Load(Path.Combine(Program.Dir, "settings.json"));   // read only
            cfg.Apply();
            cfg.SortClips = true;   // show the sorting group too (not saved)
            string dummy;
            var refs = RefStore.Load(Path.Combine(Program.Dir, "devices.json"), new string[0], out dummy);

            var mw = new MainWindow(null, cfg);
            // Recording and Settings at full height to see the whole page
            mw.ShowPage(MainWindow.PageRecord);
            mw.Update(Fake(refs, 0));
            Render((FrameworkElement)mw.W.Content, 1078, 758, Path.Combine(dir, "1-record.png"));
            Render((FrameworkElement)mw.W.Content, 1078, 1700, Path.Combine(dir, "1-record-full.png"));
            mw.ShowPage(MainWindow.PageSettings);
            Render((FrameworkElement)mw.W.Content, 1078, 758, Path.Combine(dir, "2-settings.png"));
            foreach (var tab in mw.SettingsTabKeys)
            {
                mw.ShowSettingsTab(tab);
                Render((FrameworkElement)mw.W.Content, 1078, 1600, Path.Combine(dir, "2-settings-" + tab + ".png"));
            }
            mw.ShowSettingsTab(MainWindow.TabGeneral);
            // the first-run setup, every step
            for (int i = 0; i < 5; i++)
            {
                mw.PreviewSetup(i);
                Render((FrameworkElement)mw.W.Content, 1078, 758, Path.Combine(dir, "20-setup-" + (i + 1) + ".png"));
            }
            mw.PreviewSetup(-1);
            // statistics over the real folders
            mw.PreviewStats(@"D:\Sources", Directory.Exists(cfg.TrimFolder) ? cfg.TrimFolder : null, @"D:\Clips");
            mw.ShowPage(MainWindow.PageStats);
            Render((FrameworkElement)mw.W.Content, 1078, 758, Path.Combine(dir, "19-stats.png"));
            Render((FrameworkElement)mw.W.Content, 1078, 1500, Path.Combine(dir, "19-stats-full.png"));
            // library: real games, Steam covers, clips and thumbnails from the recording folder
            const string clipsRoot = @"D:\Sources";
            mw.ShowPage(MainWindow.PageClips);
            mw.Update(Fake(refs, 0));
            cfg.CollectionFolder = @"D:\Clips";   // for the picture only, not saved
            mw.PreviewLibrary(clipsRoot, 3, 0);
            Render((FrameworkElement)mw.W.Content, 1078, 758, Path.Combine(dir, "14-library.png"));
            Render((FrameworkElement)mw.W.Content, 1500, 2400, Path.Combine(dir, "14-library-full.png"));   // every card, Desktop too
            mw.PreviewLibrary(cfg.CollectionFolder, 0, 2);
            Render((FrameworkElement)mw.W.Content, 1500, 940, Path.Combine(dir, "14-library-collection.png"));
            Render((FrameworkElement)mw.W.Content, 1500, 2400, Path.Combine(dir, "14-library-collection-full.png"));
            if (Directory.Exists(cfg.TrimFolder))
            {
                mw.PreviewLibrary(cfg.TrimFolder, 0, 1);
                Render((FrameworkElement)mw.W.Content, 1078, 758, Path.Combine(dir, "14-library-ready.png"));
            }
            mw.PreviewLibrary(clipsRoot, 0, 0);

            // a non-Steam game: the banner is a clip frame
            var val = ClipScanner.Scan(Path.Combine(clipsRoot, "Valorant"), 3).Select(f => ClipScanner.Describe(f, clipsRoot, null)).ToList();
            foreach (var vm in val)
            {
                var img = Thumbs.Load(vm.Path, 320, 180);
                if (img != null) vm.Thumb = new ImageBrush(img) { Stretch = Stretch.UniformToFill };
            }
            val.ForEach(MainWindow.GameViewTitle);
            mw.PreviewClips(val, clipsRoot, "Valorant");
            Render((FrameworkElement)mw.W.Content, 1078, 758, Path.Combine(dir, "14-game-frame-hero.png"));

            const string game = "Hunt Showdown";
            var clipVms = ClipScanner.Scan(Path.Combine(clipsRoot, game), 9).Select(f => ClipScanner.Describe(f, clipsRoot, null)).ToList();
            if (clipVms.Count > 0) clipVms[0].NewVis = Visibility.Visible;
            if (clipVms.Count > 1) clipVms[1].Fav = true;
            clipVms.ForEach(MainWindow.GameViewTitle);
            foreach (var vm in clipVms)
            {
                var img = Thumbs.Load(vm.Path, 320, 180);
                if (img != null) vm.Thumb = new ImageBrush(img) { Stretch = Stretch.UniformToFill };
            }
            mw.PreviewClips(clipVms, clipsRoot, game);
            Render((FrameworkElement)mw.W.Content, 1078, 758, Path.Combine(dir, "14-clips.png"));
            clipVms[0].EditText = L.T("Bridge duel", "Дуэль на мосту");   // renaming on the card
            clipVms[0].Editing = true;
            Render((FrameworkElement)mw.W.Content, 1078, 758, Path.Combine(dir, "14-rename.png"));
            clipVms[0].Editing = false;
            mw.PreviewFind("hunt", clipsRoot, Directory.Exists(cfg.TrimFolder) ? cfg.TrimFolder : null, cfg.CollectionFolder);
            Render((FrameworkElement)mw.W.Content, 1078, 758, Path.Combine(dir, "14-find.png"));

            // source editor
            mw.ShowPage(MainWindow.PageRecord);
            mw.Update(Fake(refs, 0));
            var first = refs.Items.First();
            mw.PreviewEditor(0, new Guard.SourceState
            {
                Type = first.Value.Type, Value = first.Value.Value, Tracks = first.Value.Tracks ?? "2,4", VolumeDb = first.Value.VolumeDb ?? 0,
                Options = new List<DevItem>
                {
                    new DevItem(L.T("Default", "По умолчанию"), "default"),
                    new DevItem(first.Value.Display, first.Value.Value),
                    new DevItem("SteelSeries Sonar - Chat (SteelSeries Sonar Virtual Audio Device)", "{chat}"),
                    new DevItem(L.T("Speakers (Realtek(R) Audio)", "Динамики (Realtek(R) Audio)"), "{rt}"),
                },
            });
            Render((FrameworkElement)mw.W.Content, 1078, 1700, Path.Combine(dir, "15-source-editor.png"));

            if (Ffmpeg.Available && clipVms.Count > 0)
            {
                string clip = clipVms[0].Path;
                var info = Ffmpeg.Info(clip);
                string[] names = { "Discord", "Game", "Mic", "Mix" };
                for (int i = 0; i < info.Audio.Count && i < names.Length; i++) info.Audio[i].Title = names[i];
                string sp = Path.Combine(dir, "_strip.png"), wp = Path.Combine(dir, "_wave.png");
                Ffmpeg.Filmstrip(clip, info.Duration, 14, 108, sp, System.Threading.CancellationToken.None);
                Ffmpeg.Waveform(clip, info.Audio.Count - 1, 2400, 120, "0xC4C8D0", wp, System.Threading.CancellationToken.None);
                cfg.TrimFolder = L.T(@"D:\Clips\Trims", @"D:\Clips\Обрезки");   // for the picture only, not saved
                var tw = new TrimWindow(null, cfg, clip, info.Audio.Count - 1);
                tw.PreviewLoad(info, Png(sp), Png(wp), 54.5, 84.5, 61, clipsRoot);
                tw.PreviewGain(0, -6, true, false);
                tw.PreviewGain(2, 0, false, false);
                tw.PreviewCuts(new[] { new[] { 62.0, 66.5 }, new[] { 74.0, 77.0 } });   // two cuts inside the range
                Render((FrameworkElement)tw.W.Content, 1400, 900, Path.Combine(dir, "16-trim.png"));
                tw.PreviewShare(true);
                Render((FrameworkElement)tw.W.Content, 1400, 900, Path.Combine(dir, "16-trim-share.png"));
                tw.PreviewShare(false);
                tw.PreviewZoom(55, 80, 64.2);   // 5.6× zoom and the time label under the cursor
                Render((FrameworkElement)tw.W.Content, 1400, 900, Path.Combine(dir, "16-trim-zoom.png"));
                tw.PreviewLanes(true);   // separate track lanes
                Render((FrameworkElement)tw.W.Content, 1400, 900, Path.Combine(dir, "16-trim-lanes.png"));
                tw.PreviewKeys();
                Render((FrameworkElement)tw.W.Content, 1400, 900, Path.Combine(dir, "16-trim-keys.png"));
                var tw2 = new TrimWindow(null, cfg, clip, info.Audio.Count - 1);
                tw2.PreviewLoad(info, Png(sp), Png(wp), 54.5, 84.5, 61, clipsRoot);
                tw2.PreviewResult(new[]
                {
                    new TrimStep { State = 1, Text = L.T("Saved: " + Path.GetFileNameWithoutExtension(clip) + " — trim.mp4 · 451 MB", "Сохранено: " + Path.GetFileNameWithoutExtension(clip) + " — обрезка.mp4 · 451 МБ") },
                    new TrimStep { State = 1, Text = L.T("Length 0:30.5 (the start snapped to a keyframe, +0.6 s)", "Длина 0:30.5 (начало встало на ключевой кадр, +0.6 с)") },
                    new TrimStep { State = 1, Text = L.T("The video reads from start to end", "Видео читается от начала до конца") },
                    new TrimStep { State = 1, Text = L.T("Audio tracks: 4 of 4", "Звуковых дорожек: 4 из 4") },
                    new TrimStep { State = 1, Text = L.T("Track 1 · Discord: has audio (-39.3 dB)", "Дорожка 1 · Discord: звук есть (-39.3 дБ)") },
                    new TrimStep { State = 1, Text = L.T("Track 2 · Game: has audio (-14.1 dB)", "Дорожка 2 · Game: звук есть (-14.1 дБ)") },
                    new TrimStep { State = 3, Text = L.T("Track 3 · Mic: silence — the source is quiet on this range too", "Дорожка 3 · Mic: тишина — в исходнике на этом отрезке тоже тихо") },
                    new TrimStep { State = 1, Text = L.T("Track 4 · Mix: has audio (-14.1 dB)", "Дорожка 4 · Mix: звук есть (-14.1 дБ)") },
                    new TrimStep { State = 1, Text = L.T("Written to the file: \"Hunt Showdown\", clip from 04.10.2026 13:18", "В файле записано: «Hunt Showdown», клип от 04.10.2026 13:18") },
                }, true, Path.Combine(Path.GetDirectoryName(clip), Path.GetFileNameWithoutExtension(clip) + L.T(" — trim.mp4", " — обрезка.mp4")));
                Render((FrameworkElement)tw2.W.Content, 1400, 900, Path.Combine(dir, "17-trim-result.png"));
                File.Delete(sp);
                File.Delete(wp);
            }

            mw.ShowPage(MainWindow.PageRecord);
            mw.Update(Fake(refs, 2));
            Render((FrameworkElement)mw.W.Content, 1078, 758, Path.Combine(dir, "7-overview-problem.png"));
            mw.Update(Fake(refs, 3));
            Render((FrameworkElement)mw.W.Content, 1078, 758, Path.Combine(dir, "8-overview-no-obs.png"));

            RenderAuto(new AlertWindow(null, AlertKind.Alarm, 0).PreviewContent(L.T("OBS: recording is not going right!", "OBS: запись идёт не так, как надо!"),
                L.T("• game: Windows is playing audio but OBS is not getting it — the source is stuck. Reselect the device in OBS or restart OBS\n" +
                    "• The replay buffer stopped because of an error, restarting…\n\n\"Restart OBS\" closes OBS and starts it again with the replay buffer.",
                    "• game: в Windows звук идёт, а OBS его не получает — источник завис. Перевыберите устройство в OBS или перезапустите OBS\n" +
                    "• Буфер повтора остановился из-за ошибки, перезапускаю…\n\n«Перезапустить OBS» закроет OBS и запустит его снова с буфером повтора."), true),
                Path.Combine(dir, "9-alarm.png"));
            RenderAuto(new AlertWindow(null, AlertKind.Notice, 15).PreviewContent(L.T("⚠ Clip saved with a problem", "⚠ Клип сохранён с проблемой"),
                L.T("• length 25 s of 140 — hit the \"Maximum memory\" of 512 MB, raise it in the replay buffer settings\n• File: HuntGame - Replay 2026-10-03 18-05-44.mp4",
                    "• длина 25 с из 140 — упёрся в «Максимум памяти» 512 МБ, увеличь его в настройках буфера повтора\n• Файл: HuntGame - Replay 2026-10-03 18-05-44.mp4"), false),
                Path.Combine(dir, "10-notice.png"));
            RenderAuto(new AlertWindow(null, AlertKind.Ok, 7).PreviewContent(L.T("OBS: devices restored", "OBS: устройства восстановлены"),
                L.T("• game: restored \"SteelSeries Sonar - Gaming (SteelSeries Sonar Virtual Audio Device)\" (by name)", "• game: вернул «SteelSeries Sonar - Gaming (SteelSeries Sonar Virtual Audio Device)» (по имени)"), false),
                Path.Combine(dir, "11-ok.png"));
            RenderAuto(new AlertWindow(null, AlertKind.Toast, 4).PreviewContent(L.T("✓ Connection settings saved", "✓ Настройки подключения сохранены"), null, false),
                Path.Combine(dir, "12-toast.png"));
            var real = ClipScanner.Scan(@"D:\Sources\Hunt Showdown", 1).FirstOrDefault();   // a real frame when the folder is there
            var card = new ClipInfo { Path = real != null ? real.FullName : @"D:\Sources\Hunt Showdown\2026-10\Hunt Showdown - Replay 2026-10-05 21-14-08.mp4", Game = "Hunt Showdown",
                                      Duration = 140, Tracks = 4, Bytes = 1186L * 1048576, Today = 3 };
            RenderAuto(ClipCard.PreviewContent(card), Path.Combine(dir, "12-clip.png"));
            RenderAuto(ClipCard.PreviewContent(card, true), Path.Combine(dir, "12-clip-delete.png"));
            card.Duration = 25;
            card.Bytes = 512L * 1048576;
            card.Issues = new List<string> { L.T("length 25 s of 140 — hit the \"Maximum memory\" of 512 MB, raise it in the replay buffer settings", "длина 25 с из 140 — упёрся в «Максимум памяти» 512 МБ, увеличь его в настройках буфера повтора") };
            RenderAuto(ClipCard.PreviewContent(card), Path.Combine(dir, "12-clip-problem.png"));
            RenderAuto(new TrayMenu(null).PreviewContent(Fake(refs, 0)), Path.Combine(dir, "18-tray-menu.png"));
            RenderAuto(new TrayMenu(null).PreviewContent(Fake(refs, 2)), Path.Combine(dir, "18-tray-menu-problem.png"));
            File.WriteAllBytes(Path.Combine(dir, "13-icon.png"), PngOf(Icons.RenderApp(256)));
            return 0;
        }

        // ClipKeeper.exe --preview-settings <folder> [--lang en|ru] — the settings tabs and the Recording page with default settings,
        // for the README screenshots; needs no folders or OBS, so CI makes them (the build artifact)
        public static int SettingsScreens(string dir)
        {
            Directory.CreateDirectory(dir);
            var cfg = Settings.Load(Path.Combine(Path.GetTempPath(), "clipkeeper-preview-" + Guid.NewGuid().ToString("N") + ".json"));   // defaults, not saved
            cfg.Apply();
            cfg.SortClips = true;
            string dummy;
            var refs = RefStore.Load(Path.Combine(Path.GetTempPath(), "clipkeeper-preview-none.json"), new string[0], out dummy);
            var mw = new MainWindow(null, cfg);
            var root = (FrameworkElement)mw.W.Content;
            mw.ShowPage(MainWindow.PageRecord);
            mw.Update(Fake(refs, 2));
            Render(root, 1078, 758, Path.Combine(dir, "recording-problem.png"));
            mw.Update(Fake(refs, 0));   // no problem dot on "Recording" in the settings pictures
            mw.ShowPage(MainWindow.PageSettings);
            foreach (var tab in mw.SettingsTabKeys)
            {
                mw.ShowSettingsTab(tab);
                Render(root, 1078, 758, Path.Combine(dir, "settings-" + tab + ".png"));
            }
            mw.SearchSettingsForTest(L.T("replay", "буфер"));
            Render(root, 1078, 758, Path.Combine(dir, "settings-search.png"));
            return 0;
        }

        public static Snapshot Fake(RefStore refs, int level)
        {
            var now = DateTime.Now;
            var s = new Snapshot
            {
                Level = level, Connected = level != 3, Endpoint = "127.0.0.1:4455", LastCheck = now,
                ObsStatus = level == 3 ? L.T("OBS is not running", "OBS не запущен") : L.T("connected to OBS", "подключено к OBS"),
                RbState = level == 3 ? -1 : 1, RbInfo = L.T("up to 140 s · memory up to 3072 MB", "до 140 с · память до 3072 МБ"),
                DiskFreeGb = 412, DiskRoot = "D:", ClipText = L.T("140 s · 4 tracks", "140 с · 4 дорожки"), ClipNote = L.T("1186 MB", "1186 МБ"),
                ClipAt = now.AddMinutes(-7), ClipOk = true, Status = "",
                PerfLevel = level == 3 ? -1 : level == 2 ? 1 : 0, PerfValue = "60 fps",
                PerfSub = level == 2 ? L.T("drops 4% of frames · GPU", "теряет 4% кадров · видеокарта") : L.T("no dropped frames", "кадры не теряются"),
            };
            foreach (var kv in refs.Items) s.Refs.Add(kv);
            s.Events = new List<string>
            {
                now.AddMinutes(-1).ToString("HH:mm:ss") + L.T("  ✓ clip saved: 140 s · tracks: 4 · 1186 MB", "  ✓ клип сохранён: 140 с · дорожек: 4 · 1186 МБ"),
                now.AddMinutes(-9).ToString("HH:mm:ss") + L.T("  ✓ game: restored \"SteelSeries Sonar - Gaming (SteelSeries Sonar Virtual Audio Device)\" (by name)", "  ✓ game: вернул «SteelSeries Sonar - Gaming (SteelSeries Sonar Virtual Audio Device)» (по имени)"),
                now.AddMinutes(-9).ToString("HH:mm:ss") + L.T("  ⚠ game: \"SteelSeries Sonar - Gaming (SteelSeries Sonar Virtual Audio Device)\" is not in the system", "  ⚠ game: «SteelSeries Sonar - Gaming (SteelSeries Sonar Virtual Audio Device)» не найдено в системе"),
                now.AddMinutes(-31).ToString("HH:mm:ss") + L.T("  OBS settings backup: backups\\", "  резервная копия настроек OBS: backups\\") + now.ToString("yyyy-MM-dd"),
                now.AddMinutes(-31).ToString("HH:mm:ss") + L.T("  connected to OBS", "  подключено к OBS"),
            };
            if (level == 2)
            {
                s.Due.Add(L.T("game: Windows is playing audio but OBS is not getting it — the source is stuck. Reselect the device in OBS or restart OBS", "game: в Windows звук идёт, а OBS его не получает — источник завис. Перевыберите устройство в OBS или перезапустите OBS"));
                s.ProblemKeys.Add("audio:game");
                s.DueKeys.Add("audio:game");
                s.Notes.Add(L.T("micro: muted in the OBS mixer", "micro: звук выключен в микшере OBS"));
                s.NoteKeys.Add("mute:micro");
                s.NotesKeys.Add("mute:micro");
                s.Notes.Add(L.T("OBS is dropping frames: 4% per minute (144) — rendering can't keep up (the GPU is busy with the game). Clips will stutter. " +
                                "Capping the game FPS 5–10 below usual helps, or running OBS as administrator — then Windows gives it GPU priority",
                                "OBS теряет кадры: 4% за минуту (144 шт.) — не успевает рендер (видеокарта занята игрой). Клипы будут дёргаться. " +
                                "Помогает ограничить FPS в игре на 5–10 ниже обычного или запускать OBS от имени администратора — тогда Windows даёт ему приоритет на видеокарте"));
                s.NoteKeys.Add("perf");
                s.NotesKeys.Add("perf");
            }
            return s;
        }

        static BitmapSource Png(string path)
        {
            if (!File.Exists(path)) return null;
            var b = new BitmapImage();
            b.BeginInit();
            b.CacheOption = BitmapCacheOption.OnLoad;
            b.UriSource = new Uri(path);
            b.EndInit();
            b.Freeze();
            return b;
        }

        public static void Pump()
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background,
                new DispatcherOperationCallback(o => { ((DispatcherFrame)o).Continue = false; return null; }), frame);
            Dispatcher.PushFrame(frame);
        }

        static void Render(FrameworkElement el, double w, double h, string path)
        {
            el.Measure(new Size(w, h));
            el.Arrange(new Rect(0, 0, w, h));
            el.UpdateLayout();
            Pump();
            el.UpdateLayout();
            Save(el, w, h, path, Wpf.Res<Brush>("Bg"));
        }

        static void RenderAuto(FrameworkElement el, string path)
        {
            el.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var sz = el.DesiredSize;
            el.Arrange(new Rect(sz));
            el.UpdateLayout();
            Pump();
            el.UpdateLayout();
            // a backdrop — as if the window is over a game
            Save(el, sz.Width, sz.Height, path, Wpf.Br("#3A4250"));
        }

        static void Save(FrameworkElement el, double w, double h, string path, Brush bg)
        {
            File.WriteAllBytes(path, PngOf(Snap(el, w, h, bg, 1.25)));
        }

        static BitmapSource Snap(FrameworkElement el, double w, double h, Brush bg, double scale)
        {
            var rtb = new RenderTargetBitmap((int)(w * scale), (int)(h * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen()) dc.DrawRectangle(bg, null, new Rect(0, 0, w, h));
            rtb.Render(dv);
            rtb.Render(el);
            rtb.Freeze();
            return rtb;
        }

        static byte[] PngOf(BitmapSource b)
        {
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(b));
            using (var ms = new MemoryStream())
            {
                enc.Save(ms);
                return ms.ToArray();
            }
        }
    }
}
