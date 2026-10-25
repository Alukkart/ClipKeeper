using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace DeviceGuard
{
    // Clips saved for sharing (the editor's "Share" mode and its one-click tiles) are usually sent once and never looked at
    // again, so they don't go to Ready: they go to a "sent" folder in ClipKeeper's data, which the library doesn't show.
    // After N days (a setting, 0 — never) they go to the Windows Recycle Bin; "Keep in Ready" in the editor saves one for good.
    static class SentClips
    {
        public static string Dir { get { return Path.Combine(Program.Dir, "sent"); } }

        public static bool IsSent(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            string dir = Path.GetDirectoryName(path) ?? "";
            return string.Equals(dir.TrimEnd('\\'), Dir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
        }

        // files in the folder older than `days` (by the later of modified and created); nothing when days is 0 — kept
        public static List<FileInfo> Expired(string dir, int days, DateTime now)
        {
            if (days <= 0 || !Directory.Exists(dir)) return new List<FileInfo>();
            try
            {
                return new DirectoryInfo(dir).GetFiles().Where(f => (f.Attributes & FileAttributes.Hidden) == 0 && now - Cleanup.AgeOf(f) > TimeSpan.FromDays(days)).ToList();
            }
            catch (Exception ex) { Log.Write("sent clips: " + ex.Message); return new List<FileInfo>(); }
        }

        // the old ones to the Recycle Bin; a file still open (an upload going on) stays until the next time
        public static int Prune(int days)
        {
            int moved = 0;
            foreach (var f in Expired(Dir, days, DateTime.Now))
                if (Shell.Recycle(f.FullName)) { moved++; Log.Write("sent clip to the Recycle Bin after " + days + " d: " + f.Name); }
            return moved;
        }

        // the saved file moves to where trims go (Ready, or next to the source); null if it could not
        public static string Keep(string file, string readyDir, string source)
        {
            string dir = !string.IsNullOrEmpty(readyDir) && Directory.Exists(readyDir) ? readyDir : Path.GetDirectoryName(source);
            string name = Path.GetFileNameWithoutExtension(file), ext = Path.GetExtension(file);
            string path = Path.Combine(dir, name + ext);
            for (int i = 2; File.Exists(path); i++) path = Path.Combine(dir, name + " " + i + ext);
            try { File.Move(file, path); return path; }
            catch (Exception ex) { Log.Write("could not keep " + file + ": " + ex.Message); return null; }
        }
    }
}
