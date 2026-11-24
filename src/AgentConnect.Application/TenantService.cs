using Microsoft.Extensions.Logging;

namespace AgentConnect;

/// <summary>
/// Tenant bootstrap, suspension, and page sizes.
/// </summary>
public sealed class TenantService
{
    /// <summary>
    /// Initializes a new instance of the <see cref="TenantService"/> class.
    /// </summary>
    /// <param name="tenants">The tenants.</param>
    /// <param name="members">The members.</param>
    /// <param name="tokens">The token service.</param>
    /// <param name="email">The email sender.</param>
    /// <param name="audit">The audit recorder.</param>
    /// <param name="clock">The clock.</param>
    /// <param name="options">The options.</param>
    /// <param name="logger">The logger.</param>
    public TenantService(
        ITenantRepository tenants,
        IMemberRepository members,
        TokenService tokens,
        IEmailSender email,
        IAuditRecorder audit,
        IClock clock,
        AgentConnectOptions options,
        ILogger<TenantService> logger)
    {
        ArgumentNullException.ThrowIfNull(tenants);
        ArgumentNullException.ThrowIfNull(members);
        ArgumentNullException.ThrowIfNull(tokens);
        ArgumentNullException.ThrowIfNull(email);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _tenants = tenants;
        _members = members;
        _tokens = tokens;
        _email = email;
        _audit = audit;
        _clock = clock;
        _options = options;
        _logger = logger;
    }

    /// <summary>
    /// Creates a tenant and its first TenantOwner, then sends the invitation. FR-1.
    /// </summary>
    /// <param name="name">The raw name.</param>
    /// <param name="email">The raw email.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The tenant, or an error.</returns>
    public async Task<OperationResult<TenantCreatedResult>> CreateAsync(string? name, string? email, CancellationToken cancellationToken)
    {
        if (!NameRules.TryNormalizeName(name, out var tenantName, out var nameError))
        {
            return OperationResult<TenantCreatedResult>.Fail(PlatformError.BadRequest(nameError));
        }

        if (!EmailRules.TryNormalize(email, out var ownerEmail, out var emailError))
        {
            return OperationResult<TenantCreatedResult>.Fail(PlatformError.BadRequest(emailError));
        }

        var now = _clock.UtcNow;
        var tenant = new TenantDocument
        {
            Id = PlatformSupport.NewId(),
            Name = tenantName,
            Status = TenantStatuses.Active,
            PageSizeDefault = PageRules.DefaultPageSize,
            PageSizeMax = PageRules.DefaultPageSizeMax,
            Created = now,
            Subscription = null,
        };
        var member = NewMember(tenant.Id, ownerEmail, isTenantOwner: true, now);
        tenant.FirstMemberId = member.Id;
        await _tenants.CreateAsync(tenant, cancellationToken).ConfigureAwait(false);
        var duplicate = await Unique.CreateAsync(ct => _members.CreateAsync(member, ct), "email: already exists", cancellationToken).ConfigureAwait(false);
        if (duplicate is not null)
        {
            return OperationResult<TenantCreatedResult>.Fail(duplicate);
        }
        Audit(PlatformSupport.PlatformOperator, AuditActions.TenantCreated, ("tenantId", tenant.Id));
        Audit(PlatformSupport.PlatformOperator, AuditActions.MemberAdded, ("tenantId", tenant.Id), ("memberId", member.Id));
        var sent = await SendInvitationAsync(member, tenant, "AgentConnect", null, null, cancellationToken).ConfigureAwait(false);
        return OperationResult<TenantCreatedResult>.Ok(new TenantCreatedResult(tenant.Id, tenant.Name, tenant.Status, tenant.PageSizeDefault, tenant.PageSizeMax, member.Id, sent));
    }

    /// <summary>
    /// Resends the first member's invitation. FR-52.
    /// </summary>
    /// <param name="tenantId">The tenant id.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>Whether the email was sent.</returns>
    public async Task<OperationResult<EmailSentResult>> SendFirstInvitationAsync(string tenantId, CancellationToken cancellationToken)
    {
        var tenant = await LiveTenantAsync(tenantId, cancellationToken).ConfigureAwait(false);
        if (tenant.Error is not null)
        {
            return OperationResult<EmailSentResult>.Fail(tenant.Error);
        }

        var member = await _members.GetAsync(tenant.Value!.Id, tenant.Value.FirstMemberId, cancellationToken).ConfigureAwait(false);
        if (member is null)
        {
            return OperationResult<EmailSentResult>.Fail(PlatformError.NotFound);
        }

        var sent = await SendInvitationAsync(member, tenant.Value, "AgentConnect", null, null, cancellationToken).ConfigureAwait(false);
        return OperationResult<EmailSentResult>.Ok(new EmailSentResult(sent));
    }

    /// <summary>
    /// Suspends a tenant. Suspending a suspended tenant changes nothing. FR-37.
    /// </summary>
    /// <param name="tenantId">The tenant id.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The status.</returns>
    public async Task<OperationResult<StatusResult>> SuspendAsync(string tenantId, CancellationToken cancellationToken)
    {
        return await SetStatusAsync(tenantId, TenantStatuses.Suspended, AuditActions.TenantSuspended, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Resumes a tenant. Resuming an active tenant changes nothing. FR-37.
    /// </summary>
    /// <param name="tenantId">The tenant id.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The status.</returns>
    public async Task<OperationResult<StatusResult>> ResumeAsync(string tenantId, CancellationToken cancellationToken)
    {
        return await SetStatusAsync(tenantId, TenantStatuses.Active, AuditActions.TenantResumed, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Marks a tenant deleted. FR-38.
    /// </summary>
    /// <param name="tenantId">The tenant id.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The confirmation.</returns>
    public async Task<OperationResult<DeletedResult>> DeleteAsync(string tenantId, CancellationToken cancellationToken)
    {
        var tenant = await _tenants.GetAsync(tenantId, cancellationToken).ConfigureAwait(false);
        if (tenant is null || tenant.DeletedAt is not null)
        {
            return OperationResult<DeletedResult>.Fail(PlatformError.NotFound);
        }

        await DocumentUpdates.UpdateAsync(
            ct => _tenants.GetAsync(tenantId, ct),
            (current, ct) => _tenants.ReplaceAsync(current, ct),
            current =>
            {
                current.DeletedAt = _clock.UtcNow;
            },
            _options.ConcurrencyRetries,
            cancellationToken).ConfigureAwait(false);
        Audit(PlatformSupport.PlatformOperator, AuditActions.TenantDeleted, ("tenantId", tenant.Id));
        return OperationResult<DeletedResult>.Ok(new DeletedResult(true));
    }

    /// <summary>
    /// Reads the caller's page sizes. FR-50.
    /// </summary>
    /// <param name="caller">The caller.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The settings.</returns>
    public Task<OperationResult<TenantSettingsResult>> GetSettingsAsync(Caller caller, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        var error = Permissions.AuthorizeTenant(caller, PlatformAction.ViewTenantSettings, null);
        if (error is not null)
        {
            return Task.FromResult(OperationResult<TenantSettingsResult>.Fail(error));
        }

        return Task.FromResult(OperationResult<TenantSettingsResult>.Ok(new TenantSettingsResult(caller.Tenant.PageSizeDefault, caller.Tenant.PageSizeMax)));
    }

    /// <summary>
    /// Changes page sizes. A field that is not sent keeps its stored value. FR-50.
    /// </summary>
    /// <param name="caller">The caller.</param>
    /// <param name="pageSizeDefault">The new default, or null to keep the stored value.</param>
    /// <param name="pageSizeMax">The new maximum, or null to keep the stored value.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The settings.</returns>
    public async Task<OperationResult<TenantSettingsResult>> UpdateSettingsAsync(Caller caller, int? pageSizeDefault, int? pageSizeMax, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        var error = Permissions.AuthorizeTenant(caller, PlatformAction.SetTenantSettings, null);
        if (error is not null)
        {
            return OperationResult<TenantSettingsResult>.Fail(error);
        }

        var nextDefault = pageSizeDefault ?? caller.Tenant.PageSizeDefault;
        var nextMax = pageSizeMax ?? caller.Tenant.PageSizeMax;
        var pairError = PageRules.ValidatePair(nextDefault, nextMax);
        if (pairError is not null)
        {
            return OperationResult<TenantSettingsResult>.Fail(PlatformError.BadRequest(pairError));
        }

        if (nextDefault != caller.Tenant.PageSizeDefault || nextMax != caller.Tenant.PageSizeMax)
        {
            await DocumentUpdates.UpdateAsync(
                ct => DocumentUpdates.LiveAsync(token => _tenants.GetAsync(caller.Tenant.Id, token), current => current.DeletedAt, ct),
                (current, ct) => _tenants.ReplaceAsync(current, ct),
                current =>
                {
                    current.PageSizeDefault = nextDefault;
                    current.PageSizeMax = nextMax;
                },
                _options.ConcurrencyRetries,
                cancellationToken).ConfigureAwait(false);
            caller.Tenant.PageSizeDefault = nextDefault;
            caller.Tenant.PageSizeMax = nextMax;
            Audit(caller.Member.Id, AuditActions.TenantSettingsUpdated, ("tenantId", caller.Tenant.Id));
        }

        return OperationResult<TenantSettingsResult>.Ok(new TenantSettingsResult(nextDefault, nextMax));
    }

    #region private

    private readonly ITenantRepository _tenants;
    private readonly IMemberRepository _members;
    private readonly TokenService _tokens;
    private readonly IEmailSender _email;
    private readonly IAuditRecorder _audit;
    private readonly IClock _clock;
    private readonly AgentConnectOptions _options;
    private readonly ILogger<TenantService> _logger;

    private MemberDocument NewMember(string tenantId, string email, bool isTenantOwner, DateTimeOffset now)
    {
        var id = PlatformSupport.NewId();
        var issued = _tokens.Issue(tenantId, id);
        return new MemberDocument
        {
            Id = id,
            TenantId = tenantId,
            Email = email,
            TokenHash = issued.Hash,
            TokenEncrypted = issued.Encrypted,
            IsTenantOwner = isTenantOwner,
            InvitationSent = false,
            Created = now,
            LastTokenIssued = now,
        };
    }

    private async Task<bool> SendInvitationAsync(MemberDocument member, TenantDocument tenant, string invitedBy, string? workspaceName, string? role, CancellationToken cancellationToken)
    {
        string token;
        try
        {
            token = _tokens.Decrypt(member.TokenEncrypted);
        }
        catch (Exception exception)
        {
            _logger.LogWarning("Stored token could not be opened ({ExceptionType}).", exception.GetType().Name);
            return false;
        }

        var message = EmailTemplates.Invitation(member.Email, tenant.Name, invitedBy, workspaceName, role, token, _options.McpServerUrl, _options.RestApiUrl);
        var sent = await PlatformSupport.TrySendAsync(_email, _logger, message, cancellationToken).ConfigureAwait(false);
        if (!sent)
        {
            return false;
        }

        await DocumentUpdates.UpdateAsync(
            ct => _members.GetAsync(member.TenantId, member.Id, ct),
            (current, ct) => _members.ReplaceAsync(current, ct),
            current =>
            {
                current.InvitationSent = true;
                current.LastTokenSent = PlatformSupport.After(_clock.UtcNow, current.LastTokenIssued);
            },
            _options.ConcurrencyRetries,
            cancellationToken).ConfigureAwait(false);
        Audit(invitedBy == "AgentConnect" ? PlatformSupport.PlatformOperator : invitedBy, AuditActions.InvitationSent, ("tenantId", tenant.Id), ("memberId", member.Id));
        return true;
    }

    private async Task<OperationResult<TenantDocument>> LiveTenantAsync(string tenantId, CancellationToken cancellationToken)
    {
        var tenant = await _tenants.GetAsync(tenantId, cancellationToken).ConfigureAwait(false);
        if (tenant is null || tenant.DeletedAt is not null)
        {
            return OperationResult<TenantDocument>.Fail(PlatformError.NotFound);
        }

        return OperationResult<TenantDocument>.Ok(tenant);
    }

    private async Task<OperationResult<StatusResult>> SetStatusAsync(string tenantId, string status, string action, CancellationToken cancellationToken)
    {
        var tenant = await LiveTenantAsync(tenantId, cancellationToken).ConfigureAwait(false);
        if (tenant.Error is not null)
        {
            return OperationResult<StatusResult>.Fail(tenant.Error);
        }

        if (tenant.Value!.Status != status)
        {
            await DocumentUpdates.UpdateAsync(
                ct => _tenants.GetAsync(tenant.Value.Id, ct),
                (current, ct) => _tenants.ReplaceAsync(current, ct),
                current =>
                {
                    if (current.DeletedAt is null)
                    {
                        current.Status = status;
                    }
                },
                _options.ConcurrencyRetries,
                cancellationToken).ConfigureAwait(false);
            Audit(PlatformSupport.PlatformOperator, action, ("tenantId", tenant.Value.Id));
        }

        return OperationResult<StatusResult>.Ok(new StatusResult(status));
    }

    private void Audit(string actor, string action, params (string Key, string Value)[] targets)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in targets)
        {
            map[key] = value;
        }

        _audit.Record(new AuditEntry(actor, action, _clock.UtcNow, map));
    }

    #endregion
}
