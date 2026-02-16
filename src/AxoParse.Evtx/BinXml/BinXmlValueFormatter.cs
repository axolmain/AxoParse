using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace AxoParse.Evtx;

/// <summary>
/// Shared static helpers for BinXml value formatting: hex conversion, XML escaping,
/// GUID/SID/timestamp rendering, and element size lookup. Used by both XML and JSON
/// rendering paths to avoid duplicating format-specific logic.
/// </summary>
internal static class BinXmlValueFormatter
{
    /// <summary>
    /// Windows FILETIME epoch (1601-01-01) to .NET DateTime epoch (0001-01-01) delta in 100-ns ticks.
    /// FILETIME stores ticks since 1601-01-01; adding this constant converts to DateTime ticks.
    /// </summary>
    internal const long FileTimeEpochDelta = 504911232000000000L;

    /// <summary>
    /// Pre-computed lookup table mapping byte values 0x00..0xFF to two-character uppercase hex strings.
    /// </summary>
    internal static readonly string[] HexLookup = InitHexLookup();

    /// <summary>
    /// Builds a 256-entry lookup table for fast byte-to-hex conversion.
    /// </summary>
    /// <returns>Array where index <c>i</c> contains the uppercase two-character hex string for byte <c>i</c>.</returns>
    private static string[] InitHexLookup()
    {
        string[] table = new string[256];
        for (int i = 0; i < 256; i++)
            table[i] = i.ToString("X2");
        return table;
    }

    /// <summary>
    /// Appends each byte as a two-character uppercase hex string using the precomputed <see cref="HexLookup"/> table.
    /// </summary>
    /// <param name="vsb">String builder that receives the hex output.</param>
    /// <param name="data">Bytes to convert.</param>
    internal static void AppendHex(ref ValueStringBuilder vsb, ReadOnlySpan<byte> data)
    {
        for (int i = 0; i < data.Length; i++)
            vsb.Append(HexLookup[data[i]]);
    }

    /// <summary>
    /// Formats a 16-byte GUID in braced lowercase hex format: {xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx}.
    /// First three components (Data1/Data2/Data3) are little-endian; last 8 bytes are big-endian.
    /// </summary>
    /// <param name="b">16-byte span containing the raw GUID.</param>
    /// <param name="vsb">String builder that receives the formatted GUID.</param>
    internal static void FormatGuid(ReadOnlySpan<byte> b, ref ValueStringBuilder vsb)
    {
        uint d1 = MemoryMarshal.Read<uint>(b);
        ushort d2 = MemoryMarshal.Read<ushort>(b[4..]);
        ushort d3 = MemoryMarshal.Read<ushort>(b[6..]);

        vsb.Append('{');
        vsb.AppendFormatted(d1, "X8");
        vsb.Append('-');
        vsb.AppendFormatted(d2, "X4");
        vsb.Append('-');
        vsb.AppendFormatted(d3, "X4");
        vsb.Append('-');
        vsb.Append(HexLookup[b[8]]);
        vsb.Append(HexLookup[b[9]]);
        vsb.Append(HexLookup[b[10]]);
        vsb.Append(HexLookup[b[11]]);
        vsb.Append('-');
        vsb.Append(HexLookup[b[12]]);
        vsb.Append(HexLookup[b[13]]);
        vsb.Append(HexLookup[b[14]]);
        vsb.Append(HexLookup[b[15]]);
        vsb.Append('}');
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
    /// Appends text to the string builder with XML entity escaping (&amp;, &lt;, &gt;, &quot;, &apos;).
    /// Uses a fast path for text containing no special characters. Replaces unpaired surrogates with U+FFFD.
    /// </summary>
    /// <param name="vsb">String builder that receives the escaped text.</param>
    /// <param name="text">Source character span to escape and append.</param>
    internal static void AppendXmlEscaped(ref ValueStringBuilder vsb, scoped ReadOnlySpan<char> text)
    {
        // Fast path: no XML-special chars and no surrogates → bulk append
        if (text.IndexOfAny('&', '<', '>') < 0 &&
            text.IndexOfAny('"', '\'') < 0 &&
            text.IndexOfAnyInRange('\uD800', '\uDFFF') < 0)
        {
            vsb.Append(text);
            return;
        }

        // Slow path: XML-escape + replace unpaired surrogates with U+FFFD
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (char.IsHighSurrogate(c))
            {
                if (i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
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
                    case '&': vsb.Append("&amp;"); break;
                    case '<': vsb.Append("&lt;"); break;
                    case '>': vsb.Append("&gt;"); break;
                    case '"': vsb.Append("&quot;"); break;
                    case '\'': vsb.Append("&apos;"); break;
                    default: vsb.Append(c); break;
                }
            }
        }
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
        if (span.IndexOfAny('&', '<', '>') < 0 && span.IndexOfAny('"', '\'') < 0)
            return str;
        return str.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
            .Replace("\"", "&quot;").Replace("'", "&apos;");
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
    /// Appends a FILETIME value as an ISO 8601 UTC timestamp (yyyy-MM-ddTHH:mm:ss.fffffffZ).
    /// Returns false if the FILETIME is zero (caller decides how to handle empty).
    /// </summary>
    /// <param name="valueBytes">8-byte FILETIME value (little-endian 100-ns ticks since 1601-01-01).</param>
    /// <param name="vsb">String builder that receives the formatted timestamp.</param>
    /// <returns>True if a timestamp was appended; false if the FILETIME was zero.</returns>
    internal static bool AppendFileTime(ReadOnlySpan<byte> valueBytes, ref ValueStringBuilder vsb)
    {
        long ticks = MemoryMarshal.Read<long>(valueBytes);
        if (ticks == 0) return false;
        DateTime dt = new DateTime(ticks + FileTimeEpochDelta, DateTimeKind.Utc);
        vsb.AppendFormatted(dt, "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'");
        return true;
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
        vsb.AppendFormatted(yr, "D4");
        vsb.Append('-');
        vsb.AppendFormatted(mo, "D2");
        vsb.Append('-');
        vsb.AppendFormatted(dy, "D2");
        vsb.Append('T');
        vsb.AppendFormatted(hr, "D2");
        vsb.Append(':');
        vsb.AppendFormatted(mn, "D2");
        vsb.Append(':');
        vsb.AppendFormatted(sc, "D2");
        vsb.Append('.');
        vsb.AppendFormatted(ms, "D3");
        vsb.Append('Z');
    }
}
