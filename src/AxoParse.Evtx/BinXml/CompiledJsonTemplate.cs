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
internal sealed record CompiledJsonTemplate(string[] Parts, int[] SubIds, bool[] IsOptional, bool[] InAttrValue);