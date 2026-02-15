namespace AxoParse.Web.Models;

/// <summary>
/// Flat view of a single parsed EVTX event record for table display.
/// Fields are extracted from the record's XML representation.
/// </summary>
/// <param name="RecordId">The event record ID from the EVTX record header.</param>
/// <param name="Timestamp">ISO 8601 timestamp converted from the FILETIME written time.</param>
/// <param name="EventId">The EventID element value from the System section.</param>
/// <param name="Provider">The Name attribute of the Provider element.</param>
/// <param name="Level">Numeric event level (0=LogAlways, 1=Critical, 2=Error, 3=Warning, 4=Information, 5=Verbose).</param>
/// <param name="LevelText">Human-readable level name.</param>
/// <param name="Computer">The Computer element value from the System section.</param>
/// <param name="Channel">The Channel element value from the System section.</param>
/// <param name="EventData">Concatenated key-value pairs from EventData or UserData sections.</param>
public readonly record struct EventRecordView(
    ulong RecordId,
    string Timestamp,
    string EventId,
    string Provider,
    int Level,
    string LevelText,
    string Computer,
    string Channel,
    string EventData);
