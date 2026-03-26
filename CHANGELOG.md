# Changelog

All notable changes to this project will be documented in this file.

---

## [Unreleased] - Fix HTTP 400 and blazor.web.js 404 in Docker / TrueNAS Scale

### Fixed
- **HTTP 400 on button clicks** when the container is deployed behind a reverse proxy
  (e.g. TrueNAS Scale, Traefik).  
  Added `ForwardedHeaders` middleware configuration in `Program.cs` so that
  ASP.NET Core's antiforgery validation uses the real public-facing scheme and host
  reported by the proxy rather than the container-internal values.  The middleware is
  now applied as the very first step in the request pipeline, before the exception
  handler, HSTS and antiforgery checks.
- **`_framework/blazor.web.js` 404** in .NET 10 deployments.  
  Removed the `@Assets[…]` fingerprinting wrapper from the framework script tag in
  `App.razor`.  Using `@Assets` for this file triggers a manifest lookup that can
  fail in .NET 10 (see [dotnet/aspnetcore#63962](https://github.com/dotnet/aspnetcore/issues/63962));
  the Blazor runtime registers its own dedicated endpoint for `_framework/blazor.web.js`
  so a direct path reference is both correct and sufficient.
- Changed `ENV DOWNLOAD_PATH=/downloads` to `ENV DOWNLOAD_DIR=/downloads` in the `Dockerfile`.
  `DOWNLOAD_DIR` takes precedence over `DOWNLOAD_PATH` in the lookup chain, so the container's
  default download directory is now always `/downloads` even when a user overrides `DOWNLOAD_PATH`
  with a relative value (e.g. `./mydownloads`).  Relative values for `DOWNLOAD_PATH` were
  previously resolved against the container's `WORKDIR` (`/app`), causing files to land in
  `/app/<relative-path>` instead of the expected location.

### Changed
- Updated `README.md` to recommend `DOWNLOAD_DIR` (instead of `DOWNLOAD_PATH`) for TrueNAS /
  direct Docker setups, and added a note that both variables must be **absolute paths** when used
  as container environment variables.

---

## [v1.7.3] - Documentation Updates
**Release Date**: 2025-05-28

### Added
- Updated `README.md` for improved clarity and structure.

### Changed
- Improved formatting and consistency across documentation files.

---

## [v1.7.2] - Upstream Merge & Sync
**Release Date**: 2025-05-06

### Added
- Merged latest changes from `upstream/main`, including upstream's implementation of Method 7.

### Removed
- Redundant upstream Method 7 code block — already present and documented in earlier implementation.

### Changed
- Codebase synced with upstream while preserving local improvements and structure.

---

## [v1.7.1] - Improved Bait Detection
**Release Date**: 2025-05-06

### Added
- Enhanced bait detection logic to include additional patterns for filenames and domains.

---

## [v1.7.0] - Method 8 for Source Detection
**Release Date**: 2025-05-06

### Added
- Implemented Method 8 for source detection, contributed by @Domkeykong.
  - Decoding process includes multiple steps: ROT13, pattern replacement, Base64 decoding, character shifting, reversing, and final Base64 decoding.
  - Handles obfuscated JSON sources embedded in `<script type="application/json">` tags.
  - Added helper functions for decoding and deobfuscation.

### Changed
- Updated `help()` function to include Method 8 in the version history.
- Improved error handling for obfuscated JSON parsing.

---

## [v1.6.0] - Enhanced Source Detection with Method 7
**Release Date**: 2025-04-21

### Added
- Implemented Method 7 for source detection, contributed by @th3infinity and @ottobauer.
  - Decoding process includes multiple steps: ROT13, pattern replacement, underscore removal, Base64 decoding, character shifting, reversing, and final Base64 decoding.
  - Enhanced handling of encrypted and hidden sources.

### Changed
- Improved robustness of source detection logic to accommodate evolving site structures.

---

## [v1.5.1] - Documentation Updates
**Release Date**: 2025-04-06

### Added
- Updated `README.md` with detailed usage instructions, including examples for single and batch downloads.
- Added information about the `-w` argument for parallel downloads.
- Included troubleshooting tips and common error fixes.

### Changed
- Improved the `help()` function to include a description of the `-w` argument.

---

## [v1.5.0] - Improved Source Detection and Bait Handling
**Release Date**: 2025-04-06

### Added
- Introduced functionality to identify and ignore "bait" sources using a predefined list.
- Enhanced Method 6 to support additional patterns like `var a168c = '...'` for extracting encoded sources.
- Added a `clean_base64` function to safely handle and validate Base64 strings.

### Changed
- Improved error handling and debugging output for better traceability.
- Ensured flexibility by allowing easy extension of "bait" patterns and encoded source patterns.

---

## [v1.4.0] - Forked by MPZ-00
**Release Date**: 2025-01-14

### Changed
- General improvements and fixes to the downloader script.

---

## [v1.3.1] - Forked by HerobrineTV
**Release Date**: 2025-03-15

### Fixed
- Resolved issues with finding download links.
- Updated the script to handle changes in voe.sx URLs, including Base64-encoded keys.
- Added more user agents to better mimic browser behavior.

---

## [v1.2.4] - Initial Stable Release
**Release Date**: 2024-07-15

### Added
- Basic functionality to download single and multiple videos from `voe.sx`.
- Support for batch downloads using a file with links.
- Output videos saved in the same folder where the script is executed.

---

## [v1.2.3] - Help Description Updated
**Release Date**: 2024-07-14

### Changed
- Revised the help descriptions to provide clearer guidance on the tool's usage.

---

## [v1.2.2] - Site Update Compatibility
**Release Date**: 2024-04-21

### Fixed
- Adjusted the script to accommodate changes made to the voe.sx site.

---

## [v1.2.1] - Site Structure Adaptation
**Release Date**: 2024-02-26

### Fixed
- Updated the script in response to structural changes on the voe.sx site to ensure continued functionality.

---

## [v1.2.0] - Site Structure Update
**Release Date**: 2024-02-23

### Fixed
- Modified the script to align with recent changes to the voe.sx site.

---

## [v1.1.1] - Extended Support for Embedded Links
**Release Date**: 2023-05-11

### Added
- Added functionality to handle embedded voe.sx links, expanding the tool's versatility.

---

## [v1.1.0] - HLS Stream Download Support
**Release Date**: 2023-04-21

### Added
- Introduced support for downloading HLS streams from voe.sx, addressing the removal of direct MP4 links from the site.

---

## [v1.0.1] - Bug Fix
**Release Date**: 2023-02-24

### Fixed
- Implemented a minor bug fix caused by changes on the voe.sx platform to maintain download reliability.

---

## [v1.0.0] - Initial Release
**Release Date**: 2023-01-18

### Added
- Launched the first version of `voe-dl`, a Python-based downloader for voe.sx videos.
- Provided both Windows and Linux executables.
