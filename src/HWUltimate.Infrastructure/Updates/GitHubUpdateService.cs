using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using HWUltimate.Core.Updates;

namespace HWUltimate.Infrastructure.Updates;

/// <summary>
/// Checks GitHub Releases for a newer HWUltimate, downloads the Windows package, verifies it against the SHA-256 GitHub
/// reports (or the release's .sha256 file) and installs it by replacing the program files after the app exits.
/// A package without a verifiable checksum is never installed.
/// </summary>
public sealed class GitHubUpdateService : IUpdateService, IDisposable
{
    private readonly HttpClient _http;
    private readonly ILogger<GitHubUpdateService> _logger;
    private readonly AppPaths _paths;
    private readonly string _installDir;
    private readonly string? _executable;

    public GitHubUpdateService(AppPaths paths, ILogger<GitHubUpdateService> logger, Version currentVersion, HttpMessageHandler? handler = null,
        string? installDir = null, string? executable = null)
    {
        _paths = paths;
        _logger = logger;
        CurrentVersion = currentVersion;
        _installDir = installDir ?? AppContext.BaseDirectory;
        _executable = executable ?? Environment.ProcessPath;
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.Timeout = TimeSpan.FromMinutes(5);
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("HWUltimate", UpdateSource.Format(currentVersion)));
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
    }

    public Version CurrentVersion { get; }

    public string UpdatesDir => Path.Combine(_paths.Root, "updates");

    public async Task<ReleaseInfo?> CheckAsync(CancellationToken cancellationToken = default)
    {
        using var response = await _http.GetAsync(UpdateSource.LatestReleaseApi, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            _logger.LogInformation("No published release yet");
            return null;
        }

        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var release = ReleaseParser.Parse(json);
        if (release is null || !ReleaseParser.IsNewer(release.Version, CurrentVersion))
        {
            _logger.LogInformation("Up to date ({Current}; latest {Latest})", UpdateSource.Format(CurrentVersion), release is null ? "none" : UpdateSource.Format(release.Version));
            return null;
        }

        _logger.LogInformation("Update available: {Version}", UpdateSource.Format(release.Version));
        return release;
    }

    public async Task<string> DownloadAsync(ReleaseInfo release, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        if (release.Package is not { } package) throw new InvalidOperationException("This release has no Windows package.");
        var expected = package.Sha256 ?? await ReadChecksumAsync(release, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("The release does not publish a SHA-256 checksum for its package; it will not be installed.");

        Directory.CreateDirectory(UpdatesDir);
        var zip = Path.Combine(UpdatesDir, package.Name);
        var staging = Path.Combine(UpdatesDir, "staging");

        using (var response = await _http.GetAsync(package.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
        {
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength ?? package.Size;
            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using var target = File.Create(zip);
            var buffer = new byte[81920];
            long done = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                done += read;
                if (total > 0) progress?.Report(Math.Min(1, (double)done / total));
            }
        }

        string actual;
        await using (var file = File.OpenRead(zip))
        {
            actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(file, cancellationToken).ConfigureAwait(false));
        }

        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(zip);
            throw new InvalidDataException($"Checksum mismatch: expected {expected}, got {actual}. The download was discarded.");
        }

        if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        // ExtractToDirectory rejects entries that would land outside the target folder.
        ZipFile.ExtractToDirectory(zip, staging);
        if (!File.Exists(Path.Combine(staging, "HWUltimate.exe")))
        {
            throw new InvalidDataException("The package does not contain HWUltimate.exe at its root.");
        }

        _logger.LogInformation("Update {Version} downloaded and verified (sha256 {Hash})", UpdateSource.Format(release.Version), actual);
        return staging;
    }

    private async Task<string?> ReadChecksumAsync(ReleaseInfo release, CancellationToken cancellationToken)
    {
        if (release.ChecksumAsset is not { } asset) return null;
        var text = await _http.GetStringAsync(asset.DownloadUrl, cancellationToken).ConfigureAwait(false);
        return ReleaseParser.ParseChecksumFile(text);
    }

    /// <summary>True when the program folder can be written (otherwise the update must be installed manually).</summary>
    public bool CanWriteInstallFolder()
    {
        try
        {
            var probe = Path.Combine(_installDir, $".hwu-write-test-{Guid.NewGuid():N}");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public void ScheduleInstall(string stagingDir)
    {
        if (_executable is null) throw new InvalidOperationException("The running executable is unknown.");
        if (!CanWriteInstallFolder()) throw new UnauthorizedAccessException($"No write access to {_installDir}.");
        var script = Path.Combine(UpdatesDir, "apply-update.ps1");
        File.WriteAllText(script, UpdateScript.Build(Environment.ProcessId, stagingDir, _installDir.TrimEnd(Path.DirectorySeparatorChar), _executable,
            Path.Combine(UpdatesDir, "backup"), Path.Combine(_paths.LogDirectory, "update.log")));
        var start = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-WindowStyle", "Hidden", "-File", script }) start.ArgumentList.Add(arg);
        Process.Start(start);
        _logger.LogInformation("Update scheduled; the app will restart after it exits");
    }

    public void Dispose() => _http.Dispose();
}
