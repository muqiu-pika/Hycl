using System.Runtime.InteropServices;

namespace Hycl.Launcher.Core.Platform;

/// <summary>
/// Avalonia 尚未初始化时（例如运行环境缺失）使用的原生提示框。
/// 只在 Windows 上有界面；其他平台退化为标准错误输出，避免影响跨平台构建。
/// </summary>
public static class NativeMessageBox
{
    private const uint MbOk = 0x00000000;
    private const uint MbYesNo = 0x00000004;
    private const uint MbIconError = 0x00000010;
    private const uint MbIconWarning = 0x00000030;
    private const uint MbSetForeground = 0x00010000;
    private const uint MbTopMost = 0x00040000;

    private const int IdYes = 6;

    /// <summary>显示「确定」提示框，返回是否成功显示。</summary>
    public static bool ShowInfo(string title, string message, bool isError = false)
    {
        var flags = MbOk | MbSetForeground | MbTopMost | (isError ? MbIconError : MbIconWarning);
        return Send(title, message, flags) != 0;
    }

    /// <summary>显示「是 / 否」提示框；只有选择「是」才返回 true。</summary>
    public static bool ShowYesNo(string title, string message, bool isError = false)
    {
        var flags = MbYesNo | MbSetForeground | MbTopMost | (isError ? MbIconError : MbIconWarning);
        return Send(title, message, flags) == IdYes;
    }

    private static int Send(string title, string message, uint flags)
    {
        if (PlatformInfo.IsWindows)
        {
            try
            {
                return MessageBoxW(nint.Zero, message, title, flags);
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                // user32 不可用时退化为控制台输出
            }
        }

        Console.Error.WriteLine($"[{title}] {message}");
        return 0;
    }

    [DllImport("user32.dll", EntryPoint = "MessageBoxW", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(nint hWnd, string text, string caption, uint type);
}
