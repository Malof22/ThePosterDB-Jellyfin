# Jellyfin PosterDB Plugin

A Jellyfin plugin that integrates [ThePosterDB](https://theposterdb.com) as a remote image provider for movies, series and collections.

## Features

- Browse and apply posters from ThePosterDB directly from Jellyfin's "Edit Images" panel
- Supports Movies, TV Shows and Collections
- Paginated poster fetching (all pages, not just the first 24)
- 5-minute result cache to speed up repeat searches
- Parallel fetching for fast results

## Requirements

- Jellyfin 12.x
- A [ThePosterDB](https://theposterdb.com) account (free)

## Installation

### Via Jellyfin Plugin Repository (recommended)

1. In Jellyfin, go to **Dashboard → Plugins → Repositories**
2. Add a new repository with this URL:
   ```
   https://raw.githubusercontent.com/Malof22/ThePosterDB-Jellyfin/main/manifest.json
   ```
3. Go to **Dashboard → Plugins → Catalog**, find **PosterDB** and install it
4. Restart Jellyfin

### Manual installation

1. Download the latest `posterdb_x.x.x.x.zip` from the [Releases](../../releases) page
2. Extract the contents into `<JellyfinDataDir>/plugins/PosterDB_x.x.x.x/`
   - Docker: `/config/plugins/PosterDB_x.x.x.x/`
3. Restart Jellyfin

## Configuration

1. Go to **Dashboard → Plugins → PosterDB**
2. Enter your ThePosterDB email and password
3. Click **Save**, then **Test Connection**
4. Use **Edit Images** on any movie or series to browse posters

## Development

```bash
# Build
dotnet build --configuration Release

# Publish release artifacts
dotnet publish Jellyfin.Plugin.PosterDB/Jellyfin.Plugin.PosterDB.csproj \
  --configuration Release \
  --output ./dist
```

## Releasing

Tag a commit with `v1.x.x.x` to trigger the GitHub Actions release workflow. The workflow will:
1. Build and package the plugin as a ZIP
2. Create a GitHub Release with the ZIP attached
3. Update `manifest.json` with the new version and checksum

## License

MIT
