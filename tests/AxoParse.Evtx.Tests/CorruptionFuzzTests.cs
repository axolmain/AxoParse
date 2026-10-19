using System.Buffers.Binary;
using System.Text.Json;
using AxoParse.Evtx.Evtx;

namespace AxoParse.Evtx.Tests;

/// <summary>
/// Seeded corruption test: mutates every sample file many times (bit flips, random overwrites, zeroed ranges,
/// truncation, and targeted hits on the file header, chunk headers and record headers) and asserts that parsing never
/// throws anything but the documented <see cref="EvtxParseException"/>. Corruption must be recovered from, not crash
/// the caller. Deterministic: each (sample, round) has a fixed seed, and a failure reports the seed and mutation.
/// <para>
/// Rounds per sample default to <see cref="_defaultRounds"/>. Set <c>AXOPARSE_FUZZ_ROUNDS</c> for longer local runs,
/// e.g. <c>AXOPARSE_FUZZ_ROUNDS=5000</c> before a release.
/// </para>
/// </summary>
public class CorruptionFuzzTests
{
    #region Public Methods

    /// <summary>
    /// Verifies that no seeded mutation of any sample makes <see cref="EvtxParser.Parse"/> or
    /// <see cref="EvtxParser.WriteTo"/> throw an undocumented exception, single-threaded and (every tenth round)
    /// multi-threaded. Each mutation gets <see cref="_perMutationTimeout"/>, so an infinite loop is reported with its
    /// seed instead of hanging the run, whatever the round count.
    /// </summary>
    [Fact]
    public async Task MutatedSamplesNeverThrowUndocumentedExceptions()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        int rounds = int.TryParse(Environment.GetEnvironmentVariable(_roundsVariable), out int r) && (r > 0) ? r : _defaultRounds;
        List<string> failures = new();

        foreach (string file in Directory.GetFiles(TestPaths.TestDataDir, "*.evtx").Order(StringComparer.Ordinal))
        {
            string name = Path.GetFileName(file);
            byte[] original = await File.ReadAllBytesAsync(file, token);
            // Large samples are cut to a prefix so the default run stays well under 30 s; truncation is itself a
            // valid corruption, and the prefix still spans 16 chunks
            if (original.Length > _maxBaseBytes)
                original = original.AsSpan(0, _maxBaseBytes).ToArray();

            for (int round = 0; round < rounds; round++)
            {
                token.ThrowIfCancellationRequested();
                int seed = StableSeed(name, round);
                (byte[] data, string mutation) = Mutate(original, new Random(seed));
                int threads = round % 10 == 9 ? 4 : 1;
                string? error;
                try
                {
                    error = await Task.Run(() => Exercise(data, threads, token), token).WaitAsync(_perMutationTimeout, token);
                }
                catch (TimeoutException)
                {
                    // The hung parse keeps its thread; stop here rather than pile up more
                    failures.Add($"{name} round {round} seed {seed} [{mutation}] threads={threads}: " +
                                 $"no result after {_perMutationTimeout.TotalSeconds:F0} s (infinite loop?)");
                    break;
                }

                if (error != null)
                    failures.Add($"{name} round {round} seed {seed} [{mutation}] threads={threads}: {error}");
            }
        }

        Assert.True(failures.Count == 0,
            $"{failures.Count} mutation(s) threw undocumented exceptions:\n" + string.Join('\n', failures.Take(25)));
    }

    #endregion

    #region Non-Public Methods

    /// <summary>
    /// Parses and streams one mutated input, both XML and JSON, then parses it again in salvage mode and checks that
    /// every non-empty JSON record, salvaged or not, is well-formed JSON.
    /// </summary>
    /// <param name="data">Mutated file bytes.</param>
    /// <param name="threads">Thread count for both entry points.</param>
    /// <param name="token">Test cancellation token.</param>
    /// <returns>Null if nothing undocumented was thrown and all JSON is valid; otherwise a description of the problem.</returns>
    private static string? Exercise(byte[] data, int threads, CancellationToken token)
    {
        try
        {
            EvtxParser parser = EvtxParser.Parse(data, threads, OutputFormat.Xml, cancellationToken: token);
            foreach (EvtxEvent _ in parser.GetEvents()) { }
            EvtxParser.WriteTo(data, Stream.Null, OutputFormat.Json, threads, cancellationToken: token);
            EvtxParser.WriteTo(data, Stream.Null, OutputFormat.Xml, threads, validateChecksums: true, token);

            EvtxParser salvaged = EvtxParser.Parse(data, threads, OutputFormat.Json, cancellationToken: token, salvage: true);
            foreach (EvtxEvent evt in salvaged.GetEvents())
            {
                if (evt.Json.IsEmpty)
                    continue;
                try
                {
                    using JsonDocument _ = JsonDocument.Parse(evt.Json);
                }
                catch (JsonException ex)
                {
                    return $"record {evt.Record.EventRecordId} ({evt.Diagnostic ?? "rendered"}) is not valid JSON: {ex.Message}";
                }
            }

            return null;
        }
        catch (EvtxParseException)
        {
            // Documented: "this isn't an EVTX file" errors throw
            return null;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Exception inner = ex is AggregateException { InnerExceptions.Count: 1 } agg ? agg.InnerExceptions[0] : ex;
            string frame = inner.StackTrace?.Split('\n').FirstOrDefault()?.Trim() ?? "";
            return $"{inner.GetType().Name}: {inner.Message} {frame}";
        }
    }

    /// <summary>
    /// Applies one to three random mutations to a copy of the input.
    /// </summary>
    /// <param name="original">Unmodified sample bytes.</param>
    /// <param name="rng">Seeded random source.</param>
    /// <returns>The mutated bytes and a description of what was done (for reproducing a failure).</returns>
    private static (byte[] Data, string Mutation) Mutate(byte[] original, Random rng)
    {
        byte[] data = (byte[])original.Clone();
        List<string> applied = new();
        int count = rng.Next(1, 4);
        for (int m = 0; m < count; m++)
        {
            if (data.Length == 0)
                break;

            switch (rng.Next(7))
            {
                case 0:
                {
                    int flips = rng.Next(1, 17);
                    for (int i = 0; i < flips; i++)
                        data[rng.Next(data.Length)] ^= (byte)(1 << rng.Next(8));
                    applied.Add($"flip {flips} bits");
                    break;
                }
                case 1:
                {
                    int at = rng.Next(data.Length);
                    int len = Math.Min(rng.Next(1, 257), data.Length - at);
                    rng.NextBytes(data.AsSpan(at, len));
                    applied.Add($"random {len} bytes @0x{at:X}");
                    break;
                }
                case 2:
                {
                    int at = rng.Next(data.Length);
                    int len = Math.Min(rng.Next(1, 4097), data.Length - at);
                    data.AsSpan(at, len).Clear();
                    applied.Add($"zero {len} bytes @0x{at:X}");
                    break;
                }
                case 3:
                {
                    int len = rng.Next(data.Length);
                    data = data.AsSpan(0, len).ToArray();
                    applied.Add($"truncate to {len}");
                    break;
                }
                case 4:
                {
                    // File header: 4 KB block, sizes and counts at offsets 8-48 drive chunk iteration
                    int at = rng.Next(Math.Min(128, data.Length));
                    data[at] = (byte)rng.Next(256);
                    applied.Add($"file header byte @0x{at:X}");
                    break;
                }
                case 5:
                {
                    // Chunk header: record IDs, offsets and the template/string tables live in the first 512 bytes
                    int chunks = Math.Max(1, (data.Length - _fileHeaderSize) / EvtxChunk.ChunkSize);
                    int at = _fileHeaderSize + (rng.Next(chunks) * EvtxChunk.ChunkSize) + rng.Next(512);
                    if (at < data.Length)
                    {
                        WriteRandomField(data, at, rng);
                        applied.Add($"chunk header field @0x{at:X}");
                    }
                    break;
                }
                default:
                {
                    // Record area: record headers (size at +4) and BinXml token streams follow the chunk header
                    int chunks = Math.Max(1, (data.Length - _fileHeaderSize) / EvtxChunk.ChunkSize);
                    int at = _fileHeaderSize + (rng.Next(chunks) * EvtxChunk.ChunkSize) + 512 + rng.Next(4096);
                    if (at < data.Length)
                    {
                        WriteRandomField(data, at, rng);
                        applied.Add($"record field @0x{at:X}");
                    }
                    break;
                }
            }
        }

        return (data, string.Join(", ", applied));
    }

    /// <summary>
    /// Overwrites a 1-, 2- or 4-byte field with a boundary value or a random one; boundary values (0, max, sign bit)
    /// are what tend to break size and offset arithmetic.
    /// </summary>
    /// <param name="data">Bytes to modify.</param>
    /// <param name="at">Field offset.</param>
    /// <param name="rng">Seeded random source.</param>
    private static void WriteRandomField(byte[] data, int at, Random rng)
    {
        uint[] interesting = [0, 1, 0x7F, 0x80, 0xFF, 0x7FFF, 0x8000, 0xFFFF, 0x10000, 0x7FFFFFFF, 0x80000000, 0xFFFFFFFF];
        uint value = rng.Next(2) == 0 ? interesting[rng.Next(interesting.Length)] : (uint)rng.Next();
        int width = rng.Next(3) switch { 0 => 1, 1 => 2, _ => 4 };
        if (at + width > data.Length)
            width = data.Length - at;
        Span<byte> field = data.AsSpan(at, width);
        if (width == 4) BinaryPrimitives.WriteUInt32LittleEndian(field, value);
        else if (width == 2) BinaryPrimitives.WriteUInt16LittleEndian(field, (ushort)value);
        else field[0] = (byte)value;
    }

    /// <summary>
    /// Seed that is stable across processes and platforms (string.GetHashCode is randomised per process).
    /// </summary>
    /// <param name="name">Sample file name.</param>
    /// <param name="round">Round number.</param>
    /// <returns>FNV-1a hash of the name mixed with the round.</returns>
    private static int StableSeed(string name, int round)
    {
        uint hash = 2166136261;
        foreach (char c in name)
            hash = (hash ^ c) * 16777619;
        return (int)((hash ^ (uint)round) * 16777619);
    }

    #endregion

    #region Non-Public Fields

    /// <summary>
    /// Mutations per sample in the default (CI) run.
    /// </summary>
    private const int _defaultRounds = 100;

    /// <summary>
    /// Size of the EVTX file header block; the first chunk starts here.
    /// </summary>
    private const int _fileHeaderSize = 4096;

    /// <summary>
    /// Largest base input mutated (4 KB header + 16 chunks); bigger samples are cut to this prefix.
    /// </summary>
    private const int _maxBaseBytes = _fileHeaderSize + (16 * EvtxChunk.ChunkSize);

    /// <summary>
    /// Time allowed for one mutated input. A normal one takes milliseconds (inputs are at most 1 MB).
    /// </summary>
    private static readonly TimeSpan _perMutationTimeout = TimeSpan.FromSeconds(10);

    private const string _roundsVariable = "AXOPARSE_FUZZ_ROUNDS";

    #endregion
}
