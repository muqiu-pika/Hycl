using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Reactive;
using Avalonia.Styling;

namespace Hycl.Launcher.UI.Controls;

/// <summary>
/// 设置页的加载动画：启动器图标像球一样上下弹跳（ease-in-out），
/// 落地时轻微压扁后回弹，循环播放直到数据就绪（由 <see cref="IsVisible"/> 控制启停）。
/// 动画完全由 Avalonia 的 <see cref="Animation"/> 驱动，且仅在控件实际可见时运行，
/// 不在 UI 线程做任何同步等待。
/// </summary>
public class BouncingLogo : Control
{
    public static readonly StyledProperty<string?> SourceProperty =
        AvaloniaProperty.Register<BouncingLogo, string?>(nameof(Source));

    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<BouncingLogo, string?>(nameof(Text));

    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        AvaloniaProperty.Register<BouncingLogo, IBrush?>(nameof(Foreground));

    private const double JumpHeight = 26;
    private const int RiseMilliseconds = 420;
    private const int FallMilliseconds = 380;
    private const int SquashMilliseconds = 90;
    private const int RecoverMilliseconds = 170;
    private const int RestMilliseconds = 110;

    private readonly Image _logo;
    private readonly TextBlock _caption;
    private readonly TranslateTransform _move = new();
    private readonly ScaleTransform _squash = new(1, 1);
    private CancellationTokenSource? _loopCts;

    public BouncingLogo()
    {
        _logo = new Image
        {
            Width = 72,
            Height = 72,
            Stretch = Stretch.Uniform,
            RenderTransformOrigin = new RelativePoint(0.5, 1, RelativeUnit.Relative),
            RenderTransform = new TransformGroup
            {
                Children = { _squash, _move },
            },
        };

        _caption = new TextBlock
        {
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            FontSize = 12.5,
            Margin = new Thickness(0, 12, 0, 0),
        };

        var panel = new StackPanel
        {
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Children = { _logo, _caption },
        };
        VisualChildren.Add(panel);
        LogicalChildren.Add(panel);

        _caption.Bind(TextBlock.TextProperty, this.GetObservable(TextProperty));
        _caption.Bind(TextBlock.ForegroundProperty, this.GetObservable(ForegroundProperty));

        AttachedToVisualTree += (_, _) => SyncLoop();
        DetachedFromVisualTree += (_, _) => StopLoop();
        // 由 IsVisible 绑定（IsLoading）驱动启停；页面隐藏时也会随 IsEffectivelyVisible 退出。
        this.GetObservable(IsVisibleProperty)
            .Subscribe(new AnonymousObserver<bool>(_ => SyncLoop()));
        UpdateSource();
    }

    /// <summary>图标资源地址（如 avares://Hycl.Launcher.UI/Assets/hycl.png）。</summary>
    public string? Source
    {
        get => GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    /// <summary>图标下方的加载提示文本（可为空）。</summary>
    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    /// <summary>提示文本颜色。</summary>
    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SourceProperty)
        {
            UpdateSource();
        }
    }

    private void UpdateSource()
    {
        var source = Source;
        if (string.IsNullOrWhiteSpace(source))
        {
            _logo.Source = null;
            return;
        }

        try
        {
            _logo.Source = new Bitmap(AssetLoader.Open(new Uri(source)));
        }
        catch (Exception)
        {
            // 资源缺失时不阻断页面加载，图标留空。
            _logo.Source = null;
        }
    }

    private void SyncLoop()
    {
        if (IsEffectivelyVisible)
        {
            StartLoop();
        }
        else
        {
            StopLoop();
        }
    }

    private void StartLoop()
    {
        if (_loopCts is not null)
        {
            return;
        }

        _loopCts = new CancellationTokenSource();
        _ = RunLoopAsync(_loopCts.Token);
    }

    private void StopLoop()
    {
        _loopCts?.Cancel();
        _loopCts = null;
        _move.Y = 0;
        _squash.ScaleX = 1;
        _squash.ScaleY = 1;
    }

    private async Task RunLoopAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                // 起跳 + 下落都使用 ease-in-out，接近顶点与落地时速度自然放缓。
                await AnimateAsync(_move, TranslateTransform.YProperty, 0, -JumpHeight, RiseMilliseconds,
                    new SineEaseInOut(), token);
                await AnimateAsync(_move, TranslateTransform.YProperty, -JumpHeight, 0, FallMilliseconds,
                    new SineEaseInOut(), token);

                // 落地压扁：横向略撑、纵向略压，随后回弹恢复成球。
                await SquashAsync(1.0, 0.80, 1.14, SquashMilliseconds, token);
                await SquashAsync(0.80, 1.0, 1.0, RecoverMilliseconds, token);

                await Task.Delay(RestMilliseconds, token);
            }
        }
        catch (OperationCanceledException)
        {
            // 数据就绪或控件隐藏时正常结束，不做补偿。
        }
    }

    private static async Task AnimateAsync(
        Animatable target,
        AvaloniaProperty property,
        double from,
        double to,
        int durationMs,
        Easing easing,
        CancellationToken token)
    {
        var animation = BuildAnimation(property, from, to, durationMs, easing);
        await animation.RunAsync(target, token);
        target.SetValue(property, to);
    }

    private async Task SquashAsync(
        double fromScale,
        double toScaleY,
        double toScaleX,
        int durationMs,
        CancellationToken token)
    {
        var animation = new Animation
        {
            Duration = TimeSpan.FromMilliseconds(durationMs),
            Easing = new CubicEaseOut(),
            Children =
            {
                new KeyFrame
                {
                    Cue = new Cue(0d),
                    Setters =
                    {
                        new Setter(ScaleTransform.ScaleYProperty, fromScale),
                        new Setter(ScaleTransform.ScaleXProperty, fromScale),
                    },
                },
                new KeyFrame
                {
                    Cue = new Cue(1d),
                    Setters =
                    {
                        new Setter(ScaleTransform.ScaleYProperty, toScaleY),
                        new Setter(ScaleTransform.ScaleXProperty, toScaleX),
                    },
                },
            },
        };

        await animation.RunAsync(_squash, token);
        _squash.ScaleY = toScaleY;
        _squash.ScaleX = toScaleX;
    }

    private static Animation BuildAnimation(
        AvaloniaProperty property,
        double from,
        double to,
        int durationMs,
        Easing easing) =>
        new()
        {
            Duration = TimeSpan.FromMilliseconds(durationMs),
            Easing = easing,
            Children =
            {
                new KeyFrame
                {
                    Cue = new Cue(0d),
                    Setters = { new Setter(property, from) },
                },
                new KeyFrame
                {
                    Cue = new Cue(1d),
                    Setters = { new Setter(property, to) },
                },
            },
        };
}
