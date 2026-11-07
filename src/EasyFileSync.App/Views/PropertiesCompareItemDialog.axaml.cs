using System;
using System.Diagnostics;
using System.IO;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;

namespace EasyFileSync.App.Views;

public partial class PropertiesCompareItemDialog : Window
{
    private string _leftFullPath = "";
    private string _rightFullPath = "";

    public PropertiesCompareItemDialog()
    {
        InitializeComponent();
    }

    public PropertiesCompareItemDialog(SyncActionViewModel item, string leftBasePath, string rightBasePath) : this()
    {
        string relPath = item.RelativePath.Replace('/', '\\');

        if (!string.IsNullOrEmpty(leftBasePath))
        {
            _leftFullPath = Path.Combine(leftBasePath, relPath);
        }

        if (!string.IsNullOrEmpty(rightBasePath))
        {
            _rightFullPath = Path.Combine(rightBasePath, relPath);
        }

        Title = "Properties - " + item.DisplayName;

        var txtIcon = this.FindControl<TextBlock>("TxtHeaderIcon");
        if (txtIcon != null)
        {
            if (item.IsDirectory)
            {
                txtIcon.Text = "📁";
            }
            else
            {
                txtIcon.Text = "📄";
            }
        }

        var txtName = this.FindControl<TextBlock>("TxtDisplayName");
        if (txtName != null)
        {
            txtName.Text = item.DisplayName;
        }

        var txtRel = this.FindControl<TextBlock>("TxtRelativePath");
        if (txtRel != null)
        {
            txtRel.Text = "Relative: " + item.RelativePath;
        }

        // Sisi Kiri
        var txtLeftPath = this.FindControl<TextBlock>("TxtLeftPath");
        var txtLeftSize = this.FindControl<TextBlock>("TxtLeftSize");
        var txtLeftMod = this.FindControl<TextBlock>("TxtLeftModified");
        var btnOpenLeft = this.FindControl<Button>("BtnOpenLeft");

        if (item.HasLeft)
        {
            if (txtLeftPath != null)
            {
                txtLeftPath.Text = _leftFullPath;
            }
            if (txtLeftSize != null)
            {
                txtLeftSize.Text = item.LeftSize;
            }
            if (txtLeftMod != null)
            {
                txtLeftMod.Text = item.LeftDate;
            }
            if (btnOpenLeft != null)
            {
                btnOpenLeft.IsEnabled = true;
                btnOpenLeft.Click += OnOpenLeftClick;
            }
        }
        else
        {
            if (txtLeftPath != null)
            {
                txtLeftPath.Text = "(Not present on Left)";
                txtLeftPath.Foreground = new SolidColorBrush(Color.Parse("#9CA3AF"));
            }
            if (txtLeftSize != null)
            {
                txtLeftSize.Text = "-";
            }
            if (txtLeftMod != null)
            {
                txtLeftMod.Text = "-";
            }
            if (btnOpenLeft != null)
            {
                btnOpenLeft.IsEnabled = false;
            }
        }

        // Sisi Kanan
        var txtRightPath = this.FindControl<TextBlock>("TxtRightPath");
        var txtRightSize = this.FindControl<TextBlock>("TxtRightSize");
        var txtRightMod = this.FindControl<TextBlock>("TxtRightModified");
        var btnOpenRight = this.FindControl<Button>("BtnOpenRight");

        if (item.HasRight)
        {
            if (txtRightPath != null)
            {
                txtRightPath.Text = _rightFullPath;
            }
            if (txtRightSize != null)
            {
                txtRightSize.Text = item.RightSize;
            }
            if (txtRightMod != null)
            {
                txtRightMod.Text = item.RightDate;
            }
            if (btnOpenRight != null)
            {
                btnOpenRight.IsEnabled = true;
                btnOpenRight.Click += OnOpenRightClick;
            }
        }
        else
        {
            if (txtRightPath != null)
            {
                txtRightPath.Text = "(Not present on Right)";
                txtRightPath.Foreground = new SolidColorBrush(Color.Parse("#9CA3AF"));
            }
            if (txtRightSize != null)
            {
                txtRightSize.Text = "-";
            }
            if (txtRightMod != null)
            {
                txtRightMod.Text = "-";
            }
            if (btnOpenRight != null)
            {
                btnOpenRight.IsEnabled = false;
            }
        }

        // Status Perbandingan
        var txtCompStatus = this.FindControl<TextBlock>("TxtComparisonStatus");
        if (txtCompStatus != null)
        {
            if (item.IsOrphan)
            {
                if (item.HasLeft && !item.HasRight)
                {
                    txtCompStatus.Text = "Left Orphan (File only exists on Left side)";
                    txtCompStatus.Foreground = new SolidColorBrush(Color.Parse("#7B1FA2"));
                }
                else
                {
                    txtCompStatus.Text = "Right Orphan (File only exists on Right side)";
                    txtCompStatus.Foreground = new SolidColorBrush(Color.Parse("#7B1FA2"));
                }
            }
            else if (item.HasDifference)
            {
                txtCompStatus.Text = "Different (File modified dates or sizes differ)";
                txtCompStatus.Foreground = new SolidColorBrush(Color.Parse("#C62828"));
            }
            else
            {
                txtCompStatus.Text = "Identical (Files are identical on both sides)";
                txtCompStatus.Foreground = new SolidColorBrush(Color.Parse("#2E7D32"));
            }
        }

        var btnClose = this.FindControl<Button>("BtnClose");
        if (btnClose != null)
        {
            btnClose.Click += (_, _) => Close();
        }
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private void OnOpenLeftClick(object? sender, RoutedEventArgs e)
    {
        OpenInExplorer(_leftFullPath);
    }

    private void OnOpenRightClick(object? sender, RoutedEventArgs e)
    {
        OpenInExplorer(_rightFullPath);
    }

    private static void OpenInExplorer(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            if (File.Exists(path))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = "/select,\"" + path + "\"",
                    UseShellExecute = true
                });
            }
            else if (Directory.Exists(path))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = "\"" + path + "\"",
                    UseShellExecute = true
                });
            }
            else
            {
                string? parent = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(parent) && Directory.Exists(parent))
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "explorer.exe",
                        Arguments = "\"" + parent + "\"",
                        UseShellExecute = true
                    });
                }
            }
        }
        catch
        {
            // Abaikan jika explorer gagal dipanggil
        }
    }
}

