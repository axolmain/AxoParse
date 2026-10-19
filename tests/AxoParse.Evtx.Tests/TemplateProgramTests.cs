using System.Runtime.InteropServices;
using AxoParse.Evtx.BinXml;
using AxoParse.Evtx.Evtx;

namespace AxoParse.Evtx.Tests;

/// <summary>
/// Phase-1 gate for the format-neutral renderer (docs/plans/FormatNeutralRenderer.md): every template, compiled once
/// into a <see cref="TemplateProgram"/> and lowered, must reproduce exactly what today's per-format compilers produce,
/// for XML and JSON, including when compilation bails (both null) or throws (both throw).
/// </summary>
public class TemplateProgramTests(ITestOutputHelper output)
{
    #region Public Methods

    /// <summary>
    /// Verifies that lowering reproduces the existing XML and JSON compilers for every template definition in every
    /// chunk's template table across all sample files.
    /// </summary>
    [Fact]
    public void LoweredProgramMatchesExistingCompilersForAllSampleTemplates()
    {
        List<string> failures = new();
        int templates = 0;
        foreach (string file in Directory.GetFiles(TestPaths.TestDataDir, "*.evtx").Order(StringComparer.Ordinal))
        {
            byte[] data = File.ReadAllBytes(file);
            foreach ((int chunkOffset, BinXmlTemplateDefinition definition) in TemplateDefinitions(data))
            {
                templates++;
                string? difference = Compare(data, chunkOffset, definition);
                if (difference != null)
                    failures.Add($"{Path.GetFileName(file)} chunk 0x{chunkOffset:X} template 0x{definition.DefDataOffset:X}: {difference}");
            }
        }

        output.WriteLine($"Compared {templates} templates");
        Assert.True(templates > 100, $"Only {templates} templates found; the corpus should have far more");
        Assert.True(failures.Count == 0, $"{failures.Count} template(s) differ:\n" + string.Join('\n', failures.Take(20)));
    }

    /// <summary>
    /// Verifies equivalence on corrupted template bodies: each sample template is mutated with seeded random byte
    /// overwrites, exercising the bail, unexpected-close-token, depth-limit and exception paths, which must behave
    /// identically in the program compiler and the existing compilers.
    /// </summary>
    [Fact]
    public void LoweredProgramMatchesExistingCompilersForMutatedTemplates()
    {
        List<string> failures = new();
        int compared = 0;
        foreach (string file in Directory.GetFiles(TestPaths.TestDataDir, "*.evtx").Order(StringComparer.Ordinal))
        {
            byte[] original = File.ReadAllBytes(file);
            // One template per chunk keeps this fast while still covering every sample's template shapes
            foreach (IGrouping<int, (int ChunkOffset, BinXmlTemplateDefinition Definition)> chunk in
                     TemplateDefinitions(original).GroupBy(t => t.ChunkOffset).Take(16))
            {
                (int chunkOffset, BinXmlTemplateDefinition definition) = chunk.First();
                for (int round = 0; round < _mutationRounds; round++)
                {
                    Random rng = new((chunkOffset * 31) + round + Path.GetFileName(file).Length);
                    byte[] data = (byte[])original.Clone();
                    int bodyStart = chunkOffset + (int)definition.DefDataOffset + 24;
                    int bodyLength = (int)Math.Min(definition.DataSize, (uint)(data.Length - bodyStart));
                    if (bodyLength <= 4)
                        continue;
                    for (int m = rng.Next(1, 4); m > 0; m--)
                        data[bodyStart + 4 + rng.Next(bodyLength - 4)] = (byte)rng.Next(256);

                    compared++;
                    string? difference = Compare(data, chunkOffset, definition);
                    if (difference != null)
                        failures.Add($"{Path.GetFileName(file)} chunk 0x{chunkOffset:X} template 0x{definition.DefDataOffset:X} round {round}: {difference}");
                }
            }
        }

        output.WriteLine($"Compared {compared} mutated templates");
        Assert.True(failures.Count == 0, $"{failures.Count} mutated template(s) differ:\n" + string.Join('\n', failures.Take(20)));
    }

    #endregion

    #region Non-Public Methods

    /// <summary>
    /// Compiles one template with the existing XML and JSON compilers and with the program compiler (lowered both
    /// ways), each on a fresh parser so name caches can't hide differences.
    /// </summary>
    /// <param name="data">File bytes.</param>
    /// <param name="chunkOffset">File offset of the chunk holding the template.</param>
    /// <param name="definition">Template definition from the chunk's template table.</param>
    /// <returns>Null if equivalent; otherwise a description of the difference.</returns>
    private static string? Compare(byte[] data, int chunkOffset, BinXmlTemplateDefinition definition)
    {
        int offset = (int)definition.DefDataOffset;
        int size = (int)definition.DataSize;

        (CompiledTemplate? xml, string? xmlError) = Try(() => NewParser(data, chunkOffset).CompileTemplate(offset, size));
        (CompiledTemplate? loweredXml, string? programXmlError) =
            Try(() => NewParser(data, chunkOffset).CompileProgram(offset, size)?.LowerToXml());
        if ((xmlError != null) || (programXmlError != null))
        {
            if ((xmlError == null) != (programXmlError == null))
                return $"XML: existing compiler {xmlError ?? "succeeded"}, program {programXmlError ?? "succeeded"}";
        }
        else if (TemplateProgram.DescribeDifference(xml, loweredXml) is { } xmlDifference)
        {
            return "XML " + xmlDifference;
        }

        (CompiledJsonTemplate? json, string? jsonError) = Try(() => NewParser(data, chunkOffset).CompileJsonTemplate(offset, size));
        (CompiledJsonTemplate? loweredJson, string? programJsonError) =
            Try(() => NewParser(data, chunkOffset).CompileProgram(offset, size)?.LowerToJson());
        if ((jsonError != null) || (programJsonError != null))
            return (jsonError == null) == (programJsonError == null) ? null : $"JSON: existing compiler {jsonError ?? "succeeded"}, program {programJsonError ?? "succeeded"}";
        return TemplateProgram.DescribeDifference(json, loweredJson) is { } jsonDifference ? "JSON " + jsonDifference : null;
    }

    /// <summary>
    /// Creates a parser scoped to one chunk, with its template table preloaded.
    /// </summary>
    /// <param name="data">File bytes.</param>
    /// <param name="chunkOffset">File offset of the chunk.</param>
    /// <returns>The parser.</returns>
    private static BinXmlParser NewParser(byte[] data, int chunkOffset)
    {
        ReadOnlySpan<byte> chunk = data.AsSpan(chunkOffset, EvtxChunk.ChunkSize);
        Dictionary<uint, BinXmlTemplateDefinition> templates = BinXmlTemplateDefinition.PreloadFromChunk(
            chunk, MemoryMarshal.Cast<byte, uint>(chunk.Slice(384, 128)), chunkOffset);
        return new BinXmlParser(data, chunkOffset, templates, null);
    }

    /// <summary>
    /// Enumerates every template definition in the template table of every chunk with a valid header.
    /// </summary>
    /// <param name="data">File bytes.</param>
    /// <returns>Chunk offset and definition pairs.</returns>
    private static IEnumerable<(int ChunkOffset, BinXmlTemplateDefinition Definition)> TemplateDefinitions(byte[] data)
    {
        for (int chunkOffset = _fileHeaderSize; chunkOffset + EvtxChunk.ChunkSize <= data.Length; chunkOffset += EvtxChunk.ChunkSize)
        {
            if (!data.AsSpan(chunkOffset, 8).SequenceEqual("ElfChnk\0"u8))
                continue;
            ReadOnlySpan<byte> chunk = data.AsSpan(chunkOffset, EvtxChunk.ChunkSize);
            Dictionary<uint, BinXmlTemplateDefinition> templates = BinXmlTemplateDefinition.PreloadFromChunk(
                chunk, MemoryMarshal.Cast<byte, uint>(chunk.Slice(384, 128)), chunkOffset);
            foreach (BinXmlTemplateDefinition definition in templates.Values.OrderBy(d => d.DefDataOffset))
                yield return (chunkOffset, definition);
        }
    }

    /// <summary>
    /// Runs a compile step, capturing an exception as its type name instead of throwing.
    /// </summary>
    /// <typeparam name="T">Result type.</typeparam>
    /// <param name="step">Compile step.</param>
    /// <returns>The result and null, or default and the exception's type name.</returns>
    private static (T? Result, string? Error) Try<T>(Func<T?> step) where T : class
    {
        try
        {
            return (step(), null);
        }
        catch (Exception ex)
        {
            return (null, $"threw {ex.GetType().Name}");
        }
    }

    #endregion

    #region Non-Public Fields

    private const int _fileHeaderSize = 4096;

    private const int _mutationRounds = 25;

    #endregion
}
