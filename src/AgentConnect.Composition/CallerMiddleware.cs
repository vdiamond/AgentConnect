using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace AgentConnect;

/// <summary>
/// Authenticates member calls. Platform-operator paths are left to the admin-key check.
/// A rejected call does not log the token or the admin key.
/// </summary>
public sealed class CallerMiddleware
{
    /// <summary>
    /// Initializes a new instance of the <see cref="CallerMiddleware"/> class.
    /// </summary>
    /// <param name="next">The next delegate.</param>
    public CallerMiddleware(RequestDelegate next)
    {
        ArgumentNullException.ThrowIfNull(next);
        _next = next;
    }

    /// <summary>
    /// Authenticates the request.
    /// </summary>
    /// <param name="context">The HTTP context.</param>
    /// <param name="resolver">The caller resolver.</param>
    /// <returns>A task.</returns>
    public async Task InvokeAsync(HttpContext context, CallerResolver resolver)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(resolver);
        if (IsPlatformOperator(context.Request))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        var result = await resolver.ResolveAsync(context.Request.Headers.Authorization.ToString(), context.RequestAborted).ConfigureAwait(false);
        if (result.Error is not null)
        {
            context.Response.StatusCode = result.Error.StatusCode;
            context.Response.ContentType = "application/json";
            await JsonSerializer.SerializeAsync(context.Response.Body, new ErrorBody(result.Error.Text), Json, context.RequestAborted).ConfigureAwait(false);
            return;
        }

        context.Items[AgentConnectBuilder.CallerItemKey] = result.Value;
        await _next(context).ConfigureAwait(false);
    }

    #region private

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly RequestDelegate _next;

    private static bool IsPlatformOperator(HttpRequest request)
    {
        var path = request.Path.Value ?? "";
        return path.Equals("/tenants", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/tenants/", StringComparison.OrdinalIgnoreCase);
    }

    #endregion
}
