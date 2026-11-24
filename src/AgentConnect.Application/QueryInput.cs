using System.Text.Json;

namespace AgentConnect;

/// <summary>
/// Reads the REST <c>query</c> parameter.
/// </summary>
public static class QueryInput
{
    /// <summary>
    /// Parses the REST <c>query</c> parameter into the FR-29 fields.
    /// </summary>
    /// <param name="queryText">The parameter, or null when it was omitted.</param>
    /// <param name="schemaVersion">The schemaVersion value, or undefined.</param>
    /// <param name="where">The where value, or undefined.</param>
    /// <param name="orderBy">The orderBy value, or undefined.</param>
    /// <param name="unknownPaths">Unknown keys on the object.</param>
    /// <param name="error">The error text when the parameter is not usable.</param>
    /// <returns><see langword="false"/> when the parameter is malformed or not an object.</returns>
    public static bool TryRead(
        string? queryText,
        out JsonElement schemaVersion,
        out JsonElement where,
        out JsonElement orderBy,
        out IReadOnlyList<string> unknownPaths,
        out string error)
    {
        schemaVersion = default;
        where = default;
        orderBy = default;
        unknownPaths = [];
        error = "";
        if (queryText is null)
        {
            return true;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(queryText);
        }
        catch (JsonException)
        {
            error = "query: malformed JSON";
            return false;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                error = "query: expected Object, found " + JsonKinds.Of(root);
                return false;
            }

            var unknown = new List<string>();
            foreach (var property in root.EnumerateObject())
            {
                switch (property.Name)
                {
                    case "schemaVersion":
                    {
                        schemaVersion = property.Value.Clone();
                        break;
                    }
                    case "where":
                    {
                        where = property.Value.Clone();
                        break;
                    }
                    case "orderBy":
                    {
                        orderBy = property.Value.Clone();
                        break;
                    }
                    default:
                    {
                        unknown.Add(property.Name);
                        break;
                    }
                }
            }

            unknownPaths = unknown;
            return true;
        }
    }
}

/// <summary>
/// Document size limit from NFR-5.
/// </summary>
public static class DocumentLimits
{
    /// <summary>The Cosmos DB document limit, in bytes.</summary>
    public const int MaxItemBytes = 2 * 1024 * 1024;

    /// <summary>
    /// Returns true when the serialized item document exceeds <see cref="MaxItemBytes"/>.
    /// </summary>
    /// <param name="item">The item.</param>
    /// <returns><see langword="true"/> when the document is too large.</returns>
    public static bool ItemTooLarge(ItemDocument item)
    {
        var json = JsonSerializer.Serialize(item);
        return System.Text.Encoding.UTF8.GetByteCount(json) > MaxItemBytes;
    }
}
