using System.Text;

namespace AxoParse.Evtx.BinXml;

/// <summary>
/// How a <see cref="JsonTree"/> value part is written when it is an element's or attribute's whole value.
/// </summary>
internal enum JsonPartKind : byte
{
    /// <summary>
    /// Text: a JSON string.
    /// </summary>
    Text,

    /// <summary>
    /// An integer or floating-point value: a JSON number when its text is one.
    /// </summary>
    Number,

    /// <summary>
    /// A boolean: <c>true</c> or <c>false</c>.
    /// </summary>
    Bool,

    /// <summary>
    /// An array value: its JSON array (its text is the XML rendering, used when it is mixed with other text).
    /// </summary>
    Array,
}

/// <summary>
/// One event as a small tree of elements, attributes and value parts, written as the compact
/// <see cref="OutputFormat.Json"/> shape. Built per record from a template program and its substitution values (or
/// from decoded XML), then written in one pass. Arrays are reused across records, so a warm tree allocates nothing.
/// <para>
/// The shape, for each element:
/// <list type="bullet">
/// <item>no attributes and no child elements: its text as a scalar (a single typed value keeps its type: number,
/// boolean or array), or <c>null</c> when it is empty;</item>
/// <item>otherwise an object: attributes as plain keys, child elements as keys (a name repeated among siblings becomes
/// an array, in document order), and any text under <c>#text</c>.</item>
/// </list>
/// The document's <c>Event</c> root is written as its object (no <c>Event</c> key); any other root is written as
/// <c>{"Name": …}</c>. Under <c>EventData</c> a child with a <c>Name</c> attribute is keyed by that name. <c>xmlns</c>
/// attributes are dropped, and so is an attribute whose whole value is an empty optional substitution (as XML omits
/// it). Known numeric <c>System</c> values (EventID, Version, Level, Task, Opcode, EventRecordID, Qualifiers,
/// ProcessID, ThreadID) are numbers even when the template stores them as text.
/// </para>
/// </summary>
internal sealed class JsonTree
{
    #region Properties

    /// <summary>
    /// True if the tree holds no element.
    /// </summary>
    public bool IsEmpty => _elementCount == 0;

    #endregion

    #region Public Methods

    /// <summary>
    /// Adds an attribute to <paramref name="element"/>.
    /// </summary>
    /// <param name="element">Owning element.</param>
    /// <param name="name">Attribute name.</param>
    /// <returns>The attribute's index.</returns>
    public int AddAttribute(int element, string name)
    {
        int a = _attributeCount++;
        if (a == _attributeNames.Length)
        {
            int size = a * 2;
            Array.Resize(ref _attributeNames, size);
            Array.Resize(ref _attributeNext, size);
            Array.Resize(ref _attributeFirstPart, size);
            Array.Resize(ref _attributeLastPart, size);
        }

        _attributeNames[a] = name;
        _attributeNext[a] = -1;
        _attributeFirstPart[a] = -1;
        _attributeLastPart[a] = -1;
        if (_elementLastAttribute[element] < 0)
            _elementFirstAttribute[element] = a;
        else
            _attributeNext[_elementLastAttribute[element]] = a;
        _elementLastAttribute[element] = a;
        return a;
    }

    /// <summary>
    /// Adds an element under <paramref name="parent"/> (or at the document level for -1).
    /// </summary>
    /// <param name="parent">Parent element, or -1.</param>
    /// <param name="name">Element name.</param>
    /// <returns>The element's index.</returns>
    /// <exception cref="InvalidDataException">Elements nest deeper than <see cref="MaxDepth"/> (corrupt input).</exception>
    public int AddElement(int parent, string name)
    {
        int depth = parent < 0 ? 1 : _elementDepth[parent] + 1;
        if (depth > MaxDepth)
            throw new InvalidDataException($"Elements nest deeper than {MaxDepth} levels");

        int e = _elementCount++;
        if (e == _elementNames.Length)
        {
            int size = e * 2;
            Array.Resize(ref _elementNames, size);
            Array.Resize(ref _elementParent, size);
            Array.Resize(ref _elementFirstChild, size);
            Array.Resize(ref _elementLastChild, size);
            Array.Resize(ref _elementNext, size);
            Array.Resize(ref _elementFirstAttribute, size);
            Array.Resize(ref _elementLastAttribute, size);
            Array.Resize(ref _elementFirstPart, size);
            Array.Resize(ref _elementLastPart, size);
            Array.Resize(ref _elementDepth, size);
        }

        _elementNames[e] = name;
        _elementDepth[e] = depth;
        _elementParent[e] = parent;
        _elementFirstChild[e] = -1;
        _elementLastChild[e] = -1;
        _elementNext[e] = -1;
        _elementFirstAttribute[e] = -1;
        _elementLastAttribute[e] = -1;
        _elementFirstPart[e] = -1;
        _elementLastPart[e] = -1;
        if (parent < 0)
        {
            if (_rootLast < 0)
                _rootFirst = e;
            else
                _elementNext[_rootLast] = e;
            _rootLast = e;
        }
        else
        {
            if (_elementLastChild[parent] < 0)
                _elementFirstChild[parent] = e;
            else
                _elementNext[_elementLastChild[parent]] = e;
            _elementLastChild[parent] = e;
        }

        return e;
    }

    /// <summary>
    /// Adds a value part (text written with <see cref="Reserve"/>) to an element or attribute.
    /// </summary>
    /// <param name="owner">Element or attribute index.</param>
    /// <param name="attribute">True if <paramref name="owner"/> is an attribute.</param>
    /// <param name="kind">How the part is written when it is the whole value.</param>
    /// <param name="start">Start of the part's text in the buffer.</param>
    /// <param name="length">Length of the part's text.</param>
    /// <param name="jsonStart">Start of an array part's JSON, else 0.</param>
    /// <param name="jsonLength">Length of an array part's JSON, else 0.</param>
    public void AddPart(int owner, bool attribute, JsonPartKind kind, int start, int length, int jsonStart = 0, int jsonLength = 0)
    {
        int p = _partCount++;
        if (p == _partKinds.Length)
        {
            int size = p * 2;
            Array.Resize(ref _partKinds, size);
            Array.Resize(ref _partStarts, size);
            Array.Resize(ref _partLengths, size);
            Array.Resize(ref _partJsonStarts, size);
            Array.Resize(ref _partJsonLengths, size);
            Array.Resize(ref _partNext, size);
        }

        _partKinds[p] = kind;
        _partStarts[p] = start;
        _partLengths[p] = length;
        _partJsonStarts[p] = jsonStart;
        _partJsonLengths[p] = jsonLength;
        _partNext[p] = -1;
        int[] first = attribute ? _attributeFirstPart : _elementFirstPart;
        int[] last = attribute ? _attributeLastPart : _elementLastPart;
        if (last[owner] < 0)
            first[owner] = p;
        else
            _partNext[last[owner]] = p;
        last[owner] = p;
    }

    /// <summary>
    /// Adds literal text as a part.
    /// </summary>
    /// <param name="owner">Element or attribute index.</param>
    /// <param name="attribute">True if <paramref name="owner"/> is an attribute.</param>
    /// <param name="text">The text.</param>
    public void AddText(int owner, bool attribute, scoped ReadOnlySpan<char> text)
    {
        if (text.IsEmpty)
            return;
        int start = _bufferLength;
        int written = Encoding.UTF8.GetBytes(text, Reserve(Encoding.UTF8.GetMaxByteCount(text.Length)));
        _bufferLength += written;
        AddPart(owner, attribute, JsonPartKind.Text, start, written);
    }

    /// <summary>
    /// Adds UTF-8 text as a part.
    /// </summary>
    /// <param name="owner">Element or attribute index.</param>
    /// <param name="attribute">True if <paramref name="owner"/> is an attribute.</param>
    /// <param name="kind">How the part is written when it is the whole value.</param>
    /// <param name="utf8">The text.</param>
    public void AddUtf8(int owner, bool attribute, JsonPartKind kind, scoped ReadOnlySpan<byte> utf8)
    {
        if (utf8.IsEmpty)
            return;
        AddPart(owner, attribute, kind, Append(utf8), utf8.Length);
    }

    /// <summary>
    /// Copies bytes into the buffer.
    /// </summary>
    /// <param name="utf8">The bytes.</param>
    /// <returns>Where they start.</returns>
    public int Append(scoped ReadOnlySpan<byte> utf8)
    {
        int start = _bufferLength;
        utf8.CopyTo(Reserve(utf8.Length));
        _bufferLength += utf8.Length;
        return start;
    }

    /// <summary>
    /// True if an attribute has no parts.
    /// </summary>
    /// <param name="attribute">The attribute.</param>
    /// <returns>True if its value is empty.</returns>
    public bool AttributeIsEmpty(int attribute) => _attributeFirstPart[attribute] < 0;

    /// <summary>
    /// The parent of an element, or -1 at the document level.
    /// </summary>
    /// <param name="element">The element.</param>
    /// <returns>Its parent.</returns>
    public int Parent(int element) => _elementParent[element];

    /// <summary>
    /// Removes the last attribute of <paramref name="element"/> (which must be <paramref name="attribute"/>).
    /// </summary>
    /// <param name="element">The element.</param>
    /// <param name="attribute">Its last attribute.</param>
    public void RemoveLastAttribute(int element, int attribute)
    {
        int previous = -1;
        for (int a = _elementFirstAttribute[element]; a != attribute; a = _attributeNext[a])
            previous = a;
        if (previous < 0)
            _elementFirstAttribute[element] = -1;
        else
            _attributeNext[previous] = -1;
        _elementLastAttribute[element] = previous;
    }

    /// <summary>
    /// Free buffer space of at least <paramref name="minimum"/> bytes; advance with <see cref="Commit"/>.
    /// </summary>
    /// <param name="minimum">Bytes needed.</param>
    /// <returns>The free space.</returns>
    public Span<byte> Reserve(int minimum)
    {
        if (_buffer.Length - _bufferLength < minimum)
            Array.Resize(ref _buffer, Math.Max(_buffer.Length * 2, _bufferLength + minimum));
        return _buffer.AsSpan(_bufferLength);
    }

    /// <summary>
    /// Marks <paramref name="length"/> bytes of the space from <see cref="Reserve"/> as used.
    /// </summary>
    /// <param name="length">Bytes written.</param>
    /// <returns>Where they start.</returns>
    public int Commit(int length)
    {
        int start = _bufferLength;
        _bufferLength += length;
        return start;
    }

    /// <summary>
    /// Empties the tree for the next record.
    /// </summary>
    public void Reset()
    {
        _elementCount = 0;
        _attributeCount = 0;
        _partCount = 0;
        _bufferLength = 0;
        _rootFirst = -1;
        _rootLast = -1;
        _keyCount = 0;
    }

    /// <summary>
    /// Writes the tree as compact JSON; writes nothing for an empty tree.
    /// </summary>
    /// <param name="vub">Output builder.</param>
    public void Write(ref ValueUtf8Builder vub)
    {
        if (_rootFirst < 0)
            return;

        // The usual case: one Event root, written as its own object
        if ((_elementNext[_rootFirst] < 0) && (_elementNames[_rootFirst] == "Event"))
        {
            WriteValue(ref vub, _rootFirst, skipNameAttribute: false, forceObject: true);
            return;
        }

        vub.Append((byte)'{');
        WriteMembers(ref vub, -1, _rootFirst, first: true);
        vub.Append((byte)'}');
    }

    #endregion

    #region Non-Public Methods

    /// <summary>
    /// True for an attribute that is dropped from the output (namespace declarations).
    /// </summary>
    /// <param name="name">Attribute name.</param>
    /// <returns>True to drop it.</returns>
    internal static bool IsDropped(string name) => (name == "xmlns") || name.StartsWith("xmlns:", StringComparison.Ordinal);

    /// <summary>
    /// True if text is a JSON number (<c>-?digits[.digits][e[+-]digits]</c>).
    /// </summary>
    /// <param name="utf8">The text.</param>
    /// <returns>True if it can be written unquoted.</returns>
    internal static bool IsJsonNumber(ReadOnlySpan<byte> utf8)
    {
        int i = 0;
        if ((i < utf8.Length) && (utf8[i] == '-'))
            i++;
        int digits = i;
        while ((i < utf8.Length) && char.IsAsciiDigit((char)utf8[i]))
            i++;
        if (i == digits)
            return false;
        // No leading zeros on multi-digit integers
        if ((utf8[digits] == '0') && (i - digits > 1))
            return false;
        if ((i < utf8.Length) && (utf8[i] == '.'))
        {
            int fraction = ++i;
            while ((i < utf8.Length) && char.IsAsciiDigit((char)utf8[i]))
                i++;
            if (i == fraction)
                return false;
        }

        if ((i < utf8.Length) && ((utf8[i] | 0x20) == 'e'))
        {
            i++;
            if ((i < utf8.Length) && ((utf8[i] == '+') || (utf8[i] == '-')))
                i++;
            int exponent = i;
            while ((i < utf8.Length) && char.IsAsciiDigit((char)utf8[i]))
                i++;
            if (i == exponent)
                return false;
        }

        return i == utf8.Length;
    }

    /// <summary>
    /// True for an element or attribute whose value is a known numeric System field.
    /// </summary>
    /// <param name="element">The element (for an attribute, its owner).</param>
    /// <param name="attribute">Attribute name, or null for the element's own value.</param>
    /// <returns>True if its text is written as a number when it is one.</returns>
    private bool IsNumericSystemField(int element, string? attribute)
    {
        int parent = _elementParent[element];
        return attribute != null
            ? IsNumericSystemAttribute(_elementNames[element], attribute)
            : IsNumericSystemElement(parent >= 0 ? _elementNames[parent] : null, _elementNames[element]);
    }

    /// <summary>
    /// True for an attribute that is a known numeric System field (Execution@ProcessID/ThreadID, EventID@Qualifiers).
    /// </summary>
    /// <param name="element">Owning element's name.</param>
    /// <param name="attribute">Attribute name.</param>
    /// <returns>True if its text is written as a number when it is one.</returns>
    internal static bool IsNumericSystemAttribute(string element, string attribute) => attribute switch
    {
        "ProcessID" or "ThreadID" => element == "Execution",
        "Qualifiers" => element == "EventID",
        _ => false,
    };

    /// <summary>
    /// True for an element that is a known numeric System field (EventID, Version, Level, Task, Opcode, EventRecordID).
    /// </summary>
    /// <param name="parent">Parent element's name, or null at the document level.</param>
    /// <param name="element">Element name.</param>
    /// <returns>True if its text is written as a number when it is one.</returns>
    internal static bool IsNumericSystemElement(string? parent, string element) =>
        (parent == "System") && element is "EventID" or "Version" or "Level" or "Task" or "Opcode" or "EventRecordID";

    /// <summary>
    /// The key a child element is written under: its <c>Name</c> attribute's text when it sits under
    /// <c>EventData</c> and has one, else its name.
    /// </summary>
    /// <param name="parent">The parent element (-1 at the document level).</param>
    /// <param name="child">The child.</param>
    /// <returns>The key, with whether it came from the Name attribute.</returns>
    private Key KeyOf(int parent, int child)
    {
        if ((parent >= 0) && (_elementNames[parent] == "EventData"))
        {
            for (int a = _elementFirstAttribute[child]; a >= 0; a = _attributeNext[a])
            {
                if ((_attributeNames[a] != "Name") || (_attributeFirstPart[a] < 0))
                    continue;
                int part = _attributeFirstPart[a];
                if (_partNext[part] < 0)
                    return new Key(null, _partStarts[part], _partLengths[part]);

                // A name made of several parts: join them into one run of the buffer
                int start = _bufferLength;
                for (int p = part; p >= 0; p = _partNext[p])
                    Append(_buffer.AsSpan(_partStarts[p], _partLengths[p]));
                return new Key(null, start, _bufferLength - start);
            }
        }

        return new Key(_elementNames[child], 0, 0);
    }

    /// <summary>
    /// True if two keys are equal.
    /// </summary>
    /// <param name="a">First key.</param>
    /// <param name="b">Second key.</param>
    /// <returns>True if equal.</returns>
    private bool SameKey(in Key a, in Key b)
    {
        if ((a.Name != null) && (b.Name != null))
            return a.Name == b.Name;
        if ((a.Name == null) && (b.Name == null))
            return _buffer.AsSpan(a.Start, a.Length).SequenceEqual(_buffer.AsSpan(b.Start, b.Length));

        // A name against a buffer run (an element among Name-keyed EventData items): compare as UTF-8
        string name = a.Name ?? b.Name!;
        ReadOnlySpan<byte> run = a.Name == null ? _buffer.AsSpan(a.Start, a.Length) : _buffer.AsSpan(b.Start, b.Length);
        if (name.Length > run.Length)
            return false;
        // A UTF-16 code unit is at most 3 UTF-8 bytes, so 85 characters fit the stack buffer
        Span<byte> utf8 = name.Length <= 85 ? stackalloc byte[256] : new byte[Encoding.UTF8.GetMaxByteCount(name.Length)];
        return Encoding.UTF8.TryGetBytes(name, utf8, out int written) && utf8[..written].SequenceEqual(run);
    }

    /// <summary>
    /// Writes a value made of parts: null when there are none, a single typed part as its JSON, else the joined text as
    /// a string (as a number for a known numeric System field whose text is one).
    /// </summary>
    /// <param name="vub">Output builder.</param>
    /// <param name="firstPart">First part, or -1.</param>
    /// <param name="numeric">True for a known numeric System field.</param>
    private void WriteParts(ref ValueUtf8Builder vub, int firstPart, bool numeric)
    {
        if (firstPart < 0)
        {
            vub.Append("null"u8);
            return;
        }

        if (_partNext[firstPart] < 0)
        {
            ReadOnlySpan<byte> text = _buffer.AsSpan(_partStarts[firstPart], _partLengths[firstPart]);
            switch (_partKinds[firstPart])
            {
                case JsonPartKind.Number or JsonPartKind.Bool when IsJsonNumber(text) || text.SequenceEqual("true"u8) || text.SequenceEqual("false"u8):
                    vub.Append(text);
                    return;
                case JsonPartKind.Array:
                    vub.Append(_buffer.AsSpan(_partJsonStarts[firstPart], _partJsonLengths[firstPart]));
                    return;
                case JsonPartKind.Text when numeric && IsJsonNumber(text):
                    vub.Append(text);
                    return;
            }
        }

        vub.Append((byte)'"');
        for (int p = firstPart; p >= 0; p = _partNext[p])
            BinXmlValueFormatter.AppendJsonEscapedUtf8(ref vub, _buffer.AsSpan(_partStarts[p], _partLengths[p]));
        vub.Append((byte)'"');
    }

    /// <summary>
    /// Writes an element's value (see the class remarks).
    /// </summary>
    /// <param name="vub">Output builder.</param>
    /// <param name="element">The element.</param>
    /// <param name="skipNameAttribute">True if its <c>Name</c> attribute is its key (EventData items).</param>
    /// <param name="forceObject">True to write an object even without attributes or children (the Event root).</param>
    private void WriteValue(ref ValueUtf8Builder vub, int element, bool skipNameAttribute, bool forceObject = false)
    {
        bool hasAttributes = false;
        for (int a = _elementFirstAttribute[element]; a >= 0; a = _attributeNext[a])
        {
            if (!IsDropped(_attributeNames[a]) && !(skipNameAttribute && (_attributeNames[a] == "Name")))
            {
                hasAttributes = true;
                break;
            }
        }

        int firstChild = _elementFirstChild[element];
        if (!hasAttributes && (firstChild < 0) && !forceObject)
        {
            WriteParts(ref vub, _elementFirstPart[element], IsNumericSystemField(element, null));
            return;
        }

        vub.Append((byte)'{');
        bool first = true;
        for (int a = _elementFirstAttribute[element]; a >= 0; a = _attributeNext[a])
        {
            string name = _attributeNames[a];
            if (IsDropped(name) || (skipNameAttribute && (name == "Name")))
                continue;
            if (!first)
                vub.Append((byte)',');
            first = false;
            WriteKey(ref vub, new Key(name, 0, 0));
            WriteParts(ref vub, _attributeFirstPart[a], IsNumericSystemField(element, name));
        }

        first = WriteMembers(ref vub, element, firstChild, first);
        if (_elementFirstPart[element] >= 0)
        {
            if (!first)
                vub.Append((byte)',');
            vub.Append("\"#text\":"u8);
            WriteParts(ref vub, _elementFirstPart[element], IsNumericSystemField(element, null));
        }

        vub.Append((byte)'}');
    }

    /// <summary>
    /// Writes an element's children as object members: each distinct key once, in order of first appearance; a key
    /// shared by several children holds an array of their values in document order.
    /// </summary>
    /// <param name="vub">Output builder.</param>
    /// <param name="parent">The parent (-1 for the document level).</param>
    /// <param name="firstChild">First child, or -1.</param>
    /// <param name="first">True if no member has been written yet.</param>
    /// <returns>True if still no member has been written.</returns>
    private bool WriteMembers(ref ValueUtf8Builder vub, int parent, int firstChild, bool first)
    {
        if (firstChild < 0)
            return first;

        // Keys of this element's children live in a stack region of the key arrays, released on return. Each child
        // records the first child with an equal key (its group's leader) and each leader its group size, found with
        // hashes so equal keys are compared only on a hash match.
        int baseIndex = _keyCount;
        for (int c = firstChild; c >= 0; c = _elementNext[c])
        {
            if (_keyCount == _keys.Length)
            {
                int size = _keys.Length * 2;
                Array.Resize(ref _keys, size);
                Array.Resize(ref _keyElements, size);
                Array.Resize(ref _keyHashes, size);
                Array.Resize(ref _keyLeaders, size);
                Array.Resize(ref _keyCounts, size);
            }

            int k = _keyCount++;
            Key key = KeyOf(parent, c);
            int hash = HashOf(key);
            int leader = k;
            for (int j = baseIndex; j < k; j++)
            {
                if ((_keyLeaders[j] == j) && (_keyHashes[j] == hash) && SameKey(_keys[j], key))
                {
                    leader = j;
                    break;
                }
            }

            _keys[k] = key;
            _keyElements[k] = c;
            _keyHashes[k] = hash;
            _keyLeaders[k] = leader;
            _keyCounts[k] = 0;
            _keyCounts[leader]++;
        }

        int end = _keyCount;
        for (int i = baseIndex; i < end; i++)
        {
            if (_keyLeaders[i] != i)
                continue;

            if (!first)
                vub.Append((byte)',');
            first = false;
            WriteKey(ref vub, _keys[i]);
            bool named = _keys[i].Name == null;
            if (_keyCounts[i] == 1)
            {
                WriteValue(ref vub, _keyElements[i], named);
                continue;
            }

            vub.Append((byte)'[');
            bool firstItem = true;
            for (int j = i; j < end; j++)
            {
                if (_keyLeaders[j] != i)
                    continue;
                if (!firstItem)
                    vub.Append((byte)',');
                firstItem = false;
                WriteValue(ref vub, _keyElements[j], named);
            }
            vub.Append((byte)']');
        }

        _keyCount = baseIndex;
        return first;
    }

    /// <summary>
    /// Hash of a key's UTF-8 bytes (FNV-1a), equal for a name and a buffer run holding the same text.
    /// </summary>
    /// <param name="key">The key.</param>
    /// <returns>The hash.</returns>
    private int HashOf(in Key key)
    {
        uint hash = 2166136261;
        if (key.Name == null)
        {
            foreach (byte b in _buffer.AsSpan(key.Start, key.Length))
                hash = (hash ^ b) * 16777619;
            return (int)hash;
        }

        foreach (char c in key.Name)
        {
            if (c >= 0x80)
                return HashOfUtf8(key.Name);
            hash = (hash ^ c) * 16777619;
        }

        return (int)hash;

        static int HashOfUtf8(string name)
        {
            uint h = 2166136261;
            foreach (byte b in Encoding.UTF8.GetBytes(name))
                h = (h ^ b) * 16777619;
            return (int)h;
        }
    }

    /// <summary>
    /// Writes an object key and its colon.
    /// </summary>
    /// <param name="vub">Output builder.</param>
    /// <param name="key">The key.</param>
    private void WriteKey(ref ValueUtf8Builder vub, in Key key)
    {
        vub.Append((byte)'"');
        if (key.Name != null)
            BinXmlValueFormatter.AppendJsonEscaped(ref vub, key.Name);
        else
            BinXmlValueFormatter.AppendJsonEscapedUtf8(ref vub, _buffer.AsSpan(key.Start, key.Length));
        vub.Append("\":"u8);
    }

    #endregion

    #region Fields

    /// <summary>
    /// Deepest element nesting written: real events nest about 6 deep, and with an array per level the JSON stays
    /// within the 64 levels JSON parsers accept by default (System.Text.Json's limit). Embedded BinXml can stack
    /// templates without bound in corrupt input, so deeper records fail as corrupt.
    /// </summary>
    public const int MaxDepth = 32;

    #endregion

    #region Non-Public Fields

    /// <summary>
    /// Nesting depth of each element (1 at the document level).
    /// </summary>
    private int[] _elementDepth = new int[64];

    /// <summary>
    /// Attribute count.
    /// </summary>
    private int _attributeCount;

    /// <summary>
    /// First part of each attribute's value, or -1.
    /// </summary>
    private int[] _attributeFirstPart = new int[64];

    /// <summary>
    /// Last part of each attribute's value, or -1.
    /// </summary>
    private int[] _attributeLastPart = new int[64];

    /// <summary>
    /// Attribute names.
    /// </summary>
    private string[] _attributeNames = new string[64];

    /// <summary>
    /// Next attribute of the same element, or -1.
    /// </summary>
    private int[] _attributeNext = new int[64];

    /// <summary>
    /// Text of every part, as UTF-8.
    /// </summary>
    private byte[] _buffer = new byte[8192];

    /// <summary>
    /// Bytes of <see cref="_buffer"/> in use.
    /// </summary>
    private int _bufferLength;

    /// <summary>
    /// Element count.
    /// </summary>
    private int _elementCount;

    /// <summary>
    /// First attribute of each element, or -1.
    /// </summary>
    private int[] _elementFirstAttribute = new int[64];

    /// <summary>
    /// First child of each element, or -1.
    /// </summary>
    private int[] _elementFirstChild = new int[64];

    /// <summary>
    /// First text part of each element, or -1.
    /// </summary>
    private int[] _elementFirstPart = new int[64];

    /// <summary>
    /// Last attribute of each element, or -1.
    /// </summary>
    private int[] _elementLastAttribute = new int[64];

    /// <summary>
    /// Last child of each element, or -1.
    /// </summary>
    private int[] _elementLastChild = new int[64];

    /// <summary>
    /// Last text part of each element, or -1.
    /// </summary>
    private int[] _elementLastPart = new int[64];

    /// <summary>
    /// Element names.
    /// </summary>
    private string[] _elementNames = new string[64];

    /// <summary>
    /// Next sibling of each element, or -1.
    /// </summary>
    private int[] _elementNext = new int[64];

    /// <summary>
    /// Parent of each element, or -1 at the document level.
    /// </summary>
    private int[] _elementParent = new int[64];

    /// <summary>
    /// Keys of the children being written, a stack across nesting levels.
    /// </summary>
    private Key[] _keys = new Key[64];

    /// <summary>
    /// Entries of <see cref="_keys"/> in use.
    /// </summary>
    private int _keyCount;

    /// <summary>
    /// The child element of each entry of <see cref="_keys"/>.
    /// </summary>
    private int[] _keyElements = new int[64];

    /// <summary>
    /// Group size of each entry of <see cref="_keys"/> that leads its group.
    /// </summary>
    private int[] _keyCounts = new int[64];

    /// <summary>
    /// Hash of each entry of <see cref="_keys"/>.
    /// </summary>
    private int[] _keyHashes = new int[64];

    /// <summary>
    /// First entry with an equal key (the group leader) of each entry of <see cref="_keys"/>.
    /// </summary>
    private int[] _keyLeaders = new int[64];

    /// <summary>
    /// Part count.
    /// </summary>
    private int _partCount;

    /// <summary>
    /// Length of each array part's JSON.
    /// </summary>
    private int[] _partJsonLengths = new int[128];

    /// <summary>
    /// Start of each array part's JSON in the buffer.
    /// </summary>
    private int[] _partJsonStarts = new int[128];

    /// <summary>
    /// Kind of each part.
    /// </summary>
    private JsonPartKind[] _partKinds = new JsonPartKind[128];

    /// <summary>
    /// Length of each part's text.
    /// </summary>
    private int[] _partLengths = new int[128];

    /// <summary>
    /// Next part of the same owner, or -1.
    /// </summary>
    private int[] _partNext = new int[128];

    /// <summary>
    /// Start of each part's text in the buffer.
    /// </summary>
    private int[] _partStarts = new int[128];

    /// <summary>
    /// First document-level element, or -1.
    /// </summary>
    private int _rootFirst = -1;

    /// <summary>
    /// Last document-level element, or -1.
    /// </summary>
    private int _rootLast = -1;

    #endregion

    #region Nested Types

    /// <summary>
    /// An object key: an element name, or a run of the buffer (an EventData item's Name attribute).
    /// </summary>
    /// <param name="Name">The name, or null for a buffer run.</param>
    /// <param name="Start">Start of the buffer run.</param>
    /// <param name="Length">Length of the buffer run.</param>
    private readonly record struct Key(string? Name, int Start, int Length);

    #endregion
}
