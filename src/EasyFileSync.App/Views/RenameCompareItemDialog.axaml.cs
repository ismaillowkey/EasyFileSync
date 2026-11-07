using System;
using System.IO;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;

namespace EasyFileSync.App.Views;

public class RenameCompareItemResult
{
    public bool Confirmed { get; set; }
    public string NewName { get; set; } = "";
    public bool RenameBothSides { get; set; } = true;
}

public partial class RenameCompareItemDialog : Window
{
    private readonly string _oldName = "";
    private readonly bool _hasBothSides = false;

    public RenameCompareItemDialog()
    {
        InitializeComponent();
    }

    public RenameCompareItemDialog(string currentName, bool isDirectory, bool hasBothSides) : this()
    {
        _oldName = currentName;
        _hasBothSides = hasBothSides;

        var txtPrompt = this.FindControl<TextBlock>("TxtPrompt");
        if (txtPrompt != null)
        {
            if (isDirectory)
            {
                txtPrompt.Text = "Enter new folder name:";
                Title = "Rename Folder";
            }
            else
            {
                txtPrompt.Text = "Enter new file name:";
                Title = "Rename File";
            }
        }

        var chkBoth = this.FindControl<CheckBox>("ChkRenameBothSides");
        if (chkBoth != null)
        {
            if (hasBothSides)
            {
                chkBoth.IsVisible = true;
                chkBoth.IsChecked = true;
            }
            else
            {
                chkBoth.IsVisible = false;
                chkBoth.IsChecked = false;
            }
        }

        var txtName = this.FindControl<TextBox>("TxtNewName");
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
        var txtName = this.FindControl<TextBox>("TxtNewName");
        var txtError = this.FindControl<TextBlock>("TxtError");
        var chkBoth = this.FindControl<CheckBox>("ChkRenameBothSides");

        string newName = "";
        if (txtName != null && txtName.Text != null)
        {
            newName = txtName.Text.Trim();
        }

        if (string.IsNullOrWhiteSpace(newName))
        {
            if (txtError != null)
            {
                txtError.Text = "Name cannot be empty.";
                txtError.IsVisible = true;
            }
            return;
        }

        // Cek karakter terlarang Windows file name
        char[] invalidChars = Path.GetInvalidFileNameChars();
        if (newName.IndexOfAny(invalidChars) >= 0)
        {
            if (txtError != null)
            {
                txtError.Text = "Name contains invalid characters (e.g. \\ / : * ? \" < > |).";
                txtError.IsVisible = true;
            }
            return;
        }

        bool renameBoth = false;
        if (chkBoth != null && chkBoth.IsChecked.HasValue)
        {
            renameBoth = chkBoth.IsChecked.Value;
        }

        var result = new RenameCompareItemResult
        {
            Confirmed = true,
            NewName = newName,
            RenameBothSides = renameBoth
        };

        Close(result);
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        Close(new RenameCompareItemResult { Confirmed = false });
    }
}

