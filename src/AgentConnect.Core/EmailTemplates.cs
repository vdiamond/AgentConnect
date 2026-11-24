namespace AgentConnect;

/// <summary>
/// An email to send. The body is plain text.
/// </summary>
/// <param name="To">The recipient.</param>
/// <param name="Subject">The subject. It names AgentConnect and the tenant.</param>
/// <param name="Body">The body.</param>
/// <param name="ContainsToken">Whether the body contains a token.</param>
public sealed record EmailMessage(string To, string Subject, string Body, bool ContainsToken);

/// <summary>
/// Sends email. A failure is reported to the caller; it does not roll back the change that triggered it.
/// </summary>
public interface IEmailSender
{
    /// <summary>
    /// Sends one email.
    /// </summary>
    /// <param name="message">The message.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task.</returns>
    Task SendAsync(EmailMessage message, CancellationToken cancellationToken);
}

/// <summary>
/// Raised when the email service cannot send.
/// </summary>
public sealed class EmailDeliveryException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="EmailDeliveryException"/> class.
    /// </summary>
    /// <param name="message">The message, which must not contain a token.</param>
    public EmailDeliveryException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// The three FR-43 templates.
/// </summary>
public static class EmailTemplates
{
    /// <summary>The line that tells the reader the previous token is dead.</summary>
    public const string PreviousTokenLine = "The previous token no longer works.";

    /// <summary>The note that travels with every token.</summary>
    public const string TokenNote = "Keep this token secret. It does not expire. Ask a TenantOwner if it is lost.";

    /// <summary>
    /// Builds an invitation email.
    /// </summary>
    /// <param name="to">The recipient.</param>
    /// <param name="tenantName">The tenant name.</param>
    /// <param name="invitedBy">The inviting member's email, or <c>AgentConnect</c> for FR-1 and FR-52.</param>
    /// <param name="workspaceName">The workspace, when FR-19 sends the invitation.</param>
    /// <param name="role">The API role name, when FR-19 sends the invitation.</param>
    /// <param name="token">The token.</param>
    /// <param name="mcpServerUrl">The MCP Server URL.</param>
    /// <param name="restApiUrl">The REST API URL.</param>
    /// <returns>The message.</returns>
    public static EmailMessage Invitation(
        string to,
        string tenantName,
        string invitedBy,
        string? workspaceName,
        string? role,
        string token,
        string mcpServerUrl,
        string restApiUrl)
    {
        var body = "Tenant: " + tenantName + "\n"
            + "Invited by: " + invitedBy + "\n";
        if (workspaceName is not null)
        {
            body += "Workspace: " + workspaceName + "\n"
                + "Role: " + role + "\n";
        }

        body += "Token: " + token + "\n"
            + TokenNote + "\n"
            + "MCP Server URL: " + mcpServerUrl + "\n"
            + "REST API URL: " + restApiUrl + "\n";
        return new EmailMessage(to, "AgentConnect invitation — " + tenantName, body, true);
    }

    /// <summary>
    /// Builds a token email.
    /// </summary>
    /// <param name="to">The recipient.</param>
    /// <param name="tenantName">The tenant name.</param>
    /// <param name="token">The token.</param>
    /// <param name="mcpServerUrl">The MCP Server URL.</param>
    /// <param name="restApiUrl">The REST API URL.</param>
    /// <param name="previousTokenNoLongerWorks">Whether regeneration happened after the last email that contained a token.</param>
    /// <returns>The message.</returns>
    public static EmailMessage Token(
        string to,
        string tenantName,
        string token,
        string mcpServerUrl,
        string restApiUrl,
        bool previousTokenNoLongerWorks)
    {
        var body = "Tenant: " + tenantName + "\n"
            + "Token: " + token + "\n"
            + TokenNote + "\n"
            + "MCP Server URL: " + mcpServerUrl + "\n"
            + "REST API URL: " + restApiUrl + "\n";
        if (previousTokenNoLongerWorks)
        {
            body += PreviousTokenLine + "\n";
        }

        return new EmailMessage(to, "AgentConnect token — " + tenantName, body, true);
    }

    /// <summary>
    /// Builds a workspace access email. It does not contain a token.
    /// </summary>
    /// <param name="to">The recipient.</param>
    /// <param name="tenantName">The tenant name.</param>
    /// <param name="workspaceName">The workspace name.</param>
    /// <param name="role">The API role name.</param>
    /// <param name="grantedBy">The granting member's email.</param>
    /// <returns>The message.</returns>
    public static EmailMessage WorkspaceAccess(
        string to,
        string tenantName,
        string workspaceName,
        string role,
        string grantedBy)
    {
        var body = "Tenant: " + tenantName + "\n"
            + "Workspace: " + workspaceName + "\n"
            + "Role: " + role + "\n"
            + "Granted by: " + grantedBy + "\n";
        return new EmailMessage(to, "AgentConnect workspace access — " + tenantName, body, false);
    }
}

/// <summary>
/// An audit entry. It never contains a token.
/// </summary>
/// <param name="ActorId">The member id, or <c>platform-operator</c>.</param>
/// <param name="Action">The action name.</param>
/// <param name="Occurred">When the change happened, in UTC.</param>
/// <param name="Targets">Target ids. Keys are names such as <c>tenantId</c>.</param>
public sealed record AuditEntry(string ActorId, string Action, DateTimeOffset Occurred, IReadOnlyDictionary<string, string> Targets);

/// <summary>
/// Writes audit entries. Application Insights custom events are excluded from sampling.
/// </summary>
public interface IAuditRecorder
{
    /// <summary>
    /// Records one change.
    /// </summary>
    /// <param name="entry">The entry.</param>
    void Record(AuditEntry entry);

    /// <summary>
    /// Flushes buffered entries. The default implementation does nothing.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task.</returns>
    Task FlushAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}

/// <summary>
/// Audit action names.
/// </summary>
public static class AuditActions
{
    /// <summary>A tenant was created.</summary>
    public const string TenantCreated = "tenant.created";

    /// <summary>A tenant was suspended.</summary>
    public const string TenantSuspended = "tenant.suspended";

    /// <summary>A tenant was resumed.</summary>
    public const string TenantResumed = "tenant.resumed";

    /// <summary>A tenant was marked deleted.</summary>
    public const string TenantDeleted = "tenant.deleted";

    /// <summary>Tenant page sizes changed.</summary>
    public const string TenantSettingsUpdated = "tenant.settings.updated";

    /// <summary>A member was added.</summary>
    public const string MemberAdded = "member.added";

    /// <summary>A member was deleted.</summary>
    public const string MemberDeleted = "member.deleted";

    /// <summary>An invitation email was sent.</summary>
    public const string InvitationSent = "member.invitation.sent";

    /// <summary>A token was regenerated.</summary>
    public const string TokenRegenerated = "member.token.regenerated";

    /// <summary>A token email was sent.</summary>
    public const string TokenSent = "member.token.sent";

    /// <summary>The TenantOwner flag changed.</summary>
    public const string TenantOwnerChanged = "member.tenantOwner.changed";

    /// <summary>A workspace was created.</summary>
    public const string WorkspaceCreated = "workspace.created";

    /// <summary>A workspace was updated.</summary>
    public const string WorkspaceUpdated = "workspace.updated";

    /// <summary>A workspace was marked deleted.</summary>
    public const string WorkspaceDeleted = "workspace.deleted";

    /// <summary>A workspace role was assigned.</summary>
    public const string WorkspaceRoleAssigned = "workspace.role.assigned";

    /// <summary>A workspace role was changed.</summary>
    public const string WorkspaceRoleChanged = "workspace.role.changed";

    /// <summary>A workspace role was removed.</summary>
    public const string WorkspaceRoleRemoved = "workspace.role.removed";

    /// <summary>A list was created.</summary>
    public const string ListCreated = "list.created";

    /// <summary>A list was updated.</summary>
    public const string ListUpdated = "list.updated";

    /// <summary>A list schema was replaced.</summary>
    public const string SchemaUpdated = "list.schema.updated";

    /// <summary>A list was marked deleted.</summary>
    public const string ListDeleted = "list.deleted";
}
