# ──────────────────────────────────────────────────────────────────────────────
# Stage 1 – build the .NET application
# ──────────────────────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build

WORKDIR /src

# Restore dependencies first (layer-cache friendly)
COPY src/VoeDl.Web/VoeDl.Web.csproj src/VoeDl.Web/
RUN dotnet restore src/VoeDl.Web/VoeDl.Web.csproj

# Copy the rest of the source and publish
COPY src/ src/
RUN dotnet publish src/VoeDl.Web/VoeDl.Web.csproj \
    -c Release \
    -o /app/publish \
    --no-restore

# ──────────────────────────────────────────────────────────────────────────────
# Stage 2 – runtime image
# ──────────────────────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final

# Install Python 3, pip, ffmpeg and yt-dlp for the actual media downloading
RUN apt-get update \
    && apt-get install -y --no-install-recommends \
        python3 \
        python3-pip \
        ffmpeg \
    && pip3 install --no-cache-dir yt-dlp --break-system-packages \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app
COPY --from=build /app/publish .

# Directory for downloaded files (mount a host volume here)
RUN mkdir -p /downloads
ENV DOWNLOAD_DIR=/downloads

# Bake the app version into the image at build time.
# Pass --build-arg APP_VERSION=<tag> when building, e.g. via CI.
ARG APP_VERSION=dev
ENV APP_VERSION=${APP_VERSION}

EXPOSE 5000

ENTRYPOINT ["dotnet", "VoeDl.Web.dll"]

