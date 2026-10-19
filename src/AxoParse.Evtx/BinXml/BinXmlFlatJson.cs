using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace AxoParse.Evtx.BinXml;

/// <summary>
/// Partial class containing the compact <see cref="OutputFormat.Json"/> renderer. A record is built into a
/// <see cref="JsonTree"/> from its template's <see cref="TemplateProgram"/> and substitution values, which keeps each
/// value's type (a UInt16 EventID becomes a JSON number, a string array a JSON array), then written in the tree's flat
/// shape. Embedded BinXml values (Security logs keep EventData in its own template) are built in place, as nested
/// objects rather than escaped strings.
/// <para>
/// A stream without a template program to walk (a record or value of plain elements, or a template that doesn't
/// compile) is decoded to XML by the shared decoder and scanned into the tree, so strict and salvage decoding behave
/// exactly as in the other formats; its values are then text, apart from the known numeric System fields. In salvage
/// mode a record whose values turn out corrupt is rebuilt that way too, keeping its well-formed prefix.
/// </para>
/// </summary>
internal sealed partial class BinXmlParser
{
    #region Non-Public Methods

    /// <summary>
    /// Renders a record as compact JSON.
    /// </summary>
    /// <param name="record">The record.</param>
    /// <param name="vub">Output builder.</param>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void RenderFlatJson(EvtxRecord record, ref ValueUtf8Builder vub)
    {
        ReadOnlySpan<byte> eventData = record.GetEventData(_fileData);
        int binxmlChunkBase = record.EventDataFileOffset - _chunkFileOffset;
        if (!ForceJsonTree)
        {
            int start = vub.Length;
            try
            {
                if (TryWritePlanned(eventData, binxmlChunkBase, ref vub))
                    return;
            }
            catch (Exception ex) when (_salvage && ex is not OutOfMemoryException)
            {
                // Corrupt values: the tree path below salvages the record through the decoder
            }

            vub.Truncate(start);
        }

        JsonTree tree = _threadJsonTree ??= new JsonTree();
        tree.Reset();
        try
        {
            BuildFragment(eventData, binxmlChunkBase, tree, -1, 0, recordLevel: true);
        }
        catch (Exception ex) when (_salvage && ex is not OutOfMemoryException)
        {
            tree.Reset();
            BuildFromXml(eventData, binxmlChunkBase, tree, -1, recordLevel: true);
        }

        tree.Write(ref vub);
    }

    /// <summary>
    /// Writes a record from its template's <see cref="JsonPlan"/>, or returns false (having written a partial record the
    /// caller discards) when the record needs the tree: no template instance, a template without a plan, or a value the
    /// plan can't place.
    /// </summary>
    /// <param name="data">The record's event data.</param>
    /// <param name="binxmlChunkBase">Chunk-relative offset of <paramref name="data"/>.</param>
    /// <param name="vub">Output builder.</param>
    /// <returns>True if the record was written.</returns>
    private bool TryWritePlanned(ReadOnlySpan<byte> data, int binxmlChunkBase, ref ValueUtf8Builder vub)
    {
        if (!TryReadPlannedInstance(data, binxmlChunkBase, null, recordLevel: true, out JsonPlan? plan, out int valuesPos,
                out int numVals))
            return false;

        Span<int> offsets = numVals <= 64 ? stackalloc int[64] : new int[numVals];
        ReadOnlySpan<SubstitutionDescriptor> descriptors = ValuesOf(data, binxmlChunkBase, valuesPos, numVals, offsets);
        _nestedKeyCount = 0;
        if (plan!.RootName == "Event")
            return WritePlanned(plan.Root, offsets[..numVals], descriptors, ref vub, forceObject: true, 0);

        vub.Append((byte)'{');
        vub.Append(plan.RootKey);
        if (!WritePlanned(plan.Root, offsets[..numVals], descriptors, ref vub, forceObject: false, 0))
            return false;
        vub.Append((byte)'}');
        return true;
    }

    /// <summary>
    /// Reads a BinXml stream's template instance header and finds its plan.
    /// </summary>
    /// <param name="data">BinXml bytes.</param>
    /// <param name="binxmlChunkBase">Chunk-relative offset of <paramref name="data"/>.</param>
    /// <param name="parentName">Name of the element the stream's root sits in, or null for a record.</param>
    /// <param name="recordLevel">True for a record's own data (anything after its root is ignored, as when decoding).</param>
    /// <param name="plan">The plan.</param>
    /// <param name="valuesPos">Position of the substitution descriptors in <paramref name="data"/>.</param>
    /// <param name="numVals">Number of substitution values.</param>
    /// <returns>False if the stream isn't a single planned template instance.</returns>
    private bool TryReadPlannedInstance(ReadOnlySpan<byte> data, int binxmlChunkBase, string? parentName, bool recordLevel,
                                        out JsonPlan? plan, out int valuesPos, out int numVals)
    {
        plan = null;
        valuesPos = 0;
        numVals = 0;
        int pos = 0;
        while ((pos < data.Length) && ((byte)(data[pos] & ~BinXmlToken.HasMoreDataFlag) == BinXmlToken.FragmentHeader))
            pos += 4; // token + major + minor + flags
        if ((pos >= data.Length) || ((byte)(data[pos] & ~BinXmlToken.HasMoreDataFlag) != BinXmlToken.TemplateInstance))
            return false;

        // token + unknown + template ID, definition offset, the inline definition on first use, then the value count
        int p = pos + 6;
        uint defDataOffset = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(p, 4));
        p += 4;
        bool inline = defDataOffset == (uint)(binxmlChunkBase + p);
        ref ResolvedTemplate resolved = ref ResolveTemplate<XmlWriter>(defDataOffset, inline ? data[p..] : default);
        plan = resolved.FieldSource?.JsonPlanFor(parentName);
        if (plan == null)
            return false;
        if (inline)
            p += 24 + (int)resolved.DataSize;

        numVals = ReadSubstitutionCount(data, p);
        valuesPos = p + 4;
        if (recordLevel)
            return true;

        // An embedded fragment with more items after its instance needs the tree (see BuildFragment)
        int end = valuesPos + (numVals * 4);
        foreach (SubstitutionDescriptor descriptor in MemoryMarshal.Cast<byte, SubstitutionDescriptor>(data.Slice(valuesPos, numVals * 4)))
            end += descriptor.Size;
        return (end >= data.Length) || ((byte)(data[end] & ~BinXmlToken.HasMoreDataFlag) == BinXmlToken.Eof);
    }

    /// <summary>
    /// Computes the file offset of each substitution value of an instance.
    /// </summary>
    /// <param name="data">BinXml bytes.</param>
    /// <param name="binxmlChunkBase">Chunk-relative offset of <paramref name="data"/>.</param>
    /// <param name="valuesPos">Position of the descriptors.</param>
    /// <param name="numVals">Number of values.</param>
    /// <param name="offsets">Receives each value's file offset.</param>
    /// <returns>The descriptors.</returns>
    private ReadOnlySpan<SubstitutionDescriptor> ValuesOf(ReadOnlySpan<byte> data, int binxmlChunkBase, int valuesPos,
                                                          int numVals, Span<int> offsets)
    {
        ReadOnlySpan<SubstitutionDescriptor> descriptors =
            MemoryMarshal.Cast<byte, SubstitutionDescriptor>(data.Slice(valuesPos, numVals * 4));
        int fileOffset = _chunkFileOffset + binxmlChunkBase + valuesPos + (numVals * 4);
        for (int i = 0; i < descriptors.Length; i++)
        {
            offsets[i] = fileOffset;
            fileOffset += descriptors[i].Size;
        }

        return descriptors;
    }

    /// <summary>
    /// Writes an element from its plan, exactly as the tree would. Returns false when a value needs the tree: embedded
    /// BinXml that isn't a single planned instance, whose key collides with another member, or plain text where the
    /// plan has members.
    /// </summary>
    /// <param name="plan">The element's plan.</param>
    /// <param name="offsets">File offset of each substitution value of its template instance.</param>
    /// <param name="descriptors">Size and type of each value.</param>
    /// <param name="vub">Output builder.</param>
    /// <param name="forceObject">True for the Event root, always an object.</param>
    /// <param name="depthBase">Depth of the template root's parent in the whole event.</param>
    /// <returns>False if the record needs the tree.</returns>
    private bool WritePlanned(JsonElementPlan plan, scoped ReadOnlySpan<int> offsets,
                              scoped ReadOnlySpan<SubstitutionDescriptor> descriptors, ref ValueUtf8Builder vub,
                              bool forceObject, int depthBase)
    {
        if (depthBase + plan.Depth > JsonTree.MaxDepth)
            return false;

        int contentSub = plan.ContentSubId;
        // The usual leaf: <EventID>%3</EventID>, written as its value
        if (plan.IsScalarSubstitution && ((contentSub >= offsets.Length) || (descriptors[contentSub].Type != BinXmlValueType.BinXml)))
        {
            WriteScalar(contentSub, offsets, descriptors, plan.Numeric, ref vub);
            return true;
        }

        bool contentNested = (contentSub >= 0) && (contentSub < offsets.Length) &&
                             (descriptors[contentSub].Type == BinXmlValueType.BinXml) && (descriptors[contentSub].Size > 0);
        bool isObject = plan.AlwaysObject || forceObject || contentNested;
        if (!isObject && plan.HasOptionalAttributes)
        {
            foreach (JsonAttributePlan attribute in plan.Attributes)
            {
                if (attribute.OptionalOnly && HasText(attribute.SubId, offsets, descriptors))
                {
                    isObject = true;
                    break;
                }
            }
        }

        if (!isObject)
        {
            if (plan.Content == JsonContentPlan.Literal)
                vub.Append(plan.ContentLiteral);
            else if (contentSub >= 0)
                WriteScalar(contentSub, offsets, descriptors, plan.Numeric, ref vub);
            else
                vub.Append("null"u8);
            return true;
        }

        vub.Append((byte)'{');
        bool first = true;
        foreach (JsonAttributePlan attribute in plan.Attributes)
        {
            if (attribute.Literal != null)
            {
                Member(ref vub, ref first, attribute.Key);
                vub.Append(attribute.Literal);
                continue;
            }

            if (attribute.OptionalOnly && !HasText(attribute.SubId, offsets, descriptors))
                continue;
            Member(ref vub, ref first, attribute.Key);
            WriteScalar(attribute.SubId, offsets, descriptors, attribute.Numeric, ref vub);
        }

        // Embedded roots written in this object, for the key collision check; released when the object closes (a record
        // that bails out resets the stack in TryWritePlanned)
        int nestedBase = _nestedKeyCount;
        {
            foreach (JsonMemberPlan member in plan.Members)
            {
                if (member.Elements is { } elements)
                {
                    Member(ref vub, ref first, member.Key!);
                    if (elements.Length == 1)
                    {
                        if (!WritePlanned(elements[0], offsets, descriptors, ref vub, forceObject: false, depthBase))
                            return false;
                        continue;
                    }

                    vub.Append((byte)'[');
                    for (int i = 0; i < elements.Length; i++)
                    {
                        if (i > 0)
                            vub.Append((byte)',');
                        if (!WritePlanned(elements[i], offsets, descriptors, ref vub, forceObject: false, depthBase))
                            return false;
                    }
                    vub.Append((byte)']');
                    continue;
                }

                if (!WriteNestedMember(plan, member.NestedSubId, offsets, descriptors, ref vub, ref first, depthBase))
                    return false;
            }

            if (contentNested)
            {
                if (!WriteNestedMember(plan, contentSub, offsets, descriptors, ref vub, ref first, depthBase))
                    return false;
            }
            else if (plan.Content == JsonContentPlan.Literal)
            {
                Member(ref vub, ref first, "\"#text\":"u8);
                vub.Append(plan.ContentLiteral);
            }
            else if ((contentSub >= 0) && HasText(contentSub, offsets, descriptors))
            {
                Member(ref vub, ref first, "\"#text\":"u8);
                WriteScalar(contentSub, offsets, descriptors, plan.Numeric, ref vub);
            }
        }

        _nestedKeyCount = nestedBase;
        vub.Append((byte)'}');
        return true;
    }

    /// <summary>
    /// Writes the root of an embedded BinXml value as a member of <paramref name="parent"/>'s object. An empty value
    /// writes nothing; text where a member is expected, or a key another member has, needs the tree.
    /// </summary>
    /// <param name="parent">Plan of the element holding the value.</param>
    /// <param name="subId">The value's substitution.</param>
    /// <param name="offsets">File offset of each substitution value of the parent's instance.</param>
    /// <param name="descriptors">Size and type of each value.</param>
    /// <param name="vub">Output builder.</param>
    /// <param name="first">True if the object has no member yet.</param>
    /// <param name="depthBase">Depth of the parent's template root's parent.</param>
    /// <returns>False if the record needs the tree.</returns>
    private bool WriteNestedMember(JsonElementPlan parent, int subId, scoped ReadOnlySpan<int> offsets,
                                   scoped ReadOnlySpan<SubstitutionDescriptor> descriptors, ref ValueUtf8Builder vub,
                                   ref bool first, int depthBase)
    {
        if ((subId >= offsets.Length) || (descriptors[subId].Size == 0) || (descriptors[subId].Type == BinXmlValueType.Null))
            return true;
        if (descriptors[subId].Type != BinXmlValueType.BinXml)
            return !HasText(subId, offsets, descriptors);

        ReadOnlySpan<byte> data = ChunkBytes(offsets[subId], descriptors[subId].Size);
        int binxmlChunkBase = offsets[subId] - _chunkFileOffset;
        if (!TryReadPlannedInstance(data, binxmlChunkBase, parent.Name, recordLevel: false, out JsonPlan? plan,
                out int valuesPos, out int numVals))
            return false;

        // The tree groups an embedded root with siblings of the same key; the plan only writes unique keys
        foreach (JsonMemberPlan member in parent.Members)
        {
            if ((member.Key != null) && member.Key.AsSpan().SequenceEqual(plan!.RootKey))
                return false;
        }

        _nestedKeys ??= new byte[8][];
        for (int i = 0; i < _nestedKeyCount; i++)
        {
            if (_nestedKeys[i].AsSpan().SequenceEqual(plan!.RootKey))
                return false;
        }

        if (_nestedKeyCount == _nestedKeys.Length)
            Array.Resize(ref _nestedKeys, _nestedKeys.Length * 2);
        _nestedKeys[_nestedKeyCount++] = plan!.RootKey;

        Span<int> nestedOffsets = numVals <= 64 ? stackalloc int[64] : new int[numVals];
        ReadOnlySpan<SubstitutionDescriptor> nestedDescriptors = ValuesOf(data, binxmlChunkBase, valuesPos, numVals, nestedOffsets);
        Member(ref vub, ref first, plan.RootKey);
        return WritePlanned(plan.Root, nestedOffsets[..numVals], nestedDescriptors, ref vub, forceObject: false,
            depthBase + parent.Depth);
    }

    /// <summary>
    /// Writes an object member's separating comma (after the first) and key.
    /// </summary>
    /// <param name="vub">Output builder.</param>
    /// <param name="first">True if the object has no member yet; cleared.</param>
    /// <param name="key">The key with its colon.</param>
    private static void Member(ref ValueUtf8Builder vub, ref bool first, scoped ReadOnlySpan<byte> key)
    {
        if (!first)
            vub.Append((byte)',');
        first = false;
        vub.Append(key);
    }

    /// <summary>
    /// True if a substitution value formats to non-empty text (what makes the tree keep an optional attribute), decided
    /// from its type and bytes without formatting it: a string is empty when it holds only a terminating NUL, and the
    /// fixed-size types write nothing when they are too short (see <see cref="WriteValue{TFormat}"/>).
    /// </summary>
    /// <param name="subId">The substitution.</param>
    /// <param name="offsets">File offset of each value.</param>
    /// <param name="descriptors">Size and type of each value.</param>
    /// <returns>True if the value has text.</returns>
    private bool HasText(int subId, scoped ReadOnlySpan<int> offsets, scoped ReadOnlySpan<SubstitutionDescriptor> descriptors)
    {
        if (subId >= offsets.Length)
            return false;
        int size = descriptors[subId].Size;
        byte type = descriptors[subId].Type;
        if ((size == 0) || (type == BinXmlValueType.Null))
            return false;
        // Arrays always make a part in the tree, even when their text is empty
        if ((type & BinXmlValueType.ArrayFlag) != 0)
            return true;
        return type switch
        {
            // Two or more characters keep at least one after the terminating NUL is trimmed
            BinXmlValueType.String => (size >= 4) || ((size >= 2) && (MemoryMarshal.Read<char>(ChunkBytes(offsets[subId], size)) != '\0')),
            BinXmlValueType.AnsiString => ChunkBytes(offsets[subId], size)[0] != 0,
            BinXmlValueType.Guid or BinXmlValueType.SystemTime => size >= 16,
            // A zero FILETIME is written as nothing
            BinXmlValueType.FileTime => (size >= 8) && (MemoryMarshal.Read<long>(ChunkBytes(offsets[subId], size)) != 0),
            BinXmlValueType.Sid => size >= 8,
            _ => true,
        };
    }

    /// <summary>
    /// Writes one substitution value as the tree writes a single part: null when empty, an array as its JSON array, a
    /// number or boolean unquoted, anything else as an escaped string (a numeric System field's text as a number when
    /// it is one). Common types are written straight to the output: strings escaped from their UTF-16 text, integers
    /// as digits, and hex, GUID, SID and time values quoted as formatted (their text never needs escaping).
    /// </summary>
    /// <param name="subId">The substitution.</param>
    /// <param name="offsets">File offset of each value.</param>
    /// <param name="descriptors">Size and type of each value.</param>
    /// <param name="numeric">True for a known numeric System field.</param>
    /// <param name="vub">Output builder.</param>
    private void WriteScalar(int subId, scoped ReadOnlySpan<int> offsets, scoped ReadOnlySpan<SubstitutionDescriptor> descriptors,
                             bool numeric, ref ValueUtf8Builder vub)
    {
        if (!HasText(subId, offsets, descriptors))
        {
            vub.Append("null"u8);
            return;
        }

        byte type = descriptors[subId].Type;
        int size = descriptors[subId].Size;
        int offset = offsets[subId];
        switch (type)
        {
            case BinXmlValueType.String when !numeric:
            {
                ReadOnlySpan<char> chars = MemoryMarshal.Cast<byte, char>(ChunkBytes(offset, size));
                if (chars[^1] == '\0')
                    chars = chars[..^1];
                vub.Append((byte)'"');
                BinXmlValueFormatter.AppendJsonEscaped(ref vub, chars);
                vub.Append((byte)'"');
                return;
            }

            case BinXmlValueType.Int8 or BinXmlValueType.UInt8 or BinXmlValueType.Int16 or BinXmlValueType.UInt16 or
                BinXmlValueType.Int32 or BinXmlValueType.UInt32 or BinXmlValueType.Int64 or BinXmlValueType.UInt64 or
                BinXmlValueType.Bool:
                // Digits (or true/false): always a valid JSON number or literal
                WriteValue<TextWriter>(size, type, offset, offset - _chunkFileOffset, ref vub, escaped: false);
                return;

            case BinXmlValueType.HexInt32 or BinXmlValueType.HexInt64 or BinXmlValueType.SizeT or BinXmlValueType.Guid or
                BinXmlValueType.Sid or BinXmlValueType.FileTime or BinXmlValueType.SystemTime or BinXmlValueType.Binary:
                // ASCII without quotes or backslashes, so it is its own JSON string content
                vub.Append((byte)'"');
                WriteValue<TextWriter>(size, type, offset, offset - _chunkFileOffset, ref vub, escaped: false);
                vub.Append((byte)'"');
                return;
        }

        if ((type & BinXmlValueType.ArrayFlag) != 0)
        {
            WriteArrayJson(type, size, offset, ref vub);
            return;
        }

        ValueUtf8Builder text = new(stackalloc byte[256]);
        WriteValue<TextWriter>(size, type, offset, offset - _chunkFileOffset, ref text, escaped: false);
        ReadOnlySpan<byte> utf8 = text.AsSpan();
        if (utf8.IsEmpty)
            vub.Append("null"u8);
        else if ((PartKindOf(type) != JsonPartKind.Text) &&
                 (JsonTree.IsJsonNumber(utf8) || utf8.SequenceEqual("true"u8) || utf8.SequenceEqual("false"u8)))
            vub.Append(utf8);
        else if (numeric && (PartKindOf(type) == JsonPartKind.Text) && JsonTree.IsJsonNumber(utf8))
            vub.Append(utf8);
        else
        {
            vub.Append((byte)'"');
            BinXmlValueFormatter.AppendJsonEscapedUtf8(ref vub, utf8);
            vub.Append((byte)'"');
        }

        text.Dispose();
    }

    /// <summary>
    /// The tree's part kind for a (non-array) value type.
    /// </summary>
    /// <param name="type">Value type.</param>
    /// <returns>Number for integers and floating-point values, Bool for booleans, else Text.</returns>
    private static JsonPartKind PartKindOf(byte type) => type switch
    {
        BinXmlValueType.Int8 or BinXmlValueType.UInt8 or BinXmlValueType.Int16 or BinXmlValueType.UInt16 or
            BinXmlValueType.Int32 or BinXmlValueType.UInt32 or BinXmlValueType.Int64 or BinXmlValueType.UInt64 or
            BinXmlValueType.Float or BinXmlValueType.Double => JsonPartKind.Number,
        BinXmlValueType.Bool => JsonPartKind.Bool,
        _ => JsonPartKind.Text,
    };

    /// <summary>
    /// Builds a BinXml stream (a record's event data or an embedded value) into the tree under
    /// <paramref name="parent"/>: its template instance from the template's program, anything else through the decoder.
    /// </summary>
    /// <param name="data">BinXml bytes.</param>
    /// <param name="binxmlChunkBase">Chunk-relative offset of <paramref name="data"/>.</param>
    /// <param name="tree">The tree.</param>
    /// <param name="parent">Element the stream's roots go under, or -1 for the document.</param>
    /// <param name="depth">Embedded BinXml nesting depth.</param>
    /// <param name="recordLevel">True for a record's own event data.</param>
    private void BuildFragment(ReadOnlySpan<byte> data, int binxmlChunkBase, JsonTree tree, int parent, int depth,
                               bool recordLevel)
    {
        if (depth > _maxRecursionDepth)
            Corrupt("embedded BinXml nests too deeply", 0);

        int pos = 0;
        while ((pos < data.Length) && ((byte)(data[pos] & ~BinXmlToken.HasMoreDataFlag) == BinXmlToken.FragmentHeader))
            pos += 4; // token + major + minor + flags
        if (pos >= data.Length)
            return;
        if ((byte)(data[pos] & ~BinXmlToken.HasMoreDataFlag) != BinXmlToken.TemplateInstance)
        {
            BuildFromXml(data, binxmlChunkBase, tree, parent, recordLevel);
            return;
        }

        // Same layout as WriteTemplateInstance reads: token + unknown + template ID, definition offset, the inline
        // definition when this is its first use, then the value count, descriptors and values
        int p = pos + 6;
        uint defDataOffset = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(p, 4));
        p += 4;
        bool inline = defDataOffset == (uint)(binxmlChunkBase + p);
        ref ResolvedTemplate resolved = ref ResolveTemplate<XmlWriter>(defDataOffset, inline ? data[p..] : default);
        TemplateProgram? program = resolved.FieldSource?.Program;
        if (program == null)
        {
            // No program to walk (the template doesn't compile, or is missing): the decoder renders what it can
            BuildFromXml(data, binxmlChunkBase, tree, parent, recordLevel);
            return;
        }

        if (inline)
            p += 24 + (int)resolved.DataSize;
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

        // An embedded fragment can hold more items after its template instance (Shell-Core values carry a
        // RenderingInfo element after it); the decoder renders them all, so let it build the whole fragment. A record's
        // own stream ends after its root, as in the other formats.
        int end = fileOffset - _chunkFileOffset - binxmlChunkBase;
        if (!recordLevel && (end < data.Length) && ((byte)(data[end] & ~BinXmlToken.HasMoreDataFlag) != BinXmlToken.Eof))
        {
            BuildFromXml(data, binxmlChunkBase, tree, parent, recordLevel);
            return;
        }

        BuildFromProgram(program.Ops, valueOffsets[..numVals], descriptors, tree, parent, depth);
    }

    /// <summary>
    /// Builds a template instance into the tree by walking its program with its substitution values.
    /// </summary>
    /// <param name="ops">The template's program.</param>
    /// <param name="valueOffsets">File offset of each substitution value.</param>
    /// <param name="descriptors">Size and type of each substitution value.</param>
    /// <param name="tree">The tree.</param>
    /// <param name="parent">Element the template's root goes under, or -1 for the document.</param>
    /// <param name="depth">Embedded BinXml nesting depth.</param>
    private void BuildFromProgram(TemplateOp[] ops, scoped ReadOnlySpan<int> valueOffsets,
                                  scoped ReadOnlySpan<SubstitutionDescriptor> descriptors, JsonTree tree, int parent, int depth)
    {
        int element = parent;
        int attribute = -1;
        // An attribute whose whole value is one optional substitution is omitted when that value is empty, as in XML
        bool attributeIsOptionalOnly = false;
        for (int i = 0; i < ops.Length; i++)
        {
            TemplateOp op = ops[i];
            switch (op.Kind)
            {
                case TemplateOpKind.StartElement:
                    element = tree.AddElement(element, op.Text!);
                    break;

                case TemplateOpKind.Attribute:
                    attribute = tree.AddAttribute(element, op.Text!);
                    attributeIsOptionalOnly = (i + 2 < ops.Length) && (ops[i + 1].Kind == TemplateOpKind.Substitution) &&
                                              ops[i + 1].IsOptional && (ops[i + 2].Kind == TemplateOpKind.EndAttribute);
                    break;

                case TemplateOpKind.EndAttribute:
                    if (attributeIsOptionalOnly && tree.AttributeIsEmpty(attribute))
                        tree.RemoveLastAttribute(element, attribute);
                    attribute = -1;
                    break;

                case TemplateOpKind.EndElement:
                case TemplateOpKind.CloseEmptyElement:
                    element = tree.Parent(element);
                    break;

                case TemplateOpKind.Text:
                case TemplateOpKind.CData:
                    AddText(tree, element, attribute, op.Text);
                    break;

                case TemplateOpKind.CharRef:
                    AddText(tree, element, attribute, [(char)op.CharValue]);
                    break;

                case TemplateOpKind.EntityRef:
                    AddText(tree, element, attribute, op.Text switch
                    {
                        "amp" => "&",
                        "lt" => "<",
                        "gt" => ">",
                        "quot" => "\"",
                        "apos" => "'",
                        _ => $"&{op.Text};",
                    });
                    break;

                case TemplateOpKind.Substitution:
                    if (op.SubId < valueOffsets.Length)
                        AddValue(tree, element, attribute, descriptors[op.SubId].Type, descriptors[op.SubId].Size,
                            valueOffsets[op.SubId], depth);
                    break;
            }
        }
    }

    /// <summary>
    /// Adds literal text to the current attribute, or else to the current element.
    /// </summary>
    /// <param name="tree">The tree.</param>
    /// <param name="element">Current element.</param>
    /// <param name="attribute">Current attribute, or -1.</param>
    /// <param name="text">The text.</param>
    private static void AddText(JsonTree tree, int element, int attribute, scoped ReadOnlySpan<char> text)
    {
        if (element < 0)
            return;
        if (attribute >= 0)
            tree.AddText(attribute, attribute: true, text);
        else
            tree.AddText(element, attribute: false, text);
    }

    /// <summary>
    /// Adds a substitution value to the current attribute or element, keeping its type: integers and floating-point
    /// values as numbers, booleans as booleans, arrays as JSON arrays, anything else as the text XML shows; an embedded
    /// BinXml value in element content is built in place.
    /// </summary>
    /// <param name="tree">The tree.</param>
    /// <param name="element">Current element.</param>
    /// <param name="attribute">Current attribute, or -1.</param>
    /// <param name="type">Value type.</param>
    /// <param name="size">Value size.</param>
    /// <param name="offset">Value file offset.</param>
    /// <param name="depth">Embedded BinXml nesting depth.</param>
    private void AddValue(JsonTree tree, int element, int attribute, byte type, int size, int offset, int depth)
    {
        if ((size == 0) || (type == BinXmlValueType.Null) || (element < 0))
            return;

        int binxmlChunkBase = offset - _chunkFileOffset;
        if ((type == BinXmlValueType.BinXml) && (attribute < 0))
        {
            BuildFragment(ChunkBytes(offset, size), binxmlChunkBase, tree, element, depth + 1, recordLevel: false);
            return;
        }

        int owner = attribute >= 0 ? attribute : element;
        bool isAttribute = attribute >= 0;
        if ((type & BinXmlValueType.ArrayFlag) != 0)
        {
            AddArray(tree, owner, isAttribute, type, size, offset);
            return;
        }

        JsonPartKind kind = PartKindOf(type);

        ValueUtf8Builder text = new(tree.Reserve(_jsonValueBytes));
        WriteValue<TextWriter>(size, type, offset, binxmlChunkBase, ref text, escaped: false);
        int start = text.IsInInitialBuffer ? tree.Commit(text.Length) : tree.Append(text.AsSpan());
        int length = text.Length;
        text.Dispose();
        if (length > 0)
            tree.AddPart(owner, isAttribute, kind, start, length);
    }

    /// <summary>
    /// Adds an array value: its XML text (items joined with <c>", "</c>) for use within other text, and its JSON array
    /// (numbers unquoted, everything else as strings) for when it is the whole value.
    /// </summary>
    /// <param name="tree">The tree.</param>
    /// <param name="owner">Element or attribute index.</param>
    /// <param name="isAttribute">True if <paramref name="owner"/> is an attribute.</param>
    /// <param name="type">Value type (with the array flag).</param>
    /// <param name="size">Value size.</param>
    /// <param name="offset">Value file offset.</param>
    private void AddArray(JsonTree tree, int owner, bool isAttribute, byte type, int size, int offset)
    {
        int binxmlChunkBase = offset - _chunkFileOffset;
        ValueUtf8Builder text = new(stackalloc byte[512]);
        WriteValue<TextWriter>(size, type, offset, binxmlChunkBase, ref text, escaped: false);
        int textStart = tree.Append(text.AsSpan());
        int textLength = text.Length;
        text.Dispose();

        ValueUtf8Builder json = new(stackalloc byte[512]);
        WriteArrayJson(type, size, offset, ref json);
        int jsonStart = tree.Append(json.AsSpan());
        int jsonLength = json.Length;
        json.Dispose();
        tree.AddPart(owner, isAttribute, JsonPartKind.Array, textStart, textLength, jsonStart, jsonLength);
    }

    /// <summary>
    /// Writes an array value as a JSON array: string items as strings, integer items as numbers, any other item type as
    /// the strings the XML output shows for them.
    /// </summary>
    /// <param name="type">Value type (with the array flag).</param>
    /// <param name="size">Value size.</param>
    /// <param name="offset">Value file offset.</param>
    /// <param name="json">Output builder.</param>
    private void WriteArrayJson(byte type, int size, int offset, ref ValueUtf8Builder json)
    {
        int binxmlChunkBase = offset - _chunkFileOffset;
        byte baseType = (byte)(type & ~BinXmlValueType.ArrayFlag);
        json.Append((byte)'[');
        if (baseType == BinXmlValueType.String)
        {
            ReadOnlySpan<char> chars = MemoryMarshal.Cast<byte, char>(ChunkBytes(offset, size));
            bool first = true;
            int start = 0;
            for (int i = 0; i <= chars.Length; i++)
            {
                if ((i < chars.Length) && (chars[i] != '\0'))
                    continue;
                if (i > start)
                {
                    if (!first)
                        json.Append((byte)',');
                    first = false;
                    json.Append((byte)'"');
                    BinXmlValueFormatter.AppendJsonEscaped(ref json, chars[start..i]);
                    json.Append((byte)'"');
                }
                start = i + 1;
            }
        }
        else
        {
            int elementSize = BinXmlValueFormatter.GetElementSize(baseType);
            bool numeric = baseType is BinXmlValueType.Int8 or BinXmlValueType.UInt8 or BinXmlValueType.Int16 or
                BinXmlValueType.UInt16 or BinXmlValueType.Int32 or BinXmlValueType.UInt32 or BinXmlValueType.Int64 or
                BinXmlValueType.UInt64;
            Span<byte> itemBuffer = stackalloc byte[128];
            for (int i = 0; (elementSize > 0) && (i + elementSize <= size); i += elementSize)
            {
                if (i > 0)
                    json.Append((byte)',');
                ValueUtf8Builder item = new(itemBuffer);
                WriteValue<TextWriter>(elementSize, baseType, offset + i, binxmlChunkBase + i, ref item, escaped: false);
                if (numeric)
                {
                    json.Append(item.AsSpan());
                }
                else
                {
                    json.Append((byte)'"');
                    BinXmlValueFormatter.AppendJsonEscapedUtf8(ref json, item.AsSpan());
                    json.Append((byte)'"');
                }
                item.Dispose();
            }
        }

        json.Append((byte)']');
    }

    /// <summary>
    /// Builds a BinXml stream into the tree through the shared decoder: decodes it to XML, then scans the XML (the
    /// decoder's own output, so a simple scan suffices; see <see cref="ScanXmlFields"/>).
    /// </summary>
    /// <param name="data">BinXml bytes.</param>
    /// <param name="binxmlChunkBase">Chunk-relative offset of <paramref name="data"/>.</param>
    /// <param name="tree">The tree.</param>
    /// <param name="parent">Element the stream's roots go under, or -1 for the document.</param>
    /// <param name="recordLevel">True for a record's own event data.</param>
    private void BuildFromXml(ReadOnlySpan<byte> data, int binxmlChunkBase, JsonTree tree, int parent, bool recordLevel)
    {
        ValueUtf8Builder xml = new(stackalloc byte[1024]);
        try
        {
            int pos = 0;
            DecodeTopLevel<XmlWriter>(data, ref pos, binxmlChunkBase, ref xml, recordLevel, escaped: false);
            ScanXmlTree(xml.AsSpan(), tree, parent);
        }
        finally
        {
            xml.Dispose();
        }
    }

    /// <summary>
    /// Scans XML produced by <see cref="XmlWriter"/> into the tree: elements, attributes and text (entities resolved).
    /// </summary>
    /// <param name="xml">The XML.</param>
    /// <param name="tree">The tree.</param>
    /// <param name="parent">Element the XML's roots go under, or -1 for the document.</param>
    private static void ScanXmlTree(ReadOnlySpan<byte> xml, JsonTree tree, int parent)
    {
        int element = parent;
        List<byte> value = new();
        int i = 0;
        while (i < xml.Length)
        {
            if (xml[i] != '<')
            {
                int next = xml[i..].IndexOf((byte)'<');
                int end = next < 0 ? xml.Length : i + next;
                if (element >= 0)
                {
                    value.Clear();
                    Unescape(xml[i..end], value);
                    tree.AddUtf8(element, attribute: false, JsonPartKind.Text, CollectionsMarshal.AsSpan(value));
                }
                i = end;
                continue;
            }

            if (xml[i..].StartsWith("<![CDATA["u8))
            {
                int end = xml[i..].IndexOf("]]>"u8);
                if (end < 0)
                    return;
                if (element >= 0)
                    tree.AddUtf8(element, attribute: false, JsonPartKind.Text, xml.Slice(i + 9, end - 9));
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
                if (element == parent)
                    return;
                element = tree.Parent(element);
                continue;
            }

            int nameEnd = tag.IndexOfAny((byte)' ', (byte)'/');
            element = tree.AddElement(element, Encoding.UTF8.GetString(nameEnd < 0 ? tag : tag[..nameEnd]));
            ReadOnlySpan<byte> rest = nameEnd < 0 ? default : tag[nameEnd..];
            while (true)
            {
                int eq = rest.IndexOf("=\""u8);
                if (eq < 0)
                    break;
                int attribute = tree.AddAttribute(element, Encoding.UTF8.GetString(rest[..eq].Trim((byte)' ')));
                rest = rest[(eq + 2)..];
                int quote = rest.IndexOf((byte)'"');
                if (quote < 0)
                    break;
                value.Clear();
                Unescape(rest[..quote], value);
                tree.AddUtf8(attribute, attribute: true, JsonPartKind.Text, CollectionsMarshal.AsSpan(value));
                rest = rest[(quote + 1)..];
            }
        }
    }

    #endregion

    #region Non-Public Fields

    /// <summary>
    /// Free tree buffer space requested before formatting a value into it (longer values spill and are copied).
    /// </summary>
    private const int _jsonValueBytes = 512;

    /// <summary>
    /// Test hook: true to render compact JSON through the tree only on this thread (single-threaded parses render on
    /// the calling thread), to check the plan writer against it.
    /// </summary>
    [ThreadStatic]
    internal static bool ForceJsonTree;

    /// <summary>
    /// Keys of embedded roots written in the open objects of the plan writer (a stack; see
    /// <see cref="WriteNestedMember"/>).
    /// </summary>
    private byte[][]? _nestedKeys;

    /// <summary>
    /// Entries of <see cref="_nestedKeys"/> in use.
    /// </summary>
    private int _nestedKeyCount;

    /// <summary>
    /// Per-thread tree, reused for every record.
    /// </summary>
    [ThreadStatic]
    private static JsonTree? _threadJsonTree;

    #endregion
}
