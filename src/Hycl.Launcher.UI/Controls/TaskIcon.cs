using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Hycl.Launcher.UI.Controls;

/// <summary>
/// 安装任务图标：优先显示社区资源自身的网络图标（异步加载，失败后回退），
/// 否则显示版本/加载器/整合包对应的方块位图，最后兜底启动器图标，不留空白或裂图。
/// 尺寸由 <see cref="Size"/> 控制，随外层进度圈缩放。
/// </summary>
public class TaskIcon : Control
{
    private const string LauncherIconResource = "avares://Hycl.Launcher.UI/Assets/hycl.png";

    public static readonly StyledProperty<string?> KindProperty =
        AvaloniaProperty.Register<TaskIcon, string?>(nameof(Kind));

    public static readonly StyledProperty<string?> UrlProperty =
        AvaloniaProperty.Register<TaskIcon, string?>(nameof(Url));

    public static readonly StyledProperty<double> SizeProperty =
        AvaloniaProperty.Register<TaskIcon, double>(nameof(Size), 24d);

    private static Bitmap? _launcherIcon;
    private static bool _launcherIconLoaded;

    private readonly Panel _panel;
    private readonly Image _launcher;
    private readonly BlockIcon _block;
    private readonly HyclImage _photo;

    /// <summary>网络图标的当前加载结果：null=加载中，true=成功，false=失败。</summary>
    private bool? _photoResult;

    /// <summary>图标键名（如 "grass-block" / "forge" / "package"）。</summary>
    public string? Kind
    {
        get => GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    /// <summary>社区资源自身的图标地址（可为空）。</summary>
    public string? Url
    {
        get => GetValue(UrlProperty);
        set => SetValue(UrlProperty, value);
    }

    /// <summary>图标边长（像素）。</summary>
    public double Size
    {
        get => GetValue(SizeProperty);
        set => SetValue(SizeProperty, value);
    }

    public TaskIcon()
    {
        _launcher = new Image
        {
            Stretch = Stretch.Uniform,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Source = LauncherIcon,
            IsVisible = false,
        };

        _block = new BlockIcon
        {
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            IsVisible = false,
        };

        _photo = new HyclImage
        {
            Stretch = Stretch.Uniform,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            IsVisible = false,
        };
        _photo.LoadCompleted += OnPhotoLoaded;

        _panel = new Panel
        {
            Children = { _launcher, _block, _photo },
        };
        VisualChildren.Add(_panel);
        LogicalChildren.Add(_panel);

        UpdateMetrics();
        UpdateContent();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var side = Math.Clamp(Size, 0, 512);
        _panel.Measure(new Size(side, side));
        return new Size(side, side);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        _panel.Arrange(new Rect(0, 0, finalSize.Width, finalSize.Height));
        return finalSize;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == KindProperty || change.Property == UrlProperty)
        {
            UpdateContent();
        }
        else if (change.Property == SizeProperty)
        {
            UpdateMetrics();
        }
    }

    private void UpdateMetrics()
    {
        var side = Math.Clamp(Size, 0, 512);
        foreach (var child in new Control[] { _launcher, _block, _photo })
        {
            child.Width = side;
            child.Height = side;
        }

        InvalidateMeasure();
    }

    private void UpdateContent()
    {
        _block.Kind = Kind;

        var url = Url;
        if (string.IsNullOrWhiteSpace(url))
        {
            _photoResult = false;
            _photo.Url = null;
        }
        else if (!string.Equals(_photo.Url, url, StringComparison.Ordinal))
        {
            _photoResult = null;
            _photo.Url = url;
        }

        UpdateVisibility();
    }

    private void OnPhotoLoaded(object? sender, bool success)
    {
        _photoResult = success;
        UpdateVisibility();
    }

    private void UpdateVisibility()
    {
        var photoVisible = _photoResult == true;
        var blockVisible = !photoVisible && _block.Source is not null;

        _photo.IsVisible = photoVisible;
        _block.IsVisible = blockVisible;
        _launcher.IsVisible = !photoVisible && !blockVisible;
    }

    private static Bitmap? LauncherIcon
    {
        get
        {
            if (!_launcherIconLoaded)
            {
                _launcherIconLoaded = true;
                try
                {
                    _launcherIcon = new Bitmap(AssetLoader.Open(new Uri(LauncherIconResource)));
                }
                catch (Exception)
                {
                    _launcherIcon = null;
                }
            }

            return _launcherIcon;
        }
    }
}
