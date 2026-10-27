# Ludo Crown game server — container image.
# Build from the repository root:  docker build -f deploy/Dockerfile -t ludocrown-server .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props ./
COPY src/LudoCrown.Core/ src/LudoCrown.Core/
COPY src/LudoCrown.Server.Data/ src/LudoCrown.Server.Data/
COPY src/LudoCrown.Server.Postgres/ src/LudoCrown.Server.Postgres/
COPY src/LudoCrown.Server/ src/LudoCrown.Server/
COPY db/ db/
RUN dotnet publish src/LudoCrown.Server/LudoCrown.Server.csproj -c Release -o /app /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app ./
ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_RUNNING_IN_CONTAINER=true
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "LudoCrown.Server.dll"]
