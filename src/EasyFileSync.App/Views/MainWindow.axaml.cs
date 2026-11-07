using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using EasyFileSync.Core.Engine;
using EasyFileSync.Core.Models;
using EasyFileSync.Core.Storage;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EasyFileSync.App.Views;

public partial class MainWindow : Window
{
    private JobStore? _jobStore;
    private SyncEngine? _syncEngine;
    private AutoRunner? _autoRunner;
    private JobConfig? _currentJob;

    // State saat berada di Folder Select (Browse) Mode
    private bool _isBrowseMode = false;
    private string _initialLeftPath = "";
    private string _initialRightPath = "";
    private string _selectedLeftPath = "";
    private string _selectedRightPath = "";
    private System.Threading.CancellationTokenSource? _activeLoadingCts;
    private System.Threading.CancellationTokenSource? _activeAnalysisCts;
    private SyncPlan? _currentPlan;

    private enum ActiveView
    {
        Tutorial,
        Browse,
        Analyzing,
        AnalyzeResult
    }

    /// <summary>
    /// Menyimpan state visual, hasil analisa, dan status eksekusi secara independen per masing-masing job.
    /// Hal ini mencegah data hilang atau tampilan reset ke tutorial saat pengguna berpindah antar job di sidebar.
    /// </summary>
    private class JobUiState
    {
        public SyncPlan? Plan { get; set; }
        public List<SyncActionViewModel> ComparisonRoots { get; set; } = new();
        public ActiveView View { get; set; } = ActiveView.Tutorial;

        // Status dan visualisasi saat proses analisa berjalan
        public bool IsAnalyzing { get; set; }
        public CancellationTokenSource? AnalysisCts { get; set; }
        public string AnalyzePercentText { get; set; } = "01% analyzing";
        public string AnalyzeStatusText { get; set; } = "";
        public int AnalyzeLeftFolders { get; set; }
        public int AnalyzeLeftFiles { get; set; }
        public int AnalyzeRightFolders { get; set; }
        public int AnalyzeRightFiles { get; set; }
        public double AnalyzeLeftProgress { get; set; }
        public double AnalyzeRightProgress { get; set; }
        public string SummaryText { get; set; } = "";
        public string DetailsText { get; set; } = "";
        public string OperationStatsText { get; set; } = "";

        // Status saat proses sinkronisasi paralel berjalan
        public bool IsSyncing { get; set; }
        public CancellationTokenSource? SyncCts { get; set; }

        // Status notifikasi banner saat analisa/sinkronisasi selesai atau idle
        public MainJobState BannerState { get; set; } = MainJobState.LastSync;
        public string BannerMessage { get; set; } = "";
    }

    private readonly Dictionary<string, JobUiState> _jobUiStates = new(StringComparer.OrdinalIgnoreCase);

    private JobUiState GetOrCreateUiState(string jobName)
    {
        if (!_jobUiStates.TryGetValue(jobName, out var state))
        {
            state = new JobUiState();
            _jobUiStates[jobName] = state;
        }
        return state;
    }

    public ObservableCollection<JobConfig> JobList { get; } = new();
    public ObservableCollection<JobSidebarItem> SidebarItems { get; } = new();
    private readonly HashSet<string> _collapsedGroups = new(StringComparer.OrdinalIgnoreCase);
    public ObservableCollection<FsTreeNode> LeftTreeNodes { get; } = new();
    public ObservableCollection<FsTreeNode> RightTreeNodes { get; } = new();
    public ObservableCollection<SyncActionViewModel> AnalyzeActions { get; } = new();
    private List<SyncActionViewModel> _comparisonRoots = new();
    public ObservableCollection<TransferTaskViewModel> TransferTasks { get; } = new();
    private bool _isExplicitExit = false;

    private TrayIcon? _trayIcon;

    /// <summary>
    /// Inisialisasi ikon system tray (notification area) di Windows.
    /// </summary>
    private void SetupSystemTray()
    {
        try
        {
            var iconUri = new Uri("avares://EasyFileSync/Assets/app.ico");
            var windowIcon = new WindowIcon(Avalonia.Platform.AssetLoader.Open(iconUri));

            // Set ikon jendela utama juga
            Icon = windowIcon;

            _trayIcon = new TrayIcon
            {
                Icon = windowIcon,
                ToolTipText = "EasyFileSync",
                IsVisible = true
            };

            // Klik pada ikon tray akan memunculkan kembali jendela EasyFileSync
            _trayIcon.Clicked += (s, e) =>
            {
                RestoreFromTray();
            };

            // Menu klik kanan pada ikon tray
            var menu = new NativeMenu();

            var itemOpen = new NativeMenuItem("Open EasyFileSync");
            itemOpen.Click += (s, e) =>
            {
                RestoreFromTray();
            };
            menu.Items.Add(itemOpen);

            menu.Items.Add(new NativeMenuItemSeparator());

            var itemExit = new NativeMenuItem("Exit");
            itemExit.Click += (s, e) =>
            {
                _isExplicitExit = true;
                Close();
            };
            menu.Items.Add(itemExit);

            _trayIcon.Menu = menu;

            var trayIcons = new TrayIcons { _trayIcon };
            if (Application.Current != null)
            {
                TrayIcon.SetIcons(Application.Current, trayIcons);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("SetupSystemTray failed: " + ex.Message);
        }
    }

    private WindowState _previousWindowState = WindowState.Maximized;
    private TrayNotificationToast? _activeToast;

    /// <summary>
    /// Menampilkan notifikasi pop-up melayang di pojok kanan bawah (di atas system tray)
    /// yang memberi tahu pengguna bahwa aplikasi tetap aktif bekerja di latar belakang.
    /// </summary>
    private void ShowTrayNotification(string message)
    {
        try
        {
            if (_activeToast != null)
            {
                _activeToast.Close();
                _activeToast = null;
            }

            _activeToast = new TrayNotificationToast(message, RestoreFromTray);
            _activeToast.Closed += (sender, e) =>
            {
                _activeToast = null;
            };
            _activeToast.Show();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("ShowTrayNotification failed: " + ex.Message);
        }
    }

    /// <summary>
    /// Memulihkan tampilan jendela EasyFileSync dari system tray ke layar utama.
    /// </summary>
    private void RestoreFromTray()
    {
        if (_activeToast != null)
        {
            _activeToast.Close();
            _activeToast = null;
        }

        Show();
        WindowState = _previousWindowState;
        Activate();
    }

    /// <summary>
    /// Event saat jendela akan ditutup.
    /// Jika pengguna mengklik tombol Close (X), sembunyikan jendela ke system tray
    /// dan tampilkan notifikasi bahwa aplikasi tetap bekerja di background.
    /// </summary>
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (!_isExplicitExit)
        {
            e.Cancel = true;
            if (WindowState != WindowState.Minimized)
            {
                _previousWindowState = WindowState;
            }

            // Sembunyikan jendela dari taskbar dan desktop sehingga berpindah ke system tray
            Hide();

            // Tampilkan notifikasi balon / toast di atas system tray
            ShowTrayNotification("EasyFileSync is running in the background.\nClick the tray icon to restore.");
            return;
        }

        if (_activeToast != null)
        {
            _activeToast.Close();
            _activeToast = null;
        }

        // Lepas langganan event trigger dari AutoRunner
        if (_autoRunner != null)
        {
            _autoRunner.JobTriggered -= OnAutoJobTriggered;
        }

        // Hapus dan bersihkan icon dari system tray saat aplikasi keluar penuh
        if (_trayIcon != null)
        {
            _trayIcon.IsVisible = false;
            _trayIcon.Dispose();
            _trayIcon = null;
        }

        base.OnClosing(e);
    }

    /// <summary>
    /// Menutup aplikasi secara penuh saat dipilih dari menu Job -> Exit.
    /// </summary>
    private void OnMenuExitClick(object? sender, RoutedEventArgs e)
    {
        _isExplicitExit = true;
        Close();
    }

    public MainWindow()
    {
        InitializeComponent();
    }

    public MainWindow(JobStore jobStore, SyncEngine syncEngine, AutoRunner autoRunner) : this()
    {
        _jobStore = jobStore;
        _syncEngine = syncEngine;
        _autoRunner = autoRunner;

        if (_autoRunner != null)
        {
            _autoRunner.JobTriggered += OnAutoJobTriggered;
            _autoRunner.Note += (cat, msg) => AppendDebugLog(cat, msg);
        }

        SetupSystemTray();
        SetupEvents();
        LoadJobs();
        UpdateAutoRunMenuDisplay();
        AppendDebugLog("System", "EasyFileSync v0.5.0 initialized.");
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private void SetupEvents()
    {
        // Hubungkan event klik tombol New Job di toolbar
        var btnNewJob = this.FindControl<Button>("BtnNewJob");
        if (btnNewJob != null)
        {
            btnNewJob.Click += OnNewJobClicked;
        }

        // Hubungkan event klik tombol New Group di toolbar
        var btnNewGroup = this.FindControl<Button>("BtnNewGroup");
        if (btnNewGroup != null)
        {
            btnNewGroup.Click += OnNewGroupClicked;
        }

        // Hubungkan event klik Apply dan Cancel pada mode Browse
        var btnApply = this.FindControl<Button>("BtnApply");
        if (btnApply != null)
        {
            btnApply.Click += OnApplyClicked;
        }

        var btnCancel = this.FindControl<Button>("BtnCancel");
        if (btnCancel != null)
        {
            btnCancel.Click += OnCancelClicked;
        }

        // Hubungkan event klik Kotak Kiri dan Kanan untuk masuk ke Browse Mode
        var boxLeft = this.FindControl<Border>("BoxLeftFolder");
        if (boxLeft != null)
        {
            boxLeft.PointerPressed += OnLeftFolderBoxPressed;
        }

        var boxRight = this.FindControl<Border>("BoxRightFolder");
        if (boxRight != null)
        {
            boxRight.PointerPressed += OnRightFolderBoxPressed;
        }

        // Hubungkan event perubahan seleksi job pada sidebar (SidebarItems mendukung pengelompokan Group)
        var listJobs = this.FindControl<ListBox>("ListJobs");
        if (listJobs != null)
        {
            listJobs.ItemsSource = SidebarItems;
            listJobs.SelectionChanged += OnJobSelectionChanged;
        }

        // Hubungkan TreeView Kiri dan Kanan
        var treeLeft = this.FindControl<TreeView>("TreeLeft");
        if (treeLeft != null)
        {
            treeLeft.ItemsSource = LeftTreeNodes;
            treeLeft.SelectionChanged += OnLeftTreeSelectionChanged;
        }

        var treeRight = this.FindControl<TreeView>("TreeRight");
        if (treeRight != null)
        {
            treeRight.ItemsSource = RightTreeNodes;
            treeRight.SelectionChanged += OnRightTreeSelectionChanged;
        }

        // Hubungkan event input teks manual / paste pada Address Bar Kiri
        var txtLeftAddress = this.FindControl<TextBox>("TxtLeftAddress");
        if (txtLeftAddress != null)
        {
            txtLeftAddress.KeyDown += OnLeftAddressKeyDown;
            txtLeftAddress.LostFocus += OnLeftAddressLostFocus;
        }

        // Hubungkan event input teks manual / paste pada Address Bar Kanan
        var txtRightAddress = this.FindControl<TextBox>("TxtRightAddress");
        if (txtRightAddress != null)
        {
            txtRightAddress.KeyDown += OnRightAddressKeyDown;
            txtRightAddress.LostFocus += OnRightAddressLostFocus;
        }

        // Hubungkan event klik tombol Analyze
        var btnAnalyze = this.FindControl<Button>("BtnAnalyze");
        if (btnAnalyze != null)
        {
            btnAnalyze.Click += OnAnalyzeClicked;
        }

        // Hubungkan event klik tombol Sync di toolbar
        var btnSync = this.FindControl<Button>("BtnSync");
        if (btnSync != null)
        {
            btnSync.Click += async (s, e) =>
            {
                if (_currentJob != null && _currentPlan != null)
                {
                    await ExecuteGroupedSyncAsync(_currentJob, _currentPlan);
                }
            };
        }

        // Hubungkan event klik tombol Options di toolbar
        var btnOptions = this.FindControl<Button>("BtnOptions");
        if (btnOptions != null)
        {
            btnOptions.Click += (s, e) =>
            {
                OnJobContextOptionsClick(s, e);
            };
        }

        // Hubungkan event klik tombol Auto di toolbar
        var btnAuto = this.FindControl<Button>("BtnAuto");
        if (btnAuto != null)
        {
            btnAuto.Click += OnAutoButtonClicked;
        }

        // Hubungkan event klik tombol Stop
        var btnStop = this.FindControl<Button>("BtnStop");
        if (btnStop != null)
        {
            btnStop.Click += OnStopClicked;
        }

        // Hubungkan ListBox hasil analisa
        var listAnalyze = this.FindControl<ListBox>("ListAnalyzeActions");
        if (listAnalyze != null)
        {
            listAnalyze.ItemsSource = AnalyzeActions;
        }

        // Hubungkan ListBox antrean tugas transfer (1 row = 1 process)
        var listTasks = this.FindControl<ListBox>("ListTransferTasks");
        if (listTasks != null)
        {
            listTasks.ItemsSource = TransferTasks;
        }

        // Hubungkan event tombol Cancel pada Loading Overlay Modal
        var btnCancelLoading = this.FindControl<Button>("BtnCancelLoading");
        if (btnCancelLoading != null)
        {
            btnCancelLoading.Click += (_, _) =>
            {
                if (_activeLoadingCts != null)
                {
                    _activeLoadingCts.Cancel();
                }
                HideLoadingOverlay();
            };
        }

        // Hubungkan event dropdown Operations (Beyond Compare style)
        var cmbOps = this.FindControl<ComboBox>("CmbOperations");
        if (cmbOps != null)
        {
            cmbOps.SelectionChanged += OnOperationSelectionChanged;
        }

        // Hubungkan event tombol filter dialog dan input cepat Quick Filter
        var btnOpenFilters = this.FindControl<Button>("BtnOpenFilters");
        if (btnOpenFilters != null)
        {
            btnOpenFilters.Click += OnOpenFiltersClicked;
        }

        var txtQuickFilter = this.FindControl<TextBox>("TxtQuickFilter");
        if (txtQuickFilter != null)
        {
            txtQuickFilter.KeyDown += OnQuickFilterKeyDown;
            txtQuickFilter.LostFocus += OnQuickFilterLostFocus;
        }

        // Hubungkan event klik tombol Swap / Swipe Folder Kiri <-> Kanan
        var btnSwap = this.FindControl<Button>("BtnSwapFolders");
        if (btnSwap != null)
        {
            btnSwap.Click += OnSwapFoldersClicked;
        }
    }

    /// <summary>
    /// Memuat daftar job dari JobStore (SQLite) ke sidebar dengan dukungan Folder Grouping hierarkis.
    /// </summary>
    public void LoadJobs()
    {
        if (_jobStore == null)
        {
            return;
        }

        // Sinkronkan daftar job mentah (JobList) untuk kompatibilitas menu batch
        JobList.Clear();
        foreach (var job in _jobStore.Jobs)
        {
            JobList.Add(job);
        }

        // Ambil daftar grup tersimpan dari database SQLite
        var allGroups = _jobStore.GetGroups();

        // Pastikan grup yang dipakai oleh job tetapi belum terdaftar ikut dimasukkan
        foreach (var job in _jobStore.Jobs)
        {
            if (!string.IsNullOrWhiteSpace(job.Group))
            {
                bool exists = false;
                foreach (var g in allGroups)
                {
                    if (string.Equals(g, job.Group, StringComparison.OrdinalIgnoreCase))
                    {
                        exists = true;
                        break;
                    }
                }
                if (!exists)
                {
                    allGroups.Add(job.Group);
                }
            }
        }

        SidebarItems.Clear();

        // 1. Tambahkan setiap grup dan seluruh job di dalamnya
        foreach (var grp in allGroups)
        {
            var memberJobs = _jobStore.Jobs
                .Where(j => string.Equals(j.Group, grp, StringComparison.OrdinalIgnoreCase))
                .ToList();

            bool isExpanded = !_collapsedGroups.Contains(grp);

            var groupHeader = new JobSidebarItem
            {
                IsGroup = true,
                GroupName = grp,
                JobCount = memberJobs.Count,
                IsExpanded = isExpanded,
                IsVisible = true
            };
            SidebarItems.Add(groupHeader);

            foreach (var mJob in memberJobs)
            {
                var jobItem = new JobSidebarItem
                {
                    IsGroup = false,
                    Job = mJob,
                    Name = mJob.Name,
                    GroupName = grp,
                    IsVisible = isExpanded
                };
                SidebarItems.Add(jobItem);
            }
        }

        // 2. Tambahkan job yang berada di level Root (tanpa grup)
        var rootJobs = _jobStore.Jobs
            .Where(j => string.IsNullOrWhiteSpace(j.Group))
            .ToList();

        foreach (var rJob in rootJobs)
        {
            var jobItem = new JobSidebarItem
            {
                IsGroup = false,
                Job = rJob,
                Name = rJob.Name,
                GroupName = "",
                IsVisible = true
            };
            SidebarItems.Add(jobItem);
        }

        // 3. Pulihkan seleksi job aktif
        if (_currentJob != null)
        {
            SelectJobInSidebar(_currentJob);
        }
        else if (_jobStore.Jobs.Count > 0)
        {
            var firstJob = _jobStore.Jobs[0];
            SelectJobInSidebar(firstJob);
            SelectJob(firstJob);
        }
    }

    /// <summary>
    /// Memilih item job pada sidebar (ListJobs) berdasarkan objek JobConfig.
    /// </summary>
    public void SelectJobInSidebar(JobConfig? job)
    {
        if (job == null)
        {
            return;
        }

        var listJobs = this.FindControl<ListBox>("ListJobs");
        if (listJobs == null)
        {
            return;
        }

        foreach (var item in SidebarItems)
        {
            if (!item.IsGroup && item.Job != null)
            {
                if (string.Equals(item.Job.Name, job.Name, StringComparison.OrdinalIgnoreCase))
                {
                    listJobs.SelectedItem = item;
                    return;
                }
            }
        }
    }

    /// <summary>
    /// Membuka dialog New Job untuk membuat job baru.
    /// </summary>
    private async void OnNewJobClicked(object? sender, RoutedEventArgs e)
    {
        if (_jobStore == null)
        {
            return;
        }

        string defaultName = _jobStore.UniqueName("New Job");
        var dialog = new NewJobWindow(defaultName, _jobStore);
        var result = await dialog.ShowDialog<NewJobResult?>(this);

        if (result != null)
        {
            var newJob = new JobConfig
            {
                Name = result.Name,
                Direction = result.Direction
            };

            _jobStore.Add(newJob);
            _jobStore.Save();

            LoadJobs();
            SelectJobInSidebar(newJob);
            SelectJob(newJob);

            // Langsung buka Folder Select Mode agar pengguna dapat memilih folder kiri & kanan
            EnterBrowseMode();
        }
    }

    /// <summary>
    /// Membuka dialog New Group untuk membuat grup job baru.
    /// </summary>
    private async void OnNewGroupClicked(object? sender, RoutedEventArgs e)
    {
        if (_jobStore == null)
        {
            return;
        }

        var existingGroups = _jobStore.GetGroups();
        string defaultName = GenerateUniqueGroupName("New Group", existingGroups);

        var dialog = new NewGroupDialog(defaultName, _jobStore);
        var groupName = await dialog.ShowDialog<string?>(this);

        if (!string.IsNullOrWhiteSpace(groupName))
        {
            _jobStore.AddGroup(groupName.Trim());
            LoadJobs();
            AppendDebugLog("Group", "Created new group: " + groupName.Trim());
        }
    }

    private string GenerateUniqueGroupName(string baseName, List<string> existing)
    {
        bool exists = false;
        foreach (var name in existing)
        {
            if (string.Equals(name, baseName, StringComparison.OrdinalIgnoreCase))
            {
                exists = true;
                break;
            }
        }

        if (!exists)
        {
            return baseName;
        }

        int counter = 2;
        while (true)
        {
            string candidate = baseName + " " + counter;
            bool candidateExists = false;
            foreach (var name in existing)
            {
                if (string.Equals(name, candidate, StringComparison.OrdinalIgnoreCase))
                {
                    candidateExists = true;
                    break;
                }
            }
            if (!candidateExists)
            {
                return candidate;
            }
            counter++;
        }
    }

    /// <summary>
    /// Event saat seleksi sidebar ListJobs berubah.
    /// Menangani klik header group untuk melipat/membuka, atau seleksi job untuk berganti halaman.
    /// </summary>
    private void OnJobSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        var listJobs = this.FindControl<ListBox>("ListJobs");
        if (listJobs == null)
        {
            return;
        }

        var selected = listJobs.SelectedItem as JobSidebarItem;
        if (selected == null)
        {
            return;
        }

        // 1. Jika pengguna mengklik header Group: toggle lipat/buka folder group
        if (selected.IsGroup)
        {
            ToggleGroupExpanded(selected);

            // Kembalikan seleksi visual ke job aktif saat ini agar header group tidak tertahan sebagai job
            if (_currentJob != null)
            {
                SelectJobInSidebar(_currentJob);
            }
            return;
        }

        // 2. Jika pengguna mengklik baris Job
        if (selected.Job != null)
        {
            // Jika sedang dalam browse mode, batalkan dulu sebelum berganti job
            if (_isBrowseMode)
            {
                ExitBrowseMode();
            }

            SelectJob(selected.Job);
        }
    }

    /// <summary>
    /// Melipat atau membuka folder grup pada sidebar.
    /// </summary>
    private void ToggleGroupExpanded(JobSidebarItem groupItem)
    {
        if (!groupItem.IsGroup || string.IsNullOrWhiteSpace(groupItem.GroupName))
        {
            return;
        }

        bool newExpanded = !groupItem.IsExpanded;
        groupItem.IsExpanded = newExpanded;

        if (newExpanded)
        {
            _collapsedGroups.Remove(groupItem.GroupName);
        }
        else
        {
            _collapsedGroups.Add(groupItem.GroupName);
        }

        foreach (var item in SidebarItems)
        {
            if (!item.IsGroup && string.Equals(item.GroupName, groupItem.GroupName, StringComparison.OrdinalIgnoreCase))
            {
                item.IsVisible = newExpanded;
            }
        }
    }

    /// <summary>
    /// Menu Klik Kanan Grup: New Job in this Group...
    /// </summary>
    private async void OnGroupContextNewJobClick(object? sender, RoutedEventArgs e)
    {
        if (_jobStore == null)
        {
            return;
        }

        string? groupName = GetGroupNameFromSender(sender);
        string defaultName = _jobStore.UniqueName("New Job");
        var dialog = new NewJobWindow(defaultName, _jobStore);
        var result = await dialog.ShowDialog<NewJobResult?>(this);

        if (result != null)
        {
            string assignedGroup = "";
            if (!string.IsNullOrWhiteSpace(groupName))
            {
                assignedGroup = groupName;
            }

            var newJob = new JobConfig
            {
                Name = result.Name,
                Direction = result.Direction,
                Group = assignedGroup
            };

            _jobStore.Add(newJob);
            _jobStore.Save();

            LoadJobs();
            SelectJobInSidebar(newJob);
            SelectJob(newJob);
            EnterBrowseMode();
        }
    }

    /// <summary>
    /// Menu Klik Kanan Grup: Rename Group...
    /// </summary>
    private async void OnGroupContextRenameClick(object? sender, RoutedEventArgs e)
    {
        if (_jobStore == null)
        {
            return;
        }

        string? oldName = GetGroupNameFromSender(sender);
        if (string.IsNullOrWhiteSpace(oldName))
        {
            return;
        }

        var existingGroups = _jobStore.GetGroups();
        var dialog = new RenameJobDialog(oldName, existingGroups);
        var newName = await dialog.ShowDialog<string?>(this);

        if (!string.IsNullOrWhiteSpace(newName) && !string.Equals(oldName, newName, StringComparison.OrdinalIgnoreCase))
        {
            _jobStore.RenameGroup(oldName, newName.Trim());
            LoadJobs();
            AppendDebugLog("Group", "Renamed group '" + oldName + "' to '" + newName.Trim() + "'");
        }
    }

    /// <summary>
    /// Menu Klik Kanan Grup: Delete Group
    /// </summary>
    private async void OnGroupContextDeleteClick(object? sender, RoutedEventArgs e)
    {
        if (_jobStore == null)
        {
            return;
        }

        string? groupName = GetGroupNameFromSender(sender);
        if (string.IsNullOrWhiteSpace(groupName))
        {
            return;
        }

        var dialog = new ConfirmDeleteDialog("Delete Group", "Are you sure you want to delete the group '" + groupName + "'?\n\nJobs inside this group will not be deleted; they will be moved to root.");
        var confirm = await dialog.ShowDialog<bool>(this);

        if (confirm)
        {
            _jobStore.DeleteGroup(groupName);
            LoadJobs();
            AppendDebugLog("Group", "Deleted group: " + groupName);
        }
    }

    /// <summary>
    /// Menu Klik Kanan Job: Move to Group...
    /// </summary>
    private async void OnJobContextMoveToGroupClick(object? sender, RoutedEventArgs e)
    {
        if (_jobStore == null)
        {
            return;
        }

        var job = GetJobFromSender(sender);
        if (job == null)
        {
            return;
        }

        var allGroups = _jobStore.GetGroups();
        var dialog = new MoveToGroupDialog(job.Name, job.Group, allGroups);
        var targetGroup = await dialog.ShowDialog<string?>(this);

        if (targetGroup != null)
        {
            job.Group = targetGroup;
            _jobStore.Update(job);
            _jobStore.Save();

            LoadJobs();
            SelectJobInSidebar(job);
            SelectJob(job);

            string dest = "(Root)";
            if (!string.IsNullOrWhiteSpace(targetGroup))
            {
                dest = targetGroup;
            }
            AppendDebugLog("Job", "Moved job '" + job.Name + "' to group: " + dest);
        }
    }

    private string? GetGroupNameFromSender(object? sender)
    {
        var menuItem = sender as MenuItem;
        if (menuItem != null)
        {
            var sidebarItem = menuItem.DataContext as JobSidebarItem;
            if (sidebarItem != null && sidebarItem.IsGroup)
            {
                return sidebarItem.GroupName;
            }
        }
        return null;
    }

    public void SelectJob(JobConfig job)
    {
        // 1. Simpan state job sebelumnya jika ada (jangan timpa plan jika job lama sedang menganalisa atau sinkronisasi)
        if (_currentJob != null && !string.Equals(_currentJob.Name, job.Name, StringComparison.OrdinalIgnoreCase))
        {
            var prevState = GetOrCreateUiState(_currentJob.Name);
            if (!prevState.IsAnalyzing && !prevState.IsSyncing)
            {
                prevState.Plan = _currentPlan;
                prevState.ComparisonRoots = _comparisonRoots.ToList();
                if (_currentPlan != null)
                {
                    prevState.View = ActiveView.AnalyzeResult;
                }
                else
                {
                    prevState.View = ActiveView.Tutorial;
                }
            }
        }

        _currentJob = job;
        Title = "EasyFileSync - " + job.Name;

        _selectedLeftPath = job.Left.Path ?? "";
        _selectedRightPath = job.Right.Path ?? "";

        var curState = GetOrCreateUiState(job.Name);

        var btnAnalyze = this.FindControl<Button>("BtnAnalyze");
        var btnSync = this.FindControl<Button>("BtnSync");
        var btnStop = this.FindControl<Button>("BtnStop");

        // 2. Tampilkan tampilan sesuai state independen job yang dipilih
        if (curState.IsAnalyzing)
        {
            // Job ini sedang berjalan analisanya di background: tampilkan layar progress analisa
            _currentPlan = null;
            _comparisonRoots.Clear();
            AnalyzeActions.Clear();

            SetActiveView(ActiveView.Analyzing);
            RestoreAnalyzeProgressDisplay(curState);

            if (btnAnalyze != null)
            {
                btnAnalyze.IsEnabled = false;
            }
            if (btnSync != null)
            {
                btnSync.IsEnabled = false;
            }
            if (btnStop != null)
            {
                btnStop.IsVisible = true;
            }
        }
        else if (curState.IsSyncing)
        {
            // Job ini sedang berjalan sinkronisasinya
            if (curState.Plan != null)
            {
                _currentPlan = curState.Plan;
                _comparisonRoots = curState.ComparisonRoots.ToList();
                RefreshVisibleAnalyzeActions();
                SetActiveView(ActiveView.AnalyzeResult);
            }

            if (btnAnalyze != null)
            {
                btnAnalyze.IsEnabled = false;
            }
            if (btnSync != null)
            {
                btnSync.IsEnabled = false;
            }
            if (btnStop != null)
            {
                btnStop.IsVisible = true;
            }
        }
        else if (curState.Plan != null)
        {
            // Job ini telah selesai dianalisa sebelumnya: pulihkan hasil analisa lengkap
            _currentPlan = curState.Plan;
            _comparisonRoots = curState.ComparisonRoots.ToList();
            RefreshVisibleAnalyzeActions();
            SetActiveView(ActiveView.AnalyzeResult);

            var txtSummary = this.FindControl<TextBlock>("TxtAnalyzeSummary");
            if (txtSummary != null)
            {
                txtSummary.Text = curState.SummaryText;
            }

            var txtDetails = this.FindControl<TextBlock>("TxtAnalyzeDetails");
            if (txtDetails != null)
            {
                txtDetails.Text = curState.DetailsText;
            }

            var txtOpStats = this.FindControl<TextBlock>("TxtOperationStats");
            if (txtOpStats != null)
            {
                txtOpStats.Text = curState.OperationStatsText;
            }

            if (btnAnalyze != null)
            {
                btnAnalyze.IsEnabled = true;
            }
            if (btnStop != null)
            {
                btnStop.IsVisible = false;
            }
            if (btnSync != null)
            {
                if (curState.Plan.HasChanges || curState.Plan.Actions.Count > 0)
                {
                    btnSync.IsEnabled = true;
                }
                else
                {
                    btnSync.IsEnabled = false;
                }
            }
        }
        else
        {
            // Job ini belum pernah dianalisa: tampilkan layar panduan tutorial
            _currentPlan = null;
            _comparisonRoots.Clear();
            AnalyzeActions.Clear();
            SetActiveView(ActiveView.Tutorial);

            if (btnAnalyze != null)
            {
                btnAnalyze.IsEnabled = true;
            }
            if (btnStop != null)
            {
                btnStop.IsVisible = false;
            }
            if (btnSync != null)
            {
                btnSync.IsEnabled = false;
            }
        }

        UpdateFolderBoxesDisplay();
        UpdateDirectionArrow(job.Direction);
        UpdateOperationsBar(job);
        UpdateAutoButtonDisplay(job);
        LoadFilterToPanel(job);

        // 3. Perbarui Status Banner utama sesuai kondisi independen job
        if (curState.IsAnalyzing)
        {
            SetMainJobStatus(MainJobState.Running, "Analyzing '" + job.Name + "'...");
        }
        else if (curState.IsSyncing)
        {
            SetMainJobStatus(MainJobState.Running, "Synchronizing '" + job.Name + "'...");
        }
        else if (!string.IsNullOrWhiteSpace(curState.BannerMessage))
        {
            SetMainJobStatus(curState.BannerState, curState.BannerMessage);
        }
        else if (job.LastRunUtc != DateTime.MinValue)
        {
            SetMainJobStatus(MainJobState.LastSync, job.LastRunUtc.ToLocalTime().ToString("G") + " Successfully synchronized");
        }
        else
        {
            SetMainJobStatus(MainJobState.LastSync, "No synchronization performed yet.");
        }
    }

    /// <summary>
    /// Memulihkan indikator progress analisa pada layar saat pengguna kembali ke job yang sedang dianalisa.
    /// </summary>
    private void RestoreAnalyzeProgressDisplay(JobUiState state)
    {
        var txtPercent = this.FindControl<TextBlock>("TxtAnalyzePercent");
        var txtStatus = this.FindControl<TextBlock>("TxtAnalyzeStatus");
        var progLeft = this.FindControl<ProgressBar>("ProgAnalyzeLeft");
        var progRight = this.FindControl<ProgressBar>("ProgAnalyzeRight");
        var txtLeftFolders = this.FindControl<TextBlock>("TxtAnalyzeLeftFolders");
        var txtLeftFiles = this.FindControl<TextBlock>("TxtAnalyzeLeftFiles");
        var txtRightFolders = this.FindControl<TextBlock>("TxtAnalyzeRightFolders");
        var txtRightFiles = this.FindControl<TextBlock>("TxtAnalyzeRightFiles");

        if (txtPercent != null)
        {
            txtPercent.Text = state.AnalyzePercentText;
        }
        if (txtStatus != null)
        {
            txtStatus.Text = state.AnalyzeStatusText;
        }
        if (progLeft != null)
        {
            progLeft.Value = state.AnalyzeLeftProgress;
        }
        if (progRight != null)
        {
            progRight.Value = state.AnalyzeRightProgress;
        }
        if (txtLeftFolders != null)
        {
            txtLeftFolders.Text = state.AnalyzeLeftFolders + " Folders";
        }
        if (txtLeftFiles != null)
        {
            txtLeftFiles.Text = state.AnalyzeLeftFiles + " Files";
        }
        if (txtRightFolders != null)
        {
            txtRightFolders.Text = "Folders " + state.AnalyzeRightFolders;
        }
        if (txtRightFiles != null)
        {
            txtRightFiles.Text = "Files " + state.AnalyzeRightFiles;
        }
    }

    private void UpdateDirectionArrow(JobDirection direction)
    {
        var txtArrow = this.FindControl<TextBlock>("TxtDirectionArrow");
        if (txtArrow == null)
        {
            return;
        }

        if (direction == JobDirection.TwoWay)
        {
            txtArrow.Text = "⇄";
        }
        else if (direction == JobDirection.LeftToRight)
        {
            txtArrow.Text = "➔";
        }
        else
        {
            txtArrow.Text = "⬅";
        }
    }

    /// <summary>
    /// Memperbarui indikator tombol Auto pada toolbar kanan berdasarkan konfigurasi job aktif.
    /// </summary>
    private void UpdateAutoButtonDisplay(JobConfig job)
    {
        var imgAutoOn = this.FindControl<Image>("ImgAutoOn");
        var imgAutoOff = this.FindControl<Image>("ImgAutoOff");
        var txtAuto = this.FindControl<TextBlock>("TxtAutoLabel");

        bool isAutoActive = false;
        if (job != null)
        {
            if (job.Auto != null)
            {
                if (job.Auto.AnyEnabled)
                {
                    isAutoActive = true;
                }
            }
        }

        if (imgAutoOn != null)
        {
            imgAutoOn.IsVisible = isAutoActive;
        }

        if (imgAutoOff != null)
        {
            imgAutoOff.IsVisible = !isAutoActive;
        }

        if (txtAuto != null)
        {
            txtAuto.Text = "Auto Sync";
            if (isAutoActive)
            {
                txtAuto.Foreground = Avalonia.Media.Brush.Parse("#2E7D32");
                txtAuto.FontWeight = Avalonia.Media.FontWeight.SemiBold;
            }
            else
            {
                txtAuto.Foreground = Avalonia.Media.Brush.Parse("#333333");
                txtAuto.FontWeight = Avalonia.Media.FontWeight.Normal;
            }
        }

        var btnAuto = this.FindControl<Button>("BtnAuto");
        if (btnAuto != null)
        {
            if (isAutoActive)
            {
                ToolTip.SetTip(btnAuto, "Auto Sync (Enabled)");
            }
            else
            {
                ToolTip.SetTip(btnAuto, "Auto Sync (Disabled)");
            }
        }
    }

    /// <summary>
    /// Memperbarui pilihan dropdown Operations, teks deskripsi, dan isi Quick Filter sesuai konfigurasi job aktif.
    /// </summary>
    private void UpdateOperationsBar(JobConfig job)
    {
        var cmbOps = this.FindControl<ComboBox>("CmbOperations");
        var txtDesc = this.FindControl<TextBlock>("TxtOperationDescription");
        var txtFilter = this.FindControl<TextBox>("TxtQuickFilter");

        if (txtFilter != null)
        {
            if (job.Filter != null && job.Filter.Exclude != null && job.Filter.Exclude.Count > 0)
            {
                txtFilter.Text = string.Join("; ", job.Filter.Exclude);
            }
            else
            {
                txtFilter.Text = "";
            }
        }

        // Tentukan indeks pilihan ComboBox berdasarkan Direction dan PropagateDeletions
        // 0: Update Left, 1: Update Right, 2: Update Both, 3: Mirror to Left, 4: Mirror to Right
        int targetIndex = 1;
        string description = "Copy newer and orphan files/folders from left to right.";

        if (job.Direction == JobDirection.LeftToRight)
        {
            if (job.Options.PropagateDeletions)
            {
                targetIndex = 4;
                description = "Make right folder an exact replica of left folder (propagate deletions).";
            }
            else
            {
                targetIndex = 1;
                description = "Copy newer and orphan files/folders from left to right.";
            }
        }
        else if (job.Direction == JobDirection.RightToLeft)
        {
            if (job.Options.PropagateDeletions)
            {
                targetIndex = 3;
                description = "Make left folder an exact replica of right folder (propagate deletions).";
            }
            else
            {
                targetIndex = 0;
                description = "Copy newer and orphan files/folders from right to left.";
            }
        }
        else
        {
            targetIndex = 2;
            description = "Copy newer files/folders in both directions (Two-Way).";
        }

        if (cmbOps != null)
        {
            // Lepas sementara handler event agar tidak memicu OnOperationSelectionChanged berulang kali
            cmbOps.SelectionChanged -= OnOperationSelectionChanged;
            cmbOps.SelectedIndex = targetIndex;
            cmbOps.SelectionChanged += OnOperationSelectionChanged;
        }

        if (txtDesc != null)
        {
            txtDesc.Text = description;
        }

        // Tampilkan ringkasan status operasi file
        var txtStats = this.FindControl<TextBlock>("TxtOperationStats");
        if (txtStats != null)
        {
            if (_currentPlan == null)
            {
                txtStats.Text = "Ready to analyze";
            }
            else
            {
                string formattedSize = SyncActionViewModel.FormatBytes(_currentPlan.BytesToCopy);
                txtStats.Text = "Copy " + _currentPlan.CopyCount + " file(s), " + formattedSize;
            }
        }
    }

    public enum MainJobState
    {
        LastSync,
        Running,
        Paused,
        Error
    }

    /// <summary>
    /// Memperbarui tampilan Status Banner utama di atas area kerja persis GoodSync:
    /// - LastSync (Abu-abu): Menampilkan riwayat waktu sinkronisasi terakhir
    /// - Running (Hijau): Sedang menganalisa atau menyalin berkas
    /// - Paused / Stopped (Kuning khas GoodSync #FFF9C4): Dihentikan oleh pengguna
    /// - Error (Merah): Terjadi kegagalan proses
    /// </summary>
    public void SetMainJobStatus(MainJobState state, string message, string detail = "")
    {
        if (_currentJob != null)
        {
            var jobState = GetOrCreateUiState(_currentJob.Name);
            jobState.BannerState = state;
            jobState.BannerMessage = message;
        }

        Dispatcher.UIThread.Post(() =>
        {
            var banner = this.FindControl<Border>("BannerJobStatus");
            var txtMsg = this.FindControl<TextBlock>("TxtJobStatusMessage");
            var btnClose = this.FindControl<Button>("BtnCloseJobStatus");

            if (banner == null || txtMsg == null)
            {
                return;
            }

            txtMsg.Text = message;

            // 1. Status Running (Hijau)
            if (state == MainJobState.Running)
            {
                banner.Background = new SolidColorBrush(Color.Parse("#DCFCE7"));
                banner.BorderBrush = new SolidColorBrush(Color.Parse("#86EFAC"));
                txtMsg.Foreground = new SolidColorBrush(Color.Parse("#14532D"));
                if (btnClose != null)
                {
                    btnClose.IsVisible = false;
                }
            }
            // 2. Status Paused / Stopped (Kuning Terang khas GoodSync #FFF9C4)
            else if (state == MainJobState.Paused)
            {
                banner.Background = new SolidColorBrush(Color.Parse("#FFF9C4"));
                banner.BorderBrush = new SolidColorBrush(Color.Parse("#FFF59D"));
                txtMsg.Foreground = new SolidColorBrush(Color.Parse("#212121"));
                if (btnClose != null)
                {
                    btnClose.IsVisible = true;
                }
            }
            // 3. Status Error / Failed (Merah)
            else if (state == MainJobState.Error)
            {
                banner.Background = new SolidColorBrush(Color.Parse("#FEE2E2"));
                banner.BorderBrush = new SolidColorBrush(Color.Parse("#FCA5A5"));
                txtMsg.Foreground = new SolidColorBrush(Color.Parse("#991B1B"));
                if (btnClose != null)
                {
                    btnClose.IsVisible = true;
                }
            }
            // 4. Status Last Sync / Idle (Abu-abu netral)
            else
            {
                banner.Background = new SolidColorBrush(Color.Parse("#F1F5F9"));
                banner.BorderBrush = new SolidColorBrush(Color.Parse("#E2E8F0"));
                txtMsg.Foreground = new SolidColorBrush(Color.Parse("#475569"));
                if (btnClose != null)
                {
                    btnClose.IsVisible = false;
                }
            }
        });
    }

    /// <summary>
    /// Klik tombol silang merah (✕) pada notifikasi banner untuk menutup/mereset notifikasi.
    /// </summary>
    private void OnDismissJobStatusClick(object? sender, RoutedEventArgs e)
    {
        if (_currentJob != null)
        {
            var jobState = GetOrCreateUiState(_currentJob.Name);
            jobState.BannerMessage = "";

            if (_currentJob.LastRunUtc != DateTime.MinValue)
            {
                SetMainJobStatus(MainJobState.LastSync, _currentJob.LastRunUtc.ToLocalTime().ToString("G") + " Successfully synchronized");
            }
            else
            {
                SetMainJobStatus(MainJobState.LastSync, "No synchronization performed yet.");
            }
        }
        else
        {
            SetMainJobStatus(MainJobState.LastSync, "No synchronization performed yet.");
        }
    }

    /// <summary>
    /// Event ketika pengguna mengubah opsi pada dropdown Operations (Beyond Compare style).
    /// </summary>
    private void OnOperationSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_currentJob == null)
        {
            return;
        }

        var cmbOps = sender as ComboBox;
        if (cmbOps == null)
        {
            return;
        }

        var selectedItem = cmbOps.SelectedItem as ComboBoxItem;
        if (selectedItem == null)
        {
            return;
        }

        string tag = selectedItem.Tag?.ToString() ?? "";
        var txtDesc = this.FindControl<TextBlock>("TxtOperationDescription");

        if (tag == "UpdateLeft")
        {
            _currentJob.Direction = JobDirection.RightToLeft;
            _currentJob.Options.PropagateDeletions = false;
            if (txtDesc != null)
            {
                txtDesc.Text = "Copy newer and orphan files/folders from right to left.";
            }
        }
        else if (tag == "UpdateRight")
        {
            _currentJob.Direction = JobDirection.LeftToRight;
            _currentJob.Options.PropagateDeletions = false;
            if (txtDesc != null)
            {
                txtDesc.Text = "Copy newer and orphan files/folders from left to right.";
            }
        }
        else if (tag == "UpdateBoth")
        {
            _currentJob.Direction = JobDirection.TwoWay;
            _currentJob.Options.PropagateDeletions = false;
            if (txtDesc != null)
            {
                txtDesc.Text = "Copy newer files/folders in both directions (Two-Way).";
            }
        }
        else if (tag == "MirrorLeft")
        {
            _currentJob.Direction = JobDirection.RightToLeft;
            _currentJob.Options.PropagateDeletions = true;
            if (txtDesc != null)
            {
                txtDesc.Text = "Make left folder an exact replica of right folder (propagate deletions).";
            }
        }
        else if (tag == "MirrorRight")
        {
            _currentJob.Direction = JobDirection.LeftToRight;
            _currentJob.Options.PropagateDeletions = true;
            if (txtDesc != null)
            {
                txtDesc.Text = "Make right folder an exact replica of left folder (propagate deletions).";
            }
        }

        // Perbarui tampilan panah arah di header
        UpdateDirectionArrow(_currentJob.Direction);

        // Simpan perubahan ke database job
        _jobStore?.Save();
    }

    /// <summary>
    /// Event saat pengguna menekan tombol Enter pada input Quick Filter.
    /// </summary>
    private void OnQuickFilterKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            ApplyQuickFilter();
        }
    }

    /// <summary>
    /// Event saat fokus berpindah keluar dari input Quick Filter.
    /// </summary>
    private void OnQuickFilterLostFocus(object? sender, RoutedEventArgs e)
    {
        ApplyQuickFilter();
    }

    /// <summary>
    /// Menerapkan perubahan filter pengecualian dari kotak Quick Filter ke model job.
    /// </summary>
    private void ApplyQuickFilter()
    {
        if (_currentJob == null)
        {
            return;
        }

        var txtFilter = this.FindControl<TextBox>("TxtQuickFilter");
        if (txtFilter == null)
        {
            return;
        }

        string raw = txtFilter.Text ?? "";
        string[] parts = raw.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries);

        _currentJob.Filter.Exclude.Clear();
        foreach (string part in parts)
        {
            string trimmed = part.Trim();
            if (!string.IsNullOrEmpty(trimmed))
            {
                _currentJob.Filter.Exclude.Add(trimmed);
            }
        }

        _jobStore?.Save();
    }

    /// <summary>
    /// Membuka dialog Job Options langsung pada tab Filters saat tombol corong filter diklik.
    /// </summary>
    private async void OnOpenFiltersClicked(object? sender, RoutedEventArgs e)
    {
        if (_currentJob == null || _jobStore == null)
        {
            return;
        }

        // Buka dialog Job Options dengan langsung memilih tab Filters (indeks 1)
        var dialog = new JobOptionsDialog(_currentJob, _jobStore, initialTabIndex: 1);
        await dialog.ShowDialog(this);

        // Perbarui teks Quick Filter setelah kembali dari dialog
        var txtFilter = this.FindControl<TextBox>("TxtQuickFilter");
        if (txtFilter != null)
        {
            if (_currentJob.Filter != null && _currentJob.Filter.Exclude != null && _currentJob.Filter.Exclude.Count > 0)
            {
                txtFilter.Text = string.Join("; ", _currentJob.Filter.Exclude);
            }
            else
            {
                txtFilter.Text = "";
            }
        }
    }

    /// <summary>
    /// Mendapatkan informasi kapasitas drive (NTFS/FAT/ReFS) atau keterangan SMB untuk ditampilkan di bawah path folder.
    /// </summary>
    private static string GetDriveSpaceInfo(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "";
        }

        try
        {
            string root = Path.GetPathRoot(path) ?? "";
            if (!string.IsNullOrEmpty(root))
            {
                DriveInfo drive = new DriveInfo(root);
                if (drive.IsReady)
                {
                    string freeStr = SyncActionViewModel.FormatBytes(drive.AvailableFreeSpace);
                    string totalStr = SyncActionViewModel.FormatBytes(drive.TotalSize);

                    // Pastikan tidak ada double colons (misal D::)
                    string driveName = drive.Name.TrimEnd('\\');
                    if (!driveName.EndsWith(":"))
                    {
                        driveName += ":";
                    }

                    return "Local " + driveName + " " + drive.DriveFormat + "\nFree: " + freeStr + ", Total: " + totalStr;
                }
            }
        }
        catch
        {
            // Abaikan jika info drive lokal tidak dapat diambil
        }

        if (path.StartsWith(@"\\"))
        {
            return "Local Network " + path.TrimStart('\\');
        }

        return "";
    }

    /// <summary>
    /// Mengatur tampilan panel tengah secara eksklusif (Tutorial, Browse, Sedang Menganalisa, atau Diff Hasil Analisa).
    /// </summary>
    private void SetActiveView(ActiveView view)
    {
        var panelTutorial = this.FindControl<Grid>("PanelTutorial");
        if (panelTutorial != null)
        {
            if (view == ActiveView.Tutorial)
            {
                panelTutorial.IsVisible = true;
            }
            else
            {
                panelTutorial.IsVisible = false;
            }
        }

        var panelBrowse = this.FindControl<Grid>("PanelBrowseMode");
        if (panelBrowse != null)
        {
            if (view == ActiveView.Browse)
            {
                panelBrowse.IsVisible = true;
            }
            else
            {
                panelBrowse.IsVisible = false;
            }
        }

        var panelAnalyzing = this.FindControl<Grid>("PanelAnalyzing");
        if (panelAnalyzing != null)
        {
            if (view == ActiveView.Analyzing)
            {
                panelAnalyzing.IsVisible = true;
            }
            else
            {
                panelAnalyzing.IsVisible = false;
            }
        }

        var panelResult = this.FindControl<DockPanel>("PanelAnalyzeResult");
        if (panelResult != null)
        {
            if (view == ActiveView.AnalyzeResult)
            {
                panelResult.IsVisible = true;
            }
            else
            {
                panelResult.IsVisible = false;
            }
        }
    }

    private void UpdateFolderBoxesDisplay()
    {
        var txtLeftPlaceholder = this.FindControl<TextBlock>("TxtLeftPlaceholder");
        var panelLeftInfo = this.FindControl<StackPanel>("PanelLeftSelectedInfo");
        var txtLeftPath = this.FindControl<TextBlock>("TxtLeftPath");
        var txtLeftDiskInfo = this.FindControl<TextBlock>("TxtLeftDiskInfo");
        var imgLeftLocal = this.FindControl<Image>("ImgLeftFolderLocal");
        var imgLeftSmb = this.FindControl<Image>("ImgLeftFolderSmb");

        var txtRightPlaceholder = this.FindControl<TextBlock>("TxtRightPlaceholder");
        var panelRightInfo = this.FindControl<StackPanel>("PanelRightSelectedInfo");
        var txtRightPath = this.FindControl<TextBlock>("TxtRightPath");
        var txtRightDiskInfo = this.FindControl<TextBlock>("TxtRightDiskInfo");
        var imgRightLocal = this.FindControl<Image>("ImgRightFolderLocal");
        var imgRightSmb = this.FindControl<Image>("ImgRightFolderSmb");

        // Periksa folder kiri
        if (string.IsNullOrWhiteSpace(_selectedLeftPath))
        {
            if (txtLeftPlaceholder != null)
            {
                txtLeftPlaceholder.IsVisible = true;
            }
            if (panelLeftInfo != null)
            {
                panelLeftInfo.IsVisible = false;
            }
        }
        else
        {
            if (txtLeftPlaceholder != null)
            {
                txtLeftPlaceholder.IsVisible = false;
            }
            if (panelLeftInfo != null)
            {
                panelLeftInfo.IsVisible = true;
            }
            if (txtLeftPath != null)
            {
                txtLeftPath.Text = _selectedLeftPath;
            }
            if (txtLeftDiskInfo != null)
            {
                txtLeftDiskInfo.Text = GetDriveSpaceInfo(_selectedLeftPath);
            }
            if (imgLeftLocal != null && imgLeftSmb != null)
            {
                if (_selectedLeftPath.StartsWith(@"\\"))
                {
                    imgLeftLocal.IsVisible = false;
                    imgLeftSmb.IsVisible = true;
                }
                else
                {
                    imgLeftLocal.IsVisible = true;
                    imgLeftSmb.IsVisible = false;
                }
            }
        }

        // Periksa folder kanan
        if (string.IsNullOrWhiteSpace(_selectedRightPath))
        {
            if (txtRightPlaceholder != null)
            {
                txtRightPlaceholder.IsVisible = true;
            }
            if (panelRightInfo != null)
            {
                panelRightInfo.IsVisible = false;
            }
        }
        else
        {
            if (txtRightPlaceholder != null)
            {
                txtRightPlaceholder.IsVisible = false;
            }
            if (panelRightInfo != null)
            {
                panelRightInfo.IsVisible = true;
            }
            if (txtRightPath != null)
            {
                txtRightPath.Text = _selectedRightPath;
            }
            if (txtRightDiskInfo != null)
            {
                txtRightDiskInfo.Text = GetDriveSpaceInfo(_selectedRightPath);
            }
            if (imgRightLocal != null && imgRightSmb != null)
            {
                if (_selectedRightPath.StartsWith(@"\\"))
                {
                    imgRightLocal.IsVisible = false;
                    imgRightSmb.IsVisible = true;
                }
                else
                {
                    imgRightLocal.IsVisible = true;
                    imgRightSmb.IsVisible = false;
                }
            }
        }
    }

    /// <summary>
    /// Menukar posisi folder kiri dan kanan (Swipe / Swap).
    /// </summary>
    private void OnSwapFoldersClicked(object? sender, RoutedEventArgs e)
    {
        if (_isBrowseMode)
        {
            // Jika sedang dalam Mode Browse, tukar path yang sedang dipilih dan update Address Bar
            string tempBrowsePath = _selectedLeftPath;
            _selectedLeftPath = _selectedRightPath;
            _selectedRightPath = tempBrowsePath;

            var txtLeftAddress = this.FindControl<TextBox>("TxtLeftAddress");
            if (txtLeftAddress != null)
            {
                txtLeftAddress.Text = _selectedLeftPath;
            }

            var txtRightAddress = this.FindControl<TextBox>("TxtRightAddress");
            if (txtRightAddress != null)
            {
                txtRightAddress.Text = _selectedRightPath;
            }

            UpdateFolderBoxesDisplay();
            ValidateBrowseSelection();
        }
        else
        {
            // Jika dalam Mode Normal, tukar endpoint Left dan Right pada Job aktif
            if (_currentJob == null)
            {
                return;
            }

            // Tukar konfigurasi endpoint Left & Right
            var tempEndpoint = _currentJob.Left;
            _currentJob.Left = _currentJob.Right;
            _currentJob.Right = tempEndpoint;

            // Tukar variabel path
            string tempPath = _selectedLeftPath;
            _selectedLeftPath = _selectedRightPath;
            _selectedRightPath = tempPath;

            _initialLeftPath = _selectedLeftPath;
            _initialRightPath = _selectedRightPath;

            // Reset plan analisa karena target folder telah bertukar
            _currentPlan = null;
            _comparisonRoots.Clear();
            AnalyzeActions.Clear();

            var btnSync = this.FindControl<Button>("BtnSync");
            if (btnSync != null)
            {
                btnSync.IsEnabled = false;
            }

            // Kembalikan ke tampilan tutorial/idle
            SetActiveView(ActiveView.Tutorial);

            // Perbarui tampilan kotak folder kiri dan kanan
            UpdateFolderBoxesDisplay();

            // Simpan perubahan konfigurasi job ke database SQLite
            _jobStore?.Save();
        }
    }

    /// <summary>
    /// Event klik kotak folder kiri -> Masuk ke Folder Select (Browse) Mode.
    /// </summary>
    private void OnLeftFolderBoxPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_currentJob == null)
        {
            return;
        }

        if (!_isBrowseMode)
        {
            EnterBrowseMode();
        }
    }

    /// <summary>
    /// Event klik kotak folder kanan -> Masuk ke Folder Select (Browse) Mode.
    /// </summary>
    private void OnRightFolderBoxPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_currentJob == null)
        {
            return;
        }

        if (!_isBrowseMode)
        {
            EnterBrowseMode();
        }
    }

    /// <summary>
    /// Masuk ke tampilan Folder Select (Browse) Mode.
    /// </summary>
    private void EnterBrowseMode()
    {
        _isBrowseMode = true;

        if (_currentJob != null)
        {
            _initialLeftPath = _currentJob.Left.Path ?? "";
            _initialRightPath = _currentJob.Right.Path ?? "";
            _selectedLeftPath = _initialLeftPath;
            _selectedRightPath = _initialRightPath;
        }

        // Ganti panel aksi toolbar dari Analyze/Sync ke Apply/Cancel
        var panelNormal = this.FindControl<StackPanel>("PanelActionNormal");
        if (panelNormal != null)
        {
            panelNormal.IsVisible = false;
        }

        var panelBrowse = this.FindControl<StackPanel>("PanelActionBrowse");
        if (panelBrowse != null)
        {
            panelBrowse.IsVisible = true;
        }

        // Tampilkan judul mode di atas kotak
        var titleLeft = this.FindControl<TextBlock>("TxtBrowseLeftModeTitle");
        if (titleLeft != null)
        {
            titleLeft.IsVisible = true;
        }

        var titleRight = this.FindControl<TextBlock>("TxtBrowseRightModeTitle");
        if (titleRight != null)
        {
            titleRight.IsVisible = true;
        }

        // Switch panel tombol kanan: sembunyikan Options/Auto dan tampilkan Multiple Folders/Files
        var panelRightNormal = this.FindControl<StackPanel>("PanelRightNormal");
        if (panelRightNormal != null)
        {
            panelRightNormal.IsVisible = false;
        }

        var panelRightBrowse = this.FindControl<StackPanel>("PanelRightBrowse");
        if (panelRightBrowse != null)
        {
            panelRightBrowse.IsVisible = true;
        }

        // Tampilkan Banner Peringatan
        var banner = this.FindControl<Border>("BannerStatus");
        if (banner != null)
        {
            banner.IsVisible = true;
        }

        // Sembunyikan bar operasi saat dalam mode browse folder
        var barOps = this.FindControl<Border>("BarOperations");
        if (barOps != null)
        {
            barOps.IsVisible = false;
        }

        // Sembunyikan tutorial 3 langkah dan tampilkan dual-pane browse mode
        SetActiveView(ActiveView.Browse);

        // Muat pohon direktori This Computer jika belum dimuat
        if (LeftTreeNodes.Count == 0)
        {
            var leftItems = FsTreeNode.BuildThisComputerTree();
            foreach (var item in leftItems)
            {
                LeftTreeNodes.Add(item);
            }
        }

        if (RightTreeNodes.Count == 0)
        {
            var rightItems = FsTreeNode.BuildThisComputerTree();
            foreach (var item in rightItems)
            {
                RightTreeNodes.Add(item);
            }
        }

        // Update Address Bar
        ClearAddressError(true);
        ClearAddressError(false);

        var txtLeftAddress = this.FindControl<TextBox>("TxtLeftAddress");
        if (txtLeftAddress != null)
        {
            txtLeftAddress.Text = _selectedLeftPath;
        }

        var txtRightAddress = this.FindControl<TextBox>("TxtRightAddress");
        if (txtRightAddress != null)
        {
            txtRightAddress.Text = _selectedRightPath;
        }

        // Validasi tombol Apply dan Banner
        ValidateBrowseSelection();
    }

    /// <summary>
    /// Keluar dari Folder Select (Browse) Mode dan kembali ke tampilan normal.
    /// </summary>
    private void ExitBrowseMode()
    {
        _isBrowseMode = false;

        // Kembalikan panel aksi ke Analyze & Sync
        var panelNormal = this.FindControl<StackPanel>("PanelActionNormal");
        if (panelNormal != null)
        {
            panelNormal.IsVisible = true;
        }

        var panelBrowse = this.FindControl<StackPanel>("PanelActionBrowse");
        if (panelBrowse != null)
        {
            panelBrowse.IsVisible = false;
        }

        // Sembunyikan judul mode dan tombol browse
        var titleLeft = this.FindControl<TextBlock>("TxtBrowseLeftModeTitle");
        if (titleLeft != null)
        {
            titleLeft.IsVisible = false;
        }

        var titleRight = this.FindControl<TextBlock>("TxtBrowseRightModeTitle");
        if (titleRight != null)
        {
            titleRight.IsVisible = false;
        }

        // Switch panel tombol kanan kembali ke Options & Auto
        var panelRightNormal = this.FindControl<StackPanel>("PanelRightNormal");
        if (panelRightNormal != null)
        {
            panelRightNormal.IsVisible = true;
        }

        var panelRightBrowse = this.FindControl<StackPanel>("PanelRightBrowse");
        if (panelRightBrowse != null)
        {
            panelRightBrowse.IsVisible = false;
        }

        // Tampilkan kembali bar operasi
        var barOps = this.FindControl<Border>("BarOperations");
        if (barOps != null)
        {
            barOps.IsVisible = true;
        }

        var banner = this.FindControl<Border>("BannerStatus");
        if (banner != null)
        {
            banner.IsVisible = false;
        }

        // Tampilkan kembali panel utama yang sesuai
        if (_currentPlan != null)
        {
            SetActiveView(ActiveView.AnalyzeResult);
        }
        else
        {
            SetActiveView(ActiveView.Tutorial);
        }

        UpdateFolderBoxesDisplay();
    }

    /// <summary>
    /// Event saat pengguna memilih item di pohon direktori Kiri.
    /// </summary>
    private void OnLeftTreeSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        var treeLeft = this.FindControl<TreeView>("TreeLeft");
        if (treeLeft != null)
        {
            var selected = treeLeft.SelectedItem as FsTreeNode;
            if (selected != null)
            {
                // Jika node belum pernah dimuat subfoldernya, muat sekarang juga
                if (!selected.HasLoadedChildren)
                {
                    selected.LoadSubFolders();
                    selected.IsExpanded = true;
                }

                if (!string.IsNullOrWhiteSpace(selected.FullPath))
                {
                    _selectedLeftPath = selected.FullPath;

                    // Bersihkan pesan error manual sebelumnya jika ada
                    ClearAddressError(true);

                    var txtAddress = this.FindControl<TextBox>("TxtLeftAddress");
                    if (txtAddress != null)
                    {
                        txtAddress.Text = _selectedLeftPath;
                    }

                    UpdateFolderBoxesDisplay();
                    ValidateBrowseSelection();
                }
            }
        }
    }

    /// <summary>
    /// Event saat pengguna memilih item di pohon direktori Kanan.
    /// </summary>
    private void OnRightTreeSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        var treeRight = this.FindControl<TreeView>("TreeRight");
        if (treeRight != null)
        {
            var selected = treeRight.SelectedItem as FsTreeNode;
            if (selected != null)
            {
                // Jika node belum pernah dimuat subfoldernya, muat sekarang juga
                if (!selected.HasLoadedChildren)
                {
                    selected.LoadSubFolders();
                    selected.IsExpanded = true;
                }

                if (!string.IsNullOrWhiteSpace(selected.FullPath))
                {
                    _selectedRightPath = selected.FullPath;

                    // Bersihkan pesan error manual sebelumnya jika ada
                    ClearAddressError(false);

                    var txtAddress = this.FindControl<TextBox>("TxtRightAddress");
                    if (txtAddress != null)
                    {
                        txtAddress.Text = _selectedRightPath;
                    }

                    UpdateFolderBoxesDisplay();
                    ValidateBrowseSelection();
                }
            }
        }
    }

    private void OnLeftAddressKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            var txt = this.FindControl<TextBox>("TxtLeftAddress");
            if (txt != null)
            {
                _ = ApplyManualPathAsync(true, txt.Text);
            }
        }
    }

    private void OnLeftAddressLostFocus(object? sender, RoutedEventArgs e)
    {
        var txt = this.FindControl<TextBox>("TxtLeftAddress");
        if (txt != null)
        {
            _ = ApplyManualPathAsync(true, txt.Text);
        }
    }

    private void OnRightAddressKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            var txt = this.FindControl<TextBox>("TxtRightAddress");
            if (txt != null)
            {
                _ = ApplyManualPathAsync(false, txt.Text);
            }
        }
    }

    private void OnRightAddressLostFocus(object? sender, RoutedEventArgs e)
    {
        var txt = this.FindControl<TextBox>("TxtRightAddress");
        if (txt != null)
        {
            _ = ApplyManualPathAsync(false, txt.Text);
        }
    }

    /// <summary>
    /// Menampilkan overlay modal loading in-app yang solid dan anti-glitch di atas jendela utama.
    /// </summary>
    private void ShowLoadingOverlay(string targetName, System.Threading.CancellationTokenSource cts)
    {
        _activeLoadingCts = cts;

        var txtTitle = this.FindControl<TextBlock>("TxtLoadingTitle");
        if (txtTitle != null)
        {
            txtTitle.Text = "Connecting to " + targetName + "...";
        }

        var overlay = this.FindControl<Grid>("OverlayLoading");
        if (overlay != null)
        {
            overlay.IsVisible = true;
        }
    }

    /// <summary>
    /// Menyembunyikan overlay modal loading in-app.
    /// </summary>
    private void HideLoadingOverlay()
    {
        var overlay = this.FindControl<Grid>("OverlayLoading");
        if (overlay != null)
        {
            overlay.IsVisible = false;
        }

        _activeLoadingCts = null;
    }

    /// <summary>
    /// Memvalidasi dan menerapkan path folder (Lokal maupun SMB/Network Share) secara asinkron dengan loading popup yang dapat dibatalkan (cancelable).
    /// </summary>
    private async System.Threading.Tasks.Task ApplyManualPathAsync(bool isLeft, string? rawPath)
    {
        string trimmedPath = "";
        if (rawPath != null)
        {
            trimmedPath = rawPath.Trim();
        }

        // Hapus tanda petik ganda jika pengguna mem-paste path dengan tanda kutip
        if (trimmedPath.StartsWith("\"") && trimmedPath.EndsWith("\"") && trimmedPath.Length >= 2)
        {
            trimmedPath = trimmedPath.Substring(1, trimmedPath.Length - 2).Trim();
        }

        // Normalisasi format smb:// menjadi UNC \\
        if (trimmedPath.StartsWith("smb://", StringComparison.OrdinalIgnoreCase))
        {
            trimmedPath = @"\\" + trimmedPath.Substring(6).Replace('/', '\\');
        }

        if (string.IsNullOrWhiteSpace(trimmedPath))
        {
            SetAddressError(isLeft, "Folder path cannot be empty.");
            return;
        }

        try
        {
            // 1. PENANGANAN PATH NETWORK SHARE / SMB (UNC Path: diawali \\)
            if (trimmedPath.StartsWith(@"\\"))
            {
                var uncParts = trimmedPath.TrimStart('\\').Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries);

                // KASUS A: Pengguna hanya memasukkan Server Host / IP (misal \\192.168.10 atau \\192.168.10\)
                if (uncParts.Length == 1)
                {
                    string serverHost = uncParts[0];

                    using var cts = new System.Threading.CancellationTokenSource();
                    ShowLoadingOverlay(@"\\" + serverHost, cts);

                    System.Collections.Generic.List<string> shares = new();
                    bool wasCanceled = false;

                    try
                    {
                        // Jalankan query SMB share di background thread agar UI tidak ngehang
                        shares = await System.Threading.Tasks.Task.Run(() =>
                        {
                            if (cts.Token.IsCancellationRequested)
                            {
                                return new System.Collections.Generic.List<string>();
                            }

                            return SmbShareEnumerator.EnumerateShares(serverHost);
                        }, cts.Token);
                    }
                    catch (System.OperationCanceledException)
                    {
                        wasCanceled = true;
                    }
                    catch
                    {
                        // Error jaringan atau timeout
                    }
                    finally
                    {
                        HideLoadingOverlay();
                    }

                    if (wasCanceled || cts.Token.IsCancellationRequested)
                    {
                        var txtBannerMessage = this.FindControl<TextBlock>("TxtBannerMessage");
                        if (txtBannerMessage != null)
                        {
                            txtBannerMessage.Text = "Connection to \\\\" + serverHost + " was canceled by user.";
                            txtBannerMessage.Foreground = new SolidColorBrush(Color.Parse("#D32F2F"));
                        }
                        return;
                    }

                    // Terapkan hasil share ke pohon direktori
                    AddSmbServerWithSharesToTree(isLeft, serverHost, shares);
                    return;
                }

                // KASUS B: Pengguna memasukkan path lengkap dengan nama share (misal \\192.168.10\ShareName)
                if (uncParts.Length >= 2)
                {
                    using var cts = new System.Threading.CancellationTokenSource();
                    ShowLoadingOverlay(trimmedPath, cts);

                    bool exists = false;
                    bool wasCanceled = false;

                    try
                    {
                        exists = await System.Threading.Tasks.Task.Run(() =>
                        {
                            if (cts.Token.IsCancellationRequested)
                            {
                                return false;
                            }
                            return Directory.Exists(trimmedPath);
                        }, cts.Token);
                    }
                    catch
                    {
                        wasCanceled = true;
                    }
                    finally
                    {
                        HideLoadingOverlay();
                    }

                    if (wasCanceled || cts.Token.IsCancellationRequested)
                    {
                        var txtBannerMessage = this.FindControl<TextBlock>("TxtBannerMessage");
                        if (txtBannerMessage != null)
                        {
                            txtBannerMessage.Text = "Check on '" + trimmedPath + "' was canceled.";
                            txtBannerMessage.Foreground = new SolidColorBrush(Color.Parse("#D32F2F"));
                        }
                        return;
                    }

                    if (exists)
                    {
                        ClearAddressError(isLeft);
                        SetSelectedPath(isLeft, trimmedPath, EndpointKind.Smb);
                        return;
                    }
                    else
                    {
                        SetAddressError(isLeft, "Cannot access SMB share '" + trimmedPath + "'. Check network connection or permissions.");
                        return;
                    }
                }
            }

            // 2. PENANGANAN PATH FOLDER LOKAL (C:\..., D:\..., dll)
            if (Directory.Exists(trimmedPath))
            {
                string fullPath = Path.GetFullPath(trimmedPath);
                ClearAddressError(isLeft);
                SetSelectedPath(isLeft, fullPath, EndpointKind.Local);
            }
            else
            {
                SetAddressError(isLeft, "Folder '" + trimmedPath + "' does not exist or invalid path.");
            }
        }
        catch (Exception ex)
        {
            SetAddressError(isLeft, "Invalid path format: " + ex.Message);
        }
    }

    /// <summary>
    /// Menambahkan server SMB dan daftar share yang didapat ke dalam pohon direktori.
    /// </summary>
    private void AddSmbServerWithSharesToTree(bool isLeft, string serverHost, System.Collections.Generic.List<string> shares)
    {
        var targetTreeList = LeftTreeNodes;
        if (!isLeft)
        {
            targetTreeList = RightTreeNodes;
        }

        // Cari node kategori Net Shares (Win-SMB)
        FsTreeNode? smbGroup = null;
        foreach (var node in targetTreeList)
        {
            if (node.NodeType == FsNodeType.SmbGroup)
            {
                smbGroup = node;
                break;
            }
        }

        if (smbGroup == null)
        {
            smbGroup = new FsTreeNode
            {
                Name = "Net Shares (Win-SMB)",
                Comment = "Windows Network Shares (SMB)...",
                FullPath = "",
                NodeType = FsNodeType.SmbGroup,
                IsExpanded = true
            };
            targetTreeList.Add(smbGroup);
        }

        // Periksa apakah server ini sudah pernah ada
        FsTreeNode? serverNode = null;
        foreach (var child in smbGroup.Children)
        {
            if (child.NodeType == FsNodeType.SmbServer)
            {
                if (string.Equals(child.Name, @"\\" + serverHost, StringComparison.OrdinalIgnoreCase))
                {
                    serverNode = child;
                    break;
                }
            }
        }

        if (serverNode == null)
        {
            serverNode = new FsTreeNode
            {
                Name = @"\\" + serverHost,
                Comment = "SMB Server",
                FullPath = @"\\" + serverHost,
                NodeType = FsNodeType.SmbServer,
                IsExpanded = true
            };
            smbGroup.Children.Add(serverNode);
        }

        // Isi share folder yang didapat
        serverNode.Children.Clear();
        foreach (var share in shares)
        {
            string sharePath = @"\\" + serverHost + @"\" + share;
            var shareNode = new FsTreeNode
            {
                Name = share,
                Comment = "Network Share",
                FullPath = sharePath,
                NodeType = FsNodeType.SmbShare
            };
            shareNode.Children.Add(new FsTreeNode { Name = "Loading...", NodeType = FsNodeType.Folder });
            serverNode.Children.Add(shareNode);
        }

        serverNode.IsExpanded = true;
        smbGroup.IsExpanded = true;

        ClearAddressError(isLeft);

        TextBox? txtAddress = null;
        if (isLeft)
        {
            txtAddress = this.FindControl<TextBox>("TxtLeftAddress");
        }
        else
        {
            txtAddress = this.FindControl<TextBox>("TxtRightAddress");
        }

        if (txtAddress != null)
        {
            txtAddress.Text = @"\\" + serverHost + @"\";
        }

        var txtBannerIcon = this.FindControl<TextBlock>("TxtBannerIcon");
        var txtBannerMessage = this.FindControl<TextBlock>("TxtBannerMessage");

        if (shares.Count > 0)
        {
            if (txtBannerIcon != null)
            {
                txtBannerIcon.Text = "📁";
            }
            if (txtBannerMessage != null)
            {
                txtBannerMessage.Text = "Connected to SMB server \\\\" + serverHost + " (" + shares.Count + " shares found). Please select a share folder below.";
                txtBannerMessage.Foreground = new SolidColorBrush(Color.Parse("#1976D2"));
            }
        }
        else
        {
            if (txtBannerIcon != null)
            {
                txtBannerIcon.Text = "ℹ️";
            }
            if (txtBannerMessage != null)
            {
                txtBannerMessage.Text = "SMB server \\\\" + serverHost + " connected. Type the share name (e.g. \\\\" + serverHost + "\\share) or browse below.";
                txtBannerMessage.Foreground = new SolidColorBrush(Color.Parse("#1976D2"));
            }
        }
    }

    /// <summary>
    /// Menetapkan path folder terpilih ke variabel state dan address bar.
    /// </summary>
    private void SetSelectedPath(bool isLeft, string fullPath, EndpointKind kind)
    {
        if (isLeft)
        {
            _selectedLeftPath = fullPath;
            if (_currentJob != null)
            {
                _currentJob.Left.Kind = kind;
            }
            var txt = this.FindControl<TextBox>("TxtLeftAddress");
            if (txt != null)
            {
                txt.Text = fullPath;
            }
        }
        else
        {
            _selectedRightPath = fullPath;
            if (_currentJob != null)
            {
                _currentJob.Right.Kind = kind;
            }
            var txt = this.FindControl<TextBox>("TxtRightAddress");
            if (txt != null)
            {
                txt.Text = fullPath;
            }
        }

        UpdateFolderBoxesDisplay();
        ValidateBrowseSelection();
    }

    /// <summary>
    /// Menampilkan pesan error dan mengubah border address bar menjadi merah saat path salah.
    /// </summary>
    private void SetAddressError(bool isLeft, string errorMessage)
    {
        TextBox? txtBox = null;
        TextBlock? errLabel = null;

        if (isLeft)
        {
            txtBox = this.FindControl<TextBox>("TxtLeftAddress");
            errLabel = this.FindControl<TextBlock>("TxtLeftAddressError");
            _selectedLeftPath = "";
        }
        else
        {
            txtBox = this.FindControl<TextBox>("TxtRightAddress");
            errLabel = this.FindControl<TextBlock>("TxtRightAddressError");
            _selectedRightPath = "";
        }

        if (txtBox != null)
        {
            txtBox.BorderBrush = new SolidColorBrush(Color.Parse("#E53935"));
        }

        if (errLabel != null)
        {
            errLabel.Text = errorMessage;
            errLabel.IsVisible = true;
        }

        // Tampilkan pesan error pada banner status di atas
        var txtBannerIcon = this.FindControl<TextBlock>("TxtBannerIcon");
        if (txtBannerIcon != null)
        {
            txtBannerIcon.Text = "⛔";
        }

        var txtBannerMessage = this.FindControl<TextBlock>("TxtBannerMessage");
        if (txtBannerMessage != null)
        {
            txtBannerMessage.Text = errorMessage;
            txtBannerMessage.Foreground = new SolidColorBrush(Color.Parse("#D32F2F"));
        }

        // Nonaktifkan tombol Apply karena salah satu path tidak valid
        var btnApply = this.FindControl<Button>("BtnApply");
        if (btnApply != null)
        {
            btnApply.IsEnabled = false;
        }

        UpdateFolderBoxesDisplay();
    }

    /// <summary>
    /// Membersihkan tampilan error pada Address Bar.
    /// </summary>
    private void ClearAddressError(bool isLeft)
    {
        TextBox? txtBox = null;
        TextBlock? errLabel = null;

        if (isLeft)
        {
            txtBox = this.FindControl<TextBox>("TxtLeftAddress");
            errLabel = this.FindControl<TextBlock>("TxtLeftAddressError");
        }
        else
        {
            txtBox = this.FindControl<TextBox>("TxtRightAddress");
            errLabel = this.FindControl<TextBlock>("TxtRightAddressError");
        }

        if (txtBox != null)
        {
            txtBox.BorderBrush = new SolidColorBrush(Color.Parse("#C5CCD6"));
        }

        if (errLabel != null)
        {
            errLabel.Text = "";
            errLabel.IsVisible = false;
        }
    }

    /// <summary>
    /// Memvalidasi pilihan folder: tombol Apply HANYA aktif jika kedua sisi sudah terisi folder.
    /// </summary>
    private void ValidateBrowseSelection()
    {
        var btnApply = this.FindControl<Button>("BtnApply");
        var txtBannerIcon = this.FindControl<TextBlock>("TxtBannerIcon");
        var txtBannerMessage = this.FindControl<TextBlock>("TxtBannerMessage");

        bool hasLeft = !string.IsNullOrWhiteSpace(_selectedLeftPath);
        bool hasRight = !string.IsNullOrWhiteSpace(_selectedRightPath);

        if (!hasLeft)
        {
            // Folder kiri belum dipilih
            if (btnApply != null)
            {
                btnApply.IsEnabled = false;
            }
            if (txtBannerIcon != null)
            {
                txtBannerIcon.Text = "⛔";
            }
            if (txtBannerMessage != null)
            {
                txtBannerMessage.Text = "Please select a folder on left side";
                txtBannerMessage.Foreground = new SolidColorBrush(Color.Parse("#D32F2F"));
            }
        }
        else if (!hasRight)
        {
            // Folder kanan belum dipilih
            if (btnApply != null)
            {
                btnApply.IsEnabled = false;
            }
            if (txtBannerIcon != null)
            {
                txtBannerIcon.Text = "⛔";
            }
            if (txtBannerMessage != null)
            {
                txtBannerMessage.Text = "Please select a folder on right side";
                txtBannerMessage.Foreground = new SolidColorBrush(Color.Parse("#D32F2F"));
            }
        }
        else
        {
            // KEDUA FOLDER SUDAH DIPILIH -> Tombol Apply BISA DIKLIK!
            if (btnApply != null)
            {
                btnApply.IsEnabled = true;
            }
            if (txtBannerIcon != null)
            {
                txtBannerIcon.Text = "✅";
            }
            if (txtBannerMessage != null)
            {
                txtBannerMessage.Text = "Folder selection complete. Click Apply to confirm.";
                txtBannerMessage.Foreground = new SolidColorBrush(Color.Parse("#2E7D32"));
            }
        }
    }

    /// <summary>
    /// Menyimpan hasil pemilihan folder ke konfigurasi job dan database SQLite.
    /// </summary>
    private void OnApplyClicked(object? sender, RoutedEventArgs e)
    {
        if (_currentJob == null)
        {
            return;
        }

        // Pastikan kedua folder valid
        if (string.IsNullOrWhiteSpace(_selectedLeftPath))
        {
            return;
        }
        if (string.IsNullOrWhiteSpace(_selectedRightPath))
        {
            return;
        }

        // Terapkan path ke JobConfig
        _currentJob.Left.Path = _selectedLeftPath;
        _currentJob.Right.Path = _selectedRightPath;

        // Simpan langsung ke database SQLite
        if (_jobStore != null)
        {
            _jobStore.Update(_currentJob);
            _jobStore.Save();
        }

        // Keluar dari Browse Mode
        ExitBrowseMode();
    }

    /// <summary>
    /// Membatalkan perubahan folder dan mengembalikan ke nilai semula.
    /// </summary>
    private void OnCancelClicked(object? sender, RoutedEventArgs e)
    {
        _selectedLeftPath = _initialLeftPath;
        _selectedRightPath = _initialRightPath;

        ExitBrowseMode();
    }

    /// <summary>
    /// Event handler saat pengguna mengklik menu 'New Folder...' lewat klik kanan pada pohon direktori.
    /// </summary>
    private async void OnNewFolderMenuItemClick(object? sender, RoutedEventArgs e)
    {
        var menuItem = sender as MenuItem;
        if (menuItem == null)
        {
            return;
        }

        var node = menuItem.DataContext as FsTreeNode;
        if (node == null)
        {
            return;
        }

        // Pastikan node memiliki path fisik (Drive, Folder, atau SMB Share)
        if (string.IsNullOrWhiteSpace(node.FullPath))
        {
            var txtBannerMessage = this.FindControl<TextBlock>("TxtBannerMessage");
            if (txtBannerMessage != null)
            {
                txtBannerMessage.Text = "Please select a specific drive or folder first to create a new folder.";
                txtBannerMessage.Foreground = new SolidColorBrush(Color.Parse("#D32F2F"));
            }
            return;
        }

        bool isLeft = LeftTreeNodes.Contains(node) || IsNodeInTree(LeftTreeNodes, node);

        // Buka dialog input nama folder baru
        var dialog = new NewFolderDialog();
        var folderName = await dialog.ShowDialog<string?>(this);

        if (string.IsNullOrWhiteSpace(folderName))
        {
            return;
        }

        try
        {
            string newFolderPath = Path.Combine(node.FullPath, folderName);

            // Buat folder fisik di disk atau SMB share
            if (!Directory.Exists(newFolderPath))
            {
                Directory.CreateDirectory(newFolderPath);
            }

            // Muat ulang daftar subfolder node ini dan buka ekspansinya
            node.LoadSubFolders();
            node.IsExpanded = true;

            // Set folder baru ini sebagai folder terpilih
            if (isLeft)
            {
                _selectedLeftPath = newFolderPath;
                ClearAddressError(true);

                var txt = this.FindControl<TextBox>("TxtLeftAddress");
                if (txt != null)
                {
                    txt.Text = newFolderPath;
                }
            }
            else
            {
                _selectedRightPath = newFolderPath;
                ClearAddressError(false);

                var txt = this.FindControl<TextBox>("TxtRightAddress");
                if (txt != null)
                {
                    txt.Text = newFolderPath;
                }
            }

            UpdateFolderBoxesDisplay();
            ValidateBrowseSelection();

            // Tampilkan pesan konfirmasi sukses di banner
            var txtBannerIcon = this.FindControl<TextBlock>("TxtBannerIcon");
            if (txtBannerIcon != null)
            {
                txtBannerIcon.Text = "📁";
            }

            var txtBanner = this.FindControl<TextBlock>("TxtBannerMessage");
            if (txtBanner != null)
            {
                txtBanner.Text = "Folder '" + folderName + "' created successfully.";
                txtBanner.Foreground = new SolidColorBrush(Color.Parse("#2E7D32"));
            }
        }
        catch (Exception ex)
        {
            SetAddressError(isLeft, "Cannot create folder: " + ex.Message);
        }
    }

    /// <summary>
    /// Event handler saat pengguna mengklik menu 'Refresh' lewat klik kanan.
    /// </summary>
    private void OnRefreshTreeMenuItemClick(object? sender, RoutedEventArgs e)
    {
        var menuItem = sender as MenuItem;
        if (menuItem == null)
        {
            return;
        }

        var node = menuItem.DataContext as FsTreeNode;
        if (node == null)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(node.FullPath))
        {
            node.LoadSubFolders();
            node.IsExpanded = true;
        }
    }

    /// <summary>
    /// Memeriksa apakah sebuah node FsTreeNode berada di dalam hierarki pohon tertentu.
    /// </summary>
    private bool IsNodeInTree(System.Collections.Generic.IEnumerable<FsTreeNode> collection, FsTreeNode target)
    {
        foreach (var item in collection)
        {
            if (item == target)
            {
                return true;
            }

            if (item.Children.Count > 0)
            {
                if (IsNodeInTree(item.Children, target))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Menampilkan pesan banner status atau notifikasi pada status banner utama (BannerJobStatus).
    /// </summary>
    private void ShowBannerMessage(string message, bool isError)
    {
        if (isError)
        {
            SetMainJobStatus(MainJobState.Error, message);
        }
        else
        {
            SetMainJobStatus(MainJobState.LastSync, message);
        }
    }

    /// <summary>
    /// Event saat pengguna menekan tombol Analyze pada toolbar.
    /// Melakukan scanning dan perbandingan file secara asinkron tanpa mengunci UI.
    /// </summary>
    private async void OnAnalyzeClicked(object? sender, RoutedEventArgs e)
    {
        if (_currentJob == null)
        {
            ShowBannerMessage("Please select a job first.", true);
            return;
        }

        var jobState = GetOrCreateUiState(_currentJob.Name);
        if (jobState.IsAnalyzing)
        {
            ShowBannerMessage("Job '" + _currentJob.Name + "' is already analyzing. Please wait.", false);
            return;
        }
        if (jobState.IsSyncing)
        {
            ShowBannerMessage("Job '" + _currentJob.Name + "' is currently synchronizing. Please wait.", false);
            return;
        }

        await RunAnalysisAsync(_currentJob);
    }

    /// <summary>
    /// Menjalankan scanning dan perbandingan file (Analyze) secara asinkron.
    /// Mengembalikan objek SyncPlan hasil analisa.
    /// </summary>
    private async Task<SyncPlan?> RunAnalysisAsync(JobConfig job)
    {
        if (string.IsNullOrWhiteSpace(job.Left.Path) || string.IsNullOrWhiteSpace(job.Right.Path))
        {
            ShowBannerMessage("Please select both left and right folders first before analyzing.", true);
            return null;
        }

        if (_isBrowseMode)
        {
            ExitBrowseMode();
        }

        var jobState = GetOrCreateUiState(job.Name);
        jobState.IsAnalyzing = true;
        jobState.AnalysisCts = new CancellationTokenSource();
        var ct = jobState.AnalysisCts.Token;

        var btnStop = this.FindControl<Button>("BtnStop");
        var btnAnalyze = this.FindControl<Button>("BtnAnalyze");
        var btnSync = this.FindControl<Button>("BtnSync");

        // Jika saat ini user sedang melihat job ini, tampilkan layar progress analisa
        if (string.Equals(_currentJob?.Name, job.Name, StringComparison.OrdinalIgnoreCase))
        {
            SetActiveView(ActiveView.Analyzing);
            SetMainJobStatus(MainJobState.Running, "Analyzing '" + job.Name + "'...");

            if (btnStop != null)
            {
                btnStop.IsVisible = true;
            }
            if (btnAnalyze != null)
            {
                btnAnalyze.IsEnabled = false;
            }
            if (btnSync != null)
            {
                btnSync.IsEnabled = false;
            }
        }

        // Reset elemen visual progress analisa
        var txtPercent = this.FindControl<TextBlock>("TxtAnalyzePercent");
        var txtStatus = this.FindControl<TextBlock>("TxtAnalyzeStatus");
        var progLeft = this.FindControl<ProgressBar>("ProgAnalyzeLeft");
        var progRight = this.FindControl<ProgressBar>("ProgAnalyzeRight");
        var txtLeftFolders = this.FindControl<TextBlock>("TxtAnalyzeLeftFolders");
        var txtLeftFiles = this.FindControl<TextBlock>("TxtAnalyzeLeftFiles");
        var txtRightFolders = this.FindControl<TextBlock>("TxtAnalyzeRightFolders");
        var txtRightFiles = this.FindControl<TextBlock>("TxtAnalyzeRightFiles");

        jobState.AnalyzePercentText = "01% analyzing";
        jobState.AnalyzeStatusText = "Reading State from database (EasyFileSync.db)...";
        jobState.AnalyzeLeftProgress = 0;
        jobState.AnalyzeRightProgress = 0;
        jobState.AnalyzeLeftFolders = 0;
        jobState.AnalyzeLeftFiles = 0;
        jobState.AnalyzeRightFolders = 0;
        jobState.AnalyzeRightFiles = 0;

        if (string.Equals(_currentJob?.Name, job.Name, StringComparison.OrdinalIgnoreCase))
        {
            if (txtPercent != null)
            {
                txtPercent.Text = "01% analyzing";
            }
            if (txtStatus != null)
            {
                txtStatus.Text = "Reading State from database (EasyFileSync.db)...";
            }
            if (progLeft != null)
            {
                progLeft.Value = 0;
            }
            if (progRight != null)
            {
                progRight.Value = 0;
            }
            if (txtLeftFolders != null)
            {
                txtLeftFolders.Text = "0 Folders";
            }
            if (txtLeftFiles != null)
            {
                txtLeftFiles.Text = "0 Files";
            }
            if (txtRightFolders != null)
            {
                txtRightFolders.Text = "Folders 0";
            }
            if (txtRightFiles != null)
            {
                txtRightFiles.Text = "Files 0";
            }
        }

        int leftFolderCount = 0;
        int leftFileCount = 0;
        int rightFolderCount = 0;
        int rightFileCount = 0;

        _activeAnalysisCts = jobState.AnalysisCts;

        var progress = new Progress<string>(msg =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (msg.StartsWith("left|"))
                {
                    string rel = msg.Substring(5);
                    if (rel.EndsWith("/") || !rel.Contains("."))
                    {
                        leftFolderCount++;
                    }
                    else
                    {
                        leftFileCount++;
                    }

                    jobState.AnalyzeLeftFolders = leftFolderCount;
                    jobState.AnalyzeLeftFiles = leftFileCount;
                    jobState.AnalyzeLeftProgress = Math.Min(100, (jobState.AnalyzeLeftProgress + 5) % 100);
                    jobState.AnalyzePercentText = "25% analyzing";
                    jobState.AnalyzeStatusText = Path.Combine(job.Left.Path ?? "", rel.Replace('/', '\\'));

                    if (string.Equals(_currentJob?.Name, job.Name, StringComparison.OrdinalIgnoreCase))
                    {
                        if (txtLeftFolders != null)
                        {
                            txtLeftFolders.Text = leftFolderCount + " Folders";
                        }
                        if (txtLeftFiles != null)
                        {
                            txtLeftFiles.Text = leftFileCount + " Files";
                        }
                        if (progLeft != null)
                        {
                            progLeft.Value = jobState.AnalyzeLeftProgress;
                        }
                        if (txtPercent != null)
                        {
                            txtPercent.Text = jobState.AnalyzePercentText;
                        }
                        if (txtStatus != null)
                        {
                            txtStatus.Text = jobState.AnalyzeStatusText;
                        }
                    }
                }
                else if (msg.StartsWith("right|"))
                {
                    string rel = msg.Substring(6);
                    if (rel.EndsWith("/") || !rel.Contains("."))
                    {
                        rightFolderCount++;
                    }
                    else
                    {
                        rightFileCount++;
                    }

                    jobState.AnalyzeRightFolders = rightFolderCount;
                    jobState.AnalyzeRightFiles = rightFileCount;
                    jobState.AnalyzeRightProgress = Math.Min(100, (jobState.AnalyzeRightProgress + 5) % 100);
                    jobState.AnalyzePercentText = "50% analyzing";
                    jobState.AnalyzeStatusText = Path.Combine(job.Right.Path ?? "", rel.Replace('/', '\\'));

                    if (string.Equals(_currentJob?.Name, job.Name, StringComparison.OrdinalIgnoreCase))
                    {
                        if (txtRightFolders != null)
                        {
                            txtRightFolders.Text = "Folders " + rightFolderCount;
                        }
                        if (txtRightFiles != null)
                        {
                            txtRightFiles.Text = "Files " + rightFileCount;
                        }
                        if (progRight != null)
                        {
                            progRight.Value = jobState.AnalyzeRightProgress;
                        }
                        if (txtPercent != null)
                        {
                            txtPercent.Text = jobState.AnalyzePercentText;
                        }
                        if (txtStatus != null)
                        {
                            txtStatus.Text = jobState.AnalyzeStatusText;
                        }
                    }
                }
                else if (msg == "reading_state")
                {
                    jobState.AnalyzePercentText = "10% analyzing";
                    jobState.AnalyzeStatusText = "Reading State from database (EasyFileSync.db)...";

                    if (string.Equals(_currentJob?.Name, job.Name, StringComparison.OrdinalIgnoreCase))
                    {
                        if (txtPercent != null)
                        {
                            txtPercent.Text = jobState.AnalyzePercentText;
                        }
                        if (txtStatus != null)
                        {
                            txtStatus.Text = jobState.AnalyzeStatusText;
                        }
                    }
                }
                else if (msg == "comparing")
                {
                    jobState.AnalyzeLeftProgress = 100;
                    jobState.AnalyzeRightProgress = 100;
                    jobState.AnalyzePercentText = "75% analyzing";
                    jobState.AnalyzeStatusText = "Comparing file differences and calculating sync plan...";

                    if (string.Equals(_currentJob?.Name, job.Name, StringComparison.OrdinalIgnoreCase))
                    {
                        if (progLeft != null)
                        {
                            progLeft.Value = 100;
                        }
                        if (progRight != null)
                        {
                            progRight.Value = 100;
                        }
                        if (txtPercent != null)
                        {
                            txtPercent.Text = jobState.AnalyzePercentText;
                        }
                        if (txtStatus != null)
                        {
                            txtStatus.Text = jobState.AnalyzeStatusText;
                        }
                    }
                }
                else
                {
                    jobState.AnalyzeStatusText = msg;
                    if (string.Equals(_currentJob?.Name, job.Name, StringComparison.OrdinalIgnoreCase))
                    {
                        if (txtStatus != null)
                        {
                            txtStatus.Text = msg;
                        }
                    }
                }
            });
        });

        try
        {
            JobRunStatus status = new JobRunStatus { JobName = job.Name };
            SyncPlan? plan = null;

            await Task.Run(() =>
            {
                if (_syncEngine != null)
                {
                    plan = _syncEngine.AnalyzePlan(job, status, progress, ct);
                }
            }, ct);

            if (plan != null)
            {
                jobState.Plan = plan;
                jobState.ComparisonRoots = SyncActionViewModel.BuildTree(plan.Actions);
                jobState.View = ActiveView.AnalyzeResult;

                if (plan.ChangedCount > 0)
                {
                    jobState.SummaryText = "Analysis complete: " + plan.ChangedCount + " changes (" + SyncActionViewModel.FormatBytes(plan.BytesToCopy) + ") to synchronize.";
                    jobState.OperationStatsText = "Copy " + plan.CopyCount + " file(s) to right, " + SyncActionViewModel.FormatBytes(plan.BytesToCopy);
                    jobState.BannerMessage = "Analysis completed: " + plan.ChangedCount + " change(s) found to synchronize.";
                }
                else
                {
                    jobState.SummaryText = "Analysis complete: Folders are in sync. No changes needed.";
                    jobState.OperationStatsText = "Folders are in sync. 0 files to copy.";
                    jobState.BannerMessage = "Analysis completed: Folders are in sync. No changes needed.";
                }

                jobState.BannerState = MainJobState.LastSync;
                jobState.DetailsText = "Copy: " + plan.CopyCount + " | Delete: " + plan.DeleteCount + " | Conflict: " + plan.ConflictCount;

                // Jika pengguna saat ini sedang membuka halaman job ini, tampilkan hasilnya ke layar
                if (string.Equals(_currentJob?.Name, job.Name, StringComparison.OrdinalIgnoreCase))
                {
                    _currentPlan = plan;

                    if (txtPercent != null)
                    {
                        txtPercent.Text = "100% analyzing";
                    }
                    if (progLeft != null)
                    {
                        progLeft.Value = 100;
                    }
                    if (progRight != null)
                    {
                        progRight.Value = 100;
                    }

                    // Beri jeda 300 milidetik agar transisi visual selesai dengan mulus
                    await Task.Delay(300);

                    // Pastikan pengguna masih membuka job ini setelah jeda asinkron
                    if (string.Equals(_currentJob?.Name, job.Name, StringComparison.OrdinalIgnoreCase))
                    {
                        _comparisonRoots = jobState.ComparisonRoots.ToList();
                        RefreshVisibleAnalyzeActions();

                        var txtSummary = this.FindControl<TextBlock>("TxtAnalyzeSummary");
                        if (txtSummary != null)
                        {
                            txtSummary.Text = jobState.SummaryText;
                        }

                        var txtDetails = this.FindControl<TextBlock>("TxtAnalyzeDetails");
                        if (txtDetails != null)
                        {
                            txtDetails.Text = jobState.DetailsText;
                        }

                        var txtOpStats = this.FindControl<TextBlock>("TxtOperationStats");
                        if (txtOpStats != null)
                        {
                            txtOpStats.Text = jobState.OperationStatsText;
                        }

                        SetActiveView(ActiveView.AnalyzeResult);

                        if (btnSync != null)
                        {
                            if (plan.HasChanges || plan.Actions.Count > 0)
                            {
                                btnSync.IsEnabled = true;
                            }
                            else
                            {
                                btnSync.IsEnabled = false;
                            }
                        }

                        SetMainJobStatus(MainJobState.LastSync, jobState.BannerMessage);
                    }
                }
                else
                {
                    AppendDebugLog("Analysis", "Job '" + job.Name + "': Analysis completed with " + plan.ChangedCount + " changes.");
                }
            }
        }
        catch (OperationCanceledException)
        {
            jobState.Plan = null;
            jobState.View = ActiveView.Tutorial;
            jobState.BannerState = MainJobState.Paused;
            jobState.BannerMessage = "Synchronization stopped by user, click Sync again to continue";

            if (string.Equals(_currentJob?.Name, job.Name, StringComparison.OrdinalIgnoreCase))
            {
                SetMainJobStatus(MainJobState.Paused, "Synchronization stopped by user, click Sync again to continue");

                if (_currentPlan != null)
                {
                    SetActiveView(ActiveView.AnalyzeResult);
                }
                else
                {
                    SetActiveView(ActiveView.Tutorial);
                }
            }
        }
        catch (Exception ex)
        {
            if (string.Equals(_currentJob?.Name, job.Name, StringComparison.OrdinalIgnoreCase))
            {
                ShowBannerMessage("Analysis error: " + ex.Message, true);
                SetMainJobStatus(MainJobState.Error, "Analysis error: " + ex.Message);

                if (_currentPlan != null)
                {
                    SetActiveView(ActiveView.AnalyzeResult);
                }
                else
                {
                    SetActiveView(ActiveView.Tutorial);
                }
            }
            else
            {
                AppendDebugLog("Error", "Job '" + job.Name + "' analysis error: " + ex.Message);
            }
        }
        finally
        {
            jobState.IsAnalyzing = false;
            jobState.AnalysisCts = null;

            var panelStatus = this.FindControl<StackPanel>("PanelTransferStatus");
            var btnCancel = this.FindControl<Button>("BtnCancelTransfer");
            if (panelStatus != null)
            {
                panelStatus.IsVisible = false;
            }
            if (btnCancel != null)
            {
                btnCancel.IsVisible = false;
            }

            if (string.Equals(_currentJob?.Name, job.Name, StringComparison.OrdinalIgnoreCase))
            {
                if (btnStop != null)
                {
                    btnStop.IsVisible = false;
                }
                if (btnAnalyze != null)
                {
                    btnAnalyze.IsEnabled = true;
                }
                _activeAnalysisCts = null;
            }
        }

        return jobState.Plan;
    }

    /// <summary>
    /// Event saat pengguna menekan tombol Stop pada toolbar untuk membatalkan proses analisa.
    /// </summary>
    private void OnStopClicked(object? sender, RoutedEventArgs e)
    {
        if (_currentJob != null)
        {
            var jobState = GetOrCreateUiState(_currentJob.Name);
            if (jobState.AnalysisCts != null)
            {
                jobState.AnalysisCts.Cancel();
            }
            if (jobState.SyncCts != null)
            {
                jobState.SyncCts.Cancel();
            }
        }
        else if (_activeAnalysisCts != null)
        {
            _activeAnalysisCts.Cancel();
        }

        SetMainJobStatus(MainJobState.Paused, "Synchronization / Analysis stopped by user.");
    }

    /// <summary>
    /// Memperbarui daftar item perbandingan yang terlihat berdasarkan status expand/collapse setiap folder
    /// serta opsi filter 'Show only different'.
    /// </summary>
    private void RefreshVisibleAnalyzeActions()
    {
        AnalyzeActions.Clear();

        bool showOnlyDifferent = false;
        var chkDiff = this.FindControl<CheckBox>("ChkShowOnlyDifferent");
        if (chkDiff != null)
        {
            if (chkDiff.IsChecked.HasValue)
            {
                showOnlyDifferent = chkDiff.IsChecked.Value;
            }
        }

        var visibleItems = SyncActionViewModel.FlattenVisible(_comparisonRoots, showOnlyDifferent);
        foreach (var item in visibleItems)
        {
            AnalyzeActions.Add(item);
        }
    }

    /// <summary>
    /// Event saat pengguna mencentang atau menghilangkan centang 'Show only different'.
    /// </summary>
    private void OnShowOnlyDifferentClick(object? sender, RoutedEventArgs e)
    {
        RefreshVisibleAnalyzeActions();
    }

    /// <summary>
    /// Event saat tombol expand/reduce (▾ / ▸) pada folder ditekan oleh pengguna.
    /// </summary>
    private void OnToggleFolderExpandClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn)
        {
            if (btn.Tag is SyncActionViewModel vm)
            {
                if (vm.IsDirectory)
                {
                    vm.IsExpanded = !vm.IsExpanded;
                    RefreshVisibleAnalyzeActions();
                }
            }
        }
    }

    /// <summary>
    /// Event double tap / klik ganda pada baris item perbandingan untuk toggle buka/tutup folder.
    /// </summary>
    private void OnListAnalyzeDoubleTapped(object? sender, RoutedEventArgs e)
    {
        var listBox = sender as ListBox;
        if (listBox != null)
        {
            if (listBox.SelectedItem is SyncActionViewModel vm)
            {
                if (vm.IsDirectory)
                {
                    vm.IsExpanded = !vm.IsExpanded;
                    RefreshVisibleAnalyzeActions();
                }
            }
        }
    }

    /// <summary>
    /// Mengubah status expand seluruh hierarki folder secara rekursif.
    /// </summary>
    private void SetExpandRecursive(IEnumerable<SyncActionViewModel> items, bool expand)
    {
        foreach (var item in items)
        {
            if (item.IsDirectory)
            {
                item.IsExpanded = expand;
                if (item.Children.Count > 0)
                {
                    SetExpandRecursive(item.Children, expand);
                }
            }
        }
    }

    /// <summary>
    /// Membuka seluruh folder pada hasil perbandingan.
    /// </summary>
    private void OnExpandAllFoldersClick(object? sender, RoutedEventArgs e)
    {
        if (_comparisonRoots.Count > 0)
        {
            SetExpandRecursive(_comparisonRoots, true);
            RefreshVisibleAnalyzeActions();
        }
    }

    /// <summary>
    /// Menutup / melipat (reduce) seluruh folder pada hasil perbandingan.
    /// </summary>
    private void OnCollapseAllFoldersClick(object? sender, RoutedEventArgs e)
    {
        if (_comparisonRoots.Count > 0)
        {
            SetExpandRecursive(_comparisonRoots, false);
            RefreshVisibleAnalyzeActions();
        }
    }

    /// <summary>
    /// Mengambil item hasil perbandingan yang sedang diklik kanan dari ContextMenu.
    /// </summary>
    private SyncActionViewModel? GetSelectedCompareItem(object? sender)
    {
        if (sender is MenuItem mi)
        {
            if (mi.DataContext is SyncActionViewModel vm1)
            {
                return vm1;
            }

            if (mi.Parent is ContextMenu cm)
            {
                if (cm.PlacementTarget is Control target)
                {
                    if (target.DataContext is SyncActionViewModel vm2)
                    {
                        return vm2;
                    }
                }
            }
        }

        var list = this.FindControl<ListBox>("ListAnalyzeActions");
        if (list != null)
        {
            if (list.SelectedItem is SyncActionViewModel vm3)
            {
                return vm3;
            }
        }

        return null;
    }

    /// <summary>
    /// Menambahkan baris tugas transfer baru ke antrean (1 row = 1 process) dan memulainya secara background.
    /// </summary>
    private void EnqueueTransferTask(SyncActionViewModel item, bool leftToRight)
    {
        if (_currentJob == null)
        {
            return;
        }

        string sourceBase = "";
        string targetBase = "";
        string dirText = "";

        if (leftToRight)
        {
            sourceBase = _currentJob.Left.Path ?? "";
            targetBase = _currentJob.Right.Path ?? "";
            dirText = "Left ➔ Right";
        }
        else
        {
            sourceBase = _currentJob.Right.Path ?? "";
            targetBase = _currentJob.Left.Path ?? "";
            dirText = "Right ➔ Left";
        }

        if (string.IsNullOrEmpty(sourceBase) || string.IsNullOrEmpty(targetBase))
        {
            ShowBannerMessage("Source or target folder path is not set.", true);
            return;
        }

        string rel = item.RelativePath.Replace('/', '\\');
        string sourceFull = Path.Combine(sourceBase, rel);
        string targetFull = Path.Combine(targetBase, rel);

        bool sourceExists = false;
        if (item.IsDirectory)
        {
            sourceExists = Directory.Exists(sourceFull);
        }
        else
        {
            sourceExists = File.Exists(sourceFull);
        }

        if (!sourceExists)
        {
            ShowBannerMessage("Item does not exist on disk to copy.", true);
            return;
        }

        string currentJobName = "";
        if (_currentJob != null)
        {
            currentJobName = _currentJob.Name;
        }

        var task = new TransferTaskViewModel
        {
            JobName = currentJobName,
            Name = item.DisplayName,
            RelativePath = item.RelativePath,
            Direction = dirText,
            LeftToRight = leftToRight,
            SourcePath = sourceFull,
            TargetPath = targetFull,
            IsDirectory = item.IsDirectory,
            OnTaskCompleted = () =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    UpdateQueueSummary();
                    // HANYA analisa ulang folder/file yang bersangkutan saja (tidak scan semua folder)
                    ReanalyzeSingleItem(item.RelativePath, item.IsDirectory);
                });
            }
        };

        TransferTasks.Add(task);
        UpdateQueueSummary();

        // Mulai proses transfer baris ini secara mandiri
        _ = task.StartAsync();

        ShowBannerMessage("Added '" + item.DisplayName + "' to transfer queue.", false);
    }

    /// <summary>
    /// Menganalisa ulang HANYA satu folder atau file tertentu yang baru saja selesai disinkronkan,
    /// tanpa menganalisa ulang seluruh folder pada job.
    /// </summary>
    private void ReanalyzeSingleItem(string relativePath, bool isDirectory)
    {
        if (_currentJob == null || string.IsNullOrEmpty(relativePath))
        {
            return;
        }

        string leftBase = _currentJob.Left.Path ?? "";
        string rightBase = _currentJob.Right.Path ?? "";

        if (string.IsNullOrEmpty(leftBase) || string.IsNullOrEmpty(rightBase))
        {
            return;
        }

        var targetNode = FindNodeByRelativePath(_comparisonRoots, relativePath);
        if (targetNode == null)
        {
            return;
        }

        string leftTarget = Path.Combine(leftBase, targetNode.RelativePath.Replace('/', '\\'));
        string rightTarget = Path.Combine(rightBase, targetNode.RelativePath.Replace('/', '\\'));

        if (!targetNode.IsDirectory)
        {
            // Update untuk satu file tunggal
            bool leftExists = File.Exists(leftTarget);
            bool rightExists = File.Exists(rightTarget);

            targetNode.HasLeft = leftExists;
            targetNode.HasRight = rightExists;

            if (leftExists && rightExists)
            {
                try
                {
                    var fiLeft = new FileInfo(leftTarget);
                    var fiRight = new FileInfo(rightTarget);

                    targetNode.LeftTotalBytes = fiLeft.Length;
                    targetNode.RightTotalBytes = fiRight.Length;
                    targetNode.LeftDateTime = fiLeft.LastWriteTimeUtc;
                    targetNode.RightDateTime = fiRight.LastWriteTimeUtc;

                    bool sameSize = (fiLeft.Length == fiRight.Length);
                    bool sameTime = Math.Abs((fiLeft.LastWriteTimeUtc - fiRight.LastWriteTimeUtc).TotalSeconds) <= 2;

                    if (sameSize && sameTime)
                    {
                        targetNode.HasDifference = false;
                    }
                    else
                    {
                        targetNode.HasDifference = true;
                    }
                }
                catch
                {
                    targetNode.HasDifference = true;
                }
            }
            else
            {
                targetNode.HasDifference = true;
            }
        }
        else
        {
            // Update untuk folder beserta seluruh anak di dalamnya
            bool leftDirExists = Directory.Exists(leftTarget);
            bool rightDirExists = Directory.Exists(rightTarget);

            targetNode.HasLeft = leftDirExists;
            targetNode.HasRight = rightDirExists;

            // Baca informasi disk hanya untuk folder ini saja
            var diskFiles = new Dictionary<string, (long leftSize, DateTime leftMod, bool hasLeft, long rightSize, DateTime rightMod, bool hasRight)>(StringComparer.OrdinalIgnoreCase);

            if (leftDirExists)
            {
                try
                {
                    var files = Directory.GetFiles(leftTarget, "*", SearchOption.AllDirectories);
                    foreach (string file in files)
                    {
                        string subRel = Path.GetRelativePath(leftTarget, file).Replace('\\', '/');
                        try
                        {
                            var fi = new FileInfo(file);
                            diskFiles[subRel] = (fi.Length, fi.LastWriteTimeUtc, true, 0, DateTime.MinValue, false);
                        }
                        catch
                        {
                        }
                    }
                }
                catch
                {
                }
            }

            if (rightDirExists)
            {
                try
                {
                    var files = Directory.GetFiles(rightTarget, "*", SearchOption.AllDirectories);
                    foreach (string file in files)
                    {
                        string subRel = Path.GetRelativePath(rightTarget, file).Replace('\\', '/');
                        try
                        {
                            var fi = new FileInfo(file);
                            if (diskFiles.TryGetValue(subRel, out var existing))
                            {
                                diskFiles[subRel] = (existing.leftSize, existing.leftMod, existing.hasLeft, fi.Length, fi.LastWriteTimeUtc, true);
                            }
                            else
                            {
                                diskFiles[subRel] = (0, DateTime.MinValue, false, fi.Length, fi.LastWriteTimeUtc, true);
                            }
                        }
                        catch
                        {
                        }
                    }
                }
                catch
                {
                }
            }

            // Perbarui status setiap file di dalam subpohon folder ini
            void UpdateChildFiles(SyncActionViewModel folder)
            {
                folder.HasLeft = Directory.Exists(Path.Combine(leftBase, folder.RelativePath.Replace('/', '\\')));
                folder.HasRight = Directory.Exists(Path.Combine(rightBase, folder.RelativePath.Replace('/', '\\')));

                foreach (var child in folder.Children)
                {
                    if (child.IsDirectory)
                    {
                        UpdateChildFiles(child);
                    }
                    else
                    {
                        string subRel = "";
                        string folderPrefix = targetNode.RelativePath.Replace('\\', '/').Trim('/');
                        string childRel = child.RelativePath.Replace('\\', '/').Trim('/');

                        if (childRel.StartsWith(folderPrefix, StringComparison.OrdinalIgnoreCase))
                        {
                            subRel = childRel.Substring(folderPrefix.Length).TrimStart('/');
                        }
                        else
                        {
                            subRel = child.DisplayName;
                        }

                        if (diskFiles.TryGetValue(subRel, out var info))
                        {
                            child.HasLeft = info.hasLeft;
                            child.HasRight = info.hasRight;
                            child.LeftTotalBytes = info.leftSize;
                            child.RightTotalBytes = info.rightSize;
                            child.LeftDateTime = info.leftMod;
                            child.RightDateTime = info.rightMod;

                            if (info.hasLeft && info.hasRight)
                            {
                                bool sameSize = (info.leftSize == info.rightSize);
                                bool sameTime = Math.Abs((info.leftMod - info.rightMod).TotalSeconds) <= 2;
                                if (sameSize && sameTime)
                                {
                                    child.HasDifference = false;
                                    child.HasOrphanOnlyDiff = false;
                                }
                                else
                                {
                                    child.HasDifference = true;
                                    child.HasOrphanOnlyDiff = false;
                                }
                            }
                            else
                            {
                                child.HasDifference = true;
                                child.HasOrphanOnlyDiff = true;
                            }
                        }
                    }
                }
            }

            UpdateChildFiles(targetNode);

            // Hitung ulang statistik ukuran dan perbedaan folder
            RecomputeFolderStats(targetNode);
        }

        // Jika node memiliki parent, perbarui juga statistik parent sampai ke root
        var currentParent = targetNode.Parent;
        while (currentParent != null)
        {
            RecomputeFolderStats(currentParent);
            currentParent = currentParent.Parent;
        }

        // Perbarui tampilan item yang terlihat
        RefreshVisibleAnalyzeActions();

        // Perbarui ringkasan status di bagian bawah
        UpdateAnalyzeSummaryStats();
    }

    /// <summary>
    /// Mencari objek SyncActionViewModel berdasarkan relative path-nya di dalam pohon.
    /// </summary>
    private SyncActionViewModel? FindNodeByRelativePath(IEnumerable<SyncActionViewModel> nodes, string targetRelPath)
    {
        string targetNorm = targetRelPath.Replace('\\', '/').Trim('/');

        foreach (var node in nodes)
        {
            string nodeNorm = node.RelativePath.Replace('\\', '/').Trim('/');
            if (nodeNorm.Equals(targetNorm, StringComparison.OrdinalIgnoreCase))
            {
                return node;
            }

            if (node.IsDirectory && node.Children.Count > 0)
            {
                var found = FindNodeByRelativePath(node.Children, targetRelPath);
                if (found != null)
                {
                    return found;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Menghitung ulang total ukuran dan status perbedaan pada folder berdasarkan anak-anaknya.
    /// </summary>
    private void RecomputeFolderStats(SyncActionViewModel folder)
    {
        long leftTotal = 0;
        long rightTotal = 0;
        DateTime leftMax = DateTime.MinValue;
        DateTime rightMax = DateTime.MinValue;
        bool anyDiff = false;
        int diffCount = 0;
        int orphanCount = 0;

        foreach (var child in folder.Children)
        {
            if (child.IsDirectory)
            {
                RecomputeFolderStats(child);
                leftTotal += child.LeftTotalBytes;
                rightTotal += child.RightTotalBytes;
                if (child.LeftDateTime > leftMax)
                {
                    leftMax = child.LeftDateTime;
                }
                if (child.RightDateTime > rightMax)
                {
                    rightMax = child.RightDateTime;
                }
                if (child.HasDifference || child.IsOrphan)
                {
                    anyDiff = true;
                    diffCount++;
                }
                if (child.IsOrphan)
                {
                    orphanCount++;
                }
            }
            else
            {
                if (child.HasLeft)
                {
                    leftTotal += child.LeftTotalBytes;
                    if (child.LeftDateTime > leftMax)
                    {
                        leftMax = child.LeftDateTime;
                    }
                }
                if (child.HasRight)
                {
                    rightTotal += child.RightTotalBytes;
                    if (child.RightDateTime > rightMax)
                    {
                        rightMax = child.RightDateTime;
                    }
                }
                if (child.HasDifference || child.IsOrphan)
                {
                    anyDiff = true;
                    diffCount++;
                }
                if (child.IsOrphan)
                {
                    orphanCount++;
                }
            }
        }

        folder.LeftTotalBytes = leftTotal;
        folder.RightTotalBytes = rightTotal;
        folder.LeftDateTime = leftMax;
        folder.RightDateTime = rightMax;
        folder.HasDifference = anyDiff;

        if (diffCount > 0 && diffCount == orphanCount)
        {
            folder.HasOrphanOnlyDiff = true;
        }
        else
        {
            folder.HasOrphanOnlyDiff = false;
        }
    }

    /// <summary>
    /// Memperbarui teks ringkasan status bar analisa di bagian bawah secara cepat.
    /// </summary>
    private void UpdateAnalyzeSummaryStats()
    {
        int remainingDiffs = 0;
        long remainingBytes = 0;

        void CountRemaining(SyncActionViewModel node)
        {
            if (!node.IsDirectory)
            {
                if (node.HasDifference || node.IsOrphan)
                {
                    remainingDiffs++;
                    remainingBytes += node.LeftTotalBytes;
                }
            }
            else
            {
                foreach (var child in node.Children)
                {
                    CountRemaining(child);
                }
            }
        }

        foreach (var root in _comparisonRoots)
        {
            CountRemaining(root);
        }

        var txtSummary = this.FindControl<TextBlock>("TxtAnalyzeSummary");
        if (txtSummary != null)
        {
            if (remainingDiffs > 0)
            {
                txtSummary.Text = "Analysis complete: " + remainingDiffs + " changes (" + SyncActionViewModel.FormatBytes(remainingBytes) + ") to synchronize.";
            }
            else
            {
                txtSummary.Text = "Analysis complete: Folders are in sync. No changes needed.";
            }
        }

        var txtOpStats = this.FindControl<TextBlock>("TxtOperationStats");
        if (txtOpStats != null)
        {
            if (remainingDiffs > 0)
            {
                txtOpStats.Text = "Copy " + remainingDiffs + " file(s) to right, " + SyncActionViewModel.FormatBytes(remainingBytes);
            }
            else
            {
                txtOpStats.Text = "Folders are in sync. 0 files to copy.";
            }
        }
    }

    /// <summary>
    /// Memperbarui ringkasan total tugas transfer di bar antrean.
    /// </summary>
    private void UpdateQueueSummary()
    {
        var txtSummary = this.FindControl<TextBlock>("TxtQueueSummary");
        if (txtSummary != null)
        {
            txtSummary.Text = "(" + TransferTasks.Count + " tasks)";
        }
    }

    /// <summary>
    /// Klik tombol Pause pada baris tugas transfer.
    /// </summary>
    private void OnTaskPauseClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn)
        {
            if (btn.Tag is TransferTaskViewModel task)
            {
                task.Pause();
                SetMainJobStatus(MainJobState.Paused, "Transfer paused (" + task.Name + "). Click Resume to continue.");
            }
        }
    }

    /// <summary>
    /// Klik tombol Resume pada baris tugas transfer.
    /// </summary>
    private void OnTaskResumeClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn)
        {
            if (btn.Tag is TransferTaskViewModel task)
            {
                task.Resume();
                SetMainJobStatus(MainJobState.Running, "Resuming transfer (" + task.Name + ")...");
            }
        }
    }

    /// <summary>
    /// Klik tombol Stop pada baris tugas transfer.
    /// </summary>
    private void OnTaskStopClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn)
        {
            if (btn.Tag is TransferTaskViewModel task)
            {
                task.Stop();
                SetMainJobStatus(MainJobState.Paused, "Transfer stopped (" + task.Name + ").");
            }
        }
    }

    /// <summary>
    /// Klik tombol Retry pada baris tugas transfer yang gagal atau dihentikan.
    /// </summary>
    private void OnTaskRetryClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn)
        {
            if (btn.Tag is TransferTaskViewModel task)
            {
                task.Retry();
            }
        }
    }

    /// <summary>
    /// Klik tombol Remove / Delete pada baris tugas transfer untuk menghapusnya dari tabel.
    /// </summary>
    private void OnTaskDeleteClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn)
        {
            if (btn.Tag is TransferTaskViewModel task)
            {
                TransferTasks.Remove(task);
                UpdateQueueSummary();
            }
        }
    }

    /// <summary>
    /// Membersihkan semua tugas transfer yang telah selesai (Completed) dari tabel.
    /// </summary>
    private void OnClearCompletedTasksClick(object? sender, RoutedEventArgs e)
    {
        var completedList = TransferTasks.Where(t => t.Status == TransferTaskStatus.Completed).ToList();
        foreach (var task in completedList)
        {
            TransferTasks.Remove(task);
        }
        UpdateQueueSummary();
    }

    /// <summary>
    /// Klik kanan menu: Copy file/folder dari sisi Kiri ke sisi Kanan.
    /// </summary>
    private void OnContextCopyLeftToRightClick(object? sender, RoutedEventArgs e)
    {
        var item = GetSelectedCompareItem(sender);
        if (item == null)
        {
            return;
        }

        EnqueueTransferTask(item, true);
    }

    /// <summary>
    /// Klik kanan menu: Copy file/folder dari sisi Kanan ke sisi Kiri.
    /// </summary>
    private void OnContextCopyRightToLeftClick(object? sender, RoutedEventArgs e)
    {
        var item = GetSelectedCompareItem(sender);
        if (item == null)
        {
            return;
        }

        EnqueueTransferTask(item, false);
    }

    /// <summary>
    /// Klik kanan menu: Hapus (Delete) file/folder dengan dialog konfirmasi.
    /// </summary>
    private async void OnContextDeleteClick(object? sender, RoutedEventArgs e)
    {
        var item = GetSelectedCompareItem(sender);
        if (item == null || _currentJob == null)
        {
            return;
        }

        string leftBase = _currentJob.Left.Path ?? "";
        string rightBase = _currentJob.Right.Path ?? "";

        string rel = item.RelativePath.Replace('/', '\\');
        string leftFull = Path.Combine(leftBase, rel);
        string rightFull = Path.Combine(rightBase, rel);

        bool hasLeft = item.HasLeft || File.Exists(leftFull) || Directory.Exists(leftFull);
        bool hasRight = item.HasRight || File.Exists(rightFull) || Directory.Exists(rightFull);

        if (!hasLeft && !hasRight)
        {
            ShowBannerMessage("Item does not exist on disk.", true);
            return;
        }

        var dialog = new DeleteCompareItemDialog(item.DisplayName, item.IsDirectory, hasLeft, hasRight);
        var result = await dialog.ShowDialog<DeleteCompareItemResult>(this);

        if (result == null || !result.Confirmed)
        {
            return;
        }

        try
        {
            if (result.TargetSide == DeleteTargetSide.LeftOnly || result.TargetSide == DeleteTargetSide.Both)
            {
                ForceDeletePath(leftFull, item.IsDirectory);
            }

            if (result.TargetSide == DeleteTargetSide.RightOnly || result.TargetSide == DeleteTargetSide.Both)
            {
                ForceDeletePath(rightFull, item.IsDirectory);
            }

            ShowBannerMessage("Successfully deleted '" + item.DisplayName + "'.", false);

            // Perbarui hasil analisa
            OnAnalyzeClicked(null, new RoutedEventArgs());
        }
        catch (Exception ex)
        {
            ShowBannerMessage("Error deleting item: " + ex.Message, true);
        }
    }

    /// <summary>
    /// Klik kanan menu: Rename file/folder dengan dialog input nama baru.
    /// </summary>
    private async void OnContextRenameClick(object? sender, RoutedEventArgs e)
    {
        var item = GetSelectedCompareItem(sender);
        if (item == null || _currentJob == null)
        {
            return;
        }

        string leftBase = _currentJob.Left.Path ?? "";
        string rightBase = _currentJob.Right.Path ?? "";

        string rel = item.RelativePath.Replace('/', '\\');
        string leftFull = Path.Combine(leftBase, rel);
        string rightFull = Path.Combine(rightBase, rel);

        bool hasLeft = item.HasLeft || File.Exists(leftFull) || Directory.Exists(leftFull);
        bool hasRight = item.HasRight || File.Exists(rightFull) || Directory.Exists(rightFull);

        var dialog = new RenameCompareItemDialog(item.DisplayName, item.IsDirectory, hasLeft && hasRight);
        var result = await dialog.ShowDialog<RenameCompareItemResult>(this);

        if (result == null || !result.Confirmed || string.IsNullOrWhiteSpace(result.NewName))
        {
            return;
        }

        string newName = result.NewName;
        if (newName.Equals(item.DisplayName, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            if (hasLeft)
            {
                string? parentDir = Path.GetDirectoryName(leftFull);
                if (!string.IsNullOrEmpty(parentDir))
                {
                    string newLeftFull = Path.Combine(parentDir, newName);
                    if (item.IsDirectory)
                    {
                        if (Directory.Exists(leftFull))
                        {
                            Directory.Move(leftFull, newLeftFull);
                        }
                    }
                    else
                    {
                        if (File.Exists(leftFull))
                        {
                            File.Move(leftFull, newLeftFull);
                        }
                    }
                }
            }

            if (hasRight && (result.RenameBothSides || !hasLeft))
            {
                string? parentDir = Path.GetDirectoryName(rightFull);
                if (!string.IsNullOrEmpty(parentDir))
                {
                    string newRightFull = Path.Combine(parentDir, newName);
                    if (item.IsDirectory)
                    {
                        if (Directory.Exists(rightFull))
                        {
                            Directory.Move(rightFull, newRightFull);
                        }
                    }
                    else
                    {
                        if (File.Exists(rightFull))
                        {
                            File.Move(rightFull, newRightFull);
                        }
                    }
                }
            }

            ShowBannerMessage("Successfully renamed to '" + newName + "'.", false);

            // Perbarui hasil analisa
            OnAnalyzeClicked(null, new RoutedEventArgs());
        }
        catch (Exception ex)
        {
            ShowBannerMessage("Error renaming item: " + ex.Message, true);
        }
    }

    /// <summary>
    /// Klik kanan menu: Properties untuk melihat detail perbandingan file/folder.
    /// </summary>
    private async void OnContextPropertiesClick(object? sender, RoutedEventArgs e)
    {
        var item = GetSelectedCompareItem(sender);
        if (item == null || _currentJob == null)
        {
            return;
        }

        string leftBase = _currentJob.Left.Path ?? "";
        string rightBase = _currentJob.Right.Path ?? "";

        var dialog = new PropertiesCompareItemDialog(item, leftBase, rightBase);
        await dialog.ShowDialog(this);
    }

    /// <summary>
    /// Menambahkan aturan filter exclude ke job saat ini dan memperbarui konfigurasi serta UI.
    /// </summary>
    private void AddExcludeRuleToCurrentJob(string rule)
    {
        if (_currentJob == null)
        {
            ShowBannerMessage("Please select a job first.", true);
            return;
        }

        if (_jobStore == null)
        {
            return;
        }

        string trimmedRule = rule.Trim();
        if (string.IsNullOrEmpty(trimmedRule))
        {
            return;
        }

        if (_currentJob.Filter == null)
        {
            _currentJob.Filter = new FilterOptions();
        }

        if (_currentJob.Filter.Exclude == null)
        {
            _currentJob.Filter.Exclude = new List<string>();
        }

        // Cek apakah aturan ini sudah ada di dalam list exclude
        bool alreadyExists = false;
        foreach (var existing in _currentJob.Filter.Exclude)
        {
            if (string.Equals(existing.Trim(), trimmedRule, StringComparison.OrdinalIgnoreCase))
            {
                alreadyExists = true;
                break;
            }
        }

        if (!alreadyExists)
        {
            _currentJob.Filter.Exclude.Add(trimmedRule);
            _jobStore.Update(_currentJob);

            // Perbarui tampilan panel filter jika sedang dibuka
            LoadFilterToPanel(_currentJob);

            AppendDebugLog("Filter", "Added exclude rule: " + trimmedRule);
            ShowBannerMessage("Excluded '" + trimmedRule + "'. Click Analyze to apply changes.", false);
        }
        else
        {
            ShowBannerMessage("Rule '" + trimmedRule + "' is already in Exclude filters.", false);
        }
    }

    /// <summary>
    /// Klik kanan menu: Exclude berdasarkan Nama file/folder.
    /// </summary>
    private void OnContextExcludeByNameClick(object? sender, RoutedEventArgs e)
    {
        var item = GetSelectedCompareItem(sender);
        if (item == null)
        {
            return;
        }

        string name = item.DisplayName.Trim();
        if (string.IsNullOrEmpty(name))
        {
            return;
        }

        string rule = "name " + name;
        AddExcludeRuleToCurrentJob(rule);
    }

    /// <summary>
    /// Klik kanan menu: Exclude berdasarkan Path relatif file/folder.
    /// </summary>
    private void OnContextExcludeByPathClick(object? sender, RoutedEventArgs e)
    {
        var item = GetSelectedCompareItem(sender);
        if (item == null)
        {
            return;
        }

        string relPath = item.RelativePath.Replace('\\', '/').Trim('/');
        if (string.IsNullOrEmpty(relPath))
        {
            return;
        }

        string rule = "";
        if (item.IsDirectory)
        {
            rule = "path */" + relPath + "/*";
        }
        else
        {
            rule = "path */" + relPath;
        }

        AddExcludeRuleToCurrentJob(rule);
    }

    /// <summary>
    /// Klik kanan menu pada pohon direktori (TreeLeft / TreeRight): Exclude folder dari job.
    /// </summary>
    private void OnTreeExcludeFolderClick(object? sender, RoutedEventArgs e)
    {
        var menuItem = sender as MenuItem;
        if (menuItem == null)
        {
            return;
        }

        var node = menuItem.DataContext as FsTreeNode;
        if (node == null)
        {
            return;
        }

        string folderName = node.Name.Trim();
        if (string.IsNullOrEmpty(folderName))
        {
            return;
        }

        string rule = "name " + folderName;
        AddExcludeRuleToCurrentJob(rule);
    }

    /// <summary>
    /// Menyalin direktori secara rekursif ke target tujuan.
    /// </summary>
    private static void CopyDirectoryRecursive(string sourceDir, string targetDir)
    {
        Directory.CreateDirectory(targetDir);

        foreach (string file in Directory.GetFiles(sourceDir))
        {
            string destFile = Path.Combine(targetDir, Path.GetFileName(file));
            File.Copy(file, destFile, true);
        }

        foreach (string dir in Directory.GetDirectories(sourceDir))
        {
            string destSubDir = Path.Combine(targetDir, Path.GetFileName(dir));
            CopyDirectoryRecursive(dir, destSubDir);
        }
    }

    /// <summary>
    /// Menghapus file atau direktori beserta atribut ReadOnly jika terkunci.
    /// </summary>
    private static void ForceDeletePath(string path, bool isDirectory)
    {
        if (isDirectory)
        {
            if (Directory.Exists(path))
            {
                foreach (string file in Directory.GetFiles(path, "*", SearchOption.AllDirectories))
                {
                    var attrs = File.GetAttributes(file);
                    if (attrs.HasFlag(FileAttributes.ReadOnly))
                    {
                        File.SetAttributes(file, attrs & ~FileAttributes.ReadOnly);
                    }
                }
                Directory.Delete(path, true);
            }
        }
        else
        {
            if (File.Exists(path))
            {
                var attrs = File.GetAttributes(path);
                if (attrs.HasFlag(FileAttributes.ReadOnly))
                {
                    File.SetAttributes(path, attrs & ~FileAttributes.ReadOnly);
                }
                File.Delete(path);
            }
        }
    }

    /// <summary>
    /// <summary>
    /// Mengambil objek JobConfig yang diklik dari ContextMenu atau fallback ke _currentJob.
    /// Mendukung DataContext berupa JobSidebarItem maupun JobConfig.
    /// </summary>
    private JobConfig? GetJobFromSender(object? sender)
    {
        var menuItem = sender as MenuItem;
        if (menuItem != null)
        {
            var sidebarItem = menuItem.DataContext as JobSidebarItem;
            if (sidebarItem != null && sidebarItem.Job != null)
            {
                return sidebarItem.Job;
            }

            var job = menuItem.DataContext as JobConfig;
            if (job != null)
            {
                return job;
            }
        }

        return _currentJob;
    }

    /// <summary>
    /// Klik kanan menu 'Analyze' pada daftar job sidebar.
    /// </summary>
    private void OnJobContextAnalyzeClick(object? sender, RoutedEventArgs e)
    {
        var job = GetJobFromSender(sender);
        if (job == null)
        {
            return;
        }

        if (_currentJob != job)
        {
            SelectJobInSidebar(job);
            SelectJob(job);
        }

        OnAnalyzeClicked(sender, e);
    }

    /// <summary>
    /// Klik kanan menu 'Synchronize' pada daftar job sidebar.
    /// </summary>
    private async void OnJobContextSyncClick(object? sender, RoutedEventArgs e)
    {
        var job = GetJobFromSender(sender);
        if (job == null)
        {
            return;
        }

        if (_currentJob != job)
        {
            SelectJobInSidebar(job);
            SelectJob(job);
        }

        if (_currentPlan == null)
        {
            // Jika belum memiliki plan, jalankan analisa dulu
            OnAnalyzeClicked(sender, e);
            return;
        }

        await ExecuteGroupedSyncAsync(job, _currentPlan);
    }

    /// <summary>
    /// Klik kanan menu 'Analyze and Sync' pada daftar job sidebar.
    /// </summary>
    private async void OnJobContextAnalyzeAndSyncClick(object? sender, RoutedEventArgs e)
    {
        var job = GetJobFromSender(sender);
        if (job == null)
        {
            job = _currentJob;
        }

        if (job == null)
        {
            return;
        }

        var plan = await RunAnalysisAsync(job);
        if (plan != null)
        {
            if (plan.HasChanges || plan.CopyCount > 0 || plan.DeleteCount > 0)
            {
                await ExecuteGroupedSyncAsync(job, plan);
            }
        }
    }

    /// <summary>
    /// Menu All -> Analyze All (F6): Jalankan analisa secara berurutan untuk semua Job.
    /// </summary>
    private async void OnAnalyzeAllClicked(object? sender, RoutedEventArgs e)
    {
        if (JobList == null || JobList.Count == 0)
        {
            ShowBannerMessage("No jobs available to analyze.", true);
            return;
        }

        ShowBannerMessage("Starting analysis for all jobs...", false);
        var allJobs = JobList.ToList();
        for (int i = 0; i < allJobs.Count; i++)
        {
            var job = allJobs[i];
            SelectJobInSidebar(job);
            SelectJob(job);

            await RunAnalysisAsync(job);
        }

        ShowBannerMessage("All jobs have been analyzed successfully.", false);
    }

    /// <summary>
    /// Menu All -> Synchronize All (F8): Jalankan sinkronisasi untuk semua Job yang telah dianalisa.
    /// </summary>
    private async void OnSyncAllClicked(object? sender, RoutedEventArgs e)
    {
        if (JobList == null || JobList.Count == 0)
        {
            ShowBannerMessage("No jobs available to synchronize.", true);
            return;
        }

        ShowBannerMessage("Synchronizing all jobs...", false);
        var allJobs = JobList.ToList();
        for (int i = 0; i < allJobs.Count; i++)
        {
            var job = allJobs[i];
            SelectJobInSidebar(job);
            SelectJob(job);

            // Jika job saat ini belum dianalisa, lakukan analisa terlebih dahulu
            SyncPlan? plan = _currentPlan;
            if (plan == null)
            {
                plan = await RunAnalysisAsync(job);
            }

            if (plan != null)
            {
                if (plan.HasChanges || plan.CopyCount > 0 || plan.DeleteCount > 0)
                {
                    await ExecuteGroupedSyncAsync(job, plan);
                }
            }
        }

        ShowBannerMessage("All jobs synchronization completed.", false);
    }

    /// <summary>
    /// Menu All -> Analyze and Sync All (F10): Analisa lalu langsung sinkronkan semua Job.
    /// </summary>
    private async void OnAnalyzeAndSyncAllClicked(object? sender, RoutedEventArgs e)
    {
        if (JobList == null || JobList.Count == 0)
        {
            ShowBannerMessage("No jobs available.", true);
            return;
        }

        ShowBannerMessage("Starting Analyze and Sync for all jobs...", false);
        var allJobs = JobList.ToList();
        for (int i = 0; i < allJobs.Count; i++)
        {
            var job = allJobs[i];
            SelectJobInSidebar(job);
            SelectJob(job);

            var plan = await RunAnalysisAsync(job);
            if (plan != null)
            {
                if (plan.HasChanges || plan.CopyCount > 0 || plan.DeleteCount > 0)
                {
                    await ExecuteGroupedSyncAsync(job, plan);
                }
            }
        }

        ShowBannerMessage("Analyze and Sync completed for all jobs.", false);
    }

    /// <summary>
    /// Menu All -> Stop All: Hentikan analisa yang sedang berjalan dan semua tugas di antrean transfer.
    /// </summary>
    private void OnStopAllClicked(object? sender, RoutedEventArgs e)
    {
        // Batalkan proses scanning / analisa aktif
        if (_activeAnalysisCts != null)
        {
            _activeAnalysisCts.Cancel();
        }

        // Hentikan semua tugas transfer aktif pada daftar antrean (TransferTasks)
        int stoppedCount = 0;
        foreach (var task in TransferTasks.ToList())
        {
            if (task.Status == TransferTaskStatus.Running || task.Status == TransferTaskStatus.Paused || task.Status == TransferTaskStatus.Reconnecting)
            {
                task.Stop();
                stoppedCount++;
            }
        }

        if (stoppedCount > 0)
        {
            ShowBannerMessage("Stopped " + stoppedCount + " active transfer task(s).", false);
        }
        else
        {
            ShowBannerMessage("All processes stopped.", false);
        }
    }

    /// <summary>
    /// Menu All -> AutoRun OFF / ON (F4): Aktifkan atau jeda seluruh otomatisasi background secara global.
    /// </summary>
    private void OnAutoRunToggleClicked(object? sender, RoutedEventArgs e)
    {
        if (_autoRunner == null)
        {
            return;
        }

        bool isCurrentlySuspended = _autoRunner.IsSuspended;
        bool targetSuspendedState = !isCurrentlySuspended;

        _autoRunner.SetSuspended(targetSuspendedState);
        UpdateAutoRunMenuDisplay();

        if (targetSuspendedState)
        {
            ShowBannerMessage("AutoRun is now OFF (paused globally).", false);
        }
        else
        {
            ShowBannerMessage("AutoRun is now ON (active globally).", false);
        }
    }

    /// <summary>
    /// Memperbarui teks header pada menu AutoRun di tab All sesuai status suspensi saat ini.
    /// </summary>
    private void UpdateAutoRunMenuDisplay()
    {
        var menuAuto = this.FindControl<MenuItem>("MenuAutoRunToggle");
        if (menuAuto == null)
        {
            return;
        }

        if (_autoRunner != null && _autoRunner.IsSuspended)
        {
            menuAuto.Header = "AutoRun ON";
        }
        else
        {
            menuAuto.Header = "AutoRun OFF";
        }
    }

    /// <summary>
    /// Menu Tools -> Export All Jobs: Mengekspor semua konfigurasi job ke file JSON.
    /// </summary>
    private async void OnExportAllJobsClick(object? sender, RoutedEventArgs e)
    {
        if (_jobStore == null)
        {
            return;
        }

        var storageProvider = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storageProvider == null)
        {
            return;
        }

        var saveOptions = new FilePickerSaveOptions
        {
            Title = "Export All Jobs",
            SuggestedFileName = "EasyFileSync_Jobs_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".json",
            DefaultExtension = "json",
            FileTypeChoices = new List<FilePickerFileType>
            {
                new FilePickerFileType("JSON files (*.json)")
                {
                    Patterns = new[] { "*.json" }
                },
                new FilePickerFileType("All files (*.*)")
                {
                    Patterns = new[] { "*.*" }
                }
            }
        };

        var targetFile = await storageProvider.SaveFilePickerAsync(saveOptions);
        if (targetFile != null)
        {
            try
            {
                var allJobs = _jobStore.Jobs.ToList();

                var serializerOptions = new JsonSerializerOptions
                {
                    WriteIndented = true,
                    Converters = { new JsonStringEnumConverter() }
                };

                string exportedJson = JsonSerializer.Serialize(allJobs, serializerOptions);

                await using (var stream = await targetFile.OpenWriteAsync())
                await using (var writer = new StreamWriter(stream, System.Text.Encoding.UTF8))
                {
                    await writer.WriteAsync(exportedJson);
                }

                string displayPath = targetFile.Name;
                string? localPath = targetFile.TryGetLocalPath();
                if (!string.IsNullOrEmpty(localPath))
                {
                    displayPath = localPath;
                }

                AppendDebugLog("Tools", "Exported " + allJobs.Count + " jobs to " + displayPath);
                ShowBannerMessage("Successfully exported " + allJobs.Count + " jobs.", false);
            }
            catch (Exception ex)
            {
                AppendDebugLog("Error", "Export jobs failed: " + ex.Message);
                ShowBannerMessage("Export failed: " + ex.Message, true);
            }
        }
    }

    /// <summary>
    /// Menu Tools -> Import All Jobs: Mengimpor konfigurasi job dari file JSON.
    /// </summary>
    private async void OnImportAllJobsClick(object? sender, RoutedEventArgs e)
    {
        if (_jobStore == null)
        {
            return;
        }

        var storageProvider = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storageProvider == null)
        {
            return;
        }

        var openOptions = new FilePickerOpenOptions
        {
            Title = "Import All Jobs",
            AllowMultiple = false,
            FileTypeFilter = new List<FilePickerFileType>
            {
                new FilePickerFileType("JSON files (*.json)")
                {
                    Patterns = new[] { "*.json" }
                },
                new FilePickerFileType("All files (*.*)")
                {
                    Patterns = new[] { "*.*" }
                }
            }
        };

        var files = await storageProvider.OpenFilePickerAsync(openOptions);
        if (files != null && files.Count > 0)
        {
            var selectedFile = files[0];
            try
            {
                string jsonContent = "";
                await using (var stream = await selectedFile.OpenReadAsync())
                using (var reader = new StreamReader(stream, System.Text.Encoding.UTF8))
                {
                    jsonContent = await reader.ReadToEndAsync();
                }

                if (string.IsNullOrWhiteSpace(jsonContent))
                {
                    ShowBannerMessage("The selected file is empty.", true);
                    return;
                }

                var serializerOptions = new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    Converters = { new JsonStringEnumConverter() }
                };

                List<JobConfig>? importedJobs = null;

                // Coba deserialisasi sebagai kumpulan (List) job
                try
                {
                    importedJobs = JsonSerializer.Deserialize<List<JobConfig>>(jsonContent, serializerOptions);
                }
                catch
                {
                    // Jika bukan format List, coba deserialisasi sebagai single job
                    var singleJob = JsonSerializer.Deserialize<JobConfig>(jsonContent, serializerOptions);
                    if (singleJob != null)
                    {
                        importedJobs = new List<JobConfig> { singleJob };
                    }
                }

                if (importedJobs == null || importedJobs.Count == 0)
                {
                    ShowBannerMessage("No valid job configuration found in the selected file.", true);
                    return;
                }

                int importedCount = 0;
                foreach (var job in importedJobs)
                {
                    if (job != null)
                    {
                        if (!string.IsNullOrWhiteSpace(job.Name))
                        {
                            // Pastikan objek filter terinisialisasi dengan aman
                            if (job.Filter == null)
                            {
                                job.Filter = new FilterOptions();
                            }
                            if (job.Filter.Include == null)
                            {
                                job.Filter.Include = new List<string>();
                            }
                            if (job.Filter.Exclude == null)
                            {
                                job.Filter.Exclude = new List<string>();
                            }

                            _jobStore.Update(job);
                            importedCount = importedCount + 1;
                        }
                    }
                }

                LoadJobs();

                string displayPath = selectedFile.Name;
                string? localPath = selectedFile.TryGetLocalPath();
                if (!string.IsNullOrEmpty(localPath))
                {
                    displayPath = localPath;
                }

                AppendDebugLog("Tools", "Imported " + importedCount + " jobs from " + displayPath);
                ShowBannerMessage("Successfully imported " + importedCount + " jobs.", false);
            }
            catch (Exception ex)
            {
                AppendDebugLog("Error", "Import jobs failed: " + ex.Message);
                ShowBannerMessage("Import failed: " + ex.Message, true);
            }
        }
    }

    /// <summary>
    /// Klik kanan menu 'Options...' pada daftar job sidebar.
    /// </summary>
    private async void OnJobContextOptionsClick(object? sender, RoutedEventArgs e)
    {
        var job = GetJobFromSender(sender);
        if (job == null)
        {
            return;
        }

        if (_currentJob != job)
        {
            SelectJobInSidebar(job);
            SelectJob(job);
        }

        var dialog = new JobOptionsDialog(job, _jobStore);
        var result = await dialog.ShowDialog<bool>(this);
        if (result)
        {
            UpdateDirectionArrow(job.Direction);
            UpdateAutoButtonDisplay(job);
            _autoRunner?.Reload();
            Title = "EasyFileSync - " + job.Name;
            ShowBannerMessage("Job options updated successfully.", false);
        }
    }

    /// <summary>
    /// Klik tombol Auto pada toolbar kanan untuk mengatur jadwal dan pemicu otomatis.
    /// </summary>
    private async void OnAutoButtonClicked(object? sender, RoutedEventArgs e)
    {
        if (_currentJob == null)
        {
            return;
        }

        var dialog = new AutoSyncDialog(_currentJob, _jobStore);
        var result = await dialog.ShowDialog<bool>(this);
        if (result)
        {
            // Perbarui scheduler dan watcher di engine AutoRunner
            _autoRunner?.Reload();

            // Perbarui tampilan status tombol Auto di toolbar
            UpdateAutoButtonDisplay(_currentJob);

            if (_currentJob.Auto.AnyEnabled)
            {
                ShowBannerMessage("Automated synchronization enabled for '" + _currentJob.Name + "'.", false);
            }
            else
            {
                ShowBannerMessage("Automated synchronization disabled for '" + _currentJob.Name + "'.", false);
            }
        }
    }

    /// <summary>
    /// Klik kanan menu 'Auto...' pada daftar job di sidebar.
    /// </summary>
    private async void OnJobContextAutoClick(object? sender, RoutedEventArgs e)
    {
        var job = GetJobFromSender(sender);
        if (job == null)
        {
            job = _currentJob;
        }

        if (job == null)
        {
            return;
        }

        if (_currentJob != job)
        {
            SelectJobInSidebar(job);
            SelectJob(job);
        }

        var dialog = new AutoSyncDialog(job, _jobStore);
        var result = await dialog.ShowDialog<bool>(this);
        if (result)
        {
            // Perbarui scheduler dan watcher di engine AutoRunner
            _autoRunner?.Reload();

            // Perbarui tampilan status tombol Auto di toolbar
            UpdateAutoButtonDisplay(job);

            if (job.Auto.AnyEnabled)
            {
                ShowBannerMessage("Automated synchronization enabled for '" + job.Name + "'.", false);
            }
            else
            {
                ShowBannerMessage("Automated synchronization disabled for '" + job.Name + "'.", false);
            }
        }
    }

    /// <summary>
    /// Klik kanan menu 'Rename' pada daftar job sidebar.
    /// </summary>
    private async void OnJobContextRenameClick(object? sender, RoutedEventArgs e)
    {
        var job = GetJobFromSender(sender);
        if (job == null)
        {
            return;
        }

        if (_currentJob != job)
        {
            SelectJobInSidebar(job);
            SelectJob(job);
        }

        var dialog = new RenameJobDialog(job.Name, _jobStore);
        var newName = await dialog.ShowDialog<string?>(this);
        if (!string.IsNullOrWhiteSpace(newName))
        {
            string oldName = job.Name;
            if (_jobStore != null)
            {
                _jobStore.Rename(oldName, newName);
                _jobStore.Save();
            }

            if (_jobUiStates.TryGetValue(oldName, out var state))
            {
                _jobUiStates.Remove(oldName);
                _jobUiStates[newName] = state;
            }

            job.Name = newName;
            Title = "EasyFileSync - " + newName;

            // Muat ulang daftar job di sidebar agar tampilan ter-refresh
            LoadJobs();
            SelectJobInSidebar(job);
            SelectJob(job);

            ShowBannerMessage("Job renamed to '" + newName + "' successfully.", false);
        }
    }

    /// <summary>
    /// Klik kanan menu 'Delete' pada daftar job sidebar.
    /// </summary>
    private async void OnJobContextDeleteClick(object? sender, RoutedEventArgs e)
    {
        var job = GetJobFromSender(sender);
        if (job == null)
        {
            return;
        }

        var dialog = new ConfirmDeleteDialog(job.Name);
        var confirmed = await dialog.ShowDialog<bool>(this);
        if (confirmed)
        {
            string deletedName = job.Name;
            if (_jobStore != null)
            {
                _jobStore.Remove(deletedName);
                _jobStore.Save();
            }

            _jobUiStates.Remove(deletedName);

            JobList.Remove(job);

            if (_currentJob == job)
            {
                LoadJobs();
                if (_jobStore != null && _jobStore.Jobs.Count > 0)
                {
                    var firstJob = _jobStore.Jobs[0];
                    SelectJobInSidebar(firstJob);
                    SelectJob(firstJob);
                }
                else
                {
                    _currentJob = null;
                    Title = "EasyFileSync";
                    _selectedLeftPath = "";
                    _selectedRightPath = "";
                    _currentPlan = null;
                    _comparisonRoots.Clear();
                    AnalyzeActions.Clear();
                    UpdateFolderBoxesDisplay();
                    SetActiveView(ActiveView.Tutorial);
                }
            }
            else
            {
                LoadJobs();
            }

            ShowBannerMessage("Job '" + deletedName + "' deleted successfully.", false);
        }
    }

    /// <summary>
    /// Klik kanan menu 'Clone' pada daftar job sidebar.
    /// </summary>
    private void OnJobContextCloneClick(object? sender, RoutedEventArgs e)
    {
        var job = GetJobFromSender(sender);
        if (job == null)
        {
            return;
        }

        string cloneName = "";
        if (_jobStore != null)
        {
            cloneName = _jobStore.UniqueName(job.Name + " - Copy");
        }
        else
        {
            cloneName = job.Name + " - Copy";
        }

        var cloneJob = new JobConfig
        {
            Name = cloneName,
            Direction = job.Direction,
            Left = new EndpointConfig
            {
                Kind = job.Left.Kind,
                Path = job.Left.Path,
                Host = job.Left.Host,
                Port = job.Left.Port,
                User = job.Left.User,
                PasswordProtected = job.Left.PasswordProtected
            },
            Right = new EndpointConfig
            {
                Kind = job.Right.Kind,
                Path = job.Right.Path,
                Host = job.Right.Host,
                Port = job.Right.Port,
                User = job.Right.User,
                PasswordProtected = job.Right.PasswordProtected
            },
            Options = new SyncOptions
            {
                PropagateDeletions = job.Options.PropagateDeletions,
                DetectMovesAndRenames = job.Options.DetectMovesAndRenames,
                CompareAllByChecksum = job.Options.CompareAllByChecksum,
                ConflictPolicy = job.Options.ConflictPolicy
            }
        };

        if (_jobStore != null)
        {
            _jobStore.Add(cloneJob);
            _jobStore.Save();
        }

        JobList.Add(cloneJob);
        LoadJobs();
        SelectJobInSidebar(cloneJob);
        SelectJob(cloneJob);
        ShowBannerMessage("Job cloned as '" + cloneName + "'.", false);
    }

    /// <summary>
    /// Menjalankan eksekusi sinkronisasi file secara paralel dengan grouping per folder utama (root folder).
    /// Setiap folder utama memiliki 1 baris tugas di antrean transfer (Transfer Queue) yang berjalan mandiri.
    /// </summary>
    private async Task ExecuteGroupedSyncAsync(JobConfig job, SyncPlan plan)
    {
        var btnStop = this.FindControl<Button>("BtnStop");
        if (btnStop != null)
        {
            btnStop.IsVisible = true;
        }

        var btnSync = this.FindControl<Button>("BtnSync");
        if (btnSync != null)
        {
            btnSync.IsEnabled = false;
        }

        var btnAnalyze = this.FindControl<Button>("BtnAnalyze");
        if (btnAnalyze != null)
        {
            btnAnalyze.IsEnabled = false;
        }

        string leftBase = job.Left.Path ?? "";
        string rightBase = job.Right.Path ?? "";

        // 1. Kelompokkan aksi sinkronisasi berdasarkan folder utama (top-level root folder)
        var grouped = new Dictionary<string, List<SyncAction>>(StringComparer.OrdinalIgnoreCase);

        foreach (var action in plan.Actions)
        {
            if (action.Kind == ActionKind.None || action.Kind == ActionKind.Skip)
            {
                continue;
            }

            string rel = action.RelativePath.Replace('\\', '/').Trim('/');
            if (string.IsNullOrEmpty(rel))
            {
                continue;
            }

            int slashIndex = rel.IndexOf('/');
            string rootFolder;
            if (slashIndex > 0)
            {
                rootFolder = rel.Substring(0, slashIndex);
            }
            else
            {
                rootFolder = "[Root Files]";
            }

            if (!grouped.TryGetValue(rootFolder, out var list))
            {
                list = new List<SyncAction>();
                grouped[rootFolder] = list;
            }
            list.Add(action);
        }

        // 2. Buat objek TransferTaskViewModel untuk setiap folder utama
        var tasksToRun = new List<TransferTaskViewModel>();

        foreach (var entry in grouped)
        {
            string folderName = entry.Key;
            var actions = entry.Value;

            var explicitFiles = new List<(string srcPath, string dstPath, string relDisplay, long size)>();
            foreach (var a in actions)
            {
                if (a.Kind == ActionKind.CopyLeftToRight || a.Kind == ActionKind.CopyRightToLeft)
                {
                    string relPath = a.RelativePath.Replace('/', '\\');
                    string src;
                    string dst;
                    if (a.TargetSide == Side.Right)
                    {
                        src = Path.Combine(leftBase, relPath);
                        dst = Path.Combine(rightBase, relPath);
                    }
                    else
                    {
                        src = Path.Combine(rightBase, relPath);
                        dst = Path.Combine(leftBase, relPath);
                    }

                    if (File.Exists(src) || a.Size > 0 || (a.Left != null && !a.Left.IsDirectory))
                    {
                        explicitFiles.Add((src, dst, a.RelativePath, a.Size));
                    }
                }
            }

            if (explicitFiles.Count == 0)
            {
                continue;
            }

            bool isDir = true;
            if (folderName == "[Root Files]")
            {
                isDir = false;
            }

            string dirText = "Left ➔ Right";
            if (job.Direction == JobDirection.RightToLeft)
            {
                dirText = "Right ➔ Left";
            }
            else if (job.Direction == JobDirection.TwoWay)
            {
                dirText = "Synchronize (2-Way)";
            }

            string srcFolder = leftBase;
            string dstFolder = rightBase;
            if (isDir)
            {
                srcFolder = Path.Combine(leftBase, folderName);
                dstFolder = Path.Combine(rightBase, folderName);
            }

            var task = new TransferTaskViewModel
            {
                JobName = job.Name,
                Name = folderName,
                RelativePath = isDir ? folderName : "",
                Direction = dirText,
                LeftToRight = (job.Direction == JobDirection.LeftToRight),
                SourcePath = srcFolder,
                TargetPath = dstFolder,
                IsDirectory = isDir,
                ExplicitFiles = explicitFiles,
                StatsText = explicitFiles.Count + " files queued",
                OnTaskCompleted = () =>
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        UpdateQueueSummary();
                        if (isDir)
                        {
                            ReanalyzeSingleItem(folderName, true);
                        }
                    });
                }
            };

            tasksToRun.Add(task);
            TransferTasks.Add(task);
        }

        UpdateQueueSummary();

        if (tasksToRun.Count == 0)
        {
            ShowBannerMessage("No files to synchronize.", false);
            if (btnAnalyze != null)
            {
                btnAnalyze.IsEnabled = true;
            }
            if (btnStop != null)
            {
                btnStop.IsVisible = false;
            }
            return;
        }

        ShowBannerMessage("Synchronizing " + tasksToRun.Count + " folder(s) in parallel...", false);
        SetMainJobStatus(MainJobState.Running, "Synchronizing " + tasksToRun.Count + " folder(s) in parallel...");

        // 3. Eksekusi transfer paralel dengan pembatas thread (default 3, min 1, max 10)
        int maxParallel = Math.Clamp(job.Options.ParallelTransfers, 1, 10);
        var semaphore = new SemaphoreSlim(maxParallel);

        _activeAnalysisCts = new CancellationTokenSource();
        var ct = _activeAnalysisCts.Token;

        try
        {
            var runningTasks = tasksToRun.Select(async t =>
            {
                await semaphore.WaitAsync(ct);
                try
                {
                    ct.ThrowIfCancellationRequested();
                    await t.StartAsync();
                }
                catch (OperationCanceledException)
                {
                    t.Status = TransferTaskStatus.Stopped;
                }
                catch (Exception ex)
                {
                    t.Status = TransferTaskStatus.Failed;
                    t.ErrorMessage = ex.Message;
                }
                finally
                {
                    semaphore.Release();
                }
            });

            await Task.WhenAll(runningTasks);

            // Perbarui tanggal terakhir sinkronisasi pada job
            job.LastRunUtc = DateTime.UtcNow;
            _jobStore?.Save();

            SetMainJobStatus(MainJobState.LastSync, DateTime.Now.ToString("G") + " Successfully synchronized (" + plan.CopyCount + " items)");
            ShowBannerMessage("All folder synchronization completed.", false);
        }
        catch (OperationCanceledException)
        {
            ShowBannerMessage("Synchronization stopped by user.", true);
            SetMainJobStatus(MainJobState.Paused, "Synchronization stopped by user. Click Sync again to continue.");
        }
        catch (Exception ex)
        {
            ShowBannerMessage("Synchronization error: " + ex.Message, true);
            SetMainJobStatus(MainJobState.Error, "Synchronization error: " + ex.Message);
        }
        finally
        {
            if (btnStop != null)
            {
                btnStop.IsVisible = false;
            }
            if (btnAnalyze != null)
            {
                btnAnalyze.IsEnabled = true;
            }
            _activeAnalysisCts = null;
        }
    }

    /// <summary>
    /// Dipanggil saat jadwal atau pemicu AutoRunner aktif (timer, interval, jam, atau deteksi koneksi).
    /// Menjalankan simulasi seolah-olah tombol Analyze ditekan, lalu dilanjutkan dengan tombol Sync (grouping per folder utama).
    /// </summary>
    private void OnAutoJobTriggered(string jobName)
    {
        Dispatcher.UIThread.Post(async () =>
        {
            // Jangan bentrok jika sedang ada proses analisa atau transfer berjalan
            if (_activeAnalysisCts != null)
            {
                return;
            }

            JobConfig? job = null;
            if (_jobStore != null)
            {
                job = _jobStore.Find(jobName);
            }
            if (job == null)
            {
                job = _currentJob;
            }

            if (job == null)
            {
                return;
            }

            // Pastikan job aktif terpilih di antarmuka
            if (_currentJob != job)
            {
                SelectJobInSidebar(job);
                SelectJob(job);
            }

            // 1. Seolah-olah tombol Analyze ditekan
            ShowBannerMessage("Auto Sync: Running analysis for '" + job.Name + "'...", false);
            var plan = await RunAnalysisAsync(job);

            // 2. Seolah-olah tombol Sync ditekan jika ada perubahan berkas
            if (plan != null)
            {
                if (plan.HasChanges || plan.CopyCount > 0 || plan.DeleteCount > 0)
                {
                    ShowBannerMessage("Auto Sync: Running grouped sync for '" + job.Name + "'...", false);
                    await ExecuteGroupedSyncAsync(job, plan);
                }
                else
                {
                    ShowBannerMessage("Auto Sync: '" + job.Name + "' is up to date. No changes to synchronize.", false);
                }
            }
        });
    }

    // ==========================================
    // VIEW MENU, FILTERS PANEL, & DEBUG LOG PANEL
    // ==========================================

    private void OnMinimizeToTrayClick(object? sender, RoutedEventArgs e)
    {
        if (WindowState != WindowState.Minimized)
        {
            _previousWindowState = WindowState;
        }
        AppendDebugLog("View", "Window minimized to system tray via View menu.");
        Hide();
        ShowTrayNotification("EasyFileSync is running in the background.\nClick the tray icon to restore.");
    }

    private void OnToggleFiltersViewClick(object? sender, RoutedEventArgs e)
    {
        var panelFilters = this.FindControl<Border>("PanelFilters");
        var menuViewFilters = this.FindControl<MenuItem>("MenuViewFilters");
        if (panelFilters != null && menuViewFilters != null)
        {
            bool newVisibility = !panelFilters.IsVisible;
            panelFilters.IsVisible = newVisibility;
            menuViewFilters.IsChecked = newVisibility;

            if (newVisibility && _currentJob != null)
            {
                LoadFilterToPanel(_currentJob);
            }
        }
    }

    private async void OnOpenFilterHelpClick(object? sender, RoutedEventArgs e)
    {
        var dialog = new FilterHelpDialog();
        await dialog.ShowDialog(this);
    }

    /// <summary>
    /// Membuka dialog pemeriksaan pembaruan (Check for Updates).
    /// </summary>
    private async void OnCheckForUpdatesClick(object? sender, RoutedEventArgs e)
    {
        var dialog = new CheckForUpdatesDialog();
        await dialog.ShowDialog(this);
    }

    /// <summary>
    /// Membuka dialog informasi aplikasi (About EasyFileSync).
    /// </summary>
    private async void OnAboutClick(object? sender, RoutedEventArgs e)
    {
        var dialog = new AboutDialog();
        await dialog.ShowDialog(this);
    }

    private void OnCloseFiltersPanelClick(object? sender, RoutedEventArgs e)
    {
        var panelFilters = this.FindControl<Border>("PanelFilters");
        var menuViewFilters = this.FindControl<MenuItem>("MenuViewFilters");
        if (panelFilters != null)
        {
            panelFilters.IsVisible = false;
        }
        if (menuViewFilters != null)
        {
            menuViewFilters.IsChecked = false;
        }
    }

    private void OnToggleLogViewClick(object? sender, RoutedEventArgs e)
    {
        var panelLog = this.FindControl<Border>("PanelLog");
        var menuViewLog = this.FindControl<MenuItem>("MenuViewLog");
        if (panelLog != null && menuViewLog != null)
        {
            bool newVisibility = !panelLog.IsVisible;
            panelLog.IsVisible = newVisibility;
            menuViewLog.IsChecked = newVisibility;
        }
    }

    private void OnCloseLogPanelClick(object? sender, RoutedEventArgs e)
    {
        var panelLog = this.FindControl<Border>("PanelLog");
        var menuViewLog = this.FindControl<MenuItem>("MenuViewLog");
        if (panelLog != null)
        {
            panelLog.IsVisible = false;
        }
        if (menuViewLog != null)
        {
            menuViewLog.IsChecked = false;
        }
    }

    private bool _isUpdatingFilterUi = false;

    private void LoadFilterToPanel(JobConfig job)
    {
        _isUpdatingFilterUi = true;

        var txtInclude = this.FindControl<TextBox>("TxtIncludeRules");
        var txtExclude = this.FindControl<TextBox>("TxtExcludeRules");
        var chkEmpty = this.FindControl<CheckBox>("ChkExcludeEmptyFolders");
        var chkHidden = this.FindControl<CheckBox>("ChkExcludeHidden");
        var chkSystem = this.FindControl<CheckBox>("ChkExcludeSystem");
        var chkTemp = this.FindControl<CheckBox>("ChkExcludeTemp");

        if (txtInclude != null)
        {
            txtInclude.Text = string.Join(Environment.NewLine, job.Filter.Include);
        }

        if (txtExclude != null)
        {
            txtExclude.Text = string.Join(Environment.NewLine, job.Filter.Exclude);
        }

        if (chkEmpty != null)
        {
            chkEmpty.IsChecked = job.Filter.ExcludeEmptyFolders;
        }
        if (chkHidden != null)
        {
            chkHidden.IsChecked = job.Filter.ExcludeHidden;
        }
        if (chkSystem != null)
        {
            chkSystem.IsChecked = job.Filter.ExcludeSystem;
        }
        if (chkTemp != null)
        {
            chkTemp.IsChecked = job.Filter.ExcludeTemp;
        }

        _isUpdatingFilterUi = false;
    }

    private void SaveFilterFromPanel()
    {
        if (_isUpdatingFilterUi || _currentJob == null)
        {
            return;
        }

        var txtInclude = this.FindControl<TextBox>("TxtIncludeRules");
        var txtExclude = this.FindControl<TextBox>("TxtExcludeRules");
        var chkEmpty = this.FindControl<CheckBox>("ChkExcludeEmptyFolders");
        var chkHidden = this.FindControl<CheckBox>("ChkExcludeHidden");
        var chkSystem = this.FindControl<CheckBox>("ChkExcludeSystem");
        var chkTemp = this.FindControl<CheckBox>("ChkExcludeTemp");

        if (txtInclude != null && txtInclude.Text != null)
        {
            _currentJob.Filter.Include = txtInclude.Text
                .Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim())
                .Where(s => !string.IsNullOrEmpty(s))
                .ToList();
        }

        if (txtExclude != null && txtExclude.Text != null)
        {
            _currentJob.Filter.Exclude = txtExclude.Text
                .Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim())
                .Where(s => !string.IsNullOrEmpty(s))
                .ToList();
        }

        if (chkEmpty != null && chkEmpty.IsChecked.HasValue)
        {
            _currentJob.Filter.ExcludeEmptyFolders = chkEmpty.IsChecked.Value;
        }
        if (chkHidden != null && chkHidden.IsChecked.HasValue)
        {
            _currentJob.Filter.ExcludeHidden = chkHidden.IsChecked.Value;
        }
        if (chkSystem != null && chkSystem.IsChecked.HasValue)
        {
            _currentJob.Filter.ExcludeSystem = chkSystem.IsChecked.Value;
        }
        if (chkTemp != null && chkTemp.IsChecked.HasValue)
        {
            _currentJob.Filter.ExcludeTemp = chkTemp.IsChecked.Value;
        }

        _jobStore?.Save();
    }

    private void OnFilterTextChanged(object? sender, TextChangedEventArgs e)
    {
        SaveFilterFromPanel();
    }

    private void OnFilterOptionChanged(object? sender, RoutedEventArgs e)
    {
        SaveFilterFromPanel();
    }

    private async void OnCopyIncludeRulesClick(object? sender, RoutedEventArgs e)
    {
        var txt = this.FindControl<TextBox>("TxtIncludeRules");
        if (txt != null && !string.IsNullOrEmpty(txt.Text))
        {
            if (Clipboard != null)
            {
                await Clipboard.SetTextAsync(txt.Text);
                ShowBannerMessage("Include rules copied to clipboard.", false);
            }
        }
    }

    private async void OnPasteIncludeRulesClick(object? sender, RoutedEventArgs e)
    {
        var txt = this.FindControl<TextBox>("TxtIncludeRules");
        if (txt != null && Clipboard != null)
        {
#pragma warning disable CS0618
            string? clipText = await Clipboard.GetTextAsync();
#pragma warning restore CS0618
            if (!string.IsNullOrEmpty(clipText))
            {
                if (string.IsNullOrEmpty(txt.Text))
                {
                    txt.Text = clipText;
                }
                else
                {
                    txt.Text += Environment.NewLine + clipText;
                }
                SaveFilterFromPanel();
            }
        }
    }

    private void OnAddIncludeRuleClick(object? sender, RoutedEventArgs e)
    {
        var txt = this.FindControl<TextBox>("TxtIncludeRules");
        if (txt != null)
        {
            if (!string.IsNullOrWhiteSpace(txt.Text))
            {
                txt.Text = txt.Text.TrimEnd() + Environment.NewLine;
            }
            if (txt.Text != null)
            {
                txt.CaretIndex = txt.Text.Length;
            }
            txt.Focus();
        }
    }

    private void OnRemoveIncludeRuleClick(object? sender, RoutedEventArgs e)
    {
        var txt = this.FindControl<TextBox>("TxtIncludeRules");
        if (txt != null && !string.IsNullOrEmpty(txt.Text))
        {
            var lines = txt.Text.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries).ToList();
            if (lines.Count > 0)
            {
                lines.RemoveAt(lines.Count - 1);
                txt.Text = string.Join(Environment.NewLine, lines);
                SaveFilterFromPanel();
            }
        }
    }

    private async void OnCopyExcludeRulesClick(object? sender, RoutedEventArgs e)
    {
        var txt = this.FindControl<TextBox>("TxtExcludeRules");
        if (txt != null && !string.IsNullOrEmpty(txt.Text))
        {
            if (Clipboard != null)
            {
                await Clipboard.SetTextAsync(txt.Text);
                ShowBannerMessage("Exclude rules copied to clipboard.", false);
            }
        }
    }

    private async void OnPasteExcludeRulesClick(object? sender, RoutedEventArgs e)
    {
        var txt = this.FindControl<TextBox>("TxtExcludeRules");
        if (txt != null && Clipboard != null)
        {
#pragma warning disable CS0618
            string? clipText = await Clipboard.GetTextAsync();
#pragma warning restore CS0618
            if (!string.IsNullOrEmpty(clipText))
            {
                if (string.IsNullOrEmpty(txt.Text))
                {
                    txt.Text = clipText;
                }
                else
                {
                    txt.Text += Environment.NewLine + clipText;
                }
                SaveFilterFromPanel();
            }
        }
    }

    private void OnAddExcludeRuleClick(object? sender, RoutedEventArgs e)
    {
        var txt = this.FindControl<TextBox>("TxtExcludeRules");
        if (txt != null)
        {
            if (!string.IsNullOrWhiteSpace(txt.Text))
            {
                txt.Text = txt.Text.TrimEnd() + Environment.NewLine;
            }
            if (txt.Text != null)
            {
                txt.CaretIndex = txt.Text.Length;
            }
            txt.Focus();
        }
    }

    private void OnRemoveExcludeRuleClick(object? sender, RoutedEventArgs e)
    {
        var txt = this.FindControl<TextBox>("TxtExcludeRules");
        if (txt != null && !string.IsNullOrEmpty(txt.Text))
        {
            var lines = txt.Text.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries).ToList();
            if (lines.Count > 0)
            {
                lines.RemoveAt(lines.Count - 1);
                txt.Text = string.Join(Environment.NewLine, lines);
                SaveFilterFromPanel();
            }
        }
    }

    public void AppendDebugLog(string category, string message)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var txtLog = this.FindControl<TextBox>("TxtDebugLog");
            var txtSummary = this.FindControl<TextBlock>("TxtLogSummary");
            if (txtLog != null)
            {
                string line = "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "] [" + category + "] " + message;
                if (string.IsNullOrEmpty(txtLog.Text))
                {
                    txtLog.Text = line;
                }
                else
                {
                    txtLog.Text += Environment.NewLine + line;
                }

                txtLog.CaretIndex = txtLog.Text.Length;

                if (txtSummary != null)
                {
                    int count = txtLog.Text.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries).Length;
                    txtSummary.Text = "(" + count + " lines)";
                }
            }
        });
    }

    private void OnClearLogClick(object? sender, RoutedEventArgs e)
    {
        var txt = this.FindControl<TextBox>("TxtDebugLog");
        var txtSummary = this.FindControl<TextBlock>("TxtLogSummary");
        if (txt != null)
        {
            txt.Text = "";
        }
        if (txtSummary != null)
        {
            txtSummary.Text = "(0 lines)";
        }
    }

    private async void OnCopyAllLogClick(object? sender, RoutedEventArgs e)
    {
        var txt = this.FindControl<TextBox>("TxtDebugLog");
        if (txt != null && !string.IsNullOrEmpty(txt.Text))
        {
            if (Clipboard != null)
            {
                await Clipboard.SetTextAsync(txt.Text);
                ShowBannerMessage("Log copied to clipboard.", false);
            }
        }
    }
}
