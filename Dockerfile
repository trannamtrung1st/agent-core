# syntax=docker/dockerfile:1

FROM node:22-bookworm-slim AS web
WORKDIR /src/web
COPY web/package.json web/package-lock.json ./
RUN npm ci
COPY web/ ./
RUN npm run build

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
ENV ASPNETCORE_URLS=http://0.0.0.0:8080
ENV AgentCore__Profile=Synthetic
ENV AgentCore__AgentDirectory=/app/agents
ENV Persistence__Provider=InMemory
ENV Providers__LanguageModels__primary-llm__Adapter=Scripted
EXPOSE 8080
ENTRYPOINT ["dotnet", "AgentCore.Api.dll"]
