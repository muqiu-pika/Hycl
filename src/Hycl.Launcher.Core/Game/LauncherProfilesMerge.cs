using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Hycl.Launcher.Core.Services;

namespace Hycl.Launcher.Core.Game;

/// <summary>
/// launcher_profiles.json 合并（思路对应 PCL-CE 启动前的 launcher_profiles 更新）：
/// 正版（Microsoft）登录后，游戏会读取游戏目录下的 launcher_profiles.json 来找当前账号的档案条目，
/// 缺失时游戏内统计、多人列表等以档案为键的功能会拿不到数据。启动前把当前账号
/// （uuid / name / accessToken 结构）合并进该文件；只增改本启动器相关的键，
/// 其它启动器写下的内容原样保留。文件损坏（JSON 解析失败）时先改名备份再重建最小结构，
/// 不像 PCL-CE 那样直接删除，保留现场便于玩家排查。只实现本类，接线由启动流程完成。
/// </summary>
public static class LauncherProfilesMerge
{
    private const string FileName = "launcher_profiles.json";

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.General);

    /// <summary>
    /// 把当前账号合并进游戏目录的 launcher_profiles.json。返回是否成功写入；
    /// 失败只记日志，不抛异常，避免阻断启动。
    /// </summary>
    /// <param name="gameDirectory">游戏根目录。</param>
    /// <param name="profileId">当前档案 uuid（正版账号与档案同 id）。</param>
    /// <param name="playerName">玩家名（档案显示名）。</param>
    /// <param name="accessToken">登录令牌；为空表示离线/外置账号，写入 offline 型档案。</param>
    /// <param name="logger">可选日志。</param>
    public static bool EnsureProfile(
        string gameDirectory,
        string profileId,
        string playerName,
        string accessToken,
        LauncherLogger? logger = null)
    {
        try
        {
            var directory = Path.GetFullPath(gameDirectory);
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, FileName);

            var root = LoadRoot(path, logger);

            // profiles：档案条目。只写最小字段（name/type/created/lastUsed），
            // 图标、lastVersionId 等由游戏或其它启动器自行补全
            var profiles = EnsureObject(root, "profiles");
            var now = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
            profiles[profileId] = new JsonObject
            {
                ["name"] = playerName,
                ["type"] = string.IsNullOrWhiteSpace(accessToken) ? "offline" : "mica",
                ["created"] = now,
                ["lastUsed"] = now
            };

            // authenticationDatabase：账号条目（uuid → 玩家名 + 令牌 + 档案显示名）。
            // 已有条目（其它启动器写的）只更新令牌相关字段，其余保留
            var authenticationDatabase = EnsureObject(root, "authenticationDatabase");
            var account = authenticationDatabase[profileId] as JsonObject ?? new JsonObject();
            account["username"] = playerName;
            if (!string.IsNullOrWhiteSpace(accessToken))
            {
                account["accessToken"] = accessToken;
            }

            var accountProfiles = account["profiles"] as JsonObject ?? new JsonObject();
            accountProfiles[profileId] = new JsonObject { ["displayName"] = playerName };
            account["profiles"] = accountProfiles;
            authenticationDatabase[profileId] = account;

            // selectedUser：本次启动使用的账号与档案，覆盖为当前登录的账号
            var selectedUser = EnsureObject(root, "selectedUser");
            selectedUser["account"] = profileId;
            selectedUser["profile"] = profileId;

            // 原子写，避免游戏或其它启动器读到半截文件
            var temp = path + ".hycl-tmp";
            File.WriteAllText(temp, root.ToJsonString(SerializerOptions), new UTF8Encoding(false));
            File.Move(temp, path, true);
            logger?.Info($"已合并当前账号（{playerName}）到 {path}");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            logger?.Warn($"合并 launcher_profiles.json 失败（不阻断启动）：{ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 读取现有文件为 JSON 对象；不存在或损坏时返回最小结构
    /// （{ profiles: {}, settings: {} }），损坏的文件已在此处改名备份。
    /// </summary>
    private static JsonObject LoadRoot(string path, LauncherLogger? logger)
    {
        if (File.Exists(path))
        {
            try
            {
                return JsonNode.Parse(File.ReadAllText(path)) as JsonObject
                       ?? throw new JsonException("顶层不是 JSON 对象");
            }
            catch (JsonException ex)
            {
                // 损坏文件先改名备份再重建，不直接删除
                var backup = path + ".hycl-bad-"
                             + DateTimeOffset.Now.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
                File.Move(path, backup);
                logger?.Warn($"launcher_profiles.json 解析失败（{ex.Message}），已备份为 {Path.GetFileName(backup)} 并重建");
            }
        }

        return new JsonObject
        {
            ["profiles"] = new JsonObject(),
            ["settings"] = new JsonObject()
        };
    }

    /// <summary>取根对象下的子对象；缺失或类型不对（如被写成了字符串）时重建。</summary>
    private static JsonObject EnsureObject(JsonObject root, string key)
    {
        if (root[key] is not JsonObject value)
        {
            value = new JsonObject();
            root[key] = value;
        }

        return value;
    }
}
