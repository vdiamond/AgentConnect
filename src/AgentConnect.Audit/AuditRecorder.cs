using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.Extensibility;
using Microsoft.Extensions.Logging;

namespace AgentConnect;

/// <summary>
/// Names and sampling rules for audit events. Audit events are custom events excluded from sampling.
/// </summary>
public static class AuditTelemetry
{
    /// <summary>The Application Insights custom-event name.</summary>
    public const string EventName = "AgentConnect.Audit";

    /// <summary>
    /// Returns true when an event must be kept. Audit events are always kept.
    /// </summary>
    /// <param name="name">The event name.</param>
    /// <param name="otherSamplingRatio">The sampling ratio applied to every other event, from 0 to 1.</param>
    /// <param name="bucket">A stable bucket in the range 0 to 9999.</param>
    /// <returns><see langword="true"/> when the event is kept.</returns>
    public static bool Keep(string? name, double otherSamplingRatio, int bucket)
    {
        if (name == EventName)
        {
            return true;
        }

        if (otherSamplingRatio >= 1)
        {
            return true;
        }

        if (otherSamplingRatio <= 0)
        {
            return false;
        }

        var threshold = (int)(otherSamplingRatio * 10000);
        var slot = bucket % 10000;
        if (slot < 0)
        {
            slot += 10000;
        }

        return slot < threshold;
    }
}

/// <summary>
/// Writes audit entries as Application Insights custom events. The client is configured with a sampling ratio of 1.
/// </summary>
public sealed class ApplicationInsightsAuditRecorder : IAuditRecorder
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ApplicationInsightsAuditRecorder"/> class.
    /// </summary>
    /// <param name="connectionString">The Application Insights connection string.</param>
    public ApplicationInsightsAuditRecorder(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        var configuration = new TelemetryConfiguration
        {
            ConnectionString = connectionString,
            SamplingRatio = 1,
        };
        _client = new TelemetryClient(configuration);
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ApplicationInsightsAuditRecorder"/> class.
    /// </summary>
    /// <param name="client">A client whose sampling does not drop <see cref="AuditTelemetry.EventName"/>.</param>
    public ApplicationInsightsAuditRecorder(TelemetryClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        _client = client;
    }

    /// <inheritdoc />
    public void Record(AuditEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var properties = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["actorId"] = entry.ActorId,
            ["action"] = entry.Action,
            ["occurred"] = entry.Occurred.ToString("o"),
        };
        foreach (var (key, value) in entry.Targets)
        {
            properties["target." + key] = value;
        }

        _client.TrackEvent(AuditTelemetry.EventName, properties);
    }

    #region private

    private readonly TelemetryClient _client;

    #endregion
}

/// <summary>
/// Writes audit entries to the log when Application Insights is not configured. The entry has no token.
/// </summary>
public sealed class LoggingAuditRecorder : IAuditRecorder
{
    /// <summary>
    /// Initializes a new instance of the <see cref="LoggingAuditRecorder"/> class.
    /// </summary>
    /// <param name="logger">The logger.</param>
    public LoggingAuditRecorder(ILogger<LoggingAuditRecorder> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    /// <inheritdoc />
    public void Record(AuditEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        _logger.LogInformation("Audit {Action} by {ActorId} at {Occurred}.", entry.Action, entry.ActorId, entry.Occurred);
    }

    #region private

    private readonly ILogger _logger;

    #endregion
}
