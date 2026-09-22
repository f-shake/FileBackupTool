using FileBackupTool.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace FileBackupTool.Engine.Blobs;

/// <summary>
/// 按备份目录创建 blob 池。**每个任务的备份目录独立、禁止共享**（决策 #8），
/// 所以 blob 池是"每任务一个"的，必须按目录现取。
/// </summary>
public sealed class BlobStoreFactory(IFileSystem fs, IPathNormalizer normalizer, IHashService hash)
{
    private readonly IFileSystem _fs = fs;
    private readonly IPathNormalizer _normalizer = normalizer;
    private readonly IHashService _hash = hash;

    public PhysicalBlobStore Create(string backupDir) => new(_fs, _normalizer, _hash, backupDir);
}
