namespace MatPhoto.Data;

/// <summary>What every record has: an Id (BIGINT) and who created and changed it, and when (the columns are filled by <see cref="AppDbContext"/>).</summary>
public abstract class Entity
{
    public long Id { get; set; }

    public DateTime CreateDate { get; set; }

    public long? CreateUserId { get; set; }

    public DateTime UpdateDate { get; set; }

    public long? UpdateUserId { get; set; }
}

/// <summary>A record that supports soft delete: 0 = deleted, 1 = created, 2 = updated. A deleted record is hidden by a query filter.</summary>
public abstract class SoftEntity : Entity
{
    public const short Deleted = 0;
    public const short Created = 1;
    public const short Updated = 2;

    public short UpdateState { get; set; } = Created;
}

public enum UserRole
{
    Admin = 1,
    User = 2,
}

/// <summary>What a person may do with the photos of a storage.</summary>
public enum AccessRole
{
    None = 0,

    /// <summary>May look at the photos and videos, download them, mark favourites and put them on lists.</summary>
    Viewer = 1,

    /// <summary>May also import into the storage, create, rename and remove albums and move photos into the trash folder.</summary>
    Editor = 2,
}

public sealed class User : SoftEntity
{
    /// <summary>The address to sign in with; unique, stored in lower case.</summary>
    public string Email { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public string PasswordHash { get; set; } = string.Empty;

    public UserRole Role { get; set; } = UserRole.User;

    public bool IsActive { get; set; } = true;

    /// <summary>The IANA time zone dates are shown in.</summary>
    public string TimeZone { get; set; } = "Europe/Berlin";

    /// <summary>The language of the interface: en or de; empty = the language of the browser.</summary>
    public string Language { get; set; } = string.Empty;

    public int FailedLogins { get; set; }

    public DateTime? LockoutUntil { get; set; }

    public DateTime? LastLoginDate { get; set; }

    /// <summary>system, light or dark.</summary>
    public string Theme { get; set; } = "system";

    /// <summary>standard, paper, contrast or oled.</summary>
    public string Scheme { get; set; } = "standard";

    /// <summary>The person of MatIdentity (the claim <c>sub</c>) this account is linked with; null = a local account only.</summary>
    public string? ExternalSubject { get; set; }
}

/// <summary>A signed-in browser. The cookie carries the <see cref="Token"/>; the session lives in the database.</summary>
public sealed class UserSession : Entity
{
    public Guid Token { get; set; } = Guid.NewGuid();

    public long UserId { get; set; }

    public User? User { get; set; }

    public DateTime LastSeenDate { get; set; }

    public DateTime ExpiresDate { get; set; }

    public bool IsRevoked { get; set; }

    public string? UserAgent { get; set; }

    public string? IpAddress { get; set; }

    /// <summary>The ID token of a sign-in through MatIdentity (for the sign-out there); null for a local sign-in. A secret: never exported.</summary>
    public string? IdentityToken { get; set; }

    /// <summary>The session of MatIdentity (the claim <c>sid</c>) this session started with: the back-channel logout ends it by this.</summary>
    public string? IdentitySessionId { get; set; }
}

public enum StorageKind
{
    /// <summary>A folder of the server (a bind mount of the NAS below /storage).</summary>
    Local = 1,
    Smb = 2,
    Sftp = 3,
    Ftp = 4,
    WebDav = 5,
}

/// <summary>
/// A storage: a folder of the server or a share of another machine (a NAS over SMB, WebDAV, SFTP, FTP). A storage that is a <see cref="IsLibrary">library</see> is
/// scanned: every folder with photos or videos in it is an <see cref="Album"/> (the physical place of the files, e.g. <c>2023.11.09 Hochzeit Julius</c>). A storage
/// can also hold the cache of the thumbnails (<see cref="SystemSetting.CacheStorageId"/>) or be the source of an import.
/// </summary>
public sealed class Storage : SoftEntity
{
    public string Name { get; set; } = string.Empty;

    public StorageKind Kind { get; set; }

    /// <summary>The folder (local: inside the container, e.g. /storage/photos; remote: the path on the server).</summary>
    public string RootPath { get; set; } = "/";

    public string? Host { get; set; }

    public int? Port { get; set; }

    /// <summary>The share of an SMB server.</summary>
    public string? Share { get; set; }

    public string? Username { get; set; }

    /// <summary>The password, encrypted with the keys of the data volume (DataProtection). Never sent to a browser. With a private key (SFTP) it is the passphrase of the key.</summary>
    public string? SecretProtected { get; set; }

    /// <summary>The domain of an SMB account (empty: the account of the server itself).</summary>
    public string? Domain { get; set; }

    /// <summary>The address of a WebDAV share (https://cloud.example.com/remote.php/dav/files/anna).</summary>
    public string? Url { get; set; }

    /// <summary>FTP: an encrypted connection (FTPS, explicit TLS).</summary>
    public bool UseTls { get; set; }

    /// <summary>FTPS and WebDAV over https: accept a certificate that is not trusted (a NAS with a certificate of its own).</summary>
    public bool AcceptAnyCertificate { get; set; }

    /// <summary>SFTP: the private key (OpenSSH or PEM), encrypted like the password. Never sent to a browser.</summary>
    public string? PrivateKeyProtected { get; set; }

    /// <summary>MatPhoto never writes here: no import, no new album, no trash.</summary>
    public bool IsReadOnly { get; set; }

    public bool IsActive { get; set; } = true;

    /// <summary>The photos of this storage are scanned and shown (the albums are its folders). False: only a target of the cache or a source of an import.</summary>
    public bool IsLibrary { get; set; } = true;

    /// <summary>How often the storage is scanned for new, changed and removed files (minutes; 0 = only when somebody starts it).</summary>
    public int ScanIntervalMinutes { get; set; } = 60;

    /// <summary>Folder names that are never scanned besides the built-in ones (<c>@eaDir</c>, <c>#recycle</c> …), separated by semicolons.</summary>
    public string? ExcludeFolders { get; set; }

    public DateTime? LastScanDate { get; set; }

    /// <summary>What the last scan found, in words (counts or the error).</summary>
    public string? LastScanResult { get; set; }

    /// <summary>Every person may see it (with <see cref="EveryoneRole"/>); otherwise only the people in <see cref="Access"/>.</summary>
    public bool ForEveryone { get; set; } = true;

    public AccessRole EveryoneRole { get; set; } = AccessRole.Viewer;

    public string? Note { get; set; }

    public ICollection<StorageAccessEntry> Access { get; set; } = [];
}

public sealed class StorageAccessEntry : Entity
{
    public long StorageId { get; set; }

    public Storage? Storage { get; set; }

    public long UserId { get; set; }

    public User? User { get; set; }

    public AccessRole Role { get; set; } = AccessRole.Viewer;
}

/// <summary>
/// An album: a folder of a library storage with photos or videos in it. The album IS the folder: renaming it renames the folder, the photos lie in it. The name
/// follows the pattern of the installation (<c>yyyy.MM.dd Title</c>); <see cref="AlbumDate"/> and <see cref="Title"/> are read from it.
/// </summary>
public sealed class Album : SoftEntity
{
    public long StorageId { get; set; }

    public Storage? Storage { get; set; }

    /// <summary>The folder relative to the storage, with slashes and without a leading one (<c>2023/2023.11.09 Hochzeit Julius</c>; the top of the storage is empty).</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>The name of the folder.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>The name without the date at its start (<c>Hochzeit Julius</c>).</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>The date at the start of the name, if it has one.</summary>
    public DateOnly? AlbumDate { get; set; }

    /// <summary>The first and the last photo (when they were taken).</summary>
    public DateTime? StartDate { get; set; }

    public DateTime? EndDate { get; set; }

    public int MediaCount { get; set; }

    public long Bytes { get; set; }

    /// <summary>The photo shown on the card; null = the first one.</summary>
    public long? CoverMediaId { get; set; }

    public string? Description { get; set; }
}

public enum MediaType
{
    Photo = 1,
    Video = 2,
}

/// <summary>What the scan recognised about a photo or video (the automatic albums of Explore).</summary>
[Flags]
public enum MediaTraits
{
    None = 0,
    Screenshot = 1,
    Selfie = 2,
    Panorama = 4,
    Raw = 8,
    Animated = 16,

    /// <summary>A photo with a short video of the same name next to it (Live Photo).</summary>
    LivePhoto = 32,

    /// <summary>The video part of a Live Photo: it is played with the photo and not shown on its own.</summary>
    LiveVideo = 64,
}

public enum ThumbState
{
    Pending = 0,
    Ready = 1,
    Failed = 2,
}

/// <summary>Where the date of a photo came from.</summary>
public enum DateSource
{
    None = 0,
    Exif = 1,
    FileName = 2,
    FileDate = 3,
    Folder = 4,
}

/// <summary>
/// A photo or a video in a library. The file stays where it is (<see cref="Path"/> in its storage); the record holds what was read from it (the date it was taken,
/// the camera, the place) and the keys of its thumbnails in the cache (<see cref="ContentHash"/>).
/// </summary>
public sealed class Media : SoftEntity
{
    public long StorageId { get; set; }

    public Storage? Storage { get; set; }

    public long AlbumId { get; set; }

    public Album? Album { get; set; }

    /// <summary>The file relative to the storage (<c>2023/2023.11.09 Hochzeit Julius/IMG_0001.HEIC</c>).</summary>
    public string Path { get; set; } = string.Empty;

    public string FileName { get; set; } = string.Empty;

    public MediaType Type { get; set; }

    public MediaTraits Traits { get; set; }

    public string MimeType { get; set; } = string.Empty;

    public long Size { get; set; }

    /// <summary>When the file was changed last (UTC): a scan reads a file again only when this or the size changed.</summary>
    public DateTime FileModified { get; set; }

    /// <summary>SHA-256 of the bytes (hex; a large video: of its size, start and end). The key of the thumbnails and of the exact duplicates.</summary>
    public string ContentHash { get; set; } = string.Empty;

    /// <summary>A 64-bit picture hash (dHash) of the thumbnail: similar photos differ in few bits.</summary>
    public long? PerceptualHash { get; set; }

    /// <summary>The width and height as shown (the orientation of the camera applied).</summary>
    public int Width { get; set; }

    public int Height { get; set; }

    /// <summary>When it was taken, the local time of the camera (no time zone).</summary>
    public DateTime? TakenDate { get; set; }

    public DateSource DateSource { get; set; }

    /// <summary>What the timeline sorts by: <see cref="TakenDate"/>, else the date of the file.</summary>
    public DateTime SortDate { get; set; }

    public string? CameraMake { get; set; }

    public string? CameraModel { get; set; }

    public string? LensModel { get; set; }

    public double? FNumber { get; set; }

    /// <summary>The exposure time in seconds.</summary>
    public double? ExposureTime { get; set; }

    public int? Iso { get; set; }

    public double? FocalLength { get; set; }

    public double? Latitude { get; set; }

    public double? Longitude { get; set; }

    public double? Altitude { get; set; }

    /// <summary>The place nearby (offline from GeoNames: a town of 15,000 people or more).</summary>
    public string? City { get; set; }

    public string? Region { get; set; }

    public string? Country { get; set; }

    public string? CountryCode { get; set; }

    /// <summary>The length of a video in milliseconds.</summary>
    public int? DurationMs { get; set; }

    public string? VideoCodec { get; set; }

    /// <summary>The video of a Live Photo (a <see cref="MediaTraits.LiveVideo"/> beside it).</summary>
    public long? LiveVideoId { get; set; }

    public ThumbState ThumbState { get; set; }

    public string? ThumbError { get; set; }

    /// <summary>Whether the faces of the photo were looked for (people); a changed file starts again.</summary>
    public FaceState FaceState { get; set; }
}

public enum FaceState : short
{
    /// <summary>Not looked for yet (or the file changed).</summary>
    Pending = 0,

    Done = 1,

    /// <summary>The preview could not be read; looked for again when its thumbnails are made again.</summary>
    Failed = 2,
}

/// <summary>
/// A person whose face is on photos: made by the recognition when several faces look alike (then without a name: "who is this?"), named by a person of the
/// installation. The faces are linked to it (<see cref="Face.PersonId"/>); every person of the installation sees a person only on the photos they may see.
/// </summary>
public sealed class Person : SoftEntity
{
    /// <summary>The name; null = not named yet.</summary>
    public string? Name { get; set; }

    /// <summary>Not shown among the people (someone in the background, a poster).</summary>
    public bool IsHidden { get; set; }

    /// <summary>The face that shows the person (the clearest one, or the one chosen).</summary>
    public long? CoverFaceId { get; set; }
}

/// <summary>
/// A face found on a photo: where it is (relative to the picture, 0 to 1, so it fits every size of preview), how sure the detector was, and its features (128 numbers
/// of SFace, of length 1: two faces of the same person have a large dot product).
/// </summary>
public sealed class Face : Entity
{
    public long MediaId { get; set; }

    public Media? Media { get; set; }

    public long? PersonId { get; set; }

    public Person? Person { get; set; }

    public float X { get; set; }

    public float Y { get; set; }

    public float Width { get; set; }

    public float Height { get; set; }

    public float Score { get; set; }

    public float[] Embedding { get; set; } = [];

    /// <summary>The grouping looked at it (a new face is looked at once; it may still be without a person).</summary>
    public bool Clustered { get; set; }

    /// <summary>A person said it belongs to nobody worth naming (the person was "forgotten"): never grouped again.</summary>
    public bool IsIgnored { get; set; }

    /// <summary>A person put it to its person by hand: the grouping never changes it.</summary>
    public bool IsConfirmed { get; set; }
}

/// <summary>A person said this face is not this person: the grouping never puts it there again.</summary>
public sealed class FaceRejection : Entity
{
    public long FaceId { get; set; }

    public long PersonId { get; set; }
}

/// <summary>A photo a person marked as a favourite.</summary>
public sealed class MediaFavorite : Entity
{
    public long UserId { get; set; }

    public long MediaId { get; set; }

    public Media? Media { get; set; }
}

/// <summary>
/// A list (a "linked" album): photos from any album put together for a project, e.g. "Girlande Max Geburtstag". It holds links only; the files stay in their albums.
/// </summary>
public sealed class Collection : SoftEntity
{
    public long OwnerId { get; set; }

    public User? Owner { get; set; }

    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    /// <summary>A colour the person chose (#rrggbb), shown as a mark next to the name.</summary>
    public string? Colour { get; set; }

    /// <summary>Every person of the installation sees the list (each only the photos of the storages they may see) and may add to it.</summary>
    public bool IsShared { get; set; }

    public long? CoverMediaId { get; set; }

    public ICollection<CollectionItem> Items { get; set; } = [];
}

public sealed class CollectionItem : Entity
{
    public long CollectionId { get; set; }

    public Collection? Collection { get; set; }

    public long MediaId { get; set; }

    public Media? Media { get; set; }

    /// <summary>The order on the list (the order they were added in).</summary>
    public int Position { get; set; }
}

/// <summary>Two photos a person said are not duplicates of each other (they are not offered again).</summary>
public sealed class DuplicateDismissal : Entity
{
    /// <summary>The smaller of the two ids.</summary>
    public long FirstMediaId { get; set; }

    public long SecondMediaId { get; set; }
}

public enum ImportSource
{
    /// <summary>Files sent from the browser (into a folder of the data volume until they are imported).</summary>
    Upload = 1,

    /// <summary>A folder of a storage (an inbox the phone syncs to, a card reader).</summary>
    Storage = 2,
}

public enum ImportState
{
    Collecting = 1,
    Analyzing = 2,
    Review = 3,
    Importing = 4,
    Done = 5,
    Failed = 6,
    Cancelled = 7,
}

/// <summary>
/// An import: files are collected (uploaded or a folder of a storage), analysed (date, place, type, duplicates) and grouped into suggested albums; the person checks the
/// names and MatPhoto copies (or moves) the files into the folders of the target storage.
/// </summary>
public sealed class ImportSession : Entity
{
    /// <summary>The key of the address (<c>/Import/Review/{token}</c>) and of the folder of the uploads.</summary>
    public Guid Token { get; set; } = Guid.NewGuid();

    public long UserId { get; set; }

    public ImportSource Source { get; set; }

    public long? SourceStorageId { get; set; }

    /// <summary>The folder of the source storage (relative).</summary>
    public string? SourcePath { get; set; }

    /// <summary>Move the files out of the source folder (a storage source only); false = copy.</summary>
    public bool MoveFiles { get; set; }

    public ImportState State { get; set; } = ImportState.Collecting;

    public long? TargetStorageId { get; set; }

    /// <summary>The folder of the target storage the new albums are made in (relative; empty = the top).</summary>
    public string TargetFolder { get; set; } = string.Empty;

    public int TotalCount { get; set; }

    public int DoneCount { get; set; }

    public int ImportedCount { get; set; }

    public int SkippedCount { get; set; }

    public int FailedCount { get; set; }

    public string? Message { get; set; }

    public DateTime? FinishedDate { get; set; }

    public ICollection<ImportGroup> Groups { get; set; } = [];

    public ICollection<ImportItem> Items { get; set; } = [];
}

/// <summary>A suggested album of an import: photos taken close together in time and place.</summary>
public sealed class ImportGroup : Entity
{
    public long ImportSessionId { get; set; }

    public ImportSession? ImportSession { get; set; }

    public int Position { get; set; }

    /// <summary>The name of the album folder (suggested, then as the person changed it).</summary>
    public string Name { get; set; } = string.Empty;

    public DateTime? StartDate { get; set; }

    public DateTime? EndDate { get; set; }

    /// <summary>The place most photos were taken at.</summary>
    public string? Place { get; set; }

    /// <summary>Why the photos are together (time, place, screenshots, no date): a key of the texts.</summary>
    public string Reason { get; set; } = string.Empty;

    /// <summary>Import this group (false: its files stay out).</summary>
    public bool Include { get; set; } = true;

    /// <summary>Put the photos into this album that exists already instead of a new folder.</summary>
    public long? ExistingAlbumId { get; set; }

    public int ItemCount { get; set; }
}

public enum ImportItemState
{
    Pending = 0,
    Ready = 1,
    Imported = 2,
    Skipped = 3,
    Failed = 4,
}

public sealed class ImportItem : Entity
{
    public long ImportSessionId { get; set; }

    public ImportSession? ImportSession { get; set; }

    public long? ImportGroupId { get; set; }

    /// <summary>The file relative to the source (the upload folder or the folder of the source storage).</summary>
    public string SourcePath { get; set; } = string.Empty;

    public string FileName { get; set; } = string.Empty;

    /// <summary>The folder the file came from (an upload of a folder, a subfolder of the source): its name helps to name the album.</summary>
    public string? SourceFolder { get; set; }

    public long Size { get; set; }

    public DateTime FileModified { get; set; }

    public MediaType Type { get; set; }

    public MediaTraits Traits { get; set; }

    public string ContentHash { get; set; } = string.Empty;

    public int Width { get; set; }

    public int Height { get; set; }

    public DateTime? TakenDate { get; set; }

    public DateSource DateSource { get; set; }

    public double? Latitude { get; set; }

    public double? Longitude { get; set; }

    public string? City { get; set; }

    public string? Country { get; set; }

    public string? CameraModel { get; set; }

    /// <summary>The same file is in the library already.</summary>
    public long? DuplicateOfMediaId { get; set; }

    /// <summary>The same file came twice in this import (this one is skipped).</summary>
    public long? DuplicateOfItemId { get; set; }

    public ImportItemState State { get; set; }

    public string? Error { get; set; }

    /// <summary>Where it went (relative to the target storage).</summary>
    public string? TargetPath { get; set; }

    public bool HasThumb { get; set; }
}

/// <summary>What happened: a scan, an import, an album renamed, a photo moved into the trash … (the log of the administration).</summary>
public sealed class Activity : Entity
{
    public long? UserId { get; set; }

    public long? StorageId { get; set; }

    public long? AlbumId { get; set; }

    public long? MediaId { get; set; }

    /// <summary>scan, import, album, trash, collection, storage, setting …</summary>
    public string Action { get; set; } = string.Empty;

    /// <summary>The name of the item at the time (it may be gone later).</summary>
    public string? Target { get; set; }

    public string? Detail { get; set; }

    public DateTime OccurredDate { get; set; }
}

/// <summary>The settings of the whole installation (one row, Id 1).</summary>
public sealed class SystemSetting : Entity
{
    /// <summary>Whether anyone may create an account (the first account of an installation can always sign up and becomes the administrator).</summary>
    public bool RegistrationOpen { get; set; }

    /// <summary>The address the app is reached at from outside (links in mails); empty = the address of the request.</summary>
    public string? PublicUrl { get; set; }

    /// <summary>The storage that holds the cache of the thumbnails; null = the data volume of MatPhoto (<c>/data/cache</c>).</summary>
    public long? CacheStorageId { get; set; }

    /// <summary>The folder of the cache in that storage (relative).</summary>
    public string CacheFolder { get; set; } = ".matphoto-cache";

    /// <summary>The height of a thumbnail of the grid in pixels (twice the largest tile, for sharp screens).</summary>
    public int ThumbnailHeight { get; set; } = 480;

    /// <summary>The longer side of the preview in the viewer in pixels.</summary>
    public int PreviewSize { get; set; } = 2048;

    /// <summary>JPEG quality of thumbnails and previews (50 to 95).</summary>
    public int ThumbnailQuality { get; set; } = 82;

    /// <summary>The date at the start of the name of a new album (.NET format): <c>yyyy.MM.dd</c> makes <c>2023.11.09 Hochzeit Julius</c>.</summary>
    public string AlbumDateFormat { get; set; } = "yyyy.MM.dd";

    /// <summary>An import starts a new album when this many hours lie between two photos.</summary>
    public int ImportGapHours { get; set; } = 8;

    /// <summary>An import starts a new album when two photos in a row were taken this far apart (km).</summary>
    public int ImportDistanceKm { get; set; } = 50;

    /// <summary>The storage an import goes to unless the person chooses another.</summary>
    public long? ImportStorageId { get; set; }

    /// <summary>The folder of that storage the new albums are made in.</summary>
    public string ImportFolder { get; set; } = string.Empty;

    /// <summary>Similar photos: the most bits two picture hashes may differ in (0 to 12).</summary>
    public int SimilarThreshold { get; set; } = 6;

    /// <summary>The largest file that may be uploaded (bytes; 0 = no limit).</summary>
    public long MaxUploadBytes { get; set; }

    /// <summary>Faces are looked for and grouped into people (on this server; off until an administrator switches it on: faces are biometric data).</summary>
    public bool FacesEnabled { get; set; }
}
