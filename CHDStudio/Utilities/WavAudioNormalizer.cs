#if WINDOWS
using NAudio.Wave;
#endif

namespace CHDStudio.Utilities;

/// <summary>
///     Normalizes WAVE tracks to the format both encoders require for cue WAVE tracks:
///     exactly 44100 Hz, stereo, 16-bit PCM. Non-standard rips (22.05 kHz, mono, 8/24-bit or
///     float WAVE) otherwise fail in chdman ("unsupported samplerate") and in CHDSharp
///     ("Unsupported samplerate - only 44100 is supported").
/// </summary>
internal static class WavAudioNormalizer
{
    /// <summary>Returns whether <paramref name="wavPath" /> is a WAVE track the encoders cannot consume directly.</summary>
    /// <param name="wavPath">Path of the WAVE file to inspect.</param>
    /// <returns>True when the file is a readable WAVE whose format is not 44100 Hz stereo 16-bit PCM.</returns>
    internal static bool NeedsNormalization(string wavPath)
    {
        return WavHeaderSanitizer.TryGetFormat(wavPath, out var format)
               && !format.IsChdmanCompatible;
    }

    /// <summary>
    ///     Writes a 44100 Hz stereo 16-bit PCM copy of <paramref name="sourcePath" /> to
    ///     <paramref name="destinationPath" />.
    /// </summary>
    /// <param name="sourcePath">WAVE file to normalize.</param>
    /// <param name="destinationPath">Destination WAVE path.</param>
    /// <param name="onLog">Optional logging callback.</param>
    /// <param name="token">Cancellation token.</param>
    /// <returns>A task that completes when the normalized file has been written.</returns>
    internal static async Task NormalizeAsync(
        string sourcePath,
        string destinationPath,
        Action<string>? onLog,
        CancellationToken token
    )
    {
        WavHeaderSanitizer.TryGetFormat(sourcePath, out var format);
        onLog?.Invoke(
            $"Normalizing {Path.GetFileName(sourcePath)} to 44100 Hz stereo 16-bit PCM "
                + $"(source: {format.SampleRate} Hz, {format.Channels} channel(s), {format.BitsPerSample}-bit)..."
        );

#if WINDOWS
        if (OperatingSystem.IsWindows())
        {
            if (format.Channels > 2)
            {
                throw new InvalidDataException(
                    $"{Path.GetFileName(sourcePath)} has {format.Channels} audio channels; only mono or stereo WAVE tracks can be normalized."
                );
            }

            try
            {
                await Task.Run(
                        () => NormalizeWithNaudio(sourcePath, destinationPath),
                        token
                    )
                    .ConfigureAwait(false);
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                onLog?.Invoke(
                    $"WAV: the built-in decoder could not normalize {Path.GetFileName(sourcePath)} ({ex.Message}); trying ffmpeg..."
                );

                try
                {
                    await Mp3ToWavDecoder
                        .ConvertToChdmanWavAsync(sourcePath, destinationPath, token)
                        .ConfigureAwait(false);
                    return;
                }
                catch (Exception ffmpegEx) when (ffmpegEx is not OperationCanceledException)
                {
                    throw new InvalidDataException(
                        $"Could not normalize {Path.GetFileName(sourcePath)}: {ex.Message}; ffmpeg fallback also failed: {ffmpegEx.Message}",
                        ex
                    );
                }
            }
        }
#endif

        await Mp3ToWavDecoder
            .ConvertToChdmanWavAsync(sourcePath, destinationPath, token)
            .ConfigureAwait(false);
    }

#if WINDOWS
    /// <summary>Normalizes a WAVE track with NAudio's managed reader and WDL resampler.</summary>
    /// <param name="sourcePath">WAVE file to normalize.</param>
    /// <param name="destinationPath">Destination WAVE path.</param>
    private static void NormalizeWithNaudio(string sourcePath, string destinationPath)
    {
        using var reader = new WaveFileReader(sourcePath);
        var sample = reader.ToSampleProvider();
        WaveFileWriter.CreateWaveFile16(
            destinationPath,
            Mp3ToWavDecoder.NormalizeForChdman(sample)
        );
    }
#endif
}
