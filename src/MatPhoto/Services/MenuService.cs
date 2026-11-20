using MatPhoto.Data;
using MatPhoto.Services.Library;
using MatPhoto.Services.Storages;
using Microsoft.EntityFrameworkCore;

namespace MatPhoto.Services;

/// <summary>What the menu shows: the lists of the person (with their colour), whether they may import, imports that wait for them.</summary>
public sealed class MenuModel
{
    public IReadOnlyList<(long Id, string Name, string? Colour)> Lists { get; init; } = [];

    public bool CanImport { get; init; }

    /// <summary>Imports of the person that wait for a look (analysed, not imported yet).</summary>
    public int ImportsWaiting { get; init; }

    public bool HasStorages { get; init; }

    /// <summary>The recognition of faces is switched on: the menu shows People.</summary>
    public bool People { get; init; }
}

/// <summary>One menu of the switcher at the foot of the menu.</summary>
public sealed record MenuArea(string Id, string Title, string Icon);

public sealed class MenuService(AppDbContext db, CurrentUser me, LibrarySettings library)
{
    private MenuModel? _model;

    /// <summary>The menus of the switcher: the photos, the settings of the person and, for an administrator, the administration.</summary>
    public static IReadOnlyList<MenuArea> Areas(bool admin)
    {
        var list = new List<MenuArea>
        {
            new("photos", Localization.Text.T("Shell.Area.Photos"), "image"),
            new("settings", Localization.Text.T("Settings.Title"), "settings"),
        };
        if (admin)
        {
            list.Add(new MenuArea("admin", Localization.Text.T("Admin.Title"), "shield"));
        }

        return list;
    }

    /// <summary>The menu a page belongs to, by its address.</summary>
    public static string AreaOf(string? path)
    {
        path ??= "/";
        bool Under(string prefix) => path.Equals(prefix, StringComparison.OrdinalIgnoreCase) || path.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase);
        if (Under("/Admin"))
        {
            return "admin";
        }

        return Under("/Account") ? "settings" : "photos";
    }

    public async Task<MenuModel> GetAsync()
    {
        if (_model is not null)
        {
            return _model;
        }

        var userId = me.RequiredId;
        var grants = await StorageAccess.ListForPersonAsync(db, userId);
        _model = new MenuModel
        {
            Lists = await CollectionService.TargetsAsync(db, userId),
            CanImport = grants.Any(g => g.IsLibrary && g.Role >= AccessRole.Editor && !g.IsReadOnly),
            ImportsWaiting = await db.ImportSessions.CountAsync(s => s.UserId == userId && s.State == ImportState.Review),
            HasStorages = grants.Any(g => g.IsLibrary),
            People = library.Current.FacesEnabled,
        };
        return _model;
    }
}
