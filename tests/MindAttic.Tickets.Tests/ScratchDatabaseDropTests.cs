using Microsoft.Data.SqlClient;
using MindAttic.Tickets.Data;
using NUnit.Framework;

namespace MindAttic.Tickets.Tests;

/// <summary>The scratch-database drop and sweep (M.TIX-T333) touch only MindAttic.Tickets.Test-* names.</summary>
public sealed class ScratchDatabaseDropTests
{
    private static string? Server => Environment.GetEnvironmentVariable(TicketsServer.TestEnvVar) is { Length: > 0 } s ? s : null;

    [TestCase("MindAttic.Tickets")]
    [TestCase("master")]
    [TestCase("MindAttic.Tickets.Test-")]
    [TestCase("MindAttic.Tickets.Test-x]; DROP DATABASE [MindAttic.Tickets")]
    [TestCase("Other.MindAttic.Tickets.Test-1")]
    public void Names_outside_the_scratch_pattern_are_never_dropped(string name) =>
        Assert.That(TicketsServer.IsTestDatabase(name), Is.False);

    [Test]
    public void A_scratch_name_is_droppable() => Assert.That(TicketsServer.IsTestDatabase("MindAttic.Tickets.Test-ABC123"), Is.True);

    [Test]
    public void A_created_scratch_database_is_gone_after_the_drop()
    {
        Assume.That(Server, Is.Not.Null, "needs MINDATTIC_TICKETS_TEST_SQLSERVER");
        var path = Path.Combine(Path.GetTempPath(), "scratch-drop-" + Guid.NewGuid().ToString("N"), "Tickets.db");
        var server = TicketsServer.For(path)!;
        var name = new SqlConnectionStringBuilder(server.ConnectionString).InitialCatalog;
        Assert.That(Exists(name), Is.True);
        TicketsServer.DropCreatedTestDatabases();
        Assert.That(Exists(name), Is.False);
    }

    private static bool Exists(string name)
    {
        SqlConnection.ClearAllPools();
        using var master = new SqlConnection(TicketsServer.DefaultConnection(Server!, "master"));
        master.Open();
        using var cmd = master.CreateCommand();
        cmd.CommandText = "SELECT DB_ID(@n)";
        cmd.Parameters.AddWithValue("@n", name);
        return cmd.ExecuteScalar() is not DBNull and not null;
    }
}
