using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace DeviceGuard
{
    // Frames or a list: the same clips either as a grid of frames or as rows with columns (name and when, game, length,
    // size), for a big folder. The choice is remembered (Settings.ClipsList); a click on a column name orders the clips by it.
    partial class MainWindow
    {
        ToggleButton viewGrid, viewList;

        void InitList()
        {
            viewGrid = F<ToggleButton>("ViewGrid");
            viewList = F<ToggleButton>("ViewList");
            viewGrid.Click += (s, e) => SetListView(false);
            viewList.Click += (s, e) => SetListView(true);
            F<Button>("ColName").Click += (s, e) => SortBy(clipsSort == SortNew ? SortOld : SortNew);
            F<Button>("ColLength").Click += (s, e) => SortBy(SortLong);
            F<Button>("ColSize").Click += (s, e) => SortBy(SortBig);
            ApplyListView();
        }

        void SetListView(bool list)
        {
            cfg.ClipsList = list;
            Save();
            ApplyListView();
        }

        void SortBy(int sort)
        {
            clipsSort = sort;
            clipsLimit = 30;
            BuildFilters();
            LoadClips(true);
        }

        void ApplyListView()
        {
            bool list = cfg.ClipsList;
            viewGrid.IsChecked = !list;
            viewList.IsChecked = list;
            clipsList.ItemTemplate = (DataTemplate)W.FindResource(list ? "ClipRowTpl" : "ClipTpl");
            var panel = new FrameworkElementFactory(list ? typeof(StackPanel) : typeof(WrapPanel));
            clipsList.ItemsPanel = new ItemsPanelTemplate(panel);
            ShowListHead();
        }

        // the column names: over a list of clips only, with the order they set marked
        void ShowListHead()
        {
            bool page = gameView != null || flatView;
            F<FrameworkElement>("ClipsListHead").Visibility = cfg.ClipsList && page && clips.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            F<FrameworkElement>("ViewSwitch").Visibility = page ? Visibility.Visible : Visibility.Collapsed;
            Head("ColName", L.T("Name", "Название"), clipsSort == SortNew ? "" : clipsSort == SortOld ? "" : null);
            Head("ColLength", L.T("Length", "Длина"), clipsSort == SortLong ? "" : null);
            Head("ColSize", L.T("Size", "Размер"), clipsSort == SortBig ? "" : null);
        }

        void Head(string name, string text, string arrow)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            sp.Children.Add(new TextBlock { Text = text, FontSize = 12, Foreground = arrow != null ? Wpf.Res<Brush>("Sub") : Wpf.Br("#5F646D") });
            if (arrow != null) sp.Children.Add(new TextBlock { Style = S("Icon"), Text = arrow, FontSize = 8, Margin = new Thickness(5, 1, 0, 0), Foreground = Wpf.Res<Brush>("Sub") });
            var b = F<Button>(name);
            b.Content = sp;
            b.ToolTip = L.T("Order the clips by it", "Упорядочить клипы по этой колонке");
        }

        // for the self-test: the clip panel, the list, the search and the recording card open over the test cards, or closed
        public void TestPanels(bool open)
        {
            if (open)
            {
                Peek(clips[0]);
                PreviewList(true);
                OpenPalette();
                OpenRecPop();
            }
            else
            {
                CloseRecPop();
                ClosePalette();
                PreviewList(false);
                ClosePeek();
            }
        }

        // for previews: the list or the frames, not saved
        public void PreviewList(bool list)
        {
            cfg.ClipsList = list;
            ApplyListView();
        }

        // a row of the list (Tag="row" in ClipRowTpl) the element belongs to
        static bool RowUnder(DependencyObject d)
        {
            for (; d != null; d = d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
            {
                var fe = d as FrameworkElement;
                if (fe != null && Equals(fe.Tag, "row")) return true;
            }
            return false;
        }
    }
}
