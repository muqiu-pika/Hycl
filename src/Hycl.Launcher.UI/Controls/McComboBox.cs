using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Hycl.Launcher.UI.Controls;

/// <summary>
/// 下拉选择控件，对应网页版 McComboBox / PCL-CE 的 MyComboBox：玻璃白底、细蓝边、
/// 展开时边框转主色且右侧箭头旋转 180°，浮层为带蓝调投影的白色面板。
///
/// 浮层直接挂在当前窗口的 <see cref="OverlayLayer"/> 上，不使用 <see cref="Popup"/>：
/// Popup 在 Avalonia 12 会创建独立的顶层宿主窗口（PopupRoot : WindowBase），
/// 其首帧布局回调会解引用已关闭的 PopupOpenState，导致进程级空引用崩溃。
///
/// 点击外部关闭由 TopLevel 隧道阶段接管且不标记 Handled：点击可继续落到被覆盖的
/// 控件（如标签栏），一次点击同时完成「收起 + 切页」，与 WPF 轻量关闭行为一致。
/// </summary>
public class McComboBox : TemplatedControl
{
    public static readonly StyledProperty<IReadOnlyList<string>?> ItemsProperty =
        AvaloniaProperty.Register<McComboBox, IReadOnlyList<string>?>(nameof(Items));

    public static readonly StyledProperty<string?> SelectedItemProperty =
        AvaloniaProperty.Register<McComboBox, string?>(nameof(SelectedItem));

    public static readonly StyledProperty<bool> IsDropDownOpenProperty =
        AvaloniaProperty.Register<McComboBox, bool>(nameof(IsDropDownOpen));

    /// <summary>全局同时只允许一个下拉展开；展开前收起上一个（覆盖延后展开的竞态）。</summary>
    private static McComboBox? _openCombo;

    private Button? _trigger;
    private ListBox? _list;
    private Border? _panel;
    private TopLevel? _topLevel;
    private OverlayLayer? _overlay;
    private bool _panelOpen;

    /// <summary>已安排一次延后展开，等待下一次 UI 线程回调执行（防止重复投递）。</summary>
    private bool _openPending;

    /// <summary>
    /// 程序性同步 <see cref="ListBox.SelectedItem"/> / <see cref="ListBox.ItemsSource"/> 时抑制
    /// 选中回调：否则版本列表刷新后（同一集合实例 Clear+重填，Items 属性不变，
    /// ListBox 选中被清空）再次展开时的一次同步会被当成用户点选，把 IsDropDownOpen
    /// 改回 false 而浮层照常挂载，造成「浮层悬空 + 触发器失灵」的卡死。
    /// </summary>
    private bool _suppressSelectionChanged;

    /// <summary>监听 IsVisible 的各级祖先（页面只做可见性切换、不脱离可视树）。</summary>
    private readonly List<Control> _watchedAncestors = new();

    public McComboBox()
    {
        // 样式选择器用 .mc-combo 代替 c:McComboBox（Avalonia 12 选择器解析 xmlns:Type:伪类有问题）；
        // 展开态用普通类 open 而非伪类 :open —— Avalonia 12 禁止 Classes.Add 以冒号开头的伪类
        Classes.Add("mc-combo");
    }

    public IReadOnlyList<string>? Items
    {
        get => GetValue(ItemsProperty);
        set => SetValue(ItemsProperty, value);
    }

    public string? SelectedItem
    {
        get => GetValue(SelectedItemProperty);
        set => SetValue(SelectedItemProperty, value);
    }

    public bool IsDropDownOpen
    {
        get => GetValue(IsDropDownOpenProperty);
        set => SetValue(IsDropDownOpenProperty, value);
    }

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        Detach();

        base.OnApplyTemplate(e);

        _trigger = e.NameScope.Find<Button>("PART_Trigger");
        if (_trigger is not null)
        {
            _trigger.Click += OnTriggerClick;
        }

        UpdateDropDown();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        // 控件此前未附加（例如在构造函数里就设了 IsDropDownOpen）时，在这里补一次展开
        WatchAncestors();
        UpdateDropDown();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        // 页面切走 / 控件脱离：取消尚未执行的展开、收起浮层并复位状态
        CloseForHide();
        UnwatchAncestors();
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == ItemsProperty && _list is not null)
        {
            SyncListSelection(syncItemsSource: true);
        }
        else if (change.Property == SelectedItemProperty && _list is not null &&
                 !string.Equals(_list.SelectedItem as string, SelectedItem, StringComparison.Ordinal))
        {
            SyncListSelection(syncItemsSource: false);
        }
        else if (change.Property == IsDropDownOpenProperty)
        {
            // 驱动展开态样式（触发器配色 + 箭头旋转），见 Controls.axaml 的 .mc-combo.open
            if ((bool)change.NewValue!)
            {
                Classes.Add("open");
            }
            else
            {
                Classes.Remove("open");
            }

            UpdateDropDown();
        }
    }

    private void OnTriggerClick(object? sender, RoutedEventArgs e) => IsDropDownOpen = !IsDropDownOpen;

    private void OnListSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelectionChanged)
        {
            return;
        }

        if (_list?.SelectedItem is string value)
        {
            SelectedItem = value;
        }

        IsDropDownOpen = false;
    }

    private void OnListPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        // 点击某个选项（包括当前已选中、不触发 SelectionChanged 的项）后收起，与 PCL-CE 一致；
        // 滚动条等非选项区域不收起
        if (!_panelOpen || e.Source is not Visual source || !IsWithinListBoxItem(source))
        {
            return;
        }

        CloseForHide();
    }

    /// <summary>TopLevel 隧道阶段的按下：面板/触发器以外的地方按下即收起，且不吞掉事件。</summary>
    private void OnTopLevelPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!_panelOpen)
        {
            return;
        }

        var source = e.Source as Visual;
        if (source is not null &&
            (IsDescendantOf(source, _panel) || IsDescendantOf(source, _trigger)))
        {
            // 面板内部交给列表自己处理（选择、滚动条）；触发器按下交给 Click 翻转
            return;
        }

        CloseForHide();
    }

    private void OnTopLevelKeyDown(object? sender, KeyEventArgs e)
    {
        if (!_panelOpen || e.Key != Key.Escape)
        {
            return;
        }

        CloseForHide();
        e.Handled = true;
    }

    private void OnTopLevelPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        // 窗口尺寸变化时重新定位，避免浮层悬空或与触发器错位
        if (e.Property == Visual.BoundsProperty && _panelOpen && _overlay is not null && IsDropDownOpen)
        {
            PositionDropDown(_overlay);
        }
    }

    private void OnAncestorPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        // 任一祖先被隐藏（切页、对话框收起）都立即收起浮层，
        // 避免“页面已切走、浮层还挂在窗口覆盖层上”的残留
        if (e.Property == Visual.IsVisibleProperty && e.NewValue is false)
        {
            CloseForHide();
        }
    }

    /// <summary>
    /// 把 <see cref="IsDropDownOpen"/> 的意图同步到覆盖层，但不在此刻直接挂载。
    /// 真正的挂载延后到下一次 UI 线程回调，避开模板应用 / 布局过程。
    /// </summary>
    private void UpdateDropDown()
    {
        if (!IsDropDownOpen)
        {
            _openPending = false;
            CloseDropDown();
            return;
        }

        if (_openPending || _panelOpen)
        {
            return;
        }

        if (!CanOpenDropDown())
        {
            ResetOpenState();
            return;
        }

        _openPending = true;
        Dispatcher.UIThread.Post(TryOpenDropDown, DispatcherPriority.Background);
    }

    private void TryOpenDropDown()
    {
        if (!_openPending)
        {
            return;
        }

        _openPending = false;

        // 延后期间状态可能已被外部改回 false，或被切页/隐藏，必须重新校验
        if (!IsDropDownOpen || !CanOpenDropDown())
        {
            ResetOpenState();
            return;
        }

        var overlay = OverlayLayer.GetOverlayLayer(this);
        if (overlay is null)
        {
            ResetOpenState();
            return;
        }

        EnsureDropDown();

        // 程序性同步期间可能被取消展开（例如绑定回写触发选中回调），此刻必须放弃挂载，
        // 否则浮层与控件状态脱钩：显示已展开、触发器却再也收不起来（卡死）
        if (!IsDropDownOpen || _panel is null)
        {
            CloseDropDown();
            return;
        }

        // 同一时刻只允许一个下拉展开（例如连续快速点开两个不同下拉框的竞态）
        if (_openCombo is not null && !ReferenceEquals(_openCombo, this))
        {
            _openCombo.CloseForHide();
        }

        _openCombo = this;
        _topLevel = TopLevel.GetTopLevel(this);

        // 先挂载再测量定位：分离状态的 ListBox 模板未应用、Measure 高度为 0，
        // 挂载后应用模板再量才能拿到真实高度（决定向下/向上展开）
        _overlay = overlay;
        overlay.Children.Add(_panel);
        _panelOpen = true;
        PositionDropDown(overlay);

        // 淡入：挂载后先置 0 再回 1，配合 .combo-pop 的透明度过渡产生渐显
        _panel.Opacity = 0;
        Dispatcher.UIThread.Post(FadeDropDownIn, DispatcherPriority.Loaded);

        if (_topLevel is not null)
        {
            _topLevel.AddHandler(InputElement.PointerPressedEvent, OnTopLevelPointerPressed, RoutingStrategies.Tunnel);
            _topLevel.AddHandler(InputElement.KeyDownEvent, OnTopLevelKeyDown, RoutingStrategies.Tunnel);
            _topLevel.PropertyChanged += OnTopLevelPropertyChanged;
        }
    }

    private void FadeDropDownIn()
    {
        if (_panelOpen && _panel is not null)
        {
            _panel.Opacity = 1;
        }
    }

    private void PositionDropDown(OverlayLayer overlay)
    {
        var panel = _panel!;
        panel.Width = Bounds.Width;
        panel.MinWidth = Bounds.Width;

        // 挂载后补应用列表模板并测量，拿到真实浮层高度
        _list?.ApplyTemplate();
        panel.InvalidateMeasure();

        var anchor = _trigger ?? (Control)this;
        var topLeft = anchor.TranslatePoint(new Point(0, 0), overlay);
        var bottomLeft = anchor.TranslatePoint(new Point(0, anchor.Bounds.Height), overlay);
        var overlaySize = overlay.Bounds.Size;
        if (topLeft is null || bottomLeft is null)
        {
            return;
        }

        panel.Measure(new Size(panel.Width, overlaySize.Height));
        var panelHeight = Math.Min(panel.DesiredSize.Height, overlaySize.Height);

        // 与网页版一致：浮层与触发器保持 4px 间距；靠近窗口底部时改为向上展开
        var top = bottomLeft.Value.Y + 4;
        if (top + panelHeight > overlaySize.Height && topLeft.Value.Y - panelHeight - 4 >= 0)
        {
            top = topLeft.Value.Y - panelHeight - 4;
        }

        var left = Math.Max(0, Math.Min(topLeft.Value.X, overlaySize.Width - panel.Width));
        Canvas.SetLeft(panel, left);
        Canvas.SetTop(panel, top);
    }

    /// <summary>展开前置条件：控件已连到 TopLevel、实际可见、已有宽度且窗口覆盖层可用。</summary>
    private bool CanOpenDropDown() =>
        IsEffectivelyVisible &&
        Bounds.Width > 0 &&
        TopLevel.GetTopLevel(this) is not null &&
        OverlayLayer.GetOverlayLayer(this) is not null;

    private void EnsureDropDown()
    {
        if (_panel is not null)
        {
            SyncListSelection(syncItemsSource: true);
            return;
        }

        _list = new ListBox
        {
            ItemsSource = Items,
            SelectedItem = SelectedItem
        };
        _list.Classes.Add("combo-list");
        _list.SelectionChanged += OnListSelectionChanged;
        _list.PointerReleased += OnListPointerReleased;

        _panel = new Border { Child = _list };
        _panel.Classes.Add("combo-pop");
    }

    /// <summary>把 Items / SelectedItem 程序性同步到内部列表，期间抑制选中回调。</summary>
    private void SyncListSelection(bool syncItemsSource)
    {
        if (_list is null)
        {
            return;
        }

        _suppressSelectionChanged = true;
        try
        {
            if (syncItemsSource)
            {
                _list.ItemsSource = Items;
            }

            _list.SelectedItem = SelectedItem;
        }
        finally
        {
            _suppressSelectionChanged = false;
        }
    }

    private bool IsWithinListBoxItem(Visual node)
    {
        foreach (var current in node.GetVisualAncestors())
        {
            if (ReferenceEquals(current, _list))
            {
                return false;
            }

            if (current is ListBoxItem)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsDescendantOf(Visual? node, Visual? ancestor)
    {
        if (node is null || ancestor is null)
        {
            return false;
        }

        if (ReferenceEquals(node, ancestor))
        {
            return true;
        }

        foreach (var current in node.GetVisualAncestors())
        {
            if (ReferenceEquals(current, ancestor))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>监听各级祖先的 IsVisible：页面用可见性切换而非脱离可视树（见 MainWindow）。</summary>
    private void WatchAncestors()
    {
        UnwatchAncestors();

        foreach (var ancestor in this.GetVisualAncestors())
        {
            if (ancestor is Control control)
            {
                control.PropertyChanged += OnAncestorPropertyChanged;
                _watchedAncestors.Add(control);
            }
        }
    }

    private void UnwatchAncestors()
    {
        foreach (var ancestor in _watchedAncestors)
        {
            ancestor.PropertyChanged -= OnAncestorPropertyChanged;
        }

        _watchedAncestors.Clear();
    }

    /// <summary>收起并复位全部展开状态（切页/隐藏/点击外部/失去宿主共用）。</summary>
    private void CloseForHide()
    {
        _openPending = false;
        CloseDropDown();
        if (IsDropDownOpen)
        {
            IsDropDownOpen = false;
        }
    }

    private void ResetOpenState()
    {
        _openPending = false;
        if (IsDropDownOpen)
        {
            IsDropDownOpen = false;
        }
    }

    private void CloseDropDown()
    {
        if (_topLevel is not null)
        {
            _topLevel.RemoveHandler(InputElement.PointerPressedEvent, OnTopLevelPointerPressed);
            _topLevel.RemoveHandler(InputElement.KeyDownEvent, OnTopLevelKeyDown);
            _topLevel.PropertyChanged -= OnTopLevelPropertyChanged;
        }

        _topLevel = null;

        if (_overlay is not null && _panel is not null)
        {
            _overlay.Children.Remove(_panel);
        }

        _overlay = null;
        _panelOpen = false;
        if (_panel is not null)
        {
            _panel.Opacity = 1;
        }

        if (ReferenceEquals(_openCombo, this))
        {
            _openCombo = null;
        }
    }

    private void Detach()
    {
        _openPending = false;
        CloseDropDown();

        if (_trigger is not null)
        {
            _trigger.Click -= OnTriggerClick;
        }

        _trigger = null;
    }
}
