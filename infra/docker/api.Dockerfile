# Build context: repository root (see infra/docker-compose.yml)
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY backend/ ./backend/
RUN dotnet restore backend/ClipForge.sln \
 && dotnet publish backend/src/ClipForge.Api/ClipForge.Api.csproj -c Release -o /app /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
RUN apt-get update && apt-get install -y --no-install-recommends wget && rm -rf /var/lib/apt/lists/* \
 && mkdir -p /data/storage && chown -R app:app /data
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8080 \
    Kestrel__Endpoints__Http__Url=http://0.0.0.0:8080 \
    DOTNET_RUNNING_IN_CONTAINER=true
EXPOSE 8080
USER app
ENTRYPOINT ["dotnet", "ClipForge.Api.dll"]
