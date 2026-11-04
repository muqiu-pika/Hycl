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
            // 进度一律按文件数计（对齐 PCL-CE）：已完成文件 / 总文件数；
            // 仅当任务没有文件计数（如单文件下载）时才回退到字节数
            if (FilesTotal > 0)
            {
                return Math.Clamp((double)FilesDone / FilesTotal, 0, 1);
            }

            return BytesTotal > 0 ? Math.Clamp(BytesDone / BytesTotal, 0, 1) : 0;
        }
    }
}
