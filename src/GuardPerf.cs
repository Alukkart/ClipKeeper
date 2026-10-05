using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace DeviceGuard
{
    // OBS frame counters (GetStats) — cumulative since OBS started
    class PerfSample
    {
        public DateTime At;
        public long RenderSkip, RenderTotal, OutSkip, OutTotal;
        public double Fps;
    }

    // Frame loss over a time span. Separate from Guard so it can be tested without OBS (--selftest).
    static class FrameStats
    {
        public class Loss
        {
            public long RenderSkip, RenderTotal, OutSkip, OutTotal;
            public double Seconds;
            public long Lost { get { return RenderSkip + OutSkip; } }
            public double Pct { get { return RenderTotal > 0 ? Lost * 100.0 / RenderTotal : 0; } }
            // who is to blame: rendering (the GPU is busy with the game) or the encoder
            public bool RenderSide { get { return RenderSkip >= OutSkip; } }
        }

        // counter difference between the last sample at or before from and the last at or before to
        public static Loss Between(IList<PerfSample> s, DateTime from, DateTime to)
        {
            PerfSample a = null, b = null;
            foreach (var x in s)
            {
                if (x.At <= from) a = x;
                if (x.At <= to) b = x;
            }
            if (a == null && s.Count > 0 && s[0].At <= to) a = s[0];   // less history than the span — use what there is
            if (a == null || b == null || ReferenceEquals(a, b)) return null;
            return new Loss
            {
                RenderSkip = b.RenderSkip - a.RenderSkip, RenderTotal = b.RenderTotal - a.RenderTotal,
                OutSkip = b.OutSkip - a.OutSkip, OutTotal = b.OutTotal - a.OutTotal,
                Seconds = (b.At - a.At).TotalSeconds,
            };
        }

        // noticeable: ≥ 1% and not a couple of random frames on a loading screen
        public static bool Noticeable(Loss l, double pct, long minFrames) { return l != null && l.Lost >= minFrames && l.Pct >= pct; }

        public static string PctText(double pct) { return pct.ToString(pct < 10 ? "0.#" : "0", L.Culture) + "%"; }

        public static string Cause(Loss l)
        {
            return l.RenderSide ? L.T("rendering can't keep up (the GPU is busy with the game)", "не успевает рендер (видеокарта занята игрой)")
                                : L.T("the video encoder can't keep up", "не успевает кодировщик видео");
        }

        public static string Advice(Loss l)
        {
            return l.RenderSide
                ? L.T("Capping the game FPS 5–10 below usual helps, or running OBS as administrator — then Windows gives it GPU priority",
                      "Помогает ограничить FPS в игре на 5–10 ниже обычного или запускать OBS от имени администратора — тогда Windows даёт ему приоритет на видеокарте")
                : L.T("Lower the encoder preset in OBS output settings (e.g. P7 → P5) or turn off multipass",
                      "Понизь пресет кодировщика в настройках вывода OBS (например, P7 → P5) или выключи многопроходный режим");
        }
    }

    // Check: OBS drops frames
    partial class Guard
    {
        readonly List<PerfSample> perf = new List<PerfSample>();   // under st; the last 15 minutes
        DateTime perfBadUntil = DateTime.MinValue;
        string perfNoteText;
        bool perfLogged;
        double targetFps;
        DateTime lastFpsCheck = DateTime.MinValue;
        // for the window
        volatile string uiPerfValue, uiPerfSub;
        volatile int uiPerfLevel = -1;

        void ResetPerf()
        {
            lock (st) perf.Clear();
            perfBadUntil = DateTime.MinValue;
            uiPerfLevel = -1;
        }

        void CheckPerf(DateTime now, Dictionary<string, string> notesCur)
        {
            if (!Cfg.PerfCheck) { ResetPerf(); return; }
            Dictionary<string, object> r;
            try { r = obs.Request("GetStats", null, ReqTimeout); }
            catch (ObsException ex) { if (ex.Code != 207) Log.Write("GetStats: " + ex.Message); return; }

            var s = new PerfSample
            {
                At = now,
                RenderSkip = (long)ToD(Get(r, "renderSkippedFrames")), RenderTotal = (long)ToD(Get(r, "renderTotalFrames")),
                OutSkip = (long)ToD(Get(r, "outputSkippedFrames")), OutTotal = (long)ToD(Get(r, "outputTotalFrames")),
                Fps = ToD(Get(r, "activeFps")),
            };
            if (!perfLogged)
            {
                perfLogged = true;
                Log.Write(string.Format(CultureInfo.InvariantCulture, "OBS frames: {0:0.#} fps, render skipped {1} of {2}, encoder {3} of {4} (since OBS started)",
                    s.Fps, s.RenderSkip, s.RenderTotal, s.OutSkip, s.OutTotal));
            }
            if ((now - lastFpsCheck).TotalMinutes >= 1)
            {
                lastFpsCheck = now;
                try
                {
                    var v = obs.Request("GetVideoSettings", null, ReqTimeout);
                    double num = ToD(Get(v, "fpsNumerator")), den = ToD(Get(v, "fpsDenominator"));
                    if (num > 0 && den > 0) targetFps = num / den;
                }
                catch (ObsException) { }
            }

            FrameStats.Loss minute;
            lock (st)
            {
                // OBS restarted — the counters started over
                var lastS = perf.Count > 0 ? perf[perf.Count - 1] : null;
                if (lastS != null && (s.RenderTotal < lastS.RenderTotal || s.OutTotal < lastS.OutTotal)) perf.Clear();
                perf.Add(s);
                perf.RemoveAll(x => (now - x.At).TotalMinutes > 15);
                minute = FrameStats.Between(perf, now.AddSeconds(-60), now);
            }

            bool bad = FrameStats.Noticeable(minute, 1.0, 20);
            if (bad)
            {
                perfBadUntil = now.AddMinutes(3);   // no flicker: the warning holds until 3 clean minutes
                perfNoteText = L.T("OBS is dropping frames: " + FrameStats.PctText(minute.Pct) + " per minute (" + minute.Lost + ") — " +
                                   FrameStats.Cause(minute) + ". Clips will stutter. ",
                                   "OBS теряет кадры: " + FrameStats.PctText(minute.Pct) + " за минуту (" + minute.Lost + " шт.) — " +
                                   FrameStats.Cause(minute) + ". Клипы будут дёргаться. ") + FrameStats.Advice(minute);
            }
            if (now < perfBadUntil && perfNoteText != null) notesCur["perf"] = perfNoteText;

            // for the "OBS" tile
            string fps = s.Fps > 0 ? s.Fps.ToString("0", CultureInfo.InvariantCulture) + " fps" : L.T("Connected", "Подключён");
            bool lowFps = targetFps > 0 && s.Fps > 0 && s.Fps < targetFps * 0.9;
            if (minute == null) { uiPerfLevel = lowFps ? 1 : 0; uiPerfValue = fps; uiPerfSub = L.T("counting frames…", "считаю кадры…"); }
            else if (bad || now < perfBadUntil)
            {
                uiPerfLevel = 1;
                uiPerfValue = fps;
                uiPerfSub = bad ? L.T("drops " + FrameStats.PctText(minute.Pct) + " of frames · ", "теряет " + FrameStats.PctText(minute.Pct) + " кадров · ") +
                                  (minute.RenderSide ? L.T("GPU", "видеокарта") : L.T("encoder", "кодировщик"))
                                : L.T("frames were dropped a couple of minutes ago", "кадры терялись пару минут назад");
            }
            else
            {
                uiPerfLevel = lowFps ? 1 : 0;
                uiPerfValue = fps;
                uiPerfSub = lowFps ? L.T("below the configured ", "ниже настроенных ") + targetFps.ToString("0", CultureInfo.InvariantCulture) + " fps"
                          : minute.Lost > 0 ? L.T("almost no loss: " + minute.Lost + " frames a minute", "почти без потерь: " + minute.Lost + " кадр. за минуту")
                          : L.T("no dropped frames", "кадры не теряются");
            }
        }

        static object Get(Dictionary<string, object> d, string key)
        {
            object v;
            return d != null && d.TryGetValue(key, out v) ? v : null;
        }

        // frame loss during a clip (for the clip check); null — not enough history
        FrameStats.Loss ClipLoss(DateTime savedAt, double seconds)
        {
            if (!Cfg.PerfCheck || seconds <= 0) return null;
            lock (st) return FrameStats.Between(perf, savedAt.AddSeconds(-seconds), savedAt);
        }
    }
}
