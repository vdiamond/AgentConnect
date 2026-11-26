namespace AgentConnect;

/// <summary>
/// A failure returned to a REST caller or an MCP tool. The text is the FR-42 body.
/// </summary>
public sealed record PlatformError(int StatusCode, string Text, TimeSpan? RetryAfter = null)
{
    /// <summary>Gets the 401 <c>$: invalid token</c> error.</summary>
    public static PlatformError InvalidToken { get; } = new(401, "$: invalid token");

    /// <summary>Gets the 401 <c>$: invalid admin key</c> error.</summary>
    public static PlatformError InvalidAdminKey { get; } = new(401, "$: invalid admin key");

    /// <summary>Gets the 403 <c>$: tenant suspended</c> error.</summary>
    public static PlatformError TenantSuspended { get; } = new(403, "$: tenant suspended");

    /// <summary>Gets the 403 <c>$: not allowed</c> error.</summary>
    public static PlatformError NotAllowed { get; } = new(403, "$: not allowed");

    /// <summary>Gets the 403 <c>$: not allowed on yourself</c> error.</summary>
    public static PlatformError NotAllowedOnYourself { get; } = new(403, "$: not allowed on yourself");

    /// <summary>Gets the 404 <c>$: not found</c> error.</summary>
    public static PlatformError NotFound { get; } = new(404, "$: not found");

    /// <summary>Gets the 413 <c>$: item too large</c> error.</summary>
    public static PlatformError ItemTooLarge { get; } = new(413, "$: item too large");

    /// <summary>Gets the 413 <c>$: batch too large</c> error. The batch payload exceeds 2 MB. FR-56.</summary>
    public static PlatformError BatchTooLarge { get; } = new(413, "$: batch too large");

    /// <summary>Gets the 424 <c>$: not applied</c> error. Another operation in the batch failed. FR-56.</summary>
    public static PlatformError NotApplied { get; } = new(424, "$: not applied");

    /// <summary>Gets the 507 <c>$: list storage limit reached</c> error.</summary>
    public static PlatformError ListStorageLimit { get; } = new(507, "$: list storage limit reached");

    /// <summary>Gets the 500 <c>$: internal error</c> error. The text has no exception detail.</summary>
    public static PlatformError InternalError { get; } = new(500, "$: internal error");

    /// <summary>
    /// Creates a 429 <c>$: too many requests</c> error.
    /// </summary>
    /// <param name="retryAfter">The Retry-After value taken from Cosmos DB.</param>
    /// <returns>The error.</returns>
    public static PlatformError TooManyRequests(TimeSpan retryAfter)
    {
        return new PlatformError(429, "$: too many requests", retryAfter);
    }

    /// <summary>
    /// Creates a 503 <c>$: service unavailable</c> error.
    /// </summary>
    /// <param name="retryAfter">The Retry-After value.</param>
    /// <returns>The error.</returns>
    public static PlatformError ServiceUnavailable(TimeSpan retryAfter)
    {
        return new PlatformError(503, "$: service unavailable", retryAfter);
    }

    /// <summary>
    /// Creates a 400 error.
    /// </summary>
    /// <param name="text">The <c>path: problem</c> text.</param>
    /// <returns>The error.</returns>
    public static PlatformError BadRequest(string text)
    {
        return new PlatformError(400, text);
    }

    /// <summary>
    /// Creates a 409 error.
    /// </summary>
    /// <param name="text">The <c>field: already exists</c> text.</param>
    /// <returns>The error.</returns>
    public static PlatformError Conflict(string text)
    {
        return new PlatformError(409, text);
    }
}

/// <summary>
/// The JSON body of a REST error.
/// </summary>
/// <param name="Error">The error text.</param>
public sealed record ErrorBody(string Error);

/// <summary>
/// A successful value or a <see cref="PlatformError"/>.
/// </summary>
/// <typeparam name="T">The success value type.</typeparam>
public readonly struct OperationResult<T>
{
    private OperationResult(T? value, PlatformError? error)
    {
        Value = value;
        Error = error;
    }

    /// <summary>Gets the success value when <see cref="IsSuccess"/> is true.</summary>
    public T? Value { get; }

    /// <summary>Gets the error when <see cref="IsSuccess"/> is false.</summary>
    public PlatformError? Error { get; }

    /// <summary>Gets a value indicating whether the operation succeeded.</summary>
    public bool IsSuccess
    {
        get
        {
            return Error is null;
        }
    }

    /// <summary>
    /// Creates a success result.
    /// </summary>
    /// <param name="value">The value.</param>
    /// <returns>The result.</returns>
    public static OperationResult<T> Ok(T value)
    {
        return new OperationResult<T>(value, null);
    }

    /// <summary>
    /// Creates a failure result.
    /// </summary>
    /// <param name="error">The error.</param>
    /// <returns>The result.</returns>
    public static OperationResult<T> Fail(PlatformError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new OperationResult<T>(default, error);
    }
}
