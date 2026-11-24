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

Tests do not call Azure. They use the in-memory store and a fake email sender. The `cosmos-emulator` tests run against the Cosmos DB emulator when both `AGENTCONNECT_COSMOS_ENDPOINT` and `AGENTCONNECT_COSMOS_KEY` are set, and skip cleanly otherwise. Those tests create the section 5 containers on that account. This environment has no emulator, so they skip.

## Configuration

Secrets are environment variables or Container Apps secrets. Do not put them in source.

| Variable | Purpose |
|---|---|
| `AgentConnect__AdminKey` | Platform admin key (`X-Admin-Key`) |
| `AgentConnect__TokenEncryptionKey` | Base64 32-byte key for stored tokens |
| `AgentConnect__McpServerUrl` | MCP URL included in emails |
| `AgentConnect__RestApiUrl` | REST URL included in emails |
| `AgentConnect__DataProvider` | `Cosmos` or `Memory`. `Memory` is refused when the environment is Production |
| `AgentConnect__Cosmos__Endpoint` | Cosmos account endpoint (managed identity) |
| `AGENTCONNECT_COSMOS_ENDPOINT` | Emulator endpoint for the Cosmos integration tests |
| `AGENTCONNECT_COSMOS_KEY` | Emulator account key. Used only when the endpoint host is `localhost`, `127.0.0.1`, or `::1` and the environment is set to a value other than Production. Otherwise the client uses managed identity |
| `AgentConnect__Cosmos__Database` | Database name |
| `AgentConnect__Email__Endpoint` | Azure Communication Services endpoint |
| `AgentConnect__Email__SenderAddress` | Sender on `azure4smb.com` |
| `AgentConnect__ApplicationInsights__ConnectionString` | Audit and telemetry |

Cosmos and email access use `ManagedIdentityCredential` for the system-assigned identity. The emulator key is read only from `AGENTCONNECT_COSMOS_KEY` and is never committed. It is used only for a loopback endpoint outside Production, as in the table above. Queries that order by two properties need composite indexes (`created`+`id` and `email`+`id` on Members; `updated`+`id` on Workspaces, Lists, and Items). Members, Workspaces, and Lists also need a unique key policy. Cosmos DB can only set a unique key when the container is created; it cannot be added later. The paths are Members `/emailKey` (partition `/tenantId`), Workspaces `/nameKey` (partition `/tenantId`), and Lists `/nameKey` (partition `/tenantId` and `/workspaceId`). Tenants and Items have no unique key. The emulator tests create those containers, indexes, and unique keys. The application does not create Azure resources.
