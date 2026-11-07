using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media;
using Avalonia.Threading;

namespace EasyFileSync.App.Views;

public enum TransferTaskStatus
{
    Queued,
    Running,
    Paused,
    Completed,
    Stopped,
    Failed,
    Reconnecting
}

/// <summary>
/// Model untuk 1 baris tugas transfer data pada antrean transfer (Transfer Queue).
/// Mendukung Pause, Resume, Stop, Retry, dan Delete.
/// </summary>
public class TransferTaskViewModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    public Guid Id { get; } = Guid.NewGuid();
    public string JobName { get; set; } = "";
    public string Name { get; set; } = "";
    public string RelativePath { get; set; } = "";
    public string Direction { get; set; } = "Left ➔ Right";
    public bool LeftToRight { get; set; } = true;
    public string SourcePath { get; set; } = "";
    public string TargetPath { get; set; } = "";
    public bool IsDirectory { get; set; }

    /// <summary>
    /// Daftar berkas eksplisit yang akan disalin (diperoleh dari hasil analisa SyncPlan).
    /// </summary>
    public List<(string srcPath, string dstPath, string relDisplay, long size)>? ExplicitFiles { get; set; }

    private TransferTaskStatus _status = TransferTaskStatus.Queued;
    public TransferTaskStatus Status
    {
        get
        {
            return _status;
        }
        set
        {
            if (_status != value)
            {
                _status = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(StatusText));
                OnPropertyChanged(nameof(StatusBrush));
                OnPropertyChanged(nameof(CanPause));
                OnPropertyChanged(nameof(CanResume));
                OnPropertyChanged(nameof(CanStop));
                OnPropertyChanged(nameof(CanRetry));
                OnPropertyChanged(nameof(CanDelete));
            }
        }
    }

    private int _progressPercent = 0;
    public int ProgressPercent
    {
        get
        {
            return _progressPercent;
        }
        set
        {
            if (_progressPercent != value)
            {
                _progressPercent = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ProgressText));
            }
        }
    }

    public string ProgressText
    {
        get
        {
            return _progressPercent + "%";
        }
    }

    private string _currentFileName = "";
    public string CurrentFileName
    {
        get
        {
            return _currentFileName;
        }
        set
        {
            if (_currentFileName != value)
            {
                _currentFileName = value;
                OnPropertyChanged();
            }
        }
    }

    private string _statsText = "Waiting in queue...";
    public string StatsText
    {
        get
        {
            return _statsText;
        }
        set
        {
            if (_statsText != value)
            {
                _statsText = value;
                OnPropertyChanged();
            }
        }
    }

    private string _errorMessage = "";
    public string ErrorMessage
    {
        get
        {
            return _errorMessage;
        }
        set
        {
            if (_errorMessage != value)
            {
                _errorMessage = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(StatusText));
            }
        }
    }

    public string StatusText
    {
        get
        {
            if (_status == TransferTaskStatus.Queued)
            {
                return "Queued";
            }
            if (_status == TransferTaskStatus.Running)
            {
                return "Running";
            }
            if (_status == TransferTaskStatus.Paused)
            {
                return "Paused";
            }
            if (_status == TransferTaskStatus.Completed)
            {
                return "Completed";
            }
            if (_status == TransferTaskStatus.Stopped)
            {
                return "Stopped";
            }
            if (_status == TransferTaskStatus.Failed)
            {
                if (!string.IsNullOrEmpty(_errorMessage))
                {
                    return "Failed: " + _errorMessage;
                }
                return "Failed";
            }
            return "Unknown";
        }
    }

    public IBrush StatusBrush
    {
        get
        {
            if (_status == TransferTaskStatus.Running)
            {
                return new SolidColorBrush(Color.Parse("#2563EB")); // Biru
            }
            if (_status == TransferTaskStatus.Completed)
            {
                return new SolidColorBrush(Color.Parse("#16A34A")); // Hijau
            }
            if (_status == TransferTaskStatus.Paused)
            {
                return new SolidColorBrush(Color.Parse("#D97706")); // Oranye
            }
            if (_status == TransferTaskStatus.Failed)
            {
                return new SolidColorBrush(Color.Parse("#DC2626")); // Merah
            }
            if (_status == TransferTaskStatus.Stopped)
            {
                return new SolidColorBrush(Color.Parse("#64748B")); // Abu-abu
            }
            return new SolidColorBrush(Color.Parse("#6B7280"));
        }
    }

    public bool CanPause
    {
        get
        {
            return _status == TransferTaskStatus.Running;
        }
    }

    public bool CanResume
    {
        get
        {
            return _status == TransferTaskStatus.Paused;
        }
    }

    public bool CanStop
    {
        get
        {
            if (_status == TransferTaskStatus.Running)
            {
                return true;
            }
            if (_status == TransferTaskStatus.Paused)
            {
                return true;
            }
            return false;
        }
    }

    public bool CanRetry
    {
        get
        {
            if (_status == TransferTaskStatus.Failed)
            {
                return true;
            }
            if (_status == TransferTaskStatus.Stopped)
            {
                return true;
            }
            return false;
        }
    }

    public bool CanDelete
    {
        get
        {
            return _status != TransferTaskStatus.Running;
        }
    }

    // Mekanisme Pengontrol Thread Transfer
    private CancellationTokenSource? _cts;
    private ManualResetEventSlim _pauseEvent = new ManualResetEventSlim(true);
    public Action? OnTaskCompleted { get; set; }

    /// <summary>
    /// Memulai proses transfer untuk baris ini secara asynchronous.
    /// </summary>
    public async Task StartAsync()
    {
        _cts = new CancellationTokenSource();
        _pauseEvent.Set();
        var ct = _cts.Token;

        Status = TransferTaskStatus.Running;
        ErrorMessage = "";

        try
        {
            await Task.Run(async () =>
            {
                var filesToCopy = new List<(string srcPath, string dstPath, string relDisplay, long size)>();

                if (ExplicitFiles != null && ExplicitFiles.Count > 0)
                {
                    // Gunakan daftar file eksplisit hasil analisa SyncPlan
                    filesToCopy = ExplicitFiles;
                }
                else if (IsDirectory)
                {
                    if (Directory.Exists(SourcePath))
                    {
                        var allFiles = Directory.GetFiles(SourcePath, "*", SearchOption.AllDirectories);
                        foreach (string src in allFiles)
                        {
                            string subRel = Path.GetRelativePath(SourcePath, src);
                            string dst = Path.Combine(TargetPath, subRel);
                            long sz = 0;
                            try
                            {
                                var fi = new FileInfo(src);
                                sz = fi.Length;
                            }
                            catch
                            {
                            }
                            filesToCopy.Add((src, dst, subRel, sz));
                        }
                    }
                }
                else
                {
                    if (File.Exists(SourcePath))
                    {
                        long sz = 0;
                        try
                        {
                            var fi = new FileInfo(SourcePath);
                            sz = fi.Length;
                        }
                        catch
                        {
                        }
                        filesToCopy.Add((SourcePath, TargetPath, Path.GetFileName(SourcePath), sz));
                    }
                }

                int totalFiles = filesToCopy.Count;
                long totalBytes = filesToCopy.Sum(f => f.size);
                long bytesCopied = 0;
                var sw = Stopwatch.StartNew();

                for (int i = 0; i < totalFiles; i++)
                {
                    ct.ThrowIfCancellationRequested();

                    // Cek jika pengguna sedang me-pause proses
                    _pauseEvent.Wait(ct);

                    var fileInfo = filesToCopy[i];
                    string src = fileInfo.srcPath;
                    string dst = fileInfo.dstPath;
                    long sz = fileInfo.size;

                    string? parentDir = Path.GetDirectoryName(dst);
                    if (!string.IsNullOrEmpty(parentDir) && !Directory.Exists(parentDir))
                    {
                        Directory.CreateDirectory(parentDir);
                    }

                    Dispatcher.UIThread.Post(() =>
                    {
                        CurrentFileName = Path.GetFileName(src);
                    });

                    // Salin file stream dengan chunk 128KB untuk akurasi progress dan pause
                    byte[] buffer = new byte[131072];
                    await using (var srcStream = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, true))
                    await using (var dstStream = new FileStream(dst, FileMode.Create, FileAccess.Write, FileShare.None, 131072, true))
                    {
                        int bytesRead;
                        while ((bytesRead = await srcStream.ReadAsync(buffer, 0, buffer.Length, ct)) > 0)
                        {
                            // Cek pause di tengah pembacaan chunk
                            _pauseEvent.Wait(ct);

                            await dstStream.WriteAsync(buffer, 0, bytesRead, ct);
                            bytesCopied += bytesRead;

                            int percent = 0;
                            if (totalBytes > 0)
                            {
                                percent = (int)((bytesCopied * 100) / totalBytes);
                            }
                            else if (totalFiles > 0)
                            {
                                percent = (int)(((i + 1) * 100) / totalFiles);
                            }

                            double elapsedSec = sw.Elapsed.TotalSeconds;
                            double speedBytesPerSec = 0;
                            if (elapsedSec > 0.1)
                            {
                                speedBytesPerSec = bytesCopied / elapsedSec;
                            }
                            // Tampilkan kecepatan transfer dalam format MB/s
                            double speedMbPerSec = speedBytesPerSec / (1024.0 * 1024.0);
                            string speedStr = speedMbPerSec.ToString("0.00") + " MB/s";

                            Dispatcher.UIThread.Post(() =>
                            {
                                ProgressPercent = Math.Clamp(percent, 0, 100);
                                StatsText = (i + 1) + " / " + totalFiles + " files • " + speedStr;
                            });
                        }
                    }

                    // Samakan timestamp file
                    try
                    {
                        File.SetLastWriteTimeUtc(dst, File.GetLastWriteTimeUtc(src));
                    }
                    catch
                    {
                    }
                }

                sw.Stop();

                Dispatcher.UIThread.Post(() =>
                {
                    ProgressPercent = 100;
                    StatsText = totalFiles + " files copied (" + SyncActionViewModel.FormatBeyondCompareSize(totalBytes) + ") in " + sw.Elapsed.ToString(@"m\:ss");
                    CurrentFileName = "Completed";
                    Status = TransferTaskStatus.Completed;
                });
            }, ct);
        }
        catch (OperationCanceledException)
        {
            Dispatcher.UIThread.Post(() =>
            {
                Status = TransferTaskStatus.Stopped;
                StatsText = "Transfer stopped by user.";
            });
        }
        catch (Exception ex)
        {
            Dispatcher.UIThread.Post(() =>
            {
                Status = TransferTaskStatus.Failed;
                ErrorMessage = ex.Message;
                StatsText = "Error: " + ex.Message;
            });
        }
        finally
        {
            OnTaskCompleted?.Invoke();
        }
    }

    /// <summary>
    /// Jeda (Pause) transfer file.
    /// </summary>
    public void Pause()
    {
        if (Status == TransferTaskStatus.Running)
        {
            _pauseEvent.Reset();
            Status = TransferTaskStatus.Paused;
            StatsText = "Transfer paused.";
        }
    }

    /// <summary>
    /// Lanjutkan (Resume) transfer file.
    /// </summary>
    public void Resume()
    {
        if (Status == TransferTaskStatus.Paused)
        {
            _pauseEvent.Set();
            Status = TransferTaskStatus.Running;
            StatsText = "Resuming transfer...";
        }
    }

    /// <summary>
    /// Hentikan paksa (Stop) transfer file.
    /// </summary>
    public void Stop()
    {
        if (Status == TransferTaskStatus.Running || Status == TransferTaskStatus.Paused)
        {
            _pauseEvent.Set();
            if (_cts != null)
            {
                _cts.Cancel();
            }
            Status = TransferTaskStatus.Stopped;
            StatsText = "Transfer stopped.";
        }
    }

    /// <summary>
    /// Ulangi proses transfer (Retry) jika gagal atau dihentikan.
    /// </summary>
    public void Retry()
    {
        if (Status == TransferTaskStatus.Failed || Status == TransferTaskStatus.Stopped)
        {
            _ = StartAsync();
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? propName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propName));
    }
}

