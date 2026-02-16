using System.Diagnostics;

namespace AxoParse.Evtx.Tests;

public class EvtxChunkTests(ITestOutputHelper testOutputHelper)
{
    private static readonly string TestDataDir = TestPaths.TestDataDir;

    private const int FileHeaderSize = 4096;

    private static (byte[] fileData, int chunkFileOffset) GetChunkInfo(string filename, int chunkIndex = 0)
    {
        byte[] data = File.ReadAllBytes(Path.Combine(TestDataDir, filename));
        int offset = FileHeaderSize + chunkIndex * EvtxChunk.ChunkSize;
        return (data, offset);
    }

    [Fact]
    public void ParsesFirstChunkOfSecurityEvtx()
    {
        var (fileData, chunkOffset) = GetChunkInfo("security.evtx");

        Stopwatch sw = Stopwatch.StartNew();
        EvtxChunk chunk = EvtxChunk.Parse(fileData.AsSpan(chunkOffset, EvtxChunk.ChunkSize), chunkOffset);
        sw.Stop();

        Assert.True(chunk.Records.Count > 0);
        Assert.True(chunk.Templates.Count > 0);
        Assert.Equal(128u, chunk.Header.HeaderSize);

        testOutputHelper.WriteLine($"[security.evtx chunk 0] Parsed in {sw.Elapsed.TotalMicroseconds:F1}µs");
        testOutputHelper.WriteLine($"  Records: {chunk.Records.Count}, Templates: {chunk.Templates.Count}");
    }

    [Fact]
    public void RecordsAreSequential()
    {
        var (fileData, chunkOffset) = GetChunkInfo("security.evtx");
        EvtxChunk chunk = EvtxChunk.Parse(fileData.AsSpan(chunkOffset, EvtxChunk.ChunkSize), chunkOffset);

        for (int i = 1; i < chunk.Records.Count; i++)
            Assert.Equal(chunk.Records[i - 1].EventRecordId + 1, chunk.Records[i].EventRecordId);
    }

    [Fact]
    public void RecordCountMatchesHeader()
    {
        var (fileData, chunkOffset) = GetChunkInfo("security.evtx");
        EvtxChunk chunk = EvtxChunk.Parse(fileData.AsSpan(chunkOffset, EvtxChunk.ChunkSize), chunkOffset);

        ulong expected = chunk.Header.LastEventRecordId - chunk.Header.FirstEventRecordId + 1;
        Assert.Equal((int)expected, chunk.Records.Count);
    }

    /// <summary>
    /// Verifies that every chunk in security.evtx parses with at least one record and one template.
    /// </summary>
    [Fact]
    public void ParsesAllChunksInSecurityEvtx()
    {
        byte[] data = File.ReadAllBytes(Path.Combine(TestDataDir, "security.evtx"));
        EvtxFileHeader fileHeader = EvtxFileHeader.ParseEvtxFileHeader(data);

        for (int i = 0; i < fileHeader.NumberOfChunks; i++)
        {
            int offset = FileHeaderSize + i * EvtxChunk.ChunkSize;

            EvtxChunk chunk = EvtxChunk.Parse(data.AsSpan(offset, EvtxChunk.ChunkSize), offset);

            Assert.True(chunk.Records.Count > 0, $"Chunk {i}: no records");
            Assert.True(chunk.Templates.Count > 0, $"Chunk {i}: no templates");
        }
    }

    /// <summary>
    /// Verifies that GetEvent() returns an EvtxEvent matching the parallel array data.
    /// </summary>
    [Fact]
    public void GetEventMatchesDirectIndexAccess()
    {
        byte[] data = File.ReadAllBytes(Path.Combine(TestDataDir, "security.evtx"));
        EvtxParser parser = EvtxParser.Parse(data);

        EvtxChunk chunk = parser.Chunks[0];
        for (int i = 0; i < chunk.Records.Count; i++)
        {
            EvtxEvent evt = chunk.GetEvent(i);
            Assert.Equal(chunk.Records[i], evt.Record);
            Assert.Equal(chunk.ParsedXml[i], evt.Xml);
            Assert.True(evt.IsSuccess);
        }
    }

    /// <summary>
    /// Verifies that RenderDiagnostics is empty when all records in a clean file render successfully.
    /// </summary>
    [Fact]
    public void RenderDiagnosticsEmptyOnCleanChunks()
    {
        byte[] data = File.ReadAllBytes(Path.Combine(TestDataDir, "security.evtx"));
        EvtxParser parser = EvtxParser.Parse(data);

        foreach (EvtxChunk chunk in parser.Chunks)
        {
            Assert.Empty(chunk.RenderDiagnostics);
        }
    }
}