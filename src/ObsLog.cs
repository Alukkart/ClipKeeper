using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace DeviceGuard
{
    // The OBS log: what the WebSocket does not say. When the encoder fails (NVENC after a graphics driver update),
    // OBS stops the replay buffer itself and reports it exactly like a stop by hand (STOPPING, then STOPPED);
    // only its log has "Error encoding with encoder" right before the stop.
    static class ObsLog
    {
        const string StopMark = "==== Replay Buffer Stop", StartMark = "==== Replay Buffer Start", EncodeError = "Error encoding with encoder";

        static string Dir { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"obs-studio\logs"); } }

        // the line with the encoder error if the replay buffer that just stopped was stopped by it; null — a stop by hand
        // (or the log can't tell). OBS writes the stop line before it sends STOPPED, but give the file a moment anyway.
        public static string ReplayStopError()
        {
            for (int i = 0; i < 4; i++)
            {
                bool logged;
                string err = StopError(Tail(256 * 1024), out logged);
                if (logged) return err;
                Thread.Sleep(250);
            }
            return null;
        }

        // logged — the last buffer stop is in the text (no buffer start after it, so it is this stop, not an older one)
        public static string StopError(string text, out bool logged)
        {
            logged = false;
            if (string.IsNullOrEmpty(text)) return null;
            int stop = text.LastIndexOf(StopMark, StringComparison.Ordinal);
            if (stop < 0 || text.IndexOf(StartMark, stop, StringComparison.Ordinal) >= 0) return null;
            logged = true;
            // the error is a few lines above the stop ("Output '…': stopping", the frame counts); an older one,
            // from before this buffer started, does not count
            int start = text.LastIndexOf(StartMark, stop, StringComparison.Ordinal);
            int from = Math.Max(Math.Max(0, start), stop - 4096);
            var lines = text.Substring(from, stop - from).Split('\n');
            return lines.Reverse().Take(30).Select(l => l.Trim()).FirstOrDefault(l => l.Contains(EncodeError));
        }

        // the end of the current log (files are named by start time: "2026-10-08 09-45-33.txt")
        static string Tail(int bytes)
        {
            try
            {
                var file = new DirectoryInfo(Dir).GetFiles("????-??-?? ??-??-??.txt").OrderByDescending(f => f.Name, StringComparer.Ordinal).FirstOrDefault();
                if (file == null) return null;
                using (var fs = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    long n = Math.Min(bytes, fs.Length);
                    fs.Seek(-n, SeekOrigin.End);
                    var buf = new byte[n];
                    int read = 0;
                    while (read < n) { int k = fs.Read(buf, read, (int)n - read); if (k <= 0) break; read += k; }
                    return Encoding.UTF8.GetString(buf, 0, read);
                }
            }
            catch (Exception ex) { Log.Write("OBS log: " + ex.Message); return null; }
        }
    }
}
