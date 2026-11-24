using System.Text.Json;
using Microsoft.Extensions.Options;

namespace AgentConnect;

/// <summary>
/// The REST surface from section 6.
/// </summary>
public static class ApiEndpoints
{
    /// <summary>
    /// Maps the endpoints.
    /// </summary>
    /// <param name="app">The application.</param>
    public static void Map(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.MapPost("/tenants", CreateTenantAsync);
        app.MapPost("/tenants/{id}/send-invitation", SendFirstInvitationAsync);
        app.MapPost("/tenants/{id}/suspend", SuspendAsync);
        app.MapPost("/tenants/{id}/resume", ResumeAsync);
        app.MapDelete("/tenants/{id}", DeleteTenantAsync);
        app.MapGet("/tenant", GetTenantAsync);
        app.MapPatch("/tenant", PatchTenantAsync);
        app.MapPost("/members", AddMemberAsync);
        app.MapPost("/members/{email}/send-invitation", SendInvitationAsync);
        app.MapDelete("/members/{email}", DeleteMemberAsync);
        app.MapPost("/members/{email}/token/regenerate", RegenerateAsync);
        app.MapPost("/members/{email}/token/send", SendTokenAsync);
        app.MapGet("/members", ListMembersAsync);
        app.MapPost("/workspaces", CreateWorkspaceAsync);
        app.MapDelete("/workspaces/{id}", DeleteWorkspaceAsync);
        app.MapGet("/workspaces", ListWorkspacesAsync);
        app.MapPatch("/workspaces/{id}", PatchWorkspaceAsync);
        app.MapGet("/workspaces/{id}/members", ListWorkspaceMembersAsync);
        app.MapPost("/workspaces/{id}/members", AddWorkspaceMemberAsync);
        app.MapDelete("/workspaces/{id}/members/{email}", RemoveWorkspaceMemberAsync);
        app.MapPatch("/workspaces/{id}/members/{email}", ChangeRoleAsync);
        app.MapPost("/workspaces/{id}/lists", CreateListAsync);
        app.MapPatch("/workspaces/{id}/lists/{listId}", PatchListAsync);
        app.MapDelete("/workspaces/{id}/lists/{listId}", DeleteListAsync);
        app.MapGet("/workspaces/{id}/lists", ListListsAsync);
        app.MapPut("/workspaces/{id}/lists/{listId}/schema", UpdateSchemaAsync);
        app.MapGet("/workspaces/{id}/lists/{listId}/schema", GetSchemaAsync);
        app.MapGet("/workspaces/{id}/lists/{listId}/items", QueryItemsAsync);
        app.MapGet("/workspaces/{id}/lists/{listId}/items/{itemId}", GetItemAsync);
        app.MapPut("/workspaces/{id}/lists/{listId}/items/{itemId}", UpsertItemAsync);
        app.MapDelete("/workspaces/{id}/lists/{listId}/items/{itemId}", DeleteItemAsync);
    }

    #region private

    private static async Task CreateTenantAsync(HttpContext http, TenantService tenants, IOptions<AgentConnectOptions> options)
    {
        if (!await AdminAsync(http, options.Value).ConfigureAwait(false))
        {
            return;
        }

        var parsed = await TryObjectAsync(http).ConfigureAwait(false);
        if (parsed is not JsonElement body)
        {
            return;
        }

        var result = await tenants.CreateAsync(Text(body, "name"), Text(body, "email"), http.RequestAborted).ConfigureAwait(false);
        await WriteAsync(http, result).ConfigureAwait(false);
    }

    private static async Task SendFirstInvitationAsync(HttpContext http, string id, TenantService tenants, IOptions<AgentConnectOptions> options)
    {
        if (!await AdminAsync(http, options.Value).ConfigureAwait(false))
        {
            return;
        }

        await WriteAsync(http, await tenants.SendFirstInvitationAsync(id, http.RequestAborted).ConfigureAwait(false)).ConfigureAwait(false);
    }

    private static async Task SuspendAsync(HttpContext http, string id, TenantService tenants, IOptions<AgentConnectOptions> options)
    {
        if (!await AdminAsync(http, options.Value).ConfigureAwait(false))
        {
            return;
        }

        await WriteAsync(http, await tenants.SuspendAsync(id, http.RequestAborted).ConfigureAwait(false)).ConfigureAwait(false);
    }

    private static async Task ResumeAsync(HttpContext http, string id, TenantService tenants, IOptions<AgentConnectOptions> options)
    {
        if (!await AdminAsync(http, options.Value).ConfigureAwait(false))
        {
            return;
        }

        await WriteAsync(http, await tenants.ResumeAsync(id, http.RequestAborted).ConfigureAwait(false)).ConfigureAwait(false);
    }

    private static async Task DeleteTenantAsync(HttpContext http, string id, TenantService tenants, IOptions<AgentConnectOptions> options)
    {
        if (!await AdminAsync(http, options.Value).ConfigureAwait(false))
        {
            return;
        }

        await WriteAsync(http, await tenants.DeleteAsync(id, http.RequestAborted).ConfigureAwait(false)).ConfigureAwait(false);
    }

    private static async Task GetTenantAsync(HttpContext http, TenantService tenants)
    {
        await WriteAsync(http, await tenants.GetSettingsAsync(Caller(http), http.RequestAborted).ConfigureAwait(false)).ConfigureAwait(false);
    }

    private static async Task PatchTenantAsync(HttpContext http, TenantService tenants)
    {
        var parsed = await TryObjectAsync(http).ConfigureAwait(false);
        if (parsed is not JsonElement body)
        {
            return;
        }

        var unknown = Unknown(body, "pageSizeDefault", "pageSizeMax");
        if (unknown is not null)
        {
            await WriteErrorAsync(http, PlatformError.BadRequest(unknown + ": unknown field")).ConfigureAwait(false);
            return;
        }

        int? pageSizeDefault = null;
        int? pageSizeMax = null;
        if (body.TryGetProperty("pageSizeDefault", out var defaultValue))
        {
            if (!TryWhole(defaultValue, out var number))
            {
                await WriteErrorAsync(http, PlatformError.BadRequest("pageSizeDefault: must be a whole number of at least 1")).ConfigureAwait(false);
                return;
            }

            pageSizeDefault = number;
        }

        if (body.TryGetProperty("pageSizeMax", out var maxValue))
        {
            if (!TryWhole(maxValue, out var number))
            {
                await WriteErrorAsync(http, PlatformError.BadRequest("pageSizeMax: must be a whole number of at least 1")).ConfigureAwait(false);
                return;
            }

            pageSizeMax = number;
        }

        await WriteAsync(http, await tenants.UpdateSettingsAsync(Caller(http), pageSizeDefault, pageSizeMax, http.RequestAborted).ConfigureAwait(false)).ConfigureAwait(false);
    }

    private static async Task AddMemberAsync(HttpContext http, MemberService members)
    {
        var parsed = await TryObjectAsync(http).ConfigureAwait(false);
        if (parsed is not JsonElement body)
        {
            return;
        }

        await WriteAsync(http, await members.AddAsync(Caller(http), Text(body, "email"), http.RequestAborted).ConfigureAwait(false)).ConfigureAwait(false);
    }

    private static async Task SendInvitationAsync(HttpContext http, string email, MemberService members)
    {
        await WriteAsync(http, await members.SendInvitationAsync(Caller(http), email, http.RequestAborted).ConfigureAwait(false)).ConfigureAwait(false);
    }

    private static async Task DeleteMemberAsync(HttpContext http, string email, MemberService members)
    {
        await WriteAsync(http, await members.DeleteAsync(Caller(http), email, http.RequestAborted).ConfigureAwait(false)).ConfigureAwait(false);
    }

    private static async Task RegenerateAsync(HttpContext http, string email, MemberService members)
    {
        await WriteAsync(http, await members.RegenerateAsync(Caller(http), email, http.RequestAborted).ConfigureAwait(false)).ConfigureAwait(false);
    }

    private static async Task SendTokenAsync(HttpContext http, string email, MemberService members)
    {
        await WriteAsync(http, await members.SendTokenAsync(Caller(http), email, http.RequestAborted).ConfigureAwait(false)).ConfigureAwait(false);
    }

    private static async Task ListMembersAsync(HttpContext http, MemberService members)
    {
        var (size, specified) = PageSize(http);
        await WriteAsync(http, await members.ListAsync(Caller(http), size, specified, Continuation(http), http.RequestAborted).ConfigureAwait(false)).ConfigureAwait(false);
    }

    private static async Task CreateWorkspaceAsync(HttpContext http, WorkspaceService workspaces)
    {
        var parsed = await TryObjectAsync(http).ConfigureAwait(false);
        if (parsed is not JsonElement body)
        {
            return;
        }

        var initial = new List<InitialRole>();
        if (body.TryGetProperty("members", out var members))
        {
            if (members.ValueKind != JsonValueKind.Array)
            {
                await WriteErrorAsync(http, PlatformError.BadRequest("members: expected array, found " + JsonKinds.Of(members))).ConfigureAwait(false);
                return;
            }

            foreach (var entry in members.EnumerateArray())
            {
                initial.Add(new InitialRole(Text(entry, "email"), Text(entry, "role")));
            }
        }

        var result = await workspaces.CreateAsync(Caller(http), Text(body, "name"), Text(body, "description"), body.TryGetProperty("description", out _), initial, http.RequestAborted).ConfigureAwait(false);
        await WriteAsync(http, result).ConfigureAwait(false);
    }

    private static async Task DeleteWorkspaceAsync(HttpContext http, string id, WorkspaceService workspaces)
    {
        await WriteAsync(http, await workspaces.DeleteAsync(Caller(http), id, http.RequestAborted).ConfigureAwait(false)).ConfigureAwait(false);
    }

    private static async Task ListWorkspacesAsync(HttpContext http, WorkspaceService workspaces)
    {
        var (size, specified) = PageSize(http);
        await WriteAsync(http, await workspaces.ListAsync(Caller(http), size, specified, Continuation(http), http.RequestAborted).ConfigureAwait(false)).ConfigureAwait(false);
    }

    private static async Task PatchWorkspaceAsync(HttpContext http, string id, WorkspaceService workspaces)
    {
        var parsed = await TryObjectAsync(http).ConfigureAwait(false);
        if (parsed is not JsonElement body)
        {
            return;
        }

        var result = await workspaces.UpdateAsync(Caller(http), id, Optional(body, "name"), Optional(body, "description"), http.RequestAborted).ConfigureAwait(false);
        await WriteAsync(http, result).ConfigureAwait(false);
    }

    private static async Task ListWorkspaceMembersAsync(HttpContext http, string id, MemberService members)
    {
        var (size, specified) = PageSize(http);
        await WriteAsync(http, await members.ListWorkspaceMembersAsync(Caller(http), id, size, specified, Continuation(http), http.RequestAborted).ConfigureAwait(false)).ConfigureAwait(false);
    }

    private static async Task AddWorkspaceMemberAsync(HttpContext http, string id, MemberService members)
    {
        var parsed = await TryObjectAsync(http).ConfigureAwait(false);
        if (parsed is not JsonElement body)
        {
            return;
        }

        await WriteAsync(http, await members.AddToWorkspaceAsync(Caller(http), id, Text(body, "email"), Text(body, "role"), http.RequestAborted).ConfigureAwait(false)).ConfigureAwait(false);
    }

    private static async Task RemoveWorkspaceMemberAsync(HttpContext http, string id, string email, MemberService members)
    {
        await WriteAsync(http, await members.RemoveFromWorkspaceAsync(Caller(http), id, email, http.RequestAborted).ConfigureAwait(false)).ConfigureAwait(false);
    }

    private static async Task ChangeRoleAsync(HttpContext http, string id, string email, MemberService members)
    {
        var parsed = await TryObjectAsync(http).ConfigureAwait(false);
        if (parsed is not JsonElement body)
        {
            return;
        }

        await WriteAsync(http, await members.ChangeRoleAsync(Caller(http), id, email, Text(body, "role"), http.RequestAborted).ConfigureAwait(false)).ConfigureAwait(false);
    }

    private static async Task CreateListAsync(HttpContext http, string id, ContentService content)
    {
        var parsed = await TryObjectAsync(http).ConfigureAwait(false);
        if (parsed is not JsonElement body)
        {
            return;
        }

        string? schema = null;
        if (body.TryGetProperty("schema", out var schemaElement))
        {
            schema = schemaElement.GetRawText();
        }

        var result = await content.CreateListAsync(Caller(http), id, Text(body, "name"), Text(body, "description"), body.TryGetProperty("description", out _), schema, http.RequestAborted).ConfigureAwait(false);
        await WriteAsync(http, result).ConfigureAwait(false);
    }

    private static async Task PatchListAsync(HttpContext http, string id, string listId, ContentService content)
    {
        var parsed = await TryObjectAsync(http).ConfigureAwait(false);
        if (parsed is not JsonElement body)
        {
            return;
        }

        await WriteAsync(http, await content.UpdateListAsync(Caller(http), id, listId, Optional(body, "name"), Optional(body, "description"), http.RequestAborted).ConfigureAwait(false)).ConfigureAwait(false);
    }

    private static async Task DeleteListAsync(HttpContext http, string id, string listId, ContentService content)
    {
        await WriteAsync(http, await content.DeleteListAsync(Caller(http), id, listId, http.RequestAborted).ConfigureAwait(false)).ConfigureAwait(false);
    }

    private static async Task ListListsAsync(HttpContext http, string id, ContentService content)
    {
        var (size, specified) = PageSize(http);
        await WriteAsync(http, await content.ListListsAsync(Caller(http), id, size, specified, Continuation(http), http.RequestAborted).ConfigureAwait(false)).ConfigureAwait(false);
    }

    private static async Task UpdateSchemaAsync(HttpContext http, string id, string listId, ContentService content)
    {
        using var reader = new StreamReader(http.Request.Body);
        var schema = await reader.ReadToEndAsync(http.RequestAborted).ConfigureAwait(false);
        await WriteAsync(http, await content.UpdateSchemaAsync(Caller(http), id, listId, schema, http.RequestAborted).ConfigureAwait(false)).ConfigureAwait(false);
    }

    private static async Task GetSchemaAsync(HttpContext http, string id, string listId, ContentService content)
    {
        await WriteAsync(http, await content.GetSchemaAsync(Caller(http), id, listId, http.RequestAborted).ConfigureAwait(false)).ConfigureAwait(false);
    }

    private static async Task QueryItemsAsync(HttpContext http, string id, string listId, ContentService content)
    {
        var (size, specified) = PageSize(http);
        if (!QueryInput.TryRead(http.Request.Query["query"].FirstOrDefault(), out var schemaVersion, out var where, out var orderBy, out var unknown, out var error))
        {
            await WriteErrorAsync(http, PlatformError.BadRequest(error)).ConfigureAwait(false);
            return;
        }

        var shape = http.Request.Query.ContainsKey("shape") ? http.Request.Query["shape"].ToString() : null;
        var result = await content.QueryItemsAsync(Caller(http), id, listId, shape, schemaVersion, where, orderBy, unknown, size, specified, Continuation(http), http.RequestAborted).ConfigureAwait(false);
        await WriteAsync(http, result).ConfigureAwait(false);
    }

    private static async Task GetItemAsync(HttpContext http, string id, string listId, string itemId, ContentService content)
    {
        await WriteAsync(http, await content.GetItemAsync(Caller(http), id, listId, itemId, http.RequestAborted).ConfigureAwait(false)).ConfigureAwait(false);
    }

    private static async Task UpsertItemAsync(HttpContext http, string id, string listId, string itemId, ContentService content)
    {
        using var reader = new StreamReader(http.Request.Body);
        var data = await reader.ReadToEndAsync(http.RequestAborted).ConfigureAwait(false);
        await WriteAsync(http, await content.UpsertItemAsync(Caller(http), id, listId, itemId, data, http.RequestAborted).ConfigureAwait(false)).ConfigureAwait(false);
    }

    private static async Task DeleteItemAsync(HttpContext http, string id, string listId, string itemId, ContentService content)
    {
        await WriteAsync(http, await content.DeleteItemAsync(Caller(http), id, listId, itemId, http.RequestAborted).ConfigureAwait(false)).ConfigureAwait(false);
    }

    private static async Task<bool> AdminAsync(HttpContext http, AgentConnectOptions options)
    {
        if (TokenService.AdminKeyMatches(http.Request.Headers["X-Admin-Key"].FirstOrDefault(), options.AdminKey))
        {
            return true;
        }

        await WriteErrorAsync(http, PlatformError.InvalidAdminKey).ConfigureAwait(false);
        return false;
    }

    private static Caller Caller(HttpContext http)
    {
        return (Caller)http.Items[AgentConnectBuilder.CallerItemKey]!;
    }

    private static async Task<JsonElement?> TryObjectAsync(HttpContext http)
    {
        try
        {
            using var document = await JsonDocument.ParseAsync(http.Request.Body, cancellationToken: http.RequestAborted).ConfigureAwait(false);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                await WriteErrorAsync(http, PlatformError.BadRequest("$: expected Object, found " + JsonKinds.Of(document.RootElement))).ConfigureAwait(false);
                return null;
            }

            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            await WriteErrorAsync(http, PlatformError.BadRequest("$: malformed JSON")).ConfigureAwait(false);
            return null;
        }
    }

    private static string? Text(JsonElement body, string name)
    {
        if (!body.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return value.GetString();
    }

    private static OptionalText Optional(JsonElement body, string name)
    {
        if (!body.TryGetProperty(name, out var value))
        {
            return new OptionalText(false, null);
        }

        return new OptionalText(true, value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText());
    }

    private static string? Unknown(JsonElement body, params string[] allowed)
    {
        foreach (var property in body.EnumerateObject())
        {
            if (!allowed.Contains(property.Name, StringComparer.Ordinal))
            {
                return property.Name;
            }
        }

        return null;
    }

    private static bool TryWhole(JsonElement value, out int number)
    {
        number = 0;
        if (value.ValueKind != JsonValueKind.Number)
        {
            return false;
        }

        var raw = value.GetRawText();
        if (raw.Contains('.') || raw.Contains('e') || raw.Contains('E') || raw.StartsWith('-'))
        {
            return false;
        }

        return value.TryGetInt32(out number);
    }

    private static (int? Size, bool Specified) PageSize(HttpContext http)
    {
        if (!http.Request.Query.ContainsKey("pageSize"))
        {
            return (null, false);
        }

        var text = http.Request.Query["pageSize"].ToString();
        if (!int.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var size))
        {
            return (0, true);
        }

        return (size, true);
    }

    private static string? Continuation(HttpContext http)
    {
        return http.Request.Query.ContainsKey("continuation") ? http.Request.Query["continuation"].ToString() : null;
    }

    private static async Task WriteAsync<T>(HttpContext http, OperationResult<T> result)
    {
        if (result.Error is PlatformError error)
        {
            await WriteErrorAsync(http, error).ConfigureAwait(false);
            return;
        }

        http.Response.StatusCode = StatusCodes.Status200OK;
        await http.Response.WriteAsJsonAsync(result.Value, http.RequestAborted).ConfigureAwait(false);
    }

    private static async Task WriteErrorAsync(HttpContext http, PlatformError error)
    {
        http.Response.StatusCode = error.StatusCode;
        if (error.RetryAfter is TimeSpan retry)
        {
            http.Response.Headers.RetryAfter = Math.Max(1, (int)Math.Ceiling(retry.TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        await http.Response.WriteAsJsonAsync(new ErrorBody(error.Text), http.RequestAborted).ConfigureAwait(false);
    }

    #endregion
}
