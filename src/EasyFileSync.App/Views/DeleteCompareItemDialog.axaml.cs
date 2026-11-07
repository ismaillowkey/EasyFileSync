using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace EasyFileSync.App.Views;

public enum DeleteTargetSide
{
    None,
    LeftOnly,
    RightOnly,
    Both
}

public class DeleteCompareItemResult
{
    public bool Confirmed { get; set; }
    public DeleteTargetSide TargetSide { get; set; } = DeleteTargetSide.None;
}

public partial class DeleteCompareItemDialog : Window
{
    private readonly bool _hasLeft = false;
    private readonly bool _hasRight = false;

    public DeleteCompareItemDialog()
    {
        InitializeComponent();
    }

    public DeleteCompareItemDialog(string itemName, bool isDirectory, bool hasLeft, bool hasRight) : this()
    {
        _hasLeft = hasLeft;
        _hasRight = hasRight;

        string itemType = "file";
        if (isDirectory)
        {
            itemType = "folder";
        }

        var txtTitle = this.FindControl<TextBlock>("TxtTitlePrompt");
        if (txtTitle != null)
        {
            txtTitle.Text = "Are you sure you want to delete this " + itemType + "?";
        }

        var txtName = this.FindControl<TextBlock>("TxtItemName");
        if (txtName != null)
        {
            txtName.Text = itemName;
        }

        var panelChoice = this.FindControl<StackPanel>("PanelChoice");
        var txtSingleNotice = this.FindControl<TextBlock>("TxtSingleSideNotice");

        if (hasLeft && hasRight)
        {
            if (panelChoice != null)
            {
                panelChoice.IsVisible = true;
            }
            if (txtSingleNotice != null)
            {
                txtSingleNotice.IsVisible = false;
            }
        }
        else
        {
            if (panelChoice != null)
            {
                panelChoice.IsVisible = false;
            }
            if (txtSingleNotice != null)
            {
                txtSingleNotice.IsVisible = true;
                if (hasLeft)
                {
                    txtSingleNotice.Text = "This " + itemType + " will be deleted from the Left folder.";
                }
                else
                {
                    txtSingleNotice.Text = "This " + itemType + " will be deleted from the Right folder.";
                }
            }
        }

        var btnDelete = this.FindControl<Button>("BtnDelete");
        if (btnDelete != null)
        {
            btnDelete.Click += OnDeleteClick;
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

    private void OnDeleteClick(object? sender, RoutedEventArgs e)
    {
        DeleteTargetSide side = DeleteTargetSide.None;

        if (_hasLeft && _hasRight)
        {
            var rbBoth = this.FindControl<RadioButton>("RbDeleteBoth");
            var rbLeft = this.FindControl<RadioButton>("RbDeleteLeft");
            var rbRight = this.FindControl<RadioButton>("RbDeleteRight");

            if (rbLeft != null && rbLeft.IsChecked == true)
            {
                side = DeleteTargetSide.LeftOnly;
            }
            else if (rbRight != null && rbRight.IsChecked == true)
            {
                side = DeleteTargetSide.RightOnly;
            }
            else
            {
                side = DeleteTargetSide.Both;
            }
        }
        else if (_hasLeft)
        {
            side = DeleteTargetSide.LeftOnly;
        }
        else if (_hasRight)
        {
            side = DeleteTargetSide.RightOnly;
        }

        var result = new DeleteCompareItemResult
        {
            Confirmed = true,
            TargetSide = side
        };

        Close(result);
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        Close(new DeleteCompareItemResult { Confirmed = false, TargetSide = DeleteTargetSide.None });
    }
}

