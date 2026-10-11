using System.Buffers.Binary;
using System.Text;

namespace CHDStudio.Utilities;

/// <summary>
///     Detects and repairs PCM WAVE files whose RIFF/data headers declare more audio bytes than the
///     file actually contains. Both encoders trust the header: chdman aborts mid-conversion with
///     "Error reading input file" when the audio runs short, and the built-in CHDSharp encoder
///     reports the same condition as "Unexpected end of file". Some widely distributed rips (e.g.
///     the Policenauts Saturn English patch) ship WAVE tracks whose data chunk is exactly one CD
///     frame larger than the audio present, so every cue/iso/wav conversion of those sets fails.
///     Clamping the declared sizes to the bytes that are actually present lets both encoders read
///     the audio that exists.
/// </summary>
internal static class WavHeaderSanitizer
{
    /// <summary>Size of the RIFF/WAVE prefix ("RIFF", the RIFF size field and "WAVE").</summary>
    private const long RiffHeaderBytes = 12;

    /// <summary>Size of a RIFF chunk's four-character code plus size field.</summary>
    private const long ChunkHeaderBytes = 8;

    /// <summary>Size of the mandatory PCM fields of a "fmt " chunk.</summary>
    private const uint MinimumFmtChunkBytes = 16;

    /// <summary>
    ///     Walks the RIFF chunks exactly like the encoders' CUE WAVE parser (MAME's
    ///     <c>parse_wav_sample</c>) and returns where the audio data starts and how many bytes the
    ///     data chunk declares.
    /// </summary>
    /// <param name="wavPath">Path of the WAVE file to inspect.</param>
    /// <param name="dataOffset">Receives the offset of the first audio byte.</param>
    /// <param name="declaredDataLength">Receives the data chunk's size field.</param>
    /// <param name="fileLength">Receives the file's actual length in bytes.</param>
    /// <returns>
    ///     <see langword="true" /> when a WAVE data chunk was located; otherwise <see langword="false" />
    ///     (not a WAVE file, malformed, or unreadable).
    /// </returns>
    internal static bool TryGetDataChunk(
        string wavPath,
        out long dataOffset,
        out uint declaredDataLength,
        out long fileLength
    )
    {
        dataOffset = 0;
        declaredDataLength = 0;
        fileLength = 0;

        try
        {
            using var stream = new FileStream(
                wavPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite
            );
            fileLength = stream.Length;
            if (fileLength < RiffHeaderBytes) return false;

            long offset = 0;
            if (
                !TryReadFourCc(stream, offset, out var riff)
                || !string.Equals(riff, "RIFF", StringComparison.Ordinal)
            )
            {
                return false;
            }

            offset += 4;
            if (!TryReadU32(stream, ref offset, out _)) return false;
            if (
                !TryReadFourCc(stream, offset, out var wave)
                || !string.Equals(wave, "WAVE", StringComparison.Ordinal)
            )
            {
                return false;
            }


            offset += 4;

            if (!TryFindChunk(stream, ref offset, fileLength, "fmt ", out var fmtSize)) return false;
            if (fmtSize < MinimumFmtChunkBytes) return false;

            // The encoders consume the 16 mandatory fmt fields and then skip size - 16, landing on
            // the next chunk header. Mirroring that keeps this walk aligned with what they parse.
            offset += fmtSize;
            if (offset >= fileLength) return false;

            if (!TryFindChunk(stream, ref offset, fileLength, "data", out declaredDataLength))
                return false;

            dataOffset = offset;
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    ///     Returns whether the WAVE file's data chunk declares more audio bytes than the file
    ///     contains.
    /// </summary>
    /// <param name="wavPath">Path of the WAVE file to inspect.</param>
    /// <returns>
    ///     <see langword="true" /> when the declared audio length runs past the end of the file.
    /// </returns>
    internal static bool IsOverDeclared(string wavPath)
    {
        return TryGetDataChunk(wavPath, out var dataOffset, out var declared, out var fileLength)
            && declared > 0
            && dataOffset + declared > fileLength;
    }

    /// <summary>
    ///     Clamps an over-declared WAVE file's data chunk size (and its RIFF size when that
    ///     over-declares too) to the bytes the file actually contains. Files that are not
    ///     over-declared are left untouched.
    /// </summary>
    /// <param name="wavPath">Path of the WAVE file to repair.</param>
    /// <returns>The number of audio bytes the header over-declared, or 0 when nothing changed.</returns>
    internal static long ClampDeclaredSizes(string wavPath)
    {
        if (!TryGetDataChunk(wavPath, out var dataOffset, out var declared, out var fileLength))
            return 0;

        var actualDataLength = fileLength - dataOffset;
        if (actualDataLength < 0 || declared <= actualDataLength) return 0;

        // The header fields are 32-bit; a file larger than that cannot be represented.
        if (fileLength > uint.MaxValue) return 0;

        try
        {
            using var stream = new FileStream(
                wavPath,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.None
            );

            WriteU32(stream, dataOffset - 4, (uint)actualDataLength);

            var riffOffset = 4L;
            if (
                TryReadU32(stream, ref riffOffset, out var declaredRiffSize)
                && declaredRiffSize > fileLength - ChunkHeaderBytes
            )
            {
                WriteU32(stream, 4, (uint)(fileLength - ChunkHeaderBytes));
            }

            return declared - actualDataLength;
        }
        catch (IOException)
        {
            return 0;
        }
        catch (UnauthorizedAccessException)
        {
            return 0;
        }
    }

    /// <summary>
    ///     Scans forward from <paramref name="offset" /> for the chunk with the wanted four-character
    ///     code, skipping each chunk's payload. Returns with <paramref name="offset" /> on the first
    ///     payload byte of the matching chunk and its declared size in <paramref name="chunkSize" />.
    /// </summary>
    /// <param name="stream">Open stream to read from.</param>
    /// <param name="offset">Current position; updated as chunks are skipped.</param>
    /// <param name="fileLength">Total stream length, used to stop at the end of the file.</param>
    /// <param name="wantedFourCc">Chunk code to find ("fmt " or "data").</param>
    /// <param name="chunkSize">Receives the matching chunk's declared payload size.</param>
    /// <returns><see langword="true" /> when the chunk was found; otherwise <see langword="false" />.</returns>
    private static bool TryFindChunk(
        Stream stream,
        ref long offset,
        long fileLength,
        string wantedFourCc,
        out uint chunkSize
    )
    {
        chunkSize = 0;
        while (true)
        {
            if (!TryReadFourCc(stream, offset, out var fourCc)) return false;
            offset += 4;
            if (!TryReadU32(stream, ref offset, out chunkSize)) return false;
            if (string.Equals(fourCc, wantedFourCc, StringComparison.Ordinal)) return true;

            offset += chunkSize;
            if (offset >= fileLength) return false;
        }
    }

    /// <summary>
    ///     Reads a four-character code at <paramref name="offset" />, failing at the end of the file.
    /// </summary>
    /// <param name="stream">Open stream to read from.</param>
    /// <param name="offset">Offset of the four-character code.</param>
    /// <param name="fourCc">Receives the decoded code.</param>
    /// <returns><see langword="true" /> when four bytes were read; otherwise <see langword="false" />.</returns>
    private static bool TryReadFourCc(Stream stream, long offset, out string fourCc)
    {
        Span<byte> buffer = stackalloc byte[4];
        stream.Position = offset;
        if (stream.Read(buffer) != 4)
        {
            fourCc = string.Empty;
            return false;
        }

        fourCc = Encoding.ASCII.GetString(buffer);
        return true;
    }

    /// <summary>
    ///     Reads a little-endian 32-bit value at <paramref name="offset" />, advancing the offset.
    /// </summary>
    /// <param name="stream">Open stream to read from.</param>
    /// <param name="offset">Offset to read at; advanced by four on success.</param>
    /// <param name="value">Receives the decoded value.</param>
    /// <returns><see langword="true" /> when four bytes were read; otherwise <see langword="false" />.</returns>
    private static bool TryReadU32(Stream stream, ref long offset, out uint value)
    {
        Span<byte> buffer = stackalloc byte[4];
        stream.Position = offset;
        if (stream.Read(buffer) != 4)
        {
            value = 0;
            return false;
        }

        value = BinaryPrimitives.ReadUInt32LittleEndian(buffer);
        offset += 4;
        return true;
    }

    /// <summary>
    ///     Writes a little-endian 32-bit value at <paramref name="offset" />.
    /// </summary>
    /// <param name="stream">Open stream to write to.</param>
    /// <param name="offset">Offset to write at.</param>
    /// <param name="value">Value to write.</param>
    private static void WriteU32(Stream stream, long offset, uint value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, value);
        stream.Position = offset;
        stream.Write(buffer);
    }
}
