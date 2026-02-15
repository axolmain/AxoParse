namespace AxoParse.Web.Models;

/// <summary>
/// Extracts display fields from EVTX record XML using fast string scanning.
/// Ported from AxoParse.Browser.EvtxInterop — adapted to return an <see cref="EventRecordView"/>
/// instead of writing to a <see cref="System.Text.Json.Utf8JsonWriter"/>.
/// </summary>
public static class EvtxFieldExtractor
{
    private static readonly string[] LevelNames = ["", "Critical", "Error", "Warning", "Information", "Verbose"];

    /// <summary>
    /// Extracts display fields from a single EVTX record's XML and returns a typed view.
    /// </summary>
    /// <param name="recordId">The event record ID from the record header.</param>
    /// <param name="writtenTime">The FILETIME timestamp from the record header.</param>
    /// <param name="xml">The reconstructed XML string for this record.</param>
    /// <returns>A populated <see cref="EventRecordView"/> with all extracted fields.</returns>
    public static EventRecordView Extract(ulong recordId, ulong writtenTime, string xml)
    {
        if (string.IsNullOrEmpty(xml))
        {
            return new EventRecordView(recordId, FileTimeToIso(writtenTime), "", "", 0, "", "", "", "");
        }

        string levelStr = ExtractTagText(xml, "Level");
        int level = 0;
        if (!string.IsNullOrEmpty(levelStr)) int.TryParse(levelStr, out level);
        string levelText = level >= 0 && level < LevelNames.Length ? LevelNames[level] : $"Level {level}";

        return new EventRecordView(
            RecordId: recordId,
            Timestamp: FileTimeToIso(writtenTime),
            EventId: ExtractTagText(xml, "EventID"),
            Provider: ExtractAttrValue(xml, "Provider", "Name"),
            Level: level,
            LevelText: levelText,
            Computer: ExtractTagText(xml, "Computer"),
            Channel: ExtractTagText(xml, "Channel"),
            EventData: ExtractEventData(xml));
    }

    /// <summary>
    /// Converts a Windows FILETIME (100-ns intervals since 1601-01-01) to an ISO 8601 string.
    /// </summary>
    /// <param name="filetime">The FILETIME value.</param>
    /// <returns>ISO 8601 date string, or empty if the value is out of range.</returns>
    private static string FileTimeToIso(ulong filetime)
    {
        if (filetime == 0) return "";
        // 1601-01-01 to 0001-01-01 in ticks
        const long epochDelta = 504_911_232_000_000_000L;
        long ticks = (long)filetime + epochDelta;
        if (ticks < DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks) return "";
        return new DateTime(ticks, DateTimeKind.Utc).ToString("o");
    }

    /// <summary>
    /// Extracts the text content of the first occurrence of the given XML tag.
    /// Handles self-closing tags and verifies the tag name boundary to avoid substring matches.
    /// </summary>
    /// <param name="xml">The XML string to search.</param>
    /// <param name="tag">The tag name (without angle brackets).</param>
    /// <returns>The text content between the open and close tags, or empty if not found.</returns>
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

    /// <summary>
    /// Extracts the value of a named attribute from the first occurrence of the given XML tag.
    /// </summary>
    /// <param name="xml">The XML string to search.</param>
    /// <param name="tag">The tag name (without angle brackets).</param>
    /// <param name="attr">The attribute name.</param>
    /// <returns>The attribute value, or empty if the tag or attribute is not found.</returns>
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

    /// <summary>
    /// Extracts event data from the EventData or UserData XML sections.
    /// Tries EventData first (with named Data elements), then falls back to UserData (leaf elements).
    /// </summary>
    /// <param name="xml">The full record XML.</param>
    /// <returns>Newline-separated key-value pairs, or empty if no data found.</returns>
    private static string ExtractEventData(string xml)
    {
        string content = ExtractTagText(xml, "EventData");
        if (!string.IsNullOrEmpty(content))
        {
            string result = ExtractDataPairs(content);
            if (!string.IsNullOrEmpty(result)) return result;
        }

        content = ExtractTagText(xml, "UserData");
        if (!string.IsNullOrEmpty(content))
        {
            string result = ExtractLeafPairs(content);
            if (!string.IsNullOrEmpty(result)) return result;
        }

        return "";
    }

    /// <summary>
    /// Extracts named key-value pairs from Data elements inside an EventData section.
    /// Data elements may have a Name attribute; unnamed data is included as a bare value.
    /// </summary>
    /// <param name="section">The inner content of the EventData element.</param>
    /// <returns>Newline-separated "Name: Value" pairs.</returns>
    private static string ExtractDataPairs(string section)
    {
        List<string> pairs = new();
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

    /// <summary>
    /// Extracts leaf element text from a UserData section as key-value pairs.
    /// Only includes elements that contain no child elements (leaf nodes with text content).
    /// </summary>
    /// <param name="section">The inner content of the UserData element.</param>
    /// <returns>Newline-separated "TagName: Value" pairs.</returns>
    private static string ExtractLeafPairs(string section)
    {
        List<string> pairs = new();
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

            string content = section.Substring(gt + 1, closePos - gt - 1);
            if (content.IndexOf('<') == -1)
            {
                string trimmed = content.Trim();
                if (!string.IsNullOrEmpty(trimmed))
                    pairs.Add($"{tag}: {trimmed}");
            }

            pos = gt + 1;
        }

        return string.Join("\n", pairs);
    }
}
