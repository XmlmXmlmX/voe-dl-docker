# ──────────────────────────────────────────────────────────────────────────────
# Stage 1 – build the .NET application
# ──────────────────────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build

WORKDIR /src

# Restore dependencies first (layer-cache friendly)
COPY src/VoeDl.Web/VoeDl.Web.csproj src/VoeDl.Web/
COPY src/VoeDl.ServiceDefaults/VoeDl.ServiceDefaults.csproj src/VoeDl.ServiceDefaults/
RUN dotnet restore src/VoeDl.Web/VoeDl.Web.csproj

# Copy the rest of the source and publish
COPY src/ src/
RUN dotnet publish src/VoeDl.Web/VoeDl.Web.csproj \
    -c Release \
    -o /app/publish \
    --no-restore

# Some container build environments publish framework static assets via endpoint
# metadata only, which can lead to missing physical /wwwroot/_framework files.
# Ensure blazor.web.js exists so clients can always bootstrap Blazor Server.
RUN if [ ! -f /app/publish/wwwroot/_framework/blazor.web.js ]; then \
            echo "blazor.web.js missing from publish output, copying fallback asset"; \
            mkdir -p /app/publish/wwwroot/_framework; \
            ASSET_PATH="$(find /root/.nuget/packages/microsoft.aspnetcore.app.internal.assets -path '*/_framework/blazor.web.js' | head -n 1)"; \
            if [ -n "$ASSET_PATH" ]; then \
                cp "$ASSET_PATH" /app/publish/wwwroot/_framework/blazor.web.js; \
            else \
                echo "ERROR: could not locate fallback blazor.web.js in NuGet cache"; \
                exit 1; \
            fi; \
        fi

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

EXPOSE 8080

ENTRYPOINT ["dotnet", "VoeDl.Web.dll"]

