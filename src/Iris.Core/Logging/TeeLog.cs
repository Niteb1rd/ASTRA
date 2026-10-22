namespace Iris.Core.Logging;

/// <summary>Sends every line to several logs. A log that throws does not stop the others.</summary>
public sealed class TeeLog : ILog
{
    private readonly ILog[] _targets;

    public TeeLog(params ILog[] targets)
    {
        _targets = targets;
    }

    public void Write(DateTimeOffset utcTime, string message)
    {
        foreach (var target in _targets)
        {
            try
            {
                target.Write(utcTime, message);
            }
            catch
            {
                // one broken log must not hide the line from the others
            }
        }
    }
}
