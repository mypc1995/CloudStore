using System;
using System.IO;
using System.Text.Json;

namespace CloudStore.Core;

/// <summary>配置文件的读写。Secret Access Key 使用 Windows DPAPI 加密后落盘。</summary>
public static class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// 配置目录。优先是程序所在目录（安装包安装后就是安装路径），
    /// 不可写时退回 %APPDATA%\R2Manager。
    /// </summary>
    public static string Directory
    {
        get
        {
            string dir = AppPaths.DataDirectory;

            try
            {
                System.IO.Directory.CreateDirectory(dir);
            }
            catch (Exception)
            {
                // 建不出来就直接用原路径，Save 时会再兜一次
            }

            return dir;
        }
    }

    public static string FilePath => Path.Combine(Directory, "settings.json");

    /// <summary>
    /// 旧版本把配置放在 %APPDATA%\R2Manager。改用程序目录后要把老配置搬过来，
    /// 否则用户会以为「密钥丢了」。
    /// <para>
    /// 密文是 DPAPI 加密的，熵固定为 R2Manager.v1.secret，搬文件不影响解密——
    /// 前提是熵千万别改，一改老用户的密钥就解不开了。
    /// </para>
    /// </summary>
    public static void MigrateLegacyIfNeeded()
    {
        try
        {
            string target = FilePath;

            if (File.Exists(target))
            {
                return;
            }

            string legacy = Path.Combine(AppPaths.LegacyDirectory, "settings.json");

            if (!File.Exists(legacy))
            {
                return;
            }

            if (string.Equals(
                    Path.GetFullPath(legacy),
                    Path.GetFullPath(target),
                    StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            string? dir = Path.GetDirectoryName(target);

            if (!string.IsNullOrEmpty(dir))
            {
                System.IO.Directory.CreateDirectory(dir);
            }

            File.Copy(legacy, target, false);
        }
        catch (Exception)
        {
            // 迁移失败不该影响启动，大不了用户重新填一次密钥
        }
    }

    public static StorageSettings Load(out string? warning)
    {
        warning = null;

        try
        {
            MigrateLegacyIfNeeded();

            if (!File.Exists(FilePath))
            {
                return new StorageSettings();
            }

            string json = File.ReadAllText(FilePath);
            var settings = JsonSerializer.Deserialize<StorageSettings>(json, JsonOptions) ?? new StorageSettings();

            if (settings.RememberSecret)
            {
                string? secret = SecretProtector.TryUnprotect(settings.ProtectedSecret);
                if (secret is null && !string.IsNullOrEmpty(settings.ProtectedSecret))
                {
                    warning = "本机保存的密钥无法解密（可能更换了 Windows 账户），请在设置中重新填写 Secret Access Key。";
                }

                settings.SecretAccessKey = secret ?? string.Empty;
            }
            else
            {
                settings.SecretAccessKey = string.Empty;
            }

            return settings;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            warning = "读取配置文件失败，已使用默认设置：" + ex.Message;
            return new StorageSettings();
        }
    }

    public static void Save(StorageSettings settings)
    {
        string dir = Directory;
        string temp = Path.Combine(dir, $"settings.{Guid.NewGuid():N}.tmp");
        string target = FilePath;

        var copy = settings.Clone();
        copy.SecretAccessKey = string.Empty;
        copy.ProtectedSecret = settings.RememberSecret
            ? SecretProtector.Protect(settings.SecretAccessKey)
            : string.Empty;

        string json = JsonSerializer.Serialize(copy, JsonOptions);
        File.WriteAllText(temp, json);

        if (File.Exists(target))
        {
            File.Delete(target);
        }

        File.Move(temp, target);
    }
}
