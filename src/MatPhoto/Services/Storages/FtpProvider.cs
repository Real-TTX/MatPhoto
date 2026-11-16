using System.Net.Sockets;
using FluentFTP;
using FluentFTP.Exceptions;
using MatPhoto.Data;

namespace MatPhoto.Services.Storages;

/// <summary>
/// A folder on an FTP or FTPS server (FluentFTP, explicit TLS). Data connections are passive (EPSV: only the port comes back, which works behind NAT and in Docker).
/// The connections come from a pool. An empty folder of the location is the folder the account starts in.
/// </summary>
public sealed class FtpProvider(StorageSettings settings, ConnectionPool<AsyncFtpClient> pool) : IStorageProvider
{
    public const int DefaultPort = 21;

    private readonly string _base = PathGuard.RemoteBase(settings.RootPath);

    /// <summary>No folder given: the folder the account starts in (an explicit "/" is the top of the server).</summary>
    private readonly bool _useHome = string.IsNullOrWhiteSpace(settings.RootPath);

    public StorageKind Kind => StorageKind.Ftp;

    public bool HasPermissions => true;

    private int Port => settings.Port is > 0 and < 65536 ? settings.Port.Value : DefaultPort;

    private string Key => ConnectionPool<AsyncFtpClient>.KeyOf("ftp", settings.Host, Port.ToString(System.Globalization.CultureInfo.InvariantCulture), settings.Username, settings.Password,
        settings.UseTls.ToString(), settings.AcceptAnyCertificate.ToString());

    public Task CheckAsync(CancellationToken ct) => Run(async (c, home) =>
    {
        if (!await c.DirectoryExists(Remote(home, string.Empty), ct))
        {
            throw new StorageException(StorageError.NotFound);
        }
    }, ct);

    public Task<IReadOnlyList<StorageEntry>> ListAsync(string path, CancellationToken ct) => Run<IReadOnlyList<StorageEntry>>(async (c, home) =>
    {
        var normalized = PathGuard.Check(path);
        var remote = Remote(home, normalized);
        if (!await c.DirectoryExists(remote, ct))
        {
            throw new StorageException(StorageError.NotFound);
        }

        var items = await c.GetListing(remote, FtpListOption.AllFiles, ct);
        return items
            .Where(i => i.Name is not ("." or ".."))
            .Select(i => ToEntry(i, PathGuard.Combine(normalized, i.Name)))
            .ToList();
    }, ct);

    public Task<StorageEntry?> GetAsync(string path, CancellationToken ct) => Run(async (c, home) =>
    {
        var normalized = PathGuard.Check(path);
        var remote = Remote(home, normalized);
        if (normalized.Length == 0 || await c.DirectoryExists(remote, ct))
        {
            return (StorageEntry?)new StorageEntry(PathGuard.NameOf(normalized), normalized, true, 0, await ModifiedAsync(c, remote, ct));
        }

        if (!await c.FileExists(remote, ct))
        {
            return null;
        }

        var size = await c.GetFileSize(remote, -1, ct);
        var name = PathGuard.NameOf(normalized);
        return new StorageEntry(name, normalized, false, Math.Max(0, size), await ModifiedAsync(c, remote, ct), name.StartsWith('.'));
    }, ct);

    public async Task<Stream> OpenReadAsync(string path, long offset, CancellationToken ct)
    {
        var lease = await ConnectAsync(ct);
        try
        {
            var home = await HomeAsync(lease.Client, ct);
            var remote = Remote(home, PathGuard.Check(path));
            var stream = await Map(async () =>
            {
                if (!await lease.Client.FileExists(remote, ct))
                {
                    throw new StorageException(StorageError.NotFound);
                }

                return await lease.Client.OpenRead(remote, FtpDataType.Binary, offset, 0L, ct);
            }, lease);
            return new HoldingStream(stream, lease);
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    public Task WriteAsync(string path, Stream content, long? length, bool overwrite, CancellationToken ct) => Run(async (c, home) =>
    {
        var normalized = PathGuard.Check(path);
        var remote = Remote(home, normalized);
        if (await c.DirectoryExists(remote, ct) || (!overwrite && await c.FileExists(remote, ct)))
        {
            throw new StorageException(StorageError.Conflict);
        }

        var parent = PathGuard.Parent(normalized);
        if (!string.IsNullOrEmpty(parent))
        {
            await EnsureFolderAsync(c, Remote(home, parent), ct);
        }

        var status = await c.UploadStream(content, remote, overwrite ? FtpRemoteExists.Overwrite : FtpRemoteExists.Skip, createRemoteDir: false, progress: null, token: ct);
        if (status == FtpStatus.Failed)
        {
            throw new StorageException(StorageError.Remote, "The upload failed.");
        }

        if (status == FtpStatus.Skipped)
        {
            throw new StorageException(StorageError.Conflict);
        }
    }, ct);

    public Task CreateFolderAsync(string path, CancellationToken ct) => Run(async (c, home) =>
    {
        var remote = Remote(home, PathGuard.Check(path));
        if (await c.DirectoryExists(remote, ct) || await c.FileExists(remote, ct))
        {
            throw new StorageException(StorageError.Conflict);
        }

        if (!await c.CreateDirectory(remote, true, ct))
        {
            throw new StorageException(StorageError.Remote, "The folder was not created.");
        }
    }, ct);

    public Task MoveAsync(string from, string to, bool overwrite, CancellationToken ct) => Run(async (c, home) =>
    {
        if (PathGuard.Normalize(from).Length == 0)
        {
            throw new StorageException(StorageError.InvalidPath);
        }

        var source = Remote(home, PathGuard.Check(from));
        var targetPath = PathGuard.Check(to);
        var target = Remote(home, targetPath);
        var isFolder = await c.DirectoryExists(source, ct);
        if (!isFolder && !await c.FileExists(source, ct))
        {
            throw new StorageException(StorageError.NotFound);
        }

        var caseOnly = string.Equals(source, target, StringComparison.OrdinalIgnoreCase) && source != target;
        if (!caseOnly && (await c.DirectoryExists(target, ct) || await c.FileExists(target, ct)))
        {
            if (!overwrite || isFolder || await c.DirectoryExists(target, ct))
            {
                throw new StorageException(StorageError.Conflict);
            }

            await c.DeleteFile(target, ct);
        }

        var parent = PathGuard.Parent(targetPath);
        if (!string.IsNullOrEmpty(parent))
        {
            await EnsureFolderAsync(c, Remote(home, parent), ct);
        }

        await c.Rename(source, target, ct);
    }, ct);

    /// <summary>Makes a folder with the folders above it; another upload into the same new folder may make it at the same moment (then it is just there).</summary>
    private static async Task EnsureFolderAsync(AsyncFtpClient c, string remote, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            if (await c.DirectoryExists(remote, ct))
            {
                return;
            }

            try
            {
                if (await c.CreateDirectory(remote, true, ct))
                {
                    return;
                }
            }
            catch (FtpCommandException) when (attempt < 2)
            {
                // made meanwhile (550 exists): look again
            }

            if (attempt >= 2)
            {
                throw new StorageException(StorageError.Remote, "The folder was not created.");
            }
        }
    }

    public Task DeleteAsync(string path, CancellationToken ct) => Run(async (c, home) =>
    {
        var normalized = PathGuard.Check(path);
        if (normalized.Length == 0)
        {
            throw new StorageException(StorageError.InvalidPath);
        }

        var remote = Remote(home, normalized);
        if (await c.DirectoryExists(remote, ct))
        {
            await c.DeleteDirectory(remote, FtpListOption.Recursive | FtpListOption.AllFiles, ct);
            return;
        }

        if (!await c.FileExists(remote, ct))
        {
            throw new StorageException(StorageError.NotFound);
        }

        await c.DeleteFile(remote, ct);
    }, ct);

    /// <summary>FTP has no way to ask for the space.</summary>
    public Task<StorageSpace?> SpaceAsync(CancellationToken ct) => Task.FromResult<StorageSpace?>(null);

    /// <summary>Only when the server sends a Unix listing (the rwx bits); otherwise null and the dialog leaves the part out.</summary>
    public Task<StoragePermissions?> PermissionsAsync(string path, CancellationToken ct) => Run(async (c, home) =>
    {
        var normalized = PathGuard.Check(path);
        var remote = Remote(home, normalized);
        FtpListItem? item;
        if (normalized.Length == 0)
        {
            item = null;
        }
        else
        {
            var parent = Remote(home, PathGuard.Parent(normalized) ?? string.Empty);
            var name = PathGuard.NameOf(normalized);
            item = (await c.GetListing(parent, FtpListOption.AllFiles, ct)).FirstOrDefault(i => i.Name == name) ?? await c.GetObjectInfo(remote, false, ct);
        }

        if (item is null || item.Chmod <= 0)
        {
            return (StoragePermissions?)null;
        }

        return new StoragePermissions(Convert.ToInt32(item.Chmod.ToString(System.Globalization.CultureInfo.InvariantCulture), 8) & StoragePermissions.Mask,
            string.IsNullOrWhiteSpace(item.RawOwner) ? null : item.RawOwner, string.IsNullOrWhiteSpace(item.RawGroup) ? null : item.RawGroup);
    }, ct);

    public Task SetPermissionsAsync(string path, int mode, CancellationToken ct) => Run(async (c, home) =>
    {
        var remote = Remote(home, PathGuard.Check(path));
        if (!await c.FileExists(remote, ct) && !await c.DirectoryExists(remote, ct))
        {
            throw new StorageException(StorageError.NotFound);
        }

        try
        {
            // SITE CHMOD takes the octal digits: 755
            await c.Chmod(remote, int.Parse(StoragePermissions.ToText(mode), System.Globalization.CultureInfo.InvariantCulture), ct);
        }
        catch (FtpCommandException ex)
        {
            throw new StorageException(StorageError.NotSupported, ex.Message, ex);
        }
    }, ct);

    // ---- connection ---------------------------------------------------------------------------------------------------------------

    private Task<ConnectionPool<AsyncFtpClient>.Lease> ConnectAsync(CancellationToken ct) => pool.RentAsync(Key, () => CreateAsync(ct));

    private async Task<AsyncFtpClient> CreateAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(settings.Host))
        {
            throw new StorageException(StorageError.Unreachable);
        }

        var client = new AsyncFtpClient(settings.Host.Trim(), string.IsNullOrEmpty(settings.Username) ? "anonymous" : settings.Username, settings.Password ?? string.Empty, Port);
        client.Config.EncryptionMode = settings.UseTls ? FtpEncryptionMode.Explicit : FtpEncryptionMode.None;
        client.Config.ValidateAnyCertificate = settings.AcceptAnyCertificate;
        client.Config.ConnectTimeout = 15000;
        client.Config.ReadTimeout = 30000;
        client.Config.DataConnectionConnectTimeout = 15000;
        client.Config.DataConnectionReadTimeout = 30000;
        client.Config.DataConnectionType = FtpDataConnectionType.EPSV;
        client.Config.RetryAttempts = 1;

        // the times of the server (MDTM is UTC by the RFC, and a NAS lists in UTC): taken as UTC, never shifted by the zone of this machine
        client.Config.TimeConversion = FtpDate.UTC;
        try
        {
            await client.Connect(ct);
            return client;
        }
        catch (Exception ex)
        {
            client.Dispose();
            throw ex switch
            {
                FtpAuthenticationException => new StorageException(StorageError.AccessDenied, ex.Message, ex),
                TimeoutException => new StorageException(StorageError.Timeout, null, ex),
                SocketException or IOException => new StorageException(StorageError.Unreachable, ex.Message, ex),
                FtpException => new StorageException(StorageError.Remote, ex.Message, ex),
                _ => ex,
            };
        }
    }

    private static async Task<string> HomeAsync(AsyncFtpClient c, CancellationToken ct)
    {
        // the folder the account starts in (kept per connection: the pool lends the same client again)
        if (!Homes.TryGetValue(c, out var home))
        {
            home = (await c.GetWorkingDirectory(ct)).TrimEnd('/');
            Homes.AddOrUpdate(c, home);
        }

        return home;
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<AsyncFtpClient, string> Homes = [];

    private async Task<T> Run<T>(Func<AsyncFtpClient, string, Task<T>> action, CancellationToken ct)
    {
        using var lease = await ConnectAsync(ct);
        return await Map(async () => await action(lease.Client, await HomeAsync(lease.Client, ct)), lease);
    }

    private Task Run(Func<AsyncFtpClient, string, Task> action, CancellationToken ct) => Run<object?>(async (c, home) =>
    {
        await action(c, home);
        return null;
    }, ct);

    private static async Task<T> Map<T>(Func<Task<T>> action, ConnectionPool<AsyncFtpClient>.Lease lease)
    {
        try
        {
            return await action();
        }
        catch (StorageException)
        {
            throw;
        }
        catch (FtpCommandException ex) when (ex.CompletionCode is "550" or "553")
        {
            throw new StorageException(ex.Message.Contains("ermission", StringComparison.Ordinal) ? StorageError.AccessDenied : StorageError.NotFound, ex.Message, ex);
        }
        catch (Exception ex) when (ex is TimeoutException)
        {
            lease.Broken = true;
            throw new StorageException(StorageError.Timeout, null, ex);
        }
        catch (Exception ex) when (ex is SocketException or IOException or ObjectDisposedException)
        {
            lease.Broken = true;
            throw new StorageException(StorageError.Unreachable, ex.Message, ex);
        }
        catch (FtpException ex)
        {
            lease.Broken = true;
            throw new StorageException(StorageError.Remote, ex.Message, ex);
        }
    }

    private string Remote(string home, string relative) => PathGuard.Remote(_useHome ? home : _base, relative);

    private static async Task<DateTime> ModifiedAsync(AsyncFtpClient c, string remote, CancellationToken ct)
    {
        try
        {
            var value = await c.GetModifiedTime(remote, ct);
            return value == DateTime.MinValue ? DateTime.UtcNow : Utc(value);
        }
        catch (FtpException)
        {
            // not every server knows MDTM
            return DateTime.UtcNow;
        }
    }

    /// <summary>A time of the server as UTC: one without a kind is UTC already (FtpDate.UTC), not a time of this machine.</summary>
    private static DateTime Utc(DateTime value) => value.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(value, DateTimeKind.Utc) : value.ToUniversalTime();

    private static StorageEntry ToEntry(FtpListItem item, string path)
    {
        var folder = item.Type == FtpObjectType.Directory || (item.Type == FtpObjectType.Link && item.LinkObject?.Type == FtpObjectType.Directory);
        return new StorageEntry(item.Name, path, folder, folder ? 0 : Math.Max(0, item.Size),
            item.Modified == DateTime.MinValue ? DateTime.UtcNow : Utc(item.Modified), item.Name.StartsWith('.'), item.Type == FtpObjectType.Link);
    }
}
