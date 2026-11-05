using System.Globalization;
using System.Text;
using Hycl.Launcher.Core.Config;

namespace Hycl.Launcher.Core.Game;

/// <summary>
/// 全局游戏内设置落地：只在实例首次启动时把设置写入 options.txt，
/// 之后不再覆盖，尊重玩家在游戏内做的修改。
/// </summary>
public static class GameOptionsService
{
    private const string OptionsFileName = "options.txt";

    private const string AppliedMarkerName = ".hycl-game-settings.applied";

    /// <summary>本启动器会覆盖的键；其余键（键位、资源包等）原样保留。</summary>
    private static readonly HashSet<string> ManagedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "renderDistance", "simulationDistance", "fov", "guiScale", "maxFps", "enableVsync",
        "gamma", "graphics", "graphicsMode", "particles", "renderClouds", "ao", "entityShadows",
        "fovEffectScale", "screenEffectScale", "mouseSensitivity", "invertYMouse", "autoJump", "mainHand",
        "soundCategory_master", "soundCategory_music", "soundCategory_record", "soundCategory_weather",
        "soundCategory_block", "soundCategory_hostile", "soundCategory_neutral", "soundCategory_player",
        "soundCategory_ambient", "soundCategory_voice",
        "chatOpacity", "chatScale", "chatWidth", "chatHeightFocused", "chatHeightUnfocused",
        "chatVisibility", "chatColors", "chatLinks",
        "narrator", "reducedMotion", "forceUnicodeFont", "hideSplashTexts", "language", "lang"
    };

    /// <summary>
    /// 尝试写入一次。已禁用、已应用过时返回 false 并通过 <paramref name="reason"/> 说明原因；
    /// 成功写入返回 true。
    /// </summary>
    /// <param name="targetDirectory">实例工作目录（版本隔离时为实例目录，否则为游戏根目录）。</param>
    /// <param name="settings">全局游戏内设置。</param>
    /// <param name="gameVersion">实例对应的原版版本号，用于选择旧版/新版 options.txt 格式。</param>
    /// <param name="dataVersion">客户端 jar 中 version.json 的 world_version；0 表示无法获取。</param>
    /// <param name="reason">未写入时的原因。</param>
    public static bool ApplyOnce(string targetDirectory, GameSettingsConfig settings, string gameVersion, int dataVersion, out string reason)
    {
        if (!settings.Enabled)
        {
            reason = "全局游戏内设置已禁用";
            return false;
        }

        var directory = Path.GetFullPath(targetDirectory);
        var marker = Path.Combine(directory, AppliedMarkerName);
        if (File.Exists(marker))
        {
            reason = "该实例已应用过全局游戏内设置";
            return false;
        }

        Directory.CreateDirectory(directory);
        var optionsPath = Path.Combine(directory, OptionsFileName);
        var existing = File.Exists(optionsPath) ? File.ReadAllLines(optionsPath) : Array.Empty<string>();

        // 只替换本启动器管理的键，其余键（键位、资源包等）原样保留
        var lines = existing.Where(line => !IsManagedKey(line)).ToList();
        if (dataVersion > 0 && !lines.Any(line => line.StartsWith("version:", StringComparison.OrdinalIgnoreCase)))
        {
            // 1.14+ 缺少 version 行时游戏会丢弃整份 options.txt，因此首次生成时必须带上
            lines.Insert(0, $"version:{dataVersion}");
        }

        foreach (var pair in BuildValues(settings, IsLegacyFormat(gameVersion)))
        {
            lines.Add($"{pair.Key}:{pair.Value}");
        }

        // 原子写，避免游戏读到半截文件
        var temp = optionsPath + ".hycl-tmp";
        File.WriteAllLines(temp, lines, new UTF8Encoding(false));
        File.Move(temp, optionsPath, true);

        File.WriteAllText(marker, DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture), new UTF8Encoding(false));
        reason = "";
        return true;
    }

    /// <summary>1.13 起 options.txt 的图形/粒子改用数字枚举，FOV 仍为「相对默认视角的偏移」。</summary>
    private static bool IsLegacyFormat(string gameVersion)
    {
        var parts = gameVersion.Split('.');
        if (parts.Length >= 2
            && int.TryParse(parts[0], out var major)
            && int.TryParse(parts[1], out var minor))
        {
            return major == 1 && minor < 13;
        }

        return false;
    }

    private static Dictionary<string, string> BuildValues(GameSettingsConfig settings, bool legacy)
    {
        var values = new Dictionary<string, string>
        {
            ["renderDistance"] = settings.RenderDistance.ToString(CultureInfo.InvariantCulture),
            ["simulationDistance"] = settings.SimulationDistance.ToString(CultureInfo.InvariantCulture),
            // 游戏内 FOV 滑块以默认 70° 为 0，范围为 -1..1（对应 30°..110°）
            ["fov"] = Format((settings.Fov - 70) / 40.0),
            ["guiScale"] = settings.GuiScale.ToString(CultureInfo.InvariantCulture),
            ["maxFps"] = settings.MaxFps.ToString(CultureInfo.InvariantCulture),
            ["enableVsync"] = Bool(settings.VSync),
            ["gamma"] = Format(settings.Gamma),
            ["particles"] = legacy ? settings.Particles : ParticleIndex(settings.Particles),
            ["renderClouds"] = Bool(settings.Clouds),
            ["ao"] = Bool(settings.SmoothLighting),
            ["entityShadows"] = Bool(settings.EntityShadows),
            ["fovEffectScale"] = Format(settings.FovEffectScale),
            ["screenEffectScale"] = Format(settings.ScreenEffectScale),
            ["mouseSensitivity"] = Format(settings.MouseSensitivity),
            ["invertYMouse"] = Bool(settings.InvertMouse),
            ["autoJump"] = Bool(settings.AutoJump),
            ["mainHand"] = settings.MainHand,
            ["soundCategory_master"] = Format(settings.MasterVolume),
            ["soundCategory_music"] = Format(settings.MusicVolume),
            ["soundCategory_record"] = Format(settings.RecordVolume),
            ["soundCategory_weather"] = Format(settings.WeatherVolume),
            ["soundCategory_block"] = Format(settings.BlockVolume),
            ["soundCategory_hostile"] = Format(settings.HostileVolume),
            ["soundCategory_neutral"] = Format(settings.NeutralVolume),
            ["soundCategory_player"] = Format(settings.PlayerVolume),
            ["soundCategory_ambient"] = Format(settings.AmbientVolume),
            ["soundCategory_voice"] = Format(settings.VoiceVolume),
            ["chatOpacity"] = Format(settings.ChatOpacity),
            ["chatScale"] = Format(settings.ChatScale),
            ["chatWidth"] = Format(settings.ChatWidth),
            ["chatHeightFocused"] = Format(settings.ChatHeightFocused),
            ["chatHeightUnfocused"] = Format(settings.ChatHeightUnfocused),
            ["chatVisibility"] = settings.ChatVisibility.ToString(CultureInfo.InvariantCulture),
            ["chatColors"] = Bool(settings.ChatColors),
            ["chatLinks"] = Bool(settings.ChatLinks),
            ["narrator"] = settings.Narrator.ToString(CultureInfo.InvariantCulture),
            ["reducedMotion"] = Bool(settings.ReducedMotion),
            ["forceUnicodeFont"] = Bool(settings.ForceUnicodeFont),
            ["hideSplashTexts"] = Bool(settings.HideSplashTexts),
            ["lang"] = settings.Language
        };

        if (legacy)
        {
            values["graphics"] = settings.Graphics;
        }
        else
        {
            values["graphicsMode"] = settings.Graphics switch
            {
                "fast" => "0",
                "fabulous" => "2",
                _ => "1"
            };
        }

        return values;
    }

    private static string ParticleIndex(string particles) => particles switch
    {
        "decreased" => "1",
        "minimal" => "2",
        _ => "0"
    };

    private static string Bool(bool value) => value ? "true" : "false";

    private static string Format(double value) =>
        value.ToString("0.#####", CultureInfo.InvariantCulture);

    private static bool IsManagedKey(string line)
    {
        var colon = line.IndexOf(':');
        return colon > 0 && ManagedKeys.Contains(line[..colon]);
    }
}
