using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace AxoParse.Evtx.Tests;

public class EventFieldsTests(ITestOutputHelper testOutputHelper)
{
    #region Public Methods

    /// <summary>
    /// Oracle test: for every record of every sample, the typed System fields and EventData/UserData pairs read from
    /// the template's substitution values equal the same fields parsed out of the record's own rendered XML with
    /// System.Xml. Records whose XML a strict parser rejects (control characters Windows wrote as is) are skipped and
    /// counted.
    /// </summary>
    [Fact]
    public void FieldsMatchRenderedXmlForAllTestFiles()
    {
        int compared = 0;
        int unparsable = 0;
        List<string> failures = new();
        foreach (string file in Directory.GetFiles(TestPaths.TestDataDir, "*.evtx"))
        {
            string name = Path.GetFileName(file);
            EvtxLog log = EvtxParser.Parse(file, new EvtxOptions { Fields = EvtxFields.All },
                TestContext.Current.CancellationToken);
            foreach (EvtxEvent evt in log.Events)
            {
                if (evt.Utf8.IsEmpty)
                    continue;

                XElement root;
                try
                {
                    root = XElement.Parse(evt.GetText(), LoadOptions.PreserveWhitespace);
                }
                catch (XmlException)
                {
                    unparsable++;
                    continue;
                }

                compared++;
                string where = $"{name} record {evt.RecordId}";
                foreach (string difference in Compare(evt, root))
                {
                    if (failures.Count < 50)
                        failures.Add($"{where}: {difference}");
                }
            }
        }

        testOutputHelper.WriteLine($"Compared {compared} records; {unparsable} not parseable as XML");
        Assert.True(compared > 90_000, $"only {compared} records compared");
        Assert.True(unparsable < compared / 100, $"{unparsable} records not parseable as XML");
        Assert.True(failures.Count == 0, string.Join('\n', failures));
    }

    /// <summary>
    /// Verifies that fields don't depend on how a log is read: XML, structured JSON and fields-only parses give every
    /// event the same System fields and pairs, at 1 and 8 threads.
    /// </summary>
    [Fact]
    public void FieldsAreTheSameInEveryFormatAndMode()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        foreach (string file in Directory.GetFiles(TestPaths.TestDataDir, "*.evtx"))
        {
            byte[] data = File.ReadAllBytes(file);
            List<string> expected = EvtxParser.Parse(data, new EvtxOptions { Fields = EvtxFields.All, MaxThreads = 1 }, token)
                .Events.Select(Describe).ToList();
            foreach (EvtxOptions options in new EvtxOptions[]
                     {
                         new() { Fields = EvtxFields.All, Format = OutputFormat.JsonStructured, MaxThreads = 8 },
                         new() { Fields = EvtxFields.All, FieldsOnly = true, MaxThreads = 8 },
                         new() { Fields = EvtxFields.All, FieldsOnly = true, MaxThreads = 1 },
                     })
            {
                List<string> actual = EvtxParser.ReadEvents(data, options, token).Select(Describe).ToList();
                Assert.Equal(expected.Count, actual.Count);
                for (int i = 0; i < expected.Count; i++)
                    Assert.True(expected[i] == actual[i],
                        $"{Path.GetFileName(file)} event {i} ({options.Format}, FieldsOnly={options.FieldsOnly}):\n{expected[i]}\n{actual[i]}");
            }
        }
    }

    /// <summary>
    /// Verifies fields-only parsing: no rendered output, the same events as a rendering parse, and System fields set.
    /// </summary>
    [Fact]
    public void FieldsOnlyRendersNothing()
    {
        byte[] data = File.ReadAllBytes(Path.Combine(TestPaths.TestDataDir, "security.evtx"));
        EvtxLog rendered = EvtxParser.Parse(data, cancellationToken: TestContext.Current.CancellationToken);
        EvtxLog fieldsOnly = EvtxParser.Parse(data, new EvtxOptions { FieldsOnly = true },
            TestContext.Current.CancellationToken);

        Assert.Equal(rendered.RecordCount, fieldsOnly.RecordCount);
        Assert.All(fieldsOnly.Events, evt => Assert.True(evt.Utf8.IsEmpty));
        EvtxEvent first = fieldsOnly.Events.Skip(1).First();
        Assert.Equal(4624, first.EventId);
        Assert.Equal("Microsoft-Windows-Security-Auditing", first.Provider);
        Assert.Equal("Security", first.Channel);
        Assert.Throws<ArgumentException>(() => EvtxParser.WriteTo(data, Stream.Null, new EvtxOptions { FieldsOnly = true },
            TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// Verifies the shape of a known Security event's fields and pairs, and that fields that weren't requested throw
    /// (<see cref="EvtxFields.System"/> reads no pairs; no fields at all by default).
    /// </summary>
    [Fact]
    public void KnownSecurityEventFields()
    {
        byte[] data = File.ReadAllBytes(Path.Combine(TestPaths.TestDataDir, "security.evtx"));
        EvtxEvent evt = EvtxParser.Parse(data, new EvtxOptions { Fields = EvtxFields.All }, TestContext.Current.CancellationToken)
            .Events.Single(e => e.RecordId == 2);

        Assert.Equal(4624, evt.EventId);
        Assert.Equal(12544, evt.Task);
        Assert.Equal(0x8020000000000000UL, evt.Keywords);
        Assert.Equal(new DateTime(2016, 7, 8, 18, 12, 51, DateTimeKind.Utc).AddTicks(6816408), evt.TimeCreated);
        Assert.Equal(Guid.Parse("54849625-5478-4994-A5BA-3E3B0328C30D"), evt.ProviderGuid);
        Assert.Null(evt.ActivityId);
        Assert.Equal(456u, evt.ProcessId);
        Assert.Equal(460u, evt.ThreadId);
        Assert.Equal("37L4247F27-25", evt.Computer);
        Assert.Null(evt.UserId);
        Assert.Equal(20, evt.EventData.Count);
        Assert.True(evt.EventData.TryGetValue("TargetUserName", out string user));
        Assert.Equal("SYSTEM", user);
        Assert.Equal("0x3e7", evt.EventData[7].Value);
        Assert.False(evt.EventData.TryGetValue("NoSuchName", out _));

        EvtxEvent systemOnly = EvtxParser.Parse(data, new EvtxOptions { Fields = EvtxFields.System },
            TestContext.Current.CancellationToken).Events.Single(e => e.RecordId == 2);
        Assert.Equal(4624, systemOnly.EventId);
        Assert.Throws<InvalidOperationException>(() => systemOnly.EventData);

        EvtxEvent noFields = EvtxParser.Parse(data, cancellationToken: TestContext.Current.CancellationToken).Events.First();
        Assert.Throws<InvalidOperationException>(() => noFields.EventId);
        Assert.Throws<InvalidOperationException>(() => noFields.EventData);
    }

    #endregion

    #region Non-Public Methods

    /// <summary>
    /// Lists every difference between an event's typed fields and pairs and the same fields read from its XML.
    /// </summary>
    /// <param name="evt">The event.</param>
    /// <param name="root">The event's rendered XML.</param>
    /// <returns>Descriptions of the differences.</returns>
    private static IEnumerable<string> Compare(EvtxEvent evt, XElement root)
    {
        XElement? system = Child(root, "System");
        string? Text(string element) => NonEmpty(Child(system, element)?.Value);
        string? Attr(string element, string attribute) => NonEmpty(Child(system, element)?.Attribute(attribute)?.Value);

        if (Number(Text("EventID")) is { } eventId && (eventId != evt.EventId)) yield return $"EventID {eventId} != {evt.EventId}";
        if (Number(Text("Version")) is { } version && (version != evt.Version)) yield return $"Version {version} != {evt.Version}";
        if (Number(Text("Level")) is { } level && (level != evt.Level)) yield return $"Level {level} != {evt.Level}";
        if (Number(Text("Task")) is { } task && (task != evt.Task)) yield return $"Task {task} != {evt.Task}";
        if (Number(Text("Opcode")) is { } opcode && (opcode != evt.Opcode)) yield return $"Opcode {opcode} != {evt.Opcode}";
        if (Number(Text("Keywords")) is { } keywords && (keywords != evt.Keywords)) yield return $"Keywords {keywords} != {evt.Keywords}";
        if (Number(Attr("Execution", "ProcessID")) is { } pid && (pid != evt.ProcessId)) yield return $"ProcessID {pid} != {evt.ProcessId}";
        if (Number(Attr("Execution", "ThreadID")) is { } tid && (tid != evt.ThreadId)) yield return $"ThreadID {tid} != {evt.ThreadId}";
        if (Text("Channel") != evt.Channel) yield return $"Channel '{Text("Channel")}' != '{evt.Channel}'";
        if (Text("Computer") != evt.Computer) yield return $"Computer '{Text("Computer")}' != '{evt.Computer}'";
        if (Attr("Provider", "Name") != evt.Provider) yield return $"Provider '{Attr("Provider", "Name")}' != '{evt.Provider}'";
        if (Attr("Security", "UserID") != evt.UserId) yield return $"UserID '{Attr("Security", "UserID")}' != '{evt.UserId}'";
        if (Guid.TryParse(Attr("Provider", "Guid"), out Guid providerGuid) && (providerGuid != evt.ProviderGuid))
            yield return $"Provider@Guid {providerGuid} != {evt.ProviderGuid}";
        if (Guid.TryParse(Attr("Correlation", "ActivityID"), out Guid activity) && (activity != evt.ActivityId))
            yield return $"ActivityID {activity} != {evt.ActivityId}";
        if (DateTime.TryParse(Attr("TimeCreated", "SystemTime"), CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out DateTime created) &&
            (created != evt.TimeCreated))
            yield return $"TimeCreated {created:O} != {evt.TimeCreated:O}";

        List<string> expected = new();
        if (Child(root, "EventData") is { } eventData)
            CollectLeaves(eventData, byNameAttribute: true, expected);
        if (Child(root, "UserData") is { } userData)
            CollectLeaves(userData, byNameAttribute: false, expected);
        // An XML parser normalises line breaks to LF; the pairs keep the text the renderer writes
        List<string> actual = evt.EventData.Select(kv => $"{kv.Key}={kv.Value.ReplaceLineEndings("\n")}").ToList();
        if (!expected.SequenceEqual(actual))
            yield return $"pairs differ:\n  xml:    {string.Join(" | ", expected)}\n  fields: {string.Join(" | ", actual)}";
    }

    /// <summary>
    /// The first child element named <paramref name="name"/> (any namespace).
    /// </summary>
    /// <param name="parent">Parent element, or null.</param>
    /// <param name="name">Local name.</param>
    /// <returns>The child, or null.</returns>
    private static XElement? Child(XElement? parent, string name) =>
        parent?.Elements().FirstOrDefault(e => e.Name.LocalName == name);

    /// <summary>
    /// Adds every leaf element below <paramref name="parent"/> as <c>name=value</c>, in document order.
    /// </summary>
    /// <param name="parent">EventData or UserData.</param>
    /// <param name="byNameAttribute">True to name a leaf by its <c>Name</c> attribute when it has one (EventData).</param>
    /// <param name="pairs">Receives the pairs.</param>
    private static void CollectLeaves(XElement parent, bool byNameAttribute, List<string> pairs)
    {
        foreach (XElement child in parent.Elements())
        {
            if (child.HasElements)
            {
                CollectLeaves(child, byNameAttribute, pairs);
                continue;
            }

            string name = (byNameAttribute ? child.Attribute("Name")?.Value : null) ?? child.Name.LocalName;
            pairs.Add($"{name}={child.Value}");
        }
    }

    /// <summary>
    /// One line describing an event's fields and pairs, for comparing parses.
    /// </summary>
    /// <param name="evt">The event.</param>
    /// <returns>The description.</returns>
    private static string Describe(EvtxEvent evt) =>
        $"{evt.RecordId} {evt.EventId} {evt.Version} {evt.Level} {evt.Task} {evt.Opcode} {evt.Keywords} {evt.TimeCreated:O} " +
        $"{evt.Provider} {evt.ProviderGuid} {evt.ActivityId} {evt.ProcessId} {evt.ThreadId} {evt.Channel} {evt.Computer} " +
        $"{evt.UserId} [{string.Join(" | ", evt.EventData.Select(kv => $"{kv.Key}={kv.Value}"))}]";

    /// <summary>
    /// Null for an empty string.
    /// </summary>
    /// <param name="text">Text, or null.</param>
    /// <returns>The text, or null when empty.</returns>
    private static string? NonEmpty(string? text) => string.IsNullOrEmpty(text) ? null : text;

    /// <summary>
    /// Parses a decimal or <c>0x</c> hexadecimal integer, or null.
    /// </summary>
    /// <param name="text">The text, or null.</param>
    /// <returns>The value, or null when absent or not an integer.</returns>
    private static ulong? Number(string? text)
    {
        if (text == null)
            return null;
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return ulong.TryParse(text.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong hex) ? hex : null;
        return ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out ulong value) ? value : null;
    }

    #endregion
}
