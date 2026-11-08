using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Hycl.Launcher.UI.Styles;

/// <summary>
/// 深色主题画刷字典的代码后置：XAML 编译器生成类型化加载代码，
/// ThemeManager 直接 <c>new ColorsDark()</c> 换肤，避免运行时 URI 加载（裁剪不安全）。
/// </summary>
public partial class ColorsDark : ResourceDictionary
{
    public ColorsDark()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
