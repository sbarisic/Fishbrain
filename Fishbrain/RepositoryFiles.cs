using System.Security.Cryptography;

namespace Fishbrain;

internal static class RepositoryFiles
{
    internal static string ResolveRepositoryFile(params string[] segments) =>
        ResolveRepositoryFileFrom([Environment.CurrentDirectory, AppContext.BaseDirectory], segments);

    internal static string ResolveRepositoryFileFrom(IEnumerable<string> anchors, params string[] segments)
    {
        if (segments.Length == 0 || segments.Any(string.IsNullOrWhiteSpace)) throw new ArgumentException("File path segments cannot be empty.");
        foreach (var anchor in anchors.Where(x => !string.IsNullOrWhiteSpace(x)).Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase))
            for (var directory = new DirectoryInfo(anchor); directory is not null; directory = directory.Parent)
            {
                var candidate = Path.Combine([directory.FullName, .. segments]);
                if (File.Exists(candidate)) return candidate;
            }
        throw new FileNotFoundException($"Repository file was not found: {Path.Combine(segments)}");
    }

    internal static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    internal static string TelemetryDirectory(string anchorPath)
    {
        foreach (var anchor in new[] { Path.GetDirectoryName(Path.GetFullPath(anchorPath))!, Environment.CurrentDirectory, AppContext.BaseDirectory })
            for (var directory = new DirectoryInfo(anchor); directory is not null; directory = directory.Parent)
                if (File.Exists(Path.Combine(directory.FullName, "Fishbrain.slnx"))) return Path.Combine(directory.FullName, "data", "telemetry");
        throw new DirectoryNotFoundException("Could not locate the Fishbrain repository for telemetry output.");
    }
}
