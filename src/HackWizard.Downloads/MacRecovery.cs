using System.Security.Cryptography;
using System.Text;
using Serilog;

namespace HackWizard.Downloads;

public sealed record RecoveryImageInfo(string Product, string ImageUrl, string ImageHash, string ImageToken, string ChunklistUrl, string ChunklistHash, string ChunklistToken);
public sealed record RecoveryResult(string DmgPath, string ChunklistPath, RecoveryImageInfo Info, int ChunksVerified);

/// <summary>C# port of OpenCorePkg Utilities/macrecovery/macrecovery.py ("download" action):
/// session cookie → InstallationPayload/RecoveryImage → AssetToken downloads → chunklist verification.</summary>
public sealed class MacRecoveryClient
{
    public const string RecoveryHost = "osrecovery.apple.com";
    public const string UserAgent = "InternetRecovery/1.0";
    public const string MlbZero = "00000000000000000";
    private readonly HttpClient _http;
    private readonly ResumableDownloader _dl;

    /// <param name="httpWithoutCookies">Must be created with UseCookies = false (cookies are set manually).</param>
    public MacRecoveryClient(HttpClient httpWithoutCookies)
    {
        _http = httpWithoutCookies;
        _dl = new ResumableDownloader(httpWithoutCookies);
    }

    internal static string GenerateId(int length, Func<int, int>? rng = null)
    {
        const string hex = "0123456789ABCDEF";
        var sb = new StringBuilder(length);
        for (var i = 0; i < length; i++) sb.Append(hex[rng is null ? RandomNumberGenerator.GetInt32(16) : rng(16)]);
        return sb.ToString();
    }

    public async Task<string> GetSessionAsync(CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"http://{RecoveryHost}/");
        req.Headers.TryAddWithoutValidation("Connection", "close");
        req.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        if (resp.Headers.TryGetValues("Set-Cookie", out var cookies))
            foreach (var c in cookies)
                foreach (var part in c.Split("; "))
                    if (part.StartsWith("session=", StringComparison.Ordinal)) return part;
        throw new DownloadException("Apple recovery server did not return a session.", "Check your internet connection and that http://osrecovery.apple.com is not blocked by a proxy/firewall.");
    }

    internal static string BuildPostBody(string boardId, string mlb, string osType, Func<int, string> gen) =>
        string.Join("\n", "cid=" + gen(16), "sn=" + mlb, "bid=" + boardId, "k=" + gen(64), "fg=" + gen(64), "os=" + osType);

    internal static RecoveryImageInfo ParseInfo(string body)
    {
        var info = new Dictionary<string, string>();
        foreach (var line in body.Split('\n'))
        {
            var i = line.IndexOf(": ", StringComparison.Ordinal);
            if (i > 0) info[line[..i].Trim()] = line[(i + 2)..].Trim();
        }
        foreach (var k in new[] { "AP", "AU", "AH", "AT", "CU", "CH", "CT" })
            if (!info.ContainsKey(k)) throw new DownloadException($"Recovery response is missing '{k}'.", "Apple may not serve this board-id/OS combination any more; update the knowledge base.");
        return new RecoveryImageInfo(info["AP"], info["AU"], info["AH"], info["AT"], info["CU"], info["CH"], info["CT"]);
    }

    public async Task<RecoveryImageInfo> GetImageInfoAsync(string session, string boardId, string mlb, string osType, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, $"http://{RecoveryHost}/InstallationPayload/RecoveryImage");
        req.Headers.TryAddWithoutValidation("Connection", "close");
        req.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        req.Headers.TryAddWithoutValidation("Cookie", session);
        req.Content = new StringContent(BuildPostBody(boardId, mlb, osType, n => GenerateId(n)), Encoding.UTF8);
        req.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/plain");
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        return ParseInfo(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
    }

    public async Task<RecoveryResult> DownloadAsync(string boardId, string mlb, string osType, string outDir, IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(outDir);
        var session = await GetSessionAsync(ct).ConfigureAwait(false);
        var info = await GetImageInfoAsync(session, boardId, mlb, osType, ct).ConfigureAwait(false);
        Log.Information("macOS recovery: product {Product} image {Url} chunklist {Cnk}", info.Product, info.ImageUrl, info.ChunklistUrl);

        var cnkPath = Path.Combine(outDir, Path.GetFileName(new Uri(info.ChunklistUrl).AbsolutePath));
        var dmgPath = Path.Combine(outDir, Path.GetFileName(new Uri(info.ImageUrl).AbsolutePath));
        await _dl.DownloadAsync(info.ChunklistUrl, cnkPath, Headers(info.ChunklistToken), "Recovery chunklist", progress, ct).ConfigureAwait(false);
        int verified;
        if (File.Exists(dmgPath))
        {
            try { verified = Chunklist.Verify(dmgPath, cnkPath); progress?.Report(new DownloadProgress("macOS Recovery", 1, 1, "Cached image verified")); return new RecoveryResult(dmgPath, cnkPath, info, verified); }
            catch (InvalidDataException) { File.Delete(dmgPath); }
        }
        await _dl.DownloadAsync(info.ImageUrl, dmgPath, Headers(info.ImageToken), "macOS Recovery image", progress, ct).ConfigureAwait(false);
        progress?.Report(new DownloadProgress("macOS Recovery", 0, null, "Verifying chunklist…"));
        verified = Chunklist.Verify(dmgPath, cnkPath);
        progress?.Report(new DownloadProgress("macOS Recovery", 1, 1, $"Verified {verified} chunks"));
        return new RecoveryResult(dmgPath, cnkPath, info, verified);
    }

    private static Dictionary<string, string> Headers(string token) => new()
    {
        ["Connection"] = "close", ["User-Agent"] = UserAgent, ["Cookie"] = "AssetToken=" + token
    };
}

/// <summary>Apple chunklist (CNKL) parser and verifier, matching macrecovery.py verify_chunklist/verify_image.</summary>
public static class Chunklist
{
    public const int HeaderSize = 0x24;

    public static List<(uint Size, byte[] Sha256)> Parse(string path, out int signatureMethod)
    {
        using var f = File.OpenRead(path);
        using var br = new BinaryReader(f);
        var header = br.ReadBytes(HeaderSize);
        if (header.Length != HeaderSize || Encoding.ASCII.GetString(header, 0, 4) != "CNKL") throw new InvalidDataException("Not a chunklist (bad magic).");
        var headerSize = BitConverter.ToUInt32(header, 4);
        var fileVersion = header[8]; var chunkMethod = header[9]; signatureMethod = header[10];
        var numChunks = BitConverter.ToUInt64(header, 12);
        var chunkOffset = BitConverter.ToUInt64(header, 20);
        var sigOffset = BitConverter.ToUInt64(header, 28);
        if (headerSize != HeaderSize || fileVersion != 1 || chunkMethod != 1 || signatureMethod is not (1 or 2) || numChunks == 0 ||
            chunkOffset != HeaderSize || sigOffset != chunkOffset + HeaderSize * numChunks)
            throw new InvalidDataException("Chunklist header is invalid.");
        var list = new List<(uint, byte[])>();
        for (ulong i = 0; i < numChunks; i++)
        {
            var size = br.ReadUInt32();
            var hash = br.ReadBytes(32);
            if (hash.Length != 32) throw new InvalidDataException("Truncated chunklist.");
            list.Add((size, hash));
        }
        return list;
    }

    public static int Verify(string imagePath, string chunklistPath)
    {
        var chunks = Parse(chunklistPath, out var sigMethod);
        if (sigMethod != 1) Log.Warning("Chunklist signature method {M}: per-chunk SHA-256 verified; RSA signature not checked.", sigMethod);
        using var img = File.OpenRead(imagePath);
        var n = 0;
        foreach (var (size, expected) in chunks)
        {
            var buf = new byte[size];
            var read = 0;
            while (read < size) { var r = img.Read(buf, read, (int)size - read); if (r == 0) break; read += r; }
            if (read != size) throw new InvalidDataException($"Image truncated at chunk {n}.");
            if (!SHA256.HashData(buf).AsSpan().SequenceEqual(expected)) throw new InvalidDataException($"Chunk {n} hash mismatch — image corrupted.");
            n++;
        }
        if (img.ReadByte() != -1) throw new InvalidDataException("Image has extra data after the last chunk.");
        return n;
    }
}
