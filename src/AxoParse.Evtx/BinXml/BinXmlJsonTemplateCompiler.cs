using System.Runtime.InteropServices;

namespace AxoParse.Evtx.BinXml;

/// <summary>
/// Partial class containing JSON template compilation methods.
/// Compiles BinXml template bodies into <see cref="CompiledJsonTemplate"/> objects
/// with interleaved static JSON structural text and substitution slot metadata.
/// Mirrors the XML compiler (BinXmlTemplateCompiler.cs) but produces
/// the structural JSON format: {"#name":"...","#attrs":{...},"#content":[...]}.
/// </summary>
internal sealed partial class BinXmlParser
{
    #region Non-Public Methods

    /// <summary>
    /// Walks BinXml content tokens, appending static JSON text to the last entry in <paramref name="parts"/>
    /// and recording substitution slots. Sets <paramref name="bail"/> to true if a nested template
    /// or unsupported token is encountered (compilation cannot proceed).
    /// </summary>
    /// <param name="data">BinXml byte stream (template body).</param>
    /// <param name="pos">Current read position; advanced past consumed tokens.</param>
    /// <param name="binxmlChunkBase">Chunk-relative base offset of <paramref name="data"/>.</param>
    /// <param name="parts">Accumulator for static JSON string fragments.</param>
    /// <param name="subIds">Accumulator for substitution slot indices.</param>
    /// <param name="isOptional">Accumulator for whether each substitution is optional.</param>
    /// <param name="inAttrValue">Accumulator for whether each substitution is inside a JSON string literal.</param>
    /// <param name="bail">Set to true if compilation must abort.</param>
    /// <param name="depth">Current recursion depth for stack overflow protection.</param>
    /// <param name="insideAttrValue">True when compiling content inside an attribute value (JSON string literal context).</param>
    /// <param name="needsComma">Ref tracking whether a comma is needed before the next array item in #content.</param>
    private void CompileJsonContent(ReadOnlySpan<byte> data, ref int pos, int binxmlChunkBase,
                                    List<string> parts, List<int> subIds, List<bool> isOptional,
                                    List<bool> inAttrValue, ref bool bail, int depth,
                                    bool insideAttrValue, ref bool needsComma)
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
                    if (!insideAttrValue)
                    {
                        if (needsComma) parts[^1] += ",";
                        needsComma = true;
                    }
                    CompileJsonElement(data, ref pos, binxmlChunkBase, parts, subIds, isOptional, inAttrValue, ref bail, depth + 1);
                    break;

                case BinXmlToken.Value:
                    pos++; // token
                    pos++; // value type
                    string str = BinXmlValueFormatter.ReadUnicodeTextStringAsString(data, ref pos);
                    if (insideAttrValue)
                    {
                        parts[^1] += BinXmlValueFormatter.JsonEscapeString(str);
                    }
                    else
                    {
                        if (needsComma) parts[^1] += ",";
                        needsComma = true;
                        parts[^1] += "\"" + BinXmlValueFormatter.JsonEscapeString(str) + "\"";
                    }
                    break;

                case BinXmlToken.NormalSubstitution:
                case BinXmlToken.OptionalSubstitution:
                    pos++;
                    ushort subId = MemoryMarshal.Read<ushort>(data[pos..]);
                    pos += 2;
                    pos++; // subValType
                    if (!insideAttrValue)
                    {
                        if (needsComma) parts[^1] += ",";
                        needsComma = true;
                        parts[^1] += "\"";
                    }
                    subIds.Add(subId);
                    isOptional.Add(baseTok == BinXmlToken.OptionalSubstitution);
                    inAttrValue.Add(insideAttrValue);
                    if (!insideAttrValue)
                    {
                        parts.Add("\"");
                    }
                    else
                    {
                        parts.Add(string.Empty);
                    }
                    break;

                case BinXmlToken.CharRef:
                {
                    pos++;
                    ushort charVal = MemoryMarshal.Read<ushort>(data[pos..]);
                    pos += 2;
                    char ch = (char)charVal;
                    string escaped = BinXmlValueFormatter.JsonEscapeString(ch.ToString());
                    if (insideAttrValue)
                    {
                        parts[^1] += escaped;
                    }
                    else
                    {
                        if (needsComma) parts[^1] += ",";
                        needsComma = true;
                        parts[^1] += "\"" + escaped + "\"";
                    }
                    break;
                }

                case BinXmlToken.EntityRef:
                {
                    pos++;
                    uint nameOff = MemoryMarshal.Read<uint>(data[pos..]);
                    pos += 4;
                    string entityName = ReadName(nameOff);
                    string resolved = entityName switch
                    {
                        "amp" => "&",
                        "lt" => "<",
                        "gt" => ">",
                        "quot" => "\"",
                        "apos" => "'",
                        _ => $"&{entityName};"
                    };
                    string escapedEntity = BinXmlValueFormatter.JsonEscapeString(resolved);
                    if (insideAttrValue)
                    {
                        parts[^1] += escapedEntity;
                    }
                    else
                    {
                        if (needsComma) parts[^1] += ",";
                        needsComma = true;
                        parts[^1] += "\"" + escapedEntity + "\"";
                    }
                    break;
                }

                case BinXmlToken.CDataSection:
                {
                    pos++;
                    string cdataStr = BinXmlValueFormatter.ReadUnicodeTextStringAsString(data, ref pos);
                    string escapedCdata = BinXmlValueFormatter.JsonEscapeString(cdataStr);
                    if (insideAttrValue)
                    {
                        parts[^1] += escapedCdata;
                    }
                    else
                    {
                        if (needsComma) parts[^1] += ",";
                        needsComma = true;
                        parts[^1] += "\"" + escapedCdata + "\"";
                    }
                    break;
                }

                case BinXmlToken.TemplateInstance:
                    // Nested template instances cannot be compiled — bail
                    bail = true;
                    return;

                default:
                    bail = true;
                    return;
            }
        }
    }

    /// <summary>
    /// Compiles a single OpenStartElement and its children into static JSON fragments.
    /// Produces the structural format: {"#name":"elemName","#attrs":{...},"#content":[...]}.
    /// </summary>
    /// <param name="data">BinXml byte stream (template body).</param>
    /// <param name="pos">Current read position; advanced past the entire element.</param>
    /// <param name="binxmlChunkBase">Chunk-relative base offset of <paramref name="data"/>.</param>
    /// <param name="parts">Accumulator for static JSON string fragments.</param>
    /// <param name="subIds">Accumulator for substitution slot indices.</param>
    /// <param name="isOptional">Accumulator for whether each substitution is optional.</param>
    /// <param name="inAttrValue">Accumulator for whether each substitution is inside a JSON string literal.</param>
    /// <param name="bail">Set to true if compilation must abort.</param>
    /// <param name="depth">Current recursion depth for stack overflow protection.</param>
    private void CompileJsonElement(ReadOnlySpan<byte> data, ref int pos, int binxmlChunkBase,
                                    List<string> parts, List<int> subIds, List<bool> isOptional,
                                    List<bool> inAttrValue, ref bool bail, int depth = 0)
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

        string elemName = BinXmlValueFormatter.JsonEscapeString(ReadName(nameOffset));
        parts[^1] += "{\"#name\":\"" + elemName + "\"";

        // Compile attributes
        if (hasAttrs)
        {
            uint attrListSize = MemoryMarshal.Read<uint>(data[pos..]);
            pos += 4;
            int attrEnd = pos + (int)attrListSize;

            parts[^1] += ",\"#attrs\":{";
            bool firstAttr = true;

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

                string attrName = BinXmlValueFormatter.JsonEscapeString(ReadName(attrNameOff));
                if (!firstAttr) parts[^1] += ",";
                firstAttr = false;
                parts[^1] += "\"" + attrName + "\":\"";

                // Compile attribute value content in attr-value context (inside JSON string literal)
                bool attrNeedsComma = false;
                CompileJsonContent(data, ref pos, binxmlChunkBase, parts, subIds, isOptional, inAttrValue, ref bail, depth + 1,
                    insideAttrValue: true, needsComma: ref attrNeedsComma);
                if (bail) return;
                parts[^1] += "\"";
            }

            parts[^1] += "}";
        }

        // Close token
        if (pos >= data.Length)
        {
            parts[^1] += "}";
            return;
        }

        byte closeTok = data[pos];
        if (closeTok == BinXmlToken.CloseEmptyElement)
        {
            pos++;
            parts[^1] += "}";
        }
        else if (closeTok == BinXmlToken.CloseStartElement)
        {
            pos++;
            parts[^1] += ",\"#content\":[";

            // Compile child content as array items
            bool contentNeedsComma = false;
            CompileJsonContent(data, ref pos, binxmlChunkBase, parts, subIds, isOptional, inAttrValue, ref bail, depth + 1,
                insideAttrValue: false, needsComma: ref contentNeedsComma);
            if (bail) return;

            if ((pos < data.Length) && (data[pos] == BinXmlToken.EndElement))
                pos++;
            parts[^1] += "]}";
        }
        else
        {
            parts[^1] += "}";
        }
    }

    /// <summary>
    /// Compiles a template body into a <see cref="CompiledJsonTemplate"/> of interleaved JSON string parts
    /// and substitution slot IDs. Returns null if the template contains nested templates or
    /// other constructs that prevent static compilation.
    /// </summary>
    /// <param name="defDataOffset">Chunk-relative offset of the template definition (before the 24-byte header).</param>
    /// <param name="dataSize">Size in bytes of the template body (after the 24-byte header).</param>
    /// <returns>A compiled JSON template, or null if the template cannot be statically compiled.</returns>
    private CompiledJsonTemplate? CompileJsonTemplate(int defDataOffset, int dataSize)
    {
        int tplBodyFileOffset = _chunkFileOffset + defDataOffset + 24;
        if (tplBodyFileOffset + dataSize > _fileData.Length) return null;

        ReadOnlySpan<byte> tplBody = _fileData.AsSpan(tplBodyFileOffset, dataSize);
        int tplChunkBase = defDataOffset + 24;

        List<string> parts = new() { string.Empty };
        List<int> subIds = new();
        List<bool> isOptional = new();
        List<bool> inAttrValueList = new();
        bool bail = false;

        int pos = 0;
        // Skip fragment header
        if ((tplBody.Length >= 4) && (tplBody[0] == BinXmlToken.FragmentHeader))
            pos += 4;

        bool needsComma = false;
        CompileJsonContent(tplBody, ref pos, tplChunkBase, parts, subIds, isOptional, inAttrValueList, ref bail, 0,
            insideAttrValue: false, needsComma: ref needsComma);

        if (bail) return null;
        return new CompiledJsonTemplate(parts.ToArray(), subIds.ToArray(), isOptional.ToArray(), inAttrValueList.ToArray());
    }

    #endregion
}