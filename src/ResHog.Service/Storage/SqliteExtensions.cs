using Microsoft.Data.Sqlite;

namespace ResHog.Storage;

/// <summary>
/// Extension methods for SqliteConnection to simplify executing raw SQL.
/// </summary>
internal static class SqliteConnectionExtensions
{
    /// <summary>
    /// Execute a non-query SQL command with the given text.
    /// </summary>
    public static int ExecuteNonQuery(this SqliteConnection conn, string commandText)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = commandText;
        return cmd.ExecuteNonQuery();
    }
}
