using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace AxoParse.Evtx;

/// <summary>
/// Partial class containing JSON writing methods.
/// Converts BinXml token streams to structured UTF-8 JSON output with typed values,
/// EventData/UserData flattening, and duplicate name suffixing.
/// </summary>
internal sealed partial class BinXmlParser
{
    /// <summary>
    /// Shared JSON writer options with validation skipped for performance.
    /// </summary>
    private static readonly JsonWriterOptions JsonOpts = new() { SkipValidation = true };

    /// <summary>
    /// Parses a single record's BinXml event data into UTF-8 JSON bytes.
    /// </summary>
    public byte[] ParseRecordJson(EvtxRecord record)
    {
        ReadOnlySpan<byte> eventData = record.GetEventData(_fileData);
        int binxmlChunkBase = record.EventDataFileOffset - _chunkFileOffset;

        ArrayBufferWriter<byte> buffer = new(512);
        using Utf8JsonWriter w = new(buffer, JsonOpts);
        int pos = 0;
        ParseDocumentJson(eventData, ref pos, binxmlChunkBase, w);
        w.Flush();
        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>
    /// JSON variant of <see cref="ParseDocument"/>. Consumes fragment headers and skips
    /// processing instructions (no JSON equivalent), then dispatches to fragment parsing.
    /// </summary>
    /// <param name="data">BinXml byte stream.</param>
    /// <param name="pos">Current read position; advanced past consumed tokens.</param>
    /// <param name="binxmlChunkBase">Chunk-relative base offset of <paramref name="data"/>.</param>
    /// <param name="w">UTF-8 JSON writer that receives the output.</param>
    private void ParseDocumentJson(ReadOnlySpan<byte> data, ref int pos, int binxmlChunkBase, Utf8JsonWriter w)
    {
        while (pos < data.Length)
        {
            byte tok = data[pos];
            byte baseTok = (byte)(tok & ~BinXmlToken.HasMoreDataFlag);

            if (baseTok == BinXmlToken.Eof)
                break;

            if (baseTok == BinXmlToken.FragmentHeader)
            {
                ParseFragmentJson(data, ref pos, binxmlChunkBase, w);
            }
            else if (baseTok == BinXmlToken.PiTarget)
            {
                // Skip PI for JSON — no equivalent
                pos++;
                pos += 4; // nameOffset
                if (pos < data.Length && data[pos] == BinXmlToken.PiData)
                {
                    pos++;
                    ushort numChars = MemoryMarshal.Read<ushort>(data[pos..]);
                    pos += 2 + numChars * 2;
                }
            }
            else
            {
                break;
            }
        }
    }

    /// <summary>
    /// JSON variant of <see cref="ParseFragment"/>. Skips the 4-byte fragment header, then
    /// dispatches to template instance or bare element JSON rendering.
    /// </summary>
    /// <param name="data">BinXml byte stream.</param>
    /// <param name="pos">Current read position; advanced past the fragment.</param>
    /// <param name="binxmlChunkBase">Chunk-relative base offset of <paramref name="data"/>.</param>
    /// <param name="w">UTF-8 JSON writer that receives the output.</param>
    private void ParseFragmentJson(ReadOnlySpan<byte> data, ref int pos, int binxmlChunkBase, Utf8JsonWriter w)
    {
        if (pos + 4 > data.Length) return;
        pos += 4; // fragment header

        if (pos >= data.Length) return;

        byte nextTok = data[pos];
        byte nextBase = (byte)(nextTok & ~BinXmlToken.HasMoreDataFlag);

        if (nextBase == BinXmlToken.TemplateInstance)
            ParseTemplateInstanceJson(data, ref pos, binxmlChunkBase, w);
        else if (nextBase == BinXmlToken.OpenStartElement)
            WriteElementJson(data, ref pos, null, null, null, binxmlChunkBase, w);
    }

    /// <summary>
    /// JSON variant of <see cref="ParseTemplateInstance"/>. Uses <see cref="ReadTemplateInstanceData"/>
    /// for shared data extraction, then walks the template body producing JSON output.
    /// Unlike the XML path, this always does a full tree walk (no compiled template shortcut).
    /// </summary>
    /// <param name="data">BinXml byte stream.</param>
    /// <param name="pos">Current read position; advanced past the entire template instance.</param>
    /// <param name="binxmlChunkBase">Chunk-relative base offset of <paramref name="data"/>.</param>
    /// <param name="w">UTF-8 JSON writer that receives the output.</param>
    private void ParseTemplateInstanceJson(
        ReadOnlySpan<byte> data,
        ref int pos,
        int binxmlChunkBase,
        Utf8JsonWriter w)
    {
        TemplateInstanceData tid =
            ReadTemplateInstanceData(data, ref pos, binxmlChunkBase);

        if (tid.DataSize == 0)
            return;

        int tplBodyFileOffset =
            _chunkFileOffset + (int)tid.DefDataOffset + 24;

        if ((uint)(tplBodyFileOffset + tid.DataSize) > (uint)_fileData.Length)
            return;

        ReadOnlySpan<byte> tplBody =
            _fileData.AsSpan(tplBodyFileOffset, (int)tid.DataSize);

        int tplPos = 0;
        int tplChunkBase = (int)tid.DefDataOffset + 24;

        if (tplBody.Length >= 4 &&
            tplBody[0] == BinXmlToken.FragmentHeader)
        {
            tplPos += 4;
        }

        ParseContentJson(
            tplBody,
            ref tplPos,
            tid.ValueOffsets,
            tid.ValueSizes,
            tid.ValueTypes,
            tplChunkBase,
            w);
    }


    /// <summary>
    /// JSON variant of <see cref="ParseContent"/>. Walks content tokens and writes JSON values
    /// for text, substitutions, character/entity references, and CDATA. Dispatches child elements
    /// and nested templates to their respective JSON renderers.
    /// </summary>
    /// <param name="data">BinXml byte stream.</param>
    /// <param name="pos">Current read position; advanced past consumed content tokens.</param>
    /// <param name="valueOffsets">File offsets of substitution values, or null if no template context.</param>
    /// <param name="valueSizes">Byte sizes of substitution values.</param>
    /// <param name="valueTypes">BinXml value type codes for each substitution.</param>
    /// <param name="binxmlChunkBase">Chunk-relative base offset of <paramref name="data"/>.</param>
    /// <param name="w">UTF-8 JSON writer that receives the output.</param>
    /// <param name="depth">Current recursion depth for stack overflow protection.</param>
    private void ParseContentJson(
    ReadOnlySpan<byte> data,
    ref int pos,
    int[]? valueOffsets,
    int[]? valueSizes,
    byte[]? valueTypes,
    int binxmlChunkBase,
    Utf8JsonWriter w,
    int depth = 0)
{
    int length = data.Length;

    while (pos < length)
    {
        byte tok = data[pos];
        byte baseTok = (byte)(tok & ~BinXmlToken.HasMoreDataFlag);

        // Break tokens
        if (baseTok == BinXmlToken.Eof ||
            baseTok == BinXmlToken.CloseStartElement ||
            baseTok == BinXmlToken.CloseEmptyElement ||
            baseTok == BinXmlToken.EndElement ||
            baseTok == BinXmlToken.Attribute)
        {
            break;
        }

        switch (baseTok)
        {
            case BinXmlToken.OpenStartElement:
                WriteElementJson(
                    data, ref pos,
                    valueOffsets, valueSizes, valueTypes,
                    binxmlChunkBase, w, depth + 1);
                break;

            case BinXmlToken.Value:
            {
                pos += 2; // token + value type
                ReadOnlySpan<char> chars =
                    BinXmlValueFormatter.ReadUnicodeTextString(data, ref pos);
                w.WriteStringValue(chars);
                break;
            }

            case BinXmlToken.NormalSubstitution:
            case BinXmlToken.OptionalSubstitution:
            {
                bool optional =
                    baseTok == BinXmlToken.OptionalSubstitution;

                pos++;

                ushort subId =
                    BinaryPrimitives.ReadUInt16LittleEndian(
                        data.Slice(pos, 2));
                pos += 2;

                pos++; // subValType

                if (valueOffsets != null &&
                    subId < valueOffsets.Length)
                {
                    int valSize = valueSizes![subId];
                    byte valType = valueTypes![subId];

                    if (!optional ||
                        (valType != BinXmlValueType.Null &&
                         valSize > 0))
                    {
                        WriteValueJson(
                            valSize,
                            valType,
                            valueOffsets[subId],
                            binxmlChunkBase,
                            w);
                    }
                }

                break;
            }

            case BinXmlToken.CharRef:
            {
                pos++;

                ushort charVal =
                    BinaryPrimitives.ReadUInt16LittleEndian(
                        data.Slice(pos, 2));
                pos += 2;

                w.WriteStringValue(char.ConvertFromUtf32(charVal));
                break;
            }

            case BinXmlToken.EntityRef:
            {
                pos++;

                uint nameOff =
                    BinaryPrimitives.ReadUInt32LittleEndian(
                        data.Slice(pos, 4));
                pos += 4;

                string entityName = ReadName(nameOff);

                string resolved = entityName switch
                {
                    "amp"  => "&",
                    "lt"   => "<",
                    "gt"   => ">",
                    "quot" => "\"",
                    "apos" => "'",
                    _      => $"&{entityName};"
                };

                w.WriteStringValue(resolved);
                break;
            }

            case BinXmlToken.CDataSection:
            {
                pos++;

                ReadOnlySpan<char> cdataChars =
                    BinXmlValueFormatter.ReadUnicodeTextString(data, ref pos);

                w.WriteStringValue(cdataChars);
                break;
            }

            case BinXmlToken.TemplateInstance:
                ParseTemplateInstanceJson(
                    data, ref pos, binxmlChunkBase, w);
                break;

            case BinXmlToken.FragmentHeader:
                ParseFragmentJson(
                    data, ref pos, binxmlChunkBase, w);
                break;

            default:
                pos++;
                break;
        }
    }
}


    /// <summary>
    /// Classification result for an element's children — determines JSON representation.
    /// </summary>
    private readonly struct ElementClassification
    {
        /// <summary>
        /// True if the element contains at least one child element or embedded BinXml substitution.
        /// </summary>
        public readonly bool HasChildElements;

        /// <summary>
        /// True if the element contains at least one text value, substitution, char/entity ref, or CDATA.
        /// </summary>
        public readonly bool HasText;

        /// <summary>
        /// True if the element has no child elements and no text content.
        /// </summary>
        public readonly bool IsEmpty;

        /// <summary>
        /// Initialises a new classification result.
        /// </summary>
        /// <param name="hasChildElements">Whether child elements are present.</param>
        /// <param name="hasText">Whether text content is present.</param>
        /// <param name="isEmpty">Whether the element is empty.</param>
        public ElementClassification(bool hasChildElements, bool hasText, bool isEmpty)
        {
            HasChildElements = hasChildElements;
            HasText = hasText;
            IsEmpty = isEmpty;
        }
    }

    /// <summary>
    /// Pre-scans element children to classify without consuming position.
    /// </summary>
    /// <param name="data">BinXml byte stream.</param>
    /// <param name="pos">Read position to scan from (not modified — value copy).</param>
    /// <param name="valueOffsets">File offsets of substitution values, or null.</param>
    /// <param name="valueSizes">Byte sizes of substitution values.</param>
    /// <param name="valueTypes">BinXml value type codes for each substitution.</param>
    /// <param name="binxmlChunkBase">Chunk-relative base offset of <paramref name="data"/>.</param>
    /// <param name="depth">Current recursion depth for stack overflow protection.</param>
    /// <returns>Classification indicating whether children contain elements, text, both, or neither.</returns>
    private ElementClassification ClassifyChildren(ReadOnlySpan<byte> data, int pos,
                                                   int[]? valueOffsets, int[]? valueSizes, byte[]? valueTypes, int binxmlChunkBase, int depth = 0)
    {
        bool hasChildElements = false;
        bool hasText = false;

        while (pos < data.Length)
        {
            byte tok = data[pos];
            byte baseTok = (byte)(tok & ~BinXmlToken.HasMoreDataFlag);

            if (baseTok == BinXmlToken.Eof ||
                baseTok == BinXmlToken.EndElement ||
                baseTok == BinXmlToken.CloseStartElement ||
                baseTok == BinXmlToken.CloseEmptyElement ||
                baseTok == BinXmlToken.Attribute)
                break;

            switch (baseTok)
            {
                case BinXmlToken.OpenStartElement:
                    hasChildElements = true;
                    SkipElement(data, ref pos, binxmlChunkBase, depth + 1);
                    break;

                case BinXmlToken.Value:
                    hasText = true;
                    pos++;
                    pos++; // value type
                    ushort numCharsVal = MemoryMarshal.Read<ushort>(data[pos..]);
                    pos += 2 + numCharsVal * 2;
                    break;

                case BinXmlToken.NormalSubstitution:
                {
                    pos++;
                    ushort subId = MemoryMarshal.Read<ushort>(data[pos..]);
                    pos += 2;
                    pos++; // subValType
                    if (valueOffsets != null && subId < valueOffsets.Length)
                    {
                        byte vt = valueTypes![subId];
                        if (vt == BinXmlValueType.BinXml)
                            hasChildElements = true;
                        else if (valueSizes![subId] > 0)
                            hasText = true;
                    }
                    break;
                }

                case BinXmlToken.OptionalSubstitution:
                {
                    pos++;
                    ushort subId = MemoryMarshal.Read<ushort>(data[pos..]);
                    pos += 2;
                    pos++; // subValType
                    if (valueOffsets != null && subId < valueOffsets.Length)
                    {
                        byte vt = valueTypes![subId];
                        int vs = valueSizes![subId];
                        if (vt != BinXmlValueType.Null && vs > 0)
                        {
                            if (vt == BinXmlValueType.BinXml)
                                hasChildElements = true;
                            else
                                hasText = true;
                        }
                    }
                    break;
                }

                case BinXmlToken.CharRef:
                    hasText = true;
                    pos += 3;
                    break;

                case BinXmlToken.EntityRef:
                    hasText = true;
                    pos += 5;
                    break;

                case BinXmlToken.CDataSection:
                    hasText = true;
                    pos++;
                    ushort numCharsCdata = MemoryMarshal.Read<ushort>(data[pos..]);
                    pos += 2 + numCharsCdata * 2;
                    break;

                case BinXmlToken.TemplateInstance:
                    hasChildElements = true;
                    goto done; // can't easily skip templates

                default:
                    pos++;
                    break;
            }
        }
        done:

        bool isEmpty = !hasChildElements && !hasText;
        return new ElementClassification(hasChildElements, hasText, isEmpty);
    }

    /// <summary>
    /// Skips an element without producing output (for classification pre-scanning).
    /// </summary>
    /// <param name="data">BinXml byte stream.</param>
    /// <param name="pos">Current read position; advanced past the entire element.</param>
    /// <param name="binxmlChunkBase">Chunk-relative base offset of <paramref name="data"/>.</param>
    /// <param name="depth">Current recursion depth for stack overflow protection.</param>
    private void SkipElement(ReadOnlySpan<byte> data, ref int pos, int binxmlChunkBase, int depth = 0)
    {
        if (depth >= MaxRecursionDepth) return;

        byte tok = data[pos];
        bool hasAttrs = (tok & BinXmlToken.HasMoreDataFlag) != 0;
        pos++;
        pos += 2; // depId
        uint elemDataSize = MemoryMarshal.Read<uint>(data[pos..]);
        pos += 4;
        uint nameOffset = MemoryMarshal.Read<uint>(data[pos..]);
        pos += 4;

        if (!TrySkipInlineName(data, ref pos, nameOffset, binxmlChunkBase)) return;

        if (hasAttrs)
        {
            uint attrListSize = MemoryMarshal.Read<uint>(data[pos..]);
            pos += 4 + (int)attrListSize;
        }

        if (pos >= data.Length) return;
        byte closeTok = data[pos];
        if (closeTok == BinXmlToken.CloseEmptyElement)
        {
            pos++;
        }
        else if (closeTok == BinXmlToken.CloseStartElement)
        {
            pos++;
            // Skip content until EndElement
            SkipContent(data, ref pos, binxmlChunkBase, depth + 1);
            if (pos < data.Length && data[pos] == BinXmlToken.EndElement)
                pos++;
        }
        else
        {
            pos++;
        }
    }

    /// <summary>
    /// Skips content tokens without producing output, advancing <paramref name="pos"/> past
    /// child elements, values, substitutions, char/entity refs, and CDATA sections until a break token.
    /// </summary>
    /// <param name="data">BinXml byte stream.</param>
    /// <param name="pos">Current read position; advanced past all skipped content.</param>
    /// <param name="binxmlChunkBase">Chunk-relative base offset of <paramref name="data"/>.</param>
    /// <param name="depth">Current recursion depth for stack overflow protection.</param>
    private void SkipContent(ReadOnlySpan<byte> data, ref int pos, int binxmlChunkBase, int depth = 0)
    {
        while (pos < data.Length)
        {
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
                    SkipElement(data, ref pos, binxmlChunkBase, depth + 1);
                    break;
                case BinXmlToken.Value:
                    pos++;
                    pos++;
                    ushort numChars = MemoryMarshal.Read<ushort>(data[pos..]);
                    pos += 2 + numChars * 2;
                    break;
                case BinXmlToken.NormalSubstitution:
                case BinXmlToken.OptionalSubstitution:
                    pos += 4; // token + subId(2) + valType(1)
                    break;
                case BinXmlToken.CharRef:
                    pos += 3;
                    break;
                case BinXmlToken.EntityRef:
                    pos += 5;
                    break;
                case BinXmlToken.CDataSection:
                    pos++;
                    ushort numCharsCdata = MemoryMarshal.Read<ushort>(data[pos..]);
                    pos += 2 + numCharsCdata * 2;
                    break;
                default:
                    pos++;
                    break;
            }
        }
    }

    /// <summary>
    /// Writes a single element as JSON. Core method for JSON output.
    /// Classifies children to decide between null, scalar value, or nested object representation.
    /// Attributes are emitted under a "#attributes" property when present.
    /// </summary>
    /// <param name="data">BinXml byte stream.</param>
    /// <param name="pos">Current read position; advanced past the entire element.</param>
    /// <param name="valueOffsets">File offsets of substitution values, or null.</param>
    /// <param name="valueSizes">Byte sizes of substitution values.</param>
    /// <param name="valueTypes">BinXml value type codes for each substitution.</param>
    /// <param name="binxmlChunkBase">Chunk-relative base offset of <paramref name="data"/>.</param>
    /// <param name="w">UTF-8 JSON writer that receives the output.</param>
    /// <param name="depth">Current recursion depth for stack overflow protection.</param>
    private void WriteElementJson(ReadOnlySpan<byte> data, ref int pos,
                                   int[]? valueOffsets, int[]? valueSizes, byte[]? valueTypes,
                                   int binxmlChunkBase, Utf8JsonWriter w, int depth = 0)
    {
        if (depth >= MaxRecursionDepth)
        {
            w.WriteNullValue();
            return;
        }

        byte tok = data[pos];
        bool hasAttrs = (tok & BinXmlToken.HasMoreDataFlag) != 0;
        pos++;

        pos += 2; // depId
        pos += 4; // dataSize
        uint nameOffset = MemoryMarshal.Read<uint>(data[pos..]);
        pos += 4;

        if (!TrySkipInlineName(data, ref pos, nameOffset, binxmlChunkBase)) return;

        string elemName = ReadName(nameOffset);

        // Collect attributes
        List<(string name, string value)>? attrs = null;
        if (hasAttrs)
        {
            uint attrListSize = MemoryMarshal.Read<uint>(data[pos..]);
            pos += 4;
            int attrEnd = pos + (int)attrListSize;

            attrs = new List<(string, string)>();
            while (pos < attrEnd)
            {
                byte attrTok = data[pos];
                byte attrBase = (byte)(attrTok & ~BinXmlToken.HasMoreDataFlag);
                if (attrBase != BinXmlToken.Attribute) break;

                pos++;
                uint attrNameOff = MemoryMarshal.Read<uint>(data[pos..]);
                pos += 4;
                if (!TrySkipInlineName(data, ref pos, attrNameOff, binxmlChunkBase)) break;

                string attrName = ReadName(attrNameOff);
                string attrValue = FormatContentAsString(data, ref pos, valueOffsets, valueSizes, valueTypes,
                    binxmlChunkBase);
                attrs.Add((attrName, attrValue));
            }
        }

        // Close token
        if (pos >= data.Length)
        {
            // Self-closing, no content
            w.WriteNullValue();
            return;
        }

        byte closeTok = data[pos];
        if (closeTok == BinXmlToken.CloseEmptyElement)
        {
            pos++;
            // Check if all attrs are empty — if so, null; else write object
            bool allAttrsEmpty = true;
            if (attrs != null)
            {
                for (int index = 0; index < attrs.Count; index++)
                {
                    (string _, string v) = attrs[index];
                    if (v.Length > 0)
                    {
                        allAttrsEmpty = false;
                        break;
                    }
                }
            }

            if (attrs == null || allAttrsEmpty)
            {
                w.WriteNullValue();
            }
            else
            {
                w.WriteStartObject();
                w.WritePropertyName("#attributes");
                w.WriteStartObject();
                for (int index = 0; index < attrs.Count; index++)
                {
                    (string n, string v) = attrs[index];
                    w.WriteString(n, v);
                }
                w.WriteEndObject();
                w.WriteEndObject();
            }

            return;
        }

        if (closeTok != BinXmlToken.CloseStartElement)
        {
            w.WriteNullValue();
            return;
        }

        pos++; // consume CloseStartElement

        // Classify children
        ElementClassification cls = ClassifyChildren(data, pos, valueOffsets, valueSizes, valueTypes, binxmlChunkBase, depth + 1);

        bool hasNonEmptyAttrs = false;
        if (attrs != null)
        {
            for (int index = 0; index < attrs.Count; index++)
            {
                (string _, string v) = attrs[index];
                if (v.Length > 0)
                {
                    hasNonEmptyAttrs = true;
                    break;
                }
            }
        }

        // Check for EventData/UserData flattening
        bool isDataContainer = elemName == "EventData" || elemName == "UserData";

        if (cls.IsEmpty && !hasNonEmptyAttrs)
        {
            // Empty element — null
            SkipToEndElement(data, ref pos);
            w.WriteNullValue();
        }
        else if (!cls.HasChildElements && !hasNonEmptyAttrs)
        {
            // Scalar element — direct value
            WriteTextContentJson(data, ref pos, valueOffsets, valueSizes, valueTypes, binxmlChunkBase, w);
            if (pos < data.Length && data[pos] == BinXmlToken.EndElement)
                pos++;
        }
        else
        {
            // Object element
            w.WriteStartObject();

            if (hasNonEmptyAttrs)
            {
                w.WritePropertyName("#attributes");
                w.WriteStartObject();
                for (int index = 0; index < attrs!.Count; index++)
                {
                    (string n, string v) = attrs![index];
                    w.WriteString(n, v);
                }
                w.WriteEndObject();
            }

            if (cls.HasText && cls.HasChildElements)
            {
                // Mixed content — capture text as #text
                string textVal =
                    FormatContentAsString(data, ref pos, valueOffsets, valueSizes, valueTypes, binxmlChunkBase);
                if (textVal.Length > 0)
                    w.WriteString("#text", textVal);
            }
            else
            {
                // Write child elements as properties
                WriteChildElementsJson(data, ref pos, valueOffsets, valueSizes, valueTypes, binxmlChunkBase, w,
                    isDataContainer, depth + 1);
            }

            if (pos < data.Length && data[pos] == BinXmlToken.EndElement)
                pos++;

            w.WriteEndObject();
        }
    }

    /// <summary>
    /// Writes text-only content as a JSON value (string or typed primitive).
    /// When a single typed substitution is the only content, renders it as a native JSON type
    /// (number, boolean, etc.) rather than a string for schema fidelity.
    /// </summary>
    /// <param name="data">BinXml byte stream.</param>
    /// <param name="pos">Current read position; advanced past consumed content tokens.</param>
    /// <param name="valueOffsets">File offsets of substitution values, or null.</param>
    /// <param name="valueSizes">Byte sizes of substitution values.</param>
    /// <param name="valueTypes">BinXml value type codes for each substitution.</param>
    /// <param name="binxmlChunkBase">Chunk-relative base offset of <paramref name="data"/>.</param>
    /// <param name="w">UTF-8 JSON writer that receives the output.</param>
    private void WriteTextContentJson(ReadOnlySpan<byte> data, ref int pos,
                                       int[]? valueOffsets, int[]? valueSizes, byte[]? valueTypes,
                                       int binxmlChunkBase, Utf8JsonWriter w)
    {
        // Check if there's a single substitution — render as typed value
        int savedPos = pos;
        int subCount = 0;
        int firstSubId = -1;
        bool hasOtherContent = false;

        while (savedPos < data.Length)
        {
            byte tok = data[savedPos];
            byte baseTok = (byte)(tok & ~BinXmlToken.HasMoreDataFlag);

            if (baseTok == BinXmlToken.Eof || baseTok == BinXmlToken.EndElement ||
                baseTok == BinXmlToken.Attribute) break;

            if (baseTok == BinXmlToken.NormalSubstitution || baseTok == BinXmlToken.OptionalSubstitution)
            {
                savedPos++;
                ushort subId = MemoryMarshal.Read<ushort>(data[savedPos..]);
                savedPos += 2;
                savedPos++; // valType
                if (subCount == 0) firstSubId = subId;
                subCount++;
            }
            else if (baseTok == BinXmlToken.Value || baseTok == BinXmlToken.CharRef ||
                     baseTok == BinXmlToken.EntityRef || baseTok == BinXmlToken.CDataSection)
            {
                hasOtherContent = true;
                break;
            }
            else break;
        }

        if (subCount == 1 && !hasOtherContent && valueOffsets != null && firstSubId >= 0 &&
            firstSubId < valueOffsets.Length)
        {
            // Single typed substitution — render as JSON primitive
            byte valType = valueTypes![firstSubId];
            int valSize = valueSizes![firstSubId];

            // Consume the substitution token
            pos++; // token
            pos += 2; // subId
            pos++; // valType

            if (valType == BinXmlValueType.Null || valSize == 0)
            {
                w.WriteNullValue();
                return;
            }

            WriteValueJson(valSize, valType, valueOffsets[firstSubId], binxmlChunkBase, w);
            return;
        }

        // Multiple subs or mixed content — render as concatenated string
        string text = FormatContentAsString(data, ref pos, valueOffsets, valueSizes, valueTypes, binxmlChunkBase);
        w.WriteStringValue(text);
    }

    /// <summary>
    /// Writes child elements as JSON properties. Handles duplicate name suffixing and EventData/UserData flattening.
    /// For EventData/UserData containers, Data elements with a Name= attribute are flattened to
    /// direct key-value pairs (e.g., &lt;Data Name="Foo"&gt;bar&lt;/Data&gt; becomes "Foo": "bar").
    /// Duplicate element names receive _N suffixes (e.g., "Data_1", "Data_2").
    /// </summary>
    /// <param name="data">BinXml byte stream.</param>
    /// <param name="pos">Current read position; advanced past consumed child content.</param>
    /// <param name="valueOffsets">File offsets of substitution values, or null.</param>
    /// <param name="valueSizes">Byte sizes of substitution values.</param>
    /// <param name="valueTypes">BinXml value type codes for each substitution.</param>
    /// <param name="binxmlChunkBase">Chunk-relative base offset of <paramref name="data"/>.</param>
    /// <param name="w">UTF-8 JSON writer that receives the output.</param>
    /// <param name="isDataContainer">True if the parent element is EventData or UserData, enabling Name= flattening.</param>
    /// <param name="depth">Current recursion depth for stack overflow protection.</param>
    private void WriteChildElementsJson(ReadOnlySpan<byte> data, ref int pos,
                                         int[]? valueOffsets, int[]? valueSizes, byte[]? valueTypes,
                                         int binxmlChunkBase, Utf8JsonWriter w, bool isDataContainer, int depth = 0)
    {
        Dictionary<string, int>? nameCounts = null;

        while (pos < data.Length)
        {
            byte tok = data[pos];
            byte baseTok = (byte)(tok & ~BinXmlToken.HasMoreDataFlag);

            if (baseTok == BinXmlToken.Eof || baseTok == BinXmlToken.EndElement ||
                baseTok == BinXmlToken.Attribute) break;

            if (baseTok == BinXmlToken.OpenStartElement)
            {
                // Peek at element name without consuming
                int peekPos = pos;
                peekPos++; // token
                peekPos += 2; // depId
                peekPos += 4; // dataSize
                uint nameOff = MemoryMarshal.Read<uint>(data[peekPos..]);

                string childName = ReadName(nameOff);

                // For EventData/UserData container: check for Name= attribute
                if (isDataContainer && childName == "Data")
                {
                    string? namedKey = PeekDataNameAttribute(data, pos, binxmlChunkBase, valueOffsets, valueSizes,
                        valueTypes);
                    if (namedKey != null)
                    {
                        // Named data: <Data Name="Foo">bar</Data> → "Foo": "bar"
                        w.WritePropertyName(namedKey);
                        WriteDataElementValueJson(data, ref pos, valueOffsets, valueSizes, valueTypes, binxmlChunkBase,
                            w);
                        continue;
                    }
                }

                // Handle duplicate names with _N suffixing
                nameCounts ??= new Dictionary<string, int>();
                if (nameCounts.TryGetValue(childName, out int count))
                {
                    nameCounts[childName] = count + 1;
                    w.WritePropertyName($"{childName}_{count}");
                }
                else
                {
                    nameCounts[childName] = 1;
                    w.WritePropertyName(childName);
                }

                WriteElementJson(data, ref pos, valueOffsets, valueSizes, valueTypes, binxmlChunkBase, w, depth + 1);
            }
            else if (baseTok == BinXmlToken.Value)
            {
                pos++;
                pos++; // value type
                ReadOnlySpan<char> textChars = BinXmlValueFormatter.ReadUnicodeTextString(data, ref pos);
                if (textChars.Length > 0)
                    w.WriteString("#text", textChars);
            }
            else if (baseTok == BinXmlToken.NormalSubstitution || baseTok == BinXmlToken.OptionalSubstitution)
            {
                pos++;
                ushort subId = MemoryMarshal.Read<ushort>(data[pos..]);
                pos += 2;
                pos++; // subValType
                if (valueOffsets != null && subId < valueOffsets.Length)
                {
                    byte valType = valueTypes![subId];
                    int valSize = valueSizes![subId];
                    bool skip = baseTok == BinXmlToken.OptionalSubstitution &&
                                (valType == BinXmlValueType.Null || valSize == 0);
                    if (!skip && valType == BinXmlValueType.BinXml && valSize > 0)
                    {
                        // Embedded BinXml — render inline
                        ReadOnlySpan<byte> embeddedData = _fileData.AsSpan(valueOffsets[subId], valSize);
                        int embeddedChunkBase = valueOffsets[subId] - _chunkFileOffset;
                        int embeddedPos = 0;
                        ParseDocumentJson(embeddedData, ref embeddedPos, embeddedChunkBase, w);
                    }
                    else if (!skip && valSize > 0)
                    {
                        w.WritePropertyName("#text");
                        WriteValueJson(valSize, valType, valueOffsets[subId], binxmlChunkBase, w);
                    }
                }
            }
            else if (baseTok == BinXmlToken.TemplateInstance)
            {
                ParseTemplateInstanceJson(data, ref pos, binxmlChunkBase, w);
            }
            else if (baseTok == BinXmlToken.FragmentHeader)
            {
                ParseFragmentJson(data, ref pos, binxmlChunkBase, w);
            }
            else
            {
                pos++;
            }
        }
    }

    /// <summary>
    /// Peeks at a Data element to check for Name="..." attribute, returns the value or null.
    /// Does not advance the caller's position (uses a local copy of <paramref name="pos"/>).
    /// </summary>
    /// <param name="data">BinXml byte stream.</param>
    /// <param name="pos">Element start position (not modified — value copy).</param>
    /// <param name="binxmlChunkBase">Chunk-relative base offset of <paramref name="data"/>.</param>
    /// <param name="valueOffsets">File offsets of substitution values, or null.</param>
    /// <param name="valueSizes">Byte sizes of substitution values.</param>
    /// <param name="valueTypes">BinXml value type codes for each substitution.</param>
    /// <returns>The Name attribute value if present and non-empty; otherwise null.</returns>
    private string? PeekDataNameAttribute(ReadOnlySpan<byte> data, int pos,
                                          int binxmlChunkBase, int[]? valueOffsets, int[]? valueSizes, byte[]? valueTypes)
    {
        byte tok = data[pos];
        bool hasAttrs = (tok & BinXmlToken.HasMoreDataFlag) != 0;
        if (!hasAttrs) return null;

        pos++; // token
        pos += 2; // depId
        pos += 4; // dataSize
        uint nameOffset = MemoryMarshal.Read<uint>(data[pos..]);
        pos += 4;

        if (!TrySkipInlineName(data, ref pos, nameOffset, binxmlChunkBase)) return null;

        // Now at attribute list
        if (pos + 4 > data.Length) return null;
        uint attrListSize = MemoryMarshal.Read<uint>(data[pos..]);
        pos += 4;
        int attrEnd = pos + (int)attrListSize;

        while (pos < attrEnd)
        {
            byte attrTok = data[pos];
            byte attrBase = (byte)(attrTok & ~BinXmlToken.HasMoreDataFlag);
            if (attrBase != BinXmlToken.Attribute) break;

            pos++;
            uint attrNameOff = MemoryMarshal.Read<uint>(data[pos..]);
            pos += 4;
            if (!TrySkipInlineName(data, ref pos, attrNameOff, binxmlChunkBase)) break;

            string attrName = ReadName(attrNameOff);
            if (attrName == "Name")
            {
                string val =
                    FormatContentAsString(data, ref pos, valueOffsets, valueSizes, valueTypes, binxmlChunkBase);
                return val.Length > 0 ? val : null;
            }

            SkipContent(data, ref pos, binxmlChunkBase);
        }

        return null;
    }

    /// <summary>
    /// Writes a Data element's text value as JSON, skipping the element structure.
    /// Used for EventData/UserData Name= flattening.
    /// </summary>
    /// <param name="data">BinXml byte stream.</param>
    /// <param name="pos">Current read position at the Data element's OpenStartElement token; advanced past the entire element.</param>
    /// <param name="valueOffsets">File offsets of substitution values, or null.</param>
    /// <param name="valueSizes">Byte sizes of substitution values.</param>
    /// <param name="valueTypes">BinXml value type codes for each substitution.</param>
    /// <param name="binxmlChunkBase">Chunk-relative base offset of <paramref name="data"/>.</param>
    /// <param name="w">UTF-8 JSON writer that receives the output.</param>
    private void WriteDataElementValueJson(ReadOnlySpan<byte> data, ref int pos,
                                            int[]? valueOffsets, int[]? valueSizes, byte[]? valueTypes,
                                            int binxmlChunkBase, Utf8JsonWriter w)
    {
        byte tok = data[pos];
        bool hasAttrs = (tok & BinXmlToken.HasMoreDataFlag) != 0;
        pos++;
        pos += 2; // depId
        pos += 4; // dataSize
        uint nameOffset = MemoryMarshal.Read<uint>(data[pos..]);
        pos += 4;

        if (!TrySkipInlineName(data, ref pos, nameOffset, binxmlChunkBase))
        {
            w.WriteNullValue();
            return;
        }

        if (hasAttrs)
        {
            uint attrListSize = MemoryMarshal.Read<uint>(data[pos..]);
            pos += 4;
            int attrEnd = pos + (int)attrListSize;
            // Skip attributes (we already peeked Name=)
            while (pos < attrEnd)
            {
                byte attrTok = data[pos];
                byte attrBase = (byte)(attrTok & ~BinXmlToken.HasMoreDataFlag);
                if (attrBase != BinXmlToken.Attribute) break;
                pos++;
                uint attrNameOff = MemoryMarshal.Read<uint>(data[pos..]);
                pos += 4;
                if (!TrySkipInlineName(data, ref pos, attrNameOff, binxmlChunkBase)) break;

                SkipContent(data, ref pos, binxmlChunkBase);
            }
        }

        if (pos >= data.Length)
        {
            w.WriteNullValue();
            return;
        }

        byte closeTok = data[pos];
        if (closeTok == BinXmlToken.CloseEmptyElement)
        {
            pos++;
            w.WriteNullValue();
            return;
        }

        if (closeTok == BinXmlToken.CloseStartElement)
        {
            pos++;
            WriteTextContentJson(data, ref pos, valueOffsets, valueSizes, valueTypes, binxmlChunkBase, w);
            if (pos < data.Length && data[pos] == BinXmlToken.EndElement)
                pos++;
        }
        else
        {
            w.WriteNullValue();
        }
    }

    /// <summary>
    /// Formats content tokens as a plain string (for attribute values and text content in JSON mode).
    /// Thin wrapper around <see cref="ParseContent"/> with <c>resolveEntities: true</c> to produce
    /// plain text output (no XML escaping, resolved character/entity references, unwrapped CDATA).
    /// </summary>
    /// <param name="data">BinXml byte stream.</param>
    /// <param name="pos">Current read position; advanced past consumed content tokens.</param>
    /// <param name="valueOffsets">File offsets of substitution values, or null.</param>
    /// <param name="valueSizes">Byte sizes of substitution values.</param>
    /// <param name="valueTypes">BinXml value type codes for each substitution.</param>
    /// <param name="binxmlChunkBase">Chunk-relative base offset of <paramref name="data"/>.</param>
    /// <returns>The concatenated plain-text string of all content tokens.</returns>
    private string FormatContentAsString(ReadOnlySpan<byte> data, ref int pos,
                                         int[]? valueOffsets, int[]? valueSizes, byte[]? valueTypes, int binxmlChunkBase)
    {
        ValueStringBuilder vsb = new(stackalloc char[128]);
        ParseContent(data, ref pos, valueOffsets, valueSizes, valueTypes, binxmlChunkBase, ref vsb,
            resolveEntities: true);
        string result = vsb.ToString();
        vsb.Dispose();
        return result;
    }

    /// <summary>
    /// Skips all content tokens and consumes the trailing EndElement (0x04) token if present.
    /// Used to discard the body of an empty or unneeded element.
    /// </summary>
    /// <param name="data">BinXml byte stream.</param>
    /// <param name="pos">Current read position; advanced past content and the EndElement token.</param>
    private void SkipToEndElement(ReadOnlySpan<byte> data, ref int pos)
    {
        SkipContent(data, ref pos, 0);
        if (pos < data.Length && data[pos] == BinXmlToken.EndElement)
            pos++;
    }

    /// <summary>
    /// Writes a typed value as a JSON value.
    /// Numeric types are written as JSON numbers; strings, GUIDs, SIDs, timestamps, and hex values
    /// are written as JSON strings; booleans as JSON booleans; embedded BinXml is recursively parsed.
    /// </summary>
    /// <param name="size">Byte size of the value data.</param>
    /// <param name="valueType">BinXml value type code. Bit 0x80 indicates an array.</param>
    /// <param name="fileOffset">Absolute byte offset of the value data within <see cref="_fileData"/>.</param>
    /// <param name="binxmlChunkBase">Chunk-relative base offset for embedded BinXml (type 0x21) resolution.</param>
    /// <param name="w">UTF-8 JSON writer that receives the output.</param>
    private void WriteValueJson(int size, byte valueType, int fileOffset, int binxmlChunkBase, Utf8JsonWriter w)
    {
        if (size == 0)
        {
            w.WriteNullValue();
            return;
        }

        ReadOnlySpan<byte> valueBytes = _fileData.AsSpan(fileOffset, size);

        // Array flag
        if ((valueType & BinXmlValueType.ArrayFlag) != 0)
        {
            WriteArrayJson(valueBytes, (byte)(valueType & 0x7F), fileOffset, binxmlChunkBase, w);
            return;
        }

        switch (valueType)
        {
            case BinXmlValueType.Null:
                w.WriteNullValue();
                break;

            case BinXmlValueType.String:
            {
                ReadOnlySpan<char> chars = MemoryMarshal.Cast<byte, char>(valueBytes);
                if (chars.Length > 0 && chars[^1] == '\0')
                    chars = chars[..^1];
                w.WriteStringValue(chars);
                break;
            }

            case BinXmlValueType.AnsiString:
            {
                // Convert byte-by-byte to string
                int len = valueBytes.IndexOf((byte)0);
                if (len < 0) len = valueBytes.Length;
                Span<char> ansiChars = stackalloc char[len];
                for (int i = 0; i < len; i++) ansiChars[i] = (char)valueBytes[i];
                w.WriteStringValue(ansiChars);
                break;
            }

            case BinXmlValueType.Int8:
                w.WriteNumberValue((sbyte)valueBytes[0]);
                break;

            case BinXmlValueType.UInt8:
                w.WriteNumberValue(valueBytes[0]);
                break;

            case BinXmlValueType.Int16:
                w.WriteNumberValue(MemoryMarshal.Read<short>(valueBytes));
                break;

            case BinXmlValueType.UInt16:
                w.WriteNumberValue(MemoryMarshal.Read<ushort>(valueBytes));
                break;

            case BinXmlValueType.Int32:
                w.WriteNumberValue(MemoryMarshal.Read<int>(valueBytes));
                break;

            case BinXmlValueType.UInt32:
                w.WriteNumberValue(MemoryMarshal.Read<uint>(valueBytes));
                break;

            case BinXmlValueType.Int64:
                w.WriteNumberValue(MemoryMarshal.Read<long>(valueBytes));
                break;

            case BinXmlValueType.UInt64:
                w.WriteNumberValue(MemoryMarshal.Read<ulong>(valueBytes));
                break;

            case BinXmlValueType.Float:
                w.WriteNumberValue(MemoryMarshal.Read<float>(valueBytes));
                break;

            case BinXmlValueType.Double:
                w.WriteNumberValue(MemoryMarshal.Read<double>(valueBytes));
                break;

            case BinXmlValueType.Bool:
                w.WriteBooleanValue(MemoryMarshal.Read<uint>(valueBytes) != 0);
                break;

            case BinXmlValueType.Binary:
            {
                ValueStringBuilder vsb = new(stackalloc char[size * 2]);
                BinXmlValueFormatter.AppendHex(ref vsb, valueBytes);
                w.WriteStringValue(vsb.AsSpan());
                vsb.Dispose();
                break;
            }

            case BinXmlValueType.Guid:
            {
                if (size < 16)
                {
                    w.WriteNullValue();
                    break;
                }

                ValueStringBuilder vsb = new(stackalloc char[38]);
                BinXmlValueFormatter.FormatGuid(valueBytes, ref vsb);
                w.WriteStringValue(vsb.AsSpan());
                vsb.Dispose();
                break;
            }

            case BinXmlValueType.SizeT:
            {
                ValueStringBuilder vsb = new(stackalloc char[18]);
                vsb.Append("0x");
                if (size == 8)
                    vsb.AppendFormatted(MemoryMarshal.Read<ulong>(valueBytes), "x16");
                else
                    vsb.AppendFormatted(MemoryMarshal.Read<uint>(valueBytes), "x8");
                w.WriteStringValue(vsb.AsSpan());
                vsb.Dispose();
                break;
            }

            case BinXmlValueType.FileTime:
            {
                if (size < 8)
                {
                    w.WriteNullValue();
                    break;
                }

                long ticks = MemoryMarshal.Read<long>(valueBytes);
                if (ticks == 0)
                {
                    w.WriteStringValue("");
                    break;
                }

                ValueStringBuilder vsb = new(stackalloc char[28]);
                BinXmlValueFormatter.AppendFileTime(valueBytes, ref vsb);
                w.WriteStringValue(vsb.AsSpan());
                vsb.Dispose();
                break;
            }

            case BinXmlValueType.SystemTime:
            {
                if (size < 16)
                {
                    w.WriteNullValue();
                    break;
                }

                ValueStringBuilder vsb = new(stackalloc char[24]);
                BinXmlValueFormatter.AppendSystemTime(valueBytes, ref vsb);
                w.WriteStringValue(vsb.AsSpan());
                vsb.Dispose();
                break;
            }

            case BinXmlValueType.Sid:
            {
                if (size < 8)
                {
                    w.WriteNullValue();
                    break;
                }

                ValueStringBuilder vsb = new(stackalloc char[64]);
                BinXmlValueFormatter.AppendSid(valueBytes, size, ref vsb);
                w.WriteStringValue(vsb.AsSpan());
                vsb.Dispose();
                break;
            }

            case BinXmlValueType.HexInt32:
            {
                ValueStringBuilder vsb = new(stackalloc char[10]);
                vsb.Append("0x");
                vsb.AppendFormatted(MemoryMarshal.Read<uint>(valueBytes), "x8");
                w.WriteStringValue(vsb.AsSpan());
                vsb.Dispose();
                break;
            }

            case BinXmlValueType.HexInt64:
            {
                ValueStringBuilder vsb = new(stackalloc char[18]);
                vsb.Append("0x");
                vsb.AppendFormatted(MemoryMarshal.Read<ulong>(valueBytes), "x16");
                w.WriteStringValue(vsb.AsSpan());
                vsb.Dispose();
                break;
            }

            case BinXmlValueType.BinXml:
            {
                int embeddedChunkBase = fileOffset - _chunkFileOffset;
                int embeddedPos = 0;
                ParseDocumentJson(valueBytes, ref embeddedPos, embeddedChunkBase, w);
                break;
            }

            default:
            {
                ValueStringBuilder vsb = new(stackalloc char[size * 2]);
                BinXmlValueFormatter.AppendHex(ref vsb, valueBytes);
                w.WriteStringValue(vsb.AsSpan());
                vsb.Dispose();
                break;
            }
        }
    }

    /// <summary>
    /// Writes an array-typed value as a JSON array. String arrays are split on null terminators;
    /// fixed-size types are split by element size. Falls back to a single hex string for unknown types.
    /// </summary>
    /// <param name="valueBytes">Raw value bytes containing the array data.</param>
    /// <param name="baseType">Base BinXml value type (with array flag 0x80 masked off).</param>
    /// <param name="fileOffset">Absolute byte offset of the value data within <see cref="_fileData"/>.</param>
    /// <param name="binxmlChunkBase">Chunk-relative base offset for nested value rendering.</param>
    /// <param name="w">UTF-8 JSON writer that receives the output.</param>
    private void WriteArrayJson(ReadOnlySpan<byte> valueBytes, byte baseType, int fileOffset,
                                 int binxmlChunkBase, Utf8JsonWriter w)
    {
        w.WriteStartArray();

        if (baseType == BinXmlValueType.String)
        {
            ReadOnlySpan<char> chars = MemoryMarshal.Cast<byte, char>(valueBytes);
            int start = 0;
            for (int i = 0; i <= chars.Length; i++)
            {
                if (i == chars.Length || chars[i] == '\0')
                {
                    if (i > start)
                        w.WriteStringValue(chars.Slice(start, i - start));
                    start = i + 1;
                }
            }
        }
        else
        {
            int elemSize = BinXmlValueFormatter.GetElementSize(baseType);
            if (elemSize > 0 && valueBytes.Length >= elemSize)
            {
                for (int i = 0; i + elemSize <= valueBytes.Length; i += elemSize)
                    WriteValueJson(elemSize, baseType, fileOffset + i, binxmlChunkBase, w);
            }
            else
            {
                ValueStringBuilder vsb = new(stackalloc char[valueBytes.Length * 2]);
                BinXmlValueFormatter.AppendHex(ref vsb, valueBytes);
                w.WriteStringValue(vsb.AsSpan());
                vsb.Dispose();
            }
        }

        w.WriteEndArray();
    }
}
