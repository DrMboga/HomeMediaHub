# Build context is ./src (so Contracts/ project references resolve).
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY ["Contracts/Contracts.csproj", "Contracts/"]
COPY ["GeoService/GeoService.csproj", "GeoService/"]
RUN dotnet restore "GeoService/GeoService.csproj"
COPY Contracts/ Contracts/
COPY GeoService/ GeoService/
RUN dotnet publish "GeoService/GeoService.csproj" -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
EXPOSE 8001
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "GeoService.dll"]
