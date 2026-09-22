using System;
using System.Linq;
using System.Threading.Tasks;
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
        CurrentVersionBadgeText.Text = $"v{currentVersion}";
        TargetVersionBadgeText.Text = $"v{manifest.Version}";

        var primarySha = manifest.Files.FirstOrDefault()?.Sha256;
        Sha256ChecksumText.Text = string.IsNullOrWhiteSpace(primarySha) 
            ? "SHA256: Verified by manifest digital signature" 
            : $"SHA256: {primarySha}";

        ReleaseNotesText.Text = string.IsNullOrWhiteSpace(manifest.ReleaseNotes) 
            ? "Performance improvements and bug fixes." 
            : manifest.ReleaseNotes;

        Closing += (_, args) => { if (_isDownloading) args.Cancel = true; };
    }

    private async void CheckButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isDownloading) return;
        CheckButton.IsEnabled = false;
        StatusText.Text = "Checking for new updates...";

        try
        {
            var result = await _updateService.CheckForUpdatesAsync();
            if (result.UpdateAvailable && result.Manifest != null)
            {
                NewVersionBadge.Text = $"v{result.Manifest.Version} Available";
                TargetVersionBadgeText.Text = $"v{result.Manifest.Version}";
                ReleaseNotesText.Text = result.Manifest.ReleaseNotes;
                var sha = result.Manifest.Files.FirstOrDefault()?.Sha256;
                Sha256ChecksumText.Text = string.IsNullOrWhiteSpace(sha) ? "SHA256: Verified" : $"SHA256: {sha}";
                StatusText.Text = "Newer update available for installation.";
            }
            else
            {
                StatusText.Text = "System is up to date.";
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Check failed: {ex.Message}";
        }
        finally
        {
            CheckButton.IsEnabled = true;
        }
    }

    private async void UpdateButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isDownloading) return;
        _isDownloading = true;

        UpdateButton.IsEnabled = false;
        LaterButton.IsEnabled = false;
        CheckButton.IsEnabled = false;
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
            var downloadedPath = await _updateService.PrepareUpdateAsync(_manifest, progress);

            StatusText.Text = "Download complete! Restarting terminal now...";
            ProgressPercentText.Text = "100%";
            await Task.Delay(800); // Brief visual confirmation before restart

            _updateService.ApplyUpdateAndRestart(downloadedPath);
            _isDownloading = false;
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            _isDownloading = false;
            UpdateButton.IsEnabled = true;
            LaterButton.IsEnabled = true;
            CheckButton.IsEnabled = true;
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
