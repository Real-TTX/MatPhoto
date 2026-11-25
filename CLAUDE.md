# CLAUDE.md

Guidance for working in this repository. Keep it in sync with the code: update it whenever architecture, interfaces or conventions change.

## What MatPhoto is

A photo library in the browser (like the photo apps people know) that leaves the photos where they are: on **storages** – folders of the server (bind mounts of a
NAS below `/storage`) and shares over **SMB, WebDAV, SFTP, FTP** (the providers of MatFile/MatDrive). A storage that is a *library* is **scanned** (sync, every
`ScanIntervalMinutes`): every folder with photos or videos is an **album** – the album IS the folder, its name is human readable (`2023.11.09 Hochzeit Julius`, the
pattern `AlbumDateFormat`), renaming the album renames the folder. **Lists** (`Collection`) link photos of any album for a project, links only. **Shared links** (`Share`, `/s/{token}`) show one album or list to people without an account. Thumbnails and previews
are made once (ImageMagick via Magick.NET: JPEG, HEIC, PNG, WebP, AVIF, TIFF, RAW; ffmpeg for videos) and kept in the **cache**: the data volume or a folder of a
storage (the NAS), keyed by the content hash. **Explore** makes automatic albums: places (offline reverse geocoding with GeoNames, `Resources/Geo`), types
(videos, screenshots, selfies, panoramas, Live Photos, animations, RAW, no date, no place), years, cameras. **Duplicates**: the same file (SHA-256) and similar pictures
(64-bit dHash). The **smart import** (upload or a folder of a storage) reads every file, marks duplicates and suggests albums by time, place and source folder; the
person checks the names and MatPhoto copies or moves the files into folders. Nothing is ever deleted for good: "delete" moves into `.matphoto-trash/` of the storage.

Part of the Mat family on **MatPlatform 2.4** (MatDesign `docs/APP-MODEL.md`): MatPhoto **consumes** `identity.oidc` (sign-in through **MatIdentity**) and
`mail.smtp` (**MatSMTP**), both optional; it **contributes** the launcher Photos (quick actions Import, Albums, Explore, Favourites) and two admin widgets. **People**:
faces are found and grouped into people on the server itself (YuNet + SFace of the OpenCV model zoo with ONNX Runtime, CPU only; off until an administrator switches
it on). Other AI features are not part of it yet.

Stack: ASP.NET Core on **.NET 10**, C#, **Razor Pages**, EF Core + **PostgreSQL**, the **MatUi** toolkit of MatDesign (`wwwroot/matui`, release **0.12.0**,
bundled from `origin/main`: never edit by hand), plain JavaScript ES modules without a build step. Magick.NET (ImageMagick), MetadataExtractor (EXIF/XMP/QuickTime),
ffmpeg/ffprobe in the image, SMBLibrary, SSH.NET, FluentFTP. Docker-first. Web port **4830** (pgAdmin 4831, local Postgres 4832). UI languages: English and German
(catalog in `Resources/Texts/*.json`, German with "du"). Accent: orchid `#b81f8a` (white text 5.8:1).

## Build / run / test

- **Build:** `dotnet build MatPhoto.slnx` (SDK 10, `global.json`). Expected: 0 warnings, 0 errors.
- **Tests:** `dotnet test`. Database tests run when `MATPHOTO_TEST_DB` is set (an administrator connection string, e.g.
  `Host=localhost;Port=4832;Username=matphoto;Password=matphoto;Database=postgres` while the local stack runs); each test creates, migrates and drops a database of its own.
  `LibraryDbTests`: scan, visibility, moves, album rename and trash, duplicates, lists, shared links (what a link shows, expiry, withdrawal), people (grouping, naming, joining, "not this person", forgetting). `RawTests`: every RAW extension is handed to LibRaw by its name; `Probe` with `MATPHOTO_RAW_PROBE=<folder of RAW files>` writes their previews into `out/` (size, time, brightness). `LibraryTests`: names, dates, places, scopes, the import suggestion, dHash,
  the indexer with a real JPEG. `TextCatalogTests` checks every text key of the server code and of the scripts (`t('Grid.X')` → `Js.Grid.X`) in English and German.
  `StorageRemoteTests` run against real SFTP/SMB/FTP/WebDAV servers when `MATPHOTO_TEST_STORAGES` is set (see the class).
- **Run / test the app: always rebuild the container** (`./scripts/redeploy.ps1`, `-Clean` for empty volumes). CSS, JS and cshtml are baked into the image.
- **Sample photos:** `dotnet run --project tools/SampleData -- sample-data` (48 files with EXIF dates, GPS, cameras, screenshots, a duplicate, a panorama, an inbox).
  The local compose mounts `./sample-data` as `/storage/sample` (Administration → Storages → New → Local folder `/storage/sample`). `sample-data/` is not committed.
- **Test account (always):** `admin@example.com` / `Start12345` – the first account of a fresh stack (it becomes the administrator).
- **Real tests are end to end in a browser** (Playwright with the installed Edge, `channel: 'msedge'`): scan, grid, viewer (keys, swipe), favourites, lists, import
  (upload and storage folder), duplicates, a phone (390 px), dark mode.
- **Migrations:** `dotnet ef migrations add <Name> -p src/MatPhoto -o Data/Migrations` (local tool in `dotnet-tools.json`; a design-time factory exists). Applied at start-up.
- **Updating MatUi:** take the release of MatDesign (`origin/main`): the CSS is the parts of `src/MatUi/wwwroot/css` joined in the order of the first line of `matui.css`;
  `matui.js`, `matui.boot.js`, `matui-icons.svg` as they are. **MatPlatform:** `node tools/matplatform-copy.js ../MatPhoto/src/MatPhoto` in MatDesign.
- **Models of the face recognition:** `node scripts/models.js` downloads YuNet and SFace into `src/MatPhoto/models` (git-ignored; copied beside the app, the
  tests use them). The Dockerfile downloads them itself (`ADD --checksum`, readable for the app user). `FaceTests.Probe` with `MATPHOTO_FACE_PROBE=<folder of JPEGs>`
  draws the faces it finds into `out/` and prints how alike they are.
- **Places:** `node scripts/geonames.js <folder with the GeoNames dumps>` writes `Resources/Geo` (towns of 15,000+, without the parts of a town).
- **Logo:** `node scripts/logo.js` writes the SVGs (mark, favicon, maskable, mono, logo, logo-dark), `scripts/icons.ps1` the PNGs with Edge headless.
- Write files with the editor tools, not with shell heredocs that contain apostrophes or backslashes.

## Map

```
src/MatPhoto
  Program.cs                 composition root: services, cookie auth (+ MatIdentity), antiforgery header, razor conventions, rate limit, static files, endpoints
  Infrastructure.cs          HealthCheck (--healthcheck), Database.MigrateWithRetry, SecurityHeaders (CSP), MaintenanceService (InstanceService.CleanUpAsync every 6 h)
  Data/                      Entities.cs (User, UserSession, Storage(+StorageAccess), Album, Media, MediaFavorite, Collection(+Item), Share, DuplicateDismissal,
                             ImportSession/Group/Item, Activity, SystemSetting), AppDbContext.cs, Migrations/
  Services/Storages/         the providers (like MatFile): IStorageProvider + Local/Smb/Sftp/Ftp/WebDav, PathGuard (every path), ConnectionPool, StorageConnections
                             (providers, secrets, reachability), StorageMonitor (check every 5 min), StorageAdmin (administrators: list, save with secrets encrypted – the administrator who adds a storage becomes its Editor –,
                             test, delete, access), StorageAccess (the role of a person; VisibleIdsAsync limits EVERY query of the library), StorageFolders, StorageErrors
  Services/Library/          THE business layer of the photos, static classes taking the AppDbContext and the person:
                             LibraryService (grids by MediaScope in month buckets, details, favourites, Explore, search), AlbumService (folders: list, create, rename,
                             trash), CollectionService (lists), ShareService (links for people without an account), DuplicateService (exact + similar, dismiss), MediaActions (trash), ImportService (sessions, uploads,
                             Suggest, decisions), ActivityLog; the machinery: LibraryScanner (hosted: sync of the storages), MediaIndexer (hash, metadata, thumbnails;
                             large remote videos as head + tail), ImageRenderer (Magick.NET, dHash; a RAW is read by its format: the embedded camera JPEG turned by the EXIF orientation, LibRaw develops only when there is none), VideoTools (ffprobe/ffmpeg), MetadataReader (MetadataExtractor,
                             dates from names), GeoPlaces (offline towns), ThumbnailCache (data volume or a storage; memory LRU), LibrarySettings (settings in memory),
                             ImportWorker (hosted: analysis and run), MediaFormats, AlbumNames, MediaScope, LibraryEndpoints (/media/{id}/thumb|preview|original,
                             /import/{token}/thumb|preview|original/{item}: the review page shows the files large, /live/*, /s/{token}/live/timeline|items|media/{id},
                             /s/{token}/media/{id}/thumb|preview|original, /s/{token}/zip: the page of a link, without an account)
  Services/Faces/           the people: FaceEngine (ONNX Runtime: YuNet finds faces in 640 × 640 with five points, the face is turned onto the ArcFace template
                             112 × 112, SFace makes 128 numbers of length 1), FaceGrouping (pure: a face joins the person of the most alike face ≥ 0.42, else 3 alike
                             faces ≥ 0.47 make a new person without a name), FaceWorker (hosted: previews of the cache, newest first, then grouping), FaceCrops
                             (round pictures of faces), PeopleService (business layer: list, name – a name another person has joins them –, merge, hide, "not this
                             person", forget, a face to a person by name (`AssignAsync`: the viewer's "who is this?", splitting a group on the page Edit; a new
                             name makes a person, the face is confirmed and the old person rejected) or to nobody (`IgnoreAsync`), stats, delete all; a person exists for a viewer only on photos they may see, who may then also name it)
  Services/Accounts|Auth/    profiles, user administration, sessions in the DB, MatIdentity sign-in, GDPR export/erase
  Services/                  InstanceService (numbers, settings, housekeeping), MenuService, Ui (icons, logo, bytes), ListModels (toolbar, pager), Dates, Assets
  MatPlatform/               copy of the shared package (MatDesign, 2.4.0). Never edit here.
  Platform/                  MatPhotoPlatform (tiles, settings, actions scanAll/checkStorages/cleanUp, launcher, widgets, health), MatPhotoIdentityUsers
  Controls/                  ONE code base per control: <md-field>, <md-buttons>, <md-table>, <md-tabs>/<md-tab>
  Pages/                     Photos (timeline), Explore (+View?scope=), People (Index, View/{id} with "who is this?", Edit/{id}), Albums (Index, View/{id}, Edit), Lists (Index, View/{id}, Edit), Shares (Index, Edit: the links of the person), Link (`/s/{token}`, anonymous, `_LayoutShare`), Favorites, Search,
                             Duplicates, Import (Index, Review/{token}, History), Account, Admin (Users, Storages: Edit/Access, Scans, System with tabs, Connections,
                             Activity), About; Shared/_PhotoGrid (the grid of every photo page), _Cards, _GridZoom
  wwwroot/js/                photos.js (grid: buckets, justified rows, virtualisation, selection, viewer with preload/swipe/zoom/slideshow), import.js (uploads, the viewer of the review page,
                             folders, progress), scans.js, util.js, offline.js, pwa.js; sw.js (shell cache, thumbnails cache-first, grids network-first)
  wwwroot/css/app.css        accent + the styles of the app (prefix md-)
  Resources/Geo/             cities.tsv.gz + countries.tsv (GeoNames, CC BY 4.0: towns of 15,000 people or more)
tools/SampleData             the sample photos of the local stack
tests/MatPhoto.Tests         unit + database tests
```

## Conventions

**Database (PostgreSQL)**: tables PascalCase and singular, key always `Id` BIGINT, every record has `CreateDate/CreateUserId/UpdateDate/UpdateUserId` (filled by
`AppDbContext.SaveChanges`), `UpdateState` (0 deleted, 1 created, 2 updated) for soft delete (`User`, `Storage`, `Album`, `Media`, `Collection`). The time a photo was
taken is `timestamp without time zone` (the wall clock of the camera). A `Token` (uuid) for a security key (`UserSession.Token`, `ImportSession.Token`).

**Security**: every query of photos goes through `StorageAccess.VisibleIdsAsync` (Viewer) / `WritableIdsAsync` (Editor); what a person may not see answers 404.
Administrators manage storages but see their photos only with a role like anybody else. Every change under `/live` needs the antiforgery header
`RequestVerificationToken`. Originals are served with their own CSP (sandbox), inline only for images and videos. Storage secrets are encrypted with DataProtection
(keys in the volume) and never leave `StorageAdmin`. Thumbnails are addressed with `?v=<hash>` and cached immutable; their bytes come only after the access check.
**Shared links** are the only way in without an account: every request under `/s/{token}` starts with `ShareService.OpenAsync` (32 characters of base64url, 24 random
bytes) and sees the album or list through the eyes of the person who made it (`LibraryService.QueryAsync` with their id) – dead when sharing is off
(`SystemSetting.SharingEnabled`), the link expired (`ExpiresDate`, their time zone) or was withdrawn (soft delete), they are switched off or may not see it any more.
A photo outside of it answers 404 (`ShareService.FileAsync`, the video of a Live Photo of it included). The visitor's viewer shows no place, folder, album, people,
lists or favourites; originals of photos and every download only with `AllowDownload` (a video always plays). The endpoints keep an open link 30 s in memory
(`LibraryEndpoints.ForgetShare` after a change); the page counts visits (not those of the person who made it), sends `noindex` and no referrer.

**Library**: a scan lists, adds new files at once (`ThumbState.Pending`, resumable), then indexes new/changed/pending files newest first (3 at a time); a new path with
the hash and size of a vanished record takes that record (favourites and lists survive moves). Live Photos: photo + video ≤ 5 s of the same name. Folders a NAS keeps
(`@eaDir`, `#recycle` …), hidden folders, `.matphoto-trash` and the cache folder are never scanned. **People**: `Media.FaceState` (Pending / Done / Failed; a changed file is looked at again), `Face` (box relative 0..1, `Embedding` real[128], `Clustered`,
`IsIgnored` = forgotten, `IsConfirmed`), `Person` (soft delete, `Name` null = "who is this?", `IsHidden`, `CoverFaceId`), `FaceRejection` ("not this person").
`SystemSetting.FacesEnabled` (off by default: biometric data). Scope `person:<id>`; the search finds names; the viewer info lists the people and the largest faces of
nobody (`/live/faces/{id}/name|ignore`, names from `/live/people/names`); a person left without faces is gone. A change of the cache place or the sizes sets every record to
`Pending` (they stay visible) and the next scans make the thumbnails again. **Import suggestion** (`ImportService.Suggest`): a folder a person named is ONE album
whatever the camera clock says (`AlbumNames.AlbumFolderOf`: day folders like `20100127` belong to their parent; device folders `DCIM`, `100CANON`, `WhatsApp Images`,
`Fotos`, years name no album); a year in the name becomes the first date of that year, else the name stays (`2008 Familie Olleck`). Only the rest goes by time and
place. Files of the import's source that are in the library are no duplicates of the library. **In place** (`ImportService.IsInPlace`: a folder of a library
imported into the same library, e.g. a NAS connected anew): the files are always moved, never copied; the target folder is the top, not `ImportFolder`; a folder that
is an album already is the group's album and a file in its album stays (no `name (2).jpg` beside itself). After every import that moved files the folders the files left that hold only what a
system leaves (`MediaFormats.IsLeftover`: `@eaDir`, `.DS_Store`, `Thumbs.db`) go into `.matphoto-trash/<date>/` (the source folder itself stays).

**UI (MatDesign rules)**: CRUD = list page (toolbar above, table/cards, pager under it, actions left below) + separate edit page (Save, Back, space, Delete); a
storage has **tabs** (General, Access), System has tabs (General, Thumbnails, Import). The grid and the viewer are the special UI of the app (the viewer on black),
built on the toolkit. Icons via `Ui.Icon` (toolkit sprite + `wwwroot/img/app-icons.svg`). Every `td` has `data-label`. The version at the foot of the menu links to `/About`.

**Code**: Microsoft C# guidelines, file-scoped namespaces, `.editorconfig`. The business layer is static classes taking the `AppDbContext` and the person. No database
queries in page models or views beyond calling the business layer (the admin page Scans reads counts directly – keep it small). English identifiers and comments,
UI texts only in the catalog.

**Compose files**: no environment variables to set, no placeholders; a stack (`name:`) per file; `docker-compose.yml` (release), `.dev.yml` (nightly + pgAdmin),
`.local.yml` (build + pgAdmin + `./sample-data` as `/storage/sample`, subnet 10.253.83.0/24).

**Git**: commit under the user's name; never add a Claude/Anthropic co-author trailer or a "generated with" line (global rule of the user; it overrides any default). Branches:
`main` = release, `dev` = development. Versions: release `<major>.<minor>.<build>-<date>`, development `nightly-<build>-<date>`, local `local-<date>` (`Directory.Build.props`;
`.github/workflows/build.yml` builds, tests and pushes the image `ghcr.io/real-ttx/matphoto`).
