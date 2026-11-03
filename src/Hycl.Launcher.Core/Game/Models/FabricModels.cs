using System.Text.Json.Serialization;

namespace Hycl.Launcher.Core.Game.Models;

/// <summary>Fabric Meta loader 列表项。</summary>
public sealed class FabricLoaderEntry
{
    [JsonPropertyName("loader")]
    public FabricLoaderInfo? Loader { get; set; }

    [JsonPropertyName("intermediary")]
    public FabricIntermediary? Intermediary { get; set; }
}

public sealed class FabricLoaderInfo
{
    [JsonPropertyName("version")]
    public string Version { get; set; } = "";

    [JsonPropertyName("stable")]
    public bool Stable { get; set; }
}

public sealed class FabricIntermediary
{
    [JsonPropertyName("version")]
    public string Version { get; set; } = "";
}
