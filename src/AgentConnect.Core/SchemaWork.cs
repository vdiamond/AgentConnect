using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using EasyJsonSchema;
using SchemaDocument = EasyJsonSchema.EasyJsonSchema;

namespace AgentConnect;

/// <summary>
/// A property resolved from the current schema.
/// </summary>
public sealed class ResolvedProperty
{
    /// <summary>Gets the EasyJsonSchema type name, including <c>[]</c> for arrays.</summary>
    public required string TypeName { get; init; }

    /// <summary>Gets a value indicating whether every property on the path is required.</summary>
    public required bool RequiredAlongPath { get; init; }

    /// <summary>Gets a value indicating whether the property is an array.</summary>
    public bool IsArray
    {
        get
        {
            return TypeName.EndsWith("[]", StringComparison.Ordinal);
        }
    }

    /// <summary>Gets a value indicating whether the property is an object or an object array.</summary>
    public bool IsObject
    {
        get
        {
            return TypeName is "Object" or "Object[]";
        }
    }

    /// <summary>Gets the element type of an array, or the type itself.</summary>
    public string ElementType
    {
        get
        {
            return IsArray ? TypeName[..^2] : TypeName;
        }
    }
}

/// <summary>
/// Looks up paths in one EasyJsonSchema document.
/// </summary>
public sealed class SchemaIndex
{
    /// <summary>
    /// Builds an index from a schema that has already passed schema validation.
    /// </summary>
    /// <param name="schemaJson">The schema JSON.</param>
    /// <returns>The index.</returns>
    public static SchemaIndex Load(string schemaJson)
    {
        var service = new EasyJsonSchemaService();
        if (!service.DeserializeEasyJsonSchema(schemaJson, out var schema, out var error) || schema is null)
        {
            throw new InvalidOperationException(error);
        }

        return new SchemaIndex(schema);
    }

    /// <summary>
    /// Resolves a filter or sort path.
    /// </summary>
    /// <param name="path">The path, with <c>.</c> between object properties.</param>
    /// <param name="property">The property when it resolves.</param>
    /// <returns><see langword="false"/> when the path is unknown or enters an <c>Object[]</c>.</returns>
    public bool TryResolve(string path, out ResolvedProperty property)
    {
        property = new ResolvedProperty
        {
            TypeName = "",
            RequiredAlongPath = false,
        };
        if (path.Length == 0)
        {
            return false;
        }

        var segments = path.Split('.');
        IReadOnlyList<PropertyDef> level = _root;
        var required = true;
        for (var index = 0; index < segments.Length; index++)
        {
            PropertyDef? match = null;
            foreach (var candidate in level)
            {
                if (candidate.Name == segments[index])
                {
                    match = candidate;
                    break;
                }
            }

            if (match is null || match.Type.Length == 0)
            {
                return false;
            }

            if (!match.Required)
            {
                required = false;
            }

            var last = index == segments.Length - 1;
            if (last)
            {
                property = new ResolvedProperty
                {
                    TypeName = match.Type,
                    RequiredAlongPath = required,
                };
                return true;
            }

            if (match.Type == "Object[]")
            {
                return false;
            }

            if (match.Type != "Object" || match.Properties is null)
            {
                return false;
            }

            level = match.Properties;
        }

        return false;
    }

    /// <summary>Gets the root property definitions.</summary>
    public IReadOnlyList<PropertyDef> Properties
    {
        get
        {
            return _root;
        }
    }

    #region private

    private readonly IReadOnlyList<PropertyDef> _root;

    private SchemaIndex(SchemaDocument schema)
    {
        _root = schema.Properties;
    }

    #endregion
}

/// <summary>
/// EasyJsonSchema validation and DateTime normalization.
/// </summary>
public sealed class SchemaGateway
{
    /// <summary>
    /// Initializes a new instance of the <see cref="SchemaGateway"/> class.
    /// </summary>
    /// <param name="schemas">The EasyJsonSchema library.</param>
    public SchemaGateway(IEasyJsonSchema schemas)
    {
        ArgumentNullException.ThrowIfNull(schemas);
        _schemas = schemas;
    }

    /// <summary>
    /// Validates a schema document.
    /// </summary>
    /// <param name="schemaJson">The schema JSON.</param>
    /// <param name="error">The EasyJsonSchema error, or empty.</param>
    /// <returns><see langword="true"/> when the schema is valid.</returns>
    public bool TryValidateSchema(string schemaJson, out string error)
    {
        return _schemas.ValidateEasyJsonSchema(schemaJson, out error);
    }

    /// <summary>
    /// Validates item JSON against a schema.
    /// </summary>
    /// <param name="schemaJson">The schema JSON.</param>
    /// <param name="dataJson">The item JSON.</param>
    /// <param name="error">The EasyJsonSchema error, or empty.</param>
    /// <returns><see langword="true"/> when the data is valid.</returns>
    public bool TryValidateData(string schemaJson, string dataJson, out string error)
    {
        if (!_schemas.DeserializeEasyJsonSchema(schemaJson, out var schema, out error) || schema is null)
        {
            return false;
        }

        return _schemas.ValidateJsonObject(schema, dataJson, out error);
    }

    /// <summary>
    /// Rewrites every DateTime value into <c>yyyy-MM-ddTHH:mm:ss.fffffffZ</c>.
    /// </summary>
    /// <param name="schemaJson">The schema the data was validated against.</param>
    /// <param name="dataJson">The item JSON.</param>
    /// <param name="normalized">The rewritten JSON.</param>
    /// <param name="error">The error when a DateTime value cannot be read.</param>
    /// <returns><see langword="true"/> when normalization succeeds.</returns>
    public bool TryNormalizeData(string schemaJson, string dataJson, out string normalized, out string error)
    {
        normalized = "";
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(dataJson);
        }
        catch (JsonException)
        {
            error = "$: malformed JSON";
            return false;
        }

        if (node is not JsonObject root)
        {
            error = "$: expected Object, found " + JsonKinds.OfNode(node);
            return false;
        }

        var index = SchemaIndex.Load(schemaJson);
        if (!RewriteObject(root, index.Properties, "$", out error))
        {
            return false;
        }

        normalized = root.ToJsonString();
        error = "";
        return true;
    }

    /// <summary>
    /// Converts a DateTime filter or stored value to the canonical UTC form.
    /// </summary>
    /// <param name="text">The ISO-8601 text.</param>
    /// <param name="canonical">The stored form.</param>
    /// <returns><see langword="false"/> when the text is not a date or date-time.</returns>
    public static bool TryCanonicalDateTime(string text, out string canonical)
    {
        canonical = "";
        if (text.Length == 0)
        {
            return false;
        }

        if (text.IndexOf('T') < 0 && text.IndexOf('t') < 0)
        {
            if (!DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            {
                return false;
            }

            canonical = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "T00:00:00.0000000Z";
            return true;
        }

        DateTimeOffset instant;
        if (HasZone(text))
        {
            if (!DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out instant))
            {
                return false;
            }
        }
        else if (!DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out instant))
        {
            return false;
        }

        canonical = instant.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);
        return true;
    }

    #region private

    private readonly IEasyJsonSchema _schemas;

    private static bool RewriteObject(JsonObject obj, IReadOnlyList<PropertyDef> properties, string path, out string error)
    {
        foreach (var property in obj)
        {
            PropertyDef? definition = null;
            foreach (var candidate in properties)
            {
                if (candidate.Name == property.Key)
                {
                    definition = candidate;
                    break;
                }
            }

            if (definition is null)
            {
                continue;
            }

            var childPath = path == "$" ? property.Key : path + "." + property.Key;
            if (!RewriteValue(property.Value, definition, childPath, out var replacement, out error))
            {
                return false;
            }

            if (!ReferenceEquals(replacement, property.Value))
            {
                obj[property.Key] = replacement;
            }
        }

        error = "";
        return true;
    }

    private static bool RewriteValue(JsonNode? node, PropertyDef definition, string path, out JsonNode? replacement, out string error)
    {
        replacement = node;
        error = "";
        if (definition.Type == "DateTime")
        {
            if (node is not JsonValue value || !value.TryGetValue<string>(out var text) || !TryCanonicalDateTime(text, out var canonical))
            {
                error = path + ": expected ISO-8601 date-time, e.g. 2026-11-19T14:22:05Z";
                return false;
            }

            replacement = canonical;
            return true;
        }

        if (definition.Type == "DateTime[]")
        {
            if (node is not JsonArray array)
            {
                error = path + ": expected DateTime[], found " + JsonKinds.OfNode(node);
                return false;
            }

            for (var index = 0; index < array.Count; index++)
            {
                if (array[index] is not JsonValue value
                    || !value.TryGetValue<string>(out var text)
                    || !TryCanonicalDateTime(text, out var canonical))
                {
                    error = path + "[" + index.ToString(CultureInfo.InvariantCulture) + "]: expected ISO-8601 date-time, e.g. 2026-11-19T14:22:05Z";
                    return false;
                }

                array[index] = canonical;
            }

            return true;
        }

        if (definition.Type == "Object" && node is JsonObject child && definition.Properties is not null)
        {
            return RewriteObject(child, definition.Properties, path, out error);
        }

        if (definition.Type == "Object[]" && node is JsonArray objects && definition.Properties is not null)
        {
            foreach (var element in objects)
            {
                if (element is JsonObject item && !RewriteObject(item, definition.Properties, path, out error))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static bool HasZone(string text)
    {
        if (text.EndsWith('Z') || text.EndsWith('z') || text.Contains('+'))
        {
            return true;
        }

        var time = text.IndexOf('T');
        if (time < 0)
        {
            time = text.IndexOf('t');
        }

        return time >= 0 && text[(time + 1)..].Contains('-');
    }

    #endregion
}

/// <summary>
/// JSON kind names, matching EasyJsonSchema.
/// </summary>
public static class JsonKinds
{
    /// <summary>
    /// Returns the kind of a <see cref="JsonElement"/>.
    /// </summary>
    /// <param name="element">The element.</param>
    /// <returns>The kind name.</returns>
    public static string Of(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Object => "Object",
            JsonValueKind.Array => "Array",
            JsonValueKind.String => "String",
            JsonValueKind.Number => "Number",
            JsonValueKind.True or JsonValueKind.False => "Boolean",
            JsonValueKind.Null => "Null",
            _ => "Undefined",
        };
    }

    /// <summary>
    /// Returns the kind of a <see cref="JsonNode"/>.
    /// </summary>
    /// <param name="node">The node.</param>
    /// <returns>The kind name.</returns>
    public static string OfNode(JsonNode? node)
    {
        return node switch
        {
            null => "Null",
            JsonObject => "Object",
            JsonArray => "Array",
            JsonValue value when value.TryGetValue<bool>(out _) => "Boolean",
            JsonValue value when value.TryGetValue<string>(out _) => "String",
            JsonValue => "Number",
            _ => "Undefined",
        };
    }
}
