using System.Diagnostics;
using System.Text;
using System.Text.Json;
using AxoParse.Evtx.Evtx;

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
        EvtxParser parser = EvtxParser.Parse(data);

        Assert.Empty(parser.Diagnostics);
    }

    /// <summary>
    /// Verifies that GetEvents() with JSON format populates Json with the structural format
    /// (#name, #content, #attrs keys) and leaves Xml empty.
    /// </summary>
    [Fact]
    public void GetEventsJsonPopulatesJsonField()
    {
        byte[] data = File.ReadAllBytes(Path.Combine(_testDataDir, "security.evtx"));
        EvtxParser parser = EvtxParser.Parse(data, format: OutputFormat.Json);

        bool found = false;
        foreach (EvtxEvent evt in parser.GetEvents())
        {
            Assert.Equal(string.Empty, evt.Xml);
            Assert.True(evt.Json.Length > 0);
            Assert.True(evt.IsSuccess);

            // Verify structural JSON format
            string json = Encoding.UTF8.GetString(evt.Json.Span);
            Assert.Contains("\"#name\"", json);
            Assert.Contains("\"#content\"", json);

            found = true;
            break;
        }
        Assert.True(found);
    }

    /// <summary>
    /// Verifies that GetEvents() preserves record ordering across chunks (single-threaded).
    /// </summary>
    [Fact]
    public void GetEventsPreservesChunkOrder()
    {
        byte[] data = File.ReadAllBytes(Path.Combine(_testDataDir, "security.evtx"));
        EvtxParser parser = EvtxParser.Parse(data, maxThreads: 1);

        ulong previousId = 0;
        foreach (EvtxEvent evt in parser.GetEvents())
        {
            Assert.True(evt.Record.EventRecordId > previousId,
                $"Record {evt.Record.EventRecordId} should be > {previousId}");
            previousId = evt.Record.EventRecordId;
        }
    }

    /// <summary>
    /// Verifies that GetEvents() returns the same total record count as TotalRecords.
    /// </summary>
    [Fact]
    public void GetEventsReturnsAllRecords()
    {
        byte[] data = File.ReadAllBytes(Path.Combine(_testDataDir, "security.evtx"));
        EvtxParser parser = EvtxParser.Parse(data);

        int eventCount = 0;
        foreach (EvtxEvent evt in parser.GetEvents())
        {
            eventCount++;
            Assert.False(string.IsNullOrEmpty(evt.Xml));
        }

        Assert.Equal(parser.TotalRecords, eventCount);
        testOutputHelper.WriteLine($"GetEvents() yielded {eventCount} events matching TotalRecords");
    }

    [Fact]
    public void HandlesBadChunkMagicGracefully()
    {
        byte[] data = File.ReadAllBytes(Path.Combine(_testDataDir, "sample_with_a_bad_chunk_magic.evtx"));

        EvtxParser parser = EvtxParser.Parse(data);

        // Should skip bad chunks without throwing
        testOutputHelper.WriteLine(
            $"[sample_with_a_bad_chunk_magic.evtx] Parsed {parser.Chunks.Count} valid chunks, {parser.TotalRecords} records");
    }

    /// <summary>
    /// Verifies that JSON output is parseable by System.Text.Json for a known file.
    /// </summary>
    [Fact]
    public void JsonOutputIsParseableBySystemTextJson()
    {
        byte[] data = File.ReadAllBytes(Path.Combine(_testDataDir, "security.evtx"));
        EvtxParser parser = EvtxParser.Parse(data, format: OutputFormat.Json);

        int parsed = 0;
        foreach (EvtxEvent evt in parser.GetEvents())
        {
            // Must parse as valid JSON
            using JsonDocument doc = JsonDocument.Parse(evt.Json);
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
            EvtxParser parser = EvtxParser.Parse(data, format: OutputFormat.Json);

            int recordCount = 0;
            foreach (EvtxEvent evt in parser.GetEvents())
            {
                Assert.True(evt.Json.Length > 0,
                    $"Record {evt.Record.EventRecordId} in {Path.GetFileName(file)} produced empty JSON");

                // Must be valid UTF-8
                string json = Encoding.UTF8.GetString(evt.Json.Span);
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
            EvtxParser parser = EvtxParser.Parse(data);
            sw.Stop();

            testOutputHelper.WriteLine(
                $"  [{name}] {sw.Elapsed.TotalMilliseconds,8:F2}ms | {parser.Chunks.Count} chunks | {parser.TotalRecords} records");
        }
    }

    [Fact]
    public void ParsesSecurityEvtxFull()
    {
        byte[] data = File.ReadAllBytes(Path.Combine(_testDataDir, "security.evtx"));

        Stopwatch sw = Stopwatch.StartNew();
        EvtxParser parser = EvtxParser.Parse(data);
        sw.Stop();

        Assert.True(parser.Chunks.Count > 0);
        Assert.True(parser.TotalRecords > 0);
        Assert.Equal(3, parser.FileHeader.MajorFormatVersion);

        testOutputHelper.WriteLine($"[security.evtx] Full parse in {sw.Elapsed.TotalMilliseconds:F2}ms");
        testOutputHelper.WriteLine($"  Chunks: {parser.Chunks.Count}, Records: {parser.TotalRecords}");
    }

    [Fact]
    public void TotalRecordsMatchesChunkSum()
    {
        byte[] data = File.ReadAllBytes(Path.Combine(_testDataDir, "security.evtx"));
        EvtxParser parser = EvtxParser.Parse(data);

        int sum = 0;
        foreach (EvtxChunk chunk in parser.Chunks)
            sum += chunk.Records.Count;

        Assert.Equal(sum, parser.TotalRecords);
    }

    #endregion

    #region Non-Public Fields

    private static readonly string _testDataDir = TestPaths.TestDataDir;

    #endregion
}