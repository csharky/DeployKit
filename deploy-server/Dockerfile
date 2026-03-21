FROM mcr.microsoft.com/dotnet/sdk:9.0-alpine AS build
WORKDIR /src

COPY ["DeployKit.DeployServer.csproj", "./"]
RUN dotnet restore "DeployKit.DeployServer.csproj"

COPY . .

RUN dotnet publish "DeployKit.DeployServer.csproj" -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:9.0-alpine AS final
WORKDIR /app

COPY --from=build /app/publish .

EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
ENV ASPNETCORE_ENVIRONMENT=Production

ENTRYPOINT ["dotnet", "DeployKit.DeployServer.dll"]
