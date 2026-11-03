using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hycl.Launcher.Core.Game.Models;

/// <summary>版本 JSON（versions/&lt;id&gt;/&lt;id&gt;.json）。</summary>
public sealed class VersionInfo
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("inheritsFrom")]
    public string? InheritsFrom { get; set; }

    [JsonPropertyName("mainClass")]
    public string? MainClass { get; set; }

    [JsonPropertyName("minecraftArguments")]
    public string? MinecraftArguments { get; set; }

    [JsonPropertyName("arguments")]
    public VersionArguments? Arguments { get; set; }

    [JsonPropertyName("assetIndex")]
    public AssetIndexInfo? AssetIndex { get; set; }

    [JsonPropertyName("assets")]
    public string? Assets { get; set; }

    [JsonPropertyName("downloads")]
    public VersionDownloads? Downloads { get; set; }

    [JsonPropertyName("libraries")]
    public List<LibraryInfo> Libraries { get; set; } = new();

    [JsonPropertyName("logging")]
    public LoggingInfo? Logging { get; set; }

    [JsonPropertyName("javaVersion")]
    public JavaVersionInfo? JavaVersion { get; set; }

    [JsonPropertyName("releaseTime")]
    public string? ReleaseTime { get; set; }

    [JsonPropertyName("time")]
    public string? Time { get; set; }

    [JsonPropertyName("complianceLevel")]
    public int? ComplianceLevel { get; set; }
}

public sealed class VersionArguments
{
    [JsonPropertyName("game")]
    public List<ArgumentEntry> Game { get; set; } = new();

    [JsonPropertyName("jvm")]
    public List<ArgumentEntry> Jvm { get; set; } = new();
}

[JsonConverter(typeof(ArgumentEntryConverter))]
public sealed class ArgumentEntry
{
    public string[] Values { get; set; } = Array.Empty<string>();

    public RuleInfo[]? Rules { get; set; }
}

public sealed class RuleInfo
{
    [JsonPropertyName("action")]
    public string Action { get; set; } = "allow";

    [JsonPropertyName("os")]
    public RuleOs? Os { get; set; }

    [JsonPropertyName("features")]
    public Dictionary<string, bool>? Features { get; set; }
}

public sealed class RuleOs
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("arch")]
    public string? Arch { get; set; }

    [JsonPropertyName("version")]
    public string? Version { get; set; }
}

public sealed class AssetIndexInfo
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("sha1")]
    public string? Sha1 { get; set; }

    [JsonPropertyName("size")]
    public long Size { get; set; }

    [JsonPropertyName("totalSize")]
    public long TotalSize { get; set; }

    [JsonPropertyName("url")]
    public string Url { get; set; } = "";
}

public sealed class VersionDownloads
{
    [JsonPropertyName("client")]
    public LibraryDownload? Client { get; set; }
}

public sealed class LibraryInfo
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("url")]
    public string? Url { get; set; }

    [JsonPropertyName("downloads")]
    public LibraryDownloads? Downloads { get; set; }

    [JsonPropertyName("natives")]
    public Dictionary<string, string>? Natives { get; set; }

    [JsonPropertyName("extract")]
    public LibraryExtract? Extract { get; set; }

    [JsonPropertyName("rules")]
    public RuleInfo[]? Rules { get; set; }
}

public sealed class LibraryDownloads
{
    [JsonPropertyName("artifact")]
    public LibraryDownload? Artifact { get; set; }

    [JsonPropertyName("classifiers")]
    public Dictionary<string, LibraryDownload>? Classifiers { get; set; }
}

public sealed class LibraryDownload
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("path")]
    public string? Path { get; set; }

    [JsonPropertyName("sha1")]
    public string? Sha1 { get; set; }

    [JsonPropertyName("size")]
    public long Size { get; set; }

    [JsonPropertyName("url")]
    public string Url { get; set; } = "";
}

public sealed class LibraryExtract
{
    [JsonPropertyName("exclude")]
    public List<string> Exclude { get; set; } = new();
}

public sealed class LoggingInfo
{
    [JsonPropertyName("client")]
    public LoggingClient? Client { get; set; }
}

public sealed class LoggingClient
{
    [JsonPropertyName("argument")]
    public string? Argument { get; set; }

    [JsonPropertyName("file")]
    public LibraryDownload? File { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }
}

public sealed class JavaVersionInfo
{
    [JsonPropertyName("component")]
    public string? Component { get; set; }

    [JsonPropertyName("majorVersion")]
    public int MajorVersion { get; set; }
}

/// <summary>arguments 数组中的字符串或 { "rules", "value" } 对象。</summary>
public sealed class ArgumentEntryConverter : JsonConverter<ArgumentEntry>
{
    public override ArgumentEntry Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            return new ArgumentEntry { Values = new[] { reader.GetString() ?? "" } };
        }

        if (reader.TokenType != JsonTokenType.StartObject)
        {
            reader.Skip();
            return new ArgumentEntry();
        }

        var values = Array.Empty<string>();
        RuleInfo[]? rules = null;
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
            {
                break;
            }

            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                continue;
            }

            var propertyName = reader.GetString();
            reader.Read();
            switch (propertyName)
            {
                case "rules":
                    rules = JsonSerializer.Deserialize<RuleInfo[]>(ref reader, options);
                    break;
                case "value" when reader.TokenType == JsonTokenType.String:
                    values = new[] { reader.GetString() ?? "" };
                    break;
                case "value" when reader.TokenType == JsonTokenType.StartArray:
                {
                    var list = new List<string>();
                    while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                    {
                        if (reader.TokenType == JsonTokenType.String)
                        {
                            list.Add(reader.GetString() ?? "");
                        }
                    }

                    values = list.ToArray();
                    break;
                }
                default:
                    reader.Skip();
                    break;
            }
        }

        return new ArgumentEntry { Values = values, Rules = rules };
    }

    public override void Write(Utf8JsonWriter writer, ArgumentEntry value, JsonSerializerOptions options)
        => throw new NotSupportedException("ArgumentEntry 仅用于反序列化。");
}

/// <summary>资源索引（assets/indexes/&lt;id&gt;.json）。</summary>
public sealed class AssetIndex
{
    [JsonPropertyName("objects")]
    public Dictionary<string, AssetObject>? Objects { get; set; }

    [JsonPropertyName("virtual")]
    public bool Virtual { get; set; }

    [JsonPropertyName("map_to_resources")]
    public bool MapToResources { get; set; }
}

public sealed class AssetObject
{
    [JsonPropertyName("hash")]
    public string Hash { get; set; } = "";

    [JsonPropertyName("size")]
    public long Size { get; set; }
}
