using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace EasyFileSync.App.Views;

public partial class TrayNotificationToast : Window
{
    private readonly Action? _onActivate;
    private readonly DispatcherTimer _timer;

    public TrayNotificationToast()
    {
        InitializeComponent();
        _timer = new DispatcherTimer();
    }

    public TrayNotificationToast(string message, Action? onActivate) : this()
    {
        _onActivate = onActivate;

        var txtMessage = this.FindControl<TextBlock>("TxtMessage");
        if (txtMessage != null && !string.IsNullOrWhiteSpace(message))
        {
            txtMessage.Text = message;
        }

        var btnDismiss = this.FindControl<Button>("BtnDismiss");
        if (btnDismiss != null)
        {
            btnDismiss.Click += (sender, e) =>
            {
                _timer.Stop();
                Close();
            };
        }

        var borderContainer = this.FindControl<Border>("BorderContainer");
        if (borderContainer != null)
        {
            borderContainer.PointerPressed += (sender, e) =>
            {
                _timer.Stop();
                Close();
                _onActivate?.Invoke();
            };
        }

        // Atur posisi pop-up notifikasi tepat di atas system tray taskbar
        Opened += (sender, e) => PositionToast();

        // Pasang timer otomatis tutup setelah 4.5 detik
        _timer.Interval = TimeSpan.FromSeconds(4.5);
        _timer.Tick += (sender, e) =>
        {
            _timer.Stop();
            Close();
        };
        _timer.Start();
    }

    /// <summary>
    /// Menghitung koordinat pojok kanan bawah layar primer (di atas taskbar)
    /// dengan memperhitungkan faktor skala DPI layar.
    /// </summary>
    private void PositionToast()
    {
        var screen = Screens.Primary;
        if (screen == null)
        {
            return;
        }

        var workingArea = screen.WorkingArea;
        double scaling = screen.Scaling;

        int toastWidthPx = (int)(Width * scaling);
        int toastHeightPx = (int)(Height * scaling);
        int marginPx = (int)(16 * scaling);

        int posX = workingArea.X + workingArea.Width - toastWidthPx - marginPx;
        int posY = workingArea.Y + workingArea.Height - toastHeightPx - marginPx;

        Position = new PixelPoint(posX, posY);
    }
}

