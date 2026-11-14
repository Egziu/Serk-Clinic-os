using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Serk.Clinic.Platform.Backup;

/// <summary>
/// Encrypted container for organisation backups: <c>"SERKBAK1" | iterations (uint32 BE) | salt (16) | nonce (12) |
/// AES-256-GCM ciphertext | tag (16)</c>. The key is PBKDF2-HMAC-SHA256 of the owner's passphrase; the header is
/// authenticated as associated data, so any change to the file — or a wrong passphrase — fails decryption.
/// </summary>
public static class BackupArchive
{
    public const int DefaultIterations = 600_000;
    public const int MinPassphraseLength = 12;
    public const int MaxIterations = 2_000_000;
    private static readonly byte[] Magic = "SERKBAK1"u8.ToArray();
    private const int HeaderLength = 8 + 4 + 16 + 12;
    private const int TagLength = 16;

    public static byte[] Encrypt(byte[] plaintext, string passphrase, int iterations = DefaultIterations)
    {
        var header = new byte[HeaderLength];
        Magic.CopyTo(header, 0);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(8, 4), (uint)iterations);
        RandomNumberGenerator.Fill(header.AsSpan(12, 16));
        RandomNumberGenerator.Fill(header.AsSpan(28, 12));
        var key = DeriveKey(passphrase, header.AsSpan(12, 16), iterations);
        var output = new byte[HeaderLength + plaintext.Length + TagLength];
        header.CopyTo(output, 0);
        using var aes = new AesGcm(key, TagLength);
        aes.Encrypt(header.AsSpan(28, 12), plaintext, output.AsSpan(HeaderLength, plaintext.Length), output.AsSpan(HeaderLength + plaintext.Length, TagLength), header);
        CryptographicOperations.ZeroMemory(key);
        return output;
    }

    /// <summary>Returns the plaintext, or null when the file is not a SERK backup, was altered, or the passphrase is wrong.</summary>
    public static byte[]? TryDecrypt(ReadOnlySpan<byte> file, string passphrase, out string problem)
    {
        problem = "";
        if (file.Length < HeaderLength + TagLength || !file[..8].SequenceEqual(Magic))
        {
            problem = "This is not a SERK organisation backup file.";
            return null;
        }

        var iterations = (int)Math.Min(BinaryPrimitives.ReadUInt32BigEndian(file.Slice(8, 4)), int.MaxValue);
        if (iterations is < 100_000 or > MaxIterations)
        {
            problem = "The backup header is invalid.";
            return null;
        }

        var key = DeriveKey(passphrase, file.Slice(12, 16), iterations);
        var cipherLength = file.Length - HeaderLength - TagLength;
        var plain = new byte[cipherLength];
        try
        {
            using var aes = new AesGcm(key, TagLength);
            aes.Decrypt(file.Slice(28, 12), file.Slice(HeaderLength, cipherLength), file.Slice(HeaderLength + cipherLength, TagLength), plain, file[..HeaderLength]);
            return plain;
        }
        catch (AuthenticationTagMismatchException)
        {
            problem = "Wrong passphrase, or the file was changed or damaged.";
            return null;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private static byte[] DeriveKey(string passphrase, ReadOnlySpan<byte> salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(passphrase.Normalize(NormalizationForm.FormC)), salt, iterations, HashAlgorithmName.SHA256, 32);
}
