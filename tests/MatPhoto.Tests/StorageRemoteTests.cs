using System.Text;
using MatPhoto.Data;
using MatPhoto.Services.Storages;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;

namespace MatPhoto.Tests;

/// <summary>
/// A test against real servers of the four protocols: it runs when <c>MATPHOTO_TEST_STORAGES</c> names them, else it is skipped. <c>loctest</c> means the throwaway
/// servers of the compose project <c>matphoto-loctest</c> (SFTP 47222 with the folder /upload, SMB 47445 with the share public, FTP 47021, WebDAV 47080; tester /
/// Secret123); otherwise <c>host=…;sftp=…;smb=…;ftp=…;webdav=…;user=…;password=…</c>.
/// </summary>
public sealed class RemoteFactAttribute : FactAttribute
{
    public RemoteFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MATPHOTO_TEST_STORAGES")))
        {
            Skip = "MATPHOTO_TEST_STORAGES is not set (servers for SFTP, SMB, FTP and WebDAV, e.g. the compose project matphoto-loctest: \"loctest\").";
        }
    }
}

public sealed class StorageRemoteTests : IDisposable
{
    private static readonly CancellationToken None = CancellationToken.None;
    private readonly StorageConnections _connections;
    private readonly Dictionary<string, string> _servers;

    public StorageRemoteTests()
    {
        var spec = Environment.GetEnvironmentVariable("MATPHOTO_TEST_STORAGES") ?? string.Empty;
        _servers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["host"] = "127.0.0.1", ["sftp"] = "47222", ["smb"] = "47445", ["ftp"] = "47021", ["webdav"] = "47080", ["user"] = "tester", ["password"] = "Secret123",
        };
        foreach (var pair in spec.Split(';', StringSplitOptions.RemoveEmptyEntries).Select(p => p.Split('=', 2)).Where(p => p.Length == 2))
        {
            _servers[pair[0].Trim()] = pair[1].Trim();
        }

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["StorageRoot"] = Path.GetTempPath(), ["DataDirectory"] = Path.Combine(Path.GetTempPath(), "matphoto-remote-test-data") }).Build();
        _connections = new StorageConnections(new EphemeralDataProtectionProvider(), new PlainHttp(), configuration);
    }

    [RemoteFact]
    public Task Sftp() => ExerciseAsync(Settings(StorageKind.Sftp, root: "/upload"), permissions: true);

    [RemoteFact]
    public Task Smb() => ExerciseAsync(Settings(StorageKind.Smb, share: "public"), permissions: false);

    [RemoteFact]
    public Task Ftp() => ExerciseAsync(Settings(StorageKind.Ftp), permissions: true);

    [RemoteFact]
    public Task WebDav() => ExerciseAsync(Settings(StorageKind.WebDav), permissions: false);

    [RemoteFact]
    public async Task A_wrong_password_is_access_denied()
    {
        foreach (var kind in new[] { StorageKind.Sftp, StorageKind.Smb, StorageKind.Ftp, StorageKind.WebDav })
        {
            var status = await StorageMonitor.ProbeAsync(_connections, Settings(kind, share: kind == StorageKind.Smb ? "public" : null, root: kind == StorageKind.Sftp ? "/upload" : string.Empty) with { Password = "wrong" }, None);
            Assert.False(status.Reachable, kind.ToString());
            Assert.Equal(StorageError.AccessDenied, status.Error);
        }
    }

    [RemoteFact]
    public async Task Smb_without_a_share_shows_the_shares()
    {
        var provider = _connections.Create(Settings(StorageKind.Smb));
        var shares = (await provider.ListAsync(string.Empty, None)).Select(e => e.Name).ToList();
        Assert.Contains("public", shares);
        Assert.All(await provider.ListAsync(string.Empty, None), e => Assert.True(e.IsFolder));
        Assert.Equal(StorageError.ShareLevel, (await Assert.ThrowsAsync<StorageException>(() => provider.CreateFolderAsync("new-share", None))).Code);
        Assert.False(provider.CanMove("public/a.txt", "second/a.txt"));
    }

    private StorageSettings Settings(StorageKind kind, string? share = null, string root = "")
    {
        var host = _servers["host"];
        return kind switch
        {
            StorageKind.WebDav => new StorageSettings(kind, string.Empty, Username: _servers["user"], Password: _servers["password"], Url: $"http://{host}:{_servers["webdav"]}/"),
            _ => new StorageSettings(kind, root, host, int.Parse(_servers[kind.ToString().ToLowerInvariant()], System.Globalization.CultureInfo.InvariantCulture), share, _servers["user"], _servers["password"]),
        };
    }

    /// <summary>The same walk for every protocol: folders, writes (also two at once into a new folder), a range, move, copy, permissions, delete.</summary>
    private async Task ExerciseAsync(StorageSettings settings, bool permissions)
    {
        var provider = _connections.Create(settings);
        await provider.CheckAsync(None);
        var top = "remote-test-" + Guid.NewGuid().ToString("N")[..8];
        await provider.CreateFolderAsync(top, None);
        try
        {
            Assert.Equal(StorageError.Conflict, (await Assert.ThrowsAsync<StorageException>(() => provider.CreateFolderAsync(top, None))).Code);

            // two uploads into the same new folder at once: both arrive
            await Task.WhenAll(
                WriteAsync(provider, $"{top}/new/deep/a.txt", Text("0123456789"), 10, overwrite: false, None),
                WriteAsync(provider, $"{top}/new/deep/b.txt", Text("bbb"), 3, overwrite: false, None),
                WriteAsync(provider, $"{top}/new/c.txt", Text("c"), 1, overwrite: false, None));
            var names = (await provider.ListAsync($"{top}/new/deep", None)).Select(e => e.Name).Order(StringComparer.Ordinal).ToArray();
            Assert.Equal(new[] { "a.txt", "b.txt" }, names); // no temporary names left

            var a = await provider.GetAsync($"{top}/new/deep/a.txt", None);
            Assert.NotNull(a);
            Assert.Equal(10, a.Size);
            Assert.False(a.IsFolder);

            // the time of the server is UTC, not shifted by the zone of this machine
            Assert.InRange(a.Modified, DateTime.UtcNow.AddMinutes(-10), DateTime.UtcNow.AddMinutes(10));

            await using (var stream = await provider.OpenReadAsync($"{top}/new/deep/a.txt", 4, None))
            {
                Assert.Equal("456789", await new StreamReader(stream).ReadToEndAsync());
            }

            Assert.Null(await provider.GetAsync($"{top}/missing.txt", None));
            Assert.Equal(StorageError.Conflict, (await Assert.ThrowsAsync<StorageException>(() => provider.WriteAsync($"{top}/new/c.txt", Text("x"), 1, overwrite: false, None))).Code);
            await provider.WriteAsync($"{top}/new/c.txt", Text("replaced"), 8, overwrite: true, None);
            Assert.Equal(8, (await provider.GetAsync($"{top}/new/c.txt", None))!.Size);

            await provider.MoveAsync($"{top}/new/c.txt", $"{top}/moved/c.txt", overwrite: false, None);
            Assert.Null(await provider.GetAsync($"{top}/new/c.txt", None));
            Assert.NotNull(await provider.GetAsync($"{top}/moved/c.txt", None));


            if (permissions)
            {
                var before = await provider.PermissionsAsync($"{top}/new/deep/b.txt", None);
                Assert.NotNull(before);
                await provider.SetPermissionsAsync($"{top}/new/deep/b.txt", Convert.ToInt32("640", 8), None);
                Assert.Equal("640", StoragePermissions.ToText((await provider.PermissionsAsync($"{top}/new/deep/b.txt", None))!.Mode));
            }

            _ = await provider.SpaceAsync(None); // not every server tells; it must not fail

            await provider.DeleteAsync($"{top}/moved", None);
            Assert.Null(await provider.GetAsync($"{top}/moved", None));
            Assert.Equal(StorageError.InvalidPath, (await Assert.ThrowsAsync<StorageException>(() => provider.ListAsync("../", None))).Code);
        }
        finally
        {
            await provider.DeleteAsync(top, None);
        }

        Assert.Null(await provider.GetAsync(top, None));
    }

    private static MemoryStream Text(string text) => new(Encoding.UTF8.GetBytes(text));

    public void Dispose() => _connections.Dispose();

    /// <summary>A file into a folder that may not exist yet (the way the import writes).</summary>
    private static async Task WriteAsync(IStorageProvider provider, string path, Stream content, long length, bool overwrite, CancellationToken ct)
    {
        await StorageFolders.EnsureAsync(provider, path[..path.LastIndexOf('/')], ct);
        await provider.WriteAsync(path, content, length, overwrite, ct);
    }

    /// <summary>Like the clients of the app (StorageSetup): redirects by hand, so the authorization stays on the same host.</summary>
    private sealed class PlainHttp : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new SocketsHttpHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
    }
}
