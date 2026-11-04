using System;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;

namespace Hycl.Launcher.UI.Controls;

/// <summary>
/// 全局唯一的等待加载动画：软件图标像一颗球从原位（空中）落到不可见的虚拟地面，
/// 落地瞬间轻微压扁后反弹回原位，循环往复；下落用 ease-in 加速、上升用 ease-out 减速。
/// 动画由 Avalonia 的样式动画（无限迭代）驱动，控件不可见时自动暂停，不在 UI 线程等待。
/// 全软件所有加载场景共用此控件。
/// </summary>
public class McLoading : Control
{
    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<McLoading, string?>(nameof(Text));

    /// <summary>提示文本颜色。</summary>
    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        AvaloniaProperty.Register<McLoading, IBrush?>(nameof(Foreground));

    /// <summary>图标边长（像素）。</summary>
    public static readonly StyledProperty<double> IconSizeProperty =
        AvaloniaProperty.Register<McLoading, double>(nameof(IconSize), 32d);

    private const string IconResource = "avares://Hycl.Launcher.UI/Assets/hycl.png";

    private static readonly TimeSpan CycleDuration = TimeSpan.FromMilliseconds(1500);

    /// <summary>图标从原位落到虚拟地面的距离（固定值，避免运行期重建样式）。</summary>
    private const double FallDistance = 16d;

    private readonly Image _icon;

    private readonly Grid _iconArea;

    private readonly TextBlock _textBlock;

    private readonly StackPanel _panel;

    private readonly TranslateTransform _fall = new();

    private readonly ScaleTransform _squash = new(1, 1);

    /// <summary>加载提示文本（可为空）。</summary>
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

    /// <summary>图标边长（像素）。</summary>
    public double IconSize
    {
        get => GetValue(IconSizeProperty);
        set => SetValue(IconSizeProperty, value);
    }

    public McLoading()
    {
        _icon = new Image
        {
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            // 变形原点放在图标底部中心：压扁时底部贴住虚拟地面，不会穿模。
            RenderTransformOrigin = new RelativePoint(0.5, 1, RelativeUnit.Relative),
            RenderTransform = new TransformGroup
            {
                Children = { _squash, _fall },
            },
            Source = new Bitmap(AssetLoader.Open(new Uri(IconResource))),
        };

        _iconArea = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { _icon },
        };

        _textBlock = new TextBlock
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 0),
            FontSize = 12.5,
            IsVisible = false,
        };

        _panel = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { _iconArea, _textBlock },
        };
        VisualChildren.Add(_panel);
        LogicalChildren.Add(_panel);

        _textBlock.Bind(TextBlock.TextProperty, this.GetObservable(TextProperty));
        _textBlock.Bind(TextBlock.ForegroundProperty, this.GetObservable(ForegroundProperty));

        // 样式动画必须在控件进入可视树前挂到样式集合上，之后由 Avalonia 自动播放/暂停。
        Styles.Add(BuildAnimationStyle());

        UpdateMetrics();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        _panel.Measure(availableSize);
        return _panel.DesiredSize;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        // 无论父容器如何拉伸，都把动画区域放在正中央（水平 + 垂直）。
        var width = Math.Min(_panel.DesiredSize.Width, finalSize.Width);
        var height = Math.Min(_panel.DesiredSize.Height, finalSize.Height);
        _panel.Arrange(new Rect(
            (finalSize.Width - width) / 2,
            (finalSize.Height - height) / 2,
            width,
            height));
        return finalSize;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TextProperty)
        {
            _textBlock.IsVisible = !string.IsNullOrWhiteSpace(Text);
        }
        else if (change.Property == IconSizeProperty)
        {
            UpdateMetrics();
        }
    }

    private void UpdateMetrics()
    {
        var size = Math.Clamp(IconSize, 16, 160);

        _icon.Width = size;
        _icon.Height = size;
        // 顶部预留固定下落高度，并给落地压扁（横向 1.14 倍）留出余量，避免超出容器。
        _iconArea.Width = size * 1.28;
        _iconArea.Height = size + FallDistance;
    }

    /// <summary>
    /// 构建一段循环动画：0% 在空中 → 42% 加速下坠到虚拟地面 → 落地压扁并回弹 →
    /// 100% 用 ease-out 减速回到原位。只有垂直位移与底部压扁，不绘制任何地面元素。
    /// </summary>
    private static Style BuildAnimationStyle() =>
        new(x => x.OfType<Image>())
        {
            Animations =
            {
                new Animation
                {
                    Duration = CycleDuration,
                    IterationCount = IterationCount.Infinite,
                    Children =
                    {
                        // 垂直位移：空中原位 → 虚拟地面（ease-in）→ 回弹升空（ease-out）
                        new KeyFrame
                        {
                            Cue = new Cue(0d),
                            Setters = { new Setter(TranslateTransform.YProperty, 0d) },
                        },
                        new KeyFrame
                        {
                            Cue = new Cue(0.42d),
                            KeySpline = new KeySpline(0.42, 0, 1, 1),
                            Setters = { new Setter(TranslateTransform.YProperty, FallDistance) },
                        },
                        new KeyFrame
                        {
                            Cue = new Cue(0.6d),
                            Setters = { new Setter(TranslateTransform.YProperty, FallDistance) },
                        },
                        new KeyFrame
                        {
                            Cue = new Cue(1d),
                            KeySpline = new KeySpline(0, 0, 0.58, 1),
                            Setters = { new Setter(TranslateTransform.YProperty, 0d) },
                        },
                        // 落地压扁（原点在底部中心：横向变宽、纵向变矮，不会穿模）
                        new KeyFrame
                        {
                            Cue = new Cue(0d),
                            Setters =
                            {
                                new Setter(ScaleTransform.ScaleXProperty, 1d),
                                new Setter(ScaleTransform.ScaleYProperty, 1d),
                            },
                        },
                        new KeyFrame
                        {
                            Cue = new Cue(0.42d),
                            Setters =
                            {
                                new Setter(ScaleTransform.ScaleXProperty, 1d),
                                new Setter(ScaleTransform.ScaleYProperty, 1d),
                            },
                        },
                        new KeyFrame
                        {
                            Cue = new Cue(0.46d),
                            KeySpline = new KeySpline(0.2, 0, 0.8, 1),
                            Setters =
                            {
                                new Setter(ScaleTransform.ScaleXProperty, 1.14d),
                                new Setter(ScaleTransform.ScaleYProperty, 0.84d),
                            },
                        },
                        new KeyFrame
                        {
                            Cue = new Cue(0.54d),
                            KeySpline = new KeySpline(0.2, 0, 0.8, 1),
                            Setters =
                            {
                                new Setter(ScaleTransform.ScaleXProperty, 0.97d),
                                new Setter(ScaleTransform.ScaleYProperty, 1.02d),
                            },
                        },
                        new KeyFrame
                        {
                            Cue = new Cue(0.6d),
                            Setters =
                            {
                                new Setter(ScaleTransform.ScaleXProperty, 1d),
                                new Setter(ScaleTransform.ScaleYProperty, 1d),
                            },
                        },
                        new KeyFrame
                        {
                            Cue = new Cue(1d),
                            Setters =
                            {
                                new Setter(ScaleTransform.ScaleXProperty, 1d),
                                new Setter(ScaleTransform.ScaleYProperty, 1d),
                            },
                        },
                    },
                },
            },
        };
}
