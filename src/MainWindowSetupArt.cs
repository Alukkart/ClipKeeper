using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace DeviceGuard
{
    // The picture on top of the first-run setup: "what you get" — a library of games and a saved clip card,
    // drawn in code (no files), with the three steps of the main flow under it.
    partial class MainWindow
    {
        static LinearGradientBrush Grad(string a, string b)
        {
            var g = new LinearGradientBrush(Wpf.C(a), Wpf.C(b), new Point(0, 0), new Point(1, 1));
            g.Freeze();
            return g;
        }

        static FrameworkElement WhatYouGet()
        {
            var wrap = new StackPanel { Margin = new Thickness(0, 0, 0, 22) };
            var box = new Grid { Height = 168 };
            box.Children.Add(new Border { CornerRadius = new CornerRadius(12), Background = Wpf.Res<Brush>("Card"), BorderBrush = Wpf.Res<Brush>("Line"), BorderThickness = new Thickness(1) });

            // the library: three game covers fanned out
            var covers = new Canvas { Width = 230, Height = 168, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(26, 0, 0, 0) };
            string[][] art = { new[] { "#2DD4BF", "#1E3A8A", "12" }, new[] { "#F472B6", "#6D28D9", "30" }, new[] { "#FDBA74", "#B91C1C", "7" } };
            double[] angle = { -8, 0, 8 }, left = { 4, 64, 124 }, top = { 32, 22, 32 };
            for (int i = 0; i < 3; i++)
            {
                var c = new Grid { Width = 78, Height = 117, RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new RotateTransform(angle[i]) };
                c.Children.Add(new Border { CornerRadius = new CornerRadius(8), Background = Grad(art[i][0], art[i][1]),
                                            BorderBrush = Wpf.Br("#40FFFFFF"), BorderThickness = new Thickness(1) });
                var count = new Border { CornerRadius = new CornerRadius(4), Background = Wpf.Br("#CC0B0C0E"), Padding = new Thickness(5, 1, 5, 1), Margin = new Thickness(6),
                                         HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom };
                var cs = new StackPanel { Orientation = Orientation.Horizontal };
                cs.Children.Add(new TextBlock { Style = S("Icon"), Text = "", FontSize = 9, Foreground = Wpf.Br("#D4D4D8"), Margin = new Thickness(0, 0, 4, 0) });
                cs.Children.Add(new TextBlock { Text = art[i][2], FontSize = 10, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White });
                count.Child = cs;
                c.Children.Add(count);
                Canvas.SetLeft(c, left[i]);
                Canvas.SetTop(c, top[i]);
                Panel.SetZIndex(c, i == 1 ? 2 : 1);
                covers.Children.Add(c);
            }
            box.Children.Add(covers);

            // a saved clip: thumbnail, "latest", title, actions
            var card = new Border
            {
                Width = 236, CornerRadius = new CornerRadius(10), Background = Wpf.Res<Brush>("Card"), BorderBrush = Wpf.Res<Brush>("LineHi"),
                BorderThickness = new Thickness(1), Padding = new Thickness(6), HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 26, 0),
                RenderTransform = new TranslateTransform(),
            };
            var cp = new StackPanel();
            var thumb = new Grid { Height = 96 };
            thumb.Children.Add(new Border { CornerRadius = new CornerRadius(7), Background = Grad("#3B2F8F", "#0F172A") });
            thumb.Children.Add(new Border
            {
                Width = 34, Height = 34, CornerRadius = new CornerRadius(17), Background = Wpf.Br("#B30B0C0E"),
                Child = new TextBlock { Style = S("Icon"), Text = "", FontSize = 14, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(2, 0, 0, 0) },
            });
            thumb.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(4), Background = Wpf.Res<Brush>("Brand"), Padding = new Thickness(6, 1, 6, 1), Margin = new Thickness(6),
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
                Child = new TextBlock { Text = L.T("latest", "последний"), FontSize = 10, FontWeight = FontWeights.SemiBold, Foreground = Wpf.Res<Brush>("OnBrand") },
            });
            thumb.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(4), Background = Wpf.Br("#CC0B0C0E"), Padding = new Thickness(5, 1, 5, 1), Margin = new Thickness(6),
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom,
                Child = new TextBlock { Text = "0:30", FontFamily = Wpf.Res<FontFamily>("MonoFont"), FontSize = 10.5, Foreground = Brushes.White },
            });
            cp.Children.Add(thumb);
            var title = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6, 8, 0, 0) };
            title.Children.Add(new TextBlock { Style = S("Icon"), Text = "", FontSize = 11, Foreground = Wpf.Br(Wpf.Ok, 255), Margin = new Thickness(0, 0, 6, 0) });
            title.Children.Add(new TextBlock { Text = L.T("Clip saved", "Клип сохранён"), FontWeight = FontWeights.SemiBold, FontSize = 12.5 });
            cp.Children.Add(title);
            var acts = new Grid { Margin = new Thickness(6, 6, 0, 2) };
            acts.Children.Add(new TextBlock { Text = L.T("today, 22:40 · 28 MB", "сегодня, 22:40 · 28 МБ"), Foreground = Wpf.Res<Brush>("Muted"), FontSize = 11, VerticalAlignment = VerticalAlignment.Center });
            var copy = new Border
            {
                CornerRadius = new CornerRadius(5), Background = Wpf.Res<Brush>("Brand"), Padding = new Thickness(8, 3, 8, 3), HorizontalAlignment = HorizontalAlignment.Right,
                Child = new TextBlock { Text = L.T("Copy", "Копировать"), FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = Wpf.Res<Brush>("OnBrand") },
            };
            acts.Children.Add(copy);
            cp.Children.Add(acts);
            card.Child = cp;
            box.Children.Add(card);
            wrap.Children.Add(box);

            // the main flow in three steps
            var flow = new UniformGrid { Columns = 3, Margin = new Thickness(0, 12, 0, 0) };
            flow.Children.Add(FlowStep("", L.T("A hotkey — the clip is saved", "Клавиша — и клип сохранён")));
            flow.Children.Add(FlowStep("", L.T("Trimmed in seconds", "Обрезал за секунды")));
            flow.Children.Add(FlowStep("", L.T("Ctrl+V into Discord", "Ctrl+V в Discord")));
            wrap.Children.Add(flow);

            // the clip card slides in when the step opens
            wrap.Loaded += (s, e) =>
            {
                var t = TimeSpan.FromSeconds(0.5);
                var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
                ((TranslateTransform)card.RenderTransform).BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(18, 0, t) { EasingFunction = ease });
                card.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, t));
            };
            return wrap;
        }

        static FrameworkElement FlowStep(string glyph, string text)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            sp.Children.Add(new Border
            {
                Width = 24, Height = 24, CornerRadius = new CornerRadius(12), Background = Wpf.Res<Brush>("BrandSoft"), Margin = new Thickness(0, 0, 8, 0),
                Child = new TextBlock { Style = S("Icon"), Text = glyph, FontSize = 11, Foreground = Wpf.Res<Brush>("BrandHi"), HorizontalAlignment = HorizontalAlignment.Center },
            });
            sp.Children.Add(new TextBlock { Text = text, Foreground = Wpf.Res<Brush>("Sub"), FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, MaxWidth = 150 });
            return sp;
        }
    }
}
