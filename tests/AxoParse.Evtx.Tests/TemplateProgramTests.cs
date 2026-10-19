using System.Buffers.Binary;
using System.Text;
using AxoParse.Evtx.BinXml;

namespace AxoParse.Evtx.Tests;

/// <summary>
/// Exact-output tests for compiling a <see cref="TemplateProgram"/> (<see cref="BinXmlParser.CompileXml"/>,
/// <see cref="BinXmlParser.CompileJson"/>) on hand-built programs covering constructs the sample templates rarely or
/// never contain (character and entity references, CDATA, optional attributes, an empty attribute list). The expected
/// strings are what the former per-format lowerings produced; the golden output test covers the common shapes on real
/// data, and <see cref="CompiledDecoderParityTests"/> checks compiled output against the decoder.
/// </summary>
public class TemplateProgramTests
{
    #region Public Methods

    /// <summary>
    /// Verifies text is XML-escaped / JSON-escaped, character references render as <c>&amp;#N;</c> in XML and as the
    /// character in JSON, predefined entities resolve only in JSON (others stay <c>&amp;name;</c>), and CDATA is kept
    /// verbatim in XML and escaped as a string item in JSON.
    /// </summary>
    [Fact]
    public void CompilesTextCharRefEntityRefAndCData()
    {
        TemplateProgram program = new([
            new(TemplateOpKind.StartElement, "Data"), new(TemplateOpKind.CloseStartElement),
            new(TemplateOpKind.Text, "a<b&\"c\""), new(TemplateOpKind.CharRef, CharValue: 9),
            new(TemplateOpKind.EntityRef, "amp"), new(TemplateOpKind.EntityRef, "custom"),
            new(TemplateOpKind.CData, "x]y\"z"), new(TemplateOpKind.EndElement, "Data")]);

        Assert.Equal(["<Data>a&lt;b&amp;&quot;c&quot;&#9;&amp;&custom;<![CDATA[x]y\"z]]></Data>"],
            Parts(BinXmlParser.CompileXml(program)));
        Assert.Equal(["{\"#name\":\"Data\",\"#content\":[\"a<b&\\\"c\\\"\",\"\\t\",\"&\",\"&custom;\",\"x]y\\\"z\"]}"],
            Parts(BinXmlParser.CompileJson(program)));
    }

    /// <summary>
    /// Verifies attributes: an optional substitution that is an attribute's whole value moves the <c> Name="</c> /
    /// <c>"</c> markup into its XML slot (so an empty value drops the attribute), a normal one stays in the parts, and a
    /// present-but-empty attribute list still emits <c>"#attrs":{}</c> in JSON.
    /// </summary>
    [Fact]
    public void CompilesAttributesOptionalSlotsAndEmptyAttributeList()
    {
        TemplateProgram program = new([
            new(TemplateOpKind.StartElement, "Event"), new(TemplateOpKind.CloseStartElement),
            new(TemplateOpKind.StartElement, "Provider"), new(TemplateOpKind.BeginAttributes),
            new(TemplateOpKind.Attribute, "Name"), new(TemplateOpKind.Substitution, SubId: 0, IsOptional: true),
            new(TemplateOpKind.EndAttribute),
            new(TemplateOpKind.Attribute, "Guid"), new(TemplateOpKind.Text, "{"), new(TemplateOpKind.Substitution, SubId: 1),
            new(TemplateOpKind.EndAttribute),
            new(TemplateOpKind.EndAttributes), new(TemplateOpKind.CloseEmptyElement, "Provider"),
            new(TemplateOpKind.StartElement, "Empty"), new(TemplateOpKind.BeginAttributes), new(TemplateOpKind.EndAttributes),
            new(TemplateOpKind.CloseEmptyElement, "Empty"), new(TemplateOpKind.EndElement, "Event")]);

        CompiledTemplate xml = BinXmlParser.CompileXml(program);
        Assert.Equal(["<Event><Provider", " Guid=\"{", "\"></Provider><Empty></Empty></Event>"], Parts(xml));
        Assert.Equal((0, true, " Name=\"", "\""), Slot(xml.Slots[0]));
        Assert.Equal((1, false, null, null), Slot(xml.Slots[1]));

        CompiledTemplate json = BinXmlParser.CompileJson(program);
        Assert.Equal(["{\"#name\":\"Event\",\"#content\":[{\"#name\":\"Provider\",\"#attrs\":{\"Name\":\"", "\",\"Guid\":\"{",
                      "\"}},{\"#name\":\"Empty\",\"#attrs\":{}}]}"],
            Parts(json));
        Assert.Equal((0, true, null, null), Slot(json.Slots[0]));
        Assert.Equal((1, false, null, null), Slot(json.Slots[1]));
    }

    /// <summary>
    /// Verifies content: JSON content items are comma-separated, substitutions in content are quoted string items, and
    /// a nested element starts its own comma state.
    /// </summary>
    [Fact]
    public void CompilesContentItemsAndNestedElements()
    {
        TemplateProgram program = new([
            new(TemplateOpKind.StartElement, "Event"), new(TemplateOpKind.CloseStartElement),
            new(TemplateOpKind.Substitution, SubId: 2), new(TemplateOpKind.Text, "t"),
            new(TemplateOpKind.StartElement, "Child"), new(TemplateOpKind.CloseStartElement),
            new(TemplateOpKind.Substitution, SubId: 3, IsOptional: true), new(TemplateOpKind.EndElement, "Child"),
            new(TemplateOpKind.EndElement, "Event")]);

        CompiledTemplate xml = BinXmlParser.CompileXml(program);
        Assert.Equal(["<Event>", "t<Child>", "</Child></Event>"], Parts(xml));
        Assert.Equal([2, 3], xml.Slots.Select(s => s.SubId));

        CompiledTemplate json = BinXmlParser.CompileJson(program);
        Assert.Equal(["{\"#name\":\"Event\",\"#content\":[\"", "\",\"t\",{\"#name\":\"Child\",\"#content\":[\"", "\"]}]}"],
            Parts(json));
        Assert.Equal([false, true], json.Slots.Select(s => s.IsOptional));
    }

    /// <summary>
    /// Verifies that a program whose text contains a surrogate code unit isn't split into parts (a surrogate at a part's
    /// edge could pair with a neighbouring value): it compiles to <see cref="CompiledTemplate.DecodeDirectly"/> in both
    /// formats.
    /// </summary>
    [Fact]
    public void ProgramWithSurrogatesIsDecodedDirectly()
    {
        TemplateProgram program = new([
            new(TemplateOpKind.StartElement, "Data"), new(TemplateOpKind.CloseStartElement),
            new(TemplateOpKind.Text, "x\uD83D"), new(TemplateOpKind.Substitution, SubId: 0),
            new(TemplateOpKind.EndElement, "Data")]);

        Assert.Same(CompiledTemplate.DecodeDirectly, BinXmlParser.CompileXml(program));
        Assert.Same(CompiledTemplate.DecodeDirectly, BinXmlParser.CompileJson(program));
    }

    /// <summary>
    /// Verifies that the compiler rejects an element inside an attribute value, as the decoder does, so a template
    /// holding one falls back to the decoder and fails there rather than rendering differently per path. Body (WEVT
    /// inline names): fragment header; OpenStartElement 0x41 (with attributes) + dependency ID + data size + name "A" +
    /// attribute list size; Attribute 0x06 + name "x"; then OpenStartElement 0x01 for "B" inside the value.
    /// </summary>
    [Fact]
    public void CompilerRejectsElementInsideAttributeValue()
    {
        List<byte> body = [0x0F, 0x01, 0x01, 0x00];
        body.AddRange([0x41, 0xFF, 0xFF, 0, 0, 0, 0]);
        body.AddRange(WevtInlineName("A"));
        body.AddRange([0x40, 0, 0, 0]);
        body.Add(0x06);
        body.AddRange(WevtInlineName("x"));
        body.AddRange([0x01, 0xFF, 0xFF, 0, 0, 0, 0]);
        body.AddRange(WevtInlineName("B"));
        body.AddRange([0x03, 0x02, 0x04, 0x00]);

        Assert.Null(TemplateProgramCompiler.Compile(body.ToArray(), default(WevtInlineNameReader)));
    }

    #endregion

    #region Non-Public Methods

    /// <summary>
    /// A compiled template's static parts as strings.
    /// </summary>
    /// <param name="compiled">Compiled template.</param>
    /// <returns>The parts decoded from UTF-8.</returns>
    private static string[] Parts(CompiledTemplate compiled) =>
        compiled.Parts.Select(p => Encoding.UTF8.GetString(p)).ToArray();

    /// <summary>
    /// A slot as a comparable tuple, with its conditional markup decoded from UTF-8.
    /// </summary>
    /// <param name="slot">Substitution slot.</param>
    /// <returns>Substitution index, optional flag, prefix and suffix.</returns>
    private static (int, bool, string?, string?) Slot(SubSlot slot) =>
        (slot.SubId, slot.IsOptional,
         slot.Prefix == null ? null : Encoding.UTF8.GetString(slot.Prefix),
         slot.Suffix == null ? null : Encoding.UTF8.GetString(slot.Suffix));

    /// <summary>
    /// Encodes a WEVT inline name: hash (2, unused by the parser) + character count (2) + UTF-16LE + NUL (2).
    /// </summary>
    /// <param name="name">Name to encode.</param>
    /// <returns>The encoded bytes.</returns>
    private static byte[] WevtInlineName(string name)
    {
        byte[] bytes = new byte[4 + (name.Length * 2) + 2];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(2), (ushort)name.Length);
        Encoding.Unicode.GetBytes(name, bytes.AsSpan(4));
        return bytes;
    }

    #endregion
}
