# Compact JSON format (`OutputFormat.Json`)

`OutputFormat.Json` writes each event as one compact JSON object, shaped for reading by people, scripts and AI tools:
names you'd look for are keys, values keep their types, and nothing that carries information is dropped. The shape is
stable: it is derived from the event's XML by the fixed rules below, so the same event always gives the same JSON.

```json
{
  "System": {
    "Provider": {"Name": "Microsoft-Windows-Security-Auditing", "Guid": "54849625-5478-4994-A5BA-3E3B0328C30D"},
    "EventID": 4624,
    "Version": 0,
    "Level": 0,
    "Task": 12544,
    "Opcode": 0,
    "Keywords": "0x8020000000000000",
    "TimeCreated": {"SystemTime": "2016-07-08T18:12:51.6816408Z"},
    "EventRecordID": 2,
    "Correlation": null,
    "Execution": {"ProcessID": 456, "ThreadID": 460},
    "Channel": "Security",
    "Computer": "37L4247F27-25",
    "Security": null
  },
  "EventData": {
    "SubjectUserSid": "S-1-0-0",
    "TargetUserName": "SYSTEM",
    "TargetLogonId": "0x3e7",
    "LogonType": 0,
    "ProcessName": null
  }
}
```

(Pretty-printed and shortened here; the output is one line per event, and `WriteTo` writes JSON Lines.)

## Rules

**The root.** The `<Event>` element is written as its own object, so the top-level keys are its children (`System`,
`EventData`, `UserData`, `RenderingInfo`, …). An event whose root isn't `Event` is written as `{"RootName": …}`.

**Each element** becomes one of:

| The element has                                | It is written as                                                    |
|------------------------------------------------|---------------------------------------------------------------------|
| no attributes, no child elements, no text      | `null`                                                              |
| no attributes, no child elements, text         | the text as a scalar (see Values)                                   |
| attributes and/or child elements               | an object: attributes as plain keys, then child elements as keys, then its text (if any) under `"#text"` |

So `<EventID>4624</EventID>` is `"EventID": 4624`, and `<EventID Qualifiers="16384">7036</EventID>` is
`"EventID": {"Qualifiers": 16384, "#text": 7036}`.

**Child keys.** A child element's key is its name, except under `EventData`, where an item with a `Name` attribute is
keyed by that name (`<Data Name="TargetUserName">SYSTEM</Data>` is `"TargetUserName": "SYSTEM"`, without the `Name`
attribute). Children whose key repeats become one array, in document order, at the position of the first:
unnamed `<Data>` items become `"Data": ["a", "b"]`, and repeated `UserData` elements an array of objects.

**Attributes.** Namespace declarations (`xmlns`, `xmlns:*`) are dropped. An attribute whose template value is an
optional substitution that is empty is omitted, as in the XML output. An empty attribute that is always written in XML
is `null`.

**Values.** A value that is one substitution keeps its BinXml type:

| BinXml type                                             | JSON                                         |
|---------------------------------------------------------|----------------------------------------------|
| Int8 … UInt64, Float, Double                            | number (a non-finite float is a string)      |
| Bool                                                    | `true` / `false`                             |
| String, AnsiString                                      | string                                       |
| HexInt32/64, SizeT                                      | string, as XML writes it (`"0x3e7"`)         |
| GUID, SID, FILETIME, SYSTEMTIME, Binary                 | string, as XML writes it                     |
| an array of any of these                                | JSON array of the items                      |
| embedded BinXml (0x21)                                  | its elements, nested in place as members     |
| empty or Null                                           | `null`                                       |

Text made of several parts (literal template text mixed with values) is one string. The known numeric `System` values
(`EventID`, `Version`, `Level`, `Task`, `Opcode`, `EventRecordID`, `Execution@ProcessID`, `Execution@ThreadID`,
`EventID@Qualifiers`) are numbers even when a template stores them as text. Hexadecimal values (like `Keywords`) stay
strings, because their notation is part of their meaning.

**Records without a template** (rare: forwarded events, CAPI2 `UserData`) follow the same rules; their values have no
BinXml type, so apart from the known numeric `System` values they are strings.

**Corrupt records** fail like in the other formats (no output, a diagnostic), or with `Salvage` keep the part decoded
before the corruption. Elements nested more than 32 deep (only possible in corrupt data) fail the record, which keeps
every output within the 64 levels JSON parsers accept by default.

## Compared with `OutputFormat.JsonStructured`

`JsonStructured` is a lossless rendering of the XML tree (`{"#name":…,"#attrs":{…},"#content":[…]}`): it keeps element
order, mixed content and namespace declarations, and writes every value as a string, with embedded BinXml as an escaped
JSON string. Use it to reconstruct the exact XML; use `Json` for everything else.
