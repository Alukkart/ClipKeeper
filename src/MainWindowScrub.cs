using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Threading;

namespace DeviceGuard
{
    // Frames under the mouse: moving across a clip's thumbnail scrolls through the clip, like in Medal or YouTube.
    // The frames are the editor's strip (Filmstrips); a strip is made when the mouse rests on a card for a moment.
    partial class MainWindow
    {
        ClipVm scrubVm;                 // the card under the mouse
        FrameworkElement scrubThumb;
        double scrubAt;                 // 0..1 across the thumbnail
        volatile string scrubWanted;    // the clip whose strip is wanted now: older requests are dropped before ffmpeg starts
        readonly DispatcherTimer scrubDelay = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        readonly HashSet<string> scrubFailed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void InitScrub()
        {
            clipsList.MouseMove += (s, e) => ScrubMove(e);
            clipsList.MouseLeave += (s, e) => ScrubEnd();
            scrubDelay.Tick += (s, e) =>
            {
                scrubDelay.Stop();
                if (scrubVm != null) LoadFrames(scrubVm);
            };
        }

        // the thumbnail (Tag="thumb" in ClipTpl) the mouse is over, or null
        static FrameworkElement ThumbUnder(object source)
        {
            var d = source as DependencyObject;
            while (d != null)
            {
                var fe = d as FrameworkElement;
                if (fe != null && Equals(fe.Tag, "thumb")) return fe;
                d = d is Visual || d is Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
            }
            return null;
        }

        void ScrubMove(MouseEventArgs e)
        {
            var thumb = ThumbUnder(e.OriginalSource);
            var vm = thumb != null ? thumb.DataContext as ClipVm : null;
            if (vm != scrubVm)
            {
                ScrubEnd();
                scrubVm = vm;
                scrubThumb = thumb;
                if (vm == null) return;
                // a strip made before opens at once; a new one only when the mouse stays on the card
                if (Filmstrips.Has(vm.Path)) LoadFrames(vm);
                else scrubDelay.Start();
            }
            if (vm == null) return;
            scrubAt = Math.Max(0, Math.Min(0.999, e.GetPosition(thumb).X / Math.Max(1, thumb.ActualWidth)));
            ShowScrub();
        }

        void ShowScrub()
        {
            var vm = scrubVm;
            if (vm == null || vm.Frames == null) return;   // frames are not ready: the card stays as it is
            vm.Scrub = vm.Frames[(int)(scrubAt * vm.Frames.Length)];
            vm.ScrubWidth = scrubAt * scrubThumb.ActualWidth;
        }

        // the mouse left the card: back to the thumbnail; the frames are let go (a strip is on disk, it loads fast again)
        void ScrubEnd()
        {
            scrubDelay.Stop();
            if (scrubVm != null)
            {
                scrubVm.Scrub = null;
                scrubVm.ScrubWidth = -1;
                scrubVm.Frames = null;
            }
            scrubVm = null;
            scrubThumb = null;
        }

        void LoadFrames(ClipVm vm)
        {
            if (!Ffmpeg.Available || vm.Frames != null || scrubFailed.Contains(vm.Path)) return;
            string path = vm.Path;
            scrubWanted = path;
            bool skipped = false;
            Task.Factory.StartNew(() =>
            {
                if (scrubWanted != path) { skipped = true; return null; }
                return Filmstrips.Get(path, 0, CancellationToken.None);
            }).ContinueWith(t => W.Dispatcher.BeginInvoke(new Action(() =>
            {
                if (skipped) return;
                if (t.IsFaulted || t.Result == null)
                {
                    if (t.IsFaulted) Log.Write("frames for " + path + ": " + t.Exception.GetBaseException().Message);
                    scrubFailed.Add(path);   // not again for this clip until the program restarts
                    return;
                }
                if (scrubVm != vm) return;
                vm.Frames = Filmstrips.Frames(t.Result);
                ShowScrub();
            })));
        }
    }
}
