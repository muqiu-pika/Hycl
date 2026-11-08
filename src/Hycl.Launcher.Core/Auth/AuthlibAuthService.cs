using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Hycl.Launcher.Core.Download;
using Hycl.Launcher.Core.Services;

namespace Hycl.Launcher.Core.Auth;

/// <summary>皮肤站（Yggdrasil）服务信息：API 根地址与站点名。</summary>
public sealed record AuthlibServerInfo(string ApiRoot, string ServerName);

/// <summary>Yggdrasil 角色。</summary>
public sealed record AuthlibProfile(string Id, string Name);

/// <summary>档案接口解析出的纹理信息：角色名 / 皮肤 / 披风 / 模型（slim 或空）。</summary>
public sealed record MinecraftTextures(string Name, string SkinUrl, string CapeUrl, string Model);

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
public sealed partial class AuthlibAuthService
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
                using var request = new HttpRequestMessage(HttpMethod.Get, candidate);
                using var response = await SendWithRedirectAsync(request, cancellationToken).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                using var document = JsonDocument.Parse(body);
                var serverName = "";
                if (document.RootElement.TryGetProperty("meta", out var meta)
                    && meta.TryGetProperty("serverName", out var name))
                {
                    serverName = name.GetString() ?? "";
                }

                // 记录跟随重定向后的最终地址：后续 POST 直接用它，避免每次都经历重定向
                var finalRoot = (response.RequestMessage?.RequestUri ?? request.RequestUri)
                    ?.ToString().TrimEnd('/') ?? candidate;
                _logger.Info($"皮肤站探测成功：{finalRoot}（{serverName}）");
                return new AuthlibServerInfo(finalRoot, serverName);
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
                AuthlibJsonContext.Default.AuthlibAuthenticateBody,
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
                new AuthlibRefreshBody(accessToken, clientToken, new AuthlibProfileRef(profile.Id, profile.Name)),
                AuthlibJsonContext.Default.AuthlibRefreshBody,
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
            using var request = new HttpRequestMessage(HttpMethod.Post, apiRoot.TrimEnd('/') + "/authserver/validate")
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(
                        new AuthlibValidateBody(accessToken, clientToken),
                        AuthlibJsonContext.Default.AuthlibValidateBody),
                    Encoding.UTF8,
                    "application/json")
            };
            using var response = await SendWithRedirectAsync(request, cancellationToken).ConfigureAwait(false);
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
                new AuthlibRefreshBody(account.AccessToken, account.ClientToken, null),
                AuthlibJsonContext.Default.AuthlibRefreshBody,
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

    // PCL-CE 的做法：latest.json 中取 download_url（旧接口曾用 url），
    // 官方源失败时用 BMCLAPI 的 /mirrors/authlib-injector 镜像；
    // 元数据完全不可用时仍按固定规则拼出 jar 地址，避免直接拦住启动。
    private const string AuthlibOfficialRoot = "https://authlib-injector.yushi.moe/artifact";
    private const string AuthlibMirrorRoot = "https://bmclapi2.bangbang93.com/mirrors/authlib-injector/artifact";
    private const string AuthlibFallbackVersion = "1.2.8";
    private const int AuthlibFallbackBuild = 56;
    private const string AuthlibFallbackSha256 = "9c7f4343e6c82034958ffb48c14a2cb0c85928be7283103ce17da00c6d5a7b10";

    /// <summary>
    /// 确保 authlib-injector.jar 就绪并返回路径：读取 latest.json（官方源 + BMCLAPI 镜像），
    /// 元数据不可用时回退到固定版本规则；与缓存版本不一致时重新下载。
    /// </summary>
    public async Task<string> EnsureAuthlibInjectorJarAsync(
        string cacheDirectory,
        DownloadClient client,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(cacheDirectory);

        var version = AuthlibFallbackVersion;
        var build = AuthlibFallbackBuild;
        var sha256 = AuthlibFallbackSha256;
        var artifactUrl = "";
        var metadataError = "";

        try
        {
            var latest = await client.GetStringAsync(
                [$"{AuthlibOfficialRoot}/latest.json", $"{AuthlibMirrorRoot}/latest.json"],
                cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(latest);
            var root = document.RootElement;
            version = root.TryGetProperty("version", out var versionElement) ? versionElement.GetString() ?? "" : "";
            build = root.TryGetProperty("build_number", out var buildElement) && buildElement.TryGetInt32(out var parsedBuild)
                ? parsedBuild
                : 0;
            artifactUrl = root.TryGetProperty("download_url", out var downloadElement)
                ? downloadElement.GetString() ?? ""
                : "";
            if (string.IsNullOrWhiteSpace(artifactUrl))
            {
                // 旧版 latest.json 字段名为 url
                artifactUrl = root.TryGetProperty("url", out var urlElement) ? urlElement.GetString() ?? "" : "";
            }

            sha256 = root.TryGetProperty("checksums", out var checksums)
                      && checksums.TryGetProperty("sha256", out var shaElement)
                ? shaElement.GetString() ?? ""
                : "";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            metadataError = ex.Message;
            _logger.Warn($"authlib-injector 元数据获取失败（官方源与 BMCLAPI 镜像均不可用）：{ex.Message}");
        }

        if (string.IsNullOrWhiteSpace(version)
            || version.Length > 50
            || version.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            version = AuthlibFallbackVersion;
        }

        if (build <= 0)
        {
            build = AuthlibFallbackBuild;
        }

        if (string.IsNullOrWhiteSpace(sha256))
        {
            sha256 = AuthlibFallbackSha256;
        }

        var officialJar = NormalizeArtifactUrl(artifactUrl, build, version, useMirror: false);
        var mirrorJar = NormalizeArtifactUrl(artifactUrl, build, version, useMirror: true);
        _logger.Info($"authlib-injector 目标版本 {version}（build {build}），候选地址：{officialJar} | {mirrorJar}");

        var jarPath = Path.Combine(cacheDirectory, $"authlib-injector-{version}.jar");
        var versionMarker = jarPath + ".version";
        if (File.Exists(jarPath)
            && File.Exists(versionMarker)
            && (await File.ReadAllTextAsync(versionMarker, cancellationToken).ConfigureAwait(false)).Trim() == version)
        {
            return jarPath;
        }

        try
        {
            await client
                .DownloadFileAsync([officialJar, mirrorJar], jarPath, null, sha256, null, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is DownloadException or HttpRequestException or IOException or UnauthorizedAccessException)
        {
            var metadataNote = metadataError.Length == 0
                ? ""
                : $"（元数据获取失败：{metadataError}，已改用固定版本 {AuthlibFallbackVersion}）";
            throw new AuthlibException(
                $"authlib-injector jar 下载失败{metadataNote}：已尝试官方源 {officialJar} 与 BMCLAPI 镜像 {mirrorJar}；{ex.Message}",
                ex);
        }

        await File.WriteAllTextAsync(versionMarker, version, cancellationToken).ConfigureAwait(false);
        _logger.Info($"authlib-injector 已就绪：{jarPath}");
        return jarPath;
    }

    /// <summary>
    /// 规整 jar 下载地址：优先用元数据里的 download_url；缺失时按版本/build 拼固定规则，
    /// 并把 BMCLAPI 镜像与官方源各出一份。
    /// </summary>
    private static string NormalizeArtifactUrl(string artifactUrl, int build, string version, bool useMirror)
    {
        var url = artifactUrl;
        if (string.IsNullOrWhiteSpace(url))
        {
            url = $"{AuthlibOfficialRoot}/{build}/authlib-injector-{version}.jar";
        }

        if (url.Contains("bmclapi2.bangbang93.com/mirrors/authlib-injector", StringComparison.OrdinalIgnoreCase))
        {
            url = url.Replace(
                "https://bmclapi2.bangbang93.com/mirrors/authlib-injector",
                "https://authlib-injector.yushi.moe",
                StringComparison.OrdinalIgnoreCase);
        }

        return useMirror
            ? url.Replace(
                "https://authlib-injector.yushi.moe",
                "https://bmclapi2.bangbang93.com/mirrors/authlib-injector",
                StringComparison.OrdinalIgnoreCase)
            : url;
    }

    /// <summary>抓取皮肤站根响应并 Base64（-Dauthlibinjector.yggdrasil.prefetched）。</summary>
    public async Task<string> GetPrefetchedAsync(string apiRoot, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, apiRoot);
        using var response = await SendWithRedirectAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(body));
    }

    /// <summary>
    /// 从皮肤站 sessionserver 取玩家当前皮肤 URL（失败静默返回 null，皮肤非关键数据）。
    /// </summary>
    public async Task<string?> TryGetSkinUrlAsync(
        string apiRoot,
        string uuid,
        CancellationToken cancellationToken = default)
        => (await TryGetProfileAsync(apiRoot, uuid, null, cancellationToken).ConfigureAwait(false))?.SkinUrl;

    /// <summary>
    /// 读取皮肤站档案（角色名 / 皮肤 / 披风 / 模型）。档案接口是公开的，
    /// 令牌仅用于让支持鉴权的站点返回当前登录身份下的数据；失败静默返回 null。
    /// </summary>
    public async Task<MinecraftTextures?> TryGetProfileAsync(
        string apiRoot,
        string uuid,
        string? accessToken = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var url = apiRoot.TrimEnd('/') + "/sessionserver/session/minecraft/profile/" + uuid + "?unsigned=false";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            // 皮肤站纹理地址在 LittleSkin 等站点可直接匿名访问；带上 API 地址头便于其他站点识别来源
            request.Headers.TryAddWithoutValidation("x-authlib-injector-api-location", apiRoot.TrimEnd('/'));
            if (!string.IsNullOrWhiteSpace(accessToken))
            {
                request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + accessToken);
            }

            using var response = await SendWithRedirectAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.Warn($"皮肤档案接口返回 HTTP {(int)response.StatusCode}：{url}");
                return null;
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var textures = ParseProfile(body);
            if (textures is null)
            {
                _logger.Warn("皮肤档案缺少 textures/SKIN（账号可能未设置自定义皮肤）");
            }

            return textures;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or FormatException or TaskCanceledException)
        {
            _logger.Warn($"获取皮肤站皮肤失败（忽略）：{ex.Message}");
        }

        return null;
    }

    /// <summary>
    /// 解析 sessionserver 档案响应：根节点的 name + properties[textures].value（Base64 JSON）。
    /// 皮肤站与 Mojang 的响应结构一致，正版刷新也复用此解析。
    /// </summary>
    public static MinecraftTextures? ParseProfile(string body)
    {
        using var document = JsonDocument.Parse(body);
        var name = document.RootElement.TryGetProperty("name", out var nameNode) ? nameNode.GetString() ?? "" : "";
        if (!document.RootElement.TryGetProperty("properties", out var properties)
            || properties.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var property in properties.EnumerateArray())
        {
            if ((property.TryGetProperty("name", out var n) ? n.GetString() : null) != "textures")
            {
                continue;
            }

            var value = property.TryGetProperty("value", out var v) ? v.GetString() : null;
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            var decoded = JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(value)));
            if (!decoded.RootElement.TryGetProperty("textures", out var textures))
            {
                return null;
            }

            var skinUrl = "";
            var model = "";
            if (textures.TryGetProperty("SKIN", out var skin))
            {
                skinUrl = skin.TryGetProperty("url", out var skinUrlNode) ? skinUrlNode.GetString() ?? "" : "";
                if (skin.TryGetProperty("metadata", out var metadata)
                    && metadata.TryGetProperty("model", out var modelNode))
                {
                    model = modelNode.GetString() ?? "";
                }
            }

            var capeUrl = "";
            if (textures.TryGetProperty("CAPE", out var cape))
            {
                capeUrl = cape.TryGetProperty("url", out var capeUrlNode) ? capeUrlNode.GetString() ?? "" : "";
            }

            return new MinecraftTextures(name, skinUrl, capeUrl, model);
        }

        return null;
    }

    // ---------- 内部 ----------

    private static HttpClient CreateClient()
    {
        // 关键：禁用自动重定向。.NET 对 301/302 的 POST 会改写成 GET 并丢弃请求体，
        // 皮肤站（littleskin.cn 有重定向）会把无 body 的 GET 当空模型，
        // 返回 "username should not be empty ... agent must be an object"。
        // 改为手动跟随重定向并保留方法与请求体（与 DownloadClient.SendWithRedirectAsync 同方案）。
        var client = new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromSeconds(20)
        })
        {
            Timeout = TimeSpan.FromSeconds(30)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) HYCL/1.0 (Hyplay Craft Launcher)");
        return client;
    }

    /// <summary>
    /// 发送请求并手动跟随重定向（最多 5 跳，保留方法、请求体与 Content 头）。
    /// </summary>
    private static async Task<HttpResponseMessage> SendWithRedirectAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        for (var hops = 0;
             (int)response.StatusCode is 301 or 302 or 307 or 308
             && response.Headers.Location is { } location
             && hops < 5;
             hops++)
        {
            var currentUri = request.RequestUri
                             ?? throw new AuthlibException("请求缺少地址");
            var nextUri = location.IsAbsoluteUri ? location : new Uri(currentUri, location);
            var method = request.Method;
            var content = request.Content;
            // 先摘掉 Content 再 Dispose：HttpRequestMessage.Dispose 会连带释放请求体，
            // 而重定向后的新请求还要继续用它（307/308 必须保留 body）。
            request.Content = null;
            request.Dispose();
            response.Dispose();
            request = new HttpRequestMessage(method, nextUri) { Content = content };
            response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }

        return response;
    }

    private static async Task<JsonElement> PostAsync<T>(
        string url,
        T body,
        JsonTypeInfo<T> typeInfo,
        CancellationToken cancellationToken)
    {
        string text;
        int statusCode;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(
                    // 关键：Yggdrasil 规范字段名为小写 camelCase，且 agent 必须是对象；
                    // 记录类型上的 JsonPropertyName 保证序列化形态与规范一致。
                    JsonSerializer.Serialize(body, typeInfo),
                    Encoding.UTF8,
                    "application/json")
            };
            using var response = await SendWithRedirectAsync(request, cancellationToken).ConfigureAwait(false);
            statusCode = (int)response.StatusCode;
            text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new AuthlibException($"无法连接皮肤站（{url}）：{Describe(ex)}", ex);
        }

        JsonElement json;
        try
        {
            json = JsonDocument.Parse(text).RootElement.Clone();
        }
        catch (JsonException ex)
        {
            throw new AuthlibException(
                text.Length == 0
                    ? $"皮肤站返回错误（HTTP {statusCode}）"
                    : $"皮肤站返回了无法解析的数据（HTTP {statusCode}）",
                ex);
        }

        if (json.ValueKind == JsonValueKind.Object && json.TryGetProperty("error", out var error))
        {
            throw new AuthlibException(DescribeServerError(
                error.GetString(),
                json.TryGetProperty("errorMessage", out var messageNode) ? messageNode.GetString() : null,
                statusCode));
        }

        if (statusCode is < 200 or >= 300)
        {
            throw new AuthlibException($"皮肤站返回错误（HTTP {statusCode}）");
        }

        return json;
    }

    /// <summary>把 Yggdrasil 错误类型/服务端消息整理成玩家可读的中文提示。</summary>
    private static string DescribeServerError(string? error, string? message, int statusCode)
    {
        var type = error ?? "";
        if (type == "ForbiddenOperationException")
        {
            // 已知的英文规范提示优先本地化；皮肤站自带的中文消息原样保留
            if (message?.Contains("Incorrect username or password", StringComparison.OrdinalIgnoreCase) == true)
            {
                return "用户名或密码错误";
            }

            if (message?.Contains("Invalid token", StringComparison.OrdinalIgnoreCase) == true)
            {
                return "登录凭据已失效，请重新登录";
            }

            return !string.IsNullOrWhiteSpace(message) ? message : "用户名或密码错误";
        }

        if (type == "IllegalArgumentException")
        {
            // 形如 "username: username should not be empty, agent: agent must be an object"
            // 属于请求体校验失败，不是玩家输入问题，给可读提示而不是原样甩给玩家。
            return "皮肤站拒绝了登录请求（请求格式不正确），请检查皮肤站地址是否正确";
        }

        if (!string.IsNullOrWhiteSpace(message))
        {
            return message;
        }

        return type.Length > 0
            ? $"皮肤站返回错误：{type}（HTTP {statusCode}）"
            : $"皮肤站返回错误（HTTP {statusCode}）";
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

    // Yggdrasil 规范要求字段名为 camelCase，agent/selectedProfile 必须是嵌套对象。
    private sealed record AuthlibAgent(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("version")] int Version);

    private sealed record AuthlibAuthenticateBody(
        [property: JsonPropertyName("agent")] AuthlibAgent Agent,
        [property: JsonPropertyName("username")] string Username,
        [property: JsonPropertyName("password")] string Password,
        [property: JsonPropertyName("clientToken")] string ClientToken,
        [property: JsonPropertyName("requestUser")] bool RequestUser = false);

    private sealed record AuthlibProfileRef(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("name")] string Name);

    private sealed record AuthlibRefreshBody(
        [property: JsonPropertyName("accessToken")] string AccessToken,
        [property: JsonPropertyName("clientToken")] string ClientToken,
        [property: JsonPropertyName("selectedProfile"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] AuthlibProfileRef? SelectedProfile);

    private sealed record AuthlibValidateBody(
        [property: JsonPropertyName("accessToken")] string AccessToken,
        [property: JsonPropertyName("clientToken")] string ClientToken);

    /// <summary>
    /// Yggdrasil 请求体的 source-gen 元数据：私有 record 经嵌套 context 收录，支持裁剪。
    /// 所有字段均带 [JsonPropertyName]，输出形态与反射序列化一致。
    /// </summary>
    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
    [JsonSerializable(typeof(AuthlibAuthenticateBody))]
    [JsonSerializable(typeof(AuthlibRefreshBody))]
    [JsonSerializable(typeof(AuthlibValidateBody))]
    private sealed partial class AuthlibJsonContext : JsonSerializerContext;
}
