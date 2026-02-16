using System.Diagnostics;

namespace AxoParse.Evtx.Tests;

public class EvtxChunkHeaderTests(ITestOutputHelper testOutputHelper)
{
    #region Public Methods

    /// <summary>
    /// Verifies that every chunk in security.evtx parses without throwing and has valid structural invariants.
    /// </summary>
    [Fact]
    public void ParsesAllChunksInSecurityEvtx()
    {
        byte[] data = File.ReadAllBytes(Path.Combine(TestDataDir, "security.evtx"));
        EvtxFileHeader fileHeader = EvtxFileHeader.ParseEvtxFileHeader(data);

        for (int i = 0; i < fileHeader.NumberOfChunks; i++)
        {
            int offset = FileHeaderSize + i * ChunkSize;
            byte[] chunkData = data[offset..(offset + ChunkSize)];

            EvtxChunkHeader chunk = EvtxChunkHeader.ParseEvtxChunkHeader(chunkData);

            Assert.Equal(128u, chunk.HeaderSize);
            Assert.True(chunk.LastEventRecordId >= chunk.FirstEventRecordId,
                $"Chunk {i}: LastEventRecordId < FirstEventRecordId");
        }
    }

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
    /// Verifies that data with a wrong magic signature throws EvtxParseException with InvalidChunkSignature.
    /// </summary>
    [Fact]
    public void ThrowsOnBadSignature()
    {
        byte[] data = new byte[512];
        EvtxParseException ex = Assert.Throws<EvtxParseException>(() => EvtxChunkHeader.ParseEvtxChunkHeader(data));
        Assert.Equal(EvtxParseError.InvalidChunkSignature, ex.ErrorCode);
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

    #endregion

    #region Non-Public Fields

    private const int ChunkSize = 65536;
    private const int FileHeaderSize = 4096;
    private static readonly string TestDataDir = TestPaths.TestDataDir;

    #endregion
}