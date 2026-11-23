using System.Globalization;

using System.Text.Json;
using MatPhoto.Data;
using MatPhoto.Localization;
using MatPhoto.Services.Storages;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.EntityFrameworkCore;
using Microsoft.Net.Http.Headers;

namespace MatPhoto.Services.Library;

public sealed record IdsRequest(long[]? Ids, bool On = true);

public sealed record NameRequest(string? Name);

/// <summary>The services of the library (one call in Program.cs): the indexer, the cache, the scanner, the import worker.</summary>
public static class LibrarySetup
{
    public static IServiceCollection AddLibrary(this IServiceCollection services)
    {
        services.AddSingleton<LibrarySettings>();
        services.AddSingleton<ThumbnailCache>();
        services.AddSingleton<VideoTools>();
        services.AddSingleton<MediaIndexer>();
        services.AddSingleton<LibraryScanner>();
        services.AddHostedService(sp => sp.GetRequiredService<LibraryScanner>());
        services.AddSingleton<ImportWorker>();
        services.AddHostedService(sp => sp.GetRequiredService<ImportWorker>());

        // the people: the models beside the app (the image downloads them), or where ModelsDirectory says
        services.AddSingleton(sp => new Faces.FaceEngine(sp.GetRequiredService<IConfiguration>()["ModelsDirectory"] ?? Path.Combine(AppContext.BaseDirectory, "models")));
        services.AddSingleton<Faces.FaceCrops>();
        services.AddSingleton<Faces.FaceWorker>();
        services.AddHostedService(sp => sp.GetRequiredService<Faces.FaceWorker>());
        return services;
    }
}

/// <summary>
/// The addresses of the library: the pictures (<c>/media/{id}/thumb|preview|original</c>: the thumbnails are kept by the browser for good, their address names their
/// content; an original is streamed with ranges so a video can be wound), and the JSON of the scripts under <c>/live</c> (every change with the anti-forgery header).
/// What a request may see and do is decided by the business layer; what it may not see answers 404.
/// </summary>
public static class LibraryEndpoints
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static void MapLibraryEndpoints(this IEndpointRouteBuilder app)
    {
        StorageErrors.Log ??= app.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("MatPhoto.Storages");

        // ---- pictures ----------------------------------------------------------------------------------------------------------
        app.MapGet("/media/{id:long}/thumb", (long id, HttpContext http, AppDbContext db, CurrentUser me, ThumbnailCache cache) => Picture(id, ThumbKind.Thumb, http, db, me, cache));
        app.MapGet("/media/{id:long}/preview", (long id, HttpContext http, AppDbContext db, CurrentUser me, ThumbnailCache cache) => Picture(id, ThumbKind.Preview, http, db, me, cache));
        app.MapMethods("/media/{id:long}/original", ["GET", "HEAD"], async (long id, bool? download, HttpContext http, AppDbContext db, CurrentUser me, StorageConnections connections) =>
        {
            var media = await LibraryService.FindAsync(db, me.RequiredId, id, http.RequestAborted);
            return media?.Storage is null ? Results.NotFound() : await OriginalAsync(http, connections, media.Storage, media.Path, media.FileName, media.MimeType, media.Size, media.FileModified, download == true);
        });

        // the round picture of a face (a person): cut out of the preview, only of photos the person may see
        app.MapGet("/faces/{faceId:long}/thumb", async (long faceId, HttpContext http, AppDbContext db, CurrentUser me, Faces.FaceCrops crops) =>
        {
            var face = await Faces.PeopleService.FaceAsync(db, me.RequiredId, faceId, http.RequestAborted);
            var data = face is { } f ? await crops.GetAsync(faceId, f.Hash, f.X, f.Y, f.Width, f.Height, http.RequestAborted) : null;
            if (data is null)
            {
                return Results.NotFound();
            }

            http.Response.Headers.CacheControl = "private, max-age=31536000, immutable";
            return Results.Bytes(data, "image/jpeg");
        });

        // the files of an import (the review page shows them before they are in the library)
        app.MapGet("/import/{token:guid}/thumb/{itemId:long}", (Guid token, long itemId, HttpContext http, AppDbContext db, CurrentUser me, ThumbnailCache cache)
            => ImportPicture(token, itemId, ThumbKind.Thumb, http, db, me, cache));
        app.MapGet("/import/{token:guid}/preview/{itemId:long}", (Guid token, long itemId, HttpContext http, AppDbContext db, CurrentUser me, ThumbnailCache cache)
            => ImportPicture(token, itemId, ThumbKind.Preview, http, db, me, cache));
        app.MapMethods("/import/{token:guid}/original/{itemId:long}", ["GET", "HEAD"], async (Guid token, long itemId, HttpContext http, AppDbContext db, CurrentUser me,
            StorageConnections connections, IConfiguration configuration) =>
        {
            var file = await ImportService.FileAsync(db, me.RequiredId, token, itemId, configuration["DataDirectory"] ?? "/data", http.RequestAborted);
            if (file?.LocalFile is { } local)
            {
                OriginalHeaders(http);
                return Results.File(local, Inline(file.MimeType) ? file.MimeType : "application/octet-stream", null,
                    new DateTimeOffset(DateTime.SpecifyKind(file.Modified, DateTimeKind.Utc)), enableRangeProcessing: true);
            }

            return file?.Storage is null || file.Path is null
                ? Results.NotFound()
                : await OriginalAsync(http, connections, file.Storage, file.Path, file.FileName, file.MimeType, file.Size, file.Modified, download: false);
        });

        // ---- the JSON of the grids and the viewer ----------------------------------------------------------------------------------
        var live = app.MapGroup("/live").AddEndpointFilter(Antiforgery);

        live.MapGet("/timeline", async (string? scope, AppDbContext db, CurrentUser me, CancellationToken ct) =>
        {
            var buckets = await LibraryService.BucketsAsync(db, me.RequiredId, MediaScope.Parse(scope), ct);
            return buckets is null ? Results.NotFound() : Results.Json(new
            {
                buckets = buckets.Select(b => new { k = b.Key, y = b.Year, m = b.Month, n = b.Count, label = BucketLabel(b) }),
                total = buckets.Sum(b => b.Count),
            }, Json);
        });

        live.MapGet("/items", async (string? scope, string? bucket, int? offset, int? limit, AppDbContext db, CurrentUser me, CancellationToken ct) =>
        {
            var items = await LibraryService.ItemsAsync(db, me.RequiredId, MediaScope.Parse(scope), bucket ?? string.Empty, offset ?? 0, limit ?? 500, ct);
            return items is null ? Results.NotFound() : Results.Json(new
            {
                items = items.Select(i => new
                {
                    i = i.Id,
                    w = i.Width,
                    h = i.Height,
                    k = i.Hash.Length >= 12 ? i.Hash[..12] : i.Hash,
                    v = i.Type == MediaType.Video ? Math.Max(1, i.DurationMs ?? 1) : 0,
                    l = i.Live ? 1 : 0,
                    f = i.Favorite ? 1 : 0,
                    t = i.HasThumb ? 1 : 0,
                    d = i.Date.ToString("yyyy-MM-dd'T'HH:mm", CultureInfo.InvariantCulture),
                }),
            }, Json);
        });

        live.MapGet("/media/{id:long}", async (long id, AppDbContext db, CurrentUser me, CancellationToken ct) =>
        {
            var d = await LibraryService.DetailAsync(db, me.RequiredId, id, ct);
            return d is null ? Results.NotFound() : Results.Json(Describe(d), Json);
        });

        live.MapPost("/media/favorite", async (IdsRequest body, AppDbContext db, CurrentUser me, CancellationToken ct) =>
        {
            var ids = Ids(body.Ids);
            var changed = await LibraryService.SetFavoriteAsync(db, me.RequiredId, ids, body.On, ct);
            return Results.Json(new { changed }, Json);
        });

        live.MapPost("/media/trash", async (IdsRequest body, AppDbContext db, CurrentUser me, StorageConnections connections, CancellationToken ct) =>
        {
            var result = await MediaActions.TrashAsync(db, connections, me.RequiredId, Ids(body.Ids), ct);
            return Results.Json(new
            {
                moved = result.Moved,
                refused = result.Refused,
                errors = result.Errors,
                message = Text.T("Trash.Moved", result.Moved) + (result.Refused > 0 ? " " + Text.T("Trash.Refused", result.Refused) : string.Empty),
            }, Json);
        });

        // ---- the faces of a photo (the viewer: "who is this?") -----------------------------------------------------------------
        live.MapGet("/people/names", async (AppDbContext db, CurrentUser me, CancellationToken ct) =>
        {
            var names = await Faces.PeopleService.NamesAsync(db, me.RequiredId, ct);
            return Results.Json(new { names = names.Select(n => n.Name) }, Json);
        });

        live.MapPost("/faces/{faceId:long}/name", async (long faceId, NameRequest body, AppDbContext db, CurrentUser me, CancellationToken ct) =>
        {
            var outcome = await Faces.PeopleService.AssignAsync(db, me.RequiredId, [faceId], body.Name, ct);
            return outcome.NotAllowed ? Results.NotFound()
                : outcome.Ok ? Results.Json(new { person = outcome.Value, message = Text.T("People.Named", Faces.PeopleService.Clean(body.Name)!) }, Json)
                : Problem(outcome.Error);
        });

        live.MapPost("/faces/{faceId:long}/ignore", async (long faceId, AppDbContext db, CurrentUser me, CancellationToken ct)
            => await Faces.PeopleService.IgnoreAsync(db, me.RequiredId, faceId, ct) ? Results.Json(new { ok = true, message = Text.T("People.Ignored") }, Json) : Results.NotFound());

        live.MapGet("/lists", async (AppDbContext db, CurrentUser me, CancellationToken ct) =>
        {
            var lists = await CollectionService.TargetsAsync(db, me.RequiredId, ct);
            return Results.Json(new { lists = lists.Select(l => new { id = l.Id, name = l.Name, colour = l.Colour }) }, Json);
        });

        live.MapPost("/lists", async (NameRequest body, AppDbContext db, CurrentUser me, CancellationToken ct) =>
        {
            var outcome = await CollectionService.QuickCreateAsync(db, me.RequiredId, body.Name, ct);
            return outcome.Ok ? Results.Json(new { id = outcome.Value.Id, name = outcome.Value.Name }, Json) : Problem(outcome.Error);
        });

        live.MapPost("/lists/{id:long}/add", async (long id, IdsRequest body, AppDbContext db, CurrentUser me, CancellationToken ct) =>
        {
            var added = await CollectionService.AddAsync(db, me.RequiredId, id, Ids(body.Ids), ct);
            return added is null ? Results.NotFound() : Results.Json(new { added, message = Text.T("Lists.Added", added) }, Json);
        });

        live.MapPost("/lists/{id:long}/remove", async (long id, IdsRequest body, AppDbContext db, CurrentUser me, CancellationToken ct) =>
        {
            var removed = await CollectionService.RemoveAsync(db, me.RequiredId, id, Ids(body.Ids), ct);
            return removed is null ? Results.NotFound() : Results.Json(new { removed, message = Text.T("Lists.Removed", removed) }, Json);
        });

        live.MapPost("/duplicates/dismiss", async (IdsRequest body, AppDbContext db, CurrentUser me, CancellationToken ct) =>
            Results.Json(new { dismissed = await DuplicateService.DismissAsync(db, me.RequiredId, Ids(body.Ids), ct) }, Json));

        // the folders of a storage (the pickers of the import: its source and its target)
        live.MapGet("/storages/{id:long}/folders", async (long id, string? path, AppDbContext db, CurrentUser me, StorageConnections connections, CancellationToken ct) =>
        {
            var found = await StorageAccess.FindAsync(db, me.RequiredId, id, ct: ct);
            if (found is null)
            {
                return Results.NotFound();
            }

            try
            {
                var folder = PathGuard.Normalize(path ?? string.Empty);
                var entries = await connections.Create(found.Value.Storage).ListAsync(folder, ct);
                var folders = entries.Where(e => e.IsFolder && MediaFormats.IsScanned(e.Name)).OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase)
                    .Select(e => new { name = e.Name, path = e.Path });
                return Results.Json(new { path = folder, folders, files = entries.Count(e => !e.IsFolder && MediaFormats.IsMedia(e.Name)) }, Json);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return Problem(StorageErrors.Message(ex));
            }
        });

        // the suggestions of the search of the header bar: albums, lists and places (the photos are on the page of the results)
        live.MapGet("/suggest", async (string? q, int? limit, AppDbContext db, CurrentUser me, CancellationToken ct) =>
        {
            var text = (q ?? string.Empty).Trim();
            if (text.Length < 2)
            {
                return Results.Json(new { groups = Array.Empty<object>() }, Json);
            }

            var take = Math.Clamp(limit ?? 8, 1, 20);
            var albums = await AlbumService.ListAsync(db, me.RequiredId, new AlbumCriteria(text, null, null, "date", true, 1, take), ct);
            var lists = await CollectionService.ListAsync(db, me.RequiredId, new CollectionCriteria(text, null, "name", false, 1, 4), ct);
            var visible = await StorageAccess.VisibleIdsAsync(db, me.RequiredId, ct);
            var like = "%" + text.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
            var places = await LibraryService.Shown(db, visible).Where(m => m.City != null && EF.Functions.ILike(m.City, like))
                .GroupBy(m => new { m.CountryCode, m.City, m.Country }).Select(g => new { g.Key.CountryCode, g.Key.City, g.Key.Country, Count = g.Count() })
                .OrderByDescending(g => g.Count).Take(4).ToListAsync(ct);
            var people = await Faces.PeopleService.ListAsync(db, me.RequiredId, new Faces.PeopleCriteria(text, "named", "count", true, 1, 4), ct);
            var groups = new List<object>();
            if (people.Items.Count > 0)
            {
                groups.Add(new
                {
                    name = Text.T("People.Title"),
                    items = people.Items.Select(p => new { title = p.Name, text = Text.T("People.Photos", p.Photos), href = $"/People/View/{p.Id}", icon = "user", kind = "person", id = p.Id.ToString(CultureInfo.InvariantCulture) }),
                });
            }

            if (albums.Items.Count > 0)
            {
                groups.Add(new
                {
                    name = Text.T("Albums.Title"),
                    items = albums.Items.Select(a => new { title = a.Name, text = AlbumText.Dates(a.AlbumDate, a.StartDate, a.EndDate), href = $"/Albums/View/{a.Id}", icon = "album", kind = "album", id = a.Id.ToString(CultureInfo.InvariantCulture) }),
                });
            }

            if (lists.Items.Count > 0)
            {
                groups.Add(new
                {
                    name = Text.T("Lists.Title"),
                    items = lists.Items.Select(l => new { title = l.Name, text = Text.T("Explore.Count", l.Count), href = $"/Lists/View/{l.Id}", icon = "list", kind = "list", id = l.Id.ToString(CultureInfo.InvariantCulture) }),
                });
            }

            if (places.Count > 0)
            {
                groups.Add(new
                {
                    name = Text.T("Explore.Places"),
                    items = places.Select(p => new
                    {
                        title = p.City, text = GeoPlaces.CountryName(p.CountryCode, p.Country) + " · " + Text.T("Explore.Count", p.Count),
                        href = "/Explore/View?scope=" + Uri.EscapeDataString($"place:{p.CountryCode}/{p.City}"), icon = "map-pin", kind = "place", id = $"{p.CountryCode}/{p.City}",
                    }),
                });
            }

            return Results.Json(new { groups }, Json);
        });

        // ---- the import -----------------------------------------------------------------------------------------------------------
        live.MapPost("/import", async (AppDbContext db, CurrentUser me, CancellationToken ct) =>
        {
            if ((await AlbumService.TargetsAsync(db, me.RequiredId, ct)).Count == 0)
            {
                return Results.NotFound();
            }

            var token = await ImportService.CreateUploadAsync(db, me.RequiredId, ct);
            return Results.Json(new { token }, Json);
        });

        live.MapPost("/import/{token:guid}/files", async (Guid token, HttpContext http, AppDbContext db, CurrentUser me, LibrarySettings settings, IConfiguration configuration) =>
        {
            var name = Uri.UnescapeDataString(http.Request.Headers["X-File-Name"].ToString());
            var relative = Uri.UnescapeDataString(http.Request.Headers["X-Relative-Path"].ToString());
            var error = await ImportService.AddUploadAsync(db, me.RequiredId, token, configuration["DataDirectory"] ?? "/data", name, relative, http.Request.Body, http.Request.ContentLength,
                settings.Current, http.RequestAborted);
            return error is null ? Results.Json(new { ok = true }, Json) : Problem(error);
        }).WithMetadata(new Microsoft.AspNetCore.Mvc.DisableRequestSizeLimitAttribute());

        live.MapPost("/import/{token:guid}/analyze", async (Guid token, AppDbContext db, CurrentUser me, ImportWorker worker, CancellationToken ct) =>
        {
            var started = await ImportService.StartAnalysisAsync(db, me.RequiredId, token, ct);
            worker.Wake();
            return started ? Results.Json(new { ok = true }, Json) : Results.NotFound();
        });

        live.MapGet("/import/{token:guid}/status", async (Guid token, AppDbContext db, CurrentUser me, CancellationToken ct) =>
        {
            var s = await db.ImportSessions.AsNoTracking().Where(x => x.Token == token && x.UserId == me.RequiredId)
                .Select(x => new { x.State, x.TotalCount, x.DoneCount, x.ImportedCount, x.FailedCount, x.Message }).FirstOrDefaultAsync(ct);
            return s is null ? Results.NotFound() : Results.Json(new
            {
                state = s.State.ToString().ToLowerInvariant(),
                total = s.TotalCount,
                done = s.DoneCount,
                imported = s.ImportedCount,
                failed = s.FailedCount,
                message = s.Message,
            }, Json);
        });

        // ---- the scans (administration) --------------------------------------------------------------------------------------------
        live.MapPost("/storages/{id:long}/scan", async (long id, AppDbContext db, CurrentUser me, LibraryScanner scanner, CancellationToken ct) =>
        {
            if (!me.IsAdmin || !await db.Storages.AnyAsync(s => s.Id == id && s.IsActive && s.IsLibrary, ct))
            {
                return Results.NotFound();
            }

            scanner.Request(id);
            return Results.Json(new { ok = true, message = Text.T("Scan.Started") }, Json);
        });

        live.MapGet("/scans", (CurrentUser me, LibraryScanner scanner) => !me.IsAdmin ? Results.NotFound() : Results.Json(new
        {
            scans = scanner.Progress.Select(p => new
            {
                id = p.StorageId,
                name = p.StorageName,
                phase = p.Phase,
                phaseText = Text.T("Scan.Phase." + p.Phase),
                running = p.Running,
                folders = p.Folders,
                files = p.Files,
                toIndex = p.ToIndex,
                indexed = p.Indexed,
                failed = p.Failed,
                current = p.Current,
                result = p.Result,
            }),
        }, Json));
    }

    // ---- pictures ------------------------------------------------------------------------------------------------------------------

    /// <summary>A thumbnail or a preview from the cache. The address carries the start of the content hash (<c>?v=</c>), so the browser keeps it for a year.</summary>
    private static async Task<IResult> Picture(long id, ThumbKind kind, HttpContext http, AppDbContext db, CurrentUser me, ThumbnailCache cache)
    {
        var media = await LibraryService.FindAsync(db, me.RequiredId, id, http.RequestAborted);
        if (media is null)
        {
            return Results.NotFound();
        }

        var etag = new EntityTagHeaderValue($"\"{media.ContentHash}{(kind == ThumbKind.Thumb ? "t" : "p")}\"");
        if (http.Request.Headers.IfNoneMatch.ToString().Contains(etag.Tag.ToString(), StringComparison.Ordinal))
        {
            return Results.StatusCode(StatusCodes.Status304NotModified);
        }

        var data = media.ContentHash.Length > 0 ? await cache.ReadAsync(media.ContentHash, kind, http.RequestAborted) : null;
        if (data is null)
        {
            return Results.NotFound();
        }

        http.Response.Headers.CacheControl = http.Request.Query.ContainsKey("v") ? "private, max-age=31536000, immutable" : "private, no-cache";
        http.Response.Headers.ETag = etag.ToString();
        return Results.Bytes(data, "image/jpeg");
    }

    /// <summary>
    /// The file itself: from a local storage with the ranges of ASP.NET Core; from a remote one streamed from the asked offset (one range: what a video player asks).
    /// Shown inline only for pictures and videos (never SVG or HTML), always with a sandbox.
    /// </summary>
    /// <summary>Images and videos are shown in the page; anything else (and SVG, which could hold a script) only downloads.</summary>
    private static bool Inline(string mime)
        => (mime.StartsWith("image/", StringComparison.Ordinal) || mime.StartsWith("video/", StringComparison.Ordinal)) && mime != "image/svg+xml";

    /// <summary>An original gets a policy of its own: nothing runs, nothing loads.</summary>
    private static void OriginalHeaders(HttpContext http)
    {
        var headers = http.Response.Headers;
        headers[HeaderNames.XContentTypeOptions] = "nosniff";
        headers.CacheControl = "private, max-age=3600";
        headers.ContentSecurityPolicy = "default-src 'none'; img-src 'self' data:; media-src 'self'; sandbox; frame-ancestors 'self'";
    }

    /// <summary>The thumbnail or the preview of a file of an import (only of the person's own imports).</summary>
    private static async Task<IResult> ImportPicture(Guid token, long itemId, ThumbKind kind, HttpContext http, AppDbContext db, CurrentUser me, ThumbnailCache cache)
    {
        var hash = await ImportService.ItemHashAsync(db, me.RequiredId, token, itemId, http.RequestAborted);
        var data = hash is null ? null : await cache.ReadAsync(hash, kind, http.RequestAborted);
        if (data is null)
        {
            return Results.NotFound();
        }

        http.Response.Headers.CacheControl = "private, max-age=3600";
        return Results.Bytes(data, "image/jpeg");
    }

    public static async Task<IResult> OriginalAsync(HttpContext http, StorageConnections connections, Storage storage, string path, string fileName, string mime, long size, DateTime modified,
        bool download)
    {
        var inline = !download && Inline(mime);
        var type = inline ? mime : "application/octet-stream";
        var headers = http.Response.Headers;
        OriginalHeaders(http);
        var lastModified = new DateTimeOffset(DateTime.SpecifyKind(modified, DateTimeKind.Utc));
        var provider = connections.Create(storage);
        if (provider.LocalPathOf(path) is { } local)
        {
            return Results.File(local, type, inline ? null : fileName, lastModified, enableRangeProcessing: true);
        }

        if (!inline || download)
        {
            headers.ContentDisposition = new ContentDispositionHeaderValue("attachment") { FileNameStar = fileName }.ToString();
        }

        headers.AcceptRanges = "bytes";
        headers.LastModified = lastModified.ToString("R", CultureInfo.InvariantCulture);
        long from = 0, to = size - 1;
        var ranged = false;
        if (RangeHeaderValue.TryParse(http.Request.Headers.Range.ToString(), out var range) && range.Unit == "bytes" && range.Ranges.Count == 1 && size > 0)
        {
            var r = range.Ranges.First();
            if (r.From is { } f)
            {
                from = f;
                to = r.To is { } t ? Math.Min(t, size - 1) : size - 1;
            }
            else if (r.To is { } suffix)
            {
                from = Math.Max(0, size - suffix);
            }

            if (from > to || from >= size)
            {
                headers.ContentRange = $"bytes */{size}";
                return Results.StatusCode(StatusCodes.Status416RangeNotSatisfiable);
            }

            ranged = true;
        }

        var length = to - from + 1;
        http.Response.StatusCode = ranged ? StatusCodes.Status206PartialContent : StatusCodes.Status200OK;
        http.Response.ContentType = type;
        http.Response.ContentLength = length;
        if (ranged)
        {
            headers.ContentRange = $"bytes {from}-{to}/{size}";
        }

        if (HttpMethods.IsHead(http.Request.Method))
        {
            return Results.Empty;
        }

        try
        {
            await using var stream = await provider.OpenReadAsync(path, from, http.RequestAborted);
            var buffer = new byte[81920];
            var left = length;
            while (left > 0)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, left)), http.RequestAborted);
                if (read == 0)
                {
                    break;
                }

                await http.Response.Body.WriteAsync(buffer.AsMemory(0, read), http.RequestAborted);
                left -= read;
            }
        }
        catch (OperationCanceledException)
        {
            // the player asked for another range
        }
        catch (Exception ex) when (!http.Response.HasStarted)
        {
            return Problem(StorageErrors.Message(ex));
        }

        return Results.Empty;
    }

    // ---- what the viewer shows ---------------------------------------------------------------------------------------------------------

    private static object Describe(MediaDetail d)
    {
        var culture = CultureInfo.CurrentUICulture;
        var rows = new List<object>();
        void Row(string icon, string label, string? value, string? secondary = null, string? href = null)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                rows.Add(new { icon, label, value, secondary, href });
            }
        }

        var date = d.TakenDate ?? d.SortDate;
        Row("calendar", Text.T("Viewer.Taken"), date.ToString("D", culture) + ", " + date.ToString("t", culture), Text.T("Viewer.DateSource." + d.DateSource));
        var camera = string.Join(" ", new[] { d.CameraMake, d.CameraModel }.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct());
        if (d.CameraModel is not null && d.CameraMake is not null && d.CameraModel.StartsWith(d.CameraMake, StringComparison.OrdinalIgnoreCase))
        {
            camera = d.CameraModel;
        }

        var exposure = string.Join(" · ", new[]
        {
            d.FNumber is { } f ? "ƒ/" + f.ToString("0.#", culture) : null,
            d.ExposureTime is { } e ? (e >= 1 ? e.ToString("0.#", culture) + " s" : "1/" + Math.Round(1 / e).ToString(culture) + " s") : null,
            d.FocalLength is { } fl ? fl.ToString("0.#", culture) + " mm" : null,
            d.Iso is { } iso ? "ISO " + iso.ToString(culture) : null,
        }.Where(s => s is not null));
        Row("camera", Text.T("Viewer.Camera"), camera, string.Join(" · ", new[] { d.LensModel, exposure }.Where(s => !string.IsNullOrWhiteSpace(s))));
        var size = d.Width > 0 ? $"{d.Width} × {d.Height}" + (d.Type == MediaType.Photo ? $" · {Math.Round(d.Width * (double)d.Height / 1_000_000, 1).ToString(culture)} MP" : string.Empty) : null;
        Row(d.Type == MediaType.Video ? "video" : "image", d.FileName, string.Join(" · ", new[] { size, Ui.Bytes(d.Size) }.Where(s => s is not null)),
            d.DurationMs is { } ms ? TimeSpan.FromMilliseconds(ms).ToString(ms >= 3_600_000 ? @"h\:mm\:ss" : @"m\:ss", culture) + (d.VideoCodec is null ? string.Empty : " · " + d.VideoCodec.ToUpperInvariant()) : null);
        if (d.City is not null || d.Latitude is not null)
        {
            var place = string.Join(", ", new[] { d.City, d.Region, d.CountryCode is null ? null : GeoPlaces.CountryName(d.CountryCode, d.Country) }.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct());
            var position = d.Latitude is { } lat && d.Longitude is { } lon
                ? lat.ToString("0.#####", CultureInfo.InvariantCulture) + ", " + lon.ToString("0.#####", CultureInfo.InvariantCulture) + (d.Altitude is { } alt ? $" · {Math.Round(alt).ToString(culture)} m" : string.Empty)
                : null;
            Row("map-pin", Text.T("Viewer.Place"), place.Length > 0 ? place : position, place.Length > 0 ? position : null,
                d.CountryCode is null ? null : "/Explore/View?scope=" + Uri.EscapeDataString(d.City is null ? $"place:{d.CountryCode}" : $"place:{d.CountryCode}/{d.City}"));
        }

        Row("album", Text.T("Viewer.Album"), d.AlbumName, d.StorageName + " · " + d.Path, "/Albums/View/" + d.AlbumId.ToString(CultureInfo.InvariantCulture));
        var traits = Enum.GetValues<MediaTraits>().Where(t => t is not MediaTraits.None and not MediaTraits.LiveVideo && d.Traits.HasFlag(t)).Select(t => Text.T("Viewer.Trait." + t)).ToList();
        Row("tag", Text.T("Viewer.Recognised"), string.Join(", ", traits));
        return new
        {
            id = d.Id,
            name = d.FileName,
            type = d.Type == MediaType.Video ? "video" : "photo",
            mime = d.MimeType,
            width = d.Width,
            height = d.Height,
            hash = d.Hash.Length >= 12 ? d.Hash[..12] : d.Hash,
            hasThumb = d.HasThumb,
            favorite = d.Favorite,
            canEdit = d.CanEdit,
            live = d.LiveVideoId,
            browserCanShow = d.BrowserCanShow,
            albumId = d.AlbumId,
            lists = d.Lists.Select(l => new { id = l.Id, name = l.Name }),
            people = d.People.Select(p => new
            {
                id = p.Id,
                name = p.Name ?? (p.Id is null ? Text.T("People.WhoIsThis") : Text.T("People.Unnamed")),
                current = p.Name ?? string.Empty,
                named = p.Name is not null,
                face = p.FaceId,
            }),
            title = date.ToString("D", culture),
            rows,
        };
    }

    private static string BucketLabel(TimelineBucket b)
        => b.Key == "all" ? string.Empty : new DateTime(b.Year, b.Month, 1).ToString(Languages.IsGerman ? "MMMM yyyy" : "MMMM yyyy", CultureInfo.CurrentUICulture);

    private static long[] Ids(long[]? ids) => (ids ?? []).Where(id => id > 0).Distinct().Take(5000).ToArray();

    private static IResult Problem(string? error) => Results.Json(new { error }, Json, statusCode: StatusCodes.Status422UnprocessableEntity);

    /// <summary>The anti-forgery token of the page in the header <c>RequestVerificationToken</c> for every change; reads pass.</summary>
    public static async ValueTask<object?> Antiforgery(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        if (!HttpMethods.IsGet(http.Request.Method) && !HttpMethods.IsHead(http.Request.Method))
        {
            try
            {
                await http.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(http);
            }
            catch (AntiforgeryValidationException)
            {
                return Results.BadRequest();
            }
        }

        return await next(context);
    }
}
