using System.Runtime.InteropServices;

namespace Hycl.Launcher.PluginContract;

/// <summary>跨平台工具资产键约定（win/linux/darwin × x64/arm64）。</summary>
public static class HyclOnlineTools
{
    /// <summary>
    /// 当前平台对应的资产键（如 win-x64 / darwin-arm64）；
    /// 不在支持范围（Windows/Linux/macOS 的 x64 与 arm64）时返回空字符串。
    /// </summary>
    public static string ToolAssetKey
    {
        get
        {
            var arch = RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.X64 => "x64",
                Architecture.Arm64 => "arm64",
                _ => "",
            };
            if (arch.Length == 0)
            {
                return "";
            }

            if (OperatingSystem.IsWindows())
            {
                return $"win-{arch}";
            }

            if (OperatingSystem.IsMacOS())
            {
                return $"darwin-{arch}";
            }

            return OperatingSystem.IsLinux() ? $"linux-{arch}" : "";
        }
    }
}
