using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Rectangle = System.Windows.Shapes.Rectangle;

namespace DeviceGuard
{
    // The trim window: the timeline, zoom, cuts, undo and redo, drawing the ruler and labels.
    partial class TrimWindow
    {
        // ── timeline ──
        void BuildTimeline()
        {
            timeline.Children.Add(canvas);
            maskL.Fill = maskR.Fill = Wpf.Br("#B30B0C0E");
            selBox.Stroke = Wpf.Res<Brush>("Accent");
            selBox.StrokeThickness = 2;
            selBox.RadiusX = selBox.RadiusY = 4;
            playhead.Fill = Brushes.White;
            playhead.Width = 2;
            playhead.IsHitTestVisible = false;
            hoverLine.Fill = Wpf.Br("#73FFFFFF");
            hoverLine.Width = 1;
            hoverLine.IsHitTestVisible = false;
            hoverLine.Visibility = Visibility.Collapsed;
            hoverText.FontFamily = Wpf.Res<FontFamily>("MonoFont");
            hoverText.FontSize = 10.5;
            hoverText.Foreground = Brushes.White;
            hoverTag.Child = hoverText;
            hoverTag.Background = Wpf.Br("#F01E1F24");
            hoverTag.CornerRadius = new CornerRadius(3);
            hoverTag.Padding = new Thickness(5, 1, 5, 1);
            hoverTag.IsHitTestVisible = false;
            hoverTag.Visibility = Visibility.Collapsed;
            canvas.Children.Add(new Border { Background = Wpf.Br("#141518"), CornerRadius = new CornerRadius(6), Tag = "bg" });
            canvas.Children.Add(ruler);
            canvas.Children.Add(strip);
            canvas.Children.Add(wave);
            canvas.Children.Add(maskL);
            canvas.Children.Add(maskR);
            canvas.Children.Add(selBox);
            pendingBox.Fill = Wpf.Br(Wpf.Bad, 0x33);
            pendingBox.Stroke = Wpf.Br(Wpf.Bad, 255);
            pendingBox.StrokeDashArray = new DoubleCollection { 3, 2 };
            pendingBox.Visibility = Visibility.Collapsed;
            pendingBox.IsHitTestVisible = false;
            canvas.Children.Add(pendingBox);
            handleL = Handle();
            handleR = Handle();
            canvas.Children.Add(handleL);
            canvas.Children.Add(handleR);
            waveCaption.FontFamily = Wpf.Res<FontFamily>("UiFont");
            waveCaption.Foreground = Wpf.Res<Brush>("Muted");
            canvas.Children.Add(waveCaption);
            canvas.Children.Add(hoverLine);
            canvas.Children.Add(playhead);
            canvas.Children.Add(hoverTag);

            canvas.Background = Brushes.Transparent;
            canvas.MouseLeftButtonDown += (s, e) =>
            {
                // the cross on a cut restores the piece
                for (var fe = e.OriginalSource as FrameworkElement; fe != null && fe != canvas; fe = VisualTreeHelper.GetParent(fe) as FrameworkElement)
                    if (fe.Tag is int) { RemoveCut((int)fe.Tag); e.Handled = true; return; }
                double x = e.GetPosition(canvas).X;
                drag = Math.Abs(x - X(inT)) <= 10 ? "in" : Math.Abs(x - X(outT)) <= 10 ? "out" : CutEdge(x) ?? "head";
                dragBefore = drag != "head" ? Snap() : null;   // edge edits can be undone
                canvas.CaptureMouse();
                Drag(x);
            };
            canvas.MouseMove += (s, e) =>
            {
                double x = e.GetPosition(canvas).X;
                ShowHover(x);
                if (drag != null) Drag(x);
                else canvas.Cursor = Math.Abs(x - X(inT)) <= 10 || Math.Abs(x - X(outT)) <= 10 || CutEdge(x) != null ? Cursors.SizeWE : Cursors.Hand;
            };
            canvas.MouseLeave += (s, e) => { if (drag == null) HideHover(); };
            canvas.MouseLeftButtonUp += (s, e) =>
            {
                if (drag != null && drag.StartsWith("c")) { NormalizeCuts(); RebuildCutVisuals(); Layout(); UpdateEstimate(); }
                if (dragBefore != null && !Same(dragBefore, Snap())) PushUndo(dragBefore);
                dragBefore = null;
                drag = null;
                canvas.ReleaseMouseCapture();
            };
            // Ctrl/Alt + wheel zooms at the cursor; the plain wheel scrolls the zoomed timeline
            canvas.MouseWheel += (s, e) =>
            {
                double x = e.GetPosition(canvas).X;
                if ((Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Alt)) != 0) ZoomAt(Tm(x), e.Delta > 0 ? 1.25 : 0.8);
                else if (Zoomed) Pan(-e.Delta / 120.0 * viewLen * 0.12);
                e.Handled = true;
            };
            timeline.SizeChanged += (s, e) => { rulerSig = null; Layout(); };
        }

        static Border Handle()
        {
            return new Border
            {
                Width = 10, Background = Wpf.Res<Brush>("Accent"), CornerRadius = new CornerRadius(3), Cursor = Cursors.SizeWE,
                Child = new Rectangle { Width = 2, Height = 18, Fill = Wpf.Br("#0B0C0E"), RadiusX = 1, RadiusY = 1 },
            };
        }

        double Wd { get { return Math.Max(1, timeline.ActualWidth); } }
        double X(double t) { return (t - viewStart) / viewLen * Wd; }
        double Tm(double x) { return Math.Max(0, Math.Min(duration, viewStart + x / Wd * viewLen)); }
        static double Clamp(double v, double a, double b) { return Math.Max(a, Math.Min(b, v)); }

        void Drag(double x)
        {
            double t = Tm(x);
            if (drag == "in") SetIn(t);
            else if (drag == "out") SetOut(t);
            else if (drag.StartsWith("c"))
            {
                // a cut edge: "c0:index" — start, "c1:index" — end
                int i = int.Parse(drag.Substring(3)), k = drag[1] == '0' ? 0 : 1;
                if (i >= cuts.Count) return;
                cuts[i][k] = k == 0 ? Math.Min(t, cuts[i][1] - 0.1) : Math.Max(t, cuts[i][0] + 0.1);
                Layout();
                UpdateEstimate();
            }
            else Seek(t);
        }

        void ShowHover(double x)
        {
            double t = Tm(x);
            hoverLine.Visibility = hoverTag.Visibility = Visibility.Visible;
            hoverLine.Height = timeline.ActualHeight;
            Canvas.SetLeft(hoverLine, X(t));
            hoverText.Text = Trimmer.Dur(t);
            hoverTag.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double tw = hoverTag.DesiredSize.Width;
            Canvas.SetLeft(hoverTag, x + 6 + tw > Wd ? x - 6 - tw : x + 6);
            Canvas.SetTop(hoverTag, 1);
        }

        void HideHover() { hoverLine.Visibility = hoverTag.Visibility = Visibility.Collapsed; }

        // ── zoom ──
        bool Zoomed { get { return viewLen < duration - 1e-6; } }
        double MinView { get { return Math.Min(duration, 1.0); } }   // no closer than 1 second across the width

        void ClampView()
        {
            viewLen = Clamp(viewLen, MinView, duration);
            viewStart = Clamp(viewStart, 0, duration - viewLen);
        }

        double ZoomAnchor() { return pos >= viewStart && pos <= viewStart + viewLen ? pos : viewStart + viewLen / 2; }

        void ZoomAt(double anchor, double factor)
        {
            double newLen = Clamp(viewLen / factor, MinView, duration);
            double rel = (anchor - viewStart) / viewLen;
            viewStart = anchor - rel * newLen;
            viewLen = newLen;
            ClampView();
            Layout();
        }

        void ZoomFit()
        {
            viewStart = 0;
            viewLen = duration;
            Layout();
        }

        void Pan(double dt)
        {
            viewStart += dt;
            ClampView();
            Layout();
        }

        // the playhead must not leave the zoomed timeline
        void Follow(bool page)
        {
            if (!Zoomed) return;
            if (pos < viewStart || pos > viewStart + viewLen) viewStart = pos - viewLen * (page ? 0.08 : 0.5);
            else if (page && pos > viewStart + viewLen * 0.92) viewStart = pos - viewLen * 0.08;
            else return;
            ClampView();
        }

        // the strip under the timeline: the visible part, draggable
        void BuildOverview()
        {
            overview.MouseLeftButtonDown += (s, e) =>
            {
                if (!Zoomed) return;
                double x = e.GetPosition(overview).X, ow = Math.Max(1, overview.ActualWidth);
                double tx = viewStart / duration * ow, tw = Math.Max(12, viewLen / duration * ow);
                overviewGrab = x >= tx && x <= tx + tw ? x - tx : tw / 2;   // a click outside the thumb centers on it
                overviewDrag = true;
                overview.CaptureMouse();
                MoveOverview(x);
            };
            overview.MouseMove += (s, e) => { if (overviewDrag) MoveOverview(e.GetPosition(overview).X); };
            overview.MouseLeftButtonUp += (s, e) => { overviewDrag = false; overview.ReleaseMouseCapture(); };
        }

        void MoveOverview(double x)
        {
            viewStart = (x - overviewGrab) / Math.Max(1, overview.ActualWidth) * duration;
            ClampView();
            Layout();
        }

        // ruler: the tick step follows the zoom
        static readonly double[] RulerSteps = { 0.1, 0.2, 0.5, 1, 2, 5, 10, 15, 30, 60, 120, 300, 600 };

        void DrawRuler(double w)
        {
            string sig = viewStart.ToString("0.###", Inv) + "|" + viewLen.ToString("0.###", Inv) + "|" + (int)w;
            if (sig == rulerSig) return;
            rulerSig = sig;
            ruler.Children.Clear();
            double pxPerSec = w / viewLen;
            double step = RulerSteps.FirstOrDefault(st => st * pxPerSec >= 80);
            if (step == 0) step = 1200;
            double minor = step / 5;
            var faint = Wpf.Br("#34363C");
            var strong = Wpf.Br("#5A5D66");
            var label = Wpf.Res<Brush>("Muted");
            var mono = Wpf.Res<FontFamily>("MonoFont");
            for (long n = (long)Math.Floor(viewStart / minor); n * minor <= viewStart + viewLen + minor; n++)
            {
                double t = n * minor, x = X(t);
                if (t < 0 || t > duration || x < -1 || x > w + 1) continue;
                bool major = n % 5 == 0;
                var tickMark = new Rectangle { Width = 1, Height = major ? 8 : 4, Fill = major ? strong : faint };
                Canvas.SetLeft(tickMark, x);
                Canvas.SetTop(tickMark, RulerH - tickMark.Height);
                ruler.Children.Add(tickMark);
                if (!major) continue;
                var tb = new TextBlock { Text = RulerLabel(t, step), FontFamily = mono, FontSize = 10.5, Foreground = label };
                Canvas.SetLeft(tb, x + 4);
                Canvas.SetTop(tb, 2);
                ruler.Children.Add(tb);
            }
        }

        static string RulerLabel(double t, double step)
        {
            var ts = TimeSpan.FromSeconds(Math.Round(t, 2));
            string s = (int)ts.TotalMinutes + ":" + ts.Seconds.ToString("00");
            return step < 1 ? s + "." + (ts.Milliseconds / 100) : s;
        }

        // ── cuts ──
        string CutEdge(double x)
        {
            for (int i = 0; i < cuts.Count; i++)
            {
                if (Math.Abs(x - X(cuts[i][0])) <= 6) return "c0:" + i;
                if (Math.Abs(x - X(cuts[i][1])) <= 6) return "c1:" + i;
            }
            return null;
        }

        // X: the first press marks the piece start, the second its end
        void ToggleCut()
        {
            var btn = F<Button>("BtnCut");
            if (cutStart == null)
            {
                cutStart = pos;
                btn.Foreground = Wpf.Br(Wpf.Bad, 255);
                btn.ToolTip = L.T("Piece start marked — go to its end and press again (X). Esc — cancel", "Начало куска отмечено — дойди до конца куска и нажми ещё раз (X). Esc — отменить");
                Layout();
                return;
            }
            double a = Math.Min(cutStart.Value, pos), b = Math.Max(cutStart.Value, pos);
            CancelPendingCut();
            if (b - a >= 0.1)
            {
                PushUndo(Snap());
                cuts.Add(new[] { a, b });
                NormalizeCuts();
                RebuildCutVisuals();
            }
            Layout();
            UpdateEstimate();
        }

        void CancelPendingCut()
        {
            cutStart = null;
            var btn = F<Button>("BtnCut");
            btn.ClearValue(Control.ForegroundProperty);
            btn.ToolTip = L.T("Cut a piece from the middle  ·  X — press at the piece start and again at its end", "Вырезать кусок из середины  ·  X — нажми в начале куска и ещё раз в конце");
            Layout();
        }

        void RemoveCut(int i)
        {
            if (i < 0 || i >= cuts.Count) return;
            PushUndo(Snap());
            cuts.RemoveAt(i);
            RebuildCutVisuals();
            Layout();
            UpdateEstimate();
        }

        // in order, overlapping ones merged
        void NormalizeCuts()
        {
            var sorted = cuts.Where(c => c[1] - c[0] >= 0.1).OrderBy(c => c[0]).ToList();
            cuts.Clear();
            foreach (var c in sorted)
            {
                if (cuts.Count > 0 && c[0] <= cuts[cuts.Count - 1][1]) cuts[cuts.Count - 1][1] = Math.Max(cuts[cuts.Count - 1][1], c[1]);
                else cuts.Add(c);
            }
        }

        void RebuildCutVisuals()
        {
            foreach (var r in cutBoxes) canvas.Children.Remove(r);
            foreach (var x in cutX) canvas.Children.Remove(x);
            cutBoxes.Clear();
            cutX.Clear();
            int at = canvas.Children.IndexOf(pendingBox);
            for (int i = 0; i < cuts.Count; i++)
            {
                var box = new Rectangle { Fill = Wpf.Br(Wpf.Bad, 0x4D), Stroke = Wpf.Br(Wpf.Bad, 0xCC), StrokeThickness = 1, IsHitTestVisible = false };
                var x = new Border
                {
                    Width = 20, Height = 18, CornerRadius = new CornerRadius(4), Background = Wpf.Br("#E60B0C0E"), Cursor = Cursors.Hand, Tag = i,
                    ToolTip = L.T("Restore this piece  ·  Delete when the playhead is inside", "Вернуть этот кусок  ·  Delete, когда бегунок внутри"),
                    Child = new TextBlock { Style = Wpf.Res<Style>("Icon"), Text = "", FontSize = 9, Foreground = Brushes.White,
                                            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
                };
                canvas.Children.Insert(at++, box);
                canvas.Children.Insert(at++, x);
                cutBoxes.Add(box);
                cutX.Add(x);
            }
        }

        // ── undo and redo ──
        EditState Snap() { return new EditState { In = inT, Out = outT, Cuts = cuts.Select(c => new[] { c[0], c[1] }).ToList() }; }

        static bool Same(EditState a, EditState b)
        {
            return Math.Abs(a.In - b.In) < 1e-6 && Math.Abs(a.Out - b.Out) < 1e-6 && a.Cuts.Count == b.Cuts.Count &&
                   a.Cuts.Zip(b.Cuts, (x, y) => Math.Abs(x[0] - y[0]) < 1e-6 && Math.Abs(x[1] - y[1]) < 1e-6).All(v => v);
        }

        void PushUndo(EditState s)
        {
            if (undo.Count > 0 && Same(undo[undo.Count - 1], s)) return;
            undo.Add(s);
            if (undo.Count > 200) undo.RemoveAt(0);
            redo.Clear();
        }

        void Undo() { Shift(undo, redo); }
        void Redo() { Shift(redo, undo); }

        void Shift(List<EditState> from, List<EditState> to)
        {
            if (from.Count == 0) return;
            to.Add(Snap());
            var s = from[from.Count - 1];
            from.RemoveAt(from.Count - 1);
            inT = s.In;
            outT = s.Out;
            cuts.Clear();
            cuts.AddRange(s.Cuts.Select(c => new[] { c[0], c[1] }));
            if (cutStart != null) CancelPendingCut();
            RebuildCutVisuals();
            Layout();
            UpdateEstimate();
        }

        void MarkIn() { PushUndo(Snap()); SetIn(pos); }
        void MarkOut() { PushUndo(Snap()); SetOut(pos); }

        // ── drawing the timeline and labels ──
        void Layout()
        {
            double w = Wd, h = timeline.ActualHeight;
            if (h > 0)
            {
                double body = h - RulerH;
                foreach (var bg in canvas.Children.OfType<Border>().Where(b => (b.Tag as string) == "bg")) { bg.Width = w; bg.Height = h; }
                DrawRuler(w);
                double x0 = X(0), x1 = X(duration);
                strip.Width = Math.Max(1, x1 - x0); strip.Height = StripH; Canvas.SetLeft(strip, x0); Canvas.SetTop(strip, RulerH);
                wave.Width = Math.Max(1, x1 - x0); wave.Height = Math.Max(1, h - WaveTop); Canvas.SetLeft(wave, x0); Canvas.SetTop(wave, WaveTop);
                for (int i = 0; lanesOn && i < laneImgs.Count; i++)
                {
                    double top = WaveTop + i * LaneH;
                    laneSeps[i].Width = w; Canvas.SetLeft(laneSeps[i], 0); Canvas.SetTop(laneSeps[i], top);
                    laneImgs[i].Width = Math.Max(1, x1 - x0); laneImgs[i].Height = LaneH - 6; Canvas.SetLeft(laneImgs[i], x0); Canvas.SetTop(laneImgs[i], top + 3);
                    Canvas.SetLeft(laneTags[i], 6); Canvas.SetTop(laneTags[i], top + 3);
                }
                waveCaption.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                Canvas.SetLeft(waveCaption, w - waveCaption.DesiredSize.Width - 8);
                Canvas.SetTop(waveCaption, WaveTop + 2);
                double xi = X(inT), xo = X(outT), ci = Clamp(xi, 0, w), co = Clamp(xo, 0, w);
                maskL.Width = ci; maskL.Height = body; Canvas.SetLeft(maskL, 0); Canvas.SetTop(maskL, RulerH);
                maskR.Width = Math.Max(0, w - co); maskR.Height = body; Canvas.SetLeft(maskR, co); Canvas.SetTop(maskR, RulerH);
                selBox.Width = Math.Max(2, xo - xi); selBox.Height = body; Canvas.SetLeft(selBox, xi); Canvas.SetTop(selBox, RulerH);
                handleL.Height = handleR.Height = body;
                Canvas.SetLeft(handleL, xi - 5); Canvas.SetTop(handleL, RulerH);
                Canvas.SetLeft(handleR, xo - 5); Canvas.SetTop(handleR, RulerH);
                playhead.Height = h; Canvas.SetLeft(playhead, X(pos) - 1);
                for (int i = 0; i < cutBoxes.Count && i < cuts.Count; i++)
                {
                    double a = X(cuts[i][0]), b = X(cuts[i][1]);
                    cutBoxes[i].Width = Math.Max(2, b - a); cutBoxes[i].Height = body; Canvas.SetLeft(cutBoxes[i], a); Canvas.SetTop(cutBoxes[i], RulerH);
                    Canvas.SetLeft(cutX[i], (a + b) / 2 - 10); Canvas.SetTop(cutX[i], RulerH + 4);
                    cutX[i].Visibility = b - a >= 24 ? Visibility.Visible : Visibility.Collapsed;
                }
                if (cutStart != null)
                {
                    double a = X(Math.Min(cutStart.Value, pos)), b = X(Math.Max(cutStart.Value, pos));
                    pendingBox.Visibility = Visibility.Visible;
                    pendingBox.Width = Math.Max(2, b - a); pendingBox.Height = body; Canvas.SetLeft(pendingBox, a); Canvas.SetTop(pendingBox, RulerH);
                }
                else pendingBox.Visibility = Visibility.Collapsed;

                // overview strip and scale
                double ow = Math.Max(1, overview.ActualWidth);
                overview.Opacity = Zoomed ? 1 : 0;   // without zoom the overview strip is not needed
                overview.IsHitTestVisible = Zoomed;
                overviewThumb.Visibility = Zoomed ? Visibility.Visible : Visibility.Collapsed;
                overviewThumb.Width = Math.Max(12, viewLen / duration * ow);
                overviewThumb.Margin = new Thickness(Math.Min(ow - overviewThumb.Width, viewStart / duration * ow), 0, 0, 0);
                double z = duration / viewLen;
                zoomText.Text = (z < 10 ? z.ToString("0.#", Inv) : z.ToString("0", Inv)) + "×";
            }
            timeText.Text = Trimmer.Dur(pos);
            totalText.Text = "/ " + Trimmer.Dur(duration);
            speedText.Text = playing && speed > 1 ? speed.ToString("0", Inv) + "×" : "";
            double total = cuts.Count > 0 ? Trimmer.KeptSegments(inT, outT, cuts).Sum(k => k[1] - k[0]) : outT - inT;
            selText.Text = Trimmer.Dur(inT) + " → " + Trimmer.Dur(outT);
            selText.ToolTip = L.T("Range ", "Отрезок ") + Trimmer.Dur(inT) + " → " + Trimmer.Dur(outT) + L.T(" · result ", " · итог ") + Trimmer.Dur(total) +
                              (cuts.Count > 0 ? L.T(" (without the cut parts)", " (без вырезанного)") : "");
        }

        void SetIn(double t) { inT = Math.Max(0, Math.Min(t, outT - 0.5)); Layout(); UpdateEstimate(); }
        void SetOut(double t) { outT = Math.Min(duration, Math.Max(t, inT + 0.5)); Layout(); UpdateEstimate(); }
    }
}
