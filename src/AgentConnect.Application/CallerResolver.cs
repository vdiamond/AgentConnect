using Microsoft.Extensions.Logging;

namespace AgentConnect;

/// <summary>
/// Resolves a bearer token to the current Member and Tenant. FR-5, FR-11, NFR-2.
/// </summary>
public sealed class CallerResolver
{
    /// <summary>
    /// Initializes a new instance of the <see cref="CallerResolver"/> class.
    /// </summary>
    /// <param name="tenants">The tenants.</param>
    /// <param name="members">The members.</param>
    /// <param name="tokens">The token service.</param>
    /// <param name="logger">The logger.</param>
    public CallerResolver(ITenantRepository tenants, IMemberRepository members, TokenService tokens, ILogger<CallerResolver> logger)
    {
        ArgumentNullException.ThrowIfNull(tenants);
        ArgumentNullException.ThrowIfNull(members);
        ArgumentNullException.ThrowIfNull(tokens);
        ArgumentNullException.ThrowIfNull(logger);
        _tenants = tenants;
        _members = members;
        _tokens = tokens;
        _logger = logger;
    }

    /// <summary>
    /// Resolves <paramref name="authorization"/>. A bad token is 401 even when the tenant is suspended.
    /// </summary>
    /// <param name="authorization">The Authorization header.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The caller, or an error.</returns>
    public async Task<OperationResult<Caller>> ResolveAsync(string? authorization, CancellationToken cancellationToken)
    {
        if (!TryBearer(authorization, out var token)
            || !_tokens.TryParse(token, out var tenantId, out var memberId, out var secret))
        {
            return OperationResult<Caller>.Fail(PlatformError.InvalidToken);
        }

        var member = await _members.GetAsync(tenantId, memberId, cancellationToken).ConfigureAwait(false);
        if (member is null || !_tokens.HashMatches(secret, member.TokenHash))
        {
            _logger.LogInformation("Rejected a token for tenant {TenantId}.", tenantId);
            return OperationResult<Caller>.Fail(PlatformError.InvalidToken);
        }

        var tenant = await _tenants.GetAsync(tenantId, cancellationToken).ConfigureAwait(false);
        if (tenant is null || tenant.DeletedAt is not null)
        {
            return OperationResult<Caller>.Fail(PlatformError.InvalidToken);
        }

        if (tenant.Status == TenantStatuses.Suspended)
        {
            return OperationResult<Caller>.Fail(PlatformError.TenantSuspended);
        }

        return OperationResult<Caller>.Ok(new Caller(member, tenant));
    }

    #region private

    private readonly ITenantRepository _tenants;
    private readonly IMemberRepository _members;
    private readonly TokenService _tokens;
    private readonly ILogger<CallerResolver> _logger;

    private static bool TryBearer(string? authorization, out string token)
    {
        token = "";
        if (string.IsNullOrWhiteSpace(authorization))
        {
            return false;
        }

        const string prefix = "Bearer ";
        if (!authorization.StartsWith(prefix, StringComparison.Ordinal) || authorization.Length == prefix.Length)
        {
            return false;
        }

        token = authorization[prefix.Length..].Trim();
        return token.Length > 0;
    }

    #endregion
}
