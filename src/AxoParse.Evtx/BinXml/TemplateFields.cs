namespace AxoParse.Evtx.BinXml;

/// <summary>
/// What a <see cref="FieldSlot"/> fills.
/// </summary>
internal enum FieldKind : byte
{
    /// <summary>
    /// <c>System/Provider@Name</c>.
    /// </summary>
    Provider,

    /// <summary>
    /// <c>System/Provider@Guid</c>.
    /// </summary>
    ProviderGuid,

    /// <summary>
    /// <c>System/EventID</c>.
    /// </summary>
    EventId,

    /// <summary>
    /// <c>System/Version</c>.
    /// </summary>
    Version,

    /// <summary>
    /// <c>System/Level</c>.
    /// </summary>
    Level,

    /// <summary>
    /// <c>System/Task</c>.
    /// </summary>
    Task,

    /// <summary>
    /// <c>System/Opcode</c>.
    /// </summary>
    Opcode,

    /// <summary>
    /// <c>System/Keywords</c>.
    /// </summary>
    Keywords,

    /// <summary>
    /// <c>System/TimeCreated@SystemTime</c>.
    /// </summary>
    TimeCreated,

    /// <summary>
    /// <c>System/Correlation@ActivityID</c>.
    /// </summary>
    ActivityId,

    /// <summary>
    /// <c>System/Execution@ProcessID</c>.
    /// </summary>
    ProcessId,

    /// <summary>
    /// <c>System/Execution@ThreadID</c>.
    /// </summary>
    ThreadId,

    /// <summary>
    /// <c>System/Channel</c>.
    /// </summary>
    Channel,

    /// <summary>
    /// <c>System/Computer</c>.
    /// </summary>
    Computer,

    /// <summary>
    /// <c>System/Security@UserID</c>.
    /// </summary>
    UserId,

    /// <summary>
    /// A name/value pair: a leaf element under <c>EventData</c> (named by its <c>Name</c> attribute, else by the
    /// element) or under <c>UserData</c> (named by the element).
    /// </summary>
    Pair,

    /// <summary>
    /// Content that only matters when its value is embedded BinXml (type 0x21): the fragment is read with
    /// <see cref="FieldSlot.Context"/> as its position in the event.
    /// </summary>
    NestedOnly,
}

/// <summary>
/// Where a template's root sits in the event, which decides what its elements mean. A record's own template is at the
/// <see cref="Document"/> level; a template inside an embedded BinXml value (type 0x21) takes the context of the element
/// holding the value.
/// </summary>
internal enum FieldContext : byte
{
    /// <summary>
    /// The template root is the document root (normally <c>Event</c>).
    /// </summary>
    Document,

    /// <summary>
    /// The template root is a child of <c>Event</c> (e.g. <c>EventData</c> or <c>UserData</c> in its own template).
    /// </summary>
    InEvent,

    /// <summary>
    /// The template root is inside <c>Event/EventData</c>.
    /// </summary>
    InEventData,

    /// <summary>
    /// The template root is inside <c>Event/UserData</c>.
    /// </summary>
    InUserData,

    /// <summary>
    /// Anywhere else: nothing in the template is a field.
    /// </summary>
    Other,
}

/// <summary>
/// One part of a field's value: a substitution or literal template text.
/// </summary>
/// <param name="SubId">Substitution index, or -1 for literal text.</param>
/// <param name="Literal">Literal text (unescaped) when <paramref name="SubId"/> is -1.</param>
internal readonly record struct FieldPart(int SubId, string? Literal);

/// <summary>
/// The value of a field whose template text is all literal (Security templates hard-code the provider, channel and
/// often the level and task), parsed once when the slot is built instead of for every record.
/// </summary>
/// <param name="values">The parsed value, in the member <see cref="FieldSlot.Kind"/> selects.</param>
/// <param name="utf8">The text as UTF-8 (pairs).</param>
internal sealed class FieldConstant(SystemFields values, byte[] utf8)
{
    /// <summary>
    /// The parsed value, in the member the slot's kind selects.
    /// </summary>
    public readonly SystemFields Values = values;

    /// <summary>
    /// The text as UTF-8.
    /// </summary>
    public readonly byte[] Utf8 = utf8;
}

/// <summary>
/// One field of a template in one context: what it fills and where its value comes from.
/// </summary>
/// <param name="Kind">What the slot fills.</param>
/// <param name="Name">Pair name (<see cref="FieldKind.Pair"/>), or null when <paramref name="NameSubId"/> names it.</param>
/// <param name="NameSubId">Substitution holding the pair name (a substituted <c>Data@Name</c>), or -1.</param>
/// <param name="Parts">The value's parts, in order; usually a single substitution.</param>
/// <param name="Context">Context for an embedded BinXml value in a content part.</param>
/// <param name="Constant">The parsed value when every part is literal text, else null.</param>
internal readonly record struct FieldSlot(FieldKind Kind, string? Name, int NameSubId, FieldPart[] Parts, FieldContext Context,
                                          FieldConstant? Constant = null);

/// <summary>
/// The fields of one template: every attribute value and leaf-element content of its <see cref="TemplateProgram"/>,
/// with the element path that leads to it, interpreted per <see cref="FieldContext"/> into <see cref="FieldSlot"/>s on
/// first use. Built once per template (alongside its <see cref="CompiledTemplate"/>), so reading a record's fields is
/// a walk over a handful of slots and its substitution values, without decoding or re-parsing text.
/// </summary>
internal sealed class TemplateFields
{
    #region Constructors And Destructors

    /// <summary>
    /// Creates the fields of a template.
    /// </summary>
    /// <param name="facts">Every value position in the template.</param>
    private TemplateFields(Fact[] facts)
    {
        _facts = facts;
    }

    #endregion

    #region Public Methods

    /// <summary>
    /// Walks a template program and records each value position: attribute values (with the attribute and element
    /// path) and the content of each element (substitutions and literal text, with the path and the element's literal
    /// <c>Name</c> attribute).
    /// </summary>
    /// <param name="program">The template's program.</param>
    /// <returns>The template's fields.</returns>
    public static TemplateFields Compile(TemplateProgram program)
    {
        List<Fact> facts = new();
        List<Frame> stack = new();
        List<FieldPart>? attributeParts = null;
        string? attributeName = null;
        foreach (TemplateOp op in program.Ops)
        {
            switch (op.Kind)
            {
                case TemplateOpKind.StartElement:
                    if (stack.Count > 0)
                        stack[^1].HasChildren = true;
                    stack.Add(new Frame(op.Text!));
                    break;

                case TemplateOpKind.Attribute:
                    attributeName = op.Text;
                    attributeParts = new List<FieldPart>();
                    break;

                case TemplateOpKind.EndAttribute:
                {
                    Frame frame = stack[^1];
                    FieldPart[] parts = attributeParts!.ToArray();
                    if ((attributeName == "Name") && (parts.Length == 1))
                    {
                        frame.NameLiteral = parts[0].Literal;
                        frame.NameSubId = parts[0].SubId;
                    }

                    facts.Add(new Fact(PathOf(stack), attributeName, parts, null, -1, false));
                    attributeParts = null;
                    attributeName = null;
                    break;
                }

                case TemplateOpKind.Text:
                case TemplateOpKind.CData:
                    AddPart(new FieldPart(-1, op.Text ?? string.Empty));
                    break;

                case TemplateOpKind.CharRef:
                    AddPart(new FieldPart(-1, ((char)op.CharValue).ToString()));
                    break;

                case TemplateOpKind.EntityRef:
                    AddPart(new FieldPart(-1, EntityText(op.Text!)));
                    break;

                case TemplateOpKind.Substitution:
                    AddPart(new FieldPart(op.SubId, null));
                    break;

                case TemplateOpKind.EndElement:
                case TemplateOpKind.CloseEmptyElement:
                {
                    Frame frame = stack[^1];
                    facts.Add(new Fact(PathOf(stack), null, frame.Content.ToArray(), frame.NameLiteral, frame.NameSubId,
                        frame.HasChildren));
                    stack.RemoveAt(stack.Count - 1);
                    break;
                }
            }
        }

        return new TemplateFields(facts.ToArray());

        void AddPart(FieldPart part)
        {
            if (attributeParts != null)
                attributeParts.Add(part);
            else if (stack.Count > 0)
                stack[^1].Content.Add(part);
        }
    }

    /// <summary>
    /// The template's field slots when its root sits in <paramref name="context"/>, built on first use. Without pairs,
    /// only slots that can set a System field are returned: pair slots, and embedded BinXml that can only hold pairs
    /// (inside EventData or UserData), are left out.
    /// </summary>
    /// <param name="context">Where the template's root sits in the event.</param>
    /// <param name="pairs">True to include EventData/UserData pair slots.</param>
    /// <returns>The slots, in template order.</returns>
    public FieldSlot[] Slots(FieldContext context, bool pairs)
    {
        // Unsynchronised publish: racing threads build identical arrays (provider templates are shared)
        int index = ((int)context * 2) + (pairs ? 1 : 0);
        return _slots[index] ??= pairs ? Interpret(context) : SystemOnly(Slots(context, true));
    }

    #endregion

    #region Non-Public Methods

    /// <summary>
    /// The text an entity reference stands for (the five XML entities), or the reference itself.
    /// </summary>
    /// <param name="name">Entity name.</param>
    /// <returns>The entity's text.</returns>
    private static string EntityText(string name) => name switch
    {
        "amp" => "&",
        "lt" => "<",
        "gt" => ">",
        "quot" => "\"",
        "apos" => "'",
        _ => $"&{name};",
    };

    /// <summary>
    /// Element names from the root to the innermost open element.
    /// </summary>
    /// <param name="stack">Open elements.</param>
    /// <returns>The path.</returns>
    private static string[] PathOf(List<Frame> stack)
    {
        string[] path = new string[stack.Count];
        for (int i = 0; i < path.Length; i++)
            path[i] = stack[i].Name;
        return path;
    }

    /// <summary>
    /// The context an embedded BinXml value takes from the element holding it.
    /// </summary>
    /// <param name="path">Full path (context prefix + template path) of the element.</param>
    /// <returns>The nested context.</returns>
    private static FieldContext ContextOf(ReadOnlySpan<string> path)
    {
        if (path.Length == 0)
            return FieldContext.Document;
        if (path[0] != "Event")
            return FieldContext.Other;
        if (path.Length == 1)
            return FieldContext.InEvent;
        return path[1] switch
        {
            "EventData" => FieldContext.InEventData,
            "UserData" => FieldContext.InUserData,
            _ => FieldContext.Other,
        };
    }

    /// <summary>
    /// Interprets every value position for one context.
    /// </summary>
    /// <param name="context">Where the template's root sits.</param>
    /// <returns>The slots that fill something.</returns>
    private FieldSlot[] Interpret(FieldContext context)
    {
        string[] prefix = Prefix(context);

        List<FieldSlot> slots = new();
        foreach (Fact fact in _facts)
        {
            if (context == FieldContext.Other)
                break;

            string[] path = [.. prefix, .. fact.Path];
            if (fact.Attribute != null)
            {
                if (SystemAttribute(path, fact.Attribute) is { } kind && (fact.Parts.Length > 0))
                    slots.Add(new FieldSlot(kind, null, -1, fact.Parts, FieldContext.Other, ConstantOf(kind, fact.Parts)));
                continue;
            }

            FieldContext nested = ContextOf(path);
            if (fact.HasChildren)
            {
                // Content mixed with child elements matters only as embedded BinXml (e.g. <Event><System/>%1</Event>)
                if (HasSubstitution(fact.Parts))
                    slots.Add(new FieldSlot(FieldKind.NestedOnly, null, -1, fact.Parts, nested));
                continue;
            }

            if (SystemElement(path) is { } systemKind)
            {
                if (fact.Parts.Length > 0)
                    slots.Add(new FieldSlot(systemKind, null, -1, fact.Parts, nested, ConstantOf(systemKind, fact.Parts)));
                continue;
            }

            if (IsPair(path))
            {
                // Under EventData a leaf is named by its Name attribute (<Data Name="SubjectUserSid">), else by itself
                bool eventData = path[1] == "EventData";
                string? name = eventData && (fact.NameSubId < 0) ? fact.NameLiteral ?? path[^1] : path[^1];
                int nameSubId = eventData ? fact.NameSubId : -1;
                slots.Add(new FieldSlot(FieldKind.Pair, nameSubId >= 0 ? null : name, nameSubId, fact.Parts, nested,
                    ConstantOf(FieldKind.Pair, fact.Parts)));
                continue;
            }

            if (HasSubstitution(fact.Parts))
                slots.Add(new FieldSlot(FieldKind.NestedOnly, null, -1, fact.Parts, nested));
        }

        return slots.ToArray();
    }

    /// <summary>
    /// Parses a value whose parts are all literal text once, or returns null if any part is a substitution.
    /// </summary>
    /// <param name="kind">The field.</param>
    /// <param name="parts">The value's parts.</param>
    /// <returns>The constant, or null.</returns>
    private static FieldConstant? ConstantOf(FieldKind kind, FieldPart[] parts)
    {
        if (HasSubstitution(parts))
            return null;

        string text = string.Concat(parts.Select(part => part.Literal));
        byte[] utf8 = System.Text.Encoding.UTF8.GetBytes(text);
        SystemFields values = default;
        if (kind != FieldKind.Pair)
            BinXmlParser.SetSystemText(kind, utf8, ref values, null);
        return new FieldConstant(values, utf8);
    }

    /// <summary>
    /// The slots that can set a System field.
    /// </summary>
    /// <param name="slots">Every slot.</param>
    /// <returns>The slots without pair-only ones.</returns>
    private static FieldSlot[] SystemOnly(FieldSlot[] slots) =>
        slots.Where(slot => (slot.Kind != FieldKind.Pair) &&
                            (slot.Context is not (FieldContext.InEventData or FieldContext.InUserData) ||
                             (slot.Kind != FieldKind.NestedOnly))).ToArray();

    /// <summary>
    /// The element path leading to a template root in <paramref name="context"/>.
    /// </summary>
    /// <param name="context">Where the root sits.</param>
    /// <returns>Element names from the document root.</returns>
    internal static string[] Prefix(FieldContext context) => context switch
    {
        FieldContext.InEvent => ["Event"],
        FieldContext.InEventData => ["Event", "EventData"],
        FieldContext.InUserData => ["Event", "UserData"],
        _ => [],
    };

    /// <summary>
    /// True if any part is a substitution.
    /// </summary>
    /// <param name="parts">Value parts.</param>
    /// <returns>True if a substitution is present.</returns>
    private static bool HasSubstitution(FieldPart[] parts)
    {
        foreach (FieldPart part in parts)
        {
            if (part.SubId >= 0)
                return true;
        }

        return false;
    }

    /// <summary>
    /// True for a leaf below <c>Event/EventData</c> or <c>Event/UserData</c>.
    /// </summary>
    /// <param name="path">Full element path.</param>
    /// <returns>True if the element's content is a name/value pair.</returns>
    internal static bool IsPair(ReadOnlySpan<string> path) =>
        (path.Length >= 3) && (path[0] == "Event") && ((path[1] == "EventData") || (path[1] == "UserData"));

    /// <summary>
    /// The System attribute at <paramref name="path"/>, or null.
    /// </summary>
    /// <param name="path">Full element path.</param>
    /// <param name="attribute">Attribute name.</param>
    /// <returns>The field kind, or null.</returns>
    internal static FieldKind? SystemAttribute(ReadOnlySpan<string> path, string attribute)
    {
        if ((path.Length != 3) || (path[0] != "Event") || (path[1] != "System"))
            return null;
        return (path[2], attribute) switch
        {
            ("Provider", "Name") => FieldKind.Provider,
            ("Provider", "Guid") => FieldKind.ProviderGuid,
            ("TimeCreated", "SystemTime") => FieldKind.TimeCreated,
            ("Correlation", "ActivityID") => FieldKind.ActivityId,
            ("Execution", "ProcessID") => FieldKind.ProcessId,
            ("Execution", "ThreadID") => FieldKind.ThreadId,
            ("Security", "UserID") => FieldKind.UserId,
            _ => null,
        };
    }

    /// <summary>
    /// The System element at <paramref name="path"/>, or null.
    /// </summary>
    /// <param name="path">Full element path.</param>
    /// <returns>The field kind, or null.</returns>
    internal static FieldKind? SystemElement(ReadOnlySpan<string> path)
    {
        if ((path.Length != 3) || (path[0] != "Event") || (path[1] != "System"))
            return null;
        return path[2] switch
        {
            "EventID" => FieldKind.EventId,
            "Version" => FieldKind.Version,
            "Level" => FieldKind.Level,
            "Task" => FieldKind.Task,
            "Opcode" => FieldKind.Opcode,
            "Keywords" => FieldKind.Keywords,
            "Channel" => FieldKind.Channel,
            "Computer" => FieldKind.Computer,
            _ => null,
        };
    }

    #endregion

    #region Non-Public Fields

    /// <summary>
    /// Every value position in the template.
    /// </summary>
    private readonly Fact[] _facts;

    /// <summary>
    /// Slots per <see cref="FieldContext"/>, built on first use.
    /// </summary>
    private readonly FieldSlot[]?[] _slots = new FieldSlot[]?[((int)FieldContext.Other + 1) * 2];

    #endregion

    #region Nested Types

    /// <summary>
    /// A value position: an attribute value, or an element's content.
    /// </summary>
    /// <param name="Path">Element names from the template root.</param>
    /// <param name="Attribute">Attribute name, or null for element content.</param>
    /// <param name="Parts">The value's parts.</param>
    /// <param name="NameLiteral">Element content: the element's literal <c>Name</c> attribute, or null.</param>
    /// <param name="NameSubId">Element content: the substitution of the element's <c>Name</c> attribute, or -1.</param>
    /// <param name="HasChildren">Element content: true if the element has child elements.</param>
    private readonly record struct Fact(string[] Path, string? Attribute, FieldPart[] Parts, string? NameLiteral,
                                        int NameSubId, bool HasChildren);

    /// <summary>
    /// An open element during <see cref="Compile"/>.
    /// </summary>
    /// <param name="name">Element name.</param>
    private sealed class Frame(string name)
    {
        /// <summary>
        /// Element name.
        /// </summary>
        public readonly string Name = name;

        /// <summary>
        /// Content parts (text and substitutions) seen so far.
        /// </summary>
        public readonly List<FieldPart> Content = new();

        /// <summary>
        /// True once a child element starts.
        /// </summary>
        public bool HasChildren;

        /// <summary>
        /// Literal value of the element's <c>Name</c> attribute, or null.
        /// </summary>
        public string? NameLiteral;

        /// <summary>
        /// Substitution of the element's <c>Name</c> attribute, or -1.
        /// </summary>
        public int NameSubId = -1;
    }

    #endregion
}
