using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using HWUltimate.Core.Updates;
using HWUltimate.Infrastructure;
using HWUltimate.Infrastructure.Updates;

namespace HWUltimate.Tests;

public class ReleaseParsingTests
{
    internal static string ReleaseJson(string tag, string? digest, bool draft = false, bool prerelease = false, string host = "github.com", string? assetName = null)
    {
        var name = assetName ?? $"HWUltimate-{tag}-win-x64.zip";
        var digestJson = digest is null ? "null" : $"\"sha256:{digest}\"";
        return $$"""
            { "tag_name": "{{tag}}", "name": "HWUltimate {{tag}}", "body": "### Changes\n- Faster", "draft": {{draft.ToString().ToLowerInvariant()}},
              "prerelease": {{prerelease.ToString().ToLowerInvariant()}}, "published_at": "2026-11-20T10:00:00Z",
              "html_url": "https://github.com/eren-agalar/HWUltimate/releases/tag/{{tag}}",
              "assets": [
                { "name": "{{name}}", "size": 1234, "digest": {{digestJson}},
                  "browser_download_url": "https://{{host}}/eren-agalar/HWUltimate/releases/download/{{tag}}/{{name}}" },
                { "name": "{{name}}.sha256", "size": 90, "digest": null,
                  "browser_download_url": "https://github.com/eren-agalar/HWUltimate/releases/download/{{tag}}/{{name}}.sha256" }
              ] }
            """;
    }

    [Theory]
    [InlineData("v0.3.1", "0.3.1")]
    [InlineData("0.4", "0.4.0")]
    [InlineData("v1.2.3-beta.1", "1.2.3")]
    [InlineData("V2.0.0+build5", "2.0.0")]
    public void Parses_versions(string tag, string expected) => Assert.Equal(Version.Parse(expected), ReleaseParser.ParseVersion(tag));

    [Theory]
    [InlineData("")]
    [InlineData("latest")]
    [InlineData(null)]
    public void Rejects_bad_versions(string? tag) => Assert.Null(ReleaseParser.ParseVersion(tag));

    [Fact]
    public void Compares_versions()
    {
        Assert.True(ReleaseParser.IsNewer(new Version(0, 3, 1), new Version(0, 3, 0)));
        Assert.True(ReleaseParser.IsNewer(new Version(0, 10, 0), new Version(0, 9, 9)));
        Assert.False(ReleaseParser.IsNewer(new Version(0, 3, 0), new Version(0, 3, 0, 0)));
        Assert.False(ReleaseParser.IsNewer(new Version(0, 2, 9), new Version(0, 3, 0)));
    }

    [Fact]
    public void Parses_a_github_release()
    {
        var hash = new string('a', 64);
        var r = ReleaseParser.Parse(ReleaseJson("v0.3.1", hash))!;
        Assert.Equal(new Version(0, 3, 1), r.Version);
        Assert.Equal("HWUltimate-v0.3.1-win-x64.zip", r.Package!.Name);
        Assert.Equal(hash, r.Package.Sha256);
        Assert.NotNull(r.ChecksumAsset);
        Assert.Contains("Faster", r.Notes, StringComparison.Ordinal);
    }

    [Fact]
    public void Drafts_prereleases_and_foreign_download_hosts_are_ignored()
    {
        Assert.Null(ReleaseParser.Parse(ReleaseJson("v0.4.0", null, draft: true)));
        Assert.Null(ReleaseParser.Parse(ReleaseJson("v0.4.0", null, prerelease: true)));
        Assert.Null(ReleaseParser.Parse(ReleaseJson("v0.4.0", null, host: "evil.example.com"))!.Package);
        Assert.Null(ReleaseParser.Parse(ReleaseJson("v0.4.0", null, host: "github.com.evil.example"))!.Package);
    }

    [Fact]
    public void Reads_checksum_files()
    {
        var h = new string('B', 64);
        Assert.Equal(h.ToLowerInvariant(), ReleaseParser.ParseChecksumFile($"{h}  HWUltimate-v0.3.1-win-x64.zip\n"));
        Assert.Equal(h.ToLowerInvariant(), ReleaseParser.ParseChecksumFile($"{h} *file.zip"));
        Assert.Null(ReleaseParser.ParseChecksumFile("not a hash"));
    }

    [Fact]
    public void Install_script_waits_backs_up_copies_restarts_and_rolls_back()
    {
        var s = UpdateScript.Build(4242, @"C:\Users\A'B\AppData\Local\HWUltimate\updates\staging", @"C:\Tools\HWUltimate", @"C:\Tools\HWUltimate\HWUltimate.exe",
            @"C:\b", @"C:\l.log");
        Assert.Contains("Wait-Process -Id 4242", s, StringComparison.Ordinal);
        Assert.Contains("'C:\\Users\\A''B\\AppData", s, StringComparison.Ordinal); // quotes escaped
        Assert.True(s.IndexOf("$backup -Recurse -Force\n", StringComparison.Ordinal) < s.IndexOf("Join-Path $staging", StringComparison.Ordinal) || s.Contains("Copy-Item -Path (Join-Path $install '*') -Destination $backup", StringComparison.Ordinal));
        Assert.Contains("--updated", s, StringComparison.Ordinal);
        Assert.Contains("Copy-Item -Path (Join-Path $backup '*') -Destination $install", s, StringComparison.Ordinal);
    }
}

internal sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    public List<Uri> Requests { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request.RequestUri!);
        return Task.FromResult(respond(request));
    }
}

public class GitHubUpdateServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"hwu-update-{Guid.NewGuid():N}");

    private static byte[] PackageZip(bool withExe = true)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = zip.CreateEntry(withExe ? "HWUltimate.exe" : "readme.txt");
            using var w = new StreamWriter(entry.Open());
            w.Write("new build");
        }

        return ms.ToArray();
    }

    private GitHubUpdateService Service(Func<HttpRequestMessage, HttpResponseMessage> respond, string current = "0.3.0") =>
        new(new AppPaths(_root), NullLogger<GitHubUpdateService>.Instance, Version.Parse(current), new StubHandler(respond), installDir: _root, executable: Path.Combine(_root, "HWUltimate.exe"));

    private static HttpResponseMessage Ok(string text) => new(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8) };
    private static HttpResponseMessage Ok(byte[] bytes) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };

    [Fact]
    public async Task Reports_only_newer_releases()
    {
        var json = ReleaseParsingTests.ReleaseJson("v0.3.1", new string('a', 64));
        Assert.NotNull(await Service(_ => Ok(json)).CheckAsync(TestContext.Current.CancellationToken));
        Assert.Null(await Service(_ => Ok(json), current: "0.3.1").CheckAsync(TestContext.Current.CancellationToken));
        Assert.Null(await Service(_ => new HttpResponseMessage(HttpStatusCode.NotFound)).CheckAsync(TestContext.Current.CancellationToken)); // nothing published yet
    }

    [Fact]
    public async Task Downloads_verifies_and_extracts_the_package()
    {
        var zip = PackageZip();
        var hash = Convert.ToHexStringLower(SHA256.HashData(zip));
        var svc = Service(r => r.RequestUri!.AbsolutePath.EndsWith(".zip", StringComparison.Ordinal) ? Ok(zip) : Ok(ReleaseParsingTests.ReleaseJson("v0.3.1", hash)));
        var release = (await svc.CheckAsync(TestContext.Current.CancellationToken))!;
        double last = 0;
        var staging = await svc.DownloadAsync(release, new Progress<double>(p => last = p), TestContext.Current.CancellationToken);
        Assert.Equal("new build", await File.ReadAllTextAsync(Path.Combine(staging, "HWUltimate.exe"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Uses_the_sha256_file_when_github_reports_no_digest()
    {
        var zip = PackageZip();
        var hash = Convert.ToHexStringLower(SHA256.HashData(zip));
        var svc = Service(r => r.RequestUri!.AbsolutePath switch
        {
            var p when p.EndsWith(".sha256", StringComparison.Ordinal) => Ok($"{hash}  HWUltimate-v0.3.1-win-x64.zip"),
            var p when p.EndsWith(".zip", StringComparison.Ordinal) => Ok(zip),
            _ => Ok(ReleaseParsingTests.ReleaseJson("v0.3.1", null)),
        });
        var release = (await svc.CheckAsync(TestContext.Current.CancellationToken))!;
        Assert.True(Directory.Exists(await svc.DownloadAsync(release, null, TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task Tampered_unverifiable_or_wrong_packages_are_never_installed()
    {
        var zip = PackageZip();
        var wrong = new string('0', 64);
        var tampered = Service(r => r.RequestUri!.AbsolutePath.EndsWith(".zip", StringComparison.Ordinal) ? Ok(zip) : Ok(ReleaseParsingTests.ReleaseJson("v0.3.1", wrong)));
        var r1 = (await tampered.CheckAsync(TestContext.Current.CancellationToken))!;
        await Assert.ThrowsAsync<InvalidDataException>(() => tampered.DownloadAsync(r1, null, TestContext.Current.CancellationToken));
        Assert.False(File.Exists(Path.Combine(_root, "updates", r1.Package!.Name))); // discarded

        var noHash = Service(r => r.RequestUri!.AbsolutePath switch
        {
            var p when p.EndsWith(".sha256", StringComparison.Ordinal) => Ok("no checksum here"),
            var p when p.EndsWith(".zip", StringComparison.Ordinal) => Ok(zip),
            _ => Ok(ReleaseParsingTests.ReleaseJson("v0.3.1", null)),
        });
        var r2 = (await noHash.CheckAsync(TestContext.Current.CancellationToken))!;
        await Assert.ThrowsAsync<InvalidDataException>(() => noHash.DownloadAsync(r2, null, TestContext.Current.CancellationToken));

        var notApp = PackageZip(withExe: false);
        var svc = Service(r => r.RequestUri!.AbsolutePath.EndsWith(".zip", StringComparison.Ordinal) ? Ok(notApp)
            : Ok(ReleaseParsingTests.ReleaseJson("v0.3.1", Convert.ToHexStringLower(SHA256.HashData(notApp)))));
        var r3 = (await svc.CheckAsync(TestContext.Current.CancellationToken))!;
        await Assert.ThrowsAsync<InvalidDataException>(() => svc.DownloadAsync(r3, null, TestContext.Current.CancellationToken));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
