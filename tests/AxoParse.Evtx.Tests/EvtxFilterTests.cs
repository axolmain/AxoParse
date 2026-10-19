namespace AxoParse.Evtx.Tests;

public class EvtxFilterTests
{
    #region Public Methods

    /// <summary>
    /// Verifies that a filtered parse produces exactly the events of the unfiltered parse that pass the filter (same
    /// order, offsets and output), for every sample, a range of filters (event IDs, levels, providers, time and
    /// record-ID ranges, combined), single- and multi-threaded. Record-ID ranges skip chunks by their headers, so this
    /// also proves no matching record is lost to a skipped chunk. <c>WriteTo</c> must write the same events' output.
    /// </summary>
    /// <param name="threads">Thread count.</param>
    [Theory]
    [InlineData(1)]
    [InlineData(8)]
    public void FilteredParseEqualsUnfilteredParseFiltered(int threads)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        int checkedFilters = 0;
        foreach (string file in Directory.GetFiles(TestPaths.TestDataDir, "*.evtx"))
        {
            string name = Path.GetFileName(file);
            byte[] data = File.ReadAllBytes(file);
            EvtxOptions baseOptions = new() { MaxThreads = threads, Fields = EvtxFields.System };
            List<EvtxEvent> all = EvtxParser.Parse(data, baseOptions, token).Events.ToList();
            if (all.Count == 0)
                continue;

            foreach ((string label, EvtxFilter filter, Func<EvtxEvent, bool> predicate) in Filters(all))
            {
                checkedFilters++;
                string where = $"{name} [{label}] ({threads} threads)";
                List<EvtxEvent> expected = all.Where(predicate).ToList();
                EvtxOptions options = baseOptions with { Filter = filter };
                List<EvtxEvent> actual = EvtxParser.Parse(data, options, token).Events.ToList();
                AssertSame(expected, actual, where);

                using MemoryStream written = new();
                int count = EvtxParser.WriteTo(data, written, options, token);
                Assert.True(expected.Count == count, $"{where}: WriteTo wrote {count} records, expected {expected.Count}");
                using MemoryStream framed = new();
                foreach (EvtxEvent evt in expected.Where(e => !e.Utf8.IsEmpty))
                {
                    framed.Write(evt.Utf8.Span);
                    framed.WriteByte((byte)'\n');
                }
                Assert.True(framed.ToArray().AsSpan().SequenceEqual(written.ToArray()), $"{where}: WriteTo output differs");
            }
        }

        Assert.True(checkedFilters > 100, $"only {checkedFilters} filters checked");
    }

    /// <summary>
    /// Verifies that fields-only and streaming reads apply the filter the same way as a rendering parse.
    /// </summary>
    [Fact]
    public async Task FilterAppliesToEveryReadPath()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        string file = Path.Combine(TestPaths.TestDataDir, "security_big_sample.evtx");
        List<EvtxEvent> all = EvtxParser.Parse(file, new EvtxOptions { Fields = EvtxFields.System }, token).Events.ToList();
        ushort[] eventIds = all.GroupBy(e => e.EventId).OrderByDescending(g => g.Count()).Take(3).Select(g => g.Key).ToArray();
        EvtxFilter filter = new() { EventIds = eventIds, FromRecordId = all[all.Count / 10].RecordId, ToRecordId = all[^(all.Count / 10)].RecordId };
        EvtxOptions options = new() { Filter = filter, Fields = EvtxFields.System };
        List<ulong> expected = EvtxParser.Parse(file, options, token).Events.Select(e => e.RecordId).ToList();
        Assert.NotEmpty(expected);

        Assert.Equal(expected, EvtxParser.ReadEvents(file, options with { MaxThreads = 8 }, token).Select(e => e.RecordId));
        Assert.Equal(expected, EvtxParser.ReadEvents(file, options with { FieldsOnly = true }, token).Select(e => e.RecordId));
        List<ulong> async = new();
        await foreach (EvtxEvent evt in EvtxParser.ReadEventsAsync(file, options, token))
            async.Add(evt.RecordId);
        Assert.Equal(expected, async);
        Assert.All(EvtxParser.ReadEvents(file, options, token), evt => Assert.Contains(evt.EventId, filter.EventIds!));
    }

    /// <summary>
    /// Verifies record-ID lookup: the found event equals the one a full parse yields, and a missing ID gives null.
    /// </summary>
    [Fact]
    public void FindEventReturnsTheRecord()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        byte[] data = File.ReadAllBytes(Path.Combine(TestPaths.TestDataDir, "security_big_sample.evtx"));
        List<EvtxEvent> all = EvtxParser.Parse(data, cancellationToken: token).Events.ToList();
        foreach (EvtxEvent want in new[] { all[0], all[all.Count / 2], all[^1] })
        {
            EvtxEvent found = Assert.NotNull(EvtxParser.FindEvent(data, want.RecordId, cancellationToken: token));
            Assert.Equal(want.RecordId, found.RecordId);
            Assert.Equal(want.FileOffset, found.FileOffset);
            Assert.True(want.Utf8.Span.SequenceEqual(found.Utf8.Span));
        }

        Assert.Null(EvtxParser.FindEvent(data, ulong.MaxValue, cancellationToken: token));
    }

    /// <summary>
    /// Verifies resuming by record ID: reading pages of 1,000 events, each starting after the previous page's last
    /// record ID, yields every event exactly once (on a log whose output order is record-ID order).
    /// </summary>
    [Fact]
    public void ResumeByRecordIdPagesThroughTheLog()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        string file = Path.Combine(TestPaths.TestDataDir, "security_big_sample.evtx");
        List<ulong> all = EvtxParser.ReadEvents(file, cancellationToken: token).Select(e => e.RecordId).ToList();
        Assert.Equal(all.Order(), all);

        List<ulong> paged = new();
        ulong next = 0;
        while (true)
        {
            EvtxOptions page = new() { Filter = new EvtxFilter { FromRecordId = next } };
            List<ulong> ids = EvtxParser.ReadEvents(file, page, token).Take(1000).Select(e => e.RecordId).ToList();
            if (ids.Count == 0)
                break;
            paged.AddRange(ids);
            next = ids[^1] + 1;
        }

        Assert.Equal(all, paged);
    }

    /// <summary>
    /// Verifies the chunk-skip decision: a chunk is skipped only when its header's plausible record-ID range lies
    /// entirely outside the filter's, never for an implausible (inverted) range, and a dirty chunk's last ID is not
    /// trusted.
    /// </summary>
    [Fact]
    public void SkipsChunkOnlyForDisjointPlausibleRanges()
    {
        RecordFilter filter = RecordFilter.For(new EvtxFilter { FromRecordId = 100, ToRecordId = 200 })!;
        Assert.True(filter.SkipsChunk(Header(1, 99)));
        Assert.True(filter.SkipsChunk(Header(201, 300)));
        Assert.False(filter.SkipsChunk(Header(50, 100)));
        Assert.False(filter.SkipsChunk(Header(200, 250)));
        Assert.False(filter.SkipsChunk(Header(120, 130)));
        Assert.False(filter.SkipsChunk(Header(300, 1)));
        // A dirty chunk can hold records past its header's last ID: only its first ID is trusted
        Assert.False(filter.SkipsChunk(Header(1, 99, ChunkFlags.Dirty)));
        Assert.True(filter.SkipsChunk(Header(201, 300, ChunkFlags.Dirty)));
        Assert.False(filter.NeedsFields);
        Assert.True(RecordFilter.For(new EvtxFilter { Levels = [2] })!.NeedsFields);
        Assert.Null(RecordFilter.For(null));
    }

    #endregion

    #region Non-Public Methods

    /// <summary>
    /// Asserts that two event lists hold the same events.
    /// </summary>
    /// <param name="expected">Expected events.</param>
    /// <param name="actual">Actual events.</param>
    /// <param name="where">Context for failure messages.</param>
    private static void AssertSame(List<EvtxEvent> expected, List<EvtxEvent> actual, string where)
    {
        Assert.True(expected.Count == actual.Count, $"{where}: {actual.Count} events, expected {expected.Count}");
        for (int i = 0; i < expected.Count; i++)
        {
            Assert.True(expected[i].RecordId == actual[i].RecordId, $"{where} event {i}: record ID differs");
            Assert.True(expected[i].FileOffset == actual[i].FileOffset, $"{where} event {i}: file offset differs");
            Assert.True(expected[i].Utf8.Span.SequenceEqual(actual[i].Utf8.Span), $"{where} event {i}: output differs");
        }
    }

    /// <summary>
    /// Filters built from a log's own events, with the equivalent predicate.
    /// </summary>
    /// <param name="all">Every event of the log (parsed with System fields).</param>
    /// <returns>Labelled filters and predicates.</returns>
    private static IEnumerable<(string Label, EvtxFilter Filter, Func<EvtxEvent, bool> Predicate)> Filters(List<EvtxEvent> all)
    {
        ushort[] topIds = all.GroupBy(e => e.EventId).OrderByDescending(g => g.Count()).Take(2).Select(g => g.Key).ToArray();
        yield return ("event IDs", new EvtxFilter { EventIds = topIds }, e => topIds.Contains(e.EventId));

        byte[] levels = [2, 3];
        yield return ("levels", new EvtxFilter { Levels = levels }, e => levels.Contains(e.Level));

        if (all.FirstOrDefault(e => e.Provider != null).Provider is { } provider)
        {
            string[] providers = [provider.ToUpperInvariant()];
            yield return ("provider", new EvtxFilter { Providers = providers },
                e => string.Equals(e.Provider, provider, StringComparison.OrdinalIgnoreCase));
        }

        DateTime[] times = all.Select(e => e.TimeCreated).Order().ToArray();
        DateTime from = times[times.Length / 3];
        DateTime to = times[2 * times.Length / 3];
        yield return ("time", new EvtxFilter { From = from, To = to }, e => (e.TimeCreated >= from) && (e.TimeCreated < to));

        ulong[] ids = all.Select(e => e.RecordId).Order().ToArray();
        ulong lo = ids[ids.Length / 4];
        ulong hi = ids[3 * ids.Length / 4];
        yield return ("record IDs", new EvtxFilter { FromRecordId = lo, ToRecordId = hi },
            e => (e.RecordId >= lo) && (e.RecordId <= hi));
        yield return ("from record ID", new EvtxFilter { FromRecordId = hi }, e => e.RecordId >= hi);
        yield return ("combined", new EvtxFilter { FromRecordId = lo, ToRecordId = hi, EventIds = topIds },
            e => (e.RecordId >= lo) && (e.RecordId <= hi) && topIds.Contains(e.EventId));
        yield return ("nothing", new EvtxFilter { EventIds = [] }, _ => false);
    }

    /// <summary>
    /// A chunk header with the given record-ID range.
    /// </summary>
    /// <param name="first">First record ID.</param>
    /// <param name="last">Last record ID.</param>
    /// <param name="flags">Chunk flags.</param>
    /// <returns>The header.</returns>
    private static EvtxChunkHeader Header(ulong first, ulong last, ChunkFlags flags = ChunkFlags.None) =>
        new(0, 0, first, last, 128, 0, 0, 0, flags, 0);

    #endregion
}
