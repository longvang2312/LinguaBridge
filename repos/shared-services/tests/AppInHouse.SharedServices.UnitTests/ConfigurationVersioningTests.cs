using AppInHouse.SharedServices.Domain;

namespace AppInHouse.SharedServices.UnitTests;

// P0: versioned configuration with activation and rollback (domain rules, no database).

public class ConfigurationVersioningTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 11, 8, 0, 0, TimeSpan.Zero);
    private static DateTimeOffset At(int minutes) => T0.AddMinutes(minutes);

    private static ChannelConfiguration NewSeries(string settings = """{"host":"a"}""", string? secret = "cipher-1") =>
        ChannelConfiguration.CreateFirstVersion("default", ChannelType.Email, ProviderType.Smtp, "Main", settings, secret, T0);

    private static ChannelConfiguration Next(List<ChannelConfiguration> series, string settings = """{"host":"b"}""", string? secret = null, int minutes = 1)
    {
        var next = ChannelConfigurationVersioning.CreateNextVersion(series, ProviderType.Smtp, settings, secret, At(minutes));
        series.Add(next);
        return next;
    }

    [Fact]
    public void ANewConfiguration_IsDraftVersionOne()
    {
        var first = NewSeries();

        Assert.Equal(1, first.Version);
        Assert.Equal(ConfigurationStatus.Draft, first.Status);
        Assert.Null(first.ActivatedAt);
        Assert.Equal(TestStatus.Untested, first.LastTestStatus);
        Assert.NotEqual(Guid.Empty, first.Id);
    }

    [Fact]
    public void SavingANewVersion_CreatesVersionTwoAsDraft_AndLeavesVersionOneAlone()
    {
        var series = new List<ChannelConfiguration> { NewSeries() };

        var second = Next(series);

        Assert.Equal(2, second.Version);
        Assert.Equal(ConfigurationStatus.Draft, second.Status);
        Assert.Equal(ConfigurationStatus.Draft, series[0].Status);
        Assert.Equal("""{"host":"a"}""", series[0].SettingsJson);      // a saved version never changes
        Assert.Equal(second.ProjectCode, series[0].ProjectCode);
        Assert.Equal(second.Name, series[0].Name);
    }

    [Fact]
    public void ActivatingVersionTwo_ArchivesVersionOne_AndCanBeRolledBackToVersionOne()
    {
        // The acceptance scenario: Draft v1, save v2, activate v2 -> v1 is Archived, then roll back to v1.
        var series = new List<ChannelConfiguration> { NewSeries() };
        var v2 = Next(series);

        ChannelConfigurationVersioning.Activate(series, 2, At(5));

        Assert.Equal(ConfigurationStatus.Archived, series[0].Status);
        Assert.Equal(ConfigurationStatus.Active, v2.Status);
        Assert.Equal(At(5), v2.ActivatedAt);

        var restored = ChannelConfigurationVersioning.Rollback(series, targetVersion: 1, At(6));

        Assert.Equal(1, restored.Version);
        Assert.Equal(ConfigurationStatus.Active, series[0].Status);
        Assert.Equal(ConfigurationStatus.Archived, v2.Status);
        Assert.Single(series, v => v.Status == ConfigurationStatus.Active);
    }

    [Fact]
    public void ActivatingANewVersion_ArchivesTheActiveOne_AndTheOlderDrafts_ButNotNewerDrafts()
    {
        var series = new List<ChannelConfiguration> { NewSeries() };
        var v2 = Next(series);
        var v3 = Next(series);
        var v4 = Next(series);
        ChannelConfigurationVersioning.Activate(series, 2, At(5));

        ChannelConfigurationVersioning.Activate(series, 3, At(6));

        Assert.Equal(ConfigurationStatus.Archived, series[0].Status);   // old draft, superseded
        Assert.Equal(ConfigurationStatus.Archived, v2.Status);          // the active one it replaced
        Assert.Equal(ConfigurationStatus.Active, v3.Status);
        Assert.Equal(ConfigurationStatus.Draft, v4.Status);             // work in progress is kept
    }

    [Fact]
    public void ActivatingTheActiveVersion_IsRefused()
    {
        var series = new List<ChannelConfiguration> { NewSeries() };
        ChannelConfigurationVersioning.Activate(series, 1, At(1));

        var exception = Assert.Throws<DomainException>(() => ChannelConfigurationVersioning.Activate(series, 1, At(2)));

        Assert.Contains("already active", exception.Message);
    }

    [Fact]
    public void ActivatingAVersionThatDoesNotExist_IsRefused()
    {
        var series = new List<ChannelConfiguration> { NewSeries() };

        Assert.Throws<DomainException>(() => ChannelConfigurationVersioning.Activate(series, 9, At(1)));
    }

    [Fact]
    public void RollbackWithoutATarget_GoesBackToTheVersionThatWasActiveBefore()
    {
        var series = new List<ChannelConfiguration> { NewSeries() };
        ChannelConfigurationVersioning.Activate(series, 1, At(1));
        Next(series);
        ChannelConfigurationVersioning.Activate(series, 2, At(2));
        Next(series);
        ChannelConfigurationVersioning.Activate(series, 3, At(3));

        var restored = ChannelConfigurationVersioning.Rollback(series, targetVersion: null, At(4));

        Assert.Equal(2, restored.Version);   // the most recently active one, not simply "the one below"
        Assert.Equal(ConfigurationStatus.Archived, series.Single(v => v.Version == 3).Status);
        Assert.Equal(ConfigurationStatus.Archived, series.Single(v => v.Version == 1).Status);
    }

    [Fact]
    public void RollbackTwice_FlipsBetweenTheTwoLastActiveVersions()
    {
        var series = new List<ChannelConfiguration> { NewSeries() };
        ChannelConfigurationVersioning.Activate(series, 1, At(1));
        Next(series);
        ChannelConfigurationVersioning.Activate(series, 2, At(2));

        Assert.Equal(1, ChannelConfigurationVersioning.Rollback(series, null, At(3)).Version);
        Assert.Equal(2, ChannelConfigurationVersioning.Rollback(series, null, At(4)).Version);
    }

    [Fact]
    public void Rollback_KeepsDraftsThatAreInProgress()
    {
        var series = new List<ChannelConfiguration> { NewSeries() };
        ChannelConfigurationVersioning.Activate(series, 1, At(1));
        Next(series);
        ChannelConfigurationVersioning.Activate(series, 2, At(2));
        var draft = Next(series);

        ChannelConfigurationVersioning.Rollback(series, null, At(3));

        Assert.Equal(ConfigurationStatus.Draft, draft.Status);
    }

    [Fact]
    public void RollbackWithNothingActive_OrNothingEarlier_IsRefused()
    {
        var noActive = new List<ChannelConfiguration> { NewSeries() };
        Assert.Throws<DomainException>(() => ChannelConfigurationVersioning.Rollback(noActive, null, At(1)));

        var onlyOne = new List<ChannelConfiguration> { NewSeries() };
        ChannelConfigurationVersioning.Activate(onlyOne, 1, At(1));
        var exception = Assert.Throws<DomainException>(() => ChannelConfigurationVersioning.Rollback(onlyOne, null, At(2)));
        Assert.Contains("nothing to roll back to", exception.Message);
    }

    [Fact]
    public void RollbackToADraftOrToTheActiveVersion_IsRefused()
    {
        var series = new List<ChannelConfiguration> { NewSeries() };
        ChannelConfigurationVersioning.Activate(series, 1, At(1));
        Next(series);   // v2 stays a draft

        Assert.Throws<DomainException>(() => ChannelConfigurationVersioning.Rollback(series, 2, At(2)));   // a draft: activate it instead
        Assert.Throws<DomainException>(() => ChannelConfigurationVersioning.Rollback(series, 1, At(2)));   // already active
    }

    [Fact]
    public void VersionNumbers_AreNeverReused()
    {
        var series = new List<ChannelConfiguration> { NewSeries() };
        Next(series);
        Next(series);
        ChannelConfigurationVersioning.Activate(series, 3, At(5));

        var next = Next(series);

        Assert.Equal(4, next.Version);
        Assert.Equal(new[] { 1, 2, 3, 4 }, series.Select(v => v.Version).Order());
    }

    [Fact]
    public void ANewVersionWithoutASecret_KeepsTheSecretOfTheLatestVersion()
    {
        var series = new List<ChannelConfiguration> { NewSeries(secret: "cipher-1") };

        var withoutSecret = Next(series, secret: null);
        var withNewSecret = Next(series, secret: "cipher-2");
        var againWithout = Next(series, secret: null);

        Assert.Equal("cipher-1", withoutSecret.EncryptedSecret);
        Assert.Equal("cipher-2", withNewSecret.EncryptedSecret);
        Assert.Equal("cipher-2", againWithout.EncryptedSecret);
    }

    [Fact]
    public void AtMostOneVersionIsActive_Throughout()
    {
        var series = new List<ChannelConfiguration> { NewSeries() };
        for (var round = 0; round < 6; round++) Next(series);

        foreach (var version in new[] { 3, 1, 5, 2, 7, 4 })
        {
            ChannelConfigurationVersioning.Activate(series, version, At(10 + version));
            Assert.Single(series, v => v.Status == ConfigurationStatus.Active);
            Assert.Equal(version, ChannelConfigurationVersioning.ActiveVersion(series)!.Version);
        }
    }

    [Fact]
    public void AProviderThatDoesNotFitTheChannel_IsRefused()
    {
        Assert.Throws<DomainException>(() =>
            ChannelConfiguration.CreateFirstVersion("default", ChannelType.Email, ProviderType.S3, "x", "{}", null, T0));
        Assert.Throws<DomainException>(() =>
            ChannelConfiguration.CreateFirstVersion("default", ChannelType.Storage, ProviderType.Smtp, "x", "{}", null, T0));
        Assert.Throws<DomainException>(() =>
            ChannelConfiguration.CreateFirstVersion("default", ChannelType.Telegram, ProviderType.SendGrid, "x", "{}", null, T0));
        _ = ChannelConfiguration.CreateFirstVersion("default", ChannelType.Telegram, ProviderType.TelegramBot, "x", "{}", null, T0);
    }

    [Fact]
    public void RecordingATest_DoesNotChangeStatusOrSettings()
    {
        var first = NewSeries();

        first.RecordTest(TestStatus.Failed, At(3));

        Assert.Equal(TestStatus.Failed, first.LastTestStatus);
        Assert.Equal(At(3), first.LastTestedAt);
        Assert.Equal(ConfigurationStatus.Draft, first.Status);
        Assert.Equal("""{"host":"a"}""", first.SettingsJson);
    }

    [Fact]
    public void CreatingTheNextVersionOfAnEmptySeries_IsRefused()
    {
        Assert.Throws<DomainException>(() =>
            ChannelConfigurationVersioning.CreateNextVersion([], ProviderType.Smtp, "{}", null, T0));
    }
}
