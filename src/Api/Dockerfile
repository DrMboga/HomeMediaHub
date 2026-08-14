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
# The libheif PPA is wired up by hand rather than with `add-apt-repository`: that tool resolves
# the PPA through api.launchpad.net, which the server can only reach over IPv6 — and the build
# container is IPv4-only, so it hangs until it times out. ppa.launchpadcontent.net is fine on IPv4.
# curl stays in the final image — the compose healthcheck uses it.
RUN apt-get update && apt-get install -y --no-install-recommends \
    ca-certificates \
    curl \
    && install -d -m 0755 /etc/apt/keyrings \
    && curl -fsSL "https://keyserver.ubuntu.com/pks/lookup?op=get&options=mr&search=0x47E92B9C13C97BB081F5FFC1717DB416116004C9" \
        -o /etc/apt/keyrings/strukturag-libheif.asc \
    && echo "deb [signed-by=/etc/apt/keyrings/strukturag-libheif.asc] https://ppa.launchpadcontent.net/strukturag/libheif/ubuntu noble main" \
        > /etc/apt/sources.list.d/strukturag-libheif.list \
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
