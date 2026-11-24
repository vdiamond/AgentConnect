# AgentConnect

Multi-tenant workspace for agents and people. Agents use the MCP server. People and other systems use the REST API. Both hosts call the same C# libraries. Data is stored in Cosmos DB. This repository implements AgentConnect specification v6.

## Layout

- `src/AgentConnect.Core` — permissions, validation, paging, query parsing, tokens, email text, errors
- `src/AgentConnect.Application` — the shared use cases
- `src/AgentConnect.Data.Cosmos` — Cosmos DB containers from section 5
- `src/AgentConnect.Data.Memory` — in-memory store with the same interfaces
- `src/AgentConnect.Email` — Azure Communication Services Email
- `src/AgentConnect.Audit` — Application Insights custom events, excluded from sampling
- `src/AgentConnect.Api` — REST API
- `src/AgentConnect.Mcp` — MCP Streamable HTTP server, stateless, at `/mcp`
- `src/AgentConnect.DeletionJob` — one run of background deletion
- `external/EasyJsonSchema` — submodule, spec v23, commit `91235520bd06f2ce67d14b84dd99af9df686eaa8`
- `tests/AgentConnect.Tests` — unit and integration tests. Each spec case is marked with a `T` trait (`T-1` … `T-64`)

## Build and test

The SDK is pinned in `global.json` to `10.0.404`.

```bash
dotnet build AgentConnect.slnx
dotnet test AgentConnect.slnx
```

Tests do not call Azure. They use the in-memory store and a fake email sender. One test is skipped unless a Cosmos DB emulator is configured; this environment does not run the emulator.

## Configuration

Secrets are environment variables or Container Apps secrets. Do not put them in source.

| Variable | Purpose |
|---|---|
| `AgentConnect__AdminKey` | Platform admin key (`X-Admin-Key`) |
| `AgentConnect__TokenEncryptionKey` | Base64 32-byte key for stored tokens |
| `AgentConnect__McpServerUrl` | MCP URL included in emails |
| `AgentConnect__RestApiUrl` | REST URL included in emails |
| `AgentConnect__DataProvider` | `Cosmos` or `Memory` |
| `AgentConnect__Cosmos__Endpoint` | Cosmos account endpoint (managed identity) |
| `AgentConnect__Cosmos__Database` | Database name |
| `AgentConnect__Email__Endpoint` | Azure Communication Services endpoint |
| `AgentConnect__Email__SenderAddress` | Sender on `azure4smb.com` |
| `AgentConnect__ApplicationInsights__ConnectionString` | Audit and telemetry |

Cosmos access uses `DefaultAzureCredential`. Account keys are not used. The Items container needs the composite index in `CosmosModel` (`updated` ascending, then `id` ascending). The application does not create Azure resources.
