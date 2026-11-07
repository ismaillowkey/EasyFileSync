using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using EasyFileSync.Core.Engine;

namespace EasyFileSync.App.Views;

public partial class RenameJobDialog : Window
{
    private readonly string _oldName = "";
    private readonly JobStore? _jobStore;
    private readonly List<string>? _existingNames;

    public RenameJobDialog()
    {
        InitializeComponent();
    }

    public RenameJobDialog(string currentName, JobStore? jobStore) : this()
    {
        _oldName = currentName;
        _jobStore = jobStore;

        var txtName = this.FindControl<TextBox>("TxtJobName");
        if (txtName != null)
        {
            txtName.Text = currentName;
            Dispatcher.UIThread.Post(() =>
            {
                txtName.Focus();
                txtName.SelectAll();
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
            btnCancel.Click += OnCancelClick;
        }
    }

    public RenameJobDialog(string currentName, List<string> existingNames) : this()
    {
        _oldName = currentName;
        _existingNames = existingNames;
        Title = "Rename Group";

        var txtName = this.FindControl<TextBox>("TxtJobName");
        if (txtName != null)
        {
            txtName.Text = currentName;
            Dispatcher.UIThread.Post(() =>
            {
                txtName.Focus();
                txtName.SelectAll();
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
            btnCancel.Click += OnCancelClick;
        }
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private void OnOkClick(object? sender, RoutedEventArgs e)
    {
        var txtName = this.FindControl<TextBox>("TxtJobName");
        var txtError = this.FindControl<TextBlock>("TxtError");

        string newName = "";
        if (txtName != null)
        {
            if (txtName.Text != null)
            {
                newName = txtName.Text.Trim();
            }
        }

        if (string.IsNullOrWhiteSpace(newName))
        {
            if (txtError != null)
            {
                txtError.Text = "Job name cannot be empty.";
                txtError.IsVisible = true;
            }
            if (txtName != null)
            {
                txtName.Focus();
            }
            return;
        }

        if (string.Equals(newName, _oldName, StringComparison.OrdinalIgnoreCase))
        {
            // Nama sama persis, tidak ada perubahan
            Close(null);
            return;
        }

        if (_existingNames != null)
        {
            bool exists = false;
            foreach (var name in _existingNames)
            {
                if (string.Equals(name, newName, StringComparison.OrdinalIgnoreCase))
                {
                    exists = true;
                    break;
                }
            }
            if (exists)
            {
                if (txtError != null)
                {
                    txtError.Text = "A group with this name already exists.";
                    txtError.IsVisible = true;
                }
                if (txtName != null)
                {
                    txtName.Focus();
                }
                return;
            }
        }
        else if (_jobStore != null)
        {
            if (_jobStore.NameExists(newName))
            {
                if (txtError != null)
                {
                    txtError.Text = "A job with this name already exists.";
                    txtError.IsVisible = true;
                }
                if (txtName != null)
                {
                    txtName.Focus();
                }
                return;
            }
        }

        Close(newName);
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        Close(null);
    }
}

