using Iris.Core.Settings;

namespace Iris.Core.Config;

/// <summary>
/// When the opt-in automatic check of the hosted config address is due. One rule, kept here so it can be tested:
/// the setting is on, there is a valid https address, and it is the first check ever or at least 24 hours since the last one
/// (success or failure; <see cref="AppSettings.HostedConfigLastCheck"/>). A last check that lies in the FUTURE (the PC clock was set back)
/// counts as due, so one wrong clock reading can never stop the checks for days.
/// </summary>
public static class HostedConfigSchedule
{
    /// <summary>Time between two automatic checks.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromHours(24);

    /// <summary>How often the app looks at the clock to see whether a check is due (a check itself is made at most once per <see cref="Interval"/>).</summary>
    public static readonly TimeSpan WakeInterval = TimeSpan.FromMinutes(10);

    public static bool IsDue(AppSettings settings, DateTimeOffset utcNow)
    {
        if (!settings.HostedConfigAutoCheck || HostedConfigUrl.Check(settings.HostedConfigUrl) is not null)
            return false;
        if (settings.HostedConfigLastCheck is not { } last)
            return true;
        return last > utcNow || utcNow - last >= Interval;
    }

    /// <summary>
    /// True when a fetched hosted file is not the one imported last time (<see cref="AppSettings.HostedConfigLastHash"/>), so the user
    /// should be asked. Same file as last time: nothing to say. Upper/lower case of the hex does not matter.
    /// </summary>
    public static bool IsUpdate(AppSettings settings, string fetchedHash) =>
        !string.Equals(fetchedHash, settings.HostedConfigLastHash, StringComparison.OrdinalIgnoreCase);
}
