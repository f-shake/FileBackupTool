using System.Reflection;
using FileBackupTool.Api.Contracts;
using FileBackupTool.Api.Infrastructure;
using FileBackupTool.Core.Abstractions;
using FileBackupTool.Core.Entities;
using FileBackupTool.Data;
using FileBackupTool.Engine.Blobs;

namespace FileBackupTool.Api.Services;

/// <summary>
/// <c>GET /api/settings/info</c> 的数据来源（设计文档 06 §4.9）。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么要缓存</b>：blob 池占用只能靠遍历 <c>&lt;BackupDir&gt;\blobs</c> 得到
/// （池里没有"总字节数"这种元数据，内容寻址的代价就是每次都得数）。
/// 百万级 blob 下那是一次几秒到几十秒的目录遍历。这个端点会被用户在设置页反复打开，
/// 每次都真扫一遍等于给机械盘加一条随机 IO 洪流。所以缓存 15 分钟——
/// 这个数字的准确性对用户的意义只是"知道盘快满了"，晚一刻钟知道完全够用。
/// </para>
/// <para>
/// <b>回收站占用是准确的</b>：账本是单个 JSON（02 §5.2），读它是廉价的，
/// 所以不缓存。而它恰恰是用户最需要准的那个数——"空间没释放"的迷惑都在这。
/// </para>
/// </remarks>
public sealed class ServerInfoService(
    AppPaths paths,
    TaskStore tasks,
    BlobStoreFactory blobFactory,
    RuntimeInfo runtime,
    IFileSystem fs,
    IClock clock,
    ILogger<ServerInfoService> logger)
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(15);

    private static readonly string Version =
        typeof(ServerInfoService).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(ServerInfoService).Assembly.GetName().Version?.ToString()
        ?? "未知";

    private readonly AppPaths _paths = paths;
    private readonly TaskStore _tasks = tasks;
    private readonly BlobStoreFactory _blobFactory = blobFactory;
    private readonly RuntimeInfo _runtime = runtime;
    private readonly IFileSystem _fs = fs;
    private readonly IClock _clock = clock;
    private readonly ILogger<ServerInfoService> _logger = logger;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private (DateTime AtUtc, long BlobBytes, long RecycleBytes) _cache = (DateTime.MinValue, 0, 0);

    public async Task<ServerInfoDto> GetAsync(CancellationToken ct)
    {
        (long blobBytes, long recycleBytes) = await GetPoolUsageAsync(ct).ConfigureAwait(false);
        (long freeBytes, long totalBytes) = GetDiskSpace();

        return new ServerInfoDto
        {
            Version = Version,
            DataDir = _paths.DataDir,
            LogDir = _paths.LogDir,
            DatabasePath = _paths.DatabasePath,
            DatabaseBytes = GetDatabaseBytes(),
            BlobPoolBytes = blobBytes,
            RecycleBinBytes = recycleBytes,
            DiskFreeBytes = freeBytes,
            DiskTotalBytes = totalBytes,
            StartedAt = _runtime.StartedAtUtc,
            ServerTime = _clock.UtcNow,
            Notices = [.. _runtime.Notices],
        };
    }

    private async Task<(long BlobBytes, long RecycleBytes)> GetPoolUsageAsync(CancellationToken ct)
    {
        if (_clock.UtcNow - _cache.AtUtc < CacheTtl)
        {
            return (_cache.BlobBytes, _cache.RecycleBytes);
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // 双重检查：等锁期间可能已经有人算完了。
            if (_clock.UtcNow - _cache.AtUtc < CacheTtl)
            {
                return (_cache.BlobBytes, _cache.RecycleBytes);
            }

            List<BackupTask> all = await _tasks.GetAllAsync(includeDeleted: true, ct).ConfigureAwait(false);

            // 目录遍历是同步 IO（IFileSystem 的枚举是惰性的 IEnumerable），
            // 放到线程池上执行，避免占着一个请求线程几十秒。
            (long blobBytes, long recycleBytes) = await Task.Run(() => SumPools(all), ct).ConfigureAwait(false);

            _cache = (_clock.UtcNow, blobBytes, recycleBytes);
            return (blobBytes, recycleBytes);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "统计 blob 池占用失败，本次返回上次的已知值。");
            return (_cache.BlobBytes, _cache.RecycleBytes);
        }
        finally
        {
            _gate.Release();
        }
    }

    private (long BlobBytes, long RecycleBytes) SumPools(List<BackupTask> tasks)
    {
        long blobBytes = 0;
        long recycleBytes = 0;

        foreach (BackupTask task in tasks)
        {
            if (string.IsNullOrWhiteSpace(task.BackupDir))
            {
                continue;
            }

            try
            {
                PhysicalBlobStore store = _blobFactory.Create(task.BackupDir);

                foreach (string hash in store.EnumerateBlobHashes())
                {
                    blobBytes += store.GetLength(hash);
                }

                recycleBytes += store.GetRecycleBinStatus().TotalBytes;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 单个任务的备份目录不可读不该让整个统计失败（盘被拔了、权限变了）。
                _logger.LogWarning(ex, "统计任务「{Name}」的 blob 池占用失败：{Dir}", task.Name, task.BackupDir);
            }
        }

        return (blobBytes, recycleBytes);
    }

    /// <summary>数据库文件占用（含 WAL 边车，它在大批量提交后会明显变大）。</summary>
    private long GetDatabaseBytes()
    {
        long total = _fs.GetFile(_paths.DatabasePath)?.Length ?? 0;

        foreach (string suffix in new[] { "-wal", "-shm" })
        {
            total += _fs.GetFile(_paths.DatabasePath + suffix)?.Length ?? 0;
        }

        return total;
    }

    private (long FreeBytes, long TotalBytes) GetDiskSpace()
    {
        try
        {
            string? root = Path.GetPathRoot(_paths.DataDir);
            if (string.IsNullOrEmpty(root))
            {
                return (0, 0);
            }

            var drive = new DriveInfo(root);
            return drive.IsReady ? (drive.AvailableFreeSpace, drive.TotalSize) : (0, 0);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _logger.LogWarning(ex, "读取磁盘剩余空间失败：{Dir}", _paths.DataDir);
            return (0, 0);
        }
    }
}
