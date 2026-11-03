using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;

namespace Hycl.Launcher.UI.Controls;

/// <summary>
/// 卡片容器：半透明白底 + 细边框 + 标题，对应网页版 McCard 组件。
/// 悬停时边框与阴影轻微高亮（见 Themes/McCard.axaml）；
/// AllowCollapse 为 true 时点击卡片可折叠/展开正文（对应网页版 swap）。
/// </summary>
public class McCard : ContentControl
{
    public static readonly StyledProperty<string?> TitleProperty =
        AvaloniaProperty.Register<McCard, string?>(nameof(Title));

    public static readonly StyledProperty<bool> AllowCollapseProperty =
        AvaloniaProperty.Register<McCard, bool>(nameof(AllowCollapse));

    public static readonly StyledProperty<bool> IsExpandedProperty =
        AvaloniaProperty.Register<McCard, bool>(nameof(IsExpanded), true);

    static McCard()
    {
        IsExpandedProperty.Changed.AddClassHandler<McCard>((card, _) => card.UpdateExpandedClass());
    }

    public string? Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    /// <summary>是否允许点击标题折叠/展开（对应网页版 McCard 的 swap）。</summary>
    public bool AllowCollapse
    {
        get => GetValue(AllowCollapseProperty);
        set => SetValue(AllowCollapseProperty, value);
    }

    public bool IsExpanded
    {
        get => GetValue(IsExpandedProperty);
        set => SetValue(IsExpandedProperty, value);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        UpdateExpandedClass();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);

        if (AllowCollapse)
        {
            IsExpanded = !IsExpanded;
            e.Handled = true;
        }
    }

    private void UpdateExpandedClass() => PseudoClasses.Set(":expanded", IsExpanded);
}
