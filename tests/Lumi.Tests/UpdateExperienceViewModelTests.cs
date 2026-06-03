using System.Reflection;
using Lumi.Localization;
using Lumi.Models;
using Lumi.Services;
using Lumi.ViewModels;
using Xunit;

namespace Lumi.Tests;

public sealed class UpdateExperienceViewModelTests
{
    private const int SettingsNavIndex = MainViewModel.SettingsNavIndex;

    private static MainViewModel CreateMainViewModel()
    {
        Loc.Load("en");

        var data = new AppData
        {
            Settings = new UserSettings
            {
                AutoSaveChats = false,
                EnableMemoryAutoSave = false
            }
        };

        return new MainViewModel(new DataStore(data), new CopilotService(), new UpdateService());
    }

    private static void SetNonPublicProperty<T>(object instance, string propertyName, T value)
    {
        var property = instance.GetType().GetProperty(
            propertyName,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.NotNull(property);
        property!.SetValue(instance, value);
    }

    [Fact]
    public async Task CheckForUpdateAsync_WhenReadyToRestart_PreservesPendingRestartState()
    {
        var service = new UpdateService();
        SetNonPublicProperty(service, nameof(UpdateService.CurrentStatus), UpdateStatus.ReadyToRestart);
        SetNonPublicProperty(service, nameof(UpdateService.AvailableVersion), "1.2.3");
        SetNonPublicProperty(service, nameof(UpdateService.DownloadProgress), 100);

        await service.CheckForUpdateAsync();

        Assert.Equal(UpdateStatus.ReadyToRestart, service.CurrentStatus);
        Assert.Equal("1.2.3", service.AvailableVersion);
        Assert.Equal(100, service.DownloadProgress);
    }

    [Fact]
    public async Task CheckForUpdateAsync_WhenReadyToRestart_IgnoresDebugSimulation()
    {
        var originalVersion = Environment.GetEnvironmentVariable("LUMI_DEBUG_UPDATE_VERSION");
        var originalNotes = Environment.GetEnvironmentVariable("LUMI_DEBUG_UPDATE_NOTES");

        try
        {
            Environment.SetEnvironmentVariable("LUMI_DEBUG_UPDATE_VERSION", "9.9.9");
            Environment.SetEnvironmentVariable("LUMI_DEBUG_UPDATE_NOTES", "## Simulated");

            var service = new UpdateService();
            SetNonPublicProperty(service, nameof(UpdateService.CurrentStatus), UpdateStatus.ReadyToRestart);
            SetNonPublicProperty(service, nameof(UpdateService.AvailableVersion), "1.2.3");

            await service.CheckForUpdateAsync();

            Assert.Equal(UpdateStatus.ReadyToRestart, service.CurrentStatus);
            Assert.Equal("1.2.3", service.AvailableVersion);
        }
        finally
        {
            Environment.SetEnvironmentVariable("LUMI_DEBUG_UPDATE_VERSION", originalVersion);
            Environment.SetEnvironmentVariable("LUMI_DEBUG_UPDATE_NOTES", originalNotes);
        }
    }

    [Fact]
    public void InitialUpdateState_IsNeutralUntilFirstCheck()
    {
        var vm = CreateMainViewModel();

        Assert.Equal(Loc.Update_StatusIdle, vm.SettingsVM.UpdateStatusBadgeText);
        Assert.Equal(Loc.Update_HeroIdleTitle, vm.SettingsVM.UpdateHeroTitle);
        Assert.Equal(Loc.Update_HeroIdleBody, vm.SettingsVM.UpdateHeroDescription);
    }

    [Fact]
    public void SetNav_WhenUpdateNeedsAttention_OpensUpdateCenter()
    {
        var vm = CreateMainViewModel();

        vm.SettingsVM.IsUpdateAvailable = true;
        vm.SettingsVM.AvailableUpdateVersion = "1.2.3";
        vm.SettingsVM.ShouldAutoNavigateToUpdateCenter = true;
        vm.SettingsVM.SelectedPageIndex = 1;

        vm.SetNavCommand.Execute(SettingsNavIndex.ToString());

        Assert.Equal(SettingsNavIndex, vm.SelectedNavIndex);
        Assert.Equal(SettingsViewModel.AboutPageIndex, vm.SettingsVM.SelectedPageIndex);
        Assert.False(vm.SettingsVM.ShouldAutoNavigateToUpdateCenter);
    }

    [Fact]
    public void OpenUpdateCenterCommand_ShowsSettingsAboutPageAndHidesGlobalBanner()
    {
        var vm = CreateMainViewModel();

        vm.SettingsVM.IsUpdateAvailable = true;
        vm.SettingsVM.AvailableUpdateVersion = "1.2.3";

        Assert.True(vm.IsGlobalUpdateBannerVisible);

        vm.OpenUpdateCenterCommand.Execute(null);

        Assert.Equal(SettingsNavIndex, vm.SelectedNavIndex);
        Assert.Equal(SettingsViewModel.AboutPageIndex, vm.SettingsVM.SelectedPageIndex);
        Assert.False(vm.IsGlobalUpdateBannerVisible);
    }

    [Fact]
    public void DismissUpdateBanner_HidesGlobalBannerButKeepsUpdateBadge()
    {
        var vm = CreateMainViewModel();

        vm.SettingsVM.IsUpdateAvailable = true;
        vm.SettingsVM.AvailableUpdateVersion = "1.2.3";

        Assert.True(vm.IsGlobalUpdateBannerVisible);

        vm.SettingsVM.DismissUpdateBannerCommand.Execute(null);

        Assert.False(vm.IsGlobalUpdateBannerVisible);
        Assert.True(vm.SettingsVM.ShouldShowUpdateBadge);
        Assert.Equal("pending:1.2.3", vm.DataStore.Data.Settings.DismissedUpdateBannerToken);
    }

    [Fact]
    public void DismissUpdateBanner_StaysHiddenWhileUpdateDownloads()
    {
        var vm = CreateMainViewModel();

        vm.SettingsVM.IsUpdateAvailable = true;
        vm.SettingsVM.AvailableUpdateVersion = "1.2.3";
        vm.SettingsVM.DismissUpdateBannerCommand.Execute(null);

        vm.SettingsVM.IsUpdateAvailable = false;
        vm.SettingsVM.IsUpdateDownloading = true;

        Assert.False(vm.IsGlobalUpdateBannerVisible);
    }

    [Fact]
    public void DismissUpdateBanner_ReappearsWhenUpdateNeedsRestart()
    {
        var vm = CreateMainViewModel();

        vm.SettingsVM.IsUpdateAvailable = true;
        vm.SettingsVM.AvailableUpdateVersion = "1.2.3";
        vm.SettingsVM.DismissUpdateBannerCommand.Execute(null);

        vm.SettingsVM.IsUpdateAvailable = false;
        vm.SettingsVM.IsUpdateReadyToRestart = true;

        Assert.True(vm.IsGlobalUpdateBannerVisible);
    }

    [Fact]
    public void DismissUpdateBanner_DoesNotHideANewerVersion()
    {
        var vm = CreateMainViewModel();

        vm.SettingsVM.IsUpdateAvailable = true;
        vm.SettingsVM.AvailableUpdateVersion = "1.2.3";
        vm.SettingsVM.DismissUpdateBannerCommand.Execute(null);

        vm.SettingsVM.AvailableUpdateVersion = "1.2.4";

        Assert.True(vm.SettingsVM.ShouldShowUpdateBanner);
    }

    [Fact]
    public void UpdateReleaseNotesDisplayMarkdown_FallsBackWhenNotesAreMissing()
    {
        var vm = CreateMainViewModel();

        vm.SettingsVM.IsUpdateAvailable = true;
        vm.SettingsVM.UpdateReleaseNotesMarkdown = string.Empty;

        Assert.Equal(Loc.Update_ReleaseNotesFallback, vm.SettingsVM.UpdateReleaseNotesDisplayMarkdown);

        vm.SettingsVM.UpdateReleaseNotesMarkdown = "## Notes";

        Assert.Equal("## Notes", vm.SettingsVM.UpdateReleaseNotesDisplayMarkdown);
    }
}
