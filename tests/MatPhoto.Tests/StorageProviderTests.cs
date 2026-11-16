using System.Text;
using MatPhoto.Localization;
using MatPhoto.Services.Storages;

namespace MatPhoto.Tests;

/// <summary>The local folder as a location (the same contract as the remote providers), the pool of connections, the rwx bits and the words of the errors.</summary>
public sealed class StorageProviderTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    [Fact]
    public async Task A_local_folder_lists_reads_writes_moves_copies_and_deletes()
    {
        using var bed = new TempFolder();
        var root = bed.Make("storage");
        var provider = new LocalProvider(root, null);
        await provider.CheckAsync(None);

        await provider.CreateFolderAsync("docs", None);
        await provider.WriteAsync("docs/a.txt", Text("hello world"), 11, overwrite: false, None);
        await provider.WriteAsync("docs/deep/er/b.txt", Text("b"), 1, overwrite: false, None); // the folders above are made
        await provider.WriteAsync(".hidden", Text("x"), 1, overwrite: false, None);

        var top = await provider.ListAsync(string.Empty, None);
        Assert.Contains(top, e => e is { Name: "docs", IsFolder: true });
        Assert.Contains(top, e => e is { Name: ".hidden", IsHidden: true });

        var a = await provider.GetAsync("docs/a.txt", None);
        Assert.NotNull(a);
        Assert.Equal(11, a.Size);
        Assert.Equal("docs/a.txt", a.Path);
        Assert.Null(await provider.GetAsync("docs/missing.txt", None));

        // a range: from the seventh byte
        await using (var stream = await provider.OpenReadAsync("docs/a.txt", 6, None))
        {
            Assert.Equal("world", await new StreamReader(stream).ReadToEndAsync());
        }

        // a name that is taken: refused unless replaced
        var taken = await Assert.ThrowsAsync<StorageException>(() => provider.WriteAsync("docs/a.txt", Text("again"), 5, overwrite: false, None));
        Assert.Equal(StorageError.Conflict, taken.Code);
        await provider.WriteAsync("docs/a.txt", Text("replaced"), 8, overwrite: true, None);
        Assert.Equal(8, (await provider.GetAsync("docs/a.txt", None))!.Size);

        await provider.MoveAsync("docs/a.txt", "docs/renamed.txt", overwrite: false, None);
        Assert.Null(await provider.GetAsync("docs/a.txt", None));
        Assert.True(await provider.TryCopyAsync("docs", "copy-of-docs", None));
        Assert.NotNull(await provider.GetAsync("copy-of-docs/deep/er/b.txt", None));

        await provider.DeleteAsync("docs", None);
        Assert.Null(await provider.GetAsync("docs", None));
        Assert.True(File.Exists(Path.Combine(root, "copy-of-docs", "renamed.txt")));

        // the top itself is never deleted or moved
        Assert.Equal(StorageError.InvalidPath, (await Assert.ThrowsAsync<StorageException>(() => provider.DeleteAsync("/", None))).Code);
        Assert.Equal(StorageError.InvalidPath, (await Assert.ThrowsAsync<StorageException>(() => provider.MoveAsync("", "x", false, None))).Code);
        Assert.Equal(StorageError.InvalidPath, (await Assert.ThrowsAsync<StorageException>(() => provider.ListAsync("../", None))).Code);
    }

    [Fact]
    public async Task A_missing_folder_of_a_location_is_unreachable()
    {
        using var bed = new TempFolder();
        var provider = new LocalProvider(Path.Combine(bed.Root, "gone"), null);
        var ex = await Assert.ThrowsAsync<StorageException>(() => provider.CheckAsync(None));
        Assert.Equal(StorageError.Unreachable, ex.Code);
    }

    [Fact]
    public async Task The_data_folder_of_MatPhoto_is_never_part_of_a_location()
    {
        using var bed = new TempFolder();
        var data = bed.Make("data");
        File.WriteAllText(Path.Combine(data, "secret.db"), "secret");
        bed.Make("photos");
        var provider = new LocalProvider(bed.Root, data);

        var names = (await provider.ListAsync(string.Empty, None)).Select(e => e.Name).ToList();
        Assert.Contains("photos", names);
        Assert.DoesNotContain("data", names);
        Assert.Equal(StorageError.InvalidPath, (await Assert.ThrowsAsync<StorageException>(() => provider.OpenReadAsync("data/secret.db", 0, None))).Code);
        Assert.Equal(StorageError.InvalidPath, (await Assert.ThrowsAsync<StorageException>(() => provider.DeleteAsync("data", None))).Code);
    }

    [Fact]
    public void The_rwx_bits()
    {
        Assert.Equal("755", StoragePermissions.ToText(493));
        Assert.Equal("rwxr-xr-x", StoragePermissions.ToSymbols(493));
        Assert.Equal("rw-r-----", StoragePermissions.ToSymbols(Convert.ToInt32("640", 8)));
        Assert.Equal(Convert.ToInt32("640", 8), StoragePermissions.FromFlags(true, true, false, true, false, false, false, false, false));
        Assert.True(StoragePermissions.IsValid(511));
        Assert.False(StoragePermissions.IsValid(512)); // no special bits
        Assert.False(StoragePermissions.IsValid(-1));
    }

    [Fact]
    public void Every_error_has_words_in_both_languages()
    {
        foreach (var code in Enum.GetValues<StorageError>())
        {
            foreach (var language in Languages.Codes)
            {
                var text = Languages.In(language, () => StorageErrors.Message(code, null));
                Assert.False(string.IsNullOrWhiteSpace(text));
                Assert.DoesNotContain("Storages.Error", text, StringComparison.Ordinal);
            }
        }

        // what a server said comes along in brackets; a worded error is shown as it is
        Assert.Contains("530 Login incorrect", Languages.In("en", () => StorageErrors.Message(StorageError.AccessDenied, "530 Login incorrect")));
        Assert.Equal("Quota", StorageErrors.Message(StorageError.Worded, "Quota"));
        Assert.Equal(StorageError.Timeout, StorageErrors.Classify(new TimeoutException()).Code);
        Assert.Equal(StorageError.AccessDenied, StorageErrors.Classify(new UnauthorizedAccessException()).Code);
    }

    [Fact]
    public async Task The_pool_lends_one_connection_at_a_time_and_closes_broken_ones()
    {
        var closed = new List<int>();
        var made = 0;
        using var pool = new ConnectionPool<Box>(b => b.Alive, b => closed.Add(b.Number), perKey: 2);
        Task<Box> Create() => Task.FromResult(new Box(++made));

        var first = await pool.RentAsync("k", Create);
        var second = await pool.RentAsync("k", Create); // the first is lent: a new one
        Assert.NotSame(first.Client, second.Client);
        first.Dispose();
        second.Dispose();
        Assert.Equal(2, pool.IdleCount);

        var again = await pool.RentAsync("k", Create);
        Assert.Equal(2, made); // taken from the pool
        again.Broken = true;
        again.Dispose();
        Assert.Single(closed);
        Assert.Equal(1, pool.IdleCount);

        // a connection that died while it waited is not lent again
        var idle = await pool.RentAsync("k", Create);
        idle.Client.Alive = false;
        idle.Dispose();
        Assert.Equal(0, pool.IdleCount);

        // other keys (another server, another account) never share
        using var other = await pool.RentAsync("other", Create);
        Assert.Equal(3, made);
        Assert.NotEqual(ConnectionPool<Box>.KeyOf("sftp", "nas", "22", "anna", "pw1"), ConnectionPool<Box>.KeyOf("sftp", "nas", "22", "anna", "pw2"));
        Assert.DoesNotContain("pw1", ConnectionPool<Box>.KeyOf("sftp", "nas", "22", "anna", "pw1"), StringComparison.Ordinal);
    }

    private static MemoryStream Text(string text) => new(Encoding.UTF8.GetBytes(text));

    public sealed class Box(int number)
    {
        public int Number { get; } = number;

        public bool Alive { get; set; } = true;
    }

    /// <summary>A stream that breaks after a few bytes (a connection that is cut during an upload).</summary>
    private sealed class BrokenStream : Stream
    {
        private int _reads;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => 0; set => throw new NotSupportedException(); }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (++_reads > 2)
            {
                throw new IOException("The connection was cut.");
            }

            buffer[offset] = 1;
            return 1;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
