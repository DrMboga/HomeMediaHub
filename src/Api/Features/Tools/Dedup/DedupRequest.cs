using MediatR;

namespace Api.Features.Tools.Dedup;

public record DedupRequest(string Hash) : IRequest<DedupResponse>;
public record DedupResponse(bool Exists, Guid? MediaFileId);
