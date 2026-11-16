using MatPhoto.Localization;

namespace MatPhoto.Services.Storages;

/// <summary>
/// Errors of the locations as words for people (<c>Storages.Error.{Code}</c>). What a remote server said is added in brackets (it helps the administrator);
/// a local error never shows a path of the container. An error the providers did not expect is logged with its stack and shown as "the server answered with an error".
/// </summary>
public static class StorageErrors
{
    /// <summary>Set at the start of the app (the static business layer has no logger of its own).</summary>
    public static ILogger? Log { get; set; }

    public static (StorageError Code, string? Detail) Classify(Exception ex)
    {
        switch (ex)
        {
            case StorageException le:
                return (le.Code, le.Detail);
            case OperationCanceledException or TimeoutException:
                return (StorageError.Timeout, null);
            case UnauthorizedAccessException:
                return (StorageError.AccessDenied, null);
            case IOException:
                return (StorageError.Remote, null);
            default:
                Log?.LogWarning(ex, "A storage location failed in an unexpected way.");
                return (StorageError.Remote, ex.Message);
        }
    }

    public static string Message(Exception ex)
    {
        var (code, detail) = Classify(ex);
        return Message(code, detail);
    }

    public static string Message(StorageError code, string? detail)
    {
        if (code == StorageError.Worded && !string.IsNullOrWhiteSpace(detail))
        {
            return detail;
        }

        var text = Text.T("Storages.Error." + code);
        return string.IsNullOrWhiteSpace(detail) || code is not (StorageError.Remote or StorageError.Unreachable or StorageError.AccessDenied or StorageError.TooLarge or StorageError.FolderMissing)
            ? text
            : Text.T("Storages.Error.WithDetail", text, Short(detail));
    }

    private static string Short(string detail)
    {
        var line = detail.ReplaceLineEndings(" ").Trim();
        return line.Length > 200 ? line[..200] + "…" : line;
    }
}
