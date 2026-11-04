using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;

namespace Hycl.Launcher.UI.Controls;

/// <summary>
/// 全局等待加载动画：软件图标（hycl.png）像球一样上下来回弹跳，可选底部提示文本。
/// 全软件所有加载场景共用此控件（对应原 PCL-CE 镐子挖矿动画的替代设计）。
/// </summary>
public class McLoading : Control
{
    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<McLoading, string?>(nameof(Text));

    /// <summary>提示文本颜色。</summary>
    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        AvaloniaProperty.Register<McLoading, IBrush?>(nameof(Foreground));

    /// <summary>提示文本颜色。</summary>
    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    /// <summary>单次弹跳高度（像素）。</summary>
    private const double BounceHeight = 12;

    private readonly Image _icon;

    private readonly TranslateTransform _bounce = new();

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
        _icon = new Image
        {
            Width = 32,
            Height = 32,
            Source = new Bitmap(AssetLoader.Open(new Uri("avares://Hycl.Launcher.UI/Assets/hycl.png"))),
            RenderTransform = _bounce,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Bottom,
        };

        // 上方留出弹跳空间，图标从底部弹起
        var iconArea = new Grid
        {
            Height = 32 + BounceHeight,
            Children = { _icon },
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
            Children = { iconArea, _textBlock },
        };
        VisualChildren.Add(panel);
        LogicalChildren.Add(panel);

        _textBlock.Bind(TextBlock.TextProperty, this.GetObservable(TextProperty));
        _textBlock.Bind(TextBlock.ForegroundProperty, this.GetObservable(ForegroundProperty));

        AttachedToVisualTree += (_, _) => StartLoop();
        DetachedFromVisualTree += (_, _) => StopLoop();
    }

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

    /// <summary>球类弹跳：升起用缓出（减速到顶），落下用缓入（加速触底），触底稍作停顿。</summary>
    private async Task RunLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await AnimateYAsync(_bounce, -BounceHeight, 260, new CubicEaseOut(), token);
                await AnimateYAsync(_bounce, 0, 260, new CubicEaseIn(), token);
                await Task.Delay(140, token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private static async Task AnimateYAsync(
        TranslateTransform transform,
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
                    Setters = { new Setter(TranslateTransform.YProperty, transform.Y) },
                },
                new KeyFrame
                {
                    Cue = new Cue(1d),
                    Setters = { new Setter(TranslateTransform.YProperty, to) },
                },
            },
        };
        await animation.RunAsync(transform, token);
        transform.Y = to;
    }
}
