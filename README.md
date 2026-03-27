# voe-dl

A downloader for videos hosted on [voe.sx](https://voe.sx), now rewritten in **C# / .NET 10** with a **Blazor Server** web interface.

> The original Python implementation is preserved in the [`legacy/`](legacy/) folder for reference.

### 🔄 Release Status

| Source               | Version                                                                  |
|----------------------|--------------------------------------------------------------------------|
| **Upstream**         | [![Upstream Release](https://img.shields.io/github/v/release/p4ul17/voe-dl)](https://github.com/p4ul17/voe-dl/releases) |
| **MPZ-00's Fork**    | [![MPZ-00's Release](https://img.shields.io/github/v/release/MPZ-00/voe-dl)](https://github.com/MPZ-00/voe-dl/releases/latest) |


---

## ⚠️ Always Use the Latest Version

> Voe frequently updates their website to break download scripts.
> **Make sure you are using the latest version** to ensure compatibility.

---

## 🏗 Technology Stack

| Layer | Technology |
|-------|-----------|
| **Backend** | C# / .NET 10 |
| **Frontend** | Blazor Server (real-time via SignalR) |
| **HTML parsing** | AngleSharp |
| **Media download** | yt-dlp (external binary, included in Docker image) |
| **Container** | Docker / docker-compose |

---

## 🐳 Docker Deployment

### Quick Start

```bash
docker compose up -d
```

Downloads are saved to `./downloads` on the host by default.
Access the web UI at `http://localhost:8080`.

### Custom Output Path

Set the `DOWNLOAD_PATH` environment variable to save downloads to any host directory:

```bash
DOWNLOAD_PATH=/mnt/media/movies docker compose up -d
```

Or create a `.env` file next to `docker-compose.yml`:

```env
DOWNLOAD_PATH=/mnt/media/movies
```

Then run:

```bash
docker compose up -d
```

### TrueNAS Scale

In TrueNAS Scale's Docker (or Apps) configuration, set the **container path** for the storage mount and then tell the app about it via the `DOWNLOAD_DIR` environment variable:

1. Add a storage mount in the TrueNAS app UI:
   - **Host path**: your NAS dataset (e.g. `/mnt/pool/dataset/movies`)
   - **Mount path (container path)**: e.g. `/downloads`
2. Set the environment variable `DOWNLOAD_DIR` to match the container mount path:
   ```
   DOWNLOAD_DIR=/downloads
   ```

> ⚠️ Always use an **absolute path** for `DOWNLOAD_DIR` (and `DOWNLOAD_PATH`) when setting them as container environment variables. Relative paths are resolved inside the container and may not point to the expected location.

The app checks `DOWNLOAD_DIR` first, then `DOWNLOAD_PATH`, so you only need to set one of them.

To avoid antiforgery/session errors after app restarts, also persist ASP.NET Data Protection keys:

1. Add a second storage mount in the TrueNAS app UI:
   - **Host path**: a persistent dataset/folder (e.g. `/mnt/pool/appdata/voedl-dataprotection`)
   - **Mount path (container path)**: `/var/lib/voedl/dataprotection`
2. Set the environment variable:
   ```
   DataProtection__KeysPath=/var/lib/voedl/dataprotection
   ```

If keys are not persisted, existing browser antiforgery cookies can no longer be decrypted after container recreation/redeploy.

### Logging Configuration

> Helpful for troubleshooting issues like "download button does nothing"

In TrueNAS Scale's app UI, add this **Environment Variable** to enable debug logging:

```
Logging__LogLevel__Default=Debug
```

Log levels (least to most verbose):
- `Warning` – only problems and errors
- `Information` (default) – normal operations + warnings
- `Debug` – detailed component state and URL resolution

**Example**: To debug only VoeDl.Web components:
```
Logging__LogLevel__VoeDl.Web=Debug
Logging__LogLevel__Microsoft.AspNetCore=Warning
```

Once set, **restart the app** and check the container logs in TrueNAS.

### Troubleshooting on TrueNAS

#### "Database connection configured: False" or "DOWNLOAD_DIR not set"

If the logs show that environment variables are not being read, you need to **explicitly set them** in TrueNAS:

1. **Open the App Configuration** in TrueNAS Scale → edit your voe-dl app
2. **Go to Environment Variables** section
3. **Add these variables** (update paths to match your setup):
   ```
   DOWNLOAD_DIR=/downloads
   DataProtection__KeysPath=/var/lib/voedl/dataprotection
   ConnectionStrings__DefaultConnection=Host=db;Database=voedl;Username=voedl;Password=<your-password>
   ```
4. **Add the storage mounts** to match the paths above
5. **Restart the app**
6. **Check logs again** – should now show the resolved paths

**Note**: The `docker-compose.yml` defaults only apply when using `docker compose up`. In TrueNAS, you must set all variables explicitly.

#### "Antiforgery token could not be decrypted" after restart

Make sure you've added the **DataProtection volume mount**:
- Host path: `/mnt/pool/appdata/voedl-dataprotection` (or your chosen location)
- Container path: `/var/lib/voedl/dataprotection`
- Set env: `DataProtection__KeysPath=/var/lib/voedl/dataprotection`

Without this, encryption keys are lost on container restart and old tokens can't be decrypted.

### Environment Variables

| Variable | Default | Description |
|----------|---------|-------------|
| `DOWNLOAD_PATH` | `./downloads` | Host-side path for the volume mount in `docker compose`. Can also be used as the **container** download path in direct Docker / TrueNAS setups — must be an **absolute path** when used this way. |
| `DOWNLOAD_DIR` | `/downloads` | Container download path (overrides `DOWNLOAD_PATH` when both are set). Always use an absolute path. |
| `DOWNLOAD_TIMEOUT` | `3600` | Max download time in seconds before a job is cancelled |
| `MAX_CONCURRENT_DOWNLOADS` | `3` | Maximum number of downloads running in parallel; additional jobs stay queued |
| `CREATE_SUBFOLDER` | `0` | Set to `1` to save each download in its own sub-directory named after the video title |
| `WRITE_TVSHOW_NFO` | `0` | Set to `1` to additionally write `tvshow.nfo` in the series root folder for TV episode downloads |
| `APP_VERSION` | `dev` | Application version label shown in the footer |
| `DATAPROTECTION_PATH` | `./dataprotection` | Host-side path for persisting ASP.NET Core Data Protection keys in `docker compose`. |
| `DataProtection__KeysPath` | `/var/lib/voedl/dataprotection` | Container path used by ASP.NET Core for Data Protection key storage. Must point to persistent storage in Docker/TrueNAS. |
| `Logging__LogLevel__Default` | `Information` | Set to `Debug` on TrueNAS to see detailed troubleshooting logs. |
| `Logging__LogLevel__Microsoft.AspNetCore` | `Warning` | ASP.NET framework logging level. Set to `Information` or `Debug` only when debugging. |

---

## 📂 Output

Downloaded videos are saved to the configured download directory (default `/downloads` inside the container, mapped to `./downloads` on the host).

---

## 💡 Contributing

Pull requests are welcome! If you fix a bug or add a feature, please update the README accordingly.
