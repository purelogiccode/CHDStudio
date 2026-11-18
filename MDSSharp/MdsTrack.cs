using System.Diagnostics.CodeAnalysis;

namespace MDSSharp;

/// <summary>
///     One track from an Alcohol 120% .mds track table.
/// </summary>
/// <param name="Number">Track number as recorded in the descriptor's POINT field (1-99).</param>
/// <param name="ModeByte">Raw mode byte; only its low nibble selects the mode, as libmirage's reverse engineering established.</param>
/// <param name="SectorSize">Bytes per sector for this track as the rip stored them, subchannel included.</param>
/// <param name="StartLba">Absolute sector at which the track starts, as recorded in the descriptor.</param>
public sealed record MdsTrack(int Number, byte ModeByte, int SectorSize, long StartLba)
{
    // Cue TRACK type strings for the modes a cue can express.
    private const string CueAudio = "AUDIO";
    private const string CueMode1Raw = "MODE1/2352";
    private const string CueMode1Cooked = "MODE1/2048";
    private const string CueMode2Raw = "MODE2/2352";
    private const string CueMode2Xa = "MODE2/2336";
    private const string CueMode2Form1Cooked = "MODE2/2048";
    private const string CueMode2Form2 = "MODE2/2324";

    // Only the low nibble of the mode byte selects the mode; the high nibble has no effect.
    private const int ModeMode2 = 0x00;
    private const int ModeAudio = 0x01;
    private const int ModeMode1 = 0x02;
    private const int ModeMode2Alternate = 0x03;
    private const int ModeMode2Form1 = 0x04;
    private const int ModeMode2Form2 = 0x05;
    private const int ModeMode2Alternate2 = 0x07;

    /// <summary>Sectors of pregap before this track's data, or 0 when the descriptor records none.</summary>
    public long PregapSectors { get; init; }

    /// <summary>Sectors of track data recorded in the descriptor, or 0 when it records no length.</summary>
    public long LengthSectors { get; init; }

    /// <summary>
    ///     Sectors of track data actually stored in the data file when the descriptor records it
    ///     (MDS v2 footer blocks), or 0 when unknown (MDS v1). A stored count equal to
    ///     <see cref="LengthSectors" /> means the pregap is not in the data file; a count equal to
    ///     <see cref="LengthSectors" /> plus <see cref="PregapSectors" /> means the pregap is
    ///     stored at the start of the track, exactly as libMirage validates it.
    /// </summary>
    public long StoredDataSectors { get; init; }

    /// <summary>Subchannel mode byte from the descriptor (0x08 is 96-byte interleaved P-W).</summary>
    [SuppressMessage(
        "ReSharper",
        "UnusedAutoPropertyAccessor.Global",
        Justification = "Part of the public MDS track model."
    )]
    public byte SubchannelMode { get; init; }

    /// <summary>ADR/CTL byte from the descriptor's subchannel Q information.</summary>
    [SuppressMessage(
        "ReSharper",
        "UnusedAutoPropertyAccessor.Global",
        Justification = "Part of the public MDS track model."
    )]
    public byte AdrCtl { get; init; }

    /// <summary>True for a CDDA audio track.</summary>
    public bool IsAudio => NormalizedMode == ModeAudio;

    /// <summary>
    ///     The cue TRACK type for this track, or null when the mode or stored sector size is one a cue
    ///     cannot describe honestly.
    /// </summary>
    public string? CueTrackType =>
        NormalizedMode switch
        {
            ModeAudio => IsRawSectorSize ? CueAudio : null,
            ModeMode1 => SectorSize == MdsDisc.CookedSectorSize
                ? CueMode1Cooked
                : IsRawSectorSize
                    ? CueMode1Raw
                    : null,
            ModeMode2 or ModeMode2Alternate or ModeMode2Alternate2 => Mode2CueType,
            ModeMode2Form1 => Mode2Form1CueType,
            ModeMode2Form2 => Mode2Form2CueType,
            _ => null
        };

    /// <summary>A short description used in log messages.</summary>
    public string Description =>
        $"{Number}:{CueTrackType ?? $"UNKNOWN(0x{ModeByte:x2})"}@{StartLba}/ss{SectorSize}";

    /// <summary>
    ///     The mode number: the low nibble of the mode byte, folded down by 8 when the high bit of
    ///     the nibble is set, which is how Alcohol records the same modes in two ranges.
    /// </summary>
    private int NormalizedMode
    {
        get
        {
            var mode = ModeByte & 0x0F;

            return mode >= 8 ? mode - 8 : mode;
        }
    }

    /// <summary>True when the stored sector is one of the 2352-byte layouts a cue describes directly.</summary>
    private bool IsRawSectorSize =>
        SectorSize
            is MdsDisc.RawSectorSize
            or MdsDisc.RawPlusSubchannelSize
            or MdsDisc.RawPlusShortSubchannelSize;

    /// <summary>
    ///     The cue type for a generic Mode 2 track (0x00/0x03/0x07): raw sectors are described as
    ///     MODE2/2352 and the 2336-byte cooked layout as MODE2/2336. Any other stored size is not
    ///     a layout a cue can describe honestly.
    /// </summary>
    private string? Mode2CueType =>
        SectorSize switch
        {
            MdsDisc.RawSectorSize
                or MdsDisc.RawPlusSubchannelSize
                or MdsDisc.RawPlusShortSubchannelSize => CueMode2Raw,
            MdsDisc.Mode2XaSectorSize => CueMode2Xa,
            _ => null
        };

    /// <summary>
    ///     The cue type for a Mode 2 Form 1 track (0x04). chdman accepts the cooked user-data
    ///     layout as MODE2/2048, which keeps the Mode 2 sector type instead of silently turning
    ///     the track into MODE1 data. Raw and 2336-byte forms use the matching Mode 2 layouts.
    /// </summary>
    private string? Mode2Form1CueType =>
        SectorSize switch
        {
            MdsDisc.CookedSectorSize => CueMode2Form1Cooked,
            MdsDisc.RawSectorSize
                or MdsDisc.RawPlusSubchannelSize
                or MdsDisc.RawPlusShortSubchannelSize => CueMode2Raw,
            MdsDisc.Mode2XaSectorSize => CueMode2Xa,
            _ => null
        };

    /// <summary>
    ///     The cue type for a Mode 2 Form 2 track (0x05): the 2324-byte user-data layout is
    ///     MODE2/2324; raw and 2336-byte forms use the matching Mode 2 layouts.
    /// </summary>
    private string? Mode2Form2CueType =>
        SectorSize switch
        {
            MdsDisc.Mode2Form2SectorSize => CueMode2Form2,
            MdsDisc.RawSectorSize
                or MdsDisc.RawPlusSubchannelSize
                or MdsDisc.RawPlusShortSubchannelSize => CueMode2Raw,
            MdsDisc.Mode2XaSectorSize => CueMode2Xa,
            _ => null
        };
}