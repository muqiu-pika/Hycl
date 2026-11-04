using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Hycl.Launcher.UI.Controls;

/// <summary>
/// 环形进度指示（对应 PCL-CE 右下角下载按钮的进度环）：
/// 一圈细底环 + 从正上方顺时针推进的进度弧，进度以轻微的颜色差呈现。
/// </summary>
public class McProgressRing : Control
{
    public static readonly StyledProperty<double> PercentProperty =
        AvaloniaProperty.Register<McProgressRing, double>(
            nameof(Percent),
            coerce: (_, value) => Math.Clamp(value, 0d, 100d));

    public static readonly StyledProperty<IBrush?> TrackBrushProperty =
        AvaloniaProperty.Register<McProgressRing, IBrush?>(nameof(TrackBrush));

    public static readonly StyledProperty<IBrush?> ProgressBrushProperty =
        AvaloniaProperty.Register<McProgressRing, IBrush?>(nameof(ProgressBrush));

    public static readonly StyledProperty<double> ThicknessProperty =
        AvaloniaProperty.Register<McProgressRing, double>(nameof(Thickness), 3d);

    static McProgressRing()
    {
        AffectsRender<McProgressRing>(PercentProperty, TrackBrushProperty, ProgressBrushProperty, ThicknessProperty);
    }

    public double Percent
    {
        get => GetValue(PercentProperty);
        set => SetValue(PercentProperty, value);
    }

    public IBrush? TrackBrush
    {
        get => GetValue(TrackBrushProperty);
        set => SetValue(TrackBrushProperty, value);
    }

    public IBrush? ProgressBrush
    {
        get => GetValue(ProgressBrushProperty);
        set => SetValue(ProgressBrushProperty, value);
    }

    public double Thickness
    {
        get => GetValue(ThicknessProperty);
        set => SetValue(ThicknessProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        var side = Math.Min(Bounds.Width, Bounds.Height);
        if (side <= 0)
        {
            return;
        }

        var center = new Point(Bounds.Width / 2, Bounds.Height / 2);
        var radius = Math.Max(0, side / 2 - Thickness / 2);

        if (TrackBrush is { } track)
        {
            context.DrawEllipse(null, new Pen(track, Thickness, lineCap: PenLineCap.Round), center, radius, radius);
        }

        var percent = Percent;
        if (percent <= 0 || ProgressBrush is not { } progress)
        {
            return;
        }

        var progressPen = new Pen(progress, Thickness, lineCap: PenLineCap.Round);
        if (percent >= 99.95)
        {
            context.DrawEllipse(null, progressPen, center, radius, radius);
            return;
        }

        // 从正上方开始顺时针推进；50% 以内走小弧，超过后走大弧
        var angle = percent / 100d * 2d * Math.PI;
        var start = new Point(center.X, center.Y - radius);
        var end = new Point(
            center.X + radius * Math.Sin(angle),
            center.Y - radius * Math.Cos(angle));
        var figure = new PathFigure
        {
            StartPoint = start,
            IsClosed = false,
            Segments = new PathSegments
            {
                new ArcSegment
                {
                    Point = end,
                    Size = new Size(radius, radius),
                    IsLargeArc = percent > 50,
                    SweepDirection = SweepDirection.Clockwise
                }
            }
        };
        var geometry = new PathGeometry { Figures = new PathFigures { figure } };
        context.DrawGeometry(null, progressPen, geometry);
    }
}
