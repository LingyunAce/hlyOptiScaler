using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using Deployer.Core.Abstractions;
using Deployer.Core.Domain;
using Deployer.Core.Elevation;
using SharpCompress.Archives;

namespace Deployer.Core.Payload;

/// <summary>Downloads and safely extracts the official OptiScaler GitHub 7z release.</summary>
public sealed class GitHubReleasePayloadSource : IPayloadSource
{
    private const string ApiRoot = "https://api.github.com/repos/OptiScaler/OptiScaler/releases";
    private const string RateLimitedStableTag = "v0.9.4";
    private const string RateLimitedStableUrl = "https://github.com/optiscaler/OptiScaler/releases/download/v0.9.4/Optiscaler_0.9.4-final.20260718._MM.7z";
    private const string RateLimitedStableDigest = "575cb4df866116093df75af607e37fd70e10f5163e0f23fd5c804142e80ef0ad";
    private readonly HttpClient _http;
    private readonly string _cacheRoot;
    private readonly string _channel;
    private readonly IFileVersionReader? _versions;
    private readonly long _maxDownloadBytes, _maxExpandedBytes;
    private readonly int _maxEntryCount;
    private readonly TimeSpan _timeout;

    public GitHubReleasePayloadSource(HttpClient httpClient, string cacheRoot, string channel = "stable",
        TimeSpan? timeout = null, long maxDownloadBytes = 512L * 1024 * 1024,
        long maxExpandedBytes = 2L * 1024 * 1024 * 1024, int maxEntryCount = 10000,
        IFileVersionReader? versions = null)
    {
        _http = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _cacheRoot = Path.GetFullPath(cacheRoot ?? throw new ArgumentNullException(nameof(cacheRoot)));
        if (channel is not ("nightly" or "stable")) throw new ArgumentException("Unknown release channel.", nameof(channel));
        if (maxDownloadBytes <= 0 || maxExpandedBytes <= 0 || maxEntryCount <= 0) throw new ArgumentOutOfRangeException(nameof(maxDownloadBytes));
        _channel = channel;
        _versions = versions;
        _maxDownloadBytes = maxDownloadBytes; _maxExpandedBytes = maxExpandedBytes; _maxEntryCount = maxEntryCount;
        _timeout = timeout ?? TimeSpan.FromMinutes(5);
    }

    public string Description => $"GitHub Releases ({_channel})";
    public string? LastError { get; private set; }

    public async Task<PayloadBundle?> TryGetAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        LastError = null;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(_timeout);
        var callerToken = ct; ct = deadline.Token;
        string? fallbackDiagnostic = null;
        try
        {
            using var response = await GetOfficialAsync(new Uri(_channel == "nightly" ? ApiRoot + "/tags/nightly" : ApiRoot + "/latest"), ct).ConfigureAwait(false);
            JsonDocument doc;
            if (_channel == "stable" && (int)response.StatusCode is 403 or 429)
            {
                fallbackDiagnostic = $"GitHub Releases metadata returned HTTP {(int)response.StatusCode} ({response.ReasonPhrase}); using pinned official stable {RateLimitedStableTag}, which may not be the latest release.";
                doc = JsonDocument.Parse(JsonSerializer.Serialize(new
                {
                    tag_name = RateLimitedStableTag,
                    assets = new[] { new { name = "Optiscaler_0.9.4-final.20260718._MM.7z", browser_download_url = RateLimitedStableUrl, digest = "sha256:" + RateLimitedStableDigest } }
                }));
            }
            else
            {
                if (!response.IsSuccessStatusCode)
                {
                    LastError = $"GitHub Releases metadata returned HTTP {(int)response.StatusCode} ({response.ReasonPhrase}). For HTTP 403/429, retry after the API limit resets or select a manual payload directory.";
                    return null;
                }
                await using var metadata = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                using var bounded = new MemoryStream();
                var metadataBuffer = new byte[8192]; int read;
                while ((read = await metadata.ReadAsync(metadataBuffer, ct).ConfigureAwait(false)) > 0)
                { if (bounded.Length + read > 2 * 1024 * 1024) throw new InvalidDataException("Release metadata limit."); bounded.Write(metadataBuffer, 0, read); }
                doc = JsonDocument.Parse(bounded.ToArray());
            }
            using (doc)
            {
                var root = doc.RootElement; var tag = root.GetProperty("tag_name").GetString();
                if (string.IsNullOrWhiteSpace(tag) || tag is "." or ".." || tag.Length > 100 || tag.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('.' or '-' or '_'))) return null;
                var asset = SelectAsset(root); if (asset is null) return null;
                var url = asset.Value.GetProperty("browser_download_url").GetString();
                if (!IsOfficialUrl(url)) return null;
                if (!asset.Value.TryGetProperty("digest", out var digest)) return null;
                var expected = ReadDigest(digest);
                if (expected is null || expected.Length != 64 || expected.Any(c => !Uri.IsHexDigit(c))) return null;
                var version = Sanitize(tag!); var destination = Path.Combine(_cacheRoot, version);
                ElevationPolicy.RejectReparseAncestors(destination);
                if (TryValidate(destination, expected, out var existing)) return Bundle(existing, tag!);
                Directory.CreateDirectory(_cacheRoot);
                ElevationPolicy.RejectReparseAncestors(Path.Combine(_cacheRoot, ".cache.lock"));
                using var cacheLock = new FileStream(Path.Combine(_cacheRoot, ".cache.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                if (TryValidate(destination, expected, out existing)) return Bundle(existing, tag!);
                var staging = Path.Combine(_cacheRoot, ".staging-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(staging);
                try
                {
                    var archive = Path.Combine(staging, "payload.7z");
                    await Download(url!, archive, ct).ConfigureAwait(false);
                    var hash = await HashFile(archive, ct).ConfigureAwait(false);
                    if (expected is not null && !hash.Equals(expected, StringComparison.OrdinalIgnoreCase))
                    { LastError = AddFallbackDiagnostic(fallbackDiagnostic, "Downloaded release SHA256 did not match the official digest."); return null; }
                    var extracted = Path.Combine(staging, "extracted"); Directory.CreateDirectory(extracted);
                    Extract(archive, extracted, ct);
                    var payload = FindPayload(extracted); if (payload is null)
                    { LastError = AddFallbackDiagnostic(fallbackDiagnostic, "Downloaded release does not contain OptiScaler.dll and OptiScaler.ini."); return null; }
                    var hashes = Directory.GetFiles(payload, "*", SearchOption.AllDirectories)
                        .ToDictionary(p => Path.GetRelativePath(payload, p), p => HashFile(p, ct).GetAwaiter().GetResult());
                    var content = Path.Combine(staging, "content"); Directory.Move(payload, content);
                    await File.WriteAllTextAsync(Path.Combine(staging, "integrity.json"), JsonSerializer.Serialize(new CacheIntegrity(hash, hashes)), ct).ConfigureAwait(false);
                    File.Delete(archive);
                    if (Directory.Exists(destination)) { RejectReparseTree(destination); Directory.Delete(destination, true); }
                    Directory.CreateDirectory(destination);
                    Directory.Move(content, Path.Combine(destination, "content"));
                    File.Move(Path.Combine(staging, "integrity.json"), Path.Combine(destination, "integrity.json"));
                    return Bundle(Path.Combine(destination, "content"), tag!);
                }
                finally { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
            }
        }
        catch (OperationCanceledException) when (!callerToken.IsCancellationRequested) { LastError = AddFallbackDiagnostic(fallbackDiagnostic, "Release download timed out."); return null; }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { LastError = AddFallbackDiagnostic(fallbackDiagnostic, ex.ToString()); return null; }
    }

    private async Task Download(string? url, string path, CancellationToken ct)
    {
        using var response = await GetOfficialAsync(new Uri(url!), ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.RequestMessage?.RequestUri is { } final && !IsOfficialUrl(final.AbsoluteUri)) throw new InvalidDataException("Unofficial redirect host.");
        if (response.Content.Headers.ContentLength > _maxDownloadBytes) throw new InvalidDataException("download limit");
        await using var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var output = File.Create(path); var buffer = new byte[81920]; long total = 0; int n;
        while ((n = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0) { total += n; if (total > _maxDownloadBytes) throw new InvalidDataException("download limit"); await output.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false); }
    }

    private static string AddFallbackDiagnostic(string? fallback, string detail) => fallback is null ? detail : fallback + Environment.NewLine + detail;

    private void Extract(string archive, string destination, CancellationToken ct)
    {
        using var a = ArchiveFactory.OpenArchive(archive); long expanded = 0; int count = 0;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in a.Entries)
        {
            ct.ThrowIfCancellationRequested(); if (++count > _maxEntryCount) throw new InvalidDataException("entry limit");
            var relative = (e.Key ?? throw new InvalidDataException("Missing entry path")).Replace('/', Path.DirectorySeparatorChar); var full = Path.GetFullPath(Path.Combine(destination, relative));
            if (Path.IsPathRooted(relative) || !full.StartsWith(Path.GetFullPath(destination) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("traversal");
            if (relative.Contains(':') || relative.Contains("\\\\") || relative.TrimEnd('\\').Split('\\').Any(p => p is ".." or "." or "" || p.EndsWith('.') || p.EndsWith(' '))) throw new InvalidDataException("invalid path");
            if (!seen.Add(full.TrimEnd('\\'))) throw new InvalidDataException("duplicate path");
            if (e.IsDirectory) { Directory.CreateDirectory(full); continue; }
            if (e.Size < 0 || e.Size > _maxExpandedBytes - expanded) throw new InvalidDataException("expanded limit");
            Directory.CreateDirectory(Path.GetDirectoryName(full)!); using var input = e.OpenEntryStream(); using var output = new FileStream(full, FileMode.CreateNew);
            var buffer = new byte[81920]; int countRead;
            while ((countRead = input.Read(buffer)) > 0)
            { ct.ThrowIfCancellationRequested(); expanded = checked(expanded + countRead); if (expanded > _maxExpandedBytes) throw new InvalidDataException("expanded limit"); output.Write(buffer, 0, countRead); }
        }
    }

    private PayloadBundle Bundle(string root, string tag)
    {
        var version = _versions?.GetFileVersion(Path.Combine(root, "OptiScaler.dll"));
        return new(root, string.IsNullOrWhiteSpace(version) ? tag.TrimStart('v') : version, PayloadOrigin.GitHubRelease);
    }
    private async Task<HttpResponseMessage> GetOfficialAsync(Uri uri, CancellationToken ct)
    {
        for (var redirects = 0; redirects <= 5; redirects++)
        {
            if (!IsOfficialUrl(uri.AbsoluteUri)) throw new InvalidDataException("Unofficial redirect host.");
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("OptiScalerDeployer", "1.0"));
            var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (response.RequestMessage?.RequestUri is { } final && !IsOfficialUrl(final.AbsoluteUri))
            { response.Dispose(); throw new InvalidDataException("Unofficial redirect host."); }
            if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
            {
                var location = response.Headers.Location; response.Dispose();
                if (location is null) throw new InvalidDataException("Missing redirect location.");
                uri = location.IsAbsoluteUri ? location : new Uri(uri, location); continue;
            }
            return response;
        }
        throw new InvalidDataException("Too many redirects.");
    }
    private static void RejectReparseTree(string root)
    {
        ElevationPolicy.RejectReparseAncestors(root);
        foreach (var child in Directory.EnumerateFileSystemEntries(root))
        {
            if (File.GetAttributes(child).HasFlag(FileAttributes.ReparsePoint)) throw new InvalidDataException("Cache reparse point.");
            if (Directory.Exists(child)) RejectReparseTree(child);
        }
    }

    private static JsonElement? SelectAsset(JsonElement root) => root.GetProperty("assets").EnumerateArray().Where(a => { var n = a.GetProperty("name").GetString() ?? ""; return n.EndsWith(".7z", StringComparison.OrdinalIgnoreCase) && !n.Contains("source", StringComparison.OrdinalIgnoreCase); }).SingleOrDefault();
    private static bool IsOfficialUrl(string? value) => Uri.TryCreate(value, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps && u.IsDefaultPort && string.IsNullOrEmpty(u.UserInfo) && u.Host is "github.com" or "api.github.com" or "codeload.github.com" or "objects.githubusercontent.com" or "release-assets.githubusercontent.com";
    private static string? ReadDigest(JsonElement e) => e.ValueKind == JsonValueKind.String && (e.GetString() ?? "").StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? e.GetString()![7..] : null;
    private static string Sanitize(string value) => string.Join("_", value.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries));
    private static async Task<string> HashFile(string path, CancellationToken ct) { await using var s = File.OpenRead(path); return Convert.ToHexString(await SHA256.HashDataAsync(s, ct)); }
    private static string? FindPayload(string root) { foreach (var d in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories).Prepend(root)) if (File.Exists(Path.Combine(d, "OptiScaler.dll")) && File.Exists(Path.Combine(d, "OptiScaler.ini"))) return d; return null; }
    private bool TryValidate(string path, string? expected, out string valid)
    {
        valid = Path.Combine(path, "content");
        if (!Directory.Exists(valid) || !File.Exists(Path.Combine(valid, "OptiScaler.dll")) || !File.Exists(Path.Combine(valid, "OptiScaler.ini"))) return false;
        var marker = Path.Combine(path, "integrity.json");
        if (!File.Exists(marker) || new FileInfo(marker).Length > 2 * 1024 * 1024) return false;
        try
        {
            RejectReparseTree(path);
            var integrity = JsonSerializer.Deserialize<CacheIntegrity>(File.ReadAllText(marker));
            if (integrity is null || !integrity.ArchiveHash.Equals(expected, StringComparison.OrdinalIgnoreCase)) return false;
            var files = Directory.GetFiles(valid, "*", SearchOption.AllDirectories);
            if (files.Length > _maxEntryCount || files.Length != integrity.Files.Count) return false;
            long size = 0;
            foreach (var file in files)
            {
                ElevationPolicy.RejectReparseAncestors(file); size += new FileInfo(file).Length;
                if (size > _maxExpandedBytes || !integrity.Files.TryGetValue(Path.GetRelativePath(valid, file), out var hash)
                    || !HashFile(file, default).GetAwaiter().GetResult().Equals(hash, StringComparison.OrdinalIgnoreCase)) return false;
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return false; }
    }
    private sealed record CacheIntegrity(string ArchiveHash, Dictionary<string, string> Files);
}
