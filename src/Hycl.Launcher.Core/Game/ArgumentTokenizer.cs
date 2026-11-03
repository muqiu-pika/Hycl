using System.Text;

namespace Hycl.Launcher.Core.Game;

/// <summary>按空格拆分启动参数，正确处理双引号。</summary>
public static class ArgumentTokenizer
{
    public static IReadOnlyList<string> Split(string? arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments))
        {
            return Array.Empty<string>();
        }

        var results = new List<string>();
        var builder = new StringBuilder();
        var inQuotes = false;
        foreach (var character in arguments)
        {
            if (character == '"')
            {
                inQuotes = !inQuotes;
                continue;
            }

            if (char.IsWhiteSpace(character) && !inQuotes)
            {
                if (builder.Length > 0)
                {
                    results.Add(builder.ToString());
                    builder.Clear();
                }

                continue;
            }

            builder.Append(character);
        }

        if (builder.Length > 0)
        {
            results.Add(builder.ToString());
        }

        return results;
    }
}
