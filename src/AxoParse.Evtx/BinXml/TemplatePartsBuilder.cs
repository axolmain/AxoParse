using System.Text;

namespace AxoParse.Evtx.BinXml;

/// <summary>
/// Accumulates the static string parts of a compiled template (XML or JSON).
/// Parts alternate with substitution slots: parts[0] + sub[0] + parts[1] + ... + parts[N].
/// The open (last) part is built in a single reusable <see cref="StringBuilder"/>, so appending
/// markup is amortised O(1) instead of re-concatenating the whole part on every token.
/// </summary>
internal sealed class TemplatePartsBuilder
{
    #region Properties

    /// <summary>
    /// Builder for the part currently being accumulated (the text after the most recent substitution).
    /// </summary>
    public StringBuilder Current { get; } = new(256);

    #endregion

    #region Public Methods

    /// <summary>
    /// Closes the current part and starts a new, empty one. Called at every substitution token.
    /// </summary>
    public void NextPart()
    {
        _parts.Add(Current.ToString());
        Current.Clear();
    }

    /// <summary>
    /// Closes the current part and returns all parts. The builder must not be used afterwards.
    /// </summary>
    /// <returns>Static parts; length is always substitution count + 1.</returns>
    public string[] ToArray()
    {
        NextPart();
        return _parts.ToArray();
    }

    #endregion

    #region Non-Public Fields

    /// <summary>
    /// Completed parts, in template order.
    /// </summary>
    private readonly List<string> _parts = new();

    #endregion
}
