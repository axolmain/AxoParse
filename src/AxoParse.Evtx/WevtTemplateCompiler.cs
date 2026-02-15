using System.Runtime.InteropServices;

namespace AxoParse.Evtx;

/// <summary>
/// Compiles WEVT BinXML fragments into <see cref="CompiledTemplate"/> objects.
/// WEVT templates use a different name encoding than EVTX chunks:
/// no 4-byte nameOffset field, all names are inline at the current position.
/// WEVT inline name layout: hash(2) + numChars(2) + numChars*2 UTF-16LE + nul(2) = 6 + numChars*2 bytes.
/// </summary>
internal static class WevtTemplateCompiler
{
    /// <summary>
    /// Maximum nesting depth for recursive element parsing to prevent stack overflow on crafted input.
    /// </summary>
    private const int MaxRecursionDepth = 64;

    /// <summary>
    /// Compiles a WEVT BinXML fragment into a <see cref="CompiledTemplate"/>.
    /// Handles the WEVT-specific name encoding (no 4-byte nameOffset, all names inline).
    /// </summary>
    /// <param name="binXmlData">Raw BinXML bytes from a TEMP entry (starting at offset 40).</param>
    /// <returns>Compiled template, or null if the fragment contains unsupported tokens or is malformed.</returns>
    internal static CompiledTemplate? Compile(ReadOnlySpan<byte> binXmlData)
    {
        if (binXmlData.Length < 4)
            return null;

        List<string> parts = new() { string.Empty };
        List<int> subIds = new();
        List<bool> isOptional = new();
        bool bail = false;

        int pos = 0;

        // Skip fragment header token (0x0F + 3 bytes)
        if (binXmlData[0] == BinXmlToken.FragmentHeader)
            pos += 4;

        CompileContentWevt(binXmlData, ref pos, parts, subIds, isOptional, ref bail);

        if (bail)
            return null;

        return new CompiledTemplate(parts.ToArray(), subIds.ToArray(), isOptional.ToArray());
    }

    /// <summary>
    /// Reads a WEVT inline name structure at the current position.
    /// Layout: hash(2) + numChars(2) + numChars*2 UTF-16LE bytes + nul(2) = 6 + numChars*2 bytes.
    /// </summary>
    /// <param name="data">BinXml byte stream.</param>
    /// <param name="pos">Current read position; advanced past the inline name.</param>
    /// <returns>The name string, or null if bounds check fails.</returns>
    private static string? ReadWevtInlineName(ReadOnlySpan<byte> data, ref int pos)
    {
        // hash(2) + numChars(2) = minimum 4 bytes before the string
        if (pos + 4 > data.Length)
            return null;

        pos += 2; // skip hash
        ushort numChars = MemoryMarshal.Read<ushort>(data.Slice(pos));
        pos += 2;

        int stringBytes = numChars * 2;
        // string bytes + 2 byte nul terminator
        if (pos + stringBytes + 2 > data.Length)
            return null;

        ReadOnlySpan<char> chars = MemoryMarshal.Cast<byte, char>(data.Slice(pos, stringBytes));
        pos += stringBytes;
        pos += 2; // nul terminator

        return new string(chars);
    }

    /// <summary>
    /// Walks WEVT BinXml content tokens, appending static XML text to the last entry in
    /// <paramref name="parts"/> and recording substitution slots. Mirrors
    /// <see cref="BinXmlParser"/>.CompileContent but uses WEVT inline name encoding.
    /// </summary>
    /// <param name="data">BinXml byte stream (WEVT template body).</param>
    /// <param name="pos">Current read position; advanced past consumed tokens.</param>
    /// <param name="parts">Accumulator for static XML string fragments.</param>
    /// <param name="subIds">Accumulator for substitution slot indices.</param>
    /// <param name="isOptional">Accumulator for whether each substitution is optional.</param>
    /// <param name="bail">Set to true if compilation must abort.</param>
    /// <param name="depth">Current recursion depth for stack overflow protection.</param>
    private static void CompileContentWevt(ReadOnlySpan<byte> data, ref int pos,
                                           List<string> parts, List<int> subIds, List<bool> isOptional, ref bool bail, int depth = 0)
    {
        while (pos < data.Length)
        {
            if (bail) return;
            byte tok = data[pos];
            byte baseTok = (byte)(tok & ~BinXmlToken.HasMoreDataFlag);

            if (baseTok == BinXmlToken.Eof ||
                baseTok == BinXmlToken.CloseStartElement ||
                baseTok == BinXmlToken.CloseEmptyElement ||
                baseTok == BinXmlToken.EndElement ||
                baseTok == BinXmlToken.Attribute)
                break;

            switch (baseTok)
            {
                case BinXmlToken.OpenStartElement:
                    CompileElementWevt(data, ref pos, parts, subIds, isOptional, ref bail, depth + 1);
                    break;

                case BinXmlToken.Value:
                    pos++; // token
                    pos++; // value type
                    string str = BinXmlParser.ReadUnicodeTextStringAsString(data, ref pos);
                    parts[^1] += BinXmlParser.XmlEscapeString(str);
                    break;

                case BinXmlToken.NormalSubstitution:
                case BinXmlToken.OptionalSubstitution:
                    pos++;
                    ushort subId = MemoryMarshal.Read<ushort>(data.Slice(pos));
                    pos += 2;
                    pos++; // subValType
                    subIds.Add(subId);
                    isOptional.Add(baseTok == BinXmlToken.OptionalSubstitution);
                    parts.Add(string.Empty);
                    break;

                case BinXmlToken.CharRef:
                    pos++;
                    ushort charVal = MemoryMarshal.Read<ushort>(data.Slice(pos));
                    pos += 2;
                    parts[^1] += $"&#{charVal};";
                    break;

                case BinXmlToken.EntityRef:
                    pos++;
                    // WEVT: no nameOffset — read inline name directly
                    string? entityName = ReadWevtInlineName(data, ref pos);
                    if (entityName is null)
                    {
                        bail = true;
                        return;
                    }
                    parts[^1] += $"&{entityName};";
                    break;

                case BinXmlToken.CDataSection:
                    pos++;
                    string cdataStr = BinXmlParser.ReadUnicodeTextStringAsString(data, ref pos);
                    parts[^1] += $"<![CDATA[{cdataStr}]]>";
                    break;

                default:
                    bail = true;
                    return;
            }
        }
    }

    /// <summary>
    /// Compiles a single WEVT OpenStartElement and its children into static XML fragments.
    /// Mirrors <see cref="BinXmlParser"/>.CompileElement but reads names inline (no nameOffset).
    /// WEVT OpenStartElement layout: token(1) + depId(2) + dataSize(4) + inline name (NO nameOffset).
    /// WEVT Attribute layout: token(1) + inline name (NO nameOffset).
    /// </summary>
    /// <param name="data">BinXml byte stream (WEVT template body).</param>
    /// <param name="pos">Current read position; advanced past the entire element.</param>
    /// <param name="parts">Accumulator for static XML string fragments.</param>
    /// <param name="subIds">Accumulator for substitution slot indices.</param>
    /// <param name="isOptional">Accumulator for whether each substitution is optional.</param>
    /// <param name="bail">Set to true if compilation must abort.</param>
    /// <param name="depth">Current recursion depth for stack overflow protection.</param>
    private static void CompileElementWevt(ReadOnlySpan<byte> data, ref int pos,
                                           List<string> parts, List<int> subIds, List<bool> isOptional, ref bool bail, int depth = 0)
    {
        if (depth >= MaxRecursionDepth)
        {
            bail = true;
            return;
        }

        byte tok = data[pos];
        bool hasAttrs = (tok & BinXmlToken.HasMoreDataFlag) != 0;
        pos++;

        pos += 2; // depId
        pos += 4; // dataSize

        // WEVT: no nameOffset field — read inline name directly
        string? elemName = ReadWevtInlineName(data, ref pos);
        if (elemName is null)
        {
            bail = true;
            return;
        }

        parts[^1] += $"<{elemName}";

        if (hasAttrs)
        {
            if (pos + 4 > data.Length)
            {
                bail = true;
                return;
            }
            uint attrListSize = MemoryMarshal.Read<uint>(data.Slice(pos));
            pos += 4;
            int attrEnd = pos + (int)attrListSize;

            while (pos < attrEnd)
            {
                if (bail) return;
                byte attrTok = data[pos];
                byte attrBase = (byte)(attrTok & ~BinXmlToken.HasMoreDataFlag);
                if (attrBase != BinXmlToken.Attribute) break;

                pos++;

                // WEVT: no nameOffset field — read inline name directly
                string? attrName = ReadWevtInlineName(data, ref pos);
                if (attrName is null)
                {
                    bail = true;
                    return;
                }

                parts[^1] += $" {attrName}=\"";
                CompileContentWevt(data, ref pos, parts, subIds, isOptional, ref bail, depth + 1);
                if (bail) return;
                parts[^1] += "\"";
            }
        }

        if (pos >= data.Length)
        {
            parts[^1] += "/>";
            return;
        }

        byte closeTok = data[pos];
        if (closeTok == BinXmlToken.CloseEmptyElement)
        {
            pos++;
            parts[^1] += "/>";
        }
        else if (closeTok == BinXmlToken.CloseStartElement)
        {
            pos++;
            parts[^1] += ">";
            CompileContentWevt(data, ref pos, parts, subIds, isOptional, ref bail, depth + 1);
            if (bail) return;
            if (pos < data.Length && data[pos] == BinXmlToken.EndElement)
                pos++;
            parts[^1] += $"</{elemName}>";
        }
        else
        {
            parts[^1] += "/>";
        }
    }
}