using System.Diagnostics;

namespace AxoParse.Evtx.Tests;

public class EvtxChunkTests(ITestOutputHelper testOutputHelper)
{
    private static readonly string TestDataDir = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "data"));

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

    [Fact]
    public void ParsesAllChunksInSecurityEvtx()
    {
        byte[] data = File.ReadAllBytes(Path.Combine(TestDataDir, "security.evtx"));
        EvtxFileHeader fileHeader = EvtxFileHeader.ParseEvtxFileHeader(data);
        Stopwatch sw = new Stopwatch();

        testOutputHelper.WriteLine($"Parsing {fileHeader.NumberOfChunks} full chunks:");

        for (int i = 0; i < fileHeader.NumberOfChunks; i++)
        {
            int offset = FileHeaderSize + i * EvtxChunk.ChunkSize;

            sw.Restart();
            EvtxChunk chunk = EvtxChunk.Parse(data.AsSpan(offset, EvtxChunk.ChunkSize), offset);
            sw.Stop();

            testOutputHelper.WriteLine(
                $"  [chunk {i}] {sw.Elapsed.TotalMicroseconds,8:F1}µs | {chunk.Records.Count} records | {chunk.Templates.Count} templates");
        }
    }
}