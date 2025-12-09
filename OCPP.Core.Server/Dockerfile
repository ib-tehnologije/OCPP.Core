# Build stage
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

ARG BUILD_CONFIGURATION=Release

# copy project references for restore
COPY OCPP.Core.sln ./
COPY OCPP.Core.Server/OCPP.Core.Server.csproj OCPP.Core.Server/
COPY OCPP.Core.Database/OCPP.Core.Database.csproj OCPP.Core.Database/
COPY OCPP.Core.Server.Extensions/OCPP.Core.Server.Extensions.csproj OCPP.Core.Server.Extensions/

RUN dotnet restore OCPP.Core.Server/OCPP.Core.Server.csproj

# copy the rest of the source
COPY . .

RUN dotnet publish OCPP.Core.Server/OCPP.Core.Server.csproj -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

# runtime image
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app

# Allow overriding listening address and environment via runtime configuration
ENV ASPNETCORE_URLS="http://+:8081"
ENV ASPNETCORE_ENVIRONMENT="Production"

EXPOSE 8081

COPY --from=build /app/publish .

ENTRYPOINT ["dotnet", "OCPP.Core.Server.dll"]
