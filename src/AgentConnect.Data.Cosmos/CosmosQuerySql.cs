using System.Globalization;
using System.Text;
using Microsoft.Azure.Cosmos;

namespace AgentConnect;

/// <summary>
/// Builds a parameterized Cosmos DB query for one List partition. Values are never inserted into the text.
/// </summary>
public static class CosmosQuerySql
{
    /// <summary>
    /// Builds the query.
    /// </summary>
    /// <param name="query">The validated query.</param>
    /// <returns>The query definition.</returns>
    public static QueryDefinition Build(CompiledItemQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        var sql = new StringBuilder("SELECT * FROM c WHERE c.tenantId = @tenantId AND c.workspaceId = @workspaceId AND c.listId = @listId");
        var parameters = new List<(string Name, object Value)>
        {
            ("@tenantId", query.TenantId),
            ("@workspaceId", query.WorkspaceId),
            ("@listId", query.ListId),
        };
        if (query.SchemaVersionEquals is int equals)
        {
            sql.Append(" AND c.schemaVersion = @sv");
            parameters.Add(("@sv", equals));
        }
        else if (query.BelowCurrent)
        {
            sql.Append(" AND c.schemaVersion < @sv");
            parameters.Add(("@sv", query.CurrentSchemaVersion));
        }

        var parameter = 0;
        foreach (var condition in query.Where)
        {
            var path = DataPath(condition.Path);
            sql.Append(" AND IS_DEFINED(").Append(path).Append(") AND NOT IS_NULL(").Append(path).Append(')');
            sql.Append(" AND ").Append(TypePredicate(path, condition.TypeName));
            AppendCondition(sql, condition, path, ref parameter, parameters);
        }

        if (query.OrderBy is QueryOrder order)
        {
            var path = DataPath(order.Path);
            sql.Append(" AND IS_DEFINED(").Append(path).Append(") AND NOT IS_NULL(").Append(path).Append(')');
            sql.Append(" AND ").Append(TypePredicate(path, order.TypeName));
            sql.Append(" ORDER BY ").Append(path).Append(order.Descending ? " DESC" : " ASC");
        }
        else
        {
            sql.Append(" ORDER BY c.updated ASC, c.id ASC");
        }

        var definition = new QueryDefinition(sql.ToString());
        foreach (var (name, value) in parameters)
        {
            definition = definition.WithParameter(name, value);
        }

        return definition;
    }

    #region private

    private static void AppendCondition(StringBuilder sql, QueryCondition condition, string path, ref int parameter, List<(string Name, object Value)> parameters)
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
                parameters.Add((name, ParameterValue(condition.Value.Items[index])));
            }

            sql.Append(')');
            return;
        }

        var parameterName = "@p" + parameter.ToString(CultureInfo.InvariantCulture);
        parameter++;
        parameters.Add((parameterName, ParameterValue(condition.Value)));
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
            case "contains" when condition.TypeName.EndsWith("[]", StringComparison.Ordinal):
            {
                sql.Append("ARRAY_CONTAINS(").Append(path).Append(", ").Append(parameterName).Append(')');
                break;
            }
            default:
            {
                sql.Append("CONTAINS(").Append(path).Append(", ").Append(parameterName).Append(", false)");
                break;
            }
        }
    }

    private static object ParameterValue(QueryValue value)
    {
        return value.Kind switch
        {
            QueryValueKind.Number => value.Number,
            QueryValueKind.Boolean => value.Flag,
            _ => value.Text ?? "",
        };
    }

    private static string DataPath(string path)
    {
        var builder = new StringBuilder("c.data");
        foreach (var segment in path.Split('.'))
        {
            builder.Append("[\"").Append(segment.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)).Append("\"]");
        }

        return builder.ToString();
    }

    private static string TypePredicate(string path, string typeName)
    {
        if (typeName.EndsWith("[]", StringComparison.Ordinal))
        {
            return "IS_ARRAY(" + path + ")";
        }

        return typeName switch
        {
            "String" or "DateTime" => "IS_STRING(" + path + ")",
            "Boolean" => "IS_BOOL(" + path + ")",
            _ => "IS_NUMBER(" + path + ")",
        };
    }

    #endregion
}
