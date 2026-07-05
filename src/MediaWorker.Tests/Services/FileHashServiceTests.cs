using MediaWorker.Services;

namespace MediaWorker.Tests.Services;

public sealed class FileHashServiceTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"hmh-hash-{Guid.NewGuid()}");
    private readonly FileHashService _service = new();

    public FileHashServiceTests() => Directory.CreateDirectory(_tempDir);
    public void Dispose() => Directory.Delete(_tempDir, recursive: true);

    [Fact]
    public async Task Hash_KnownContent_ReturnsSha256()
    {
        var path = Path.Combine(_tempDir, "hello.bin");
        await File.WriteAllBytesAsync(path, "hello"u8.ToArray());

        var hash = await _service.HashAsync(path, CancellationToken.None);

        // SHA-256("hello") verified with standard test vector
        Assert.Equal("2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824", hash);
    }

    [Fact]
    public async Task Hash_EmptyFile_ReturnsEmptyInputHash()
    {
        var path = Path.Combine(_tempDir, "empty.bin");
        await File.WriteAllBytesAsync(path, Array.Empty<byte>());

        var hash = await _service.HashAsync(path, CancellationToken.None);

        Assert.Equal("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", hash);
    }

    [Fact]
    public async Task Hash_IsLowercaseHex()
    {
        var path = Path.Combine(_tempDir, "data.bin");
        await File.WriteAllBytesAsync(path, new byte[] { 0xDE, 0xAD, 0xBE, 0xEF });

        var hash = await _service.HashAsync(path, CancellationToken.None);

        Assert.Equal(hash, hash.ToLowerInvariant());
        Assert.Matches("^[0-9a-f]{64}$", hash);
    }

    [Fact]
    public async Task Hash_DifferentContent_ProducesDifferentHashes()
    {
        var path1 = Path.Combine(_tempDir, "a.bin");
        var path2 = Path.Combine(_tempDir, "b.bin");
        await File.WriteAllBytesAsync(path1, "abc"u8.ToArray());
        await File.WriteAllBytesAsync(path2, "xyz"u8.ToArray());

        var h1 = await _service.HashAsync(path1, CancellationToken.None);
        var h2 = await _service.HashAsync(path2, CancellationToken.None);

        Assert.NotEqual(h1, h2);
    }

    [Fact]
    public async Task Hash_NonExistentFile_ThrowsFileNotFoundException()
    {
        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            _service.HashAsync(Path.Combine(_tempDir, "missing.bin"), CancellationToken.None));
    }
}
