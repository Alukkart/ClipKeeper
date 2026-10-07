using System;
using System.IO;
using System.Text.RegularExpressions;

namespace DeviceGuard
{
    // A clip's own name lives in its file name, so it shows the same in Explorer, in Discord and in Telegram.
    // A source keeps its game and recording time around the name: "Hunt Showdown - Bridge duel - 2026-10-05 19-56-05.mp4"
    // (OBS writes "Hunt Showdown - Replay 2026-10-05 19-56-05": a space before the time, not " - " — so it has no name of its own).
    // A ready clip is named by its title alone, as the editor saves it.
    static class ClipNames
    {
        static readonly Regex Stamped = new Regex(@"^(?:(?<pre>.+?) - )?(?<label>.+?)(?<sep> - | |_)(?<ts>\d{4}-\d{2}-\d{2}[ _]\d{2}-\d{2}-\d{2})$", RegexOptions.Compiled);

        // the name a source was given in ClipKeeper, or null
        public static string TitleOf(string fileName)
        {
            var m = Stamped.Match(Path.GetFileNameWithoutExtension(fileName));
            return m.Success && m.Groups["sep"].Value == " - " ? m.Groups["label"].Value : null;
        }

        // the file name for a new title: a source keeps what was around its name, a ready clip is the title itself
        public static string NameFor(string path, string title, bool source)
        {
            string t = Trimmer.SafeName(title ?? "");
            if (t.Length == 0) return null;
            string ext = Path.GetExtension(path);
            if (!source) return t + ext;
            var m = Stamped.Match(Path.GetFileNameWithoutExtension(path));
            string pre = m.Success && m.Groups["pre"].Success ? m.Groups["pre"].Value + " - " : "";
            string ts = m.Success ? m.Groups["ts"].Value : File.GetLastWriteTime(path).ToString("yyyy-MM-dd HH-mm-ss");
            return pre + t + " - " + ts + ext;
        }

        // for the self-test: names read and made (no file is renamed: the statistics and favorites are real)
        public static void Test(Action<bool, string> check)
        {
            check(TitleOf("Hunt Showdown - Replay 2026-10-05 19-56-05.mp4") == null && TitleOf("Replay 2026-10-05 19-56-05.mp4") == null,
                  "clip names: an OBS name has no name of its own");
            check(TitleOf("Hunt Showdown - Bridge duel - 2026-10-05 19-56-05.mp4") == "Bridge duel" && TitleOf("Duel - on - a bridge - 2026-10-05 19-56-05.mp4") == "on - a bridge"
                  && TitleOf("Bridge duel - 2026-10-05 19-56-05.mp4") == "Bridge duel", "clip names: a name between the game and the time");
            check(NameFor(@"D:\c\Hunt Showdown - Replay 2026-10-05 19-56-05.mp4", "Тройное у Блэкфорда", true) == "Hunt Showdown - Тройное у Блэкфорда - 2026-10-05 19-56-05.mp4"
                  && NameFor(@"D:\c\Replay 2026-10-05 19-56-05.mkv", "Duel", true) == "Duel - 2026-10-05 19-56-05.mkv"
                  && NameFor(@"D:\c\Hunt Showdown - Old - 2026-10-05 19-56-05.mp4", "New", true) == "Hunt Showdown - New - 2026-10-05 19-56-05.mp4",
                  "clip names: a source keeps its game and time");
            check(NameFor(@"D:\r\Hunt Showdown 05.10 19-56.mp4", "Best: of *the* night?", false) == "Best of the night.mp4",
                  "clip names: a ready clip is its title, without what a file name can't hold");
            check(NameFor(@"D:\c\x.mp4", "  ", true) == null, "clip names: an empty name is refused");
        }

        // renames the file; the star, the statistics and the last clip follow it. null — done, otherwise what went wrong
        public static string Rename(string path, string title, bool source, out string renamed)
        {
            renamed = path;
            string name = NameFor(path, title, source);
            if (name == null) return L.T("The name is empty", "Пустое название");
            string dir = Path.GetDirectoryName(path), to = Path.Combine(dir, name);
            if (string.Equals(to, path, StringComparison.Ordinal)) return null;
            // another clip has this name already: "(2)" goes before the extension
            for (int i = 2; File.Exists(to) && !string.Equals(to, path, StringComparison.OrdinalIgnoreCase); i++)
                to = Path.Combine(dir, Path.GetFileNameWithoutExtension(name) + " (" + i + ")" + Path.GetExtension(name));
            try { File.Move(path, to); }
            catch (IOException ex) { return L.T("The file is busy — close it in the player and try again (", "Файл занят — закрой его в плеере и попробуй снова (") + ex.Message + ")"; }
            catch (Exception ex) { return ex.Message; }
            Favorites.Renamed(path, to);
            Reviewed.Renamed(path, to);
            ClipStats.Renamed(Path.GetFileName(path), Path.GetFileName(to));
            Log.Write("renamed: " + path + " → " + Path.GetFileName(to));
            renamed = to;
            return null;
        }
    }
}
