using Api.Data;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace Api.Features.Tools.Scan;

public sealed class ScanDirectoryHandler : IRequestHandler<ScanDirectoryRequest, ScanDirectoryResult>
{
    private static readonly HashSet<string> SupportedExtensions =
        new(StringComparer.OrdinalIgnoreCase)
        { ".jpg", ".jpeg", ".png", ".heic", ".heif", ".mp4", ".mov", ".m4v", ".avi" };

    private readonly AppDbContext _db;

    public ScanDirectoryHandler(AppDbContext db) => _db = db;

    public async Task<ScanDirectoryResult> Handle(ScanDirectoryRequest request, CancellationToken cancellationToken)
    {
        var existing = await _db.MediaFiles
            .Select(f => f.FilePath)
            .ToHashSetAsync(cancellationToken);

        var files = Directory.EnumerateFiles(request.Path, "*", SearchOption.AllDirectories)
            .Where(f => SupportedExtensions.Contains(Path.GetExtension(f)))
            .Where(f => !existing.Contains(f))
            .OrderBy(f => f)
            .ToList();

        return new ScanDirectoryResult(files);
    }
}
