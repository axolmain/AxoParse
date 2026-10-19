using System.Text;
using Microsoft.Win32.SafeHandles;

namespace AxoParse.Evtx.Tests;

public class EvtxParserWriteToTests
{
    #region Public Methods

    /// <summary>
    /// Verifies that streaming output is byte-identical to concatenating the UTF-8 output of <see cref="EvtxParser.Parse"/>
    /// (valid chunks in file order, then recovered chunks) and reports the same record count, for every sample file,
    /// both formats, single- and multi-threaded rendering, and with checksum validation off and on.
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
    public void WriteToMatchesParseOutputForAllTestFiles(OutputFormat format, int threads, bool validateChecksums)
    {
        foreach (string file in Directory.GetFiles(_testDataDir, "*.evtx"))
        {
            byte[] data = File.ReadAllBytes(file);
            EvtxParser parser = EvtxParser.Parse(data, threads, format, validateChecksums,
                cancellationToken: TestContext.Current.CancellationToken);

            using MemoryStream stream = new();
            int written = EvtxParser.WriteTo(data, stream, format, threads, validateChecksums,
                TestContext.Current.CancellationToken);

            Assert.Equal(parser.TotalRecords, written);
            Assert.True(ExpectedBytes(parser, format).AsSpan().SequenceEqual(stream.ToArray()),
                $"{Path.GetFileName(file)}: WriteTo output differs from Parse output " +
                $"({format}, {threads} threads, validateChecksums={validateChecksums})");
        }
    }

    /// <summary>
    /// Verifies that streaming from disk (each worker reading its own 64 KB chunk) writes exactly the bytes and record
    /// count of the in-memory overload, for every sample file, both formats, single- and multi-threaded rendering, and
    /// with checksum validation off and on, through both the path and the file-handle overloads.
    /// </summary>
    /// <param name="format">Output format under test.</param>
    /// <param name="threads">Thread count passed to both overloads.</param>
    /// <param name="validateChecksums">Checksum validation flag passed to both overloads.</param>
    [Theory]
    [InlineData(OutputFormat.Xml, 1, false)]
    [InlineData(OutputFormat.Xml, 8, false)]
    [InlineData(OutputFormat.Json, 1, false)]
    [InlineData(OutputFormat.Json, 8, false)]
    [InlineData(OutputFormat.Xml, 1, true)]
    [InlineData(OutputFormat.Xml, 8, true)]
    [InlineData(OutputFormat.Json, 1, true)]
    [InlineData(OutputFormat.Json, 8, true)]
    public void WriteToFromFileMatchesInMemoryForAllTestFiles(OutputFormat format, int threads, bool validateChecksums)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        foreach (string file in Directory.GetFiles(_testDataDir, "*.evtx"))
        {
            string where = $"{Path.GetFileName(file)} ({format}, {threads} threads, validateChecksums={validateChecksums})";
            using MemoryStream expected = new();
            int expectedCount = EvtxParser.WriteTo(File.ReadAllBytes(file), expected, format, threads, validateChecksums, token);

            using MemoryStream fromPath = new();
            int pathCount = EvtxParser.WriteTo(file, fromPath, format, threads, validateChecksums, token);
            Assert.True(expectedCount == pathCount, $"{where}: path overload wrote {pathCount} records, in-memory {expectedCount}");
            Assert.True(expected.ToArray().AsSpan().SequenceEqual(fromPath.ToArray()), $"{where}: path overload output differs");

            using SafeFileHandle handle = File.OpenHandle(file);
            using MemoryStream fromHandle = new();
            int handleCount = EvtxParser.WriteTo(handle, fromHandle, format, threads, validateChecksums, token);
            Assert.True(expectedCount == handleCount, $"{where}: handle overload wrote {handleCount} records, in-memory {expectedCount}");
            Assert.True(expected.ToArray().AsSpan().SequenceEqual(fromHandle.ToArray()), $"{where}: handle overload output differs");
        }
    }

    /// <summary>
    /// Verifies that the path overload throws the documented <see cref="EvtxParseException"/> for a file too short to
    /// hold an EVTX header, as the in-memory overload does.
    /// </summary>
    [Fact]
    public void WriteToFromFileThrowsOnTruncatedHeader()
    {
        string path = Path.Combine(Directory.CreateTempSubdirectory("axoparse-writeto-").FullName, "short.evtx");
        File.WriteAllBytes(path, File.ReadAllBytes(Path.Combine(_testDataDir, "security.evtx")).AsSpan(0, 100).ToArray());

        EvtxParseException ex = Assert.Throws<EvtxParseException>(() => EvtxParser.WriteTo(path, Stream.Null, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(EvtxParseError.FileHeaderTooShort, ex.ErrorCode);
        File.Delete(path);
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
