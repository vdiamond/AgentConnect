using System.Collections.Concurrent;

namespace AgentConnect.Tests.Support;

/// <summary>Captures emails. A failed send throws <see cref="EmailDeliveryException"/>.</summary>
public sealed class RecordingEmail : IEmailSender
{
    /// <summary>Gets the captured messages.</summary>
    public ConcurrentQueue<EmailMessage> Messages { get; } = new();

    /// <summary>Gets or sets a value that makes the next sends fail.</summary>
    public bool Fail { get; set; }

    /// <inheritdoc />
    public Task SendAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        if (Fail)
        {
            throw new EmailDeliveryException("unavailable");
        }

        Messages.Enqueue(message);
        return Task.CompletedTask;
    }

    /// <summary>Returns messages sent to an email.</summary>
    /// <param name="email">The recipient.</param>
    /// <returns>The messages.</returns>
    public IReadOnlyList<EmailMessage> For(string email)
    {
        return Messages.Where(message => EmailRules.Same(message.To, email)).ToArray();
    }
}

/// <summary>Captures audit entries.</summary>
public sealed class RecordingAudit : IAuditRecorder
{
    /// <summary>Gets the entries.</summary>
    public ConcurrentQueue<AuditEntry> Entries { get; } = new();

    /// <inheritdoc />
    public void Record(AuditEntry entry)
    {
        Entries.Enqueue(entry);
    }

    /// <summary>Returns entries for a tenant.</summary>
    /// <param name="tenantId">The tenant id.</param>
    /// <returns>The entries.</returns>
    public IReadOnlyList<AuditEntry> ForTenant(string tenantId)
    {
        return Entries.Where(entry => entry.Targets.TryGetValue("tenantId", out var value) && value == tenantId).ToArray();
    }
}
