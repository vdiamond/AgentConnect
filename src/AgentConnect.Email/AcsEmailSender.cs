using Azure.Communication.Email;
using Azure.Identity;

namespace AgentConnect;

/// <summary>
/// Sends mail with Azure Communication Services Email and a managed identity.
/// </summary>
public sealed class AcsEmailSender : IEmailSender
{
    /// <summary>
    /// Initializes a new instance of the <see cref="AcsEmailSender"/> class.
    /// </summary>
    /// <param name="options">The options. The endpoint must be set.</param>
    public AcsEmailSender(AgentConnectOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.Email.Endpoint))
        {
            throw new InvalidOperationException("AgentConnect:Email:Endpoint is required.");
        }

        _sender = options.Email.SenderAddress;
        _client = new EmailClient(new Uri(options.Email.Endpoint), new ManagedIdentityCredential(ManagedIdentityId.SystemAssigned));
    }

    /// <inheritdoc />
    public async Task SendAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        var content = new EmailContent(message.Subject);
        if (message.Html)
        {
            content.Html = message.Body;
        }
        else
        {
            content.PlainText = message.Body;
        }
        var recipients = new EmailRecipients([new EmailAddress(message.To)]);
        var outbound = new Azure.Communication.Email.EmailMessage(_sender, recipients, content);
        try
        {
            var operation = await _client.SendAsync(Azure.WaitUntil.Completed, outbound, cancellationToken).ConfigureAwait(false);
            if (operation.Value.Status == EmailSendStatus.Failed)
            {
                throw new EmailDeliveryException("Email send failed.");
            }
        }
        catch (EmailDeliveryException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new EmailDeliveryException(exception.GetType().Name);
        }
    }

    #region private

    private readonly EmailClient _client;
    private readonly string _sender;

    #endregion
}

/// <summary>
/// Fails every send. Used when no email endpoint is configured, so a missing service does not hide the data change.
/// </summary>
public sealed class UnconfiguredEmailSender : IEmailSender
{
    /// <inheritdoc />
    public Task SendAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        throw new EmailDeliveryException("Email is not configured.");
    }
}
