using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Astra.App.ViewModels;

/// <summary>The smallest possible INotifyPropertyChanged base class (no MVVM package used).</summary>
public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Stores the value and raises PropertyChanged if it really changed. Returns true when it changed.</summary>
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;
        field = value;
        Raise(propertyName);
        return true;
    }

    protected void Raise([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
