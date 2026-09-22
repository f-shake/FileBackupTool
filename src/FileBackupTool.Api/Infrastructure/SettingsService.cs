using System.Globalization;
using FileBackupTool.Api.Contracts;
using FileBackupTool.Core.Configuration;
using FileBackupTool.Data;
using FileBackupTool.Core.Abstractions;

namespace FileBackupTool.Api.Infrastructure;

/// <summary>
/// 运行期可改的全局设置（数据库里的键值对）。
/// </summary>
/// <remarks>
/// <para>
/// 设计文档 06 §4.9 的划分：**基础设施**（连接串、数据目录、日志级别、信任代理、限流参数）
/// 留 <c>appsettings.json</c>；**业务上要即时改的**（还原根白名单、保留策略）放数据库。
/// </para>
/// <para>
/// 这里额外定义了几个 <c>SettingsStore</c> 没有常量的键。之所以不在 Data 层加常量，
/// 是因为文件所有权：本层只能使用 Data 已有的公开成员。
/// 键名统一收敛在这里，不再散落到控制器里拼字符串。
/// </para>
/// </remarks>
public sealed class SettingsService(SettingsStore store, IClock clock)
{
    private readonly SettingsStore _store = store;
    private readonly IClock _clock = clock;

    // ---- 键名 ----
    public const string KeyRetentionMode = "Retention.Mode";
    public const string KeyRetentionApplyToFullChains = "Retention.ApplyToFullChains";
    public const string KeyAutoBackupEnabled = "Scheduler.AutoBackupEnabled";
    public const string KeyVerifyDeepHash = "Security.VerifyDeepHashByDefault";
    public const string KeyFailureRateThreshold = "Jobs.FailureRateThresholdPercent";

    // ---- 读 ----

    /// <summary>自动备份总开关。**默认开启**（关掉定时备份不是"安全默认"，用户会以为在备份）。</summary>
    public async Task<bool> GetAutoBackupEnabledAsync(CancellationToken ct) =>
        await GetBoolAsync(KeyAutoBackupEnabled, defaultValue: true, ct).ConfigureAwait(false);

    public async Task<bool> GetVerifyDeepHashByDefaultAsync(CancellationToken ct) =>
        await GetBoolAsync(KeyVerifyDeepHash, defaultValue: false, ct).ConfigureAwait(false);

    public async Task<double> GetFailureRateThresholdPercentAsync(CancellationToken ct)
    {
        string? raw = await _store.GetStringAsync(KeyFailureRateThreshold, ct).ConfigureAwait(false);
        return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
            ? value
            : 10d;
    }

    public Task<List<string>> GetAllowedRestoreRootsAsync(CancellationToken ct) =>
        _store.GetAllowedRestoreRootsAsync(ct);

    public Task SetAllowedRestoreRootsAsync(IReadOnlyList<string> roots, CancellationToken ct) =>
        _store.SetAllowedRestoreRootsAsync(roots, ct);

    /// <summary>
    /// 读保留策略（04 §2.3）。<c>mode</c> 决定 <c>value</c> 落到哪个旧键上，
    /// 这样既满足前端"一个 mode + 一个 value"的形状，又保住 <c>SettingsStore</c> 已有的键。
    /// </summary>
    public async Task<RetentionSettingsDto> GetRetentionAsync(CancellationToken ct)
    {
        string mode = await _store.GetStringAsync(KeyRetentionMode, ct).ConfigureAwait(false) ?? "Count";

        long value = mode switch
        {
            "Days" => await GetIntAsync(SettingsStore.KeyRetentionMaxAgeDays, 180, ct).ConfigureAwait(false),
            "Size" => await GetIntAsync(SettingsStore.KeyRetentionMaxBytesGb, 0, ct).ConfigureAwait(false),
            _ => await GetIntAsync(SettingsStore.KeyRetentionMaxGroups, 10, ct).ConfigureAwait(false),
        };

        return new RetentionSettingsDto
        {
            Enabled = await GetBoolAsync(SettingsStore.KeyRetentionAutoEviction, defaultValue: false, ct).ConfigureAwait(false),
            Mode = mode,
            Value = value,
            ApplyToFullChains = await GetBoolAsync(KeyRetentionApplyToFullChains, defaultValue: true, ct).ConfigureAwait(false),
        };
    }

    public async Task<AppSettingsDto> GetAppSettingsAsync(CancellationToken ct) => new()
    {
        AutoBackupEnabled = await GetAutoBackupEnabledAsync(ct).ConfigureAwait(false),
        AllowedRestoreRoots = await GetAllowedRestoreRootsAsync(ct).ConfigureAwait(false),
        Retention = await GetRetentionAsync(ct).ConfigureAwait(false),
        VerifyDeepHashByDefault = await GetVerifyDeepHashByDefaultAsync(ct).ConfigureAwait(false),
        FailureRateThresholdPercent = await GetFailureRateThresholdPercentAsync(ct).ConfigureAwait(false),
    };

    // ---- 写 ----

    public async Task SaveAppSettingsAsync(AppSettingsDto settings, CancellationToken ct)
    {
        await SetBoolAsync(KeyAutoBackupEnabled, settings.AutoBackupEnabled, ct).ConfigureAwait(false);
        await SetBoolAsync(KeyVerifyDeepHash, settings.VerifyDeepHashByDefault, ct).ConfigureAwait(false);

        await _store.SetStringAsync(
            KeyFailureRateThreshold,
            settings.FailureRateThresholdPercent.ToString(CultureInfo.InvariantCulture),
            ct).ConfigureAwait(false);

        if (settings.Retention is { } retention)
        {
            await SetBoolAsync(SettingsStore.KeyRetentionAutoEviction, retention.Enabled, ct).ConfigureAwait(false);
            await SetBoolAsync(KeyRetentionApplyToFullChains, retention.ApplyToFullChains, ct).ConfigureAwait(false);
            await _store.SetStringAsync(KeyRetentionMode, NormalizeMode(retention.Mode), ct).ConfigureAwait(false);

            // 只写与当前 mode 对应的那个键，另外两个保持原值——
            // 用户切换 mode 时不该把别的阈值抹掉。
            string value = retention.Value.ToString(CultureInfo.InvariantCulture);
            string key = NormalizeMode(retention.Mode) switch
            {
                "Days" => SettingsStore.KeyRetentionMaxAgeDays,
                "Size" => SettingsStore.KeyRetentionMaxBytesGb,
                _ => SettingsStore.KeyRetentionMaxGroups,
            };
            await _store.SetStringAsync(key, value, ct).ConfigureAwait(false);
        }
    }

    public static string NormalizeMode(string? mode) => mode switch
    {
        "Days" => "Days",
        "Size" => "Size",
        _ => "Count",
    };

    /// <summary>
    /// 首次启动播种：数据库里**没有**该键时，用 <c>appsettings.json</c> 的值填进去。
    /// </summary>
    /// <remarks>
    /// 只在"读不到"时写，所以不会覆盖用户在 UI 里改过的值。
    /// 这是让"配置文件给出厂默认、运行时以数据库为准"两件事共存的唯一做法。
    /// </remarks>
    public async Task SeedFromConfigurationAsync(
        SecurityOptions security,
        RetentionOptions retention,
        CancellationToken ct)
    {
        if (await _store.GetStringAsync(KeyAutoBackupEnabled, ct).ConfigureAwait(false) is null)
        {
            await SetBoolAsync(KeyAutoBackupEnabled, true, ct).ConfigureAwait(false);
        }

        if (await _store.GetStringAsync(KeyVerifyDeepHash, ct).ConfigureAwait(false) is null)
        {
            // 出厂默认写死 false（设计文档 04 §2.3）。**不能**从 Core 的 SecurityOptions 读：
            // 那个类型里没有这个属性（引擎不关心"校验默认深不深"），
            // 而项目规则不允许为这一处去改 Core。要改这个出厂默认值，
            // 请改这里，或者在首次启动后用 UI 改（UI 改的值写在数据库里，优先级更高）。
            await SetBoolAsync(KeyVerifyDeepHash, false, ct).ConfigureAwait(false);
        }

        if (await _store.GetStringAsync(SettingsStore.KeyRetentionAutoEviction, ct).ConfigureAwait(false) is null)
        {
            await SetBoolAsync(SettingsStore.KeyRetentionAutoEviction, retention.AutoEvictionEnabled, ct).ConfigureAwait(false);
        }

        if (await _store.GetStringAsync(KeyRetentionMode, ct).ConfigureAwait(false) is null)
        {
            await _store.SetStringAsync(KeyRetentionMode, "Count", ct).ConfigureAwait(false);
        }

        if (await _store.GetStringAsync(KeyRetentionApplyToFullChains, ct).ConfigureAwait(false) is null)
        {
            await SetBoolAsync(KeyRetentionApplyToFullChains, true, ct).ConfigureAwait(false);
        }

        if (await _store.GetStringAsync(SettingsStore.KeyRetentionMaxGroups, ct).ConfigureAwait(false) is null)
        {
            await _store.SetStringAsync(
                SettingsStore.KeyRetentionMaxGroups,
                retention.MaxSnapshotGroups.ToString(CultureInfo.InvariantCulture),
                ct).ConfigureAwait(false);
        }

        if (await _store.GetStringAsync(SettingsStore.KeyRetentionMaxAgeDays, ct).ConfigureAwait(false) is null)
        {
            await _store.SetStringAsync(
                SettingsStore.KeyRetentionMaxAgeDays,
                retention.MaxAgeDays.ToString(CultureInfo.InvariantCulture),
                ct).ConfigureAwait(false);
        }

        if (await _store.GetStringAsync(SettingsStore.KeyRetentionMaxBytesGb, ct).ConfigureAwait(false) is null)
        {
            await _store.SetStringAsync(
                SettingsStore.KeyRetentionMaxBytesGb,
                retention.MaxTotalBytesGb.ToString(CultureInfo.InvariantCulture),
                ct).ConfigureAwait(false);
        }

        // 还原根白名单：数据库为空且配置里有值时才播种（设计文档 08 §2.2 第 5 步）。
        List<string> existing = await _store.GetAllowedRestoreRootsAsync(ct).ConfigureAwait(false);
        if (existing.Count == 0 && security.AllowedRestoreRoots.Count > 0)
        {
            await SetAllowedRestoreRootsAsync(security.AllowedRestoreRoots, ct).ConfigureAwait(false);
        }
    }

    /// <summary>读保留策略的强类型版本，给 <c>RetentionService</c> 用。</summary>
    public async Task<RetentionOptions> GetRetentionOptionsAsync(CancellationToken ct)
    {
        RetentionSettingsDto dto = await GetRetentionAsync(ct).ConfigureAwait(false);
        return new RetentionOptions
        {
            AutoEvictionEnabled = dto.Enabled,
            MaxSnapshotGroups = (int)await GetIntAsync(SettingsStore.KeyRetentionMaxGroups, 10, ct).ConfigureAwait(false),
            MaxAgeDays = (int)await GetIntAsync(SettingsStore.KeyRetentionMaxAgeDays, 180, ct).ConfigureAwait(false),
            MaxTotalBytesGb = await GetIntAsync(SettingsStore.KeyRetentionMaxBytesGb, 0, ct).ConfigureAwait(false),
        };
    }

    // ---- 底层 ----

    private async Task<bool> GetBoolAsync(string key, bool defaultValue, CancellationToken ct)
    {
        string? raw = await _store.GetStringAsync(key, ct).ConfigureAwait(false);
        return bool.TryParse(raw, out bool value) ? value : defaultValue;
    }

    private async Task<long> GetIntAsync(string key, long defaultValue, CancellationToken ct)
    {
        string? raw = await _store.GetStringAsync(key, ct).ConfigureAwait(false);
        return long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out long value)
            ? value
            : defaultValue;
    }

    private Task SetBoolAsync(string key, bool value, CancellationToken ct) =>
        _store.SetStringAsync(key, value ? "true" : "false", ct);

    /// <summary>供会话登记等旁路数据使用；语义与 <see cref="SettingsStore"/> 一致。</summary>
    public Task<string?> GetRawAsync(string key, CancellationToken ct) =>
        _store.GetStringAsync(key, ct);

    public Task SetRawAsync(string key, string value, CancellationToken ct) =>
        _store.SetStringAsync(key, value, ct);

    public DateTime UtcNow => clock.UtcNow;
}
