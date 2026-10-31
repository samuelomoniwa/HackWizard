using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;
using HackWizard.Core.Rules;

namespace HackWizard.Downloads;

/// <summary>Resolves knowledge-base sources to verified local files using a versioned cache
/// (cache\{sourceId}\{version}\{asset} + cache\{sourceId}\latest.json). Every URL/version/hash is logged.</summary>
public sealed class DownloadManager
{
    private readonly GitHubReleases _gh;
    private readonly ResumableDownloader _dl;
    private readonly string _cache;
    public bool Offline { get; set; }
    public List<ResolvedArtifact> Log { get; } = new();

    public DownloadManager(HttpClient http, string cacheDir)
    {
        _gh = new GitHubReleases(http); _dl = new ResumableDownloader(http); _cache = cacheDir;
        Directory.CreateDirectory(_cache);
    }

    private sealed record CacheEntry(string Version, string AssetName, string Url, string Sha256, string HashSource, DateTime ResolvedUtc);

    public async Task<ResolvedArtifact> GetAsync(KbSource src, IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        var dir = Path.Combine(_cache, Sanitize(src.Id));
        var latestFile = Path.Combine(dir, "latest.json");
        ResolvedArtifact result;

        if (Offline)
        {
            if (!File.Exists(latestFile)) throw new DownloadException($"{src.Id}: not in the local cache.", "Turn off offline mode and connect to the internet once.");
            var e = JsonSerializer.Deserialize<CacheEntry>(await File.ReadAllTextAsync(latestFile, ct).ConfigureAwait(false))!;
            var local = Path.Combine(dir, Sanitize(e.Version), e.AssetName);
            if (!File.Exists(local) || !ResumableDownloader.Sha256(local).Equals(e.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new DownloadException($"{src.Id}: cached file missing or corrupted.", "Turn off offline mode to re-download.");
            progress?.Report(new DownloadProgress(src.Id, 1, 1, $"Cached {e.Version}"));
            result = new ResolvedArtifact(src.Id, e.Version, e.AssetName, e.Url, local, e.Sha256, e.HashSource + " (cached)", null);
        }
        else if (src.Type == "raw")
        {
            if (string.IsNullOrWhiteSpace(src.Url)) throw new DownloadException($"{src.Id}: no URL configured.", "Optional tool; configure its URL in sources.json to enable it.");
            var assetName = Path.GetFileName(new Uri(src.Url).AbsolutePath);
            var version = DateTime.UtcNow.ToString("yyyyMMdd");
            var local = Path.Combine(dir, version, assetName);
            progress?.Report(new DownloadProgress(src.Id, 0, null, "Downloading " + src.Url));
            var sha = await _dl.DownloadAsync(src.Url, local, null, src.Id, progress, ct).ConfigureAwait(false);
            string hashSource;
            if (!string.IsNullOrEmpty(src.Sha256))
            {
                if (!sha.Equals(src.Sha256, StringComparison.OrdinalIgnoreCase))
                { File.Delete(local); throw new DownloadException($"{src.Id}: SHA-256 mismatch (expected {src.Sha256}, got {sha}).", "Upstream file changed; update the knowledge base."); }
                hashSource = "pinned in knowledge base";
            }
            else hashSource = "computed (raw file, no upstream digest; logged)";
            result = new ResolvedArtifact(src.Id, version, assetName, src.Url, local, sha, hashSource, null);
        }
        else if (src.Type == "github-zipball")
        {
            var rel = await _gh.GetLatestAsync(src.Repo, ct).ConfigureAwait(false);
            if (string.IsNullOrEmpty(rel.ZipballUrl)) throw new DownloadException($"{src.Id}: release has no source archive.", "Update the knowledge base.");
            var assetName = $"{src.Repo.Replace('/', '-')}-{Sanitize(rel.Tag)}-source.zip";
            var local = Path.Combine(dir, Sanitize(rel.Tag), assetName);
            var sha = File.Exists(local) ? ResumableDownloader.Sha256(local)
                : await _dl.DownloadAsync(rel.ZipballUrl, local, null, $"{src.Id} {rel.Tag}", progress, ct).ConfigureAwait(false);
            result = new ResolvedArtifact(src.Id, rel.Tag, assetName, rel.ZipballUrl, local, sha, "computed (GitHub source archive; logged)", null);
        }
        else
        {
            var rel = await _gh.GetLatestAsync(src.Repo, ct).ConfigureAwait(false);
            var asset = SelectAsset(rel.Assets, src.AssetPattern, src.ExcludePattern)
                        ?? throw new DownloadException($"{src.Id}: no asset matching '{src.AssetPattern}' in {src.Repo} {rel.Tag}.", "Upstream changed its release file names. Update the knowledge base.");
            var local = Path.Combine(dir, Sanitize(rel.Tag), asset.Name);
            string sha;
            if (File.Exists(local) && (asset.Sha256Digest is null || ResumableDownloader.Sha256(local).Equals(asset.Sha256Digest, StringComparison.OrdinalIgnoreCase)))
            {
                sha = ResumableDownloader.Sha256(local);
                progress?.Report(new DownloadProgress(src.Id, asset.Size, asset.Size, $"Cached {rel.Tag}"));
            }
            else sha = await _dl.DownloadAsync(asset.DownloadUrl, local, null, $"{src.Id} {rel.Tag}", progress, ct).ConfigureAwait(false);

            string hashSource;
            if (asset.Sha256Digest != null)
            {
                if (!sha.Equals(asset.Sha256Digest, StringComparison.OrdinalIgnoreCase))
                { File.Delete(local); throw new DownloadException($"{src.Id}: SHA-256 mismatch with GitHub's published digest.", "The download was corrupted or tampered with. Retry."); }
                hashSource = "verified against GitHub asset digest";
            }
            else hashSource = "computed (GitHub published no digest for this asset; logged)";
            result = new ResolvedArtifact(src.Id, rel.Tag, asset.Name, asset.DownloadUrl, local, sha, hashSource, null);
        }

        if (!Offline)
        {
            Directory.CreateDirectory(dir);
            await File.WriteAllTextAsync(latestFile, JsonSerializer.Serialize(new CacheEntry(result.Version, result.AssetName, result.Url, result.Sha256, result.HashSource, DateTime.UtcNow)), ct).ConfigureAwait(false);
        }

        if (result.LocalPath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            var ex = result.LocalPath + ".extracted";
            if (!Directory.Exists(ex))
            {
                var tmp = ex + ".tmp";
                if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
                ZipFile.ExtractToDirectory(result.LocalPath, tmp, overwriteFiles: true);
                Directory.Move(tmp, ex);
            }
            result = result with { ExtractedDir = ex };
        }

        Serilog.Log.Information("Artifact {Id} {Version} {Url} sha256={Sha} ({HashSource}) -> {Path}", result.SourceId, result.Version, result.Url, result.Sha256, result.HashSource, result.LocalPath);
        lock (Log) Log.Add(result);
        return result;
    }

    internal static GitHubAsset? SelectAsset(IEnumerable<GitHubAsset> assets, string pattern, string? exclude) =>
        assets.Where(a => Regex.IsMatch(a.Name, pattern) && (exclude is null || !Regex.IsMatch(a.Name, exclude)))
              .OrderBy(a => a.Name, StringComparer.Ordinal).FirstOrDefault();

    /// <summary>Finds a top-level bundle/file by name inside an extracted archive, ignoring __MACOSX and Debug builds.</summary>
    public static string FindInExtracted(string extractedDir, string name)
    {
        var sep = Path.DirectorySeparatorChar;
        bool Ok(string p) => !p.Contains("__MACOSX", StringComparison.Ordinal) && !p.Split(sep).Any(s => s.Equals("Debug", StringComparison.OrdinalIgnoreCase));
        var isKext = name.EndsWith(".kext", StringComparison.OrdinalIgnoreCase);
        var hits = isKext ? Directory.GetDirectories(extractedDir, name, SearchOption.AllDirectories) : Directory.GetFiles(extractedDir, name, SearchOption.AllDirectories);
        var best = hits.Where(Ok)
                       .Where(h => !isKext || !Path.GetRelativePath(extractedDir, h).Contains($".kext{sep}", StringComparison.OrdinalIgnoreCase))
                       .OrderBy(h => h.Length).FirstOrDefault();
        return best ?? throw new DownloadException($"'{name}' not found inside {Path.GetFileName(extractedDir)}.", "Upstream archive layout changed; update the knowledge base.");
    }

    public void WriteLog(string path) => File.WriteAllText(path, JsonSerializer.Serialize(Log, new JsonSerializerOptions { WriteIndented = true }));

    private static string Sanitize(string s) => string.Concat(s.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
}
