# syntax=docker/dockerfile:1

# ---------- build ----------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/TaskHive.Core/TaskHive.Core.csproj src/TaskHive.Core/
COPY src/TaskHive.Web/TaskHive.Web.csproj src/TaskHive.Web/
RUN dotnet restore src/TaskHive.Web/TaskHive.Web.csproj

COPY src/ src/
RUN dotnet publish src/TaskHive.Web/TaskHive.Web.csproj -c Release --no-restore -o /app/publish /p:UseAppHost=false

# ---------- runtime ----------
# Chiseled: no shell or package manager, runs as the non-root "app" user.
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled AS runtime
WORKDIR /app

ENV ASPNETCORE_HTTP_PORTS=8080 \
    ConnectionStrings__DefaultConnection="Data Source=/home/app/data/taskhive.db;Cache=Shared" \
    DataProtection__KeysPath=/home/app/data/keys \
    Database__MigrateOnStartup=true

COPY --from=build /app/publish .

# Mount a volume here to keep the SQLite database and cookie encryption keys across restarts.
VOLUME ["/home/app/data"]
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "TaskHive.Web.dll"]
