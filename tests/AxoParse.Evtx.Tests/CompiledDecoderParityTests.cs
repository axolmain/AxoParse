using System.Text;
using AxoParse.Evtx.BinXml;

namespace AxoParse.Evtx.Tests;

/// <summary>
/// The compiled-template path must render exactly what the decoder renders for the same template: compiled templates
/// are only a cache of the decoder's static output. Every record of every sample, and of seeded mutations of each, is
/// rendered once with a compiled-template cache and once without (which sends every template body through the
/// decoder), in XML and JSON, and the results must match byte for byte (or both fail). The cache is cleared before
/// each record so the compiled side compiles that record's own template body: the cache is keyed by GUID, so in a
/// corrupt chunk a later instance with a reused GUID would otherwise render an earlier template than the decoder sees.
/// </summary>
public class CompiledDecoderParityTests(ITestOutputHelper output)
{
    #region Public Methods

    /// <summary>
    /// Verifies compiled and decoded rendering agree for every record of every sample and of
    /// <see cref="_mutationsPerSample"/> seeded mutations of each (<c>AXOPARSE_FUZZ_ROUNDS</c> overrides the count, as
    /// for <see cref="CorruptionFuzzTests"/>).
    /// </summary>
    [Fact]
    public void CompiledTemplatesRenderLikeTheDecoder()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        List<string> differences = new();
        int renders = 0;
        int rounds = int.TryParse(Environment.GetEnvironmentVariable("AXOPARSE_FUZZ_ROUNDS"), out int r) && (r > 0)
            ? r
            : _mutationsPerSample;
        foreach (string file in Directory.GetFiles(TestPaths.TestDataDir, "*.evtx").Order(StringComparer.Ordinal))
        {
            string name = Path.GetFileName(file);
            byte[] original = File.ReadAllBytes(file);
            renders += Compare(name, original, differences);
            byte[] prefix = original.Length > _mutationBaseBytes ? original.AsSpan(0, _mutationBaseBytes).ToArray() : original;
            for (int round = 0; round < rounds; round++)
            {
                token.ThrowIfCancellationRequested();
                int seed = CorruptionFuzzTests.StableSeed(name, round);
                (byte[] data, string mutation) = CorruptionFuzzTests.Mutate(prefix, new Random(seed));
                renders += Compare($"{name} seed {seed} [{mutation}]", data, differences);
            }
        }

        output.WriteLine($"{renders} renders compared, {differences.Count} differences");
        Assert.True(differences.Count == 0,
            $"{differences.Count} record(s) render differently compiled and decoded:\n" + string.Join('\n', differences.Take(20)));
    }

    #endregion

    #region Non-Public Methods

    /// <summary>
    /// Renders every record of every chunk with valid magic in <paramref name="data"/> both ways, in both formats.
    /// </summary>
    /// <param name="label">Input description for failure messages.</param>
    /// <param name="data">File bytes.</param>
    /// <param name="differences">Receives a description of each mismatch.</param>
    /// <returns>Number of records compared (per format).</returns>
    private static int Compare(string label, byte[] data, List<string> differences)
    {
        int count = 0;
        for (int offset = _firstChunkOffset; offset + EvtxChunk.ChunkSize <= data.Length; offset += EvtxChunk.ChunkSize)
        {
            if (!data.AsSpan(offset, 8).SequenceEqual("ElfChnk\0"u8))
                continue;

            EvtxChunk chunk;
            try
            {
                chunk = EvtxChunk.Parse(data, offset, new Dictionary<Guid, CompiledTemplate?>(), OutputFormat.Xml);
            }
            catch (EvtxParseException)
            {
                continue;
            }

            foreach (OutputFormat format in new[] { OutputFormat.Xml, OutputFormat.Json })
            {
                Dictionary<Guid, CompiledTemplate?> cache = new();
                BinXmlParser compiled = new(data, offset, cache);
                BinXmlParser decoded = new(data, offset, compiledCache: null);
                foreach (EvtxRecord record in chunk.Records)
                {
                    count++;
                    cache.Clear();
                    string a = Render(compiled, record, format);
                    string b = Render(decoded, record, format);
                    if (a != b)
                        differences.Add($"{label} chunk 0x{offset:X} record {record.EventRecordId} {format}:\n" +
                                        $"  compiled: {Excerpt(a, b)}\n  decoded:  {Excerpt(b, a)}");
                }
            }
        }

        return count;
    }

    /// <summary>
    /// Renders one record, returning its output or a description of the exception it threw.
    /// </summary>
    /// <param name="parser">Parser for the record's chunk.</param>
    /// <param name="record">Record to render.</param>
    /// <param name="format">Output format.</param>
    /// <returns>The output, or <c>!ExceptionType: message</c>.</returns>
    private static string Render(BinXmlParser parser, EvtxRecord record, OutputFormat format)
    {
        ValueUtf8Builder vub = new(new byte[4096]);
        try
        {
            parser.RenderRecord(record, format, ref vub);
            return Encoding.UTF8.GetString(vub.AsSpan());
        }
        catch (Exception ex)
        {
            return $"!{ex.GetType().Name}: {ex.Message}";
        }
        finally
        {
            vub.Dispose();
        }
    }

    /// <summary>
    /// Shows <paramref name="text"/> around its first difference from <paramref name="other"/>.
    /// </summary>
    /// <param name="text">Text to show.</param>
    /// <param name="other">Text it is compared with.</param>
    /// <returns>An excerpt with the difference position.</returns>
    private static string Excerpt(string text, string other)
    {
        int i = 0;
        while ((i < text.Length) && (i < other.Length) && (text[i] == other[i]))
            i++;
        int start = Math.Max(0, i - 60);
        return $"@{i}/{text.Length} …{text[start..Math.Min(text.Length, i + 100)]}…";
    }

    #endregion

    #region Non-Public Fields

    /// <summary>
    /// File offset of the first chunk (after the 4 KB file header).
    /// </summary>
    private const int _firstChunkOffset = 4096;

    /// <summary>
    /// Seeded mutations rendered per sample.
    /// </summary>
    private const int _mutationsPerSample = 10;

    /// <summary>
    /// Mutations are applied to at most this prefix of each sample (16 chunks), to keep the test quick.
    /// </summary>
    private const int _mutationBaseBytes = 1 << 20;

    #endregion
}
