# EVTX Binary Format

A primer on the Windows Event Log (.evtx) binary format. For the full spec, see [libevtx](https://github.com/libyal/libevtx/blob/main/documentation/Windows%20XML%20Event%20Log%20(EVTX).asciidoc).

## File structure

```
Offset 0x0000  +-----------------------+
               | File Header (4096 B)  |  "ElfFile\0" magic
               +-----------------------+
Offset 0x1000  | Chunk 0 (65536 B)     |  "ElfChnk\0" magic
               +-----------------------+
Offset 0x11000 | Chunk 1 (65536 B)     |
               +-----------------------+
               | ...                   |
               +-----------------------+
```

An EVTX file is a 4KB file header followed by sequential 64KB chunks. Each chunk is self-contained with its own header, string/template caches, and event records.

## File header (128 bytes used, 4096 bytes reserved)

| Offset | Size | Field                                    |
|--------|------|------------------------------------------|
| 0      | 8    | Magic: `ElfFile\0`                       |
| 8      | 8    | First chunk number                       |
| 16     | 8    | Last chunk number                        |
| 24     | 8    | Next record identifier                   |
| 32     | 4    | Header size (always 128)                 |
| 36     | 2    | Minor format version                     |
| 38     | 2    | Major format version                     |
| 40     | 2    | Header block size (always 4096)          |
| 42     | 2    | Number of chunks                         |
| 120    | 4    | Flags (Dirty=0x1, Full=0x2, NoCrc32=0x4) |
| 124    | 4    | CRC32 checksum of bytes 0-119            |

## Chunk header (512 bytes)

| Offset | Size | Field                                             |
|--------|------|---------------------------------------------------|
| 0      | 8    | Magic: `ElfChnk\0`                                |
| 8      | 8    | First event record number                         |
| 16     | 8    | Last event record number                          |
| 24     | 8    | First event record ID                             |
| 32     | 8    | Last event record ID                              |
| 40     | 4    | Header size (always 128)                          |
| 44     | 4    | Last event record data offset                     |
| 48     | 4    | Free space offset                                 |
| 52     | 4    | Event records CRC32                               |
| 120    | 4    | Flags                                             |
| 124    | 4    | Header CRC32: `CRC32(0..120) XOR CRC32(128..512)` |
| 128    | 256  | Common string offset table (64 x uint32)          |
| 384    | 128  | Template pointer table (32 x uint32)              |

Records start at offset 512 within the chunk and run until the free space offset.

## Event record

| Offset | Size | Field                |
|--------|------|----------------------|
| 0      | 4    | Magic: `0x00002A2A`  |
| 4      | 4    | Record size          |
| 8      | 8    | Record number        |
| 16     | 8    | Timestamp (FILETIME) |
| 24     | ...  | BinXml event data    |
| last 4 | 4    | Copy of record size  |

## BinXml tokens

Event data is encoded as a BinXml token stream. Key tokens:

| Token | Name              | Description                                            |
|-------|-------------------|--------------------------------------------------------|
| 0x00  | EOF               | End of fragment                                        |
| 0x01  | OpenStartElement  | Element open (bit 0x40 = has attributes)               |
| 0x02  | CloseStartElement | End of start tag, children follow                      |
| 0x03  | CloseEmptyElement | Self-closing element                                   |
| 0x04  | EndElement        | `</tag>`                                               |
| 0x05  | Value             | Inline text value                                      |
| 0x06  | Attribute         | Attribute name + value                                 |
| 0x0C  | TemplateInstance  | Reference to a compiled template + substitution values |
| 0x0F  | FragmentHeader    | Start of a BinXml document fragment                    |

Templates (0x0C) are the most important optimisation target — a single template definition is referenced by many records. The parser compiles each template once and caches it by GUID.

## References

- [libevtx format spec](https://github.com/libyal/libevtx/blob/main/documentation/Windows%20XML%20Event%20Log%20(EVTX).asciidoc) — the most complete public documentation of the format
- [Microsoft event logging](https://learn.microsoft.com/en-us/windows/win32/wes/windows-event-log) — official Windows Event Log docs
- [omerbenamram/evtx](https://github.com/omerbenamram/evtx) — Rust parser with good format notes
