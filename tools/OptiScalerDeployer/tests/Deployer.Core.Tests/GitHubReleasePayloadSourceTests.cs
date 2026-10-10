using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Deployer.Core.Payload;
using Xunit;

namespace Deployer.Core.Tests;

public sealed class GitHubReleasePayloadSourceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "OptiScalerPayloadTests", Guid.NewGuid().ToString("N"));
    private static byte[] Archive(params (string Name, string Text)[] entries)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
            foreach (var (name, text) in entries)
            { using var writer = new StreamWriter(zip.CreateEntry(name).Open()); writer.Write(text); }
        return stream.ToArray();
    }
    private static byte[] ValidArchive() => Archive(("OptiScaler.dll", "dll bytes"), ("OptiScaler.ini", "[Log]\nLogToFile=true"));
    private sealed class RateLimitedHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("API rate limit exceeded") });
    }
    [Fact]
    public async Task Http_failure_keeps_actionable_status_diagnostic()
    {
        using var http = new HttpClient(new RateLimitedHandler());
        var source = new GitHubReleasePayloadSource(http, _root);
        Assert.Null(await source.TryGetAsync(default));
        Assert.Contains("403", source.LastError);
        var error = await Assert.ThrowsAsync<Deployer.Core.Errors.PayloadNotFoundException>(() => new PayloadSourceChain([source]).GetOrThrowAsync(default));
        Assert.Contains("403", error.Diagnostic);
    }
    private sealed class Handler(byte[] archive, string? digest = null, string tag = "v0.9.1", string url = "https://github.com/OptiScaler/OptiScaler/releases/download/v0.9.1/payload.7z") : HttpMessageHandler
    {
        public int Downloads { get; private set; }
        public string? ApiPath { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri!.Host == "api.github.com")
            {
                Assert.NotEmpty(request.Headers.UserAgent);
                ApiPath = request.RequestUri.AbsolutePath;
                var json = JsonSerializer.Serialize(new { tag_name = tag, assets = new[] { new { name = "payload.7z", browser_download_url = url, digest = digest ?? "sha256:" + Convert.ToHexString(SHA256.HashData(archive)) } } });
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
            }
            Downloads++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(archive) });
        }
    }

    [Fact]
    public async Task Download_validates_and_reuses_cache_without_deploying_metadata()
    {
        var handler = new Handler(ValidArchive()); using var http = new HttpClient(handler);
        var source = new GitHubReleasePayloadSource(http, _root);
        var resolved = await source.TryGetAsync(default);
        Assert.True(resolved is not null, source.LastError);
        var first = Assert.IsType<Deployer.Core.Domain.PayloadBundle>(resolved);
        Assert.Equal("dll bytes", File.ReadAllText(Path.Combine(first.RootDirectory, "OptiScaler.dll")));
        Assert.DoesNotContain(Directory.GetFiles(first.RootDirectory), path => path.EndsWith("integrity.sha256"));
        Assert.NotNull(await source.TryGetAsync(default));
        Assert.Equal(1, handler.Downloads);
    }

    [Fact]
    public async Task Modified_cached_file_is_redownloaded()
    {
        var handler = new Handler(ValidArchive()); using var http = new HttpClient(handler);
        var source = new GitHubReleasePayloadSource(http, _root);
        var first = Assert.IsType<Deployer.Core.Domain.PayloadBundle>(await source.TryGetAsync(default));
        File.WriteAllText(Path.Combine(first.RootDirectory, "OptiScaler.dll"), "corrupt");
        var next = Assert.IsType<Deployer.Core.Domain.PayloadBundle>(await source.TryGetAsync(default));
        Assert.Equal("dll bytes", File.ReadAllText(Path.Combine(next.RootDirectory, "OptiScaler.dll")));
        Assert.Equal(2, handler.Downloads);
    }

    [Theory]
    [InlineData("https://evilgithubusercontent.com/payload.7z")]
    [InlineData("http://github.com/payload.7z")]
    public async Task Nonofficial_url_is_rejected_before_download(string url)
    {
        var handler = new Handler(ValidArchive(), url: url); using var http = new HttpClient(handler);
        Assert.Null(await new GitHubReleasePayloadSource(http, _root).TryGetAsync(default));
        Assert.Equal(0, handler.Downloads);
    }

    [Theory]
    [InlineData("bad")]
    [InlineData("sha256:0000000000000000000000000000000000000000000000000000000000000000")]
    public async Task Missing_or_mismatching_digest_is_rejected(string digest)
    {
        using var http = new HttpClient(new Handler(ValidArchive(), digest));
        Assert.Null(await new GitHubReleasePayloadSource(http, _root).TryGetAsync(default));
    }

    [Fact]
    public async Task Nightly_uses_tag_endpoint()
    {
        var handler = new Handler(ValidArchive(), tag: "nightly"); using var http = new HttpClient(handler);
        Assert.NotNull(await new GitHubReleasePayloadSource(http, _root, "nightly").TryGetAsync(default));
        Assert.EndsWith("/tags/nightly", handler.ApiPath);
    }

    [Theory]
    [InlineData("../escaped.dll")]
    [InlineData("C:/escaped.dll")]
    [InlineData("OptiScaler.dll:stream")]
    public async Task Archive_path_escape_is_rejected(string name)
    {
        using var http = new HttpClient(new Handler(Archive(("OptiScaler.dll", "dll"), ("OptiScaler.ini", "ini"), (name, "bad"))));
        Assert.Null(await new GitHubReleasePayloadSource(http, _root).TryGetAsync(default));
        Assert.False(File.Exists(Path.Combine(_root, "escaped.dll")));
    }

    [Fact]
    public async Task Download_expansion_and_entry_limits_are_enforced()
    {
        using var http = new HttpClient(new Handler(ValidArchive()));
        Assert.Null(await new GitHubReleasePayloadSource(http, _root, maxDownloadBytes: 1).TryGetAsync(default));
        Assert.Null(await new GitHubReleasePayloadSource(http, _root, maxExpandedBytes: 1).TryGetAsync(default));
        Assert.Null(await new GitHubReleasePayloadSource(http, _root, maxEntryCount: 1).TryGetAsync(default));
    }

    [Fact]
    public async Task Unsafe_tag_and_missing_required_files_are_rejected()
    {
        using var http = new HttpClient(new Handler(ValidArchive(), tag: ".."));
        Assert.Null(await new GitHubReleasePayloadSource(http, _root).TryGetAsync(default));
        using var missing = new HttpClient(new Handler(Archive(("OptiScaler.dll", "dll"))));
        Assert.Null(await new GitHubReleasePayloadSource(missing, _root).TryGetAsync(default));
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
