using System.Collections;
using System.Text;

namespace AxoParse.Evtx;

/// <summary>
/// An event's EventData or UserData as name/value pairs, in document order: each <c>&lt;Data Name="…"&gt;</c> under
/// <c>EventData</c> (named by its <c>Name</c> attribute, or <c>Data</c> when it has none, so names can repeat), or each
/// leaf element under <c>UserData</c> (named by the element). Values are the text the XML output shows, unescaped.
/// A small view over the chunk's data; each value is decoded to a new string when read.
/// </summary>
public readonly struct EvtxEventData : IReadOnlyList<KeyValuePair<string, string>>
{
    #region Constructors And Destructors

    /// <summary>
    /// Creates a view of pairs <paramref name="start"/> (inclusive) to <paramref name="end"/> (exclusive).
    /// </summary>
    /// <param name="fields">The chunk's fields.</param>
    /// <param name="start">Index of the event's first pair.</param>
    /// <param name="end">Index after the event's last pair.</param>
    internal EvtxEventData(ChunkFields fields, int start, int end)
    {
        _fields = fields;
        _start = start;
        _end = end;
    }

    #endregion

    #region Properties

    /// <summary>
    /// Number of pairs.
    /// </summary>
    public int Count => _end - _start;

    /// <summary>
    /// The pair at <paramref name="index"/>.
    /// </summary>
    /// <param name="index">Zero-based pair index.</param>
    public KeyValuePair<string, string> this[int index]
    {
        get
        {
            if ((uint)index >= (uint)Count)
                throw new ArgumentOutOfRangeException(nameof(index));
            return new KeyValuePair<string, string>(_fields.Names[_start + index], Value(_start + index));
        }
    }

    #endregion

    #region Public Methods

    /// <summary>
    /// Enumerates the pairs in document order.
    /// </summary>
    /// <returns>An enumerator over the pairs.</returns>
    public IEnumerator<KeyValuePair<string, string>> GetEnumerator()
    {
        for (int i = _start; i < _end; i++)
            yield return new KeyValuePair<string, string>(_fields.Names[i], Value(i));
    }

    /// <summary>
    /// Finds the first pair named <paramref name="name"/> (ordinal comparison).
    /// </summary>
    /// <param name="name">Pair name, e.g. <c>TargetUserName</c>.</param>
    /// <param name="value">The value, when found.</param>
    /// <returns>True if a pair has that name.</returns>
    public bool TryGetValue(string name, out string value)
    {
        for (int i = _start; i < _end; i++)
        {
            if (string.Equals(_fields.Names[i], name, StringComparison.Ordinal))
            {
                value = Value(i);
                return true;
            }
        }

        value = string.Empty;
        return false;
    }

    /// <summary>
    /// The UTF-8 value of the pair at <paramref name="index"/>, without decoding it.
    /// </summary>
    /// <param name="index">Zero-based pair index.</param>
    /// <returns>The value bytes, a slice of the chunk's buffer.</returns>
    public ReadOnlyMemory<byte> GetUtf8Value(int index)
    {
        if ((uint)index >= (uint)Count)
            throw new ArgumentOutOfRangeException(nameof(index));
        return Utf8(_start + index);
    }

    /// <summary>
    /// Non-generic enumerator.
    /// </summary>
    /// <returns>An enumerator over the pairs.</returns>
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    #endregion

    #region Non-Public Methods

    /// <summary>
    /// The UTF-8 value of pair <paramref name="pair"/> (an index into the chunk's pairs).
    /// </summary>
    /// <param name="pair">Chunk-wide pair index.</param>
    /// <returns>The value bytes.</returns>
    private ReadOnlyMemory<byte> Utf8(int pair)
    {
        int start = pair == 0 ? 0 : _fields.ValueEnds[pair - 1];
        return new ReadOnlyMemory<byte>(_fields.Text, start, _fields.ValueEnds[pair] - start);
    }

    /// <summary>
    /// The value of pair <paramref name="pair"/> as a new string.
    /// </summary>
    /// <param name="pair">Chunk-wide pair index.</param>
    /// <returns>The value.</returns>
    private string Value(int pair) => Encoding.UTF8.GetString(Utf8(pair).Span);

    #endregion

    #region Non-Public Fields

    /// <summary>
    /// Index after the event's last pair.
    /// </summary>
    private readonly int _end;

    /// <summary>
    /// The chunk's fields.
    /// </summary>
    private readonly ChunkFields _fields;

    /// <summary>
    /// Index of the event's first pair.
    /// </summary>
    private readonly int _start;

    #endregion
}
