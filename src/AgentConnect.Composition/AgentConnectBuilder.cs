using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace AgentConnect;

/// <summary>
/// Registers the shared services.
/// </summary>
public static class AgentConnectBuilder
{
    /// <summary>The item key for the resolved caller.</summary>
    public const string CallerItemKey = "AgentConnect.Caller";

    /// <summary>
    /// Adds AgentConnect services.
    /// </summary>
    /// <param name="services">The services.</param>
    /// <param name="configuration">The configuration.</param>
    /// <returns>The same collection.</returns>
    public static IServiceCollection AddAgentConnect(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        services.AddOptions<AgentConnectOptions>().Bind(configuration.GetSection(AgentConnectOptions.SectionName));
        services.TryAddSingleton<IClock, SystemClock>();
        services.AddSingleton(sp =>
        {
            var options = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<AgentConnectOptions>>().Value;
            return new TokenService(options.TokenEncryptionKey);
        });
        services.AddSingleton(sp => sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<AgentConnectOptions>>().Value);
        services.AddSingleton<EasyJsonSchema.IEasyJsonSchema, EasyJsonSchema.EasyJsonSchemaService>();
        services.AddSingleton<SchemaGateway>();
        services.AddSingleton<CallerResolver>();
        services.AddSingleton<TenantService>();
        services.AddSingleton<MemberService>();
        services.AddSingleton<WorkspaceService>();
        services.AddSingleton<ContentService>();
        services.AddSingleton<DeletionService>();

        var provider = configuration[$"{AgentConnectOptions.SectionName}:DataProvider"] ?? "Cosmos";
        if (string.Equals(provider, "Memory", StringComparison.OrdinalIgnoreCase))
        {
            services.TryAddSingleton<MemoryDatabase>();
            services.AddSingleton<ITenantRepository>(sp => sp.GetRequiredService<MemoryDatabase>());
            services.AddSingleton<IMemberRepository>(sp => sp.GetRequiredService<MemoryDatabase>());
            services.AddSingleton<IWorkspaceRepository>(sp => sp.GetRequiredService<MemoryDatabase>());
            services.AddSingleton<IListRepository>(sp => sp.GetRequiredService<MemoryDatabase>());
            services.AddSingleton<IItemRepository>(sp => sp.GetRequiredService<MemoryDatabase>());
            services.AddSingleton<IDeletionStore>(sp => sp.GetRequiredService<MemoryDatabase>());
        }
        else
        {
            services.AddSingleton<CosmosDatabase>();
            services.AddSingleton<ITenantRepository>(sp => sp.GetRequiredService<CosmosDatabase>());
            services.AddSingleton<IMemberRepository>(sp => sp.GetRequiredService<CosmosDatabase>());
            services.AddSingleton<IWorkspaceRepository>(sp => sp.GetRequiredService<CosmosDatabase>());
            services.AddSingleton<IListRepository>(sp => sp.GetRequiredService<CosmosDatabase>());
            services.AddSingleton<IItemRepository>(sp => sp.GetRequiredService<CosmosDatabase>());
            services.AddSingleton<IDeletionStore>(sp => sp.GetRequiredService<CosmosDatabase>());
        }

        services.AddSingleton<IEmailSender>(sp =>
        {
            var options = sp.GetRequiredService<AgentConnectOptions>();
            return string.IsNullOrWhiteSpace(options.Email.Endpoint)
                ? new UnconfiguredEmailSender()
                : new AcsEmailSender(options);
        });
        services.AddSingleton<IAuditRecorder>(sp =>
        {
            var options = sp.GetRequiredService<AgentConnectOptions>();
            if (string.IsNullOrWhiteSpace(options.ApplicationInsights.ConnectionString))
            {
                return new LoggingAuditRecorder(sp.GetRequiredService<ILogger<LoggingAuditRecorder>>());
            }

            return new ApplicationInsightsAuditRecorder(options.ApplicationInsights.ConnectionString);
        });
        return services;
    }
}
