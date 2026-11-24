# Container images

Packaging for the three AgentConnect hosts. The image does not contain secrets. Build from the repository root after `git submodule update --init` so `external/EasyJsonSchema` is present.

| Host | Dockerfile | Image | Listen |
| --- | --- | --- | --- |
| REST API | `src/AgentConnect.Api/Dockerfile` | `ghcr.io/vdiamond/agentconnect-api` | 8080 |
| MCP server (`/mcp`) | `src/AgentConnect.Mcp/Dockerfile` | `ghcr.io/vdiamond/agentconnect-mcp` | 8080 |
| Deletion job | `src/AgentConnect.DeletionJob/Dockerfile` | `ghcr.io/vdiamond/agentconnect-deletionjob` | does not listen |

Api and Mcp use the .NET 10 ASP.NET image and set `ASPNETCORE_HTTP_PORTS=8080`. The deletion job uses the .NET 10 runtime image. All three run as the image's non-root `app` user. Base images are `mcr.microsoft.com/dotnet/sdk:10.0`, `mcr.microsoft.com/dotnet/aspnet:10.0`, and `mcr.microsoft.com/dotnet/runtime:10.0`.

`AgentConnect.Composition` references the ASP.NET shared framework, and the deletion job references Composition, so the published job asks for `Microsoft.AspNetCore.App`. On the runtime image the process exits before `Main`. Api and Mcp use the ASP.NET image and are not affected.

## Build, tag, and push

Replace `<TAG>` with the tag to publish (a git commit SHA is a good choice). These commands are run by a person; this repository does not push images.

```bash
docker build -f src/AgentConnect.Api/Dockerfile -t agentconnect-api:<TAG> .
docker tag agentconnect-api:<TAG> ghcr.io/vdiamond/agentconnect-api:<TAG>
docker push ghcr.io/vdiamond/agentconnect-api:<TAG>

docker build -f src/AgentConnect.Mcp/Dockerfile -t agentconnect-mcp:<TAG> .
docker tag agentconnect-mcp:<TAG> ghcr.io/vdiamond/agentconnect-mcp:<TAG>
docker push ghcr.io/vdiamond/agentconnect-mcp:<TAG>

docker build -f src/AgentConnect.DeletionJob/Dockerfile -t agentconnect-deletionjob:<TAG> .
docker tag agentconnect-deletionjob:<TAG> ghcr.io/vdiamond/agentconnect-deletionjob:<TAG>
docker push ghcr.io/vdiamond/agentconnect-deletionjob:<TAG>
```

## Environment settings

Names only. Supply values at deploy time.

- `AgentConnect__AdminKey`
- `AgentConnect__TokenEncryptionKey`
- `AgentConnect__McpServerUrl`
- `AgentConnect__RestApiUrl`
- `AgentConnect__DataProvider` — `Cosmos` for the prototype
- `AgentConnect__Cosmos__Endpoint`
- `AgentConnect__Cosmos__Database`
- `AgentConnect__Email__Endpoint`
- `AgentConnect__Email__SenderAddress`
- `AgentConnect__ApplicationInsights__ConnectionString`

Api and Mcp also read `ASPNETCORE_HTTP_PORTS` (set to `8080` in the image).

## Plan check

Container names, partition-key paths, unique keys, and the setting names above match the code (`CosmosOptions` defaults, `EnsureEmulatorContainersAsync`, `AgentConnectOptions`).

| Container | Partition key paths | Unique key |
| --- | --- | --- |
| Tenants | `/id` | none |
| Members | `/tenantId` | `/emailKey` |
| Workspaces | `/tenantId` | `/nameKey` |
| Lists | `/tenantId`, `/workspaceId` | `/nameKey` |
| Items | `/tenantId`, `/workspaceId`, `/listId` | none |

Lists and Items are hierarchical. Microsoft.Azure.Cosmos 3.64.0 sets those to MultiHash version 2 when `PartitionKeyPaths` is assigned. Members also has composite indexes `/created`+`/id` and `/email`+`/id`. Workspaces, Lists, and Items have `/updated`+`/id`.

One mismatch: the plan calls Tenants, Members, and Workspaces Hash version 2. The code only sets `PartitionKeyPath`. In that SDK the stored kind is Hash and the version is left unset. Image names in the plan match the names above. The plan's Azure app names (`ac-api`, `ac-mcp`, `ac-deletion-job`) are not in this repository.
