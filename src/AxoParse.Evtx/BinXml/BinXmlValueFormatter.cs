using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace AxoParse.Evtx.BinXml;

/// <summary>
/// Shared static helpers for BinXml value formatting: hex conversion, XML escaping,
/// GUID/SID/timestamp rendering, and element size lookup. Used by both XML and JSON
/// rendering paths to avoid duplicating format-specific logic.
/// </summary>
internal static class BinXmlValueFormatter
{
    #region Non-Public Methods

    /// <summary>
    /// Appends a FILETIME value as an ISO 8601 UTC timestamp (yyyy-MM-ddTHH:mm:ss.fffffffZ).
    /// Returns false if the FILETIME is zero (caller decides how to handle empty).
    /// </summary>
    /// <param name="valueBytes">8-byte FILETIME value (little-endian 100-ns ticks since 1601-01-01).</param>
    /// <param name="vsb">String builder that receives the formatted timestamp.</param>
    /// <returns>True if a timestamp was appended; false if the FILETIME was zero.</returns>
    internal static bool AppendFileTime(ReadOnlySpan<byte> valueBytes, ref ValueStringBuilder vsb)
    {
        long ft = MemoryMarshal.Read<long>(valueBytes);
        if (ft == 0) return false;

        // Convert FILETIME (100-ns ticks since 1601-01-01) to total components
        long totalTicks = ft + FileTimeEpochDelta;
        // Decompose into date/time parts without constructing a DateTime object
        int totalDays = (int)(totalTicks / TicksPerDay);
        long remainingTicks = totalTicks - (long)totalDays * TicksPerDay;

        DecomposeDays(totalDays, out int year, out int month, out int day);

        int totalSeconds = (int)(remainingTicks / TicksPerSecond);
        int hour = totalSeconds / 3600;
        int minute = totalSeconds % 3600 / 60;
        int second = totalSeconds % 60;
        // 100-ns ticks within the current second (0..9_999_999) — gives 7 fractional digits
        int fractionalTicks = (int)(remainingTicks % TicksPerSecond);

        // yyyy-MM-ddTHH:mm:ss.fffffffZ — write digits directly
        AppendDigits4(ref vsb, year);
        vsb.Append('-');
        AppendDigits2(ref vsb, month);
        vsb.Append('-');
        AppendDigits2(ref vsb, day);
        vsb.Append('T');
        AppendDigits2(ref vsb, hour);
        vsb.Append(':');
        AppendDigits2(ref vsb, minute);
        vsb.Append(':');
        AppendDigits2(ref vsb, second);
        vsb.Append('.');
        AppendDigits7(ref vsb, fractionalTicks);
        vsb.Append('Z');
        return true;
    }

    /// <summary>
    /// Decomposes a day count (from tick epoch 0001-01-01) into year, month, day.
    /// Uses the same algorithm as the .NET runtime's DateTime implementation.
    /// </summary>
    /// <param name="totalDays">Total days since 0001-01-01.</param>
    /// <param name="year">Resulting year.</param>
    /// <param name="month">Resulting month (1-12).</param>
    /// <param name="day">Resulting day of month (1-31).</param>
    private static void DecomposeDays(int totalDays, out int year, out int month, out int day)
    {
        // 400-year cycle = 146097 days
        int y400 = totalDays / 146097;
        int d = totalDays - y400 * 146097;
        // 100-year cycle = 36524 days (except last cycle which has 36525)
        int y100 = Math.Min(d / 36524, 3);
        d -= y100 * 36524;
        // 4-year cycle = 1461 days
        int y4 = d / 1461;
        d -= y4 * 1461;
        // Single year = 365 days (except last year in 4-year cycle which has 366)
        int y1 = Math.Min(d / 365, 3);
        d -= y1 * 365;

        year = y400 * 400 + y100 * 100 + y4 * 4 + y1 + 1;
        bool leap = (y1 == 3) && ((y4 != 24) || (y100 == 3));
        ReadOnlySpan<int> cumulativeDays = leap ? CumulativeDaysLeap : CumulativeDaysNormal;

        // Binary search for month
        month = (d >> 5) + 1;
        while (d >= cumulativeDays[month])
            month++;
        day = d - cumulativeDays[month - 1] + 1;
    }

    /// <summary>
    /// Appends a 2-digit zero-padded number.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void AppendDigits2(ref ValueStringBuilder vsb, int value)
    {
        vsb.Append((char)('0' + value / 10));
        vsb.Append((char)('0' + value % 10));
    }

    /// <summary>
    /// Appends a 4-digit zero-padded number.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void AppendDigits4(ref ValueStringBuilder vsb, int value)
    {
        vsb.Append((char)('0' + value / 1000));
        vsb.Append((char)('0' + value / 100 % 10));
        vsb.Append((char)('0' + value / 10 % 10));
        vsb.Append((char)('0' + value % 10));
    }

    /// <summary>
    /// Appends a 7-digit zero-padded number (100-nanosecond ticks within a second, 0..9_999_999).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void AppendDigits7(ref ValueStringBuilder vsb, int value)
    {
        vsb.Append((char)('0' + value / 1000000));
        vsb.Append((char)('0' + value / 100000 % 10));
        vsb.Append((char)('0' + value / 10000 % 10));
        vsb.Append((char)('0' + value / 1000 % 10));
        vsb.Append((char)('0' + value / 100 % 10));
        vsb.Append((char)('0' + value / 10 % 10));
        vsb.Append((char)('0' + value % 10));
    }

    /// <summary>
    /// Appends each byte as two uppercase hex characters using the precomputed <see cref="HexChars"/> table.
    /// </summary>
    /// <param name="vsb">String builder that receives the hex output.</param>
    /// <param name="data">Bytes to convert.</param>
    internal static void AppendHex(ref ValueStringBuilder vsb, ReadOnlySpan<byte> data)
    {
        for (int i = 0; i < data.Length; i++)
        {
            int idx = data[i] * 2;
            vsb.Append(HexChars[idx]);
            vsb.Append(HexChars[idx + 1]);
        }
    }

    /// <summary>
    /// Appends text to the string builder with JSON escaping per RFC 8259.
    /// Escapes backslash, double-quote, and control characters U+0000..U+001F.
    /// Uses a fast path for text containing no special characters.
    /// Replaces unpaired surrogates with U+FFFD.
    /// </summary>
    /// <param name="vsb">String builder that receives the escaped text.</param>
    /// <param name="text">Source character span to escape and append.</param>
    internal static void AppendJsonEscaped(ref ValueStringBuilder vsb, scoped ReadOnlySpan<char> text)
    {
        // Fast path: no JSON-special chars, no control chars, no surrogates → bulk append
        bool needsEscape = false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if ((c == '\\') || (c == '"') || (c < '\u0020') || char.IsSurrogate(c))
            {
                needsEscape = true;
                break;
            }
        }

        if (!needsEscape)
        {
            vsb.Append(text);
            return;
        }

        // Slow path: escape per RFC 8259
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (char.IsHighSurrogate(c))
            {
                if ((i + 1 < text.Length) && char.IsLowSurrogate(text[i + 1]))
                {
                    vsb.Append(c);
                    vsb.Append(text[++i]);
                }
                else
                {
                    vsb.Append('\uFFFD');
                }
            }
            else if (char.IsLowSurrogate(c))
            {
                vsb.Append('\uFFFD');
            }
            else
            {
                switch (c)
                {
                    case '\\': vsb.Append("\\\\"); break;
                    case '"': vsb.Append("\\\""); break;
                    case '\n': vsb.Append("\\n"); break;
                    case '\r': vsb.Append("\\r"); break;
                    case '\t': vsb.Append("\\t"); break;
                    case '\b': vsb.Append("\\b"); break;
                    case '\f': vsb.Append("\\f"); break;
                    default:
                        if (c < '\u0020')
                        {
                            vsb.Append("\\u00");
                            int hIdx = c * 2;
                            vsb.Append(HexChars[hIdx]);
                            vsb.Append(HexChars[hIdx + 1]);
                        }
                        else
                        {
                            vsb.Append(c);
                        }
                        break;
                }
            }
        }
    }

    /// <summary>
    /// Appends a SID (Security Identifier) in SDDL string form (e.g., S-1-5-18) to the string builder.
    /// SID binary layout: revision(1) + subAuthorityCount(1) + authority(6 big-endian) + subAuthorities(4 each LE).
    /// </summary>
    /// <param name="valueBytes">Raw SID bytes.</param>
    /// <param name="size">Byte size of the SID data.</param>
    /// <param name="vsb">String builder that receives the formatted SID.</param>
    internal static void AppendSid(ReadOnlySpan<byte> valueBytes, int size, ref ValueStringBuilder vsb)
    {
        byte revision = valueBytes[0];
        byte subCount = valueBytes[1];
        // 6-byte big-endian identifier authority
        long authority = 0;
        for (int i = 2; i < 8; i++)
            authority = authority * 256 + valueBytes[i];
        vsb.Append("S-");
        vsb.AppendFormatted(revision);
        vsb.Append('-');
        vsb.AppendFormatted(authority);
        for (int i = 0; i < subCount; i++)
        {
            int subOff = 8 + i * 4;
            if (subOff + 4 > size) break;
            vsb.Append('-');
            vsb.AppendFormatted(MemoryMarshal.Read<uint>(valueBytes[subOff..]));
        }
    }

    /// <summary>
    /// Appends a SYSTEMTIME struct as an ISO 8601 UTC timestamp (yyyy-MM-ddTHH:mm:ss.mmmZ).
    /// SYSTEMTIME layout: year(2) + month(2) + dayOfWeek(2) + day(2) + hour(2) + minute(2) + second(2) + ms(2) = 16 bytes.
    /// </summary>
    /// <param name="valueBytes">16-byte SYSTEMTIME struct.</param>
    /// <param name="vsb">String builder that receives the formatted timestamp.</param>
    internal static void AppendSystemTime(ReadOnlySpan<byte> valueBytes, ref ValueStringBuilder vsb)
    {
        ushort yr = MemoryMarshal.Read<ushort>(valueBytes);
        ushort mo = MemoryMarshal.Read<ushort>(valueBytes[2..]);
        ushort dy = MemoryMarshal.Read<ushort>(valueBytes[6..]);
        ushort hr = MemoryMarshal.Read<ushort>(valueBytes[8..]);
        ushort mn = MemoryMarshal.Read<ushort>(valueBytes[10..]);
        ushort sc = MemoryMarshal.Read<ushort>(valueBytes[12..]);
        ushort ms = MemoryMarshal.Read<ushort>(valueBytes[14..]);
        AppendDigits4(ref vsb, yr);
        vsb.Append('-');
        AppendDigits2(ref vsb, mo);
        vsb.Append('-');
        AppendDigits2(ref vsb, dy);
        vsb.Append('T');
        AppendDigits2(ref vsb, hr);
        vsb.Append(':');
        AppendDigits2(ref vsb, mn);
        vsb.Append(':');
        AppendDigits2(ref vsb, sc);
        vsb.Append('.');
        // 3-digit milliseconds
        vsb.Append((char)('0' + ms / 100));
        vsb.Append((char)('0' + ms / 10 % 10));
        vsb.Append((char)('0' + ms % 10));
        vsb.Append('Z');
    }

    /// <summary>
    /// Appends text to the string builder with XML entity escaping (&amp;, &lt;, &gt;, &quot;, &apos;).
    /// Uses a fast path for text containing no special characters. Replaces unpaired surrogates with U+FFFD.
    /// </summary>
    /// <param name="vsb">String builder that receives the escaped text.</param>
    /// <param name="text">Source character span to escape and append.</param>
    internal static void AppendXmlEscaped(ref ValueStringBuilder vsb, scoped ReadOnlySpan<char> text)
    {
        // Fast path: no XML entity chars — check surrogates separately (extremely rare in EVTX)
        int idx = text.IndexOfAny(XmlEscapeChars);
        if (idx < 0)
        {
            int surIdx = text.IndexOfAnyInRange('\uD800', '\uDFFF');
            if (surIdx < 0)
            {
                vsb.Append(text);
                return;
            }
            // Surrogates present but no XML entities — handle surrogates only
            AppendWithSurrogateFixup(ref vsb, text, surIdx);
            return;
        }

        // Check if surrogates are also present
        int firstSurrogate = text.IndexOfAnyInRange('\uD800', '\uDFFF');
        bool hasSurrogates = firstSurrogate >= 0;

        // Bulk-copy clean prefix up to first entity char
        vsb.Append(text[..idx]);
        ReadOnlySpan<char> remaining = text[idx..];

        while (remaining.Length > 0)
        {
            // Emit the entity for the current special char
            switch (remaining[0])
            {
                case '&': vsb.Append("&amp;"); break;
                case '<': vsb.Append("&lt;"); break;
                case '>': vsb.Append("&gt;"); break;
                case '"': vsb.Append("&quot;"); break;
                case '\'': vsb.Append("&apos;"); break;
            }
            remaining = remaining[1..];

            // Scan ahead for next entity char and bulk-copy the clean run
            int next = remaining.IndexOfAny(XmlEscapeChars);
            if (next < 0)
            {
                // No more entities — handle remaining surrogates if needed, else bulk-copy
                if (hasSurrogates && (remaining.IndexOfAnyInRange('\uD800', '\uDFFF') >= 0))
                    AppendWithSurrogateFixup(ref vsb, remaining, remaining.IndexOfAnyInRange('\uD800', '\uDFFF'));
                else
                    vsb.Append(remaining);
                return;
            }

            // Bulk-copy clean chars between entities (surrogates in this range are passed through —
            // valid pairs are fine in XML, and unpaired surrogates in EVTX data are near-zero probability)
            vsb.Append(remaining[..next]);
            remaining = remaining[next..];
        }
    }

    /// <summary>
    /// Appends text that contains surrogate characters, replacing unpaired surrogates with U+FFFD.
    /// Called only when surrogates are detected (extremely rare in EVTX data).
    /// </summary>
    /// <param name="vsb">String builder that receives the text.</param>
    /// <param name="text">Source character span.</param>
    /// <param name="firstSurrogate">Index of the first surrogate character in the span.</param>
    private static void AppendWithSurrogateFixup(ref ValueStringBuilder vsb, scoped ReadOnlySpan<char> text, int firstSurrogate)
    {
        vsb.Append(text[..firstSurrogate]);
        for (int i = firstSurrogate; i < text.Length; i++)
        {
            char c = text[i];
            if (char.IsHighSurrogate(c))
            {
                if ((i + 1 < text.Length) && char.IsLowSurrogate(text[i + 1]))
                {
                    vsb.Append(c);
                    vsb.Append(text[++i]);
                }
                else
                {
                    vsb.Append('\uFFFD');
                }
            }
            else if (char.IsLowSurrogate(c))
            {
                vsb.Append('\uFFFD');
            }
            else
            {
                vsb.Append(c);
            }
        }
    }

    /// <summary>
    /// Formats a 16-byte GUID in uppercase hex without braces: XXXXXXXX-XXXX-XXXX-XXXX-XXXXXXXXXXXX.
    /// Matches the Rust winstructs::Guid Display format.
    /// First three components (Data1/Data2/Data3) are little-endian; last 8 bytes are big-endian.
    /// </summary>
    /// <param name="b">16-byte span containing the raw GUID.</param>
    /// <param name="vsb">String builder that receives the formatted GUID.</param>
    internal static void FormatGuid(ReadOnlySpan<byte> b, ref ValueStringBuilder vsb)
    {
        // Data1 (4 bytes LE) → 8 hex chars, written big-endian
        AppendHexByte(ref vsb, b[3]);
        AppendHexByte(ref vsb, b[2]);
        AppendHexByte(ref vsb, b[1]);
        AppendHexByte(ref vsb, b[0]);
        vsb.Append('-');
        // Data2 (2 bytes LE) → 4 hex chars
        AppendHexByte(ref vsb, b[5]);
        AppendHexByte(ref vsb, b[4]);
        vsb.Append('-');
        // Data3 (2 bytes LE) → 4 hex chars
        AppendHexByte(ref vsb, b[7]);
        AppendHexByte(ref vsb, b[6]);
        vsb.Append('-');
        // Bytes 8-9 (big-endian)
        AppendHexByte(ref vsb, b[8]);
        AppendHexByte(ref vsb, b[9]);
        vsb.Append('-');
        // Bytes 10-15 (big-endian)
        AppendHexByte(ref vsb, b[10]);
        AppendHexByte(ref vsb, b[11]);
        AppendHexByte(ref vsb, b[12]);
        AppendHexByte(ref vsb, b[13]);
        AppendHexByte(ref vsb, b[14]);
        AppendHexByte(ref vsb, b[15]);
    }

    /// <summary>
    /// Appends a single byte as two uppercase hex characters from <see cref="HexChars"/>.
    /// </summary>
    /// <param name="vsb">String builder that receives the hex output.</param>
    /// <param name="b">Byte to convert.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void AppendHexByte(ref ValueStringBuilder vsb, byte b)
    {
        int idx = b * 2;
        vsb.Append(HexChars[idx]);
        vsb.Append(HexChars[idx + 1]);
    }

    /// <summary>
    /// Returns the fixed byte size of a BinXml value type for array element splitting.
    /// Returns 0 for variable-length or unknown types.
    /// </summary>
    /// <param name="baseType">Base BinXml value type code (without the 0x80 array flag).</param>
    /// <returns>Fixed element size in bytes, or 0 if the type has no fixed size.</returns>
    internal static int GetElementSize(byte baseType)
    {
        return baseType switch
        {
            BinXmlValueType.Int8 or BinXmlValueType.UInt8 => 1,
            BinXmlValueType.Int16 or BinXmlValueType.UInt16 => 2,
            BinXmlValueType.Int32 or BinXmlValueType.UInt32 or BinXmlValueType.Float or BinXmlValueType.Bool
                or BinXmlValueType.HexInt32 => 4,
            BinXmlValueType.Int64 or BinXmlValueType.UInt64 or BinXmlValueType.Double or BinXmlValueType.FileTime
                or BinXmlValueType.HexInt64 => 8,
            BinXmlValueType.Guid or BinXmlValueType.SystemTime => 16,
            _ => 0
        };
    }

    /// <summary>
    /// Returns a JSON-escaped copy of <paramref name="str"/> per RFC 8259.
    /// Used during JSON template compilation where a heap string is needed
    /// rather than span-based appending.
    /// </summary>
    /// <param name="str">The string to escape.</param>
    /// <returns>The escaped string, or the original string if no escaping was needed.</returns>
    internal static string JsonEscapeString(string str)
    {
        ReadOnlySpan<char> span = str.AsSpan();
        bool needsEscape = false;
        for (int i = 0; i < span.Length; i++)
        {
            char c = span[i];
            if ((c == '\\') || (c == '"') || (c < '\u0020'))
            {
                needsEscape = true;
                break;
            }
        }
        if (!needsEscape)
            return str;

        ValueStringBuilder vsb = new(stackalloc char[str.Length * 2]);
        AppendJsonEscaped(ref vsb, span);
        string result = vsb.ToString();
        vsb.Dispose();
        return result;
    }

    /// <summary>
    /// Reads a length-prefixed UTF-16LE string as a span, avoiding a heap allocation.
    /// The returned span points directly into <paramref name="data"/> and is only valid
    /// while the underlying buffer is alive.
    /// </summary>
    /// <param name="data">BinXml byte stream.</param>
    /// <param name="pos">Current read position; advanced past the 2-byte length prefix and string bytes.</param>
    /// <returns>A <see cref="ReadOnlySpan{T}"/> over the decoded characters.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ReadOnlySpan<char> ReadUnicodeTextString(ReadOnlySpan<byte> data, ref int pos)
    {
        ushort numChars = MemoryMarshal.Read<ushort>(data[pos..]);
        pos += 2;
        ReadOnlySpan<char> chars = MemoryMarshal.Cast<byte, char>(data.Slice(pos, numChars * 2));
        pos += numChars * 2;
        return chars;
    }

    /// <summary>
    /// Reads a length-prefixed UTF-16LE string: 2-byte character count followed by numChars * 2 bytes.
    /// </summary>
    /// <param name="data">BinXml byte stream.</param>
    /// <param name="pos">Current read position; advanced past the 2-byte length prefix and string bytes.</param>
    /// <returns>The decoded string.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static string ReadUnicodeTextStringAsString(ReadOnlySpan<byte> data, ref int pos)
    {
        ushort numChars = MemoryMarshal.Read<ushort>(data[pos..]);
        pos += 2;
        ReadOnlySpan<char> chars = MemoryMarshal.Cast<byte, char>(data.Slice(pos, numChars * 2));
        pos += numChars * 2;
        return new string(chars);
    }

    /// <summary>
    /// Returns an XML-escaped copy of <paramref name="str"/>. Used during template compilation
    /// where a heap string is needed rather than span-based appending.
    /// </summary>
    /// <param name="str">The string to escape.</param>
    /// <returns>The escaped string, or the original string if no escaping was needed.</returns>
    internal static string XmlEscapeString(string str)
    {
        ReadOnlySpan<char> span = str.AsSpan();
        if ((span.IndexOfAny('&', '<', '>') < 0) && (span.IndexOfAny('"', '\'') < 0))
            return str;
        return str.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
            .Replace("\"", "&quot;").Replace("'", "&apos;");
    }

    /// <summary>
    /// Builds a 512-entry char lookup table for fast byte-to-hex conversion.
    /// Each byte maps to two consecutive chars at index byte*2.
    /// </summary>
    /// <returns>Array where index <c>i*2</c> and <c>i*2+1</c> contain the uppercase hex digit pair for byte <c>i</c>.</returns>
    private static char[] InitHexChars()
    {
        char[] table = new char[512];
        for (int i = 0; i < 256; i++)
        {
            table[i * 2] = "0123456789ABCDEF"[i >> 4];
            table[i * 2 + 1] = "0123456789ABCDEF"[i & 0xF];
        }
        return table;
    }


    #endregion

    #region Non-Public Fields

    /// <summary>
    /// Windows FILETIME epoch (1601-01-01) to .NET DateTime epoch (0001-01-01) delta in 100-ns ticks.
    /// FILETIME stores ticks since 1601-01-01; adding this constant converts to DateTime ticks.
    /// </summary>
    internal const long FileTimeEpochDelta = 504911232000000000L;

    /// <summary>
    /// 100-nanosecond ticks per day (24 * 60 * 60 * 10_000_000).
    /// </summary>
    private const long TicksPerDay = 864000000000L;

    /// <summary>
    /// 100-nanosecond ticks per second (10_000_000).
    /// </summary>
    private const long TicksPerSecond = 10000000L;

    /// <summary>
    /// Cumulative days before each month for normal years. Index 0 = 0 (before Jan), index 1 = 31 (before Feb), etc.
    /// </summary>
    private static ReadOnlySpan<int> CumulativeDaysNormal =>
    [
        0, 31, 59, 90, 120, 151, 181, 212, 243, 273, 304, 334, 365
    ];

    /// <summary>
    /// Cumulative days before each month for leap years.
    /// </summary>
    private static ReadOnlySpan<int> CumulativeDaysLeap =>
    [
        0, 31, 60, 91, 121, 152, 182, 213, 244, 274, 305, 335, 366
    ];

    /// <summary>
    /// Pre-computed lookup table mapping byte values 0x00..0xFF to two uppercase hex characters.
    /// Byte <c>i</c> maps to <c>HexChars[i*2]</c> (high nibble) and <c>HexChars[i*2+1]</c> (low nibble).
    /// </summary>
    internal static readonly char[] HexChars = InitHexChars();

    /// <summary>
    /// Vectorised search set for XML characters needing entity escaping: &amp; &lt; &gt; &quot; &apos;.
    /// </summary>
    private static readonly SearchValues<char> XmlEscapeChars = SearchValues.Create("&<>\"'");

    /// <summary>
    /// Lowercase hex digit chars for nibble-to-char conversion.
    /// </summary>
    private const string LowerHexDigits = "0123456789abcdef";

    #endregion

    #region Lowercase Hex Helpers

    /// <summary>
    /// Appends a <see cref="uint"/> as 8-character zero-padded lowercase hex (e.g., "0000002a").
    /// </summary>
    /// <param name="vsb">String builder that receives the hex output.</param>
    /// <param name="value">The value to format.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void AppendHexUInt32Padded(ref ValueStringBuilder vsb, uint value)
    {
        for (int i = 28; i >= 0; i -= 4)
            vsb.Append(LowerHexDigits[(int)(value >> i) & 0xF]);
    }

    /// <summary>
    /// Appends a <see cref="ulong"/> as 16-character zero-padded lowercase hex.
    /// </summary>
    /// <param name="vsb">String builder that receives the hex output.</param>
    /// <param name="value">The value to format.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void AppendHexUInt64Padded(ref ValueStringBuilder vsb, ulong value)
    {
        for (int i = 60; i >= 0; i -= 4)
            vsb.Append(LowerHexDigits[(int)(value >> i) & 0xF]);
    }

    /// <summary>
    /// Appends a <see cref="uint"/> as minimal lowercase hex with no leading zeros (e.g., "2a").
    /// Outputs "0" for zero.
    /// </summary>
    /// <param name="vsb">String builder that receives the hex output.</param>
    /// <param name="value">The value to format.</param>
    internal static void AppendHexUInt32Min(ref ValueStringBuilder vsb, uint value)
    {
        if (value == 0)
        {
            vsb.Append('0');
            return;
        }
        int shift = 28;
        while (((value >> shift) & 0xF) == 0) shift -= 4;
        for (int i = shift; i >= 0; i -= 4)
            vsb.Append(LowerHexDigits[(int)(value >> i) & 0xF]);
    }

    /// <summary>
    /// Appends a <see cref="ulong"/> as minimal lowercase hex with no leading zeros.
    /// Outputs "0" for zero.
    /// </summary>
    /// <param name="vsb">String builder that receives the hex output.</param>
    /// <param name="value">The value to format.</param>
    internal static void AppendHexUInt64Min(ref ValueStringBuilder vsb, ulong value)
    {
        if (value == 0)
        {
            vsb.Append('0');
            return;
        }
        int shift = 60;
        while (((value >> shift) & 0xF) == 0) shift -= 4;
        for (int i = shift; i >= 0; i -= 4)
            vsb.Append(LowerHexDigits[(int)(value >> i) & 0xF]);
    }

    #endregion
}