using MatPhoto.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace MatPhoto.Data;

/// <summary>
/// The database (PostgreSQL). Tables are PascalCase and singular, the key is always <c>Id</c> (BIGINT), every record has CreateDate, CreateUserId, UpdateDate and UpdateUserId
/// (filled in <see cref="SaveChangesAsync(bool, CancellationToken)"/>), and a record with an <c>UpdateState</c> is deleted softly (0 = deleted, 1 = created, 2 = updated).
/// The photos are not in the database: they lie in their storages, the thumbnails in the cache (<c>Services/Library/ThumbnailCache</c>).
/// </summary>
public sealed class AppDbContext(DbContextOptions<AppDbContext> options, CurrentUser? current = null) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();

    public DbSet<UserSession> Sessions => Set<UserSession>();

    public DbSet<Storage> Storages => Set<Storage>();

    public DbSet<StorageAccessEntry> StorageAccess => Set<StorageAccessEntry>();

    public DbSet<Album> Albums => Set<Album>();

    public DbSet<Media> Media => Set<Media>();

    public DbSet<MediaFavorite> Favorites => Set<MediaFavorite>();

    public DbSet<Collection> Collections => Set<Collection>();

    public DbSet<CollectionItem> CollectionItems => Set<CollectionItem>();

    public DbSet<DuplicateDismissal> DuplicateDismissals => Set<DuplicateDismissal>();

    public DbSet<Share> Shares => Set<Share>();

    public DbSet<Person> People => Set<Person>();

    public DbSet<Face> Faces => Set<Face>();

    public DbSet<FaceRejection> FaceRejections => Set<FaceRejection>();

    public DbSet<ImportSession> ImportSessions => Set<ImportSession>();

    public DbSet<ImportGroup> ImportGroups => Set<ImportGroup>();

    public DbSet<ImportItem> ImportItems => Set<ImportItem>();

    public DbSet<Activity> Activities => Set<Activity>();

    public DbSet<SystemSetting> SystemSettings => Set<SystemSetting>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        // the time a photo was taken is the wall clock of the camera: no time zone
        const string LocalTime = "timestamp without time zone";

        b.Entity<User>(e =>
        {
            e.ToTable("User");
            e.HasIndex(x => x.Email).IsUnique();
            e.Property(x => x.Email).HasMaxLength(254);
            e.Property(x => x.DisplayName).HasMaxLength(120);
            e.Property(x => x.TimeZone).HasMaxLength(64);
            e.Property(x => x.Language).HasMaxLength(5);
            e.Property(x => x.Theme).HasMaxLength(10);
            e.Property(x => x.Scheme).HasMaxLength(10);
            e.Property(x => x.ExternalSubject).HasMaxLength(200);
            e.HasIndex(x => x.ExternalSubject).IsUnique().HasFilter("\"ExternalSubject\" IS NOT NULL");
            e.HasQueryFilter(x => x.UpdateState != SoftEntity.Deleted);
        });

        b.Entity<UserSession>(e =>
        {
            e.ToTable("UserSession");
            e.HasIndex(x => x.Token).IsUnique();
            e.HasIndex(x => x.UserId);
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.UserAgent).HasMaxLength(300);
            e.Property(x => x.IpAddress).HasMaxLength(64);
            e.Property(x => x.IdentitySessionId).HasMaxLength(200);
            e.HasIndex(x => x.IdentitySessionId);
        });

        b.Entity<Storage>(e =>
        {
            e.ToTable("Storage");
            e.Property(x => x.Name).HasMaxLength(120);
            e.Property(x => x.RootPath).HasMaxLength(500);
            e.Property(x => x.Host).HasMaxLength(255);
            e.Property(x => x.Share).HasMaxLength(255);
            e.Property(x => x.Username).HasMaxLength(255);
            e.Property(x => x.SecretProtected).HasMaxLength(2000);
            e.Property(x => x.Domain).HasMaxLength(255);
            e.Property(x => x.Url).HasMaxLength(500);
            e.Property(x => x.PrivateKeyProtected).HasMaxLength(20000);
            e.Property(x => x.ExcludeFolders).HasMaxLength(1000);
            e.Property(x => x.LastScanResult).HasMaxLength(1000);
            e.Property(x => x.Note).HasMaxLength(1000);
            e.HasQueryFilter(x => x.UpdateState != SoftEntity.Deleted);
        });

        b.Entity<StorageAccessEntry>(e =>
        {
            e.ToTable("StorageAccess");
            e.HasIndex(x => new { x.StorageId, x.UserId }).IsUnique();
            e.HasOne(x => x.Storage).WithMany(x => x.Access).HasForeignKey(x => x.StorageId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Album>(e =>
        {
            e.ToTable("Album");
            e.HasIndex(x => new { x.StorageId, x.Path });
            e.HasIndex(x => x.StartDate);
            e.HasOne(x => x.Storage).WithMany().HasForeignKey(x => x.StorageId).OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.Path).HasMaxLength(2000);
            e.Property(x => x.Name).HasMaxLength(255);
            e.Property(x => x.Title).HasMaxLength(255);
            e.Property(x => x.Description).HasMaxLength(2000);
            e.Property(x => x.StartDate).HasColumnType(LocalTime);
            e.Property(x => x.EndDate).HasColumnType(LocalTime);
            e.HasQueryFilter(x => x.UpdateState != SoftEntity.Deleted);
        });

        b.Entity<Media>(e =>
        {
            e.ToTable("Media");
            e.HasIndex(x => new { x.StorageId, x.Path });
            e.HasIndex(x => new { x.AlbumId, x.SortDate });
            e.HasIndex(x => x.SortDate);
            e.HasIndex(x => x.ContentHash);
            e.HasIndex(x => x.PerceptualHash);
            e.HasIndex(x => new { x.CountryCode, x.City });
            e.HasIndex(x => x.ThumbState);
            e.HasIndex(x => x.FaceState);
            e.HasOne(x => x.Storage).WithMany().HasForeignKey(x => x.StorageId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Album).WithMany().HasForeignKey(x => x.AlbumId).OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.Path).HasMaxLength(2000);
            e.Property(x => x.FileName).HasMaxLength(255);
            e.Property(x => x.MimeType).HasMaxLength(100);
            e.Property(x => x.ContentHash).HasMaxLength(64);
            e.Property(x => x.TakenDate).HasColumnType(LocalTime);
            e.Property(x => x.SortDate).HasColumnType(LocalTime);
            e.Property(x => x.CameraMake).HasMaxLength(100);
            e.Property(x => x.CameraModel).HasMaxLength(100);
            e.Property(x => x.LensModel).HasMaxLength(150);
            e.Property(x => x.City).HasMaxLength(200);
            e.Property(x => x.Region).HasMaxLength(200);
            e.Property(x => x.Country).HasMaxLength(100);
            e.Property(x => x.CountryCode).HasMaxLength(2);
            e.Property(x => x.VideoCodec).HasMaxLength(40);
            e.Property(x => x.ThumbError).HasMaxLength(500);
            e.HasQueryFilter(x => x.UpdateState != SoftEntity.Deleted);
        });

        b.Entity<MediaFavorite>(e =>
        {
            e.ToTable("MediaFavorite");
            e.HasIndex(x => new { x.UserId, x.MediaId }).IsUnique();
            e.HasIndex(x => x.MediaId);
            e.HasOne(x => x.Media).WithMany().HasForeignKey(x => x.MediaId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Collection>(e =>
        {
            e.ToTable("Collection");
            e.HasIndex(x => x.OwnerId);
            e.HasOne(x => x.Owner).WithMany().HasForeignKey(x => x.OwnerId).OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.Name).HasMaxLength(120);
            e.Property(x => x.Description).HasMaxLength(2000);
            e.Property(x => x.Colour).HasMaxLength(7);
            e.HasQueryFilter(x => x.UpdateState != SoftEntity.Deleted);
        });

        b.Entity<CollectionItem>(e =>
        {
            e.ToTable("CollectionItem");
            e.HasIndex(x => new { x.CollectionId, x.MediaId }).IsUnique();
            e.HasIndex(x => x.MediaId);
            e.HasOne(x => x.Collection).WithMany(x => x.Items).HasForeignKey(x => x.CollectionId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Media).WithMany().HasForeignKey(x => x.MediaId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Share>(e =>
        {
            e.ToTable("Share");
            e.HasIndex(x => x.Token).IsUnique();
            e.HasIndex(x => x.OwnerId);
            e.HasIndex(x => x.AlbumId);
            e.HasIndex(x => x.CollectionId);
            e.HasOne(x => x.Owner).WithMany().HasForeignKey(x => x.OwnerId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Album).WithMany().HasForeignKey(x => x.AlbumId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Collection).WithMany().HasForeignKey(x => x.CollectionId).OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.Token).HasMaxLength(64);
            e.Property(x => x.Title).HasMaxLength(200);
            e.Property(x => x.Message).HasMaxLength(2000);
            e.HasQueryFilter(x => x.UpdateState != SoftEntity.Deleted);
        });

        b.Entity<DuplicateDismissal>(e =>
        {
            e.ToTable("DuplicateDismissal");
            e.HasIndex(x => new { x.FirstMediaId, x.SecondMediaId }).IsUnique();
        });

        b.Entity<Person>(e =>
        {
            e.ToTable("Person");
            e.Property(x => x.Name).HasMaxLength(200);
            e.HasIndex(x => x.Name);
            e.HasQueryFilter(x => x.UpdateState != SoftEntity.Deleted);
        });

        b.Entity<Face>(e =>
        {
            e.ToTable("Face");
            e.HasIndex(x => x.MediaId);
            e.HasIndex(x => x.PersonId);
            e.HasIndex(x => x.Clustered).HasFilter("\"Clustered\" = false");
            e.HasOne(x => x.Media).WithMany().HasForeignKey(x => x.MediaId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Person).WithMany().HasForeignKey(x => x.PersonId).OnDelete(DeleteBehavior.SetNull);
        });

        b.Entity<FaceRejection>(e =>
        {
            e.ToTable("FaceRejection");
            e.HasIndex(x => new { x.FaceId, x.PersonId }).IsUnique();
            e.HasIndex(x => x.PersonId);
        });

        b.Entity<ImportSession>(e =>
        {
            e.ToTable("ImportSession");
            e.HasIndex(x => x.Token).IsUnique();
            e.HasIndex(x => x.UserId);
            e.Property(x => x.SourcePath).HasMaxLength(2000);
            e.Property(x => x.TargetFolder).HasMaxLength(2000);
            e.Property(x => x.Message).HasMaxLength(1000);
        });

        b.Entity<ImportGroup>(e =>
        {
            e.ToTable("ImportGroup");
            e.HasIndex(x => x.ImportSessionId);
            e.HasOne(x => x.ImportSession).WithMany(x => x.Groups).HasForeignKey(x => x.ImportSessionId).OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.Name).HasMaxLength(255);
            e.Property(x => x.Place).HasMaxLength(200);
            e.Property(x => x.Reason).HasMaxLength(40);
            e.Property(x => x.StartDate).HasColumnType(LocalTime);
            e.Property(x => x.EndDate).HasColumnType(LocalTime);
        });

        b.Entity<ImportItem>(e =>
        {
            e.ToTable("ImportItem");
            e.HasIndex(x => x.ImportSessionId);
            e.HasIndex(x => x.ImportGroupId);
            e.HasOne(x => x.ImportSession).WithMany(x => x.Items).HasForeignKey(x => x.ImportSessionId).OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.SourcePath).HasMaxLength(2000);
            e.Property(x => x.FileName).HasMaxLength(255);
            e.Property(x => x.SourceFolder).HasMaxLength(2000);
            e.Property(x => x.ContentHash).HasMaxLength(64);
            e.Property(x => x.TakenDate).HasColumnType(LocalTime);
            e.Property(x => x.City).HasMaxLength(200);
            e.Property(x => x.Country).HasMaxLength(100);
            e.Property(x => x.CameraModel).HasMaxLength(100);
            e.Property(x => x.Error).HasMaxLength(500);
            e.Property(x => x.TargetPath).HasMaxLength(2000);
        });

        b.Entity<Activity>(e =>
        {
            e.ToTable("Activity");
            e.HasIndex(x => x.OccurredDate);
            e.Property(x => x.Action).HasMaxLength(30);
            e.Property(x => x.Target).HasMaxLength(255);
            e.Property(x => x.Detail).HasMaxLength(1000);
        });

        b.Entity<SystemSetting>(e =>
        {
            e.ToTable("SystemSetting");
            e.Property(x => x.PublicUrl).HasMaxLength(300);
            e.Property(x => x.CacheFolder).HasMaxLength(500);
            e.Property(x => x.AlbumDateFormat).HasMaxLength(40);
            e.Property(x => x.ImportFolder).HasMaxLength(500);
        });
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        Stamp();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        Stamp();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    /// <summary>Fills the audit columns, and turns the delete of a soft record into UpdateState 0.</summary>
    private void Stamp()
    {
        var now = DateTime.UtcNow;
        var user = current?.Id;
        foreach (var entry in ChangeTracker.Entries<Entity>().ToList())
        {
            var entity = entry.Entity;
            if (entry.State == EntityState.Deleted && entity is SoftEntity soft)
            {
                entry.State = EntityState.Modified;
                soft.UpdateState = SoftEntity.Deleted;
            }

            switch (entry.State)
            {
                case EntityState.Added:
                    if (entity.CreateDate == default)
                    {
                        entity.CreateDate = now;
                    }

                    entity.CreateUserId ??= user;
                    entity.UpdateDate = now;
                    entity.UpdateUserId = user ?? entity.CreateUserId;
                    break;
                case EntityState.Modified:
                    entity.UpdateDate = now;
                    entity.UpdateUserId = user ?? entity.UpdateUserId;
                    if (entity is SoftEntity { UpdateState: not SoftEntity.Deleted } updated)
                    {
                        updated.UpdateState = SoftEntity.Updated;
                    }

                    break;
            }
        }
    }
}

/// <summary>For <c>dotnet ef migrations add</c>: no database is needed to create a migration.</summary>
public sealed class DesignTimeFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
        => new(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql("Host=localhost;Database=matphoto;Username=matphoto;Password=matphoto").Options);
}
