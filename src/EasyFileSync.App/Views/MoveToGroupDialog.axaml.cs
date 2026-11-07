using System;
using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace EasyFileSync.App.Views;

public partial class MoveToGroupDialog : Window
{
    private const string NoneOption = "(None / Root)";

    public MoveToGroupDialog()
    {
        InitializeComponent();
    }

    public MoveToGroupDialog(string jobName, string currentGroup, List<string> availableGroups) : this()
    {
        var txtPrompt = this.FindControl<TextBlock>("TxtPrompt");
        if (txtPrompt != null)
        {
            txtPrompt.Text = "Move job '" + jobName + "' to group:";
        }

        var cmb = this.FindControl<ComboBox>("CmbGroups");
        if (cmb != null)
        {
            var items = new List<string> { NoneOption };
            foreach (var g in availableGroups)
            {
                if (!string.IsNullOrWhiteSpace(g) && !items.Contains(g))
                {
                    items.Add(g);
                }
            }

            cmb.ItemsSource = items;

            int selectedIndex = 0;
            if (!string.IsNullOrWhiteSpace(currentGroup))
            {
                for (int i = 0; i < items.Count; i++)
                {
                    if (string.Equals(items[i], currentGroup, StringComparison.OrdinalIgnoreCase))
                    {
                        selectedIndex = i;
                        break;
                    }
                }
            }
            cmb.SelectedIndex = selectedIndex;
        }

        var btnOk = this.FindControl<Button>("BtnOk");
        if (btnOk != null)
        {
            btnOk.Click += OnOkClick;
        }

        var btnCancel = this.FindControl<Button>("BtnCancel");
        if (btnCancel != null)
        {
            btnCancel.Click += (_, _) => Close(null);
        }
    }

    private void OnOkClick(object? sender, RoutedEventArgs e)
    {
        var cmb = this.FindControl<ComboBox>("CmbGroups");
        string selected = cmb?.SelectedItem as string ?? NoneOption;

        if (string.Equals(selected, NoneOption, StringComparison.OrdinalIgnoreCase))
        {
            Close("");
        }
        else
        {
            Close(selected);
        }
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}

