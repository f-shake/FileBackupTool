using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FileBackupTool.Data.Migrations
{
    /// <summary>
    /// 快照增加"因仍在写入而跳过"的计数。
    /// </summary>
    /// <remarks>
    /// 手写而非 <c>dotnet ef migrations add</c> 生成：本机没有安装 dotnet-ef 工具。
    /// 迁移本身只有一次 <c>AddColumn</c>，默认值 0 让已有快照自然落到"没有跳过"，
    /// 不需要数据回填。
    /// <para>
    /// 三处手工维护的元数据必须一致：
    /// <list type="number">
    /// <item>本文件的 <c>Up</c>/<c>Down</c>；</item>
    /// <item>同目录的 <c>20261017130000_AddSkippedFileCount.Designer.cs</c> —— 它提供
    /// <c>[DbContext]</c> / <c>[Migration]</c> 与 <c>TargetModel</c>（从
    /// <c>AppDbContextModelSnapshot</c> 转出来的，两者必须一致）；</item>
    /// <item><c>AppDbContextModelSnapshot.cs</c> 里新增的属性 —— 它决定下一次迁移的比对基线，
    /// 漏了它会让下一个人看到一份凭空多出来的差异。</item>
    /// </list>
    /// </para>
    /// <para>
    /// 属性**只写在 Designer 里**：<c>[Migration]</c> 不允许重复，部分类两半各写一次会编译不过。
    /// </para>
    /// </remarks>
    public partial class AddSkippedFileCount : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "SkippedFileCount",
                table: "Snapshots",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SkippedFileCount",
                table: "Snapshots");
        }
    }
}
