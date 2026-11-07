using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Hycl.Launcher.Core.Config;
using Hycl.Launcher.Core.Services;

namespace Hycl.Launcher.Core.Auth;

/// <summary>设备码起始响应（步骤 1）。</summary>
public sealed record MicrosoftDeviceCode(
    string DeviceCode,
    string UserCode,
    string VerificationUri,
    string VerificationUriComplete,
    int ExpiresInSeconds,
    int IntervalSeconds);

/// <summary>一次设备码轮询的结果。</summary>
public sealed record MicrosoftPollResult(
    bool IsPending,
    bool IsSlowDown,
    bool IsExpired,
    bool IsDenied,
    MicrosoftAuthResult? Result);

/// <summary>正版登录成功结果（步骤 6 之后）。</summary>
public sealed record MicrosoftAuthResult(
    string UserName,
    string Uuid,
    string AccessToken,
    string RefreshToken,
    string Xuid,
    DateTimeOffset ExpiresAt,
    string SkinUrl,
    string SkinModel = "",
    string CapeUrl = "");

/// <summary>正版登录失败（消息已按用户可读组织）。</summary>
public sealed class MicrosoftAuthException : Exception
{
    public MicrosoftAuthException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// 微软正版登录（设备码流，对应 PCL.Core MicrosoftProvider 的六步协议）：
/// 设备码 → 轮询令牌 → Xbox Live → XSTS → Minecraft 服务 → 权益与档案校验。
/// ClientId 一律使用 Hycl 自行注册的应用（<see cref="HyclSecrets.MicrosoftOAuthClientId"/>），
/// 绝不使用其他启动器的应用身份。
/// </summary>
public sealed class MicrosoftAuthService
{
    private const string DeviceEndpoint = "https://login.microsoftonline.com/consumers/oauth2/v2.0/devicecode";
    private const string TokenEndpoint = "https://login.microsoftonline.com/consumers/oauth2/v2.0/token";
    private const string XboxAuthEndpoint = "https://user.auth.xboxlive.com/user/authenticate";
    private const string XstsEndpoint = "https://xsts.auth.xboxlive.com/xsts/authorize";
    private const string MinecraftAuthEndpoint = "https://api.minecraftservices.com/authentication/login_with_xbox";
    private const string EntitlementsEndpoint = "https://api.minecraftservices.com/entitlements/mcstore";
    private const string ProfileEndpoint = "https://api.minecraftservices.com/minecraft/profile";
    private const string SessionServerEndpoint = "https://sessionserver.mojang.com/session/minecraft/profile/";

    private const string Scopes = "XboxLive.signin offline_access";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>日志脱敏：令牌/设备码等字段的值整体替换为 ***（含 UserTokens 数组）。</summary>
    private static readonly Regex SecretFieldRegex = new(
        "\"(access_token|refresh_token|token|identityToken|RpsTicket|UserTokens|device_code|user_code)\"\\s*:\\s*(\"[^\"]*\"|\\[[^\\]]*\\])",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>日志脱敏：任何超过 200 字符的字符串（JWT 等）只保留长度，避免完整令牌落盘。</summary>
    private static readonly Regex LongStringRegex = new("\"[^\"]{200,}\"", RegexOptions.Compiled);

    private static readonly HttpClient _http = CreateClient();

    private readonly LauncherLogger _logger;

    public MicrosoftAuthService(LauncherLogger logger)
    {
        _logger = logger;
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient(new SocketsHttpHandler
        {
            // 统一 HTTP/1.1：排除部分微软端点在 HTTP/2 下的握手/风控异常（与 DownloadClient 同策略）
            AutomaticDecompression = System.Net.DecompressionMethods.All
        })
        {
            Timeout = TimeSpan.FromSeconds(45),
            DefaultVersionPolicy = System.Net.Http.HttpVersionPolicy.RequestVersionExact,
            DefaultRequestVersion = System.Net.HttpVersion.Version11
        };
        // 注意：微软登录链路对浏览器型 UA 的容忍度未知，统一使用纯启动器标识
        client.DefaultRequestHeaders.UserAgent.ParseAdd("HYCL/1.0 (Hyplay Craft Launcher)");
        return client;
    }

    /// <summary>要使用的 ClientId；未配置时抛出用户可读异常。</summary>
    private string ClientId => HyclSecrets.HasMicrosoftOAuthClientId
        ? HyclSecrets.MicrosoftOAuthClientId
        : throw new MicrosoftAuthException("正版登录未配置：缺少 Microsoft OAuth ClientId");

    /// <summary>步骤 1：申请设备码，得到用户在浏览器中输入的代码。</summary>
    public async Task<MicrosoftDeviceCode> StartDeviceCodeAsync(CancellationToken cancellationToken = default)
    {
        var (status, body) = await PostFormWithStatusAsync(DeviceEndpoint, new Dictionary<string, string>
        {
            ["client_id"] = ClientId,
            ["scope"] = Scopes
        }, cancellationToken).ConfigureAwait(false);
        LogStep("步骤1（申请设备码）", status, body);
        if ((int)status != 200)
        {
            throw new MicrosoftAuthException(DescribeOAuthError("设备码申请失败", (int)status, body));
        }

        var json = Parse(body, "设备码响应");
        var deviceCode = Required(json, "device_code");
        return new MicrosoftDeviceCode(
            deviceCode,
            Required(json, "user_code"),
            Required(json, "verification_uri"),
            json.TryGetProperty("verification_uri_complete", out var complete) ? complete.GetString() ?? "" : "",
            json.TryGetProperty("expires_in", out var expiresIn) ? expiresIn.GetInt32() : 900,
            Math.Max(2, json.TryGetProperty("interval", out var interval) ? interval.GetInt32() : 5));
    }

    /// <summary>步骤 2：轮询一次令牌端点；玩家尚未在浏览器确认时返回 pending。</summary>
    public async Task<MicrosoftPollResult> PollOnceAsync(
        MicrosoftDeviceCode deviceCode,
        CancellationToken cancellationToken = default)
    {
        var (status, body) = await PostFormWithStatusAsync(TokenEndpoint, new Dictionary<string, string>
        {
            ["client_id"] = ClientId,
            ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
            ["device_code"] = deviceCode.DeviceCode
        }, cancellationToken).ConfigureAwait(false);

        if ((int)status == 200)
        {
            LogStep("步骤2（获取令牌）", status, body);
            var json = Parse(body, "令牌响应");
            var accessToken = Required(json, "access_token");
            var refreshToken = json.TryGetProperty("refresh_token", out var refresh) ? refresh.GetString() ?? "" : "";
            return new MicrosoftPollResult(false, false, false, false,
                await CompleteAsync(accessToken, refreshToken, cancellationToken).ConfigureAwait(false));
        }

        // 轮询等待/过期属于正常状态，只记状态码与 OAuth 错误名，避免刷屏
        var error = TryReadError(body);
        _logger.Info($"正版登录 步骤2（轮询令牌）：HTTP {(int)status}{(error.Length > 0 ? $" {error}" : "")}");
        if (error.Length == 0)
        {
            throw new MicrosoftAuthException(
                $"令牌轮询失败（HTTP {(int)status}）：{RedactBody(body)}");
        }

        return error switch
        {
            "authorization_pending" => new MicrosoftPollResult(true, false, false, false, null),
            "slow_down" => new MicrosoftPollResult(false, true, false, false, null),
            "expired_token" => new MicrosoftPollResult(false, false, true, false, null),
            "authorization_declined" => new MicrosoftPollResult(false, false, false, true, null),
            _ => throw new MicrosoftAuthException(
                $"登录被微软拒绝（HTTP {(int)status}）：{error}{DescribeOAuthDescription(body)}")
        };
    }

    /// <summary>用 refresh_token 换新令牌并完成后续链路（启动前的静默续期）。</summary>
    public async Task<MicrosoftAuthResult> RefreshAsync(string refreshToken, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(refreshToken);
        var (status, body) = await PostFormWithStatusAsync(TokenEndpoint, new Dictionary<string, string>
        {
            ["client_id"] = ClientId,
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["scope"] = Scopes
        }, cancellationToken).ConfigureAwait(false);

        if ((int)status != 200)
        {
            LogStep("步骤2（刷新令牌）", status, body);
            var error = DescribeAadstsError(body);
            _logger.Warn($"正版令牌刷新失败（HTTP {(int)status}）：{error}");
            throw new MicrosoftAuthException(
                "正版登录已过期，请重新登录账号（微软拒绝了自动续期）：" + error);
        }

        LogStep("步骤2（刷新令牌）", status, body);
        var json = Parse(body, "令牌响应");
        return await CompleteAsync(
                Required(json, "access_token"),
                json.TryGetProperty("refresh_token", out var refresh) ? refresh.GetString() ?? refreshToken : refreshToken,
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>步骤 3~6：XBL → XSTS → Minecraft 令牌 → 权益与档案。</summary>
    public async Task<MicrosoftAuthResult> CompleteAsync(
        string oauthAccessToken,
        string refreshToken,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(oauthAccessToken);

        // 3) Xbox Live 用户令牌（RpsTicket 必须带 d= 前缀）
        var (xblStatus, xblBody) = await PostJsonWithStatusAsync(
                XboxAuthEndpoint,
                $$"""
                {"Properties":{"AuthMethod":"RPS","SiteName":"user.auth.xboxlive.com","RpsTicket":"d={{oauthAccessToken}}"},"RelyingParty":"http://auth.xboxlive.com","TokenType":"JWT"}
                """,
                cancellationToken).ConfigureAwait(false);
        LogStep("步骤3（Xbox Live 用户令牌）", xblStatus, xblBody);
        if ((int)xblStatus != 200)
        {
            throw new MicrosoftAuthException(
                $"Xbox Live 认证失败（HTTP {(int)xblStatus}）：{DescribeXblError(xblBody)}");
        }

        var xbl = Parse(xblBody, "Xbox Live 响应");
        var xblToken = Required(xbl, "Token");

        // 4) XSTS 令牌（错误码转用户可读提示）
        var (xstsStatus, xstsBody) = await PostJsonWithStatusAsync(
                XstsEndpoint,
                $$"""
                {"Properties":{"SandboxId":"RETAIL","UserTokens":["{{xblToken}}"]},"RelyingParty":"rp://api.minecraftservices.com/","TokenType":"JWT"}
                """,
                cancellationToken).ConfigureAwait(false);
        LogStep("步骤4（XSTS 授权）", xstsStatus, xstsBody);
        if ((int)xstsStatus != 200)
        {
            throw new MicrosoftAuthException(DescribeXstsError(xstsBody));
        }

        var xsts = Parse(xstsBody, "XSTS 响应");
        var xstsToken = Required(xsts, "Token");
        var userHash = "";
        if (xsts.TryGetProperty("DisplayClaims", out var displayClaims)
            && displayClaims.TryGetProperty("xui", out var xui)
            && xui.ValueKind == JsonValueKind.Array
            && xui.GetArrayLength() > 0
            && xui[0].TryGetProperty("uhs", out var uhs))
        {
            userHash = uhs.GetString() ?? "";
        }

        if (string.IsNullOrWhiteSpace(userHash))
        {
            throw new MicrosoftAuthException("XSTS 响应缺少用户标识（uhs），请重试登录");
        }

        // 5) Minecraft 服务令牌
        // login_with_xbox 会校验应用的注册配置：账户类型为「仅个人 Microsoft 账户」的应用
        // 被拒绝（403 Invalid app registration），必须使用「任何组织目录 + 个人账户」的应用
        string mcToken;
        string tokenType;
        DateTimeOffset expiresAt;
        var (mcStatus, mcBody) = await PostJsonWithStatusAsync(
                MinecraftAuthEndpoint,
                $$"""
                {"identityToken":"XBL3.0 x={{userHash}};{{xstsToken}}"}
                """,
                cancellationToken).ConfigureAwait(false);
        LogStep("步骤5（Minecraft 服务令牌）", mcStatus, mcBody);
        if ((int)mcStatus != 200)
        {
            if (mcBody.Contains("Invalid app registration", StringComparison.OrdinalIgnoreCase))
            {
                throw new MicrosoftAuthException(
                    "微软拒绝了本次登录：Azure 应用的账户类型配置不正确。" +
                    "请到 Azure「应用注册」→ 清单，把 signInAudience 改为 \"AzureADAndPersonalMicrosoftAccount\"" +
                    "（或重新注册应用并选择「任何组织目录中的账户和个人 Microsoft 账户」，同时重新开启公共客户端流），详见 aka.ms/AppRegInfo");
            }

            throw new MicrosoftAuthException(
                $"Minecraft 服务认证失败（HTTP {(int)mcStatus}）：{RedactBody(mcBody)}");
        }

        var mc = Parse(mcBody, "Minecraft 令牌响应");
        mcToken = Required(mc, "access_token");
        tokenType = mc.TryGetProperty("token_type", out var typeNode) && typeNode.GetString() is { Length: > 0 } t
            ? t
            : "Bearer";
        var expiresIn = mc.TryGetProperty("expires_in", out var expiresInNode) ? expiresInNode.GetInt32() : 86400;
        expiresAt = DateTimeOffset.UtcNow.AddSeconds(expiresIn);

        // 6) 权益校验（拥有游戏才能进入）
        var (entStatus, entitlements) = await GetWithAuthAsync(EntitlementsEndpoint, tokenType, mcToken, cancellationToken)
            .ConfigureAwait(false);
        LogStep("步骤6（游戏权益校验）", entStatus, entitlements);
        if ((int)entStatus != 200)
        {
            throw new MicrosoftAuthException(
                $"校验 Minecraft 权益失败（HTTP {(int)entStatus}）：{RedactBody(entitlements)}");
        }

        using (var document = JsonDocument.Parse(entitlements))
        {
            var hasGame = document.RootElement.TryGetProperty("items", out var items)
                          && items.ValueKind == JsonValueKind.Array
                          && items.EnumerateArray().Any(item =>
                          {
                              var name = item.TryGetProperty("name", out var n) ? n.GetString() : null;
                              return name is "product_minecraft" or "game_minecraft" or "minecraft";
                          });
            if (!hasGame)
            {
                throw new MicrosoftAuthException("该微软账号没有购买 Minecraft（Java 版），无法正版登录");
            }
        }

        // 6b) 档案（玩家名 + UUID + XUID）；必须带 Bearer 令牌，否则接口固定返回 401
        var (profileStatus, profileBody) = await GetWithAuthAsync(ProfileEndpoint, tokenType, mcToken, cancellationToken)
            .ConfigureAwait(false);
        LogStep("步骤6（读取 Minecraft 档案）", profileStatus, profileBody);
        if ((int)profileStatus == 404)
        {
            throw new MicrosoftAuthException("该微软账号还没有创建 Minecraft 档案，请先用该账号启动一次官方启动器");
        }

        if ((int)profileStatus != 200)
        {
            throw new MicrosoftAuthException(
                $"读取 Minecraft 档案失败（HTTP {(int)profileStatus}）：{RedactBody(profileBody)}");
        }

        var profile = Parse(profileBody, "档案响应");
        var userName = Required(profile, "name");
        var uuid = Required(profile, "id");
        var xuid = ExtractXuid(xstsBody);
        var skinUrl = ExtractActiveSkinUrl(profile);
        var skinModel = ExtractActiveSkinModel(profile);
        var (capeUrl, capeAlias) = ExtractActiveCape(profile);
        if (capeUrl.Length > 0)
        {
            _logger.Info($"正版档案披风：{capeAlias}（{userName}）");
        }

        _logger.Info($"正版登录成功：{userName}（{uuid}）");
        return new MicrosoftAuthResult(
            userName, uuid, mcToken, refreshToken, xuid, expiresAt, skinUrl, skinModel, capeUrl);
    }

    /// <summary>从档案响应的 skins 数组中取当前激活皮肤的贴图 URL。</summary>
    private static string ExtractActiveSkinUrl(JsonElement profile)
    {
        try
        {
            if (profile.TryGetProperty("skins", out var skins) && skins.ValueKind == JsonValueKind.Array)
            {
                foreach (var skin in skins.EnumerateArray())
                {
                    var state = skin.TryGetProperty("state", out var s) ? s.GetString() : null;
                    var url = skin.TryGetProperty("url", out var u) ? u.GetString() : null;
                    if (IsActive(state) && !string.IsNullOrWhiteSpace(url))
                    {
                        return url;
                    }
                }
            }
        }
        catch (JsonException)
        {
        }

        return "";
    }

    /// <summary>当前激活皮肤的模型（SLIM → slim，其余为经典模型）。</summary>
    private static string ExtractActiveSkinModel(JsonElement profile)
    {
        try
        {
            if (profile.TryGetProperty("skins", out var skins) && skins.ValueKind == JsonValueKind.Array)
            {
                foreach (var skin in skins.EnumerateArray())
                {
                    var state = skin.TryGetProperty("state", out var s) ? s.GetString() : null;
                    var variant = skin.TryGetProperty("variant", out var v) ? v.GetString() : null;
                    if (IsActive(state) && variant?.Equals("SLIM", StringComparison.OrdinalIgnoreCase) == true)
                    {
                        return "slim";
                    }
                }
            }
        }
        catch (JsonException)
        {
        }

        return "";
    }

    /// <summary>
    /// 从档案响应的 capes 数组中取当前激活披风（state == ACTIVE）的贴图 URL 与名称。
    /// 未拥有披风或没有激活项时返回空值——不能把 INACTIVE 披风或空 url 当成已装备。
    /// </summary>
    private static (string Url, string Alias) ExtractActiveCape(JsonElement profile)
    {
        try
        {
            if (profile.TryGetProperty("capes", out var capes) && capes.ValueKind == JsonValueKind.Array)
            {
                foreach (var cape in capes.EnumerateArray())
                {
                    var state = cape.TryGetProperty("state", out var s) ? s.GetString() : null;
                    var url = cape.TryGetProperty("url", out var u) ? u.GetString() : null;
                    if (IsActive(state) && !string.IsNullOrWhiteSpace(url))
                    {
                        var alias = cape.TryGetProperty("alias", out var a) ? a.GetString() ?? "" : "";
                        return (url, alias);
                    }
                }
            }
        }
        catch (JsonException)
        {
        }

        return ("", "");
    }

    private static bool IsActive(string? state)
        => state?.Equals("ACTIVE", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>
    /// 从 Mojang sessionserver 读取档案纹理（角色名 / 皮肤 / 披风 / 模型）。
    /// 与皮肤站的响应结构一致，供「刷新」按钮重新拉取资料；失败静默返回 null。
    /// </summary>
    public async Task<MinecraftTextures?> GetProfileTexturesAsync(
        string uuid,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(uuid))
        {
            return null;
        }

        try
        {
            var response = await _http
                .GetAsync(SessionServerEndpoint + uuid + "?unsigned=false", cancellationToken)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.Warn($"正版档案接口返回 HTTP {(int)response.StatusCode}");
                return null;
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return AuthlibAuthService.ParseProfile(body);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or FormatException or TaskCanceledException)
        {
            _logger.Warn($"获取正版档案纹理失败（忽略）：{ex.Message}");
            return null;
        }
    }

    // ---------- HTTP 辅助 ----------

    private static async Task<(System.Net.HttpStatusCode Status, string Body)> PostFormWithStatusAsync(
        string url,
        IReadOnlyDictionary<string, string> fields,
        CancellationToken cancellationToken)
    {
        using var content = new FormUrlEncodedContent(fields);
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        return (response.StatusCode, await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
    }

    private static async Task<(System.Net.HttpStatusCode Status, string Body)> PostJsonWithStatusAsync(
        string url,
        string json,
        CancellationToken cancellationToken)
    {
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        return (response.StatusCode, await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
    }

    /// <summary>带 Bearer 鉴权的 GET；返回状态码与响应体，由调用方统一记录日志并转述错误。</summary>
    private static async Task<(System.Net.HttpStatusCode Status, string Body)> GetWithAuthAsync(
        string url,
        string tokenType,
        string accessToken,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Authorization = new AuthenticationHeaderValue(
            string.IsNullOrWhiteSpace(tokenType) ? "Bearer" : tokenType,
            accessToken);
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        return (response.StatusCode, await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
    }

    // ---------- 日志与脱敏 ----------

    /// <summary>记录一步的真实 HTTP 状态码与脱敏后的响应体，便于定位断点。</summary>
    private void LogStep(string step, System.Net.HttpStatusCode status, string body)
    {
        var text = RedactBody(body);
        _logger.Info($"正版登录 {step}：HTTP {(int)status}{(text.Length == 0 ? "" : "，响应 " + text)}");
    }

    /// <summary>脱敏：令牌/设备码字段替换为 ***，超长字符串只保留长度，并压成单行、限制总长度。</summary>
    private static string RedactBody(string body)
    {
        if (string.IsNullOrEmpty(body))
        {
            return "";
        }

        var text = SecretFieldRegex.Replace(body, match => $"\"{match.Groups[1].Value}\":\"***\"");
        text = LongStringRegex.Replace(text, match => $"\"…（已省略 {match.Length - 2} 字符）\"");
        text = text.Replace('\r', ' ').Replace('\n', ' ');
        return text.Length <= 500 ? text : text[..500] + "…";
    }

    private static string TryReadString(string body, string field)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? ""
                : "";
        }
        catch (JsonException)
        {
            return "";
        }
    }

    private static string TryReadError(string body) => TryReadString(body, "error");

    private static string DescribeOAuthDescription(string body)
    {
        var description = TryReadString(body, "error_description");
        return description.Length == 0 ? "" : $"（{RedactBody(description)}）";
    }

    private static string DescribeOAuthError(string stage, int status, string body)
    {
        var error = TryReadError(body);
        var head = error.Length > 0 ? error : RedactBody(body);
        return $"{stage}（HTTP {status}）：{head}{DescribeOAuthDescription(body)}";
    }

    private static string DescribeXblError(string body)
    {
        var message = TryReadString(body, "Message");
        if (message.Length == 0)
        {
            message = TryReadError(body);
        }

        if (message.Length > 0)
        {
            return RedactBody(message);
        }

        var fallback = RedactBody(body);
        return fallback.Length > 0 ? fallback : "响应体为空，通常表示 OAuth 访问令牌无效或已过期";
    }

    // ---------- 解析与错误转述 ----------

    private static JsonElement Parse(string body, string what)
    {
        try
        {
            return JsonDocument.Parse(body).RootElement.Clone();
        }
        catch (JsonException ex)
        {
            throw new MicrosoftAuthException($"{what}不是有效的 JSON（{ex.Message}）", ex);
        }
    }

    private static string Required(JsonElement json, string field)
        => json.TryGetProperty(field, out var value) && value.GetString() is { } text && text.Length > 0
            ? text
            : throw new MicrosoftAuthException($"登录响应缺少 {field}，请重试");

    private static string ExtractXuid(string xstsBody)
    {
        try
        {
            using var document = JsonDocument.Parse(xstsBody);
            if (document.RootElement.TryGetProperty("DisplayClaims", out var claims)
                && claims.TryGetProperty("xui", out var xui)
                && xui.ValueKind == JsonValueKind.Array
                && xui.GetArrayLength() > 0
                && xui[0].TryGetProperty("xid", out var xid))
            {
                return xid.GetString() ?? "";
            }
        }
        catch (JsonException)
        {
        }

        return "";
    }

    private static string DescribeXstsError(string body)
    {
        string? code = null;
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("XErr", out var xerr))
            {
                code = xerr.ValueKind == JsonValueKind.Number
                    ? xerr.GetRawText()
                    : xerr.GetString();
            }
        }
        catch (JsonException)
        {
        }

        // 常见 XSTS 错误码转成可操作的中文提示；未知码保留原始 XErr 便于排查
        var hint = code switch
        {
            "2148916233" => "该微软账号还没有 Xbox Live 档案，请先在 https://www.xbox.com 登录并创建 Xbox 档案后重试",
            "2148916235" => "该账号所在地区不支持 Xbox Live，无法正版登录",
            "2148916236" or "2148916237" => "该账号需要通过成人验证（所在地区要求）后才能登录 Xbox",
            "2148916238" => "这是未成年账号，需要先由家庭组管理员把该账号加入家庭组并批准，再重试",
            "2148910055" => "该账号无法登录 Xbox：需要在 Xbox 官网完成成人验证/家庭组设置后重试",
            "2148910056" => "该微软账号没有 Xbox Live 资料（所在地区不支持 Xbox 服务），无法正版登录",
            "2148910023" => "该地区被 Xbox 服务禁止，无法正版登录",
            "2148910058" or "2148910059" => "这是儿童账号：请先在家长设置中允许加入 Xbox，再重试",
            _ => "Xbox 登录失败，请稍后重试"
        };
        var codeText = code is null ? "" : $"（XErr {code}）";
        var detail = TryReadString(body, "Message");
        var detailText = detail.Length > 0 ? $"：{RedactBody(detail)}" : "";
        return hint + codeText + detailText;
    }

    private static string DescribeAadstsError(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("error_description", out var description))
            {
                var text = description.GetString() ?? "";
                return text.Contains("expired", StringComparison.OrdinalIgnoreCase) ? "设备码已过期" : text;
            }
        }
        catch (JsonException)
        {
        }

        return "未知原因";
    }
}
