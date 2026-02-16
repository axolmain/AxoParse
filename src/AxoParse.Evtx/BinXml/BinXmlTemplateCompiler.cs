using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace AxoParse.Evtx.BinXml;

/// <summary>
/// Partial class containing template compilation methods.
/// Compiles BinXml template bodies into <see cref="CompiledTemplate"/> objects
/// with interleaved static XML string parts and substitution slot metadata.
/// </summary>
internal sealed partial class BinXmlParser
{
    #region Non-Public Methods

    /// <summary>
    /// Walks BinXml content tokens, appending static XML text to the last entry in <paramref name="parts"/>
    /// and recording substitution slots. Sets <paramref name="bail"/> to true if a nested template
    /// or unsupported token is encountered (compilation cannot proceed).
    /// </summary>
    /// <param name="data">BinXml byte stream (template body).</param>
    /// <param name="pos">Current read position; advanced past consumed tokens.</param>
    /// <param name="binxmlChunkBase">Chunk-relative base offset of <paramref name="data"/>.</param>
    /// <param name="parts">Accumulator for static XML string fragments.</param>
    /// <param name="subIds">Accumulator for substitution slot indices.</param>
    /// <param name="isOptional">Accumulator for whether each substitution is optional.</param>
    /// <param name="bail">Set to true if compilation must abort.</param>
    /// <param name="depth">Current recursion depth for stack overflow protection.</param>
    private void CompileContent(ReadOnlySpan<byte> data, ref int pos, int binxmlChunkBase,
                                List<string> parts, List<int> subIds, List<bool> isOptional, ref bool bail, int depth = 0)
    {
        while (pos < data.Length)
        {
            if (bail) return;
            byte tok = data[pos];
            byte baseTok = (byte)(tok & ~BinXmlToken.HasMoreDataFlag);

            if ((baseTok == BinXmlToken.Eof) ||
                (baseTok == BinXmlToken.CloseStartElement) ||
                (baseTok == BinXmlToken.CloseEmptyElement) ||
                (baseTok == BinXmlToken.EndElement) ||
                (baseTok == BinXmlToken.Attribute))
                break;

            switch (baseTok)
            {
                case BinXmlToken.OpenStartElement:
                    CompileElement(data, ref pos, binxmlChunkBase, parts, subIds, isOptional, ref bail, depth + 1);
                    break;

                case BinXmlToken.Value:
                    pos++; // token
                    pos++; // value type
                    string str = BinXmlValueFormatter.ReadUnicodeTextStringAsString(data, ref pos);
                    parts[^1] += BinXmlValueFormatter.XmlEscapeString(str);
                    break;

                case BinXmlToken.NormalSubstitution:
                case BinXmlToken.OptionalSubstitution:
                    pos++;
                    ushort subId = MemoryMarshal.Read<ushort>(data[pos..]);
                    pos += 2;
                    pos++; // subValType
                    subIds.Add(subId);
                    isOptional.Add(baseTok == BinXmlToken.OptionalSubstitution);
                    parts.Add(string.Empty);
                    break;

                case BinXmlToken.CharRef:
                    pos++;
                    ushort charVal = MemoryMarshal.Read<ushort>(data[pos..]);
                    pos += 2;
                    parts[^1] += $"&#{charVal};";
                    break;

                case BinXmlToken.EntityRef:
                    pos++;
                    uint nameOff = MemoryMarshal.Read<uint>(data[pos..]);
                    pos += 4;
                    string entityName = ReadName(nameOff);
                    parts[^1] += $"&{entityName};";
                    break;

                case BinXmlToken.CDataSection:
                    pos++;
                    string cdataStr = BinXmlValueFormatter.ReadUnicodeTextStringAsString(data, ref pos);
                    parts[^1] += $"<![CDATA[{cdataStr}]]>";
                    break;

                default:
                    bail = true;
                    return;
            }
        }
    }

    /// <summary>
    /// Compiles a single OpenStartElement and its children into static XML fragments.
    /// Appends opening/closing tags and attribute markup to <paramref name="parts"/>,
    /// recording substitution slots encountered in attributes and child content.
    /// </summary>
    /// <param name="data">BinXml byte stream (template body).</param>
    /// <param name="pos">Current read position; advanced past the entire element.</param>
    /// <param name="binxmlChunkBase">Chunk-relative base offset of <paramref name="data"/>.</param>
    /// <param name="parts">Accumulator for static XML string fragments.</param>
    /// <param name="subIds">Accumulator for substitution slot indices.</param>
    /// <param name="isOptional">Accumulator for whether each substitution is optional.</param>
    /// <param name="bail">Set to true if compilation must abort.</param>
    /// <param name="depth">Current recursion depth for stack overflow protection.</param>
    private void CompileElement(ReadOnlySpan<byte> data, ref int pos, int binxmlChunkBase,
                                List<string> parts, List<int> subIds, List<bool> isOptional, ref bool bail, int depth = 0)
    {
        if (depth >= _maxRecursionDepth)
        {
            bail = true;
            return;
        }

        byte tok = data[pos];
        bool hasAttrs = (tok & BinXmlToken.HasMoreDataFlag) != 0;
        pos++;

        pos += 2; // depId
        pos += 4; // dataSize
        uint nameOffset = MemoryMarshal.Read<uint>(data[pos..]);
        pos += 4;

        if (!TrySkipInlineName(data, ref pos, nameOffset, binxmlChunkBase))
        {
            bail = true;
            return;
        }

        string elemName = ReadName(nameOffset);
        parts[^1] += $"<{elemName}";

        if (hasAttrs)
        {
            uint attrListSize = MemoryMarshal.Read<uint>(data[pos..]);
            pos += 4;
            int attrEnd = pos + (int)attrListSize;

            while (pos < attrEnd)
            {
                if (bail) return;
                byte attrTok = data[pos];
                byte attrBase = (byte)(attrTok & ~BinXmlToken.HasMoreDataFlag);
                if (attrBase != BinXmlToken.Attribute) break;

                pos++;
                uint attrNameOff = MemoryMarshal.Read<uint>(data[pos..]);
                pos += 4;
                if (!TrySkipInlineName(data, ref pos, attrNameOff, binxmlChunkBase))
                {
                    bail = true;
                    return;
                }

                string attrName = ReadName(attrNameOff);
                parts[^1] += $" {attrName}=\"";
                CompileContent(data, ref pos, binxmlChunkBase, parts, subIds, isOptional, ref bail, depth + 1);
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
            CompileContent(data, ref pos, binxmlChunkBase, parts, subIds, isOptional, ref bail, depth + 1);
            if (bail) return;
            if ((pos < data.Length) && (data[pos] == BinXmlToken.EndElement))
                pos++;
            parts[^1] += $"</{elemName}>";
        }
        else
        {
            parts[^1] += "/>";
        }
    }

    /// <summary>
    /// Compiles a template body into a <see cref="CompiledTemplate"/> of interleaved string parts
    /// and substitution slot IDs. Returns null if the template contains nested templates or
    /// other constructs that prevent static compilation.
    /// </summary>
    /// <param name="defDataOffset">Chunk-relative offset of the template definition (before the 24-byte header).</param>
    /// <param name="dataSize">Size in bytes of the template body (after the 24-byte header).</param>
    /// <returns>A compiled template, or null if the template cannot be statically compiled.</returns>
    private CompiledTemplate? CompileTemplate(int defDataOffset, int dataSize)
    {
        int tplBodyFileOffset = _chunkFileOffset + defDataOffset + 24;
        if (tplBodyFileOffset + dataSize > _fileData.Length) return null;

        ReadOnlySpan<byte> tplBody = _fileData.AsSpan(tplBodyFileOffset, dataSize);
        int tplChunkBase = defDataOffset + 24;

        List<string> parts = new() { string.Empty };
        List<int> subIds = new();
        List<bool> isOptional = new();
        bool bail = false;

        int pos = 0;
        // Skip fragment header
        if ((tplBody.Length >= 4) && (tplBody[0] == BinXmlToken.FragmentHeader))
            pos += 4;

        CompileContent(tplBody, ref pos, tplChunkBase, parts, subIds, isOptional, ref bail);

        if (bail) return null;
        return new CompiledTemplate(parts.ToArray(), subIds.ToArray(), isOptional.ToArray());
    }

    #endregion
}