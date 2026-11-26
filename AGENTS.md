# AGENTS.md

Development happens only through Victor and Cursor agents.

## What this repo is

AgentConnect is a multi-tenant workspace for agents and people. Agents use the MCP server. People and other systems use the REST API. Both hosts call the same C# libraries. Data is stored in Cosmos DB. The code on `main` implements AgentConnect specification v9.

## Build, run, and test

The SDK is pinned in `global.json` to `10.0.404` with `rollForward` set to `latestFeature`. The EasyJsonSchema submodule must be checked out first (see Dependencies). From the repository root:

```bash
dotnet build AgentConnect.slnx
dotnet test AgentConnect.slnx
```

Both commands were run with SDK 10.0.404. The build succeeded with 0 warnings. The test run passed 70 tests and skipped 7. The skipped tests are the `cosmos-emulator` collection. They run only when both `AGENTCONNECT_COSMOS_ENDPOINT` and `AGENTCONNECT_COSMOS_KEY` are set, and they skip otherwise. Those variables were unset here, so the seven tests skipped. The other tests do not call Azure. They use the in-memory store and a fake email sender.

This repository has no CI workflow.

### Run locally

Committed `appsettings.json` files leave secrets empty and set `DataProvider` to `Cosmos`. A host started from those files alone requires `AgentConnect:Cosmos:Endpoint`. `Memory` is refused when the environment name is Production or unset. An unset name counts as Production.

`AgentConnect__TokenEncryptionKey` must be base64 that decodes to 32 bytes. Generate it in the shell for that process. Do not commit a key.

The REST API and the MCP server are web hosts. They read `ASPNETCORE_ENVIRONMENT`. Run both from the repository root.

REST API. This listened on `http://127.0.0.1:5080`. `GET /tenant` without a bearer token returned 401 `{"error":"$: invalid token"}`. `POST /tenants` without `X-Admin-Key` returned 401 `{"error":"$: invalid admin key"}`.

```bash
export ASPNETCORE_ENVIRONMENT=Development
export AgentConnect__DataProvider=Memory
export AgentConnect__TokenEncryptionKey="$(openssl rand -base64 32)"
export AgentConnect__AdminKey=local-admin-key
export ASPNETCORE_URLS=http://127.0.0.1:5080
dotnet run --project src/AgentConnect.Api/AgentConnect.Api.csproj
```

MCP server. Streamable HTTP, stateless, at `/mcp`. This listened on `http://127.0.0.1:5081`. `POST /mcp` without a bearer token returned 401 and `WWW-Authenticate: Bearer`.

```bash
export ASPNETCORE_ENVIRONMENT=Development
export AgentConnect__DataProvider=Memory
export AgentConnect__TokenEncryptionKey="$(openssl rand -base64 32)"
export AgentConnect__AdminKey=local-admin-key
export ASPNETCORE_URLS=http://127.0.0.1:5081
dotnet run --project src/AgentConnect.Mcp/AgentConnect.Mcp.csproj
```

Deletion job. This is a generic host, so it reads `DOTNET_ENVIRONMENT`, not `ASPNETCORE_ENVIRONMENT`. With only `ASPNETCORE_ENVIRONMENT=Development` and `AgentConnect__DataProvider=Memory`, the process still sees Production and throws `AgentConnect:DataProvider Memory cannot be used in Production.` The command below was run from the repository root and exited 0 on an empty in-memory store. With no `DataProvider` override, `dotnet run --project src/AgentConnect.DeletionJob/AgentConnect.DeletionJob.csproj` throws `AgentConnect:Cosmos:Endpoint is required when DataProvider is Cosmos.`

```bash
export DOTNET_ENVIRONMENT=Development
export AgentConnect__DataProvider=Memory
export AgentConnect__TokenEncryptionKey="$(openssl rand -base64 32)"
dotnet run --project src/AgentConnect.DeletionJob/AgentConnect.DeletionJob.csproj
```

Variable names for a real deployment are in [README.md](README.md). Image build steps are in [docs/deploy/containers.md](docs/deploy/containers.md). Those Docker commands were not run while writing this file.

## Layout

Top level: `src/`, `tests/`, `external/`, `docs/`, `AgentConnect.slnx`, `global.json`, `.editorconfig`.

- `src/AgentConnect.Core` — permissions, validation, paging, query parsing, tokens, email text, errors. Project-references EasyJsonSchema.
- `src/AgentConnect.Application` — the shared use cases
- `src/AgentConnect.Data.Cosmos` — Cosmos DB containers
- `src/AgentConnect.Data.Memory` — in-memory store with the same interfaces
- `src/AgentConnect.Email` — Azure Communication Services Email
- `src/AgentConnect.Audit` — Application Insights custom events, excluded from sampling
- `src/AgentConnect.Composition` — wires the shared services for the REST API, MCP server, and deletion job (`AgentConnectBuilder.cs`, `CallerMiddleware.cs`)
- `src/AgentConnect.Api` — REST API
- `src/AgentConnect.Mcp` — MCP Streamable HTTP server, stateless, at `/mcp`
- `src/AgentConnect.DeletionJob` — one run of background deletion
- `external/EasyJsonSchema` — git submodule
- `tests/AgentConnect.Tests` — xUnit unit and integration tests. Each spec case is marked with a `T` trait (`T-1` … `T-101`)
- `docs/deploy/containers.md` — container image notes for the three hosts

## Dependencies on other vdiamond repos

### EasyJsonSchema (required to build)

- Repository: https://github.com/vdiamond/EasyJsonSchema
- How it is referenced: git submodule at `external/EasyJsonSchema` (`.gitmodules`), not a sibling-folder clone and not a NuGet package.
- Pinned commit: `91235520bd06f2ce67d14b84dd99af9df686eaa8` (EasyJsonSchema specification v23).
- Project reference: `src/AgentConnect.Core/AgentConnect.Core.csproj` references `external/EasyJsonSchema/src/EasyJsonSchema/EasyJsonSchema.csproj`. Every other project in `AgentConnect.slnx` depends on Core, so the submodule is required for the whole solution, including the tests.
- Expected layout: the submodule is inside this repository.

```bash
git clone --recurse-submodules https://github.com/vdiamond/AgentConnect.git
```

If `external/EasyJsonSchema` is empty after a clone:

```bash
git submodule update --init
```

Change EasyJsonSchema in its own repository. Do not edit it from this one, and do not move the pinned commit unless Victor asks.

### Known dependent

https://github.com/vdiamond/AgentConnectClient is a desktop client that calls this REST API and this MCP server. No project in this solution references it.

No other vdiamond repository is referenced. There is no sibling-folder `ProjectReference` and no second submodule.

## Conventions

`.cursor/rules/coding-standards.mdc` is the governing standard. Existing code may not yet comply. Bring it into compliance when you touch it. Do not reformat code you are not already changing.

What the tree does today:

- `net10.0`, `LangVersion` latest, nullable reference types, and implicit usings. `src/Directory.Build.props` and `tests/Directory.Build.props` set `TreatWarningsAsErrors` to true for every build.
- Root `.editorconfig`: 4-space C#, braces required.
- File-scoped namespaces. Projects under `src/` use the root namespace `AgentConnect`.
- XML documentation on public APIs (`GenerateDocumentationFile` in `src/Directory.Build.props`).
- Sealed classes and sealed records. Result types are sealed records. Persisted documents are mutable classes with settable properties.
- Private members sit in `#region private`. The current code has no `#region protected`.
- `ArgumentNullException.ThrowIfNull` and `ConfigureAwait(false)` on library awaits.
- xUnit, with spec cases marked by a `T` trait.

## What not to touch

- Secrets. Leave `AdminKey`, `TokenEncryptionKey`, Cosmos, email, and Application Insights values empty in committed `appsettings.json`. Supply them as environment variables or Container Apps secrets. `AGENTCONNECT_COSMOS_KEY` is only for the emulator on a loopback endpoint outside Production.
- Generated output: `bin/`, `obj/`, and `TestResults/`.
- The EasyJsonSchema submodule pointer and the files under `external/EasyJsonSchema`.
- The public REST routes, MCP tools, error text, and stored document shapes. Ask Victor before changing that contract. AgentConnectClient and other callers depend on it.
- Package versions and project references, unless Victor asks.
