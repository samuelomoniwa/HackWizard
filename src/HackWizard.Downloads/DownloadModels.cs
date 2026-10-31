namespace HackWizard.Downloads;

public sealed record DownloadProgress(string Item, long Received, long? Total, string Status)
{
    public double Percent => Total is > 0 ? Math.Min(100, Received * 100.0 / Total.Value) : 0;
}

public sealed record ResolvedArtifact(string SourceId, string Version, string AssetName, string Url, string LocalPath, string Sha256, string HashSource, string? ExtractedDir);

public sealed class DownloadException : Exception
{
    public string Fix { get; }
    public DownloadException(string message, string fix, Exception? inner = null) : base(message, inner) => Fix = fix;
}
