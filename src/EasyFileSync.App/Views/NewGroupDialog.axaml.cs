using System;
using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using EasyFileSync.Core.Engine;

namespace EasyFileSync.App.Views;

public partial class NewGroupDialog : Window
{
    private readonly JobStore? _jobStore;

    public NewGroupDialog()
    {
        InitializeComponent();
    }

    public NewGroupDialog(string defaultGroupName, JobStore? jobStore = null) : this()
    {
        _jobStore = jobStore;

        var txt = this.FindControl<TextBox>("TxtGroupName");
        if (txt != null)
        {
            txt.Text = defaultGroupName;
            Dispatcher.UIThread.Post(() =>
            {
                txt.Focus();
                txt.SelectAll();
            }, DispatcherPriority.Loaded);
        }

        var btnOk = this.FindControl<Button>("BtnOk");
        if (btnOk != null)
        {
            btnOk.Click += OnOkClick;
        }

        var btnCancel = this.FindControl<Button>("BtnCancel");
        if (btnCancel != null)
        {
            btnCancel.Click += (_, _) => Close(null);
        }
    }

    private void OnOkClick(object? sender, RoutedEventArgs e)
    {
        var txt = this.FindControl<TextBox>("TxtGroupName");
        var txtError = this.FindControl<TextBlock>("TxtError");

        string groupName = txt?.Text?.Trim() ?? "";

        if (string.IsNullOrWhiteSpace(groupName))
        {
            if (txtError != null)
            {
                txtError.Text = "Group name cannot be empty.";
                txtError.IsVisible = true;
            }
            return;
        }

        if (_jobStore != null)
        {
            var existingGroups = _jobStore.GetGroups();
            bool duplicate = false;
            foreach (var g in existingGroups)
            {
                if (string.Equals(g, groupName, StringComparison.OrdinalIgnoreCase))
                {
                    duplicate = true;
                    break;
                }
            }

            if (duplicate)
            {
                if (txtError != null)
                {
                    txtError.Text = "A group with this name already exists.";
                    txtError.IsVisible = true;
                }
                return;
            }
        }

        Close(groupName);
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}

