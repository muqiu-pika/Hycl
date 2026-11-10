using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Hycl.Launcher.Core.Services;

namespace Hycl.Launcher.Core.Auth;

/// <summary>
/// 离线账号的本地 Yggdrasil 皮肤服务：给 authlib-injector 提供 profile/textures 端点，
/// 让离线自定义皮肤在游戏内真正显示（PCL-CE 的离线皮肤只做启动器内预览，这里补上游戏内链路）。
/// 只监听 127.0.0.1（TcpListener，绕开 HttpListener 的 URLACL 需要管理员授权的问题），
/// 同一账号与皮肤文件复用同一个实例，随启动器进程结束释放。
/// 可见性限制：仅本机可见，多人里其他玩家仍按其本地皮肤显示——这是离线模式的固有限制。
/// </summary>
public sealed class LocalSkinServer : IDisposable
{
    private static readonly object Gate = new();

    private static LocalSkinServer? _current;

    private readonly TcpListener _listener;

    private readonly CancellationTokenSource _cts = new();

    private readonly LauncherLogger _logger;

    /// <summary>内容指纹：账号、皮肤文件（路径+大小+修改时间）与模型变化时重建服务。</summary>
    private readonly string _key;

    private readonly string _playerName;

    private readonly string _uuidUnsigned;

    private readonly string _skinFile;

    private readonly bool _slim;

    private string _profileJson = "{}";

    private LocalSkinServer(
        TcpListener listener,
        string key,
        string playerName,
        string uuidUnsigned,
        string skinFile,
        bool slim,
        LauncherLogger logger)
    {
        _listener = listener;
        _key = key;
        _playerName = playerName;
        _uuidUnsigned = uuidUnsigned;
        _skinFile = skinFile;
        _slim = slim;
        _logger = logger;
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        ApiRoot = $"http://127.0.0.1:{port}";
        _profileJson = BuildProfileJson();
    }

    /// <summary>注入到 javaagent 的 API 根地址（http://127.0.0.1:port）。</summary>
    public string ApiRoot { get; }

    /// <summary>
    /// 确保账号对应的本地皮肤服务在运行：同一账号+同一皮肤文件复用；
    /// 没有可用皮肤文件或启动失败返回 null（调用方降级为不注入，游戏照常启动）。
    /// </summary>
    public static LocalSkinServer? EnsureStarted(PlayerAccount account, LauncherLogger logger)
    {
        if (string.IsNullOrWhiteSpace(account.SkinPath) || !File.Exists(account.SkinPath))
        {
            return null;
        }

        var uuid = NormalizeUuid(account.Uuid, account.Name);
        string key;
        try
        {
            var info = new FileInfo(account.SkinPath);
            key = $"{account.Name}|{uuid}|{account.SkinModel}|{info.FullName}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.Warn("[本地皮肤] 皮肤文件不可读：" + ex.Message);
            return null;
        }

        lock (Gate)
        {
            if (_current is not null && string.Equals(_current._key, key, StringComparison.Ordinal))
            {
                return _current;
            }

            try
            {
                var listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                var server = new LocalSkinServer(
                    listener,
                    key,
                    account.Name,
                    uuid,
                    account.SkinPath,
                    string.Equals(account.SkinModel, "slim", StringComparison.OrdinalIgnoreCase),
                    logger);
                _ = server.RunAsync();
                _current?.Dispose();
                _current = server;
                logger.Info(
                    $"[本地皮肤] 服务已启动 {server.ApiRoot}（{server._playerName}，模型 {(server._slim ? "slim" : "classic")}）");
                return server;
            }
            catch (Exception ex)
            {
                logger.Warn("[本地皮肤] 服务启动失败：" + ex.Message);
                return null;
            }
        }
    }

    public void Dispose()
    {
        lock (Gate)
        {
            if (ReferenceEquals(_current, this))
            {
                _current = null;
            }
        }

        _cts.Cancel();
        try
        {
            _listener.Stop();
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
        {
            // 已停止
        }

        _cts.Dispose();
    }

    // ---------- 请求处理 ----------

    private async Task RunAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false);
                _ = HandleAsync(client);
            }
        }
        catch (OperationCanceledException)
        {
            // 服务停止
        }
        catch (Exception ex)
        {
            _logger.Warn("[本地皮肤] 监听循环退出：" + ex.Message);
        }
    }

    private async Task HandleAsync(TcpClient client)
    {
        try
        {
            using (client)
            {
                client.ReceiveTimeout = 5000;
                client.SendTimeout = 5000;
                var stream = client.GetStream();
                var request = await ReadRequestAsync(stream, _cts.Token).ConfigureAwait(false);
                if (string.IsNullOrEmpty(request))
                {
                    return;
                }

                var firstLine = request.Split('\n', 2)[0].TrimEnd('\r');
                var parts = firstLine.Split(' ');
                if (parts.Length < 2 || !string.Equals(parts[0], "GET", StringComparison.OrdinalIgnoreCase))
                {
                    await WriteAsync(stream, 405, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("method not allowed")).ConfigureAwait(false);
                    return;
                }

                var target = parts[1];
                var path = target.Split('?')[0];
                var status = RouteAsync(path, out var contentType, out var body);
                // 会话内请求很少（API 探测 + profile + 皮肤图），逐条记日志便于核实游戏是否真的取到了皮肤
                _logger.Info($"[本地皮肤] GET {path} -> {status}");
                await WriteAsync(stream, status, contentType, body).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException)
        {
            // 客户端提前断开：游戏启动期间属正常情况
        }
        catch (Exception ex)
        {
            _logger.Warn("[本地皮肤] 处理请求失败：" + ex.Message);
        }
    }

    private int RouteAsync(string path, out string contentType, out byte[] body)
    {
        contentType = "application/json; charset=utf-8";

        // authlib-injector 启动时探测 API 根，返回 Yggdrasil 元数据即可（字段缺失走它的默认值）。
        // skinDomains 声明本机域名，标记皮肤 URL 属于本服务（与 HMCL 本地 Yggdrasil 的元数据同思路）
        if (path is "/" or "")
        {
            body = Encoding.UTF8.GetBytes("{\"skinDomains\":[\"127.0.0.1\",\"localhost\"],\"signaturePublicKeys\":[]}");
            return 200;
        }

        // 自定义皮肤文件
        if (string.Equals(path, "/skin.png", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var bytes = File.ReadAllBytes(_skinFile);
                contentType = "image/png";
                body = bytes;
                return 200;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                body = Encoding.UTF8.GetBytes("skin unavailable");
                return 404;
            }
        }

        // 皮肤查询：<root>/sessionserver/session/minecraft/profile/<uuid32>
        var profilePrefix = "/sessionserver/session/minecraft/profile/";
        if (path.StartsWith(profilePrefix, StringComparison.OrdinalIgnoreCase))
        {
            // 与原版 sessionserver 一致用无连字符 UUID；带连字符的请求去掉后再比对，宽容处理
            var requested = path[profilePrefix.Length..].Trim('/').Replace("-", "", StringComparison.Ordinal);
            if (string.Equals(requested, _uuidUnsigned, StringComparison.OrdinalIgnoreCase))
            {
                body = Encoding.UTF8.GetBytes(_profileJson);
                return 200;
            }

            body = Encoding.UTF8.GetBytes("{}");
            return 404;
        }

        // 按名字查 UUID（离线也会走到，回自己的档案即可）
        if (path.StartsWith("/api/profiles/minecraft", StringComparison.OrdinalIgnoreCase))
        {
            body = Encoding.UTF8.GetBytes("[]");
            return 200;
        }

        // 其余（entitlements、hasJoined 等）一律 404：离线模式游戏可容忍，与原本 401 行为一致
        body = Encoding.UTF8.GetBytes("{}");
        return 404;
    }

    private string BuildProfileJson()
    {
        var textures = new StringBuilder(256);
        textures.Append("{\"timestamp\":").Append(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        textures.Append(",\"profileId\":\"").Append(_uuidUnsigned).Append('"');
        textures.Append(",\"profileName\":\"").Append(Escape(_playerName)).Append('"');
        textures.Append(",\"textures\":{\"SKIN\":{\"url\":\"").Append(ApiRoot).Append("/skin.png").Append('"');
        if (_slim)
        {
            textures.Append(",\"model\":\"slim\"");
        }

        textures.Append("}}}");
        var value = Convert.ToBase64String(Encoding.UTF8.GetBytes(textures.ToString()));

        return "{\"id\":\"" + _uuidUnsigned + "\",\"name\":\"" + Escape(_playerName)
               + "\",\"properties\":[{\"name\":\"textures\",\"value\":\"" + value + "\"}]}";
    }

    private static string Escape(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("\"", "\\\"", StringComparison.Ordinal);

    private static async Task<string> ReadRequestAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[1024];
        var builder = new StringBuilder();
        while (builder.Length < 8192)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read <= 0)
            {
                break;
            }

            builder.Append(Encoding.UTF8.GetString(buffer, 0, read));
            if (builder.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
            {
                break;
            }
        }

        return builder.ToString();
    }

    private static async Task WriteAsync(NetworkStream stream, int status, string contentType, byte[] body)
    {
        var reason = status switch
        {
            200 => "OK",
            404 => "Not Found",
            405 => "Method Not Allowed",
            _ => "OK"
        };
        var header =
            $"HTTP/1.1 {status} {reason}\r\n" +
            $"Content-Type: {contentType}\r\n" +
            $"Content-Length: {body.Length}\r\n" +
            "Cache-Control: no-store\r\n" +
            "Connection: close\r\n\r\n";
        var headerBytes = Encoding.ASCII.GetBytes(header);
        await stream.WriteAsync(headerBytes).ConfigureAwait(false);
        await stream.WriteAsync(body).ConfigureAwait(false);
        await stream.FlushAsync().ConfigureAwait(false);
    }

    /// <summary>离线 UUID：优先账号自带；缺失时按 Java 的 UUID.nameUUIDFromBytes("OfflinePlayer:"+name) 规则（version 3）补算。</summary>
    private static string NormalizeUuid(string? uuid, string playerName)
    {
        var raw = (uuid ?? "").Replace("-", "", StringComparison.Ordinal).Trim();
        if (raw.Length == 32 && raw.All(Uri.IsHexDigit))
        {
            return raw.ToLowerInvariant();
        }

        var hash = MD5.HashData(Encoding.UTF8.GetBytes("OfflinePlayer:" + playerName));
        hash[6] = (byte)((hash[6] & 0x0F) | 0x30); // version 3
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80); // IETF variant
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
