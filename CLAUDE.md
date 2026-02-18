# EVTX Parser Library

High-performance EVTX file parsing library. Optimise for speed and minimal allocations — think competitive programming mindset. Prefer stack allocations, spans, struct over class, and avoid LINQ in hot paths. Every allocation and branch matters.

# Common Commands

- use `git mv` when moving existing files

# Testing

Uses **xUnit v3**

```bash
# All tests
dotnet test --project tests/AxoParse.Evtx.Tests

# Filter by class
dotnet test --project tests/AxoParse.Evtx.Tests --filter-class "AxoParse.Evtx.Tests.EvtxFileHeaderTests"

# Filter by method
dotnet test --project tests/AxoParse.Evtx.Tests --filter-method "*ThrowsOnTruncatedData*"
```

# Code Style

- Avoid `var` unless required (anonymous types) — use explicit types
- Use enums/const/readonly instead of magic literals
- Don't litter with TODO comments
- Prefer `Span<T>`/`ReadOnlySpan<T>` over arrays where possible
- Favour `stackalloc` and value types to minimise GC pressure
- No unnecessary abstractions — if a simple loop is faster than a helper, use the loop

# Comment Guidelines

This is a library — document all public AND private/internal members so contributors can understand the EVTX binary format and parsing logic without needing external references.

## XML Documentation

```csharp
/// <summary>
/// Brief description of the method/class purpose.
/// Additional details if needed (multi-line supported, keep concise).
/// </summary>
/// <param name="paramName">Parameter description.</param>
/// <returns>Return value description.</returns>
```

- `<summary>` opening/closing tags each get their own line
- Blank line between a field/property and the next doc block
- Always include `<param>` and `<returns>` on methods and record structs
- Always add XML docs on test methods explaining what scenario is tested
- Don't explain things people should know — no comments about standard framework usage (e.g., "fields are little-endian and mapped via MemoryMarshal")
- DO document EVTX format specifics (offsets, magic values, field sizes, struct layouts) — this helps contributors understand the binary format

## Inline Comments — Only for Complex Logic

- Comment non-obvious code (e.g., unusual bit manipulations, format-specific offset arithmetic)
- Explain WHY or document expected values from non-trivial calculations
- **Rule of thumb**: If the variable name, method name, or code structure already communicates intent, don't add a comment

```csharp
// GOOD: Documents format-specific offset calculation
// Skip template definition header: 4 next-ptr + 16 GUID + 4 dataSize = 24 bytes
int tplBodyOffset = defDataOffset + 24;

// GOOD: Explains non-obvious bit operation
// Bit 0x80 = array flag; mask to get base value type
byte baseType = (byte)(valueType & 0x7F);

// BAD: States what is obvious from code
// Record data
List<byte> recordData = new List<byte> { 0x2A, 0x2A };

// BAD: Obvious from method name
// Parse the record
EvtxRecord record = EvtxRecord.ParseEvtxRecord(data, offset);
```
