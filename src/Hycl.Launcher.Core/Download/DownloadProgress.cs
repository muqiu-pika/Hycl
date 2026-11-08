namespace Hycl.Launcher.Core.Download;

/// <summary>单文件下载进度。</summary>
public sealed record DownloadByteProgress(long ReceivedBytes, long TotalBytes)
{
    public double Percentage => TotalBytes <= 0 ? 0 : Math.Clamp((double)ReceivedBytes / TotalBytes, 0, 1);
}

/// <summary>
/// 安装任务进度快照。
/// 对应 PCL-CE 的两级模型：<see cref="Stage"/> 是当前步骤（等价于一个 LoaderDownload 子任务），
/// 步骤内按文件数推进（<see cref="FilesDone"/> / <see cref="FilesTotal"/>），
/// 并用字节进度细化“当前正在下载的文件”；总进度由 <see cref="InstallProgressTracker"/> 合成。
/// </summary>
public sealed record InstallProgress(
    string Stage,
    string CurrentItem,
    int FilesDone,
    int FilesTotal,
    double BytesDone,
    double BytesTotal,
    int StepIndex = 0,
    int StepCount = 0,
    double OverallPercent = 0)
{
    /// <summary>
    /// 本步进度（0~1）。文件数可用时取“已完成文件数 / 总文件数”；
    /// 文件数未知或为 0 时退回字节比例；两者都不可用返回 0，绝不产生除零 / NaN。
    /// </summary>
    public double Percentage
    {
        get
        {
            var byFiles = FilesTotal > 0
                ? Math.Clamp((double)FilesDone / FilesTotal, 0, 1)
                : 0d;
            var byBytes = BytesTotal > 0
                ? Math.Clamp(BytesDone / BytesTotal, 0, 1)
                : 0d;
            return Math.Max(byFiles, byBytes);
        }
    }

    /// <summary>当前步骤内还剩几个文件（文件数未知时为 0）。</summary>
    public int RemainingFiles => FilesTotal > 0 ? Math.Max(0, FilesTotal - FilesDone) : 0;

    /// <summary>整条任务还剩几个步骤（含当前步骤；未知时为 0）。</summary>
    public int RemainingSteps => StepCount > 0 ? Math.Max(0, StepCount - StepIndex) : 0;
}

/// <summary>
/// 步骤进度合成器，对应 PCL-CE 的 <c>LoaderCombo.GetWeightedProgress</c>：
/// <c>Σ(ProgressWeight × Progress) / Σ(ProgressWeight)</c>。
/// PCL-CE 的 <c>ProgressWeight</c> 默认为 1，因此这里等价于
/// <c>(已完成步骤数 + 当前步骤进度) / 总步骤数</c>；步骤内进度等价于
/// <c>LoaderDownload.Progress = files.Average(file =&gt; file.Progress)</c>，
/// Hycl 用“已完成文件数 / 总文件数 + 字节比例兜底”表达同一含义。
/// 输出单调不减：重试同一文件、切换下载源、步骤切换都不会让百分比回退。
/// </summary>
public sealed class InstallProgressTracker
{
    private readonly object _lock = new();
    private readonly int _expectedSteps;
    private string _stage;
    private int _stepIndex = 1;
    private int _completedSteps;
    private double _stepFraction;
    private double _overallPercent;

    /// <param name="expectedSteps">调用方预估的总步骤数（至少 1）。</param>
    /// <param name="initialStage">任务开始时的占位步骤名，需与界面上的初始步骤一致。</param>
    public InstallProgressTracker(int expectedSteps, string initialStage = "准备中")
    {
        _expectedSteps = Math.Max(1, expectedSteps);
        _stage = initialStage;
    }

    /// <summary>把一条步骤内进度合成为带步骤序号与总进度的结果；总进度只增不减。</summary>
    public InstallProgress Update(InstallProgress progress)
    {
        lock (_lock)
        {
            if (!string.IsNullOrEmpty(progress.Stage)
                && !string.Equals(progress.Stage, _stage, StringComparison.Ordinal))
            {
                // 阶段切换：上一阶段视为已完成（对应 PCL-CE 的 LoaderBase.Progress 在 Finished 时返回 1）
                _stage = progress.Stage;
                _stepIndex++;
                _completedSteps = _stepIndex - 1;
                _stepFraction = 0;
            }

            // 同一阶段内也只增不减：重试 / 换源导致的回退被吸收
            _stepFraction = Math.Max(_stepFraction, progress.Percentage);
            var totalSteps = Math.Max(_expectedSteps, _stepIndex);
            var overall = (_completedSteps + _stepFraction) / totalSteps * 100d;
            _overallPercent = Math.Max(_overallPercent, Math.Clamp(overall, 0, 100));

            return progress with
            {
                StepIndex = _stepIndex,
                StepCount = Math.Max(_expectedSteps, _stepIndex),
                OverallPercent = _overallPercent
            };
        }
    }
}

/// <summary>同步回调进度适配器：避免 <see cref="Progress{T}"/> 的异步投递造成完成事件早于最后一批字节事件。</summary>
internal sealed class CallbackProgress<T> : IProgress<T>
{
    private readonly Action<T> _action;

    public CallbackProgress(Action<T> action)
    {
        _action = action;
    }

    public void Report(T value) => _action(value);
}

/// <summary>
/// 线程安全的步骤内计数器：把 Stage / BeginFile / FileBytes / FileCompleted
/// 汇总为一条 <see cref="InstallProgress"/>。
/// 重试或换源时收到的字节数可能归零，这里只保留观测到的最大值，保证本步进度不回退。
/// </summary>
internal sealed class InstallProgressCounter
{
    private readonly IProgress<InstallProgress>? _sink;
    private readonly object _lock = new();
    private string _stage = "";
    private string _itemName = "";
    private string _itemBytes = "";
    private int _filesDone;
    private int _filesTotal;
    private long _bytesTotal;
    private long _completedBytes;
    private long _currentReceived;
    private long _currentExpected;

    public InstallProgressCounter(IProgress<InstallProgress>? sink)
    {
        _sink = sink;
    }

    public void Stage(string stage, int totalFiles = 0)
    {
        lock (_lock)
        {
            _stage = stage;
            _filesDone = 0;
            _filesTotal = Math.Max(0, totalFiles);
            _bytesTotal = 0;
            _completedBytes = 0;
            _currentReceived = 0;
            _currentExpected = 0;
            _itemName = "";
            _itemBytes = "";
            Publish();
        }
    }

    public void BeginFile(string name, long expectedBytes)
    {
        lock (_lock)
        {
            _itemName = name;
            _itemBytes = "";
            _currentReceived = 0;
            _currentExpected = expectedBytes > 0 ? expectedBytes : 0;
            if (_currentExpected > 0)
            {
                _bytesTotal += _currentExpected;
            }

            Publish();
        }
    }

    public void FileBytes(long received, long total)
    {
        lock (_lock)
        {
            _currentReceived = Math.Max(_currentReceived, Math.Max(0, received));
            if (_currentExpected <= 0 && total > 0)
            {
                _currentExpected = total;
                _bytesTotal += total;
            }

            _itemBytes = FormatBytes(_currentReceived, total > 0 ? total : _currentExpected);
            Publish();
        }
    }

    public void FileCompleted(long bytes)
    {
        lock (_lock)
        {
            _filesDone++;
            var actual = bytes > 0 ? bytes : _currentReceived;
            _completedBytes += Math.Max(0, actual);
            if (_currentExpected <= 0 && actual > 0)
            {
                _bytesTotal += actual;
            }

            _currentReceived = 0;
            _currentExpected = 0;
            _itemName = "";
            _itemBytes = "";
            Publish();
        }
    }

    private void Publish()
    {
        var item = _itemName;
        if (item.Length > 0 && _filesTotal > 0)
        {
            item = $"{item}（{Math.Min(_filesDone + 1, _filesTotal)}/{_filesTotal}）";
        }

        if (_itemBytes.Length > 0)
        {
            item = item.Length == 0 ? _itemBytes : $"{item} · {_itemBytes}";
        }

        _sink?.Report(new InstallProgress(
            _stage,
            item,
            _filesDone,
            _filesTotal,
            _completedBytes + _currentReceived,
            _bytesTotal));
    }

    private static string FormatBytes(long received, long total)
    {
        static double Megabytes(long value) => Math.Round(value / (1024d * 1024d), 1);
        return total > 0
            ? $"{Megabytes(received)} MB / {Megabytes(total)} MB"
            : $"{Megabytes(received)} MB";
    }
}
