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
        
        CurrentVersionText.Text = $"Installed: v{currentVersion}. Unchanged files will be reused.";
        ReleaseNotesText.Text = string.IsNullOrWhiteSpace(manifest.ReleaseNotes) 
            ? "Performance improvements and bug fixes." 
            : manifest.ReleaseNotes;

        Closing += (_, args) => { if (_isDownloading) args.Cancel = true; };
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
