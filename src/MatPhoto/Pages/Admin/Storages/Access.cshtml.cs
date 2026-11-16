using MatPhoto.Controls;
using MatPhoto.Data;
using MatPhoto.Localization;
using MatPhoto.Services.Storages;
using Microsoft.AspNetCore.Mvc;

namespace MatPhoto.Pages.Admin.Storages;

/// <summary>
/// The tab "Access" of a location: whether everybody may use it (as Viewer or Editor) or nobody, and the list of people with their role (added by the address of
/// their account, changed, removed). Every change is saved at once. A read-only location gives nobody more than Viewer, whatever the list says.
/// </summary>
public sealed class AccessModel(AppDbContext db) : AppPageModel
{
    [BindProperty(SupportsGet = true)] public long Id { get; set; }

    [BindProperty] public string Everyone { get; set; } = "None";

    [BindProperty] public string Email { get; set; } = string.Empty;

    [BindProperty] public string Role { get; set; } = nameof(AccessRole.Viewer);

    [BindProperty] public long UserIdOf { get; set; }

    public StorageAccessInfo Info { get; private set; } = null!;

    public List<Choice> EveryoneChoices { get; } =
    [
        new("None", Text.T("Storages.Everyone.None"), Text.T("Storages.Everyone.NoneHint")),
        new(nameof(AccessRole.Viewer), Text.T("Roles.Viewer"), Text.T("Storages.Everyone.ViewerHint")),
        new(nameof(AccessRole.Editor), Text.T("Roles.Editor"), Text.T("Storages.Everyone.EditorHint")),
    ];

    public List<Choice> RoleChoices { get; } = [new(nameof(AccessRole.Viewer), Text.T("Roles.Viewer")), new(nameof(AccessRole.Editor), Text.T("Roles.Editor"))];

    public async Task<IActionResult> OnGetAsync()
    {
        if (!await LoadAsync())
        {
            return NotFound();
        }

        Everyone = Info.ForEveryone ? Info.EveryoneRole.ToString() : "None";
        return Page();
    }

    public async Task<IActionResult> OnPostEveryoneAsync()
    {
        var role = Enum.TryParse<AccessRole>(Everyone, out var r) ? r : AccessRole.None;
        if (!await StorageAdmin.SetEveryoneAsync(db, UserId, Id, role, HttpContext.RequestAborted))
        {
            return NotFound();
        }

        Flash(Text.T("Common.Saved"));
        return LocalRedirect($"/Admin/Storages/Access?id={Id}");
    }

    public async Task<IActionResult> OnPostAddAsync()
    {
        var role = Enum.TryParse<AccessRole>(Role, out var r) ? r : AccessRole.Viewer;
        var added = await StorageAdmin.AddPersonAsync(db, UserId, Id, Email, role, HttpContext.RequestAborted);
        if (added.NotAllowed)
        {
            return NotFound();
        }

        if (!added.Ok)
        {
            ModelState.AddModelError(nameof(Email), added.Error!);
            if (!await LoadAsync())
            {
                return NotFound();
            }

            Everyone = Info.ForEveryone ? Info.EveryoneRole.ToString() : "None";
            return Page();
        }

        Flash(Text.T("Storages.Access.Added", added.Value!.Name));
        return LocalRedirect($"/Admin/Storages/Access?id={Id}");
    }

    public async Task<IActionResult> OnPostRoleAsync()
    {
        var role = Enum.TryParse<AccessRole>(Role, out var r) ? r : AccessRole.Viewer;
        if (!await StorageAdmin.SetPersonAsync(db, UserId, Id, UserIdOf, role, HttpContext.RequestAborted))
        {
            return NotFound();
        }

        Flash(Text.T("Common.Saved"));
        return LocalRedirect($"/Admin/Storages/Access?id={Id}");
    }

    public async Task<IActionResult> OnPostRemoveAsync()
    {
        if (!await StorageAdmin.RemovePersonAsync(db, UserId, Id, UserIdOf, HttpContext.RequestAborted))
        {
            return NotFound();
        }

        Flash(Text.T("Storages.Access.Removed"));
        return LocalRedirect($"/Admin/Storages/Access?id={Id}");
    }

    public string HandlerUrl(string handler) => ButtonRowTagHelper.HandlerUrl(Request, handler);

    private async Task<bool> LoadAsync()
    {
        var info = await StorageAdmin.AccessAsync(db, UserId, Id, HttpContext.RequestAborted);
        if (info is null)
        {
            return false;
        }

        Info = info;
        return true;
    }
}
