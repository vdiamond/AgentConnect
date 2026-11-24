using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.Extensibility;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Logs;

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
        _client = new TelemetryClient(CreateConfiguration(connectionString));
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

    /// <summary>
    /// Flushes the Application Insights buffer so a scale-to-zero stop does not drop audit events.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task.</returns>
    public Task FlushAsync(CancellationToken cancellationToken)
    {
        return _client.FlushAsync(cancellationToken);
    }

    /// <summary>
    /// Records audit events through <see cref="Record"/> and flushes them. Trace sampling is set to zero.
    /// Custom events are logs, so the audit events still arrive.
    /// </summary>
    /// <param name="count">How many audit events to record.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>How many <see cref="AuditTelemetry.EventName"/> events the exporter saw, including their targets.</returns>
    public static async Task<int> ExerciseProductionSamplingAsync(int count, CancellationToken cancellationToken)
    {
        const string connection = "InstrumentationKey=00000000-0000-0000-0000-000000000000";
        var auditProbe = new AuditLogProbe();
        var configuration = CreateConfiguration(connection);
        configuration.SamplingRatio = 0;
        configuration.ConfigureOpenTelemetryBuilder(builder =>
        {
            builder.WithLogging(logging => logging.AddProcessor(auditProbe));
        });
        var recorder = new ApplicationInsightsAuditRecorder(new TelemetryClient(configuration));
        for (var index = 0; index < count; index++)
        {
            recorder.Record(new AuditEntry("actor", AuditActions.TenantCreated, DateTimeOffset.UtcNow, new Dictionary<string, string> { ["tenantId"] = "t" }));
        }

        await recorder.FlushAsync(cancellationToken).ConfigureAwait(false);
        return auditProbe.Names.Count(name => name.Contains("microsoft.custom_event.name=" + AuditTelemetry.EventName, StringComparison.Ordinal) && name.Contains("target.tenantId=t", StringComparison.Ordinal));
    }

    /// <summary>
    /// Builds the audit telemetry configuration. Sampling is 100 percent so audit events are not dropped.
    /// </summary>
    /// <param name="connectionString">The Application Insights connection string.</param>
    /// <returns>The configuration.</returns>
    public static TelemetryConfiguration CreateConfiguration(string connectionString)
    {
        return new TelemetryConfiguration
        {
            ConnectionString = connectionString,
            SamplingRatio = 1,
        };
    }

    #region private

    private readonly TelemetryClient _client;

    #endregion
}

/// <summary>
/// Counts telemetry log records so tests can show that audit events survive sampling.
/// </summary>
public sealed class AuditLogProbe : BaseProcessor<LogRecord>
{
    /// <summary>Gets the record names that reached the exporter.</summary>
    public List<string> Names { get; } = [];

    /// <inheritdoc />
    public override void OnEnd(LogRecord data)
    {
        var attributes = data.Attributes is null
            ? ""
            : string.Join(",", data.Attributes.Select(pair => pair.Key + "=" + pair.Value));
        Names.Add(attributes);
    }
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
        _logger.LogInformation(
            "Audit {Action} by {ActorId} at {Occurred} targets {Targets}.",
            entry.Action,
            entry.ActorId,
            entry.Occurred,
            string.Join(',', entry.Targets.Select(pair => pair.Key + "=" + pair.Value)));
    }

    #region private

    private readonly ILogger _logger;

    #endregion
}
