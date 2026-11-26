using System.Globalization;
using System.Text;
using Microsoft.Azure.Cosmos;

namespace AgentConnect;

/// <summary>
/// Builds a parameterized FR-55 query. Values are never inserted into the text.
/// </summary>
public static class CatalogSql
{
    /// <summary>
    /// Builds the query for one partition.
    /// </summary>
    /// <param name="query">The validated query.</param>
    /// <returns>The query definition.</returns>
    public static QueryDefinition Build(CompiledCatalogQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        var sql = new StringBuilder("SELECT * FROM c WHERE 1 = 1");
        var parameters = new List<(string Name, object Value)>();
        if (query.Kind is CatalogKind.Workspace or CatalogKind.List)
        {
            sql.Append(" AND (NOT IS_DEFINED(c.deletedAt) OR IS_NULL(c.deletedAt))");
        }

        if (query.Kind == CatalogKind.WorkspaceMember)
        {
            sql.Append(" AND IS_DEFINED(c.workspaceRoles[\"").Append(Quote(query.WorkspaceId ?? "")).Append("\"])");
        }

        if (query.Ids is not null)
        {
            sql.Append(" AND ARRAY_CONTAINS(@ids, c.id)");
            parameters.Add(("@ids", query.Ids.ToArray()));
        }

        var parameter = 0;
        foreach (var condition in query.Where)
        {
            var path = Path(query, condition.Path);
            sql.Append(" AND IS_DEFINED(").Append(path).Append(") AND NOT IS_NULL(").Append(path).Append(')');
            Append(sql, condition, path, ref parameter, parameters);
        }

        var sort = query.OrderBy?.Path ?? query.DefaultSort;
        var descending = query.OrderBy?.Descending ?? false;
        var direction = descending ? " DESC" : " ASC";
        sql.Append(" ORDER BY ").Append(SortPath(query, sort)).Append(direction).Append(", c.id").Append(direction);
        var definition = new QueryDefinition(sql.ToString());
        foreach (var (name, value) in parameters)
        {
            definition = definition.WithParameter(name, value);
        }

        return definition;
    }

    #region private

    private static void Append(StringBuilder sql, QueryCondition condition, string path, ref int parameter, List<(string Name, object Value)> parameters)
    {
        if (condition.Operator == "in" && condition.Value.Items is not null)
        {
            sql.Append(" AND ").Append(path).Append(" IN (");
            for (var index = 0; index < condition.Value.Items.Count; index++)
            {
                if (index > 0)
                {
                    sql.Append(", ");
                }

                var name = "@p" + parameter.ToString(CultureInfo.InvariantCulture);
                parameter++;
                sql.Append(name);
                parameters.Add((name, Value(condition.Value.Items[index])));
            }

            sql.Append(')');
            return;
        }

        var parameterName = "@p" + parameter.ToString(CultureInfo.InvariantCulture);
        parameter++;
        parameters.Add((parameterName, Value(condition.Value)));
        sql.Append(" AND ");
        switch (condition.Operator)
        {
            case "eq":
            {
                sql.Append(path).Append(" = ").Append(parameterName);
                break;
            }
            case "ne":
            {
                sql.Append(path).Append(" != ").Append(parameterName);
                break;
            }
            case "lt":
            {
                sql.Append(path).Append(" < ").Append(parameterName);
                break;
            }
            case "le":
            {
                sql.Append(path).Append(" <= ").Append(parameterName);
                break;
            }
            case "gt":
            {
                sql.Append(path).Append(" > ").Append(parameterName);
                break;
            }
            case "ge":
            {
                sql.Append(path).Append(" >= ").Append(parameterName);
                break;
            }
            case "startsWith":
            {
                sql.Append("STARTSWITH(").Append(path).Append(", ").Append(parameterName).Append(", false)");
                break;
            }
            default:
            {
                sql.Append("CONTAINS(").Append(path).Append(", ").Append(parameterName).Append(", false)");
                break;
            }
        }
    }

    private static object Value(QueryValue value)
    {
        return value.Kind switch
        {
            QueryValueKind.Number => value.Number,
            QueryValueKind.Boolean => value.Flag,
            _ => value.Text ?? "",
        };
    }

    private static string Path(CompiledCatalogQuery query, string field)
    {
        if (field == "email")
        {
            return "c.emailLower";
        }

        if (field == "role" && query.Kind == CatalogKind.WorkspaceMember)
        {
            var stored = "c.workspaceRoles[\"" + Quote(query.WorkspaceId ?? "") + "\"]";
            return "(" + stored + " = \"Owner\" ? \"WorkspaceOwner\" : (" + stored + " = \"Write\" ? \"WorkspaceWrite\" : \"WorkspaceRead\"))";
        }

        return "c." + field;
    }

    private static string SortPath(CompiledCatalogQuery query, string field)
    {
        return field == "email" ? "c.emailLower" : "c." + field;
    }

    private static string Quote(string id)
    {
        foreach (var character in id)
        {
            if (character is not ((>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '-'))
            {
                throw new ArgumentException("The id cannot be used in a query path.", nameof(id));
            }
        }

        return id;
    }

    #endregion
}
