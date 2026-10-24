using System.Reflection;

namespace CloudStore.Core;

/// <summary>
/// 应用层面的固定信息（版本、作者、首发地）。
/// 这些字符串只在「关于」窗口和窗口标题等少数几处用到，集中在这里避免各处写死不同值。
/// </summary>
public static class AppInfo
{
    /// <summary>应用名。窗口标题、关于窗口、发布产物都用它。</summary>
    public const string Name = "云存储管家";

    /// <summary>
    /// 对外展示的版本号。
    /// 直接读程序集的 InformationalVersion（来自根目录 Directory.Build.props 的 &lt;Version&gt;），
    /// 避免和发布版本各写一份而对不上；拿不到时回落到 1.0.0，保证界面不会显示空白。
    /// </summary>
    public static string Version { get; } = ReadVersion();

    /// <summary>作者。</summary>
    public const string Author = "mypc1995";

    /// <summary>首次发布的平台。</summary>
    public const string FirstPublish = "吾爱破解 52pojie";

    /// <summary>状态栏 / 关于窗口用的署名串。</summary>
    public const string Credits = $"作者 {Author} · 首发 {FirstPublish}";

    private static string ReadVersion()
    {
        string? raw = typeof(AppInfo).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        if (string.IsNullOrWhiteSpace(raw))
        {
            return "1.0.0";
        }

        // SourceLink 会在版本号后面追加 "+<commit sha>"，界面里只显示前半段。
        int plus = raw.IndexOf('+', StringComparison.Ordinal);
        return (plus >= 0 ? raw[..plus] : raw).Trim();
    }
}
