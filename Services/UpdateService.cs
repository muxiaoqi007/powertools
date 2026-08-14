using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace PowerTools.Services;

public sealed class UpdateService
{
    private readonly HttpClient _http;
    private readonly UpdateOptions _options;
    private readonly string _currentVersion;
    private readonly string _stagingRoot;
    private readonly SemaphoreSlim _checkLock = new(1, 1);
    private readonly SemaphoreSlim _downloadLock = new(1, 1);
    private CachedRelease? _cache;

    public UpdateService(HttpClient http, IOptions<UpdateOptions> options)
    {
        _http = http;
        _options = options.Value;
        _currentVersion = NormalizeVersion(_options.CurrentVersionOverride ??
            typeof(UpdateService).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ??
            typeof(UpdateService).Assembly.GetName().Version?.ToString() ?? "0.0.0");
        var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localData)) localData = Path.GetTempPath();
        _stagingRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Environment.ExpandEnvironmentVariables(
            string.IsNullOrWhiteSpace(_options.StagingRoot) ? Path.Combine(localData, "PowerTools", "Updates") : _options.StagingRoot)));
        Directory.CreateDirectory(_stagingRoot);
        if (!_http.DefaultRequestHeaders.UserAgent.Any()) _http.DefaultRequestHeaders.UserAgent.ParseAdd($"PowerTools/{_currentVersion}");
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        _http.DefaultRequestHeaders.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
    }

    public async Task<UpdateCheckResult> CheckAsync(bool refresh, CancellationToken cancellationToken)
    {
        if (!_options.Enabled) return DisabledResult();
        await _checkLock.WaitAsync(cancellationToken);
        try
        {
            if (!refresh && _cache is not null && DateTimeOffset.UtcNow - _cache.CheckedAt < TimeSpan.FromMinutes(Math.Clamp(_options.CacheMinutes, 1, 1440)))
                return BuildResult(_cache.Release);
            var release = await ReadLatestReleaseAsync(cancellationToken);
            _cache = new CachedRelease(DateTimeOffset.UtcNow, release);
            return BuildResult(release);
        }
        finally { _checkLock.Release(); }
    }

    public async Task<StagedUpdate> DownloadAsync(bool refresh, CancellationToken cancellationToken)
    {
        await _downloadLock.WaitAsync(cancellationToken);
        try
        {
            var check = await CheckAsync(refresh, cancellationToken);
            if (!check.UpdateAvailable) throw new InvalidOperationException("当前已经是最新版本。");
            if (!check.AutomaticInstallSupported || string.IsNullOrWhiteSpace(check.AssetName) || string.IsNullOrWhiteSpace(check.AssetSha256))
                throw new InvalidOperationException("该版本没有可自动校验的更新资产，请从 GitHub Release 手工下载安装。");
            var release = _cache?.Release ?? throw new InvalidOperationException("更新缓存不可用，请重新检查更新。");
            var asset = release.Assets.First(item => item.Name.Equals(check.AssetName, StringComparison.OrdinalIgnoreCase));
            ValidateDownloadUrl(asset.DownloadUrl);
            var maximumBytes = Math.Clamp(_options.MaximumDownloadMegabytes, 10, 2048) * 1024L * 1024L;
            if (asset.Size <= 0 || asset.Size > maximumBytes) throw new InvalidDataException("GitHub 更新资产大小超出允许范围。");

            var versionRoot = ResolveWithin(_stagingRoot, check.LatestVersion);
            Directory.CreateDirectory(versionRoot);
            var target = ResolveWithin(versionRoot, asset.Name);
            if (File.Exists(target) && new FileInfo(target).Length == asset.Size &&
                string.Equals(await ComputeSha256Async(target, cancellationToken), check.AssetSha256, StringComparison.OrdinalIgnoreCase))
                return BuildStaged(check, target, asset.Size);

            var temporary = ResolveWithin(versionRoot, $".{asset.Name}.{Guid.NewGuid():N}.download");
            try
            {
                using var response = await _http.GetAsync(asset.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                response.EnsureSuccessStatusCode();
                var contentLength = response.Content.Headers.ContentLength;
                if (contentLength is > 0 && contentLength > maximumBytes) throw new InvalidDataException("下载内容超过最大允许大小。");
                long total = 0;
                {
                    await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
                    await using var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
                    var buffer = new byte[81920];
                    int read;
                    while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
                    {
                        total += read;
                        if (total > maximumBytes) throw new InvalidDataException("下载内容超过最大允许大小。");
                        await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    }
                    await output.FlushAsync(cancellationToken);
                }
                if (total != asset.Size) throw new InvalidDataException($"下载大小校验失败：预期 {asset.Size}，实际 {total}。");
                var digest = await ComputeSha256Async(temporary, cancellationToken);
                if (!string.Equals(digest, check.AssetSha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("更新包 SHA-256 校验失败，文件已拒绝应用。");
                File.Move(temporary, target, true);
                return BuildStaged(check, target, total);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        finally { _downloadLock.Release(); }
    }

    private async Task<GitHubRelease> ReadLatestReleaseAsync(CancellationToken cancellationToken)
    {
        ValidateRepositoryPart(_options.RepositoryOwner, "RepositoryOwner");
        ValidateRepositoryPart(_options.RepositoryName, "RepositoryName");
        if (!string.IsNullOrWhiteSpace(_options.ChannelManifestName))
        {
            ValidateAssetName(_options.ChannelManifestName);
            var channelUrl = $"https://github.com/{_options.RepositoryOwner}/{_options.RepositoryName}/releases/latest/download/{_options.ChannelManifestName}";
            try
            {
                using var channelResponse = await _http.GetAsync(channelUrl, cancellationToken);
                if (channelResponse.IsSuccessStatusCode) return await ReadChannelManifestAsync(channelResponse, cancellationToken);
            }
            catch (HttpRequestException) { }
        }

        var baseUri = new Uri(_options.ApiBaseUrl.TrimEnd('/') + "/", UriKind.Absolute);
        if (baseUri.Scheme != Uri.UriSchemeHttps && !baseUri.IsLoopback) throw new InvalidDataException("更新 API 必须使用 HTTPS。");
        var uri = new Uri(baseUri, $"repos/{_options.RepositoryOwner}/{_options.RepositoryName}/releases/latest");
        try
        {
            using var response = await _http.GetAsync(uri, cancellationToken);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            return ReadGitHubRelease(json.RootElement);
        }
        catch (HttpRequestException) { return await ReadLatestTagFallbackAsync(cancellationToken); }
    }

    private GitHubRelease ReadGitHubRelease(JsonElement root)
    {
        var assets = new List<GitHubAsset>();
        if (root.TryGetProperty("assets", out var assetArray))
        foreach (var item in assetArray.EnumerateArray())
        {
            var name = item.GetProperty("name").GetString() ?? string.Empty;
            var downloadUrl = item.GetProperty("browser_download_url").GetString() ?? string.Empty;
            var digest = item.TryGetProperty("digest", out var digestNode) ? digestNode.GetString() : null;
            var size = item.TryGetProperty("size", out var sizeNode) ? sizeNode.GetInt64() : 0;
            assets.Add(new GitHubAsset(name, downloadUrl, size, ParseSha256(digest)));
        }
        var tag = root.GetProperty("tag_name").GetString() ?? "0.0.0";
        var htmlUrl = root.TryGetProperty("html_url", out var html) ? html.GetString() ?? string.Empty : string.Empty;
        if (!Uri.TryCreate(htmlUrl, UriKind.Absolute, out var releaseUri) || releaseUri.Scheme != Uri.UriSchemeHttps || !releaseUri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
            htmlUrl = $"https://github.com/{_options.RepositoryOwner}/{_options.RepositoryName}/releases";
        DateTimeOffset? published = root.TryGetProperty("published_at", out var publishedNode) && publishedNode.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(publishedNode.GetString(), out var timestamp) ? timestamp : null;
        return new GitHubRelease(NormalizeVersion(tag), Truncate(root.TryGetProperty("name", out var nameNode) ? nameNode.GetString() ?? tag : tag, 200),
            Truncate(root.TryGetProperty("body", out var bodyNode) ? bodyNode.GetString() ?? string.Empty : string.Empty, 30_000),
            published, htmlUrl, assets);
    }

    private async Task<GitHubRelease> ReadChannelManifestAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var root = json.RootElement;
        if (!root.TryGetProperty("schemaVersion", out var schema) || schema.GetInt32() != 1) throw new InvalidDataException("GitHub 更新通道清单版本不受支持。");
        var version = NormalizeVersion(root.GetProperty("version").GetString() ?? "0.0.0");
        var releaseUrl = root.TryGetProperty("releaseUrl", out var urlNode) ? urlNode.GetString() ?? string.Empty : string.Empty;
        if (!Uri.TryCreate(releaseUrl, UriKind.Absolute, out var releaseUri) || releaseUri.Scheme != Uri.UriSchemeHttps || !releaseUri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
            releaseUrl = $"https://github.com/{_options.RepositoryOwner}/{_options.RepositoryName}/releases/tag/v{version}";
        DateTimeOffset? published = root.TryGetProperty("publishedAt", out var publishedNode) && DateTimeOffset.TryParse(publishedNode.GetString(), out var timestamp) ? timestamp : null;
        var assets = new List<GitHubAsset>();
        if (root.TryGetProperty("assets", out var assetArray))
        foreach (var item in assetArray.EnumerateArray())
        {
            var name = item.GetProperty("name").GetString() ?? string.Empty;
            ValidateAssetName(name);
            assets.Add(new GitHubAsset(name, item.GetProperty("url").GetString() ?? string.Empty,
                item.GetProperty("size").GetInt64(), ParseSha256("sha256:" + (item.GetProperty("sha256").GetString() ?? string.Empty))));
        }
        return new GitHubRelease(version, Truncate(root.TryGetProperty("name", out var nameNode) ? nameNode.GetString() ?? version : version, 200),
            Truncate(root.TryGetProperty("notes", out var notesNode) ? notesNode.GetString() ?? string.Empty : string.Empty, 30_000), published, releaseUrl, assets);
    }

    private async Task<GitHubRelease> ReadLatestTagFallbackAsync(CancellationToken cancellationToken)
    {
        var url = $"https://github.com/{_options.RepositoryOwner}/{_options.RepositoryName}/releases/latest";
        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        var finalUri = response.RequestMessage?.RequestUri;
        var marker = "/releases/tag/";
        var path = finalUri?.AbsolutePath ?? string.Empty;
        var index = path.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (index < 0) throw new InvalidDataException("无法从 GitHub latest 重定向识别版本号。");
        var version = NormalizeVersion(Uri.UnescapeDataString(path[(index + marker.Length)..]));
        return new GitHubRelease(version, $"PowerTools {version}", string.Empty, null, finalUri!.ToString(), Array.Empty<GitHubAsset>());
    }

    private UpdateCheckResult BuildResult(GitHubRelease release)
    {
        if (!TryVersion(_currentVersion, out var current) || !TryVersion(release.Version, out var latest))
            return new UpdateCheckResult(_currentVersion, release.Version, false, "manual", release.Name, release.Notes, release.PublishedAt, release.HtmlUrl, null, null, null, false, IsDesktopHost(), "版本号格式无法比较，请打开 GitHub Release 手工检查。");
        if (latest <= current)
            return new UpdateCheckResult(_currentVersion, release.Version, false, "none", release.Name, release.Notes, release.PublishedAt, release.HtmlUrl, null, null, null, false, IsDesktopHost(), "当前已经是最新版本。");

        var deltaName = $"PowerTools-Delta-{_currentVersion}-to-{release.Version}-win-x64.zip";
        var fullName = $"PowerTools-Setup-{release.Version}-win-x64.exe";
        var asset = release.Assets.FirstOrDefault(item => item.Name.Equals(deltaName, StringComparison.OrdinalIgnoreCase) && item.Sha256 is not null);
        var mode = "delta";
        if (asset is null)
        {
            asset = release.Assets.FirstOrDefault(item => item.Name.Equals(fullName, StringComparison.OrdinalIgnoreCase) && item.Sha256 is not null);
            mode = asset is null ? "manual" : "full";
        }
        return new UpdateCheckResult(_currentVersion, release.Version, true, mode, release.Name, release.Notes, release.PublishedAt, release.HtmlUrl,
            asset?.Name, asset?.Size, asset?.Sha256, asset is not null, IsDesktopHost(), asset is null
                ? "发现新版本，但 Release 缺少带 SHA-256 digest 的自动更新资产。"
                : mode == "delta" ? "可使用增量更新。" : "没有匹配的增量包，将使用完整安装包更新。");
    }

    private UpdateCheckResult DisabledResult() => new(_currentVersion, _currentVersion, false, "disabled", "", "", null,
        $"https://github.com/{_options.RepositoryOwner}/{_options.RepositoryName}/releases", null, null, null, false, IsDesktopHost(), "自动更新已在配置中禁用。");

    private static StagedUpdate BuildStaged(UpdateCheckResult check, string path, long size) =>
        new(check.CurrentVersion, check.LatestVersion, check.Mode, path, check.AssetSha256!, size, check.ReleaseUrl, check.DesktopHost);

    private static string? ParseSha256(string? digest)
    {
        if (string.IsNullOrWhiteSpace(digest) || !digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)) return null;
        var value = digest[7..];
        return value.Length == 64 && value.All(Uri.IsHexDigit) ? value.ToLowerInvariant() : null;
    }

    private static string NormalizeVersion(string value)
    {
        var normalized = value.Trim().TrimStart('v', 'V');
        var metadata = normalized.IndexOfAny(new[] { '+', '-' });
        return metadata > 0 ? normalized[..metadata] : normalized;
    }

    private static bool TryVersion(string value, out Version version) => Version.TryParse(NormalizeVersion(value), out version!);
    private static string Truncate(string value, int length) => value.Length <= length ? value : value[..length] + "…";
    private static bool IsDesktopHost() => Environment.GetEnvironmentVariable("POWERTOOLS_DESKTOP_HOST") == "1";

    private static void ValidateRepositoryPart(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Any(character => !(char.IsLetterOrDigit(character) || character is '-' or '_' or '.')))
            throw new InvalidDataException($"Updates:{name} 格式无效。");
    }

    private static void ValidateAssetName(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 180 || value.IndexOfAny(new[] { '/', '\\' }) >= 0 || value.Contains("..", StringComparison.Ordinal))
            throw new InvalidDataException("更新资产名称格式无效。");
    }

    private static void ValidateDownloadUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Release 资产下载地址不是受信任的 GitHub HTTPS 地址。");
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
    }

    private static string ResolveWithin(string root, string relative)
    {
        if (Path.IsPathRooted(relative) || relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(part => part == ".."))
            throw new UnauthorizedAccessException("更新路径包含不安全的相对路径。");
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var path = Path.GetFullPath(Path.Combine(normalizedRoot, relative));
        if (!path.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new UnauthorizedAccessException("更新路径超出暂存目录。");
        return path;
    }

    private sealed record CachedRelease(DateTimeOffset CheckedAt, GitHubRelease Release);
    private sealed record GitHubRelease(string Version, string Name, string Notes, DateTimeOffset? PublishedAt, string HtmlUrl, IReadOnlyList<GitHubAsset> Assets);
    private sealed record GitHubAsset(string Name, string DownloadUrl, long Size, string? Sha256);
}
