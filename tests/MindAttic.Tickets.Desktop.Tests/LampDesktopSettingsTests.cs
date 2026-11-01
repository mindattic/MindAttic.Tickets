using System.IO;
using MindAttic.Tickets.Desktop.Data;
using MindAttic.Tickets.Desktop.ViewModels;
using MindAttic.Vault.Launch;
using MindAttic.Vault.Paths;
using MindAttic.Vault.Settings.Manifest;
using NUnit.Framework;

namespace MindAttic.Tickets.Desktop.Tests;

/// <summary>LAMP (M.TIX-T266): Tickets Desktop's remembered defaults as a per-machine Vault settings app, one test per setting.</summary>
[NonParallelizable]
public class LampDesktopSettingsTests
{
    private string root = "";
    private string? savedRoaming, savedLocal;
    private string Folder => VaultPaths.LocalApp(DesktopSettings.AppId);

    [SetUp]
    public void SetUp()
    {
        root = Path.Combine(Path.GetTempPath(), "tix-lamp-" + Guid.NewGuid().ToString("N"));
        savedRoaming = Environment.GetEnvironmentVariable(VaultPaths.RoamingRootEnvVar);
        savedLocal = Environment.GetEnvironmentVariable(VaultPaths.LocalRootEnvVar);
        Environment.SetEnvironmentVariable(VaultPaths.RoamingRootEnvVar, Path.Combine(root, "roaming"));
        Environment.SetEnvironmentVariable(VaultPaths.LocalRootEnvVar, Path.Combine(root, "local"));
    }

    [TearDown]
    public void TearDown()
    {
        Environment.SetEnvironmentVariable(VaultPaths.RoamingRootEnvVar, savedRoaming);
        Environment.SetEnvironmentVariable(VaultPaths.LocalRootEnvVar, savedLocal);
        try { Directory.Delete(root, recursive: true); } catch { }
    }

    private void WriteToday(string json)
    {
        Directory.CreateDirectory(Folder);
        File.WriteAllText(DesktopSettings.LegacyPath, json);
    }

    private MainViewModel Model(DesktopSettings s)
    {
        var workspace = Path.Combine(root, "ws");
        Directory.CreateDirectory(workspace);
        return new MainViewModel(new TicketStore(workspace), s);
    }

    private static DesktopSettings Reload() => DesktopSettings.Load();

    [Test]
    public void Setting_View_ImportsRoundTripsAndIsTheStartView()
    {
        WriteToday("""{ "View": "Board" }""");
        Assert.That(DesktopSettings.Load().View, Is.EqualTo("Board"), "today's desktop.json imported");
        var s = Reload();
        s.View = "Stories";
        s.Save();
        Assert.That(Model(Reload()).SelectedView, Is.EqualTo(MainViewModel.StoriesView));
    }

    [Test]
    public void Setting_Zoom_ImportsRoundTripsAndIsTheWindowZoom()
    {
        WriteToday("""{ "Zoom": 1.25 }""");
        Assert.That(DesktopSettings.Load().Zoom, Is.EqualTo(1.25));
        var s = Reload();
        s.Zoom = 1.5;
        s.Save();
        Assert.That(Model(Reload()).Zoom, Is.EqualTo(1.5));
    }

    [Test]
    public void Setting_GroupByRepo_ImportsRoundTripsAndGroupsTheList()
    {
        WriteToday("""{ "GroupByRepo": false }""");
        Assert.That(DesktopSettings.Load().GroupByRepo, Is.False);
        Assert.That(Model(Reload()).GroupByRepo, Is.False);
    }

    [Test]
    public void Setting_GroupStoriesByEpic_ImportsRoundTripsAndGroupsStories()
    {
        WriteToday("""{ "GroupStoriesByEpic": false }""");
        Assert.That(DesktopSettings.Load().GroupStoriesByEpic, Is.False);
        Assert.That(Model(Reload()).GroupByEpic, Is.False);
    }

    [Test]
    public void Setting_LastRepo_ImportsAndRoundTrips()
    {
        WriteToday("""{ "LastRepo": "MindAttic.Vault" }""");
        Assert.That(DesktopSettings.Load().LastRepo, Is.EqualTo("MindAttic.Vault"), "the new-ticket dialog starts on it");
        var s = Reload();
        s.LastRepo = "Prose";
        s.Save();
        Assert.That(Reload().LastRepo, Is.EqualTo("Prose"));
    }

    [Test]
    public void Setting_LastType_ImportsAndRoundTrips()
    {
        WriteToday("""{ "LastType": "fix" }""");
        Assert.That(DesktopSettings.Load().LastType, Is.EqualTo("fix"), "the new-ticket dialog starts on it");
        var field = SettingsManifestBuilder.Build<DesktopSettings>().Find("MindAttic:Settings:Tickets:lastType")!;
        Assert.That(field.Choices!.Select(c => c.Label), Does.Contain("fix"));
    }

    [Test]
    public void EverySettingHasItsOwnTest()
    {
        var tested = GetType().GetMethods().Select(m => m.Name).Where(n => n.StartsWith("Setting_")).ToList();
        foreach (var field in SettingsManifestBuilder.Build<DesktopSettings>().Fields)
            Assert.That(tested.Any(t => t.StartsWith("Setting_" + char.ToUpperInvariant(field.Property[0]) + field.Property[1..] + "_")), Is.True, field.Property);
    }

    [Test]
    public void FreshMachine_SeedsDefaultsAndAPerMachineManifest()
    {
        var s = DesktopSettings.Load();
        Assert.That((s.View, s.Zoom, s.GroupByRepo, s.LastType), Is.EqualTo(("Queue", 1.0, true, "feature")));
        var m = SettingsManifestFile.TryRead(Path.Combine(Folder, SettingsManifest.FileName))!;
        Assert.That(m.Scope, Is.EqualTo(SettingsManifest.LocalScope));
        Assert.That(m.Fields.Select(f => f.Property), Does.Not.Contain("collapsedEpics"));
    }

    [Test]
    public void OlderWorkflowFolderIsStillImportedWhenItIsAllThereIs()
    {
        Directory.CreateDirectory(VaultPaths.LocalApp("Workflow"));
        File.WriteAllText(Path.Combine(VaultPaths.LocalApp("Workflow"), "desktop.json"), """{ "View": "Audit" }""");
        Assert.That(DesktopSettings.Load().View, Is.EqualTo("Audit"));
    }

    [Test]
    public void SettingsButton_OpensVaultAtTheTicketsDesktopTab()
    {
        // M.TIX-T270: the settings moved to Vault (M.TIX-T266), so the app needs an entry that opens them there.
        var original = VaultSettings.Launcher;
        var fake = new FakeLauncher();
        try
        {
            VaultSettings.Launcher = fake;
            var model = Model(DesktopSettings.Load());
            model.OpenSettingsCommand.Execute(null);
            Assert.That(fake.Sent, Is.EqualTo(new[] { "--app " + DesktopSettings.AppId }));
            Assert.That(model.StatusMessage, Does.StartWith("Opened Tickets Desktop's settings in MindAttic Vault"));
        }
        finally { VaultSettings.Launcher = original; }

        var root = AppContext.BaseDirectory;
        while (!Path.Exists(Path.Combine(root, ".git"))) root = Path.GetDirectoryName(root)!;
        var xaml = File.ReadAllText(Path.Combine(root, "src", "MindAttic.Tickets.Desktop", "MainWindow.xaml"));
        Assert.That(xaml, Does.Contain("Command=\"{Binding OpenSettingsCommand}\""), "the header shows the Settings button");
    }

    private sealed class FakeLauncher : IVaultDesktopLauncher
    {
        public List<string> Sent { get; } = new();
        public bool TrySend(VaultOpenRequest request) { Sent.Add(string.Join(' ', request.ToArgs())); return true; }
        public bool TryStart(VaultOpenRequest request) => false;
    }

    [Test]
    public void ExistingVaultFileWins()
    {
        WriteToday("""{ "View": "Board" }""");
        var s = DesktopSettings.Load();
        s.View = "Stats";
        s.Save();
        WriteToday("""{ "View": "Audit" }""");
        Assert.That(DesktopSettings.Load().View, Is.EqualTo("Stats"));
    }

    [Test]
    public void RunPs1_WritesTheSettingsManifestAfterPublishing()
    {
        // LAMP step 7 (M.TIX-T271): a redeploy refreshes the manifest MindAttic Vault renders, not only the app's next start.
        var root = TestContext.CurrentContext.TestDirectory;
        while (!System.IO.Directory.Exists(System.IO.Path.Combine(root, ".git")) && !System.IO.File.Exists(System.IO.Path.Combine(root, ".git")))
            root = System.IO.Path.GetDirectoryName(root) ?? throw new InvalidOperationException("no repo root above the tests");
        Assert.That(System.IO.File.ReadAllText(System.IO.Path.Combine(root, "run.ps1")), Does.Contain("-ArgumentList '--write-settings-manifest'"));
    }
}
