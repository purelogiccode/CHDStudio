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
}
