using System.Globalization;
using System.Runtime.InteropServices;

namespace AxoParse.Evtx.BinXml;

/// <summary>
/// One instruction of a <see cref="TemplateProgram"/>.
/// </summary>
internal enum TemplateOpKind : byte
{
    /// <summary>
    /// Element start (OpenStartElement 0x01/0x41). <see cref="TemplateOp.Text"/> is the raw element name.
    /// </summary>
    StartElement,

    /// <summary>
    /// The element's attribute list begins (the 0x40 "has attributes" flag was set, even if the list is empty).
    /// </summary>
    BeginAttributes,

    /// <summary>
    /// An attribute (0x06/0x46) begins; its value ops follow until <see cref="EndAttribute"/>.
    /// <see cref="TemplateOp.Text"/> is the raw attribute name.
    /// </summary>
    Attribute,

    /// <summary>
    /// The current attribute's value ends.
    /// </summary>
    EndAttribute,

    /// <summary>
    /// The element's attribute list ends.
    /// </summary>
    EndAttributes,

    /// <summary>
    /// CloseStartElement (0x02): child content ops follow until <see cref="EndElement"/>.
    /// </summary>
    CloseStartElement,

    /// <summary>
    /// End of an element that had child content. <see cref="TemplateOp.Text"/> is the raw element name.
    /// </summary>
    EndElement,

    /// <summary>
    /// End of an element with no child content: CloseEmptyElement (0x03), end of data, or an unexpected token after the
    /// attributes (all three render the same). <see cref="TemplateOp.Text"/> is the raw element name.
    /// </summary>
    CloseEmptyElement,

    /// <summary>
    /// Literal text from a Value token (0x05/0x45), unescaped. <see cref="TemplateOp.Text"/> is the text.
    /// </summary>
    Text,

    /// <summary>
    /// Character reference (0x08/0x48). <see cref="TemplateOp.CharValue"/> is the UTF-16 code unit.
    /// </summary>
    CharRef,

    /// <summary>
    /// Entity reference (0x09/0x49). <see cref="TemplateOp.Text"/> is the raw entity name (e.g. <c>amp</c>).
    /// </summary>
    EntityRef,

    /// <summary>
    /// CDATA section (0x07/0x47). <see cref="TemplateOp.Text"/> is the section's text.
    /// </summary>
    CData,

    /// <summary>
    /// Substitution gap (0x0D normal / 0x0E optional). <see cref="TemplateOp.SubId"/> indexes the instance's values.
    /// </summary>
    Substitution,
}

/// <summary>
/// One instruction of a <see cref="TemplateProgram"/>.
/// </summary>
/// <param name="Kind">What the instruction does.</param>
/// <param name="Text">Element/attribute/entity name or literal text, depending on <paramref name="Kind"/>; raw (unescaped).</param>
/// <param name="SubId">Substitution index (<see cref="TemplateOpKind.Substitution"/> only).</param>
/// <param name="IsOptional">True for an optional substitution (0x0E), which renders nothing for an empty value.</param>
/// <param name="CharValue">UTF-16 code unit of a <see cref="TemplateOpKind.CharRef"/>.</param>
internal readonly record struct TemplateOp(
    TemplateOpKind Kind,
    string? Text = null,
    int SubId = 0,
    bool IsOptional = false,
    ushort CharValue = 0);

/// <summary>
/// A template body compiled once into a flat, format-neutral instruction list: element structure, literal text and
/// substitution gaps, with names and text unescaped. Each output format lowers it into its own compiled form
/// (<see cref="LowerToXml"/>, <see cref="LowerToJson"/>), so the BinXml token walk exists once instead of once per
/// format. Phase 1 of docs/plans/FormatNeutralRenderer.md: built alongside the existing compilers, which it must
/// reproduce exactly.
/// </summary>
/// <param name="Ops">Instructions in template order.</param>
internal sealed record TemplateProgram(TemplateOp[] Ops)
{
    #region Public Methods

    /// <summary>
    /// Lowers the program to the XML compiled form: static XML parts interleaved with substitution slots, with
    /// optional attribute markup moved into its slot (see <see cref="CompiledTemplate.FromParts"/>).
    /// </summary>
    /// <returns>The compiled XML template.</returns>
    public CompiledTemplate LowerToXml()
    {
        TemplatePartsBuilder parts = new();
        List<int> subIds = new();
        List<bool> isOptional = new();
        List<bool> inAttrValue = new();
        // Whether the innermost open context is an attribute value (true) or element content (false)
        Stack<bool> inAttr = new();
        inAttr.Push(false);

        foreach (TemplateOp op in Ops)
        {
            switch (op.Kind)
            {
                case TemplateOpKind.StartElement:
                    parts.Current.Append('<').Append(op.Text);
                    break;
                case TemplateOpKind.BeginAttributes:
                case TemplateOpKind.EndAttributes:
                    break;
                case TemplateOpKind.Attribute:
                    parts.Current.Append(' ').Append(op.Text).Append("=\"");
                    inAttr.Push(true);
                    break;
                case TemplateOpKind.EndAttribute:
                    inAttr.Pop();
                    parts.Current.Append('"');
                    break;
                case TemplateOpKind.CloseStartElement:
                    parts.Current.Append('>');
                    inAttr.Push(false);
                    break;
                case TemplateOpKind.EndElement:
                    inAttr.Pop();
                    parts.Current.Append("</").Append(op.Text).Append('>');
                    break;
                case TemplateOpKind.CloseEmptyElement:
                    parts.Current.Append("></").Append(op.Text).Append('>');
                    break;
                case TemplateOpKind.Text:
                    parts.Current.Append(BinXmlValueFormatter.XmlEscapeString(op.Text!));
                    break;
                case TemplateOpKind.CharRef:
                    parts.Current.Append("&#").Append(op.CharValue.ToString(NumberFormatInfo.InvariantInfo)).Append(';');
                    break;
                case TemplateOpKind.EntityRef:
                    parts.Current.Append('&').Append(op.Text).Append(';');
                    break;
                case TemplateOpKind.CData:
                    parts.Current.Append("<![CDATA[").Append(op.Text).Append("]]>");
                    break;
                case TemplateOpKind.Substitution:
                    subIds.Add(op.SubId);
                    isOptional.Add(op.IsOptional);
                    inAttrValue.Add(inAttr.Peek());
                    parts.NextPart();
                    break;
            }
        }

        return CompiledTemplate.FromParts(parts.ToArray(), subIds, isOptional, inAttrValue);
    }

    /// <summary>
    /// Lowers the program to the JSON compiled form, the structural
    /// <c>{"#name":…,"#attrs":{…},"#content":[…]}</c> shape: names and text JSON-escaped, entities resolved, content
    /// items comma-separated, and substitutions in content quoted as string items.
    /// </summary>
    /// <returns>The compiled JSON template.</returns>
    public CompiledJsonTemplate LowerToJson()
    {
        TemplatePartsBuilder parts = new();
        List<int> subIds = new();
        List<bool> isOptional = new();
        List<bool> inAttrValue = new();
        // Innermost open context: inside an attribute value (a JSON string literal) or element content (an array),
        // and whether the next content item needs a leading comma
        Stack<(bool InAttr, bool NeedsComma)> contexts = new();
        contexts.Push((false, false));
        // Per open element: whether no attribute has been written yet
        Stack<bool> firstAttr = new();

        foreach (TemplateOp op in Ops)
        {
            switch (op.Kind)
            {
                case TemplateOpKind.StartElement:
                    BeginContentItem(parts, contexts);
                    parts.Current.Append("{\"#name\":\"").Append(BinXmlValueFormatter.JsonEscapeString(op.Text!)).Append('"');
                    break;
                case TemplateOpKind.BeginAttributes:
                    parts.Current.Append(",\"#attrs\":{");
                    firstAttr.Push(true);
                    break;
                case TemplateOpKind.Attribute:
                    if (!firstAttr.Peek())
                        parts.Current.Append(',');
                    firstAttr.Pop();
                    firstAttr.Push(false);
                    parts.Current.Append('"').Append(BinXmlValueFormatter.JsonEscapeString(op.Text!)).Append("\":\"");
                    contexts.Push((true, false));
                    break;
                case TemplateOpKind.EndAttribute:
                    contexts.Pop();
                    parts.Current.Append('"');
                    break;
                case TemplateOpKind.EndAttributes:
                    firstAttr.Pop();
                    parts.Current.Append('}');
                    break;
                case TemplateOpKind.CloseStartElement:
                    parts.Current.Append(",\"#content\":[");
                    contexts.Push((false, false));
                    break;
                case TemplateOpKind.EndElement:
                    contexts.Pop();
                    parts.Current.Append("]}");
                    break;
                case TemplateOpKind.CloseEmptyElement:
                    parts.Current.Append('}');
                    break;
                case TemplateOpKind.Text:
                case TemplateOpKind.CData:
                    AppendJsonText(parts, contexts, BinXmlValueFormatter.JsonEscapeString(op.Text!));
                    break;
                case TemplateOpKind.CharRef:
                    AppendJsonText(parts, contexts, BinXmlValueFormatter.JsonEscapeString(((char)op.CharValue).ToString()));
                    break;
                case TemplateOpKind.EntityRef:
                    AppendJsonText(parts, contexts, BinXmlValueFormatter.JsonEscapeString(ResolveEntity(op.Text!)));
                    break;
                case TemplateOpKind.Substitution:
                {
                    bool attr = contexts.Peek().InAttr;
                    if (!attr)
                    {
                        BeginContentItem(parts, contexts);
                        parts.Current.Append('"');
                    }
                    subIds.Add(op.SubId);
                    isOptional.Add(op.IsOptional);
                    inAttrValue.Add(attr);
                    parts.NextPart();
                    if (!attr)
                        parts.Current.Append('"');
                    break;
                }
            }
        }

        return new CompiledJsonTemplate(parts.ToArray(), subIds.ToArray(), isOptional.ToArray(), inAttrValue.ToArray());
    }

    /// <summary>
    /// Describes the first difference between a template compiled by the per-format XML compiler and the same
    /// template lowered from its program, or returns null if they are identical (including both being null).
    /// Used by the phase-1 equivalence test and the debug-build check at compile sites.
    /// </summary>
    /// <param name="expected">Output of the existing XML compiler.</param>
    /// <param name="actual">Program lowered with <see cref="LowerToXml"/>, or null if the program didn't compile.</param>
    /// <returns>A description of the first difference, or null.</returns>
    internal static string? DescribeDifference(CompiledTemplate? expected, CompiledTemplate? actual)
    {
        if ((expected == null) || (actual == null))
            return (expected == null) == (actual == null) ? null : $"null mismatch: expected {(expected == null ? "null" : "template")}, got {(actual == null ? "null" : "template")}";
        if (expected.Parts.Length != actual.Parts.Length)
            return $"part count {expected.Parts.Length} vs {actual.Parts.Length}";
        for (int i = 0; i < expected.Parts.Length; i++)
        {
            if (!string.Equals(expected.Parts[i], actual.Parts[i], StringComparison.Ordinal))
                return $"part {i}: \"{expected.Parts[i]}\" vs \"{actual.Parts[i]}\"";
        }

        for (int i = 0; i < expected.Slots.Length; i++)
        {
            SubSlot e = expected.Slots[i];
            SubSlot a = actual.Slots[i];
            if ((e.SubId != a.SubId) || (e.IsOptional != a.IsOptional) ||
                !string.Equals(e.AttrPrefix, a.AttrPrefix, StringComparison.Ordinal) ||
                !string.Equals(e.AttrSuffix, a.AttrSuffix, StringComparison.Ordinal))
                return $"slot {i}: ({e.SubId}, {e.IsOptional}, {e.AttrPrefix}, {e.AttrSuffix}) vs ({a.SubId}, {a.IsOptional}, {a.AttrPrefix}, {a.AttrSuffix})";
        }

        return null;
    }

    /// <summary>
    /// JSON counterpart of <see cref="DescribeDifference(CompiledTemplate?, CompiledTemplate?)"/>.
    /// </summary>
    /// <param name="expected">Output of the existing JSON compiler.</param>
    /// <param name="actual">Program lowered with <see cref="LowerToJson"/>, or null if the program didn't compile.</param>
    /// <returns>A description of the first difference, or null.</returns>
    internal static string? DescribeDifference(CompiledJsonTemplate? expected, CompiledJsonTemplate? actual)
    {
        if ((expected == null) || (actual == null))
            return (expected == null) == (actual == null) ? null : $"null mismatch: expected {(expected == null ? "null" : "template")}, got {(actual == null ? "null" : "template")}";
        if (!expected.Parts.AsSpan().SequenceEqual(actual.Parts))
            return $"parts differ: [{string.Join("|", expected.Parts)}] vs [{string.Join("|", actual.Parts)}]";
        if (!expected.SubIds.AsSpan().SequenceEqual(actual.SubIds))
            return "substitution IDs differ";
        if (!expected.IsOptional.AsSpan().SequenceEqual(actual.IsOptional))
            return "optional flags differ";
        if (!expected.InAttrValue.AsSpan().SequenceEqual(actual.InAttrValue))
            return "attribute-value flags differ";
        return null;
    }

    #endregion

    #region Non-Public Methods

    /// <summary>
    /// Appends already-escaped text: directly inside an attribute value, or as a quoted string item in content.
    /// </summary>
    /// <param name="parts">Parts being built.</param>
    /// <param name="contexts">Open JSON contexts.</param>
    /// <param name="escaped">JSON-escaped text.</param>
    private static void AppendJsonText(TemplatePartsBuilder parts, Stack<(bool InAttr, bool NeedsComma)> contexts, string escaped)
    {
        if (contexts.Peek().InAttr)
        {
            parts.Current.Append(escaped);
            return;
        }

        BeginContentItem(parts, contexts);
        parts.Current.Append('"').Append(escaped).Append('"');
    }

    /// <summary>
    /// In element content, writes the comma separating this item from the previous one. Does nothing inside an
    /// attribute value, which is a single string.
    /// </summary>
    /// <param name="parts">Parts being built.</param>
    /// <param name="contexts">Open JSON contexts; the innermost one's comma state is updated.</param>
    private static void BeginContentItem(TemplatePartsBuilder parts, Stack<(bool InAttr, bool NeedsComma)> contexts)
    {
        (bool inAttr, bool needsComma) = contexts.Pop();
        if (!inAttr)
        {
            if (needsComma)
                parts.Current.Append(',');
            needsComma = true;
        }
        contexts.Push((inAttr, needsComma));
    }

    /// <summary>
    /// Resolves the five predefined XML entities for JSON output; any other entity is kept as <c>&amp;name;</c>.
    /// </summary>
    /// <param name="name">Entity name.</param>
    /// <returns>The resolved text.</returns>
    private static string ResolveEntity(string name) => name switch
    {
        "amp" => "&",
        "lt" => "<",
        "gt" => ">",
        "quot" => "\"",
        "apos" => "'",
        _ => $"&{name};"
    };

    #endregion
}

/// <summary>
/// Partial class containing the format-neutral template compiler: one BinXml token walk that records a
/// <see cref="TemplateProgram"/>, which each output format then lowers.
/// </summary>
internal sealed partial class BinXmlParser
{
    #region Non-Public Methods

    /// <summary>
    /// Compiles a template body into a <see cref="TemplateProgram"/>. Returns null if the template contains nested
    /// template instances or other constructs that prevent static compilation (the same cases in which
    /// <see cref="CompileTemplate"/> and <see cref="CompileJsonTemplate"/> return null).
    /// </summary>
    /// <param name="defDataOffset">Chunk-relative offset of the template definition (before the 24-byte header).</param>
    /// <param name="dataSize">Size in bytes of the template body (after the 24-byte header).</param>
    /// <returns>The program, or null if the template cannot be statically compiled.</returns>
    internal TemplateProgram? CompileProgram(int defDataOffset, int dataSize)
    {
        int tplBodyFileOffset = _chunkFileOffset + defDataOffset + 24;
        if (tplBodyFileOffset + dataSize > _chunkEnd) return null;

        ReadOnlySpan<byte> tplBody = _fileData.AsSpan(tplBodyFileOffset, dataSize);
        int tplChunkBase = defDataOffset + 24;
        List<TemplateOp> ops = new();
        bool bail = false;

        int pos = 0;
        // Skip fragment header
        if ((tplBody.Length >= 4) && (tplBody[0] == BinXmlToken.FragmentHeader))
            pos += 4;

        CompileProgramContent(tplBody, ref pos, tplChunkBase, ops, ref bail, 0);
        return bail ? null : new TemplateProgram(ops.ToArray());
    }

    /// <summary>
    /// Walks BinXml content tokens and records them as program ops. Stops at the end of data or a token that ends the
    /// enclosing construct (EOF, close/end element, next attribute). Sets <paramref name="bail"/> for nested template
    /// instances and unsupported tokens.
    /// </summary>
    /// <param name="data">BinXml byte stream (template body).</param>
    /// <param name="pos">Current read position; advanced past consumed tokens.</param>
    /// <param name="binxmlChunkBase">Chunk-relative base offset of <paramref name="data"/>.</param>
    /// <param name="ops">Program being recorded.</param>
    /// <param name="bail">Set to true if compilation must abort.</param>
    /// <param name="depth">Current recursion depth for stack overflow protection.</param>
    private void CompileProgramContent(ReadOnlySpan<byte> data, ref int pos, int binxmlChunkBase,
                                       List<TemplateOp> ops, ref bool bail, int depth)
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
                    CompileProgramElement(data, ref pos, binxmlChunkBase, ops, ref bail, depth + 1);
                    break;

                case BinXmlToken.Value:
                    pos++; // token
                    pos++; // value type
                    ops.Add(new TemplateOp(TemplateOpKind.Text, BinXmlValueFormatter.ReadUnicodeTextStringAsString(data, ref pos)));
                    break;

                case BinXmlToken.NormalSubstitution:
                case BinXmlToken.OptionalSubstitution:
                    pos++;
                    ushort subId = MemoryMarshal.Read<ushort>(data[pos..]);
                    pos += 2;
                    pos++; // subValType
                    ops.Add(new TemplateOp(TemplateOpKind.Substitution, SubId: subId,
                        IsOptional: baseTok == BinXmlToken.OptionalSubstitution));
                    break;

                case BinXmlToken.CharRef:
                    pos++;
                    ushort charVal = MemoryMarshal.Read<ushort>(data[pos..]);
                    pos += 2;
                    ops.Add(new TemplateOp(TemplateOpKind.CharRef, CharValue: charVal));
                    break;

                case BinXmlToken.EntityRef:
                    pos++;
                    uint nameOff = MemoryMarshal.Read<uint>(data[pos..]);
                    pos += 4;
                    ops.Add(new TemplateOp(TemplateOpKind.EntityRef, ReadName(nameOff)));
                    break;

                case BinXmlToken.CDataSection:
                    pos++;
                    ops.Add(new TemplateOp(TemplateOpKind.CData, BinXmlValueFormatter.ReadUnicodeTextStringAsString(data, ref pos)));
                    break;

                default:
                    // Includes TemplateInstance (0x0C): nested templates can't be compiled statically
                    bail = true;
                    return;
            }
        }
    }

    /// <summary>
    /// Records one element: its start, attribute list (when the 0x40 flag is set), and either child content
    /// (CloseStartElement … EndElement) or an empty close.
    /// </summary>
    /// <param name="data">BinXml byte stream (template body).</param>
    /// <param name="pos">Current read position; advanced past the entire element.</param>
    /// <param name="binxmlChunkBase">Chunk-relative base offset of <paramref name="data"/>.</param>
    /// <param name="ops">Program being recorded.</param>
    /// <param name="bail">Set to true if compilation must abort.</param>
    /// <param name="depth">Current recursion depth for stack overflow protection.</param>
    private void CompileProgramElement(ReadOnlySpan<byte> data, ref int pos, int binxmlChunkBase,
                                       List<TemplateOp> ops, ref bool bail, int depth)
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
        ops.Add(new TemplateOp(TemplateOpKind.StartElement, elemName));

        if (hasAttrs)
        {
            uint attrListSize = MemoryMarshal.Read<uint>(data[pos..]);
            pos += 4;
            int attrEnd = pos + (int)attrListSize;
            ops.Add(new TemplateOp(TemplateOpKind.BeginAttributes));

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

                ops.Add(new TemplateOp(TemplateOpKind.Attribute, ReadName(attrNameOff)));
                CompileProgramContent(data, ref pos, binxmlChunkBase, ops, ref bail, depth + 1);
                if (bail) return;
                ops.Add(new TemplateOp(TemplateOpKind.EndAttribute));
            }

            ops.Add(new TemplateOp(TemplateOpKind.EndAttributes));
        }

        if ((pos < data.Length) && (data[pos] == BinXmlToken.CloseStartElement))
        {
            pos++;
            ops.Add(new TemplateOp(TemplateOpKind.CloseStartElement));
            CompileProgramContent(data, ref pos, binxmlChunkBase, ops, ref bail, depth + 1);
            if (bail) return;
            if ((pos < data.Length) && (data[pos] == BinXmlToken.EndElement))
                pos++;
            ops.Add(new TemplateOp(TemplateOpKind.EndElement, elemName));
            return;
        }

        // CloseEmptyElement is consumed; end of data or any other token is left for the caller, as before
        if ((pos < data.Length) && (data[pos] == BinXmlToken.CloseEmptyElement))
            pos++;
        ops.Add(new TemplateOp(TemplateOpKind.CloseEmptyElement, elemName));
    }

    #endregion
}
