// SqlExplorer.cs - exploration helper for PowerShell 5.1 via Add-Type.
//
// Constraints honored here (do not "modernize"):
//   - C# 5 syntax only: no string interpolation, no ?. , no using-declarations
//   - System.Data.SqlClient (built into .NET Framework), NOT Microsoft.Data.SqlClient
//   - Load with: Add-Type -Path .\SqlExplorer.cs -ReferencedAssemblies "System.Data","System.Xml"
//   - Types cannot be redefined in a PowerShell session; restart the shell to reload.
//
// Hand-typing v0: type only ConnectionString, Open(), Scalar(), and Query(string).
// The parameterized Query overload and QueryReadOnly are stage 2 - v0 compiles without them
// (then also drop the System.Collections.Generic using).

using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;

namespace Voyager
{
    public static class SqlExplorer
    {
        // Set once from PowerShell: [Voyager.SqlExplorer]::ConnectionString = "Server=...;Database=...;Integrated Security=true;"
        public static string ConnectionString;

        // Query execution timeout in seconds (SqlCommand.CommandTimeout; ADO.NET default is only 30).
        // The config's connectTimeoutSeconds governs CONNECTING, not query execution.
        public static int CommandTimeoutSeconds = 300;

        private static SqlCommand Command(string sql, SqlConnection conn)
        {
            SqlCommand cmd = new SqlCommand(sql, conn);
            cmd.CommandTimeout = CommandTimeoutSeconds;
            return cmd;
        }

        private static SqlConnection Open()
        {
            if (String.IsNullOrEmpty(ConnectionString))
            {
                throw new InvalidOperationException("Set SqlExplorer.ConnectionString first.");
            }
            SqlConnection conn = new SqlConnection(ConnectionString);
            conn.Open();
            return conn;
        }

        // Single value, e.g. Scalar("SELECT COUNT(*) FROM sys.foreign_keys")
        public static object Scalar(string sql)
        {
            using (SqlConnection conn = Open())
            using (SqlCommand cmd = Command(sql, conn))
            {
                return cmd.ExecuteScalar();
            }
        }

        // Full result set as a DataTable - pipe to Format-Table / Out-GridView / Export-Csv in PS.
        public static DataTable Query(string sql)
        {
            using (SqlConnection conn = Open())
            using (SqlCommand cmd = Command(sql, conn))
            using (SqlDataReader reader = cmd.ExecuteReader())
            {
                DataTable dt = new DataTable();
                dt.Load(reader);
                return dt;
            }
        }

        // Parameterized variant - the habit that carries into the C# port.
        // PS: $p = New-Object 'System.Collections.Generic.Dictionary[string,object]'
        //     $p["@name"] = "SomeTable"
        //     [Voyager.SqlExplorer]::Query("SELECT ... WHERE t.name = @name", $p)
        public static DataTable Query(string sql, Dictionary<string, object> parameters)
        {
            using (SqlConnection conn = Open())
            using (SqlCommand cmd = Command(sql, conn))
            {
                foreach (KeyValuePair<string, object> kv in parameters)
                {
                    object value = kv.Value;
                    if (value == null) { value = DBNull.Value; }
                    cmd.Parameters.AddWithValue(kv.Key, value);
                }
                using (SqlDataReader reader = cmd.ExecuteReader())
                {
                    DataTable dt = new DataTable();
                    dt.Load(reader);
                    return dt;
                }
            }
        }

        // Read-only guardrail check for this exploration phase: refuse obvious writes.
        // Not a security boundary - just a tripwire against pasting the wrong snippet.
        public static DataTable QueryReadOnly(string sql)
        {
            string upper = sql.TrimStart().ToUpperInvariant();
            if (upper.StartsWith("INSERT") || upper.StartsWith("UPDATE") ||
                upper.StartsWith("DELETE") || upper.StartsWith("MERGE") ||
                upper.StartsWith("DROP") || upper.StartsWith("ALTER") ||
                upper.StartsWith("TRUNCATE") || upper.StartsWith("EXEC"))
            {
                throw new InvalidOperationException("QueryReadOnly refused a write/exec statement.");
            }
            return Query(sql);
        }
    }
}
