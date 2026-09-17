FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source
COPY global.json Directory.Build.props Directory.Build.targets Directory.Packages.props NuGet.config ./
COPY src/MCPHub.Core/ src/MCPHub.Core/
COPY src/MCPHub.Processes/ src/MCPHub.Processes/
COPY src/MCPHub.Proxy/ src/MCPHub.Proxy/
COPY samples/HeadlessRouter/ samples/HeadlessRouter/
RUN dotnet publish samples/HeadlessRouter/HeadlessRouter.csproj -c Release -o /out --no-self-contained -p:UseAppHost=false -p:DebugType=none -p:DebugSymbols=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /out/ ./
ENV MCPHUB_ROUTER_BIND=0.0.0.0 MCPHUB_ROUTER_CONFIG=/config/router.json
USER $APP_UID
EXPOSE 5801
ENTRYPOINT ["dotnet", "HeadlessRouter.dll"]
