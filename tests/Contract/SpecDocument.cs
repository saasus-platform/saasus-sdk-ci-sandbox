using YamlDotNet.Serialization;

namespace SaasusSdk.Contract;

/// <summary>
/// A parsed OpenAPI document, the source of the request inputs.
/// </summary>
/// <remarks>
/// Values come from the spec rather than being hardcoded, so they are
/// acceptable by construction: the Prism mock validates requests against the
/// same document. The synthesis rules are language independent (operationId to
/// parameters/body), hence shared with the other SDKs' suites.
/// </remarks>
internal sealed class SpecDocument
{
    private const string SyntheticValue = "synthetic";

    private static readonly string[] HttpMethods =
        { "get", "put", "post", "delete", "patch", "head", "options" };

    private readonly Dictionary<string, object?> _root;
    private readonly Dictionary<string, SpecOperation> _operations = new(StringComparer.Ordinal);

    private SpecDocument(Dictionary<string, object?> root)
    {
        _root = root;
        Index();
    }

    /// <summary>All indexed operations, ordered by operationId.</summary>
    internal IEnumerable<SpecOperation> Operations =>
        _operations.Values.OrderBy(o => o.OperationId, StringComparer.Ordinal);

    /// <summary>
    /// Loads <c>&lt;module&gt;.yml</c> from the spec directory, or returns
    /// <c>null</c> when the directory or the file is absent.
    /// </summary>
    internal static SpecDocument? LoadOrNull(string? specDir, string module)
    {
        if (string.IsNullOrEmpty(specDir))
        {
            return null;
        }
        var path = Path.Combine(specDir, module + ".yml");
        if (!File.Exists(path))
        {
            return null;
        }
        var decoded = new DeserializerBuilder().Build().Deserialize<object?>(File.ReadAllText(path));
        return Normalize(decoded) is Dictionary<string, object?> root ? new SpecDocument(root) : null;
    }

    /// <summary>
    /// Turns the YAML mappings into <c>Dictionary&lt;string, object?&gt;</c>.
    /// Unquoted response codes (<c>200:</c>) would otherwise be keyed by a
    /// non-string, leaving the document with two different map types.
    /// </summary>
    private static object? Normalize(object? value)
    {
        switch (value)
        {
            case IDictionary<object, object> map:
                var normalized = new Dictionary<string, object?>(map.Count, StringComparer.Ordinal);
                foreach (var entry in map)
                {
                    normalized[System.Convert.ToString(entry.Key) ?? string.Empty] = Normalize(entry.Value);
                }
                return normalized;
            case IList<object> list:
                return list.Select(Normalize).ToList();
            default:
                return value;
        }
    }

    // --- indexing -----------------------------------------------------------

    private void Index()
    {
        foreach (var (path, rawPathItem) in Map(_root.GetValueOrDefault("paths")))
        {
            if (rawPathItem is not Dictionary<string, object?> pathItem)
            {
                continue;
            }
            foreach (var method in HttpMethods)
            {
                if (pathItem.GetValueOrDefault(method) is not Dictionary<string, object?> rawOperation)
                {
                    continue;
                }
                if (rawOperation.GetValueOrDefault("operationId") is not string operationId)
                {
                    continue;
                }
                var operation = new SpecOperation(operationId, path);
                LoadParameters(operation, pathItem, rawOperation);
                operation.BodySchema = BodySchema(rawOperation);
                LoadStatuses(operation, rawOperation);
                _operations[operationId] = operation;
            }
        }
    }

    /// <summary>
    /// Collects the path, query and header parameters. The generated method
    /// takes all three as individual arguments, so they are not separated here;
    /// each is bound to its argument by name.
    /// </summary>
    private void LoadParameters(SpecOperation operation, Dictionary<string, object?> pathItem,
        Dictionary<string, object?> rawOperation)
    {
        // Path-level parameters apply to every operation of the path item.
        foreach (var list in new[] { pathItem.GetValueOrDefault("parameters"), rawOperation.GetValueOrDefault("parameters") })
        {
            foreach (var raw in List(list))
            {
                if (raw is not Dictionary<string, object?> reference)
                {
                    continue;
                }
                var parameter = Resolve(reference);
                if (parameter.GetValueOrDefault("name") is not string name)
                {
                    continue;
                }
                var location = parameter.GetValueOrDefault("in") as string;
                if (location is "path" or "query" or "header")
                {
                    operation.Parameters.Add(new SpecParam(name, parameter));
                }
            }
        }
    }

    private Dictionary<string, object?>? BodySchema(Dictionary<string, object?> rawOperation)
    {
        if (rawOperation.GetValueOrDefault("requestBody") is not Dictionary<string, object?> body)
        {
            return null;
        }
        if (Resolve(body).GetValueOrDefault("content") is not Dictionary<string, object?> content)
        {
            return null;
        }
        if (content.GetValueOrDefault("application/json") is not Dictionary<string, object?> json)
        {
            return null;
        }
        return json.GetValueOrDefault("schema") is Dictionary<string, object?> schema ? Resolve(schema) : null;
    }

    private static void LoadStatuses(SpecOperation operation, Dictionary<string, object?> rawOperation)
    {
        foreach (var (key, _) in Map(rawOperation.GetValueOrDefault("responses")))
        {
            if (!int.TryParse(key, out var code))
            {
                continue; // "default" and the like carry no status
            }
            operation.DeclaredStatuses.Add(code);
            if (code is >= 200 and < 300)
            {
                operation.HasSuccess = true;
            }
        }
    }

    // --- $ref resolution ----------------------------------------------------

    private Dictionary<string, object?> Resolve(Dictionary<string, object?> node)
    {
        for (var depth = 0; depth < 32; depth++)
        {
            if (node.GetValueOrDefault("$ref") is not string reference)
            {
                return node;
            }
            var next = ResolveRef(reference);
            if (next == null)
            {
                return new Dictionary<string, object?>(StringComparer.Ordinal);
            }
            node = next;
        }
        return node;
    }

    private Dictionary<string, object?>? ResolveRef(string reference)
    {
        if (!reference.StartsWith("#/", StringComparison.Ordinal))
        {
            return null;
        }
        object? current = _root;
        foreach (var rawSegment in reference.Substring(2).Split('/'))
        {
            var segment = rawSegment.Replace("~1", "/").Replace("~0", "~");
            if (current is not Dictionary<string, object?> map)
            {
                return null;
            }
            current = map.GetValueOrDefault(segment);
        }
        return current as Dictionary<string, object?>;
    }

    // --- value synthesis ----------------------------------------------------

    internal object? ParamValue(SpecParam parameter)
    {
        if (parameter.Node.TryGetValue("example", out var example))
        {
            return example;
        }
        return Synth(parameter.Node.GetValueOrDefault("schema") as Dictionary<string, object?>);
    }

    /// <summary>
    /// The labels of the request body's top-level oneOf/anyOf members, or an
    /// empty list when the body is neither. Each member becomes its own case so
    /// that every variant is actually sent.
    /// </summary>
    internal IReadOnlyList<string> BodyVariantLabels(SpecOperation operation)
    {
        var members = Variants(operation.BodySchema);
        if (members.Count == 0)
        {
            return Array.Empty<string>();
        }
        var discriminator = operation.BodySchema!.GetValueOrDefault("discriminator") as Dictionary<string, object?>;
        return members.Select(member => DiscriminatorValue(discriminator, RefOf(member))).ToList();
    }

    /// <summary>
    /// Synthesizes the request body, selecting the given top-level oneOf/anyOf
    /// member.
    /// </summary>
    internal object? BodyValue(SpecOperation operation, int variantIndex)
    {
        var members = Variants(operation.BodySchema);
        return variantIndex < members.Count
            ? SynthVariant(operation.BodySchema!, members[variantIndex])
            : Synth(operation.BodySchema);
    }

    private object? Synth(Dictionary<string, object?>? schema)
    {
        if (schema == null)
        {
            return null;
        }
        schema = Resolve(schema);
        if (schema.TryGetValue("example", out var example))
        {
            return example;
        }
        if (schema.GetValueOrDefault("enum") is IList<object?> { Count: > 0 } enumValues)
        {
            return enumValues[0];
        }
        var members = Variants(schema);
        if (members.Count > 0)
        {
            return SynthVariant(schema, members[0]);
        }
        if (schema.GetValueOrDefault("allOf") is IList<object?> allOf)
        {
            return SynthObject(MergeAllOf(allOf));
        }
        var kind = schema.GetValueOrDefault("type") as string;
        if (string.IsNullOrEmpty(kind) && schema.GetValueOrDefault("properties") is Dictionary<string, object?>)
        {
            kind = "object";
        }
        switch (kind)
        {
            case "object":
                return SynthObject(schema);
            case "array":
                var item = Synth(schema.GetValueOrDefault("items") as Dictionary<string, object?>);
                return item == null ? new List<object?>() : new List<object?> { item };
            default:
                return SynthScalar(schema);
        }
    }

    /// <summary>
    /// Fills every declared property, not just the required ones: an optional
    /// property that is never sent leaves its own serialization, and the
    /// required fields nested below it, unverified.
    /// </summary>
    private object SynthObject(Dictionary<string, object?> schema)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (name, raw) in Map(schema.GetValueOrDefault("properties")))
        {
            if (raw is Dictionary<string, object?> property)
            {
                result[name] = Synth(property);
            }
        }
        return result;
    }

    /// <summary>
    /// Composes the member schemas' properties, which is what allOf is used for
    /// in these specs.
    /// </summary>
    private Dictionary<string, object?> MergeAllOf(IList<object?> members)
    {
        var properties = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var raw in members)
        {
            if (raw is not Dictionary<string, object?> member)
            {
                continue;
            }
            foreach (var (name, property) in Map(Resolve(member).GetValueOrDefault("properties")))
            {
                properties[name] = property;
            }
        }
        return new Dictionary<string, object?>(StringComparer.Ordinal) { ["properties"] = properties };
    }

    /// <summary>
    /// Synthesizes one oneOf/anyOf member and, when the parent declares a
    /// discriminator, sets the discriminator property so that the value
    /// resolves to that member.
    /// </summary>
    private object? SynthVariant(Dictionary<string, object?> parent, object? member)
    {
        if (member is not Dictionary<string, object?> schema)
        {
            return new Dictionary<string, object?>(StringComparer.Ordinal);
        }
        var value = Synth(schema);
        if (parent.GetValueOrDefault("discriminator") is not Dictionary<string, object?> discriminator)
        {
            return value;
        }
        if (value is not Dictionary<string, object?> instance)
        {
            return value;
        }
        if (discriminator.GetValueOrDefault("propertyName") is string property)
        {
            instance[property] = DiscriminatorValue(discriminator, RefOf(member));
        }
        return instance;
    }

    private static object SynthScalar(Dictionary<string, object?> schema)
    {
        switch (schema.GetValueOrDefault("type") as string)
        {
            case "integer":
            case "number":
                return 1;
            case "boolean":
                return true;
        }
        switch (schema.GetValueOrDefault("format") as string)
        {
            case "date":
                return "2024-01-01";
            case "date-time":
                return "2024-01-01T00:00:00Z";
        }
        // A string with no example still has to clear the declared length floor
        // or the mock rejects it. Padding with an alphanumeric also satisfies
        // the character patterns these specs declare.
        var value = SyntheticValue;
        var minLength = IntOf(schema.GetValueOrDefault("minLength"));
        while (value.Length < minLength)
        {
            value += "x";
        }
        return value;
    }

    // --- small helpers ------------------------------------------------------

    /// <summary>The schema's oneOf or anyOf members, or an empty list.</summary>
    private static IList<object?> Variants(Dictionary<string, object?>? schema)
    {
        if (schema == null)
        {
            return Array.Empty<object?>();
        }
        if (schema.GetValueOrDefault("oneOf") is IList<object?> { Count: > 0 } oneOf)
        {
            return oneOf;
        }
        if (schema.GetValueOrDefault("anyOf") is IList<object?> { Count: > 0 } anyOf)
        {
            return anyOf;
        }
        return Array.Empty<object?>();
    }

    /// <summary>
    /// The discriminator value whose mapping points at the given member
    /// reference, falling back to the referenced schema name.
    /// </summary>
    private static string DiscriminatorValue(Dictionary<string, object?>? discriminator, string reference)
    {
        foreach (var (value, target) in Map(discriminator?.GetValueOrDefault("mapping")))
        {
            if (reference.Length > 0 && System.Convert.ToString(target) == reference)
            {
                return value;
            }
        }
        var slash = reference.LastIndexOf('/');
        return slash >= 0 ? reference.Substring(slash + 1) : SyntheticValue;
    }

    private static string RefOf(object? member) =>
        member is Dictionary<string, object?> schema && schema.GetValueOrDefault("$ref") is string reference
            ? reference
            : string.Empty;

    private static IEnumerable<KeyValuePair<string, object?>> Map(object? value) =>
        value as Dictionary<string, object?> ?? Enumerable.Empty<KeyValuePair<string, object?>>();

    private static IEnumerable<object?> List(object? value) =>
        value as IList<object?> ?? Enumerable.Empty<object?>();

    private static int IntOf(object? value) =>
        value != null && int.TryParse(System.Convert.ToString(value), out var parsed) ? parsed : 0;
}

/// <summary>One spec operation: its inputs and its declared responses.</summary>
internal sealed class SpecOperation
{
    internal SpecOperation(string operationId, string path)
    {
        OperationId = operationId;
        Path = path;
    }

    internal string OperationId { get; }

    internal string Path { get; }

    /// <summary>Path, query and header parameters, in declaration order.</summary>
    internal List<SpecParam> Parameters { get; } = new();

    internal Dictionary<string, object?>? BodySchema { get; set; }

    internal HashSet<int> DeclaredStatuses { get; } = new();

    internal bool HasSuccess { get; set; }
}

/// <summary>A spec parameter: its spec name and its declaration.</summary>
internal sealed class SpecParam
{
    internal SpecParam(string name, Dictionary<string, object?> node)
    {
        Name = name;
        Node = node;
    }

    internal string Name { get; }

    internal Dictionary<string, object?> Node { get; }
}
