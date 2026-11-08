using System.IO.Compression;

namespace Hycl.Launcher.Plugin.Online;

/// <summary>
/// 极简 tar.gz 提取器（对应 KAMUCL terracotta.ts 的 extractTarGz）：
/// GZip 解压后按 512 字节 POSIX tar 头解析，仅提取普通文件到平铺目标目录，
/// 256MB 总量上限 + 逐文件越界防护。Terracotta 官方包为单层平铺结构。
/// </summary>
public static class TarGzReader
{
    private const long MaxTotalBytes = 256 * 1024 * 1024;

    public static void Extract(string tarGzPath, string destinationDirectory)
    {
        using var fileStream = File.OpenRead(tarGzPath);
        using var gzip = new GZipStream(fileStream, CompressionMode.Decompress);
        using var buffer = new MemoryStream();
        gzip.CopyTo(buffer, 1024 * 1024);
        var raw = buffer.ToArray();

        Directory.CreateDirectory(destinationDirectory);
        var fullDestination = Path.GetFullPath(destinationDirectory);
        long total = 0;
        var offset = 0;
        while (offset + 512 <= raw.Length)
        {
            var header = raw.AsSpan(offset, 512);
            if (IsAllZero(header))
            {
                break;
            }

            var name = ReadUtf8(header[..100]);
            var size = ParseOctal(header[124..136]);
            var typeFlag = header.Length > 156 ? (char)header[156] : '0';
            offset += 512;
            if (size < 0 || offset + size > raw.Length)
            {
                throw new InvalidDataException("陶瓦压缩包结构不完整");
            }

            var data = raw.AsSpan(offset, (int)size);
            offset += (int)(Math.Ceiling(size / 512d) * 512);
            if (typeFlag is not ('0' or '\0'))
            {
                continue;
            }

            // 只取末段文件名，平铺写入目标目录（包内无目录层级）
            var baseName = Path.GetFileName(name.Replace('\\', '/').TrimEnd('/'));
            if (string.IsNullOrEmpty(baseName) || baseName is "." or "..")
            {
                continue;
            }

            total += size;
            if (total > MaxTotalBytes)
            {
                throw new InvalidDataException("陶瓦压缩包超出解压上限（256MB）");
            }

            var target = Path.GetFullPath(Path.Combine(fullDestination, baseName));
            if (!target.StartsWith(fullDestination + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                throw new IOException("陶瓦压缩包含有越界路径：" + baseName);
            }

            using var output = File.Create(target);
            output.Write(data);
        }
    }

    private static bool IsAllZero(ReadOnlySpan<byte> span)
    {
        foreach (var value in span)
        {
            if (value != 0)
            {
                return false;
            }
        }

        return true;
    }

    private static long ParseOctal(ReadOnlySpan<byte> span)
    {
        var text = ReadUtf8(span).Trim('\0', ' ');
        return text.Length == 0 ? 0 : Convert.ToInt64(text, 8);
    }

    private static string ReadUtf8(ReadOnlySpan<byte> span)
    {
        var end = span.IndexOf((byte)0);
        if (end >= 0)
        {
            span = span[..end];
        }

        return System.Text.Encoding.UTF8.GetString(span).Trim();
    }
}
