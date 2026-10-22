using System;
using System.Collections.Generic;
using System.Globalization;
using Astra.Core;

namespace Astra.App.Services;

/// <summary>
/// A simple log that keeps the last lines in memory. <see cref="AppServices"/> combines it with the log file.
/// </summary>
public sealed class InMemoryLog : ILog
{
    private const int MaxLines = 2000;

    private readonly object _gate = new();
    private readonly Queue<string> _lines = new();

    public void Write(DateTimeOffset utcTime, string message)
    {
        var line = utcTime.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "Z  " + message;
        lock (_gate)
        {
            _lines.Enqueue(line);
            while (_lines.Count > MaxLines)
                _lines.Dequeue();
        }
    }

    /// <summary>A copy of the lines kept so far, oldest first.</summary>
    public IReadOnlyList<string> Snapshot()
    {
        lock (_gate)
            return _lines.ToArray();
    }
}
