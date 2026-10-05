using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace DeviceGuard
{
    // Updates: asks GitHub for the latest release (once a day, or by the button in Settings → General).
    // Installing: the new ClipKeeper.exe is downloaded next to the running one, checked against the release's SHA256SUMS.txt,
    // the running exe is renamed to *.old.exe (Windows allows renaming a running exe, not deleting it), the new one takes
    // its name and starts. Settings and data stay where they are.
    // Rollback: update.json next to the exe remembers the swap. The new version marks it "started" on its first start and
    // forgets it (and deletes the old exe) after a minute of work or on a normal exit. If two starts in a row end without
    // that, the new version crashes on start — the old exe is put back and started instead.
    static class Updates
    {
        const string LatestUrl = "https://api.github.com/repos/Alukkart/ClipKeeper/releases/latest";
        const string ExeAsset = "ClipKeeper.exe", SumsAsset = "SHA256SUMS.txt";

        public class Release { public string Version, Page, ExeUrl, SumsUrl; }

        public static Release Newer;        // a release newer than this copy; null — none or not checked yet
        public static DateTime CheckedAt;   // last successful check
        public static string Error;         // last check failed

        // a local build ("0.0.0-dev") is not updated by itself; the button in Settings still works
        public static bool LocalBuild { get { return Program.Version.StartsWith("0.0.0"); } }

        // "1.2.10" > "1.2.9"; "1.2.0" > "1.2.0-beta.1"; anything that is not a version is the oldest
        public static int Compare(string a, string b)
        {
            Func<string, int[]> nums = v =>
            {
                var m = System.Text.RegularExpressions.Regex.Match(v ?? "", @"^v?(\d+)\.(\d+)\.(\d+)");
                return m.Success ? new[] { int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value) } : null;
            };
            Func<string, string> pre = v => { int i = (v ?? "").IndexOf('-'); return i < 0 ? "" : v.Substring(i + 1); };
            var x = nums(a);
            var y = nums(b);
            if (x == null || y == null) return x == null ? (y == null ? 0 : -1) : 1;
            for (int i = 0; i < 3; i++)
                if (x[i] != y[i]) return x[i].CompareTo(y[i]);
            string pa = pre(a), pb = pre(b);
            if (pa == pb) return 0;
            if (pa == "") return 1;
            if (pb == "") return -1;
            // dot-separated parts: numbers by value ("beta.10" > "beta.9"), words as text, a number before a word
            var xa = pa.Split('.');
            var xb = pb.Split('.');
            for (int i = 0; i < Math.Min(xa.Length, xb.Length); i++)
            {
                int na, nb;
                bool da = int.TryParse(xa[i], out na), db = int.TryParse(xb[i], out nb);
                int c = da && db ? na.CompareTo(nb) : da ? -1 : db ? 1 : string.CompareOrdinal(xa[i], xb[i]);
                if (c != 0) return c;
            }
            return xa.Length.CompareTo(xb.Length);
        }

        // the GitHub answer for releases/latest
        public static Release Parse(string json)
        {
            var d = Json.Obj(Json.Parse(json));
            string tag = Json.GetStr(d, "tag_name");
            if (string.IsNullOrEmpty(tag)) return null;
            var r = new Release { Version = tag.TrimStart('v'), Page = Json.GetStr(d, "html_url") };
            foreach (var a in (Json.GetArr(d, "assets") ?? new object[0]).Select(Json.Obj).Where(a => a != null))
            {
                string name = Json.GetStr(a, "name"), url = Json.GetStr(a, "browser_download_url");
                if (string.Equals(name, ExeAsset, StringComparison.OrdinalIgnoreCase)) r.ExeUrl = url;
                else if (string.Equals(name, SumsAsset, StringComparison.OrdinalIgnoreCase)) r.SumsUrl = url;
            }
            return r;
        }

        // "hash  name" lines (as the release workflow writes them) → the hash of the file
        public static string HashOf(string sums, string file)
        {
            foreach (var line in (sums ?? "").Split('\n'))
            {
                var parts = line.Trim().Split(new[] { ' ', '\t', '*' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 2 && string.Equals(parts[1], file, StringComparison.OrdinalIgnoreCase)) return parts[0].ToLowerInvariant();
            }
            return null;
        }

        static WebClient Client()
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var wc = new WebClient { Encoding = Encoding.UTF8 };
            wc.Headers[HttpRequestHeader.UserAgent] = "ClipKeeper/" + Program.Version;   // GitHub refuses requests without it
            return wc;
        }

        // network; call off the UI thread. Returns the newer release or null
        public static Release Check()
        {
            try
            {
                string json;
                using (var wc = Client())
                {
                    wc.Headers[HttpRequestHeader.Accept] = "application/vnd.github+json";
                    json = wc.DownloadString(LatestUrl);
                }
                var r = Parse(json);
                Newer = r != null && Compare(r.Version, Program.Version) > 0 ? r : null;
                CheckedAt = DateTime.Now;
                Error = null;
                if (Newer != null) Log.Write("update available: " + Newer.Version);
                return Newer;
            }
            catch (Exception ex)
            {
                // 404: no published release yet, or the repository is not public
                var we = ex as WebException;
                var resp = we != null ? we.Response as HttpWebResponse : null;
                Error = resp != null && resp.StatusCode == HttpStatusCode.NotFound
                    ? L.T("no public releases on GitHub", "на GitHub нет публичных выпусков") : ex.Message;
                Log.Write("update check: " + ex.Message);
                return null;
            }
        }

        static string ExePath { get { return System.Windows.Forms.Application.ExecutablePath; } }
        static string OldPath { get { return Path.ChangeExtension(ExePath, ".old.exe"); } }
        static string NewPath { get { return Path.ChangeExtension(ExePath, ".new.exe"); } }
        static string FailedPath { get { return Path.ChangeExtension(ExePath, ".failed.exe"); } }
        static string MarkerPath { get { return Path.Combine(Program.Dir, "update.json"); } }

        // network and files; call off the UI thread. null — the new exe is in place, restart to run it; otherwise the reason
        public static string Install(Release r)
        {
            if (r == null || r.ExeUrl == null) return L.T("the release has no ClipKeeper.exe", "в выпуске нет ClipKeeper.exe");
            if (r.SumsUrl == null) return L.T("the release has no checksums, so the file can't be checked", "в выпуске нет контрольных сумм — файл нечем проверить");
            string exe = ExePath, fresh = NewPath, old = OldPath;
            if (!Program.Writable(Path.GetDirectoryName(exe)))
                return L.T("ClipKeeper is in a folder Windows protects (like Program Files), so it can't replace itself — download the new version from GitHub by hand, or move ClipKeeper to a folder of your own",
                           "ClipKeeper лежит в папке, которую защищает Windows (вроде Program Files), и не может заменить себя сам — скачай новую версию с GitHub вручную или перенеси ClipKeeper в свою папку");
            try
            {
                string sums;
                using (var wc = Client()) sums = wc.DownloadString(r.SumsUrl);
                string want = HashOf(sums, ExeAsset);
                if (want == null) return L.T("ClipKeeper.exe is not in the checksums", "ClipKeeper.exe нет в контрольных суммах");
                using (var wc = Client()) wc.DownloadFile(r.ExeUrl, fresh);
                string got;
                using (var sha = SHA256.Create())
                using (var f = File.OpenRead(fresh))
                    got = string.Concat(sha.ComputeHash(f).Select(b => b.ToString("x2")));
                if (got != want)
                {
                    File.Delete(fresh);
                    Log.Write("update " + r.Version + ": checksum mismatch, the file is deleted");
                    return L.T("the downloaded file is damaged (checksum mismatch)", "скачанный файл повреждён (не совпала контрольная сумма)");
                }
                if (File.Exists(old)) File.Delete(old);
                // the rollback marker first: no update goes in without it
                Json.WriteFile(MarkerPath, Json.D("from", Program.Version, "to", r.Version, "started", false));
                try
                {
                    File.Move(exe, old);
                    try { File.Move(fresh, exe); }
                    catch { File.Move(old, exe); throw; }   // put the running exe back under its name
                }
                catch { try { File.Delete(MarkerPath); } catch { } throw; }
                Log.Write("update " + Program.Version + " → " + r.Version + " installed, restarting");
                return null;
            }
            catch (Exception ex)
            {
                try { if (File.Exists(fresh)) File.Delete(fresh); } catch { }
                Log.Write("update " + r.Version + ": " + ex.Message);
                return ex.Message;
            }
        }

        static void Remove(string p)
        {
            try { if (File.Exists(p)) { File.Delete(p); Log.Write("update: removed " + Path.GetFileName(p)); } }
            catch (Exception ex) { Log.Write("update: " + Path.GetFileName(p) + " not removed: " + ex.Message); }
        }

        // on start. true — the last two starts of this (new) version did not survive: the old exe is back and starting,
        // this copy must exit at once. One start that ended without a normal exit is not enough — a power cut or
        // Task Manager in the first minute is not a crash; a version that really crashes on start does it again.
        // args — this start's arguments, the old exe gets them too ("--tray" from autostart)
        public static bool OnStart(string[] args)
        {
            Remove(NewPath);   // a download that did not finish
            Dictionary<string, object> m = null;
            try { if (File.Exists(MarkerPath)) m = Json.Obj(Json.Parse(File.ReadAllText(MarkerPath, Encoding.UTF8))); }
            catch (Exception ex) { Log.Write("update.json: " + ex.Message); }
            if (m == null)
            {
                Remove(OldPath);
                Remove(FailedPath);
                return false;
            }
            int starts = Json.GetBool(m, "started", false) ? Math.Max(1, Json.GetInt(m, "starts", 1)) : 0;
            if (starts < 2)
            {
                m["started"] = true;   // until Confirm: if this copy dies twice, the next start rolls back
                m["starts"] = starts + 1;
                if (starts > 0) Log.Write("update " + Json.GetStr(m, "to") + ": the last start ended without a normal exit, trying once more");
                try { Json.WriteFile(MarkerPath, m); } catch (Exception ex) { Log.Write("update.json: " + ex.Message); }
                return false;
            }
            return RollBack(Json.GetStr(m, "from"), Json.GetStr(m, "to"), args);
        }

        static bool RollBack(string from, string to, string[] args)
        {
            string exe = ExePath, old = OldPath, failed = FailedPath;
            try { File.Delete(MarkerPath); } catch { }
            if (!File.Exists(old)) { Log.Write("update " + to + " did not survive its start, but there is no old exe to go back to"); return false; }
            try
            {
                if (File.Exists(failed)) File.Delete(failed);
                File.Move(exe, failed);
                try { File.Move(old, exe); }
                catch { File.Move(failed, exe); throw; }   // never leave the folder without ClipKeeper.exe
                Json.WriteFile(FailedNotePath, Json.D("version", to, "back", from));
                Log.Write("update " + to + " did not survive its start — rolled back to " + from);
                using (var me = System.Diagnostics.Process.GetCurrentProcess())
                    System.Diagnostics.Process.Start(exe, "--after " + me.Id + string.Concat((args ?? new string[0]).Select(a => " \"" + a + "\"")));
                return true;
            }
            catch (Exception ex)
            {
                Log.Write("update rollback: " + ex.Message);
                return false;
            }
        }

        // the new version works (a minute without a crash, or a normal exit): forget the old one.
        // Only the new version confirms: the old one exits right after Install with the marker not "started" yet
        public static void Confirm()
        {
            if (!File.Exists(MarkerPath)) return;
            try
            {
                var m = Json.Obj(Json.Parse(File.ReadAllText(MarkerPath, Encoding.UTF8)));
                if (!Json.GetBool(m, "started", false)) return;
                File.Delete(MarkerPath);
            }
            catch (Exception ex) { Log.Write("update.json: " + ex.Message); return; }
            Log.Write("update to " + Program.Version + " confirmed");
            Remove(OldPath);
        }

        static string FailedNotePath { get { return Path.Combine(Program.Dir, "update-failed.json"); } }

        // after a rollback: the version that failed (read once); null — nothing happened
        public static string TakeFailed()
        {
            if (!File.Exists(FailedNotePath)) return null;
            try { return Json.GetStr(Json.Obj(Json.Parse(File.ReadAllText(FailedNotePath, Encoding.UTF8))), "version"); }
            catch { return null; }
            finally { try { File.Delete(FailedNotePath); } catch { } }
        }
    }
}
