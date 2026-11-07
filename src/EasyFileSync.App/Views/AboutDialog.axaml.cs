using System;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace EasyFileSync.App.Views;

public partial class AboutDialog : Window
{
    public AboutDialog()
    {
        InitializeComponent();

        var txtVersion = this.FindControl<TextBlock>("TxtVersion");
        if (txtVersion != null)
        {
            var version = Assembly.GetExecutingAssembly().GetName().Version;
            if (version != null)
            {
                txtVersion.Text = "Version " + version.Major + "." + version.Minor + "." + version.Build + " (64-bit)";
            }
        }

        var btnOk = this.FindControl<Button>("BtnOk");
        if (btnOk != null)
        {
            btnOk.Click += (sender, e) => Close();
        }
    }
}

