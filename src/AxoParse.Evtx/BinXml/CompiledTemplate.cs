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
    }
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
    }
}
