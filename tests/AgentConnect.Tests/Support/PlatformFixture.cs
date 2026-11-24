extern alias ApiHost;
extern alias McpHost;

using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;

namespace AgentConnect.Tests.Support;

/// <summary>Shared in-memory platform used by the integration tests.</summary>
public sealed class PlatformFixture : IAsyncLifetime
{
    /// <summary>Gets the admin key used by the tests.</summary>
    public const string AdminKey = "test-admin-key";

    /// <summary>Initializes a new instance of the <see cref="PlatformFixture"/> class.</summary>
    public PlatformFixture()
    {
        EncryptionKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        Database = new MemoryDatabase();
        Email = new RecordingEmail();
        Audit = new RecordingAudit();
        Api = new WebApplicationFactory<ApiHost::AgentConnect.Program>().WithWebHostBuilder(builder => Configure(builder));
        Mcp = new WebApplicationFactory<McpHost::AgentConnect.Program>().WithWebHostBuilder(builder => Configure(builder));
        ApiClient = Api.CreateClient();
        McpHttp = Mcp.CreateClient();
        Tokens = new TokenService(EncryptionKey);
    }

    /// <summary>Gets the encryption key.</summary>
    public string EncryptionKey { get; }

    /// <summary>Gets the shared database.</summary>
    public MemoryDatabase Database { get; }

    /// <summary>Gets the captured email.</summary>
    public RecordingEmail Email { get; }

    /// <summary>Gets the captured audit entries.</summary>
    public RecordingAudit Audit { get; }

    /// <summary>Gets the REST factory.</summary>
    public WebApplicationFactory<ApiHost::AgentConnect.Program> Api { get; }

    /// <summary>Gets the MCP factory.</summary>
    public WebApplicationFactory<McpHost::AgentConnect.Program> Mcp { get; }

    /// <summary>Gets the REST client.</summary>
    public HttpClient ApiClient { get; }

    /// <summary>Gets the MCP HTTP client.</summary>
    public HttpClient McpHttp { get; }

    /// <summary>Gets the token service that matches the hosts.</summary>
    public TokenService Tokens { get; }

    /// <inheritdoc />
    public Task InitializeAsync()
    {
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task DisposeAsync()
    {
        ApiClient.Dispose();
        McpHttp.Dispose();
        await Api.DisposeAsync().ConfigureAwait(false);
        await Mcp.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>Sends a REST call.</summary>
    /// <param name="method">The method.</param>
    /// <param name="url">The URL.</param>
    /// <param name="body">The JSON body, or null.</param>
    /// <param name="token">The bearer token, or null.</param>
    /// <param name="admin">Whether to send the admin key.</param>
    /// <returns>The status and body.</returns>
    public async Task<(int Status, string Body, HttpResponseMessage Response)> SendAsync(HttpMethod method, string url, object? body, string? token, bool admin)
    {
        var request = new HttpRequestMessage(method, url);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        if (admin)
        {
            request.Headers.Add("X-Admin-Key", AdminKey);
        }

        var response = await ApiClient.SendAsync(request).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        return ((int)response.StatusCode, text, response);
    }

    /// <summary>Reads the error text.</summary>
    /// <param name="body">The body.</param>
    /// <returns>The error.</returns>
    public static string ErrorOf(string body)
    {
        using var document = JsonDocument.Parse(body);
        return document.RootElement.GetProperty("error").GetString() ?? "";
    }

    /// <summary>Opens an MCP client for a token.</summary>
    /// <param name="token">The bearer token.</param>
    /// <returns>The client.</returns>
    public async Task<McpClient> McpClientAsync(string token)
    {
        var http = Mcp.CreateClient();
        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(http.BaseAddress!, "mcp"),
            TransportMode = HttpTransportMode.StreamableHttp,
            AdditionalHeaders = new Dictionary<string, string>
            {
                ["Authorization"] = "Bearer " + token,
            },
        }, http, LoggerFactory.Create(builder => { }), ownsHttpClient: true);
        return await McpClient.CreateAsync(transport).ConfigureAwait(false);
    }

    #region private

    private void Configure(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
    {
        builder.UseSetting("AgentConnect:DataProvider", "Memory");
        builder.UseSetting("AgentConnect:AdminKey", AdminKey);
        builder.UseSetting("AgentConnect:TokenEncryptionKey", EncryptionKey);
        builder.UseSetting("AgentConnect:McpServerUrl", "http://localhost/mcp");
        builder.UseSetting("AgentConnect:RestApiUrl", "http://localhost");
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<MemoryDatabase>();
            services.RemoveAll<ITenantRepository>();
            services.RemoveAll<IMemberRepository>();
            services.RemoveAll<IWorkspaceRepository>();
            services.RemoveAll<IListRepository>();
            services.RemoveAll<IItemRepository>();
            services.RemoveAll<IDeletionStore>();
            services.AddSingleton(Database);
            services.AddSingleton<ITenantRepository>(sp => sp.GetRequiredService<MemoryDatabase>());
            services.AddSingleton<IMemberRepository>(sp => sp.GetRequiredService<MemoryDatabase>());
            services.AddSingleton<IWorkspaceRepository>(sp => sp.GetRequiredService<MemoryDatabase>());
            services.AddSingleton<IListRepository>(sp => sp.GetRequiredService<MemoryDatabase>());
            services.AddSingleton<IItemRepository>(sp => sp.GetRequiredService<MemoryDatabase>());
            services.AddSingleton<IDeletionStore>(sp => sp.GetRequiredService<MemoryDatabase>());
            services.RemoveAll<IEmailSender>();
            services.AddSingleton<IEmailSender>(Email);
            services.RemoveAll<IAuditRecorder>();
            services.AddSingleton<IAuditRecorder>(Audit);
        });
    }

    #endregion
}

/// <summary>Runs platform tests one at a time. They share one database.</summary>
[CollectionDefinition("platform", DisableParallelization = true)]
public sealed class PlatformCollection : ICollectionFixture<PlatformFixture>
{
}
