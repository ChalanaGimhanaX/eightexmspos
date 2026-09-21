using System.IO;
using System.Windows;
using Microsoft.Win32;

namespace Enightx.Pos.Wpf.Views;

public partial class BackupRestoreDialog : Window
{
    private readonly string _dbPath;

    public BackupRestoreDialog()
    {
        InitializeComponent();

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        _dbPath = Path.Combine(appData, "EnightxPOS", "enightx_local.db");

        DbPathText.Text = _dbPath;
        RefreshDbInfo();
    }

    private void RefreshDbInfo()
    {
        if (File.Exists(_dbPath))
        {
            var fi = new FileInfo(_dbPath);
            DbSizeText.Text = fi.Length > 1024 * 1024 
                ? $"{fi.Length / (1024.0 * 1024.0):F2} MB" 
                : $"{fi.Length / 1024.0:F1} KB";
        }
        else
        {
            DbSizeText.Text = "Not Found";
        }
    }

    private void ExportBackup_Click(object sender, RoutedEventArgs e)
    {
        if (!File.Exists(_dbPath))
        {
            MessageBox.Show("Active database file not found.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        try
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var backupDir = Path.Combine(appData, "EnightxPOS", "Backups");
            Directory.CreateDirectory(backupDir);

            var timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
            var backupFile = Path.Combine(backupDir, $"enightx_backup_{timestamp}.db");

            File.Copy(_dbPath, backupFile, overwrite: true);

            MessageBox.Show($"Backup created successfully!\n\nLocation:\n{backupFile}", "Backup Complete", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to create backup: {ex.Message}", "Backup Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void RestoreBackup_Click(object sender, RoutedEventArgs e)
    {
        var ofd = new OpenFileDialog
        {
            Title = "Select Enightx POS Backup File (.db)",
            Filter = "SQLite Database Files (*.db)|*.db|All Files (*.*)|*.*"
        };

        if (ofd.ShowDialog() == true)
        {
            var selectedFile = ofd.FileName;
            if (!File.Exists(selectedFile)) return;

            try
            {
                // Verify SQLite header
                using var fs = new FileStream(selectedFile, FileMode.Open, FileAccess.Read);
                var header = new byte[16];
                fs.Read(header, 0, 16);
                var headerStr = System.Text.Encoding.ASCII.GetString(header);

                if (!headerStr.StartsWith("SQLite format 3"))
                {
                    MessageBox.Show("The selected file is not a valid SQLite database backup.", "Invalid Backup", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                if (MessageBox.Show("WARNING: Restoring a backup will replace your current local database.\n\nAre you sure you wish to proceed?", 
                    "Confirm Database Restore", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
                {
                    fs.Close();
                    File.Copy(selectedFile, _dbPath, overwrite: true);
                    MessageBox.Show("Database restored successfully! The application will now close to reload the restored database.", "Restore Success", MessageBoxButton.OK, MessageBoxImage.Information);
                    Application.Current.Shutdown();
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Restore failed: {ex.Message}", "Restore Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}

