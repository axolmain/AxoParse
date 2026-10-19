using System.Buffers.Binary;
using System.Buffers.Text;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace AxoParse.Evtx.BinXml;

/// <summary>
/// Partial class containing field extraction: a record's typed <c>System</c> fields and its EventData/UserData pairs,
/// read from its template instance's substitution values through the template's <see cref="TemplateFields"/> slots.
/// Nothing is decoded as markup or parsed back from rendered text: each slot names a substitution, whose value is read
/// by type (an EventID is a UInt16, TimeCreated a FILETIME). Pair values are formatted as the XML renderer formats them,
/// unescaped. An embedded BinXml value (EventData in its own template, as Security logs store it) is followed into its
/// template instance with the context of the element holding it.
/// <para>
/// Extraction never changes rendering: it runs as its own pass before the record is rendered, keeps no decoder state,
/// and any exception it hits (corrupt input) only leaves the record's fields incomplete.
/// </para>
/// </summary>
internal sealed partial class BinXmlParser
{
    #region Non-Public Methods

    /// <summary>
    /// Reads a record's fields into <paramref name="fields"/>, as a pass of its own: before rendering it (so a filter
    /// can skip rendering, and rendering runs exactly the code it runs without fields) or instead of rendering it
    /// (fields-only parsing). Templates are resolved and compiled for <paramref name="format"/>, as rendering would, so
    /// the template caches stay per format.
    /// </summary>
    /// <param name="record">The record.</param>
    /// <param name="format">Format whose compiled templates (and template caches) are used.</param>
    /// <param name="fields">Receives the fields.</param>
    internal void ReadRecordFields(EvtxRecord record, OutputFormat format, FieldSink fields)
    {
        ReadOnlySpan<byte> eventData = record.GetEventData(_fileData);
        int binxmlChunkBase = record.EventDataFileOffset - _chunkFileOffset;
        if (format == OutputFormat.Xml)
            CollectFragment<XmlWriter>(eventData, binxmlChunkBase, FieldContext.Document, fields, 0);
        else
            CollectFragment<JsonWriter>(eventData, binxmlChunkBase, FieldContext.Document, fields, 0);
    }

    /// <summary>
    /// Reads the fields of a BinXml stream's first template instance (after any fragment headers): a record's event
    /// data, or an embedded BinXml value. A stream of plain elements, without a template instance (rare: CAPI2 stores
    /// its UserData this way), has no substitution values to read, so it is decoded to XML and its fields are read from
    /// that (see <see cref="CollectFromXml"/>).
    /// </summary>
    /// <typeparam name="TWriter">Format whose compiled templates are used.</typeparam>
    /// <param name="data">BinXml bytes.</param>
    /// <param name="binxmlChunkBase">Chunk-relative offset of <paramref name="data"/>.</param>
    /// <param name="context">Where the stream's root sits in the event.</param>
    /// <param name="fields">Receives the fields.</param>
    /// <param name="depth">Embedded BinXml nesting depth so far.</param>
    private void CollectFragment<TWriter>(ReadOnlySpan<byte> data, int binxmlChunkBase, FieldContext context,
                                          FieldSink fields, int depth)
        where TWriter : struct, IBinXmlWriter
    {
        if (depth > _maxFieldDepth)
            return;

        int pos = 0;
        while ((pos < data.Length) && ((byte)(data[pos] & ~BinXmlToken.HasMoreDataFlag) == BinXmlToken.FragmentHeader))
            pos += 4; // token + major + minor + flags
        if (pos >= data.Length)
            return;
        if ((byte)(data[pos] & ~BinXmlToken.HasMoreDataFlag) != BinXmlToken.TemplateInstance)
        {
            CollectFromXml(data, binxmlChunkBase, context, fields);
            return;
        }

        // Same layout as WriteTemplateInstance reads: token + unknown + template ID, definition offset, the inline
        // definition when this is its first use, then the value count, descriptors and values
        int p = pos + 6;
        uint defDataOffset = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(p, 4));
        p += 4;
        bool inline = defDataOffset == (uint)(binxmlChunkBase + p);
        ref ResolvedTemplate resolved = ref ResolveTemplate<TWriter>(defDataOffset, inline ? data[p..] : default);
        if (inline)
            p += 24 + (int)resolved.DataSize;
        if (resolved.FieldSource == null)
            return;
        FieldSlot[] slots = resolved.FieldSource.Fields.Slots(context, fields.CollectPairs);
        if (slots.Length == 0)
            return;

        int numVals = ReadSubstitutionCount(data, p);
        p += 4;
        const int StackThreshold = 64;
        Span<int> valueOffsets = numVals <= StackThreshold ? stackalloc int[StackThreshold] : new int[numVals];
        ReadOnlySpan<SubstitutionDescriptor> descriptors =
            MemoryMarshal.Cast<byte, SubstitutionDescriptor>(data.Slice(p, numVals * 4));
        p += numVals * 4;
        int fileOffset = _chunkFileOffset + binxmlChunkBase + p;
        for (int i = 0; i < descriptors.Length; i++)
        {
            valueOffsets[i] = fileOffset;
            fileOffset += descriptors[i].Size;
        }

        CollectFields<TWriter>(slots, valueOffsets[..numVals], descriptors, fields, depth);
    }

    /// <summary>
    /// Fallback for a BinXml stream without a template instance: decodes it to XML with the shared decoder (exactly
    /// the text the XML output shows) and reads its fields from that with <see cref="ScanXmlFields"/>. Decoding here
    /// must not change the record's own rendering state, so the salvage diagnostic is restored afterwards.
    /// </summary>
    /// <param name="data">BinXml bytes.</param>
    /// <param name="binxmlChunkBase">Chunk-relative offset of <paramref name="data"/>.</param>
    /// <param name="context">Where the stream's root sits in the event.</param>
    /// <param name="fields">Receives the fields.</param>
    private void CollectFromXml(ReadOnlySpan<byte> data, int binxmlChunkBase, FieldContext context, FieldSink fields)
    {
        if (context == FieldContext.Other)
            return;

        string? salvageDiagnostic = _salvageDiagnostic;
        ValueUtf8Builder xml = new(stackalloc byte[1024]);
        try
        {
            int pos = 0;
            DecodeTopLevel<XmlWriter>(data, ref pos, binxmlChunkBase, ref xml, recordLevel: context == FieldContext.Document,
                escaped: false);
            ScanXmlFields(xml.AsSpan(), context, fields);
        }
        finally
        {
            _salvageDiagnostic = salvageDiagnostic;
            xml.Dispose();
        }
    }

    /// <summary>
    /// Reads fields from XML produced by <see cref="XmlWriter"/>: each attribute and each leaf element's text is matched
    /// against the same rules as template slots (<see cref="TemplateFields.SystemElement"/>,
    /// <see cref="TemplateFields.SystemAttribute"/>, <see cref="TemplateFields.IsPair"/>). The writer's output is
    /// well-formed and uses only the five XML entities and numeric character references, and never self-closes, so a
    /// simple scan suffices.
    /// </summary>
    /// <param name="xml">The XML.</param>
    /// <param name="context">Where the XML's root sits in the event.</param>
    /// <param name="fields">Receives the fields.</param>
    private static void ScanXmlFields(ReadOnlySpan<byte> xml, FieldContext context, FieldSink fields)
    {
        List<string> path = new(TemplateFields.Prefix(context));
        List<ScanFrame> open = new();
        List<byte> value = new();
        int i = 0;
        while (i < xml.Length)
        {
            if (xml[i] != '<')
            {
                int next = xml[i..].IndexOf((byte)'<');
                int end = next < 0 ? xml.Length : i + next;
                if (open.Count > 0)
                    Unescape(xml[i..end], open[^1].Text);
                i = end;
                continue;
            }

            if (xml[i..].StartsWith("<![CDATA["u8))
            {
                int end = xml[i..].IndexOf("]]>"u8);
                if (end < 0)
                    return;
                if (open.Count > 0)
                    open[^1].Text.AddRange(xml.Slice(i + 9, end - 9));
                i += end + 3;
                continue;
            }

            int close = xml[i..].IndexOf((byte)'>');
            if (close < 0)
                return;
            ReadOnlySpan<byte> tag = xml.Slice(i + 1, close - 1);
            i += close + 1;
            if (tag.IsEmpty || (tag[0] == '?') || (tag[0] == '!'))
                continue;

            if (tag[0] == '/')
            {
                if (open.Count == 0)
                    return;
                ScanFrame frame = open[^1];
                open.RemoveAt(open.Count - 1);
                if (!frame.HasChildren)
                    AddLeaf(CollectionsMarshal.AsSpan(path), frame, fields);
                path.RemoveAt(path.Count - 1);
                continue;
            }

            int nameEnd = tag.IndexOfAny((byte)' ', (byte)'/');
            string name = Encoding.UTF8.GetString(nameEnd < 0 ? tag : tag[..nameEnd]);
            if (open.Count > 0)
                open[^1].HasChildren = true;
            ScanFrame opened = new(name);
            open.Add(opened);
            path.Add(name);

            // Attributes: name="value" pairs, values escaped with &quot; so the next quote ends them
            ReadOnlySpan<byte> rest = nameEnd < 0 ? default : tag[nameEnd..];
            while (true)
            {
                int eq = rest.IndexOf("=\""u8);
                if (eq < 0)
                    break;
                string attribute = Encoding.UTF8.GetString(rest[..eq].Trim((byte)' '));
                rest = rest[(eq + 2)..];
                int quote = rest.IndexOf((byte)'"');
                if (quote < 0)
                    break;
                value.Clear();
                Unescape(rest[..quote], value);
                rest = rest[(quote + 1)..];
                if (attribute == "Name")
                    opened.NameAttribute = Encoding.UTF8.GetString(CollectionsMarshal.AsSpan(value));
                if ((value.Count > 0) && (TemplateFields.SystemAttribute(CollectionsMarshal.AsSpan(path), attribute) is { } kind))
                    SetSystemText(kind, CollectionsMarshal.AsSpan(value), ref fields.Current, fields);
            }
        }
    }

    /// <summary>
    /// Stores a leaf element found by <see cref="ScanXmlFields"/> as a System field or a pair.
    /// </summary>
    /// <param name="path">Full element path of the leaf.</param>
    /// <param name="leaf">The leaf.</param>
    /// <param name="fields">Receives the field.</param>
    private static void AddLeaf(ReadOnlySpan<string> path, ScanFrame leaf, FieldSink fields)
    {
        ReadOnlySpan<byte> text = CollectionsMarshal.AsSpan(leaf.Text);
        if (TemplateFields.SystemElement(path) is { } kind)
        {
            if (!text.IsEmpty)
                SetSystemText(kind, text, ref fields.Current, fields);
            return;
        }

        if (!fields.CollectPairs || !TemplateFields.IsPair(path))
            return;
        string name = path[1] == "EventData" ? leaf.NameAttribute ?? leaf.Name : leaf.Name;
        fields.BeginPair(name);
        fields.EndValue(fields.AppendValue(text));
    }

    /// <summary>
    /// Appends XML text with its entity and character references resolved.
    /// </summary>
    /// <param name="escaped">Escaped XML text.</param>
    /// <param name="destination">Receives the UTF-8 text.</param>
    private static void Unescape(ReadOnlySpan<byte> escaped, List<byte> destination)
    {
        while (!escaped.IsEmpty)
        {
            int amp = escaped.IndexOf((byte)'&');
            if (amp < 0)
            {
                destination.AddRange(escaped);
                return;
            }

            destination.AddRange(escaped[..amp]);
            escaped = escaped[amp..];
            int semi = escaped.IndexOf((byte)';');
            if (semi < 0)
            {
                destination.AddRange(escaped);
                return;
            }

            ReadOnlySpan<byte> entity = escaped[1..semi];
            string? text = entity switch
            {
                _ when entity.SequenceEqual("amp"u8) => "&",
                _ when entity.SequenceEqual("lt"u8) => "<",
                _ when entity.SequenceEqual("gt"u8) => ">",
                _ when entity.SequenceEqual("quot"u8) => "\"",
                _ when entity.SequenceEqual("apos"u8) => "'",
                _ when (entity.Length > 1) && (entity[0] == '#') && Utf8Parser.TryParse(entity[1..], out ushort code, out int used) &&
                       (used == entity.Length - 1) => ((char)code).ToString(),
                _ => null,
            };
            if (text == null)
                destination.AddRange(escaped[..(semi + 1)]);
            else
                destination.AddRange(Encoding.UTF8.GetBytes(text));
            escaped = escaped[(semi + 1)..];
        }
    }

    /// <summary>
    /// Reads a template instance's fields through its template's slots.
    /// </summary>
    /// <typeparam name="TWriter">Format whose compiled templates are used for embedded instances.</typeparam>
    /// <param name="slots">The template's slots for its context (see <see cref="TemplateFields.Slots"/>).</param>
    /// <param name="valueOffsets">File offset of each substitution value.</param>
    /// <param name="descriptors">Size and type of each substitution value.</param>
    /// <param name="fields">Receives the fields.</param>
    /// <param name="depth">Embedded BinXml nesting depth so far.</param>
    private void CollectFields<TWriter>(FieldSlot[] slots, scoped ReadOnlySpan<int> valueOffsets,
                                        scoped ReadOnlySpan<SubstitutionDescriptor> descriptors, FieldSink fields, int depth)
        where TWriter : struct, IBinXmlWriter
    {
        for (int i = 0; i < slots.Length; i++)
        {
            ref readonly FieldSlot slot = ref slots[i];
            if (slot.Constant is { } constant)
            {
                ApplyConstant(slot, constant, valueOffsets, descriptors, fields);
                continue;
            }

            FieldPart[] parts = slot.Parts;
            if ((parts.Length == 1) && (parts[0].SubId >= 0))
            {
                int subId = parts[0].SubId;
                if (subId >= valueOffsets.Length)
                    continue;

                byte type = descriptors[subId].Type;
                int size = descriptors[subId].Size;
                if (type == BinXmlValueType.BinXml)
                {
                    if (size > 0)
                        CollectFragment<TWriter>(ChunkBytes(valueOffsets[subId], size),
                            valueOffsets[subId] - _chunkFileOffset, slot.Context, fields, depth + 1);
                }
                else if (slot.Kind == FieldKind.Pair)
                {
                    if (fields.CollectPairs)
                        AddPair(slot, type, size, valueOffsets[subId], valueOffsets, descriptors, fields);
                }
                else if (slot.Kind != FieldKind.NestedOnly)
                {
                    SetSystemValue(slot.Kind, type, size, valueOffsets[subId], fields);
                }

                continue;
            }

            CollectParts<TWriter>(slot, valueOffsets, descriptors, fields, depth);
        }
    }

    /// <summary>
    /// General case of <see cref="CollectFields{TWriter}"/>: a value made of literal text and/or several
    /// substitutions. Embedded BinXml parts are followed; the rest are concatenated as text and stored.
    /// </summary>
    /// <typeparam name="TWriter">Format whose compiled templates are used for embedded instances.</typeparam>
    /// <param name="slot">The slot.</param>
    /// <param name="valueOffsets">File offset of each substitution value.</param>
    /// <param name="descriptors">Size and type of each substitution value.</param>
    /// <param name="fields">Receives the fields.</param>
    /// <param name="depth">Embedded BinXml nesting depth so far.</param>
    private void CollectParts<TWriter>(in FieldSlot slot, scoped ReadOnlySpan<int> valueOffsets,
                                       scoped ReadOnlySpan<SubstitutionDescriptor> descriptors, FieldSink fields, int depth)
        where TWriter : struct, IBinXmlWriter
    {
        bool store = (slot.Kind != FieldKind.NestedOnly) && ((slot.Kind != FieldKind.Pair) || fields.CollectPairs);
        ValueUtf8Builder text = new(stackalloc byte[256]);
        foreach (FieldPart part in slot.Parts)
        {
            if (part.SubId < 0)
            {
                text.AppendUtf16Isolated(part.Literal);
                continue;
            }

            if (part.SubId >= valueOffsets.Length)
                continue;
            byte type = descriptors[part.SubId].Type;
            int size = descriptors[part.SubId].Size;
            int offset = valueOffsets[part.SubId];
            if (type == BinXmlValueType.BinXml)
            {
                if (size > 0)
                    CollectFragment<TWriter>(ChunkBytes(offset, size), offset - _chunkFileOffset, slot.Context, fields, depth + 1);
            }
            else if (store)
            {
                WriteValue<TextWriter>(size, type, offset, offset - _chunkFileOffset, ref text, escaped: false);
            }
        }

        if (store)
        {
            if (slot.Kind == FieldKind.Pair)
            {
                fields.BeginPair(PairName(slot, valueOffsets, descriptors));
                fields.EndValue(fields.AppendValue(text.AsSpan()));
            }
            else
            {
                SetSystemText(slot.Kind, text.AsSpan(), ref fields.Current, fields);
            }
        }

        text.Dispose();
    }

    /// <summary>
    /// Adds a pair whose value is one substitution, formatted straight into the sink.
    /// </summary>
    /// <param name="slot">The pair's slot.</param>
    /// <param name="type">Value type.</param>
    /// <param name="size">Value size.</param>
    /// <param name="offset">Value file offset.</param>
    /// <param name="valueOffsets">File offset of each substitution value (for a substituted name).</param>
    /// <param name="descriptors">Size and type of each substitution value (for a substituted name).</param>
    /// <param name="fields">Receives the pair.</param>
    private void AddPair(in FieldSlot slot, byte type, int size, int offset, scoped ReadOnlySpan<int> valueOffsets,
                         scoped ReadOnlySpan<SubstitutionDescriptor> descriptors, FieldSink fields)
    {
        fields.BeginPair(PairName(slot, valueOffsets, descriptors));
        ValueUtf8Builder vub = new(fields.ValueSpan(_pairValueBytes));
        WriteValue<TextWriter>(size, type, offset, offset - _chunkFileOffset, ref vub, escaped: false);
        int written = vub.IsInInitialBuffer ? vub.Length : fields.AppendValue(vub.AsSpan());
        vub.Dispose();
        fields.EndValue(written);
    }

    /// <summary>
    /// A pair's name: the slot's literal name, or its <c>Name</c> attribute's substitution formatted as text.
    /// </summary>
    /// <param name="slot">The pair's slot.</param>
    /// <param name="valueOffsets">File offset of each substitution value.</param>
    /// <param name="descriptors">Size and type of each substitution value.</param>
    /// <returns>The name.</returns>
    private string PairName(in FieldSlot slot, scoped ReadOnlySpan<int> valueOffsets,
                            scoped ReadOnlySpan<SubstitutionDescriptor> descriptors)
    {
        if ((slot.Name != null) || (slot.NameSubId >= valueOffsets.Length))
            return slot.Name ?? string.Empty;

        ValueUtf8Builder name = new(stackalloc byte[128]);
        int offset = valueOffsets[slot.NameSubId];
        WriteValue<TextWriter>(descriptors[slot.NameSubId].Size, descriptors[slot.NameSubId].Type, offset,
            offset - _chunkFileOffset, ref name, escaped: false);
        string result = Encoding.UTF8.GetString(name.AsSpan());
        name.Dispose();
        return result;
    }

    /// <summary>
    /// Stores a System field read from one substitution value of the expected type (an EventID UInt16, a
    /// TimeCreated FILETIME, a UserID SID, …); a value of another type is formatted as text and parsed.
    /// </summary>
    /// <param name="kind">The field.</param>
    /// <param name="type">Value type.</param>
    /// <param name="size">Value size.</param>
    /// <param name="offset">Value file offset.</param>
    /// <param name="fields">Receives the field.</param>
    private void SetSystemValue(FieldKind kind, byte type, int size, int offset, FieldSink fields)
    {
        if (size == 0)
            return;
        ReadOnlySpan<byte> value = ChunkBytes(offset, size);
        ref SystemFields current = ref fields.Current;
        switch (kind)
        {
            case FieldKind.Provider or FieldKind.Channel or FieldKind.Computer when type == BinXmlValueType.String:
            {
                ReadOnlySpan<char> chars = MemoryMarshal.Cast<byte, char>(value);
                if ((chars.Length > 0) && (chars[^1] == '\0'))
                    chars = chars[..^1];
                if (kind == FieldKind.Provider)
                    current.Provider = FieldSink.Intern(ref fields.LastProvider, chars);
                else if (kind == FieldKind.Channel)
                    current.Channel = FieldSink.Intern(ref fields.LastChannel, chars);
                else
                    current.Computer = FieldSink.Intern(ref fields.LastComputer, chars);
                return;
            }

            case FieldKind.UserId when (type == BinXmlValueType.Sid) && value.SequenceEqual(fields.LastUserIdBytes):
                // The same account as the previous record (the usual case): skip formatting the SID again
                current.UserId = fields.LastUserId;
                return;

            case FieldKind.TimeCreated when (type == BinXmlValueType.FileTime) && (size >= 8):
            {
                ulong fileTime = BinaryPrimitives.ReadUInt64LittleEndian(value);
                if (fileTime <= _maxFileTime)
                {
                    current.TimeCreatedTicks = DateTime.FromFileTimeUtc((long)fileTime).Ticks;
                    current.Present |= SystemPresence.TimeCreated;
                }
                return;
            }

            case FieldKind.ProviderGuid or FieldKind.ActivityId when (type == BinXmlValueType.Guid) && (size >= 16):
                SetGuid(kind, new Guid(value[..16]), ref current);
                return;

            case FieldKind.EventId or FieldKind.Version or FieldKind.Level or FieldKind.Task or FieldKind.Opcode
                or FieldKind.Keywords or FieldKind.ProcessId or FieldKind.ThreadId
                when TryReadInteger(type, value, out ulong number):
                SetInteger(kind, number, ref current);
                return;
        }

        // Any other type (a SID, a string where a number is expected, a SYSTEMTIME, …): use the text the XML renderer writes
        ValueUtf8Builder text = new(stackalloc byte[128]);
        WriteValue<TextWriter>(size, type, offset, offset - _chunkFileOffset, ref text, escaped: false);
        SetSystemText(kind, text.AsSpan(), ref fields.Current, fields);
        text.Dispose();
        if ((kind == FieldKind.UserId) && (type == BinXmlValueType.Sid))
            fields.LastUserIdBytes = value.ToArray();
    }

    /// <summary>
    /// Stores a System field from its text (literal template text, or a value of an unexpected type formatted as text).
    /// </summary>
    /// <param name="kind">The field.</param>
    /// <param name="utf8">The field's text as UTF-8.</param>
    /// <param name="current">Fields to set.</param>
    /// <param name="memo">Sink whose last strings are reused when they repeat, or null to allocate.</param>
    internal static void SetSystemText(FieldKind kind, ReadOnlySpan<byte> utf8, ref SystemFields current, FieldSink? memo)
    {
        switch (kind)
        {
            case FieldKind.Provider:
                current.Provider = memo == null ? Encoding.UTF8.GetString(utf8) : Intern(ref memo.LastProvider, utf8);
                return;
            case FieldKind.Channel:
                current.Channel = memo == null ? Encoding.UTF8.GetString(utf8) : Intern(ref memo.LastChannel, utf8);
                return;
            case FieldKind.Computer:
                current.Computer = memo == null ? Encoding.UTF8.GetString(utf8) : Intern(ref memo.LastComputer, utf8);
                return;
            case FieldKind.UserId:
                if (utf8.IsEmpty)
                    return;
                if (memo == null)
                {
                    current.UserId = Encoding.UTF8.GetString(utf8);
                    return;
                }

                // The caller records the SID bytes when this text came from a SID value
                memo.LastUserIdBytes = [];
                current.UserId = Intern(ref memo.LastUserId, utf8);
                return;
            case FieldKind.ProviderGuid or FieldKind.ActivityId:
                if ((Utf8Parser.TryParse(utf8, out Guid guid, out int consumed, 'B') && (consumed == utf8.Length)) ||
                    (Utf8Parser.TryParse(utf8, out guid, out consumed) && (consumed == utf8.Length)))
                    SetGuid(kind, guid, ref current);
                return;
            case FieldKind.TimeCreated:
                if (DateTime.TryParse(Encoding.UTF8.GetString(utf8), CultureInfo.InvariantCulture,
                        DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out DateTime time))
                {
                    current.TimeCreatedTicks = time.Ticks;
                    current.Present |= SystemPresence.TimeCreated;
                }
                return;
            default:
                if (TryParseInteger(utf8, out ulong number))
                    SetInteger(kind, number, ref current);
                return;
        }

        static string Intern(ref string? last, ReadOnlySpan<byte> utf8)
        {
            Span<char> chars = utf8.Length <= 256 ? stackalloc char[256] : new char[utf8.Length];
            int length = Encoding.UTF8.GetChars(utf8, chars);
            return FieldSink.Intern(ref last, chars[..length]);
        }
    }

    /// <summary>
    /// Copies a constant field (literal template text, parsed once) into the record's fields.
    /// </summary>
    /// <param name="slot">The slot.</param>
    /// <param name="constant">Its constant.</param>
    /// <param name="valueOffsets">File offset of each substitution value (for a substituted pair name).</param>
    /// <param name="descriptors">Size and type of each substitution value (for a substituted pair name).</param>
    /// <param name="fields">Receives the field.</param>
    private void ApplyConstant(in FieldSlot slot, FieldConstant constant, scoped ReadOnlySpan<int> valueOffsets,
                               scoped ReadOnlySpan<SubstitutionDescriptor> descriptors, FieldSink fields)
    {
        ref SystemFields current = ref fields.Current;
        ref readonly SystemFields values = ref constant.Values;
        switch (slot.Kind)
        {
            case FieldKind.Pair:
                if (fields.CollectPairs)
                {
                    fields.BeginPair(PairName(slot, valueOffsets, descriptors));
                    fields.EndValue(fields.AppendValue(constant.Utf8));
                }
                return;
            case FieldKind.Provider: current.Provider = values.Provider; return;
            case FieldKind.Channel: current.Channel = values.Channel; return;
            case FieldKind.Computer: current.Computer = values.Computer; return;
            case FieldKind.UserId: current.UserId = values.UserId ?? current.UserId; return;
            case FieldKind.ProviderGuid or FieldKind.ActivityId or FieldKind.TimeCreated:
                if ((values.Present & SystemPresence.ProviderGuid) != 0)
                    SetGuid(FieldKind.ProviderGuid, values.ProviderGuid, ref current);
                if ((values.Present & SystemPresence.ActivityId) != 0)
                    SetGuid(FieldKind.ActivityId, values.ActivityId, ref current);
                if ((values.Present & SystemPresence.TimeCreated) != 0)
                {
                    current.TimeCreatedTicks = values.TimeCreatedTicks;
                    current.Present |= SystemPresence.TimeCreated;
                }
                return;
            case FieldKind.EventId: current.EventId = values.EventId; return;
            case FieldKind.Version: current.Version = values.Version; return;
            case FieldKind.Level: current.Level = values.Level; return;
            case FieldKind.Task: current.Task = values.Task; return;
            case FieldKind.Opcode: current.Opcode = values.Opcode; return;
            case FieldKind.Keywords: current.Keywords = values.Keywords; return;
            case FieldKind.ProcessId: current.ProcessId = values.ProcessId; return;
            case FieldKind.ThreadId: current.ThreadId = values.ThreadId; return;
        }
    }

    /// <summary>
    /// Stores a GUID field.
    /// </summary>
    /// <param name="kind"><see cref="FieldKind.ProviderGuid"/> or <see cref="FieldKind.ActivityId"/>.</param>
    /// <param name="guid">The value.</param>
    /// <param name="current">Fields to set.</param>
    private static void SetGuid(FieldKind kind, Guid guid, ref SystemFields current)
    {
        if (kind == FieldKind.ProviderGuid)
        {
            current.ProviderGuid = guid;
            current.Present |= SystemPresence.ProviderGuid;
        }
        else
        {
            current.ActivityId = guid;
            current.Present |= SystemPresence.ActivityId;
        }
    }

    /// <summary>
    /// Stores an integer field, truncated to the field's width as Windows does.
    /// </summary>
    /// <param name="kind">The field.</param>
    /// <param name="number">The value.</param>
    /// <param name="current">Fields to set.</param>
    private static void SetInteger(FieldKind kind, ulong number, ref SystemFields current)
    {
        switch (kind)
        {
            case FieldKind.EventId: current.EventId = (ushort)number; break;
            case FieldKind.Version: current.Version = (byte)number; break;
            case FieldKind.Level: current.Level = (byte)number; break;
            case FieldKind.Task: current.Task = (ushort)number; break;
            case FieldKind.Opcode: current.Opcode = (byte)number; break;
            case FieldKind.Keywords: current.Keywords = number; break;
            case FieldKind.ProcessId: current.ProcessId = (uint)number; break;
            case FieldKind.ThreadId: current.ThreadId = (uint)number; break;
        }
    }

    /// <summary>
    /// Reads an integer value of any integer BinXml type (signed types are sign-extended, then reinterpreted).
    /// </summary>
    /// <param name="type">Value type.</param>
    /// <param name="value">Value bytes.</param>
    /// <param name="number">The value.</param>
    /// <returns>False if the type isn't an integer type or the value is too short.</returns>
    private static bool TryReadInteger(byte type, ReadOnlySpan<byte> value, out ulong number)
    {
        number = 0;
        switch (type)
        {
            case BinXmlValueType.UInt8 when value.Length >= 1: number = value[0]; return true;
            case BinXmlValueType.Int8 when value.Length >= 1: number = (ulong)(sbyte)value[0]; return true;
            case BinXmlValueType.UInt16 when value.Length >= 2: number = BinaryPrimitives.ReadUInt16LittleEndian(value); return true;
            case BinXmlValueType.Int16 when value.Length >= 2: number = (ulong)BinaryPrimitives.ReadInt16LittleEndian(value); return true;
            case BinXmlValueType.UInt32 or BinXmlValueType.HexInt32 when value.Length >= 4:
                number = BinaryPrimitives.ReadUInt32LittleEndian(value);
                return true;
            case BinXmlValueType.Int32 when value.Length >= 4: number = (ulong)BinaryPrimitives.ReadInt32LittleEndian(value); return true;
            case BinXmlValueType.UInt64 or BinXmlValueType.HexInt64 or BinXmlValueType.Int64 when value.Length >= 8:
                number = BinaryPrimitives.ReadUInt64LittleEndian(value);
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// Parses a decimal or <c>0x</c>-prefixed hexadecimal integer.
    /// </summary>
    /// <param name="utf8">The text.</param>
    /// <param name="number">The value.</param>
    /// <returns>False if the text isn't an integer.</returns>
    private static bool TryParseInteger(ReadOnlySpan<byte> utf8, out ulong number)
    {
        if ((utf8.Length > 2) && (utf8[0] == '0') && ((utf8[1] | 0x20) == 'x'))
            return Utf8Parser.TryParse(utf8[2..], out number, out int hexConsumed, 'x') && (hexConsumed == utf8.Length - 2);
        return Utf8Parser.TryParse(utf8, out number, out int consumed) && (consumed == utf8.Length);
    }

    #endregion

    #region Non-Public Fields

    /// <summary>
    /// Deepest embedded BinXml nesting followed for fields (EventData is one level; this bounds crafted input).
    /// </summary>
    private const int _maxFieldDepth = 8;

    /// <summary>
    /// Largest FILETIME <see cref="DateTime.FromFileTimeUtc"/> accepts (9999-12-31T23:59:59.9999999Z).
    /// </summary>
    private const ulong _maxFileTime = 2_650_467_743_999_999_999UL;

    /// <summary>
    /// Free space requested from the sink before formatting a pair value into it (longer values spill and are copied).
    /// </summary>
    private const int _pairValueBytes = 512;

    #endregion

    #region Nested Types

    /// <summary>
    /// An open element during <see cref="ScanXmlFields"/>.
    /// </summary>
    /// <param name="name">Element name.</param>
    private sealed class ScanFrame(string name)
    {
        /// <summary>
        /// Element name.
        /// </summary>
        public readonly string Name = name;

        /// <summary>
        /// The element's text so far (unescaped UTF-8).
        /// </summary>
        public readonly List<byte> Text = new();

        /// <summary>
        /// True once a child element starts.
        /// </summary>
        public bool HasChildren;

        /// <summary>
        /// The element's <c>Name</c> attribute, or null.
        /// </summary>
        public string? NameAttribute;
    }

    /// <summary>
    /// Unescaped text, as the XML renderer formats values before escaping them: used for pair values and for System
    /// fields of unexpected types. Only the value members are used; element members follow <see cref="XmlWriter"/>.
    /// </summary>
    private struct TextWriter : IBinXmlWriter
    {
        /// <inheritdoc/>
        public static bool OmitsEmptyOptionalAttributes => true;

        /// <inheritdoc/>
        public static bool ConsumesProcessingInstructions => false;

        /// <inheritdoc/>
        public static OutputFormat Format => OutputFormat.Xml;

        /// <inheritdoc/>
        public static bool PadsHexIntegers => false;

        /// <inheritdoc/>
        public static void AppendString(ref ValueUtf8Builder vub, scoped ReadOnlySpan<char> text, bool escaped) =>
            vub.AppendUtf16Isolated(text);

        /// <inheritdoc/>
        public static void AppendAnsiString(ref ValueUtf8Builder vub, ReadOnlySpan<byte> bytes, bool escaped)
        {
            foreach (byte b in bytes)
            {
                if (b == 0) break;
                if (b < 0x80) vub.Append(b);
                else BinXmlValueFormatter.AppendLatin1(ref vub, (char)b);
            }
        }

        /// <summary>
        /// Embedded BinXml is followed for its fields instead of formatted; an array of it is written as hex.
        /// </summary>
        /// <param name="parser">Parser for the chunk.</param>
        /// <param name="bytes">The fragment's bytes.</param>
        /// <param name="binxmlChunkBase">Chunk-relative offset of <paramref name="bytes"/>.</param>
        /// <param name="vub">Output builder.</param>
        /// <param name="escaped">Unused.</param>
        public static void WriteEmbeddedBinXml(BinXmlParser parser, ReadOnlySpan<byte> bytes, int binxmlChunkBase,
                                               ref ValueUtf8Builder vub, bool escaped) =>
            BinXmlValueFormatter.AppendHex(ref vub, bytes);

        /// <inheritdoc/>
        public static void WriteProcessingInstruction(BinXmlParser parser, uint nameOffset, scoped ReadOnlySpan<char> text,
                                                      ref ValueUtf8Builder vub) { }

        /// <inheritdoc/>
        public void BeginContentItem(ref ValueUtf8Builder vub) { }

        /// <inheritdoc/>
        public void TemplateInstanceItem(BinXmlParser parser, ReadOnlySpan<byte> data, ref int pos, int binxmlChunkBase,
                                         ref ValueUtf8Builder vub) =>
            parser.WriteTemplateInstance<XmlWriter>(data, ref pos, binxmlChunkBase, ref vub, escaped: false);

        /// <inheritdoc/>
        public void StartElement(ref ValueUtf8Builder vub, string name) => default(XmlWriter).StartElement(ref vub, name);

        /// <inheritdoc/>
        public void BeginAttributes(ref ValueUtf8Builder vub) { }

        /// <inheritdoc/>
        public void Attribute(ref ValueUtf8Builder vub, string name) => default(XmlWriter).Attribute(ref vub, name);

        /// <inheritdoc/>
        public void EndAttribute(ref ValueUtf8Builder vub) => default(XmlWriter).EndAttribute(ref vub);

        /// <inheritdoc/>
        public void EndAttributes(ref ValueUtf8Builder vub) { }

        /// <inheritdoc/>
        public void CloseStartElement(ref ValueUtf8Builder vub) => default(XmlWriter).CloseStartElement(ref vub);

        /// <inheritdoc/>
        public void EndElement(ref ValueUtf8Builder vub, string name) => default(XmlWriter).EndElement(ref vub, name);

        /// <inheritdoc/>
        public void CloseEmptyElement(ref ValueUtf8Builder vub, string name) => default(XmlWriter).CloseEmptyElement(ref vub, name);

        /// <inheritdoc/>
        public void Text(ref ValueUtf8Builder vub, scoped ReadOnlySpan<char> text) => vub.AppendUtf16Isolated(text);

        /// <inheritdoc/>
        public void CharRef(ref ValueUtf8Builder vub, ushort value) => vub.AppendUtf16Isolated([(char)value]);

        /// <inheritdoc/>
        public void EntityRef(ref ValueUtf8Builder vub, string name) => default(XmlWriter).EntityRef(ref vub, name);

        /// <inheritdoc/>
        public void CData(ref ValueUtf8Builder vub, scoped ReadOnlySpan<char> text) => vub.AppendUtf16Isolated(text);

        /// <inheritdoc/>
        public void BeginValue(ref ValueUtf8Builder vub) { }

        /// <inheritdoc/>
        public void EndValue(ref ValueUtf8Builder vub) { }
    }

    #endregion
}
