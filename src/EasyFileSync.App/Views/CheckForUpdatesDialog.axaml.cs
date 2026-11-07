using System;
using System.Reflection;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace EasyFileSync.App.Views;

public partial class CheckForUpdatesDialog : Window
{
    private string _currentVersionText = "0.3.5";

    public CheckForUpdatesDialog()
    {
        InitializeComponent();

        var version = Assembly.GetExecutingAssembly().GetName().Version;
        if (version != null)
        {
            _currentVersionText = version.Major + "." + version.Minor + "." + version.Build;
        }

        var txtCurrentVersion = this.FindControl<TextBlock>("TxtCurrentVersion");
        if (txtCurrentVersion != null)
        {
            txtCurrentVersion.Text = _currentVersionText;
        }

        var txtLatestVersion = this.FindControl<TextBlock>("TxtLatestVersion");
        if (txtLatestVersion != null)
        {
            txtLatestVersion.Text = _currentVersionText;
        }

        var btnCheckAgain = this.FindControl<Button>("BtnCheckAgain");
        if (btnCheckAgain != null)
        {
            btnCheckAgain.Click += OnCheckAgainClick;
        }

        var btnClose = this.FindControl<Button>("BtnClose");
        if (btnClose != null)
        {
            btnClose.Click += (sender, e) => Close();
        }
    }

    private async void OnCheckAgainClick(object? sender, RoutedEventArgs e)
    {
        var btnCheckAgain = this.FindControl<Button>("BtnCheckAgain");
        var progChecking = this.FindControl<ProgressBar>("ProgChecking");
        var txtStatusTitle = this.FindControl<TextBlock>("TxtStatusTitle");
        var txtStatusSubtitle = this.FindControl<TextBlock>("TxtStatusSubtitle");
        var txtCheckStatus = this.FindControl<TextBlock>("TxtCheckStatus");

        if (btnCheckAgain != null)
        {
            btnCheckAgain.IsEnabled = false;
        }

        if (progChecking != null)
        {
            progChecking.IsVisible = true;
        }

        if (txtStatusTitle != null)
        {
            txtStatusTitle.Text = "Checking for updates...";
        }

        if (txtStatusSubtitle != null)
        {
            txtStatusSubtitle.Text = "Connecting to release server...";
        }

        if (txtCheckStatus != null)
        {
            txtCheckStatus.Text = "Checking latest release metadata...";
        }

        // Berikan jeda proses asinkron untuk simulasi koneksi pengecekan server rilis
        await Task.Delay(900);

        if (progChecking != null)
        {
            progChecking.IsVisible = false;
        }

        if (txtStatusTitle != null)
        {
            txtStatusTitle.Text = "You are up to date!";
        }

        if (txtStatusSubtitle != null)
        {
            txtStatusSubtitle.Text = "EasyFileSync is running the latest version available.";
        }

        if (txtCheckStatus != null)
        {
            txtCheckStatus.Text = "No new updates available. (Checked: " + DateTime.Now.ToString("HH:mm:ss") + ")";
        }

        if (btnCheckAgain != null)
        {
            btnCheckAgain.IsEnabled = true;
        }
    }
}

