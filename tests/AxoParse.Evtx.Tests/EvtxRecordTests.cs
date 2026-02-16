using System.Diagnostics;

namespace AxoParse.Evtx.Tests;

public class EvtxRecordTests(ITestOutputHelper testOutputHelper)
{
    private static readonly string TestDataDir = TestPaths.TestDataDir;

    private const int FileHeaderSize = 4096;
    private const int ChunkSize = 65536;
    private const int ChunkHeaderSize = 512;

    [Fact]
    public void ParsesFirstRecordOfSecurityEvtx()
    {
        byte[] data = File.ReadAllBytes(Path.Combine(TestDataDir, "security.evtx"));
        int chunkStart = FileHeaderSize;
        EvtxChunkHeader chunk = EvtxChunkHeader.ParseEvtxChunkHeader(data.AsSpan(chunkStart, ChunkSize));

        ReadOnlySpan<byte> recordData = data.AsSpan(chunkStart + ChunkHeaderSize);
        int fileOffset = chunkStart + ChunkHeaderSize;

        Stopwatch sw = Stopwatch.StartNew();
        EvtxRecord? record = EvtxRecord.ParseEvtxRecord(recordData, fileOffset);
        sw.Stop();

        Assert.NotNull(record);
        Assert.True(record.Value.Size > 28, "Record size must be larger than the fixed header");
        Assert.Equal(record.Value.Size, record.Value.SizeCopy);
        Assert.Equal(chunk.FirstEventRecordId, record.Value.EventRecordId);
        Assert.True(record.Value.WrittenTime > 0);
        Assert.True(record.Value.EventDataLength > 0);
        Assert.Equal((int)(record.Value.Size - 28), record.Value.EventDataLength);

        testOutputHelper.WriteLine($"[security.evtx record 0] Parsed in {sw.Elapsed.TotalMicroseconds:F1}µs");
        testOutputHelper.WriteLine(
            $"  Size: {record.Value.Size}, RecordId: {record.Value.EventRecordId}, EventData: {record.Value.EventDataLength} bytes");
    }

    [Fact]
    public void ReturnsNullOnInvalidSize()
    {
        byte[] data = new byte[64];
        Assert.Null(EvtxRecord.ParseEvtxRecord(data, fileOffset: 0));
    }

    [Fact]
    public void SizeAndSizeCopyMatch()
    {
        byte[] data = File.ReadAllBytes(Path.Combine(TestDataDir, "security.evtx"));
        int chunkStart = FileHeaderSize;

        int offset = ChunkHeaderSize;
        int count = 0;
        ReadOnlySpan<byte> chunkSpan = data.AsSpan(chunkStart, ChunkSize);

        while (offset < ChunkSize - 28)
        {
            ReadOnlySpan<byte> recordSlice = chunkSpan[offset..];
            if (!recordSlice[..4].SequenceEqual("\x2a\x2a\x00\x00"u8))
                break;

            EvtxRecord? record = EvtxRecord.ParseEvtxRecord(recordSlice, chunkStart + offset);
            Assert.NotNull(record);
            Assert.Equal(record.Value.Size, record.Value.SizeCopy);
            offset += (int)record.Value.Size;
            count++;
        }

        Assert.True(count > 0, "Should have parsed at least one record");
        testOutputHelper.WriteLine($"[security.evtx chunk 0] Validated Size==SizeCopy for {count} records");
    }

    [Fact]
    public void ParsesAllRecordsInFirstChunk()
    {
        byte[] data = File.ReadAllBytes(Path.Combine(TestDataDir, "security.evtx"));
        int chunkStart = FileHeaderSize;
        EvtxChunkHeader chunk = EvtxChunkHeader.ParseEvtxChunkHeader(data.AsSpan(chunkStart, ChunkSize));

        Stopwatch sw = new Stopwatch();
        int offset = ChunkHeaderSize;
        List<EvtxRecord> records = new List<EvtxRecord>();

        ReadOnlySpan<byte> span = data.AsSpan(chunkStart, ChunkSize);
        while (offset < ChunkSize - 28)
        {
            if (!span.Slice(offset, 4).SequenceEqual("\x2a\x2a\x00\x00"u8))
                break;

            sw.Start();
            EvtxRecord? record = EvtxRecord.ParseEvtxRecord(span[offset..], chunkStart + offset);
            sw.Stop();

            Assert.NotNull(record);
            records.Add(record.Value);
            offset += (int)record.Value.Size;
        }

        Assert.True(records.Count > 0);
        for (int i = 1; i < records.Count; i++)
            Assert.Equal(records[i - 1].EventRecordId + 1, records[i].EventRecordId);

        testOutputHelper.WriteLine(
            $"[security.evtx chunk 0] Parsed {records.Count} records in {sw.Elapsed.TotalMicroseconds:F1}µs total");
        testOutputHelper.WriteLine($"  IDs: {records[0].EventRecordId}–{records[^1].EventRecordId}");
        testOutputHelper.WriteLine($"  Avg: {sw.Elapsed.TotalMicroseconds / records.Count:F2}µs/record");
    }
}