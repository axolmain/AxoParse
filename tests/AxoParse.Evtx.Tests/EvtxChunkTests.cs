using System.Diagnostics;
using AxoParse.Evtx.Evtx;

namespace AxoParse.Evtx.Tests;

public class EvtxChunkTests(ITestOutputHelper testOutputHelper)
{
    #region Public Methods

    /// <summary>
    /// Verifies that GetEvent() returns an EvtxEvent matching the parallel array data.
    /// </summary>
    [Fact]
    public void GetEventMatchesDirectIndexAccess()
    {
        byte[] data = File.ReadAllBytes(Path.Combine(_testDataDir, "security.evtx"));
        EvtxParser parser = EvtxParser.Parse(data, cancellationToken: TestContext.Current.CancellationToken);

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
    /// Verifies that every chunk in security.evtx parses with at least one record and one template.
    /// </summary>
    [Fact]
    public void ParsesAllChunksInSecurityEvtx()
    {
        byte[] data = File.ReadAllBytes(Path.Combine(_testDataDir, "security.evtx"));
        EvtxFileHeader fileHeader = EvtxFileHeader.ParseEvtxFileHeader(data);

        for (int i = 0; i < fileHeader.NumberOfChunks; i++)
        {
            int offset = _fileHeaderSize + i * EvtxChunk.ChunkSize;

            EvtxChunk chunk = EvtxChunk.Parse(data.AsSpan(offset, EvtxChunk.ChunkSize), offset);

            Assert.True(chunk.Records.Count > 0, $"Chunk {i}: no records");
            Assert.True(chunk.Templates.Count > 0, $"Chunk {i}: no templates");
        }
    }

    [Fact]
    public void ParsesFirstChunkOfSecurityEvtx()
    {
        (byte[] fileData, int chunkOffset) = GetChunkInfo("security.evtx");

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
    public void RecordCountMatchesHeader()
    {
        (byte[] fileData, int chunkOffset) = GetChunkInfo("security.evtx");
        EvtxChunk chunk = EvtxChunk.Parse(fileData.AsSpan(chunkOffset, EvtxChunk.ChunkSize), chunkOffset);

        ulong expected = chunk.Header.LastEventRecordId - chunk.Header.FirstEventRecordId + 1;
        Assert.Equal((int)expected, chunk.Records.Count);
    }

    [Fact]
    public void RecordsAreSequential()
    {
        (byte[] fileData, int chunkOffset) = GetChunkInfo("security.evtx");
        EvtxChunk chunk = EvtxChunk.Parse(fileData.AsSpan(chunkOffset, EvtxChunk.ChunkSize), chunkOffset);

        for (int i = 1; i < chunk.Records.Count; i++)
            Assert.Equal(chunk.Records[i - 1].EventRecordId + 1, chunk.Records[i].EventRecordId);
    }

    /// <summary>
    /// Verifies that RenderDiagnostics is empty when all records in a clean file render successfully.
    /// </summary>
    [Fact]
    public void RenderDiagnosticsEmptyOnCleanChunks()
    {
        byte[] data = File.ReadAllBytes(Path.Combine(_testDataDir, "security.evtx"));
        EvtxParser parser = EvtxParser.Parse(data, cancellationToken: TestContext.Current.CancellationToken);

        foreach (EvtxChunk chunk in parser.Chunks)
        {
            Assert.Empty(chunk.RenderDiagnostics);
        }
    }

    #endregion

    #region Non-Public Methods

    private static (byte[] fileData, int chunkFileOffset) GetChunkInfo(string filename, int chunkIndex = 0)
    {
        byte[] data = File.ReadAllBytes(Path.Combine(_testDataDir, filename));
        int offset = _fileHeaderSize + chunkIndex * EvtxChunk.ChunkSize;
        return (data, offset);
    }

    #endregion

    #region Non-Public Fields

    private const int _fileHeaderSize = 4096;
    private static readonly string _testDataDir = TestPaths.TestDataDir;

    #endregion
}