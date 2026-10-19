
namespace AxoParse.Evtx.Tests;

public class EvtxParserParseAsyncTests
{
    #region Public Methods

    /// <summary>
    /// Verifies that <see cref="EvtxParser.ParseAsync"/> yields exactly the events of
    /// <see cref="EvtxParser.GetEvents"/> after <see cref="EvtxParser.Parse"/>, in the same order, with the same record
    /// IDs, rendered output and diagnostics, for every sample file, both formats, single- and multi-threaded parsing, and
    /// with checksum validation off and on.
    /// </summary>
    /// <param name="format">Output format under test.</param>
    /// <param name="threads">Thread count passed to both parse paths.</param>
    /// <param name="validateChecksums">Checksum validation flag passed to both parse paths.</param>
    [Theory]
    [InlineData(OutputFormat.Xml, 1, false)]
    [InlineData(OutputFormat.Xml, 8, false)]
    [InlineData(OutputFormat.Json, 1, false)]
    [InlineData(OutputFormat.Json, 8, false)]
    [InlineData(OutputFormat.Xml, 1, true)]
    [InlineData(OutputFormat.Xml, 8, true)]
    [InlineData(OutputFormat.Json, 1, true)]
    [InlineData(OutputFormat.Json, 8, true)]
    public async Task ParseAsyncMatchesParseForAllTestFiles(OutputFormat format, int threads, bool validateChecksums)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        foreach (string file in Directory.GetFiles(_testDataDir, "*.evtx"))
        {
            string name = Path.GetFileName(file);
            byte[] data = await File.ReadAllBytesAsync(file, token);
            EvtxParser parser = EvtxParser.Parse(data, threads, format, validateChecksums, cancellationToken: token);
            List<EvtxEvent> expected = new List<EvtxEvent>(parser.GetEvents());

            int index = 0;
            await foreach (EvtxEvent actual in EvtxParser.ParseAsync(data, threads, format, validateChecksums,
                               cancellationToken: token))
            {
                Assert.True(index < expected.Count,
                    $"{name}: ParseAsync yielded more than Parse's {expected.Count} events " +
                    $"({format}, {threads} threads, validateChecksums={validateChecksums})");

                EvtxEvent want = expected[index];
                string where = $"{name} event {index} ({format}, {threads} threads, validateChecksums={validateChecksums})";
                Assert.True(want.Record.EventRecordId == actual.Record.EventRecordId, $"{where}: record ID differs");
                Assert.True(want.Xml == actual.Xml, $"{where}: XML differs");
                Assert.True(want.Json.Span.SequenceEqual(actual.Json.Span), $"{where}: JSON differs");
                Assert.True(want.Diagnostic == actual.Diagnostic, $"{where}: diagnostic differs");
                index++;
            }

            Assert.True(index == expected.Count,
                $"{name}: ParseAsync yielded {index} events, Parse {expected.Count} " +
                $"({format}, {threads} threads, validateChecksums={validateChecksums})");
        }
    }

    #endregion

    #region Non-Public Fields

    private static readonly string _testDataDir = TestPaths.TestDataDir;

    #endregion
}
