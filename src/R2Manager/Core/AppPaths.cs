using System;
using System.IO;

namespace CloudStore.Core;

/// <summary>
/// 数据目录（配置 + 日志）解析。
/// <para>
/// 优先放在「程序所在目录」——用安装包装好后程序就在安装路径里，
/// 配置、密钥、日志也就跟着落在那儿，整个目录可以整体拷走或删除。
/// </para>
/// <para>
/// 目录不可写时（例如装到 Program Files 后以普通用户身份运行），
/// 退回 %APPDATA%\R2Manager，保证程序仍然可用。
/// 免安装绿色版则天然就是「程序在哪，配置就在哪」。
/// </para>
/// </summary>
public static class AppPaths
{
    private static readonly object InitLock = new();
    private static string? _dataDirectory;
    private static bool _inProgramDirectory;

    /// <summary>程序所在目录。免安装单文件版是 exe 所在处，不是运行时解压目录。</summary>
    public static string ProgramDirectory
    {
        get
        {
            try
            {
                string? exe = Environment.ProcessPath;

                if (!string.IsNullOrEmpty(exe))
                {
                    string? dir = Path.GetDirectoryName(exe);

                    if (!string.IsNullOrEmpty(dir))
                    {
                        return dir;
                    }
                }
            }
            catch (Exception)
            {
                // 取不到就退回工作目录
            }

            return AppContext.BaseDirectory;
        }
    }

    /// <summary>历史数据目录。早期版本一直把配置放在这里，升级时要从这里迁移。</summary>
    public static string LegacyDirectory
    {
        get
        {
            try
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "R2Manager");
            }
            catch (Exception)
            {
                return Path.Combine(Path.GetTempPath(), "R2Manager");
            }
        }
    }

    /// <summary>当前实际使用的配置目录。</summary>
    public static string DataDirectory
    {
        get
        {
            EnsureInitialized();
            return _dataDirectory!;
        }
    }

    /// <summary>配置是否放在程序目录（也就是安装路径）里。</summary>
    public static bool InProgramDirectory
    {
        get
        {
            EnsureInitialized();
            return _inProgramDirectory;
        }
    }

    /// <summary>给用户看的一句话说明配置到底落在哪。</summary>
    public static string DataDirectoryHint =>
        InProgramDirectory
            ? "程序所在目录（安装路径）"
            : "系统用户目录（程序目录不可写）";

    private static void EnsureInitialized()
    {
        if (_dataDirectory is not null)
        {
            return;
        }

        lock (InitLock)
        {
            if (_dataDirectory is not null)
            {
                return;
            }

            string programDir = ProgramDirectory;

            if (TryEnsureWritable(programDir))
            {
                _dataDirectory = programDir;
                _inProgramDirectory = true;
                return;
            }

            string legacy = LegacyDirectory;

            if (TryEnsureWritable(legacy))
            {
                _dataDirectory = legacy;
                _inProgramDirectory = false;
                return;
            }

            // 连用户目录都写不了（受限环境、磁盘满）——最后退到临时目录，
            // 这里同样不能抛异常，否则「找不到可写目录」会变成「程序打不开」。
            string temp = Path.Combine(Path.GetTempPath(), "R2Manager");
            TryEnsureWritable(temp);
            _dataDirectory = temp;
            _inProgramDirectory = false;
        }
    }

    /// <summary>目录能不能建、能不能写。写一个临时文件再删掉，比只看目录存在可靠。</summary>
    private static bool TryEnsureWritable(string dir)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(dir))
            {
                return false;
            }

            Directory.CreateDirectory(dir);

            string probe = Path.Combine(dir, $".write-probe-{Guid.NewGuid():N}.tmp");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
