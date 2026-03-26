FROM mcr.microsoft.com/dotnet/sdk:9.0-alpine AS build
WORKDIR /src

COPY ["DeployKit.DeployServer.csproj", "./"]
RUN dotnet restore "DeployKit.DeployServer.csproj"

COPY . .

RUN dotnet publish "DeployKit.DeployServer.csproj" -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:9.0-alpine AS final
WORKDIR /app

COPY --from=build /app/publish .

# Persistent data directory for SQLite database (mount Railway Volume at /data)
RUN mkdir -p /data
ENV Database__Path=/data/deploy.db

EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
ENV ASPNETCORE_ENVIRONMENT=Production

ENTRYPOINT ["dotnet", "DeployKit.DeployServer.dll"]
