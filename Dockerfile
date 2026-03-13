# === Build stage ===
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# Copy solution and project files first for layer caching
COPY *.sln Directory.Packages.props Directory.Build.props ./
COPY src/CodeMap.Core/*.csproj src/CodeMap.Core/
COPY src/CodeMap.Git/*.csproj src/CodeMap.Git/
COPY src/CodeMap.Roslyn/*.csproj src/CodeMap.Roslyn/
COPY src/CodeMap.Storage/*.csproj src/CodeMap.Storage/
COPY src/CodeMap.Query/*.csproj src/CodeMap.Query/
COPY src/CodeMap.Mcp/*.csproj src/CodeMap.Mcp/
COPY src/CodeMap.Daemon/*.csproj src/CodeMap.Daemon/
RUN dotnet restore src/CodeMap.Daemon/CodeMap.Daemon.csproj

# Copy source and publish
COPY src/ src/
RUN dotnet publish src/CodeMap.Daemon -c Release -o /app/publish --no-restore

# === Runtime stage ===
# NOTE: Using the SDK image (not runtime-only) because MSBuildWorkspace requires
# MSBuild at runtime for Roslyn compilation (index.ensure_baseline). The SDK image
# is larger (~800MB) but is the simplest approach.
#
# Alternatives:
# (b) Copy MSBuild from build stage into the runtime image — more complex, smaller.
# (c) Use runtime image + pre-built baselines from shared cache only (no indexing).
FROM mcr.microsoft.com/dotnet/sdk:9.0
WORKDIR /app

COPY --from=build /app/publish .

# Default cache directory (mount a volume here for persistence)
ENV CODEMAP_CACHE_DIR=/cache

# Volume mount points:
#   /repo   — source repository (read-only recommended)
#   /cache  — shared baseline cache (persistent, read-write)
VOLUME ["/repo", "/cache"]

# MCP protocol uses stdin/stdout. Run with: docker run -i codemap-mcp
ENTRYPOINT ["dotnet", "CodeMap.Daemon.dll"]
