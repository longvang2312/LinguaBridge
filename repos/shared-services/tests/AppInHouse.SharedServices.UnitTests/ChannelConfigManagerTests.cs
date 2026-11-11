using AppInHouse.SharedServices.Application.Abstractions;
using AppInHouse.SharedServices.Application.Common;
using AppInHouse.SharedServices.Application.Configurations;
using AppInHouse.SharedServices.Domain;
using AppInHouse.SharedServices.UnitTests.Support;

namespace AppInHouse.SharedServices.UnitTests;

public class ChannelConfigManagerTests
{
    [Fact]
    public async Task Save_CreatesDraftVersionOne_ThenVersionTwo()
    {
        var fixture = new ManagerFixture();

        var first = await fixture.Manager.SaveAsync(ManagerFixture.SmtpCommand());
        var second = await fixture.Manager.SaveAsync(ManagerFixture.SmtpCommand("""{"host":"smtp2.example.com","port":465}"""));

        Assert.Equal((1, ConfigurationStatus.Draft), (first.Version, first.Status));
        Assert.Equal((2, ConfigurationStatus.Draft), (second.Version, second.Status));
        Assert.Equal(2, fixture.Repository.Rows.Count);
        Assert.Equal(2, fixture.UnitOfWork.Saves);
    }

    [Fact]
    public async Task Save_NormalisesTheProjectCode_AndKeepsSeriesApart()
    {
        var fixture = new ManagerFixture();

        var a = await fixture.Manager.SaveAsync(ManagerFixture.SmtpCommand() with { ProjectCode = "  LinguaBridge " });
        var b = await fixture.Manager.SaveAsync(ManagerFixture.SmtpCommand() with { ProjectCode = "linguabridge" });
        var other = await fixture.Manager.SaveAsync(ManagerFixture.SmtpCommand() with { ProjectCode = "identity" });
        var otherName = await fixture.Manager.SaveAsync(ManagerFixture.SmtpCommand() with { Name = "Backup SMTP" });

        Assert.Equal("linguabridge", a.ProjectCode);
        Assert.Equal(2, b.Version);                // same project, channel and name: the next version
        Assert.Equal(1, other.Version);            // another project: its own series
        Assert.Equal(1, otherName.Version);        // another name: its own series
    }

    [Fact]
    public async Task Save_WithoutASecret_KeepsTheStoredOne_AndANewSecretReplacesIt()
    {
        var fixture = new ManagerFixture();
        await fixture.Manager.SaveAsync(ManagerFixture.SmtpCommand(secret: "first-secret"));

        await fixture.Manager.SaveAsync(ManagerFixture.SmtpCommand(secret: null));
        await fixture.Manager.SaveAsync(ManagerFixture.SmtpCommand(secret: ""));
        await fixture.Manager.SaveAsync(ManagerFixture.SmtpCommand(secret: "••••••••"));   // the form sent the mask back
        var unchanged = fixture.Repository.Rows.Select(r => fixture.Protector.Unprotect(r.EncryptedSecret!)).Distinct().ToList();

        await fixture.Manager.SaveAsync(ManagerFixture.SmtpCommand(secret: "second-secret"));
        var latest = fixture.Repository.Rows.OrderBy(r => r.Version).Last();

        Assert.Equal(["first-secret"], unchanged);
        Assert.Equal("second-secret", fixture.Protector.Unprotect(latest.EncryptedSecret!));
    }

    [Fact]
    public async Task Save_RefusesInvalidInput_AndStoresNothing()
    {
        var fixture = new ManagerFixture();

        var exception = await Assert.ThrowsAsync<ValidationException>(() =>
            fixture.Manager.SaveAsync(ManagerFixture.SmtpCommand("""{"password":"oops"}""") with { ProjectCode = "Bad Code!" }));

        Assert.Contains(nameof(SaveChannelConfigurationCommand.ProjectCode), exception.Errors.Keys);
        Assert.Contains(nameof(SaveChannelConfigurationCommand.SettingsJson), exception.Errors.Keys);
        Assert.Empty(fixture.Repository.Rows);
        Assert.Equal(0, fixture.UnitOfWork.Saves);
        Assert.Empty(fixture.Audit.Entries);
    }

    [Fact]
    public async Task Activate_ArchivesThePreviousVersion_AndWritesAnAuditEntry()
    {
        var fixture = new ManagerFixture();
        var v1 = await fixture.Manager.SaveAsync(ManagerFixture.SmtpCommand());
        var v2 = await fixture.Manager.SaveAsync(ManagerFixture.SmtpCommand("""{"host":"b"}"""));

        var activated = await fixture.Manager.ActivateAsync(v2.Id);

        Assert.Equal((2, ConfigurationStatus.Active), (activated.Version, activated.Status));
        Assert.Equal(ConfigurationStatus.Archived, (await fixture.Manager.GetAsync(v1.Id)).Status);
        var entry = Assert.Single(fixture.Audit.Entries, e => e.Action == "ActivateVersion");
        Assert.Equal(v2.Id.ToString(), entry.ResourceId);
    }

    [Fact]
    public async Task Rollback_ReturnsToTheChosenVersion_AndToThePreviousActiveOne()
    {
        var fixture = new ManagerFixture();
        var v1 = await fixture.Manager.SaveAsync(ManagerFixture.SmtpCommand());
        await fixture.Manager.ActivateAsync(v1.Id);
        var v2 = await fixture.Manager.SaveAsync(ManagerFixture.SmtpCommand("""{"host":"b"}"""));
        fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        await fixture.Manager.ActivateAsync(v2.Id);
        fixture.Clock.Advance(TimeSpan.FromMinutes(1));

        var back = await fixture.Manager.RollbackAsync(new RollbackConfigurationCommand("default", ChannelType.Email, "Main SMTP"));
        var again = await fixture.Manager.RollbackAsync(new RollbackConfigurationCommand("default", ChannelType.Email, "Main SMTP", TargetVersion: 2));

        Assert.Equal(1, back.Version);
        Assert.Equal(2, again.Version);
        Assert.Equal(2, fixture.Audit.Entries.Count(e => e.Action == "RollbackVersion"));
    }

    [Fact]
    public async Task Rollback_OfAnUnknownSeries_IsNotFound()
    {
        var fixture = new ManagerFixture();

        await Assert.ThrowsAsync<NotFoundException>(() =>
            fixture.Manager.RollbackAsync(new RollbackConfigurationCommand("default", ChannelType.Email, "nope")));
    }

    [Fact]
    public async Task Get_AndActivate_OfAnUnknownId_AreNotFound()
    {
        var fixture = new ManagerFixture();

        await Assert.ThrowsAsync<NotFoundException>(() => fixture.Manager.GetAsync(Guid.NewGuid()));
        await Assert.ThrowsAsync<NotFoundException>(() => fixture.Manager.ActivateAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task ActivatingAnActiveVersion_IsADomainConflict_NotAnotherAuditEntry()
    {
        var fixture = new ManagerFixture();
        var v1 = await fixture.Manager.SaveAsync(ManagerFixture.SmtpCommand());
        await fixture.Manager.ActivateAsync(v1.Id);
        var before = fixture.Audit.Entries.Count;

        await Assert.ThrowsAsync<DomainException>(() => fixture.Manager.ActivateAsync(v1.Id));

        Assert.Equal(before, fixture.Audit.Entries.Count);
    }

    [Fact]
    public async Task List_FiltersAndOrders_NewestVersionFirstWithinASeries()
    {
        var fixture = new ManagerFixture();
        await fixture.Manager.SaveAsync(ManagerFixture.SmtpCommand());
        await fixture.Manager.SaveAsync(ManagerFixture.SmtpCommand("""{"host":"b"}"""));
        await fixture.Manager.SaveAsync(new SaveChannelConfigurationCommand("identity", ChannelType.Telegram, ProviderType.TelegramBot, "Bot", "{}", "123:token"));

        var all = await fixture.Manager.ListAsync(new ChannelConfigurationQuery());
        var email = await fixture.Manager.ListAsync(new ChannelConfigurationQuery(ChannelType: ChannelType.Email));
        var identity = await fixture.Manager.ListAsync(new ChannelConfigurationQuery(ProjectCode: "IDENTITY"));
        var active = await fixture.Manager.ListAsync(new ChannelConfigurationQuery(Status: ConfigurationStatus.Active));

        Assert.Equal(3, all.Count);
        Assert.Equal(new[] { 2, 1 }, email.Select(c => c.Version));
        Assert.Equal("Bot", Assert.Single(identity).Name);
        Assert.Empty(active);
    }

    [Fact]
    public async Task ActiveForRuntime_PicksTheActiveVersion_ByNameOrMostRecentlyActivated()
    {
        var fixture = new ManagerFixture();
        var main = await fixture.Manager.SaveAsync(ManagerFixture.SmtpCommand(secret: "main-secret"));
        await fixture.Manager.ActivateAsync(main.Id);
        fixture.Clock.Advance(TimeSpan.FromMinutes(5));
        var backup = await fixture.Manager.SaveAsync(ManagerFixture.SmtpCommand(secret: "backup-secret") with { Name = "Backup SMTP" });
        await fixture.Manager.ActivateAsync(backup.Id);

        var byName = await fixture.Manager.GetActiveForRuntimeAsync("default", ChannelType.Email, "Main SMTP");
        var latest = await fixture.Manager.GetActiveForRuntimeAsync("default", ChannelType.Email);
        var none = await fixture.Manager.GetActiveForRuntimeAsync("default", ChannelType.Telegram);
        var otherProject = await fixture.Manager.GetActiveForRuntimeAsync("shop", ChannelType.Email);

        Assert.Equal("main-secret", byName!.Secret);
        Assert.Equal("backup-secret", latest!.Secret);
        Assert.Equal("smtp.example.com", latest.Settings.GetProperty("host").GetString());
        Assert.Null(none);
        Assert.Null(otherProject);
    }

    [Fact]
    public async Task ActiveForRuntime_IgnoresDraftsAndArchivedVersions()
    {
        var fixture = new ManagerFixture();
        var v1 = await fixture.Manager.SaveAsync(ManagerFixture.SmtpCommand(secret: "one"));
        await fixture.Manager.SaveAsync(ManagerFixture.SmtpCommand(secret: "two"));

        Assert.Null(await fixture.Manager.GetActiveForRuntimeAsync("default", ChannelType.Email));

        await fixture.Manager.ActivateAsync(v1.Id);
        Assert.Equal("one", (await fixture.Manager.GetActiveForRuntimeAsync("default", ChannelType.Email))!.Secret);
    }

    [Fact]
    public async Task ARollback_TakesEffectAtOnce_WithoutRestartingAnything()
    {
        var fixture = new ManagerFixture();
        var good = await fixture.Manager.SaveAsync(ManagerFixture.SmtpCommand("""{"host":"good"}""", "good-secret"));
        await fixture.Manager.ActivateAsync(good.Id);
        fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        var broken = await fixture.Manager.SaveAsync(ManagerFixture.SmtpCommand("""{"host":"broken"}""", "broken-secret"));
        await fixture.Manager.ActivateAsync(broken.Id);
        Assert.Equal("broken", (await fixture.Manager.GetActiveForRuntimeAsync("default", ChannelType.Email))!.Settings.GetProperty("host").GetString());

        await fixture.Manager.RollbackAsync(new RollbackConfigurationCommand("default", ChannelType.Email, "Main SMTP"));

        var runtime = await fixture.Manager.GetActiveForRuntimeAsync("default", ChannelType.Email);
        Assert.Equal("good", runtime!.Settings.GetProperty("host").GetString());
        Assert.Equal("good-secret", runtime.Secret);
    }
}
