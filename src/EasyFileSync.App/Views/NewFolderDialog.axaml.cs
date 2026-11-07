using System;
using System.IO;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;

namespace EasyFileSync.App.Views;

public partial class NewFolderDialog : Window
{
    public NewFolderDialog()
    {
        InitializeComponent();

        var txtName = this.FindControl<TextBox>("TxtFolderName");
        if (txtName != null)
        {
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
        var txtName = this.FindControl<TextBox>("TxtFolderName");
        var txtError = this.FindControl<TextBlock>("TxtError");

        string folderName = "";
        if (txtName != null)
        {
            if (txtName.Text != null)
            {
                folderName = txtName.Text.Trim();
            }
        }

        if (string.IsNullOrWhiteSpace(folderName))
        {
            if (txtError != null)
            {
                txtError.Text = "Folder name cannot be empty.";
                txtError.IsVisible = true;
            }
            if (txtName != null)
            {
                txtName.Focus();
            }
            return;
        }

        // Validasi karakter terlarang dalam nama file/folder
        char[] invalidChars = Path.GetInvalidFileNameChars();
        int invalidIndex = folderName.IndexOfAny(invalidChars);
        if (invalidIndex >= 0)
        {
            if (txtError != null)
            {
                txtError.Text = "Folder name contains invalid character: " + folderName[invalidIndex];
                txtError.IsVisible = true;
            }
            if (txtName != null)
            {
                txtName.Focus();
            }
            return;
        }

        Close(folderName);
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        Close(null);
    }
}

