namespace AxoParse.Evtx;

/// <summary>
/// Specifies the output format for parsed EVTX event records.
/// </summary>
/// <remarks>
/// The format is currently chosen at parse time because the compiled template cache stores
/// precompiled XML string fragments. Separating parsing from rendering (to allow on-demand
/// format selection) would require redesigning the template compiler and is planned for a
/// future major version.
/// </remarks>
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
