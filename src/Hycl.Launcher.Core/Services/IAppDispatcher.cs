namespace Hycl.Launcher.Core.Services;

/// <summary>
/// 把后台线程的更新切回 UI 线程的抽象（Core 不引用 Avalonia）。
/// </summary>
public interface IAppDispatcher
{
    void Post(Action action);
}

/// <summary>同步执行版本（控制台/单元测试用）。</summary>
public sealed class ImmediateDispatcher : IAppDispatcher
{
    public void Post(Action action) => action();
}
