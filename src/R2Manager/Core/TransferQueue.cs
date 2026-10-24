using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CloudStore.Core;

/// <summary>把本地文件/远端对象包装成传输队列里的任务项。</summary>
public static class TransferQueue
{
    public static TransferItem CreateUpload(StorageService service, StorageSettings settings, string localPath, string key, long size)
    {
        var item = new TransferItem(TransferKind.Upload, Humanize.LeafName(key), key, size)
        {
            Source = localPath,
            RemoteKey = key,
        };

        item.Work = async ct =>
        {
            var options = new UploadOptions(
                settings.MultipartThresholdBytes,
                settings.PartSizeBytes,
                settings.Concurrency,
                settings.OverwriteExisting,
                settings.SkipExisting);

            UploadResult result = await MultipartUploader
                .UploadAsync(service, localPath, key, item.Report, ct, options)
                .ConfigureAwait(false);

            if (result.Skipped)
            {
                return "已跳过：远端已存在同名对象";
            }

            return string.Equals(result.Key, key, StringComparison.Ordinal) ? null : $"已自动改名为 {result.Key}";
        };

        return item;
    }

    public static TransferItem CreateDownload(StorageService service, StorageSettings settings, string key, string localPath, long size)
    {
        var item = new TransferItem(TransferKind.Download, Humanize.LeafName(key), localPath, size)
        {
            Source = localPath,
            RemoteKey = key,
        };

        item.Work = async ct =>
        {
            var options = new DownloadOptions(
                settings.MultipartThresholdBytes,
                settings.PartSizeBytes,
                settings.Concurrency);

            await ParallelDownloader
                .DownloadAsync(service, key, localPath, item.Report, ct, options, size > 0 ? size : null)
                .ConfigureAwait(false);

            return null;
        };

        return item;
    }

    /// <summary>批量加入上传任务（files 需要给出目标 key）。</summary>
    public static int EnqueueUploads(
        StorageService service,
        StorageSettings settings,
        IEnumerable<(string LocalPath, string Key)> files)
    {
        int count = 0;

        foreach ((string localPath, string key) in files)
        {
            long size;
            try
            {
                size = new FileInfo(localPath).Length;
            }
            catch
            {
                size = 0;
            }

            TransferManager.Instance.Add(CreateUpload(service, settings, localPath, key, size));
            count++;
        }

        return count;
    }

    /// <summary>批量加入下载任务（键到本地路径的映射由调用方给出）。</summary>
    public static int EnqueueDownloads(
        StorageService service,
        StorageSettings settings,
        IEnumerable<(string Key, string LocalPath, long Size)> objects)
    {
        int count = 0;

        foreach ((string key, string localPath, long size) in objects)
        {
            TransferManager.Instance.Add(CreateDownload(service, settings, key, localPath, size));
            count++;
        }

        return count;
    }
}
