namespace AxoParse.Evtx.Tests;

public class EvtxParserWriteToTests
{
    #region Public Methods

    /// <summary>
    /// Verifies that streaming output is byte-identical to concatenating the UTF-8 output of the events of
    /// <see cref="EvtxParser.Parse(byte[], EvtxOptions?, CancellationToken)"/>
    /// (valid chunks in file order, then recovered chunks) and reports the same record count, for every sample file,
    /// both formats, single- and multi-threaded rendering, and with checksum validation off and on.
    /// </summary>
    /// <param name="format">Output format under test.</param>
    /// <param name="threads">Thread count passed to both parse paths.</param>
    /// <param name="validateChecksums">Checksum validation flag passed to both parse paths.</param>
    [Theory]
    [InlineData(OutputFormat.Xml, 1, false)]
    [InlineData(OutputFormat.Xml, 8, false)]
    [InlineData(OutputFormat.JsonStructured, 1, false)]
    [InlineData(OutputFormat.JsonStructured, 8, false)]
    [InlineData(OutputFormat.Xml, 1, true)]
    [InlineData(OutputFormat.Xml, 8, true)]
    [InlineData(OutputFormat.JsonStructured, 1, true)]
    [InlineData(OutputFormat.JsonStructured, 8, true)]
    public void WriteToMatchesParseOutputForAllTestFiles(OutputFormat format, int threads, bool validateChecksums)
    {
        EvtxOptions options = new() { Format = format, MaxThreads = threads, ValidateChecksums = validateChecksums };
        foreach (string file in Directory.GetFiles(_testDataDir, "*.evtx"))
        {
            byte[] data = File.ReadAllBytes(file);
            EvtxLog log = EvtxParser.Parse(data, options, TestContext.Current.CancellationToken);

            using MemoryStream stream = new();
            int written = EvtxParser.WriteTo(data, stream, options, TestContext.Current.CancellationToken);

            Assert.Equal(log.RecordCount, written);
            Assert.True(ExpectedBytes(log).AsSpan().SequenceEqual(stream.ToArray()),
                $"{Path.GetFileName(file)}: WriteTo output differs from Parse output " +
                $"({format}, {threads} threads, validateChecksums={validateChecksums})");
        }
    }

    /// <summary>
    /// Verifies that streaming from disk (each worker reading its own 64 KB chunk) writes exactly the bytes and record
    /// count of the in-memory overload, for every sample file, both formats, single- and multi-threaded rendering, and
    /// with checksum validation off and on, through the path overload and the stream overload (a <see cref="FileStream"/>,
    /// read through its handle, and a <see cref="MemoryStream"/>, read under a lock).
    /// </summary>
    /// <param name="format">Output format under test.</param>
    /// <param name="threads">Thread count passed to both overloads.</param>
    /// <param name="validateChecksums">Checksum validation flag passed to both overloads.</param>
    [Theory]
    [InlineData(OutputFormat.Xml, 1, false)]
    [InlineData(OutputFormat.Xml, 8, false)]
    [InlineData(OutputFormat.JsonStructured, 1, false)]
    [InlineData(OutputFormat.JsonStructured, 8, false)]
    [InlineData(OutputFormat.Xml, 1, true)]
    [InlineData(OutputFormat.Xml, 8, true)]
    [InlineData(OutputFormat.JsonStructured, 1, true)]
    [InlineData(OutputFormat.JsonStructured, 8, true)]
    public void WriteToFromFileMatchesInMemoryForAllTestFiles(OutputFormat format, int threads, bool validateChecksums)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        EvtxOptions options = new() { Format = format, MaxThreads = threads, ValidateChecksums = validateChecksums };
        foreach (string file in Directory.GetFiles(_testDataDir, "*.evtx"))
        {
            string where = $"{Path.GetFileName(file)} ({format}, {threads} threads, validateChecksums={validateChecksums})";
            byte[] data = File.ReadAllBytes(file);
            using MemoryStream expected = new();
            int expectedCount = EvtxParser.WriteTo(data, expected, options, token);

            using MemoryStream fromPath = new();
            int pathCount = EvtxParser.WriteTo(file, fromPath, options, token);
            Assert.True(expectedCount == pathCount, $"{where}: path overload wrote {pathCount} records, in-memory {expectedCount}");
            Assert.True(expected.ToArray().AsSpan().SequenceEqual(fromPath.ToArray()), $"{where}: path overload output differs");

            using FileStream fileStream = File.OpenRead(file);
            using MemoryStream fromFileStream = new();
            int fileStreamCount = EvtxParser.WriteTo(fileStream, fromFileStream, options, token);
            Assert.True(expectedCount == fileStreamCount, $"{where}: FileStream overload wrote {fileStreamCount} records, in-memory {expectedCount}");
            Assert.True(expected.ToArray().AsSpan().SequenceEqual(fromFileStream.ToArray()), $"{where}: FileStream overload output differs");

            using MemoryStream memoryInput = new(data, writable: false);
            using MemoryStream fromMemoryStream = new();
            int memoryStreamCount = EvtxParser.WriteTo(memoryInput, fromMemoryStream, options, token);
            Assert.True(expectedCount == memoryStreamCount, $"{where}: MemoryStream overload wrote {memoryStreamCount} records, in-memory {expectedCount}");
            Assert.True(expected.ToArray().AsSpan().SequenceEqual(fromMemoryStream.ToArray()), $"{where}: MemoryStream overload output differs");
        }
    }

    /// <summary>
    /// Verifies that the stream overload rejects an input stream that can't seek.
    /// </summary>
    [Fact]
    public void WriteToRejectsNonSeekableInput()
    {
        using NonSeekableStream input = new(File.ReadAllBytes(Path.Combine(_testDataDir, "security.evtx")));
        Assert.Throws<ArgumentException>(() => EvtxParser.WriteTo(input, Stream.Null, cancellationToken: TestContext.Current.CancellationToken));
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
    /// Verifies that checksum validation skips the same chunks in streaming mode as in
    /// <see cref="EvtxParser.Parse(byte[], EvtxOptions?, CancellationToken)"/>.
    /// </summary>
    [Fact]
    public void WriteToHonoursChecksumValidation()
    {
        byte[] data = File.ReadAllBytes(Path.Combine(_testDataDir, "2-system-Security-dirty.evtx"));
        EvtxOptions options = new() { MaxThreads = 1, ValidateChecksums = true };
        EvtxLog log = EvtxParser.Parse(data, options, TestContext.Current.CancellationToken);

        using MemoryStream stream = new();
        int written = EvtxParser.WriteTo(data, stream, options, TestContext.Current.CancellationToken);

        Assert.Equal(log.RecordCount, written);
        Assert.True(ExpectedBytes(log).AsSpan().SequenceEqual(stream.ToArray()));
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
            EvtxParser.WriteTo(data, Stream.Null, new EvtxOptions { MaxThreads = 1 }, cts.Token));
    }

    #endregion

    #region Non-Public Methods

    /// <summary>
    /// Concatenates the output of every event of <paramref name="log"/>, in order.
    /// </summary>
    /// <param name="log">Result of a full parse.</param>
    /// <returns>The bytes streaming output is expected to produce.</returns>
    private static byte[] ExpectedBytes(EvtxLog log)
    {
        using MemoryStream expected = new();
        foreach (EvtxEvent evt in log.Events)
            expected.Write(evt.Utf8.Span);
        return expected.ToArray();
    }

    #endregion

    #region Non-Public Fields

    private static readonly string _testDataDir = TestPaths.TestDataDir;

    #endregion

    #region Nested Types

    /// <summary>
    /// A readable stream that can't seek.
    /// </summary>
    /// <param name="data">The stream's content.</param>
    private sealed class NonSeekableStream(byte[] data) : MemoryStream(data, writable: false)
    {
        /// <summary>
        /// Always false.
        /// </summary>
        public override bool CanSeek => false;
    }

    #endregion
}
