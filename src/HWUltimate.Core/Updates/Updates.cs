using System.Globalization;
using System.Text;
using System.Text.Json;

namespace HWUltimate.Core.Updates;

/// <summary>Where releases are published. Releases are read from the public GitHub API; no account or token is used.</summary>
public static class UpdateSource
{
    public const string Owner = "eren-agalar";
    public const string Repository = "HWUltimate";
    public static string RepositoryUrl => $"https://github.com/{Owner}/{Repository}";
    public static string ReleasesUrl => $"{RepositoryUrl}/releases";
    public static string LatestReleaseApi => $"https://api.github.com/repos/{Owner}/{Repository}/releases/latest";

    /// <summary>Package name the release workflow uploads, e.g. "HWUltimate-v0.3.1-win-x64.zip".</summary>
    public static string PackageName(Version v) => $"HWUltimate-v{Format(v)}-win-x64.zip";

    public static string Format(Version v) => v.Build >= 0 ? $"{v.Major}.{v.Minor}.{v.Build}" : $"{v.Major}.{v.Minor}";
}

public sealed record ReleaseAsset(string Name, string DownloadUrl, long Size, string? Sha256);

/// <param name="Package">The Windows x64 zip, or null when the release has none (then only the release page is offered).</param>
/// <param name="ChecksumAsset">A "&lt;package&gt;.sha256" file, used when GitHub did not report a digest.</param>
public sealed record ReleaseInfo(Version Version, string Tag, string Name, string Notes, DateTimeOffset? PublishedAt, string PageUrl, ReleaseAsset? Package, ReleaseAsset? ChecksumAsset);

public enum UpdateState
{
    Idle = 0,
    Checking,
    UpToDate,
    Available,
    Downloading,
    Installing,
    Failed,
}

public static class ReleaseParser
{
    /// <summary>"v1.2.3", "1.2.3", "v1.2" → Version; pre-release / build suffixes ("-beta", "+abc") are ignored; null when invalid.</summary>
    public static Version? ParseVersion(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        var t = tag.Trim().TrimStart('v', 'V');
        var cut = t.IndexOfAny(['-', '+', ' ']);
        if (cut >= 0) t = t[..cut];
        if (!Version.TryParse(t, out var v)) return null;
        return new Version(v.Major, v.Minor, Math.Max(v.Build, 0));
    }

    /// <summary>Strictly newer, comparing major.minor.patch only.</summary>
    public static bool IsNewer(Version candidate, Version current) =>
        new Version(candidate.Major, candidate.Minor, Math.Max(candidate.Build, 0)) > new Version(current.Major, current.Minor, Math.Max(current.Build, 0));

    /// <summary>
    /// Parses a GitHub "release" object. Drafts and pre-releases are rejected (null). The package is the asset named exactly
    /// <see cref="UpdateSource.PackageName"/>, else the only "*win-x64*.zip" asset.
    /// </summary>
    public static ReleaseInfo? Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        if (r.ValueKind != JsonValueKind.Object) return null;
        if (Bool(r, "draft") || Bool(r, "prerelease")) return null;
        var tag = Str(r, "tag_name");
        if (ParseVersion(tag) is not { } version) return null;

        var assets = new List<ReleaseAsset>();
        if (r.TryGetProperty("assets", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in list.EnumerateArray())
            {
                var name = Str(a, "name");
                var url = Str(a, "browser_download_url");
                if (name is null || url is null || !IsGitHubDownload(url)) continue;
                var digest = Str(a, "digest");
                var sha = digest is not null && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? NormalizeHash(digest[7..]) : null;
                assets.Add(new ReleaseAsset(name, url, a.TryGetProperty("size", out var s) && s.TryGetInt64(out var n) ? n : 0, sha));
            }
        }

        var expected = UpdateSource.PackageName(version);
        var package = assets.FirstOrDefault(a => a.Name.Equals(expected, StringComparison.OrdinalIgnoreCase));
        if (package is null)
        {
            var zips = assets.Where(a => a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) && a.Name.Contains("win-x64", StringComparison.OrdinalIgnoreCase)).ToList();
            if (zips.Count == 1) package = zips[0];
        }

        var checksum = package is null ? null : assets.FirstOrDefault(a => a.Name.Equals(package.Name + ".sha256", StringComparison.OrdinalIgnoreCase));
        DateTimeOffset? published = DateTimeOffset.TryParse(Str(r, "published_at"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var p) ? p : null;
        var page = Str(r, "html_url") is { } h && IsGitHubDownload(h) ? h : UpdateSource.ReleasesUrl;
        return new ReleaseInfo(version, tag!, Str(r, "name") ?? tag!, Str(r, "body") ?? string.Empty, published, page, package, checksum);
    }

    /// <summary>Downloads only from github.com over https (assets are served from github.com and redirected to GitHub's CDN).</summary>
    public static bool IsGitHubDownload(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps && u.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase);

    /// <summary>First 64-hex-digit token of a ".sha256" file ("hash  filename" or just "hash").</summary>
    public static string? ParseChecksumFile(string text)
    {
        foreach (var token in text.Split([' ', '\t', '\r', '\n', '*'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (NormalizeHash(token) is { } h) return h;
        }

        return null;
    }

    public static string? NormalizeHash(string s)
    {
        var t = s.Trim().ToLowerInvariant();
        return t.Length == 64 && t.All(Uri.IsHexDigit) ? t : null;
    }

    private static string? Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool Bool(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
}

/// <summary>
/// The PowerShell script that applies an update after HWUltimate exits: wait for the process, back up the install folder,
/// copy the verified new files over it, restart. On any error the backup is copied back and the previous version restarts.
/// </summary>
public static class UpdateScript
{
    public static string Build(int processId, string stagingDir, string installDir, string executable, string backupDir, string logFile)
    {
        static string Q(string s) => "'" + s.Replace("'", "''", StringComparison.Ordinal) + "'";
        var sb = new StringBuilder();
        sb.AppendLine("$ErrorActionPreference = 'Stop'");
        sb.AppendLine($"$staging = {Q(stagingDir)}");
        sb.AppendLine($"$install = {Q(installDir)}");
        sb.AppendLine($"$exe = {Q(executable)}");
        sb.AppendLine($"$backup = {Q(backupDir)}");
        sb.AppendLine($"$log = {Q(logFile)}");
        sb.AppendLine("function Write-Log($m) { \"$(Get-Date -Format o) $m\" | Out-File -LiteralPath $log -Append -Encoding utf8 }");
        sb.AppendLine("try {");
        sb.AppendLine($"  Wait-Process -Id {processId.ToString(CultureInfo.InvariantCulture)} -Timeout 120 -ErrorAction SilentlyContinue");
        sb.AppendLine("  Start-Sleep -Milliseconds 700");
        sb.AppendLine("  if (Test-Path -LiteralPath $backup) { Remove-Item -LiteralPath $backup -Recurse -Force }");
        sb.AppendLine("  New-Item -ItemType Directory -Path $backup -Force | Out-Null");
        sb.AppendLine("  Copy-Item -Path (Join-Path $install '*') -Destination $backup -Recurse -Force");
        sb.AppendLine("  Copy-Item -Path (Join-Path $staging '*') -Destination $install -Recurse -Force");
        sb.AppendLine("  Write-Log 'Update applied'");
        sb.AppendLine("  Start-Process -FilePath $exe -ArgumentList '--updated'");
        sb.AppendLine("} catch {");
        sb.AppendLine("  Write-Log \"Update failed: $_\"");
        sb.AppendLine("  try { Copy-Item -Path (Join-Path $backup '*') -Destination $install -Recurse -Force } catch { Write-Log \"Rollback failed: $_\" }");
        sb.AppendLine("  Start-Process -FilePath $exe -ArgumentList '--update-failed'");
        sb.AppendLine("}");
        return sb.ToString();
    }
}

public interface IUpdateService
{
    Version CurrentVersion { get; }

    /// <summary>The latest published release when it is newer than this build; otherwise null.</summary>
    Task<ReleaseInfo?> CheckAsync(CancellationToken cancellationToken = default);

    /// <summary>Downloads the package, verifies its SHA-256 and extracts it. Returns the folder holding the new files.</summary>
    Task<string> DownloadAsync(ReleaseInfo release, IProgress<double>? progress = null, CancellationToken cancellationToken = default);

    /// <summary>Starts the script that replaces the files once this process exits, then the caller shuts the app down.</summary>
    void ScheduleInstall(string stagingDir);
}
