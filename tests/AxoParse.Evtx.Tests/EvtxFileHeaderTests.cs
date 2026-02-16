using System.Diagnostics;

namespace AxoParse.Evtx.Tests;

public class EvtxFileHeaderTests(ITestOutputHelper testOutputHelper)
{
    #region Public Methods

    [Fact]
    public void ParsesAllTestFiles()
    {
        string[] evtxFiles = Directory.GetFiles(TestDataDir, "*.evtx");
        Assert.True(evtxFiles.Length > 0, "No test .evtx files found");

        Stopwatch sw = new Stopwatch();
        testOutputHelper.WriteLine($"Parsing headers for {evtxFiles.Length} files:");

        foreach (string file in evtxFiles)
        {
            byte[] data = File.ReadAllBytes(file);

            sw.Restart();
            EvtxFileHeader header = EvtxFileHeader.ParseEvtxFileHeader(data);
            sw.Stop();

            string name = Path.GetFileName(file);
            testOutputHelper.WriteLine(
                $"  [{name}] {sw.Elapsed.TotalMicroseconds,8:F1}µs | v{header.MajorFormatVersion}.{header.MinorFormatVersion} | {header.NumberOfChunks} chunks | {header.FileFlags}");
        }
    }

    [Fact]
    public void ParsesDirtyFlaggedFile()
    {
        byte[] data = File.ReadAllBytes(Path.Combine(TestDataDir, "2-system-Security-dirty.evtx"));

        Stopwatch sw = Stopwatch.StartNew();
        EvtxFileHeader header = EvtxFileHeader.ParseEvtxFileHeader(data);
        sw.Stop();

        Assert.True(header.FileFlags.HasFlag(HeaderFlags.Dirty));

        testOutputHelper.WriteLine($"[Security-dirty.evtx] Parsed header in {sw.Elapsed.TotalMicroseconds:F1}µs");
        testOutputHelper.WriteLine($"  Flags: {header.FileFlags}");
    }

    [Fact]
    public void ParsesMinimumValidHeader()
    {
        // Build a bare 128-byte buffer with just a valid signature
        byte[] data = new byte[128];
        "ElfFile\0"u8.CopyTo(data);

        EvtxFileHeader header = EvtxFileHeader.ParseEvtxFileHeader(data);

        Assert.Equal(0u, header.HeaderSize);
        Assert.Equal((ushort)0, header.NumberOfChunks);
        Assert.Equal(HeaderFlags.None, header.FileFlags);
    }

    [Fact]
    public void ParsesNoCrc32FlaggedFile()
    {
        byte[] data = File.ReadAllBytes(Path.Combine(TestDataDir, "Application_no_crc32.evtx"));

        EvtxFileHeader header = EvtxFileHeader.ParseEvtxFileHeader(data);

        Assert.True(header.FileFlags.HasFlag(HeaderFlags.NoCrc32));
        testOutputHelper.WriteLine($"[Application_no_crc32.evtx] Flags: {header.FileFlags}");
    }

    [Fact]
    public void ParsesSecurityEvtxHeader()
    {
        byte[] data = File.ReadAllBytes(Path.Combine(TestDataDir, "security.evtx"));

        Stopwatch sw = Stopwatch.StartNew();
        EvtxFileHeader header = EvtxFileHeader.ParseEvtxFileHeader(data);
        sw.Stop();

        Assert.Equal(128u, header.HeaderSize);
        Assert.Equal(3, header.MajorFormatVersion);
        Assert.Equal(1, header.MinorFormatVersion);
        Assert.Equal(4096, header.HeaderBlockSize);
        Assert.True(header.NumberOfChunks > 0);
        Assert.True(header.Checksum != 0);

        testOutputHelper.WriteLine($"[security.evtx] Parsed header in {sw.Elapsed.TotalMicroseconds:F1}µs");
        testOutputHelper.WriteLine(
            $"  Chunks: {header.NumberOfChunks}, Flags: {header.FileFlags}, NextRecord: {header.NextRecordIdentifier}");
    }

    /// <summary>
    /// Verifies that data with a wrong magic signature throws EvtxParseException with InvalidFileSignature.
    /// </summary>
    [Fact]
    public void ThrowsOnBadSignature()
    {
        byte[] data = new byte[4096];
        EvtxParseException ex = Assert.Throws<EvtxParseException>(() => EvtxFileHeader.ParseEvtxFileHeader(data));
        Assert.Equal(EvtxParseError.InvalidFileSignature, ex.ErrorCode);
    }

    /// <summary>
    /// Verifies that data shorter than 128 bytes throws EvtxParseException with FileHeaderTooShort.
    /// </summary>
    [Fact]
    public void ThrowsOnTruncatedData()
    {
        byte[] data = new byte[64];
        EvtxParseException ex = Assert.Throws<EvtxParseException>(() => EvtxFileHeader.ParseEvtxFileHeader(data));
        Assert.Equal(EvtxParseError.FileHeaderTooShort, ex.ErrorCode);
    }

    #endregion

    #region Non-Public Fields

    private static readonly string TestDataDir = TestPaths.TestDataDir;

    #endregion
}