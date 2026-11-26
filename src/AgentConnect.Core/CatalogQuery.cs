using System.Globalization;
using System.Text;
using System.Text.Json;

namespace AgentConnect;

/// <summary>Which FR-55 list a filter applies to.</summary>
public enum CatalogKind
{
    /// <summary>Workspaces the caller can see.</summary>
    Workspace,

    /// <summary>Lists in one workspace.</summary>
    List,

    /// <summary>Members of the tenant.</summary>
    TenantMember,

    /// <summary>Members who hold a role on one workspace.</summary>
    WorkspaceMember,
}

/// <summary>
/// A validated FR-55 filter. Role conditions on workspaces are the caller's role and are applied before the store query.
/// </summary>
public sealed class ParsedCatalog
{
    /// <summary>Gets the document-field conditions.</summary>
    public required IReadOnlyList<QueryCondition> Where { get; init; }

    /// <summary>Gets workspace <c>role</c> conditions, which use the caller's role and are not stored on the workspace.</summary>
    public required IReadOnlyList<QueryCondition> RoleWhere { get; init; }

    /// <summary>Gets the sort, or null for the FR-41 order.</summary>
    public QueryOrder? OrderBy { get; init; }

    /// <summary>Gets the canonical filter text used in continuation fingerprints.</summary>
    public required string Canonical { get; init; }

    /// <summary>Gets the field used when <see cref="OrderBy"/> is null.</summary>
    public required string DefaultSort { get; init; }
}

/// <summary>
/// A catalog query ready to run inside one partition.
/// </summary>
public sealed record CompiledCatalogQuery
{
    /// <summary>Gets the list kind.</summary>
    public required CatalogKind Kind { get; init; }

    /// <summary>Gets the tenant id.</summary>
    public required string TenantId { get; init; }

    /// <summary>Gets the workspace id for lists and workspace members.</summary>
    public string? WorkspaceId { get; init; }

    /// <summary>Gets the workspace ids to keep. Null means every live workspace in the tenant.</summary>
    public IReadOnlyList<string>? Ids { get; init; }

    /// <summary>Gets the document-field conditions.</summary>
    public required IReadOnlyList<QueryCondition> Where { get; init; }

    /// <summary>Gets the sort, or null for the default order.</summary>
    public QueryOrder? OrderBy { get; init; }

    /// <summary>Gets the default sort field.</summary>
    public required string DefaultSort { get; init; }

    /// <summary>Gets the page size.</summary>
    public required int PageSize { get; init; }

    /// <summary>Gets the store cursor, or null.</summary>
    public string? StoreCursor { get; init; }
}

/// <summary>
/// Parses FR-55 <c>where</c> and <c>orderBy</c> against a fixed field list. Error texts match FR-29.
/// </summary>
public static class CatalogQueryParser
{
    /// <summary>
    /// Parses a filter. <paramref name="unknownPaths"/> fail before the clauses are read.
    /// </summary>
    /// <param name="kind">The list kind.</param>
    /// <param name="where">The where value, or undefined.</param>
    /// <param name="orderBy">The orderBy value, or undefined.</param>
    /// <param name="unknownPaths">Unknown keys, in encounter order.</param>
    /// <returns>The filter, or the first error.</returns>
    public static OperationResult<ParsedCatalog> Parse(CatalogKind kind, JsonElement where, JsonElement orderBy, IReadOnlyList<string>? unknownPaths)
    {
        if (unknownPaths is { Count: > 0 })
        {
            return OperationResult<ParsedCatalog>.Fail(PlatformError.BadRequest(unknownPaths[0] + ": unknown field"));
        }

        var fields = Fields(kind);
        var conditions = new List<QueryCondition>();
        var roles = new List<QueryCondition>();
        if (where.ValueKind != JsonValueKind.Undefined && where.ValueKind != JsonValueKind.Null)
        {
            if (where.ValueKind != JsonValueKind.Array)
            {
                return OperationResult<ParsedCatalog>.Fail(PlatformError.BadRequest("where: expected array, found " + JsonKinds.Of(where)));
            }

            var index = 0;
            foreach (var element in where.EnumerateArray())
            {
                var parsed = TryCondition(fields, element, index);
                if (parsed.Error is not null)
                {
                    return OperationResult<ParsedCatalog>.Fail(parsed.Error);
                }

                if (parsed.Value!.Path == "role" && kind == CatalogKind.Workspace)
                {
                    roles.Add(parsed.Value);
                }
                else
                {
                    conditions.Add(parsed.Value);
                }

                index++;
            }
        }

        QueryOrder? order = null;
        if (orderBy.ValueKind != JsonValueKind.Undefined && orderBy.ValueKind != JsonValueKind.Null)
        {
            var parsed = TryOrder(fields, orderBy);
            if (parsed.Error is not null)
            {
                return OperationResult<ParsedCatalog>.Fail(parsed.Error);
            }

            order = parsed.Value;
        }

        return OperationResult<ParsedCatalog>.Ok(new ParsedCatalog
        {
            Where = conditions,
            RoleWhere = roles,
            OrderBy = order,
            Canonical = Canonical(conditions, roles, order),
            DefaultSort = DefaultSort(kind),
        });
    }

    /// <summary>
    /// Returns true when every condition matches <paramref name="stored"/>. Used for a caller's workspace role.
    /// </summary>
    /// <param name="stored">The stored string.</param>
    /// <param name="conditions">The conditions.</param>
    /// <returns><see langword="true"/> when every condition matches.</returns>
    public static bool MatchesAll(QueryValue stored, IReadOnlyList<QueryCondition> conditions)
    {
        foreach (var condition in conditions)
        {
            if (!CatalogEngine.ConditionMatches(stored, condition))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Builds a string query value. Email filters are trimmed and lower-cased.
    /// </summary>
    /// <param name="text">The text.</param>
    /// <param name="email">Whether the field is compared ignoring case.</param>
    /// <returns>The value.</returns>
    public static QueryValue Text(string text, bool email)
    {
        var stored = email ? text.Trim().ToLowerInvariant() : text;
        return new QueryValue
        {
            Kind = QueryValueKind.String,
            Text = stored,
            Canonical = JsonSerializer.Serialize(stored),
        };
    }

    #region private

    private static OperationResult<QueryCondition> TryCondition(IReadOnlyDictionary<string, CatalogField> fields, JsonElement element, int index)
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

        if (!hasValue || valueElement.ValueKind == JsonValueKind.Null)
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
        if (op is not ("eq" or "ne" or "lt" or "le" or "gt" or "ge" or "in" or "startsWith" or "contains"))
        {
            return OperationResult<QueryCondition>.Fail(PlatformError.BadRequest(prefix + ".op: unknown operator"));
        }

        var path = pathElement.GetString() ?? "";
        if (!fields.TryGetValue(path, out var field))
        {
            return OperationResult<QueryCondition>.Fail(PlatformError.BadRequest(prefix + ".path: unknown property"));
        }

        if (!OperatorAllowed(field.TypeName, op))
        {
            return OperationResult<QueryCondition>.Fail(PlatformError.BadRequest(prefix + ".op: not allowed for " + field.TypeName));
        }

        if (op == "in")
        {
            if (valueElement.ValueKind != JsonValueKind.Array || valueElement.GetArrayLength() == 0)
            {
                return OperationResult<QueryCondition>.Fail(PlatformError.BadRequest(prefix + ".value: expected non-empty array"));
            }

            var items = new List<QueryValue>();
            foreach (var item in valueElement.EnumerateArray())
            {
                if (!TryCoerce(item, field, out var literal, out var found))
                {
                    return OperationResult<QueryCondition>.Fail(PlatformError.BadRequest(prefix + ".value: expected " + field.TypeName + ", found " + found));
                }

                items.Add(literal);
            }

            return OperationResult<QueryCondition>.Ok(new QueryCondition
            {
                Path = path,
                Operator = op,
                TypeName = field.TypeName,
                Value = new QueryValue
                {
                    Kind = QueryValueKind.String,
                    Items = items,
                    Canonical = "[" + string.Join(",", items.Select(static item => item.Canonical)) + "]",
                },
            });
        }

        if (!TryCoerce(valueElement, field, out var value, out var kind))
        {
            return OperationResult<QueryCondition>.Fail(PlatformError.BadRequest(prefix + ".value: expected " + field.TypeName + ", found " + kind));
        }

        return OperationResult<QueryCondition>.Ok(new QueryCondition
        {
            Path = path,
            Operator = op,
            TypeName = field.TypeName,
            Value = value,
        });
    }

    private static OperationResult<QueryOrder> TryOrder(IReadOnlyDictionary<string, CatalogField> fields, JsonElement element)
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
        if (!fields.TryGetValue(path, out var field))
        {
            return OperationResult<QueryOrder>.Fail(PlatformError.BadRequest("orderBy.path: unknown property"));
        }

        if (!field.Sortable)
        {
            var text = field.Required ? "orderBy.path: not sortable" : "orderBy.path: must be a required property";
            return OperationResult<QueryOrder>.Fail(PlatformError.BadRequest(text));
        }

        return OperationResult<QueryOrder>.Ok(new QueryOrder
        {
            Path = path,
            TypeName = field.TypeName,
            Descending = descending,
        });
    }

    private static bool OperatorAllowed(string typeName, string op)
    {
        return typeName switch
        {
            "String" => op is "eq" or "ne" or "lt" or "le" or "gt" or "ge" or "in" or "startsWith" or "contains",
            "Boolean" => op is "eq" or "ne",
            "Int32" or "DateTime" => op is "eq" or "ne" or "lt" or "le" or "gt" or "ge" or "in",
            _ => false,
        };
    }

    private static bool TryCoerce(JsonElement element, CatalogField field, out QueryValue value, out string found)
    {
        value = new QueryValue
        {
            Kind = QueryValueKind.String,
            Canonical = "",
        };
        found = JsonKinds.Of(element);
        switch (field.TypeName)
        {
            case "String":
            {
                if (element.ValueKind != JsonValueKind.String)
                {
                    return false;
                }

                value = Text(element.GetString() ?? "", field.Email);
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

                if (!SchemaGateway.TryCanonicalDateTime(element.GetString() ?? "", out var canonical))
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
            case "Int32":
            {
                if (element.ValueKind != JsonValueKind.Number)
                {
                    return false;
                }

                var raw = element.GetRawText();
                if (raw.Contains('.') || raw.Contains('e') || raw.Contains('E') || !int.TryParse(raw, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var integer))
                {
                    return false;
                }

                value = new QueryValue
                {
                    Kind = QueryValueKind.Number,
                    Number = integer,
                    Canonical = integer.ToString(CultureInfo.InvariantCulture),
                };
                return true;
            }
            default:
            {
                return false;
            }
        }
    }

    private static string Canonical(IReadOnlyList<QueryCondition> where, IReadOnlyList<QueryCondition> roles, QueryOrder? order)
    {
        var builder = new StringBuilder();
        foreach (var condition in roles.Concat(where))
        {
            builder.Append("|w:").Append(condition.Path).Append(':').Append(condition.Operator).Append(':').Append(condition.Value.Canonical);
        }

        if (order is not null)
        {
            builder.Append("|o:").Append(order.Path).Append(':').Append(order.Descending ? "1" : "0");
        }

        return builder.ToString();
    }

    private static string DefaultSort(CatalogKind kind)
    {
        return kind switch
        {
            CatalogKind.TenantMember => "created",
            CatalogKind.WorkspaceMember => "email",
            _ => "updated",
        };
    }

    private static IReadOnlyDictionary<string, CatalogField> Fields(CatalogKind kind)
    {
        return kind switch
        {
            CatalogKind.Workspace => WorkspaceFields,
            CatalogKind.List => ListFields,
            CatalogKind.TenantMember => MemberFields,
            _ => WorkspaceMemberFields,
        };
    }

    private static readonly Dictionary<string, CatalogField> WorkspaceFields = new(StringComparer.Ordinal)
    {
        ["name"] = Field("name", "String", required: true, sortable: true, email: false),
        ["description"] = Field("description", "String", required: false, sortable: false, email: false),
        ["role"] = Field("role", "String", required: false, sortable: false, email: false),
        ["created"] = Field("created", "DateTime", required: true, sortable: true, email: false),
        ["updated"] = Field("updated", "DateTime", required: true, sortable: true, email: false),
    };

    private static readonly Dictionary<string, CatalogField> ListFields = new(StringComparer.Ordinal)
    {
        ["name"] = Field("name", "String", required: true, sortable: true, email: false),
        ["description"] = Field("description", "String", required: false, sortable: false, email: false),
        ["schemaVersion"] = Field("schemaVersion", "Int32", required: true, sortable: true, email: false),
        ["created"] = Field("created", "DateTime", required: true, sortable: true, email: false),
        ["updated"] = Field("updated", "DateTime", required: true, sortable: true, email: false),
    };

    private static readonly Dictionary<string, CatalogField> MemberFields = new(StringComparer.Ordinal)
    {
        ["email"] = Field("email", "String", required: true, sortable: true, email: true),
        ["isTenantOwner"] = Field("isTenantOwner", "Boolean", required: true, sortable: true, email: false),
        ["invitationSent"] = Field("invitationSent", "Boolean", required: true, sortable: true, email: false),
        ["created"] = Field("created", "DateTime", required: true, sortable: true, email: false),
    };

    private static readonly Dictionary<string, CatalogField> WorkspaceMemberFields = new(StringComparer.Ordinal)
    {
        ["email"] = Field("email", "String", required: true, sortable: true, email: true),
        ["role"] = Field("role", "String", required: true, sortable: false, email: false),
    };

    private static CatalogField Field(string name, string typeName, bool required, bool sortable, bool email)
    {
        return new CatalogField(name, typeName, required, sortable, email);
    }

    private sealed record CatalogField(string Name, string TypeName, bool Required, bool Sortable, bool Email);

    #endregion
}

/// <summary>
/// In-memory evaluation of an FR-55 query. The id tie-break follows the sort direction.
/// </summary>
public static class CatalogEngine
{
    /// <summary>
    /// Filters, sorts, and pages rows.
    /// </summary>
    /// <typeparam name="T">The document type.</typeparam>
    /// <param name="rows">The rows in one partition.</param>
    /// <param name="query">The query.</param>
    /// <param name="read">Reads one field. Returns false when the value is missing.</param>
    /// <returns>The page.</returns>
    public static ResultPage<T> Execute<T>(IReadOnlyList<T> rows, CompiledCatalogQuery query, Func<T, string, QueryValue?> read)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(read);
        var sortPath = query.OrderBy?.Path ?? query.DefaultSort;
        var descending = query.OrderBy?.Descending ?? false;
        var matched = new List<Row<T>>();
        foreach (var row in rows)
        {
            if (!WhereMatches(row, query.Where, read))
            {
                continue;
            }

            var sort = read(row, sortPath);
            if (sort is null)
            {
                continue;
            }

            var id = read(row, "id");
            matched.Add(new Row<T>(row, id?.Text ?? "", sort));
        }

        matched.Sort((left, right) => Compare(left.Sort, right.Sort, left.Id, right.Id, descending));
        CatalogCursor? cursor = null;
        if (query.StoreCursor is not null && !CatalogCursor.TryParse(query.StoreCursor, out cursor))
        {
            throw new InvalidContinuationException();
        }

        var page = new List<Row<T>>();
        foreach (var row in matched)
        {
            if (cursor is not null && Compare(row.Sort, cursor.Value, row.Id, cursor.Id, descending) <= 0)
            {
                continue;
            }

            if (page.Count == query.PageSize)
            {
                break;
            }

            page.Add(row);
        }

        string? continuation = null;
        if (page.Count == query.PageSize)
        {
            var end = page[^1];
            var more = false;
            foreach (var row in matched)
            {
                if (Compare(row.Sort, end.Sort, row.Id, end.Id, descending) > 0)
                {
                    more = true;
                    break;
                }
            }

            if (more)
            {
                continuation = new CatalogCursor(end.Id, end.Sort).Encode();
            }
        }

        return new ResultPage<T>
        {
            Items = page.Select(static row => row.Item).ToArray(),
            Continuation = continuation,
        };
    }

    /// <summary>
    /// Returns true when a stored value matches one condition.
    /// </summary>
    /// <param name="stored">The stored value.</param>
    /// <param name="condition">The condition.</param>
    /// <returns><see langword="true"/> when it matches.</returns>
    public static bool ConditionMatches(QueryValue stored, QueryCondition condition)
    {
        if (condition.Operator == "in")
        {
            return condition.Value.Items is not null && condition.Value.Items.Any(candidate => CompareValues(stored, candidate) == 0);
        }

        var comparison = CompareValues(stored, condition.Value);
        return condition.Operator switch
        {
            "eq" => comparison == 0,
            "ne" => comparison != 0,
            "lt" => comparison < 0,
            "le" => comparison <= 0,
            "gt" => comparison > 0,
            "ge" => comparison >= 0,
            "startsWith" => stored.Text is not null && condition.Value.Text is not null && stored.Text.StartsWith(condition.Value.Text, StringComparison.Ordinal),
            "contains" => stored.Text is not null && condition.Value.Text is not null && stored.Text.Contains(condition.Value.Text, StringComparison.Ordinal),
            _ => false,
        };
    }

    /// <summary>
    /// Formats a timestamp the same way Cosmos stores it, so string order matches time order.
    /// </summary>
    /// <param name="value">The timestamp.</param>
    /// <returns>The canonical UTC text.</returns>
    public static string CanonicalTime(DateTimeOffset value)
    {
        return value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Builds a string field value.
    /// </summary>
    /// <param name="text">The text.</param>
    /// <returns>The value.</returns>
    public static QueryValue String(string text)
    {
        return new QueryValue
        {
            Kind = QueryValueKind.String,
            Text = text,
            Canonical = "",
        };
    }

    /// <summary>
    /// Builds a boolean field value.
    /// </summary>
    /// <param name="flag">The flag.</param>
    /// <returns>The value.</returns>
    public static QueryValue Boolean(bool flag)
    {
        return new QueryValue
        {
            Kind = QueryValueKind.Boolean,
            Flag = flag,
            Canonical = "",
        };
    }

    /// <summary>
    /// Builds an integer field value.
    /// </summary>
    /// <param name="number">The number.</param>
    /// <returns>The value.</returns>
    public static QueryValue Number(int number)
    {
        return new QueryValue
        {
            Kind = QueryValueKind.Number,
            Number = number,
            Canonical = "",
        };
    }

    #region private

    private static bool WhereMatches<T>(T row, IReadOnlyList<QueryCondition> where, Func<T, string, QueryValue?> read)
    {
        foreach (var condition in where)
        {
            var stored = read(row, condition.Path);
            if (stored is null || !ConditionMatches(stored, condition))
            {
                return false;
            }
        }

        return true;
    }

    private static int Compare(QueryValue left, QueryValue right, string leftId, string rightId, bool descending)
    {
        var comparison = CompareValues(left, right);
        if (descending)
        {
            comparison = -comparison;
        }

        if (comparison != 0)
        {
            return comparison;
        }

        var id = string.CompareOrdinal(leftId, rightId);
        return descending ? -id : id;
    }

    private static int CompareValues(QueryValue left, QueryValue right)
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

    private sealed class Row<T>(T item, string id, QueryValue sort)
    {
        public T Item { get; } = item;

        public string Id { get; } = id;

        public QueryValue Sort { get; } = sort;
    }

    private sealed class CatalogCursor
    {
        public CatalogCursor(string id, QueryValue value)
        {
            Id = id;
            Value = value;
        }

        public string Id { get; }

        public QueryValue Value { get; }

        public string Encode()
        {
            return JsonSerializer.Serialize(new Wire(Id, Value.Kind.ToString(), Value.Text, Value.Number, Value.Flag));
        }

        public static bool TryParse(string text, out CatalogCursor cursor)
        {
            cursor = new CatalogCursor("", new QueryValue { Kind = QueryValueKind.String, Canonical = "" });
            try
            {
                var wire = JsonSerializer.Deserialize<Wire>(text);
                if (wire is null || string.IsNullOrEmpty(wire.Id) || !Enum.TryParse<QueryValueKind>(wire.Kind, out var kind))
                {
                    return false;
                }

                cursor = new CatalogCursor(wire.Id, new QueryValue
                {
                    Kind = kind,
                    Text = wire.Text,
                    Number = wire.Number,
                    Flag = wire.Flag,
                    Canonical = "",
                });
                return true;
            }
            catch (JsonException)
            {
                return false;
            }
        }

        private sealed record Wire(string Id, string Kind, string? Text, double Number, bool Flag);
    }

    #endregion
}
