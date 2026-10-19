namespace AxoParse.Evtx.Tests;

public class EvtxParserReadEventsTests
{
    #region Public Methods

    /// <summary>
    /// Verifies that every way of reading events yields exactly the events of
    /// <see cref="EvtxParser.Parse(byte[], EvtxOptions?, CancellationToken)"/>: <see cref="EvtxParser.Parse(string, EvtxOptions?, CancellationToken)"/>,
    /// <c>ReadEvents</c> and <c>ReadEventsAsync</c> over both a byte array and a path. Same order, record IDs, file
    /// offsets, timestamps, rendered output and diagnostics, for every sample file, both formats, single- and
    /// multi-threaded parsing, and with checksum validation off and on.
    /// </summary>
    /// <param name="format">Output format under test.</param>
    /// <param name="threads">Thread count passed to every path.</param>
    /// <param name="validateChecksums">Checksum validation flag passed to every path.</param>
    [Theory]
    [InlineData(OutputFormat.Xml, 1, false)]
    [InlineData(OutputFormat.Xml, 8, false)]
    [InlineData(OutputFormat.JsonStructured, 1, false)]
    [InlineData(OutputFormat.JsonStructured, 8, false)]
    [InlineData(OutputFormat.Xml, 1, true)]
    [InlineData(OutputFormat.Xml, 8, true)]
    [InlineData(OutputFormat.JsonStructured, 1, true)]
    [InlineData(OutputFormat.JsonStructured, 8, true)]
    public async Task EveryReadPathMatchesParseForAllTestFiles(OutputFormat format, int threads, bool validateChecksums)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        EvtxOptions options = new() { Format = format, MaxThreads = threads, ValidateChecksums = validateChecksums };
        foreach (string file in Directory.GetFiles(_testDataDir, "*.evtx"))
        {
            string where = $"{Path.GetFileName(file)} ({format}, {threads} threads, validateChecksums={validateChecksums})";
            byte[] data = await File.ReadAllBytesAsync(file, token);
            EvtxLog log = EvtxParser.Parse(data, options, token);
            List<EvtxEvent> expected = new(log.Events);

            EvtxLog fromPath = EvtxParser.Parse(file, options, token);
            Assert.True(log.RecordCount == fromPath.RecordCount, $"{where}: Parse(path) record count differs");
            Assert.True(log.Diagnostics.SequenceEqual(fromPath.Diagnostics), $"{where}: Parse(path) diagnostics differ");
            AssertSameEvents(expected, fromPath.Events, $"{where} Parse(path)");
            AssertSameEvents(expected, EvtxParser.ReadEvents(data, options, token), $"{where} ReadEvents(byte[])");
            AssertSameEvents(expected, EvtxParser.ReadEvents(file, options, token), $"{where} ReadEvents(path)");
            AssertSameEvents(expected, await ToListAsync(EvtxParser.ReadEventsAsync(data, options, token)), $"{where} ReadEventsAsync(byte[])");
            AssertSameEvents(expected, await ToListAsync(EvtxParser.ReadEventsAsync(file, options, token)), $"{where} ReadEventsAsync(path)");
        }
    }

    /// <summary>
    /// Verifies that <see cref="EvtxLog.Diagnostics"/> lists the chunks skipped for a checksum mismatch (with their
    /// file offsets and no record ID) when checksums are validated, and none when they are not.
    /// </summary>
    [Fact]
    public void ChecksumMismatchesAreReportedAsChunkDiagnostics()
    {
        // Every chunk of this sample has stale checksums: it parses 0 records when they are validated
        byte[] data = File.ReadAllBytes(Path.Combine(_testDataDir, "Microsoft-Windows-HelloForBusiness%4Operational.evtx"));
        EvtxLog validated = EvtxParser.Parse(data, new EvtxOptions { ValidateChecksums = true },
            TestContext.Current.CancellationToken);
        EvtxLog unvalidated = EvtxParser.Parse(data, cancellationToken: TestContext.Current.CancellationToken);

        EvtxDiagnostic[] skipped = validated.Diagnostics.Where(d => d.Kind == EvtxDiagnosticKind.ChunkChecksumMismatch).ToArray();
        Assert.NotEmpty(skipped);
        Assert.Equal(0, validated.RecordCount);
        Assert.NotEqual(0, unvalidated.RecordCount);
        Assert.DoesNotContain(unvalidated.Diagnostics, d => d.Kind == EvtxDiagnosticKind.ChunkChecksumMismatch);
        foreach (EvtxDiagnostic diagnostic in skipped)
        {
            Assert.Null(diagnostic.RecordId);
            Assert.Equal(0, (diagnostic.FileOffset - 4096) % EvtxChunk.ChunkSize);
            Assert.Equal($"Chunk at offset 0x{diagnostic.FileOffset:X} skipped: CRC32 checksum validation failed", diagnostic.Message);
        }
    }

    /// <summary>
    /// Verifies that each event's <see cref="EvtxEvent.FileOffset"/> points at its record header: the 0x00002A2A magic
    /// followed by the record ID, for every event of every sample (including recovered chunks).
    /// </summary>
    [Fact]
    public void EventFileOffsetsPointAtRecordHeaders()
    {
        foreach (string file in Directory.GetFiles(_testDataDir, "*.evtx"))
        {
            byte[] data = File.ReadAllBytes(file);
            foreach (EvtxEvent evt in EvtxParser.Parse(data, cancellationToken: TestContext.Current.CancellationToken).Events)
            {
                int offset = checked((int)evt.FileOffset);
                Assert.True(data.AsSpan(offset, 4).SequenceEqual("\x2a\x2a\x00\x00"u8), $"{Path.GetFileName(file)} @0x{offset:X}: no record magic");
                Assert.Equal(evt.RecordId, BitConverter.ToUInt64(data, offset + 8));
            }
        }
    }

    /// <summary>
    /// Verifies that stopping a multi-threaded <see cref="EvtxParser.ReadEvents(string, EvtxOptions?, CancellationToken)"/>
    /// enumeration early returns promptly and releases the file (it can be deleted afterwards).
    /// </summary>
    [Fact]
    public void ReadEventsStopsEarlyAndClosesTheFile()
    {
        string path = Path.Combine(Directory.CreateTempSubdirectory("axoparse-readevents-").FullName, "copy.evtx");
        File.Copy(Path.Combine(_testDataDir, "security_big_sample.evtx"), path);

        int seen = 0;
        foreach (EvtxEvent _ in EvtxParser.ReadEvents(path, new EvtxOptions { MaxThreads = 4 }, TestContext.Current.CancellationToken))
        {
            if (++seen == 10)
                break;
        }

        Assert.Equal(10, seen);
        File.Delete(path);
        Assert.False(File.Exists(path));
    }

    /// <summary>
    /// Verifies that <see cref="EvtxParser.ParseChunk"/> renders a chunk exactly as a whole-file parse does, with
    /// offsets relative to the chunk, and rejects input shorter than a chunk.
    /// </summary>
    [Fact]
    public void ParseChunkMatchesWholeFileParse()
    {
        byte[] data = File.ReadAllBytes(Path.Combine(_testDataDir, "security.evtx"));
        EvtxLog log = EvtxParser.Parse(data, new EvtxOptions { MaxThreads = 1 }, TestContext.Current.CancellationToken);
        EvtxChunk first = log.Chunks[0];

        EvtxChunk standalone = EvtxParser.ParseChunk(data.AsSpan((int)first.FileOffset, EvtxChunk.ChunkSize));

        Assert.Equal(0, standalone.FileOffset);
        Assert.Equal(first.Header, standalone.Header);
        Assert.Equal(first.Events.Count, standalone.Events.Count);
        for (int i = 0; i < first.Events.Count; i++)
        {
            Assert.Equal(first.Events[i].RecordId, standalone.Events[i].RecordId);
            Assert.Equal(first.Events[i].FileOffset - first.FileOffset, standalone.Events[i].FileOffset);
            Assert.True(first.Events[i].Utf8.Span.SequenceEqual(standalone.Events[i].Utf8.Span), $"record {i} output differs");
        }

        Assert.Throws<ArgumentException>(() => EvtxParser.ParseChunk(data.AsSpan(4096, 1000)));
    }

    #endregion

    #region Non-Public Methods

    /// <summary>
    /// Asserts that <paramref name="actual"/> holds the same events as <paramref name="expected"/>.
    /// </summary>
    /// <param name="expected">Events of the reference parse.</param>
    /// <param name="actual">Events under test.</param>
    /// <param name="where">Context for failure messages.</param>
    private static void AssertSameEvents(List<EvtxEvent> expected, IEnumerable<EvtxEvent> actual, string where)
    {
        int index = 0;
        foreach (EvtxEvent evt in actual)
        {
            Assert.True(index < expected.Count, $"{where}: yielded more than Parse's {expected.Count} events");
            EvtxEvent want = expected[index];
            Assert.True(want.RecordId == evt.RecordId, $"{where} event {index}: record ID differs");
            Assert.True(want.FileOffset == evt.FileOffset, $"{where} event {index}: file offset differs");
            Assert.True(want.WrittenTimeUtc == evt.WrittenTimeUtc, $"{where} event {index}: written time differs");
            Assert.True(want.Utf8.Span.SequenceEqual(evt.Utf8.Span), $"{where} event {index}: output differs");
            Assert.True(want.Diagnostic == evt.Diagnostic, $"{where} event {index}: diagnostic differs");
            index++;
        }

        Assert.True(index == expected.Count, $"{where}: yielded {index} events, Parse {expected.Count}");
    }

    /// <summary>
    /// Collects an async stream of events.
    /// </summary>
    /// <param name="events">The events.</param>
    /// <returns>The events as a list.</returns>
    private static async Task<List<EvtxEvent>> ToListAsync(IAsyncEnumerable<EvtxEvent> events)
    {
        List<EvtxEvent> list = new();
        await foreach (EvtxEvent evt in events)
            list.Add(evt);
        return list;
    }

    #endregion

    #region Non-Public Fields

    private static readonly string _testDataDir = TestPaths.TestDataDir;

    #endregion
}
