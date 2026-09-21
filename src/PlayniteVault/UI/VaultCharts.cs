using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace PlayniteVault.UI
{
    /// <summary>图表里的一段/一条。同一个结构同时喂给条形、堆叠条和环形。</summary>
    public sealed class BarItem
    {
        public string Label { get; set; }

        /// <summary>参与比例计算的数值（体积、个数都行，同一张图里必须同量纲）。</summary>
        public double Value { get; set; }

        /// <summary>显示在右侧的文字。留空则用 <see cref="Value"/> 原样打。</summary>
        public string ValueText { get; set; }

        /// <summary>指定颜色；留空则按序取 <see cref="VaultPalette.Series"/>。</summary>
        public Brush Color { get; set; }

        public BarItem(string label, double value, string valueText)
        {
            Label = label;
            Value = value;
            ValueText = valueText;
        }
    }

    /// <summary>
    /// 侧边栏页用到的几个图表部件。全部用 Grid 的星号列 + Path 画，
    /// <b>不做一次 Measure/Arrange 之后才知道宽度的计算</b> ——
    /// 那种写法在滚动容器里会拿到 0 宽，然后画出一片空白。
    ///
    /// 星号列的好处：比例交给布局系统算，窗口一缩放图就跟着变，
    /// 我们不需要知道任何一个像素值（环形图是唯一例外，它必须正方形）。
    /// </summary>
    public static class VaultCharts
    {
        // ================================================================ 容器

        /// <summary>卡片底板：Surface 底 + 2px 描边，无圆角无阴影（延续硬边那一路）。</summary>
        public static Border Card(VaultPalette p, UIElement child)
        {
            return new Border
            {
                Background = p.Surface,
                BorderBrush = p.Border,
                BorderThickness = new Thickness(2),
                Padding = new Thickness(14, 12, 14, 12),
                Child = child
            };
        }

        /// <summary>小节标题。</summary>
        public static TextBlock SectionTitle(VaultPalette p, string text)
        {
            return new TextBlock
            {
                Text = text,
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Foreground = p.TextMuted,
                Margin = new Thickness(0, 0, 0, 9)
            };
        }

        /// <summary>说明文字。</summary>
        public static TextBlock Muted(VaultPalette p, string text, double size = 11.5)
        {
            return new TextBlock
            {
                Text = text,
                FontSize = size,
                Foreground = p.TextMuted,
                TextWrapping = TextWrapping.Wrap,
                LineHeight = size * 1.55
            };
        }

        // ================================================================ 指标块

        /// <summary>
        /// 单个指标块：大数字 + 小标题。
        /// 强调色只做左边那条 4px 竖线 —— 整块底色是同一种 Surface，
        /// 所以一排放 6 个也不会花。
        /// </summary>
        public static Border StatTile(VaultPalette p, string caption, string value, Brush accent)
        {
            var valueText = new TextBlock
            {
                Text = value ?? "—",
                FontSize = 21,
                FontWeight = FontWeights.Bold,
                Foreground = p.Text,
                TextTrimming = TextTrimming.CharacterEllipsis
            };

            var stack = new StackPanel();
            stack.Children.Add(valueText);
            stack.Children.Add(new TextBlock
            {
                Text = caption,
                FontSize = 11,
                Foreground = p.TextMuted,
                Margin = new Thickness(0, 3, 0, 0)
            });

            var body = new Border
            {
                Background = p.Surface,
                BorderBrush = p.Border,
                BorderThickness = new Thickness(2),
                Padding = new Thickness(13, 10, 13, 10),
                Margin = new Thickness(0, 0, 10, 10),
                MinWidth = 132,
                Child = stack
            };

            if (accent == null)
            {
                return body;
            }

            // 左侧色条：用 Grid 而不是往 Border 上再套一层，省一层视觉树
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(4) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var bar = new Border { Background = accent, Margin = new Thickness(0, 0, 12, 0) };
            Grid.SetColumn(bar, 0);
            grid.Children.Add(bar);

            Grid.SetColumn(stack, 1);
            grid.Children.Add(stack);

            body.Padding = new Thickness(0, 10, 13, 10);
            body.Child = grid;
            return body;
        }

        // ================================================================ 条形

        /// <summary>
        /// 横向条形列表。长度以本列表的最大值为基准（也就是「相对最长的那条」），
        /// 所以它表达的是**排序和相对量级**，不是「占全库的百分比」。
        /// </summary>
        public static StackPanel BarList(VaultPalette p, IList<BarItem> items, string emptyText)
        {
            var root = new StackPanel();

            if (items == null || items.Count == 0)
            {
                root.Children.Add(Muted(p, string.IsNullOrEmpty(emptyText) ? "暂无数据。" : emptyText));
                return root;
            }

            var max = 0d;
            foreach (var item in items)
            {
                if (item.Value > max)
                {
                    max = item.Value;
                }
            }

            if (max <= 0)
            {
                max = 1;
            }

            for (var i = 0; i < items.Count; i++)
            {
                var item = items[i];
                var color = item.Color ?? p.SeriesAt(i);

                var row = new Grid { Margin = new Thickness(0, 0, 0, 7) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(118) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(78) });

                var label = new TextBlock
                {
                    Text = item.Label ?? string.Empty,
                    FontSize = 11.5,
                    Foreground = p.Text,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 8, 0)
                };
                Grid.SetColumn(label, 0);
                row.Children.Add(label);

                var bar = BuildBar(p, item.Value / max, color, 12);
                Grid.SetColumn(bar, 1);
                row.Children.Add(bar);

                var value = new TextBlock
                {
                    Text = item.ValueText ?? item.Value.ToString("0.#", CultureInfo.InvariantCulture),
                    FontSize = 11.5,
                    Foreground = p.TextMuted,
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Right
                };
                Grid.SetColumn(value, 2);
                row.Children.Add(value);

                root.Children.Add(row);
            }

            return root;
        }

        /// <summary>
        /// 一条 0~1 的进度条。轨道用 SurfaceAlt、填充用给定颜色。
        /// 宽度靠「填充星 : 剩余星」的比例交给布局系统，不需要知道容器多宽。
        /// </summary>
        private static Border BuildBar(VaultPalette p, double fraction, Brush color, double height)
        {
            if (fraction < 0)
            {
                fraction = 0;
            }
            if (fraction > 1)
            {
                fraction = 1;
            }

            var track = new Border
            {
                Background = p.SurfaceAlt,
                Height = height,
                VerticalAlignment = VerticalAlignment.Center,
                SnapsToDevicePixels = true
            };

            var grid = new Grid();
            // 极小值也要留一丝可见宽度，否则「1 字节 vs 1TB」看起来就是空的，
            // 而用户会以为「这条数据没读到」。
            var weight = Math.Max(fraction, 0.004);
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(weight, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1 - weight, GridUnitType.Star) });

            var fill = new Border
            {
                Background = color,
                Height = height,
                VerticalAlignment = VerticalAlignment.Center,
                SnapsToDevicePixels = true
            };
            Grid.SetColumn(fill, 0);
            grid.Children.Add(fill);

            track.Child = grid;
            return track;
        }

        // ================================================================ 堆叠条

        /// <summary>
        /// 一条 100% 宽的堆叠条：把总量按各段比例切开。
        /// 比环形更适合表达「构成」——因为它天然是水平的，放在卡片里不占高度。
        /// </summary>
        public static Grid StackedBar(VaultPalette p, IList<BarItem> items, double height)
        {
            var total = 0d;
            if (items != null)
            {
                foreach (var item in items)
                {
                    if (item.Value > 0)
                    {
                        total += item.Value;
                    }
                }
            }

            var grid = new Grid { Height = height };

            if (total <= 0)
            {
                grid.Children.Add(new Border { Background = p.SurfaceAlt });
                return grid;
            }

            for (var i = 0; i < items.Count; i++)
            {
                var item = items[i];
                if (item.Value <= 0)
                {
                    continue;
                }

                grid.ColumnDefinitions.Add(new ColumnDefinition
                {
                    Width = new GridLength(item.Value, GridUnitType.Star)
                });

                var segment = new Border
                {
                    Background = item.Color ?? p.SeriesAt(i),
                    Margin = new Thickness(0, 0, 2, 0),
                    SnapsToDevicePixels = true
                };
                Grid.SetColumn(segment, grid.ColumnDefinitions.Count - 1);
                grid.Children.Add(segment);
            }

            return grid;
        }

        /// <summary>图例：色块 + 名字 + 数值，一行一条。</summary>
        public static StackPanel Legend(VaultPalette p, IList<BarItem> items)
        {
            var root = new StackPanel();

            if (items == null)
            {
                return root;
            }

            for (var i = 0; i < items.Count; i++)
            {
                var item = items[i];

                var row = new Grid { Margin = new Thickness(0, 0, 0, 5) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var chip = new Border
                {
                    Width = 9,
                    Height = 9,
                    Background = item.Color ?? p.SeriesAt(i),
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(chip, 0);
                row.Children.Add(chip);

                var label = new TextBlock
                {
                    Text = item.Label ?? string.Empty,
                    FontSize = 11.5,
                    Foreground = p.Text,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(7, 0, 8, 0)
                };
                Grid.SetColumn(label, 1);
                row.Children.Add(label);

                var value = new TextBlock
                {
                    Text = item.ValueText ?? item.Value.ToString("0.#", CultureInfo.InvariantCulture),
                    FontSize = 11.5,
                    Foreground = p.TextMuted,
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(value, 2);
                row.Children.Add(value);

                root.Children.Add(row);
            }

            return root;
        }

        // ================================================================ 环形

        /// <summary>
        /// 环形图（donut）。固定正方形 —— 弧线要算三角函数，必须知道半径，
        /// 这是整个文件里唯一没法交给布局系统的地方。
        ///
        /// 画法：底下一个整圈当轨道，再每段画一条**粗描边的弧**。
        /// 用描边而不是「外弧+内弧+两条半径」围成闭合面，
        /// 是因为单段占满 360° 时闭合路径会塌掉（ArcSegment 画不出整圆），
        /// 而描边弧只要单独处理「满圈」这一种情况就够了。
        /// </summary>
        public static Grid Donut(VaultPalette p, IList<BarItem> items, double size,
            string centerValue, string centerCaption)
        {
            var canvas = new Canvas { Width = size, Height = size };

            var thickness = Math.Max(10, size * 0.155);
            var radius = (size - thickness) / 2;
            var center = size / 2;

            canvas.Children.Add(new Ellipse
            {
                Width = radius * 2,
                Height = radius * 2,
                Stroke = p.SurfaceAlt,
                StrokeThickness = thickness,
                Margin = new Thickness(center - radius)
            });

            var total = 0d;
            if (items != null)
            {
                foreach (var item in items)
                {
                    if (item.Value > 0)
                    {
                        total += item.Value;
                    }
                }
            }

            if (total > 0)
            {
                var angle = -90d;   // 从 12 点方向开始
                for (var i = 0; i < items.Count; i++)
                {
                    var item = items[i];
                    if (item.Value <= 0)
                    {
                        continue;
                    }

                    var sweep = 360d * (item.Value / total);
                    var color = item.Color ?? p.SeriesAt(i);

                    if (sweep >= 359.999)
                    {
                        canvas.Children.Add(new Ellipse
                        {
                            Width = radius * 2,
                            Height = radius * 2,
                            Stroke = color,
                            StrokeThickness = thickness,
                            Margin = new Thickness(center - radius)
                        });
                        break;
                    }

                    var path = new Path
                    {
                        Stroke = color,
                        StrokeThickness = thickness,
                        StrokeStartLineCap = PenLineCap.Flat,
                        StrokeEndLineCap = PenLineCap.Flat,
                        Data = BuildArc(center, radius, angle, sweep)
                    };
                    canvas.Children.Add(path);

                    angle += sweep;
                }
            }

            // 中间的文字：数值在上、说明在下，整体压在圆心
            var caption = new StackPanel
            {
                Width = size,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            caption.Children.Add(new TextBlock
            {
                Text = centerValue ?? string.Empty,
                FontSize = size * 0.155,
                FontWeight = FontWeights.Bold,
                Foreground = p.Text,
                HorizontalAlignment = HorizontalAlignment.Center
            });
            if (!string.IsNullOrEmpty(centerCaption))
            {
                caption.Children.Add(new TextBlock
                {
                    Text = centerCaption,
                    FontSize = size * 0.082,
                    Foreground = p.TextMuted,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 1, 0, 0)
                });
            }

            var host = new Grid { Width = size, Height = size };
            host.Children.Add(canvas);
            host.Children.Add(caption);
            return host;
        }

        /// <summary>一段顺时针圆弧的几何。</summary>
        private static Geometry BuildArc(double center, double radius, double startDegrees, double sweepDegrees)
        {
            var start = PointOnCircle(center, radius, startDegrees);
            var end = PointOnCircle(center, radius, startDegrees + sweepDegrees);

            var figure = new PathFigure
            {
                StartPoint = start,
                IsClosed = false,
                IsFilled = false
            };

            figure.Segments.Add(new ArcSegment
            {
                Point = end,
                Size = new Size(radius, radius),
                IsLargeArc = sweepDegrees > 180,
                SweepDirection = SweepDirection.Clockwise
            });

            var geometry = new PathGeometry();
            geometry.Figures.Add(figure);
            geometry.Freeze();
            return geometry;
        }

        private static Point PointOnCircle(double center, double radius, double degrees)
        {
            var rad = degrees * Math.PI / 180.0;
            return new Point(center + radius * Math.Cos(rad), center + radius * Math.Sin(rad));
        }
    }
}
