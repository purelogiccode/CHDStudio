using System.Diagnostics.CodeAnalysis;

namespace CHDStudio.Models;

/// <summary>Codec preset used when creating or recompressing a CHD.</summary>
internal enum ChdCompressionPreset
{
    /// <summary>The same defaults chdman uses (most widely compatible).</summary>
    Default = 0,

    /// <summary>Try every available codec and keep the smallest result (zstd/cdzs included).</summary>
    Smallest = 1,

    /// <summary>Use only zlib/cdzl, the most widely readable codecs.</summary>
    Compatible = 2,

    /// <summary>Pick a per-platform preset with CHDSharp's platform detector.</summary>
    Auto = 3,

    /// <summary>Store hunks uncompressed (raw/DVD/HDD only).</summary>
    Uncompressed = 4
}

/// <summary>Display metadata for a <see cref="ChdCompressionPreset" />.</summary>
/// <param name="Preset">The preset value.</param>
/// <param name="Name">Short name shown in the UI.</param>
[SuppressMessage("ReSharper", "UnusedMember.Global")]
internal sealed record ChdCompressionPresetInfo(ChdCompressionPreset Preset, string Name)
{
    /// <summary>All presets in display order.</summary>
    internal static IReadOnlyList<ChdCompressionPresetInfo> All { get; } =
    [
        new(ChdCompressionPreset.Default, "Default (chdman-compatible)"),
        new(ChdCompressionPreset.Smallest, "Smallest (zstd/cdzs)"),
        new(ChdCompressionPreset.Compatible, "Most compatible (zlib/cdzl)"),
        new(ChdCompressionPreset.Auto, "Auto (per platform)"),
        new(ChdCompressionPreset.Uncompressed, "Uncompressed (none)")
    ];

    /// <inheritdoc />
    public override string ToString()
    {
        return Name;
    }
}
