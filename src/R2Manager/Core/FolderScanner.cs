using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace CloudStore.Core;

/// <summary>扫描本地文件夹，生成 (本地路径, 对象 key) 列表。</summary>
public static class FolderScanner
{
    public static List<(string LocalPath, string Key)> Scan(string root, string keyPrefix, CancellationToken ct)
    {
        var results = new List<(string, string)>();
        string rootFull = Path.GetFullPath(root);

        if (!Directory.Exists(rootFull))
        {
            return results;
        }

        var pending = new Stack<(string Directory, string Relative)>();
        pending.Push((rootFull, string.Empty));

        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            (string directory, string relative) = pending.Pop();

            string[] files;
            string[] directories;

            try
            {
                files = Directory.GetFiles(directory);
                directories = Directory.GetDirectories(directory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (string file in files)
            {
                ct.ThrowIfCancellationRequested();

                string name = Path.GetFileName(file);
                string key = relative.Length == 0
                    ? keyPrefix + name
                    : keyPrefix + relative + "/" + name;

                results.Add((file, key));
            }

            foreach (string sub in directories)
            {
                // 跳过符号链接 / 目录联接，避免循环
                try
                {
                    if ((File.GetAttributes(sub) & FileAttributes.ReparsePoint) != 0)
                    {
                        continue;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    continue;
                }

                string name = Path.GetFileName(sub.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                string nextRelative = relative.Length == 0 ? name : relative + "/" + name;
                pending.Push((sub, nextRelative));
            }
        }

        return results;
    }
}
