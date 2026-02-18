# Contributing

## Build

```bash
dotnet build
```

Requires .NET 10 SDK.

## Run tests

```bash
# All tests
dotnet test --project tests/AxoParse.Evtx.Tests

# Filter by class
dotnet test --project tests/AxoParse.Evtx.Tests --filter-class "AxoParse.Evtx.Tests.EvtxFileHeaderTests"

# Filter by method
dotnet test --project tests/AxoParse.Evtx.Tests --filter-method "*ThrowsOnTruncatedData*"
```

## Code style

- **Explicit types** — avoid `var` unless required (anonymous types)
- **Value types** — prefer `struct`, `Span<T>`, `ReadOnlySpan<T>`, `stackalloc` over heap allocations
- **No LINQ in hot paths** — simple loops are faster and allocate less
- **Enums/const/readonly** over magic literals
- **No unnecessary abstractions** — if a loop is faster than a helper, use the loop

## Comments

Document all public and internal members with XML doc comments. This is a binary format parser — contributors need to understand EVTX specifics without external references.

```csharp
/// <summary>
/// Brief description.
/// </summary>
/// <param name="paramName">Parameter description.</param>
/// <returns>Return value description.</returns>
```

Inline comments only for non-obvious logic (bit manipulations, format-specific offsets). If the code is self-explanatory, don't comment it.

## Project structure

```
src/
  AxoParse.Evtx/           # Core parser library
    Evtx/                   # File/chunk/record parsing
    BinXml/                 # BinXml token stream parser + value formatting
    Wevt/                   # WEVT template cache (PE binary extraction)
  AxoParse.Browser/         # WASM interop layer
demos/
  AxoParse.React/           # React demo app (Vite + TypeScript)
tests/
  AxoParse.Evtx.Tests/      # xUnit v3
```

## Pull requests

Log an issue then create a PR for any change :)