using System.Buffers;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace AxoParse.Evtx.BinXml;

/// <summary>
/// Value formatting for the UTF-8 renderers: timestamps, GUID, SID, hex and numbers written as ASCII, and XML/JSON
/// escaping of UTF-16 BinXml text transcoded to UTF-8, plus the string helpers template compilation needs.
/// Each renderer method produces exactly the bytes that rendering the record as one UTF-16 string and encoding it once
/// would produce. Where that rendering replaced unpaired surrogates with U+FFFD (escaped values), text is appended with
/// <see cref="ValueUtf8Builder.AppendUtf16Isolated"/>; where it appended text unchanged, it uses
/// <see cref="ValueUtf8Builder.AppendUtf16"/> so surrogate pairs split across appends still join.
/// </summary>
internal static class BinXmlValueFormatter
{
    #region Non-Public Methods


    /// <summary>
    /// Appends a FILETIME value as an ISO 8601 UTC timestamp (yyyy-MM-ddTHH:mm:ss.fffffffZ).
    /// Returns false if the FILETIME is zero (caller decides how to handle empty).
    /// </summary>
    /// <param name="valueBytes">8-byte FILETIME value (little-endian 100-ns ticks since 1601-01-01).</param>
    /// <param name="vub">UTF-8 builder that receives the formatted timestamp.</param>
    /// <returns>True if a timestamp was appended; false if the FILETIME was zero.</returns>
    internal static bool AppendFileTime(ReadOnlySpan<byte> valueBytes, ref ValueUtf8Builder vub)
    {
        long ft = MemoryMarshal.Read<long>(valueBytes);
        if (ft == 0) return false;

        long totalTicks = ft + FileTimeEpochDelta;
        int totalDays = (int)(totalTicks / TicksPerDay);
        long remainingTicks = totalTicks - (long)totalDays * TicksPerDay;

        DecomposeDays(totalDays, out int year, out int month, out int day);

        int totalSeconds = (int)(remainingTicks / TicksPerSecond);
        int hour = totalSeconds / 3600;
        int minute = totalSeconds % 3600 / 60;
        int second = totalSeconds % 60;
        // 100-ns ticks within the current second (0..9_999_999) — gives 7 fractional digits
        int fractionalTicks = (int)(remainingTicks % TicksPerSecond);

        // yyyy-MM-ddTHH:mm:ss.fffffffZ = 28 bytes; write the last index first so the remaining bounds checks fold
        Span<byte> d = vub.AppendSpan(28);
        d[27] = (byte)'Z';
        WriteDigits4(d, 0, year);
        d[4] = (byte)'-';
        WriteDigits2(d, 5, month);
        d[7] = (byte)'-';
        WriteDigits2(d, 8, day);
        d[10] = (byte)'T';
        WriteDigits2(d, 11, hour);
        d[13] = (byte)':';
        WriteDigits2(d, 14, minute);
        d[16] = (byte)':';
        WriteDigits2(d, 17, second);
        d[19] = (byte)'.';
        WriteDigits7(d, 20, fractionalTicks);
        return true;
    }

    /// <summary>
    /// Appends each byte as two uppercase hex digits using the BCL's vectorised hex encoder.
    /// </summary>
    /// <param name="vub">UTF-8 builder that receives the hex output.</param>
    /// <param name="data">Bytes to convert (at most 65535 — value sizes are u16 — so the doubled length cannot overflow).</param>
    internal static void AppendHex(ref ValueUtf8Builder vub, ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty) return;
        Convert.TryToHexString(data, vub.AppendSpan(data.Length * 2), out _);
    }

    /// <summary>
    /// Appends text with JSON escaping per RFC 8259 (backslash, double-quote, U+0000..U+001F) as UTF-8.
    /// Unpaired surrogates become U+FFFD. Clean runs between escape characters are transcoded in bulk.
    /// </summary>
    /// <param name="vub">UTF-8 builder that receives the escaped text.</param>
    /// <param name="text">UTF-16 text to escape.</param>
    internal static void AppendJsonEscaped(ref ValueUtf8Builder vub, scoped ReadOnlySpan<char> text)
    {
        int idx = text.IndexOfAny(JsonEscapeChars);
        while (idx >= 0)
        {
            // Runs end only at ASCII escape characters, so no surrogate pair straddles a run boundary
            vub.AppendUtf16Isolated(text[..idx]);
            AppendJsonEscapeSequence(ref vub, text[idx]);
            text = text[(idx + 1)..];
            idx = text.IndexOfAny(JsonEscapeChars);
        }

        vub.AppendUtf16Isolated(text);
    }

    /// <summary>
    /// Appends text with JSON escaping applied twice as UTF-8 — the bytes of
    /// <c>AppendJsonEscaped(AppendJsonEscaped(text))</c>. Used for string values inside an embedded BinXml (0x21)
    /// fragment whose rendered JSON is itself emitted as a JSON string. Unpaired surrogates become U+FFFD.
    /// </summary>
    /// <param name="vub">UTF-8 builder that receives the double-escaped text.</param>
    /// <param name="text">UTF-16 text to escape.</param>
    internal static void AppendJsonEscapedTwice(ref ValueUtf8Builder vub, scoped ReadOnlySpan<char> text)
    {
        int idx = text.IndexOfAny(JsonEscapeChars);
        while (idx >= 0)
        {
            vub.AppendUtf16Isolated(text[..idx]);
            AppendJsonEscapedTwiceAscii(ref vub, text[idx]);
            text = text[(idx + 1)..];
            idx = text.IndexOfAny(JsonEscapeChars);
        }

        vub.AppendUtf16Isolated(text);
    }

    /// <summary>
    /// Appends one Latin-1 character (from an ANSI string byte) with JSON escaping applied twice, as UTF-8.
    /// </summary>
    /// <param name="vub">UTF-8 builder that receives the output.</param>
    /// <param name="c">Character U+0000..U+00FF.</param>
    internal static void AppendJsonEscapedTwiceChar(ref ValueUtf8Builder vub, char c)
    {
        if (c < 0x80)
            AppendJsonEscapedTwiceAscii(ref vub, c);
        else
            AppendLatin1(ref vub, c);
    }

    /// <summary>
    /// Appends JSON-escaped UTF-8 text: escapes the ASCII characters JSON requires (backslash, double-quote,
    /// U+0000..U+001F) and copies every other byte. Used to escape output rendered into a temporary UTF-8 builder,
    /// whose unpaired surrogates are already U+FFFD, so the result matches escaping the UTF-16 rendering.
    /// </summary>
    /// <param name="vub">UTF-8 builder that receives the escaped text.</param>
    /// <param name="utf8">Rendered UTF-8 text.</param>
    internal static void AppendJsonEscapedUtf8(ref ValueUtf8Builder vub, scoped ReadOnlySpan<byte> utf8)
    {
        int idx = utf8.IndexOfAny(JsonEscapeBytes);
        while (idx >= 0)
        {
            vub.Append(utf8[..idx]);
            AppendJsonEscapeSequence(ref vub, (char)utf8[idx]);
            utf8 = utf8[(idx + 1)..];
            idx = utf8.IndexOfAny(JsonEscapeBytes);
        }

        vub.Append(utf8);
    }

    /// <summary>
    /// Appends a NUL-terminated ANSI string value with JSON escaping as UTF-8. Bytes map 1:1 to U+0000..U+00FF;
    /// rendering stops at the first NUL.
    /// </summary>
    /// <param name="vub">UTF-8 builder that receives the escaped text.</param>
    /// <param name="valueBytes">ANSI string bytes.</param>
    internal static void AppendAnsiJsonEscaped(ref ValueUtf8Builder vub, ReadOnlySpan<byte> valueBytes)
    {
        for (int i = 0; i < valueBytes.Length; i++)
        {
            byte b = valueBytes[i];
            if (b == 0) break;
            if ((b < 0x20) || (b == '"') || (b == '\\'))
                AppendJsonEscapeSequence(ref vub, (char)b);
            else if (b < 0x80)
                vub.Append(b);
            else
                AppendLatin1(ref vub, (char)b);
        }
    }

    /// <summary>
    /// Appends one Latin-1 character (U+0080..U+00FF, from an ANSI string byte) as its 2-byte UTF-8 sequence.
    /// </summary>
    /// <param name="vub">UTF-8 builder that receives the output.</param>
    /// <param name="c">Character U+0080..U+00FF.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void AppendLatin1(ref ValueUtf8Builder vub, char c)
    {
        Span<byte> d = vub.AppendSpan(2);
        d[1] = (byte)(0x80 | (c & 0x3F));
        d[0] = (byte)(0xC0 | (c >> 6));
    }

    /// <summary>
    /// Appends a SID (Security Identifier) in SDDL string form (e.g., S-1-5-18).
    /// SID binary layout: revision(1) + subAuthorityCount(1) + authority(6 big-endian) + subAuthorities(4 each LE).
    /// </summary>
    /// <param name="valueBytes">Raw SID bytes.</param>
    /// <param name="size">Byte size of the SID data.</param>
    /// <param name="vub">UTF-8 builder that receives the formatted SID.</param>
    internal static void AppendSid(ReadOnlySpan<byte> valueBytes, int size, ref ValueUtf8Builder vub)
    {
        byte revision = valueBytes[0];
        byte subCount = valueBytes[1];
        // 6-byte big-endian identifier authority
        long authority = 0;
        for (int i = 2; i < 8; i++)
            authority = authority * 256 + valueBytes[i];
        vub.Append("S-"u8);
        vub.AppendFormatted(revision);
        vub.Append((byte)'-');
        vub.AppendFormatted(authority);
        for (int i = 0; i < subCount; i++)
        {
            int subOff = 8 + i * 4;
            if (subOff + 4 > size) break;
            vub.Append((byte)'-');
            vub.AppendFormatted(MemoryMarshal.Read<uint>(valueBytes[subOff..]));
        }
    }

    /// <summary>
    /// Appends a SYSTEMTIME struct as an ISO 8601 UTC timestamp (yyyy-MM-ddTHH:mm:ss.mmmZ).
    /// SYSTEMTIME layout: year(2) + month(2) + dayOfWeek(2) + day(2) + hour(2) + minute(2) + second(2) + ms(2) = 16 bytes.
    /// </summary>
    /// <param name="valueBytes">16-byte SYSTEMTIME struct.</param>
    /// <param name="vub">UTF-8 builder that receives the formatted timestamp.</param>
    internal static void AppendSystemTime(ReadOnlySpan<byte> valueBytes, ref ValueUtf8Builder vub)
    {
        ushort yr = MemoryMarshal.Read<ushort>(valueBytes);
        ushort mo = MemoryMarshal.Read<ushort>(valueBytes[2..]);
        ushort dy = MemoryMarshal.Read<ushort>(valueBytes[6..]);
        ushort hr = MemoryMarshal.Read<ushort>(valueBytes[8..]);
        ushort mn = MemoryMarshal.Read<ushort>(valueBytes[10..]);
        ushort sc = MemoryMarshal.Read<ushort>(valueBytes[12..]);
        ushort ms = MemoryMarshal.Read<ushort>(valueBytes[14..]);
        // yyyy-MM-ddTHH:mm:ss.mmmZ = 24 bytes
        Span<byte> d = vub.AppendSpan(24);
        d[23] = (byte)'Z';
        WriteDigits4(d, 0, yr);
        d[4] = (byte)'-';
        WriteDigits2(d, 5, mo);
        d[7] = (byte)'-';
        WriteDigits2(d, 8, dy);
        d[10] = (byte)'T';
        WriteDigits2(d, 11, hr);
        d[13] = (byte)':';
        WriteDigits2(d, 14, mn);
        d[16] = (byte)':';
        WriteDigits2(d, 17, sc);
        d[19] = (byte)'.';
        // 3-digit milliseconds
        d[20] = (byte)('0' + ms / 100);
        d[21] = (byte)('0' + ms / 10 % 10);
        d[22] = (byte)('0' + ms % 10);
    }

    /// <summary>
    /// Appends text with XML entity escaping (&amp;, &lt;, &gt;, &quot;, &apos;) as UTF-8. Surrogate handling mirrors the
    /// UTF-16 version exactly: text with no entity characters has unpaired surrogates replaced with U+FFFD; otherwise
    /// the prefix and the runs between entities pass through unchanged and only the tail after the last entity is fixed.
    /// </summary>
    /// <param name="vub">UTF-8 builder that receives the escaped text.</param>
    /// <param name="text">UTF-16 text to escape.</param>
    internal static void AppendXmlEscaped(ref ValueUtf8Builder vub, scoped ReadOnlySpan<char> text)
    {
        int idx = text.IndexOfAny(XmlEscapeChars);
        if (idx < 0)
        {
            vub.AppendUtf16Isolated(text);
            return;
        }

        vub.AppendUtf16(text[..idx]);
        ReadOnlySpan<char> remaining = text[idx..];
        while (true)
        {
            vub.Append(GetXmlEntityUtf8(remaining[0]));
            remaining = remaining[1..];

            int next = remaining.IndexOfAny(XmlEscapeChars);
            if (next < 0)
            {
                vub.AppendUtf16Isolated(remaining);
                return;
            }

            vub.AppendUtf16(remaining[..next]);
            remaining = remaining[next..];
        }
    }

    /// <summary>
    /// Formats a 16-byte GUID in uppercase hex without braces: XXXXXXXX-XXXX-XXXX-XXXX-XXXXXXXXXXXX.
    /// First three components (Data1/Data2/Data3) are little-endian; last 8 bytes are in order.
    /// </summary>
    /// <param name="b">Span starting with the 16-byte raw GUID.</param>
    /// <param name="vub">UTF-8 builder that receives the formatted GUID.</param>
    internal static void FormatGuid(ReadOnlySpan<byte> b, ref ValueUtf8Builder vub)
    {
        // The BCL "D" layout uses the same byte order but lowercase; 8-4-4-4-12 = 36 bytes
        Span<byte> dst = vub.AppendSpan(36);
        new Guid(b[..16]).TryFormat(dst, out _, "D");
        Ascii.ToUpperInPlace(dst, out _);
    }

    /// <summary>
    /// Appends the RFC 8259 escape sequence for one character from <see cref="JsonEscapeChars"/>.
    /// </summary>
    /// <param name="vub">UTF-8 builder that receives the escape sequence.</param>
    /// <param name="c">Backslash, double-quote, or a control character U+0000..U+001F.</param>
    private static void AppendJsonEscapeSequence(ref ValueUtf8Builder vub, char c)
    {
        switch (c)
        {
            case '\\': vub.Append("\\\\"u8); break;
            case '"': vub.Append("\\\""u8); break;
            case '\n': vub.Append("\\n"u8); break;
            case '\r': vub.Append("\\r"u8); break;
            case '\t': vub.Append("\\t"u8); break;
            case '\b': vub.Append("\\b"u8); break;
            case '\f': vub.Append("\\f"u8); break;
            default:
                Span<byte> d = vub.AppendSpan(6);
                d[5] = UpperHexDigitsUtf8[c & 0xF];
                d[4] = UpperHexDigitsUtf8[c >> 4];
                d[0] = (byte)'\\';
                d[1] = (byte)'u';
                d[2] = (byte)'0';
                d[3] = (byte)'0';
                break;
        }
    }

    /// <summary>
    /// Appends one ASCII character with JSON escaping applied twice.
    /// Mapping: <c>\</c> → <c>\\\\</c>, <c>"</c> → <c>\\\"</c>, <c>\n</c> → <c>\\n</c> (likewise r/t/b/f),
    /// other U+0000..U+001F → <c>\\u00XX</c>; any other ASCII character is unchanged.
    /// </summary>
    /// <param name="vub">UTF-8 builder that receives the output.</param>
    /// <param name="c">ASCII character.</param>
    private static void AppendJsonEscapedTwiceAscii(ref ValueUtf8Builder vub, char c)
    {
        switch (c)
        {
            case '\\': vub.Append("\\\\\\\\"u8); break;
            case '"': vub.Append("\\\\\\\""u8); break;
            case '\n': vub.Append("\\\\n"u8); break;
            case '\r': vub.Append("\\\\r"u8); break;
            case '\t': vub.Append("\\\\t"u8); break;
            case '\b': vub.Append("\\\\b"u8); break;
            case '\f': vub.Append("\\\\f"u8); break;
            default:
                if (c < ' ')
                {
                    Span<byte> d = vub.AppendSpan(7);
                    d[6] = UpperHexDigitsUtf8[c & 0xF];
                    d[5] = UpperHexDigitsUtf8[c >> 4];
                    d[0] = (byte)'\\';
                    d[1] = (byte)'\\';
                    d[2] = (byte)'u';
                    d[3] = (byte)'0';
                    d[4] = (byte)'0';
                }
                else
                {
                    vub.Append((byte)c);
                }
                break;
        }
    }

    /// <summary>
    /// Returns the UTF-8 XML entity for a character from <see cref="XmlEscapeChars"/>.
    /// </summary>
    /// <param name="c">One of &amp; &lt; &gt; &quot; &apos;.</param>
    /// <returns>The entity bytes.</returns>
    private static ReadOnlySpan<byte> GetXmlEntityUtf8(char c) => c switch
    {
        '&' => "&amp;"u8,
        '<' => "&lt;"u8,
        '>' => "&gt;"u8,
        '"' => "&quot;"u8,
        _ => "&apos;"u8
    };

    /// <summary>
    /// Writes a 2-digit zero-padded number at <paramref name="at"/>.
    /// </summary>
    /// <param name="dst">Destination span.</param>
    /// <param name="at">Index of the first digit.</param>
    /// <param name="value">Value to write.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteDigits2(Span<byte> dst, int at, int value)
    {
        dst[at] = (byte)('0' + value / 10);
        dst[at + 1] = (byte)('0' + value % 10);
    }

    /// <summary>
    /// Writes a 4-digit zero-padded number at <paramref name="at"/>.
    /// </summary>
    /// <param name="dst">Destination span.</param>
    /// <param name="at">Index of the first digit.</param>
    /// <param name="value">Value to write.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteDigits4(Span<byte> dst, int at, int value)
    {
        dst[at] = (byte)('0' + value / 1000);
        dst[at + 1] = (byte)('0' + value / 100 % 10);
        dst[at + 2] = (byte)('0' + value / 10 % 10);
        dst[at + 3] = (byte)('0' + value % 10);
    }

    /// <summary>
    /// Writes a 7-digit zero-padded number (100-nanosecond ticks within a second, 0..9_999_999) at <paramref name="at"/>.
    /// </summary>
    /// <param name="dst">Destination span.</param>
    /// <param name="at">Index of the first digit.</param>
    /// <param name="value">Value to write.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteDigits7(Span<byte> dst, int at, int value)
    {
        dst[at] = (byte)('0' + value / 1000000);
        dst[at + 1] = (byte)('0' + value / 100000 % 10);
        dst[at + 2] = (byte)('0' + value / 10000 % 10);
        dst[at + 3] = (byte)('0' + value / 1000 % 10);
        dst[at + 4] = (byte)('0' + value / 100 % 10);
        dst[at + 5] = (byte)('0' + value / 10 % 10);
        dst[at + 6] = (byte)('0' + value % 10);
    }

    /// <summary>
    /// Appends a <see cref="uint"/> as 8-digit zero-padded lowercase hex (e.g., "0000002a").
    /// </summary>
    /// <param name="vub">UTF-8 builder that receives the hex output.</param>
    /// <param name="value">The value to format.</param>
    internal static void AppendHexUInt32Padded(ref ValueUtf8Builder vub, uint value)
    {
        WriteLowerHexDigits(vub.AppendSpan(8), value);
    }

    /// <summary>
    /// Appends a <see cref="ulong"/> as 16-digit zero-padded lowercase hex.
    /// </summary>
    /// <param name="vub">UTF-8 builder that receives the hex output.</param>
    /// <param name="value">The value to format.</param>
    internal static void AppendHexUInt64Padded(ref ValueUtf8Builder vub, ulong value)
    {
        WriteLowerHexDigits(vub.AppendSpan(16), value);
    }

    /// <summary>
    /// Appends a <see cref="uint"/> as minimal lowercase hex with no leading zeros; "0" for zero.
    /// </summary>
    /// <param name="vub">UTF-8 builder that receives the hex output.</param>
    /// <param name="value">The value to format.</param>
    internal static void AppendHexUInt32Min(ref ValueUtf8Builder vub, uint value)
    {
        // Significant nibbles = ceil(significantBits / 4); "| 1" makes zero format as a single "0"
        int digits = (35 - BitOperations.LeadingZeroCount(value | 1)) >> 2;
        WriteLowerHexDigits(vub.AppendSpan(digits), value);
    }

    /// <summary>
    /// Appends a <see cref="ulong"/> as minimal lowercase hex with no leading zeros; "0" for zero.
    /// </summary>
    /// <param name="vub">UTF-8 builder that receives the hex output.</param>
    /// <param name="value">The value to format.</param>
    internal static void AppendHexUInt64Min(ref ValueUtf8Builder vub, ulong value)
    {
        int digits = (67 - BitOperations.LeadingZeroCount(value | 1)) >> 2;
        WriteLowerHexDigits(vub.AppendSpan(digits), value);
    }

    /// <summary>
    /// Fills <paramref name="dst"/> with the low <c>dst.Length</c> nibbles of <paramref name="value"/> as lowercase hex,
    /// most significant first.
    /// </summary>
    /// <param name="dst">Destination span; its length is the number of hex digits written.</param>
    /// <param name="value">The value to format.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteLowerHexDigits(Span<byte> dst, ulong value)
    {
        ReadOnlySpan<byte> digits = LowerHexDigitsUtf8;
        for (int i = dst.Length - 1; i >= 0; i--)
        {
            dst[i] = digits[(int)value & 0xF];
            value >>= 4;
        }
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
    /// Returns a JSON-escaped copy of <paramref name="str"/> per RFC 8259, for template compilation where a heap
    /// string is needed. Text with no escape characters is returned unchanged; otherwise unpaired surrogates also
    /// become U+FFFD, matching the renderer's escaping.
    /// </summary>
    /// <param name="str">The string to escape.</param>
    /// <returns>The escaped string, or the original string if no escaping was needed.</returns>
    internal static string JsonEscapeString(string str)
    {
        if (str.AsSpan().IndexOfAny(JsonEscapeChars) < 0)
            return str;

        // Escaped output is valid UTF-16 (unpaired surrogates already replaced), so the UTF-8 round trip is lossless
        ValueUtf8Builder vub = new(stackalloc byte[256]);
        AppendJsonEscaped(ref vub, str.AsSpan());
        string result = Encoding.UTF8.GetString(vub.AsSpan());
        vub.Dispose();
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
    /// Used by template compilation, which needs heap strings.
    /// </summary>
    /// <param name="data">BinXml byte stream.</param>
    /// <param name="pos">Current read position; advanced past the 2-byte length prefix and string bytes.</param>
    /// <returns>The decoded string.</returns>
    internal static string ReadUnicodeTextStringAsString(ReadOnlySpan<byte> data, ref int pos)
    {
        return new string(ReadUnicodeTextString(data, ref pos));
    }

    /// <summary>
    /// Returns an XML-escaped copy of <paramref name="str"/>. Used during template compilation
    /// where a heap string is needed rather than span-based appending.
    /// </summary>
    /// <param name="str">The string to escape.</param>
    /// <returns>The escaped string, or the original string if no escaping was needed.</returns>
    internal static string XmlEscapeString(string str)
    {
        if (str.AsSpan().IndexOfAny(XmlEscapeChars) < 0)
            return str;
        return str.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
            .Replace("\"", "&quot;").Replace("'", "&apos;");
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
    /// Vectorised search set for XML characters needing entity escaping: &amp; &lt; &gt; &quot; &apos;.
    /// </summary>
    private static readonly SearchValues<char> XmlEscapeChars = SearchValues.Create("&<>\"'");

    /// <summary>
    /// Vectorised search set for characters JSON (RFC 8259) requires escaping inside a string:
    /// double-quote, backslash and the C0 control range U+0000..U+001F. Surrogates are handled by the transcoder.
    /// </summary>
    private static readonly SearchValues<char> JsonEscapeChars = SearchValues.Create(
        "\"\\\u0000\u0001\u0002\u0003\u0004\u0005\u0006\u0007\u0008\u0009\u000A\u000B\u000C\u000D\u000E\u000F" +
        "\u0010\u0011\u0012\u0013\u0014\u0015\u0016\u0017\u0018\u0019\u001A\u001B\u001C\u001D\u001E\u001F");


    /// <summary>
    /// Lowercase hex digits as UTF-8 bytes.
    /// </summary>
    private static ReadOnlySpan<byte> LowerHexDigitsUtf8 => "0123456789abcdef"u8;

    /// <summary>
    /// Uppercase hex digits as UTF-8 bytes (JSON \u00XX escapes use uppercase, matching <see cref="HexChars"/>).
    /// </summary>
    private static ReadOnlySpan<byte> UpperHexDigitsUtf8 => "0123456789ABCDEF"u8;

    /// <summary>
    /// Vectorised search set for the bytes JSON requires escaping: double-quote, backslash and 0x00..0x1F.
    /// None of these values occur inside a multi-byte UTF-8 sequence, so a byte scan is exact.
    /// </summary>
    private static readonly SearchValues<byte> JsonEscapeBytes = SearchValues.Create(
        "\"\\\u0000\u0001\u0002\u0003\u0004\u0005\u0006\u0007\u0008\u0009\u000A\u000B\u000C\u000D\u000E\u000F"u8 +
        "\u0010\u0011\u0012\u0013\u0014\u0015\u0016\u0017\u0018\u0019\u001A\u001B\u001C\u001D\u001E\u001F"u8);

    #endregion
}
