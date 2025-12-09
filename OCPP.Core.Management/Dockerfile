# Build stage
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

ARG BUILD_CONFIGURATION=Release

COPY OCPP.Core.sln ./
COPY OCPP.Core.Management/OCPP.Core.Management.csproj OCPP.Core.Management/
COPY OCPP.Core.Database/OCPP.Core.Database.csproj OCPP.Core.Database/

RUN dotnet restore OCPP.Core.Management/OCPP.Core.Management.csproj

COPY . .

RUN dotnet publish OCPP.Core.Management/OCPP.Core.Management.csproj -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

# runtime image
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app

ENV ASPNETCORE_URLS="http://+:8082"
ENV ASPNETCORE_ENVIRONMENT="Production"

EXPOSE 8082

COPY --from=build /app/publish .

ENTRYPOINT ["dotnet", "OCPP.Core.Management.dll"]
