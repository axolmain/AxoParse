namespace AxoParse.Evtx.BinXml;

/// <summary>
/// Pre-compiled BinXml template for JSON output: string parts interleaved with substitution slots.
/// parts[0] + subs[0] + parts[1] + subs[1] + ... + parts[N]
/// Each substitution slot records whether the value appears inside a JSON string literal
/// (attribute value context) or as a standalone array item (content context).
/// </summary>
/// <param name="Parts">
/// Static JSON string fragments interleaved with substitution slots.
/// Parts[0] precedes the first substitution, Parts[N] follows the last.
/// </param>
/// <param name="SubIds">
/// Substitution slot indices corresponding to the gaps between <see cref="Parts"/>.
/// </param>
/// <param name="IsOptional">
/// Whether each substitution is optional (0x0E OptionalSubstitution vs 0x0D NormalSubstitution).
/// Optional substitutions emit empty string when the value is null or zero-length.
/// </param>
/// <param name="InAttrValue">
/// Whether each substitution appears inside a JSON string literal (attribute value context).
/// When true, the value is part of an already-quoted string — no extra quotes needed.
/// When false, the compiled Parts already include the surrounding quotes.
/// </param>
internal sealed record CompiledJsonTemplate(string[] Parts, int[] SubIds, bool[] IsOptional, bool[] InAttrValue)
{
    #region Properties

    /// <summary>
    /// <see cref="Parts"/> pre-encoded as UTF-8; an entry is null when its part contains a surrogate and must be
    /// appended as raw UTF-16 (see <see cref="CompiledTemplate.EncodePart"/>).
    /// </summary>
    public byte[]?[] Utf8Parts { get; } = EncodeParts(Parts);

    /// <summary>
    /// <see cref="Parts"/> with one further level of JSON string escaping applied, as UTF-8. Used when the template is
    /// instantiated inside an embedded BinXml value (type 0x21) whose rendered JSON is emitted as a JSON string, so the
    /// writer can emit the escaped form directly instead of rendering to a temporary buffer and escaping it.
    /// Built on first use, since only templates instantiated inside embedded BinXml need it.
    /// Null if any part contains a surrogate: a pair could then straddle a part/value boundary, and escaping
    /// part by part would differ from escaping the concatenated output.
    /// </summary>
    public byte[][]? EscapedUtf8Parts
    {
        get
        {
            // Unsynchronised publish: racing threads build identical arrays, and a reader that sees a stale null
            // just takes the exact render-then-escape fallback, so output never depends on the race
            if (!_escapedPartsBuilt)
            {
                _escapedUtf8Parts = BuildEscapedParts(Parts);
                _escapedPartsBuilt = true;
            }
            return _escapedUtf8Parts;
        }
    }

    #endregion

    #region Non-Public Methods

    /// <summary>
    /// JSON-escapes each part once more as UTF-8 with <see cref="BinXmlValueFormatter"/>'s escaper, so each part
    /// matches exactly what escaping the rendered output would produce for those characters.
    /// </summary>
    /// <param name="parts">Compiled static JSON parts.</param>
    /// <returns>Escaped UTF-8 copies of <paramref name="parts"/>, or null if any part contains a surrogate.</returns>
    private static byte[][]? BuildEscapedParts(string[] parts)
    {
        byte[][] escaped = new byte[parts.Length][];
        for (int i = 0; i < parts.Length; i++)
        {
            string part = parts[i];
            if (part.AsSpan().ContainsAnyInRange('\uD800', '\uDFFF'))
                return null;
            // Fixed-size seed: parts can be several KB, so never stackalloc by input size
            ValueUtf8Builder vub = new(stackalloc byte[512]);
            BinXmlValueFormatter.AppendJsonEscaped(ref vub, part.AsSpan());
            escaped[i] = vub.AsSpan().ToArray();
            vub.Dispose();
        }
        return escaped;
    }

    /// <summary>
    /// Pre-encodes every part with <see cref="CompiledTemplate.EncodePart"/>.
    /// </summary>
    /// <param name="parts">Compiled static JSON parts.</param>
    /// <returns>UTF-8 parts, with null entries for parts that must stay UTF-16.</returns>
    private static byte[]?[] EncodeParts(string[] parts)
    {
        byte[]?[] utf8 = new byte[]?[parts.Length];
        for (int i = 0; i < parts.Length; i++)
            utf8[i] = CompiledTemplate.EncodePart(parts[i]);
        return utf8;
    }

    #endregion

    #region Non-Public Fields

    /// <summary>
    /// Cached result of <see cref="BuildEscapedParts"/>; valid only once <see cref="_escapedPartsBuilt"/> is true.
    /// </summary>
    private byte[][]? _escapedUtf8Parts;

    /// <summary>
    /// Whether <see cref="_escapedUtf8Parts"/> has been computed (needed because null is a valid result).
    /// </summary>
    private bool _escapedPartsBuilt;

    #endregion
}
