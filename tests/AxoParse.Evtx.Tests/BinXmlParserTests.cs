using System.Diagnostics;
using System.Text;

namespace AxoParse.Evtx.Tests;

public class BinXmlParserTests(ITestOutputHelper testOutputHelper)
{
    private static readonly string TestDataDir = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "data"));

    [Fact]
    public void FirstRecordStartsWithEventXmlns()
    {
        byte[] data = File.ReadAllBytes(Path.Combine(TestDataDir, "security.evtx"));
        EvtxParser parser = EvtxParser.Parse(data);

        Assert.True(parser.Chunks.Count > 0);
        Assert.True(parser.Chunks[0].ParsedXml.Count > 0);

        string xml = parser.Chunks[0].ParsedXml[0];
        testOutputHelper.WriteLine($"First record XML ({xml.Length} chars):");
        testOutputHelper.WriteLine(xml.Length > 500 ? xml[..500] + "..." : xml);

        Assert.StartsWith("<Event xmlns=", xml);
    }

    [Fact]
    public void AllRecordsNonEmpty()
    {
        byte[] data = File.ReadAllBytes(Path.Combine(TestDataDir, "security.evtx"));
        EvtxParser parser = EvtxParser.Parse(data);

        int totalRecords = 0;
        foreach (EvtxChunk chunk in parser.Chunks)
        {
            Assert.Equal(chunk.Records.Count, chunk.ParsedXml.Count);
            for (int i = 0; i < chunk.ParsedXml.Count; i++)
            {
                Assert.False(string.IsNullOrEmpty(chunk.ParsedXml[i]),
                    $"Record {chunk.Records[i].EventRecordId} produced empty XML");
                totalRecords++;
            }
        }

        testOutputHelper.WriteLine($"All {totalRecords} records produced non-empty XML");
    }

    [Fact]
    public void ParsesAllTestFilesWithoutExceptions()
    {
        string[] evtxFiles = Directory.GetFiles(TestDataDir, "*.evtx");
        Stopwatch sw = new Stopwatch();

        testOutputHelper.WriteLine($"BinXml parse of {evtxFiles.Length} files:");

        foreach (string file in evtxFiles)
        {
            byte[] data = File.ReadAllBytes(file);
            string name = Path.GetFileName(file);

            sw.Restart();
            EvtxParser parser = EvtxParser.Parse(data);
            sw.Stop();

            int xmlCount = 0;
            foreach (EvtxChunk chunk in parser.Chunks)
                xmlCount += chunk.ParsedXml.Count;

            testOutputHelper.WriteLine(
                $"  [{name}] {sw.Elapsed.TotalMilliseconds,8:F2}ms | {parser.TotalRecords} records | {xmlCount} XML");
        }
    }

    [Fact]
    public void BigSamplePerformance()
    {
        string path = Path.Combine(TestDataDir, "security_big_sample.evtx");
        if (!File.Exists(path)) return;

        byte[] data = File.ReadAllBytes(path);

        Stopwatch sw = Stopwatch.StartNew();
        EvtxParser parser = EvtxParser.Parse(data);
        sw.Stop();

        int xmlCount = 0;
        foreach (EvtxChunk chunk in parser.Chunks)
            xmlCount += chunk.ParsedXml.Count;

        testOutputHelper.WriteLine(
            $"[security_big_sample.evtx] Full parse + BinXml in {sw.Elapsed.TotalMilliseconds:F2}ms");
        testOutputHelper.WriteLine($"  Chunks: {parser.Chunks.Count}, Records: {parser.TotalRecords}, XML: {xmlCount}");
        testOutputHelper.WriteLine($"  Avg: {sw.Elapsed.TotalMicroseconds / parser.TotalRecords:F2}µs/record");
    }

    [Fact]
    public void MultithreadedParsingProducesIdenticalOutput()
    {
        byte[] data = File.ReadAllBytes(Path.Combine(TestDataDir, "security.evtx"));

        // Parse with 1 thread as baseline
        EvtxParser baseline = EvtxParser.Parse(data, 1);
        string[] baselineXml = FlattenXml(baseline);

        foreach (int threadCount in new[] { 2, 4, 8 })
        {
            EvtxParser result = EvtxParser.Parse(data, threadCount);
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

    private static string[] FlattenXml(EvtxParser parser)
    {
        List<string> all = new();
        foreach (EvtxChunk chunk in parser.Chunks)
            all.AddRange(chunk.ParsedXml);
        return all.ToArray();
    }

    [Fact]
    public void SampleXmlOutputForInspection()
    {
        byte[] data = File.ReadAllBytes(Path.Combine(TestDataDir, "security.evtx"));
        EvtxParser parser = EvtxParser.Parse(data);

        testOutputHelper.WriteLine("=== First 5 records XML ===");
        int count = 0;
        foreach (EvtxChunk chunk in parser.Chunks)
        {
            for (int i = 0; i < chunk.ParsedXml.Count && count < 5; i++, count++)
            {
                testOutputHelper.WriteLine($"\n--- Record {chunk.Records[i].EventRecordId} ---");
                testOutputHelper.WriteLine(chunk.ParsedXml[i]);
            }

            if (count >= 5) break;
        }
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
        string[] evtxFiles = Directory.GetFiles(TestDataDir, "*.evtx");
        int totalRecords = 0;
        int withEventData = 0;
        int withNonZeroEventId = 0;

        foreach (string file in evtxFiles)
        {
            byte[] data = File.ReadAllBytes(file);
            EvtxParser parser = EvtxParser.Parse(data, 1);

            foreach (EvtxChunk chunk in parser.Chunks)
            {
                foreach (string xml in chunk.ParsedXml)
                {
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
    public void XmlOutput_SurvivesUtf8Encoding_WithUnpairedSurrogates()
    {
        // EVTX files may contain corrupt UTF-16 data with unpaired surrogates (e.g. lone \uDE1E).
        // Previously this caused EncoderFallbackException when writing XML to stdout via StreamWriter.
        // The parser's AppendXmlEscaped now replaces unpaired surrogates with U+FFFD so all output
        // is valid Unicode that encodes to UTF-8 without error.
        string[] evtxFiles = Directory.GetFiles(TestDataDir, "*.evtx");

        foreach (string file in evtxFiles)
        {
            byte[] data = File.ReadAllBytes(file);
            EvtxParser parser = EvtxParser.Parse(data, 1);

            int recordCount = 0;
            foreach (EvtxChunk chunk in parser.Chunks)
            {
                for (int i = 0; i < chunk.ParsedXml.Count; i++)
                {
                    string xml = chunk.ParsedXml[i];
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
}