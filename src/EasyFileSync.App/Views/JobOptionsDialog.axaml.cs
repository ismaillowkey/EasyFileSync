using System;
using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using EasyFileSync.Core.Engine;
using EasyFileSync.Core.Models;

namespace EasyFileSync.App.Views;

public partial class JobOptionsDialog : Window
{
    private readonly JobConfig? _job;
    private readonly JobStore? _jobStore;

    public JobOptionsDialog()
    {
        InitializeComponent();
    }

    public JobOptionsDialog(JobConfig job, JobStore? jobStore, int initialTabIndex = 1) : this()
    {
        _job = job;
        _jobStore = jobStore;

        Title = "[" + job.Name + "] Options";

        // Inisialisasi Tab Switching
        var listTabs = this.FindControl<ListBox>("ListOptionsTabs");
        if (listTabs != null)
        {
            listTabs.SelectionChanged += OnTabSelectionChanged;
            listTabs.SelectedIndex = initialTabIndex; // Default buka tab Filters seperti di screenshot
        }

        // Muat data General
        var txtName = this.FindControl<TextBox>("TxtJobName");
        if (txtName != null)
        {
            txtName.Text = job.Name;
        }

        var rbTwoWay = this.FindControl<RadioButton>("RbTwoWay");
        var rbLtoR = this.FindControl<RadioButton>("RbLeftToRight");
        var rbRtoL = this.FindControl<RadioButton>("RbRightToLeft");

        if (job.Direction == JobDirection.TwoWay)
        {
            if (rbTwoWay != null)
            {
                rbTwoWay.IsChecked = true;
            }
        }
        else if (job.Direction == JobDirection.LeftToRight)
        {
            if (rbLtoR != null)
            {
                rbLtoR.IsChecked = true;
            }
        }
        else
        {
            if (rbRtoL != null)
            {
                rbRtoL.IsChecked = true;
            }
        }

        // Muat data Compare By
        var rbCompareTime = this.FindControl<RadioButton>("RbCompareTimeAndSize");
        var rbCompareSize = this.FindControl<RadioButton>("RbCompareSizeOnly");
        var rbCompareHash = this.FindControl<RadioButton>("RbCompareChecksum");

        if (job.Options.CompareMode == CompareMode.SizeOnly)
        {
            if (rbCompareSize != null)
            {
                rbCompareSize.IsChecked = true;
            }
        }
        else if (job.Options.CompareMode == CompareMode.Checksum)
        {
            if (rbCompareHash != null)
            {
                rbCompareHash.IsChecked = true;
            }
        }
        else
        {
            if (rbCompareTime != null)
            {
                rbCompareTime.IsChecked = true;
            }
        }

        // Muat data Filters
        var txtInclude = this.FindControl<TextBox>("TxtIncludeFilters");
        if (txtInclude != null)
        {
            txtInclude.Text = string.Join(Environment.NewLine, job.Filter.Include);
        }

        var txtExclude = this.FindControl<TextBox>("TxtExcludeFilters");
        if (txtExclude != null)
        {
            txtExclude.Text = string.Join(Environment.NewLine, job.Filter.Exclude);
        }

        var chkEmpty = this.FindControl<CheckBox>("ChkExcludeEmptyFolders");
        if (chkEmpty != null)
        {
            chkEmpty.IsChecked = job.Filter.ExcludeEmptyFolders;
        }

        var chkHidden = this.FindControl<CheckBox>("ChkExcludeHidden");
        if (chkHidden != null)
        {
            chkHidden.IsChecked = job.Filter.ExcludeHidden;
        }

        var chkSystem = this.FindControl<CheckBox>("ChkExcludeSystem");
        if (chkSystem != null)
        {
            chkSystem.IsChecked = job.Filter.ExcludeSystem;
        }

        var chkTemp = this.FindControl<CheckBox>("ChkExcludeTemp");
        if (chkTemp != null)
        {
            chkTemp.IsChecked = job.Filter.ExcludeTemp;
        }

        // Muat data Analyze & Sync
        var chkRenames = this.FindControl<CheckBox>("ChkDetectRenames");
        if (chkRenames != null)
        {
            chkRenames.IsChecked = job.Options.DetectMovesAndRenames;
        }

        var chkPropagate = this.FindControl<CheckBox>("ChkPropagateDeletions");
        if (chkPropagate != null)
        {
            chkPropagate.IsChecked = job.Options.PropagateDeletions;
        }

        var chkAttributes = this.FindControl<CheckBox>("ChkCopyAttributes");
        if (chkAttributes != null)
        {
            chkAttributes.IsChecked = job.Options.CopyAttributes;
        }

        var chkBackups = this.FindControl<CheckBox>("ChkSaveBackups");
        if (chkBackups != null)
        {
            chkBackups.IsChecked = job.Options.SaveBackups;
        }

        var numParallel = this.FindControl<NumericUpDown>("NumParallelTransfers");
        if (numParallel != null)
        {
            int p = job.Options.ParallelTransfers;
            if (p < 1)
            {
                p = 3;
            }
            else if (p > 10)
            {
                p = 10;
            }
            numParallel.Value = p;
        }

        SetupFilterButtons();

        var btnSave = this.FindControl<Button>("BtnSave");
        if (btnSave != null)
        {
            btnSave.Click += OnSaveClick;
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

    private void OnTabSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        var listTabs = this.FindControl<ListBox>("ListOptionsTabs");
        if (listTabs == null)
        {
            return;
        }

        int index = listTabs.SelectedIndex;

        var panelGeneral = this.FindControl<ScrollViewer>("PanelTabGeneral");
        var panelFilters = this.FindControl<ScrollViewer>("PanelTabFilters");
        var panelAnalyze = this.FindControl<ScrollViewer>("PanelTabAnalyze");
        var panelSync = this.FindControl<ScrollViewer>("PanelTabSync");
        var panelAuto = this.FindControl<ScrollViewer>("PanelTabAuto");

        if (panelGeneral != null)
        {
            panelGeneral.IsVisible = index == 0;
        }
        if (panelFilters != null)
        {
            panelFilters.IsVisible = index == 1;
        }
        if (panelAnalyze != null)
        {
            panelAnalyze.IsVisible = index == 2;
        }
        if (panelSync != null)
        {
            panelSync.IsVisible = index == 3;
        }
        if (panelAuto != null)
        {
            panelAuto.IsVisible = index == 4;
        }
    }

    private void SetupFilterButtons()
    {
        // Tombol Copy/Paste Include
        var btnIncludeCopy = this.FindControl<Button>("BtnIncludeCopy");
        if (btnIncludeCopy != null)
        {
            btnIncludeCopy.Click += async (_, _) =>
            {
                var txt = this.FindControl<TextBox>("TxtIncludeFilters");
                if (txt != null && !string.IsNullOrEmpty(txt.Text))
                {
                    var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
                    if (clipboard != null)
                    {
                        await clipboard.SetTextAsync(txt.Text);
                    }
                }
            };
        }

        var btnIncludePaste = this.FindControl<Button>("BtnIncludePaste");
        if (btnIncludePaste != null)
        {
            btnIncludePaste.Click += async (_, _) =>
            {
                var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
                if (clipboard != null)
                {
                    string? text = await clipboard.TryGetTextAsync();
                    if (!string.IsNullOrEmpty(text))
                    {
                        var txt = this.FindControl<TextBox>("TxtIncludeFilters");
                        if (txt != null)
                        {
                            txt.Text = text;
                        }
                    }
                }
            };
        }

        var btnIncludeAdd = this.FindControl<Button>("BtnIncludeAdd");
        if (btnIncludeAdd != null)
        {
            btnIncludeAdd.Click += (_, _) =>
            {
                var txt = this.FindControl<TextBox>("TxtIncludeFilters");
                if (txt != null)
                {
                    string current = txt.Text ?? "";
                    if (!string.IsNullOrWhiteSpace(current))
                    {
                        txt.Text = current.TrimEnd() + Environment.NewLine;
                    }
                    if (txt.Text != null)
                    {
                        txt.CaretIndex = txt.Text.Length;
                    }
                    txt.Focus();
                }
            };
        }

        var btnIncludeRemove = this.FindControl<Button>("BtnIncludeRemove");
        if (btnIncludeRemove != null)
        {
            btnIncludeRemove.Click += (_, _) =>
            {
                var txt = this.FindControl<TextBox>("TxtIncludeFilters");
                if (txt != null && !string.IsNullOrEmpty(txt.Text))
                {
                    var lines = txt.Text.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries).ToList();
                    if (lines.Count > 0)
                    {
                        lines.RemoveAt(lines.Count - 1);
                        txt.Text = string.Join(Environment.NewLine, lines);
                    }
                }
            };
        }

        // Tombol Copy/Paste Exclude
        var btnExcludeCopy = this.FindControl<Button>("BtnExcludeCopy");
        if (btnExcludeCopy != null)
        {
            btnExcludeCopy.Click += async (_, _) =>
            {
                var txt = this.FindControl<TextBox>("TxtExcludeFilters");
                if (txt != null && !string.IsNullOrEmpty(txt.Text))
                {
                    var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
                    if (clipboard != null)
                    {
                        await clipboard.SetTextAsync(txt.Text);
                    }
                }
            };
        }

        var btnExcludePaste = this.FindControl<Button>("BtnExcludePaste");
        if (btnExcludePaste != null)
        {
            btnExcludePaste.Click += async (_, _) =>
            {
                var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
                if (clipboard != null)
                {
                    string? text = await clipboard.TryGetTextAsync();
                    if (!string.IsNullOrEmpty(text))
                    {
                        var txt = this.FindControl<TextBox>("TxtExcludeFilters");
                        if (txt != null)
                        {
                            txt.Text = text;
                        }
                    }
                }
            };
        }

        var btnExcludeAdd = this.FindControl<Button>("BtnExcludeAdd");
        if (btnExcludeAdd != null)
        {
            btnExcludeAdd.Click += (_, _) =>
            {
                var txt = this.FindControl<TextBox>("TxtExcludeFilters");
                if (txt != null)
                {
                    string current = txt.Text ?? "";
                    if (!string.IsNullOrWhiteSpace(current))
                    {
                        txt.Text = current.TrimEnd() + Environment.NewLine;
                    }
                    if (txt.Text != null)
                    {
                        txt.CaretIndex = txt.Text.Length;
                    }
                    txt.Focus();
                }
            };
        }

        var btnExcludeRemove = this.FindControl<Button>("BtnExcludeRemove");
        if (btnExcludeRemove != null)
        {
            btnExcludeRemove.Click += (_, _) =>
            {
                var txt = this.FindControl<TextBox>("TxtExcludeFilters");
                if (txt != null && !string.IsNullOrEmpty(txt.Text))
                {
                    var lines = txt.Text.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries).ToList();
                    if (lines.Count > 0)
                    {
                        lines.RemoveAt(lines.Count - 1);
                        txt.Text = string.Join(Environment.NewLine, lines);
                    }
                }
            };
        }

        var btnFilterHelp = this.FindControl<Button>("BtnFilterHelp");
        if (btnFilterHelp != null)
        {
            btnFilterHelp.Click += async (_, _) =>
            {
                var dlg = new FilterHelpDialog();
                await dlg.ShowDialog(this);
            };
        }

        var linkFilterHelp = this.FindControl<TextBlock>("LinkFilterHelp");
        if (linkFilterHelp != null)
        {
            linkFilterHelp.PointerPressed += async (_, _) =>
            {
                var dlg = new FilterHelpDialog();
                await dlg.ShowDialog(this);
            };
        }

        var linkProgramOptions = this.FindControl<TextBlock>("LinkProgramOptions");
        if (linkProgramOptions != null)
        {
            linkProgramOptions.PointerPressed += async (_, _) =>
            {
                var dlg = new AboutDialog();
                await dlg.ShowDialog(this);
            };
        }
    }

    private void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        if (_job == null)
        {
            Close(false);
            return;
        }

        // 1. Simpan Nama Job
        var txtName = this.FindControl<TextBox>("TxtJobName");
        if (txtName != null)
        {
            string newName = txtName.Text?.Trim() ?? "";
            if (!string.IsNullOrWhiteSpace(newName))
            {
                if (!string.Equals(newName, _job.Name, StringComparison.OrdinalIgnoreCase))
                {
                    if (_jobStore != null && _jobStore.NameExists(newName))
                    {
                        // Nama sudah dipakai job lain
                        return;
                    }

                    if (_jobStore != null)
                    {
                        _jobStore.Rename(_job.Name, newName);
                    }
                    _job.Name = newName;
                }
            }
        }

        // 2. Simpan Arah Job
        var rbTwoWay = this.FindControl<RadioButton>("RbTwoWay");
        var rbLtoR = this.FindControl<RadioButton>("RbLeftToRight");

        if (rbTwoWay != null && rbTwoWay.IsChecked == true)
        {
            _job.Direction = JobDirection.TwoWay;
        }
        else if (rbLtoR != null && rbLtoR.IsChecked == true)
        {
            _job.Direction = JobDirection.LeftToRight;
        }
        else
        {
            _job.Direction = JobDirection.RightToLeft;
        }

        // 3. Simpan Compare By (CompareMode)
        var rbCompareSize = this.FindControl<RadioButton>("RbCompareSizeOnly");
        var rbCompareHash = this.FindControl<RadioButton>("RbCompareChecksum");

        if (rbCompareSize != null && rbCompareSize.IsChecked == true)
        {
            _job.Options.CompareMode = CompareMode.SizeOnly;
        }
        else if (rbCompareHash != null && rbCompareHash.IsChecked == true)
        {
            _job.Options.CompareMode = CompareMode.Checksum;
        }
        else
        {
            _job.Options.CompareMode = CompareMode.SizeAndTimestamp;
        }

        // 4. Simpan Filters
        var txtInclude = this.FindControl<TextBox>("TxtIncludeFilters");
        if (txtInclude != null)
        {
            string text = txtInclude.Text ?? "";
            string[] lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            _job.Filter.Include.Clear();
            foreach (var line in lines)
            {
                string trimmed = line.Trim();
                if (!string.IsNullOrWhiteSpace(trimmed))
                {
                    _job.Filter.Include.Add(trimmed);
                }
            }
        }

        var txtExclude = this.FindControl<TextBox>("TxtExcludeFilters");
        if (txtExclude != null)
        {
            string text = txtExclude.Text ?? "";
            string[] lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            _job.Filter.Exclude.Clear();
            foreach (var line in lines)
            {
                string trimmed = line.Trim();
                if (!string.IsNullOrWhiteSpace(trimmed))
                {
                    _job.Filter.Exclude.Add(trimmed);
                }
            }
        }

        var chkEmpty = this.FindControl<CheckBox>("ChkExcludeEmptyFolders");
        if (chkEmpty != null)
        {
            _job.Filter.ExcludeEmptyFolders = chkEmpty.IsChecked == true;
        }

        var chkHidden = this.FindControl<CheckBox>("ChkExcludeHidden");
        if (chkHidden != null)
        {
            _job.Filter.ExcludeHidden = chkHidden.IsChecked == true;
        }

        var chkSystem = this.FindControl<CheckBox>("ChkExcludeSystem");
        if (chkSystem != null)
        {
            _job.Filter.ExcludeSystem = chkSystem.IsChecked == true;
        }

        var chkTemp = this.FindControl<CheckBox>("ChkExcludeTemp");
        if (chkTemp != null)
        {
            _job.Filter.ExcludeTemp = chkTemp.IsChecked == true;
        }

        // 5. Simpan Opsi Analyze & Sync
        var chkRenames = this.FindControl<CheckBox>("ChkDetectRenames");
        if (chkRenames != null)
        {
            _job.Options.DetectMovesAndRenames = chkRenames.IsChecked == true;
        }

        var chkPropagate = this.FindControl<CheckBox>("ChkPropagateDeletions");
        if (chkPropagate != null)
        {
            _job.Options.PropagateDeletions = chkPropagate.IsChecked == true;
        }

        var chkAttributes = this.FindControl<CheckBox>("ChkCopyAttributes");
        if (chkAttributes != null)
        {
            _job.Options.CopyAttributes = chkAttributes.IsChecked == true;
        }

        var chkBackups = this.FindControl<CheckBox>("ChkSaveBackups");
        if (chkBackups != null)
        {
            _job.Options.SaveBackups = chkBackups.IsChecked == true;
        }

        var numParallel = this.FindControl<NumericUpDown>("NumParallelTransfers");
        if (numParallel != null && numParallel.Value.HasValue)
        {
            int p = (int)numParallel.Value.Value;
            if (p < 1)
            {
                p = 1;
            }
            else if (p > 10)
            {
                p = 10;
            }
            _job.Options.ParallelTransfers = p;
        }

        // Simpan langsung ke database SQLite
        if (_jobStore != null)
        {
            _jobStore.Update(_job);
            _jobStore.Save();
        }

        Close(true);
    }
}
