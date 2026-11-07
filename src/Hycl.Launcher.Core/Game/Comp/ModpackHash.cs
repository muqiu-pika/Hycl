using System.Security.Cryptography;

namespace Hycl.Launcher.Core.Game.Comp;

/// <summary>导出整合包时一个文件的三类校验值：Modrinth 用 sha1/sha512，CurseForge 用 murmur2 指纹。</summary>
public sealed record ModpackFileHashes(string Sha1, string Sha512, uint Murmur2, long Size);

/// <summary>
/// 整合包导出所需的文件哈希：SHA-1、SHA-512 与 CurseForge 指纹。
/// CurseForge 指纹是 MurmurHash2（32 位，seed = 1），并且会忽略 ASCII 空白字节（9/10/13/32），
/// 与 CurseForge 官方指纹一致；SHA 值则按原始字节计算。
/// </summary>
public static class ModpackHash
{
    private const int BufferSize = 64 * 1024;

    private const uint Multiplier = 0x5bd1e995;

    public static async Task<ModpackFileHashes> ComputeAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        using var sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        using var sha512 = IncrementalHash.CreateHash(HashAlgorithmName.SHA512);

        // 第一遍：同时计算 SHA-1 / SHA-512，并统计过滤空白后的长度（指纹的种子需要这个长度）
        long filteredLength = 0;
        await using (var stream = OpenRead(path))
        {
            var buffer = new byte[BufferSize];
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = await stream.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                sha1.AppendData(buffer, 0, read);
                sha512.AppendData(buffer, 0, read);
                for (var index = 0; index < read; index++)
                {
                    if (!IsWhitespace(buffer[index]))
                    {
                        filteredLength++;
                    }
                }
            }
        }

        // 第二遍：MurmurHash2（跳过空白字节），避免为按 4 字节分块而在内存里再复制一份文件
        var murmur = 1u ^ (uint)filteredLength;
        uint word = 0;
        var shift = 0;
        await using (var stream = OpenRead(path))
        {
            var buffer = new byte[BufferSize];
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = await stream.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                for (var index = 0; index < read; index++)
                {
                    var value = buffer[index];
                    if (IsWhitespace(value))
                    {
                        continue;
                    }

                    word |= (uint)value << shift;
                    if (shift == 24)
                    {
                        word *= Multiplier;
                        word ^= word >> 24;
                        word *= Multiplier;
                        murmur *= Multiplier;
                        murmur ^= word;
                        word = 0;
                        shift = 0;
                    }
                    else
                    {
                        shift += 8;
                    }
                }
            }
        }

        if (shift != 0)
        {
            murmur ^= word;
            murmur *= Multiplier;
        }

        murmur ^= murmur >> 13;
        murmur *= Multiplier;
        murmur ^= murmur >> 15;

        return new ModpackFileHashes(
            Convert.ToHexString(sha1.GetHashAndReset()).ToLowerInvariant(),
            Convert.ToHexString(sha512.GetHashAndReset()).ToLowerInvariant(),
            murmur,
            new FileInfo(path).Length);
    }

    /// <summary>CurseForge 指纹会忽略的 ASCII 空白：\t \n \r 空格。</summary>
    private static bool IsWhitespace(byte value)
        => value is 9 or 10 or 13 or 32;

    private static FileStream OpenRead(string path)
        => new(path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
}
