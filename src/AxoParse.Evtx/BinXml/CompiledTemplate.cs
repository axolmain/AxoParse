namespace AxoParse.Evtx.BinXml;

/// <summary>
/// Pre-compiled BinXml template: string parts interleaved with substitution slots.
/// parts[0] + subs[0] + parts[1] + subs[1] + ... + parts[N]
/// </summary>
/// <param name="Parts">
/// Static XML string fragments interleaved with substitution slots.
/// Parts[0] precedes the first substitution, Parts[N] follows the last.
/// </param>
/// <param name="SubIds">
/// Substitution slot indices corresponding to the gaps between <see cref="Parts"/>.
/// </param>
/// <param name="IsOptional">
/// Whether each substitution is optional (0x0E OptionalSubstitution vs 0x0D NormalSubstitution).
/// Optional substitutions are skipped when the value is null or zero-length.
/// </param>
/// <param name="AttrPrefix">
/// Per-substitution attribute prefix (e.g., <c>" Name=\""</c>) that should only be emitted
/// when the optional substitution produces a non-empty value. Null entry means no conditional prefix.
/// </param>
/// <param name="AttrSuffix">
/// Per-substitution attribute suffix (e.g., <c>"\""</c>) that should only be emitted
/// when the optional substitution produces a non-empty value. Null entry means no conditional suffix.
/// </param>
internal sealed record CompiledTemplate(
    string[] Parts,
    int[] SubIds,
    bool[] IsOptional,
    string?[] AttrPrefix,
    string?[] AttrSuffix);