namespace CHDStudio.Models;

/// <summary>Progress of a diagnostic sample upload.</summary>
/// <param name="BytesUploaded">Bytes uploaded so far.</param>
/// <param name="TotalBytes">Total size of the file.</param>
public sealed record SampleUploadProgress(long BytesUploaded, long TotalBytes)
{
    /// <summary>Upload completion as a percentage between 0 and 100.</summary>
    public double PercentComplete =>
        TotalBytes <= 0 ? 0 : Math.Clamp(BytesUploaded * 100.0 / TotalBytes, 0, 100);
}
