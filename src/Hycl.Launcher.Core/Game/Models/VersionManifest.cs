using System.Text.Json.Serialization;

namespace Hycl.Launcher.Core.Game.Models;

/// <summary>官方版本清单（version_manifest_v2.json）。</summary>
public sealed class VersionManifest
{
    [JsonPropertyName("latest")]
    public LatestVersions? Latest { get; set; }

    [JsonPropertyName("versions")]
    public List<VersionSummary> Versions { get; set; } = new();
}

public sealed class LatestVersions
{
    [JsonPropertyName("release")]
    public string Release { get; set; } = "";

    [JsonPropertyName("snapshot")]
    public string Snapshot { get; set; } = "";
}

public sealed class VersionSummary
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("type")]
    public string Type { get; set; } = "release";

    [JsonPropertyName("url")]
    public string Url { get; set; } = "";

    [JsonPropertyName("time")]
    public string Time { get; set; } = "";

    [JsonPropertyName("releaseTime")]
    public string ReleaseTime { get; set; } = "";

    [JsonPropertyName("sha1")]
    public string? Sha1 { get; set; }
}
