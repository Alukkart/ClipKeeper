using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace DeviceGuard
{
    // "Find OBS" in the first-run setup: the WebSocket server settings OBS keeps on this computer, so nobody has to copy
    // the password by hand. obs-websocket 5 kept them in plugin_config\obs-websocket\config.json; since OBS 30.1 they are
    // the [OBSWebSocket] section of global.ini, since OBS 31 of user.ini. The newest file that has them wins.
    static class ObsFind
    {
        public class Found
        {
            public bool Enabled, Auth;
            public int Port = 4455;
            public string Password, From;
        }

        static string ObsDir { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "obs-studio"); } }

        // null — OBS settings are not on this computer (OBS is not installed or never started)
        public static Found Read()
        {
            var found = new List<Tuple<DateTime, Found>>();
            foreach (var ini in new[] { "user.ini", "global.ini" })
            {
                string path = Path.Combine(ObsDir, ini);
                var f = FromIni(path);
                if (f != null) found.Add(Tuple.Create(File.GetLastWriteTime(path), f));
            }
            string json = Path.Combine(ObsDir, @"plugin_config\obs-websocket\config.json");
            var j = FromJson(json);
            if (j != null) found.Add(Tuple.Create(File.GetLastWriteTime(json), j));
            return found.OrderByDescending(t => t.Item1).Select(t => t.Item2).FirstOrDefault();
        }

        public static Found FromIni(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                bool inSection = false, any = false;
                var f = new Found { From = path };
                foreach (var raw in File.ReadAllLines(path))
                {
                    string line = raw.Trim();
                    if (line.StartsWith("[")) { inSection = line.Equals("[OBSWebSocket]", StringComparison.OrdinalIgnoreCase); continue; }
                    int eq = line.IndexOf('=');
                    if (!inSection || eq <= 0) continue;
                    string k = line.Substring(0, eq).Trim(), v = line.Substring(eq + 1).Trim();
                    int n;
                    if (k == "ServerEnabled") { f.Enabled = v == "true"; any = true; }
                    else if (k == "AuthRequired") f.Auth = v == "true";
                    else if (k == "ServerPort" && int.TryParse(v, out n)) f.Port = n;
                    else if (k == "ServerPassword") f.Password = v;
                }
                return any ? f : null;
            }
            catch (Exception ex) { Log.Write("OBS settings " + path + ": " + ex.Message); return null; }
        }

        public static Found FromJson(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                var d = Json.Obj(Json.Parse(File.ReadAllText(path)));
                return new Found
                {
                    From = path, Enabled = Json.GetBool(d, "server_enabled", false), Auth = Json.GetBool(d, "auth_required", false),
                    Port = Json.GetInt(d, "server_port", 4455), Password = Json.GetStr(d, "server_password"),
                };
            }
            catch (Exception ex) { Log.Write("OBS settings " + path + ": " + ex.Message); return null; }
        }
    }
}
