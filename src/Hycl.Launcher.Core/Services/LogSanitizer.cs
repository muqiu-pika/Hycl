using System.Text.RegularExpressions;

namespace Hycl.Launcher.Core.Services;

/// <summary>
/// 日志脱敏（对应 PCL-CE PclLogger 的打码策略）：
/// 令牌（accessToken/clientToken/refreshToken、Bearer JWT）与启动参数中的用户名
/// 在进入持久化日志或界面展示前统一打码，避免 accounts 凭据随日志外泄。
/// </summary>
public static class LogSanitizer
{
    private static readonly Regex KeyedTokenPattern = new(
        @"(?i)\b(access[_-]?token|client[_-]?token|refresh[_-]?token|session[_-]?token)\s*[=:]\s*(""?[A-Za-z0-9._~+/\-]{8,}""?)",
        RegexOptions.Compiled);

    private static readonly Regex CommandLineArgPattern = new(
        @"(?i)(--(?:access[_-]?token|client[_-]?token|refresh[_-]?token|username|uuid))\s+([^\s""]+)",
        RegexOptions.Compiled);

    private static readonly Regex BearerPattern = new(
        @"(?i)\b(bearer)\s+eyJ[A-Za-z0-9._\-]+",
        RegexOptions.Compiled);

    public static string Sanitize(string line)
    {
        if (string.IsNullOrEmpty(line))
        {
            return line;
        }

        var result = KeyedTokenPattern.Replace(line, match =>
            $"{match.Groups[1].Value}={Mask(match.Groups[2].Value)}");
        result = CommandLineArgPattern.Replace(result, match =>
            $"{match.Groups[1].Value} ***");
        result = BearerPattern.Replace(result, "$1 ***");
        return result;
    }

    /// <summary>保留前 4 位便于核对是否同一令牌，其余以 *** 代替。</summary>
    private static string Mask(string value)
    {
        var core = value.Trim('"');
        return core.Length <= 4 ? "***" : core[..4] + "***";
    }
}
