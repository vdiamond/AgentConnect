using System.Text.Json.Serialization;
using ModelContextProtocol.AspNetCore;

namespace AgentConnect;

/// <summary>
/// MCP Server host. Streamable HTTP, stateless: each call carries its own token.
/// </summary>
public partial class Program
{
    /// <summary>
    /// Starts the MCP Server.
    /// </summary>
    /// <param name="args">The command line arguments.</param>
    /// <returns>A task.</returns>
    public static async Task Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.Services.ConfigureHttpJsonOptions(options =>
        {
            options.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
        });
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddAgentConnect(builder.Configuration, builder.Environment);
        builder.Services.AddMcpServer()
            .WithHttpTransport(options =>
            {
                options.Stateless = true;
            })
            .WithTools<AgentConnectTools>();
        var app = builder.Build();
        app.UseMiddleware<CallerMiddleware>();
        app.MapMcp("/mcp");
        await app.RunAsync().ConfigureAwait(false);
    }
}
