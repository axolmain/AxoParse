namespace AxoParse.Evtx;

/// <summary>
/// Thrown when EVTX data fails structural validation (truncated headers, wrong magic signatures).
/// <see cref="ErrorCode"/> identifies the specific failure so callers can handle each case
/// programmatically without parsing the message string.
/// </summary>
public sealed class EvtxParseException : Exception
{
    /// <summary>
    /// The specific validation failure that caused this exception.
    /// </summary>
    public EvtxParseError ErrorCode { get; }

    /// <summary>
    /// Creates an EvtxParseException with the specified error code and message.
    /// </summary>
    /// <param name="errorCode">Identifies the validation failure.</param>
    /// <param name="message">Human-readable description of the error.</param>
    public EvtxParseException(EvtxParseError errorCode, string message)
        : base(message)
    {
        ErrorCode = errorCode;
    }
}
