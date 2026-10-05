using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace DeviceGuard
{
    class DevItem
    {
        public string Name, Value;
        public DevItem(string name, string value) { Name = name; Value = value; }
    }

    class Resolution
    {
        public string Kind;   // "ok" | "fix" | "missing"
        public string Value, Name, How;
    }

    // Finds a reference device among the ones present now (same logic as device_guard.py)
    static class Matcher
    {
        public static bool IsMonitor(string type) { return type == "monitor_capture"; }
        public static bool IsAudio(string type) { return type == "wasapi_input_capture" || type == "wasapi_output_capture"; }

        // case, spaces and Windows duplicate prefixes like "2- "
        public static string Norm(string n)
        {
            n = (n ?? "").ToLowerInvariant().Trim();
            n = Regex.Replace(n, @"^\d+\s*-\s*", "");
            n = Regex.Replace(n, @"\(\d+\s*-\s*", "(");
            return Regex.Replace(n, @"\s+", " ");
        }

        // name without the bracketed tail: "Sonar - Gaming (Virtual Audio Device)" -> "sonar - gaming"
        public static string Base(string n)
        {
            return Regex.Replace(Norm(n), @"\s*\([^()]*\)\s*$", "").Trim();
        }

        // model code from the monitor path: \\?\DISPLAY#MSI4CE2#... -> MSI4CE2
        public static string MonitorHw(string v)
        {
            var m = Regex.Match(v ?? "", @"DISPLAY#([^#]+)#", RegexOptions.IgnoreCase);
            return m.Success ? m.Groups[1].Value.ToUpperInvariant() : "";
        }

        // «MSI MAG 275QF: 2560x1440 @ 0,0 (...)» -> «msi mag 275qf»
        public static string MonitorModel(string n)
        {
            var m = Regex.Match(n ?? "", @"^(.*?):\s*\d+\s*x\s*\d+");
            return m.Success ? m.Groups[1].Value.Trim().ToLowerInvariant() : "";
        }

        static List<string> Names(RefEntry r)
        {
            var l = new List<string>();
            if (!string.IsNullOrEmpty(r.Display)) l.Add(r.Display);
            if (!string.IsNullOrEmpty(r.LastName) && r.LastName != r.Display) l.Add(r.LastName);
            return l;
        }

        // the value is in the list and is not a placeholder like "[device unavailable]"
        static bool Alive(string value, Dictionary<string, string> byVal, List<string> names)
        {
            string n;
            if (value == null || !byVal.TryGetValue(value, out n)) return false;
            return !n.StartsWith("[") || names.Any(w => w.StartsWith("["));
        }

        public static Resolution Resolve(string type, string cur, RefEntry r, List<DevItem> items)
        {
            if (items == null) return new Resolution { Kind = "missing", How = L.T("could not get the device list", "не удалось получить список устройств") };
            var byVal = new Dictionary<string, string>();
            foreach (var it in items) if (!byVal.ContainsKey(it.Value)) byVal[it.Value] = it.Name;
            var names = Names(r);

            if (cur == r.Value && Alive(cur, byVal, names))
                return new Resolution { Kind = "ok", Value = cur, Name = byVal[cur] };
            string how;
            string target = FindTarget(type, r, items, byVal, names, out how);
            if (target == null) return new Resolution { Kind = "missing", How = how };
            if (target == cur) return new Resolution { Kind = "ok", Value = cur, Name = byVal[cur] };
            return new Resolution { Kind = "fix", Value = target, Name = byVal[target], How = how };
        }

        static string FindTarget(string type, RefEntry r, List<DevItem> items, Dictionary<string, string> byVal,
                                 List<string> names, out string how)
        {
            string want = r.Value ?? "";
            if (want != "" && Alive(want, byVal, names)) { how = L.T("by ID", "по ID"); return want; }
            if (names.Count == 0)
            {
                how = L.T("the reference has no device name — press \"Remember current devices\"", "в эталоне нет имени устройства — нажмите «Запомнить текущие устройства»");
                return null;
            }

            // "Default" is never used in place of a specific device
            var cands = items.Where(i => i.Value != "default" || want == "default").ToList();

            foreach (var w in names)
            {
                var exact = cands.FirstOrDefault(i => i.Name == w);
                if (exact != null) { how = L.T("by name", "по имени"); return exact.Value; }
            }

            List<string> ambiguous = null;
            var keys = new List<KeyValuePair<string, Func<string, string>>>
            {
                new KeyValuePair<string, Func<string, string>>(L.T("by name ignoring case/number", "по имени без учёта регистра/номера"), Norm),
                new KeyValuePair<string, Func<string, string>>(L.T("by name without brackets", "по имени без скобок"), Base),
            };
            foreach (var kv in keys)
            {
                foreach (var w in names)
                {
                    string k = kv.Value(w);
                    if (k == "") continue;
                    var found = cands.Where(i => kv.Value(i.Name) == k).ToList();
                    if (found.Count == 1) { how = kv.Key; return found[0].Value; }
                    if (found.Count > 1) ambiguous = found.Select(i => i.Name).ToList();
                }
            }

            if (IsMonitor(type))
            {
                string hw = MonitorHw(want);
                if (hw != "")
                {
                    var byHw = cands.Where(i => MonitorHw(i.Value) == hw).ToList();
                    if (byHw.Count == 1) { how = L.T("by monitor model", "по модели монитора"); return byHw[0].Value; }
                }
                foreach (var w in names)
                {
                    string model = MonitorModel(w);
                    if (model == "") continue;
                    var byModel = cands.Where(i => MonitorModel(i.Name) == model).ToList();
                    if (byModel.Count == 1) { how = L.T("by monitor name", "по названию монитора"); return byModel[0].Value; }
                }
            }

            how = ambiguous != null
                ? L.T("several similar devices: ", "несколько похожих устройств: ") + string.Join(", ", ambiguous)
                : L.T("\"" + names[0] + "\" is not in the system", "«" + names[0] + "» не найдено в системе");
            return null;
        }
    }
}
