using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Rectangle = System.Windows.Shapes.Rectangle;

namespace DeviceGuard
{
    // Simple WPF charts following dataviz rules: thin marks rounded at the tip (flat at the base),
    // 2 px of background between stacked parts, a muted grid, labels only on totals, a tooltip with numbers on hover,
    // a legend when there is more than one series; labels use text color, not the series color.
    static class Charts
    {
        public class Series { public string Name; public Color Color; }

        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        const double Gap = 2;

        public static FrameworkElement Legend(IEnumerable<Series> series)
        {
            var p = new WrapPanel { Margin = new Thickness(0, 0, 0, 12) };
            foreach (var s in series)
            {
                var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 18, 0) };
                sp.Children.Add(new Border { Width = 10, Height = 10, CornerRadius = new CornerRadius(2), Background = new SolidColorBrush(s.Color),
                                             Margin = new Thickness(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center });
                sp.Children.Add(new TextBlock { Text = s.Name, Foreground = Wpf.Res<Brush>("Sub"), FontSize = 12.5 });
                p.Children.Add(sp);
            }
            return p;
        }

        // a "round" grid step: 1, 2, 5, 10, 20, 50…
        static double Nice(double max)
        {
            if (max <= 0) return 1;
            double raw = max / 4, p = Math.Pow(10, Math.Floor(Math.Log10(raw)));
            foreach (var k in new[] { 1, 2, 5, 10 }) if (k * p >= raw) return k * p;
            return 10 * p;
        }

        // Stacked vertical columns: labels — X axis labels, values[i][s] — value of series s in column i
        public static FrameworkElement Columns(IList<string> labels, IList<double[]> values, IList<Series> series,
                                               Func<int, string> tip, double height)
        {
            double max = values.Count == 0 ? 0 : values.Max(v => v.Sum());
            double step = Nice(max), top = Math.Max(step, Math.Ceiling(max / step) * step);
            var root = new Grid { Margin = new Thickness(0, 18, 0, 0) };   // room for the total label above the tallest column
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
            root.ColumnDefinitions.Add(new ColumnDefinition());
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(height) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // grid and Y axis values
            var axis = new Grid();
            var grid = new Grid();
            Grid.SetColumn(grid, 1);
            for (double v = 0; v <= top + 1e-9; v += step)
            {
                double y = height - v / top * height;
                var line = new Rectangle { Height = 1, Fill = Wpf.Br(v == 0 ? "#34363C" : "#202126"), VerticalAlignment = VerticalAlignment.Top,
                                           Margin = new Thickness(0, Math.Min(height - 1, y), 0, 0) };
                grid.Children.Add(line);
                axis.Children.Add(new TextBlock
                {
                    Text = v.ToString("0", Inv), FontSize = 11, Foreground = Wpf.Res<Brush>("Muted"), HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, Math.Max(0, y - 8), 8, 0),
                });
            }
            root.Children.Add(axis);
            root.Children.Add(grid);

            // columns
            var cols = new UniformGrid { Rows = 1, Columns = Math.Max(1, labels.Count) };
            var names = new UniformGrid { Rows = 1, Columns = Math.Max(1, labels.Count), Margin = new Thickness(0, 6, 0, 0) };
            Grid.SetColumn(cols, 1);
            Grid.SetColumn(names, 1);
            Grid.SetRow(names, 1);
            for (int i = 0; i < labels.Count; i++)
            {
                double total = values[i].Sum();
                var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Bottom, HorizontalAlignment = HorizontalAlignment.Center, Width = 30 };
                // the total above a column — a label in text color
                stack.Children.Add(new TextBlock { Text = total > 0 ? total.ToString("0", Inv) : "", FontSize = 11.5, Foreground = Wpf.Res<Brush>("Sub"),
                                                   HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 4) });
                // top to bottom: the last series is at the tip
                int topSeries = Enumerable.Range(0, series.Count).LastOrDefault(s => values[i][s] > 0);
                for (int s = series.Count - 1; s >= 0; s--)
                {
                    double v = values[i][s];
                    if (v <= 0) continue;
                    double h = Math.Max(2, v / top * height - Gap);
                    stack.Children.Add(new Border
                    {
                        Height = h, Background = new SolidColorBrush(series[s].Color), Margin = new Thickness(0, 0, 0, Gap),
                        CornerRadius = s == topSeries ? new CornerRadius(4, 4, 0, 0) : new CornerRadius(0),
                    });
                }
                // the hover area spans the column height and is wider than the mark
                var hit = new Grid { Background = Brushes.Transparent, ToolTip = tip(i) };
                hit.Children.Add(stack);
                cols.Children.Add(hit);
                names.Children.Add(new TextBlock { Text = labels[i], FontSize = 11.5, Foreground = Wpf.Res<Brush>("Muted"), HorizontalAlignment = HorizontalAlignment.Center });
            }
            root.Children.Add(cols);
            root.Children.Add(names);
            return root;
        }

        // Stacked horizontal bars: a row is a category (game), the total label is on the right
        public static FrameworkElement Bars(IList<string> labels, IList<double[]> values, IList<Series> series,
                                            Func<int, string> right, Func<int, string> tip)
        {
            double max = values.Count == 0 ? 1 : Math.Max(1, values.Max(v => v.Sum()));
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
            for (int i = 0; i < labels.Count; i++)
            {
                g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(30) });
                var name = new TextBlock { Text = labels[i], VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
                                           Margin = new Thickness(0, 0, 12, 0), ToolTip = labels[i] };
                Grid.SetRow(name, i);
                g.Children.Add(name);

                // a bar: proportions via star columns — one scale for all rows
                var bar = new Grid { Height = 14, VerticalAlignment = VerticalAlignment.Center, Background = Brushes.Transparent, ToolTip = tip(i) };
                double sum = values[i].Sum();
                int last = Enumerable.Range(0, series.Count).LastOrDefault(s => values[i][s] > 0);
                for (int s = 0; s < series.Count; s++)
                {
                    double v = values[i][s];
                    if (v <= 0) continue;
                    bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(v, GridUnitType.Star) });
                    var seg = new Border
                    {
                        Background = new SolidColorBrush(series[s].Color), Margin = new Thickness(0, 0, s == last ? 0 : Gap, 0),
                        CornerRadius = s == last ? new CornerRadius(0, 4, 4, 0) : new CornerRadius(0),
                    };
                    Grid.SetColumn(seg, bar.ColumnDefinitions.Count - 1);
                    bar.Children.Add(seg);
                }
                if (max - sum > 0) bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(max - sum, GridUnitType.Star) });
                Grid.SetRow(bar, i);
                Grid.SetColumn(bar, 1);
                g.Children.Add(bar);

                var r = new TextBlock { Text = right(i), Foreground = Wpf.Res<Brush>("Sub"), FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center,
                                        Margin = new Thickness(12, 0, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
                Grid.SetRow(r, i);
                Grid.SetColumn(r, 2);
                g.Children.Add(r);
            }
            return g;
        }
    }
}
