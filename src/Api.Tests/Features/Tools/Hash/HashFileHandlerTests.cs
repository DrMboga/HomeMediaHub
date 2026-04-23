using Api.Features.Tools.Hash;

namespace Api.Tests.Features.Tools.Hash;

public sealed class HashFileHandlerTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"hmh-hash-{Guid.NewGuid()}");
    private readonly HashFileHandler _handler = new();

    public HashFileHandlerTests() => Directory.CreateDirectory(_tempDir);
    public void Dispose() => Directory.Delete(_tempDir, recursive: true);

    [Fact]
    public async Task Handle_KnownContent_ReturnsSha256()
    {
        var path = Path.Combine(_tempDir, "hello.bin");
        await File.WriteAllBytesAsync(path, "hello"u8.ToArray());

        var result = await _handler.Handle(new HashFileRequest(path), CancellationToken.None);

        // SHA-256("hello") verified with standard test vector
        Assert.Equal("2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824", result.Hash);
    }

    [Fact]
    public async Task Handle_EmptyFile_ReturnsEmptyInputHash()
    {
        var path = Path.Combine(_tempDir, "empty.bin");
        await File.WriteAllBytesAsync(path, Array.Empty<byte>());

        var result = await _handler.Handle(new HashFileRequest(path), CancellationToken.None);

        // SHA-256 of empty input — standard test vector
        Assert.Equal("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", result.Hash);
    }

    [Fact]
    public async Task Handle_HashIsLowercaseHex()
    {
        var path = Path.Combine(_tempDir, "data.bin");
        await File.WriteAllBytesAsync(path, new byte[] { 0xDE, 0xAD, 0xBE, 0xEF });

        var result = await _handler.Handle(new HashFileRequest(path), CancellationToken.None);

        Assert.Equal(result.Hash, result.Hash.ToLowerInvariant());
        Assert.Matches("^[0-9a-f]{64}$", result.Hash);
    }

    [Fact]
    public async Task Handle_DifferentContent_ProducesDifferentHashes()
    {
        var path1 = Path.Combine(_tempDir, "a.bin");
        var path2 = Path.Combine(_tempDir, "b.bin");
        await File.WriteAllBytesAsync(path1, "abc"u8.ToArray());
        await File.WriteAllBytesAsync(path2, "xyz"u8.ToArray());

        var result1 = await _handler.Handle(new HashFileRequest(path1), CancellationToken.None);
        var result2 = await _handler.Handle(new HashFileRequest(path2), CancellationToken.None);

        Assert.NotEqual(result1.Hash, result2.Hash);
    }

    [Fact]
    public async Task Handle_NonExistentFile_ThrowsFileNotFoundException()
    {
        var req = new HashFileRequest(Path.Combine(_tempDir, "missing.bin"));

        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            _handler.Handle(req, CancellationToken.None));
    }
}
