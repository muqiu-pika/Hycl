using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Hycl.Launcher.Core.Models;

/// <summary>
/// 设置行基类。具体类型（Combo / Toggle / Text / Info / Swatches / TextLine / Log / Buttons）
/// 由 UI 层的 DataTemplate 按类型渲染，对应网页版 SettingsView 的通用行渲染器。
/// </summary>
public abstract class SettingRowViewModel : ObservableObject
{
    protected SettingRowViewModel(string label) => Label = label;

    public string Label { get; }

    /// <summary>恢复该行的默认值。</summary>
    public virtual void Reset()
    {
    }
}

public partial class ComboRowViewModel : SettingRowViewModel
{
    private readonly string _defaultOption;

    public ComboRowViewModel(string label, IReadOnlyList<string> options, string defaultOption) : base(label)
    {
        Options = options;
        _defaultOption = defaultOption;
        _selectedOption = defaultOption;
    }

    public IReadOnlyList<string> Options { get; }

    [ObservableProperty]
    private string? _selectedOption;

    public override void Reset() => SelectedOption = _defaultOption;
}

public partial class ToggleRowViewModel : SettingRowViewModel
{
    private readonly bool _defaultValue;

    public ToggleRowViewModel(string label, bool defaultValue) : base(label)
    {
        _defaultValue = defaultValue;
        _isOn = defaultValue;
    }

    [ObservableProperty]
    private bool _isOn;

    public override void Reset() => IsOn = _defaultValue;
}

public partial class SliderRowViewModel : SettingRowViewModel
{
    private readonly double _defaultValue;

    public SliderRowViewModel(
        string label,
        double minimum,
        double maximum,
        double step,
        double defaultValue,
        string unit = "",
        bool asPercent = false) : base(label)
    {
        Minimum = minimum;
        Maximum = maximum;
        Step = step;
        Unit = unit;
        IsPercent = asPercent;
        _defaultValue = defaultValue;
        _value = defaultValue;
        _valueText = Format(defaultValue);
    }

    public double Minimum { get; }

    public double Maximum { get; }

    public double Step { get; }

    /// <summary>非百分比显示的数值单位（如 °）。</summary>
    public string Unit { get; }

    /// <summary>按百分比显示：值为 0-1 时显示 0%-100%。</summary>
    public bool IsPercent { get; }

    [ObservableProperty]
    private double _value;

    [ObservableProperty]
    private string _valueText;

    partial void OnValueChanged(double value) => ValueText = Format(value);

    private string Format(double value) =>
        IsPercent ? $"{Math.Round(value * 100)}%" : $"{Math.Round(value)}{Unit}";

    public override void Reset() => Value = _defaultValue;
}

public partial class TextRowViewModel : SettingRowViewModel
{
    private readonly string _defaultText;

    public TextRowViewModel(string label, string placeholder, string defaultText = "") : base(label)
    {
        Placeholder = placeholder;
        _defaultText = defaultText;
        _text = defaultText;
    }

    public string Placeholder { get; }

    [ObservableProperty]
    private string _text;

    public override void Reset() => Text = _defaultText;
}

public sealed class InfoRowViewModel : SettingRowViewModel
{
    public InfoRowViewModel(string label, string value) : base(label) => Value = value;

    public string Value { get; }
}

/// <summary>值可动态更新的信息行（Java 检测结果等异步内容）。</summary>
public partial class MutableInfoRowViewModel : SettingRowViewModel
{
    public MutableInfoRowViewModel(string label, string value) : base(label) => _value = value;

    [ObservableProperty]
    private string _value;
}

public partial class ThemeSwatchViewModel : ObservableObject
{
    public ThemeSwatchViewModel(string id, string colorHex)
    {
        Id = id;
        ColorHex = colorHex;
    }

    public string Id { get; }

    /// <summary>十六进制颜色，由 UI 层转换为画刷。</summary>
    public string ColorHex { get; }

    [ObservableProperty]
    private bool _isSelected;
}

public partial class SwatchesRowViewModel : SettingRowViewModel
{
    private readonly string _defaultId;

    public SwatchesRowViewModel(string label, string defaultId, params (string Id, string Color)[] swatches) : base(label)
    {
        _defaultId = defaultId;
        Swatches = swatches.Select(s => new ThemeSwatchViewModel(s.Id, s.Color)).ToArray();
        Select(Swatches.FirstOrDefault(s => s.Id == defaultId));
    }

    public IReadOnlyList<ThemeSwatchViewModel> Swatches { get; }

    [ObservableProperty]
    private ThemeSwatchViewModel? _selectedSwatch;

    [RelayCommand]
    private void Select(ThemeSwatchViewModel? swatch)
    {
        if (swatch is null)
        {
            return;
        }

        foreach (var item in Swatches)
        {
            item.IsSelected = ReferenceEquals(item, swatch);
        }

        SelectedSwatch = swatch;
    }

    public override void Reset() => Select(Swatches.FirstOrDefault(s => s.Id == _defaultId));
}

public sealed class TextLineRowViewModel : SettingRowViewModel
{
    public TextLineRowViewModel(string text) : base(string.Empty) => Text = text;

    public string Text { get; }
}

public sealed class LogRowViewModel : SettingRowViewModel
{
    public LogRowViewModel(string text) : base(string.Empty) => Text = text;

    public string Text { get; }
}

/// <summary>内容可动态刷新的日志行（设置页日志尾部随操作刷新）。</summary>
public partial class MutableLogRowViewModel : SettingRowViewModel
{
    public MutableLogRowViewModel(string text) : base(string.Empty) => _text = text;

    [ObservableProperty]
    private string _text;

    /// <summary>刷新日志尾部内容。</summary>
    public void Reload(Func<string> provider) => Text = provider();
}

public sealed class SettingButtonViewModel
{
    public SettingButtonViewModel(string label, string iconKey, bool isPrimary, Action? onClick = null)
    {
        Label = label;
        IconKey = iconKey;
        IsPrimary = isPrimary;
        Command = new RelayCommand(() => onClick?.Invoke());
    }

    public string Label { get; }

    public string IconKey { get; }

    public bool IsPrimary { get; }

    public IRelayCommand Command { get; }
}

public sealed class ButtonsRowViewModel : SettingRowViewModel
{
    public ButtonsRowViewModel(IReadOnlyList<SettingButtonViewModel> buttons) : base(string.Empty) => Buttons = buttons;

    public IReadOnlyList<SettingButtonViewModel> Buttons { get; }
}

public partial class SettingCardViewModel : ObservableObject
{
    public SettingCardViewModel(
        string title,
        IReadOnlyList<SettingRowViewModel> rows,
        bool collapsible = false,
        bool expanded = true)
    {
        Title = title;
        Rows = rows;
        IsCollapsible = collapsible;
        _isExpanded = expanded;
    }

    public string Title { get; }

    public IReadOnlyList<SettingRowViewModel> Rows { get; }

    /// <summary>是否显示折叠箭头（游戏内设置分类默认折叠）。</summary>
    public bool IsCollapsible { get; }

    [ObservableProperty]
    private bool _isExpanded;

    [RelayCommand]
    private void ToggleExpanded() => IsExpanded = !IsExpanded;

    public void Reset()
    {
        foreach (var row in Rows)
        {
            row.Reset();
        }
    }
}

public sealed class SettingsSectionViewModel
{
    public SettingsSectionViewModel(string title, IReadOnlyList<SettingCardViewModel> cards)
    {
        Title = title;
        Cards = cards;
    }

    public string Title { get; }

    public IReadOnlyList<SettingCardViewModel> Cards { get; }

    public void Reset()
    {
        foreach (var card in Cards)
        {
            card.Reset();
        }
    }
}
