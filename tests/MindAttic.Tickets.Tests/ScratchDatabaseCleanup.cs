using MindAttic.Tickets.Data;
using NUnit.Framework;
using NUnit.Framework.Interfaces;

[assembly: ScratchDatabaseCleanupAfterFixture]

/// <summary>Drops the scratch SQL Server databases a fixture made, once it is done (M.TIX-T333); fixtures here run one at a time,
/// so nothing else of this process is using them.</summary>
[AttributeUsage(AttributeTargets.Assembly)]
public sealed class ScratchDatabaseCleanupAfterFixtureAttribute : Attribute, ITestAction
{
    public ActionTargets Targets => ActionTargets.Suite;

    public void BeforeTest(ITest test) { }

    public void AfterTest(ITest test)
    {
        if (test.IsSuite && test.TypeInfo is not null) TicketsServer.DropCreatedTestDatabases();
    }
}

/// <summary>Drops any scratch database older than a day left by a run that died, and whatever this run still has.</summary>
[SetUpFixture]
public sealed class ScratchDatabaseCleanup
{
    [OneTimeSetUp]
    public void Sweep() => TicketsServer.SweepOrphanedTestDatabases();

    [OneTimeTearDown]
    public void Drop() => TicketsServer.DropCreatedTestDatabases();
}
