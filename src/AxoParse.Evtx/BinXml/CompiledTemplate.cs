namespace AxoParse.Evtx.BinXml;

/// <summary>
/// Per-substitution slot metadata packed into a single struct for cache-friendly access.
/// </summary>
/// <param name="subId">Substitution index into the template instance's values.</param>
/// <param name="isOptional">Whether this is an optional substitution (0x0E), skipped when its value is null or empty.</param>
/// <param name="prefix">Markup written before the value only when the value is written, or null.</param>
/// <param name="suffix">Markup written after the value only when the value is written, or null.</param>
internal readonly struct SubSlot(int subId, bool isOptional, byte[]? prefix, byte[]? suffix)
{
    /// <summary>
    /// Substitution index into the template instance's values.
    /// </summary>
    public readonly int SubId = subId;

    /// <summary>
    /// Whether this substitution is optional (0x0E OptionalSubstitution).
    /// Optional substitutions are skipped when the value is null or zero-length.
    /// </summary>
    public readonly bool IsOptional = isOptional;

    /// <summary>
    /// UTF-8 markup written before the value only when the value is written: in XML, the <c> Name="</c> of an
    /// attribute whose whole value is this optional substitution, so the attribute is omitted when the value is empty.
    /// Null when the slot has no conditional markup.
    /// </summary>
    public readonly byte[]? Prefix = prefix;

    /// <summary>
    /// UTF-8 markup written after the value only when the value is written (the attribute's closing quote), or null.
    /// </summary>
    public readonly byte[]? Suffix = suffix;
}

/// <summary>
/// A template compiled for one output format: the static UTF-8 output between its substitutions, and one slot per
/// substitution. It is the output the format's writer produces for the template's
/// <see cref="TemplateProgram"/> with every value left out (see <see cref="BinXmlParser.CompileXml"/> and
/// <see cref="BinXmlParser.CompileJson"/>), so rendering a record only interleaves these parts with its formatted values.
/// </summary>
internal sealed class CompiledTemplate
{
    #region Fields

    /// <summary>
    /// Static UTF-8 output; part <c>i</c> precedes slot <c>i</c>, and the last part follows the last slot.
    /// </summary>
    public readonly byte[][] Parts;

    /// <summary>
    /// One slot per substitution, in output order.
    /// </summary>
    public readonly SubSlot[] Slots;

    #endregion

    #region Constructors And Destructors

    /// <summary>
    /// Creates a compiled template.
    /// </summary>
    /// <param name="parts">Static UTF-8 parts; one more than <paramref name="slots"/>.</param>
    /// <param name="slots">Substitution slots; slot <c>i</c> sits between parts <c>i</c> and <c>i + 1</c>.</param>
    public CompiledTemplate(byte[][] parts, SubSlot[] slots)
    {
        Parts = parts;
        Slots = slots;
    }

    #endregion

    #region Properties

    /// <summary>
    /// Sentinel cached for a template that compiles to a program but isn't turned into static parts (its names or text
    /// contain surrogate code units, which may pair with neighbouring values): the template is usable, and its body is
    /// decoded directly for every record. Distinct from a null cache entry, which means the template is corrupt.
    /// </summary>
    public static CompiledTemplate DecodeDirectly { get; } = new([[]], []);

    /// <summary>
    /// <see cref="Parts"/> JSON-string-escaped, for a JSON template rendered inside an embedded BinXml value (0x21),
    /// whose whole output is itself a JSON string. Built on first use.
    /// </summary>
    public byte[][] EscapedParts
    {
        get
        {
            // Unsynchronised publish: racing threads build identical arrays
            if (_escapedParts == null)
            {
                byte[][] escaped = new byte[Parts.Length][];
                // Fixed-size seed shared by every part: parts can be several KB, so never stackalloc by input size
                Span<byte> seed = stackalloc byte[512];
                for (int i = 0; i < Parts.Length; i++)
                {
                    ValueUtf8Builder vub = new(seed);
                    BinXmlValueFormatter.AppendJsonEscapedUtf8(ref vub, Parts[i]);
                    escaped[i] = vub.AsSpan().ToArray();
                    vub.Dispose();
                }
                _escapedParts = escaped;
            }
            return _escapedParts;
        }
    }

    #endregion

    #region Non-Public Fields

    /// <summary>
    /// Backing field of <see cref="EscapedParts"/>.
    /// </summary>
    private byte[][]? _escapedParts;

    #endregion
}
