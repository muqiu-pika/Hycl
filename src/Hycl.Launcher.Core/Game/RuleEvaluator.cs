using Hycl.Launcher.Core.Platform;
using Hycl.Launcher.Core.Game.Models;

namespace Hycl.Launcher.Core.Game;

/// <summary>规则判定环境（操作系统 / 架构 / 特性）。</summary>
public sealed class RuleEnvironment
{
    public static RuleEnvironment Current(bool hasCustomResolution) => new()
    {
        OsName = PlatformInfo.IsWindows ? "windows" : PlatformInfo.IsMacOS ? "osx" : "linux",
        Arch = PlatformInfo.IsWindows
            ? RuntimeArch()
            : PlatformInfo.ArchitectureName switch
            {
                "x64" => "x86_64",
                "x86" => "x86",
                "arm64" => "arm64",
                _ => PlatformInfo.ArchitectureName
            },
        Features = new Dictionary<string, bool>
        {
            ["is_demo_user"] = false,
            ["has_custom_resolution"] = hasCustomResolution,
            ["has_quick_plays_support"] = false,
            ["is_quick_play_singleplayer"] = false,
            ["is_quick_play_multiplayer"] = false,
            ["is_quick_play_realms"] = false
        }
    };

    public string OsName { get; init; } = "windows";

    public string Arch { get; init; } = "x86_64";

    public IReadOnlyDictionary<string, bool> Features { get; init; } = new Dictionary<string, bool>();

    private static string RuntimeArch() => System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture switch
    {
        System.Runtime.InteropServices.Architecture.X64 => "x86_64",
        System.Runtime.InteropServices.Architecture.X86 => "x86",
        System.Runtime.InteropServices.Architecture.Arm64 => "arm64",
        var other => other.ToString().ToLowerInvariant()
    };
}

public static class RuleEvaluator
{
    /// <summary>按 JSON 中的 rules 判断某个库/参数是否适用于当前平台。</summary>
    public static bool IsAllowed(IEnumerable<RuleInfo>? rules, RuleEnvironment environment)
    {
        if (rules is null)
        {
            return true;
        }

        var list = rules.ToList();
        if (list.Count == 0)
        {
            return true;
        }

        var allowed = false;
        foreach (var rule in list)
        {
            if (Matches(rule, environment))
            {
                allowed = rule.Action != "disallow";
            }
        }

        return allowed;
    }

    private static bool Matches(RuleInfo rule, RuleEnvironment environment)
    {
        if (rule.Os is not null)
        {
            if (!string.IsNullOrEmpty(rule.Os.Name) && !rule.Os.Name.Equals(environment.OsName, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (!string.IsNullOrEmpty(rule.Os.Arch) && !rule.Os.Arch.Equals(environment.Arch, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        if (rule.Features is not null)
        {
            foreach (var pair in rule.Features)
            {
                var actual = environment.Features.TryGetValue(pair.Key, out var value) && value;
                if (actual != pair.Value)
                {
                    return false;
                }
            }
        }

        return true;
    }
}
