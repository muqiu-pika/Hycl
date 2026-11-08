using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

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

    /// <summary>不确定态：进度未知时画一段匀速旋转的弧，避免百分比停在 0 看起来像卡死。</summary>
    public static readonly StyledProperty<bool> IsIndeterminateProperty =
        AvaloniaProperty.Register<McProgressRing, bool>(nameof(IsIndeterminate));

    /// <summary>不确定态弧的当前起始角度（度）；仅在动画计时器运行时变化。</summary>
    private double _sweepAngle;

    private DispatcherTimer? _animationTimer;

    static McProgressRing()
    {
        AffectsRender<McProgressRing>(
            PercentProperty,
            TrackBrushProperty,
            ProgressBrushProperty,
            ThicknessProperty,
            IsIndeterminateProperty);
    }

    public bool IsIndeterminate
    {
        get => GetValue(IsIndeterminateProperty);
        set => SetValue(IsIndeterminateProperty, value);
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

        if (ProgressBrush is not { } progress)
        {
            return;
        }

        var progressPen = new Pen(progress, Thickness, lineCap: PenLineCap.Round);
        if (IsIndeterminate)
        {
            // 不确定态：一段约 90° 的弧绕圆心匀速旋转
            DrawArc(context, progressPen, center, radius, _sweepAngle, 90d);
            return;
        }

        var percent = Percent;
        if (percent <= 0)
        {
            return;
        }

        if (percent >= 99.95)
        {
            context.DrawEllipse(null, progressPen, center, radius, radius);
            return;
        }

        // 从正上方开始顺时针推进；50% 以内走小弧，超过后走大弧
        DrawArc(context, progressPen, center, radius, 0d, percent / 100d * 360d, percent > 50);
    }

    /// <summary>按起始角度与扫过角度绘制一段圆弧（角度以正上方为 0、顺时针为正）。</summary>
    private static void DrawArc(
        DrawingContext context,
        IPen pen,
        Point center,
        double radius,
        double startDegrees,
        double sweepDegrees,
        bool isLargeArc = false)
    {
        var start = startDegrees * Math.PI / 180d;
        var end = (startDegrees + sweepDegrees) * Math.PI / 180d;
        var startPoint = new Point(
            center.X + radius * Math.Sin(start),
            center.Y - radius * Math.Cos(start));
        var endPoint = new Point(
            center.X + radius * Math.Sin(end),
            center.Y - radius * Math.Cos(end));
        var figure = new PathFigure
        {
            StartPoint = startPoint,
            IsClosed = false,
            Segments = new PathSegments
            {
                new ArcSegment
                {
                    Point = endPoint,
                    Size = new Size(radius, radius),
                    IsLargeArc = isLargeArc,
                    SweepDirection = SweepDirection.Clockwise
                }
            }
        };
        context.DrawGeometry(null, pen, new PathGeometry { Figures = new PathFigures { figure } });
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        UpdateAnimation();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        StopAnimation();
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsIndeterminateProperty)
        {
            UpdateAnimation();
        }
    }

    private void UpdateAnimation()
    {
        if (IsIndeterminate && VisualRoot is not null)
        {
            if (_animationTimer is not null)
            {
                return;
            }

            _animationTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
            _animationTimer.Tick += (_, _) =>
            {
                _sweepAngle = (_sweepAngle + 4d) % 360d;
                InvalidateVisual();
            };
            _animationTimer.Start();
            return;
        }

        StopAnimation();
    }

    private void StopAnimation()
    {
        _animationTimer?.Stop();
        _animationTimer = null;
    }
}
