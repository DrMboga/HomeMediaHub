using MediatR;

namespace Api.Features.Tools.Scan;

public record ScanDirectoryRequest(string Path) : IRequest<ScanDirectoryResult>;
public record ScanDirectoryResult(List<string> Files);
