namespace AxoParse.Evtx;

/// <summary>
/// Specifies the output format for parsed EVTX event records.
/// </summary>
public enum OutputFormat
{
    /// <summary>
    /// Render each event record as an XML string.
    /// </summary>
    Xml,

    /// <summary>
    /// Render each event record as a UTF-8 JSON byte array.
    /// </summary>
    Json
}
