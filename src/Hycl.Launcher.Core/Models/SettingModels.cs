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

public sealed class SettingCardViewModel
{
    public SettingCardViewModel(string title, IReadOnlyList<SettingRowViewModel> rows)
    {
        Title = title;
        Rows = rows;
    }

    public string Title { get; }

    public IReadOnlyList<SettingRowViewModel> Rows { get; }

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
