using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace AyDesktop.Services;

/// <summary>
/// 用硬件指纹派生 AES 密钥加密本地文件。
///
/// 文件格式（版本 v1）：
///   [1 byte  version=1]
///   [12 bytes nonce]
///   [16 bytes tag]
///   [N bytes ciphertext]
///
/// 加密算法：AES-256-GCM
/// 密钥派生：PBKDF2-SHA256，200,000 轮
/// </summary>
public static class MachineKeyStore
{
    private const int Version = 1;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int KeySize = 32;
    private const int Pbkdf2Iterations = 200_000;

    private static readonly byte[] KdfSalt =
        Encoding.UTF8.GetBytes("AyDesktop::MachineKey::Salt::v1::DoNotChange");

    private static byte[]? _cachedKey;
    private static readonly object _keyLock = new();

    private static string Root =>
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "database");

    private static string PathOf(string key)
    {
        var safe = new string(key.Where(char.IsLetterOrDigit).ToArray());
        if (safe.Length == 0) throw new ArgumentException("key 无效", nameof(key));
        return Path.Combine(Root, safe + ".bin");
    }

    // ====================================================================
    // 公开 API
    // ====================================================================

    public static void Save(string key, string plaintext)
    {
        if (string.IsNullOrEmpty(key))
            throw new ArgumentException("key required", nameof(key));

        Directory.CreateDirectory(Root);

        byte[] aesKey = GetOrDeriveKey();
        byte[] nonce = RandomNumberGenerator.GetBytes(NonceSize);
        byte[] plain = Encoding.UTF8.GetBytes(plaintext ?? string.Empty);
        byte[] cipher = new byte[plain.Length];
        byte[] tag = new byte[TagSize];

        using (var gcm = new AesGcm(aesKey, TagSize))
        {
            gcm.Encrypt(nonce, plain, cipher, tag);
        }

        var blob = new byte[1 + NonceSize + TagSize + cipher.Length];
        int p = 0;
        blob[p++] = Version;
        Buffer.BlockCopy(nonce, 0, blob, p, NonceSize); p += NonceSize;
        Buffer.BlockCopy(tag, 0, blob, p, TagSize); p += TagSize;
        Buffer.BlockCopy(cipher, 0, blob, p, cipher.Length);

        File.WriteAllBytes(PathOf(key), blob);
        // 不再设置 Hidden 属性
    }

    public static string? Load(string key)
    {
        var path = PathOf(key);
        try
        {
            if (!File.Exists(path)) return null;

            var blob = File.ReadAllBytes(path);
            if (blob.Length < 1 + NonceSize + TagSize) return null;

            int p = 0;
            byte version = blob[p++];
            if (version != Version)
            {
                Clear(key);
                return null;
            }

            var nonce = new byte[NonceSize];
            var tag = new byte[TagSize];
            int cipherLen = blob.Length - 1 - NonceSize - TagSize;
            var cipher = new byte[cipherLen];

            Buffer.BlockCopy(blob, p, nonce, 0, NonceSize); p += NonceSize;
            Buffer.BlockCopy(blob, p, tag, 0, TagSize); p += TagSize;
            Buffer.BlockCopy(blob, p, cipher, 0, cipherLen);

            byte[] aesKey = GetOrDeriveKey();
            byte[] plain = new byte[cipherLen];

            using (var gcm = new AesGcm(aesKey, TagSize))
            {
                gcm.Decrypt(nonce, cipher, tag, plain);
            }

            return Encoding.UTF8.GetString(plain);
        }
        catch
        {
            Clear(key);
            return null;
        }
    }

    public static void Clear(string key)
    {
        try
        {
            var path = PathOf(key);
            if (File.Exists(path))
            {
                // 兼容旧版本可能残留的 Hidden 属性
                File.SetAttributes(path, FileAttributes.Normal);
                File.Delete(path);
            }
        }
        catch { }
    }

    public static bool Exists(string key) => File.Exists(PathOf(key));

    // ====================================================================
    // 内部：PBKDF2 派生 + 缓存
    // ====================================================================

    private static byte[] GetOrDeriveKey()
    {
        if (_cachedKey != null) return _cachedKey;
        lock (_keyLock)
        {
            if (_cachedKey != null) return _cachedKey;

            string hw = HardwareId.Get();
            using var kdf = new Rfc2898DeriveBytes(
                password: hw,
                salt: KdfSalt,
                iterations: Pbkdf2Iterations,
                hashAlgorithm: HashAlgorithmName.SHA256);

            _cachedKey = kdf.GetBytes(KeySize);
            return _cachedKey;
        }
    }
}