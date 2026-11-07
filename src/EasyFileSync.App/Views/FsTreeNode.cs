using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using EasyFileSync.Core.Storage;

namespace EasyFileSync.App.Views;

public enum FsNodeType
{
    ThisComputer,
    LocalGroup,
    Folder,
    Drive,
    SmbGroup,
    SmbServer,
    SmbShare
}

public class FsTreeNode : INotifyPropertyChanged
{
    private bool _isExpanded;
    private bool _isSelected;
    private bool _hasLoadedChildren;

    public string Name { get; set; } = "";
    public string Comment { get; set; } = "";
    public string FullPath { get; set; } = "";
    public FsNodeType NodeType { get; set; } = FsNodeType.Folder;

    public bool HasLoadedChildren
    {
        get
        {
            return _hasLoadedChildren;
        }
    }

    public bool IsThisComputer
    {
        get
        {
            return NodeType == FsNodeType.ThisComputer;
        }
    }

    public bool IsLocalGroup
    {
        get
        {
            return NodeType == FsNodeType.LocalGroup;
        }
    }

    public bool IsSmb
    {
        get
        {
            if (NodeType == FsNodeType.SmbGroup)
            {
                return true;
            }
            if (NodeType == FsNodeType.SmbServer)
            {
                return true;
            }
            if (NodeType == FsNodeType.SmbShare)
            {
                return true;
            }
            return false;
        }
    }

    public bool IsFolderOrDrive
    {
        get
        {
            if (NodeType == FsNodeType.Folder)
            {
                return true;
            }
            if (NodeType == FsNodeType.Drive)
            {
                return true;
            }
            return false;
        }
    }

    public ObservableCollection<FsTreeNode> Children { get; } = new();

    public event PropertyChangedEventHandler? PropertyChanged;

    public bool IsSelected
    {
        get
        {
            return _isSelected;
        }
        set
        {
            if (_isSelected != value)
            {
                _isSelected = value;
                OnPropertyChanged(nameof(IsSelected));
            }
        }
    }

    public bool IsExpanded
    {
        get
        {
            return _isExpanded;
        }
        set
        {
            if (_isExpanded != value)
            {
                _isExpanded = value;
                OnPropertyChanged(nameof(IsExpanded));

                // Jika di-expand dan belum pernah memuat subfolder, muat subfolder sekarang
                if (_isExpanded)
                {
                    if (!_hasLoadedChildren)
                    {
                        LoadSubFolders();
                    }
                }
            }
        }
    }

    protected void OnPropertyChanged(string propertyName)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    /// <summary>
    /// Memuat daftar subfolder dari path fisik lokal atau share SMB.
    /// </summary>
    public void LoadSubFolders()
    {
        _hasLoadedChildren = true;
        Children.Clear();

        // 1. Jika ini adalah SMB Server (misal \\192.168.2.10), muat daftar share-nya
        if (NodeType == FsNodeType.SmbServer)
        {
            try
            {
                string serverHost = FullPath.TrimStart('\\', '/');
                var shares = SmbShareEnumerator.EnumerateShares(serverHost);

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

                    // Tambahkan placeholder agar bisa di-expand jika ada subfolder
                    shareNode.Children.Add(new FsTreeNode { Name = "Loading...", NodeType = FsNodeType.Folder });
                    Children.Add(shareNode);
                }
            }
            catch
            {
            }
            return;
        }

        // 2. Jika folder fisik biasa atau SMB Share folder
        if (string.IsNullOrWhiteSpace(FullPath))
        {
            return;
        }

        if (!Directory.Exists(FullPath))
        {
            return;
        }

        try
        {
            var dirInfo = new DirectoryInfo(FullPath);
            var subDirs = dirInfo.GetDirectories();

            foreach (var sub in subDirs)
            {
                // Abaikan folder hidden dan system agar aman dan rapi
                if ((sub.Attributes & FileAttributes.Hidden) != 0)
                {
                    continue;
                }
                if ((sub.Attributes & FileAttributes.System) != 0)
                {
                    continue;
                }

                var childNode = new FsTreeNode
                {
                    Name = sub.Name,
                    Comment = "Folder",
                    FullPath = sub.FullName,
                    NodeType = FsNodeType.Folder
                };

                // Cek apakah folder ini memiliki subfolder di dalamnya untuk memunculkan panah expander
                try
                {
                    if (sub.GetDirectories().Length > 0)
                    {
                        childNode.Children.Add(new FsTreeNode { Name = "Loading...", NodeType = FsNodeType.Folder });
                    }
                }
                catch
                {
                }

                Children.Add(childNode);
            }
        }
        catch
        {
            // Akses folder ditolak atau error I/O
        }
    }

    /// <summary>
    /// Membangun pohon struktur default "This Computer" dan "Net Shares (SMB)" sesuai GoodSync.
    /// </summary>
    public static ObservableCollection<FsTreeNode> BuildThisComputerTree()
    {
        var rootList = new ObservableCollection<FsTreeNode>();

        // 1. Node Utama: This Computer
        var thisComputerNode = new FsTreeNode
        {
            Name = "This Computer",
            Comment = "Disks mounted directly...",
            FullPath = "",
            NodeType = FsNodeType.ThisComputer,
            IsExpanded = true
        };

        // Sub-node: Local
        var localGroupNode = new FsTreeNode
        {
            Name = "Local",
            Comment = "",
            FullPath = "",
            NodeType = FsNodeType.LocalGroup,
            IsExpanded = true
        };

        // Tambahkan Folder Profil User Saat Ini
        string userProfilePath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string userName = Environment.UserName;
        if (Directory.Exists(userProfilePath))
        {
            var userNode = new FsTreeNode
            {
                Name = userName,
                Comment = "User at " + userProfilePath.Replace('\\', '/'),
                FullPath = userProfilePath,
                NodeType = FsNodeType.Folder
            };

            try
            {
                if (Directory.GetDirectories(userProfilePath).Length > 0)
                {
                    userNode.Children.Add(new FsTreeNode { Name = "Loading...", NodeType = FsNodeType.Folder });
                }
            }
            catch
            {
            }

            localGroupNode.Children.Add(userNode);
        }

        // Tambahkan semua Drive lokal (C:, D:, E:, dll)
        try
        {
            var drives = DriveInfo.GetDrives();
            foreach (var drive in drives)
            {
                if (!drive.IsReady)
                {
                    continue;
                }

                string driveLetter = drive.Name.TrimEnd('\\');
                string displayName = driveLetter;
                if (!string.IsNullOrWhiteSpace(drive.VolumeLabel))
                {
                    displayName = driveLetter + " " + drive.VolumeLabel;
                }
                else
                {
                    displayName = driveLetter;
                }

                string driveTypeStr = "Fixed Disk";
                if (drive.DriveType == DriveType.CDRom)
                {
                    driveTypeStr = "DVD/CD";
                }
                else if (drive.DriveType == DriveType.Removable)
                {
                    driveTypeStr = "Removable Disk";
                }
                else if (drive.DriveType == DriveType.Network)
                {
                    driveTypeStr = "Network Drive";
                }

                string comment = driveTypeStr + " (" + drive.DriveFormat + ")";

                var driveNode = new FsTreeNode
                {
                    Name = displayName,
                    Comment = comment,
                    FullPath = drive.RootDirectory.FullName,
                    NodeType = FsNodeType.Drive
                };

                try
                {
                    if (drive.RootDirectory.GetDirectories().Length > 0)
                    {
                        driveNode.Children.Add(new FsTreeNode { Name = "Loading...", NodeType = FsNodeType.Folder });
                    }
                }
                catch
                {
                }

                localGroupNode.Children.Add(driveNode);
            }
        }
        catch
        {
        }

        thisComputerNode.Children.Add(localGroupNode);
        rootList.Add(thisComputerNode);

        // 2. Node Utama: Net Shares (Win-SMB)
        var smbGroupNode = new FsTreeNode
        {
            Name = "Net Shares (Win-SMB)",
            Comment = "Windows Network Shares (SMB)...",
            FullPath = "",
            NodeType = FsNodeType.SmbGroup,
            IsExpanded = true
        };

        rootList.Add(smbGroupNode);

        return rootList;
    }
}
