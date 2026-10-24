using System;
using System.Text;
using System.Security.Cryptography;

namespace CloudStore.Core;

/// <summary>用 Windows DPAPI（当前用户作用域）保护本地保存的 Secret Access Key。</summary>
public static class SecretProtector
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("R2Manager.v1.secret");

    public static string Protect(string? plainText)
    {
        if (string.IsNullOrEmpty(plainText))
        {
            return string.Empty;
        }

        byte[] cipher = ProtectedData.Protect(
            Encoding.UTF8.GetBytes(plainText), Entropy, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(cipher);
    }

    /// <summary>解密失败（换了 Windows 账户等）时返回 null，调用方要求重新输入。</summary>
    public static string? TryUnprotect(string? base64)
    {
        if (string.IsNullOrWhiteSpace(base64))
        {
            return null;
        }

        try
        {
            byte[] cipher = Convert.FromBase64String(base64);
            byte[] plain = ProtectedData.Unprotect(cipher, Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plain);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or ArgumentException)
        {
            return null;
        }
    }
}
