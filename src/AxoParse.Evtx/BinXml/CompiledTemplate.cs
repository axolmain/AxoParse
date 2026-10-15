namespace AxoParse.Evtx.BinXml;

/// <summary>
/// Per-substitution slot metadata packed into a single struct for cache-friendly access.
/// One array access per slot instead of four parallel array accesses.
/// </summary>
internal readonly struct SubSlot
{
    /// <summary>
    /// Substitution slot index into the value arrays.
    /// </summary>
    public readonly int SubId;

    /// <summary>
    /// Whether this substitution is optional (0x0E OptionalSubstitution).
    /// Optional substitutions are skipped when the value is null or zero-length.
    /// </summary>
    public readonly bool IsOptional;

    /// <summary>
    /// Attribute prefix (e.g., <c>" Name=\""</c>) emitted only when the optional substitution
    /// produces a non-empty value. Null means no conditional prefix.
    /// </summary>
    public readonly string? AttrPrefix;

    /// <summary>
    /// Attribute suffix (e.g., <c>"\""</c>) emitted only when the optional substitution
    /// produces a non-empty value. Null means no conditional suffix.
    /// </summary>
    public readonly string? AttrSuffix;

    /// <summary>
    /// Creates a substitution slot descriptor.
    /// </summary>
    /// <param name="subId">Substitution index.</param>
    /// <param name="isOptional">Whether the substitution is optional.</param>
    /// <param name="attrPrefix">Conditional attribute prefix, or null.</param>
    /// <param name="attrSuffix">Conditional attribute suffix, or null.</param>
    public SubSlot(int subId, bool isOptional, string? attrPrefix = null, string? attrSuffix = null)
    {
        SubId = subId;
        IsOptional = isOptional;
        AttrPrefix = attrPrefix;
        AttrSuffix = attrSuffix;
        Utf8AttrPrefix = CompiledTemplate.EncodePart(attrPrefix);
        Utf8AttrSuffix = CompiledTemplate.EncodePart(attrSuffix);
    }

    /// <summary>
    /// <see cref="AttrPrefix"/> pre-encoded as UTF-8, or null when absent or not safely pre-encodable.
    /// </summary>
    public readonly byte[]? Utf8AttrPrefix;

    /// <summary>
    /// <see cref="AttrSuffix"/> pre-encoded as UTF-8, or null when absent or not safely pre-encodable.
    /// </summary>
    public readonly byte[]? Utf8AttrSuffix;
}

/// <summary>
/// Pre-compiled BinXml template: string parts interleaved with substitution slots.
/// parts[0] + slots[0] + parts[1] + slots[1] + ... + parts[N]
/// Fields are public readonly for zero-overhead access in hot loops.
/// </summary>
internal sealed class CompiledTemplate
{
    /// <summary>
    /// Static XML string fragments interleaved with substitution slots.
    /// Parts[0] precedes the first substitution, Parts[N] follows the last.
    /// </summary>
    public readonly string[] Parts;

    /// <summary>
    /// Packed per-substitution metadata (subId, isOptional, attrPrefix, attrSuffix).
    /// </summary>
    public readonly SubSlot[] Slots;

    /// <summary>
    /// Creates a compiled template from parts and packed substitution slots.
    /// </summary>
    /// <param name="parts">Static XML string fragments.</param>
    /// <param name="slots">Packed substitution slot descriptors.</param>
    public CompiledTemplate(string[] parts, SubSlot[] slots)
    {
        Parts = parts;
        Slots = slots;
        Utf8Parts = new byte[]?[parts.Length];
        for (int i = 0; i < parts.Length; i++)
            Utf8Parts[i] = EncodePart(parts[i]);
    }

    /// <summary>
    /// <see cref="Parts"/> pre-encoded as UTF-8; an entry is null when its part contains a surrogate (see <see cref="EncodePart"/>).
    /// </summary>
    public readonly byte[]?[] Utf8Parts;

    /// <summary>
    /// Appends a static part: its pre-encoded UTF-8 bytes when available, otherwise its text as raw UTF-16.
    /// </summary>
    /// <param name="vub">UTF-8 builder that receives the part.</param>
    /// <param name="utf8">Pre-encoded bytes, or null.</param>
    /// <param name="text">The part's text (used when <paramref name="utf8"/> is null; may be null when absent).</param>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal static void AppendPart(ref ValueUtf8Builder vub, byte[]? utf8, string? text)
    {
        if (utf8 != null)
            vub.Append(utf8);
        else if (text != null)
            vub.AppendUtf16(text);
    }

    /// <summary>
    /// Pre-encodes a static part as UTF-8. Returns null for null text, and for text containing a surrogate: such a
    /// part must be appended as raw UTF-16 so a surrogate at its edge can still pair with neighbouring text, exactly
    /// as when the part was appended to a UTF-16 buffer.
    /// </summary>
    /// <param name="text">Part text, or null.</param>
    /// <returns>UTF-8 bytes, or null.</returns>
    internal static byte[]? EncodePart(string? text)
    {
        if ((text == null) || text.AsSpan().ContainsAnyInRange('\uD800', '\uDFFF'))
            return null;
        return System.Text.Encoding.UTF8.GetBytes(text);
    }
}
