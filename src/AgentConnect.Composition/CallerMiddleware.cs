using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace AgentConnect;

/// <summary>
/// Authenticates member calls. Platform-operator paths are left to the admin-key check.
/// A rejected call does not log the token or the admin key.
/// GET requests under <c>/.well-known/</c> are not found and are not authenticated, so an
/// OAuth discovery probe is not mistaken for a bearer challenge.
/// </summary>
public sealed class CallerMiddleware
{
    /// <summary>
    /// Initializes a new instance of the <see cref="CallerMiddleware"/> class.
    /// </summary>
    /// <param name="next">The next delegate.</param>
    /// <param name="logger">The logger.</param>
    public CallerMiddleware(RequestDelegate next, ILogger<CallerMiddleware> logger)
    {
        ArgumentNullException.ThrowIfNull(next);
        ArgumentNullException.ThrowIfNull(logger);
        _next = next;
        _logger = logger;
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
        try
        {
            if (IsWellKnownGet(context.Request))
            {
                await WriteAsync(context, PlatformError.NotFound).ConfigureAwait(false);
                return;
            }

            if (IsPlatformOperator(context.Request))
            {
                await _next(context).ConfigureAwait(false);
                return;
            }

            var result = await resolver.ResolveAsync(context.Request.Headers.Authorization.ToString(), context.RequestAborted).ConfigureAwait(false);
            if (result.Error is not null)
            {
                if (!context.Response.HasStarted && IsMcpEndpoint(context.Request) && IsInvalidToken(result.Error))
                {
                    // No realm and no resource_metadata: the client sends its static bearer token
                    // instead of starting OAuth discovery.
                    context.Response.Headers.WWWAuthenticate = BearerScheme;
                }

                await WriteAsync(context, result.Error).ConfigureAwait(false);
                return;
            }

            context.Items[AgentConnectBuilder.CallerItemKey] = result.Value;
            await _next(context).ConfigureAwait(false);
        }
        catch (StorageThrottleException exception)
        {
            await WriteAsync(context, PlatformError.TooManyRequests(exception.RetryAfter)).ConfigureAwait(false);
        }
        catch (StorageUnavailableException exception)
        {
            await WriteAsync(context, PlatformError.ServiceUnavailable(exception.RetryAfter)).ConfigureAwait(false);
        }
        catch (DocumentMissingException)
        {
            await WriteAsync(context, PlatformError.NotFound).ConfigureAwait(false);
        }
        catch (StorageTooLargeException)
        {
            await WriteAsync(context, PlatformError.ItemTooLarge).ConfigureAwait(false);
        }
        catch (StorageLimitException)
        {
            await WriteAsync(context, PlatformError.ListStorageLimit).ConfigureAwait(false);
        }
        catch (StorageConflictException)
        {
            await WriteAsync(context, PlatformError.Conflict("$: conflict")).ConfigureAwait(false);
        }
        catch (StorageFailureException exception)
        {
            PlatformSupport.LogCosmosFault(_logger, exception);
            await WriteAsync(context, PlatformError.InternalError).ConfigureAwait(false);
        }
    }

    #region private

    private const string WellKnownPrefix = "/.well-known/";
    private const string McpPath = "/mcp";
    private const string McpPathPrefix = "/mcp/";
    private const string BearerScheme = "Bearer";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly RequestDelegate _next;
    private readonly ILogger _logger;

    private static async Task WriteAsync(HttpContext context, PlatformError error)
    {
        if (context.Response.HasStarted)
        {
            return;
        }

        context.Response.StatusCode = error.StatusCode;
        context.Response.ContentType = "application/json";
        if (error.RetryAfter is TimeSpan retry)
        {
            context.Response.Headers.RetryAfter = Math.Max(1, (int)Math.Ceiling(retry.TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        await JsonSerializer.SerializeAsync(context.Response.Body, new ErrorBody(error.Text), Json, context.RequestAborted).ConfigureAwait(false);
    }

    private static bool IsWellKnownGet(HttpRequest request)
    {
        if (!HttpMethods.IsGet(request.Method))
        {
            return false;
        }

        var path = request.Path.Value;
        return path is not null && path.StartsWith(WellKnownPrefix, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsMcpEndpoint(HttpRequest request)
    {
        var path = request.Path.Value ?? "";
        return path.Equals(McpPath, StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(McpPathPrefix, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsInvalidToken(PlatformError error)
    {
        return error.StatusCode == PlatformError.InvalidToken.StatusCode
            && string.Equals(error.Text, PlatformError.InvalidToken.Text, StringComparison.Ordinal);
    }

    private static bool IsPlatformOperator(HttpRequest request)
    {
        var path = request.Path.Value ?? "";
        return path.Equals("/tenants", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/tenants/", StringComparison.OrdinalIgnoreCase);
    }

    #endregion
}
