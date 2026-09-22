using FileBackupTool.Core.Models;

namespace FileBackupTool.Api.Contracts;

/// <summary>
/// 统一分页响应（设计文档 06 §4：<c>{ items, total, page, pageSize }</c>）。
/// </summary>
/// <remarks>
/// 与 <c>Core.Models.PagedResult&lt;T&gt;</c> 的区别：那个是引擎层的返回类型，这个是**线上契约**。
/// 刻意不直接序列化引擎类型——一旦引擎加了内部字段，线上形状就跟着变，
/// 而这种"悄悄多一个字段"的破坏往往到前端出问题才被发现。
/// </remarks>
public sealed record PagedDto<T>(IReadOnlyList<T> Items, long Total, int Page, int PageSize)
{
    public static PagedDto<T> From<TSource>(PagedResult<TSource> source, Func<TSource, T> map) =>
        new([.. source.Items.Select(map)], source.Total, source.Page, source.PageSize);

    public static PagedDto<T> Empty(int page, int pageSize) => new([], 0, page, pageSize);
}

/// <summary>分页请求参数的统一归一化。</summary>
public static class PageQuery
{
    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 1000;

    /// <summary>
    /// 归一化 <c>?page=&amp;pageSize=</c>。
    /// 上限 1000 与引擎层 <c>MirrorQueryService</c> 的钳制保持一致，
    /// 否则 API 层允许 5000、引擎悄悄截成 1000，前端会看到"total 对不上 items"。
    /// </summary>
    public static (int Page, int PageSize) Normalize(int? page, int? pageSize)
    {
        int p = page is null or < 1 ? 1 : page.Value;
        int size = pageSize is null or < 1 ? DefaultPageSize : Math.Min(pageSize.Value, MaxPageSize);
        return (p, size);
    }
}
