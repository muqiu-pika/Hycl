using System.Text.Json;
using Hycl.Launcher.Core.Serialization;
using Hycl.Launcher.Core.Services;

namespace Hycl.Launcher.Core.Config;

/// <summary>
/// 配置读写：JSON 持久化，写入采用临时文件替换，避免半截文件。
/// 序列化走 <see cref="HyclJsonContext"/>（source-gen，裁剪安全），输出与历史 Web+缩进格式一致。
/// </summary>
public sealed class ConfigStore
{
    private readonly string _filePath;
    private readonly LauncherLogger _logger;
    private readonly SemaphoreSlim _saveLock = new(1, 1);

    public ConfigStore(string filePath, LauncherLogger logger)
    {
        _filePath = filePath;
        _logger = logger;
    }

    public LauncherConfig Config { get; private set; } = LauncherConfig.CreateDefault();

    public event EventHandler? Changed;

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_filePath))
        {
            Config = LauncherConfig.CreateDefault();
            await SaveAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        try
        {
            var json = await File.ReadAllTextAsync(_filePath, cancellationToken).ConfigureAwait(false);
            var loaded = JsonSerializer.Deserialize(json, HyclJsonContext.Default.LauncherConfig);
            Config = loaded ?? LauncherConfig.CreateDefault();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            _logger.Error("读取配置失败，已回退到默认配置", ex);
            Config = LauncherConfig.CreateDefault();
        }

        Config.Normalize();
    }

    public async Task SaveAsync(CancellationToken cancellationToken = default)
    {
        Config.Normalize();
        await _saveLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath) ?? ".");
            var tempFile = _filePath + ".tmp";
            var json = JsonSerializer.Serialize(Config, HyclJsonContext.Default.LauncherConfig);
            await File.WriteAllTextAsync(tempFile, json, cancellationToken).ConfigureAwait(false);
            File.Move(tempFile, _filePath, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.Error("保存配置失败", ex);
        }
        finally
        {
            _saveLock.Release();
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }
}
