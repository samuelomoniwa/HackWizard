using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Serilog;

namespace HackWizard.Downloads;

/// <summary>HTTP downloader with resume (Range), retries with exponential back-off, an inactivity timeout and SHA-256 hashing.</summary>
public sealed class ResumableDownloader
{
    private readonly HttpClient _http;
    public int MaxAttempts { get; init; } = 5;
    public TimeSpan InactivityTimeout { get; init; } = TimeSpan.FromSeconds(60);

    public ResumableDownloader(HttpClient http) => _http = http;

    public async Task<string> DownloadAsync(string url, string target, IDictionary<string, string>? headers, string label,
        IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var partial = target + ".partial";
        Exception? last = null;
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await DownloadOnceAsync(url, partial, headers, label, progress, ct).ConfigureAwait(false);
                if (File.Exists(target)) File.Delete(target);
                File.Move(partial, target);
                return Sha256(target);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
            {
                throw new DownloadException($"{label}: server returned {(int)ex.StatusCode!} for {url}", "The file may have moved. Update the knowledge base (Compatibility step) or try again later.", ex);
            }
            catch (Exception ex)
            {
                last = ex;
                var delay = TimeSpan.FromSeconds(Math.Min(60, Math.Pow(2, attempt)));
                Log.Warning(ex, "Download attempt {Attempt}/{Max} failed for {Url}; retrying in {Delay}", attempt, MaxAttempts, url, delay);
                progress?.Report(new DownloadProgress(label, 0, null, $"Retry {attempt}/{MaxAttempts} in {delay.TotalSeconds:F0}s: {ex.Message}"));
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
        }
        throw new DownloadException($"{label}: download failed after {MaxAttempts} attempts.", "Check your internet connection / proxy and retry. Completed parts are kept and the download resumes.", last);
    }

    private async Task DownloadOnceAsync(string url, string partial, IDictionary<string, string>? headers, string label, IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        long existing = File.Exists(partial) ? new FileInfo(partial).Length : 0;
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (headers != null) foreach (var (k, v) in headers) req.Headers.TryAddWithoutValidation(k, v);
        if (existing > 0) req.Headers.Range = new RangeHeaderValue(existing, null);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(InactivityTimeout);
        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
        if (resp.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable) { File.Delete(partial); throw new IOException("Range not satisfiable; restarting download."); }
        resp.EnsureSuccessStatusCode();

        var append = existing > 0 && resp.StatusCode == HttpStatusCode.PartialContent;
        if (!append) existing = 0;
        var total = resp.Content.Headers.ContentLength is { } len ? len + existing : (long?)null;

        await using var src = await resp.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
        await using var dst = new FileStream(partial, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, true);
        var buffer = new byte[1 << 16];
        long received = existing; var lastReport = DateTime.MinValue;
        while (true)
        {
            cts.CancelAfter(InactivityTimeout);
            var n = await src.ReadAsync(buffer, cts.Token).ConfigureAwait(false);
            if (n == 0) break;
            await dst.WriteAsync(buffer.AsMemory(0, n), cts.Token).ConfigureAwait(false);
            received += n;
            if ((DateTime.UtcNow - lastReport).TotalMilliseconds > 250) { progress?.Report(new DownloadProgress(label, received, total, "Downloading")); lastReport = DateTime.UtcNow; }
        }
        if (total is { } t && received != t) throw new IOException($"Incomplete download ({received}/{t} bytes).");
        progress?.Report(new DownloadProgress(label, received, total, "Downloaded"));
    }

    public static string Sha256(string path)
    {
        using var f = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(f));
    }
}
