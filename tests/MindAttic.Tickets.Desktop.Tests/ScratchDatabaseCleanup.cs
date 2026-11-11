using MindAttic.Tickets.Data;
using NUnit.Framework;

/// <summary>Drops the scratch SQL Server databases the run made, and any older than a day left by runs that died (M.TIX-T333).</summary>
[SetUpFixture]
public sealed class ScratchDatabaseCleanup
{
    [OneTimeSetUp]
    public void Sweep() => TicketsServer.SweepOrphanedTestDatabases();

    [OneTimeTearDown]
    public void Drop() => TicketsServer.DropCreatedTestDatabases();
}
