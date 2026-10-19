using System.Security.Cryptography;
using System.Text;

namespace AxoParse.Evtx.Tests;

/// <summary>
/// Locks the exact rendered output of every sample file. The expected SHA-256 of each (sample, format) pair's
/// record output and <c>WriteTo</c> output lives in <c>Golden/output-sha256.txt</c>; any byte change fails the test.
/// <para>
/// After an intentional output change, re-bless with <c>AXOPARSE_REBLESS=1 dotnet test ...</c>, which rewrites the
/// hash file from the current output; review and commit the diff. On a mismatch, the actual output is written to a
/// temp directory (path in the failure message) so it can be diffed against output from a known-good build.
/// </para>
/// </summary>
public class GoldenOutputTests
{
    #region Public Methods

    /// <summary>
    /// Verifies the output of every sample file, in both formats, single- and multi-threaded, against the committed
    /// golden hashes, and that every sample has golden entries. Two hashes per (format, sample):
    /// <list type="bullet">
    /// <item><c>&lt;format&gt;/&lt;sample&gt;</c>: every event's <see cref="EvtxEvent.Utf8"/> from
    /// <see cref="EvtxParser.Parse(byte[], EvtxOptions?, CancellationToken)"/>, concatenated. These are the rendered
    /// record bytes, so any rendering change shows up here.</item>
    /// <item><c>framed/&lt;format&gt;/&lt;sample&gt;</c>: the output of
    /// <see cref="EvtxParser.WriteTo(byte[], Stream, EvtxOptions?, CancellationToken)"/>, which adds the framing
    /// (a line feed after each record).</item>
    /// </list>
    /// </summary>
    [Fact]
    public void OutputMatchesGoldenHashes()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        Dictionary<string, string> golden = ReadGolden();
        SortedDictionary<string, string> actual = new(StringComparer.Ordinal);
        List<string> failures = new();
        string? dumpDir = null;

        foreach (string file in Directory.GetFiles(TestPaths.TestDataDir, "*.evtx"))
        {
            byte[] data = File.ReadAllBytes(file);
            string name = Path.GetFileName(file);
            foreach (OutputFormat format in _formats)
            {
                Check(Key(format, name), threads =>
                {
                    using MemoryStream records = new();
                    foreach (EvtxEvent evt in EvtxParser.Parse(data, new EvtxOptions { Format = format, MaxThreads = threads }, token).Events)
                        records.Write(evt.Utf8.Span);
                    return records.ToArray();
                });
                Check("framed/" + Key(format, name), threads =>
                {
                    using MemoryStream framed = new();
                    EvtxParser.WriteTo(data, framed, new EvtxOptions { Format = format, MaxThreads = threads }, token);
                    return framed.ToArray();
                });

                void Check(string key, Func<int, byte[]> render)
                {
                    string? reference = null;
                    foreach (int threads in _threadCounts)
                    {
                        byte[] output = render(threads);
                        string hash = Convert.ToHexStringLower(SHA256.HashData(output));

                        // Output must not depend on thread count; the single-threaded hash is what gets blessed
                        reference ??= hash;
                        bool threadMismatch = hash != reference;
                        bool goldenMismatch = !_rebless && (!golden.TryGetValue(key, out string? expected) || (expected != hash));
                        if (threadMismatch || goldenMismatch)
                        {
                            dumpDir ??= Directory.CreateTempSubdirectory("axoparse-golden-").FullName;
                            string dumpPath = Path.Combine(dumpDir, $"{key.Replace('/', '.')}.{threads}t.{Extension(format)}");
                            File.WriteAllBytes(dumpPath, output);
                            failures.Add(threadMismatch
                                ? $"{key} ({threads} threads): output differs from 1 thread -> {dumpPath}"
                                : $"{key} ({threads} threads): {(golden.ContainsKey(key) ? "hash changed" : "no golden entry")} -> {dumpPath}");
                        }
                    }

                    actual[key] = reference!;
                }
            }
        }

        if (_rebless)
        {
            WriteGolden(actual);
            Assert.True(failures.Count == 0, $"Re-blessed {_goldenPath}, but output varies with thread count:\n" + string.Join('\n', failures));
            return;
        }

        foreach (string key in golden.Keys)
        {
            if (!actual.ContainsKey(key))
                failures.Add($"{key}: golden entry has no matching sample file (re-bless to remove it)");
        }

        Assert.True(failures.Count == 0,
            $"{failures.Count} golden output mismatch(es). If the change is intended, re-run with {_reblessVariable}=1 " +
            $"and commit {_goldenPath}.\n" + string.Join('\n', failures));
    }

    #endregion

    #region Non-Public Methods

    /// <summary>
    /// File extension used when dumping mismatched output.
    /// </summary>
    /// <param name="format">Rendered format.</param>
    /// <returns><c>xml</c> or <c>json</c>.</returns>
    private static string Extension(OutputFormat format) => format == OutputFormat.Xml ? "xml" : "json";

    /// <summary>
    /// Builds the golden-file key for a sample rendered in a format.
    /// </summary>
    /// <param name="format">Rendered format.</param>
    /// <param name="fileName">Sample file name (no directory).</param>
    /// <returns>Key of the form <c>xml/security.evtx</c> or <c>json-structured/security.evtx</c>.</returns>
    private static string Key(OutputFormat format, string fileName) =>
        $"{format switch { OutputFormat.JsonStructured => "json-structured", OutputFormat.Json => "json", _ => "xml" }}/{fileName}";

    /// <summary>
    /// Reads the golden hash file. Each non-comment line is <c>&lt;sha256&gt; &lt;format&gt;/&lt;file&gt;</c>.
    /// </summary>
    /// <returns>Expected hash by key; empty if the file doesn't exist yet.</returns>
    private static Dictionary<string, string> ReadGolden()
    {
        Dictionary<string, string> golden = new(StringComparer.Ordinal);
        if (!File.Exists(_goldenPath))
            return golden;

        foreach (string line in File.ReadAllLines(_goldenPath))
        {
            if ((line.Length == 0) || line.StartsWith('#'))
                continue;
            int space = line.IndexOf(' ');
            golden[line[(space + 1)..]] = line[..space];
        }

        return golden;
    }

    /// <summary>
    /// Rewrites the golden hash file from the current output, sorted by key so diffs stay minimal.
    /// </summary>
    /// <param name="actual">Single-threaded output hash by key.</param>
    private static void WriteGolden(SortedDictionary<string, string> actual)
    {
        StringBuilder sb = new();
        sb.Append("# SHA-256 per <format>/<sample> of the events' concatenated output (EvtxEvent.Utf8), and per\n");
        sb.Append("# framed/<format>/<sample> of EvtxParser.WriteTo output. Generated by GoldenOutputTests;\n");
        sb.Append($"# regenerate with {_reblessVariable}=1 after an intentional output change.\n");
        foreach (KeyValuePair<string, string> kv in actual)
            sb.Append(kv.Value).Append(' ').Append(kv.Key).Append('\n');

        Directory.CreateDirectory(Path.GetDirectoryName(_goldenPath)!);
        File.WriteAllText(_goldenPath, sb.ToString());
    }

    #endregion

    #region Non-Public Fields

    /// <summary>
    /// Environment variable that switches the test from comparing to re-blessing.
    /// </summary>
    private const string _reblessVariable = "AXOPARSE_REBLESS";

    private static readonly OutputFormat[] _formats = [OutputFormat.Xml, OutputFormat.JsonStructured, OutputFormat.Json];

    /// <summary>
    /// Golden hash file in the test project's source tree (read and written in place, not copied to bin).
    /// </summary>
    private static readonly string _goldenPath = Path.Combine(TestPaths.ProjectDir, "Golden", "output-sha256.txt");

    private static readonly bool _rebless = Environment.GetEnvironmentVariable(_reblessVariable) == "1";

    private static readonly int[] _threadCounts = [1, 8];

    #endregion
}
