using System.Runtime.Versioning;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;

namespace Hycl.Launcher.UI;

/// <summary>
/// 单实例门卫：重复点击 exe 时不弹第二个窗口，而是把已运行实例的窗口唤起到最前。
/// 命名互斥体判唯一性，命名事件做「唤起窗口」信号（仅 Windows；Unix 命名内核同步对象
/// 不受支持，退化为允许多开，不影响使用）。
/// </summary>
internal static class SingleInstance
{
    private const string MutexName = @"Local\Hycl.Launcher.SingleInstance";
    private const string ShowEventName = @"Local\Hycl.Launcher.SingleInstance.Show";

    private static Mutex? _mutex;
    private static EventWaitHandle? _showEvent;

    /// <summary>
    /// 尝试成为唯一实例。返回 false 表示已有实例在运行（已向其发送唤起信号），调用方应直接退出。
    /// </summary>
    public static bool TryAcquire()
    {
        if (!OperatingSystem.IsWindows())
        {
            return true;
        }

        try
        {
            var mutex = new Mutex(initiallyOwned: false, MutexName);
            var acquired = false;
            try
            {
                acquired = mutex.WaitOne(TimeSpan.Zero);
            }
            catch (AbandonedMutexException)
            {
                // 上一个实例异常退出未释放：所有权已随异常转移给本进程，按主实例继续
                acquired = true;
            }

            if (!acquired)
            {
                mutex.Dispose();
                SignalExisting();
                return false;
            }

            _mutex = mutex;
            _showEvent = TryOpenShowEvent() ?? new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
            return true;
        }
        catch (Exception)
        {
            // 命名对象不可用（权限受限等）：退化为允许多开，不能因此挡住启动
            return true;
        }
    }

    /// <summary>主实例监听唤起信号；收到后把主窗口呼起到最前（跨线程安全，窗口未就绪时忽略）。</summary>
    public static void StartListening(Func<Window?> getWindow)
    {
        if (_showEvent is null)
        {
            return;
        }

        var thread = new Thread(() =>
        {
            while (_showEvent.WaitOne())
            {
                Dispatcher.UIThread.Post(() => BringToFront(getWindow()));
            }
        })
        {
            IsBackground = true,
            Name = "HyclSingleInstance",
        };
        thread.Start();
    }

    [SupportedOSPlatform("windows")]
    private static void SignalExisting()
    {
        try
        {
            using var evt = TryOpenShowEvent();
            evt?.Set();
        }
        catch (Exception)
        {
            // 信号发不出去也无妨：用户仍可从任务栏手动切回
        }
    }

    [SupportedOSPlatform("windows")]
    private static EventWaitHandle? TryOpenShowEvent()
    {
        return EventWaitHandle.TryOpenExisting(ShowEventName, out var evt) ? evt : null;
    }

    private static void BringToFront(Window? window)
    {
        if (window is null)
        {
            // 首实例仍在启动动画/服务加载阶段：窗口稍后才创建，本轮忽略
            return;
        }

        if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = WindowState.Normal;
        }

        // Windows 前台锁会拦住普通 Activate：Topmost 一开一关确保窗口提到最前
        window.Topmost = true;
        window.Activate();
        window.Topmost = false;
    }
}
