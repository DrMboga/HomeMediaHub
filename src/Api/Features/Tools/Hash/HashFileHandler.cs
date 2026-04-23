using System.Security.Cryptography;
using MediatR;

namespace Api.Features.Tools.Hash;

public sealed class HashFileHandler : IRequestHandler<HashFileRequest, HashFileResponse>
{
    public async Task<HashFileResponse> Handle(HashFileRequest request, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(request.FilePath);
        var bytes = await SHA256.HashDataAsync(stream, cancellationToken);
        return new HashFileResponse(Convert.ToHexString(bytes).ToLowerInvariant());
    }
}
