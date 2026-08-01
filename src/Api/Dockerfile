# Build context is ./src (so Contracts/ and Data/ project references resolve).
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY ["Contracts/Contracts.csproj", "Contracts/"]
COPY ["Data/Data.csproj", "Data/"]
COPY ["Api/Api.csproj", "Api/"]
RUN dotnet restore "Api/Api.csproj"
COPY Contracts/ Contracts/
COPY Data/ Data/
COPY Api/ Api/
RUN dotnet publish "Api/Api.csproj" -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble AS final
WORKDIR /app
RUN apt-get update && apt-get install -y --no-install-recommends \
    software-properties-common \
    ca-certificates \
    curl \
    && add-apt-repository -y ppa:strukturag/libheif \
    && apt-get update \
    && apt-get install -y --no-install-recommends \
        ffmpeg \
        libheif1 \
        libheif-examples \
        libheif-plugin-libde265 \
        libheif-plugin-aomdec \
        libheif-plugin-aomenc \
        libimage-exiftool-perl \
    && rm -rf /var/lib/apt/lists/*
EXPOSE 8080
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "Api.dll"]
