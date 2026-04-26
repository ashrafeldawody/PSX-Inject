using System.Text.RegularExpressions;

namespace PsxInject.Server;

public static partial class PsRegex
{
    // Mirrors the Node.js script:
    //   /^http:\/\/(?:gs2?\.ww\.|gst\.|gs\.)?(?:prod\.)?dl\.playstation\.net.*\/([^\?]+\.pkg)\?/
    [GeneratedRegex(
        @"^http://(?:gs2?\.ww\.|gst\.|gs\.)?(?:prod\.)?dl\.playstation\.net.*/([^\?]+\.pkg)\?",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    public static partial Regex Pattern();

    public static bool TryMatch(string url, out string fileName)
    {
        var m = Pattern().Match(url);
        if (m.Success)
        {
            fileName = m.Groups[1].Value;
            return true;
        }
        fileName = "";
        return false;
    }
}
