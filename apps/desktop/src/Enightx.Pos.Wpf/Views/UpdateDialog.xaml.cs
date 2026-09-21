using System.Windows;
using Enightx.Pos.Services;

namespace Enightx.Pos.Wpf.Views;

public partial class UpdateDialog : Window
{
    private readonly UpdateManifest _manifest;
    private readonly IUpdateService _updateService;
    private bool _isDownloading;

    public UpdateDialog(UpdateManifest manifest, string currentVersion, IUpdateService updateService)
    {
        InitializeComponent();
        _manifest = manifest;
        _updateService = updateService;

        NewVersionBadge.Text = $"v{manifest.Version} Available";
        
        bool isModular = _updateService.IsModularInstallation();
        string sizeInfo = "";
        if (isModular && manifest.ZipSizeBytes.HasValue && manifest.ZipSizeBytes.Value > 0)
        {
            sizeInfo = $" • Size: ~{manifest.ZipSizeBytes.Value / (1024.0 * 1024.0):F1} MB (Modular)";
        }
        else if (isModular && !string.IsNullOrWhiteSpace(manifest.UpdateZipUrl))
        {
            sizeInfo = " • Size: ~1.2 MB (Modular)";
        }

        CurrentVersionText.Text = $"Current installed version: v{currentVersion}{sizeInfo}";
        ReleaseNotesText.Text = string.IsNullOrWhiteSpace(manifest.ReleaseNotes) 
            ? "Performance improvements and bug fixes." 
            : manifest.ReleaseNotes;

        if (manifest.Mandatory)
        {
            LaterButton.Visibility = Visibility.Collapsed;
        }
    }

    private async void UpdateButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isDownloading) return;
        _isDownloading = true;

        UpdateButton.IsEnabled = false;
        LaterButton.IsEnabled = false;
        DownloadProgressBar.Visibility = Visibility.Visible;
        StatusText.Text = "Downloading update package...";

        var progress = new Progress<double>(pct =>
        {
            var percentInt = (int)(pct * 100);
            DownloadProgressBar.Value = percentInt;
            ProgressPercentText.Text = $"{percentInt}%";
        });

        try
        {
            var targetUrl = _updateService.GetBestDownloadUrl(_manifest);
            var downloadedPath = await _updateService.DownloadUpdateAsync(targetUrl, progress);

            StatusText.Text = "Download complete! Restarting terminal now...";
            ProgressPercentText.Text = "100%";
            await Task.Delay(800); // Brief visual confirmation before restart

            _updateService.ApplyUpdateAndRestart(downloadedPath);
        }
        catch (Exception ex)
        {
            _isDownloading = false;
            UpdateButton.IsEnabled = true;
            LaterButton.IsEnabled = true;
            DownloadProgressBar.Visibility = Visibility.Collapsed;
            ProgressPercentText.Text = "";
            StatusText.Text = $"Update failed: {ex.Message}";
            MessageBox.Show($"Unable to download update: {ex.Message}\nYou can try again later.", "Update Error", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void LaterButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isDownloading) return;
        DialogResult = false;
        Close();
    }
}
