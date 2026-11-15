namespace Hycl.Launcher.Core.ViewModels;

/// <summary>
/// 页面内部的「上一步」能力：全局 Esc 返回时优先消费当前页的内部层级
/// （如下载页的社区资源分区 / 加载器分区回到安装根视图、工具页的插件功能页回到工具首页）。
/// </summary>
public interface IBackNavigable
{
    /// <summary>当前是否存在可返回的内部层级。</summary>
    bool CanGoBack { get; }

    /// <summary>返回上一层内部视图。</summary>
    void GoBack();
}
