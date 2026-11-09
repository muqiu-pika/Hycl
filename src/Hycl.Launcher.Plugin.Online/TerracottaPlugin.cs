using Hycl.Launcher.PluginContract;

namespace Hycl.Launcher.Plugin.Online;

/// <summary>Hycl 联机插件入口：陶瓦联机（Terracotta）。版本号以 csproj 的 Version 为单一事实源。</summary>
public sealed class TerracottaPlugin : IHyclPlugin, IHyclPluginFeaturePage
{
    public string Id => "hycl.online";

    public string DisplayName => "陶瓦联机";

    public string Version => GetType().Assembly.GetName().Version!.ToString(3);

    public int AbiVersion => 1;

    private IPluginContext? _context;

    private TerracottaService? _service;

    public Task InitializeAsync(IPluginContext context, CancellationToken cancellationToken = default)
    {
        _context = context;
        _service = new TerracottaService(context);
        context.Log.Info("陶瓦联机插件已初始化（工具 v" + _service.ToolVersionNumber + "）");

        // 装好插件就把联机工具准备好：缺失 → 后台自动下载；损坏 → 只提示「补齐文件后重试」。
        _ = _service.AutoPrepareAsync();
        return Task.CompletedTask;
    }

    /// <summary>工具页「点进去」后的功能/设置页（建房、加入、日志 + 插件设置）。</summary>
    public string FeaturePageTitle => "陶瓦联机";

    public object CreateFeaturePage()
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

    /// <summary>ABI v1 原有方法：旧版宿主（或回退路径）调用时返回同一个功能页。</summary>
    public object CreateToolCard() => CreateFeaturePage();

    public async Task ShutdownAsync()
    {
        if (_service is not null)
        {
            await _service.StopAsync(silent: true).ConfigureAwait(false);
        }
    }
}
