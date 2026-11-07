using System;
using System.Threading;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace EasyFileSync.App.Views;

public partial class LoadingProgressDialog : Window
{
    private readonly CancellationTokenSource _cts;

    public LoadingProgressDialog()
    {
        InitializeComponent();
        _cts = new CancellationTokenSource();
    }

    public LoadingProgressDialog(string targetName, CancellationTokenSource cts)
    {
        InitializeComponent();
        _cts = cts;

        var txtTitle = this.FindControl<TextBlock>("TxtTitle");
        if (txtTitle != null)
        {
            txtTitle.Text = "Connecting to " + targetName + "...";
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

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        // Pengguna mengklik Cancel: batalkan proses asinkron
        _cts.Cancel();
        Close(false);
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        _cts.Cancel();
        base.OnClosing(e);
    }
}

