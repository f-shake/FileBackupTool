using FileBackupTool.Core.Abstractions;
using FileBackupTool.Core.Configuration;
using FileBackupTool.Core.Hashing;
using FileBackupTool.Core.IO;
using FileBackupTool.Core.Paths;
using FileBackupTool.Data;
using FileBackupTool.Engine.Blobs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FileBackupTool.Engine;

/// <summary>引擎与数据层的 DI 注册。</summary>
/// <remarks>
/// 调用方（Api 宿主）**必须**先完成这些注册：
/// <list type="bullet">
/// <item><c>AddDbContextFactory&lt;AppDbContext&gt;</c>（见 <see cref="DatabaseBootstrapper.BuildOptions"/>）</item>
/// <item><c>AddOptions</c> 绑定 <see cref="MonitoringOptions"/> / <see cref="JobOptions"/> /
/// <see cref="StorageOptions"/> / <see cref="SecurityOptions"/></item>
/// <item><c>IJobEventPublisher</c>（SignalR 接入点；不注册则退化为不发事件）</item>
/// </list>
/// <para>
/// <c>IManifestWriter</c> 在这里注册的是**真实的清单写入器**（决策 #11 要求备份目录里留一份
/// 不依赖本软件的抢救清单，不是可选项）。宿主若要用别的实现覆盖，后注册的赢。
/// </para>
/// </remarks>
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddFileBackupEngine(this IServiceCollection services)
    {
        // ---- Core 抽象的真实实现 ----
        services.TryAddSingleton<IClock>(SystemClock.Instance);
        services.TryAddSingleton<IPathNormalizer>(WindowsPathNormalizer.Instance);
        services.TryAddSingleton<IHashService>(Sha256HashService.Instance);
        services.TryAddSingleton<IFileSystem, PhysicalFileSystem>();
        services.TryAddSingleton<BlobStoreFactory>();

        // ---- 作业事件（默认不发） ----
        services.TryAddSingleton<IJobEventPublisher>(NullJobEventPublisher.Instance);
        services.TryAddSingleton<IManifestWriter, Manifests.ManifestWriter>();

        // ---- 数据层 ----
        services.TryAddSingleton<TaskStore>();
        services.TryAddSingleton<FileRecordStore>();
        services.TryAddSingleton<SnapshotChainService>();
        services.TryAddSingleton<LogStore>();
        services.TryAddSingleton<SettingsStore>();

        // ---- 引擎 ----
        services.TryAddSingleton<IBackupLogSink, BackupLogSink>();
        services.TryAddSingleton<IIntegrityService, GarbageCollector>();
        services.TryAddSingleton<IBackupService, BackupEngine>();
        services.TryAddSingleton<IRestoreService, RestoreService>();
        services.TryAddSingleton<IMirrorQueryService, MirrorQueryService>();
        services.TryAddSingleton<ISnapshotService, SnapshotService>();
        services.TryAddSingleton<IJobQueue, JobQueue>();

        return services;
    }
}
