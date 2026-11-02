using System.Runtime.InteropServices;

namespace CHDStudio.Models;

/// <summary>
///     Progress reported while an archive is being extracted.
/// </summary>
/// <param name="ProcessedBytes">Uncompressed bytes written so far.</param>
/// <param name="TotalBytes">Total uncompressed bytes declared by the archive, or 0 when unknown.</param>
/// <param name="CompletedEntries">Archive entries finished so far.</param>
/// <param name="TotalEntries">Total archive entries, or 0 when unknown.</param>
[StructLayout(LayoutKind.Sequential)]
internal readonly record struct ArchiveProgress(
    long ProcessedBytes,
    long TotalBytes,
    int CompletedEntries,
    int TotalEntries
)
{
    /// <summary>Gets the completed percentage (0-100).</summary>
    internal double Percent
    {
        get
        {
            if (TotalBytes > 0)
                return Math.Clamp(ProcessedBytes * 100.0 / TotalBytes, 0, 100);

            if (TotalEntries > 0)
                return Math.Clamp(CompletedEntries * 100.0 / TotalEntries, 0, 100);

            return 0;
        }
    }
}
