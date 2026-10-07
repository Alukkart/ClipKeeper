using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace DeviceGuard
{
    // Naming a clip right on its card: F2 over a card or a double click on its title. Enter renames the file (ClipNames),
    // Esc leaves it as it was; clicking elsewhere keeps what was typed, like renaming in Explorer.
    partial class MainWindow
    {
        void InitRename()
        {
            clipsList.AddHandler(UIElement.PreviewMouseLeftButtonDownEvent, new MouseButtonEventHandler((s, e) =>
            {
                var t = e.OriginalSource as TextBlock;
                var vm = t != null ? t.DataContext as ClipVm : null;
                if (vm == null || (t.Tag as string) != "title" || e.ClickCount != 2) return;
                StartRename(vm);
                e.Handled = true;
            }));
            clipsList.AddHandler(UIElement.PreviewKeyDownEvent, new KeyEventHandler((s, e) =>
            {
                var box = e.OriginalSource as TextBox;
                var vm = box != null && (box.Tag as string) == "rename" ? box.DataContext as ClipVm : null;
                if (vm == null) return;
                if (e.Key == Key.Enter) { FinishRename(vm, true); e.Handled = true; }
                else if (e.Key == Key.Escape) { FinishRename(vm, false); e.Handled = true; }
            }));
            // what a file name can't hold is not typed at all
            clipsList.AddHandler(UIElement.PreviewTextInputEvent, new TextCompositionEventHandler((s, e) =>
            {
                var box = e.OriginalSource as TextBox;
                if (box != null && (box.Tag as string) == "rename" && e.Text.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) e.Handled = true;
            }));
            clipsList.AddHandler(UIElement.LostKeyboardFocusEvent, new KeyboardFocusChangedEventHandler((s, e) =>
            {
                var box = e.OriginalSource as TextBox;
                var vm = box != null && (box.Tag as string) == "rename" ? box.DataContext as ClipVm : null;
                if (vm != null && vm.Editing) FinishRename(vm, true);
            }));
            W.PreviewKeyDown += (s, e) =>
            {
                if (e.Key != Key.F2 || Keyboard.Modifiers != ModifierKeys.None || !pages[PageClips].IsVisible || Keyboard.FocusedElement is TextBox) return;
                var vm = ClipUnder(Mouse.DirectlyOver as DependencyObject) ?? ClipUnder(Keyboard.FocusedElement as DependencyObject);
                if (vm == null) return;
                StartRename(vm);
                e.Handled = true;
            };
        }

        // the clip card an element belongs to
        static ClipVm ClipUnder(DependencyObject d)
        {
            for (; d != null; d = d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
            {
                var fe = d as FrameworkElement;
                if (fe != null && fe.DataContext is ClipVm) return (ClipVm)fe.DataContext;
            }
            return null;
        }

        void StartRename(ClipVm vm)
        {
            foreach (var other in clips.Where(c => c.Editing && c != vm).ToList()) FinishRename(other, false);
            vm.EditText = vm.Named || !vm.Source ? vm.Title : "";   // a source without a name: the date in the title is not a name
            vm.Editing = true;
            W.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
            {
                var box = Find<TextBox>(clipsList, b => (b.Tag as string) == "rename" && b.DataContext == vm);
                if (box == null) return;
                box.Focus();
                box.SelectAll();
            }));
        }

        void FinishRename(ClipVm vm, bool keep)
        {
            if (!vm.Editing) return;
            vm.Editing = false;
            string title = vm.EditText.Trim();
            if (!keep || title.Length == 0 || title == (vm.Named || !vm.Source ? vm.Title : "")) return;
            string to;
            string err = ClipNames.Rename(vm.Path, title, vm.Source, out to);
            if (err != null)
            {
                if (app != null) app.ShowNotice(L.T("The clip was not renamed", "Клип не переименован"), new List<string> { Path.GetFileName(vm.Path) + ": " + err }, false);
                return;
            }
            if (app != null && string.Equals(app.Guard.LastClipPath, vm.Path, StringComparison.OrdinalIgnoreCase)) app.Guard.LastClipPath = to;
            // the same card with the new name: its place, its day and its picture stay
            int i = clips.IndexOf(vm);
            if (i < 0) return;
            var f = new FileInfo(to);
            var nv = vm.Source ? ClipScanner.Describe(f, RootOf(last) ?? f.DirectoryName, null) : DescribeNamed(f);
            nv.Fav = vm.Fav;
            nv.Game = vm.Game;
            nv.Group = vm.Group;
            nv.ReturnTip = vm.ReturnTip;
            nv.NewVis = vm.NewVis;
            nv.Thumb = vm.Thumb;
            if (vm.Source && gameView != null && !gameView.StartsWith("*")) GameViewTitle(nv);
            clips[i] = nv;
        }

        static T Find<T>(DependencyObject root, Func<T, bool> ok) where T : DependencyObject
        {
            int n = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < n; i++)
            {
                var c = VisualTreeHelper.GetChild(root, i);
                var t = c as T;
                if (t != null && ok(t)) return t;
                var deeper = Find(c, ok);
                if (deeper != null) return deeper;
            }
            return null;
        }
    }
}
