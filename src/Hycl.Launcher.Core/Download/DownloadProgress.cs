namespace Hycl.Launcher.Core.Download;

/// <summary>单文件下载进度。</summary>
public sealed record DownloadByteProgress(long ReceivedBytes, long TotalBytes)
{
    public double Percentage => TotalBytes <= 0 ? 0 : Math.Clamp((double)ReceivedBytes / TotalBytes, 0, 1);
}

/// <summary>安装任务进度。</summary>
public sealed record InstallProgress(
    string Stage,
    string CurrentItem,
    int FilesDone,
    int FilesTotal,
    double BytesDone,
    double BytesTotal)
{
    public double Percentage
    {
        get
        {
            if (BytesTotal > 0)
            {
                return Math.Clamp(BytesDone / BytesTotal, 0, 1);
            }

            return FilesTotal > 0 ? Math.Clamp((double)FilesDone / FilesTotal, 0, 1) : 0;
        }
    }
}
