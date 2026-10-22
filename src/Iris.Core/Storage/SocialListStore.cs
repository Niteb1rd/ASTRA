using System.Text;
using Iris.Core.Config;

namespace Iris.Core.Storage;

/// <summary>What <see cref="SocialListStore.Load"/> found.</summary>
public sealed class SocialListLoadResult
{
    public SocialListLoadResult(ConfigLoadResult result, bool isNew)
    {
        Result = result;
        IsNew = isNew;
    }

    /// <summary>The config (if readable) plus every error and warning. Show these to the user; never drop them.</summary>
    public ConfigLoadResult Result { get; }

    /// <summary>True when there was no file yet, so the config is a fresh empty one.</summary>
    public bool IsNew { get; }

    /// <summary>The config to work with. Never null: an unreadable file gives an empty config (check <see cref="IsValid"/>).</summary>
    public SocialConfig Config => Result.Config ?? new SocialConfig();

    public IReadOnlyList<string> Errors => Result.Errors;

    public IReadOnlyList<string> Warnings => Result.Warnings;

    /// <summary>True when the file was missing or read without errors.</summary>
    public bool IsValid => IsNew || Result.IsValid;
}

/// <summary>Outcome of <see cref="SocialListStore.Save"/>. Saving never throws for an expected problem such as a locked file.</summary>
public sealed class SaveResult
{
    private SaveResult(bool success, string? error)
    {
        Success = success;
        Error = error;
    }

    public bool Success { get; }

    /// <summary>Plain-words reason. Only set when saving failed.</summary>
    public string? Error { get; }

    public static SaveResult Ok() => new(true, null);

    public static SaveResult Failed(string error) => new(false, error);
}

/// <summary>
/// Loads and saves the social list file. Saving is crash-safe (see <see cref="AtomicFile"/>): a crash never leaves a half-written list.
/// The previous version is kept as "name.bak" (one backup, overwritten on every save).
/// Loading never changes the file: an invalid file stays exactly as it is until the user saves.
/// </summary>
public static class SocialListStore
{
    /// <summary>An empty list as a fresh start has it: no rows, but the permanent No Match rule (Blue).</summary>
    public static SocialConfig NewConfig()
    {
        var config = new SocialConfig();
        NoMatchRule.Ensure(config);
        return config;
    }

    /// <summary>Reads the list. A missing file gives an empty config (with the No Match rule) marked as new.</summary>
    public static SocialListLoadResult Load(string path)
    {
        if (!File.Exists(path))
            return new SocialListLoadResult(new ConfigLoadResult { Config = NewConfig() }, isNew: true);

        string text;
        try
        {
            text = File.ReadAllText(path, Encoding.UTF8);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            var unreadable = new ConfigLoadResult();
            unreadable.Errors.Add("The file could not be read: " + ex.Message);
            return new SocialListLoadResult(unreadable, isNew: false);
        }

        return new SocialListLoadResult(SocialConfigToml.Read(text), isNew: false);
    }

    /// <summary>
    /// Writes the list safely (see the class summary). The folder is created if needed.
    /// With <paramref name="watched"/> (the app's watched copy of this file) the save is also remembered, so it is never taken for an outside
    /// edit, and its text becomes the last-good copy.
    /// </summary>
    public static SaveResult Save(string path, SocialConfig config, WatchedFile? watched = null)
    {
        var text = SocialConfigToml.Write(config);

        // Safety net: never replace the user's file with something this program cannot read back.
        var check = SocialConfigToml.Read(text);
        if (!check.IsValid)
            return SaveResult.Failed("The list is not valid and was not saved: " + string.Join(" ", check.Errors));

        try
        {
            AtomicFile.WriteText(path, text, keepBackup: true);
            watched?.Wrote(text);
            return SaveResult.Ok();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return SaveResult.Failed("The list could not be saved: " + ex.Message);
        }
    }
}
