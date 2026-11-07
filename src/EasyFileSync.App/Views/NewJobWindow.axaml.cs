using System;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using EasyFileSync.Core.Engine;
using EasyFileSync.Core.Models;

namespace EasyFileSync.App.Views;

public class NewJobResult
{
    public string Name { get; set; } = "";
    public JobDirection Direction { get; set; } = JobDirection.LeftToRight;
}

public partial class NewJobWindow : Window
{
    private readonly JobStore? _jobStore;

    public NewJobWindow()
    {
        InitializeComponent();
    }

    public NewJobWindow(string defaultJobName, JobStore? jobStore = null)
    {
        InitializeComponent();
        _jobStore = jobStore;

        var nameBox = this.FindControl<TextBox>("JobNameBox");
        if (nameBox is not null)
        {
            nameBox.Text = defaultJobName;

            // Pilih seluruh teks saat window dimuat agar user bisa langsung mengetik
            Dispatcher.UIThread.Post(() =>
            {
                nameBox.Focus();
                nameBox.SelectAll();
            }, DispatcherPriority.Loaded);
        }
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private void OnOkClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var nameBox = this.FindControl<TextBox>("JobNameBox");
        var errorLabel = this.FindControl<TextBlock>("ErrorLabel");
        var radioBackup = this.FindControl<RadioButton>("RadioBackup");

        var rawName = "";
        if (nameBox is not null && nameBox.Text is not null)
        {
            rawName = nameBox.Text.Trim();
        }

        // Validasi: nama job tidak boleh kosong
        if (string.IsNullOrWhiteSpace(rawName))
        {
            if (errorLabel is not null)
            {
                errorLabel.Text = "Job name cannot be empty.";
                errorLabel.IsVisible = true;
            }
            if (nameBox is not null)
            {
                nameBox.Focus();
            }
            return;
        }

        // Validasi: nama job tidak boleh duplikat
        if (_jobStore is not null)
        {
            if (_jobStore.NameExists(rawName))
            {
                if (errorLabel is not null)
                {
                    errorLabel.Text = $"Job '{rawName}' already exists.";
                    errorLabel.IsVisible = true;
                }
                if (nameBox is not null)
                {
                    nameBox.Focus();
                }
                return;
            }
        }

        // Tentukan arah job berdasarkan pilihan radio
        var selectedDirection = JobDirection.LeftToRight;
        if (radioBackup is not null)
        {
            if (radioBackup.IsChecked == true)
            {
                // Pilihan Backup adalah Left to Right
                selectedDirection = JobDirection.LeftToRight;
            }
            else
            {
                // Pilihan Synchronize adalah TwoWay
                selectedDirection = JobDirection.TwoWay;
            }
        }

        var result = new NewJobResult
        {
            Name = rawName,
            Direction = selectedDirection
        };

        Close(result);
    }

    private void OnCancelClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        Close(null);
    }
}
