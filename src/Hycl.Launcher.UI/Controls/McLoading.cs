using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using PathShape = Avalonia.Controls.Shapes.Path;
using Avalonia.Media;
using Avalonia.Styling;

namespace Hycl.Launcher.UI.Controls;

/// <summary>
/// PCL-CE 风格的加载动画：镐子挥下挖地、泥土粒子飞出，循环往复。
/// 移植自 PCL-CE 的 MyLoading 控件，用于替换框架原始的加载条。
/// 纯代码构建可视树，直接持有元素引用。
/// </summary>
public class McLoading : Control
{
    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<McLoading, string?>(nameof(Text));

    /// <summary>动画与描边颜色。</summary>
    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        AvaloniaProperty.Register<McLoading, IBrush?>(nameof(Foreground));

    /// <summary>动画与描边颜色。</summary>
    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    private readonly PathShape _pickaxe;
    private readonly PathShape _dirtLeft;
    private readonly PathShape _dirtRight;
    private readonly TranslateTransform _dirtLeftMove = new();
    private readonly TranslateTransform _dirtRightMove = new();
    private readonly RotateTransform _pickaxeRotate = new(55);
    private readonly TextBlock _textBlock;
    private CancellationTokenSource? _loopCts;

    /// <summary>加载提示文本（可为空）。</summary>
    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public McLoading()
    {
        const string pickaxeData =
            "M 963.6 858.2 410.816 305.504 C 508.116 213.304 609.204 196.8 711.104 128.6 " +
            "837.11367 49.573762 879.34045 50.334062 751.5 49.5 611.3 52 471.8 96.2 353.3 182.4 " +
            "309.8 155.7 252.1 161.2 214.5 198.9 176.9 236.6 171.3 294.2 198 337.7 111.8 456.3 " +
            "67.6 595.8 65.1 735.9 63.315254 883.82034 65.077966 837.29308 144.2 695.488 " +
            "212.4 593.588 228.888 492.4 321.088 395.2 L 873.9 948 c 0.60001 0.59999 1.6 0.6 2.2 0 " +
            "l 87.5 -87.5 c 0.6 -0.7 0.6 -1.6 0 -2.3 z";

        _pickaxe = new PathShape
        {
            Stretch = Stretch.Uniform,
            Height = 35,
            Margin = new Thickness(10, 6, 0, 0),
            StrokeThickness = 2,
            Data = StreamGeometry.Parse(pickaxeData),
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top,
            RenderTransform = _pickaxeRotate,
        };
        _dirtLeft = CreateDirt(-45, new Thickness(7, 41, 0, 0), _dirtLeftMove);
        _dirtRight = CreateDirt(45, new Thickness(14, 41, 0, 0), _dirtRightMove);

        var inner = new Grid
        {
            Width = 60,
            Height = 47,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            Children = { _pickaxe, _dirtLeft, _dirtRight },
        };
        _textBlock = new TextBlock
        {
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 0),
            FontSize = 12.5,
        };
        var panel = new StackPanel
        {
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Children = { inner, _textBlock },
        };
        VisualChildren.Add(panel);
        LogicalChildren.Add(panel);

        // 颜色跟随 Foreground。
        _pickaxe.Bind(PathShape.StrokeProperty, this.GetObservable(ForegroundProperty));
        _dirtLeft.Bind(PathShape.FillProperty, this.GetObservable(ForegroundProperty));
        _dirtRight.Bind(PathShape.FillProperty, this.GetObservable(ForegroundProperty));
        _textBlock.Bind(TextBlock.TextProperty, this.GetObservable(TextProperty));

        AttachedToVisualTree += (_, _) => StartLoop();
        DetachedFromVisualTree += (_, _) => StopLoop();
    }

    private static PathShape CreateDirt(double rotate, Thickness margin, TranslateTransform move) => new()
    {
        Opacity = 0,
        Stretch = Stretch.Uniform,
        Width = 3,
        Height = 5,
        Data = StreamGeometry.Parse("M0,0L2,0L1,4"),
        Margin = margin,
        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top,
        RenderTransform = new TransformGroup
        {
            Children = { new RotateTransform(rotate), move },
        },
    };

    private void StartLoop()
    {
        StopLoop();
        _loopCts = new CancellationTokenSource();
        _ = RunLoopAsync(_loopCts.Token);
    }

    private void StopLoop()
    {
        _loopCts?.Cancel();
        _loopCts = null;
    }

    /// <summary>
    /// 动画时序与 PCL-CE 相同：抬起 350ms（回缓入）→ 挥下 900ms（缓出）→ 回弹 900ms（弹性），
    /// 挥下触地时左右两粒泥土飞出并淡去。
    /// </summary>
    private async Task RunLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await AnimateAngleAsync(_pickaxeRotate, -20, 350, new BackEaseIn(), token);
                await AnimateAngleAsync(_pickaxeRotate, 30, 900, new CubicEaseOut(), token);

                // 挖到地：粒子飞出。
                _dirtLeft.Opacity = 1;
                _dirtRight.Opacity = 1;
                _dirtLeftMove.X = _dirtLeftMove.Y = 0;
                _dirtRightMove.X = _dirtRightMove.Y = 0;
                _ = FlyDirtAsync(_dirtLeft, _dirtLeftMove, -5, -6, token);
                _ = FlyDirtAsync(_dirtRight, _dirtRightMove, 5, -6, token);

                await AnimateAngleAsync(_pickaxeRotate, 55, 900, new ElasticEaseOut(), token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private static async Task AnimateAngleAsync(
        RotateTransform transform,
        double to,
        int durationMs,
        Easing easing,
        CancellationToken token)
    {
        var animation = new Animation
        {
            Duration = TimeSpan.FromMilliseconds(durationMs),
            Easing = easing,
            Children =
            {
                new KeyFrame
                {
                    Cue = new Cue(0d),
                    Setters = { new Setter(RotateTransform.AngleProperty, transform.Angle) },
                },
                new KeyFrame
                {
                    Cue = new Cue(1d),
                    Setters = { new Setter(RotateTransform.AngleProperty, to) },
                },
            },
        };
        await animation.RunAsync(transform, token);
        transform.Angle = to;
    }

    private static async Task FlyDirtAsync(PathShape dirt, TranslateTransform move, double toX, double toY, CancellationToken token)
    {
        try
        {
            var moveAnimation = new Animation
            {
                Duration = TimeSpan.FromMilliseconds(180),
                Easing = new CubicEaseOut(),
                Children =
                {
                    new KeyFrame
                    {
                        Cue = new Cue(0d),
                        Setters = { new Setter(TranslateTransform.XProperty, 0d), new Setter(TranslateTransform.YProperty, 0d) },
                    },
                    new KeyFrame
                    {
                        Cue = new Cue(1d),
                        Setters = { new Setter(TranslateTransform.XProperty, toX), new Setter(TranslateTransform.YProperty, toY) },
                    },
                },
            };
            var fade = new Animation
            {
                Duration = TimeSpan.FromMilliseconds(100),
                Delay = TimeSpan.FromMilliseconds(50),
                Children =
                {
                    new KeyFrame
                    {
                        Cue = new Cue(0d),
                        Setters = { new Setter(Visual.OpacityProperty, 1d) },
                    },
                    new KeyFrame
                    {
                        Cue = new Cue(1d),
                        Setters = { new Setter(Visual.OpacityProperty, 0d) },
                    },
                },
            };
            await Task.WhenAll(
                moveAnimation.RunAsync(move, token),
                fade.RunAsync(dirt, token));
            dirt.Opacity = 0;
        }
        catch (OperationCanceledException)
        {
        }
    }
}
