using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace FileBackupTool.Data;

/// <summary>
/// 设计时工厂，让 <c>dotnet ef migrations add</c> 无需启动项目即可工作。
/// </summary>
/// <remarks>
/// 迁移命令（在仓库根执行）：
/// <code>
/// dotnet ef migrations add &lt;Name&gt; -p src/FileBackupTool.Data -o Migrations
/// dotnet ef database update -p src/FileBackupTool.Data
/// </code>
/// </remarks>
public sealed class AppDbContextDesignTimeFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite("Data Source=App_Data/design-time.db")
            .Options;
        return new AppDbContext(options);
    }
}
