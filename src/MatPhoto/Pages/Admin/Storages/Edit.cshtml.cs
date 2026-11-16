using MatPhoto.Controls;
using MatPhoto.Data;
using MatPhoto.Localization;
using MatPhoto.Services.Storages;
using Microsoft.AspNetCore.Mvc;

namespace MatPhoto.Pages.Admin.Storages;

/// <summary>
/// The tab "General" of a storage: name, type and the fields of the type (only those that make sense for it), what MatPhoto does with it (a library that is scanned,
/// how often, which folders not), read-only, active, a note; "Test connection" tries
/// the values of the form without saving them. Passwords and keys are never sent back to the browser: an empty field keeps what is stored.
/// </summary>
public sealed class EditModel(AppDbContext db, StorageConnections connections) : AppPageModel
{
    [BindProperty(SupportsGet = true)] public long? Id { get; set; }

    [BindProperty] public string Name { get; set; } = string.Empty;

    [BindProperty] public StorageKind Kind { get; set; } = StorageKind.Local;

    [BindProperty] public string RootPath { get; set; } = string.Empty;

    [BindProperty] public string Host { get; set; } = string.Empty;

    [BindProperty] public int? Port { get; set; }

    [BindProperty] public string Share { get; set; } = string.Empty;

    [BindProperty] public string Username { get; set; } = string.Empty;

    [BindProperty] public string Password { get; set; } = string.Empty;

    [BindProperty] public bool ClearPassword { get; set; }

    [BindProperty] public string Domain { get; set; } = string.Empty;

    [BindProperty(Name = "Url")] public string WebDavUrl { get; set; } = string.Empty;

    [BindProperty] public bool UseTls { get; set; }

    [BindProperty] public bool AcceptAnyCertificate { get; set; }

    [BindProperty] public bool UseKey { get; set; }

    [BindProperty] public string PrivateKey { get; set; } = string.Empty;

    [BindProperty] public bool IsReadOnly { get; set; }

    [BindProperty] public bool IsActive { get; set; } = true;

    [BindProperty] public bool IsLibrary { get; set; } = true;

    [BindProperty] public int ScanIntervalMinutes { get; set; } = 60;

    [BindProperty] public string ExcludeFolders { get; set; } = string.Empty;

    [BindProperty] public string Note { get; set; } = string.Empty;

    public bool IsNew => Id is null;

    public string Title { get; private set; } = string.Empty;

    public bool HasPassword { get; private set; }

    public bool HasPrivateKey { get; private set; }

    /// <summary>The last check of the saved storage.</summary>
    public StorageStatus? Status { get; private set; }

    /// <summary>The outcome of "Test connection" (with the values of the form).</summary>
    public StorageStatus? Tested { get; private set; }

    public string? TestError { get; private set; }

    public DateTime? LastScanDate { get; private set; }

    public string? LastScanResult { get; private set; }

    public string Zone => Me.TimeZone;

    public string StorageRoot => connections.StorageRoot;

    public List<Choice> Kinds { get; } = Enum.GetValues<StorageKind>().Select(k => new Choice(k.ToString(), StorageAdmin.KindName(k))).ToList();

    public List<Choice> AuthChoices { get; } = [new("false", Text.T("Storages.Auth.Password")), new("true", Text.T("Storages.Auth.Key"))];

    public async Task<IActionResult> OnGetAsync()
    {
        if (Id is { } id)
        {
            if (await StorageAdmin.GetAsync(db, connections, UserId, id, HttpContext.RequestAborted) is not { } l)
            {
                return NotFound();
            }

            Name = l.Name;
            Kind = l.Kind;
            RootPath = l.RootPath;
            Host = l.Host ?? string.Empty;
            Port = l.Port;
            Share = l.Share ?? string.Empty;
            Username = l.Username ?? string.Empty;
            Domain = l.Domain ?? string.Empty;
            WebDavUrl = l.Url ?? string.Empty;
            UseTls = l.UseTls;
            AcceptAnyCertificate = l.AcceptAnyCertificate;
            UseKey = l.HasPrivateKey;
            IsReadOnly = l.IsReadOnly;
            IsActive = l.IsActive;
            IsLibrary = l.IsLibrary;
            ScanIntervalMinutes = l.ScanIntervalMinutes;
            ExcludeFolders = l.ExcludeFolders ?? string.Empty;
            Note = l.Note ?? string.Empty;
            LastScanDate = l.LastScanDate;
            LastScanResult = l.LastScanResult;
            HasPassword = l.HasPassword;
            HasPrivateKey = l.HasPrivateKey;
            Status = l.Status;
            Title = l.Name;
        }
        else
        {
            Title = Text.T("Storages.New");
            RootPath = connections.StorageRoot.TrimEnd('/', '\\') + (connections.StorageRoot.Contains('\\') ? "\\" : "/");
        }

        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var (savedId, errors) = await StorageAdmin.SaveAsync(db, connections, UserId, Id, Input(), HttpContext.RequestAborted);
        if (savedId is { } id)
        {
            if (Id is null)
            {
                Flash(Text.T("Storages.Created", Name.Trim()));
                HttpContext.RequestServices.GetRequiredService<MatPhoto.Services.Library.LibraryScanner>().Request(id);
                return LocalRedirect($"/Admin/Storages/Access?id={id}");
            }

            Flash(Text.T("Common.SavedNamed", Name.Trim()));
            return LocalRedirect("/Admin/Storages");
        }

        if (errors.Count == 0)
        {
            return NotFound();
        }

        errors.ForEach(e => ModelState.AddModelError(e.Field, e.Message));
        return await RedrawAsync();
    }

    /// <summary>"Test connection": the values of the form, nothing saved; the page shows the outcome above the buttons.</summary>
    public async Task<IActionResult> OnPostTestAsync()
    {
        var test = await StorageAdmin.TestAsync(db, connections, UserId, Id, Input(), HttpContext.RequestAborted);
        if (test.NotAllowed)
        {
            return NotFound();
        }

        Tested = test.Value;
        TestError = test.Error;
        return await RedrawAsync();
    }

    public async Task<IActionResult> OnPostDeleteAsync()
    {
        if (!await StorageAdmin.DeleteAsync(db, connections, UserId, Id ?? 0, HttpContext.RequestAborted))
        {
            return NotFound();
        }

        Flash(Text.T("Storages.Deleted", Name.Trim()));
        return LocalRedirect("/Admin/Storages");
    }

    private StorageInput Input() => new(Name, Kind, RootPath, Host, Port, Share, Username, Password, ClearPassword, Domain, WebDavUrl, UseTls, AcceptAnyCertificate, UseKey, PrivateKey,
        IsReadOnly, IsActive, IsLibrary, ScanIntervalMinutes, ExcludeFolders, Note);

    /// <summary>The page again after a post: the stored state (secrets, status) comes from the business layer, the fields keep what was typed.</summary>
    private async Task<IActionResult> RedrawAsync()
    {
        if (Id is { } id)
        {
            if (await StorageAdmin.GetAsync(db, connections, UserId, id, HttpContext.RequestAborted) is not { } l)
            {
                return NotFound();
            }

            HasPassword = l.HasPassword && !ClearPassword;
            HasPrivateKey = l.HasPrivateKey;
            Status = l.Status;
            Title = l.Name;
            LastScanDate = l.LastScanDate;
            LastScanResult = l.LastScanResult;
        }
        else
        {
            Title = Text.T("Storages.New");
        }

        return Page();
    }

    /// <summary>"Scan now": the scanner reads the storage again soon (the page Scans shows how far it is).</summary>
    public async Task<IActionResult> OnPostScanAsync()
    {
        if (Id is not { } id || await StorageAdmin.GetAsync(db, connections, UserId, id, HttpContext.RequestAborted) is not { IsLibrary: true, IsActive: true })
        {
            return NotFound();
        }

        HttpContext.RequestServices.GetRequiredService<MatPhoto.Services.Library.LibraryScanner>().Request(id);
        Flash(Text.T("Scan.Started"));
        return LocalRedirect("/Admin/Scans");
    }

    /// <summary>The address of a handler of this page with its query (the id stays).</summary>
    public string HandlerUrl(string handler) => ButtonRowTagHelper.HandlerUrl(Request, handler);
}
