FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

COPY ["DeployKit.DeployAgent.csproj", "./"]
RUN dotnet restore "DeployKit.DeployAgent.csproj"

COPY . .

RUN dotnet publish "DeployKit.DeployAgent.csproj" -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/runtime:9.0 AS final
WORKDIR /app

# Install additional build tools as needed, e.g.:
# RUN apt-get update && apt-get install -y --no-install-recommends \
#     gradle nodejs npm \
#     && rm -rf /var/lib/apt/lists/*

COPY --from=build /app/publish .

ENTRYPOINT ["dotnet", "DeployKit.DeployAgent.dll"]
