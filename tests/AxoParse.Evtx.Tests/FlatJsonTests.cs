using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml;
using System.Xml.Linq;
using AxoParse.Evtx.BinXml;

namespace AxoParse.Evtx.Tests;

public class FlatJsonTests(ITestOutputHelper testOutputHelper)
{
    #region Public Methods

    /// <summary>
    /// Oracle test: for every record of every sample, the compact JSON is valid and holds exactly the record's XML (parsed
    /// with System.Xml) converted by the documented rules, comparing scalars as text (so a number matches its digits and
    /// an array its items joined with ", "). Records whose XML a strict parser rejects are skipped and counted.
    /// </summary>
    [Fact]
    public void CompactJsonMatchesRenderedXmlForAllTestFiles()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        int compared = 0;
        int unparsable = 0;
        List<string> failures = new();
        foreach (string file in Directory.GetFiles(TestPaths.TestDataDir, "*.evtx"))
        {
            byte[] data = File.ReadAllBytes(file);
            List<EvtxEvent> xml = EvtxParser.Parse(data, cancellationToken: token).Events.ToList();
            List<EvtxEvent> json = EvtxParser.Parse(data, new EvtxOptions { Format = OutputFormat.Json }, token).Events.ToList();
            Assert.Equal(xml.Count, json.Count);
            for (int i = 0; i < xml.Count; i++)
            {
                if (xml[i].Utf8.IsEmpty)
                {
                    Assert.True(json[i].Utf8.IsEmpty, $"{Path.GetFileName(file)} record {xml[i].RecordId}: JSON without XML");
                    continue;
                }

                JsonNode? actual;
                try
                {
                    actual = JsonNode.Parse(json[i].Utf8.Span);
                }
                catch (JsonException ex)
                {
                    failures.Add($"{Path.GetFileName(file)} record {xml[i].RecordId}: invalid JSON: {ex.Message}");
                    continue;
                }

                XElement root;
                try
                {
                    root = XElement.Parse(xml[i].GetText(), LoadOptions.PreserveWhitespace);
                }
                catch (XmlException)
                {
                    unparsable++;
                    continue;
                }

                compared++;
                JsonNode? expected = root.Name.LocalName == "Event"
                    ? ToJson(root, skipName: false, forceObject: true)
                    : new JsonObject { [root.Name.LocalName] = ToJson(root, skipName: false) };
                string want = Normalize(expected)!;
                string got = Normalize(actual)!;
                if ((want != got) && (failures.Count < 20))
                    failures.Add($"{Path.GetFileName(file)} record {xml[i].RecordId}:\n  xml:  {Excerpt(want, got)}\n  json: {Excerpt(got, want)}");
            }
        }

        testOutputHelper.WriteLine($"Compared {compared} records; {unparsable} not parseable as XML");
        Assert.True(compared > 90_000, $"only {compared} records compared");
        Assert.True(failures.Count == 0, string.Join('\n', failures));
    }

    /// <summary>
    /// Verifies that the plan writer (templates compiled for compact JSON) and the tree writer produce identical bytes
    /// for every record of every sample, in strict and salvage mode, and that most records take the plan.
    /// </summary>
    [Fact]
    public void PlanWriterMatchesTreeWriter()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        int records = 0;
        foreach (string file in Directory.GetFiles(TestPaths.TestDataDir, "*.evtx"))
        {
            byte[] data = File.ReadAllBytes(file);
            foreach (bool salvage in new[] { false, true })
            {
                EvtxOptions options = new() { Format = OutputFormat.Json, MaxThreads = 1, Salvage = salvage };
                List<EvtxEvent> planned = EvtxParser.Parse(data, options, token).Events.ToList();
                List<EvtxEvent> tree;
                BinXmlParser.ForceJsonTree = true;
                try
                {
                    tree = EvtxParser.Parse(data, options, token).Events.ToList();
                }
                finally
                {
                    BinXmlParser.ForceJsonTree = false;
                }

                Assert.Equal(tree.Count, planned.Count);
                for (int i = 0; i < tree.Count; i++)
                {
                    records++;
                    string treeText = tree[i].GetText();
                    string planText = planned[i].GetText();
                    Assert.True(treeText == planText,
                        $"{Path.GetFileName(file)} record {tree[i].RecordId} (salvage={salvage}):\n  tree: {Excerpt(treeText, planText)}\n  plan: {Excerpt(planText, treeText)}");
                    Assert.Equal(tree[i].Diagnostic, planned[i].Diagnostic);
                }
            }
        }

        Assert.True(records > 190_000);
    }

    /// <summary>
    /// Verifies the documented shape on a known Security event: no Event wrapper, attributes as keys, typed System
    /// numbers, hex kept as text, EventData keyed by Name, empty elements null.
    /// </summary>
    [Fact]
    public void KnownSecurityEventShape()
    {
        byte[] data = File.ReadAllBytes(Path.Combine(TestPaths.TestDataDir, "security.evtx"));
        EvtxEvent evt = EvtxParser.Parse(data, new EvtxOptions { Format = OutputFormat.Json }, TestContext.Current.CancellationToken)
            .Events.Single(e => e.RecordId == 2);
        using JsonDocument doc = JsonDocument.Parse(evt.Utf8);
        JsonElement system = doc.RootElement.GetProperty("System");
        Assert.Equal(4624, system.GetProperty("EventID").GetInt32());
        Assert.Equal("Microsoft-Windows-Security-Auditing", system.GetProperty("Provider").GetProperty("Name").GetString());
        Assert.Equal("0x8020000000000000", system.GetProperty("Keywords").GetString());
        Assert.Equal("2016-07-08T18:12:51.6816408Z", system.GetProperty("TimeCreated").GetProperty("SystemTime").GetString());
        Assert.Equal(456, system.GetProperty("Execution").GetProperty("ProcessID").GetInt32());
        Assert.Equal(JsonValueKind.Null, system.GetProperty("Security").ValueKind);
        JsonElement eventData = doc.RootElement.GetProperty("EventData");
        Assert.Equal("SYSTEM", eventData.GetProperty("TargetUserName").GetString());
        Assert.Equal(0, eventData.GetProperty("LogonType").GetInt32());
        Assert.Equal("0x3e7", eventData.GetProperty("TargetLogonId").GetString());
        Assert.False(doc.RootElement.TryGetProperty("Event", out _));
    }

    #endregion

    #region Non-Public Methods

    /// <summary>
    /// Shows where two strings start to differ.
    /// </summary>
    /// <param name="text">Text to excerpt.</param>
    /// <param name="other">Text it is compared with.</param>
    /// <returns>Up to 300 characters around the first difference.</returns>
    private static string Excerpt(string text, string other)
    {
        int at = 0;
        while ((at < text.Length) && (at < other.Length) && (text[at] == other[at]))
            at++;
        int start = Math.Max(0, at - 100);
        return text.Substring(start, Math.Min(300, text.Length - start));
    }

    /// <summary>
    /// Serialises a JSON tree with every scalar as a string (numbers as their digits, booleans as true/false, arrays of
    /// scalars as their items joined with ", ") and line breaks normalised, so a typed value equals the text XML shows.
    /// </summary>
    /// <param name="node">The tree.</param>
    /// <returns>The normalised JSON text.</returns>
    private static string? Normalize(JsonNode? node)
    {
        static JsonNode? Strings(JsonNode? n) => n switch
        {
            null => null,
            JsonObject o => new JsonObject(o.Select(kv => KeyValuePair.Create(kv.Key, Strings(kv.Value)))),
            JsonArray a when a.All(item => item is JsonValue) =>
                JsonValue.Create(string.Join(", ", a.Select(item => ScalarText(item!)))),
            JsonArray a => new JsonArray(a.Select(Strings).ToArray()),
            _ => JsonValue.Create(ScalarText(n)),
        };

        static string ScalarText(JsonNode value) =>
            (value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : value.ToJsonString()).ReplaceLineEndings("\n");

        return Strings(node)?.ToJsonString();
    }

    /// <summary>
    /// The reference conversion of an XML element by the documented rules (all scalars as strings).
    /// </summary>
    /// <param name="element">The element.</param>
    /// <param name="skipName">True if its Name attribute is its key (EventData items).</param>
    /// <param name="forceObject">True to write an object even without attributes or children.</param>
    /// <returns>The element's JSON value.</returns>
    private static JsonNode? ToJson(XElement element, bool skipName, bool forceObject = false)
    {
        List<XAttribute> attributes = element.Attributes()
            .Where(a => !a.IsNamespaceDeclaration && !(skipName && (a.Name.LocalName == "Name"))).ToList();
        List<XElement> children = element.Elements().ToList();
        string text = string.Concat(element.Nodes().OfType<XText>().Select(t => t.Value));
        if ((attributes.Count == 0) && (children.Count == 0) && !forceObject)
            return text.Length == 0 ? null : JsonValue.Create(text);

        JsonObject result = new();
        foreach (XAttribute attribute in attributes)
            result[attribute.Name.LocalName] = attribute.Value.Length == 0 ? null : JsonValue.Create(attribute.Value);

        bool underEventData = element.Name.LocalName == "EventData";
        List<(string Key, XElement Child, bool Named)> keyed = children
            .Select(c => underEventData && (c.Attribute("Name") is { } name)
                ? (name.Value, c, true)
                : (c.Name.LocalName, c, false))
            .ToList();
        foreach (string key in keyed.Select(k => k.Key).Distinct())
        {
            List<(string Key, XElement Child, bool Named)> group = keyed.Where(k => k.Key == key).ToList();
            result[key] = group.Count == 1
                ? ToJson(group[0].Child, group[0].Named)
                : new JsonArray(group.Select(g => ToJson(g.Child, g.Named)).ToArray());
        }

        if (text.Length > 0)
            result["#text"] = text;
        return result;
    }

    #endregion
}
