using Avalonia;
using Avalonia.Controls.Primitives;
using Avalonia.Media;

namespace Hycl.Launcher.UI.Controls;

/// <summary>
/// 线性描边图标控件：以 24x24 视口渲染 <see cref="IconGeometry"/> 中的预置矢量图标，
/// 颜色跟随 Foreground。对应网页版所用的 Lucide 线性图标风格。
/// </summary>
public class HyclIcon : TemplatedControl
{
    public static readonly StyledProperty<string?> KindProperty =
        AvaloniaProperty.Register<HyclIcon, string?>(nameof(Kind));

    public static readonly StyledProperty<Geometry?> GeometryProperty =
        AvaloniaProperty.Register<HyclIcon, Geometry?>(nameof(Geometry));

    public static readonly StyledProperty<IBrush?> FillProperty =
        AvaloniaProperty.Register<HyclIcon, IBrush?>(nameof(Fill));

    /// <summary>图标键名（如 "play" / "download"），来自 ViewModel 的字符串以避免 Core 感知 UI。</summary>
    public string? Kind
    {
        get => GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    /// <summary>由 Kind 解析出的几何数据，供模板中的 Path 使用。</summary>
    public Geometry? Geometry
    {
        get => GetValue(GeometryProperty);
        set => SetValue(GeometryProperty, value);
    }

    /// <summary>可选填充色（如播放三角需要实心时使用），默认仅描边。</summary>
    public IBrush? Fill
    {
        get => GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == KindProperty)
        {
            SetCurrentValue(GeometryProperty, IconGeometry.Resolve(Kind));
        }
    }
}
