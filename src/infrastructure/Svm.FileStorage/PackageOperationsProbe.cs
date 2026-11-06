using System.Globalization;
using Svm.Services.Contracts.Framework;

namespace Svm.FileStorage;

public static class PackageOperationsProbe
{
    public static async Task<IReadOnlyList<OperationsMetric>> ReadAsync(PackageFileOptions? options, CancellationToken token)
    {
        if (options is null) return [new("package.files", "Disabled", null, false, null, "NotConfigured", [])];
        var result = new List<OperationsMetric>();
        try
        {
            var root = Path.GetFullPath(options.RootPath);
            var drive = DriveInfo.GetDrives().Where(d => root.StartsWith(d.Name, StringComparison.Ordinal))
                .OrderByDescending(d => d.Name.Length).First();
            result.Add(new("disk.availableBytes", "Available", drive.AvailableFreeSpace, false, null, "ObservedAt", []));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
        { result.Add(new("disk.availableBytes", "Unavailable", null, false, null, "ObservedAt", [])); }
        try
        {
            var directory = new DirectoryInfo(options.DownloadLogDirectory);
            if (!directory.Exists || directory.LinkTarget is not null) throw new IOException();
            var segments = directory.EnumerateFiles("*.jsonl").Take(201).ToArray();
            long bytes = 0; DateTimeOffset? oldest = null;
            foreach (var segment in segments.Take(200))
            {
                token.ThrowIfCancellationRequested();
                if (segment.LinkTarget is not null || !Guid.TryParseExact(Path.GetFileNameWithoutExtension(segment.Name), "D", out var generation)) throw new IOException();
                var cursorRoot = new DirectoryInfo(Path.Combine(options.RootPath, "download-cursors"));
                if (cursorRoot.Exists && cursorRoot.LinkTarget is not null) throw new IOException();
                var cursor = new FileInfo(Path.Combine(cursorRoot.FullName, generation.ToString("D") + ".offset"));
                if (cursor.LinkTarget is not null || cursor.Exists && cursor.Length > 32) throw new IOException();
                var offset = cursor.Exists ? long.Parse(await File.ReadAllTextAsync(cursor.FullName, token), CultureInfo.InvariantCulture) : 0;
                if (offset < 0 || offset > segment.Length) throw new IOException();
                bytes = checked(bytes + segment.Length - offset);
                if (segment.Length > offset && (oldest is null || segment.LastWriteTimeUtc < oldest)) oldest = segment.LastWriteTimeUtc;
            }
            result.Add(new("download.uncollectedBytes", "Available", bytes, segments.Length > 200, oldest, "OldestSampledSegmentLastWriteAt", []));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or FormatException or OverflowException or OperationCanceledException)
        { result.Add(new("download.uncollectedBytes", "Unavailable", null, false, null, "ObservedAt", [])); }
        return result;
    }
}
