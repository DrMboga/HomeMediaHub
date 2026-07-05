using MediaWorker.Services;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Metadata.Profiles.Exif;
using SixLabors.ImageSharp.PixelFormats;

namespace MediaWorker.Tests.Services;

public sealed class MetadataServiceTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"hmh-meta-{Guid.NewGuid()}");
    private readonly MetadataService _service = new();

    public MetadataServiceTests() => Directory.CreateDirectory(_tempDir);
    public void Dispose() => Directory.Delete(_tempDir, recursive: true);

    private async Task<string> SaveJpegAsync(int width = 100, int height = 80, Action<ExifProfile>? configureExif = null)
    {
        var path = Path.Combine(_tempDir, $"{Guid.NewGuid()}.jpg");
        using var image = new Image<Rgba32>(width, height);
        if (configureExif is not null)
        {
            var profile = new ExifProfile();
            configureExif(profile);
            image.Metadata.ExifProfile = profile;
        }
        await image.SaveAsJpegAsync(path);
        return path;
    }

    [Fact]
    public async Task Extract_NoExif_ReturnsBasicMetadata()
    {
        var path = await SaveJpegAsync(200, 150);

        var result = await _service.ExtractAsync(path, null, CancellationToken.None);

        Assert.Equal("image/jpeg", result.MimeType);
        Assert.Equal(200, result.Width);
        Assert.Equal(150, result.Height);
        Assert.True(result.FileSize > 0);
        // No EXIF date → falls back to the filesystem mtime (rsync/scp preserve it from the source).
        Assert.NotNull(result.TakenAt);
        Assert.Equal(new FileInfo(path).LastWriteTimeUtc, result.TakenAt!.Value, TimeSpan.FromSeconds(2));
        Assert.Null(result.Lat);
        Assert.Null(result.Lon);
        Assert.Null(result.CameraMake);
        Assert.Null(result.CameraModel);
    }

    [Fact]
    public async Task Extract_CameraExif_ReturnsMakeAndModel()
    {
        var path = await SaveJpegAsync(configureExif: p =>
        {
            p.SetValue(ExifTag.Make, "Apple");
            p.SetValue(ExifTag.Model, "iPhone 15 Pro");
        });

        var result = await _service.ExtractAsync(path, null, CancellationToken.None);

        Assert.Equal("Apple", result.CameraMake);
        Assert.Equal("iPhone 15 Pro", result.CameraModel);
    }

    [Fact]
    public async Task Extract_DateTimeOriginalExif_ReturnsTakenAt()
    {
        var path = await SaveJpegAsync(configureExif: p =>
            p.SetValue(ExifTag.DateTimeOriginal, "2023:06:15 12:30:00"));

        var result = await _service.ExtractAsync(path, null, CancellationToken.None);

        Assert.Equal(new DateTime(2023, 6, 15, 12, 30, 0), result.TakenAt);
    }

    [Fact]
    public async Task Extract_InvalidDateTimeExif_FallsBackToFilesystemMtime()
    {
        var path = await SaveJpegAsync(configureExif: p =>
            p.SetValue(ExifTag.DateTimeOriginal, "not-a-date"));

        var result = await _service.ExtractAsync(path, null, CancellationToken.None);

        // Unparseable EXIF date → filesystem mtime fallback (not null).
        Assert.NotNull(result.TakenAt);
        Assert.Equal(new FileInfo(path).LastWriteTimeUtc, result.TakenAt!.Value, TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task Extract_NorthEastGps_ReturnsPositiveCoordinates()
    {
        // 51°30'0" N, 0°7'30" E
        var path = await SaveJpegAsync(configureExif: p =>
        {
            p.SetValue(ExifTag.GPSLatitude, new[] { new Rational(51, 1), new Rational(30, 1), new Rational(0, 1) });
            p.SetValue(ExifTag.GPSLatitudeRef, "N");
            p.SetValue(ExifTag.GPSLongitude, new[] { new Rational(0, 1), new Rational(7, 1), new Rational(30, 1) });
            p.SetValue(ExifTag.GPSLongitudeRef, "E");
        });

        var result = await _service.ExtractAsync(path, null, CancellationToken.None);

        Assert.NotNull(result.Lat);
        Assert.NotNull(result.Lon);
        Assert.Equal(51.5, result.Lat!.Value, precision: 2);
        Assert.True(result.Lon > 0);
    }

    [Theory]
    [InlineData("S", "E", true,  false)]
    [InlineData("N", "W", false, true)]
    [InlineData("S", "W", true,  true)]
    public async Task Extract_GpsHemisphere_CorrectlyNegatesCoordinates(
        string latRef, string lonRef, bool expectNegativeLat, bool expectNegativeLon)
    {
        var path = await SaveJpegAsync(configureExif: p =>
        {
            p.SetValue(ExifTag.GPSLatitude, new[] { new Rational(33, 1), new Rational(52, 1), new Rational(0, 1) });
            p.SetValue(ExifTag.GPSLatitudeRef, latRef);
            p.SetValue(ExifTag.GPSLongitude, new[] { new Rational(151, 1), new Rational(12, 1), new Rational(0, 1) });
            p.SetValue(ExifTag.GPSLongitudeRef, lonRef);
        });

        var result = await _service.ExtractAsync(path, null, CancellationToken.None);

        Assert.Equal(expectNegativeLat, result.Lat < 0);
        Assert.Equal(expectNegativeLon, result.Lon < 0);
    }

    [Theory]
    [InlineData(".jpg",  "image/jpeg")]
    [InlineData(".jpeg", "image/jpeg")]
    [InlineData(".png",  "image/png")]
    [InlineData(".gif",  "image/gif")]
    [InlineData(".webp", "image/webp")]
    [InlineData(".heic", "image/heic")]
    public async Task Extract_FileExtension_DeterminesMimeType(string extension, string expectedMime)
    {
        // ImageSharp loads by content magic bytes, not extension — mime type is extension-only logic
        using var image = new Image<Rgba32>(10, 10);
        var path = Path.Combine(_tempDir, $"{Guid.NewGuid()}{extension}");
        await image.SaveAsJpegAsync(path);

        var result = await _service.ExtractAsync(path, null, CancellationToken.None);

        Assert.Equal(expectedMime, result.MimeType);
    }
}
