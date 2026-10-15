using System.Text;

namespace AxoParse.Evtx.Tests;

public class Crc32Tests
{
    #region Public Methods

    /// <summary>
    /// Verifies the standard CRC-32/ISO-HDLC check value: CRC32("123456789") = 0xCBF43926.
    /// </summary>
    [Fact]
    public void ComputeMatchesStandardCheckValue()
    {
        Assert.Equal(0xCBF43926u, Crc32.Compute("123456789"u8));
    }

    /// <summary>
    /// Verifies that the two-span overload equals one CRC over the concatenated bytes (the chunk header checksum
    /// covers bytes 0-120 and 128-512 as one stream), and differs from XORing two separate CRCs.
    /// </summary>
    [Fact]
    public void TwoSpanComputeEqualsContiguousCompute()
    {
        byte[] first = Encoding.ASCII.GetBytes("ElfChnk header part one");
        byte[] second = Encoding.ASCII.GetBytes("and the rest after the checksum field");
        byte[] joined = [..first, ..second];

        uint twoSpan = Crc32.Compute(first, second);

        Assert.Equal(Crc32.Compute(joined), twoSpan);
        Assert.NotEqual(Crc32.Compute(first) ^ Crc32.Compute(second), twoSpan);
    }

    #endregion
}
