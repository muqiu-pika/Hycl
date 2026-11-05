namespace Hycl.Launcher.Core.ViewModels.Pages;

/// <summary>
/// 独立「启动中」页面的数据上下文：持有点击启动的那个 <see cref="LaunchPageViewModel"/>，
/// 页面直接绑定 <c>Launch.Xxx</c> 属性。单独成页只是为了让页面切换沿用现有串行淡入淡出，
/// 同时不必把启动页自身的绑定拆开重写。
/// </summary>
public sealed class LaunchProgressViewModel
{
    public LaunchProgressViewModel(LaunchPageViewModel launch)
    {
        Launch = launch;
    }

    public LaunchPageViewModel Launch { get; }
}
