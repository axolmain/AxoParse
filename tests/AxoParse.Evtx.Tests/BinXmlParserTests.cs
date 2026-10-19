using System.Diagnostics;
using System.Text;

namespace AxoParse.Evtx.Tests;

public class BinXmlParserTests(ITestOutputHelper testOutputHelper)
{
    #region Public Methods

    [Fact]
    public void AllRecordsNonEmpty()
    {
        byte[] data = File.ReadAllBytes(Path.Combine(_testDataDir, "security.evtx"));
        EvtxLog parser = EvtxParser.Parse(data, cancellationToken: TestContext.Current.CancellationToken);

        int totalRecords = 0;
        foreach (EvtxChunk chunk in parser.Chunks)
        {
            Assert.Equal(chunk.Records.Count, chunk.Events.Count);
            for (int i = 0; i < chunk.Events.Count; i++)
            {
                Assert.False(string.IsNullOrEmpty(chunk.Events[i].GetText()),
                    $"Record {chunk.Records[i].EventRecordId} produced empty XML");
                totalRecords++;
            }
        }

        testOutputHelper.WriteLine($"All {totalRecords} records produced non-empty XML");
    }

    [Fact]
    public void FirstRecordStartsWithEventXmlns()
    {
        byte[] data = File.ReadAllBytes(Path.Combine(_testDataDir, "security.evtx"));
        EvtxLog parser = EvtxParser.Parse(data, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(parser.Chunks.Count > 0);
        Assert.True(parser.Chunks[0].Events.Count > 0);

        string xml = parser.Chunks[0].Events[0].GetText();
        testOutputHelper.WriteLine($"First record XML ({xml.Length} chars):");
        testOutputHelper.WriteLine(xml.Length > 500 ? xml[..500] + "..." : xml);

        Assert.StartsWith("<Event xmlns=", xml);
    }

    [Fact]
    public void MultithreadedParsingProducesIdenticalOutput()
    {
        byte[] data = File.ReadAllBytes(Path.Combine(_testDataDir, "security.evtx"));

        // Parse with 1 thread as baseline
        EvtxLog baseline = EvtxParser.Parse(data, new EvtxOptions { MaxThreads = 1 }, TestContext.Current.CancellationToken);
        string[] baselineXml = FlattenXml(baseline);

        foreach (int threadCount in new[] { 2, 4, 8 })
        {
            EvtxLog result = EvtxParser.Parse(data, new EvtxOptions { MaxThreads = threadCount }, TestContext.Current.CancellationToken);
            string[] resultXml = FlattenXml(result);

            Assert.Equal(baselineXml.Length, resultXml.Length);
            for (int i = 0; i < baselineXml.Length; i++)
            {
                Assert.True(baselineXml[i] == resultXml[i],
                    $"Record {i} differs with {threadCount} threads. " +
                    $"Expected length {baselineXml[i].Length}, got {resultXml[i].Length}");
            }

            testOutputHelper.WriteLine($"  {threadCount} threads: {resultXml.Length} records match baseline");
        }
    }

    [Fact]
    public void ParsesAllTestFilesWithoutExceptions()
    {
        string[] evtxFiles = Directory.GetFiles(_testDataDir, "*.evtx");
        Stopwatch sw = new Stopwatch();

        testOutputHelper.WriteLine($"BinXml parse of {evtxFiles.Length} files:");

        foreach (string file in evtxFiles)
        {
            byte[] data = File.ReadAllBytes(file);
            string name = Path.GetFileName(file);

            sw.Restart();
            EvtxLog parser = EvtxParser.Parse(data, cancellationToken: TestContext.Current.CancellationToken);
            sw.Stop();

            int xmlCount = 0;
            foreach (EvtxChunk chunk in parser.Chunks)
                xmlCount += chunk.Events.Count;

            testOutputHelper.WriteLine(
                $"  [{name}] {sw.Elapsed.TotalMilliseconds,8:F2}ms | {parser.RecordCount} records | {xmlCount} XML");
        }
    }

    /// <summary>
    /// Verifies that an entity reference whose name is defined inline (the name structure follows the name offset, as
    /// for element names) is skipped correctly. CAPI2 record 485 carries an embedded BinXml fragment with
    /// <c>Via: 1.1 [&amp;quot;web-proxy.shieldbase.lan&amp;quot;]</c>; reading the inline name bytes as tokens used to
    /// truncate the event after the first <c>&amp;quot;</c>, which the Rust evtx crate renders in full.
    /// </summary>
    /// <param name="format">Output format.</param>
    [Theory]
    [InlineData(OutputFormat.Xml)]
    [InlineData(OutputFormat.Json)]
    public void InlineEntityNamesAreSkipped(OutputFormat format)
    {
        byte[] data = File.ReadAllBytes(Path.Combine(_testDataDir,
            "E_ShadowCopy6_windows_system32_winevt_logs_Microsoft-Windows-CAPI2%4Operational.evtx"));
        EvtxLog parser = EvtxParser.Parse(data, new EvtxOptions { MaxThreads = 1, Format = format }, TestContext.Current.CancellationToken);

        EvtxEvent evt = parser.Events.Single(e => e.RecordId == 485);
        string output = format == OutputFormat.Xml ? evt.GetText() : Encoding.UTF8.GetString(evt.Utf8.Span);
        Assert.True(evt.IsSuccess, evt.Diagnostic?.Message);
        Assert.Contains("web-proxy.shieldbase.lan", output);
        Assert.Contains("x-ms-request-id: 44210c66-701e-00db-07a5-2ae3a6000000", output);
    }

    /// <summary>
    /// Verifies that substitution values (EventID, Level, EventData, etc.) render correctly
    /// in the XML output. Regression test for a bug where substitution value offsets were
    /// span-relative instead of absolute file offsets, causing all numeric fields to read as 0
    /// and EventData/UserData sections to render empty.
    /// </summary>
    [Fact]
    public void SubstitutionValuesRenderCorrectly()
    {
        string[] evtxFiles = Directory.GetFiles(_testDataDir, "*.evtx");
        int totalRecords = 0;
        int withEventData = 0;
        int withNonZeroEventId = 0;

        foreach (string file in evtxFiles)
        {
            byte[] data = File.ReadAllBytes(file);
            EvtxLog parser = EvtxParser.Parse(data, new EvtxOptions { MaxThreads = 1 }, TestContext.Current.CancellationToken);

            foreach (EvtxChunk chunk in parser.Chunks)
            {
                foreach (EvtxEvent evt in chunk.Events)
                {
                    string xml = evt.GetText();
                    totalRecords++;

                    if (xml.Contains("<EventData") || xml.Contains("<UserData"))
                        withEventData++;

                    // Check that EventID is not always 0 (was the symptom of the offset bug)
                    if (!xml.Contains("<EventID Qualifiers=\"\">0</EventID>"))
                        withNonZeroEventId++;
                }
            }
        }

        testOutputHelper.WriteLine($"Total: {totalRecords}, EventData: {withEventData}, NonZeroEventID: {withNonZeroEventId}");

        // With correct offsets, most records should have EventData/UserData
        Assert.True(withEventData > 100,
            $"Expected many records with EventData/UserData, got {withEventData}");

        // With correct offsets, most records should have non-zero EventID
        Assert.True(withNonZeroEventId > 100,
            $"Expected many records with non-zero EventID, got {withNonZeroEventId}");
    }

    [Fact]
    public void XmlOutputSurvivesUtf8EncodingWithUnpairedSurrogates()
    {
        // EVTX files may contain corrupt UTF-16 data with unpaired surrogates (e.g. lone \uDE1E).
        // Previously this caused EncoderFallbackException when writing XML to stdout via StreamWriter.
        // The parser's AppendXmlEscaped now replaces unpaired surrogates with U+FFFD so all output
        // is valid Unicode that encodes to UTF-8 without error.
        string[] evtxFiles = Directory.GetFiles(_testDataDir, "*.evtx");

        foreach (string file in evtxFiles)
        {
            byte[] data = File.ReadAllBytes(file);
            EvtxLog parser = EvtxParser.Parse(data, new EvtxOptions { MaxThreads = 1 }, TestContext.Current.CancellationToken);

            int recordCount = 0;
            foreach (EvtxChunk chunk in parser.Chunks)
            {
                for (int i = 0; i < chunk.Events.Count; i++)
                {
                    string xml = chunk.Events[i].GetText();
                    // Encoding to UTF-8 must not throw
                    byte[] utf8Bytes = Encoding.UTF8.GetBytes(xml);
                    Assert.True(utf8Bytes.Length > 0,
                        $"Record {chunk.Records[i].EventRecordId} in {Path.GetFileName(file)} produced empty UTF-8");
                    recordCount++;
                }
            }

            testOutputHelper.WriteLine(
                $"  [{Path.GetFileName(file)}] {recordCount} records encode to UTF-8 without error");
        }
    }

    #endregion

    #region Non-Public Methods

    private static string[] FlattenXml(EvtxLog parser)
    {
        List<string> all = new();
        foreach (EvtxChunk chunk in parser.Chunks)
            all.AddRange(chunk.Events.Select(e => e.GetText()));
        return all.ToArray();
    }

    #endregion

    #region Non-Public Fields

    private static readonly string _testDataDir = TestPaths.TestDataDir;

    #endregion
}