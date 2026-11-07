using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia;
using EasyFileSync.Core.Models;

namespace EasyFileSync.App.Views;

public class JobSidebarItem : INotifyPropertyChanged
{
    private bool _isExpanded = true;
    private bool _isVisible = true;

    private string _name = "";

    public bool IsGroup { get; set; }
    public string GroupName { get; set; } = "";
    public JobConfig? Job { get; set; }
    public string Name
    {
        get
        {
            if (Job != null && !string.IsNullOrEmpty(Job.Name))
            {
                return Job.Name;
            }
            return _name;
        }
        set
        {
            _name = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DisplayTitle));
        }
    }
    public int JobCount { get; set; }

    public string DisplayTitle
    {
        get
        {
            if (IsGroup)
            {
                if (JobCount > 0)
                {
                    return GroupName + " (" + JobCount + ")";
                }
                else
                {
                    return GroupName;
                }
            }
            return Name;
        }
    }

    public string ExpandGlyph
    {
        get
        {
            if (IsGroup)
            {
                if (_isExpanded)
                {
                    return "▾";
                }
                else
                {
                    return "▸";
                }
            }
            return "";
        }
    }

    public bool IsExpanded
    {
        get
        {
            return _isExpanded;
        }
        set
        {
            if (_isExpanded != value)
            {
                _isExpanded = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ExpandGlyph));
            }
        }
    }

    public bool IsVisible
    {
        get
        {
            return _isVisible;
        }
        set
        {
            if (_isVisible != value)
            {
                _isVisible = value;
                OnPropertyChanged();
            }
        }
    }

    public Thickness IndentMargin
    {
        get
        {
            if (IsGroup)
            {
                return new Thickness(4, 0, 0, 0);
            }
            if (Job != null && !string.IsNullOrEmpty(Job.Group))
            {
                return new Thickness(22, 0, 0, 0);
            }
            return new Thickness(6, 0, 0, 0);
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? propName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propName));
    }
}

