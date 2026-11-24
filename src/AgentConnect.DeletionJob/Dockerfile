# Build from the repository root:
#   docker build -f src/AgentConnect.DeletionJob/Dockerfile -t ghcr.io/vdiamond/agentconnect-deletionjob:latest .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_NOLOGO=1 \
    NUGET_XMLDOC_MODE=skip

COPY .editorconfig ./
COPY global.json ./
COPY src/ src/
COPY external/EasyJsonSchema/ external/EasyJsonSchema/

RUN dotnet publish src/AgentConnect.DeletionJob/AgentConnect.DeletionJob.csproj \
    -c Release \
    -o /app/publish \
    --self-contained false \
    /p:UseAppHost=false

# Composition references Microsoft.AspNetCore.App, so the final stage is the
# ASP.NET image. The job is a one-shot process and does not listen.
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /app/publish .
USER app
ENTRYPOINT ["dotnet", "AgentConnect.DeletionJob.dll"]
