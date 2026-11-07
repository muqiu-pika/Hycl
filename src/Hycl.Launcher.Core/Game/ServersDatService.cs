using System.IO.Compression;

namespace Hycl.Launcher.Core.Game;

/// <summary>servers.dat 中的一条服务器记录。</summary>
public sealed record GameServerEntry(string Name, string Ip, string Icon);

/// <summary>
/// servers.dat（NBT）读写：用于实例管理里的「服务器」页。
/// 文件通常是未压缩的 NBT，但也兼容旧版的 gzip 压缩格式；写入时保留 icon（Base64 图标）。
/// </summary>
public static class ServersDatService
{
    private const int TagEnd = 0;
    private const int TagByte = 1;
    private const int TagShort = 2;
    private const int TagInt = 3;
    private const int TagLong = 4;
    private const int TagFloat = 5;
    private const int TagDouble = 6;
    private const int TagByteArray = 7;
    private const int TagString = 8;
    private const int TagList = 9;
    private const int TagCompound = 10;
    private const int TagIntArray = 11;
    private const int TagLongArray = 12;

    public static IReadOnlyList<GameServerEntry> Read(string path)
    {
        if (!File.Exists(path))
        {
            return [];
        }

        try
        {
            var bytes = File.ReadAllBytes(path);
            using var stream = new MemoryStream(IsGzip(bytes) ? Decompress(bytes) : bytes);
            using var reader = new BinaryReader(stream);
            return Parse(reader);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or EndOfStreamException or InvalidDataException)
        {
            return [];
        }
    }

    public static void Write(string path, IReadOnlyList<GameServerEntry> servers)
    {
        var directory = Path.GetDirectoryName(path);
        if (directory is not null)
        {
            Directory.CreateDirectory(directory);
        }

        var temporary = path + ".hycl-tmp";
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var writer = new BinaryWriter(stream))
        {
            // 根 Compound
            writer.Write((byte)TagCompound);
            WriteNbtString(writer, string.Empty);

            // servers 列表（元素为 Compound）
            writer.Write((byte)TagList);
            WriteNbtString(writer, "servers");
            writer.Write((byte)TagCompound);
            writer.Write(servers.Count);
            foreach (var server in servers)
            {
                writer.Write((byte)TagCompound);
                WriteNbtString(writer, string.Empty);
                writer.Write((byte)TagString);
                WriteNbtString(writer, "name");
                WriteNbtString(writer, server.Name);
                writer.Write((byte)TagString);
                WriteNbtString(writer, "ip");
                WriteNbtString(writer, server.Ip);
                writer.Write((byte)TagString);
                WriteNbtString(writer, "icon");
                WriteNbtString(writer, server.Icon);
                writer.Write((byte)TagEnd);
            }

            writer.Write((byte)TagEnd);
        }

        File.Move(temporary, path, true);
    }

    private static IReadOnlyList<GameServerEntry> Parse(BinaryReader reader)
    {
        var type = reader.ReadByte();
        if (type != TagCompound)
        {
            return [];
        }

        ReadNbtString(reader); // 根名称
        var servers = new List<GameServerEntry>();

        while (true)
        {
            var tag = reader.ReadByte();
            if (tag == TagEnd)
            {
                break;
            }

            var name = ReadNbtString(reader);
            if (tag == TagList && name == "servers")
            {
                var elementType = reader.ReadByte();
                var count = reader.ReadInt32();
                for (var i = 0; i < count; i++)
                {
                    if (elementType == TagCompound)
                    {
                        servers.Add(ParseServer(reader));
                    }
                    else
                    {
                        Skip(reader, elementType);
                    }
                }
            }
            else
            {
                Skip(reader, tag);
            }
        }

        return servers;
    }

    private static GameServerEntry ParseServer(BinaryReader reader)
    {
        // 列表元素已经声明为 Compound，这里消费 Compound 的类型字节与名称
        if (reader.ReadByte() != TagCompound)
        {
            throw new InvalidDataException("servers 列表元素不是 Compound");
        }

        ReadNbtString(reader);

        var name = "";
        var ip = "";
        var icon = "";

        while (true)
        {
            var tag = reader.ReadByte();
            if (tag == TagEnd)
            {
                break;
            }

            var key = ReadNbtString(reader);
            if (tag == TagString)
            {
                var value = ReadNbtString(reader);
                switch (key)
                {
                    case "name":
                        name = value;
                        break;
                    case "ip":
                        ip = value;
                        break;
                    case "icon":
                        icon = value;
                        break;
                }
            }
            else
            {
                Skip(reader, tag);
            }
        }

        return new GameServerEntry(name.Length == 0 ? ip : name, ip, icon);
    }

    private static void Skip(BinaryReader reader, int type)
    {
        switch (type)
        {
            case TagByte:
                reader.ReadByte();
                break;
            case TagShort:
                reader.ReadInt16();
                break;
            case TagInt:
                reader.ReadInt32();
                break;
            case TagLong:
                reader.ReadInt64();
                break;
            case TagFloat:
                reader.ReadSingle();
                break;
            case TagDouble:
                reader.ReadDouble();
                break;
            case TagByteArray:
                reader.ReadBytes(reader.ReadInt32());
                break;
            case TagString:
                ReadNbtString(reader);
                break;
            case TagList:
                var elementType = reader.ReadByte();
                var count = reader.ReadInt32();
                for (var i = 0; i < count; i++)
                {
                    Skip(reader, elementType);
                }

                break;
            case TagCompound:
                while (true)
                {
                    var childType = reader.ReadByte();
                    if (childType == TagEnd)
                    {
                        break;
                    }

                    ReadNbtString(reader);
                    Skip(reader, childType);
                }

                break;
            case TagIntArray:
                reader.ReadBytes(reader.ReadInt32() * 4);
                break;
            case TagLongArray:
                reader.ReadBytes(reader.ReadInt32() * 8);
                break;
            default:
                throw new InvalidDataException($"未知的 NBT 标签类型：{type}");
        }
    }

    /// <summary>NBT 字符串：short 长度 + UTF-8 字节（与 Minecraft 的格式一致，不能用 BinaryWriter.Write(string)）。</summary>
    private static string ReadNbtString(BinaryReader reader)
    {
        var length = reader.ReadInt16();
        if (length <= 0)
        {
            return string.Empty;
        }

        return System.Text.Encoding.UTF8.GetString(reader.ReadBytes(length));
    }

    private static void WriteNbtString(BinaryWriter writer, string value)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(value);
        writer.Write((short)bytes.Length);
        writer.Write(bytes);
    }

    private static bool IsGzip(byte[] bytes)
        => bytes.Length >= 2 && bytes[0] == 0x1F && bytes[1] == 0x8B;

    private static byte[] Decompress(byte[] bytes)
    {
        using var input = new GZipStream(new MemoryStream(bytes), CompressionMode.Decompress);
        using var output = new MemoryStream();
        input.CopyTo(output);
        return output.ToArray();
    }
}
