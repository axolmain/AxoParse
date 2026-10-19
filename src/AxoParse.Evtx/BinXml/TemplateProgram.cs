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
/// substitution gaps, with names and text unescaped. Each output format turns it into a <see cref="CompiledTemplate"/>
/// by replaying it through that format's writer (<see cref="BinXmlParser.CompileXml"/>,
/// <see cref="BinXmlParser.CompileJson"/>), so the BinXml token walk exists once and compiled output follows the same
/// rules as decoded output.
/// </summary>
/// <param name="Ops">The instructions, in document order.</param>
internal sealed record TemplateProgram(TemplateOp[] Ops);

/// <summary>
/// Reads element, attribute and entity names during a template walk. EVTX chunk templates and WEVT provider
/// templates share the BinXml token grammar but store names differently, so the walk is generic over this.
/// </summary>
internal interface ITemplateNameReader
{
    /// <summary>
    /// Reads an element, attribute or entity name at <paramref name="pos"/>, advancing past its bytes.
    /// </summary>
    /// <param name="data">Template body.</param>
    /// <param name="pos">Position of the name field.</param>
    /// <returns>The name, or null if it can't be read (the template is treated as uncompilable).</returns>
    string? ReadName(ReadOnlySpan<byte> data, ref int pos);
}

/// <summary>
/// Reads WEVT inline names: no name-table offset, each name stored in place as
/// hash (2) + character count (2) + UTF-16LE characters + NUL (2) = 6 + 2 × count bytes.
/// </summary>
internal readonly struct WevtInlineNameReader : ITemplateNameReader
{
    #region Public Methods

    /// <inheritdoc/>
    public string? ReadName(ReadOnlySpan<byte> data, ref int pos) => ReadInline(data, ref pos);

    #endregion

    #region Non-Public Methods

    /// <summary>
    /// Reads one inline WEVT name.
    /// </summary>
    /// <param name="data">Template body.</param>
    /// <param name="pos">Position of the name; advanced past it, including the NUL.</param>
    /// <returns>The name, or null if it runs past the data.</returns>
    private static string? ReadInline(ReadOnlySpan<byte> data, ref int pos)
    {
        if (pos + 4 > data.Length)
            return null;

        ushort numChars = MemoryMarshal.Read<ushort>(data[(pos + 2)..]);
        int stringBytes = numChars * 2;
        if (pos + 4 + stringBytes + 2 > data.Length)
            return null;

        string name = new(MemoryMarshal.Cast<byte, char>(data.Slice(pos + 4, stringBytes)));
        pos += 4 + stringBytes + 2;
        return name;
    }

    #endregion
}

/// <summary>
/// The format-neutral template compiler: one BinXml token walk that records a <see cref="TemplateProgram"/>, which
/// each output format then compiles through its writer. Used for EVTX chunk templates (<see cref="BinXmlParser.CompileProgram"/>) and WEVT
/// provider templates (<see cref="Wevt.WevtCache"/>), which differ only in how names are stored.
/// </summary>
internal static class TemplateProgramCompiler
{
    #region Non-Public Methods

    /// <summary>
    /// Compiles a template body into a program. Returns null if the template contains nested template instances or
    /// other constructs that prevent static compilation; the shared decoder then decodes the body directly.
    /// </summary>
    /// <typeparam name="TNames">How names are stored in this body.</typeparam>
    /// <param name="body">Template body (BinXml fragment).</param>
    /// <param name="names">Name reader for the body.</param>
    /// <returns>The program, or null.</returns>
    internal static TemplateProgram? Compile<TNames>(ReadOnlySpan<byte> body, TNames names)
        where TNames : struct, ITemplateNameReader
    {
        List<TemplateOp> ops = new();
        bool bail = false;

        int pos = 0;
        // Skip fragment header
        if ((body.Length >= 4) && (body[0] == BinXmlToken.FragmentHeader))
            pos += 4;

        // A template describes one event: its root element, with anything after it ignored. A body that starts with
        // anything else isn't compiled, and the decoder rejects it.
        if ((pos < body.Length) && (body[pos] != BinXmlToken.Eof))
        {
            if ((byte)(body[pos] & ~BinXmlToken.HasMoreDataFlag) != BinXmlToken.OpenStartElement)
                return null;
            CompileElement(body, ref pos, names, ops, ref bail, 1);
        }

        return bail ? null : new TemplateProgram(ops.ToArray());
    }

    /// <summary>
    /// Walks BinXml content tokens and records them as program ops. Stops at the end of data or a token that ends the
    /// enclosing construct (EOF, close/end element, next attribute). Sets <paramref name="bail"/> for nested template
    /// instances, elements inside an attribute value, and unsupported tokens; the decoder then rejects the same input.
    /// </summary>
    /// <typeparam name="TNames">How names are stored in this body.</typeparam>
    /// <param name="data">BinXml byte stream (template body).</param>
    /// <param name="pos">Current read position; advanced past consumed tokens.</param>
    /// <param name="names">Name reader for the body.</param>
    /// <param name="ops">Program being recorded.</param>
    /// <param name="bail">Set to true if compilation must abort.</param>
    /// <param name="depth">Current recursion depth for stack overflow protection.</param>
    /// <param name="inAttr">True when walking an attribute value.</param>
    private static void CompileContent<TNames>(ReadOnlySpan<byte> data, ref int pos, TNames names,
                                               List<TemplateOp> ops, ref bool bail, int depth, bool inAttr)
        where TNames : struct, ITemplateNameReader
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
                    if (inAttr)
                    {
                        bail = true;
                        return;
                    }
                    CompileElement(data, ref pos, names, ops, ref bail, depth + 1);
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
                {
                    pos++;
                    string? entityName = names.ReadName(data, ref pos);
                    if (entityName == null)
                    {
                        bail = true;
                        return;
                    }
                    ops.Add(new TemplateOp(TemplateOpKind.EntityRef, entityName));
                    break;
                }

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
    /// <typeparam name="TNames">How names are stored in this body.</typeparam>
    /// <param name="data">BinXml byte stream (template body).</param>
    /// <param name="pos">Current read position; advanced past the entire element.</param>
    /// <param name="names">Name reader for the body.</param>
    /// <param name="ops">Program being recorded.</param>
    /// <param name="bail">Set to true if compilation must abort.</param>
    /// <param name="depth">Current recursion depth for stack overflow protection.</param>
    private static void CompileElement<TNames>(ReadOnlySpan<byte> data, ref int pos, TNames names,
                                               List<TemplateOp> ops, ref bool bail, int depth)
        where TNames : struct, ITemplateNameReader
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
        string? elemName = names.ReadName(data, ref pos);
        if (elemName == null)
        {
            bail = true;
            return;
        }

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
                string? attrName = names.ReadName(data, ref pos);
                if (attrName == null)
                {
                    bail = true;
                    return;
                }

                ops.Add(new TemplateOp(TemplateOpKind.Attribute, attrName));
                CompileContent(data, ref pos, names, ops, ref bail, depth + 1, inAttr: true);
                if (bail) return;
                ops.Add(new TemplateOp(TemplateOpKind.EndAttribute));
            }

            ops.Add(new TemplateOp(TemplateOpKind.EndAttributes));
        }

        if ((pos < data.Length) && (data[pos] == BinXmlToken.CloseStartElement))
        {
            pos++;
            ops.Add(new TemplateOp(TemplateOpKind.CloseStartElement));
            CompileContent(data, ref pos, names, ops, ref bail, depth + 1, inAttr: false);
            if (bail) return;
            if ((pos < data.Length) && (data[pos] == BinXmlToken.EndElement))
                pos++;
            ops.Add(new TemplateOp(TemplateOpKind.EndElement, elemName));
            return;
        }

        // CloseEmptyElement is consumed; end of data or any other token is left for the caller
        if ((pos < data.Length) && (data[pos] == BinXmlToken.CloseEmptyElement))
            pos++;
        ops.Add(new TemplateOp(TemplateOpKind.CloseEmptyElement, elemName));
    }

    #endregion

    #region Non-Public Fields

    /// <summary>
    /// Maximum element nesting depth before compilation gives up (guards against stack overflow on crafted input).
    /// </summary>
    private const int _maxRecursionDepth = 64;

    #endregion
}

/// <summary>
/// Partial class containing the EVTX entry point to the template compiler.
/// </summary>
internal sealed partial class BinXmlParser
{
    #region Non-Public Methods

    /// <summary>
    /// Compiles an EVTX chunk template body into a <see cref="TemplateProgram"/>, which the XML and JSON writers turn
    /// into their compiled forms. Returns null if the body runs past the chunk, is corrupt, or can't be compiled
    /// statically.
    /// </summary>
    /// <param name="defDataOffset">Chunk-relative offset of the template definition (before the 24-byte header).</param>
    /// <param name="dataSize">Size in bytes of the template body (after the 24-byte header).</param>
    /// <returns>The program, or null if the template cannot be statically compiled.</returns>
    internal TemplateProgram? CompileProgram(int defDataOffset, int dataSize)
    {
        int tplBodyFileOffset = _chunkFileOffset + defDataOffset + 24;
        if (tplBodyFileOffset + dataSize > _chunkEnd) return null;

        try
        {
            return TemplateProgramCompiler.Compile(_fileData.AsSpan(tplBodyFileOffset, dataSize),
                new ChunkNameReader(this, defDataOffset + 24));
        }
        catch (ArgumentOutOfRangeException)
        {
            // A corrupt body whose tokens run past its end doesn't compile, like any other corrupt template: it falls
            // back to the provider template or the decoder, instead of failing every record that uses it
            return null;
        }
    }

    #endregion

    #region Nested Types

    /// <summary>
    /// Reads EVTX chunk names: a 4-byte chunk-relative offset into the chunk's name table, with the name stored inline
    /// right after the field the first time it is defined (see <see cref="TrySkipInlineName"/>).
    /// </summary>
    /// <param name="parser">Parser for the chunk, which resolves and caches names.</param>
    /// <param name="binxmlChunkBase">Chunk-relative offset of the template body, to recognise inline definitions.</param>
    private readonly struct ChunkNameReader(BinXmlParser parser, int binxmlChunkBase) : ITemplateNameReader
    {
        /// <inheritdoc/>
        public string? ReadName(ReadOnlySpan<byte> data, ref int pos)
        {
            uint nameOffset = MemoryMarshal.Read<uint>(data[pos..]);
            pos += 4;
            return TrySkipInlineName(data, ref pos, nameOffset, binxmlChunkBase) ? parser.ReadName(nameOffset) : null;
        }
    }

    #endregion
}
