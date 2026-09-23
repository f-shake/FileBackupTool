namespace FileBackupTool.Api.Security;

/// <summary>本应用使用的自定义 Claim 类型。</summary>
public static class SecurityClaims
{
    /// <summary>会话 id。与 <see cref="SessionRegistry"/> 里的记录一一对应。</summary>
    public const string SessionId = "fbt:sid";
}
