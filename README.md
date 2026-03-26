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

### Environment Variables

| Variable | Default | Description |
|----------|---------|-------------|
| `DOWNLOAD_PATH` | `./downloads` | Host-side path for the volume mount in `docker compose`. Can also be used as the **container** download path in direct Docker / TrueNAS setups — must be an **absolute path** when used this way. |
| `DOWNLOAD_DIR` | `/downloads` | Container download path (overrides `DOWNLOAD_PATH` when both are set). Always use an absolute path. |
| `DOWNLOAD_TIMEOUT` | `3600` | Max download time in seconds before a job is cancelled |
| `CREATE_SUBFOLDER` | `0` | Set to `1` to save each download in its own sub-directory named after the video title |
| `APP_VERSION` | `dev` | Application version label shown in the footer |

---

## 📂 Output

Downloaded videos are saved to the configured download directory (default `/downloads` inside the container, mapped to `./downloads` on the host).

---

## 💡 Contributing

Pull requests are welcome! If you fix a bug or add a feature, please update the README accordingly.
