using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Enightx.Pos.Storage;

namespace Enightx.Pos.Services;

public interface ICloudBackupService
{
    Task<string> CreateEncryptedBackupAsync(string targetFilePath, string encryptionKey);
    Task<string> RestoreFromEncryptedBackupAsync(string encryptedFilePath, string targetDbPath, string encryptionKey);
    Task<bool> UploadBackupToCloudAsync(string encryptedBackupPath, string apiUrl, string tenantId, string deviceId, HttpClient? httpClient = null);
}

public class CloudBackupService : ICloudBackupService
{
    private readonly PosDatabase _db;

    public CloudBackupService(PosDatabase db)
    {
        _db = db;
    }

    public async Task<string> CreateEncryptedBackupAsync(string targetFilePath, string encryptionKey)
    {
        // 1. Snapshot database safely using SQLite backup/vacuum
        var tempSnapshot = Path.Combine(Path.GetTempPath(), $"snapshot_{Guid.NewGuid():N}.db");
        using (var sourceConn = _db.CreateConnection())
        {
            await sourceConn.OpenAsync();
            using var cmd = sourceConn.CreateCommand();
            cmd.CommandText = $"VACUUM INTO '{tempSnapshot.Replace("'", "''")}';";
            await cmd.ExecuteNonQueryAsync();
        }

        try
        {
            var rawBytes = await File.ReadAllBytesAsync(tempSnapshot);

            // 2. Encrypt using AES-256
            byte[] salt = RandomNumberGenerator.GetBytes(16);
            byte[] iv = RandomNumberGenerator.GetBytes(16);

            using var derive = new Rfc2898DeriveBytes(encryptionKey, salt, 100_000, HashAlgorithmName.SHA256);
            byte[] keyBytes = derive.GetBytes(32); // 256 bits

            using var aes = Aes.Create();
            aes.Key = keyBytes;
            aes.IV = iv;

            using var ms = new MemoryStream();
            // Header: Magic "ENXBAK01" (8 bytes) + Salt (16 bytes) + IV (16 bytes)
            byte[] magic = Encoding.ASCII.GetBytes("ENXBAK01");
            ms.Write(magic, 0, magic.Length);
            ms.Write(salt, 0, salt.Length);
            ms.Write(iv, 0, iv.Length);

            using (var cs = new CryptoStream(ms, aes.CreateEncryptor(), CryptoStreamMode.Write))
            {
                await cs.WriteAsync(rawBytes);
                await cs.FlushFinalBlockAsync();
            }

            var dir = Path.GetDirectoryName(targetFilePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            await File.WriteAllBytesAsync(targetFilePath, ms.ToArray());
            return targetFilePath;
        }
        finally
        {
            if (File.Exists(tempSnapshot))
            {
                File.Delete(tempSnapshot);
            }
        }
    }

    public async Task<string> RestoreFromEncryptedBackupAsync(string encryptedFilePath, string targetDbPath, string encryptionKey)
    {
        if (!File.Exists(encryptedFilePath))
        {
            throw new FileNotFoundException($"Backup file not found: {encryptedFilePath}");
        }

        var encryptedData = await File.ReadAllBytesAsync(encryptedFilePath);
        using var ms = new MemoryStream(encryptedData);

        // Verify magic
        byte[] magic = new byte[8];
        int read = ms.Read(magic, 0, 8);
        if (read < 8 || Encoding.ASCII.GetString(magic) != "ENXBAK01")
        {
            throw new InvalidOperationException("Invalid or corrupted backup file format.");
        }

        byte[] salt = new byte[16];
        ms.Read(salt, 0, 16);

        byte[] iv = new byte[16];
        ms.Read(iv, 0, 16);

        using var derive = new Rfc2898DeriveBytes(encryptionKey, salt, 100_000, HashAlgorithmName.SHA256);
        byte[] keyBytes = derive.GetBytes(32);

        using var aes = Aes.Create();
        aes.Key = keyBytes;
        aes.IV = iv;

        using var decryptedMs = new MemoryStream();
        using (var cs = new CryptoStream(ms, aes.CreateDecryptor(), CryptoStreamMode.Read))
        {
            await cs.CopyToAsync(decryptedMs);
        }

        var dir = Path.GetDirectoryName(targetDbPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        await File.WriteAllBytesAsync(targetDbPath, decryptedMs.ToArray());
        return targetDbPath;
    }

    public async Task<bool> UploadBackupToCloudAsync(string encryptedBackupPath, string apiUrl, string tenantId, string deviceId, HttpClient? httpClient = null)
    {
        if (!File.Exists(encryptedBackupPath)) return false;

        var client = httpClient ?? new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        var fileName = Path.GetFileName(encryptedBackupPath);

        using var content = new MultipartFormDataContent();
        var fileBytes = await File.ReadAllBytesAsync(encryptedBackupPath);
        var byteContent = new ByteArrayContent(fileBytes);
        byteContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");

        content.Add(byteContent, "file", fileName);
        content.Add(new StringContent(tenantId), "tenant_id");
        content.Add(new StringContent(deviceId), "device_id");

        try
        {
            var response = await client.PostAsync($"{apiUrl.TrimEnd('/')}/api/v1/backups/upload", content);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }
}
