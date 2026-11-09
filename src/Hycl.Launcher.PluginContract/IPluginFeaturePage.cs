namespace Hycl.Launcher.PluginContract;

/// <summary>
/// ABI v1 的可选扩展：插件声明自己在工具页「点进去」后的功能/设置页面。
/// 宿主用 <c>plugin is IHyclPluginFeaturePage</c> 探测；未实现的旧插件回退到
/// <see cref="IHyclPlugin.CreateToolCard"/>，因此无需提升 ABI，已发布插件包仍可加载。
/// </summary>
public interface IHyclPluginFeaturePage
{
    /// <summary>功能页标题（工具页入口卡片与返回栏显示）。</summary>
    string FeaturePageTitle { get; }

    /// <summary>
    /// 创建插件功能页控件（Avalonia Control）。每次调用必须返回新实例，
    /// 避免同一控件被多处挂载。
    /// </summary>
    object CreateFeaturePage();
}
