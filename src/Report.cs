using System;
using System.Diagnostics;
using System.Linq;
using Microsoft.Win32;

namespace DeviceGuard
{
    // "Report a problem": the GitHub issue form (.github/ISSUE_TEMPLATE/bug.yml) with the versions filled in, and the log shown
    // in Explorer so it can be dragged into the form. Also what a crash offers. Nothing is sent by itself — the person sees
    // the form and decides what to post
    static class Report
    {
        public const string Repo = "https://github.com/Alukkart/ClipKeeper";

        // "Windows 11 Pro 24H2 (26100)" — Environment.OSVersion says 6.2 for programs without a manifest
        public static string WindowsVersion()
        {
            try
            {
                using (var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
                {
                    string name = k.GetValue("ProductName") as string ?? "Windows", disp = k.GetValue("DisplayVersion") as string,
                           build = k.GetValue("CurrentBuild") as string ?? "";
                    int b;
                    // Windows 11 still says "Windows 10" in ProductName
                    if (int.TryParse(build, out b) && b >= 22000) name = name.Replace("Windows 10", "Windows 11");
                    return name + (disp != null ? " " + disp : "") + " (" + build + ")";
                }
            }
            catch { return Environment.OSVersion.VersionString; }
        }

        public static string IssueUrl(string title, string what)
        {
            Func<string, string> e = s => Uri.EscapeDataString(s ?? "");
            string url = Repo + "/issues/new?template=bug.yml&version=" + e(Program.Version) + "&windows=" + e(WindowsVersion());
            if (!string.IsNullOrEmpty(title)) url += "&title=" + e(title);
            if (!string.IsNullOrEmpty(what)) url += "&what=" + e(what.Length > 1500 ? what.Substring(0, 1500) + "…" : what);
            return url;
        }

        public static void Open(string title, string what)
        {
            try { Process.Start(IssueUrl(title, what)); } catch (Exception ex) { Log.Write("report: " + ex.Message); }
            Shell.Select(Log.FilePath);   // to drag into the form
        }

        // the program is about to end: say what happened and offer the form (a plain Windows dialog — WPF may be broken by now)
        public static void Crashed(Exception ex)
        {
            try
            {
                string msg = ex != null ? ex.GetType().Name + ": " + ex.Message : "unknown error";
                var answer = System.Windows.Forms.MessageBox.Show(
                    L.T("ClipKeeper ran into an error and has to close:\n\n", "ClipKeeper столкнулся с ошибкой и должен закрыться:\n\n") + msg +
                    L.T("\n\nThe details are in the log:\n", "\n\nПодробности в журнале:\n") + Log.FilePath +
                    L.T("\n\nReport it on GitHub? A form opens with the versions and the error filled in; nothing is sent by itself.",
                        "\n\nСообщить на GitHub? Откроется форма с уже подставленными версиями и ошибкой; само ничего не отправляется."),
                    "ClipKeeper", System.Windows.Forms.MessageBoxButtons.YesNo, System.Windows.Forms.MessageBoxIcon.Error);
                if (answer == System.Windows.Forms.DialogResult.Yes)
                {
                    string stack = ex != null && ex.StackTrace != null ? string.Join("\n", ex.StackTrace.Split('\n').Take(12)) : "";
                    Open(L.T("Crash: ", "Падение: ") + msg, msg + "\n\n```\n" + stack + "\n```");
                }
            }
            catch { }
        }
    }
}
