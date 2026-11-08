using Hycl.Launcher.PluginContract;

namespace Hycl.Launcher.Plugin.Online;

/// <summary>Hycl 联机插件入口：陶瓦联机（Terracotta）。</summary>
public sealed class TerracottaPlugin : IHyclPlugin
{
    public const string PluginVersionValue = "1.0.0";

    public string Id => "hycl.online";

    public string DisplayName => "陶瓦联机";

    public string Version => PluginVersionValue;

    public int AbiVersion => 1;

    private IPluginContext? _context;

    private TerracottaService? _service;

    public Task InitializeAsync(IPluginContext context, CancellationToken cancellationToken = default)
    {
        _context = context;
        _service = new TerracottaService(context);
        context.Log.Info("陶瓦联机插件已初始化（工具 v" + _service.ToolVersionNumber + "）");
        return Task.CompletedTask;
    }

    public object CreateToolCard()
    {
        if (_context is null || _service is null)
        {
            throw new InvalidOperationException("插件尚未初始化");
        }

        return new Views.OnlineToolCard
        {
            DataContext = new OnlineToolCardViewModel(_context, _service),
        };
    }

    public async Task ShutdownAsync()
    {
        if (_service is not null)
        {
            await _service.StopAsync(silent: true).ConfigureAwait(false);
        }
    }
}
