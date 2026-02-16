using System.Diagnostics;

namespace AxoParse.Evtx.Tests;

public class EvtxParserTests(ITestOutputHelper testOutputHelper)
{
    private static readonly string TestDataDir = TestPaths.TestDataDir;

    [Fact]
    public void ParsesSecurityEvtxFull()
    {
        byte[] data = File.ReadAllBytes(Path.Combine(TestDataDir, "security.evtx"));

        Stopwatch sw = Stopwatch.StartNew();
        EvtxParser parser = EvtxParser.Parse(data);
        sw.Stop();

        Assert.True(parser.Chunks.Count > 0);
        Assert.True(parser.TotalRecords > 0);
        Assert.Equal(3, parser.FileHeader.MajorFormatVersion);

        testOutputHelper.WriteLine($"[security.evtx] Full parse in {sw.Elapsed.TotalMilliseconds:F2}ms");
        testOutputHelper.WriteLine($"  Chunks: {parser.Chunks.Count}, Records: {parser.TotalRecords}");
    }

    [Fact]
    public void ParsesAllTestFiles()
    {
        string[] evtxFiles = Directory.GetFiles(TestDataDir, "*.evtx");
        Stopwatch sw = new Stopwatch();

        testOutputHelper.WriteLine($"Full parse of {evtxFiles.Length} files:");

        foreach (string file in evtxFiles)
        {
            byte[] data = File.ReadAllBytes(file);
            string name = Path.GetFileName(file);

            sw.Restart();
            EvtxParser parser = EvtxParser.Parse(data);
            sw.Stop();

            testOutputHelper.WriteLine(
                $"  [{name}] {sw.Elapsed.TotalMilliseconds,8:F2}ms | {parser.Chunks.Count} chunks | {parser.TotalRecords} records");
        }
    }

    [Fact]
    public void TotalRecordsMatchesChunkSum()
    {
        byte[] data = File.ReadAllBytes(Path.Combine(TestDataDir, "security.evtx"));
        EvtxParser parser = EvtxParser.Parse(data);

        int sum = 0;
        foreach (EvtxChunk chunk in parser.Chunks)
            sum += chunk.Records.Count;

        Assert.Equal(sum, parser.TotalRecords);
    }

    [Fact]
    public void HandlesBadChunkMagicGracefully()
    {
        byte[] data = File.ReadAllBytes(Path.Combine(TestDataDir, "sample_with_a_bad_chunk_magic.evtx"));

        EvtxParser parser = EvtxParser.Parse(data);

        // Should skip bad chunks without throwing
        testOutputHelper.WriteLine(
            $"[sample_with_a_bad_chunk_magic.evtx] Parsed {parser.Chunks.Count} valid chunks, {parser.TotalRecords} records");
    }

}