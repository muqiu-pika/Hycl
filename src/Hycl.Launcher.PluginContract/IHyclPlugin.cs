namespace Hycl.Launcher.PluginContract;

/// <summary>
/// Hycl 插件契约（ABI v1）。插件程序集需提供本接口的公开实现，
/// 由启动器在 <c>plugins/&lt;id&gt;/plugin.json</c> 声明的入口程序集中反射实例化。
/// 契约层保持精简且只增不改：字段/方法签名一旦发布不再变更，扩展通过新增成员完成。
/// </summary>
public interface IHyclPlugin
{
    /// <summary>插件唯一标识（同时是 plugins/&lt;id&gt; 安装目录名），如 "hycl.online"。</summary>
    string Id { get; }

    /// <summary>显示名（中文）。</summary>
    string DisplayName { get; }

    /// <summary>插件版本（与 plugin.json 一致）。</summary>
    string Version { get; }

    /// <summary>实现的契约版本；与启动器支持值不一致时拒绝加载。</summary>
    int AbiVersion { get; }

    /// <summary>初始化：保存 context、准备数据目录、注册内部状态。UI 卡片在此时一并创建亦可。</summary>
    Task InitializeAsync(IPluginContext context, CancellationToken cancellationToken = default);

    /// <summary>
    /// 创建工具页卡片。返回值必须是 Avalonia <c>Control</c>（由启动器直接嵌入工具页；
    /// 契约层不引用 UI 框架，因此用 object 承载）。卡片内样式通过 DynamicResource 引用
    /// 启动器主题令牌（mc-btn 等 class 在主程序 App 级样式表中全局生效）。
    /// </summary>
    object CreateToolCard();

    /// <summary>启动器退出前回调：停止后台进程/任务。超时后启动器不再等待。</summary>
    Task ShutdownAsync();
}
