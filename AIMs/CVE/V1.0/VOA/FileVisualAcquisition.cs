using System;
using System.IO;
using System.Threading.Tasks;

using Mpai.Core;

namespace Mpai.Aims.Visual;

// Visual Object Acquisition (CVE-VOA) - file source. Reads an image from disk
// and produces a Basic Visual Object. Device dependency (a camera source, etc.)
// would be a sibling implementation behind the same interface.
public sealed class FileVisualAcquisition : IVisualAcquisitionAim
{
    private readonly string? _defaultPath;

    public FileVisualAcquisition(string? defaultPath = null) => _defaultPath = defaultPath;

    public Task<BasicVisualObject> AcquireAsync(VisualAcquisitionRequest request)
    {
        var path = request.SourcePath ?? _defaultPath
            ?? throw new ArgumentException("No image source path provided.");

        if (!File.Exists(path))
            throw new FileNotFoundException($"Image not found: {path}", path);

        var bytes = File.ReadAllBytes(path);

        AimLog.Write(
            "CVE-VOA-V1.0",
            $"acquired image: {path} ({bytes.Length:N0} bytes)");

        var visual = BasicVisualObject.FromFile(path, bytes);
        return Task.FromResult(visual);
    }
}

