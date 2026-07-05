# Build context is ./src (so Contracts/ and Data/ project references resolve).
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY ["Contracts/Contracts.csproj", "Contracts/"]
COPY ["Data/Data.csproj", "Data/"]
COPY ["MediaWorker/MediaWorker.csproj", "MediaWorker/"]
RUN dotnet restore "MediaWorker/MediaWorker.csproj"
COPY Contracts/ Contracts/
COPY Data/ Data/
COPY MediaWorker/ MediaWorker/
RUN dotnet publish "MediaWorker/MediaWorker.csproj" -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble AS final
WORKDIR /app
# Same libheif PPA package set as the Api image — the worker does all HEIC/video conversion.
RUN apt-get update && apt-get install -y --no-install-recommends \
    software-properties-common \
    ca-certificates \
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
EXPOSE 9090
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "MediaWorker.dll"]
