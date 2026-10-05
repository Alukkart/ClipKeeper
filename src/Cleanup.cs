using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace DeviceGuard
{
    // Cleanup of old source clips. People must never lose a clip they care about, so it is careful by design:
    //   • off by default; by hand it first shows what would go (count, size, the full list), then asks twice;
    //   • only video files in the OBS recording folder; the Ready and Collection folders are never touched, even inside it;
    //   • never: favorites, trims (ClipKeeper's own files), sources that already have a trim (a setting, on by default),
    //     anything newer than N days by the later of "modified" and "created" (a just-copied old clip stays);
    //   • only to the Recycle Bin, only on local disks; if Windows wants to delete for good (the bin is too small),
    //     it asks instead of deleting (the automatic run doesn't ask — it checks the bin first and stops);
    //     without ffmpeg trims can't be told from sources, and with the Ready / Collection folder unavailable their trims
    //     can't be seen — then it refuses;
    //   • the automatic run stops and asks if more than 300 clips or half of the folder would go;
    //   • every file it moved is written to cleanup.log.
    static class Cleanup
    {
        public const int AutoMaxFiles = 300;
        static readonly Regex Video = new Regex(@"\.(mp4|mkv|mov|flv)$", RegexOptions.IgnoreCase);

        public class Plan
        {
            public List<FileInfo> Files = new List<FileInfo>();
            public long Bytes;
            public int Total;          // all videos in the folder (outside Ready / Collection)
            public string Refusal;     // why nothing can be planned
            public string Root;
        }

        public static string LogPath { get { return Path.Combine(Program.Dir, "cleanup.log"); } }
        public static string ListPath { get { return Path.Combine(Program.Dir, "cleanup-preview.txt"); } }

        // the age that counts: the later of modified and created — a clip copied in yesterday is "yesterday's"
        public static DateTime AgeOf(FileInfo f)
        {
            return f.LastWriteTime > f.CreationTime ? f.LastWriteTime : f.CreationTime;
        }

        static bool Inside(string path, string dir)
        {
            if (string.IsNullOrEmpty(dir)) return false;
            string d = dir.TrimEnd('\\') + "\\";
            return path.StartsWith(d, StringComparison.OrdinalIgnoreCase) || string.Equals(path.TrimEnd('\\'), dir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
        }

        static IEnumerable<FileInfo> Videos(string dir)
        {
            var stack = new Stack<string>();
            stack.Push(dir);
            while (stack.Count > 0)
            {
                string d = stack.Pop();
                FileInfo[] files;
                string[] subs;
                try
                {
                    var di = new DirectoryInfo(d);
                    files = di.GetFiles();
                    // links (junctions, symlinks) are not followed: they may lead in a circle or into Ready / Collection
                    subs = di.GetDirectories().Where(x => (x.Attributes & FileAttributes.ReparsePoint) == 0).Select(x => x.FullName).ToArray();
                }
                catch (Exception ex) { Log.Write("cleanup: skipped " + d + ": " + ex.Message); continue; }   // no access — leave it alone
                foreach (var f in files) if (Video.IsMatch(f.Name) && (f.Attributes & FileAttributes.ReparsePoint) == 0) yield return f;
                foreach (var s in subs) stack.Push(s);
            }
        }

        // what would go; deletes nothing. meta(file) — ClipKeeper's data in the file (a trim has it; may come from the cache,
        // throws if it can't be read), metaStrict(file) — the same read from the file itself; isFavorite(path)
        public static Plan Make(string root, int days, bool keepTrimmed, string[] ownRoots, Func<FileInfo, ClipMeta> meta,
                                Func<FileInfo, ClipMeta> metaStrict, Func<string, bool> isFavorite, bool metaAvailable, DateTime now)
        {
            var p = new Plan { Root = root };
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
            { p.Refusal = L.T("the recording folder is not known yet — connect OBS", "папка записи ещё неизвестна — подключи OBS"); return p; }
            try
            {
                var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(root)));
                if (drive.DriveType != DriveType.Fixed)
                { p.Refusal = L.T("the recording folder is not on a local disk — there is no Recycle Bin there", "папка записи не на локальном диске — там нет корзины"); return p; }
            }
            catch (Exception ex) { p.Refusal = ex.Message; return p; }
            if (!metaAvailable)
            { p.Refusal = L.T("ffmpeg is needed to tell trims from sources", "нужен ffmpeg, чтобы отличать обрезки от исходников"); return p; }
            if (days < 7) days = 7;

            var own = (ownRoots ?? new string[0]).Where(r => !string.IsNullOrEmpty(r)).ToArray();
            // a Ready / Collection folder that is set but not there (a disk unplugged): its trims can't protect their sources
            string missing = keepTrimmed ? own.FirstOrDefault(r => !Directory.Exists(r)) : null;
            if (missing != null)
            { p.Refusal = L.T("the folder ", "папка ") + missing + L.T(" is not available — connect the disk or choose the folder again", " недоступна — подключи диск или выбери папку заново"); return p; }
            var all = Videos(root).Where(f => !own.Any(r => Inside(f.FullName, r))).ToList();
            p.Total = all.Count;

            // sources that already have a trim: trims carry the source file name (in Ready, Collection or next to the source)
            var trimmed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (keepTrimmed)
                foreach (var f in all.Concat(own.SelectMany(Videos)))
                {
                    ClipMeta m;
                    try { m = meta(f); }
                    catch (Exception ex)
                    {
                        // a file being written right now (a recording) can't be read yet — it is not a trim of anything old
                        if ((now - f.LastWriteTime).TotalMinutes < 30) continue;
                        // an unreadable trim would leave its source unprotected — better not to clean up at all
                        p.Refusal = L.T("can't read ", "не читается ") + f.Name + L.T(" — try again later", " — попробуй позже") + " (" + ex.Message + ")";
                        p.Files.Clear();
                        p.Bytes = 0;
                        return p;
                    }
                    if (m != null && !string.IsNullOrEmpty(m.Source)) trimmed.Add(m.Source);
                }

            var cutoff = now.AddDays(-days);
            foreach (var f in all)
            {
                if (AgeOf(f) >= cutoff) continue;
                if (isFavorite(f.FullName)) continue;
                if (keepTrimmed && trimmed.Contains(f.Name)) continue;
                // a trim, not a source: the remembered data saying "trim" is enough to keep it; "no data" is read again from
                // the file itself, not the cache, before the file may go; can't read it — keep it
                try { if (meta(f) != null || metaStrict(f) != null) continue; }
                catch (Exception ex) { Log.Write("cleanup: kept " + f.Name + " (can't read it: " + ex.Message + ")"); continue; }
                p.Files.Add(f);
                p.Bytes += f.Length;
            }
            p.Files = p.Files.OrderBy(AgeOf).ToList();
            return p;
        }

        // the automatic run does not act on a plan that looks wrong (a clock jump, a copied folder) — it asks instead
        public static bool TooMuchForAuto(Plan p)
        {
            return p.Files.Count > AutoMaxFiles || p.Files.Count * 2 > p.Total;
        }

        public static void WriteList(Plan p)
        {
            var sb = new StringBuilder();
            sb.AppendLine(L.T("ClipKeeper: these clips would go to the Recycle Bin (", "ClipKeeper: эти клипы уйдут в корзину (") +
                          p.Files.Count + L.T(" clips, ", " клипов, ") + Fmt.Size(p.Bytes) + ")");
            sb.AppendLine(p.Root);
            sb.AppendLine();
            foreach (var f in p.Files) sb.AppendLine(AgeOf(f).ToString("yyyy-MM-dd") + "  " + Fmt.Size(f.Length).PadLeft(9) + "  " + f.FullName);
            File.WriteAllText(ListPath, sb.ToString(), Encoding.UTF8);
        }

        public class Result { public int Moved, Kept; public long Bytes; public bool Stopped; }

        // to the Recycle Bin, one file at a time; each one is checked again right before (a star could have been added)
        public static Result Run(Plan p, Func<string, bool> isFavorite, bool auto)
        {
            var r = new Result();
            foreach (var f in p.Files)
            {
                f.Refresh();
                if (!f.Exists || isFavorite(f.FullName)) { r.Kept++; continue; }
                long size = f.Length;
                // the automatic run must not bring up Windows' "delete permanently?" over a game: if the Recycle Bin can't
                // take the file, it stops as if that question was answered "no"
                bool declined = auto && !BinTakes(f.FullName, size);
                bool ok = !declined && Recycle(f.FullName, out declined);
                if (declined)
                {
                    // Windows wanted to delete for good (no room in the Recycle Bin) and you said no — don't ask about every file
                    r.Stopped = true;
                    r.Kept += p.Files.Count - r.Moved - r.Kept;
                    Log.Write("cleanup stopped: Windows would delete " + f.Name + " for good instead of recycling it");
                    break;
                }
                if (ok && !File.Exists(f.FullName))
                {
                    r.Moved++;
                    r.Bytes += size;
                    try
                    {
                        File.AppendAllText(LogPath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + (auto ? "  auto  " : "  hand  ") +
                                           Fmt.Size(size).PadLeft(9) + "  " + f.FullName + Environment.NewLine, Encoding.UTF8);
                    }
                    catch { }
                }
                else r.Kept++;
            }
            Log.Write("cleanup (" + (auto ? "auto" : "by hand") + "): " + r.Moved + " clips to the Recycle Bin, " + r.Kept + " kept");
            return r;
        }

        // SHFileOperation with undo (the Recycle Bin), no "are you sure", but with the warning if Windows would delete for good
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct ShFileOp
        {
            public IntPtr Hwnd;
            public uint Func;
            public string From, To;
            public ushort Flags;
            public bool Aborted;
            public IntPtr Mappings;
            public string Title;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern int SHFileOperation(ref ShFileOp op);

        const uint FoDelete = 3;
        const ushort FofSilent = 0x4, FofNoConfirmation = 0x10, FofAllowUndo = 0x40, FofNoErrorUi = 0x400, FofWantNukeWarning = 0x4000;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool GetVolumePathName(string file, StringBuilder volumePath, int length);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool GetVolumeNameForVolumeMountPoint(string mountPoint, StringBuilder volumeName, int length);

        // whether the Recycle Bin of the file's disk takes it instead of deleting for good: on, and the file fits into its size
        // (a full bin makes room by itself). The settings of every disk are under BitBucket\Volume\{volume GUID};
        // not found or not readable — false, the automatic run does not guess
        public static bool BinTakes(string path, long size)
        {
            try
            {
                var mount = new StringBuilder(1024);
                var volume = new StringBuilder(64);
                if (!GetVolumePathName(path, mount, mount.Capacity) || !GetVolumeNameForVolumeMountPoint(mount.ToString(), volume, volume.Capacity))
                    return false;
                var m = Regex.Match(volume.ToString(), @"\{[0-9a-fA-F-]+\}");
                if (!m.Success) return false;
                using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\BitBucket\Volume\" + m.Value))
                {
                    if (k == null) return false;
                    if (Convert.ToInt32(k.GetValue("NukeOnDelete", 0)) != 0) return false;
                    object cap = k.GetValue("MaxCapacity");   // MB
                    return cap != null && size <= Convert.ToInt64(cap) * 1024 * 1024;
                }
            }
            catch (Exception ex) { Log.Write("cleanup: Recycle Bin of " + path + " not known: " + ex.Message); return false; }
        }

        static bool Recycle(string path, out bool declined)
        {
            declined = false;
            try
            {
                var op = new ShFileOp
                {
                    Func = FoDelete, From = path + "\0\0",
                    Flags = (ushort)(FofAllowUndo | FofNoConfirmation | FofSilent | FofNoErrorUi | FofWantNukeWarning),
                };
                int rc = SHFileOperation(ref op);
                declined = op.Aborted;
                if (rc != 0 || op.Aborted) { Log.Write("cleanup: kept " + path + " (code " + rc + (op.Aborted ? ", declined" : "") + ")"); return false; }
                return true;
            }
            catch (Exception ex) { Log.Write("cleanup: kept " + path + ": " + ex.Message); return false; }
        }
    }
}
