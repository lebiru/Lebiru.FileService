namespace Lebiru.FileService.Services;

/// <summary>Safely resolves untrusted file names beneath an approved storage root.</summary>
public static class FilePathSecurity
{
    /// <summary>Resolves a simple file name and rejects traversal or rooted paths.</summary>
    public static string ResolveFile(string root, string untrustedFileName)
    {
        if (string.IsNullOrWhiteSpace(untrustedFileName))
            throw new ArgumentException("A file name is required.", nameof(untrustedFileName));

        var decoded = Uri.UnescapeDataString(untrustedFileName).Trim();
        var leafName = Path.GetFileName(decoded);
        if (decoded.IndexOfAny(new[] { '/', '\\', ':' }) >= 0 ||
            !string.Equals(decoded, leafName, StringComparison.Ordinal) ||
            Path.IsPathRooted(decoded) || decoded is "." or "..")
            throw new ArgumentException("The file name contains an invalid path.", nameof(untrustedFileName));

        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var resolved = Path.GetFullPath(Path.Combine(fullRoot, leafName));
        if (!resolved.StartsWith(fullRoot, OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal))
            throw new ArgumentException("The file path escapes the storage directory.", nameof(untrustedFileName));

        return resolved;
    }
}
