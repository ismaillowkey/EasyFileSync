using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using EasyFileSync.Core.Engine;
using EasyFileSync.Core.Models;

namespace EasyFileSync.App.Views;

public partial class AutoSyncDialog : Window
{
    private readonly JobConfig _job;
    private readonly JobStore? _store;

    public AutoSyncDialog()
    {
        InitializeComponent();
        _job = new JobConfig();
        _store = new JobStore(new AppPaths());
    }

    public AutoSyncDialog(JobConfig job, JobStore? store)
    {
        InitializeComponent();
        _job = job;
        _store = store;

        // Tampilkan nama job pada teks sub-header
        var txtSubtitle = this.FindControl<TextBlock>("TxtJobSubtitle");
        if (txtSubtitle != null)
        {
            txtSubtitle.Text = "Configure automatic background synchronization for: " + _job.Name;
        }

        // Muat konfigurasi Auto saat ini ke elemen UI
        LoadSettings();

        // Hubungkan event interaksi
        SetupEvents();
    }

    private void LoadSettings()
    {
        var rbEnable = this.FindControl<RadioButton>("RbEnable");
        var rbDisable = this.FindControl<RadioButton>("RbDisable");
        var borderChecklist = this.FindControl<Border>("BorderChecklist");

        var chkOnFileChange = this.FindControl<CheckBox>("ChkOnFileChange");
        var txtFileChangeDelay = this.FindControl<TextBox>("TxtFileChangeDelay");

        var chkOnInterval = this.FindControl<CheckBox>("ChkOnInterval");
        var txtIntervalHours = this.FindControl<TextBox>("TxtIntervalHours");
        var txtIntervalMinutes = this.FindControl<TextBox>("TxtIntervalMinutes");

        var chkOnClock = this.FindControl<CheckBox>("ChkOnClock");
        var txtClockHour = this.FindControl<TextBox>("TxtClockHour");
        var txtClockMinute = this.FindControl<TextBox>("TxtClockMinute");

        var chkOnConnect = this.FindControl<CheckBox>("ChkOnConnect");

        // Tentukan apakah Auto aktif atau nonaktif
        bool isEnabled = false;
        if (_job.Auto.Enabled)
        {
            isEnabled = true;
        }
        else if (_job.Auto.OnFileChange || _job.Auto.OnTimer || _job.Auto.OnClock || _job.Auto.OnConnect)
        {
            // Jika ada salah satu opsi yang aktif dari pengaturan sebelumnya, anggap aktif
            isEnabled = true;
        }

        if (rbEnable != null)
        {
            rbEnable.IsChecked = isEnabled;
        }

        if (rbDisable != null)
        {
            rbDisable.IsChecked = !isEnabled;
        }

        if (borderChecklist != null)
        {
            borderChecklist.IsEnabled = isEnabled;
            if (isEnabled)
            {
                borderChecklist.Opacity = 1.0;
            }
            else
            {
                borderChecklist.Opacity = 0.55;
            }
        }

        // 1. Opsi On File Change
        if (chkOnFileChange != null)
        {
            chkOnFileChange.IsChecked = _job.Auto.OnFileChange;
        }
        if (txtFileChangeDelay != null)
        {
            int delay = _job.Auto.FileChangeDelaySec;
            if (delay < 1)
            {
                delay = 10;
            }
            txtFileChangeDelay.Text = delay.ToString();
        }

        // 2. Opsi By Interval
        if (chkOnInterval != null)
        {
            chkOnInterval.IsChecked = _job.Auto.OnTimer;
        }
        if (txtIntervalHours != null)
        {
            txtIntervalHours.Text = _job.Auto.TimerHours.ToString();
        }
        if (txtIntervalMinutes != null)
        {
            int mins = _job.Auto.TimerMinutes;
            if (mins == 0 && _job.Auto.TimerHours == 0)
            {
                mins = 30;
            }
            txtIntervalMinutes.Text = mins.ToString();
        }

        // 3. Opsi By Date / Clock
        if (chkOnClock != null)
        {
            chkOnClock.IsChecked = _job.Auto.OnClock;
        }
        if (txtClockHour != null)
        {
            txtClockHour.Text = _job.Auto.ClockHour.ToString("D2");
        }
        if (txtClockMinute != null)
        {
            txtClockMinute.Text = _job.Auto.ClockMinute.ToString("D2");
        }

        // 4. Opsi On Device Connect
        if (chkOnConnect != null)
        {
            chkOnConnect.IsChecked = _job.Auto.OnConnect;
        }

        // 5. Opsi Maximum Parallel Transfers (Dropdown 1 - 10)
        var cmbAutoParallel = this.FindControl<ComboBox>("CmbAutoParallel");
        if (cmbAutoParallel != null)
        {
            int p = _job.Options.ParallelTransfers;
            if (p < 1)
            {
                p = 3;
            }
            else if (p > 10)
            {
                p = 10;
            }
            cmbAutoParallel.SelectedIndex = p - 1;
        }
    }

    private void SetupEvents()
    {
        var rbEnable = this.FindControl<RadioButton>("RbEnable");
        var rbDisable = this.FindControl<RadioButton>("RbDisable");
        var borderChecklist = this.FindControl<Border>("BorderChecklist");

        if (rbEnable != null)
        {
            rbEnable.IsCheckedChanged += (s, e) =>
            {
                bool isEnabled = false;
                if (rbEnable.IsChecked == true)
                {
                    isEnabled = true;
                }

                if (borderChecklist != null)
                {
                    borderChecklist.IsEnabled = isEnabled;
                    if (isEnabled)
                    {
                        borderChecklist.Opacity = 1.0;
                    }
                    else
                    {
                        borderChecklist.Opacity = 0.55;
                    }
                }
            };
        }

        // Pasang validasi angka ketat dan navigasi tombol keyboard panah untuk semua input waktu/delay
        var txtFileChangeDelay = this.FindControl<TextBox>("TxtFileChangeDelay");
        if (txtFileChangeDelay != null)
        {
            SetupNumericValidation(txtFileChangeDelay, 1, 3600, 10, false);
        }

        var txtIntervalHours = this.FindControl<TextBox>("TxtIntervalHours");
        if (txtIntervalHours != null)
        {
            SetupNumericValidation(txtIntervalHours, 0, 999, 0, false);
        }

        var txtIntervalMinutes = this.FindControl<TextBox>("TxtIntervalMinutes");
        if (txtIntervalMinutes != null)
        {
            SetupNumericValidation(txtIntervalMinutes, 0, 59, 30, false);
        }

        var txtClockHour = this.FindControl<TextBox>("TxtClockHour");
        if (txtClockHour != null)
        {
            SetupNumericValidation(txtClockHour, 0, 23, 2, true);
        }

        var txtClockMinute = this.FindControl<TextBox>("TxtClockMinute");
        if (txtClockMinute != null)
        {
            SetupNumericValidation(txtClockMinute, 0, 59, 0, true);
        }

        var btnSave = this.FindControl<Button>("BtnSave");
        if (btnSave != null)
        {
            btnSave.Click += OnSaveClick;
        }

        var btnCancel = this.FindControl<Button>("BtnCancel");
        if (btnCancel != null)
        {
            btnCancel.Click += (s, e) => Close(false);
        }
    }

    /// <summary>
    /// Membatasi input TextBox hanya menerima angka 0-9 (menolak huruf seperti 'asdsa'),
    /// mendukung tombol panah Up/Down untuk menaikkan/menurunkan angka,
    /// serta merapikan dan memformat teks saat kursor meninggalkan kotak (LostFocus).
    /// </summary>
    private void SetupNumericValidation(TextBox textBox, int minValue, int maxValue, int defaultValue, bool padTwoDigits)
    {
        // 1. Tolak langsung karakter non-angka saat pengguna mengetik
        textBox.TextInput += (sender, e) =>
        {
            if (string.IsNullOrEmpty(e.Text))
            {
                return;
            }

            for (int i = 0; i < e.Text.Length; i++)
            {
                if (!char.IsDigit(e.Text[i]))
                {
                    e.Handled = true;
                    return;
                }
            }
        };

        // 2. Dukung tombol panah keyboard (Up / Down) untuk kemudahan kontrol
        textBox.KeyDown += (sender, e) =>
        {
            if (e.Key == Key.Up)
            {
                int current = defaultValue;
                if (int.TryParse(textBox.Text, out int parsed))
                {
                    current = parsed;
                }

                current++;
                if (current > maxValue)
                {
                    current = minValue;
                }

                if (padTwoDigits)
                {
                    textBox.Text = current.ToString("D2");
                }
                else
                {
                    textBox.Text = current.ToString();
                }
                e.Handled = true;
            }
            else if (e.Key == Key.Down)
            {
                int current = defaultValue;
                if (int.TryParse(textBox.Text, out int parsed))
                {
                    current = parsed;
                }

                current--;
                if (current < minValue)
                {
                    current = maxValue;
                }

                if (padTwoDigits)
                {
                    textBox.Text = current.ToString("D2");
                }
                else
                {
                    textBox.Text = current.ToString();
                }
                e.Handled = true;
            }
        };

        // 3. Saat meninggalkan kotak input (LostFocus), rapikan angka dan batasi rentang min-max
        textBox.LostFocus += (sender, e) =>
        {
            int val = defaultValue;
            if (int.TryParse(textBox.Text, out int parsed))
            {
                val = parsed;
            }

            if (val < minValue)
            {
                val = minValue;
            }
            else if (val > maxValue)
            {
                val = maxValue;
            }

            if (padTwoDigits)
            {
                textBox.Text = val.ToString("D2");
            }
            else
            {
                textBox.Text = val.ToString();
            }
        };
    }

    private void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        var rbEnable = this.FindControl<RadioButton>("RbEnable");
        var chkOnFileChange = this.FindControl<CheckBox>("ChkOnFileChange");
        var txtFileChangeDelay = this.FindControl<TextBox>("TxtFileChangeDelay");

        var chkOnInterval = this.FindControl<CheckBox>("ChkOnInterval");
        var txtIntervalHours = this.FindControl<TextBox>("TxtIntervalHours");
        var txtIntervalMinutes = this.FindControl<TextBox>("TxtIntervalMinutes");

        var chkOnClock = this.FindControl<CheckBox>("ChkOnClock");
        var txtClockHour = this.FindControl<TextBox>("TxtClockHour");
        var txtClockMinute = this.FindControl<TextBox>("TxtClockMinute");

        var chkOnConnect = this.FindControl<CheckBox>("ChkOnConnect");

        // Simpan status Enable / Disable
        if (rbEnable != null && rbEnable.IsChecked == true)
        {
            _job.Auto.Enabled = true;
        }
        else
        {
            _job.Auto.Enabled = false;
        }

        // 1. On File Change
        if (chkOnFileChange != null)
        {
            if (chkOnFileChange.IsChecked == true)
            {
                _job.Auto.OnFileChange = true;
            }
            else
            {
                _job.Auto.OnFileChange = false;
            }
        }

        if (txtFileChangeDelay != null)
        {
            if (int.TryParse(txtFileChangeDelay.Text, out int delaySec))
            {
                if (delaySec < 1)
                {
                    delaySec = 1;
                }
                _job.Auto.FileChangeDelaySec = delaySec;
            }
            else
            {
                _job.Auto.FileChangeDelaySec = 10;
            }
        }

        // 2. By Interval
        if (chkOnInterval != null)
        {
            if (chkOnInterval.IsChecked == true)
            {
                _job.Auto.OnTimer = true;
            }
            else
            {
                _job.Auto.OnTimer = false;
            }
        }

        int hours = 0;
        if (txtIntervalHours != null && int.TryParse(txtIntervalHours.Text, out int h))
        {
            if (h >= 0)
            {
                hours = h;
            }
        }
        _job.Auto.TimerHours = hours;

        int minutes = 30;
        if (txtIntervalMinutes != null && int.TryParse(txtIntervalMinutes.Text, out int m))
        {
            if (m >= 0)
            {
                minutes = m;
            }
        }
        _job.Auto.TimerMinutes = minutes;

        // 3. By Date / Clock
        if (chkOnClock != null)
        {
            if (chkOnClock.IsChecked == true)
            {
                _job.Auto.OnClock = true;
            }
            else
            {
                _job.Auto.OnClock = false;
            }
        }

        int clockH = 2;
        if (txtClockHour != null && int.TryParse(txtClockHour.Text, out int ch))
        {
            if (ch < 0)
            {
                clockH = 0;
            }
            else if (ch > 23)
            {
                clockH = 23;
            }
            else
            {
                clockH = ch;
            }
        }
        _job.Auto.ClockHour = clockH;

        int clockM = 0;
        if (txtClockMinute != null && int.TryParse(txtClockMinute.Text, out int cm))
        {
            if (cm < 0)
            {
                clockM = 0;
            }
            else if (cm > 59)
            {
                clockM = 59;
            }
            else
            {
                clockM = cm;
            }
        }
        _job.Auto.ClockMinute = clockM;

        // 4. On Device Connect
        if (chkOnConnect != null)
        {
            if (chkOnConnect.IsChecked == true)
            {
                _job.Auto.OnConnect = true;
            }
            else
            {
                _job.Auto.OnConnect = false;
            }
        }

        // 5. Maximum Parallel Transfers
        var cmbAutoParallel = this.FindControl<ComboBox>("CmbAutoParallel");
        if (cmbAutoParallel != null && cmbAutoParallel.SelectedIndex >= 0)
        {
            int p = cmbAutoParallel.SelectedIndex + 1;
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

        // Simpan perubahan ke penyimpanan job
        _store?.Save();

        // Tutup dialog dengan hasil true
        Close(true);
    }
}
