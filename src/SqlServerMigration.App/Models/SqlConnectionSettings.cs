using Microsoft.Data.SqlClient;
using System.Text.Json.Serialization;

namespace SqlServerMigration.App.Models;

public enum SqlAuthenticationMode
{
    Windows,
    SqlServer
}

/// <summary>
/// SQL Server 连接参数。对象的字符串表示始终隐藏用户名和密码。
/// </summary>
public sealed class SqlConnectionSettings
{
    public string Server { get; set; } = string.Empty;

    public string Database { get; set; } = string.Empty;

    public SqlAuthenticationMode AuthenticationMode { get; set; } = SqlAuthenticationMode.Windows;

    public string? UserName { get; set; }

    [JsonIgnore]
    public string? Password { get; set; }

    public bool Encrypt { get; set; } = true;

    public bool TrustServerCertificate { get; set; }

    public int ConnectTimeoutSeconds { get; set; } = 15;

    public int CommandTimeoutSeconds { get; set; } = 300;

    public string SafeDisplayName => $"{Server.Trim()} / {Database.Trim()}";

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Server))
        {
            throw new ArgumentException("必须填写 SQL Server 实例。", nameof(Server));
        }

        if (string.IsNullOrWhiteSpace(Database))
        {
            throw new ArgumentException("必须填写数据库名称。", nameof(Database));
        }

        if (ConnectTimeoutSeconds is < 1 or > 300)
        {
            throw new ArgumentOutOfRangeException(nameof(ConnectTimeoutSeconds), "连接超时必须介于 1 到 300 秒之间。");
        }

        if (CommandTimeoutSeconds < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(CommandTimeoutSeconds), "命令超时不能为负数。");
        }

        if (AuthenticationMode == SqlAuthenticationMode.SqlServer &&
            (string.IsNullOrWhiteSpace(UserName) || string.IsNullOrEmpty(Password)))
        {
            throw new ArgumentException("SQL Server 身份验证必须同时填写用户名和密码。");
        }
    }

    internal string BuildConnectionString()
    {
        Validate();

        var builder = new SqlConnectionStringBuilder
        {
            DataSource = Server.Trim(),
            InitialCatalog = Database.Trim(),
            IntegratedSecurity = AuthenticationMode == SqlAuthenticationMode.Windows,
            Encrypt = Encrypt,
            TrustServerCertificate = TrustServerCertificate,
            ConnectTimeout = ConnectTimeoutSeconds,
            ApplicationName = "SQL Server 2025 to 2019 Migration",
            PersistSecurityInfo = false,
            MultipleActiveResultSets = false
        };

        if (AuthenticationMode == SqlAuthenticationMode.SqlServer)
        {
            builder.UserID = UserName!.Trim();
            builder.Password = Password!;
        }

        return builder.ConnectionString;
    }

    public override string ToString() => SafeDisplayName;
}
