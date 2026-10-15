using System.Text;
using AxoParse.Evtx.Evtx;

namespace AxoParse.Evtx.Tests;

public class EvtxParserWriteToTests
{
    #region Public Methods

    /// <summary>
    /// Verifies that streaming output is byte-identical to concatenating the UTF-8 output of <see cref="EvtxParser.Parse"/>
    /// (valid chunks in file order, then recovered chunks) and reports the same record count, for every sample file,
    /// both formats, and single- and multi-threaded rendering.
    /// </summary>
    /// <param name="format">Output format under test.</param>
    /// <param name="threads">Thread count passed to both parse paths.</param>
    [Theory]
    [InlineData(OutputFormat.Xml, 1)]
    [InlineData(OutputFormat.Xml, 8)]
    [InlineData(OutputFormat.Json, 1)]
    [InlineData(OutputFormat.Json, 8)]
    public void WriteToMatchesParseOutputForAllTestFiles(OutputFormat format, int threads)
    {
        foreach (string file in Directory.GetFiles(_testDataDir, "*.evtx"))
        {
            byte[] data = File.ReadAllBytes(file);
            EvtxParser parser = EvtxParser.Parse(data, threads, format, cancellationToken: TestContext.Current.CancellationToken);

            using MemoryStream stream = new();
            int written = EvtxParser.WriteTo(data, stream, format, threads, cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(parser.TotalRecords, written);
            Assert.True(ExpectedBytes(parser, format).AsSpan().SequenceEqual(stream.ToArray()),
                $"{Path.GetFileName(file)}: WriteTo output differs from Parse output ({format}, {threads} threads)");
        }
    }

    /// <summary>
    /// Verifies that checksum validation skips the same chunks in streaming mode as in <see cref="EvtxParser.Parse"/>.
    /// </summary>
    [Fact]
    public void WriteToHonoursChecksumValidation()
    {
        byte[] data = File.ReadAllBytes(Path.Combine(_testDataDir, "2-system-Security-dirty.evtx"));
        EvtxParser parser = EvtxParser.Parse(data, 1, OutputFormat.Xml, validateChecksums: true,
            cancellationToken: TestContext.Current.CancellationToken);

        using MemoryStream stream = new();
        int written = EvtxParser.WriteTo(data, stream, OutputFormat.Xml, 1, validateChecksums: true,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(parser.TotalRecords, written);
        Assert.True(ExpectedBytes(parser, OutputFormat.Xml).AsSpan().SequenceEqual(stream.ToArray()));
    }

    /// <summary>
    /// Verifies that a pre-cancelled token stops streaming with <see cref="OperationCanceledException"/>.
    /// </summary>
    [Fact]
    public void WriteToRespectsCancellation()
    {
        byte[] data = File.ReadAllBytes(Path.Combine(_testDataDir, "security.evtx"));
        using CancellationTokenSource cts = new();
        cts.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() =>
            EvtxParser.WriteTo(data, Stream.Null, OutputFormat.Xml, 1, cancellationToken: cts.Token));
    }

    #endregion

    #region Non-Public Methods

    /// <summary>
    /// Concatenates the parsed output of every event in <see cref="EvtxParser.GetEvents"/> order as UTF-8.
    /// </summary>
    /// <param name="parser">Result of a full parse.</param>
    /// <param name="format">Format the parse rendered.</param>
    /// <returns>The bytes streaming output is expected to produce.</returns>
    private static byte[] ExpectedBytes(EvtxParser parser, OutputFormat format)
    {
        using MemoryStream expected = new();
        foreach (EvtxEvent evt in parser.GetEvents())
        {
            if (format == OutputFormat.Json)
                expected.Write(evt.Json.Span);
            else
                expected.Write(Encoding.UTF8.GetBytes(evt.Xml));
        }

        return expected.ToArray();
    }

    #endregion

    #region Non-Public Fields

    private static readonly string _testDataDir = TestPaths.TestDataDir;

    #endregion
}
