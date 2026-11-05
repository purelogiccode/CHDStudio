using System.Buffers.Binary;
using CHDSharp.Encoder;
using CHDSharp.Encoder.Models;
using CHDStudio.Services;

namespace CHDStudio.Tests;

/// <summary>Tests for parent resolution of differential CHD files.</summary>
public class ParentChdResolverTests : IDisposable
{
    private readonly string _tempDir;

    public ParentChdResolverTests()
    {
        _tempDir = Path.Combine(
            Path.GetTempPath(),
            $"ParentChdResolverTests_{Guid.NewGuid():N}"
        );
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true);
        }
        catch
        {
            /* ignore */
        }

        GC.SuppressFinalize(this);
    }

    private string PathFor(string name)
    {
        return Path.Combine(_tempDir, name);
    }

    private (string ParentChd, string ChildChd) CreatePair()
    {
        var parentSource = PathFor("parent.bin");
        var bytes = new byte[4096 * 256];
        for (var i = 0; i < bytes.Length; i++) bytes[i] = (byte)(i * 17);
        File.WriteAllBytes(parentSource, bytes);

        var parentChd = PathFor("parent.chd");
        ChdEncoder.EncodeRaw(parentSource, parentChd);

        var childBytes = (byte[])bytes.Clone();
        for (var i = 0; i < 4096; i++) childBytes[i] = (byte)(255 - childBytes[i]);
        var childSource = PathFor("child.bin");
        File.WriteAllBytes(childSource, childBytes);

        var childChd = PathFor("child.chd");
        ChdEncoder.EncodeRaw(
            childSource,
            childChd,
            options: new ChdEncodeOptions { ParentPath = parentChd }
        );

        return (parentChd, childChd);
    }

    [Fact]
    public void Resolve_ChildWithSiblingParent_FindsParentByHash()
    {
        var (parentChd, childChd) = CreatePair();

        var resolution = ParentChdResolver.Resolve(childChd);

        Assert.True(resolution.IsRequired);
        Assert.Null(resolution.Error);
        Assert.Equal(parentChd, resolution.ParentPath);
    }

    [Fact]
    public void OrderChildrenFirst_PutsChildBeforeItsParent()
    {
        var (parentChd, childChd) = CreatePair();

        var ordered = ParentChdResolver.OrderChildrenFirst(
            [parentChd, childChd],
            explicitParentPath: null,
            autoDetect: true
        );

        Assert.Equal([childChd, parentChd], ordered);
    }

    [Fact]
    public void PrepareBatch_ReturnsResolutionForEveryFile()
    {
        var (parentChd, childChd) = CreatePair();

        var plan = ParentChdResolver.PrepareBatch(
            [parentChd, childChd],
            explicitParentPath: null,
            autoDetect: true
        );

        Assert.Equal([childChd, parentChd], plan.OrderedFiles);
        Assert.Equal(parentChd, plan.ResolutionFor(childChd).ParentPath);
        Assert.Null(plan.ResolutionFor(parentChd).ParentPath);
    }

    [Theory]
    [InlineData(1u)]
    [InlineData(2u)]
    public void Resolve_LegacyMd5Child_FindsParentByHash(uint version)
    {
        var parentHash = CreateHash(16, 0x10);
        var childHash = CreateHash(16, 0x40);
        var parentChd = PathFor($"parent-md5-v{version}.chd");
        var childChd = PathFor($"child-md5-v{version}.chd");

        WriteSyntheticHeader(
            parentChd,
            version,
            md5: parentHash,
            parentMd5: null,
            sha1: null,
            rawSha1: null,
            parentSha1: null
        );
        WriteSyntheticHeader(
            childChd,
            version,
            md5: childHash,
            parentMd5: parentHash,
            sha1: null,
            rawSha1: null,
            parentSha1: null
        );

        var resolution = ParentChdResolver.Resolve(childChd);

        Assert.True(resolution.IsRequired);
        Assert.Null(resolution.Error);
        Assert.Equal(parentChd, resolution.ParentPath);
    }

    [Fact]
    public void Resolve_V3Child_FindsParentByRawSha1()
    {
        var parentHash = CreateHash(20, 0x10);
        var childHash = CreateHash(20, 0x40);
        var parentChd = PathFor("parent-sha1-v3.chd");
        var childChd = PathFor("child-sha1-v3.chd");

        WriteSyntheticHeader(
            parentChd,
            3,
            md5: null,
            parentMd5: null,
            sha1: null,
            rawSha1: parentHash,
            parentSha1: null
        );
        WriteSyntheticHeader(
            childChd,
            3,
            md5: null,
            parentMd5: null,
            sha1: null,
            rawSha1: childHash,
            parentSha1: parentHash
        );

        var resolution = ParentChdResolver.Resolve(childChd);

        Assert.True(resolution.IsRequired);
        Assert.Null(resolution.Error);
        Assert.Equal(parentChd, resolution.ParentPath);
    }

    [Theory]
    [InlineData(4u)]
    [InlineData(5u)]
    public void Resolve_Sha1Child_FindsParentByCombinedOrRawSha1(uint version)
    {
        var parentHash = CreateHash(20, 0x10);
        var childHash = CreateHash(20, 0x40);
        var parentChd = PathFor($"parent-sha1-v{version}.chd");
        var childChd = PathFor($"child-sha1-v{version}.chd");

        WriteSyntheticHeader(
            parentChd,
            version,
            md5: null,
            parentMd5: null,
            sha1: parentHash,
            rawSha1: childHash,
            parentSha1: null
        );
        WriteSyntheticHeader(
            childChd,
            version,
            md5: null,
            parentMd5: null,
            sha1: childHash,
            rawSha1: childHash,
            parentSha1: parentHash
        );

        var resolution = ParentChdResolver.Resolve(childChd);

        Assert.True(resolution.IsRequired);
        Assert.Null(resolution.Error);
        Assert.Equal(parentChd, resolution.ParentPath);
    }

    [Fact]
    public void Resolve_ExplicitParent_ReturnsIt()
    {
        var (parentChd, childChd) = CreatePair();

        var resolution = ParentChdResolver.Resolve(childChd, parentChd, autoDetect: false);

        Assert.Null(resolution.Error);
        Assert.Equal(parentChd, resolution.ParentPath);
    }

    [Fact]
    public void Resolve_AutoDetectDisabled_ReportsMissingParent()
    {
        var (_, childChd) = CreatePair();

        var resolution = ParentChdResolver.Resolve(childChd, autoDetect: false);

        Assert.True(resolution.IsRequired);
        Assert.NotNull(resolution.Error);
        Assert.Null(resolution.ParentPath);
    }

    [Fact]
    public void Resolve_StandaloneChd_DoesNotRequireParent()
    {
        var source = PathFor("standalone.bin");
        File.WriteAllBytes(source, new byte[4096 * 64]);
        var chd = PathFor("standalone.chd");
        ChdEncoder.EncodeRaw(source, chd);

        var resolution = ParentChdResolver.Resolve(chd);

        Assert.False(resolution.IsRequired);
        Assert.Null(resolution.Error);
        Assert.Null(resolution.ParentPath);
    }

    private static byte[] CreateHash(int length, byte seed)
    {
        var hash = new byte[length];
        for (var i = 0; i < hash.Length; i++) hash[i] = (byte)(seed + i);
        return hash;
    }

    private static void WriteSyntheticHeader(
        string path,
        uint version,
        byte[]? md5,
        byte[]? parentMd5,
        byte[]? sha1,
        byte[]? rawSha1,
        byte[]? parentSha1
    )
    {
        var length = version switch
        {
            1 => 76,
            2 => 80,
            3 => 120,
            4 => 108,
            5 => 124,
            _ => throw new ArgumentOutOfRangeException(nameof(version)),
        };

        var buffer = new byte[length];
        "MComprHD"u8.CopyTo(buffer);
        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(8), (uint)length);
        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(12), version);

        switch (version)
        {
            case 1:
            case 2:
                md5?.CopyTo(buffer, 44);
                parentMd5?.CopyTo(buffer, 60);
                break;
            case 3:
                md5?.CopyTo(buffer, 44);
                parentMd5?.CopyTo(buffer, 60);
                rawSha1?.CopyTo(buffer, 80);
                parentSha1?.CopyTo(buffer, 100);
                break;
            case 4:
                sha1?.CopyTo(buffer, 48);
                parentSha1?.CopyTo(buffer, 68);
                rawSha1?.CopyTo(buffer, 88);
                break;
            default:
                rawSha1?.CopyTo(buffer, 64);
                sha1?.CopyTo(buffer, 84);
                parentSha1?.CopyTo(buffer, 104);
                break;
        }

        File.WriteAllBytes(path, buffer);
    }
}
