using System.Security.Cryptography;

namespace MediaWorker.Services;

/// <summary>SHA-256 of a file's contents (ported from the old <c>HashFileHandler</c>).</summary>
public sealed class FileHashService
{
    public async Task<string> HashAsync(string filePath, CancellationToken ct)
    {
        await using var stream = File.OpenRead(filePath);
        var bytes = await SHA256.HashDataAsync(stream, ct);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
