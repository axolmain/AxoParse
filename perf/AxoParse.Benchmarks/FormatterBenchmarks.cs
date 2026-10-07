using AxoParse.Evtx;
using AxoParse.Evtx.BinXml;
using BenchmarkDotNet.Attributes;

namespace AxoParse.Benchmarks;

/// <summary>
/// Micro-benchmarks for the per-value formatting primitives in <see cref="BinXmlValueFormatter"/>
/// and for <see cref="Crc32"/>. These run once per substitution value (many times per record),
/// so nanosecond-level wins here multiply across millions of values.
/// Each benchmark writes into a stack-backed <see cref="ValueUtf8Builder"/> and returns its length,
/// so string materialisation is excluded and only the formatting work is measured.
/// </summary>
[BenchmarkCategory("Micro")]
public class FormatterBenchmarks
{
    #region Public Methods

    /// <summary>
    /// FILETIME (8 bytes, 100-ns ticks since 1601) to ISO 8601 — appears in every System/TimeCreated.
    /// </summary>
    /// <returns>UTF-8 bytes written.</returns>
    [Benchmark]
    public int FileTime()
    {
        ValueUtf8Builder vsb = new(stackalloc byte[192]);
        BinXmlValueFormatter.AppendFileTime(_fileTime, ref vsb);
        int len = vsb.Length;
        vsb.Dispose();
        return len;
    }

    /// <summary>
    /// SYSTEMTIME (16 bytes) to ISO 8601.
    /// </summary>
    /// <returns>UTF-8 bytes written.</returns>
    [Benchmark]
    public int SystemTime()
    {
        ValueUtf8Builder vsb = new(stackalloc byte[192]);
        BinXmlValueFormatter.AppendSystemTime(_systemTime, ref vsb);
        int len = vsb.Length;
        vsb.Dispose();
        return len;
    }

    /// <summary>
    /// 16-byte GUID to braced uppercase form — ProviderGuid, ActivityID.
    /// </summary>
    /// <returns>UTF-8 bytes written.</returns>
    [Benchmark]
    public int Guid()
    {
        ValueUtf8Builder vsb = new(stackalloc byte[192]);
        BinXmlValueFormatter.FormatGuid(_guid, ref vsb);
        int len = vsb.Length;
        vsb.Dispose();
        return len;
    }

    /// <summary>
    /// Domain-user SID (5 sub-authorities) to SDDL form — dominant in Security logs.
    /// </summary>
    /// <returns>UTF-8 bytes written.</returns>
    [Benchmark]
    public int Sid()
    {
        ValueUtf8Builder vsb = new(stackalloc byte[288]);
        BinXmlValueFormatter.AppendSid(_sid, _sid.Length, ref vsb);
        int len = vsb.Length;
        vsb.Dispose();
        return len;
    }

    /// <summary>
    /// 32-byte binary blob to uppercase hex.
    /// </summary>
    /// <returns>UTF-8 bytes written.</returns>
    [Benchmark]
    public int Hex32()
    {
        ValueUtf8Builder vsb = new(stackalloc byte[288]);
        BinXmlValueFormatter.AppendHex(ref vsb, _hex);
        int len = vsb.Length;
        vsb.Dispose();
        return len;
    }

    /// <summary>
    /// XML escaping on text with no special characters — the common fast path.
    /// </summary>
    /// <returns>UTF-8 bytes written.</returns>
    [Benchmark]
    public int XmlEscapeClean()
    {
        ValueUtf8Builder vsb = new(stackalloc byte[768]);
        BinXmlValueFormatter.AppendXmlEscaped(ref vsb, CleanText);
        int len = vsb.Length;
        vsb.Dispose();
        return len;
    }

    /// <summary>
    /// XML escaping on text containing several entity characters — the slow path.
    /// </summary>
    /// <returns>UTF-8 bytes written.</returns>
    [Benchmark]
    public int XmlEscapeDirty()
    {
        ValueUtf8Builder vsb = new(stackalloc byte[768]);
        BinXmlValueFormatter.AppendXmlEscaped(ref vsb, DirtyText);
        int len = vsb.Length;
        vsb.Dispose();
        return len;
    }

    /// <summary>
    /// JSON escaping on text with no special characters.
    /// </summary>
    /// <returns>UTF-8 bytes written.</returns>
    [Benchmark]
    public int JsonEscapeClean()
    {
        ValueUtf8Builder vsb = new(stackalloc byte[768]);
        BinXmlValueFormatter.AppendJsonEscaped(ref vsb, CleanText);
        int len = vsb.Length;
        vsb.Dispose();
        return len;
    }

    /// <summary>
    /// JSON escaping on a Windows path with backslashes and quotes.
    /// </summary>
    /// <returns>UTF-8 bytes written.</returns>
    [Benchmark]
    public int JsonEscapeDirty()
    {
        ValueUtf8Builder vsb = new(stackalloc byte[768]);
        BinXmlValueFormatter.AppendJsonEscaped(ref vsb, DirtyText);
        int len = vsb.Length;
        vsb.Dispose();
        return len;
    }

    /// <summary>
    /// CRC32 over a full 64 KB chunk — the cost of <c>validateChecksums: true</c> per chunk.
    /// </summary>
    /// <returns>The checksum.</returns>
    [Benchmark]
    public uint Crc32Chunk() => Crc32.Compute(_chunk);

    #endregion

    #region Fields

    /// <summary>
    /// Typical provider/channel text with no characters that need escaping.
    /// </summary>
    private const string CleanText = "Microsoft-Windows-Security-Auditing An account was successfully logged on";

    /// <summary>
    /// Command-line-style text with quotes, ampersands, angle brackets and backslashes.
    /// </summary>
    private const string DirtyText = "\"C:\\Program Files\\App\\app.exe\" --arg=<value> & echo 'done' > C:\\out.txt";

    #endregion

    #region Non-Public Fields

    /// <summary>
    /// FILETIME for 2024-03-15T12:34:56.1234567Z.
    /// </summary>
    private readonly byte[] _fileTime = BitConverter.GetBytes(new DateTime(2024, 3, 15, 12, 34, 56, DateTimeKind.Utc).AddTicks(1234567).ToFileTimeUtc());

    /// <summary>
    /// SYSTEMTIME for 2024-03-15 12:34:56.789 (year, month, dayOfWeek, day, hour, minute, second, ms).
    /// </summary>
    private readonly byte[] _systemTime =
        [0xE8, 0x07, 0x03, 0x00, 0x05, 0x00, 0x0F, 0x00, 0x0C, 0x00, 0x22, 0x00, 0x38, 0x00, 0x15, 0x03];

    /// <summary>
    /// Arbitrary 16-byte GUID.
    /// </summary>
    private readonly byte[] _guid = System.Guid.Parse("54849625-5478-4994-a5ba-3e3b0328c30d").ToByteArray();

    /// <summary>
    /// SID S-1-5-21-3623811015-3361044348-30300820-1013: revision 1, 5 sub-authorities, authority 5.
    /// </summary>
    private readonly byte[] _sid = BuildSid([21, 3623811015, 3361044348, 30300820, 1013]);

    /// <summary>
    /// 32 bytes of 0x00..0x1F.
    /// </summary>
    private readonly byte[] _hex = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();

    /// <summary>
    /// 64 KB of deterministic pseudo-random bytes for CRC32.
    /// </summary>
    private readonly byte[] _chunk = BuildChunk();

    #endregion

    #region Non-Public Methods

    /// <summary>
    /// Builds a binary SID: revision(1) + subAuthorityCount(1) + authority(6 BE) + subAuthorities(4 LE each).
    /// </summary>
    /// <param name="subAuthorities">Sub-authority values.</param>
    /// <returns>SID bytes.</returns>
    private static byte[] BuildSid(uint[] subAuthorities)
    {
        byte[] sid = new byte[8 + subAuthorities.Length * 4];
        sid[0] = 1;
        sid[1] = (byte)subAuthorities.Length;
        sid[7] = 5; // NT authority
        for (int i = 0; i < subAuthorities.Length; i++)
            BitConverter.TryWriteBytes(sid.AsSpan(8 + i * 4), subAuthorities[i]);
        return sid;
    }

    /// <summary>
    /// Fills a 64 KB buffer with seeded random bytes.
    /// </summary>
    /// <returns>Chunk-sized buffer.</returns>
    private static byte[] BuildChunk()
    {
        byte[] chunk = new byte[65536];
        new Random(42).NextBytes(chunk);
        return chunk;
    }

    #endregion
}
