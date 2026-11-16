using System.Text.Json;
using MatPhoto.Services;
using MatPhoto.Services.Library;
using MatPhoto.Services.Storages;
using MatPlatform;

namespace MatPhoto.Platform;

/// <summary>
/// MatPhoto as an app of the platform contract (MatDesign docs/PLATFORM.md, contract 2) on the shared package MatPlatform (the folder <c>MatPlatform/</c>, a copy):
/// how a consumer such as Mattiverse shows MatPhoto (<see cref="Describe"/>: tiles, the few settings, buttons), what it consumes (the sign-in and the mail that their
/// variables set), what it adds to a shell (the launcher Photos with its views, two widgets for administrators), its values and what its buttons do. The settings are
/// those of the admin page System (registration, public address, the import), kept in the same place (<see cref="InstanceService"/>). The values come from the
/// services (no DbContext here: this is the web layer).
/// </summary>
public sealed class MatPhotoPlatform(InstanceService instance, StorageMonitor storages, LibraryScanner scanner, MatPlatformSettings settings, IServiceScopeFactory scopes,
    ILogger<MatPhotoPlatform> log)
    : IMatPlatformState, IMatPlatformConfiguration, IMatPlatformActions, IMatPlatformHealth
{
    /// <summary>The id of MatPhoto in the platform (and the default client id at MatIdentity).</summary>
    public const string AppId = "matphoto";

    /// <summary>The accent of MatPhoto (an orchid, white text 5.8:1; see <c>wwwroot/css/app.css</c>).</summary>
    public const string Accent = Ui.Accent;

    /// <summary>The MatApiDefinition of MatPhoto (<c>GET /api</c>) without <c>MAT_PUBLIC_URL</c>.</summary>
    public static void Describe(MatPlatformBuilder o) => Describe(o, null);

    /// <summary>
    /// The MatApiDefinition of MatPhoto (<c>GET /api</c>); <c>auth</c> and <c>endpoints</c> are set by MatPlatform. <paramref name="publicUrl"/> is the value of
    /// <c>MAT_PUBLIC_URL</c>: while it is set, the field <c>publicUrl</c> is fixed.
    /// </summary>
    public static void Describe(MatPlatformBuilder o, string? publicUrl)
    {
        var T = o.T;

        o.App(AppId, "MatPhoto", T("Fotos und Videos aus deinen Speichern: Alben als Ordner, Listen, Orte, Duplikate und ein kluger Import",
            "Photos and videos from your storages: albums as folders, lists, places, duplicates and a smart import"), AppInfo.Version, "/img/logo/matphoto-mark.svg", Accent, "/Photos");

        // What MatPhoto uses from other apps: the sign-in and the mail, set by their variables (MatPhoto works without both).
        o.Consumes(MatCapabilities.IdentityOidc, optional: true);
        o.Consumes(MatCapabilities.MailSmtp, optional: true);

        // What MatPhoto adds to the home screen of a shell (Mattiverse): the photos with the views of its menu, and two widgets for administrators.
        o.Launcher("photos", "Photos", "/img/logo/matphoto-mark.svg", "/Photos");
        o.QuickAction("photos", T("Importieren", "Import"), "upload", url: "/Import");
        o.QuickAction("photos", T("Alben", "Albums"), "folder", url: "/Albums");
        o.QuickAction("photos", T("Entdecken", "Explore"), "compass", url: "/Explore");
        o.QuickAction("photos", T("Favoriten", "Favourites"), "heart", url: "/Favorites");
        o.Widget(new MatWidget("library", T("Bibliothek", "Library"), MatWidgetKinds.Tiles, "medium")
        {
            Items = ["photos", "videos", "albums"],
            Icon = "image",
            Url = "/Admin/Scans",
            Audience = MatAudience.Admin,
        });
        o.Widget(new MatWidget("users", T("Aktive Benutzer", "Active users"), MatWidgetKinds.Stat)
        {
            Items = ["users"],
            Icon = "users",
            Url = "/Admin/Users",
            Audience = MatAudience.Admin,
        });

        o.Page("overview", T("Übersicht", "Overview"), "dashboard",
            MatSection.Tiles("users", "photos", "videos", "albums", "size", "storages", "pending", "failed", "lists", "imports7", "lastScan", "registration", "lastActivity"),
            MatSection.Actions("openApp", "openUsers", "openStorages", "openScans", "openConnections", "openActivity", "openSystem", "scanAll", "checkStorages", "cleanUp"));
        o.Page("settings", T("Einstellungen", "Settings"), "settings",
            MatSection.Form(T("Allgemein", "General"), "registrationOpen", "publicUrl"),
            MatSection.Form(T("Import", "Import"), "albumDateFormat", "importGapHours", "maxUploadMb"));

        o.Tile("users", T("Aktive Benutzer", "Active users"), icon: "users");
        o.Tile("photos", T("Fotos", "Photos"), icon: "image");
        o.Tile("videos", T("Videos", "Videos"), icon: "play");
        o.Tile("albums", T("Alben", "Albums"), icon: "folder");
        o.Tile("size", T("Größe der Bibliothek", "Size of the library"), "text", icon: "hard-drive");
        o.Tile("storages", T("Speicher", "Storages"), icon: "server");
        o.Tile("pending", T("Noch zu lesen", "Still to read"), icon: "clock");
        o.Tile("failed", T("Nicht lesbar", "Unreadable"), icon: "alert");
        o.Tile("lists", T("Listen", "Lists"), icon: "list");
        o.Tile("imports7", T("Importe in 7 Tagen", "Imports in 7 days"), icon: "upload");
        o.Tile("lastScan", T("Letzter Scan", "Last scan"), "time", icon: "refresh");
        o.Tile("registration", T("Registrierung", "Sign-up"), "status", icon: "user");
        o.Tile("lastActivity", T("Letzte Aktivität", "Last activity"), "time", icon: "activity");

        o.Field(new MatField("registrationOpen", T("Registrierung offen", "Open sign-up"), "bool")
        {
            Hint = T("Aus: nur Administratoren legen Konten an.", "Off: only administrators create accounts."),
        });
        var address = new MatField("publicUrl", T("Öffentliche Adresse", "Public address"), "url")
        {
            Max = 300,
            Placeholder = "https://photos.example.com",
            Hint = T("Für Links und E-Mails; leer: die Adresse des Aufrufs.", "For links and mails; empty: the address of the request."),
        };
        o.Field(publicUrl is null ? address : address with
        {
            Fixed = true,
            Hint = T("Festgelegt durch MAT_PUBLIC_URL (Mattiverse); hier lässt sie sich nicht ändern.", "Set by MAT_PUBLIC_URL (Mattiverse); it cannot be changed here."),
        });
        o.Field(new MatField("albumDateFormat", T("Datum im Albumnamen", "Date in the album name"), "text")
        {
            Max = 40,
            Placeholder = "yyyy.MM.dd",
            Hint = T("yyyy.MM.dd ergibt „2023.11.09 Hochzeit Julius“.", "yyyy.MM.dd makes “2023.11.09 Wedding Julius”."),
        });
        o.Field(new MatField("importGapHours", T("Neues Album nach (Stunden)", "New album after (hours)"), "number") { Min = 1, Max = 240 });
        o.Field(new MatField("maxUploadMb", T("Größte Datei (MB)", "Largest file (MB)"), "number") { Min = 0, Max = 10000000, Hint = T("0 = unbegrenzt.", "0 = no limit.") });

        o.LinkAction("openApp", T("Öffnen", "Open"), "external", "/Photos");
        o.LinkAction("openUsers", T("Benutzer", "Users"), "users", "/Admin/Users");
        o.LinkAction("openStorages", T("Speicher", "Storages"), "server", "/Admin/Storages");
        o.LinkAction("openScans", T("Scans", "Scans"), "refresh", "/Admin/Scans");
        o.LinkAction("openConnections", T("Verbindungen", "Connections"), "plug", "/Admin/Connections");
        o.LinkAction("openActivity", T("Aktivität", "Activity"), "activity", "/Admin/Activity");
        o.LinkAction("openSystem", T("System", "System"), "settings", "/Admin/System");
        o.PostAction("scanAll", T("Alle Speicher scannen", "Scan all storages"), "refresh");
        o.PostAction("checkStorages", T("Speicher prüfen", "Check storages"), "server");
        o.PostAction(
            "cleanUp",
            T("Aufräumen", "Clean up"),
            "broom",
            T("Beendete Sitzungen, liegen gebliebene Importe und temporäre Dateien jetzt entfernen?", "Remove ended sessions, abandoned imports and temporary files now?"));

        o.Role("admin", T("Administrator", "Administrator"));
        o.Role("user", T("Benutzer", "User"));
    }

    public async Task<IReadOnlyList<MatHealthCheck>> CheckHealthAsync(MatPlatformCall call, CancellationToken ct)
    {
        if (!await instance.CanConnectAsync(ct))
        {
            return [new MatHealthCheck("database", "down", call.T("Die Datenbank ist nicht erreichbar.", "The database cannot be reached."))];
        }

        var checks = new List<MatHealthCheck> { new("database", "ok") };
        checks.Add(instance.CanWriteFiles() ? new MatHealthCheck("files", "ok") : new MatHealthCheck("files", "down", call.T("Der Datenordner ist nicht beschreibbar.", "The data folder cannot be written.")));
        if (await instance.AnyUserAsync() && (await instance.SnapshotAsync(ct)).Admins == 0)
        {
            checks.Add(new MatHealthCheck("administrators", "degraded", call.T("Kein aktiver Administrator.", "No active administrator.")));
        }

        var (active, down) = await storages.HealthAsync(ct);
        checks.Add(down.Count == 0
            ? new MatHealthCheck("storages", "ok", active == 0 ? call.T("Keine aktiven Speicher.", "No active storages.") : null)
            : new MatHealthCheck("storages", "degraded", call.T($"Nicht erreichbar: {string.Join(", ", down)}.", $"Not reachable: {string.Join(", ", down)}.")));
        return checks;
    }

    public async Task<IReadOnlyDictionary<string, MatStateValue>> GetStateAsync(MatPlatformCall call, CancellationToken ct)
    {
        var s = await instance.SnapshotAsync(ct);
        var st = await instance.SettingsAsync();
        return new Dictionary<string, MatStateValue>
        {
            ["users"] = new(s.Users),
            ["photos"] = new(s.Photos),
            ["videos"] = new(s.Videos),
            ["albums"] = new(s.Albums),
            ["size"] = new(Ui.Bytes(s.LibraryBytes)),
            ["storages"] = new(s.Storages),
            ["pending"] = s.Pending > 0 ? new MatStateValue(s.Pending, "info") : new MatStateValue(s.Pending),
            ["failed"] = s.Failed > 0 ? new MatStateValue(s.Failed, "warn") : new MatStateValue(s.Failed),
            ["lists"] = new(s.Lists),
            ["imports7"] = new(s.Imports7Days),
            ["lastScan"] = new(s.LastScanUtc is { } scan ? new DateTimeOffset(DateTime.SpecifyKind(scan, DateTimeKind.Utc)) : null),
            ["registration"] = st.RegistrationOpen
                ? new MatStateValue("info", Text: call.T("Offen", "Open"))
                : new MatStateValue("ok", Text: call.T("Geschlossen", "Closed")),
            ["lastActivity"] = new(s.LastActivityUtc is { } last ? new DateTimeOffset(DateTime.SpecifyKind(last, DateTimeKind.Utc)) : null),
        };
    }

    public async Task<IReadOnlyDictionary<string, object?>> GetConfigurationAsync(MatPlatformCall call, CancellationToken ct)
    {
        var st = await instance.SettingsAsync();
        return new Dictionary<string, object?>
        {
            ["registrationOpen"] = st.RegistrationOpen,
            ["publicUrl"] = st.PublicUrl ?? string.Empty,
            ["albumDateFormat"] = st.AlbumDateFormat,
            ["importGapHours"] = st.ImportGapHours,
            ["maxUploadMb"] = st.MaxUploadBytes / (1024L * 1024),
        };
    }

    /// <summary>The values are checked against the definition already (MatPlatform, <c>CheckValues</c>); they are checked once more like on the admin page.</summary>
    public async Task<IReadOnlyDictionary<string, string>> ConfigureAsync(IReadOnlyDictionary<string, JsonElement> values, MatPlatformCall call, CancellationToken ct)
    {
        var errors = new Dictionary<string, string>();
        var change = new SettingsChange();
        foreach (var (key, value) in values)
        {
            switch (key)
            {
                case "registrationOpen" when value.ValueKind is JsonValueKind.True or JsonValueKind.False:
                    change = change with { RegistrationOpen = value.GetBoolean() };
                    break;
                case "publicUrl" when settings.AppPublicUrl is not null:
                    errors[key] = call.T("Festgelegt durch MAT_PUBLIC_URL.", "Set by MAT_PUBLIC_URL.");
                    break;
                case "publicUrl" when value.ValueKind is JsonValueKind.String:
                    change = change with { PublicUrl = value.GetString()! };
                    break;
                case "publicUrl" when value.ValueKind is JsonValueKind.Null:
                    change = change with { PublicUrl = string.Empty };
                    break;
                case "albumDateFormat" when value.ValueKind is JsonValueKind.String && InstanceService.IsDateFormat(value.GetString()!):
                    change = change with { AlbumDateFormat = value.GetString() };
                    break;
                case "importGapHours" when value.TryGetInt32(out var gap) && gap is >= 1 and <= 240:
                    change = change with { ImportGapHours = gap };
                    break;
                case "maxUploadMb" when value.TryGetInt64(out var mb) && mb >= 0:
                    change = change with { MaxUploadBytes = mb * 1024L * 1024 };
                    break;
                default:
                    errors[key] = call.T("Dieser Wert passt nicht.", "This value does not fit.");
                    break;
            }
        }

        if (errors.Count > 0)
        {
            return errors;
        }

        if (await instance.SaveSettingsAsync(change) is { } error)
        {
            return new Dictionary<string, string> { [char.ToLowerInvariant(error.Field[0]) + error.Field[1..]] = error.Message };
        }

        log.LogInformation("Settings changed through the platform API by {Caller}: {Keys}", call.Caller, string.Join(", ", values.Keys));
        return errors;
    }

    public async Task<MatActionResult?> RunActionAsync(string id, MatPlatformCall call, CancellationToken ct)
    {
        switch (id)
        {
            case "checkStorages":
                var summary = await storages.CheckAllAsync(ct);
                log.LogInformation("Storages checked through the platform API by {Caller}: {Reachable} of {Total} reachable", call.Caller, summary.Reachable, summary.Total);
                return new MatActionResult(summary.Unreachable == 0, call.T($"{summary.Reachable} von {summary.Total} Speichern erreichbar.", $"{summary.Reachable} of {summary.Total} storages reachable."));
            case "scanAll":
                using (var scope = scopes.CreateScope())
                {
                    var db = scope.ServiceProvider.GetRequiredService<Data.AppDbContext>();
                    var ids = db.Storages.Where(s => s.IsActive && s.IsLibrary).Select(s => s.Id).ToList();
                    ids.ForEach(scanner.Request);
                    log.LogInformation("A scan of {Count} storages was asked for through the platform API by {Caller}", ids.Count, call.Caller);
                    return new MatActionResult(true, call.T($"{ids.Count} Speicher werden gescannt.", $"{ids.Count} storages are being scanned."));
                }

            case "cleanUp":
                var result = await instance.CleanUpAsync(ct);
                log.LogInformation("Clean-up through the platform API by {Caller}: {Sessions} sessions, {Imports} imports", call.Caller, result.Sessions, result.Imports);
                return new MatActionResult(true, call.T(
                    $"Aufgeräumt: {result.Sessions} Sitzungen, {result.Imports} Importe, {result.Files} temporäre Dateien.",
                    $"Cleaned up: {result.Sessions} sessions, {result.Imports} imports, {result.Files} temporary files."));
            default:
                return null;
        }
    }
}
