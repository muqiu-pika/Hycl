using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;

namespace Hycl.Launcher.UI.Controls;

/// <summary>
/// 下拉选择控件，对应网页版 McComboBox：半透明底、细蓝边、点击展开白色浮层。
///
/// 浮层直接挂在当前窗口的 <see cref="OverlayLayer"/>（窗口内的 Canvas 覆盖层）上，
/// 不再使用 <see cref="Popup"/>：Popup 在 Avalonia 12 会创建独立的顶层宿主窗口
/// （PopupRoot : WindowBase），其首帧布局回调 Popup.RootTemplateApplied 会解引用
/// 已被关闭的 PopupOpenState，导致进程级空引用崩溃。覆盖层方案不创建任何顶层窗口。
/// </summary>
public class McComboBox : TemplatedControl
{
    public static readonly StyledProperty<IReadOnlyList<string>?> ItemsProperty =
        AvaloniaProperty.Register<McComboBox, IReadOnlyList<string>?>(nameof(Items));

    public static readonly StyledProperty<string?> SelectedItemProperty =
        AvaloniaProperty.Register<McComboBox, string?>(nameof(SelectedItem));

    public static readonly StyledProperty<bool> IsDropDownOpenProperty =
        AvaloniaProperty.Register<McComboBox, bool>(nameof(IsDropDownOpen));

    private Button? _trigger;
    private Border? _panel;
    private ListBox? _list;
    private Border? _dismissLayer;
    private OverlayLayer? _overlay;
    private bool _panelOpen;

    /// <summary>已安排一次延后展开，等待下一次 UI 线程回调执行（防止重复投递）。</summary>
    private bool _openPending;

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
        UpdateDropDown();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        // 页面切走 / 控件脱离：取消尚未执行的展开、收起浮层并复位状态，
        // 避免“控件显示已展开却没有浮层”的残留。
        _openPending = false;
        CloseDropDown();
        if (IsDropDownOpen)
        {
            IsDropDownOpen = false;
        }

        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == ItemsProperty && _list is not null)
        {
            _list.ItemsSource = Items;
            _list.SelectedItem = SelectedItem;
        }
        else if (change.Property == SelectedItemProperty && _list is not null &&
                 !string.Equals(_list.SelectedItem as string, SelectedItem, StringComparison.Ordinal))
        {
            _list.SelectedItem = SelectedItem;
        }
        else if (change.Property == IsDropDownOpenProperty)
        {
            UpdateDropDown();
        }
    }

    private void OnTriggerClick(object? sender, RoutedEventArgs e) => IsDropDownOpen = !IsDropDownOpen;

    private void OnListSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_list?.SelectedItem is string value)
        {
            SelectedItem = value;
        }

        IsDropDownOpen = false;
    }

    private void OnDismissPressed(object? sender, PointerPressedEventArgs e)
    {
        e.Handled = true;
        IsDropDownOpen = false;
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
        var panel = _panel!;

        panel.Width = Bounds.Width;
        panel.MinWidth = Bounds.Width;

        var anchor = _trigger ?? (Control)this;
        var topLeft = anchor.TranslatePoint(new Point(0, 0), overlay);
        var bottomLeft = anchor.TranslatePoint(new Point(0, anchor.Bounds.Height), overlay);
        var overlaySize = overlay.Bounds.Size;
        if (topLeft is null || bottomLeft is null || overlaySize.Width < 1 || overlaySize.Height < 1)
        {
            ResetOpenState();
            return;
        }

        // 先量一次，决定向下还是向上展开（靠近窗口底部时向上，和原来的 Popup 行为一致）
        panel.Measure(new Size(panel.Width, overlaySize.Height));
        var panelHeight = Math.Min(panel.DesiredSize.Height, overlaySize.Height);
        var top = bottomLeft.Value.Y;
        if (top + panelHeight > overlaySize.Height && topLeft.Value.Y - panelHeight >= 0)
        {
            top = topLeft.Value.Y - panelHeight;
        }

        var left = Math.Max(0, Math.Min(topLeft.Value.X, overlaySize.Width - panel.Width));
        Canvas.SetLeft(panel, left);
        Canvas.SetTop(panel, top);

        // 透明背景层：点空白处收起（相当于原来的 IsLightDismissEnabled）
        _dismissLayer = new Border
        {
            Background = Brushes.Transparent,
            Width = overlaySize.Width,
            Height = overlaySize.Height
        };
        _dismissLayer.PointerPressed += OnDismissPressed;
        Canvas.SetLeft(_dismissLayer, 0);
        Canvas.SetTop(_dismissLayer, 0);

        _overlay = overlay;
        overlay.Children.Add(_dismissLayer);
        overlay.Children.Add(panel);
        _panelOpen = true;
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
            _list!.ItemsSource = Items;
            _list.SelectedItem = SelectedItem;
            return;
        }

        _list = new ListBox
        {
            ItemsSource = Items,
            SelectedItem = SelectedItem
        };
        _list.Classes.Add("combo-list");
        _list.SelectionChanged += OnListSelectionChanged;

        _panel = new Border { Child = _list };
        _panel.Classes.Add("combo-pop");
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
        if (_overlay is not null)
        {
            if (_dismissLayer is not null)
            {
                _dismissLayer.PointerPressed -= OnDismissPressed;
                _overlay.Children.Remove(_dismissLayer);
            }

            if (_panel is not null)
            {
                _overlay.Children.Remove(_panel);
            }
        }

        _dismissLayer = null;
        _overlay = null;
        _panelOpen = false;
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
