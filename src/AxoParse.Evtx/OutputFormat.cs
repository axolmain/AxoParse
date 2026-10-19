namespace AxoParse.Evtx;

/// <summary>
/// The text format events are rendered in (<see cref="EvtxOptions.Format"/>). Every format is UTF-8.
/// </summary>
public enum OutputFormat
{
    /// <summary>
    /// The event's XML, as Windows Event Viewer shows it: <c>&lt;Event xmlns="…"&gt;&lt;System&gt;…</c>.
    /// </summary>
    Xml,

    /// <summary>
    /// A lossless JSON rendering of the XML tree: every element is an object with <c>#name</c>, optional
    /// <c>#attrs</c> (attribute name to string value) and optional <c>#content</c> (an array of child element
    /// objects and text strings, in document order). Embedded BinXml values are JSON strings holding their own
    /// rendering.
    /// </summary>
    JsonStructured,

    /// <summary>
    /// Compact JSON with a stable, documented shape (see docs/JsonFormat.md):
    /// <c>{"System":{"Provider":{"Name":"…"},"EventID":4624,…},"EventData":{"TargetUserName":"…",…}}</c>. Attributes are
    /// plain keys, an element with both attributes and text keeps the text under <c>#text</c>, EventData items are
    /// keyed by their <c>Name</c>, repeated names become arrays, and values keep their types (numbers, booleans,
    /// arrays). Embedded BinXml is nested in place.
    /// </summary>
    Json,
}
