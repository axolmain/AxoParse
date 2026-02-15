using System.Runtime.InteropServices.JavaScript;
using System.Text;
using System.Text.Json;
using AxoParse.Evtx;

namespace AxoParse.Browser;

public partial class EvtxInterop
{
    private static readonly string[] LevelNames = ["", "Critical", "Error", "Warning", "Information", "Verbose"];

    /// <summary>
    /// Parses an EVTX file from a byte array and returns a JSON string containing all parsed event records.
    /// Called from JavaScript via [JSExport].
    /// </summary>
    [JSExport]
    public static string ParseEvtxToJson(byte[] data)
    {
        var parser = EvtxParser.Parse(data, 1); // single-threaded in WASM

        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream);

        writer.WriteStartObject();

        writer.WriteNumber("totalRecords", parser.TotalRecords);
        writer.WriteNumber("numChunks", parser.Chunks.Count);

        writer.WriteStartArray("records");

        for (int ci = 0; ci < parser.Chunks.Count; ci++)
        {
            var chunk = parser.Chunks[ci];
            for (int ri = 0; ri < chunk.Records.Count; ri++)
            {
                var record = chunk.Records[ri];
                string xml = ri < chunk.ParsedXml.Length ? chunk.ParsedXml[ri] : "";

                writer.WriteStartObject();
                writer.WriteNumber("recordId", record.EventRecordId);
                writer.WriteString("timestamp", FileTimeToIso(record.WrittenTime));
                writer.WriteString("xml", xml);
                writer.WriteNumber("chunkIndex", ci);

                // Extract fields from XML for the React app
                WriteXmlFields(writer, xml);

                writer.WriteEndObject();
            }
        }

        writer.WriteEndArray();

        writer.WriteStartArray("warnings");
        writer.WriteEndArray();

        writer.WriteEndObject();
        writer.Flush();

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static string FileTimeToIso(ulong filetime)
    {
        if (filetime == 0) return "";
        const long epochDelta = 504_911_232_000_000_000L; // 1601-01-01 to 0001-01-01 in ticks
        long ticks = (long)filetime + epochDelta;
        if (ticks < DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks) return "";
        return new DateTime(ticks, DateTimeKind.Utc).ToString("o");
    }

    private static void WriteXmlFields(Utf8JsonWriter writer, string xml)
    {
        if (string.IsNullOrEmpty(xml))
        {
            WriteEmptyFields(writer);
            return;
        }

        writer.WriteString("eventId", ExtractTagText(xml, "EventID"));
        writer.WriteString("provider", ExtractAttrValue(xml, "Provider", "Name"));

        string levelStr = ExtractTagText(xml, "Level");
        int level = 0;
        if (!string.IsNullOrEmpty(levelStr)) int.TryParse(levelStr, out level);
        writer.WriteNumber("level", level);
        writer.WriteString("levelText", level >= 0 && level < LevelNames.Length ? LevelNames[level] : $"Level {level}");

        writer.WriteString("computer", ExtractTagText(xml, "Computer"));
        writer.WriteString("channel", ExtractTagText(xml, "Channel"));
        writer.WriteString("task", ExtractTagText(xml, "Task"));
        writer.WriteString("opcode", ExtractTagText(xml, "Opcode"));
        writer.WriteString("keywords", ExtractTagText(xml, "Keywords"));
        writer.WriteString("version", ExtractTagText(xml, "Version"));
        writer.WriteString("processId", ExtractAttrValue(xml, "Execution", "ProcessID"));
        writer.WriteString("threadId", ExtractAttrValue(xml, "Execution", "ThreadID"));
        writer.WriteString("securityUserId", ExtractAttrValue(xml, "Security", "UserID"));
        writer.WriteString("activityId", ExtractAttrValue(xml, "Correlation", "ActivityID"));
        writer.WriteString("relatedActivityId", ExtractAttrValue(xml, "Correlation", "RelatedActivityID"));
        writer.WriteString("eventData", ExtractEventData(xml));
    }

    private static void WriteEmptyFields(Utf8JsonWriter writer)
    {
        writer.WriteString("eventId", "");
        writer.WriteString("provider", "");
        writer.WriteNumber("level", 0);
        writer.WriteString("levelText", "");
        writer.WriteString("computer", "");
        writer.WriteString("channel", "");
        writer.WriteString("task", "");
        writer.WriteString("opcode", "");
        writer.WriteString("keywords", "");
        writer.WriteString("version", "");
        writer.WriteString("processId", "");
        writer.WriteString("threadId", "");
        writer.WriteString("securityUserId", "");
        writer.WriteString("activityId", "");
        writer.WriteString("relatedActivityId", "");
        writer.WriteString("eventData", "");
    }

    private static string ExtractTagText(string xml, string tag)
    {
        string open = $"<{tag}";
        int start = xml.IndexOf(open, StringComparison.Ordinal);
        if (start == -1) return "";

        // Verify it's not a substring of a longer tag name
        int afterTag = start + open.Length;
        if (afterTag < xml.Length)
        {
            char c = xml[afterTag];
            if (c != '>' && c != ' ' && c != '/' && c != '\t' && c != '\n' && c != '\r') return "";
        }

        int gt = xml.IndexOf('>', start);
        if (gt == -1) return "";
        if (xml[gt - 1] == '/') return ""; // self-closing

        string closeTag = $"</{tag}>";
        int close = xml.IndexOf(closeTag, gt + 1, StringComparison.Ordinal);
        if (close == -1) return "";
        return xml.Substring(gt + 1, close - gt - 1);
    }

    private static string ExtractAttrValue(string xml, string tag, string attr)
    {
        string open = $"<{tag}";
        int start = xml.IndexOf(open, StringComparison.Ordinal);
        if (start == -1) return "";

        int afterTag = start + open.Length;
        if (afterTag < xml.Length)
        {
            char c = xml[afterTag];
            if (c != '>' && c != ' ' && c != '/' && c != '\t' && c != '\n' && c != '\r') return "";
        }

        int gt = xml.IndexOf('>', start);
        if (gt == -1) return "";

        string search = $"{attr}=\"";
        int attrStart = xml.IndexOf(search, start, StringComparison.Ordinal);
        if (attrStart == -1 || attrStart >= gt) return "";

        int valStart = attrStart + search.Length;
        int valEnd = xml.IndexOf('"', valStart);
        if (valEnd == -1 || valEnd > gt) return "";
        return xml.Substring(valStart, valEnd - valStart);
    }

    private static string ExtractEventData(string xml)
    {
        // Find <EventData> section
        string content = ExtractTagText(xml, "EventData");
        if (!string.IsNullOrEmpty(content))
        {
            string result = ExtractDataPairs(content);
            if (!string.IsNullOrEmpty(result)) return result;
        }

        // UserData fallback
        content = ExtractTagText(xml, "UserData");
        if (!string.IsNullOrEmpty(content))
        {
            string result = ExtractLeafPairs(content);
            if (!string.IsNullOrEmpty(result)) return result;
        }

        return "";
    }

    private static string ExtractDataPairs(string section)
    {
        var pairs = new List<string>();
        int pos = 0;
        while (true)
        {
            int ds = section.IndexOf("<Data", pos, StringComparison.Ordinal);
            if (ds == -1) break;

            int gt = section.IndexOf('>', ds + 5);
            if (gt == -1) break;

            if (section[gt - 1] == '/')
            {
                pos = gt + 1;
                continue;
            }

            int ce = section.IndexOf("</Data>", gt + 1, StringComparison.Ordinal);
            if (ce == -1) break;

            string value = section.Substring(gt + 1, ce - gt - 1);
            if (!string.IsNullOrEmpty(value))
            {
                string search = "Name=\"";
                int ni = section.IndexOf(search, ds + 5, StringComparison.Ordinal);
                if (ni != -1 && ni < gt)
                {
                    int nvs = ni + search.Length;
                    int nve = section.IndexOf('"', nvs);
                    if (nve != -1 && nve < gt)
                        pairs.Add($"{section.Substring(nvs, nve - nvs)}: {value}");
                    else
                        pairs.Add(value);
                }
                else
                {
                    pairs.Add(value);
                }
            }

            pos = ce + 7;
        }

        return string.Join("\n", pairs);
    }

    private static string ExtractLeafPairs(string section)
    {
        var pairs = new List<string>();
        int pos = 0;
        while (pos < section.Length)
        {
            int lt = section.IndexOf('<', pos);
            if (lt == -1) break;

            char nc = lt + 1 < section.Length ? section[lt + 1] : '\0';
            if (nc == '/' || nc == '!' || nc == '?')
            {
                int gt2 = section.IndexOf('>', lt + 2);
                pos = gt2 == -1 ? section.Length : gt2 + 1;
                continue;
            }

            int ne = lt + 1;
            while (ne < section.Length)
            {
                char c2 = section[ne];
                if (c2 == ' ' || c2 == '>' || c2 == '/' || c2 == '\t' || c2 == '\n' || c2 == '\r') break;
                ne++;
            }

            string tag = section.Substring(lt + 1, ne - lt - 1);
            int gt = section.IndexOf('>', lt);
            if (gt == -1) break;

            if (section[gt - 1] == '/')
            {
                pos = gt + 1;
                continue;
            }

            string closeTag = $"</{tag}>";
            int closePos = section.IndexOf(closeTag, gt + 1, StringComparison.Ordinal);
            if (closePos == -1)
            {
                pos = gt + 1;
                continue;
            }

            string content2 = section.Substring(gt + 1, closePos - gt - 1);
            if (content2.IndexOf('<') == -1)
            {
                string trimmed = content2.Trim();
                if (!string.IsNullOrEmpty(trimmed))
                    pairs.Add($"{tag}: {trimmed}");
            }

            pos = gt + 1;
        }

        return string.Join("\n", pairs);
    }
}