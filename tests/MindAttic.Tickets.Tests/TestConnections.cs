using Microsoft.Data.SqlClient;

namespace MindAttic.Tickets.Tests;

/// <summary>The tests' scratch-server connections (M.TIX-T346).</summary>
internal static class TestConnections
{
    /// <summary><paramref name="connection"/> with a connect timeout a busy machine can meet: the default's five seconds is
    /// right for a person waiting at a prompt, but the install gate runs these on a loaded machine, where SQL Server's
    /// handshake took longer than that at random and failed whichever test was connecting.</summary>
    public static string Patient(string connection) => new SqlConnectionStringBuilder(connection) { ConnectTimeout = 30 }.ConnectionString;
}
