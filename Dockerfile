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
WORKDIR /src
COPY Directory.Build.props ./
COPY src/ ./src/
RUN dotnet publish src/AgentCore.Api/AgentCore.Api.csproj -c Release -o /app/publish --no-self-contained

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /app/publish ./
COPY --from=web /src/web/dist ./wwwroot
COPY agents ./agents
RUN mkdir -p /data /data/definition-resources /app/data/workspaces /app/data/attachments /app/data/artifacts \
    && chown -R $APP_UID:$APP_UID /data /app/data
USER $APP_UID
ENV ASPNETCORE_URLS=http://0.0.0.0:8080
ENV AgentCore__Profile=Synthetic
ENV AgentCore__AgentDirectory=/app/agents
ENV Persistence__Provider=Sqlite
ENV Persistence__ConnectionString=Data Source=/data/agent-core.db
ENV Persistence__AgentWorkspaceRoot=/data/agent-workspaces
ENV Persistence__DefinitionResourceRoot=/data/definition-resources
ENV Providers__LanguageModels__primary-llm__Adapter=Scripted
# The image does not install Playwright Chromium. Keep browser tools off until that runtime is packaged.
ENV Browser__Enabled=false
EXPOSE 8080
ENTRYPOINT ["dotnet", "AgentCore.Api.dll"]
