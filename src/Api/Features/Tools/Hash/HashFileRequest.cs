using MediatR;

namespace Api.Features.Tools.Hash;

public record HashFileRequest(string FilePath) : IRequest<HashFileResponse>;
public record HashFileResponse(string Hash);
