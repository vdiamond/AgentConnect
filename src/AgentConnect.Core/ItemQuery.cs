using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AgentConnect;

/// <summary>
/// A value in a filter, already checked against the property type.
/// </summary>
public sealed class QueryValue
{
    /// <summary>Gets the value kind.</summary>
    public required QueryValueKind Kind { get; init; }

    /// <summary>Gets the string or canonical DateTime, when <see cref="Kind"/> is <see cref="QueryValueKind.String"/>.</summary>
    public string? Text { get; init; }

    /// <summary>Gets the number as a double, when <see cref="Kind"/> is <see cref="QueryValueKind.Number"/>.</summary>
    public double Number { get; init; }

    /// <summary>Gets the boolean, when <see cref="Kind"/> is <see cref="QueryValueKind.Boolean"/>.</summary>
    public bool Flag { get; init; }

    /// <summary>Gets the elements of an <c>in</c> list.</summary>
    public IReadOnlyList<QueryValue>? Items { get; init; }

    /// <summary>Gets a canonical encoding used in continuation fingerprints.</summary>
    public required string Canonical { get; init; }
}

/// <summary>
/// The kind of a filter value.
/// </summary>
public enum QueryValueKind
{
    /// <summary>A string or a canonical DateTime.</summary>
    String,

    /// <summary>A number compared as a double when the type is Int64 or Decimal.</summary>
    Number,

    /// <summary>A boolean.</summary>
    Boolean,
}

/// <summary>
/// One <c>where</c> condition.
/// </summary>
public sealed class QueryCondition
{
    /// <summary>Gets the path.</summary>
    public required string Path { get; init; }

    /// <summary>Gets the operator.</summary>
    public required string Operator { get; init; }

    /// <summary>Gets the EasyJsonSchema type name.</summary>
    public required string TypeName { get; init; }

    /// <summary>Gets the value.</summary>
    public required QueryValue Value { get; init; }
}

/// <summary>
/// An <c>orderBy</c> clause.
/// </summary>
public sealed class QueryOrder
{
    /// <summary>Gets the path.</summary>
    public required string Path { get; init; }

    /// <summary>Gets the EasyJsonSchema type name.</summary>
    public required string TypeName { get; init; }

    /// <summary>Gets a value indicating whether the sort is descending.</summary>
    public required bool Descending { get; init; }
}

/// <summary>
/// A validated item query ready to run inside one List partition.
/// </summary>
public sealed record CompiledItemQuery
{
    /// <summary>Gets the tenant id.</summary>
    public required string TenantId { get; init; }

    /// <summary>Gets the workspace id.</summary>
    public required string WorkspaceId { get; init; }

    /// <summary>Gets the list id.</summary>
    public required string ListId { get; init; }

    /// <summary>Gets <c>full</c> or <c>ids</c>.</summary>
    public required string Shape { get; init; }

    /// <summary>Gets the schema version to match, when set.</summary>
    public int? SchemaVersionEquals { get; init; }

    /// <summary>Gets a value indicating whether to return items below the list version.</summary>
    public bool BelowCurrent { get; init; }

    /// <summary>Gets the list's current schema version.</summary>
    public int CurrentSchemaVersion { get; init; }

    /// <summary>Gets the conditions. All of them must match.</summary>
    public required IReadOnlyList<QueryCondition> Where { get; init; }

    /// <summary>Gets the sort, or null for the default order.</summary>
    public QueryOrder? OrderBy { get; init; }

    /// <summary>Gets the page size carried by this query.</summary>
    public required int PageSize { get; init; }

    /// <summary>Gets the store cursor from a continuation token, or null.</summary>
    public string? StoreCursor { get; init; }

    /// <summary>Gets the fingerprint that binds a continuation token to this query.</summary>
    public required string Fingerprint { get; init; }
}

/// <summary>
/// Parses FR-29 query input into a <see cref="CompiledItemQuery"/>.
/// </summary>
public static class ItemQueryParser
{
    /// <summary>
    /// Parses a query. <paramref name="unknownPaths"/> are keys the caller already found that FR-29 does not define.
    /// </summary>
    /// <param name="schema">The list's current schema.</param>
    /// <param name="currentSchemaVersion">The list's current version.</param>
    /// <param name="tenantId">The tenant id.</param>
    /// <param name="workspaceId">The workspace id.</param>
    /// <param name="listId">The list id.</param>
    /// <param name="shape">The shape, or null when it was omitted.</param>
    /// <param name="schemaVersion">The schemaVersion value, or undefined.</param>
    /// <param name="where">The where value, or undefined.</param>
    /// <param name="orderBy">The orderBy value, or undefined.</param>
    /// <param name="unknownPaths">Unknown keys, in encounter order.</param>
    /// <param name="pageSize">The page size to store on the query.</param>
    /// <param name="storeCursor">The store cursor, or null.</param>
    /// <returns>The query, or the first error.</returns>
    public static OperationResult<CompiledItemQuery> Parse(
        SchemaIndex schema,
        int currentSchemaVersion,
        string tenantId,
        string workspaceId,
        string listId,
        string? shape,
        JsonElement schemaVersion,
        JsonElement where,
        JsonElement orderBy,
        IReadOnlyList<string> unknownPaths,
        int pageSize,
        string? storeCursor)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(unknownPaths);
        if (shape is null)
        {
            return OperationResult<CompiledItemQuery>.Fail(PlatformError.BadRequest("shape: required"));
        }

        if (shape is not ("full" or "ids"))
        {
            return OperationResult<CompiledItemQuery>.Fail(PlatformError.BadRequest("shape: must be full or ids"));
        }

        if (unknownPaths.Count > 0)
        {
            return OperationResult<CompiledItemQuery>.Fail(PlatformError.BadRequest(unknownPaths[0] + ": unknown field"));
        }

        int? equalsVersion = null;
        var below = false;
        if (schemaVersion.ValueKind != JsonValueKind.Undefined)
        {
            if (!TrySchemaVersion(schemaVersion, out equalsVersion, out below))
            {
                return OperationResult<CompiledItemQuery>.Fail(PlatformError.BadRequest("schemaVersion: invalid filter"));
            }
        }

        var conditions = new List<QueryCondition>();
        if (where.ValueKind != JsonValueKind.Undefined)
        {
            if (where.ValueKind != JsonValueKind.Array)
            {
                return OperationResult<CompiledItemQuery>.Fail(PlatformError.BadRequest("where: expected array, found " + JsonKinds.Of(where)));
            }

            var index = 0;
            foreach (var element in where.EnumerateArray())
            {
                var parsed = TryCondition(schema, element, index);
                if (parsed.Error is not null)
                {
                    return OperationResult<CompiledItemQuery>.Fail(parsed.Error);
                }

                conditions.Add(parsed.Value!);
                index++;
            }
        }

        QueryOrder? order = null;
        if (orderBy.ValueKind != JsonValueKind.Undefined)
        {
            var parsedOrder = TryOrder(schema, orderBy);
            if (parsedOrder.Error is not null)
            {
                return OperationResult<CompiledItemQuery>.Fail(parsedOrder.Error);
            }

            order = parsedOrder.Value;
        }

        var compiled = new CompiledItemQuery
        {
            TenantId = tenantId,
            WorkspaceId = workspaceId,
            ListId = listId,
            Shape = shape,
            SchemaVersionEquals = equalsVersion,
            BelowCurrent = below,
            CurrentSchemaVersion = currentSchemaVersion,
            Where = conditions,
            OrderBy = order,
            PageSize = pageSize,
            StoreCursor = storeCursor,
            Fingerprint = "",
        };
        return OperationResult<CompiledItemQuery>.Ok(compiled with { Fingerprint = Fingerprint(compiled) });
    }

    #region private

    private static readonly HashSet<string> Operators = new(StringComparer.Ordinal)
    {
        "eq",
        "ne",
        "lt",
        "le",
        "gt",
        "ge",
        "in",
        "startsWith",
        "contains",
    };

    private static bool TrySchemaVersion(JsonElement element, out int? equalsVersion, out bool below)
    {
        equalsVersion = null;
        below = false;
        if (element.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var count = 0;
        int? eq = null;
        bool? belowValue = null;
        foreach (var property in element.EnumerateObject())
        {
            count++;
            if (property.NameEquals("eq"))
            {
                if (!TryWholeNumber(property.Value, out var number) || number < 1)
                {
                    return false;
                }

                eq = number;
            }
            else if (property.NameEquals("belowCurrent"))
            {
                if (property.Value.ValueKind != JsonValueKind.True)
                {
                    return false;
                }

                belowValue = true;
            }
            else
            {
                return false;
            }
        }

        if (count != 1)
        {
            return false;
        }

        if (eq is int version)
        {
            equalsVersion = version;
            return true;
        }

        below = belowValue == true;
        return below;
    }

    private static OperationResult<QueryCondition> TryCondition(SchemaIndex schema, JsonElement element, int index)
    {
        var prefix = "where[" + index.ToString(CultureInfo.InvariantCulture) + "]";
        if (element.ValueKind != JsonValueKind.Object)
        {
            return OperationResult<QueryCondition>.Fail(PlatformError.BadRequest(prefix + ": expected Object, found " + JsonKinds.Of(element)));
        }

        JsonElement pathElement = default;
        JsonElement opElement = default;
        JsonElement valueElement = default;
        var hasPath = false;
        var hasOp = false;
        var hasValue = false;
        string? unknown = null;
        foreach (var property in element.EnumerateObject())
        {
            switch (property.Name)
            {
                case "path":
                {
                    hasPath = true;
                    pathElement = property.Value;
                    break;
                }
                case "op":
                {
                    hasOp = true;
                    opElement = property.Value;
                    break;
                }
                case "value":
                {
                    hasValue = true;
                    valueElement = property.Value;
                    break;
                }
                default:
                {
                    unknown ??= prefix + "." + property.Name;
                    break;
                }
            }
        }

        if (!hasPath || pathElement.ValueKind == JsonValueKind.Null)
        {
            return OperationResult<QueryCondition>.Fail(PlatformError.BadRequest(prefix + ".path: required"));
        }

        if (!hasOp || opElement.ValueKind == JsonValueKind.Null)
        {
            return OperationResult<QueryCondition>.Fail(PlatformError.BadRequest(prefix + ".op: required"));
        }

        if (!hasValue)
        {
            return OperationResult<QueryCondition>.Fail(PlatformError.BadRequest(prefix + ".value: required"));
        }

        if (unknown is not null)
        {
            return OperationResult<QueryCondition>.Fail(PlatformError.BadRequest(unknown + ": unknown field"));
        }

        if (pathElement.ValueKind != JsonValueKind.String)
        {
            return OperationResult<QueryCondition>.Fail(PlatformError.BadRequest(prefix + ".path: expected String, found " + JsonKinds.Of(pathElement)));
        }

        if (opElement.ValueKind != JsonValueKind.String)
        {
            return OperationResult<QueryCondition>.Fail(PlatformError.BadRequest(prefix + ".op: expected String, found " + JsonKinds.Of(opElement)));
        }

        var op = opElement.GetString() ?? "";
        if (!Operators.Contains(op))
        {
            return OperationResult<QueryCondition>.Fail(PlatformError.BadRequest(prefix + ".op: unknown operator"));
        }

        var path = pathElement.GetString() ?? "";
        if (!schema.TryResolve(path, out var resolved))
        {
            return OperationResult<QueryCondition>.Fail(PlatformError.BadRequest(prefix + ".path: unknown property"));
        }

        if (!OperatorAllowed(resolved, op))
        {
            return OperationResult<QueryCondition>.Fail(PlatformError.BadRequest(prefix + ".op: not allowed for " + resolved.TypeName));
        }

        var valueType = resolved.IsArray ? resolved.ElementType : resolved.TypeName;
        if (op == "in")
        {
            if (valueElement.ValueKind != JsonValueKind.Array || valueElement.GetArrayLength() == 0)
            {
                return OperationResult<QueryCondition>.Fail(PlatformError.BadRequest(prefix + ".value: expected non-empty array"));
            }

            var items = new List<QueryValue>();
            foreach (var item in valueElement.EnumerateArray())
            {
                if (!TryCoerce(item, valueType, out var literal, out var found))
                {
                    return OperationResult<QueryCondition>.Fail(PlatformError.BadRequest(prefix + ".value: expected " + valueType + ", found " + found));
                }

                items.Add(literal);
            }

            var canonical = string.Join(",", items.Select(static item => item.Canonical));
            return OperationResult<QueryCondition>.Ok(new QueryCondition
            {
                Path = path,
                Operator = op,
                TypeName = resolved.TypeName,
                Value = new QueryValue
                {
                    Kind = QueryValueKind.String,
                    Items = items,
                    Canonical = "[" + canonical + "]",
                },
            });
        }

        if (!TryCoerce(valueElement, valueType, out var value, out var kind))
        {
            return OperationResult<QueryCondition>.Fail(PlatformError.BadRequest(prefix + ".value: expected " + valueType + ", found " + kind));
        }

        return OperationResult<QueryCondition>.Ok(new QueryCondition
        {
            Path = path,
            Operator = op,
            TypeName = resolved.TypeName,
            Value = value,
        });
    }

    private static OperationResult<QueryOrder> TryOrder(SchemaIndex schema, JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return OperationResult<QueryOrder>.Fail(PlatformError.BadRequest("orderBy: expected Object, found " + JsonKinds.Of(element)));
        }

        JsonElement pathElement = default;
        var hasPath = false;
        var descending = false;
        string? unknown = null;
        foreach (var property in element.EnumerateObject())
        {
            switch (property.Name)
            {
                case "path":
                {
                    hasPath = true;
                    pathElement = property.Value;
                    break;
                }
                case "desc":
                {
                    if (property.Value.ValueKind is not JsonValueKind.True and not JsonValueKind.False)
                    {
                        return OperationResult<QueryOrder>.Fail(PlatformError.BadRequest("orderBy.desc: expected Boolean, found " + JsonKinds.Of(property.Value)));
                    }

                    descending = property.Value.GetBoolean();
                    break;
                }
                default:
                {
                    unknown ??= "orderBy." + property.Name;
                    break;
                }
            }
        }

        if (!hasPath || pathElement.ValueKind == JsonValueKind.Null)
        {
            return OperationResult<QueryOrder>.Fail(PlatformError.BadRequest("orderBy.path: required"));
        }

        if (unknown is not null)
        {
            return OperationResult<QueryOrder>.Fail(PlatformError.BadRequest(unknown + ": unknown field"));
        }

        if (pathElement.ValueKind != JsonValueKind.String)
        {
            return OperationResult<QueryOrder>.Fail(PlatformError.BadRequest("orderBy.path: expected String, found " + JsonKinds.Of(pathElement)));
        }

        var path = pathElement.GetString() ?? "";
        if (!schema.TryResolve(path, out var resolved))
        {
            return OperationResult<QueryOrder>.Fail(PlatformError.BadRequest("orderBy.path: unknown property"));
        }

        if (!resolved.RequiredAlongPath)
        {
            return OperationResult<QueryOrder>.Fail(PlatformError.BadRequest("orderBy.path: must be a required property"));
        }

        if (resolved.IsArray || resolved.IsObject)
        {
            return OperationResult<QueryOrder>.Fail(PlatformError.BadRequest("orderBy.path: cannot sort on " + resolved.TypeName));
        }

        return OperationResult<QueryOrder>.Ok(new QueryOrder
        {
            Path = path,
            TypeName = resolved.TypeName,
            Descending = descending,
        });
    }

    private static bool OperatorAllowed(ResolvedProperty property, string op)
    {
        if (property.IsArray)
        {
            return property.TypeName != "Object[]" && op == "contains";
        }

        return property.TypeName switch
        {
            "String" => op is "eq" or "ne" or "lt" or "le" or "gt" or "ge" or "in" or "startsWith" or "contains",
            "Boolean" => op is "eq" or "ne",
            "Byte" or "Int16" or "Int32" or "Int64" or "Single" or "Double" or "Decimal" or "DateTime" => op is "eq" or "ne" or "lt" or "le" or "gt" or "ge" or "in",
            _ => false,
        };
    }

    private static bool TryCoerce(JsonElement element, string typeName, out QueryValue value, out string found)
    {
        value = new QueryValue
        {
            Kind = QueryValueKind.String,
            Canonical = "",
        };
        found = JsonKinds.Of(element);
        switch (typeName)
        {
            case "String":
            {
                if (element.ValueKind != JsonValueKind.String)
                {
                    return false;
                }

                var text = element.GetString() ?? "";
                value = new QueryValue
                {
                    Kind = QueryValueKind.String,
                    Text = text,
                    Canonical = JsonSerializer.Serialize(text),
                };
                return true;
            }
            case "Boolean":
            {
                if (element.ValueKind is not JsonValueKind.True and not JsonValueKind.False)
                {
                    return false;
                }

                var flag = element.GetBoolean();
                value = new QueryValue
                {
                    Kind = QueryValueKind.Boolean,
                    Flag = flag,
                    Canonical = flag ? "true" : "false",
                };
                return true;
            }
            case "DateTime":
            {
                if (element.ValueKind != JsonValueKind.String)
                {
                    return false;
                }

                var text = element.GetString() ?? "";
                if (!SchemaGateway.TryCanonicalDateTime(text, out var canonical))
                {
                    return false;
                }

                value = new QueryValue
                {
                    Kind = QueryValueKind.String,
                    Text = canonical,
                    Canonical = JsonSerializer.Serialize(canonical),
                };
                return true;
            }
            case "Byte" or "Int16" or "Int32" or "Int64":
            {
                if (!TryInteger(element, typeName, out var number))
                {
                    return false;
                }

                value = new QueryValue
                {
                    Kind = QueryValueKind.Number,
                    Number = number,
                    Canonical = number.ToString("R", CultureInfo.InvariantCulture),
                };
                return true;
            }
            case "Single" or "Double" or "Decimal":
            {
                if (element.ValueKind != JsonValueKind.Number || !element.TryGetDouble(out var number) || !double.IsFinite(number))
                {
                    return false;
                }

                value = new QueryValue
                {
                    Kind = QueryValueKind.Number,
                    Number = number,
                    Canonical = number.ToString("R", CultureInfo.InvariantCulture),
                };
                return true;
            }
            default:
            {
                return false;
            }
        }
    }

    private static bool TryInteger(JsonElement element, string typeName, out double number)
    {
        number = 0;
        if (element.ValueKind != JsonValueKind.Number)
        {
            return false;
        }

        var raw = element.GetRawText();
        if (raw.Contains('.') || raw.Contains('e') || raw.Contains('E'))
        {
            return false;
        }

        if (!long.TryParse(raw, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var integer))
        {
            return false;
        }

        var inRange = typeName switch
        {
            "Byte" => integer is >= 0 and <= 255,
            "Int16" => integer is >= short.MinValue and <= short.MaxValue,
            "Int32" => integer is >= int.MinValue and <= int.MaxValue,
            "Int64" => true,
            _ => false,
        };
        if (!inRange)
        {
            return false;
        }

        number = integer;
        return true;
    }

    private static bool TryWholeNumber(JsonElement element, out int value)
    {
        value = 0;
        if (element.ValueKind != JsonValueKind.Number)
        {
            return false;
        }

        var raw = element.GetRawText();
        if (raw.Contains('.') || raw.Contains('e') || raw.Contains('E') || raw.StartsWith('-'))
        {
            return false;
        }

        return element.TryGetInt32(out value);
    }

    private static string Fingerprint(CompiledItemQuery query)
    {
        var builder = new StringBuilder();
        builder.Append("items|").Append(query.TenantId).Append('|').Append(query.WorkspaceId).Append('|').Append(query.ListId);
        builder.Append("|shape=").Append(query.Shape);
        builder.Append("|sv=");
        if (query.SchemaVersionEquals is int equals)
        {
            builder.Append("eq:").Append(equals.ToString(CultureInfo.InvariantCulture));
        }
        else if (query.BelowCurrent)
        {
            builder.Append("below");
        }

        foreach (var condition in query.Where)
        {
            builder.Append("|w:").Append(condition.Path).Append(':').Append(condition.Operator).Append(':').Append(condition.Value.Canonical);
        }

        if (query.OrderBy is QueryOrder order)
        {
            builder.Append("|o:").Append(order.Path).Append(':').Append(order.Descending ? "1" : "0");
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    #endregion
}

/// <summary>
/// Continuation tokens. The token keeps the page size and is bound to one query.
/// </summary>
public static class ContinuationTokens
{
    /// <summary>
    /// Builds a continuation token.
    /// </summary>
    /// <param name="fingerprint">The query fingerprint.</param>
    /// <param name="pageSize">The page size of the query that issued the token.</param>
    /// <param name="storeCursor">The store cursor.</param>
    /// <returns>The opaque token.</returns>
    public static string Issue(string fingerprint, int pageSize, string storeCursor)
    {
        return Issue(fingerprint, pageSize, storeCursor, 1);
    }

    /// <summary>
    /// Builds a continuation token that also carries the next 1-based row number. FR-57.
    /// </summary>
    /// <param name="fingerprint">The query fingerprint.</param>
    /// <param name="pageSize">The page size of the query that issued the token.</param>
    /// <param name="storeCursor">The store cursor.</param>
    /// <param name="nextRow">The 1-based row number of the first entry on the next page.</param>
    /// <returns>The opaque token.</returns>
    public static string Issue(string fingerprint, int pageSize, string storeCursor, int nextRow)
    {
        var bounded = pageSize > PageRules.PlatformPageSizeMax ? PageRules.PlatformPageSizeMax : pageSize;
        var row = nextRow < 1 ? 1 : nextRow;
        var envelope = new Envelope(1, fingerprint, bounded, storeCursor, row, "");
        envelope = envelope with { M = Mac(envelope) };
        var json = JsonSerializer.Serialize(envelope);
        return Base64Url(Encoding.UTF8.GetBytes(json));
    }

    /// <summary>
    /// Installs the key that signs continuation tokens. Called when the token encryption key is loaded.
    /// </summary>
    /// <param name="encryptionKeyBase64">The token encryption key.</param>
    public static void Configure(string encryptionKeyBase64)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(encryptionKeyBase64);
        if (_key is not null)
        {
            return;
        }

        _key = SHA256.HashData(Encoding.UTF8.GetBytes("agentconnect-continuation\n" + encryptionKeyBase64));
    }

    /// <summary>
    /// Opens a continuation token for a query.
    /// </summary>
    /// <param name="token">The token.</param>
    /// <param name="fingerprint">The fingerprint of the query it is being used with.</param>
    /// <param name="pageSize">The page size stored in the token.</param>
    /// <param name="storeCursor">The store cursor.</param>
    /// <returns><see langword="false"/> when the token is malformed or belongs to another query.</returns>
    public static bool TryOpen(string token, string fingerprint, out int pageSize, out string storeCursor)
    {
        return TryOpen(token, fingerprint, out pageSize, out storeCursor, out _);
    }

    /// <summary>
    /// Opens a continuation token for a query, including the next row number.
    /// </summary>
    /// <param name="token">The token.</param>
    /// <param name="fingerprint">The fingerprint of the query it is being used with.</param>
    /// <param name="pageSize">The page size stored in the token.</param>
    /// <param name="storeCursor">The store cursor.</param>
    /// <param name="nextRow">The 1-based row number of the first entry on this page.</param>
    /// <returns><see langword="false"/> when the token is malformed or belongs to another query.</returns>
    public static bool TryOpen(string token, string fingerprint, out int pageSize, out string storeCursor, out int nextRow)
    {
        pageSize = 0;
        storeCursor = "";
        nextRow = 1;
        byte[] bytes;
        try
        {
            bytes = FromBase64Url(token);
        }
        catch (FormatException)
        {
            return false;
        }

        Envelope? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<Envelope>(bytes);
        }
        catch (JsonException)
        {
            return false;
        }

        if (envelope is null || envelope.V != 1 || envelope.P < 1 || envelope.R < 1 || string.IsNullOrEmpty(envelope.C) || string.IsNullOrEmpty(envelope.H) || string.IsNullOrEmpty(envelope.M))
        {
            return false;
        }

        if (!MacMatches(envelope))
        {
            return false;
        }

        var actual = SHA256.HashData(Encoding.UTF8.GetBytes(envelope.H));
        var expected = SHA256.HashData(Encoding.UTF8.GetBytes(fingerprint));
        if (!CryptographicOperations.FixedTimeEquals(actual, expected))
        {
            return false;
        }

        pageSize = envelope.P > PageRules.PlatformPageSizeMax ? PageRules.PlatformPageSizeMax : envelope.P;
        storeCursor = envelope.C;
        nextRow = envelope.R;
        return true;
    }

    #region private

    private static byte[]? _key;

    private sealed record Envelope(int V, string H, int P, string C, int R, string M);

    private static string Mac(Envelope envelope)
    {
        if (_key is null)
        {
            throw new InvalidOperationException("Continuation tokens are not configured.");
        }

        var payload = Encoding.UTF8.GetBytes(envelope.V.ToString(CultureInfo.InvariantCulture) + "|" + envelope.H + "|" + envelope.P.ToString(CultureInfo.InvariantCulture) + "|" + envelope.C + "|" + envelope.R.ToString(CultureInfo.InvariantCulture));
        return Convert.ToHexString(HMACSHA256.HashData(_key, payload));
    }

    private static bool MacMatches(Envelope envelope)
    {
        if (_key is null)
        {
            return false;
        }

        byte[] actual;
        byte[] expected;
        try
        {
            actual = Convert.FromHexString(envelope.M);
            expected = Convert.FromHexString(Mac(envelope));
        }
        catch (FormatException)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    private static string Base64Url(byte[] bytes)
    {
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static byte[] FromBase64Url(string text)
    {
        var padded = text.Replace('-', '+').Replace('_', '/');
        switch (padded.Length % 4)
        {
            case 2:
            {
                padded += "==";
                break;
            }
            case 3:
            {
                padded += "=";
                break;
            }
            case 0:
            {
                break;
            }
            default:
            {
                throw new FormatException();
            }
        }

        return Convert.FromBase64String(padded);
    }

    #endregion
}

/// <summary>
/// In-memory evaluation of a compiled item query. Cosmos DB runs the same predicate as SQL.
/// </summary>
public static class ItemQueryEngine
{
    /// <summary>
    /// Filters, sorts, and pages items.
    /// </summary>
    /// <param name="items">The items in one list partition.</param>
    /// <param name="query">The query.</param>
    /// <returns>The page.</returns>
    public static ResultPage<ItemDocument> Execute(IReadOnlyList<ItemDocument> items, CompiledItemQuery query)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(query);
        var matched = new List<ItemDocument>();
        foreach (var item in items)
        {
            if (!VersionMatches(item, query))
            {
                continue;
            }

            if (!WhereMatches(item, query))
            {
                continue;
            }

            if (query.OrderBy is not null && !TryReadSortValue(item, query.OrderBy, out _))
            {
                continue;
            }

            matched.Add(item);
        }

        matched.Sort((left, right) => CompareItems(left, right, query));
        StoreCursor? cursor = null;
        if (query.StoreCursor is not null && !StoreCursor.TryParse(query.StoreCursor, out cursor))
        {
            throw new InvalidContinuationException();
        }

        var page = new List<ItemDocument>();
        ItemDocument? last = null;
        foreach (var item in matched)
        {
            if (cursor is not null && CompareToCursor(item, cursor, query) <= 0)
            {
                continue;
            }

            if (page.Count == query.PageSize)
            {
                last = page[^1];
                break;
            }

            page.Add(item);
        }

        string? continuation = null;
        if (last is not null)
        {
            continuation = StoreCursor.From(last, query).Encode();
        }
        else if (page.Count == query.PageSize)
        {
            var more = false;
            var end = StoreCursor.From(page[^1], query);
            foreach (var item in matched)
            {
                if (CompareToCursor(item, end, query) > 0)
                {
                    more = true;
                    break;
                }
            }

            if (more)
            {
                continuation = end.Encode();
            }
        }

        return new ResultPage<ItemDocument>
        {
            Items = page,
            Continuation = continuation,
        };
    }

    #region private

    private static bool VersionMatches(ItemDocument item, CompiledItemQuery query)
    {
        if (query.SchemaVersionEquals is int equals)
        {
            return item.SchemaVersion == equals;
        }

        if (query.BelowCurrent)
        {
            return item.SchemaVersion < query.CurrentSchemaVersion;
        }

        return true;
    }

    private static bool WhereMatches(ItemDocument item, CompiledItemQuery query)
    {
        foreach (var condition in query.Where)
        {
            if (!ConditionMatches(item, condition))
            {
                return false;
            }
        }

        return true;
    }

    private static bool ConditionMatches(ItemDocument item, QueryCondition condition)
    {
        if (!TryGetElement(item.Data, condition.Path, out var element) || element.ValueKind == JsonValueKind.Null)
        {
            return false;
        }

        if (condition.TypeName.EndsWith("[]", StringComparison.Ordinal))
        {
            if (element.ValueKind != JsonValueKind.Array || condition.Value.Items is not null)
            {
                return false;
            }

            var elementType = condition.TypeName[..^2];
            foreach (var entry in element.EnumerateArray())
            {
                if (TryReadLiteral(entry, elementType, out var literal) && Same(literal, condition.Value))
                {
                    return true;
                }
            }

            return false;
        }

        if (!TryReadLiteral(element, condition.TypeName, out var stored))
        {
            return false;
        }

        var comparison = Compare(stored, condition.Value);
        return condition.Operator switch
        {
            "eq" => comparison == 0,
            "ne" => comparison != 0,
            "lt" => comparison < 0,
            "le" => comparison <= 0,
            "gt" => comparison > 0,
            "ge" => comparison >= 0,
            "startsWith" => stored.Kind == QueryValueKind.String && condition.Value.Text is not null && stored.Text is not null && stored.Text.StartsWith(condition.Value.Text, StringComparison.Ordinal),
            "contains" => stored.Kind == QueryValueKind.String && condition.Value.Text is not null && stored.Text is not null && stored.Text.Contains(condition.Value.Text, StringComparison.Ordinal),
            "in" => condition.Value.Items is not null && condition.Value.Items.Any(candidate => Same(stored, candidate)),
            _ => false,
        };
    }

    private static int CompareItems(ItemDocument left, ItemDocument right, CompiledItemQuery query)
    {
        if (query.OrderBy is QueryOrder order
            && TryReadSortValue(left, order, out var leftValue)
            && TryReadSortValue(right, order, out var rightValue))
        {
            var comparison = Compare(leftValue, rightValue);
            if (order.Descending)
            {
                comparison = -comparison;
            }

            if (comparison != 0)
            {
                return comparison;
            }
        }
        else if (query.OrderBy is null)
        {
            var comparison = left.Updated.CompareTo(right.Updated);
            if (comparison != 0)
            {
                return comparison;
            }
        }

        return string.CompareOrdinal(left.Id, right.Id);
    }

    private static int CompareToCursor(ItemDocument item, StoreCursor cursor, CompiledItemQuery query)
    {
        if (cursor.Mode == "time")
        {
            var comparison = item.Updated.UtcTicks.CompareTo(cursor.Ticks);
            if (comparison != 0)
            {
                return comparison;
            }

            return string.CompareOrdinal(item.Id, cursor.Id);
        }

        if (query.OrderBy is null || !TryReadSortValue(item, query.OrderBy, out var value))
        {
            return 1;
        }

        var primary = Compare(value, cursor.Value!);
        if (query.OrderBy.Descending)
        {
            primary = -primary;
        }

        if (primary != 0)
        {
            return primary;
        }

        return string.CompareOrdinal(item.Id, cursor.Id);
    }

    private static bool TryReadSortValue(ItemDocument item, QueryOrder order, out QueryValue value)
    {
        value = new QueryValue
        {
            Kind = QueryValueKind.String,
            Canonical = "",
        };
        return TryGetElement(item.Data, order.Path, out var element)
            && element.ValueKind != JsonValueKind.Null
            && TryReadLiteral(element, order.TypeName, out value);
    }

    private static bool TryGetElement(JsonElement data, string path, out JsonElement element)
    {
        element = data;
        if (data.ValueKind != JsonValueKind.Object)
        {
            element = default;
            return false;
        }

        var current = data;
        foreach (var segment in path.Split('.'))
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out current))
            {
                element = default;
                return false;
            }
        }

        element = current.Clone();
        return true;
    }

    private static bool TryReadLiteral(JsonElement element, string typeName, out QueryValue value)
    {
        value = new QueryValue
        {
            Kind = QueryValueKind.String,
            Canonical = "",
        };
        switch (typeName)
        {
            case "String":
            {
                if (element.ValueKind != JsonValueKind.String)
                {
                    return false;
                }

                value = new QueryValue
                {
                    Kind = QueryValueKind.String,
                    Text = element.GetString() ?? "",
                    Canonical = "",
                };
                return true;
            }
            case "Boolean":
            {
                if (element.ValueKind is not JsonValueKind.True and not JsonValueKind.False)
                {
                    return false;
                }

                value = new QueryValue
                {
                    Kind = QueryValueKind.Boolean,
                    Flag = element.GetBoolean(),
                    Canonical = "",
                };
                return true;
            }
            case "DateTime":
            {
                if (element.ValueKind != JsonValueKind.String)
                {
                    return false;
                }

                var text = element.GetString() ?? "";
                if (!SchemaGateway.TryCanonicalDateTime(text, out var canonical))
                {
                    return false;
                }

                value = new QueryValue
                {
                    Kind = QueryValueKind.String,
                    Text = canonical,
                    Canonical = "",
                };
                return true;
            }
            case "Byte" or "Int16" or "Int32" or "Int64" or "Single" or "Double" or "Decimal":
            {
                if (element.ValueKind != JsonValueKind.Number || !element.TryGetDouble(out var number) || !double.IsFinite(number))
                {
                    return false;
                }

                if (typeName is "Byte" or "Int16" or "Int32" or "Int64")
                {
                    var raw = element.GetRawText();
                    if (raw.Contains('.') || raw.Contains('e') || raw.Contains('E'))
                    {
                        return false;
                    }
                }

                value = new QueryValue
                {
                    Kind = QueryValueKind.Number,
                    Number = typeName is "Int64" or "Decimal" ? number : number,
                    Canonical = "",
                };
                return true;
            }
            default:
            {
                return false;
            }
        }
    }

    private static int Compare(QueryValue left, QueryValue right)
    {
        if (left.Kind == QueryValueKind.Number && right.Kind == QueryValueKind.Number)
        {
            return left.Number.CompareTo(right.Number);
        }

        if (left.Kind == QueryValueKind.Boolean && right.Kind == QueryValueKind.Boolean)
        {
            return left.Flag.CompareTo(right.Flag);
        }

        return string.CompareOrdinal(left.Text, right.Text);
    }

    private static bool Same(QueryValue left, QueryValue right)
    {
        return Compare(left, right) == 0;
    }

    private sealed class StoreCursor
    {
        public string Mode { get; init; } = "";

        public string Id { get; init; } = "";

        public long Ticks { get; init; }

        public QueryValue? Value { get; init; }

        public static StoreCursor From(ItemDocument item, CompiledItemQuery query)
        {
            if (query.OrderBy is null)
            {
                return new StoreCursor
                {
                    Mode = "time",
                    Id = item.Id,
                    Ticks = item.Updated.UtcTicks,
                };
            }

            TryReadSortValue(item, query.OrderBy, out var value);
            return new StoreCursor
            {
                Mode = "value",
                Id = item.Id,
                Value = value,
            };
        }

        public string Encode()
        {
            if (Mode == "time")
            {
                return JsonSerializer.Serialize(new TimeCursor(Mode, Id, Ticks));
            }

            return JsonSerializer.Serialize(new ValueCursor(Mode, Id, Value!.Kind.ToString(), Value.Text, Value.Number, Value.Flag));
        }

        public static bool TryParse(string text, out StoreCursor cursor)
        {
            cursor = new StoreCursor();
            try
            {
                using var document = JsonDocument.Parse(text);
                var root = document.RootElement;
                var mode = root.GetProperty("Mode").GetString() ?? root.GetProperty("mode").GetString();
                if (mode == "time")
                {
                    cursor = new StoreCursor
                    {
                        Mode = "time",
                        Id = root.TryGetProperty("Id", out var id) ? id.GetString() ?? "" : root.GetProperty("id").GetString() ?? "",
                        Ticks = root.TryGetProperty("Ticks", out var ticks) ? ticks.GetInt64() : root.GetProperty("ticks").GetInt64(),
                    };
                    return cursor.Id.Length > 0;
                }

                var kindText = root.TryGetProperty("Kind", out var kindElement) ? kindElement.GetString() : root.GetProperty("kind").GetString();
                var kind = Enum.Parse<QueryValueKind>(kindText ?? "", ignoreCase: false);
                cursor = new StoreCursor
                {
                    Mode = "value",
                    Id = root.TryGetProperty("Id", out var valueId) ? valueId.GetString() ?? "" : root.GetProperty("id").GetString() ?? "",
                    Value = new QueryValue
                    {
                        Kind = kind,
                        Text = root.TryGetProperty("Text", out var textElement) ? textElement.GetString() : root.TryGetProperty("text", out var camelText) ? camelText.GetString() : null,
                        Number = root.TryGetProperty("Number", out var number) ? number.GetDouble() : root.TryGetProperty("number", out var camelNumber) ? camelNumber.GetDouble() : 0,
                        Flag = root.TryGetProperty("Flag", out var flag) ? flag.GetBoolean() : root.TryGetProperty("flag", out var camelFlag) && camelFlag.GetBoolean(),
                        Canonical = "",
                    },
                };
                return cursor.Id.Length > 0;
            }
            catch (Exception exception) when (exception is JsonException or KeyNotFoundException or FormatException or InvalidOperationException)
            {
                return false;
            }
        }

        private sealed record TimeCursor(string Mode, string Id, long Ticks);

        private sealed record ValueCursor(string Mode, string Id, string Kind, string? Text, double Number, bool Flag);
    }

    #endregion
}
