using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace Hycl.Launcher.UI.Controls;

/// <summary>
/// 下拉选择控件，对应网页版 McComboBox：半透明底、细蓝边、点击展开白色浮层。
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
    private ListBox? _list;
    private Popup? _popup;

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
        _list = e.NameScope.Find<ListBox>("PART_List");
        _popup = e.NameScope.Find<Popup>("PART_Popup");

        if (_popup is not null)
        {
            _popup.PlacementTarget = this;
            _popup.Placement = PlacementMode.BottomEdgeAlignedLeft;
            _popup.Closed += OnPopupClosed;
        }

        if (_trigger is not null)
        {
            _trigger.Click += OnTriggerClick;
        }

        if (_list is not null)
        {
            _list.ItemsSource = Items;
            _list.SelectedItem = SelectedItem;
            _list.SelectionChanged += OnListSelectionChanged;
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
        // 页面切走 / 控件脱离：取消尚未执行的展开并立即收起浮层，
        // 否则 Popup 的宿主顶层窗口会在脱离路径上被异步打开，出现残留浮层甚至卡死。
        _openPending = false;
        CloseDropDown();
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

    private void OnPopupClosed(object? sender, EventArgs e) => IsDropDownOpen = false;

    /// <summary>
    /// 把 <see cref="IsDropDownOpen"/> 的意图同步到 Popup，但不在此刻直接展开。
    /// 展开必须满足：模板已应用、控件已附加到视觉树且实际可见；
    /// 真正的 <c>Popup.IsOpen = true</c> 延后到下一次 UI 线程回调执行，
    /// 避开模板应用 / 布局过程（Avalonia 12 在此时机会在 Popup 宿主模板回调里抛空引用）。
    /// </summary>
    private void UpdateDropDown()
    {
        if (!IsDropDownOpen)
        {
            _openPending = false;
            CloseDropDown();
            return;
        }

        if (_openPending || (_popup?.IsOpen ?? false))
        {
            return;
        }

        if (!CanOpenDropDown())
        {
            // 模板未应用 / 控件不可见：不展开，并复位状态，避免“按钮显示已展开却没有浮层”
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
        if (!IsDropDownOpen)
        {
            return;
        }

        if (_popup is null || !CanOpenDropDown())
        {
            ResetOpenState();
            return;
        }

        try
        {
            _popup.MinWidth = Bounds.Width;
            _popup.Width = Bounds.Width;
            _popup.IsOpen = true;
        }
        catch (NullReferenceException)
        {
            // Avalonia 的 Popup 宿主窗口在极端时机会在 RootTemplateApplied 中解引用空对象；
            // 展开已延后到控件附加可见之后，这里仅作最后兜底：复位到“未展开”，不留假状态。
            ResetOpenState();
        }
        catch (InvalidOperationException)
        {
            ResetOpenState();
        }
    }

    /// <summary>展开前置条件：模板里的 Popup 存在、控件已连到 TopLevel 且实际可见。</summary>
    private bool CanOpenDropDown() =>
        _popup is not null && IsEffectivelyVisible && TopLevel.GetTopLevel(this) is not null;

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
        if (_popup is not null && _popup.IsOpen)
        {
            _popup.IsOpen = false;
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

        if (_list is not null)
        {
            _list.SelectionChanged -= OnListSelectionChanged;
        }

        if (_popup is not null)
        {
            _popup.Closed -= OnPopupClosed;
        }

        _trigger = null;
        _list = null;
        _popup = null;
    }
}
