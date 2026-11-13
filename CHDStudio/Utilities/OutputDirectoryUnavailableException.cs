using System.Diagnostics.CodeAnalysis;

namespace CHDStudio.Utilities;

/// <summary>
///     Raised when a directory needed to write extracted files cannot be created. It identifies a
///     problem with the chosen output location (permissions, a read-only or disconnected volume,
///     or Windows Controlled folder access) rather than a defect in the source image, so callers
///     must not attach CHD decode diagnostics or report it as a corrupt file.
/// </summary>
[SuppressMessage(
    "Roslynator",
    "RCS1194",
    Justification = "The folder/reason constructor is the only one callers use; formatter-based serialization is obsolete on modern .NET."
)]
internal sealed class OutputDirectoryUnavailableException : IOException
{
    /// <summary>
    ///     Initializes a new instance of the <see cref="OutputDirectoryUnavailableException" />
    ///     class.
    /// </summary>
    public OutputDirectoryUnavailableException()
    {
    }

    /// <summary>
    ///     Initializes a new instance of the <see cref="OutputDirectoryUnavailableException" />
    ///     class with a caller-supplied message.
    /// </summary>
    /// <param name="message">Description of the failure.</param>
    public OutputDirectoryUnavailableException(string message)
        : base(message)
    {
    }

    /// <summary>
    ///     Initializes a new instance of the <see cref="OutputDirectoryUnavailableException" />
    ///     class with a caller-supplied message and inner exception.
    /// </summary>
    /// <param name="message">Description of the failure.</param>
    /// <param name="innerException">Exception that caused this one.</param>
    public OutputDirectoryUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>
    ///     Initializes a new instance of the <see cref="OutputDirectoryUnavailableException" />
    ///     class for a folder that could not be created.
    /// </summary>
    /// <param name="directory">Directory that could not be created.</param>
    /// <param name="reason">Why the file system refused to create it.</param>
    public OutputDirectoryUnavailableException(string directory, string reason)
        : base(BuildMessage(directory, reason))
    {
    }

    /// <summary>Builds the user-facing explanation, including the platform-specific remedy.</summary>
    /// <param name="directory">Directory that could not be created.</param>
    /// <param name="reason">Why the file system refused to create it.</param>
    /// <returns>A message that names the folder and what the user can do about it.</returns>
    private static string BuildMessage(string directory, string reason)
    {
        var remedy = OperatingSystem.IsWindows()
            ? "Choose another output folder; if this one is intended, check that it is writable and that Windows Controlled folder access allows CHD Studio, or add an exclusion to your security software."
            : "Choose another output folder; if this one is intended, check its permissions and free space, and whether it is on a read-only or disconnected volume.";

        return $"The output folder \"{directory}\" cannot be written ({reason}). {remedy}";
    }
}
