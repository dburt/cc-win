using System.IO;
using Microsoft.Win32;

namespace ClaudeSessions;

/// <summary>
/// Locates ~/.claude/projects roots — on Windows itself and inside every installed WSL
/// distribution (reachable from Windows over the \\wsl.localhost share).
/// </summary>
public static class Discovery
{
    // Infrastructure distros that shouldn't be woken up just to look for transcripts.
    private static readonly string[] SkipDistros = { "docker-desktop", "docker-desktop-data", "rancher-desktop-data" };

    public static List<string> FindRoots(IEnumerable<string>? extraRoots = null)
    {
        var roots = new List<string>();

        void TryAdd(string path)
        {
            try
            {
                if (Directory.Exists(path) && !roots.Contains(path, StringComparer.OrdinalIgnoreCase))
                    roots.Add(path);
            }
            catch { /* unreachable share, permission denied — just skip */ }
        }

        TryAdd(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "projects"));

        foreach (var distro in ListDistros())
        {
            var unc = $@"\\wsl.localhost\{distro}";
            TryAdd(Path.Combine(unc, "root", ".claude", "projects"));
            try
            {
                foreach (var home in Directory.EnumerateDirectories(Path.Combine(unc, "home")))
                    TryAdd(Path.Combine(home, ".claude", "projects"));
            }
            catch { }
        }

        foreach (var extra in extraRoots ?? Array.Empty<string>())
            TryAdd(extra);

        return roots;
    }

    /// <summary>Read distro names from the registry rather than shelling out to wsl.exe.</summary>
    public static List<string> ListDistros()
    {
        var names = new List<string>();
        try
        {
            using var lxss = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Lxss");
            if (lxss is null) return names;

            foreach (var sub in lxss.GetSubKeyNames())
            {
                using var key = lxss.OpenSubKey(sub);
                if (key?.GetValue("DistributionName") is string name
                    && !SkipDistros.Contains(name, StringComparer.OrdinalIgnoreCase))
                    names.Add(name);
            }
        }
        catch { }
        return names;
    }

    /// <summary>
    /// Claude Code names each project folder after its working directory with separators
    /// replaced by dashes. That mapping is lossy, so it is only a fallback — the authoritative
    /// cwd is read out of the transcript itself.
    /// </summary>
    public static string DecodeProjectFolder(string folderName)
    {
        var s = folderName.Replace('-', '/');
        if (s.StartsWith("//")) s = s[1..];          // "-mnt-c-..." -> "/mnt/c/..."
        return s;
    }

    public static string PrettyPath(string path)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(home) && path.StartsWith(home, StringComparison.OrdinalIgnoreCase))
            return "~" + path[home.Length..];

        // /home/<user>/x -> ~/x for WSL-side paths
        if (path.StartsWith("/home/", StringComparison.Ordinal))
        {
            var rest = path[6..];
            var slash = rest.IndexOf('/');
            if (slash > 0) return "~" + rest[slash..];
        }
        return path;
    }

    /// <summary>Label a root so the UI can say where a session came from.</summary>
    public static string DescribeRoot(string root)
        => DistroOf(root) is { } distro ? $"WSL: {distro}" : "Windows";

    /// <summary>
    /// The WSL distro a path lives in, or null when it is a plain Windows path — which side of
    /// the machine a session has to be resumed on.
    /// </summary>
    public static string? DistroOf(string path)
    {
        const string prefix = @"\\wsl.localhost\";
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
        var rest = path[prefix.Length..];
        var slash = rest.IndexOf('\\');
        return slash > 0 ? rest[..slash] : null;
    }
}
