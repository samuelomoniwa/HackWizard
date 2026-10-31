using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Serilog;

namespace HackWizard.Downloads;

public sealed record GitHubAsset(string Name, string DownloadUrl, long Size, string? Sha256Digest);
public sealed record GitHubRelease(string Tag, string Name, DateTime PublishedUtc, IReadOnlyList<GitHubAsset> Assets, string ZipballUrl);

/// <summary>GitHub REST client for "latest release" lookups with rate-limit handling.
/// HACKWIZARD_GITHUB_TOKEN (or GITHUB_TOKEN) raises the limit from 60 to 5000 requests/hour.</summary>
public sealed class GitHubReleases
{
    private readonly HttpClient _http;
    private readonly Dictionary<string, GitHubRelease> _cache = new(StringComparer.OrdinalIgnoreCase);
    public GitHubReleases(HttpClient http) => _http = http;

    public async Task<GitHubRelease> GetLatestAsync(string repo, CancellationToken ct)
    {
        lock (_cache) if (_cache.TryGetValue(repo, out var cached)) return cached;
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{repo}/releases/latest");
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            req.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
            var token = Environment.GetEnvironmentVariable("HACKWIZARD_GITHUB_TOKEN") ?? Environment.GetEnvironmentVariable("GITHUB_TOKEN");
            if (!string.IsNullOrWhiteSpace(token)) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            if (resp.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
            {
                var wait = RateLimitWait(resp);
                if (wait is { } w && w <= TimeSpan.FromMinutes(2) && attempt < 3)
                {
                    Log.Warning("GitHub rate limit hit for {Repo}; waiting {Wait}", repo, w);
                    await Task.Delay(w, ct).ConfigureAwait(false);
                    continue;
                }
                throw new DownloadException($"GitHub API rate limit reached while querying {repo}.",
                    "Wait for the limit to reset (see the log), enable 'Use cached files', or set the HACKWIZARD_GITHUB_TOKEN environment variable to a GitHub personal access token (no scopes needed).");
            }
            if (resp.StatusCode == HttpStatusCode.NotFound)
                throw new DownloadException($"{repo} has no published release (404).", "The project may have moved; update the knowledge base.");
            resp.EnsureSuccessStatusCode();
            await using var s = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(s, cancellationToken: ct).ConfigureAwait(false);
            var rel = Parse(doc.RootElement);
            lock (_cache) _cache[repo] = rel;
            return rel;
        }
        throw new DownloadException($"GitHub API unavailable for {repo}.", "Retry later.");
    }

    internal static GitHubRelease Parse(JsonElement root)
    {
        var assets = new List<GitHubAsset>();
        if (root.TryGetProperty("assets", out var arr))
            foreach (var a in arr.EnumerateArray())
            {
                string? digest = null;
                if (a.TryGetProperty("digest", out var d) && d.ValueKind == JsonValueKind.String && d.GetString() is { } v && v.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                    digest = v[7..].ToUpperInvariant();
                assets.Add(new GitHubAsset(a.GetProperty("name").GetString()!, a.GetProperty("browser_download_url").GetString()!,
                    a.TryGetProperty("size", out var sz) ? sz.GetInt64() : 0, digest));
            }
        var published = root.TryGetProperty("published_at", out var p) && p.ValueKind == JsonValueKind.String ? p.GetDateTime() : DateTime.MinValue;
        var zip = root.TryGetProperty("zipball_url", out var z) && z.ValueKind == JsonValueKind.String ? z.GetString()! : "";
        return new GitHubRelease(root.GetProperty("tag_name").GetString()!, root.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "", published, assets, zip);
    }

    private static TimeSpan? RateLimitWait(HttpResponseMessage resp)
    {
        if (resp.Headers.RetryAfter?.Delta is { } delta) return delta;
        if (resp.Headers.TryGetValues("x-ratelimit-remaining", out var rem) && rem.FirstOrDefault() == "0" &&
            resp.Headers.TryGetValues("x-ratelimit-reset", out var reset) && long.TryParse(reset.FirstOrDefault(), out var epoch))
        {
            var w = DateTimeOffset.FromUnixTimeSeconds(epoch) - DateTimeOffset.UtcNow;
            Log.Warning("GitHub rate limit resets at {Reset:u}", DateTimeOffset.FromUnixTimeSeconds(epoch));
            return w < TimeSpan.Zero ? TimeSpan.FromSeconds(1) : w;
        }
        return null;
    }
}
