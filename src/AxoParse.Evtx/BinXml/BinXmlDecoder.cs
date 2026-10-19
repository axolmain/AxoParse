using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using AxoParse.Evtx.Evtx;

namespace AxoParse.Evtx.BinXml;

/// <summary>
/// Partial class containing the shared BinXml decoder: one token walk that renders record-level streams, template
/// bodies that can't be compiled, and embedded BinXml fragments through a per-format writer (<see cref="XmlWriter"/>,
/// <see cref="JsonWriter"/>). It replaces the separate XML and JSON interpreters, so direct rendering follows exactly
/// the same rules as the compiled path in each format.
/// <para>
/// Decoding is strict: unknown tokens inside content, nesting past <see cref="_maxRecursionDepth"/>, a name that runs
/// past the data, a substitution outside a template, and an element or template instance inside an attribute value
/// throw <see cref="InvalidDataException"/>, which fails only the current record (the chunk render loop reports it as
/// a diagnostic), as in the Rust evtx crate, instead of skipping bytes and rendering partial output.
/// </para>
/// <para>
/// In salvage mode the same corruption ends the enclosing BinXml stream instead (the record's own data, an embedded
/// fragment, or a template body): decoding stops there and unwinds normally, so every open element, attribute and
/// JSON container is closed and the record keeps a well-formed prefix. The first error is kept in
/// <see cref="SalvageDiagnostic"/>.
/// </para>
/// </summary>
internal sealed partial class BinXmlParser
{
    #region Non-Public Methods

    /// <summary>
    /// Decodes a top-level BinXml stream: a record's event data, or an embedded fragment (value type 0x21). Loops
    /// over fragment headers, template instances, bare elements and processing instructions until EOF or another
    /// token, which ends the stream (trailing bytes after a record's content are not an error). A record holds one
    /// event, so its stream also ends after the first template instance or element: anything after the root would
    /// make a second XML root or a second JSON value.
    /// </summary>
    /// <typeparam name="TWriter">Output format.</typeparam>
    /// <param name="data">BinXml bytes.</param>
    /// <param name="pos">Read position; advanced past consumed tokens.</param>
    /// <param name="binxmlChunkBase">Chunk-relative offset of <paramref name="data"/>.</param>
    /// <param name="vub">Output builder.</param>
    /// <param name="recordLevel">True for a record's event data: processing instructions are rendered and the stream
    /// ends after its root.</param>
    /// <param name="escaped">JSON only: emit the stream JSON-string-escaped (an embedded fragment's JSON is a string).</param>
    private void DecodeTopLevel<TWriter>(ReadOnlySpan<byte> data, ref int pos, int binxmlChunkBase, ref ValueUtf8Builder vub,
                                         bool recordLevel, bool escaped)
        where TWriter : struct, IBinXmlWriter
    {
        while (pos < data.Length)
        {
            byte baseTok = (byte)(data[pos] & ~BinXmlToken.HasMoreDataFlag);
            switch (baseTok)
            {
                case BinXmlToken.Eof:
                    return;

                case BinXmlToken.FragmentHeader:
                    pos += 4; // token + major + minor + flags
                    break;

                case BinXmlToken.TemplateInstance:
                    WriteTemplateInstance<TWriter>(data, ref pos, binxmlChunkBase, ref vub, escaped);
                    if (recordLevel)
                        return;
                    break;

                case BinXmlToken.OpenStartElement:
                    if (escaped)
                        DecodeElementEscaped<TWriter>(data, ref pos, binxmlChunkBase, ref vub);
                    else
                    {
                        TWriter writer = default;
                        DecodeElement(data, ref pos, default, binxmlChunkBase, ref vub, ref writer, hasDepId: false,
                            depth: 0, contentItem: false);
                    }
                    if (recordLevel)
                        return;
                    break;

                case BinXmlToken.PiTarget when recordLevel || TWriter.ConsumesProcessingInstructions:
                {
                    int start = pos;
                    pos++; // token
                    uint nameOffset = MemoryMarshal.Read<uint>(data[pos..]);
                    pos += 4;
                    if (!TrySkipInlineName(data, ref pos, nameOffset, binxmlChunkBase))
                    {
                        Corrupt("processing instruction name runs past the data", start);
                        pos = data.Length;
                        return;
                    }
                    scoped ReadOnlySpan<char> text = default;
                    if ((pos < data.Length) && (data[pos] == BinXmlToken.PiData))
                    {
                        pos++;
                        text = BinXmlValueFormatter.ReadUnicodeTextString(data, ref pos);
                    }
                    TWriter.WriteProcessingInstruction(this, nameOffset, text, ref vub);
                    break;
                }

                default:
                    return;
            }
        }
    }

    /// <summary>
    /// Decodes a bare top-level element for escaped JSON output: renders it unescaped into a temporary buffer, then
    /// JSON-escapes the result. Kept out of the <see cref="DecodeTopLevel{TWriter}"/> loop so its stack buffer isn't
    /// allocated per iteration.
    /// </summary>
    /// <typeparam name="TWriter">Output format.</typeparam>
    /// <param name="data">BinXml bytes.</param>
    /// <param name="pos">Read position; advanced past the element.</param>
    /// <param name="binxmlChunkBase">Chunk-relative offset of <paramref name="data"/>.</param>
    /// <param name="vub">Output builder that receives the escaped JSON.</param>
    private void DecodeElementEscaped<TWriter>(ReadOnlySpan<byte> data, ref int pos, int binxmlChunkBase, ref ValueUtf8Builder vub)
        where TWriter : struct, IBinXmlWriter
    {
        ValueUtf8Builder temp = new(stackalloc byte[512]);
        TWriter writer = default;
        DecodeElement(data, ref pos, default, binxmlChunkBase, ref temp, ref writer, hasDepId: false, depth: 0,
            contentItem: false);
        BinXmlValueFormatter.AppendJsonEscapedUtf8(ref vub, temp.AsSpan());
        temp.Dispose();
    }

    /// <summary>
    /// Decodes a template body that can't be compiled: an optional fragment header, then its root element, whose
    /// elements carry dependency IDs and whose substitutions take <paramref name="values"/>. A template describes one
    /// event, so anything after the root is ignored, as the compiler does; a body that doesn't start with an element is
    /// corrupt.
    /// </summary>
    /// <typeparam name="TWriter">Output format.</typeparam>
    /// <param name="body">Template body bytes.</param>
    /// <param name="values">Substitution values of the template instance.</param>
    /// <param name="binxmlChunkBase">Chunk-relative offset of <paramref name="body"/>.</param>
    /// <param name="vub">Output builder.</param>
    private void DecodeTemplateBody<TWriter>(ReadOnlySpan<byte> body, scoped SubstitutionValues values, int binxmlChunkBase,
                                             ref ValueUtf8Builder vub)
        where TWriter : struct, IBinXmlWriter
    {
        int pos = (body.Length >= 4) && (body[0] == BinXmlToken.FragmentHeader) ? 4 : 0;
        if ((pos >= body.Length) || (body[pos] == BinXmlToken.Eof))
            return;

        if ((byte)(body[pos] & ~BinXmlToken.HasMoreDataFlag) != BinXmlToken.OpenStartElement)
        {
            Corrupt($"template body starts with token 0x{body[pos]:X2}, not an element", pos);
            return;
        }

        TWriter writer = default;
        DecodeElement(body, ref pos, values, binxmlChunkBase, ref vub, ref writer, hasDepId: true, depth: 1,
            contentItem: false);
    }

    /// <summary>
    /// Decodes BinXml content tokens (child elements, text, substitutions, character and entity references, CDATA,
    /// nested template instances) until a token that ends the enclosing construct: EOF, close/end element, or the next
    /// attribute.
    /// </summary>
    /// <typeparam name="TWriter">Output format.</typeparam>
    /// <param name="data">BinXml bytes.</param>
    /// <param name="pos">Read position; advanced past consumed tokens.</param>
    /// <param name="values">Substitution values of the enclosing template instance; empty outside a template.</param>
    /// <param name="binxmlChunkBase">Chunk-relative offset of <paramref name="data"/>.</param>
    /// <param name="vub">Output builder.</param>
    /// <param name="writer">Format writer and its state.</param>
    /// <param name="hasDepId">True inside a template body, whose elements carry a 2-byte dependency ID.</param>
    /// <param name="depth">Element nesting depth.</param>
    /// <param name="inAttr">True when decoding an attribute value.</param>
    private void DecodeContent<TWriter>(ReadOnlySpan<byte> data, ref int pos, scoped SubstitutionValues values,
                                        int binxmlChunkBase, ref ValueUtf8Builder vub, ref TWriter writer, bool hasDepId,
                                        int depth, bool inAttr)
        where TWriter : struct, IBinXmlWriter
    {
        while (pos < data.Length)
        {
            byte baseTok = (byte)(data[pos] & ~BinXmlToken.HasMoreDataFlag);
            if ((baseTok == BinXmlToken.Eof) ||
                (baseTok == BinXmlToken.CloseStartElement) ||
                (baseTok == BinXmlToken.CloseEmptyElement) ||
                (baseTok == BinXmlToken.EndElement) ||
                (baseTok == BinXmlToken.Attribute))
                break;

            switch (baseTok)
            {
                case BinXmlToken.OpenStartElement:
                    if (inAttr)
                    {
                        Corrupt("element inside an attribute value", pos);
                        pos = data.Length;
                        break;
                    }
                    DecodeElement(data, ref pos, values, binxmlChunkBase, ref vub, ref writer, hasDepId, depth + 1,
                        contentItem: true);
                    break;

                case BinXmlToken.Value:
                    pos += 2; // token + value type
                    writer.Text(ref vub, BinXmlValueFormatter.ReadUnicodeTextString(data, ref pos));
                    break;

                case BinXmlToken.NormalSubstitution:
                case BinXmlToken.OptionalSubstitution:
                {
                    if (!values.Present)
                    {
                        Corrupt("substitution outside a template", pos);
                        pos = data.Length;
                        break;
                    }
                    bool optional = baseTok == BinXmlToken.OptionalSubstitution;
                    ushort subId = MemoryMarshal.Read<ushort>(data[(pos + 1)..]);
                    pos += 4; // token + id + value type
                    // Same rule as the compiled executor: an index past the instance's values writes nothing, as
                    // does an empty optional value
                    writer.BeginValue(ref vub);
                    if (subId < values.Offsets.Length)
                    {
                        byte valueType = values.Types[subId];
                        int valueSize = values.Sizes[subId];
                        if (!optional || ((valueType != BinXmlValueType.Null) && (valueSize > 0)))
                            WriteValue<TWriter>(valueSize, valueType, values.Offsets[subId], binxmlChunkBase, ref vub,
                                escaped: false);
                    }
                    writer.EndValue(ref vub);
                    break;
                }

                case BinXmlToken.CharRef:
                    writer.CharRef(ref vub, MemoryMarshal.Read<ushort>(data[(pos + 1)..]));
                    pos += 3;
                    break;

                case BinXmlToken.EntityRef:
                {
                    int start = pos;
                    uint nameOffset = MemoryMarshal.Read<uint>(data[(pos + 1)..]);
                    pos += 5;
                    // Entity names use the same name structure as element names, so they can be defined inline too
                    if (!TrySkipInlineName(data, ref pos, nameOffset, binxmlChunkBase))
                    {
                        Corrupt("entity name runs past the data", start);
                        pos = data.Length;
                        break;
                    }
                    writer.EntityRef(ref vub, ReadName(nameOffset));
                    break;
                }

                case BinXmlToken.CDataSection:
                    pos++;
                    writer.CData(ref vub, BinXmlValueFormatter.ReadUnicodeTextString(data, ref pos));
                    break;

                case BinXmlToken.TemplateInstance:
                    if (inAttr)
                    {
                        Corrupt("template instance inside an attribute value", pos);
                        pos = data.Length;
                        break;
                    }
                    writer.TemplateInstanceItem(this, data, ref pos, binxmlChunkBase, ref vub);
                    break;

                case BinXmlToken.FragmentHeader:
                    pos += 4; // the fragment's template instances and elements follow as content
                    break;

                default:
                    Corrupt($"unexpected token 0x{data[pos]:X2}", pos);
                    pos = data.Length;
                    break;
            }
        }
    }

    /// <summary>
    /// Decodes one element: OpenStartElement (0x01/0x41) [+ 2 dependency ID] + 4 data size + 4 name offset
    /// [+ inline name] [+ 4 attribute list size + attributes], then either CloseStartElement + content + EndElement or
    /// an empty close (CloseEmptyElement, end of data, or any other token).
    /// </summary>
    /// <typeparam name="TWriter">Output format.</typeparam>
    /// <param name="data">BinXml bytes.</param>
    /// <param name="pos">Read position; advanced past the element.</param>
    /// <param name="values">Substitution values of the enclosing template instance; empty outside a template.</param>
    /// <param name="binxmlChunkBase">Chunk-relative offset of <paramref name="data"/>.</param>
    /// <param name="vub">Output builder.</param>
    /// <param name="writer">Format writer and its state.</param>
    /// <param name="hasDepId">True inside a template body, whose elements carry a 2-byte dependency ID.</param>
    /// <param name="depth">Element nesting depth.</param>
    /// <param name="contentItem">True when the element is an item of element content (JSON separates items).</param>
    private void DecodeElement<TWriter>(ReadOnlySpan<byte> data, ref int pos, scoped SubstitutionValues values,
                                        int binxmlChunkBase, ref ValueUtf8Builder vub, ref TWriter writer, bool hasDepId,
                                        int depth, bool contentItem)
        where TWriter : struct, IBinXmlWriter
    {
        if (depth >= _maxRecursionDepth)
        {
            Corrupt("elements nested too deeply", pos);
            pos = data.Length;
            return;
        }

        bool hasAttrs = (data[pos] & BinXmlToken.HasMoreDataFlag) != 0;
        int start = pos;
        pos++; // token
        if (hasDepId)
            pos += 2;
        pos += 4; // data size
        uint nameOffset = MemoryMarshal.Read<uint>(data[pos..]);
        pos += 4;
        if (!TrySkipInlineName(data, ref pos, nameOffset, binxmlChunkBase))
        {
            Corrupt("element name runs past the data", start);
            pos = data.Length;
            return;
        }

        string name = ReadName(nameOffset);
        if (contentItem)
            writer.BeginContentItem(ref vub);
        writer.StartElement(ref vub, name);

        if (hasAttrs)
        {
            uint attrListSize = MemoryMarshal.Read<uint>(data[pos..]);
            pos += 4;
            int attrEnd = pos + (int)attrListSize;
            writer.BeginAttributes(ref vub);

            while (pos < attrEnd)
            {
                if ((byte)(data[pos] & ~BinXmlToken.HasMoreDataFlag) != BinXmlToken.Attribute)
                    break;

                int attrStart = pos;
                pos++; // token
                uint attrNameOffset = MemoryMarshal.Read<uint>(data[pos..]);
                pos += 4;
                if (!TrySkipInlineName(data, ref pos, attrNameOffset, binxmlChunkBase))
                {
                    // The element is already open; ending the stream here still closes it below
                    Corrupt("attribute name runs past the data", attrStart);
                    pos = data.Length;
                    break;
                }

                if (TWriter.OmitsEmptyOptionalAttributes && IsEmptyOptionalAttribute(data, pos, values, out int afterValue))
                {
                    pos = afterValue;
                    continue;
                }

                writer.Attribute(ref vub, ReadName(attrNameOffset));
                DecodeContent(data, ref pos, values, binxmlChunkBase, ref vub, ref writer, hasDepId, depth + 1, inAttr: true);
                writer.EndAttribute(ref vub);
            }

            writer.EndAttributes(ref vub);
        }

        if ((pos < data.Length) && (data[pos] == BinXmlToken.CloseStartElement))
        {
            pos++;
            writer.CloseStartElement(ref vub);
            DecodeContent(data, ref pos, values, binxmlChunkBase, ref vub, ref writer, hasDepId, depth + 1, inAttr: false);
            if ((pos < data.Length) && (data[pos] == BinXmlToken.EndElement))
                pos++;
            writer.EndElement(ref vub, name);
            return;
        }

        if ((pos < data.Length) && (data[pos] == BinXmlToken.CloseEmptyElement))
            pos++;
        writer.CloseEmptyElement(ref vub, name);
    }

    /// <summary>
    /// Checks whether an attribute's whole value is one optional substitution (0x0E) whose value is null, empty, or
    /// past the instance's values; XML omits such an attribute entirely, matching the compiled path's attribute slots.
    /// </summary>
    /// <param name="data">BinXml bytes.</param>
    /// <param name="pos">Position of the attribute's first value token.</param>
    /// <param name="values">Substitution values of the enclosing template instance.</param>
    /// <param name="afterValue">Position just past the substitution token, when true.</param>
    /// <returns>True if the attribute should be omitted.</returns>
    private static bool IsEmptyOptionalAttribute(ReadOnlySpan<byte> data, int pos, scoped SubstitutionValues values, out int afterValue)
    {
        afterValue = pos + 4; // token + id + value type
        if (!values.Present || (pos + 3 >= data.Length) ||
            ((data[pos] & ~BinXmlToken.HasMoreDataFlag) != BinXmlToken.OptionalSubstitution))
            return false;

        byte next = afterValue < data.Length ? (byte)(data[afterValue] & ~BinXmlToken.HasMoreDataFlag) : BinXmlToken.Eof;
        bool onlyToken = (next == BinXmlToken.Eof) || (next == BinXmlToken.CloseStartElement) ||
                         (next == BinXmlToken.CloseEmptyElement) || (next == BinXmlToken.EndElement) ||
                         (next == BinXmlToken.Attribute);
        ushort subId = MemoryMarshal.Read<ushort>(data[(pos + 1)..]);
        return onlyToken && ((subId >= values.Offsets.Length) ||
                             (values.Types[subId] == BinXmlValueType.Null) || (values.Sizes[subId] == 0));
    }

    /// <summary>
    /// Compiles a template program for XML output (see <see cref="Lower{TWriter}"/>).
    /// </summary>
    /// <param name="program">The template's program.</param>
    /// <returns>The compiled template, or <see cref="CompiledTemplate.DecodeDirectly"/>.</returns>
    internal static CompiledTemplate CompileXml(TemplateProgram program) => Lower<XmlWriter>(program);

    /// <summary>
    /// Compiles a template program for JSON output (see <see cref="Lower{TWriter}"/>).
    /// </summary>
    /// <param name="program">The template's program.</param>
    /// <returns>The compiled template, or <see cref="CompiledTemplate.DecodeDirectly"/>.</returns>
    internal static CompiledTemplate CompileJson(TemplateProgram program) => Lower<JsonWriter>(program);

    /// <summary>
    /// Turns a template program into a compiled template by replaying it through the format's writer: the writer
    /// receives exactly the events the decoder sends for the template body, with every value left out, and the output
    /// between values becomes the static parts. Compiled and decoded rendering therefore follow one set of rules. In
    /// XML, an attribute whose whole value is one optional substitution has its markup moved into the slot, so it is
    /// omitted when the value is empty, as the decoder does. A program whose names or text contain surrogate code units
    /// isn't split into parts, since a surrogate at a part's edge may pair with a neighbouring value; it returns
    /// <see cref="CompiledTemplate.DecodeDirectly"/>.
    /// </summary>
    /// <typeparam name="TWriter">Output format.</typeparam>
    /// <param name="program">The template's program.</param>
    /// <returns>The compiled template, or <see cref="CompiledTemplate.DecodeDirectly"/>.</returns>
    private static CompiledTemplate Lower<TWriter>(TemplateProgram program)
        where TWriter : struct, IBinXmlWriter
    {
        TemplateOp[] ops = program.Ops;
        if (ContainsSurrogates(ops))
            return CompiledTemplate.DecodeDirectly;

        ValueUtf8Builder vub = new(stackalloc byte[1024]);
        // Per slot: where the preceding part ends, where its conditional prefix ends, and where its suffix ends (the
        // next part's start). A plain slot has neither, so all three are equal.
        List<(int PartEnd, int PrefixEnd, int SuffixEnd, int SubId, bool IsOptional, bool Conditional)> slots = new();
        TWriter writer = default;
        int depth = 0;
        for (int i = 0; i < ops.Length; i++)
        {
            TemplateOp op = ops[i];
            switch (op.Kind)
            {
                case TemplateOpKind.StartElement:
                    // The root is not a content item; every other element is (the compiler rejects elements in values)
                    if (depth > 0)
                        writer.BeginContentItem(ref vub);
                    writer.StartElement(ref vub, op.Text!);
                    depth++;
                    break;

                case TemplateOpKind.BeginAttributes:
                    writer.BeginAttributes(ref vub);
                    break;

                case TemplateOpKind.Attribute:
                    if (TWriter.OmitsEmptyOptionalAttributes && (i + 2 < ops.Length) &&
                        (ops[i + 1].Kind == TemplateOpKind.Substitution) && ops[i + 1].IsOptional &&
                        (ops[i + 2].Kind == TemplateOpKind.EndAttribute))
                    {
                        int partEnd = vub.AsSpan().Length;
                        writer.Attribute(ref vub, op.Text!);
                        writer.BeginValue(ref vub);
                        int prefixEnd = vub.AsSpan().Length;
                        writer.EndValue(ref vub);
                        writer.EndAttribute(ref vub);
                        slots.Add((partEnd, prefixEnd, vub.AsSpan().Length, ops[i + 1].SubId, true, true));
                        i += 2;
                        break;
                    }
                    writer.Attribute(ref vub, op.Text!);
                    break;

                case TemplateOpKind.EndAttribute:
                    writer.EndAttribute(ref vub);
                    break;

                case TemplateOpKind.EndAttributes:
                    writer.EndAttributes(ref vub);
                    break;

                case TemplateOpKind.CloseStartElement:
                    writer.CloseStartElement(ref vub);
                    break;

                case TemplateOpKind.EndElement:
                    depth--;
                    writer.EndElement(ref vub, op.Text!);
                    break;

                case TemplateOpKind.CloseEmptyElement:
                    depth--;
                    writer.CloseEmptyElement(ref vub, op.Text!);
                    break;

                case TemplateOpKind.Text:
                    writer.Text(ref vub, op.Text);
                    break;

                case TemplateOpKind.CharRef:
                    writer.CharRef(ref vub, op.CharValue);
                    break;

                case TemplateOpKind.EntityRef:
                    writer.EntityRef(ref vub, op.Text!);
                    break;

                case TemplateOpKind.CData:
                    writer.CData(ref vub, op.Text);
                    break;

                case TemplateOpKind.Substitution:
                {
                    writer.BeginValue(ref vub);
                    int at = vub.AsSpan().Length;
                    slots.Add((at, at, at, op.SubId, op.IsOptional, false));
                    writer.EndValue(ref vub);
                    break;
                }
            }
        }

        ReadOnlySpan<byte> output = vub.AsSpan();
        byte[][] parts = new byte[slots.Count + 1][];
        SubSlot[] compiled = new SubSlot[slots.Count];
        int partStart = 0;
        for (int i = 0; i < slots.Count; i++)
        {
            (int partEnd, int prefixEnd, int suffixEnd, int subId, bool isOptional, bool conditional) = slots[i];
            parts[i] = output[partStart..partEnd].ToArray();
            compiled[i] = conditional
                ? new SubSlot(subId, isOptional, output[partEnd..prefixEnd].ToArray(), output[prefixEnd..suffixEnd].ToArray())
                : new SubSlot(subId, isOptional, null, null);
            partStart = suffixEnd;
        }
        parts[^1] = output[partStart..].ToArray();
        vub.Dispose();
        return new CompiledTemplate(parts, compiled);
    }

    /// <summary>
    /// Checks whether any name, text or character reference of a program is a UTF-16 surrogate code unit.
    /// </summary>
    /// <param name="ops">The program's ops.</param>
    /// <returns>True if any surrogate is present.</returns>
    private static bool ContainsSurrogates(TemplateOp[] ops)
    {
        foreach (TemplateOp op in ops)
        {
            if (char.IsSurrogate((char)op.CharValue) ||
                ((op.Text != null) && op.Text.AsSpan().ContainsAnyInRange('\uD800', '\uDFFF')))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Reports corrupt BinXml. Strict mode throws, failing only the current record; salvage mode keeps the first
    /// error in <see cref="_salvageDiagnostic"/> and returns, and the caller then ends the enclosing stream.
    /// </summary>
    /// <param name="what">What was wrong.</param>
    /// <param name="pos">Position in the BinXml stream.</param>
    private void Corrupt(string what, int pos)
    {
        if (!_salvage)
            ThrowCorrupt(what, pos);
        _salvageDiagnostic ??= CorruptMessage(what, pos);
    }

    /// <summary>
    /// Throws for corrupt BinXml (kept out of line so the checks stay small).
    /// </summary>
    /// <param name="what">What was wrong.</param>
    /// <param name="pos">Position in the BinXml stream.</param>
    [DoesNotReturn]
    private static void ThrowCorrupt(string what, int pos) => throw new InvalidDataException(CorruptMessage(what, pos));

    /// <summary>
    /// Formats a corrupt-BinXml message.
    /// </summary>
    /// <param name="what">What was wrong.</param>
    /// <param name="pos">Position in the BinXml stream.</param>
    /// <returns>The message.</returns>
    private static string CorruptMessage(string what, int pos) => $"Corrupt BinXml at +0x{pos:X}: {what}";

    #endregion

    #region Nested Types

    /// <summary>
    /// The substitution values of a template instance, as spans over the stack buffers filled by
    /// <see cref="ReadTemplateInstanceData"/>. <see cref="Present"/> is false outside a template (record-level content
    /// and embedded fragments), where substitution tokens are corrupt.
    /// </summary>
    /// <param name="offsets">File offset of each value.</param>
    /// <param name="sizes">Byte size of each value.</param>
    /// <param name="types">BinXml value type of each value.</param>
    private readonly ref struct SubstitutionValues(ReadOnlySpan<int> offsets, ReadOnlySpan<int> sizes, ReadOnlySpan<byte> types)
    {
        /// <summary>
        /// True inside a template instance.
        /// </summary>
        public bool Present { get; } = true;

        /// <summary>
        /// File offset of each value.
        /// </summary>
        public ReadOnlySpan<int> Offsets { get; } = offsets;

        /// <summary>
        /// Byte size of each value.
        /// </summary>
        public ReadOnlySpan<int> Sizes { get; } = sizes;

        /// <summary>
        /// BinXml value type of each value.
        /// </summary>
        public ReadOnlySpan<byte> Types { get; } = types;
    }

    /// <summary>
    /// Renders decoder events in one output format. Implemented by value-type writers so each format gets its own
    /// specialised copy of the decoder (no virtual calls). Instance members carry per-stream state (JSON item commas);
    /// static members describe the format.
    /// </summary>
    private interface IBinXmlWriter
    {
        /// <summary>
        /// True if an attribute whose whole value is an empty optional substitution is omitted (XML).
        /// </summary>
        static abstract bool OmitsEmptyOptionalAttributes { get; }

        /// <summary>
        /// True if processing instructions are consumed in embedded fragments too (JSON skips them everywhere).
        /// </summary>
        static abstract bool ConsumesProcessingInstructions { get; }

        /// <summary>
        /// The output format this writer produces (selects the format's provider templates).
        /// </summary>
        static abstract OutputFormat Format { get; }

        /// <summary>
        /// True if HexInt32/HexInt64 values are zero-padded to 8/16 digits (JSON) rather than minimal (XML).
        /// </summary>
        static abstract bool PadsHexIntegers { get; }

        /// <summary>
        /// Appends a string value's text, escaped for the format.
        /// </summary>
        /// <param name="vub">Output builder.</param>
        /// <param name="text">Unescaped UTF-16 text.</param>
        /// <param name="escaped">JSON only: escape twice (see <see cref="WriteValue{TFormat}"/>).</param>
        static abstract void AppendString(ref ValueUtf8Builder vub, scoped ReadOnlySpan<char> text, bool escaped);

        /// <summary>
        /// Appends a NUL-terminated ANSI string value's text, escaped for the format.
        /// </summary>
        /// <param name="vub">Output builder.</param>
        /// <param name="bytes">ANSI bytes (U+0000..U+00FF).</param>
        /// <param name="escaped">JSON only: escape twice.</param>
        static abstract void AppendAnsiString(ref ValueUtf8Builder vub, ReadOnlySpan<byte> bytes, bool escaped);

        /// <summary>
        /// Renders an embedded BinXml value (type 0x21) as the value's text.
        /// </summary>
        /// <param name="parser">Parser for the chunk.</param>
        /// <param name="bytes">The fragment's bytes.</param>
        /// <param name="binxmlChunkBase">Chunk-relative offset of <paramref name="bytes"/>.</param>
        /// <param name="vub">Output builder.</param>
        /// <param name="escaped">JSON only: the value is already one JSON-string level deep.</param>
        static abstract void WriteEmbeddedBinXml(BinXmlParser parser, ReadOnlySpan<byte> bytes, int binxmlChunkBase,
                                                 ref ValueUtf8Builder vub, bool escaped);

        /// <summary>
        /// Renders a processing instruction (record level only in XML; JSON drops it).
        /// </summary>
        static abstract void WriteProcessingInstruction(BinXmlParser parser, uint nameOffset, scoped ReadOnlySpan<char> text,
                                                        ref ValueUtf8Builder vub);

        /// <summary>Called before an item of element content (JSON writes the separating comma).</summary>
        void BeginContentItem(ref ValueUtf8Builder vub);

        /// <summary>A template instance (0x0C) as an item of element content.</summary>
        void TemplateInstanceItem(BinXmlParser parser, ReadOnlySpan<byte> data, ref int pos, int binxmlChunkBase,
                                  ref ValueUtf8Builder vub);

        /// <summary>Element start.</summary>
        void StartElement(ref ValueUtf8Builder vub, string name);

        /// <summary>The element's attribute list begins (the 0x40 flag was set).</summary>
        void BeginAttributes(ref ValueUtf8Builder vub);

        /// <summary>An attribute begins; its value follows.</summary>
        void Attribute(ref ValueUtf8Builder vub, string name);

        /// <summary>The current attribute's value ends.</summary>
        void EndAttribute(ref ValueUtf8Builder vub);

        /// <summary>The attribute list ends.</summary>
        void EndAttributes(ref ValueUtf8Builder vub);

        /// <summary>Child content follows.</summary>
        void CloseStartElement(ref ValueUtf8Builder vub);

        /// <summary>End of an element that had child content.</summary>
        void EndElement(ref ValueUtf8Builder vub, string name);

        /// <summary>End of an element with no child content.</summary>
        void CloseEmptyElement(ref ValueUtf8Builder vub, string name);

        /// <summary>Literal text (unescaped).</summary>
        void Text(ref ValueUtf8Builder vub, scoped ReadOnlySpan<char> text);

        /// <summary>Character reference.</summary>
        void CharRef(ref ValueUtf8Builder vub, ushort value);

        /// <summary>Entity reference.</summary>
        void EntityRef(ref ValueUtf8Builder vub, string name);

        /// <summary>CDATA section text.</summary>
        void CData(ref ValueUtf8Builder vub, scoped ReadOnlySpan<char> text);

        /// <summary>
        /// A substitution begins; its value's text (possibly none) follows, then <see cref="EndValue"/>. In JSON content
        /// this opens a string item; in a compiled template, the value's slot sits between the two.
        /// </summary>
        void BeginValue(ref ValueUtf8Builder vub);

        /// <summary>The current substitution's value ends.</summary>
        void EndValue(ref ValueUtf8Builder vub);
    }

    /// <summary>
    /// XML output, matching the compiled XML templates: text and values XML-escaped, character references as
    /// <c>&amp;#N;</c>, entities kept as references, empty elements as <c>&lt;a&gt;&lt;/a&gt;</c>.
    /// </summary>
    private struct XmlWriter : IBinXmlWriter
    {
        /// <inheritdoc/>
        public static bool OmitsEmptyOptionalAttributes => true;

        /// <inheritdoc/>
        public static bool ConsumesProcessingInstructions => false;

        /// <inheritdoc/>
        public static OutputFormat Format => OutputFormat.Xml;

        /// <inheritdoc/>
        public static bool PadsHexIntegers => false;

        /// <inheritdoc/>
        public static void AppendString(ref ValueUtf8Builder vub, scoped ReadOnlySpan<char> text, bool escaped) =>
            BinXmlValueFormatter.AppendXmlEscaped(ref vub, text);

        /// <inheritdoc/>
        public static void AppendAnsiString(ref ValueUtf8Builder vub, ReadOnlySpan<byte> bytes, bool escaped) =>
            BinXmlValueFormatter.AppendAnsiXmlEscaped(ref vub, bytes);

        /// <inheritdoc/>
        public static void WriteEmbeddedBinXml(BinXmlParser parser, ReadOnlySpan<byte> bytes, int binxmlChunkBase,
                                               ref ValueUtf8Builder vub, bool escaped)
        {
            int pos = 0;
            parser.DecodeTopLevel<XmlWriter>(bytes, ref pos, binxmlChunkBase, ref vub, recordLevel: false, escaped: false);
        }

        /// <inheritdoc/>
        public static void WriteProcessingInstruction(BinXmlParser parser, uint nameOffset, scoped ReadOnlySpan<char> text,
                                                      ref ValueUtf8Builder vub)
        {
            vub.Append("<?"u8);
            vub.AppendUtf16(parser.ReadName(nameOffset));
            if (text.Length > 0)
            {
                vub.Append((byte)' ');
                vub.AppendUtf16(text);
            }
            vub.Append("?>"u8);
        }

        /// <inheritdoc/>
        public void BeginContentItem(ref ValueUtf8Builder vub) { }

        /// <inheritdoc/>
        public void TemplateInstanceItem(BinXmlParser parser, ReadOnlySpan<byte> data, ref int pos, int binxmlChunkBase,
                                         ref ValueUtf8Builder vub) =>
            parser.WriteTemplateInstance<XmlWriter>(data, ref pos, binxmlChunkBase, ref vub, escaped: false);

        /// <inheritdoc/>
        public void StartElement(ref ValueUtf8Builder vub, string name)
        {
            vub.Append((byte)'<');
            vub.AppendUtf16(name);
        }

        /// <inheritdoc/>
        public void BeginAttributes(ref ValueUtf8Builder vub) { }

        /// <inheritdoc/>
        public void Attribute(ref ValueUtf8Builder vub, string name)
        {
            vub.Append((byte)' ');
            vub.AppendUtf16(name);
            vub.Append("=\""u8);
        }

        /// <inheritdoc/>
        public void EndAttribute(ref ValueUtf8Builder vub) => vub.Append((byte)'"');

        /// <inheritdoc/>
        public void EndAttributes(ref ValueUtf8Builder vub) { }

        /// <inheritdoc/>
        public void CloseStartElement(ref ValueUtf8Builder vub) => vub.Append((byte)'>');

        /// <inheritdoc/>
        public void EndElement(ref ValueUtf8Builder vub, string name)
        {
            vub.Append("</"u8);
            vub.AppendUtf16(name);
            vub.Append((byte)'>');
        }

        /// <inheritdoc/>
        public void CloseEmptyElement(ref ValueUtf8Builder vub, string name)
        {
            vub.Append("></"u8);
            vub.AppendUtf16(name);
            vub.Append((byte)'>');
        }

        /// <inheritdoc/>
        public void Text(ref ValueUtf8Builder vub, scoped ReadOnlySpan<char> text) => BinXmlValueFormatter.AppendXmlEscaped(ref vub, text);

        /// <inheritdoc/>
        public void CharRef(ref ValueUtf8Builder vub, ushort value)
        {
            vub.Append("&#"u8);
            vub.AppendFormatted(value);
            vub.Append((byte)';');
        }

        /// <inheritdoc/>
        public void EntityRef(ref ValueUtf8Builder vub, string name)
        {
            vub.Append((byte)'&');
            vub.AppendUtf16(name);
            vub.Append((byte)';');
        }

        /// <inheritdoc/>
        public void CData(ref ValueUtf8Builder vub, scoped ReadOnlySpan<char> text)
        {
            vub.Append("<![CDATA["u8);
            vub.AppendUtf16(text);
            vub.Append("]]>"u8);
        }

        /// <inheritdoc/>
        public void BeginValue(ref ValueUtf8Builder vub) { }

        /// <inheritdoc/>
        public void EndValue(ref ValueUtf8Builder vub) { }
    }

    /// <summary>
    /// JSON output, matching the compiled JSON templates: the <c>{"#name":…,"#attrs":{…},"#content":[…]}</c> shape,
    /// content items comma-separated and written as strings, attribute values as one string, entities resolved.
    /// </summary>
    private struct JsonWriter : IBinXmlWriter
    {
        /// <inheritdoc/>
        public static bool OmitsEmptyOptionalAttributes => false;

        /// <inheritdoc/>
        public static bool ConsumesProcessingInstructions => true;

        /// <inheritdoc/>
        public static OutputFormat Format => OutputFormat.Json;

        /// <inheritdoc/>
        public static bool PadsHexIntegers => true;

        /// <inheritdoc/>
        public static void AppendString(ref ValueUtf8Builder vub, scoped ReadOnlySpan<char> text, bool escaped)
        {
            if (escaped)
                BinXmlValueFormatter.AppendJsonEscapedTwice(ref vub, text);
            else
                BinXmlValueFormatter.AppendJsonEscaped(ref vub, text);
        }

        /// <inheritdoc/>
        public static void AppendAnsiString(ref ValueUtf8Builder vub, ReadOnlySpan<byte> bytes, bool escaped)
        {
            if (escaped)
                BinXmlValueFormatter.AppendAnsiJsonEscapedTwice(ref vub, bytes);
            else
                BinXmlValueFormatter.AppendAnsiJsonEscaped(ref vub, bytes);
        }

        /// <inheritdoc/>
        public static void WriteEmbeddedBinXml(BinXmlParser parser, ReadOnlySpan<byte> bytes, int binxmlChunkBase,
                                               ref ValueUtf8Builder vub, bool escaped)
        {
            // The fragment's JSON is a JSON string: written pre-escaped (compiled templates use their escaped parts)
            int pos = 0;
            if (!escaped)
            {
                parser.DecodeTopLevel<JsonWriter>(bytes, ref pos, binxmlChunkBase, ref vub, recordLevel: false, escaped: true);
                return;
            }

            // Already one escape level deep: render the first level into a temp buffer, then escape again
            ValueUtf8Builder temp = new(stackalloc byte[512]);
            parser.DecodeTopLevel<JsonWriter>(bytes, ref pos, binxmlChunkBase, ref temp, recordLevel: false, escaped: true);
            BinXmlValueFormatter.AppendJsonEscapedUtf8(ref vub, temp.AsSpan());
            temp.Dispose();
        }

        /// <inheritdoc/>
        public static void WriteProcessingInstruction(BinXmlParser parser, uint nameOffset, scoped ReadOnlySpan<char> text,
                                                      ref ValueUtf8Builder vub) { }

        /// <inheritdoc/>
        public void BeginContentItem(ref ValueUtf8Builder vub)
        {
            if (_inAttr)
                return;
            if (_needsComma[_level])
                vub.Append((byte)',');
            _needsComma[_level] = true;
        }

        /// <inheritdoc/>
        public void TemplateInstanceItem(BinXmlParser parser, ReadOnlySpan<byte> data, ref int pos, int binxmlChunkBase,
                                         ref ValueUtf8Builder vub)
        {
            BeginContentItem(ref vub);
            int before = vub.AsSpan().Length;
            parser.WriteTemplateInstance<JsonWriter>(data, ref pos, binxmlChunkBase, ref vub, escaped: false);
            // A template that renders nothing (unavailable, empty) still fills the item its comma opened
            if (vub.AsSpan().Length == before)
                vub.Append("\"\""u8);
        }

        /// <inheritdoc/>
        public void StartElement(ref ValueUtf8Builder vub, string name)
        {
            vub.Append("{\"#name\":\""u8);
            BinXmlValueFormatter.AppendJsonEscaped(ref vub, name.AsSpan());
            vub.Append((byte)'"');
        }

        /// <inheritdoc/>
        public void BeginAttributes(ref ValueUtf8Builder vub)
        {
            vub.Append(",\"#attrs\":{"u8);
            _firstAttr = true;
        }

        /// <inheritdoc/>
        public void Attribute(ref ValueUtf8Builder vub, string name)
        {
            if (!_firstAttr)
                vub.Append((byte)',');
            _firstAttr = false;
            vub.Append((byte)'"');
            BinXmlValueFormatter.AppendJsonEscaped(ref vub, name.AsSpan());
            vub.Append("\":\""u8);
            _inAttr = true;
        }

        /// <inheritdoc/>
        public void EndAttribute(ref ValueUtf8Builder vub)
        {
            _inAttr = false;
            vub.Append((byte)'"');
        }

        /// <inheritdoc/>
        public void EndAttributes(ref ValueUtf8Builder vub) => vub.Append((byte)'}');

        /// <inheritdoc/>
        public void CloseStartElement(ref ValueUtf8Builder vub)
        {
            vub.Append(",\"#content\":["u8);
            _level++;
            _needsComma[_level] = false;
        }

        /// <inheritdoc/>
        public void EndElement(ref ValueUtf8Builder vub, string name)
        {
            _level--;
            vub.Append("]}"u8);
        }

        /// <inheritdoc/>
        public void CloseEmptyElement(ref ValueUtf8Builder vub, string name) => vub.Append((byte)'}');

        /// <inheritdoc/>
        public void Text(ref ValueUtf8Builder vub, scoped ReadOnlySpan<char> text) => WriteString(ref vub, text);

        /// <inheritdoc/>
        public void CharRef(ref ValueUtf8Builder vub, ushort value)
        {
            char ch = (char)value;
            WriteString(ref vub, new ReadOnlySpan<char>(in ch));
        }

        /// <inheritdoc/>
        public void EntityRef(ref ValueUtf8Builder vub, string name) => WriteString(ref vub, name switch
        {
            "amp" => "&",
            "lt" => "<",
            "gt" => ">",
            "quot" => "\"",
            "apos" => "'",
            _ => $"&{name};"
        });

        /// <inheritdoc/>
        public void CData(ref ValueUtf8Builder vub, scoped ReadOnlySpan<char> text) => WriteString(ref vub, text);

        /// <inheritdoc/>
        public void BeginValue(ref ValueUtf8Builder vub)
        {
            // Inside an attribute the value is part of the attribute's string; in content it is a string item
            if (_inAttr)
                return;
            BeginContentItem(ref vub);
            vub.Append((byte)'"');
        }

        /// <inheritdoc/>
        public void EndValue(ref ValueUtf8Builder vub)
        {
            if (!_inAttr)
                vub.Append((byte)'"');
        }

        /// <summary>
        /// Writes text JSON-escaped: inside an attribute value as part of its string, in content as a string item.
        /// </summary>
        /// <param name="vub">Output builder.</param>
        /// <param name="text">Unescaped text.</param>
        private void WriteString(ref ValueUtf8Builder vub, scoped ReadOnlySpan<char> text)
        {
            if (_inAttr)
            {
                BinXmlValueFormatter.AppendJsonEscaped(ref vub, text);
                return;
            }

            BeginContentItem(ref vub);
            vub.Append((byte)'"');
            BinXmlValueFormatter.AppendJsonEscaped(ref vub, text);
            vub.Append((byte)'"');
        }

        /// <summary>
        /// Per content level (0 = the stream itself): whether the next item needs a leading comma. Nesting is bounded by
        /// <see cref="_maxRecursionDepth"/>.
        /// </summary>
        private CommaStack _needsComma;

        /// <summary>
        /// Current content level.
        /// </summary>
        private int _level;

        /// <summary>
        /// True while writing an attribute value (a single JSON string).
        /// </summary>
        private bool _inAttr;

        /// <summary>
        /// True until the current element's first attribute is written.
        /// </summary>
        private bool _firstAttr;
    }

    /// <summary>
    /// Fixed-size per-level comma flags for <see cref="JsonWriter"/>: one more than the maximum nesting depth.
    /// </summary>
    [InlineArray(_maxRecursionDepth + 1)]
    private struct CommaStack
    {
        /// <summary>
        /// First element of the inline array.
        /// </summary>
        private bool _element;
    }

    #endregion
}
