using Microsoft.Extensions.Logging;

namespace AgentConnect;

/// <summary>
/// Shared page-token and email helpers.
/// </summary>
public static class PlatformSupport
{
    /// <summary>The actor id stored for platform-operator changes.</summary>
    public const string PlatformOperator = "platform-operator";

    /// <summary>
    /// Runs a check that belongs after access has been allowed. MCP uses it for unknown arguments.
    /// </summary>
    /// <param name="afterGate">The check, or null.</param>
    /// <returns>The error, or null.</returns>
    public static PlatformError? AfterGate(Func<PlatformError?>? afterGate)
    {
        return afterGate?.Invoke();
    }

    /// <summary>
    /// Resolves the page size and store cursor for a list call.
    /// </summary>
    /// <param name="tenant">The tenant.</param>
    /// <param name="requested">The requested size, when specified.</param>
    /// <param name="specified">Whether the caller sent a page size.</param>
    /// <param name="continuation">The continuation token, or null.</param>
    /// <param name="fingerprint">The query fingerprint.</param>
    /// <returns>The size and cursor, or an error.</returns>
    public static OperationResult<PageWindow> OpenPage(TenantDocument tenant, int? requested, bool specified, string? continuation, string fingerprint)
    {
        if (specified && (requested is null || requested < 1))
        {
            return OperationResult<PageWindow>.Fail(PlatformError.BadRequest("pageSize: must be a whole number of at least 1"));
        }

        if (continuation is not null)
        {
            if (!ContinuationTokens.TryOpen(continuation, fingerprint, out var size, out var cursor, out var nextRow))
            {
                return OperationResult<PageWindow>.Fail(PlatformError.BadRequest("continuation: invalid token"));
            }

            return OperationResult<PageWindow>.Ok(new PageWindow(size, cursor, nextRow));
        }

        var chosen = !specified || requested is null
            ? Math.Min(tenant.PageSizeDefault, tenant.PageSizeMax)
            : Math.Min(requested.Value, tenant.PageSizeMax);
        return OperationResult<PageWindow>.Ok(new PageWindow(chosen, null));
    }

    /// <summary>
    /// Wraps a store cursor as a continuation token.
    /// </summary>
    /// <param name="fingerprint">The query fingerprint.</param>
    /// <param name="pageSize">The page size.</param>
    /// <param name="storeCursor">The store cursor, or null.</param>
    /// <param name="nextRow">The 1-based row number of the first entry on the next page.</param>
    /// <returns>The token, or null.</returns>
    public static string? Seal(string fingerprint, int pageSize, string? storeCursor, int nextRow = 1)
    {
        if (storeCursor is null)
        {
            return null;
        }

        return ContinuationTokens.Issue(fingerprint, pageSize, storeCursor, nextRow);
    }

    /// <summary>
    /// Sends an email. A failure is logged without the message body and reported as not sent.
    /// </summary>
    /// <param name="sender">The sender.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="message">The message.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns><see langword="true"/> when the email was sent.</returns>
    public static async Task<bool> TrySendAsync(IEmailSender sender, ILogger logger, EmailMessage message, CancellationToken cancellationToken)
    {
        try
        {
            await sender.SendAsync(message, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception exception)
        {
            logger.LogWarning("Email send failed ({ExceptionType}).", exception.GetType().Name);
            return false;
        }
    }

    /// <summary>
    /// Logs a non-throttling Cosmos fault. The message has the status, substatus, and activity id only.
    /// </summary>
    /// <param name="logger">The logger.</param>
    /// <param name="exception">The fault.</param>
    public static void LogCosmosFault(ILogger logger, StorageFailureException exception)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(exception);
        logger.LogError(
            "Cosmos request failed with status {StatusCode}, substatus {SubStatusCode}, activity {ActivityId}.",
            exception.StatusCode,
            exception.SubStatusCode,
            exception.ActivityId);
    }

    /// <summary>
    /// Creates a new id.
    /// </summary>
    /// <returns>A GUID.</returns>
    public static string NewId()
    {
        return Guid.NewGuid().ToString("D");
    }

    /// <summary>
    /// A timestamp that is strictly after <paramref name="previous"/> when the clock has not moved.
    /// </summary>
    /// <param name="now">The clock time.</param>
    /// <param name="previous">The previous timestamp.</param>
    /// <returns>The timestamp to store.</returns>
    public static DateTimeOffset After(DateTimeOffset now, DateTimeOffset? previous)
    {
        if (previous is DateTimeOffset prior && now <= prior)
        {
            return prior.AddTicks(1);
        }

        return now;
    }
}

/// <summary>
/// A resolved page window.
/// </summary>
/// <param name="PageSize">The page size.</param>
/// <param name="Cursor">The store cursor, or null.</param>
/// <param name="RowStart">The 1-based row number of the first entry on this page. FR-57.</param>
public sealed record PageWindow(int PageSize, string? Cursor, int RowStart = 1);
