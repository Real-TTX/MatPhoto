# syntax=docker/dockerfile:1

# ---------------------------------------------------------------------------
# Build stage
# ---------------------------------------------------------------------------
# The build runs on the builder's own platform: the published app is portable (no native host), so the arm64 image needs no emulation here.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG Channel=local
ARG BuildNumber=0
ARG BuildDate=
WORKDIR /src

# Restore first (better layer caching).
COPY ["global.json", "Directory.Build.props", "./"]
COPY ["src/MatPhoto/MatPhoto.csproj", "src/MatPhoto/"]
RUN dotnet restore "src/MatPhoto/MatPhoto.csproj"

COPY src src
# Fall back to today's date when BuildDate is not supplied, so the version never ends in "-".
RUN BUILD_DATE="${BuildDate:-$(date -u +%Y%m%d)}" && \
    dotnet publish "src/MatPhoto/MatPhoto.csproj" \
    -c Release -o /app/publish --no-restore \
    -p:UseAppHost=false \
    -p:Channel=$Channel -p:BuildNumber=$BuildNumber -p:BuildDate=$BUILD_DATE

# ---------------------------------------------------------------------------
# Runtime stage (ASP.NET Core base image)
# ---------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime

LABEL org.opencontainers.image.title="MatPhoto" \
      org.opencontainers.image.description="Fotos und Videos aus deinen Speichern (NAS, SMB, WebDAV): Alben als Ordner, Listen, Orte, Duplikate und ein kluger Import" \
      org.opencontainers.image.source="https://github.com/Real-TTX/MatPhoto"

# ffmpeg reads the videos (their facts and a frame for the thumbnail); libgomp1 is the OpenMP of ImageMagick (Magick.NET brings ImageMagick itself, with HEIC and RAW)
RUN apt-get update \
    && apt-get install -y --no-install-recommends ffmpeg libgomp1 \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app

ENV ASPNETCORE_URLS=http://+:4830 \
    HTTP_PORTS="" \
    ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_gcServer=0 \
    TZ=Europe/Berlin

EXPOSE 4830

COPY --from=build /app/publish .

# One volume for what the app keeps in files: the keys that protect the sessions, the cache of the thumbnails (unless a storage holds it), the uploads of an import.
# The database is the other container. /storage is where folders of the NAS are mounted (bind mounts) for storages of the kind "Local folder".
RUN mkdir -p /data/keys /data/cache /data/import /data/tmp /storage && chown -R $APP_UID:$APP_UID /data
VOLUME /data
USER $APP_UID

# The app checks itself on /api/health (200 for ok and degraded, 503 for down), so the image needs no curl.
HEALTHCHECK --interval=30s --timeout=8s --start-period=30s --retries=3 \
    CMD ["dotnet", "MatPhoto.dll", "--healthcheck"]

ENTRYPOINT ["dotnet", "MatPhoto.dll"]
