using Hycl.Launcher.Core.Auth;
using Hycl.Launcher.Core.Config;

namespace Hycl.Launcher.Core.Game;

/// <summary>
/// 启动选项统一组装：实例级设置优先，缺省回落全局配置。
/// 启动页与主页共用同一份取值逻辑，保证从任何入口启动行为一致。
/// </summary>
public static class LaunchOptionsBuilder
{
    public static LaunchOptions Build(
        LauncherConfig config,
        MinecraftInstance instance,
        PlayerAccount? account,
        string? authlibJar = null,
        string? authlibPrefetched = null)
    {
        var instanceSettings = config.FindInstanceSettings(instance.Id);
        var isolated = instanceSettings?.VersionIsolation ?? true;
        return new LaunchOptions
        {
            InstanceId = instance.Id,
            GameVersion = instance.GameVersion,
            GameDirectory = config.EffectiveGameDirectory,
            InstanceDirectory = isolated ? instance.Directory : "",
            GameSettings = instanceSettings?.GameSettingsMode == "instance"
                ? null
                : config.GameSettings,
            JavaPath = string.IsNullOrWhiteSpace(instanceSettings?.JavaPath)
                ? config.JavaPath
                : instanceSettings.JavaPath,
            MemoryMegabytes = instanceSettings is { MemoryMegabytes: > 0 }
                ? instanceSettings.MemoryMegabytes
                : config.GetEffectiveMemoryMegabytes(),
            InitialMemoryMegabytes = instanceSettings is { InitialMemoryMegabytes: >= 0 }
                ? instanceSettings.InitialMemoryMegabytes
                : config.InitialMemoryMegabytes,
            LockMemory = config.LockMemory,
            WindowType = instanceSettings is { WindowType: >= 0 }
                ? instanceSettings.WindowType
                : config.WindowType,
            WindowWidth = instanceSettings is { WindowWidth: > 0 }
                ? instanceSettings.WindowWidth
                : config.WindowWidth,
            WindowHeight = instanceSettings is { WindowHeight: > 0 }
                ? instanceSettings.WindowHeight
                : config.WindowHeight,
            ExtraJvmArguments = string.IsNullOrWhiteSpace(instanceSettings?.ExtraJvmArguments)
                ? config.ExtraJvmArguments
                : instanceSettings.ExtraJvmArguments,
            ExtraGameArguments = string.IsNullOrWhiteSpace(instanceSettings?.ExtraGameArguments)
                ? config.ExtraGameArguments
                : instanceSettings.ExtraGameArguments,
            PlayerName = account?.Name ?? "",
            PlayerUuid = account?.Uuid ?? "",
            AccessToken = account?.AccessToken ?? "",
            UserType = account?.Type switch
            {
                "msa" => "msa",
                "authlib" => "Legacy",
                _ => "legacy"
            },
            AuthClientId = account?.Type == "msa" ? HyclSecrets.MicrosoftOAuthClientId : "",
            AuthXuid = account?.Xuid ?? "",
            AuthlibServer = account?.Type == "authlib" ? account.Server : "",
            AuthlibJarPath = authlibJar ?? "",
            AuthlibPrefetched = authlibPrefetched ?? "",
            WindowTitle = string.IsNullOrWhiteSpace(instanceSettings?.WindowTitle)
                ? config.WindowTitle
                : instanceSettings.WindowTitle,
            TypeInfo = string.IsNullOrWhiteSpace(instanceSettings?.TypeInfo)
                ? config.TypeInfo
                : instanceSettings.TypeInfo,
            LauncherVisibility = config.LauncherVisibility,
            ProcessPriority = config.ProcessPriority,
            PreferredIpStack = config.PreferredIpStack,
            PreLaunchCommand = config.PreLaunchCommand,
            PreLaunchCommandWait = config.PreLaunchCommandWait,
            ServerToEnter = instanceSettings?.ServerToEnter.Trim() ?? ""
        };
    }
}
