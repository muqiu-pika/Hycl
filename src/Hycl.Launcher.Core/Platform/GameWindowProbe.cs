using System.Runtime.InteropServices;
using System.Text;

namespace Hycl.Launcher.Core.Platform;

/// <summary>窗口探测结果类型。</summary>
public enum GameWindowKind
{
    None,
    LoaderWindow,
    MainWindow
}

/// <summary>找到的窗口信息（句柄与标题仅用于日志，UI 层不直接使用）。</summary>
public readonly record struct GameWindowInfo(GameWindowKind Kind, nint Handle, string Title)
{
    public bool Found => Kind != GameWindowKind.None;
}

/// <summary>
/// Windows 下按 PCL-CE 的方式判定游戏窗口：枚举顶层窗口，窗口类必须是
/// GLFW30 / LWJGL / SunAwtFrame，且窗口所属进程与游戏进程一致；
/// FML / Quilt Loader 的加载器窗口与真正的游戏主窗口分开返回。
/// macOS / Linux 不做窗口枚举，由上层回退到日志特征行判定。
/// </summary>
public static class GameWindowProbe
{
    // 旧版 Minecraft（LWJGL/GLFW）与 26.x 新版（SDL）窗口类名
    private static readonly string[] GameWindowClasses = ["GLFW30", "LWJGL", "SunAwtFrame", "SDL_app", "SDL2_app"];

    private const uint WmClose = 0x0010;

    public static GameWindowInfo Probe(int processId)
    {
        if (!OperatingSystem.IsWindows() || processId <= 0)
        {
            return default;
        }

        var processIds = GetProcessTreeIds(processId);
        var result = default(GameWindowInfo);
        _ = EnumWindows((handle, _) =>
        {
            if (result.Kind == GameWindowKind.MainWindow)
            {
                return false;
            }

            var info = Inspect(handle, processIds);
            if (!info.Found)
            {
                return true;
            }

            if (info.Kind == GameWindowKind.MainWindow)
            {
                result = info;
                return false;
            }

            // 先记录加载器窗口，继续找真正的主窗口
            result = info;
            return true;
        }, nint.Zero);

        return result;
    }

    private static GameWindowInfo Inspect(nint handle, HashSet<int> processIds)
    {
        if (!IsWindowVisible(handle))
        {
            return default;
        }

        var classBuffer = new StringBuilder(256);
        _ = GetClassName(handle, classBuffer, classBuffer.Capacity);
        var className = classBuffer.ToString();

        var owner = 0;
        _ = GetWindowThreadProcessId(handle, ref owner);
        if (!processIds.Contains(owner))
        {
            return default;
        }

        var titleBuffer = new StringBuilder(512);
        _ = GetWindowText(handle, titleBuffer, titleBuffer.Capacity);
        var title = titleBuffer.ToString();
        if (title == "PopupMessageWindow"
            || title.StartsWith("GLFW", StringComparison.OrdinalIgnoreCase))
        {
            return default;
        }

        // 已知窗口类，或标题明确包含 Minecraft（兜底覆盖未来可能更换的窗口框架）
        if (Array.IndexOf(GameWindowClasses, className) < 0
            && !title.Contains("Minecraft", StringComparison.OrdinalIgnoreCase))
        {
            return default;
        }

        if (title.StartsWith("FML", StringComparison.OrdinalIgnoreCase)
            || title.StartsWith("Quilt Loader", StringComparison.OrdinalIgnoreCase))
        {
            return new GameWindowInfo(GameWindowKind.LoaderWindow, handle, title);
        }

        return new GameWindowInfo(GameWindowKind.MainWindow, handle, title);
    }

    /// <summary>向已探测到的游戏窗口发送 WM_CLOSE，触发游戏自身的正常退出流程。</summary>
    public static bool RequestClose(nint handle)
    {
        if (!OperatingSystem.IsWindows() || handle == 0)
        {
            return false;
        }

        return PostMessage(handle, WmClose, nint.Zero, nint.Zero);
    }

    /// <summary>
    /// 收集进程及其子进程：部分 Java 路径（如 Oracle javapath）会先启动一个引导进程，
    /// 真正的 JVM 与游戏窗口属于其子进程，只看启动 PID 会漏检。
    /// </summary>
    private static HashSet<int> GetProcessTreeIds(int rootProcessId)
    {
        var childrenByParent = new Dictionary<int, List<int>>();
        var snapshot = CreateToolhelp32Snapshot(Th32CsSnapProcess, 0);
        if (snapshot != 0)
        {
            try
            {
                var entry = new ProcessEntry32 { dwSize = (uint)Marshal.SizeOf<ProcessEntry32>() };
                if (Process32First(snapshot, ref entry))
                {
                    do
                    {
                        if (!childrenByParent.TryGetValue((int)entry.th32ParentProcessID, out var children))
                        {
                            children = [];
                            childrenByParent[(int)entry.th32ParentProcessID] = children;
                        }

                        children.Add((int)entry.th32ProcessID);
                    }
                    while (Process32Next(snapshot, ref entry));
                }
            }
            finally
            {
                _ = CloseHandle(snapshot);
            }
        }

        var result = new HashSet<int> { rootProcessId };
        var queue = new Queue<int>();
        queue.Enqueue(rootProcessId);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (!childrenByParent.TryGetValue(current, out var children))
            {
                continue;
            }

            foreach (var child in children)
            {
                if (result.Add(child))
                {
                    queue.Enqueue(child);
                }
            }
        }

        return result;
    }

    private delegate bool EnumWindowsProc(nint hWnd, nint lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, nint lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetClassNameW")]
    private static extern int GetClassName(nint hWnd, StringBuilder className, int maxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetWindowTextW")]
    private static extern int GetWindowText(nint hWnd, StringBuilder text, int maxCount);

    [DllImport("user32.dll")]
    private static extern int GetWindowThreadProcessId(nint hWnd, ref int processId);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(nint hWnd);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(nint hWnd, uint message, nint wParam, nint lParam);

    private const uint Th32CsSnapProcess = 0x00000002;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public nint th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    [DllImport("kernel32.dll")]
    private static extern nint CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "Process32FirstW")]
    private static extern bool Process32First(nint snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "Process32NextW")]
    private static extern bool Process32Next(nint snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(nint handle);
}
