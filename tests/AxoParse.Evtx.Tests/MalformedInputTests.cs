using System.Buffers.Binary;

namespace AxoParse.Evtx.Tests;

/// <summary>
/// Regression tests for header and record fields that come straight from file bytes and used to size allocations
/// before being validated. Corruption must be recovered from (per the error-handling policy), not thrown or turned
/// into huge allocations.
/// </summary>
[Collection(AllocationSensitiveCollection.Name)]
public class MalformedInputTests
{
    #region Public Methods

    /// <summary>
    /// Verifies that a chunk header whose last event record ID is below its first (so the expected count underflows)
    /// doesn't throw. The ID range is only a capacity hint: records are still found by walking the chunk, so every
    /// entry point returns the same records as for the unmodified file.
    /// </summary>
    [Fact]
    public async Task ChunkHeaderWithLastRecordIdBelowFirstDoesNotThrow()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        byte[] original = await File.ReadAllBytesAsync(Path.Combine(TestPaths.TestDataDir, "security.evtx"), token);
        int expected = EvtxParser.Parse(original, new EvtxOptions { MaxThreads = 1 }, token).RecordCount;

        byte[] data = (byte[])original.Clone();
        // Chunk header: FirstEventRecordId at +24, LastEventRecordId at +32 (both u64)
        Span<byte> chunk = data.AsSpan(_firstChunkOffset, EvtxChunk.ChunkSize);
        ulong first = BinaryPrimitives.ReadUInt64LittleEndian(chunk[24..]);
        BinaryPrimitives.WriteUInt64LittleEndian(chunk[32..], first - 2);

        Assert.Equal(expected, EvtxParser.Parse(data, new EvtxOptions { MaxThreads = 1 }, token).RecordCount);
        Assert.Equal(expected, EvtxParser.WriteTo(data, Stream.Null, new EvtxOptions { Format = OutputFormat.Xml, MaxThreads = 1 }, token));

        int yielded = 0;
        await foreach (EvtxEvent _ in EvtxParser.ReadEventsAsync(data, new EvtxOptions { MaxThreads = 1 }, token))
            yielded++;
        Assert.Equal(expected, yielded);
    }

    /// <summary>
    /// Verifies that a template instance claiming far more substitutions than the record can hold fails only that
    /// record (with a diagnostic) and is rejected before anything is sized from the count: parsing allocates no more
    /// than a normal parse, rather than hundreds of MB of descriptor arrays.
    /// </summary>
    /// <param name="format">Output format under test (XML and JSON have separate template-instance readers).</param>
    [Theory]
    [InlineData(OutputFormat.Xml)]
    [InlineData(OutputFormat.JsonStructured)]
    [InlineData(OutputFormat.Json)]
    public void HugeSubstitutionCountFailsRecordWithoutLargeAllocation(OutputFormat format)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        byte[] original = File.ReadAllBytes(Path.Combine(TestPaths.TestDataDir, "security.evtx"));
        EvtxLog baseline = EvtxParser.Parse(original, new EvtxOptions { MaxThreads = 1, Format = format }, token);

        byte[] data = (byte[])original.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(FirstRecordSubstitutionCountOffset(data)), 50_000_000);

        long before = GC.GetTotalAllocatedBytes(precise: true);
        EvtxLog parser = EvtxParser.Parse(data, new EvtxOptions { MaxThreads = 1, Format = format }, token);
        long allocated = GC.GetTotalAllocatedBytes(precise: true) - before;

        Assert.Equal(baseline.RecordCount, parser.RecordCount);
        EvtxEvent firstEvent = parser.Events.First();
        Assert.False(firstEvent.IsSuccess);
        // A clean parse of this 2 MB file allocates ~10-20 MB; the old code added ~450 MB of descriptor arrays
        Assert.True(allocated < 100L * 1024 * 1024, $"Parse allocated {allocated / (1024 * 1024)} MB");
    }

    #endregion

    #region Non-Public Methods

    /// <summary>
    /// Finds the file offset of the substitution count of the first record's top-level template instance, following
    /// the same layout <c>BinXmlParser</c> reads: record header (24 bytes), fragment header (4 bytes, 0x0F 01 01 00),
    /// then the template instance token (0x0C) + 1 unknown byte + 4-byte template ID, a 4-byte definition offset, the
    /// inline definition when that offset points just past itself (4 next + 16 GUID + 4 size + body), then the count.
    /// </summary>
    /// <param name="data">Complete EVTX file bytes.</param>
    /// <returns>Absolute file offset of the u32 substitution count.</returns>
    private static int FirstRecordSubstitutionCountOffset(byte[] data)
    {
        int binXml = _firstChunkOffset + 512 + 24;
        Assert.Equal(0x0F, data[binXml]);
        int token = binXml + 4;
        Assert.Equal(0x0C, data[token]);

        int p = token + 6;
        uint defDataOffset = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(p));
        p += 4;
        if (defDataOffset == (uint)(p - _firstChunkOffset))
            p += 24 + (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(p + 20));
        return p;
    }

    #endregion

    #region Non-Public Fields

    /// <summary>
    /// The first chunk follows the 4 KB file header.
    /// </summary>
    private const int _firstChunkOffset = 4096;

    #endregion
}

/// <summary>
/// Runs its tests on their own, after the parallel ones: they measure process-wide allocations
/// (<see cref="GC.GetTotalAllocatedBytes"/>), which concurrently running tests would inflate.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class AllocationSensitiveCollection
{
    #region Non-Public Fields

    /// <summary>
    /// Collection name used by <see cref="CollectionAttribute"/>.
    /// </summary>
    public const string Name = "Allocation-sensitive";

    #endregion
}
