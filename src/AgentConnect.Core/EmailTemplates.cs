namespace AgentConnect;

/// <summary>
/// An email to send. Invitation and token emails are HTML only (FR-54). Workspace access stays plain text.
/// </summary>
/// <param name="To">The recipient.</param>
/// <param name="Subject">The subject. It names AgentConnect and the tenant.</param>
/// <param name="Body">The body. HTML when <paramref name="Html"/> is true, with no plain-text alternative.</param>
/// <param name="ContainsToken">Whether the body contains a token.</param>
/// <param name="Html">Whether <paramref name="Body"/> is HTML and must be sent without a plain-text part.</param>
public sealed record EmailMessage(string To, string Subject, string Body, bool ContainsToken, bool Html = false);

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
    /// <summary>The line that tells the reader the previous token is dead. Token emails only, after a regeneration.</summary>
    public const string PreviousTokenLine = "Your previous token no longer works.";

    /// <summary>The fixed-width style used for URL and token cells so mail systems do not rewrite them as links.</summary>
    public const string ValueStyle = "font-family: ui-monospace, monospace";

    /// <summary>
    /// The note that travels with every token.
    /// </summary>
    /// <param name="tenantName">The tenant name.</param>
    /// <returns>The note.</returns>
    public static string TokenNote(string tenantName)
    {
        return "Keep this token secret. It does not expire. If you lose it, ask a TenantOwner of " + tenantName + ".";
    }

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
        var greeting = invitedBy + " invited you to AgentConnect for " + tenantName + ".";
        string? workspaceLine = workspaceName is null ? null : "Workspace: " + workspaceName + ". Role: " + role + ".";
        var body = Layout(greeting, workspaceLine, mcpServerUrl, restApiUrl, token, TokenNote(tenantName), previousToken: false);
        return new EmailMessage(to, "AgentConnect invitation — " + tenantName, body, true, true);
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
        var greeting = "Here is your AgentConnect token for " + tenantName + ".";
        var body = Layout(greeting, null, mcpServerUrl, restApiUrl, token, TokenNote(tenantName), previousTokenNoLongerWorks);
        return new EmailMessage(to, "AgentConnect token — " + tenantName, body, true, true);
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
        return new EmailMessage(to, "AgentConnect workspace access — " + tenantName, body, false, false);
    }

    /// <summary>
    /// Reads the token from an invitation or token email. The value is the plain text in the Token cell.
    /// </summary>
    /// <param name="body">The HTML body.</param>
    /// <param name="token">The token when the cell is present.</param>
    /// <returns><see langword="true"/> when the cell was found.</returns>
    public static bool TryReadToken(string body, out string token)
    {
        token = "";
        const string marker = ">Token</td><td style=\"" + ValueStyle + "\">";
        var start = body.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0)
        {
            return false;
        }

        start += marker.Length;
        var end = body.IndexOf('<', start);
        if (end < 0)
        {
            return false;
        }

        token = System.Net.WebUtility.HtmlDecode(body[start..end]);
        return token.Length > 0;
    }

    #region private

    private static string Layout(string greeting, string? secondLine, string mcpServerUrl, string restApiUrl, string token, string note, bool previousToken)
    {
        var builder = new System.Text.StringBuilder();
        builder.Append("<!DOCTYPE html><html><body>");
        builder.Append("<p>").Append(Encode(greeting)).Append("</p>");
        if (secondLine is not null)
        {
            builder.Append("<p>").Append(Encode(secondLine)).Append("</p>");
        }

        builder.Append("<table>");
        Row(builder, "MCP Server URL", mcpServerUrl);
        Row(builder, "REST API URL", restApiUrl);
        Row(builder, "Token", token);
        builder.Append("</table>");
        builder.Append("<p>")
            .Append(Encode("To connect an agent, add the MCP Server URL to its MCP settings and send the token in the header Authorization: Bearer <token>. For the REST API, send the same header to the REST API URL."))
            .Append("</p>");
        builder.Append("<p>").Append(Encode(note)).Append("</p>");
        if (previousToken)
        {
            builder.Append("<p>").Append(Encode(PreviousTokenLine)).Append("</p>");
        }

        builder.Append("</body></html>");
        return builder.ToString();
    }

    private static void Row(System.Text.StringBuilder builder, string label, string value)
    {
        builder.Append("<tr><td>").Append(Encode(label)).Append("</td><td style=\"").Append(ValueStyle).Append("\">").Append(Encode(value)).Append("</td></tr>");
    }

    private static string Encode(string value)
    {
        return System.Net.WebUtility.HtmlEncode(value);
    }

    #endregion
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
