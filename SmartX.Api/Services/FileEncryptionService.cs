using System.Security.Cryptography;

namespace SmartX.Api.Services;

/// <summary>
/// Encrypts every uploaded config file / photo / log with AES-256 before it touches disk.
/// A random IV is generated per file and stored in the first 16 bytes of the file.
/// </summary>
public class FileEncryptionService
{
    private const int IvLengthBytes = 16;
    private readonly byte[] _key;

    public FileEncryptionService(IConfiguration configuration)
    {
        var keyBase64 = configuration["FileEncryption:Key"];
        if (string.IsNullOrWhiteSpace(keyBase64))
            throw new InvalidOperationException("FileEncryption:Key is not configured in appsettings.json.");

        _key = Convert.FromBase64String(keyBase64);
        if (_key.Length is not (16 or 24 or 32))
            throw new InvalidOperationException("FileEncryption:Key must decode to a 16, 24 or 32-byte AES key.");
    }

    public async Task EncryptToFileAsync(Stream source, string destinationPath, CancellationToken ct = default)
    {
        using var aes = Aes.Create();
        aes.Key = _key;
        aes.GenerateIV();

        await using var destination = File.Create(destinationPath);
        await destination.WriteAsync(aes.IV, ct);

        await using var cryptoStream = new CryptoStream(destination, aes.CreateEncryptor(), CryptoStreamMode.Write);
        await source.CopyToAsync(cryptoStream, ct);
        await cryptoStream.FlushFinalBlockAsync(ct);
    }

    public async Task<MemoryStream> DecryptFromFileAsync(string sourcePath, CancellationToken ct = default)
    {
        var fileBytes = await File.ReadAllBytesAsync(sourcePath, ct);
        var iv = fileBytes[..IvLengthBytes];
        var cipherText = fileBytes[IvLengthBytes..];

        using var aes = Aes.Create();
        aes.Key = _key;
        aes.IV = iv;

        using var decryptor = aes.CreateDecryptor();
        var plainBytes = decryptor.TransformFinalBlock(cipherText, 0, cipherText.Length);
        return new MemoryStream(plainBytes);
    }
}
