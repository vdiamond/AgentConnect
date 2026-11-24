# Build from the repository root:
#   docker build -f src/AgentConnect.Mcp/Dockerfile -t ghcr.io/vdiamond/agentconnect-mcp:latest .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_NOLOGO=1 \
    NUGET_XMLDOC_MODE=skip

COPY .editorconfig ./
COPY global.json ./
COPY src/ src/
COPY external/EasyJsonSchema/ external/EasyJsonSchema/

RUN dotnet publish src/AgentConnect.Mcp/AgentConnect.Mcp.csproj \
    -c Release \
    -o /app/publish \
    --self-contained false \
    /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /app/publish .
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
USER app
ENTRYPOINT ["dotnet", "AgentConnect.Mcp.dll"]
