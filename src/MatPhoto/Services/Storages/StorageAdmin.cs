using MatPhoto.Data;
using MatPhoto.Localization;
using MatPhoto.Services.Accounts;
using MatPhoto.Services.Auth;
using MatPhoto.Services.Library;
using Microsoft.EntityFrameworkCore;

namespace MatPhoto.Services.Storages;

/// <summary>One row of the list of storages in the administration.</summary>
public sealed record StorageRow(long Id, string Name, StorageKind Kind, string Where, bool IsActive, bool IsReadOnly, bool IsLibrary, bool ForEveryone, int People, int Albums, int Media, DateTime? LastScanDate, StorageStatus? Status);

/// <summary>What the list asks for (as it comes from the toolbar).</summary>
public sealed record StorageCriteria(string? Search, string? Kind, string? State, string Sort, bool Descending, int Page, int PageSize);

public sealed record StorageList(List<StorageRow> Items, int Total, int Matching);

/// <summary>The form of a storage as it comes from the page (not yet checked). An empty password or key keeps the stored one.</summary>
public sealed record StorageInput(
    string? Name,
    StorageKind Kind,
    string? RootPath,
    string? Host,
    int? Port,
    string? Share,
    string? Username,
    string? Password,
    bool ClearPassword,
    string? Domain,
    string? Url,
    bool UseTls,
    bool AcceptAnyCertificate,
    bool UseKey,
    string? PrivateKey,
    bool IsReadOnly,
    bool IsActive,
    bool IsLibrary,
    int ScanIntervalMinutes,
    string? ExcludeFolders,
    string? Note);

/// <summary>A storage for the edit page: everything but the secrets (only whether there are some).</summary>
public sealed record StorageDetail(
    long Id,
    string Name,
    StorageKind Kind,
    string RootPath,
    string? Host,
    int? Port,
    string? Share,
    string? Username,
    string? Domain,
    string? Url,
    bool UseTls,
    bool AcceptAnyCertificate,
    bool HasPassword,
    bool HasPrivateKey,
    bool IsReadOnly,
    bool IsActive,
    bool IsLibrary,
    int ScanIntervalMinutes,
    string? ExcludeFolders,
    string? Note,
    DateTime? LastScanDate,
    string? LastScanResult,
    StorageStatus? Status);

public sealed record StoragePerson(long UserId, string Name, string Email, AccessRole Role);

/// <summary>Who may use a storage: everybody with a role (or nobody), and the people of its list.</summary>
public sealed record StorageAccessInfo(long Id, string Name, bool IsActive, bool IsReadOnly, bool ForEveryone, AccessRole EveryoneRole, List<StoragePerson> People);

/// <summary>
/// The storages for administrators: list, create, change, test, delete, and who may use them. Every function checks in its first line that the actor is
/// an active administrator (null / denied otherwise), whatever the page did before. The secrets are encrypted (DataProtection) and never leave this class in
/// clear text; an empty field keeps the stored one. Deleting a storage never deletes its files: only the entry of MatPhoto goes.
/// </summary>
public static class StorageAdmin
{
    public const int NameMax = 120;

    public static readonly AccessRole[] Roles = [AccessRole.Viewer, AccessRole.Editor];

    /// <summary>The name of a kind of storage for people (Local folder, SMB share …).</summary>
    public static string KindName(StorageKind kind) => Text.T("Storages.Kind." + kind);

    // ---- list ---------------------------------------------------------------------------------------------------------------------

    public static async Task<StorageList?> ListAsync(AppDbContext db, StorageConnections connections, long adminId, StorageCriteria criteria, CancellationToken ct = default)
    {
        if (!await UserAdmin.IsAdminAsync(db, adminId, ct))
        {
            return null;
        }

        var all = await db.Storages.AsNoTracking()
            .Select(l => new { Storage = l, People = l.Access.Count, Albums = db.Albums.Count(a => a.StorageId == l.Id), Media = db.Media.Count(m => m.StorageId == l.Id) })
            .ToListAsync(ct);
        var rows = all.Select(x => new StorageRow(x.Storage.Id, x.Storage.Name, x.Storage.Kind, WhereOf(x.Storage), x.Storage.IsActive, x.Storage.IsReadOnly,
            x.Storage.IsLibrary, x.Storage.ForEveryone, x.People, x.Albums, x.Media, x.Storage.LastScanDate, connections.StatusOf(x.Storage.Id))).ToList();
        var total = rows.Count;

        IEnumerable<StorageRow> query = rows;
        if (!string.IsNullOrWhiteSpace(criteria.Search))
        {
            var s = criteria.Search.Trim();
            query = query.Where(r => r.Name.Contains(s, StringComparison.OrdinalIgnoreCase) || r.Where.Contains(s, StringComparison.OrdinalIgnoreCase));
        }

        if (Enum.TryParse<StorageKind>(criteria.Kind, ignoreCase: true, out var kind) && Enum.IsDefined(kind))
        {
            query = query.Where(r => r.Kind == kind);
        }

        query = criteria.State switch
        {
            "reachable" => query.Where(r => r.IsActive && r.Status is { Reachable: true }),
            "unreachable" => query.Where(r => r.IsActive && r.Status is { Reachable: false }),
            "inactive" => query.Where(r => !r.IsActive),
            "active" => query.Where(r => r.IsActive),
            _ => query,
        };

        query = (criteria.Sort, criteria.Descending) switch
        {
            ("kind", false) => query.OrderBy(r => r.Kind).ThenBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase),
            ("kind", true) => query.OrderByDescending(r => r.Kind).ThenBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase),
            ("photos", false) => query.OrderBy(r => r.Media),
            ("photos", true) => query.OrderByDescending(r => r.Media),
            ("free", false) => query.OrderBy(r => r.Status?.Space?.Free ?? long.MaxValue),
            ("free", true) => query.OrderByDescending(r => r.Status?.Space?.Free ?? -1),
            (_, true) => query.OrderByDescending(r => r.Name, StringComparer.CurrentCultureIgnoreCase),
            _ => query.OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase),
        };

        var matching = query.ToList();
        var page = matching.Skip(Math.Max(0, criteria.Page - 1) * criteria.PageSize).Take(criteria.PageSize).ToList();
        return new StorageList(page, total, matching.Count);
    }

    /// <summary>Where a storage is, for people: the folder, the share, the server and port, the address (never a password).</summary>
    public static string WhereOf(Storage l)
    {
        var port = l.Port is > 0 ? ":" + l.Port.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) : string.Empty;
        var folder = string.IsNullOrWhiteSpace(l.RootPath) ? string.Empty : PathGuard.RemoteBase(l.RootPath);
        return l.Kind switch
        {
            StorageKind.Local => l.RootPath,
            StorageKind.Smb => $@"\\{l.Host}{port}" + (string.IsNullOrWhiteSpace(l.Share) ? string.Empty : @"\" + l.Share + folder.Replace('/', '\\')),
            StorageKind.Sftp => $"sftp://{(string.IsNullOrEmpty(l.Username) ? string.Empty : l.Username + "@")}{l.Host}{port}{folder}",
            StorageKind.Ftp => $"{(l.UseTls ? "ftps" : "ftp")}://{(string.IsNullOrEmpty(l.Username) ? string.Empty : l.Username + "@")}{l.Host}{port}{folder}",
            StorageKind.WebDav => l.Url ?? string.Empty,
            _ => string.Empty,
        };
    }

    // ---- one storage -------------------------------------------------------------------------------------------------------------

    public static async Task<StorageDetail?> GetAsync(AppDbContext db, StorageConnections connections, long adminId, long id, CancellationToken ct = default)
    {
        if (!await UserAdmin.IsAdminAsync(db, adminId, ct))
        {
            return null;
        }

        var l = await db.Storages.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        return l is null ? null : new StorageDetail(l.Id, l.Name, l.Kind, l.RootPath, l.Host, l.Port, l.Share, l.Username, l.Domain, l.Url, l.UseTls, l.AcceptAnyCertificate,
            !string.IsNullOrEmpty(l.SecretProtected), !string.IsNullOrEmpty(l.PrivateKeyProtected), l.IsReadOnly, l.IsActive, l.IsLibrary, l.ScanIntervalMinutes, l.ExcludeFolders, l.Note,
            l.LastScanDate, l.LastScanResult, connections.StatusOf(l.Id));
    }

    /// <summary>Creates (<paramref name="id"/> null) or changes a storage. Answers its id, or the errors by field (or Denied / not found as null id and no error).</summary>
    public static async Task<(long? Id, List<FieldError> Errors)> SaveAsync(AppDbContext db, StorageConnections connections, long adminId, long? id, StorageInput input, CancellationToken ct = default)
    {
        if (!await UserAdmin.IsAdminAsync(db, adminId, ct))
        {
            return (null, []);
        }

        Storage? storage = null;
        if (id is { } existingId)
        {
            storage = await db.Storages.FirstOrDefaultAsync(l => l.Id == existingId, ct);
            if (storage is null)
            {
                return (null, []);
            }
        }

        var errors = Validate(connections, input, storage);
        var name = (input.Name ?? string.Empty).Trim();
        if (name.Length > 0 && await db.Storages.AnyAsync(l => l.Name.ToLower() == name.ToLower() && l.Id != (id ?? 0), ct))
        {
            errors.Add(new FieldError(nameof(StorageInput.Name), Text.T("Storages.Error.NameTaken")));
        }

        if (errors.Count > 0)
        {
            return (null, errors);
        }

        var created = storage is null;
        storage ??= db.Storages.Add(new Storage()).Entity;
        Apply(connections, storage, input);
        if (created)
        {
            // the administrator who adds it may import into it and sort it at once (managing storages alone shows no photo); the tab Access changes it
            storage.Access.Add(new StorageAccessEntry { UserId = adminId, Role = AccessRole.Editor });
        }
        else
        {
            storage.UpdateState = SoftEntity.Updated;
        }

        ActivityLog.Add(db, adminId, "storage", detail: created ? "create" : "change", target: storage.Name);
        await db.SaveChangesAsync(ct);

        // the menu and the list show the new state soon; the page does not wait for a slow server
        connections.Forget(storage.Id);
        if (storage.IsActive)
        {
            var copy = Copy(storage);
            _ = Task.Run(() => StorageMonitor.CheckAsync(connections, copy, CancellationToken.None), CancellationToken.None);
        }

        return (storage.Id, []);
    }

    /// <summary>Tries the connection with the values of the form (a stored secret is used when the field is empty), without saving anything.</summary>
    public static async Task<Outcome<StorageStatus>> TestAsync(AppDbContext db, StorageConnections connections, long adminId, long? id, StorageInput input, CancellationToken ct = default)
    {
        if (!await UserAdmin.IsAdminAsync(db, adminId, ct))
        {
            return Outcome<StorageStatus>.Denied();
        }

        Storage? stored = null;
        if (id is { } existingId)
        {
            stored = await db.Storages.AsNoTracking().FirstOrDefaultAsync(l => l.Id == existingId, ct);
            if (stored is null)
            {
                return Outcome<StorageStatus>.Denied();
            }
        }

        var errors = Validate(connections, input with { Name = string.IsNullOrWhiteSpace(input.Name) ? "test" : input.Name }, stored);
        if (errors.Count > 0)
        {
            return Outcome<StorageStatus>.Fail(errors[0].Message);
        }

        var probe = new Storage();
        if (stored is not null)
        {
            probe.SecretProtected = stored.SecretProtected;
            probe.PrivateKeyProtected = stored.PrivateKeyProtected;
        }

        Apply(connections, probe, input);
        var status = await StorageMonitor.ProbeAsync(connections, connections.SettingsOf(probe), ct);
        if (stored is not null && Same(stored, probe))
        {
            connections.Report(stored.Id, status);
        }

        return Outcome<StorageStatus>.Success(status);
    }

    /// <summary>Removes a storage from MatPhoto (soft delete) with its albums and photos in the database. Its files stay where they are.</summary>
    public static async Task<bool> DeleteAsync(AppDbContext db, StorageConnections connections, long adminId, long id, CancellationToken ct = default)
    {
        if (!await UserAdmin.IsAdminAsync(db, adminId, ct))
        {
            return false;
        }

        var storage = await db.Storages.FirstOrDefaultAsync(l => l.Id == id, ct);
        if (storage is null)
        {
            return false;
        }

        storage.UpdateState = SoftEntity.Deleted;
        storage.IsActive = false;
        storage.SecretProtected = null;
        storage.PrivateKeyProtected = null;
        db.StorageAccess.RemoveRange(db.StorageAccess.Where(a => a.StorageId == id));
        await db.Media.Where(m => m.StorageId == id).ExecuteUpdateAsync(u => u.SetProperty(m => m.UpdateState, SoftEntity.Deleted), ct);
        await db.Albums.Where(a => a.StorageId == id).ExecuteUpdateAsync(u => u.SetProperty(a => a.UpdateState, SoftEntity.Deleted), ct);
        ActivityLog.Add(db, adminId, "storage", detail: "delete", target: storage.Name);
        await db.SaveChangesAsync(ct);
        connections.Forget(id);
        return true;
    }

    /// <summary>Checks every active storage now (the button of the list, the action of the platform).</summary>
    public static async Task<StorageCheckSummary?> CheckAllAsync(AppDbContext db, StorageMonitor monitor, long adminId, CancellationToken ct = default)
        => await UserAdmin.IsAdminAsync(db, adminId, ct) ? await monitor.CheckAllAsync(ct) : null;

    // ---- access -------------------------------------------------------------------------------------------------------------------

    public static async Task<StorageAccessInfo?> AccessAsync(AppDbContext db, long adminId, long id, CancellationToken ct = default)
    {
        if (!await UserAdmin.IsAdminAsync(db, adminId, ct))
        {
            return null;
        }

        var l = await db.Storages.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (l is null)
        {
            return null;
        }

        var people = await db.StorageAccess.AsNoTracking().Where(a => a.StorageId == id)
            .Join(db.Users, a => a.UserId, u => u.Id, (a, u) => new StoragePerson(u.Id, u.DisplayName, u.Email, a.Role))
            .ToListAsync(ct);
        people = people.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        return new StorageAccessInfo(l.Id, l.Name, l.IsActive, l.IsReadOnly, l.ForEveryone, l.EveryoneRole, people);
    }

    /// <summary>Everybody gets a role on the storage (Viewer or Editor), or nobody (None): then only the people of the list.</summary>
    public static async Task<bool> SetEveryoneAsync(AppDbContext db, long adminId, long id, AccessRole role, CancellationToken ct = default)
    {
        if (!await UserAdmin.IsAdminAsync(db, adminId, ct) || (role != AccessRole.None && !Roles.Contains(role)))
        {
            return false;
        }

        var l = await db.Storages.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (l is null)
        {
            return false;
        }

        l.ForEveryone = role != AccessRole.None;
        if (role != AccessRole.None)
        {
            l.EveryoneRole = role;
        }

        l.UpdateState = SoftEntity.Updated;
        ActivityLog.Add(db, adminId, "storage", detail: "access everyone " + role.ToString().ToLowerInvariant(), target: l.Name);
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>Adds a person by the address of their account (or changes their role when they are on the list already).</summary>
    public static async Task<Outcome<StoragePerson>> AddPersonAsync(AppDbContext db, long adminId, long id, string? email, AccessRole role, CancellationToken ct = default)
    {
        if (!await UserAdmin.IsAdminAsync(db, adminId, ct) || !Roles.Contains(role))
        {
            return Outcome<StoragePerson>.Denied();
        }

        var l = await db.Storages.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (l is null)
        {
            return Outcome<StoragePerson>.Denied();
        }

        var address = (email ?? string.Empty).Trim().ToLowerInvariant();
        if (address.Length == 0)
        {
            return Outcome<StoragePerson>.Fail(Text.T("Storages.Access.EmailRequired"));
        }

        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Email == address, ct);
        if (user is null)
        {
            return Outcome<StoragePerson>.Fail(Text.T("Storages.Access.NoAccount", address));
        }

        var entry = await db.StorageAccess.FirstOrDefaultAsync(a => a.StorageId == id && a.UserId == user.Id, ct);
        if (entry is null)
        {
            db.StorageAccess.Add(new StorageAccessEntry { StorageId = id, UserId = user.Id, Role = role });
        }
        else
        {
            entry.Role = role;
        }

        ActivityLog.Add(db, adminId, "storage", detail: $"access {user.Email} {role.ToString().ToLowerInvariant()}", target: l.Name);
        await db.SaveChangesAsync(ct);
        return Outcome<StoragePerson>.Success(new StoragePerson(user.Id, user.DisplayName, user.Email, role));
    }

    public static async Task<bool> SetPersonAsync(AppDbContext db, long adminId, long id, long userId, AccessRole role, CancellationToken ct = default)
    {
        if (!await UserAdmin.IsAdminAsync(db, adminId, ct) || !Roles.Contains(role))
        {
            return false;
        }

        var entry = await db.StorageAccess.Include(a => a.Storage).Include(a => a.User).FirstOrDefaultAsync(a => a.StorageId == id && a.UserId == userId, ct);
        if (entry is null || entry.Storage is null)
        {
            return false;
        }

        entry.Role = role;
        ActivityLog.Add(db, adminId, "storage", detail: $"access {entry.User?.Email} {role.ToString().ToLowerInvariant()}", target: entry.Storage.Name);
        await db.SaveChangesAsync(ct);
        return true;
    }

    public static async Task<bool> RemovePersonAsync(AppDbContext db, long adminId, long id, long userId, CancellationToken ct = default)
    {
        if (!await UserAdmin.IsAdminAsync(db, adminId, ct))
        {
            return false;
        }

        var entry = await db.StorageAccess.Include(a => a.Storage).Include(a => a.User).FirstOrDefaultAsync(a => a.StorageId == id && a.UserId == userId, ct);
        if (entry is null || entry.Storage is null)
        {
            return false;
        }

        db.StorageAccess.Remove(entry);
        ActivityLog.Add(db, adminId, "storage", detail: $"access {entry.User?.Email} removed", target: entry.Storage.Name);
        await db.SaveChangesAsync(ct);
        return true;
    }

    // ---- helpers ------------------------------------------------------------------------------------------------------------------

    private static List<FieldError> Validate(StorageConnections connections, StorageInput input, Storage? stored)
    {
        var errors = new List<FieldError>();
        var name = (input.Name ?? string.Empty).Trim();
        if (name.Length == 0)
        {
            errors.Add(new FieldError(nameof(StorageInput.Name), Text.T("Common.NameRequired")));
        }
        else if (name.Length > NameMax)
        {
            errors.Add(new FieldError(nameof(StorageInput.Name), Text.T("Storages.Error.NameTooLong", NameMax)));
        }

        if (!Enum.IsDefined(input.Kind))
        {
            errors.Add(new FieldError(nameof(StorageInput.Kind), Text.T("Storages.Error.KindRequired")));
            return errors;
        }

        var root = (input.RootPath ?? string.Empty).Trim();
        if (root.Length > 500)
        {
            errors.Add(new FieldError(nameof(StorageInput.RootPath), Text.T("Storages.Error.TooLong")));
        }

        switch (input.Kind)
        {
            case StorageKind.Local:
                if (root.Length == 0)
                {
                    errors.Add(new FieldError(nameof(StorageInput.RootPath), Text.T("Storages.Error.PathRequired")));
                }
                else if (!Path.IsPathFullyQualified(root))
                {
                    errors.Add(new FieldError(nameof(StorageInput.RootPath), Text.T("Storages.Error.PathNotAbsolute")));
                }
                else if (!connections.IsAllowedLocalFolder(Path.GetFullPath(root)))
                {
                    errors.Add(new FieldError(nameof(StorageInput.RootPath), Text.T("Storages.Error.PathOutside", connections.StorageRoot)));
                }
                else if (!Directory.Exists(root))
                {
                    errors.Add(new FieldError(nameof(StorageInput.RootPath), Text.T("Storages.Error.PathMissing")));
                }

                break;
            case StorageKind.Smb or StorageKind.Sftp or StorageKind.Ftp:
                var host = (input.Host ?? string.Empty).Trim();
                if (host.Length == 0)
                {
                    errors.Add(new FieldError(nameof(StorageInput.Host), Text.T("Storages.Error.HostRequired")));
                }
                else if (host.Length > 255 || host.Any(c => char.IsWhiteSpace(c) || c is '/' or '\\' or '@'))
                {
                    errors.Add(new FieldError(nameof(StorageInput.Host), Text.T("Storages.Error.HostInvalid")));
                }

                if (input.Port is { } port && port is < 1 or > 65535)
                {
                    errors.Add(new FieldError(nameof(StorageInput.Port), Text.T("Storages.Error.PortInvalid")));
                }

                if ((input.Share ?? string.Empty).Trim().Any(c => c is '/' or '\\'))
                {
                    errors.Add(new FieldError(nameof(StorageInput.Share), Text.T("Storages.Error.ShareInvalid")));
                }

                if (input.Kind == StorageKind.Sftp && input.UseKey && string.IsNullOrWhiteSpace(input.PrivateKey) && string.IsNullOrEmpty(stored?.PrivateKeyProtected))
                {
                    errors.Add(new FieldError(nameof(StorageInput.PrivateKey), Text.T("Storages.Error.KeyRequired")));
                }

                if (!string.IsNullOrWhiteSpace(input.PrivateKey) && (input.PrivateKey.Length > 12000 || !input.PrivateKey.Contains("PRIVATE KEY", StringComparison.Ordinal)))
                {
                    errors.Add(new FieldError(nameof(StorageInput.PrivateKey), Text.T("Storages.Error.KeyInvalid")));
                }

                break;
            case StorageKind.WebDav:
                var url = (input.Url ?? string.Empty).Trim();
                if (url.Length == 0)
                {
                    errors.Add(new FieldError(nameof(StorageInput.Url), Text.T("Storages.Error.UrlRequired")));
                }
                else if (url.Length > 500 || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(uri.UserInfo))
                {
                    errors.Add(new FieldError(nameof(StorageInput.Url), Text.T("Storages.Error.UrlInvalid")));
                }

                break;
        }

        if ((input.Username ?? string.Empty).Length > 255 || (input.Domain ?? string.Empty).Length > 255 || (input.Share ?? string.Empty).Length > 255 || (input.Password ?? string.Empty).Length > 1000)
        {
            errors.Add(new FieldError(nameof(StorageInput.Username), Text.T("Storages.Error.TooLong")));
        }

        if (input.ScanIntervalMinutes is < 0 or > 10080)
        {
            errors.Add(new FieldError(nameof(StorageInput.ScanIntervalMinutes), Text.T("Storages.Error.IntervalInvalid")));
        }

        if ((input.ExcludeFolders ?? string.Empty).Length > 1000)
        {
            errors.Add(new FieldError(nameof(StorageInput.ExcludeFolders), Text.T("Storages.Error.TooLong")));
        }

        if ((input.Note ?? string.Empty).Length > 1000)
        {
            errors.Add(new FieldError(nameof(StorageInput.Note), Text.T("Storages.Error.TooLong")));
        }

        return errors;
    }

    /// <summary>Takes the checked values into the entity: what the kind does not use is cleared, a secret is only replaced by a new one.</summary>
    private static void Apply(StorageConnections connections, Storage l, StorageInput input)
    {
        static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

        l.Name = (input.Name ?? string.Empty).Trim();
        l.Kind = input.Kind;
        l.IsReadOnly = input.IsReadOnly;
        l.IsActive = input.IsActive;
        l.IsLibrary = input.IsLibrary;
        l.ScanIntervalMinutes = Math.Clamp(input.ScanIntervalMinutes, 0, 10080);
        l.ExcludeFolders = Clean(input.ExcludeFolders);
        l.Note = Clean(input.Note);
        var remote = input.Kind is StorageKind.Smb or StorageKind.Sftp or StorageKind.Ftp;
        l.RootPath = input.Kind switch
        {
            StorageKind.Local => Path.TrimEndingDirectorySeparator(Path.GetFullPath((input.RootPath ?? string.Empty).Trim())),
            StorageKind.WebDav => string.Empty,
            _ => (input.RootPath ?? string.Empty).Trim(),
        };
        l.Host = remote ? Clean(input.Host) : null;
        l.Port = remote ? input.Port : null;
        l.Share = input.Kind == StorageKind.Smb ? Clean(input.Share) : null;
        l.Domain = input.Kind == StorageKind.Smb ? Clean(input.Domain) : null;
        l.Url = input.Kind == StorageKind.WebDav ? Clean(input.Url) : null;
        l.UseTls = input.Kind == StorageKind.Ftp && input.UseTls;
        l.AcceptAnyCertificate = input.Kind is StorageKind.Ftp or StorageKind.WebDav && input.AcceptAnyCertificate;
        l.Username = input.Kind == StorageKind.Local ? null : Clean(input.Username);

        if (input.Kind == StorageKind.Local)
        {
            l.SecretProtected = null;
            l.PrivateKeyProtected = null;
            return;
        }

        if (input.ClearPassword)
        {
            l.SecretProtected = null;
        }
        else if (!string.IsNullOrEmpty(input.Password))
        {
            l.SecretProtected = connections.Protect(input.Password);
        }

        if (input.Kind != StorageKind.Sftp || !input.UseKey)
        {
            l.PrivateKeyProtected = null;
        }
        else if (!string.IsNullOrWhiteSpace(input.PrivateKey))
        {
            l.PrivateKeyProtected = connections.Protect(input.PrivateKey.Trim());
        }
    }

    private static bool Same(Storage a, Storage b)
        => a.Kind == b.Kind && a.RootPath == b.RootPath && a.Host == b.Host && a.Port == b.Port && a.Share == b.Share && a.Username == b.Username && a.Domain == b.Domain
           && a.Url == b.Url && a.UseTls == b.UseTls && a.SecretProtected == b.SecretProtected && a.PrivateKeyProtected == b.PrivateKeyProtected;

    private static Storage Copy(Storage l) => new()
    {
        Id = l.Id, Name = l.Name, Kind = l.Kind, RootPath = l.RootPath, Host = l.Host, Port = l.Port, Share = l.Share, Username = l.Username, SecretProtected = l.SecretProtected,
        Domain = l.Domain, Url = l.Url, UseTls = l.UseTls, AcceptAnyCertificate = l.AcceptAnyCertificate, PrivateKeyProtected = l.PrivateKeyProtected, IsActive = l.IsActive,
    };
}
