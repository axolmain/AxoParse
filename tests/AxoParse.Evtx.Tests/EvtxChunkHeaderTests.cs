using System.Diagnostics;

namespace AxoParse.Evtx.Tests;

public class EvtxChunkHeaderTests(ITestOutputHelper testOutputHelper)
{
    private static readonly string TestDataDir = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "data"));

    private const int FileHeaderSize = 4096;
    private const int ChunkSize = 65536;

    [Fact]
    public void ParsesFirstChunkOfSecurityEvtx()
    {
        byte[] data = File.ReadAllBytes(Path.Combine(TestDataDir, "security.evtx"));
        byte[] chunkData = data[FileHeaderSize..(FileHeaderSize + ChunkSize)];

        Stopwatch sw = Stopwatch.StartNew();
        EvtxChunkHeader chunk = EvtxChunkHeader.ParseEvtxChunkHeader(chunkData);
        sw.Stop();

        Assert.Equal(128u, chunk.HeaderSize);
        Assert.True(chunk.LastEventRecordNumber >= chunk.FirstEventRecordNumber);
        Assert.True(chunk.LastEventRecordId >= chunk.FirstEventRecordId);
        Assert.True(chunk.FreeSpaceOffset > 0);
        Assert.True(chunk.Checksum != 0);
        testOutputHelper.WriteLine($"[security.evtx chunk 0] Parsed in {sw.Elapsed.TotalMicroseconds:F1}µs");
        testOutputHelper.WriteLine($"  Records: {chunk.FirstEventRecordNumber}–{chunk.LastEventRecordNumber}");
        testOutputHelper.WriteLine($"  IDs: {chunk.FirstEventRecordId}–{chunk.LastEventRecordId}");
        testOutputHelper.WriteLine($"  FreeSpace: {chunk.FreeSpaceOffset}, Flags: {chunk.Flags}");
    }

    /// <summary>
    /// Verifies that data shorter than 512 bytes throws EvtxParseException with ChunkHeaderTooShort.
    /// </summary>
    [Fact]
    public void ThrowsOnTruncatedData()
    {
        byte[] data = new byte[256];
        EvtxParseException ex = Assert.Throws<EvtxParseException>(() => EvtxChunkHeader.ParseEvtxChunkHeader(data));
        Assert.Equal(EvtxParseError.ChunkHeaderTooShort, ex.ErrorCode);
    }

    /// <summary>
    /// Verifies that data with a wrong magic signature throws EvtxParseException with InvalidChunkSignature.
    /// </summary>
    [Fact]
    public void ThrowsOnBadSignature()
    {
        byte[] data = new byte[512];
        EvtxParseException ex = Assert.Throws<EvtxParseException>(() => EvtxChunkHeader.ParseEvtxChunkHeader(data));
        Assert.Equal(EvtxParseError.InvalidChunkSignature, ex.ErrorCode);
    }

    [Fact]
    public void ParsesAllChunksInSecurityEvtx()
    {
        byte[] data = File.ReadAllBytes(Path.Combine(TestDataDir, "security.evtx"));
        EvtxFileHeader fileHeader = EvtxFileHeader.ParseEvtxFileHeader(data);
        Stopwatch sw = new Stopwatch();

        testOutputHelper.WriteLine($"Parsing {fileHeader.NumberOfChunks} chunks:");

        for (int i = 0; i < fileHeader.NumberOfChunks; i++)
        {
            int offset = FileHeaderSize + i * ChunkSize;
            byte[] chunkData = data[offset..(offset + ChunkSize)];

            sw.Restart();
            EvtxChunkHeader chunk = EvtxChunkHeader.ParseEvtxChunkHeader(chunkData);
            sw.Stop();

            testOutputHelper.WriteLine(
                $"  [chunk {i}] {sw.Elapsed.TotalMicroseconds,8:F1}µs | records {chunk.FirstEventRecordNumber}–{chunk.LastEventRecordNumber} | flags {chunk.Flags}");
        }
    }
}