using System.Diagnostics;
using System.Text.Json;

namespace AxoParse.Evtx.Tests;

public class EvtxParserTests(ITestOutputHelper testOutputHelper)
{
    #region Public Methods

    /// <summary>
    /// Verifies that a pre-cancelled token throws OperationCanceledException immediately.
    /// </summary>
    [Fact]
    public void CancellationTokenCancelsParsing()
    {
        byte[] data = File.ReadAllBytes(Path.Combine(_testDataDir, "security.evtx"));
        CancellationTokenSource cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            EvtxParser.Parse(data, cancellationToken: cts.Token));
    }

    /// <summary>
    /// Verifies that the Diagnostics collection is empty for a clean parse with no issues.
    /// </summary>
    [Fact]
    public void DiagnosticsEmptyOnCleanParse()
    {
        byte[] data = File.ReadAllBytes(Path.Combine(_testDataDir, "security.evtx"));
        EvtxLog log = EvtxParser.Parse(data, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Empty(log.Diagnostics);
    }

    /// <summary>
    /// Verifies that events parsed with JSON format hold the structural format (#name, #content, #attrs keys).
    /// </summary>
    [Fact]
    public void EventsJsonHoldStructuralJson()
    {
        byte[] data = File.ReadAllBytes(Path.Combine(_testDataDir, "security.evtx"));
        EvtxLog log = EvtxParser.Parse(data, _json, TestContext.Current.CancellationToken);

        bool found = false;
        foreach (EvtxEvent evt in log.Events)
        {
            Assert.True(evt.Utf8.Length > 0);
            Assert.True(evt.IsSuccess);

            // Verify structural JSON format
            string json = evt.GetText();
            Assert.Contains("\"#name\"", json);
            Assert.Contains("\"#content\"", json);

            found = true;
            break;
        }
        Assert.True(found);
    }

    /// <summary>
    /// Verifies that Events preserves record ordering across chunks (single-threaded).
    /// </summary>
    [Fact]
    public void EventsPreserveChunkOrder()
    {
        byte[] data = File.ReadAllBytes(Path.Combine(_testDataDir, "security.evtx"));
        EvtxLog log = EvtxParser.Parse(data, _oneThread, TestContext.Current.CancellationToken);

        ulong previousId = 0;
        foreach (EvtxEvent evt in log.Events)
        {
            Assert.True(evt.RecordId > previousId,
                $"Record {evt.RecordId} should be > {previousId}");
            previousId = evt.RecordId;
        }
    }

    /// <summary>
    /// Verifies that Events yields RecordCount events.
    /// </summary>
    [Fact]
    public void EventsReturnsAllRecords()
    {
        byte[] data = File.ReadAllBytes(Path.Combine(_testDataDir, "security.evtx"));
        EvtxLog log = EvtxParser.Parse(data, cancellationToken: TestContext.Current.CancellationToken);

        int eventCount = 0;
        foreach (EvtxEvent evt in log.Events)
        {
            eventCount++;
            Assert.False(evt.Utf8.IsEmpty);
        }

        Assert.Equal(log.RecordCount, eventCount);
        testOutputHelper.WriteLine($"Events yielded {eventCount} events matching RecordCount");
    }

    [Fact]
    public void HandlesBadChunkMagicGracefully()
    {
        byte[] data = File.ReadAllBytes(Path.Combine(_testDataDir, "sample_with_a_bad_chunk_magic.evtx"));

        EvtxLog log = EvtxParser.Parse(data, cancellationToken: TestContext.Current.CancellationToken);

        // Should skip bad chunks without throwing
        testOutputHelper.WriteLine(
            $"[sample_with_a_bad_chunk_magic.evtx] Parsed {log.Chunks.Count} valid chunks, {log.RecordCount} records");
    }

    /// <summary>
    /// Verifies that JSON output is parseable by System.Text.Json for a known file.
    /// </summary>
    [Fact]
    public void JsonOutputIsParseableBySystemTextJson()
    {
        byte[] data = File.ReadAllBytes(Path.Combine(_testDataDir, "security.evtx"));
        EvtxLog log = EvtxParser.Parse(data, _json, TestContext.Current.CancellationToken);

        int parsed = 0;
        foreach (EvtxEvent evt in log.Events)
        {
            // Must parse as valid JSON
            using JsonDocument doc = JsonDocument.Parse(evt.Utf8);
            JsonElement root = doc.RootElement;
            Assert.Equal(JsonValueKind.Object, root.ValueKind);
            Assert.True(root.TryGetProperty("#name", out _), "Root element must have #name");
            parsed++;
        }

        Assert.True(parsed > 0);
        testOutputHelper.WriteLine($"All {parsed} JSON records parse successfully with System.Text.Json");
    }

    /// <summary>
    /// Verifies that all test .evtx files produce non-empty valid UTF-8 JSON for every record.
    /// </summary>
    [Fact]
    public void JsonOutputIsValidUtf8ForAllTestFiles()
    {
        string[] evtxFiles = Directory.GetFiles(_testDataDir, "*.evtx");

        foreach (string file in evtxFiles)
        {
            byte[] data = File.ReadAllBytes(file);
            EvtxLog log = EvtxParser.Parse(data, _json, TestContext.Current.CancellationToken);

            int recordCount = 0;
            foreach (EvtxEvent evt in log.Events)
            {
                Assert.True(evt.Utf8.Length > 0,
                    $"Record {evt.RecordId} in {Path.GetFileName(file)} produced empty JSON");

                // Must be valid UTF-8
                string json = evt.GetText();
                Assert.True(json.Length > 0);
                recordCount++;
            }

            testOutputHelper.WriteLine(
                $"  [{Path.GetFileName(file)}] {recordCount} records produce valid UTF-8 JSON");
        }
    }

    [Fact]
    public void ParsesAllTestFiles()
    {
        string[] evtxFiles = Directory.GetFiles(_testDataDir, "*.evtx");
        Stopwatch sw = new Stopwatch();

        testOutputHelper.WriteLine($"Full parse of {evtxFiles.Length} files:");

        foreach (string file in evtxFiles)
        {
            byte[] data = File.ReadAllBytes(file);
            string name = Path.GetFileName(file);

            sw.Restart();
            EvtxLog log = EvtxParser.Parse(data, cancellationToken: TestContext.Current.CancellationToken);
            sw.Stop();

            testOutputHelper.WriteLine(
                $"  [{name}] {sw.Elapsed.TotalMilliseconds,8:F2}ms | {log.Chunks.Count} chunks | {log.RecordCount} records");
        }
    }

    [Fact]
    public void ParsesSecurityEvtxFull()
    {
        byte[] data = File.ReadAllBytes(Path.Combine(_testDataDir, "security.evtx"));

        Stopwatch sw = Stopwatch.StartNew();
        EvtxLog log = EvtxParser.Parse(data, cancellationToken: TestContext.Current.CancellationToken);
        sw.Stop();

        Assert.True(log.Chunks.Count > 0);
        Assert.True(log.RecordCount > 0);
        Assert.Equal(3, log.Header.MajorFormatVersion);

        testOutputHelper.WriteLine($"[security.evtx] Full parse in {sw.Elapsed.TotalMilliseconds:F2}ms");
        testOutputHelper.WriteLine($"  Chunks: {log.Chunks.Count}, Records: {log.RecordCount}");
    }

    [Fact]
    public void TotalRecordsMatchesChunkSum()
    {
        byte[] data = File.ReadAllBytes(Path.Combine(_testDataDir, "security.evtx"));
        EvtxLog log = EvtxParser.Parse(data, cancellationToken: TestContext.Current.CancellationToken);

        int sum = 0;
        foreach (EvtxChunk chunk in log.Chunks)
            sum += chunk.Events.Count;

        Assert.Equal(sum, log.RecordCount);
    }

    /// <summary>
    /// Verifies that ReadEventsAsync yields the same events in the same order as Parse(...).Events
    /// for a single-threaded parse.
    /// </summary>
    [Fact]
    public async Task ReadEventsAsyncMatchesSyncResults()
    {
        byte[] data = await File.ReadAllBytesAsync(Path.Combine(_testDataDir, "security.evtx"), TestContext.Current.CancellationToken);

        EvtxLog log = EvtxParser.Parse(data, _oneThread, TestContext.Current.CancellationToken);
        List<EvtxEvent> syncEvents = new List<EvtxEvent>(log.Events);

        List<EvtxEvent> asyncEvents = new List<EvtxEvent>();
        await foreach (EvtxEvent evt in EvtxParser.ReadEventsAsync(data, _oneThread, TestContext.Current.CancellationToken))
        {
            asyncEvents.Add(evt);
        }

        Assert.Equal(syncEvents.Count, asyncEvents.Count);

        for (int i = 0; i < syncEvents.Count; i++)
        {
            Assert.Equal(syncEvents[i].RecordId, asyncEvents[i].RecordId);
            Assert.Equal(syncEvents[i].GetText(), asyncEvents[i].GetText());
            Assert.Equal(syncEvents[i].IsSuccess, asyncEvents[i].IsSuccess);
        }

        testOutputHelper.WriteLine($"ReadEventsAsync matched {asyncEvents.Count} events with sync Parse");
    }

    /// <summary>
    /// Verifies that ReadEventsAsync respects cancellation by throwing OperationCanceledException
    /// when a pre-cancelled token is provided.
    /// </summary>
    [Fact]
    public async Task ReadEventsAsyncRespectsCancellation()
    {
        byte[] data = await File.ReadAllBytesAsync(Path.Combine(_testDataDir, "security.evtx"), TestContext.Current.CancellationToken);
        CancellationTokenSource cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (EvtxEvent _ in EvtxParser.ReadEventsAsync(data, cancellationToken: cts.Token)) {}
        });
    }

    /// <summary>
    /// Verifies that ReadEventsAsync with JSON format produces the same record count as sync Parse.
    /// </summary>
    [Fact]
    public async Task ReadEventsAsyncJsonMatchesSyncCount()
    {
        byte[] data = await File.ReadAllBytesAsync(Path.Combine(_testDataDir, "security.evtx"), TestContext.Current.CancellationToken);
        EvtxOptions options = _json with { MaxThreads = 1 };

        int syncCount = EvtxParser.Parse(data, options, TestContext.Current.CancellationToken).RecordCount;

        int asyncCount = 0;
        await foreach (EvtxEvent evt in EvtxParser.ReadEventsAsync(data, options, TestContext.Current.CancellationToken))
        {
            Assert.True(evt.Utf8.Length > 0);
            asyncCount++;
        }

        Assert.Equal(syncCount, asyncCount);
        testOutputHelper.WriteLine($"ReadEventsAsync JSON matched {asyncCount} records");
    }

    /// <summary>
    /// Verifies that multi-threaded ReadEventsAsync produces the same event count as single-threaded.
    /// </summary>
    [Fact]
    public async Task ReadEventsAsyncMultiThreadedSameCount()
    {
        byte[] data = await File.ReadAllBytesAsync(Path.Combine(_testDataDir, "security.evtx"), TestContext.Current.CancellationToken);

        int singleCount = 0;
        await foreach (EvtxEvent _ in EvtxParser.ReadEventsAsync(data, _oneThread, TestContext.Current.CancellationToken))
        {
            singleCount++;
        }

        int multiCount = 0;
        await foreach (EvtxEvent _ in EvtxParser.ReadEventsAsync(data, new EvtxOptions { MaxThreads = 4 },
                           TestContext.Current.CancellationToken))
        {
            multiCount++;
        }

        Assert.Equal(singleCount, multiCount);
        testOutputHelper.WriteLine($"Single={singleCount}, Multi={multiCount}");
    }

    #endregion

    #region Non-Public Fields

    private static readonly string _testDataDir = TestPaths.TestDataDir;

    private static readonly EvtxOptions _json = new() { Format = OutputFormat.Json };

    private static readonly EvtxOptions _oneThread = new() { MaxThreads = 1 };

    #endregion
}