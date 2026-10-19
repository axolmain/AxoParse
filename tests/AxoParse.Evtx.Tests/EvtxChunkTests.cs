using System.Diagnostics;
using System.Runtime.InteropServices;
using AxoParse.Evtx.BinXml;

namespace AxoParse.Evtx.Tests;

public class EvtxChunkTests(ITestOutputHelper testOutputHelper)
{
    #region Public Methods

    /// <summary>
    /// Verifies that each event of a chunk reports its record's header fields, and that enumerating
    /// <see cref="EvtxChunk.Events"/> yields the same events as indexing it.
    /// </summary>
    [Fact]
    public void EventsMatchRecords()
    {
        byte[] data = File.ReadAllBytes(Path.Combine(_testDataDir, "security.evtx"));
        EvtxLog log = EvtxParser.Parse(data, cancellationToken: TestContext.Current.CancellationToken);

        EvtxChunk chunk = log.Chunks[0];
        int i = 0;
        foreach (EvtxEvent evt in chunk.Events)
        {
            EvtxRecord record = chunk.Records[i];
            Assert.Equal(record.EventRecordId, evt.RecordId);
            Assert.Equal(DateTime.FromFileTimeUtc((long)record.WrittenTime), evt.WrittenTimeUtc);
            Assert.Equal(record.EventDataFileOffset - EvtxRecord.HeaderSize, evt.FileOffset);
            Assert.Equal(chunk.Events[i].GetText(), evt.GetText());
            Assert.True(evt.IsSuccess);
            i++;
        }

        Assert.Equal(chunk.Records.Count, i);
        Assert.Throws<ArgumentOutOfRangeException>(() => chunk.Events[i]);
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
            Assert.True(TemplateCount(data, offset) > 0, $"Chunk {i}: no templates");
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
        Assert.True(TemplateCount(fileData, chunkOffset) > 0);
        Assert.Equal(128u, chunk.Header.HeaderSize);

        testOutputHelper.WriteLine($"[security.evtx chunk 0] Parsed in {sw.Elapsed.TotalMicroseconds:F1}µs");
        testOutputHelper.WriteLine($"  Records: {chunk.Records.Count}, Templates: {TemplateCount(fileData, chunkOffset)}");
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
    /// Verifies that no event has a diagnostic when all records in a clean file render successfully.
    /// </summary>
    [Fact]
    public void NoDiagnosticsOnCleanChunks()
    {
        byte[] data = File.ReadAllBytes(Path.Combine(_testDataDir, "security.evtx"));
        EvtxLog log = EvtxParser.Parse(data, cancellationToken: TestContext.Current.CancellationToken);

        foreach (EvtxChunk chunk in log.Chunks)
        {
            Assert.All(chunk.Events, evt => Assert.Null(evt.Diagnostic));
        }
    }

    #endregion

    #region Non-Public Methods

    /// <summary>
    /// Counts the template definitions reachable from a chunk's 32-entry template pointer table (chunk offset 384).
    /// </summary>
    /// <param name="data">File bytes.</param>
    /// <param name="chunkOffset">File offset of the chunk.</param>
    /// <returns>Number of template definitions.</returns>
    private static int TemplateCount(byte[] data, int chunkOffset)
    {
        ReadOnlySpan<byte> chunk = data.AsSpan(chunkOffset, EvtxChunk.ChunkSize);
        return BinXmlTemplateDefinition.PreloadFromChunk(chunk, MemoryMarshal.Cast<byte, uint>(chunk.Slice(384, 128)), chunkOffset).Count;
    }

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