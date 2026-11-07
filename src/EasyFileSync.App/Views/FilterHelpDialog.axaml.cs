using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace EasyFileSync.App.Views;

public partial class FilterHelpDialog : Window
{
    public FilterHelpDialog()
    {
        InitializeComponent();

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
}

