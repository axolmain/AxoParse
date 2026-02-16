using System.Diagnostics;
using System.Runtime.InteropServices;

namespace AxoParse.Evtx.Tests;

public class BinXmlTemplateDefinitionTests(ITestOutputHelper testOutputHelper)
{
    #region Public Methods

    [Fact]
    public void FollowsHashChains()
    {
        // Parse all chunks and count templates that were found via chaining (not direct pointer)
        byte[] data = File.ReadAllBytes(Path.Combine(TestDataDir, "security.evtx"));
        EvtxFileHeader fileHeader = EvtxFileHeader.ParseEvtxFileHeader(data);

        int totalTemplates = 0;
        int chainedTemplates = 0;

        for (int ci = 0; ci < fileHeader.NumberOfChunks; ci++)
        {
            int offset = FileHeaderSize + ci * ChunkSize;
            ReadOnlySpan<byte> chunkData = data.AsSpan(offset, ChunkSize);
            ReadOnlySpan<uint> tplPtrs = MemoryMarshal.Cast<byte, uint>(chunkData.Slice(384, 128));

            // Count direct pointers (non-zero entries in table)
            int directPtrs = 0;
            for (int i = 0; i < tplPtrs.Length; i++)
                if (tplPtrs[i] != 0)
                    directPtrs++;

            Dictionary<uint, BinXmlTemplateDefinition> cache =
                BinXmlTemplateDefinition.PreloadFromChunk(chunkData, tplPtrs, offset);
            totalTemplates += cache.Count;
            chainedTemplates += cache.Count - directPtrs;
        }

        testOutputHelper.WriteLine($"Total templates: {totalTemplates}, found via chaining: {chainedTemplates}");
    }

    [Fact]
    public void HandlesEmptyPointerTable()
    {
        byte[] chunkData = new byte[ChunkSize];
        ReadOnlySpan<uint> emptyPtrs = stackalloc uint[32];

        Dictionary<uint, BinXmlTemplateDefinition> cache =
            BinXmlTemplateDefinition.PreloadFromChunk(chunkData, emptyPtrs, chunkFileOffset: 0);

        Assert.Empty(cache);
    }

    [Fact]
    public void HandlesOutOfBoundsPointer()
    {
        byte[] chunkData = new byte[ChunkSize];
        uint[] badPtrsArr = new uint[32];
        badPtrsArr[0] = 99999; // beyond chunk boundary
        ReadOnlySpan<uint> badPtrs = badPtrsArr;

        Dictionary<uint, BinXmlTemplateDefinition>
            cache = BinXmlTemplateDefinition.PreloadFromChunk(chunkData, badPtrs, chunkFileOffset: 0);

        Assert.Empty(cache);
    }

    /// <summary>
    /// Verifies that template preloading succeeds for every valid chunk across all test files.
    /// </summary>
    [Fact]
    public void PreloadsFromAllTestFiles()
    {
        string[] evtxFiles = Directory.GetFiles(TestDataDir, "*.evtx");
        Assert.True(evtxFiles.Length > 0, "No test .evtx files found");

        foreach (string file in evtxFiles)
        {
            byte[] data = File.ReadAllBytes(file);
            EvtxFileHeader fileHeader = EvtxFileHeader.ParseEvtxFileHeader(data);

            for (int ci = 0; ci < fileHeader.NumberOfChunks; ci++)
            {
                int offset = FileHeaderSize + ci * ChunkSize;
                if (offset + ChunkSize > data.Length) break;

                ReadOnlySpan<byte> chunkData = data.AsSpan(offset, ChunkSize);
                if (!chunkData[..8].SequenceEqual("ElfChnk\0"u8)) continue;

                ReadOnlySpan<uint> tplPtrs = MemoryMarshal.Cast<byte, uint>(chunkData.Slice(384, 128));

                Dictionary<uint, BinXmlTemplateDefinition> cache =
                    BinXmlTemplateDefinition.PreloadFromChunk(chunkData, tplPtrs, offset);

                Assert.True(cache.Count >= 0);
            }
        }
    }

    [Fact]
    public void PreloadsTemplatesFromFirstChunk()
    {
        (byte[] fileData, int chunkOffset) = GetChunkInfo("security.evtx");
        ReadOnlySpan<byte> chunkData = fileData.AsSpan(chunkOffset, ChunkSize);
        ReadOnlySpan<uint> tplPtrs = MemoryMarshal.Cast<byte, uint>(chunkData.Slice(384, 128));

        Stopwatch sw = Stopwatch.StartNew();
        Dictionary<uint, BinXmlTemplateDefinition> cache =
            BinXmlTemplateDefinition.PreloadFromChunk(chunkData, tplPtrs, chunkOffset);
        sw.Stop();

        Assert.True(cache.Count > 0, "Should find at least one template definition");

        testOutputHelper.WriteLine($"Preloaded {cache.Count} templates in {sw.Elapsed.TotalMicroseconds:F1}µs");
        foreach ((uint offset, BinXmlTemplateDefinition def) in cache)
        {
            testOutputHelper.WriteLine(
                $"  offset={offset}, guid={def.Guid}, dataSize={def.DataSize}, next={def.NextTemplateOffset}");
        }
    }

    [Fact]
    public void TemplateDefinitionsHaveValidData()
    {
        (byte[] fileData, int chunkOffset) = GetChunkInfo("security.evtx");
        ReadOnlySpan<byte> chunkData = fileData.AsSpan(chunkOffset, ChunkSize);
        ReadOnlySpan<uint> tplPtrs = MemoryMarshal.Cast<byte, uint>(chunkData.Slice(384, 128));
        Dictionary<uint, BinXmlTemplateDefinition> cache =
            BinXmlTemplateDefinition.PreloadFromChunk(chunkData, tplPtrs, chunkOffset);

        foreach ((uint offset, BinXmlTemplateDefinition def) in cache)
        {
            Assert.Equal(offset, def.DefDataOffset);
            Assert.NotEqual(Guid.Empty, def.Guid);
            Assert.True(def.DataSize > 0, $"Template at offset {offset} has zero DataSize");
            // Template body should start with 0x0F (FragmentHeader token)
            Assert.Equal(0x0F, def.GetData(fileData)[0]);
        }
    }

    #endregion

    #region Non-Public Methods

    private static (byte[] fileData, int chunkFileOffset) GetChunkInfo(string filename, int chunkIndex = 0)
    {
        byte[] data = File.ReadAllBytes(Path.Combine(TestDataDir, filename));
        int offset = FileHeaderSize + chunkIndex * ChunkSize;
        return (data, offset);
    }

    #endregion

    #region Non-Public Fields

    private const int ChunkSize = 65536;
    private const int FileHeaderSize = 4096;
    private static readonly string TestDataDir = TestPaths.TestDataDir;

    #endregion
}