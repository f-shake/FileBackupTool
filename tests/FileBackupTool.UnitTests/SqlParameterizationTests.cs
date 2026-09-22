using FileBackupTool.Core.Entities;
using FileBackupTool.UnitTests.TestSupport;
using Xunit;

namespace FileBackupTool.UnitTests;

/// <summary>
/// 守住一个被两份复核报告给出**相反结论**的规模上限。
/// </summary>
/// <remarks>
/// 背景：<c>List&lt;int&gt;.Contains(列)</c> 在 EF Core + SQLite 下**会不会把每个元素内联成参数**，
/// 直接决定"该任务全部已完成快照的 id"能不能整份塞进一条 SQL。
/// <para>
/// 实测结论（用 <c>ToQueryString()</c> 量的）：**会内联**。
/// 10 个元素 → 899 字符；5000 个元素 → 307045 字符，即 SQL 文本随元素数线性增长。
/// 而 SQLite 的 <c>SQLITE_MAX_VARIABLE_NUMBER</c> 是 32766——超过它不是"变慢"，
/// 是 <c>too many SQL variables</c> **直接失败**。
/// </para>
/// <para>
/// 所以 <c>FileRecordStore.GetFileHistoryAsync</c> 必须像同文件其它方法那样对 id 列表分块。
/// 这条测试用 **40000 个 id**（刻意越过 32766）来守它：分块之前会抛
/// <c>SqliteException</c>，分块之后正常返回。文件历史端点的入参是"该任务全部已完成快照"，
/// 长期开着实时监控的任务（每 30 次增量一次全量）会随时间自然涨到这个量级。
/// </para>
/// <para>
/// 刻意**不**断言 EF 的内联行为本身：那是依赖的内部实现，将来 EF 改成参数化时那条断言会变成
/// 假警报。这里守的是"我们的代码在这个上限下仍然正确"。
/// </para>
/// </remarks>
public sealed class SqlParameterizationTests
{
    [Fact]
    public async Task 文件历史在快照数超过SQLite参数上限时仍然可用()
    {
        await using TestHarness harness = await TestHarness.CreateAsync();

        // 40000 > 32766（SQLite 的参数上限）
        List<int> chain = [.. Enumerable.Range(1, 40_000)];

        List<BackupFile> rows = await harness.FileRecords
            .GetFileHistoryAsync(Guid.NewGuid(), "a.txt", chain, CancellationToken.None);

        Assert.Empty(rows);
    }
}
