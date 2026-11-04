using System.Net.Http;
using System.Text;
using System.Text.Json;
using Hycl.Launcher.Core.Download;
using Hycl.Launcher.Core.Services;

namespace Hycl.Launcher.Core.Auth;

/// <summary>皮肤站（Yggdrasil）服务信息：API 根地址与站点名。</summary>
public sealed record AuthlibServerInfo(string ApiRoot, string ServerName);

/// <summary>Yggdrasil 角色。</summary>
public sealed record AuthlibProfile(string Id, string Name);

/// <summary>皮肤站登录成功结果。</summary>
public sealed record AuthlibAuthResult(
    string UserName,
    string Uuid,
    string AccessToken,
    string ClientToken,
    string LoginName,
    string ApiRoot,
    string ServerName);

/// <summary>皮肤站登录失败（消息已按用户可读组织）。</summary>
public class AuthlibException : Exception
{
    public AuthlibException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

/// <summary>账号存在多个角色、需要用户选择后继续（携带本次 authenticate 已得到的令牌）。</summary>
public sealed class AuthlibProfileSelectionRequiredException : AuthlibException
{
    public AuthlibProfileSelectionRequiredException(
        IReadOnlyList<AuthlibProfile> profiles,
        string accessToken,
        string clientToken)
        : base("该账号有多个角色，请选择要登录的角色")
    {
        Profiles = profiles;
        AccessToken = accessToken;
        ClientToken = clientToken;
    }

    public IReadOnlyList<AuthlibProfile> Profiles { get; }

    public string AccessToken { get; }

    public string ClientToken { get; }
}

/// <summary>
/// 皮肤站登录（authlib-injector / Yggdrasil 规范）：
/// authenticate / refresh / validate 三接口 + 站点 meta 探测 + authlib-injector.jar 下载。
/// 协议为公开规范，启动器不持有任何密钥；账号密码只在登录瞬间使用，不落盘。
/// </summary>
public sealed class AuthlibAuthService
{
    private static readonly HttpClient _http = CreateClient();

    private readonly LauncherLogger _logger;

    public AuthlibAuthService(LauncherLogger logger)
    {
        _logger = logger;
    }

    /// <summary>内置皮肤站预设（地址 → 显示名）。</summary>
    public static IReadOnlyList<AuthlibServerInfo> PresetServers { get; } =
    [
        new("https://littleskin.cn/api/yggdrasil", "LittleSkin")
    ];

    /// <summary>
    /// 探测皮肤站：输入可以是 Yggdrasil API 根，也可以是站点主页（自动尝试 /api/yggdrasil）。
    /// 根响应的 meta.serverName 作为站点显示名。
    /// </summary>
    public async Task<AuthlibServerInfo> ResolveServerAsync(string input, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(input);
        var trimmed = input.Trim().TrimEnd('/');
        var candidates = new List<string>();
        if (!trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            && !trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = "https://" + trimmed;
        }

        candidates.Add(trimmed);
        if (!trimmed.EndsWith("/api/yggdrasil", StringComparison.OrdinalIgnoreCase))
        {
            candidates.Add(trimmed + "/api/yggdrasil");
        }

        Exception? lastError = null;
        foreach (var candidate in candidates)
        {
            try
            {
                using var response = await _http.GetAsync(candidate, cancellationToken).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                using var document = JsonDocument.Parse(body);
                var serverName = "";
                if (document.RootElement.TryGetProperty("meta", out var meta)
                    && meta.TryGetProperty("serverName", out var name))
                {
                    serverName = name.GetString() ?? "";
                }

                _logger.Info($"皮肤站探测成功：{candidate}（{serverName}）");
                return new AuthlibServerInfo(candidate, serverName);
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
            {
                lastError = ex;
            }
        }

        throw new AuthlibException(
            $"无法连接皮肤站（{input}）：请确认地址正确、站点支持 Yggdrasil/authlib-injector 规范。" +
            $"（{Describe(lastError)}）",
            lastError);
    }

    /// <summary>
    /// 登录：authenticate → 自动确定角色（响应 selectedProfile 或单角色）并 refresh 绑定；
    /// 多角色时抛出 <see cref="AuthlibProfileSelectionRequiredException"/>，由调用方让用户选择后
    /// 调 <see cref="BindProfileAsync"/> 继续（令牌复用，不重发密码）。
    /// </summary>
    public async Task<AuthlibAuthResult> AuthenticateAsync(
        AuthlibServerInfo server,
        string username,
        string password,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(username);
        ArgumentException.ThrowIfNullOrWhiteSpace(password);
        var clientToken = Guid.NewGuid().ToString("N");

        var root = await PostAsync(
                server.ApiRoot + "/authserver/authenticate",
                new AuthlibAuthenticateBody(new AuthlibAgent("Minecraft", 1), username, password, clientToken),
                cancellationToken)
            .ConfigureAwait(false);

        var accessToken = StringOrThrow(root, "accessToken");

        // 角色确定：响应带 selectedProfile 用它；单角色自动选；多角色交由用户选择
        var profile = root.TryGetProperty("selectedProfile", out var selectedNode)
                      && selectedNode.ValueKind == JsonValueKind.Object
                      && selectedNode.TryGetProperty("id", out _)
            ? new AuthlibProfile(
                selectedNode.GetProperty("id").GetString() ?? "",
                selectedNode.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "")
            : null;
        var available = ParseProfiles(root);

        if (profile is null)
        {
            if (available.Count == 1)
            {
                profile = available[0];
            }
            else
            {
                if (available.Count == 0)
                {
                    throw new AuthlibException("该账号在皮肤站上没有任何角色，请先到皮肤站创建角色");
                }

                throw new AuthlibProfileSelectionRequiredException(available, accessToken, clientToken);
            }
        }

        return await BindProfileAsync(
                server.ApiRoot,
                accessToken,
                clientToken,
                profile,
                username,
                server.ServerName,
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>refresh 绑定所选角色（多角色选择后的继续步骤）。</summary>
    public async Task<AuthlibAuthResult> BindProfileAsync(
        string apiRoot,
        string accessToken,
        string clientToken,
        AuthlibProfile profile,
        string loginName,
        string serverName,
        CancellationToken cancellationToken = default)
    {
        var bound = await PostAsync(
                apiRoot.TrimEnd('/') + "/authserver/refresh",
                new AuthlibRefreshBody(accessToken, clientToken, profile.Id, profile.Name),
                cancellationToken)
            .ConfigureAwait(false);
        var boundToken = StringOrThrow(bound, "accessToken");

        _logger.Info($"皮肤站登录成功：{profile.Name}（{profile.Id}）@ {serverName}");
        return new AuthlibAuthResult(
            profile.Name,
            profile.Id,
            boundToken,
            clientToken,
            loginName,
            apiRoot,
            serverName);
    }

    /// <summary>校验令牌是否仍有效（204 = 有效）。</summary>
    public async Task<bool> ValidateAsync(
        string apiRoot,
        string accessToken,
        string clientToken,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(accessToken) || string.IsNullOrWhiteSpace(clientToken))
        {
            return false;
        }

        try
        {
            using var content = new StringContent(
                JsonSerializer.Serialize(new AuthlibValidateBody(accessToken, clientToken)),
                Encoding.UTF8,
                "application/json");
            using var response = await _http
                .PostAsync(apiRoot.TrimEnd('/') + "/authserver/validate", content, cancellationToken)
                .ConfigureAwait(false);
            return (int)response.StatusCode == 204;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return false;
        }
    }

    /// <summary>静默续期（refresh，不换角色）。</summary>
    public async Task<AuthlibAuthResult> RefreshAsync(
        PlayerAccount account,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(account.Server);
        ArgumentException.ThrowIfNullOrWhiteSpace(account.AccessToken);
        var root = await PostAsync(
                account.Server.TrimEnd('/') + "/authserver/refresh",
                new AuthlibRefreshBody(account.AccessToken, account.ClientToken, null, null),
                cancellationToken)
            .ConfigureAwait(false);
        var accessToken = StringOrThrow(root, "accessToken");
        var profile = ParseProfiles(root).FirstOrDefault(p => p.Id.Equals(account.Uuid, StringComparison.OrdinalIgnoreCase))
                      ?? new AuthlibProfile(account.Uuid, account.Name);
        return new AuthlibAuthResult(
            profile.Name,
            profile.Id,
            accessToken,
            account.ClientToken,
            account.LoginName,
            account.Server,
            account.ServerName);
    }

    // ---------- authlib-injector 注入支持 ----------

    /// <summary>
    /// 确保 authlib-injector.jar 就绪并返回路径：读取 latest.json（官方源 + BMCLAPI 镜像），
    /// 与缓存版本不一致时重新下载。
    /// </summary>
    public async Task<string> EnsureAuthlibInjectorJarAsync(
        string cacheDirectory,
        DownloadClient client,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(cacheDirectory);
        const string latestUrl = "https://authlib-injector.yushi.moe/artifact/latest.json";
        var mirrorUrl = "https://bmclapi2.bangbang93.com/mirror/authlib-injector/artifact/latest.json";

        string latest;
        try
        {
            latest = await client.GetStringAsync([latestUrl, mirrorUrl], cancellationToken).ConfigureAwait(false);
        }
        catch (DownloadException ex)
        {
            throw new AuthlibException("下载 authlib-injector 元数据失败，请检查网络后重试", ex);
        }

        using var document = JsonDocument.Parse(latest);
        var version = document.RootElement.TryGetProperty("version", out var v) ? v.GetString() ?? "" : "";
        var artifactUrl = document.RootElement.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "";
        var sha256 = document.RootElement.TryGetProperty("checksums", out var c)
                     && c.TryGetProperty("sha256", out var s)
            ? s.GetString()
            : null;
        if (string.IsNullOrWhiteSpace(artifactUrl))
        {
            throw new AuthlibException("authlib-injector 元数据缺少下载地址");
        }

        var jarPath = Path.Combine(cacheDirectory, $"authlib-injector-{version}.jar");
        var versionMarker = jarPath + ".version";
        if (File.Exists(jarPath)
            && File.Exists(versionMarker)
            && (await File.ReadAllTextAsync(versionMarker, cancellationToken).ConfigureAwait(false)).Trim() == version)
        {
            return jarPath;
        }

        // 官方源优先，BMCLAPI 镜像兜底（同路径换主机）
        var urls = new List<string> { artifactUrl };
        if (artifactUrl.Contains("authlib-injector.yushi.moe", StringComparison.OrdinalIgnoreCase))
        {
            urls.Add(artifactUrl.Replace(
                "https://authlib-injector.yushi.moe",
                "https://bmclapi2.bangbang93.com/mirror/authlib-injector",
                StringComparison.OrdinalIgnoreCase));
        }

        await client.DownloadFileAsync(urls, jarPath, null, sha256, null, cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(versionMarker, version, cancellationToken).ConfigureAwait(false);
        _logger.Info($"authlib-injector 已就绪：{jarPath}");
        return jarPath;
    }

    /// <summary>抓取皮肤站根响应并 Base64（-Dauthlibinjector.yggdrasil.prefetched）。</summary>
    public async Task<string> GetPrefetchedAsync(string apiRoot, CancellationToken cancellationToken = default)
    {
        var body = await _http.GetStringAsync(apiRoot, cancellationToken).ConfigureAwait(false);
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(body));
    }

    // ---------- 内部 ----------

    private static HttpClient CreateClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(30)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) HYCL/1.0 (Hyplay Craft Launcher)");
        return client;
    }

    private static async Task<JsonElement> PostAsync(string url, object body, CancellationToken cancellationToken)
    {
        string text;
        try
        {
            using var content = new StringContent(
                JsonSerializer.Serialize(body),
                Encoding.UTF8,
                "application/json");
            using var response = await _http.PostAsync(url, content, cancellationToken).ConfigureAwait(false);
            text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new AuthlibException($"无法连接皮肤站（{url}）：{Describe(ex)}", ex);
        }

        try
        {
            var json = JsonDocument.Parse(text).RootElement.Clone();
            if (json.ValueKind == JsonValueKind.Object && json.TryGetProperty("error", out var error))
            {
                var message = json.TryGetProperty("errorMessage", out var m) ? m.GetString() : null;
                var isForbidden = error.GetString() == "ForbiddenOperationException";
                throw new AuthlibException(
                    isForbidden && !string.IsNullOrWhiteSpace(message) && message.Contains("credential", StringComparison.OrdinalIgnoreCase)
                        ? "用户名或密码错误"
                        : !string.IsNullOrWhiteSpace(message) ? message : $"皮肤站返回错误：{error}");
            }

            return json;
        }
        catch (JsonException ex)
        {
            throw new AuthlibException("皮肤站返回了无法解析的数据", ex);
        }
    }

    private static List<AuthlibProfile> ParseProfiles(JsonElement root)
    {
        var result = new List<AuthlibProfile>();
        if (root.TryGetProperty("availableProfiles", out var profiles) && profiles.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in profiles.EnumerateArray())
            {
                if (item.TryGetProperty("id", out var id))
                {
                    result.Add(new AuthlibProfile(
                        id.GetString() ?? "",
                        item.TryGetProperty("name", out var n) ? n.GetString() ?? "" : ""));
                }
            }
        }

        return result;
    }

    private static string StringOrThrow(JsonElement json, string field)
        => json.TryGetProperty(field, out var value) && value.GetString() is { } text && text.Length > 0
            ? text
            : throw new AuthlibException($"皮肤站响应缺少 {field}");

    private static string Describe(Exception? ex)
        => ex is null ? "未知原因" : ex.GetType() == typeof(TaskCanceledException) ? "连接超时" : ex.Message;

    private sealed record AuthlibAgent(string Name, int Version);

    private sealed record AuthlibAuthenticateBody(AuthlibAgent Agent, string Username, string Password, string ClientToken);

    private sealed record AuthlibRefreshBody(string AccessToken, string ClientToken, string? SelectedProfileId, string? SelectedProfileName);

    private sealed record AuthlibValidateBody(string AccessToken, string ClientToken);
}
