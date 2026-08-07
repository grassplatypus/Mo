using Mo.Core.DisplayConfiguration;

namespace Mo.Core.Tests;

public class EdidManufacturerTests
{
    // EDID packs three 5-bit letters (A=1..Z=26) into a big-endian ushort whose
    // top bit is always 0. Helper builds the canonical, well-formed encoding.
    private static ushort Encode(string pnp) => (ushort)(
        ((pnp[0] - 'A' + 1) << 10) |
        ((pnp[1] - 'A' + 1) << 5) |
        (pnp[2] - 'A' + 1));

    private static ushort ByteSwap(ushort v) => (ushort)((v << 8) | (v >> 8));

    [Theory]
    [InlineData("AOC")]
    [InlineData("GSM")]
    [InlineData("DEL")]
    [InlineData("SAM")]
    public void GetPnpId_DecodesWellFormedId(string pnp)
    {
        Assert.Equal(pnp, EdidManufacturer.GetPnpId(Encode(pnp)));
    }

    [Theory]
    [InlineData("AOC")] // byte-swapped 0x05E3 -> 0xE305, which also decodes to letters ("XXE")
    [InlineData("GSM")]
    [InlineData("DEL")]
    public void GetPnpId_RecoversFromByteSwappedId(string pnp)
    {
        Assert.Equal(pnp, EdidManufacturer.GetPnpId(ByteSwap(Encode(pnp))));
    }

    // The regression this guards: AOC encodes to 0x05E3, whose byte swap 0xE305
    // ALSO decodes to three A–Z letters ("XXE"). A tie-break that only checks
    // "is it letters?" picks the wrong one and shows users "XXE".
    [Fact]
    public void GetPnpId_PrefersTheCandidateWithTopBitClear()
    {
        Assert.Equal("AOC", EdidManufacturer.GetPnpId(0xE305));
        Assert.Equal("AOC", EdidManufacturer.GetPnpId(0x05E3));
    }

    [Fact]
    public void GetBrandName_MapsKnownCodesToFriendlyNames()
    {
        Assert.Equal("LG", EdidManufacturer.GetBrandName(Encode("GSM")));
        Assert.Equal("AOC", EdidManufacturer.GetBrandName(ByteSwap(Encode("AOC"))));
    }

    [Fact]
    public void GetBrandName_FallsBackToRawPnpIdWhenUnknown()
    {
        Assert.Equal("ZZZ", EdidManufacturer.GetBrandName(Encode("ZZZ")));
    }

    [Fact]
    public void GetPnpId_ReturnsEmptyForZero()
    {
        Assert.Equal(string.Empty, EdidManufacturer.GetPnpId(0));
        Assert.Null(EdidManufacturer.GetBrandName(0));
    }
}
