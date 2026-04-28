using System.Diagnostics;
using MediatR;

namespace Api.Features.Tools.Convert;

public sealed class ConvertFileHandler : IRequestHandler<ConvertFileRequest, ConvertFileResponse>
{
    public async Task<ConvertFileResponse> Handle(ConvertFileRequest request, CancellationToken ct)
    {
        if (request.FileType == "heic")
        {
            await RunAsync("heif-convert", ["-q", "90", request.SourceFilePath, request.DestFilePath], ct);
            // --Orientation excludes the Orientation tag from the copy: heif-convert already
            // applied the rotation physically, so copying Orientation back would cause AutoOrient
            // to rotate again, producing a double-rotation (upside-down thumbnails).
            await RunAsync("exiftool", ["-TagsFromFile", request.SourceFilePath, "--Orientation", request.DestFilePath, "-overwrite_original", "-q"], ct);
        }
        else if (request.FileType == "video")
        {
            await RunAsync("ffmpeg", ["-i", request.SourceFilePath, "-ss", "00:00:01", "-vframes", "1", "-q:v", "2", request.DestFilePath, "-y"], ct);
        }

        return new ConvertFileResponse(true);
    }

    private static async Task RunAsync(string exe, string[] args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);

        using var proc = Process.Start(psi)!;
        await proc.WaitForExitAsync(ct);

        if (proc.ExitCode != 0)
        {
            var err = await proc.StandardError.ReadToEndAsync(ct);
            throw new InvalidOperationException($"{exe} exited {proc.ExitCode}: {err}");
        }
    }
}
