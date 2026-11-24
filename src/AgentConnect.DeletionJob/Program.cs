using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AgentConnect;

/// <summary>
/// One run of the background deletion job. FR-51.
/// </summary>
public static class Program
{
    /// <summary>
    /// Runs deletion until nothing marked deleted remains, or the process is cancelled.
    /// </summary>
    /// <param name="args">The command line arguments.</param>
    /// <returns>A task.</returns>
    public static async Task Main(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);
        builder.Services.AddAgentConnect(builder.Configuration);
        using var host = builder.Build();
        var deletion = host.Services.GetRequiredService<DeletionService>();
        await deletion.RunUntilQuietAsync(CancellationToken.None).ConfigureAwait(false);
    }
}
