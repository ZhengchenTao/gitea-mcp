# syntax=docker/dockerfile:1.6
# ── Stage 1: build ──────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS builder

WORKDIR /src

# 先复制 csproj，单独 restore（利用层缓存）
COPY gitea-mcp.csproj .
RUN --mount=type=cache,target=/root/.nuget/packages,sharing=locked \
    dotnet restore gitea-mcp.csproj

# 复制剩余源码并发布
COPY . .
RUN --mount=type=cache,target=/root/.nuget/packages,sharing=locked \
    dotnet publish gitea-mcp.csproj \
    -c Release \
    -o /app/publish \
    --no-restore

# ── Stage 2: runtime ────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime

# OCI 标签（CI 会在 build-push 时注入 source 和 revision）
LABEL org.opencontainers.image.title="gitea-mcp"
LABEL org.opencontainers.image.description="MCP server exposing Gitea REST API via OAuth-issued JWT"
LABEL org.opencontainers.image.licenses="MIT"

WORKDIR /app

# 非 root 用户运行（最小权限）。
# 先建用户、再 COPY --chown，确保拷进来的文件归属正确（不能依赖默认 644 让 appuser 兜底读）。
RUN useradd --system --no-create-home --shell /usr/sbin/nologin appuser
COPY --from=builder --chown=appuser:appuser /app/publish .
USER appuser

# 容器内监听 0.0.0.0:8080，宿主机映射到 9092
ENV ASPNETCORE_URLS=http://0.0.0.0:8080
ENV ASPNETCORE_ENVIRONMENT=Production

EXPOSE 8080

ENTRYPOINT ["dotnet", "gitea-mcp.dll"]
