using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HackWizard.Core.Logging;
using Serilog;

namespace HackWizard.Core.Rules;

/// <summary>Loads the knowledge base from %ProgramData%\HackWizard\Rules (seeded from embedded resources)
/// and updates it from a hash-pinned, optionally signed JSON manifest.</summary>
public sealed class KnowledgeBaseLoader
{
    /// <summary>ECDSA P-256 public key (SubjectPublicKeyInfo, base64) for manifest signatures. See tools/New-ManifestKey.ps1.
    /// Forks publishing their own knowledge base should set their own key. Empty = per-file SHA-256 only.</summary>
    public static string ManifestPublicKey { get; set; } = "";

    public static KnowledgeBase LoadFromDirectory(string dir)
    {
        var manifest = Read<KbManifest>(Path.Combine(dir, "manifest.json"));
        foreach (var (rel, hash) in manifest.Files)
        {
            var full = Path.Combine(dir, rel.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(full)) throw new InvalidDataException($"Knowledge-base file missing: {rel}");
            if (!string.IsNullOrEmpty(hash) && !Sha256File(full).Equals(hash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Knowledge-base file hash mismatch: {rel}. Run tools/Update-RulesManifest after editing rules, or reinstall HackWizard.");
        }
        var rules = new List<KbRule>();
        foreach (var f in Directory.GetFiles(Path.Combine(dir, "kb"), "rules-*.json").OrderBy(f => f, StringComparer.Ordinal))
            rules.AddRange(Read<KbRuleFile>(f).Rules);
        var dup = rules.GroupBy(r => r.Id).FirstOrDefault(g => g.Count() > 1);
        if (dup != null) throw new InvalidDataException($"Duplicate rule id '{dup.Key}'.");
        return new KnowledgeBase
        {
            Manifest = manifest,
            Rules = rules,
            Cpu = Read<KbCpuCodenames>(Path.Combine(dir, "kb", "cpu-codenames.json")),
            Sources = Read<KbKextSources>(Path.Combine(dir, "kb", "sources.json")),
            Macos = Read<KbMacosVersions>(Path.Combine(dir, "kb", "macos.json")),
            Models = Read<KbModels>(Path.Combine(dir, "kb", "models.json")),
        };
    }

    public static KnowledgeBase LoadOrSeed()
    {
        var dir = AppPaths.Rules;
        var embedded = EmbeddedVersion();
        var onDisk = File.Exists(Path.Combine(dir, "manifest.json")) ? TryRead<KbManifest>(Path.Combine(dir, "manifest.json"))?.KbVersion : null;
        // A newer HackWizard build ships a newer KB: replace an older on-disk copy.
        if (onDisk is null || CompareVersions(embedded, onDisk) > 0) ExtractEmbedded(dir);
        try { return LoadFromDirectory(dir); }
        catch (Exception ex)
        {
            Log.Warning(ex, "On-disk knowledge base invalid; restoring embedded copy");
            ExtractEmbedded(dir);
            return LoadFromDirectory(dir);
        }
    }

    private static string EmbeddedVersion()
    {
        using var s = typeof(KnowledgeBaseLoader).Assembly.GetManifestResourceStream("Rules/manifest.json");
        if (s is null) return "0.0.0";
        return JsonSerializer.Deserialize<KbManifest>(s, KnowledgeBase.JsonOptions)?.KbVersion ?? "0.0.0";
    }

    public static void ExtractEmbedded(string dir)
    {
        var asm = typeof(KnowledgeBaseLoader).Assembly;
        if (Directory.Exists(Path.Combine(dir, "kb"))) Directory.Delete(Path.Combine(dir, "kb"), true);
        foreach (var name in asm.GetManifestResourceNames().Where(n => n.StartsWith("Rules/", StringComparison.Ordinal)))
        {
            var rel = name["Rules/".Length..].Replace('\\', '/');
            var target = Path.Combine(dir, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var s = asm.GetManifestResourceStream(name)!;
            using var f = File.Create(target);
            s.CopyTo(f);
        }
    }

    /// <summary>Downloads a newer KB if the remote manifest version is higher and its signature/hashes verify.</summary>
    public static async Task<string> UpdateAsync(HttpClient http, KnowledgeBase current, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(current.Manifest.UpdateUrl)) return "No update URL configured in Rules/manifest.json.";
        var baseUrl = current.Manifest.UpdateUrl.TrimEnd('/');
        var manifestJson = await http.GetStringAsync(baseUrl + "/manifest.json", ct).ConfigureAwait(false);
        var remote = JsonSerializer.Deserialize<KbManifest>(manifestJson, KnowledgeBase.JsonOptions) ?? throw new InvalidDataException("Invalid remote manifest.");
        if (!VerifySignature(remote)) throw new InvalidDataException("Remote knowledge-base manifest signature is invalid. Update refused.");
        if (CompareVersions(remote.KbVersion, current.Manifest.KbVersion) <= 0) return $"Knowledge base is up to date ({current.Manifest.KbVersion}).";

        var staging = Path.Combine(AppPaths.Work, "kb-update-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        foreach (var (rel, hash) in remote.Files)
        {
            if (rel.Contains("..", StringComparison.Ordinal) || Path.IsPathRooted(rel)) throw new InvalidDataException($"Illegal path in manifest: {rel}");
            var bytes = await http.GetByteArrayAsync($"{baseUrl}/{rel}", ct).ConfigureAwait(false);
            if (!Convert.ToHexString(SHA256.HashData(bytes)).Equals(hash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Hash mismatch for {rel}. Update refused.");
            var target = Path.Combine(staging, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await File.WriteAllBytesAsync(target, bytes, ct).ConfigureAwait(false);
        }
        await File.WriteAllTextAsync(Path.Combine(staging, "manifest.json"), manifestJson, ct).ConfigureAwait(false);
        _ = LoadFromDirectory(staging);

        var dir = AppPaths.Rules.TrimEnd('\\', '/');
        var backup = dir + ".bak";
        if (Directory.Exists(backup)) Directory.Delete(backup, true);
        Directory.Move(dir, backup);
        Directory.Move(staging, dir);
        Log.Information("Knowledge base updated {Old} -> {New}", current.Manifest.KbVersion, remote.KbVersion);
        return $"Knowledge base updated to {remote.KbVersion}.";
    }

    public static bool VerifySignature(KbManifest m)
    {
        if (string.IsNullOrEmpty(ManifestPublicKey))
        {
            Log.Warning("No manifest public key configured: knowledge-base update verified by per-file SHA-256 only (HTTPS transport).");
            return true;
        }
        if (string.IsNullOrEmpty(m.Signature)) return false;
        using var ecdsa = ECDsa.Create();
        ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(ManifestPublicKey), out _);
        return ecdsa.VerifyData(CanonicalBytes(m), Convert.FromBase64String(m.Signature), HashAlgorithmName.SHA256);
    }

    /// <summary>"kbVersion\n" + ordinal-sorted "path:SHA256\n" lines. Must match tools/Sign-Manifest.ps1.</summary>
    public static byte[] CanonicalBytes(KbManifest m)
    {
        var sb = new StringBuilder();
        sb.Append(m.KbVersion).Append('\n');
        foreach (var kv in m.Files.OrderBy(k => k.Key, StringComparer.Ordinal)) sb.Append(kv.Key).Append(':').Append(kv.Value.ToUpperInvariant()).Append('\n');
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    public static int CompareVersions(string a, string b)
    {
        Version.TryParse(a, out var va); Version.TryParse(b, out var vb);
        return (va ?? new Version(0, 0)).CompareTo(vb ?? new Version(0, 0));
    }

    public static T Read<T>(string path) =>
        JsonSerializer.Deserialize<T>(File.ReadAllText(path), KnowledgeBase.JsonOptions) ?? throw new InvalidDataException($"Could not parse {path}");

    private static T? TryRead<T>(string path) where T : class { try { return Read<T>(path); } catch { return null; } }

    public static string Sha256File(string path)
    {
        using var f = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(f));
    }
}
