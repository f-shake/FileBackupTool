using System.Text.Json;
using System.Text.Json.Serialization;

namespace FileBackupTool.Api.Infrastructure;

/// <summary>
/// 保证 <see cref="DateTime"/> 出参**一定**带 <c>Z</c>（UTC）。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么非要有这个转换器</b>：SQLite 把时间存成 TEXT，EF Core 读回来后
/// <c>DateTime.Kind</c> 是 <c>Unspecified</c>（Microsoft.Data.Sqlite 用
/// <c>DateTime.Parse(...)</c> 解析）。而 <c>System.Text.Json</c> 对 <c>Unspecified</c> 的
/// 默认行为是**不加后缀**——于是前端拿到的字符串是 <c>"2026-09-22T10:00:00"</c>，
/// 浏览器会按**本地时区**解释它。中国用户看到的时间会凭空早 8 小时，
/// 而更糟的是这类偏差在开发机上（同一时区）看不出来，上线到异地才暴露。
/// </para>
/// <para>
/// <b>为什么不能用 <c>ToUniversalTime()</c></b>：对 <c>Unspecified</c>，它按**本机时区**
/// 换算，等于把"其实已经是 UTC"的值又减了一次偏移，是同一个 bug 的反向版本。
/// 唯一正确的做法是 <c>SpecifyKind(…, Utc)</c>——声明它的时区语义，不做任何数值换算。
/// </para>
/// <para>
/// 数据库里**只**存 UTC（设计文档 08 §4.1），所以"Unspecified 就是 UTC"这个前提成立。
/// </para>
/// </remarks>
public sealed class UtcDateTimeJsonConverter : JsonConverter<DateTime>
{
    public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        DateTime value = reader.GetDateTime();

        // 入参：带 Z 或带偏移的一律折成 UTC；不带后缀的按 UTC 理解（见类注释的前提）。
        return value.Kind switch
        {
            DateTimeKind.Local => value.ToUniversalTime(),
            DateTimeKind.Unspecified => DateTime.SpecifyKind(value, DateTimeKind.Utc),
            _ => value,
        };
    }

    public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options)
    {
        DateTime utc = value.Kind switch
        {
            DateTimeKind.Local => value.ToUniversalTime(),
            DateTimeKind.Unspecified => DateTime.SpecifyKind(value, DateTimeKind.Utc),
            _ => value,
        };

        // 交给 Utf8JsonWriter 自己格式化：它对 Kind=Utc 会补上 Z 后缀
        //（且会裁掉小数秒里无意义的 0），不需要我们自己拼格式串。
        writer.WriteStringValue(utc);
    }
}

/// <summary><see cref="UtcDateTimeJsonConverter"/> 的可空版本（<c>DateTime?</c> 字段同样需要保证带 Z）。</summary>
public sealed class NullableUtcDateTimeJsonConverter : JsonConverter<DateTime?>
{
    private static readonly UtcDateTimeJsonConverter Inner = new();

    public override DateTime? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.Null ? null : Inner.Read(ref reader, typeof(DateTime), options);

    public override void Write(Utf8JsonWriter writer, DateTime? value, JsonSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNullValue();
            return;
        }

        Inner.Write(writer, value.Value, options);
    }
}
