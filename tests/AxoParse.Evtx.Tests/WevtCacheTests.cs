using System.Collections.Concurrent;

namespace AxoParse.Evtx.Tests;

public class WevtCacheTests
{
    private static readonly string TestDataDir = TestPaths.TestDataDir;

    private static readonly string DllsDir = Path.Combine(TestDataDir, "dlls");

    /// <summary>
    /// Verifies that WevtManifest extracts 464 TEMP entries from the CRIM manifest in adtschema.dll.
    /// adtschema.dll contains 1 provider with 464 template definitions (some share GUIDs).
    /// </summary>
    [Fact]
    public void ParseCrimManifest_AdtSchema_Extracts464TempEntries()
    {
        string dllPath = Path.Combine(DllsDir, "adtschema.dll");
        byte[] peData = File.ReadAllBytes(dllPath);
        byte[]? wevtData = PeResourceReader.ExtractWevtTemplate(peData);

        Assert.NotNull(wevtData);

        // CRIM magic validation
        Assert.Equal((byte)'C', wevtData[0]);
        Assert.Equal((byte)'R', wevtData[1]);
        Assert.Equal((byte)'I', wevtData[2]);
        Assert.Equal((byte)'M', wevtData[3]);

        List<WevtTemplate> templates = WevtManifest.ParseCrimManifest(wevtData);
        Assert.Equal(464, templates.Count);
    }

    /// <summary>
    /// Verifies that WevtCache deduplicates templates by GUID.
    /// adtschema.dll has 464 TEMP entries but only 312 unique GUIDs (152 duplicates).
    /// </summary>
    [Fact]
    public void AddFromFile_AdtSchema_Extracts312UniqueTemplates()
    {
        string dllPath = Path.Combine(DllsDir, "adtschema.dll");
        WevtCache cache = new();

        int added = cache.AddFromFile(dllPath);

        Assert.Equal(312, added);
        Assert.Equal(312, cache.Count);
    }

    /// <summary>
    /// Verifies that at least some WEVT BinXML templates compile successfully into CompiledTemplate objects.
    /// Templates may fail compilation if they contain nested TemplateInstance tokens, but
    /// most adtschema.dll templates are simple element trees that should compile.
    /// </summary>
    [Fact]
    public void AddFromFile_AdtSchema_CompilesTemplates()
    {
        string dllPath = Path.Combine(DllsDir, "adtschema.dll");
        WevtCache cache = new();

        cache.AddFromFile(dllPath);

        Assert.True(cache.CompiledCount > 0,
            $"Expected at least some templates to compile, but CompiledCount={cache.CompiledCount}");
    }

    /// <summary>
    /// Verifies that specific known template GUIDs from adtschema.dll are present.
    /// First TEMP: guid=b7a692cd-c953-5a0f-445e-82bb75770d40 (verified against Rust tool output).
    /// </summary>
    [Fact]
    public void ParseCrimManifest_AdtSchema_ContainsKnownTemplateGuids()
    {
        string dllPath = Path.Combine(DllsDir, "adtschema.dll");
        byte[] peData = File.ReadAllBytes(dllPath);
        byte[]? wevtData = PeResourceReader.ExtractWevtTemplate(peData);
        Assert.NotNull(wevtData);

        List<WevtTemplate> templates = WevtManifest.ParseCrimManifest(wevtData);

        // First template GUID from Rust reference output
        Guid firstGuid = new("b7a692cd-c953-5a0f-445e-82bb75770d40");
        Assert.Contains(templates, t => t.Guid == firstGuid);
    }

    /// <summary>
    /// Verifies that PopulateCache correctly injects WEVT templates into a parser's compiled cache.
    /// </summary>
    [Fact]
    public void PopulateCache_InjectsTemplatesIntoConcurrentDictionary()
    {
        string dllPath = Path.Combine(DllsDir, "adtschema.dll");
        WevtCache cache = new();
        cache.AddFromFile(dllPath);

        ConcurrentDictionary<Guid, CompiledTemplate?> targetCache = new();
        cache.PopulateCache(targetCache);

        Assert.Equal(cache.Count, targetCache.Count);
    }

    /// <summary>
    /// Verifies that AddFromPeData returns 0 for non-PE data without throwing.
    /// </summary>
    [Fact]
    public void AddFromPeData_InvalidData_ReturnsZero()
    {
        WevtCache cache = new();

        int added = cache.AddFromPeData([0x00, 0x01, 0x02, 0x03]);

        Assert.Equal(0, added);
        Assert.Equal(0, cache.Count);
    }

    /// <summary>
    /// Verifies that AddFromDirectory scans a directory and loads templates from PE files.
    /// </summary>
    [Fact]
    public void AddFromDirectory_DllsDir_LoadsTemplates()
    {
        WevtCache cache = new();

        int added = cache.AddFromDirectory(DllsDir);

        Assert.True(added > 0, "Expected templates from DLLs directory");
    }

    /// <summary>
    /// Verifies that duplicate GUIDs across multiple AddFromFile calls use first-in-wins semantics.
    /// Loading the same DLL twice should add 0 new templates the second time.
    /// </summary>
    [Fact]
    public void AddFromFile_DuplicateGuids_FirstInWins()
    {
        string dllPath = Path.Combine(DllsDir, "adtschema.dll");
        WevtCache cache = new();

        int first = cache.AddFromFile(dllPath);
        int second = cache.AddFromFile(dllPath);

        Assert.Equal(312, first);
        Assert.Equal(0, second);
        Assert.Equal(312, cache.Count);
    }
}