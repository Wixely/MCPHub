# Built from the repository root:
#   docker build -f src/MCPHub.Server/Dockerfile -t mcphub:local .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source
COPY global.json Directory.Build.props Directory.Build.targets Directory.Packages.props NuGet.config ./
COPY src/MCPHub.Core/ src/MCPHub.Core/
COPY src/MCPHub.Processes/ src/MCPHub.Processes/
COPY src/MCPHub.Proxy/ src/MCPHub.Proxy/
COPY src/MCPHub.Hosting/ src/MCPHub.Hosting/
COPY src/MCPHub.Hub/ src/MCPHub.Hub/
COPY src/MCPHub.Server/ src/MCPHub.Server/
RUN dotnet publish src/MCPHub.Server/MCPHub.Server.csproj -c Release -o /out \
    --no-self-contained -p:UseAppHost=false -p:DebugType=none -p:DebugSymbols=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /out/ ./

# Both are written to: identity, permissions and routes are edited through the management tools,
# which is the reason to run this headless at all. Mount them, or they live and die with the
# container — a hub that forgets every key it issued on restart is worse than one that will not start.
ENV MCPHUB_CONFIG_DIR=/config MCPHUB_DATA_DIR=/data MCPHUB_PROXY_BIND=0.0.0.0 MCPHUB_PROXY_PORT=5800
RUN mkdir -p /config /data && chown -R $APP_UID:$APP_UID /config /data
VOLUME ["/config", "/data"]

USER $APP_UID
EXPOSE 5800
ENTRYPOINT ["dotnet", "MCPHub.Server.dll"]
