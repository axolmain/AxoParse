using System.Text;

namespace AxoParse.Evtx.BinXml;

/// <summary>
/// An attribute of a <see cref="JsonElementPlan"/>: a literal value (written as precomputed JSON) or one substitution.
/// </summary>
/// <param name="Key">The attribute's JSON key with its colon (<c>"Name":</c>).</param>
/// <param name="Literal">The value's JSON when the attribute is literal text, else null.</param>
/// <param name="SubId">The substitution holding the value, or -1 for a literal.</param>
/// <param name="OptionalOnly">True if the value is one optional substitution, so the attribute is omitted when it is empty.</param>
/// <param name="Numeric">True for a known numeric System attribute.</param>
internal sealed record JsonAttributePlan(byte[] Key, byte[]? Literal, int SubId, bool OptionalOnly, bool Numeric);

/// <summary>
/// A member of a <see cref="JsonElementPlan"/>'s object: a group of static child elements sharing a key (an array when
/// there are several), or a substitution whose embedded BinXml value adds its root as a member at run time.
/// </summary>
/// <param name="Key">The group's JSON key with its colon, or null for a nested substitution.</param>
/// <param name="Elements">The group's elements in document order, or null for a nested substitution.</param>
/// <param name="NestedSubId">The nested substitution, or -1.</param>
internal sealed record JsonMemberPlan(byte[]? Key, JsonElementPlan[]? Elements, int NestedSubId);

/// <summary>
/// How a <see cref="JsonElementPlan"/>'s own text is made.
/// </summary>
internal enum JsonContentPlan : byte
{
    /// <summary>
    /// No text.
    /// </summary>
    None,

    /// <summary>
    /// Literal text, written as precomputed JSON.
    /// </summary>
    Literal,

    /// <summary>
    /// One substitution (embedded BinXml adds its root as a member instead).
    /// </summary>
    Substitution,
}

/// <summary>
/// One element of a <see cref="JsonPlan"/>: everything about how it is written that doesn't depend on the record.
/// </summary>
internal sealed class JsonElementPlan
{
    /// <summary>
    /// Attributes, without dropped ones (<c>xmlns</c>) and without the Name attribute of a keyed EventData item.
    /// </summary>
    public JsonAttributePlan[] Attributes = [];

    /// <summary>
    /// How the element's own text is made.
    /// </summary>
    public JsonContentPlan Content;

    /// <summary>
    /// The text's JSON when <see cref="Content"/> is <see cref="JsonContentPlan.Literal"/>.
    /// </summary>
    public byte[]? ContentLiteral;

    /// <summary>
    /// The text's substitution when <see cref="Content"/> is <see cref="JsonContentPlan.Substitution"/>, else -1.
    /// </summary>
    public int ContentSubId = -1;

    /// <summary>
    /// Nesting depth below the template root (the root is 1).
    /// </summary>
    public int Depth;

    /// <summary>
    /// Object members in order of first appearance: static child groups and nested substitutions.
    /// </summary>
    public JsonMemberPlan[] Members = [];

    /// <summary>
    /// Element name (the parent name for embedded BinXml in its content).
    /// </summary>
    public string Name = string.Empty;

    /// <summary>
    /// True for a known numeric System element.
    /// </summary>
    public bool Numeric;

    /// <summary>
    /// True if an attribute is one optional substitution (it may be omitted).
    /// </summary>
    public bool HasOptionalAttributes;

    /// <summary>
    /// True if a member is a nested substitution (embedded BinXml).
    /// </summary>
    public bool HasNestedMembers;

    /// <summary>
    /// True for a leaf whose value is its one substitution: no attributes and no members, so it is written as that value
    /// unless the value is embedded BinXml.
    /// </summary>
    public bool IsScalarSubstitution;

    /// <summary>
    /// True if the element is always an object: it has static children, or an attribute that is always present.
    /// Otherwise it is an object only when an optional attribute or an embedded BinXml value is present.
    /// </summary>
    public bool AlwaysObject;
}

/// <summary>
/// A template compiled for the compact <see cref="OutputFormat.Json"/> shape: keys pre-escaped, child groups and arrays
/// decided, literal values pre-written, so a record is written by walking the plan with its substitution values (see
/// <see cref="BinXmlParser"/>'s plan writer) instead of building a <see cref="JsonTree"/>. Templates whose shape depends
/// on more than optional attributes and embedded BinXml (a substituted EventData Name, text mixed with
/// substitutions, literal text beside child elements) have no plan and use the tree; both give identical output.
/// </summary>
internal sealed class JsonPlan
{
    #region Constructors And Destructors

    /// <summary>
    /// Creates a plan.
    /// </summary>
    /// <param name="root">The template root's plan.</param>
    /// <param name="rootName">The template root's element name.</param>
    /// <param name="rootKey">The root's JSON key with its colon, as a member of its parent.</param>
    private JsonPlan(JsonElementPlan root, string rootName, byte[] rootKey)
    {
        Root = root;
        RootName = rootName;
        RootKey = rootKey;
    }

    #endregion

    #region Properties

    /// <summary>
    /// The template root's plan.
    /// </summary>
    public JsonElementPlan Root { get; }

    /// <summary>
    /// The root's JSON key with its colon, as a member of its parent (its Name for a keyed EventData item).
    /// </summary>
    public byte[] RootKey { get; }

    /// <summary>
    /// The template root's element name.
    /// </summary>
    public string RootName { get; }

    #endregion

    #region Public Methods

    /// <summary>
    /// Compiles a plan for a template program whose root sits under <paramref name="parentName"/>.
    /// </summary>
    /// <param name="program">The template's program.</param>
    /// <param name="parentName">Name of the element the root sits in (EventData keys items by Name), or null.</param>
    /// <returns>The plan, or null if the template's shape can't be planned.</returns>
    public static JsonPlan? Compile(TemplateProgram program, string? parentName)
    {
        Node? root = BuildTree(program.Ops);
        if (root == null)
            return null;
        JsonElementPlan? plan = PlanElement(root, parentName, 1, out byte[]? key);
        return plan == null ? null : new JsonPlan(plan, root.Name, key!);
    }

    #endregion

    #region Non-Public Methods

    /// <summary>
    /// Builds the template's element tree from its program.
    /// </summary>
    /// <param name="ops">The program.</param>
    /// <returns>The root, or null for a program without an element.</returns>
    private static Node? BuildTree(TemplateOp[] ops)
    {
        Node? root = null;
        Stack<Node> open = new();
        Attr? attribute = null;
        foreach (TemplateOp op in ops)
        {
            switch (op.Kind)
            {
                case TemplateOpKind.StartElement:
                {
                    Node node = new(op.Text!);
                    if (open.Count > 0)
                        open.Peek().Content.Add(node);
                    else
                        root ??= node;
                    open.Push(node);
                    break;
                }

                case TemplateOpKind.Attribute:
                    attribute = new Attr(op.Text!);
                    open.Peek().Attributes.Add(attribute);
                    break;

                case TemplateOpKind.EndAttribute:
                    attribute = null;
                    break;

                case TemplateOpKind.EndElement:
                case TemplateOpKind.CloseEmptyElement:
                    open.Pop();
                    break;

                case TemplateOpKind.Text:
                case TemplateOpKind.CData:
                    AddPart(new FieldPart(-1, op.Text ?? string.Empty), false);
                    break;

                case TemplateOpKind.CharRef:
                    AddPart(new FieldPart(-1, ((char)op.CharValue).ToString()), false);
                    break;

                case TemplateOpKind.EntityRef:
                    AddPart(new FieldPart(-1, op.Text switch
                    {
                        "amp" => "&",
                        "lt" => "<",
                        "gt" => ">",
                        "quot" => "\"",
                        "apos" => "'",
                        _ => $"&{op.Text};",
                    }), false);
                    break;

                case TemplateOpKind.Substitution:
                    AddPart(new FieldPart(op.SubId, null), op.IsOptional);
                    break;
            }
        }

        return root;

        void AddPart(FieldPart part, bool optional)
        {
            // Empty literal text adds nothing, as in the tree
            if ((part.SubId < 0) && string.IsNullOrEmpty(part.Literal))
                return;
            if (attribute != null)
            {
                attribute.Parts.Add(part);
                attribute.Optional |= optional;
            }
            else if (open.Count > 0)
            {
                open.Peek().Content.Add(part);
            }
        }
    }

    /// <summary>
    /// A JSON key with its colon.
    /// </summary>
    /// <param name="name">The key text.</param>
    /// <returns>The escaped key bytes.</returns>
    private static byte[] KeyBytes(string name)
    {
        ValueUtf8Builder vub = new(stackalloc byte[256]);
        vub.Append((byte)'"');
        BinXmlValueFormatter.AppendJsonEscaped(ref vub, name);
        vub.Append("\":"u8);
        byte[] key = vub.AsSpan().ToArray();
        vub.Dispose();
        return key;
    }

    /// <summary>
    /// Literal text as the JSON value the tree writes for it: a number for a numeric field whose text is one, else a
    /// string.
    /// </summary>
    /// <param name="text">The text.</param>
    /// <param name="numeric">True for a known numeric System field.</param>
    /// <returns>The JSON bytes.</returns>
    private static byte[] LiteralJson(string text, bool numeric)
    {
        byte[] utf8 = Encoding.UTF8.GetBytes(text);
        if (numeric && JsonTree.IsJsonNumber(utf8))
            return utf8;
        ValueUtf8Builder vub = new(stackalloc byte[256]);
        vub.Append((byte)'"');
        BinXmlValueFormatter.AppendJsonEscapedUtf8(ref vub, utf8);
        vub.Append((byte)'"');
        byte[] json = vub.AsSpan().ToArray();
        vub.Dispose();
        return json;
    }

    /// <summary>
    /// Plans one element.
    /// </summary>
    /// <param name="node">The element.</param>
    /// <param name="parentName">Its parent's name, or null at the template root without a known parent.</param>
    /// <param name="depth">Its depth below the template root.</param>
    /// <param name="key">Its JSON key as a member of its parent.</param>
    /// <returns>The plan, or null if the element's shape can't be planned.</returns>
    private static JsonElementPlan? PlanElement(Node node, string? parentName, int depth, out byte[]? key)
    {
        key = null;
        bool keyedItem = false;
        string keyText = node.Name;
        if (parentName == "EventData")
        {
            foreach (Attr attribute in node.Attributes)
            {
                if (attribute.Name != "Name")
                    continue;
                // The tree keys an item by its Name's text; a substituted name isn't known until run time
                // An empty Name doesn't key the item in the tree (it keeps its element name and a null Name); not planned
                if ((attribute.Parts.Count == 0) || attribute.Parts.Exists(part => part.SubId >= 0))
                    return null;
                keyText = string.Concat(attribute.Parts.Select(part => part.Literal));
                keyedItem = true;
                break;
            }
        }

        key = KeyBytes(keyText);
        JsonElementPlan plan = new()
        {
            Depth = depth,
            Name = node.Name,
            Numeric = JsonTree.IsNumericSystemElement(parentName, node.Name),
        };
        List<JsonAttributePlan> attributes = new();
        foreach (Attr attribute in node.Attributes)
        {
            if (JsonTree.IsDropped(attribute.Name) || (keyedItem && (attribute.Name == "Name")))
                continue;
            byte[] attributeKey = KeyBytes(attribute.Name);
            bool numeric = JsonTree.IsNumericSystemAttribute(node.Name, attribute.Name);
            if (attribute.Parts.Count == 0)
            {
                attributes.Add(new JsonAttributePlan(attributeKey, "null"u8.ToArray(), -1, false, numeric));
                plan.AlwaysObject = true;
            }
            else if (!attribute.Parts.Exists(part => part.SubId >= 0))
            {
                string literal = string.Concat(attribute.Parts.Select(part => part.Literal));
                // The tree writes a value made of several parts as a string, whatever its text
                attributes.Add(new JsonAttributePlan(attributeKey, LiteralJson(literal, numeric && (attribute.Parts.Count == 1)), -1,
                    false, numeric));
                plan.AlwaysObject = true;
            }
            else if (attribute.Parts.Count == 1)
            {
                bool optionalOnly = attribute.Optional;
                attributes.Add(new JsonAttributePlan(attributeKey, null, attribute.Parts[0].SubId, optionalOnly, numeric));
                plan.AlwaysObject |= !optionalOnly;
            }
            else
            {
                // Text mixed with substitutions in one attribute
                return null;
            }
        }

        plan.Attributes = attributes.ToArray();
        plan.HasOptionalAttributes = attributes.Exists(attribute => attribute.OptionalOnly);

        // Content: static children grouped by key, substitutions beside them as nested members, or the element's text
        List<(byte[] Key, List<JsonElementPlan> Elements)> groups = new();
        List<JsonMemberPlan?> order = new();
        List<FieldPart> text = new();
        bool hasChildren = node.Content.Exists(item => item is Node);
        foreach (object item in node.Content)
        {
            if (item is Node child)
            {
                if (depth + 1 > JsonTree.MaxDepth)
                    return null;
                JsonElementPlan? childPlan = PlanElement(child, node.Name, depth + 1, out byte[]? childKey);
                if (childPlan == null)
                    return null;
                int group = groups.FindIndex(g => g.Key.AsSpan().SequenceEqual(childKey));
                if (group < 0)
                {
                    groups.Add((childKey!, new List<JsonElementPlan>()));
                    order.Add(null);
                    group = groups.Count - 1;
                }
                groups[group].Elements.Add(childPlan);
                continue;
            }

            FieldPart part = (FieldPart)item;
            if (hasChildren)
            {
                // Literal text beside children goes to #text after the members; not planned
                if (part.SubId < 0)
                    return null;
                order.Add(new JsonMemberPlan(null, null, part.SubId));
                continue;
            }

            text.Add(part);
        }

        if (hasChildren)
        {
            List<JsonMemberPlan> members = new();
            int nextGroup = 0;
            foreach (JsonMemberPlan? member in order)
            {
                if (member != null)
                {
                    members.Add(member);
                    continue;
                }

                (byte[] groupKey, List<JsonElementPlan> elements) = groups[nextGroup++];
                members.Add(new JsonMemberPlan(groupKey, elements.ToArray(), -1));
            }

            plan.Members = members.ToArray();
            plan.HasNestedMembers = members.Exists(member => member.NestedSubId >= 0);
            plan.AlwaysObject = true;
        }
        else if (text.Count == 1 && (text[0].SubId >= 0))
        {
            plan.Content = JsonContentPlan.Substitution;
            plan.ContentSubId = text[0].SubId;
            plan.IsScalarSubstitution = plan.Attributes.Length == 0;
        }
        else if ((text.Count > 0) && !text.Exists(part => part.SubId >= 0))
        {
            plan.Content = JsonContentPlan.Literal;
            plan.ContentLiteral = LiteralJson(string.Concat(text.Select(part => part.Literal)), plan.Numeric && (text.Count == 1));
        }
        else if (text.Count > 0)
        {
            // Text mixed with substitutions
            return null;
        }

        return plan;
    }

    #endregion

    #region Nested Types

    /// <summary>
    /// An attribute of the template tree.
    /// </summary>
    /// <param name="name">Attribute name.</param>
    private sealed class Attr(string name)
    {
        /// <summary>
        /// Attribute name.
        /// </summary>
        public readonly string Name = name;

        /// <summary>
        /// Value parts.
        /// </summary>
        public readonly List<FieldPart> Parts = new();

        /// <summary>
        /// True if a value part is an optional substitution.
        /// </summary>
        public bool Optional;
    }

    /// <summary>
    /// An element of the template tree.
    /// </summary>
    /// <param name="name">Element name.</param>
    private sealed class Node(string name)
    {
        /// <summary>
        /// Element name.
        /// </summary>
        public readonly string Name = name;

        /// <summary>
        /// Attributes in order.
        /// </summary>
        public readonly List<Attr> Attributes = new();

        /// <summary>
        /// Child elements and text parts in document order.
        /// </summary>
        public readonly List<object> Content = new();
    }

    #endregion
}
