# syntax=docker/dockerfile:1

FROM node:22-bookworm-slim AS web
WORKDIR /src/web
RUN corepack enable
COPY web/package.json web/pnpm-lock.yaml ./
RUN pnpm install --frozen-lockfile
COPY web/ ./
# Vite reads this at image build time. Unset stays MessagePack. A later container
# environment variable does not switch the already-built SPA.
ARG VITE_AGENTCORE_REALTIME_PROTOCOL
ENV VITE_AGENTCORE_REALTIME_PROTOCOL=$VITE_AGENTCORE_REALTIME_PROTOCOL
RUN pnpm run build

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG AGENTCORE_SOURCE_REVISION=unrecorded
WORKDIR /src
# Follow the shared SDK policy; rebuild with --pull to pick up serviced 10.0 images.
COPY global.json Directory.Build.props ./
COPY src/ ./src/
RUN dotnet publish src/AgentCore.Api/AgentCore.Api.csproj -c Release -o /app/publish --no-self-contained "-p:SourceRevisionId=$AGENTCORE_SOURCE_REVISION"
# Browser is unsupported in this image (no Chromium). Do not ship its unused
# bundled Node/driver runtime; native development retains the NuGet driver.
RUN rm -rf /app/publish/.playwright

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
ARG AGENTCORE_SOURCE_REVISION=unrecorded
LABEL org.opencontainers.image.revision=$AGENTCORE_SOURCE_REVISION
ARG AGENTCORE_RUNTIME_UID=1654
ARG AGENTCORE_RUNTIME_GID=1654
WORKDIR /app
COPY --from=build /app/publish ./
COPY --from=web /src/web/dist ./wwwroot
COPY agents ./agents
RUN mkdir -p /data /data/definition-resources /app/data/workspaces /app/data/attachments /app/data/artifacts \
    && chmod 700 /data /app/data \
    && chown -R $AGENTCORE_RUNTIME_UID:$AGENTCORE_RUNTIME_GID /data /app/data
USER $AGENTCORE_RUNTIME_UID:$AGENTCORE_RUNTIME_GID
ENV ASPNETCORE_URLS=http://0.0.0.0:8080
ENV AgentCore__Profile=Synthetic
ENV AgentCore__AgentDirectory=/app/agents
ENV Persistence__Provider=Sqlite
ENV Persistence__ConnectionString="Data Source=/data/agent-core.db"
ENV Persistence__DefinitionResourceRoot=/data/definition-resources
ENV Providers__LanguageModels__primary-llm__Adapter=Scripted
# The image does not install Playwright Chromium. Keep browser tools off until that runtime is packaged.
ENV Browser__Enabled=false
EXPOSE 8080
ENTRYPOINT ["dotnet", "AgentCore.Api.dll"]
