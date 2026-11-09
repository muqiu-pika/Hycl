namespace Hycl.Launcher.PluginContract;

/// <summary>
/// 可选扩展（ABI v1 追加，旧插件不受影响）：支持热禁用/热启用的插件实现。
/// 插件程序集在默认 AssemblyLoadContext 中无法卸载，宿主的热禁用因此不卸载实例，
/// 而是调用 <see cref="SuspendAsync"/> 停止后台进程/任务并隐藏入口；重新启用时调用
/// <see cref="ResumeAsync"/> 恢复。未实现本接口的插件退化为只切换工具页入口。
/// </summary>
public interface IHyclPluginHotToggle
{
    /// <summary>插件被禁用时调用：停止后台进程/任务（如联机会话、后台下载）。实例保持已初始化。</summary>
    Task SuspendAsync();

    /// <summary>插件重新启用时调用：恢复后台任务（如工具自检与自动准备）。</summary>
    Task ResumeAsync();
}
