using System.Text.Json;
using Hycl.Launcher.Core.Platform;

namespace Hycl.Launcher.Core.Services;

/// <summary>
/// 启动器主窗口尺寸记忆：保存在数据目录的 window-state.json，
/// 与 config.json 分离，便于窗口 Show 之前同步读取。
/// </summary>
public sealed record LauncherWindowState(double Width, double Height, bool Maximized)
{
    private static string FilePath =>
        Path.Combine(PlatformInfo.LauncherDataDirectory, "window-state.json");

    /// <summary>读取上次关闭时的窗口状态；文件缺失/损坏时返回 null（使用默认尺寸）。</summary>
    public static LauncherWindowState? Load()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                return null;
            }

            using var stream = File.OpenRead(FilePath);
            using var document = JsonDocument.Parse(stream);
            var root = document.RootElement;
            var width = root.TryGetProperty("width", out var widthElement) &&
                        widthElement.TryGetDouble(out var parsedWidth)
                ? parsedWidth
                : 0;
            var height = root.TryGetProperty("height", out var heightElement) &&
                         heightElement.TryGetDouble(out var parsedHeight)
                ? parsedHeight
                : 0;
            var maximized = root.TryGetProperty("maximized", out var maximizedElement) &&
                            maximizedElement.ValueKind == JsonValueKind.True;
            if (width < 1 || height < 1)
            {
                return null;
            }

            return new LauncherWindowState(width, height, maximized);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>写入窗口状态；失败时静默忽略，不影响关闭流程。</summary>
    public static void Save(double width, double height, bool maximized)
    {
        try
        {
            Directory.CreateDirectory(PlatformInfo.LauncherDataDirectory);
            using var stream = File.Create(FilePath);
            using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
            writer.WriteStartObject();
            writer.WriteNumber("width", Math.Round(Math.Max(width, 0)));
            writer.WriteNumber("height", Math.Round(Math.Max(height, 0)));
            writer.WriteBoolean("maximized", maximized);
            writer.WriteEndObject();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 磁盘不可写时保持默认行为
        }
    }
}
