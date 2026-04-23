using MediatR;

namespace Api.Features.Tools.Thumbnail;

public record GenerateThumbnailRequest(string FilePath, string MediaFileId) : IRequest<ThumbnailResult>;
public record ThumbnailResult(string ThumbnailPath);
