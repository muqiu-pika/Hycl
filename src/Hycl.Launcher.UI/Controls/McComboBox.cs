using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;

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
            _popup.IsOpen = IsDropDownOpen;
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
        else if (change.Property == IsDropDownOpenProperty && _popup is not null)
        {
            if (IsDropDownOpen)
            {
                _popup.MinWidth = Bounds.Width;
                _popup.Width = Bounds.Width;
            }

            _popup.IsOpen = IsDropDownOpen;
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

    private void Detach()
    {
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
