using Npgsql;

namespace Aeza.Risk.Infrastructure.Data;

/// <summary>Нормализация строки подключения PostgreSQL</summary>
public static class ConnectionStrings
{
    /// <summary>Kerberos/GSS не используется: без явной настройки GSS-шифрование отключается (в chiseled-образе нет libgssapi, Npgsql иначе пишет ошибку загрузки библиотеки при каждом подключении)</summary>
    public static string Normalize(string connectionString)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        if (!connectionString.Contains("Gss Encryption Mode", StringComparison.OrdinalIgnoreCase))
            builder.GssEncryptionMode = GssEncryptionMode.Disable;
        return builder.ConnectionString;
    }

    /// <summary>Выделенное соединение LISTEN канала live-событий</summary>
    public static string ForListen(string connectionString) =>
        new NpgsqlConnectionStringBuilder(Normalize(connectionString)) { Pooling = false, KeepAlive = 15, ApplicationName = "aeza-live" }.ConnectionString;
}
