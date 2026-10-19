using AxoParse.Evtx.BinXml;

namespace AxoParse.Evtx.Tests;

/// <summary>
/// Exact-output tests for <see cref="TemplateProgram"/> lowering on hand-built programs covering constructs the sample
/// templates rarely or never contain (character and entity references, CDATA, optional attributes, an empty attribute
/// list, an element inside an attribute value). The expected strings are what the former per-format compilers
/// produced; the golden output test covers the common shapes on real data.
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
    public void LowersTextCharRefEntityRefAndCData()
    {
        TemplateProgram program = new([
            new(TemplateOpKind.StartElement, "Data"), new(TemplateOpKind.CloseStartElement),
            new(TemplateOpKind.Text, "a<b&\"c\""), new(TemplateOpKind.CharRef, CharValue: 9),
            new(TemplateOpKind.EntityRef, "amp"), new(TemplateOpKind.EntityRef, "custom"),
            new(TemplateOpKind.CData, "x]y\"z"), new(TemplateOpKind.EndElement, "Data")]);

        Assert.Equal(["<Data>a&lt;b&amp;&quot;c&quot;&#9;&amp;&custom;<![CDATA[x]y\"z]]></Data>"], program.LowerToXml().Parts);
        Assert.Equal(["{\"#name\":\"Data\",\"#content\":[\"a<b&\\\"c\\\"\",\"\\t\",\"&\",\"&custom;\",\"x]y\\\"z\"]}"],
            program.LowerToJson().Parts);
    }

    /// <summary>
    /// Verifies attribute lowering: an optional substitution that is an attribute's whole value moves the
    /// <c> Name="</c> / <c>"</c> markup into its XML slot (so an empty value drops the attribute), a normal one stays
    /// in the parts, and a present-but-empty attribute list still emits <c>"#attrs":{}</c> in JSON.
    /// </summary>
    [Fact]
    public void LowersAttributesOptionalSlotsAndEmptyAttributeList()
    {
        TemplateProgram program = new([
            new(TemplateOpKind.StartElement, "Provider"), new(TemplateOpKind.BeginAttributes),
            new(TemplateOpKind.Attribute, "Name"), new(TemplateOpKind.Substitution, SubId: 0, IsOptional: true),
            new(TemplateOpKind.EndAttribute),
            new(TemplateOpKind.Attribute, "Guid"), new(TemplateOpKind.Text, "{"), new(TemplateOpKind.Substitution, SubId: 1),
            new(TemplateOpKind.EndAttribute),
            new(TemplateOpKind.EndAttributes), new(TemplateOpKind.CloseEmptyElement, "Provider"),
            new(TemplateOpKind.StartElement, "Empty"), new(TemplateOpKind.BeginAttributes), new(TemplateOpKind.EndAttributes),
            new(TemplateOpKind.CloseEmptyElement, "Empty")]);

        CompiledTemplate xml = program.LowerToXml();
        Assert.Equal(["<Provider", " Guid=\"{", "\"></Provider><Empty></Empty>"], xml.Parts);
        Assert.Equal((0, true, " Name=\"", "\""), (xml.Slots[0].SubId, xml.Slots[0].IsOptional, xml.Slots[0].AttrPrefix, xml.Slots[0].AttrSuffix));
        Assert.Equal((1, false, null, null), (xml.Slots[1].SubId, xml.Slots[1].IsOptional, xml.Slots[1].AttrPrefix, xml.Slots[1].AttrSuffix));

        CompiledJsonTemplate json = program.LowerToJson();
        Assert.Equal(["{\"#name\":\"Provider\",\"#attrs\":{\"Name\":\"", "\",\"Guid\":\"{", "\"}},{\"#name\":\"Empty\",\"#attrs\":{}}"],
            json.Parts);
        Assert.Equal([true, true], json.InAttrValue);
        Assert.Equal([true, false], json.IsOptional);
    }

    /// <summary>
    /// Verifies content lowering: JSON content items are comma-separated, substitutions in content are quoted string
    /// items, and a nested element starts its own comma state.
    /// </summary>
    [Fact]
    public void LowersContentItemsAndNestedElements()
    {
        TemplateProgram program = new([
            new(TemplateOpKind.StartElement, "Event"), new(TemplateOpKind.CloseStartElement),
            new(TemplateOpKind.Substitution, SubId: 2), new(TemplateOpKind.Text, "t"),
            new(TemplateOpKind.StartElement, "Child"), new(TemplateOpKind.CloseStartElement),
            new(TemplateOpKind.Substitution, SubId: 3, IsOptional: true), new(TemplateOpKind.EndElement, "Child"),
            new(TemplateOpKind.EndElement, "Event")]);

        CompiledTemplate xml = program.LowerToXml();
        Assert.Equal(["<Event>", "t<Child>", "</Child></Event>"], xml.Parts);
        Assert.Equal([2, 3], xml.Slots.Select(s => s.SubId));

        CompiledJsonTemplate json = program.LowerToJson();
        Assert.Equal(["{\"#name\":\"Event\",\"#content\":[\"", "\",\"t\",{\"#name\":\"Child\",\"#content\":[\"", "\"]}]}"],
            json.Parts);
        Assert.Equal([false, false], json.InAttrValue);
        Assert.Equal([false, true], json.IsOptional);
    }

    /// <summary>
    /// Verifies that an element inside an attribute value renders its markup inline (no JSON comma) and that its own
    /// content is element content again, not attribute-value context.
    /// </summary>
    [Fact]
    public void LowersElementInsideAttributeValue()
    {
        TemplateProgram program = new([
            new(TemplateOpKind.StartElement, "A"), new(TemplateOpKind.BeginAttributes),
            new(TemplateOpKind.Attribute, "x"), new(TemplateOpKind.StartElement, "B"), new(TemplateOpKind.CloseStartElement),
            new(TemplateOpKind.Substitution, SubId: 0), new(TemplateOpKind.EndElement, "B"), new(TemplateOpKind.Substitution, SubId: 1),
            new(TemplateOpKind.EndAttribute), new(TemplateOpKind.EndAttributes), new(TemplateOpKind.CloseEmptyElement, "A")]);

        Assert.Equal(["<A x=\"<B>", "</B>", "\"></A>"], program.LowerToXml().Parts);

        CompiledJsonTemplate json = program.LowerToJson();
        Assert.Equal(["{\"#name\":\"A\",\"#attrs\":{\"x\":\"{\"#name\":\"B\",\"#content\":[\"", "\"]}", "\"}}"], json.Parts);
        Assert.Equal([false, true], json.InAttrValue);
    }

    #endregion
}
