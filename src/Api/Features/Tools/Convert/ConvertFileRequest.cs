using MediatR;

namespace Api.Features.Tools.Convert;

public sealed record ConvertFileRequest(string SourceFilePath, string DestFilePath, string FileType)
    : IRequest<ConvertFileResponse>;

public sealed record ConvertFileResponse(bool Success);
