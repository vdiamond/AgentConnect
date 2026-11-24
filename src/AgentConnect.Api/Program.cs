namespace AgentConnect;

/// <summary>
/// REST API host.
/// </summary>
public partial class Program
{
    /// <summary>
    /// Starts the REST API.
    /// </summary>
    /// <param name="args">The command line arguments.</param>
    /// <returns>A task.</returns>
    public static async Task Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.Services.AddAgentConnect(builder.Configuration, builder.Environment);
        var app = builder.Build();
        app.UseMiddleware<CallerMiddleware>();
        ApiEndpoints.Map(app);
        await app.RunAsync().ConfigureAwait(false);
    }
}
