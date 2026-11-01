using SamaLcd.Core.Rendering;

namespace SamaLcd.Core;

public sealed record SdVideoFile(string Name, long Size);
public sealed record SdCleanupRequest(string Id, long RequiredBytes, long FreeBytes, SdVideoFile[] Files)
{
    public long ReclaimableBytes => Files.Sum(f => f.Size);
}

public static class VideoStoragePolicy
{
    public const long ReserveBytes = 512 * 1024;
    public static long RequiredBytes(long videoBytes) => checked(videoBytes + ReserveBytes);

    public static void DeleteApprovedSnapshot(bool approved, SdVideoFile[] files, Func<string,long> size, Action<string> delete)
    {
        if (!approved) throw new OperationCanceledException();
        foreach (var file in files)
        {
            if (file.Name is "." or ".." || file.Name.IndexOfAny(['/', '\\']) >= 0 || file.Size <= 0)
                throw new IOException("Invalid SD filename");
            if (size(file.Name) != file.Size)
                throw new IOException(Localization.Localizer.Get("SD files changed. Please retry and review the new file list."));
        }
        foreach (var file in files)
        {
            delete(file.Name);
            if (size(file.Name) != 0) throw new IOException(Localization.Localizer.Get("Could not delete SD file: {0}", file.Name));
        }
    }

    public static long EncodingBudget(long[] storage, double seconds)
    {
        if (storage.Length != 6 || storage.Any(n => n < 0)) throw new IOException("Invalid storage reply");
        long free = Math.Max(storage[2], storage[5]) * 1024 - ReserveBytes;
        if (free > 0)
        {
            try { DeviceVideo.TargetBitrate(free, seconds); return free; }
            catch (IOException) { } // Encode for total capacity; ask about cleanup AFTER conversion.
        }
        long capacity = Math.Max(storage[0], storage[3]) * 1024 - ReserveBytes;
        if (capacity <= 0) throw new IOException("No usable display storage");
        DeviceVideo.TargetBitrate(capacity, seconds); // Deletion cannot help if even total capacity is insufficient.
        return capacity;
    }
}
