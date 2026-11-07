using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace EasyFileSync.App.Views;

public partial class ConfirmDeleteDialog : Window
{
    public ConfirmDeleteDialog()
    {
        InitializeComponent();
    }

    public ConfirmDeleteDialog(string jobName) : this()
    {
        var txtMsg = this.FindControl<TextBlock>("TxtMessage");
        if (txtMsg != null)
        {
            txtMsg.Text = "Are you sure you want to delete job '" + jobName + "'?";
        }

        var btnDelete = this.FindControl<Button>("BtnDelete");
        if (btnDelete != null)
        {
            btnDelete.Click += (_, _) => Close(true);
        }

        var btnCancel = this.FindControl<Button>("BtnCancel");
        if (btnCancel != null)
        {
            btnCancel.Click += (_, _) => Close(false);
        }
    }

    public ConfirmDeleteDialog(string title, string message) : this()
    {
        Title = title;
        var txtMsg = this.FindControl<TextBlock>("TxtMessage");
        if (txtMsg != null)
        {
            txtMsg.Text = message;
        }

        var btnDelete = this.FindControl<Button>("BtnDelete");
        if (btnDelete != null)
        {
            btnDelete.Click += (_, _) => Close(true);
        }

        var btnCancel = this.FindControl<Button>("BtnCancel");
        if (btnCancel != null)
        {
            btnCancel.Click += (_, _) => Close(false);
        }
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}

