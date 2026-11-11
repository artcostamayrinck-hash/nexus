using Microsoft.Extensions.Logging.Abstractions;
using Nexus.Contracts.Automation;
using Nexus.Contracts.Commands;
using Nexus.Contracts.Connections;
using Nexus.Contracts.Devices;
using Nexus.Contracts.Events;
using Nexus.Contracts.Messaging;
using Nexus.Contracts.Modules;
using Nexus.Contracts.Notifications;
using Nexus.Contracts.Security;
using Nexus.Contracts.Sync;
using Nexus.Contracts.Transfers;
using Nexus.Core.Abstractions;
using Nexus.Core.Automation;
using Nexus.Core.Connections;
using Nexus.Core.Devices;
using Nexus.Core.Errors;
using Nexus.Core.Modules;
using Nexus.Core.Services;
using Nexus.Core.State;
using Nexus.Core.Sync;
using Nexus.Core.Transfers;
using Nexus.Desktop.Services.ViewModels;
using Nexus.Infrastructure.Automation;
using Nexus.Infrastructure.Modules;
using Nexus.Infrastructure.Persistence;
using Nexus.Infrastructure.Repositories;
using Nexus.Infrastructure.Security;
using Nexus.Infrastructure.Settings;
using Nexus.Mobile.Services.ViewModels;
using Xunit;

namespace Nexus.UnitTests;

public sealed class PanelAndAutomationTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "nexus-p9-unit-" + Guid.NewGuid().ToString("N"));
    private readonly ManualClock _clock = new(new DateTimeOffset(2026, 11, 11, 14, 0, 0, TimeSpan.Zero));

    public PanelAndAutomationTests()
    {
        Directory.CreateDirectory(_tempDir);
    }

    [Fact]
    public async Task PanelLayoutService_SupportsAll10WidgetsAddRemoveResizeReorderThemesAndAutoSaves()
    {
        var harness = await CreateHarnessAsync();

        var initialProfiles = await harness.PanelService.GetProfilesAsync();
        Assert.True(initialProfiles.IsSuccess);
        Assert.True(initialProfiles.Value.Count >= 2);

        var custom = await harness.PanelService.CreateProfileAsync(
            "Meu Painel Completo",
            PanelThemePreset.OledBlack,
            PanelAccentColor.Emerald);
        Assert.True(custom.IsSuccess);

        var activated = await harness.PanelService.ActivateProfileAsync(custom.Value.ProfileId);
        Assert.True(activated.IsSuccess);
        Assert.True(activated.Value.IsActive);
        Assert.NotEmpty(activated.Value.Widgets);

        var cleared = await harness.PanelService.SaveProfileAsync(activated.Value with
        {
            Widgets = Array.Empty<PanelWidgetItem>()
        });
        Assert.True(cleared.IsSuccess);
        Assert.Empty(cleared.Value.Widgets);

        // Add all 10 required widget types cleanly
        foreach (var widgetType in Enum.GetValues<PanelWidgetType>())
        {
            var addRes = await harness.PanelService.AddWidgetAsync(widgetType, $"W-{widgetType}", PanelWidgetSize.Compact1x1);
            Assert.True(addRes.IsSuccess, addRes.Error?.Message);
        }

        var profileAfterAdds = (await harness.PanelService.GetActiveProfileAsync()).Value;
        Assert.Equal(Enum.GetValues<PanelWidgetType>().Length, profileAfterAdds.Widgets.Count);
        foreach (var expectedType in Enum.GetValues<PanelWidgetType>())
        {
            Assert.Contains(profileAfterAdds.Widgets, w => w.WidgetType == expectedType);
        }

        var firstWidget = profileAfterAdds.Widgets[0];
        var lastIndex = profileAfterAdds.Widgets.Count - 1;

        var resized = await harness.PanelService.ResizeWidgetAsync(firstWidget.WidgetId, PanelWidgetSize.Large2x2);
        Assert.True(resized.IsSuccess);
        var updatedFirst = resized.Value.Widgets.Single(w => w.WidgetId == firstWidget.WidgetId);
        Assert.Equal(PanelWidgetSize.Large2x2, updatedFirst.Size);
        Assert.Equal(2, updatedFirst.ColumnSpan);
        Assert.Equal(2, updatedFirst.RowSpan);

        var moved = await harness.PanelService.MoveWidgetAsync(firstWidget.WidgetId, lastIndex);
        Assert.True(moved.IsSuccess);
        Assert.Equal(firstWidget.WidgetId, moved.Value.Widgets.OrderBy(w => w.SortOrder).Last().WidgetId);

        var removed = await harness.PanelService.RemoveWidgetAsync(firstWidget.WidgetId);
        Assert.True(removed.IsSuccess);
        Assert.Equal(9, removed.Value.Widgets.Count);

        var themed = await harness.PanelService.UpdateAppearanceAsync(PanelThemePreset.HighContrast, PanelAccentColor.Rose, 3);
        Assert.True(themed.IsSuccess);
        Assert.Equal(PanelThemePreset.HighContrast, themed.Value.Theme);
        Assert.Equal(PanelAccentColor.Rose, themed.Value.Accent);
        Assert.Equal(3, themed.Value.GridColumns);

        // Recreate repository/service on the same SQLite DB to verify automatic save persisted
        var harness2 = await CreateHarnessAsync();
        var reloaded = await harness2.PanelService.GetActiveProfileAsync();
        Assert.True(reloaded.IsSuccess);
        Assert.Equal(custom.Value.ProfileId, reloaded.Value.ProfileId);
        Assert.Equal(PanelThemePreset.HighContrast, reloaded.Value.Theme);
        Assert.Equal(3, reloaded.Value.GridColumns);
        Assert.Equal(9, reloaded.Value.Widgets.Count);
    }

    [Fact]
    public async Task AutomationEngine_SimpleTrigger_ActivatesProfileEmitsIndicatorAndLogsNotification()
    {
        var harness = await CreateHarnessAsync();
        var secondProfile = (await harness.PanelService.GetProfilesAsync()).Value[1];

        var rule = new AutomationRule
        {
            RuleId = Guid.NewGuid(),
            Name = "Conectar PC -> Ativar Perfil + Aviso",
            Trigger = AutomationTriggerType.ConnectedToComputer,
            Actions =
            [
                new AutomationAction
                {
                    ActionType = AutomationActionType.ActivatePanelProfile,
                    TargetProfileId = secondProfile.ProfileId,
                    Label = "Ativar perfil"
                },
                new AutomationAction
                {
                    ActionType = AutomationActionType.ShowPanelIndicator,
                    Title = "Conectado",
                    Message = "Perfil alternado com sucesso.",
                    Label = "Indicador"
                },
                new AutomationAction
                {
                    ActionType = AutomationActionType.SendNotification,
                    Title = "PC Conectado",
                    Message = "NEXUS conectado.",
                    Label = "Notificar"
                }
            ]
        };

        Assert.True((await harness.Engine.SaveRuleAsync(rule)).IsSuccess);

        var eval = await harness.Engine.EvaluateTriggerAsync(new AutomationTriggerEvent
        {
            Trigger = AutomationTriggerType.ConnectedToComputer,
            SourceDeviceId = Guid.NewGuid(),
            IsConnected = true,
            ActiveRouteKind = "LocalLan"
        });

        Assert.True(eval.IsSuccess);
        var exec = Assert.Single(eval.Value);
        Assert.Equal(AutomationExecutionOutcome.Succeeded, exec.Outcome);

        var activeProfile = (await harness.PanelService.GetActiveProfileAsync()).Value;
        Assert.Equal(secondProfile.ProfileId, activeProfile.ProfileId);
        Assert.NotEmpty(harness.Engine.ActiveIndicators);

        var history = (await harness.Engine.GetExecutionHistoryAsync()).Value;
        Assert.Contains(history, h => h.ExecutionId == exec.ExecutionId && h.Outcome == AutomationExecutionOutcome.Succeeded);

        var notifyFeed = (await harness.NotifyService.ListHistoryAsync()).Value;
        Assert.Contains(notifyFeed, n => n.Title == "PC Conectado");
    }

    [Fact]
    public async Task AutomationEngine_UnmetConditions_DoNotExecuteAndDoNotConsumeCooldown()
    {
        var harness = await CreateHarnessAsync(initialBatteryPercent: 30);

        var rule = new AutomationRule
        {
            RuleId = Guid.NewGuid(),
            Name = "Regra Condicional LAN + Bateria >= 50%",
            Trigger = AutomationTriggerType.TransferStarted,
            ConditionLogic = AutomationLogicMode.All,
            CooldownSeconds = 30,
            Conditions =
            [
                new AutomationCondition { Operator = AutomationConditionOperator.BatteryAtOrAbove, ExpectedValue = "50" },
                new AutomationCondition { Operator = AutomationConditionOperator.RouteKindEquals, ExpectedValue = "LocalLan" }
            ],
            Actions =
            [
                new AutomationAction
                {
                    ActionType = AutomationActionType.ShowPanelIndicator,
                    Title = "Transferindo na LAN",
                    Message = "Transferência iniciada com bateria suficiente."
                }
            ]
        };

        Assert.True((await harness.Engine.SaveRuleAsync(rule)).IsSuccess);

        // First attempt at 30% battery fails condition without consuming cooldown
        var unmet = await harness.Engine.EvaluateTriggerAsync(new AutomationTriggerEvent
        {
            Trigger = AutomationTriggerType.TransferStarted,
            IsConnected = true,
            ActiveRouteKind = "LocalLan"
        });
        Assert.Equal(AutomationExecutionOutcome.ConditionsNotMet, Assert.Single(unmet.Value).Outcome);
        Assert.Empty(harness.Engine.ActiveIndicators);

        // 1 second later battery is 75%: should fire immediately because cooldown was NOT consumed
        _clock.Advance(TimeSpan.FromSeconds(1));
        harness.BatteryProvider.Battery = new BatteryStateSnapshot
        {
            IsBatteryPresent = true,
            IsAvailable = true,
            ChargePercent = 75,
            IsCharging = true,
            StatusMessage = "75%"
        };

        var met = await harness.Engine.EvaluateTriggerAsync(new AutomationTriggerEvent
        {
            Trigger = AutomationTriggerType.TransferStarted,
            IsConnected = true,
            ActiveRouteKind = "LocalLan"
        });
        Assert.Equal(AutomationExecutionOutcome.Succeeded, Assert.Single(met.Value).Outcome);
        Assert.Single(harness.Engine.ActiveIndicators);
    }

    [Fact]
    public async Task AutomationEngine_CooldownAndRateLimits_EnforceLimitsSafely()
    {
        var harness = await CreateHarnessAsync();

        var rule = new AutomationRule
        {
            RuleId = Guid.NewGuid(),
            Name = "Regra Rate Limited",
            Trigger = AutomationTriggerType.NetworkRouteChanged,
            CooldownSeconds = 5,
            MaxExecutionsPerMinute = 2,
            Actions =
            [
                new AutomationAction { ActionType = AutomationActionType.ShowPanelIndicator, Title = "Rota", Message = "Mudou" }
            ]
        };

        Assert.True((await harness.Engine.SaveRuleAsync(rule)).IsSuccess);

        var r1 = await harness.Engine.TriggerRuleManuallyAsync(rule.RuleId);
        Assert.Equal(AutomationExecutionOutcome.Succeeded, r1.Value.Outcome);

        // 2s later -> cooldown still active
        _clock.Advance(TimeSpan.FromSeconds(2));
        var r2 = await harness.Engine.TriggerRuleManuallyAsync(rule.RuleId);
        Assert.Equal(AutomationExecutionOutcome.SkippedCooldown, r2.Value.Outcome);

        // +4s -> cooldown elapsed (total 6s), 2nd execution in minute passes
        _clock.Advance(TimeSpan.FromSeconds(4));
        var r3 = await harness.Engine.TriggerRuleManuallyAsync(rule.RuleId);
        Assert.Equal(AutomationExecutionOutcome.Succeeded, r3.Value.Outcome);

        // +6s -> cooldown elapsed, but 2/minute already used -> RateLimit
        _clock.Advance(TimeSpan.FromSeconds(6));
        var r4 = await harness.Engine.TriggerRuleManuallyAsync(rule.RuleId);
        Assert.Equal(AutomationExecutionOutcome.SkippedRateLimit, r4.Value.Outcome);

        // +61s -> sliding window cleared -> succeeds again
        _clock.Advance(TimeSpan.FromSeconds(61));
        var r5 = await harness.Engine.TriggerRuleManuallyAsync(rule.RuleId);
        Assert.Equal(AutomationExecutionOutcome.Succeeded, r5.Value.Outcome);
    }

    [Fact]
    public async Task AutomationEngine_LoopProtection_BlocksCyclesAndExcessiveChainDepth()
    {
        var harness = await CreateHarnessAsync();
        var rule = new AutomationRule
        {
            RuleId = Guid.NewGuid(),
            Name = "Regra em Cadeia",
            Trigger = AutomationTriggerType.ConnectedToComputer,
            Actions =
            [
                new AutomationAction { ActionType = AutomationActionType.ShowPanelIndicator, Title = "LoopTest", Message = "Ping" }
            ]
        };
        Assert.True((await harness.Engine.SaveRuleAsync(rule)).IsSuccess);

        var visitedCycle = await harness.Engine.EvaluateTriggerAsync(new AutomationTriggerEvent
        {
            Trigger = AutomationTriggerType.ConnectedToComputer,
            VisitedRuleIds = [rule.RuleId],
            ChainDepth = 1
        });
        Assert.Equal(AutomationExecutionOutcome.SkippedLoopGuard, Assert.Single(visitedCycle.Value).Outcome);

        var maxDepthExceeded = await harness.Engine.EvaluateTriggerAsync(new AutomationTriggerEvent
        {
            Trigger = AutomationTriggerType.ConnectedToComputer,
            ChainDepth = 4
        });
        Assert.Equal(AutomationExecutionOutcome.SkippedLoopGuard, Assert.Single(maxDepthExceeded.Value).Outcome);
    }

    [Fact]
    public async Task AutomationEngine_SensitiveActionsAllowlistAndPermissions_AreStrictlyEnforced()
    {
        var harness = await CreateHarnessAsync();
        await harness.Engine.SaveSettingsAsync(new AutomationEngineSettings
        {
            ApprovedApps =
            [
                new ApprovedAppShortcut { ShortcutId = "calc", DisplayName = "Calculadora", ExecutablePath = "calc.exe", RequireConfirmation = true }
            ]
        });

        var powerRule = new AutomationRule
        {
            RuleId = Guid.NewGuid(),
            Name = "Bloquear PC",
            Trigger = AutomationTriggerType.Manual,
            RequireSensitiveAuthorization = true,
            IsSensitiveAuthorized = false,
            Actions =
            [
                new AutomationAction
                {
                    ActionType = AutomationActionType.ExecuteControlCommand,
                    ControlAction = RemoteControlActionType.PowerLock,
                    IsSensitiveAction = true
                }
            ]
        };
        Assert.True((await harness.Engine.SaveRuleAsync(powerRule)).IsSuccess);

        // 1) Missing Automation permission rejects caller
        var noAutoPerm = await harness.Engine.TriggerRuleManuallyAsync(powerRule.RuleId, NexusPermission.ViewStatus);
        Assert.True(noAutoPerm.IsFailure);
        Assert.Equal(ErrorCategory.Forbidden, noAutoPerm.Error!.Category);

        // 2) Missing SystemPower permission blocks action
        var missingPowerPerm = await harness.Engine.TriggerRuleManuallyAsync(
            powerRule.RuleId,
            NexusPermission.Automation | NexusPermission.ControlInput,
            approvedSensitiveExecution: true);
        Assert.Equal(AutomationExecutionOutcome.BlockedPermission, missingPowerPerm.Value.Outcome);

        // 3) Unapproved sensitive rule blocks execution
        var blockedAuth = await harness.Engine.TriggerRuleManuallyAsync(
            powerRule.RuleId,
            NexusPermission.All,
            approvedSensitiveExecution: false);
        Assert.Equal(AutomationExecutionOutcome.BlockedAuthorization, blockedAuth.Value.Outcome);

        // 4) Explicitly approved executes safely (DryRun)
        var approved = await harness.Engine.TriggerRuleManuallyAsync(
            powerRule.RuleId,
            NexusPermission.All,
            approvedSensitiveExecution: true);
        Assert.Equal(AutomationExecutionOutcome.Succeeded, approved.Value.Outcome);

        // 5) Unlisted app shortcut fails allowlist guard
        var nonAllowlistedAppRule = new AutomationRule
        {
            RuleId = Guid.NewGuid(),
            Name = "Abrir executável não aprovado",
            Trigger = AutomationTriggerType.Manual,
            IsSensitiveAuthorized = true,
            Actions =
            [
                new AutomationAction
                {
                    ActionType = AutomationActionType.LaunchApprovedApp,
                    ShortcutId = "powershell-evil",
                    IsSensitiveAction = true
                }
            ]
        };
        Assert.True((await harness.Engine.SaveRuleAsync(nonAllowlistedAppRule)).IsSuccess);
        var rejectedApp = await harness.Engine.TriggerRuleManuallyAsync(nonAllowlistedAppRule.RuleId, NexusPermission.All, approvedSensitiveExecution: true);
        Assert.Equal(AutomationExecutionOutcome.Failed, rejectedApp.Value.Outcome);
        Assert.Contains("allowlist", rejectedApp.Value.Detail, StringComparison.OrdinalIgnoreCase);

        // 6) Approved allowlisted app succeeds
        var allowlistedAppRule = new AutomationRule
        {
            RuleId = Guid.NewGuid(),
            Name = "Abrir Calculadora Aprovada",
            Trigger = AutomationTriggerType.Manual,
            IsSensitiveAuthorized = true,
            Actions =
            [
                new AutomationAction
                {
                    ActionType = AutomationActionType.LaunchApprovedApp,
                    ShortcutId = "calc",
                    IsSensitiveAction = true
                }
            ]
        };
        Assert.True((await harness.Engine.SaveRuleAsync(allowlistedAppRule)).IsSuccess);
        var launchedApp = await harness.Engine.TriggerRuleManuallyAsync(allowlistedAppRule.RuleId, NexusPermission.All, approvedSensitiveExecution: true);
        Assert.Equal(AutomationExecutionOutcome.Succeeded, launchedApp.Value.Outcome);
    }

    [Fact]
    public async Task AutomationEngine_ImmediateDisableAndKillSwitch_HaltExecutions()
    {
        var harness = await CreateHarnessAsync();
        var rule = new AutomationRule
        {
            RuleId = Guid.NewGuid(),
            Name = "Indicador simples",
            Trigger = AutomationTriggerType.Manual,
            Actions = [new AutomationAction { ActionType = AutomationActionType.ShowPanelIndicator, Title = "OK", Message = "OK" }]
        };
        Assert.True((await harness.Engine.SaveRuleAsync(rule)).IsSuccess);

        Assert.True((await harness.Engine.SetRuleEnabledAsync(rule.RuleId, false)).IsSuccess);
        var skippedDisabled = await harness.Engine.TriggerRuleManuallyAsync(rule.RuleId);
        Assert.Equal(AutomationExecutionOutcome.SkippedDisabled, skippedDisabled.Value.Outcome);

        Assert.True((await harness.Engine.SetRuleEnabledAsync(rule.RuleId, true)).IsSuccess);
        Assert.True((await harness.Engine.SetEmergencyKillSwitchAsync(true)).IsSuccess);

        var killed = await harness.Engine.TriggerRuleManuallyAsync(rule.RuleId);
        Assert.Equal(AutomationExecutionOutcome.SkippedDisabled, killed.Value.Outcome);
        Assert.Contains("Kill Switch", killed.Value.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BatteryProviderAndUnavailableHardware_NeverFabricateValues()
    {
        var winProvider = new WindowsDeviceBatteryProvider();
        var realSnapshot = await winProvider.ReadBatteryStateAsync();
        if (!realSnapshot.IsAvailable)
        {
            Assert.Null(realSnapshot.ChargePercent);
        }
        else
        {
            Assert.InRange(realSnapshot.ChargePercent!.Value, 0, 100);
        }

        // Verify engine handles unavailable battery cleanly without false trigger fires
        var harness = await CreateHarnessAsync(initialBatteryPercent: null);
        var lowBatteryRule = new AutomationRule
        {
            RuleId = Guid.NewGuid(),
            Name = "Alerta bateria <= 20%",
            Trigger = AutomationTriggerType.BatteryLevelThreshold,
            BatteryThresholdPercent = 20,
            Actions = [new AutomationAction { ActionType = AutomationActionType.ShowPanelIndicator, Title = "Low", Message = "Low" }]
        };
        Assert.True((await harness.Engine.SaveRuleAsync(lowBatteryRule)).IsSuccess);

        var eval = await harness.Engine.EvaluateTriggerAsync(new AutomationTriggerEvent
        {
            Trigger = AutomationTriggerType.BatteryLevelThreshold
        });
        Assert.Equal(AutomationExecutionOutcome.ConditionsNotMet, Assert.Single(eval.Value).Outcome);
        Assert.Empty(harness.Engine.ActiveIndicators);
    }

    [Fact]
    public async Task MobileAndDesktopViewModels_CustomizePanelManageRulesAndReadHistory()
    {
        var harness = await CreateHarnessAsync(initialBatteryPercent: 85);

        var panelVm = new MobilePanelViewModel(
            harness.PanelService,
            harness.Engine,
            new StubRemotePanelClient(),
            new StubModulesClient(),
            new StubRouteOrchestrator(),
            new StubRealtimeChannel(),
            new StubTransferManager(),
            new StubNotesService(),
            harness.LocalDevice);

        await panelVm.InitializeAsync();
        Assert.NotEmpty(panelVm.Profiles);
        Assert.NotEmpty(panelVm.Widgets);

        panelVm.SelectedWidgetToAdd = PanelWidgetType.NetworkInfo;
        panelVm.NewWidgetTitle = "Minha Rede";
        await panelVm.AddWidgetCommand.ExecuteAsync();
        Assert.Contains(panelVm.Widgets, w => w.Title == "Minha Rede");

        var automateVm = new MobileAutomateViewModel(
            harness.Engine,
            harness.PanelService,
            harness.BatteryProvider,
            new StubRemotePanelClient(),
            new StubRealtimeChannel());

        await automateVm.InitializeAsync();
        Assert.Contains("85%", automateVm.BatterySummary);
        Assert.Contains("Android", automateVm.AndroidLimitationNotice);

        await automateVm.ApplyTransferIndicatorTemplateCommand.ExecuteAsync();
        await automateVm.SaveRuleCommand.ExecuteAsync();
        Assert.Single(automateVm.Rules);

        await automateVm.RunRuleManuallyCommand.ExecuteAsync(automateVm.Rules[0]);
        Assert.NotEmpty(automateVm.ExecutionHistory);

        var desktopVm = new AutomationViewModel(
            harness.PanelService,
            harness.Engine,
            harness.BatteryProvider,
            new StubRemotePanelClient(),
            new StubRealtimeChannel());
        await desktopVm.InitializeAsync();
        Assert.Single(desktopVm.Rules);

        desktopVm.NewShortcutId = "vscode";
        desktopVm.NewShortcutName = "VS Code";
        desktopVm.NewShortcutPath = @"C:\Users\demo\Code.exe";
        await desktopVm.AddApprovedAppCommand.ExecuteAsync();
        Assert.Contains(desktopVm.ApprovedApps, a => a.ShortcutId == "vscode");
    }

    private async Task<UnitHarness> CreateHarnessAsync(int? initialBatteryPercent = 80)
    {
        var options = new NexusStorageOptions
        {
            RootDirectory = _tempDir,
            DatabaseFileName = "panel-automation-unit.db",
            EnablePooling = false
        };
        var factory = new SqliteConnectionFactory(options);
        var failureHandler = new StorageFailureHandler(NullLogger<StorageFailureHandler>.Instance);
        var serializer = new SystemTextJsonSerializer();

        var localDevice = new DefaultLocalDeviceContext(
            Guid.NewGuid(),
            "UnitTestNode",
            DevicePlatform.Windows,
            DeviceType.Desktop,
            "Windows 11",
            "0.9.0",
            DeviceCapability.All);
        var stateMgr = new InMemoryStateManager<NexusRuntimeState>(new NexusRuntimeState { LocalDevice = localDevice.ToDeviceInfo() });

        var initializer = new SqliteStorageInitializer(factory, failureHandler, stateMgr, _clock, NullLogger<SqliteStorageInitializer>.Instance);
        await initializer.InitializeAsync();

        var settingsStore = new SqliteSettingsStore(factory, failureHandler, serializer, _clock);
        var panelRepo = new SqlitePanelProfileRepository(factory, failureHandler, serializer);
        var ruleRepo = new SqliteAutomationRuleRepository(factory, failureHandler, serializer);
        var execRepo = new SqliteAutomationExecutionRepository(factory, failureHandler);

        var modSettings = new ModuleSettingsService(settingsStore, _clock, NullLogger<ModuleSettingsService>.Instance);
        var notifyRepo = new SqliteMobileNotificationFeedRepository(factory, failureHandler);
        var notifyService = new NexusNotifyService(modSettings, notifyRepo, _clock, NullLogger<NexusNotifyService>.Instance);
        var ctrlOptions = new WindowsControlOptions
        {
            DryRunPowerActions = true,
            SimulateInputInHeadlessTests = true,
            DryRunApprovedAppLaunches = true
        };
        var controlExecutor = new WindowsControlExecutor(modSettings, localDevice, ctrlOptions, _clock, NullLogger<WindowsControlExecutor>.Instance);
        var mediaService = new WindowsMediaService(modSettings, controlExecutor, localDevice, _clock, NullLogger<WindowsMediaService>.Instance);
        var appLauncher = new WindowsApprovedAppLauncher(ctrlOptions, NullLogger<WindowsApprovedAppLauncher>.Instance);
        var batteryProvider = new FakeBatteryProvider(initialBatteryPercent);

        var panelService = new PanelLayoutService(panelRepo, _clock, NullLogger<PanelLayoutService>.Instance);
        var engine = new AutomationEngineService(
            ruleRepo,
            execRepo,
            settingsStore,
            panelService,
            batteryProvider,
            appLauncher,
            controlExecutor,
            mediaService,
            notifyService,
            localDevice,
            _clock,
            NullLogger<AutomationEngineService>.Instance);

        return new UnitHarness(panelService, engine, notifyService, batteryProvider, localDevice);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, recursive: true); } catch { }
        }
    }

    private sealed record UnitHarness(
        PanelLayoutService PanelService,
        AutomationEngineService Engine,
        NexusNotifyService NotifyService,
        FakeBatteryProvider BatteryProvider,
        ILocalDeviceContext LocalDevice);

    private sealed class ManualClock(DateTimeOffset start) : IClock
    {
        public DateTimeOffset UtcNow { get; private set; } = start;
        public void Advance(TimeSpan delta) => UtcNow = UtcNow.Add(delta);
    }

    private sealed class FakeBatteryProvider(int? percent) : IDeviceBatteryProvider
    {
        public BatteryStateSnapshot Battery { get; set; } = percent.HasValue
            ? new BatteryStateSnapshot
            {
                IsBatteryPresent = true,
                IsAvailable = true,
                ChargePercent = percent.Value,
                IsCharging = false,
                StatusMessage = $"Bateria simulada em teste unitário: {percent.Value}%"
            }
            : new BatteryStateSnapshot
            {
                IsBatteryPresent = false,
                IsAvailable = false,
                ChargePercent = null,
                StatusMessage = "Bateria indisponível neste dispositivo."
            };

        public Task<BatteryStateSnapshot> ReadBatteryStateAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(Battery);
    }

    private sealed class StubRemotePanelClient : IPanelAndAutomationClient
    {
        public Task<Result<IReadOnlyList<PanelProfile>>> GetRemoteProfilesAsync(CancellationToken ct = default)
            => Task.FromResult(Result<IReadOnlyList<PanelProfile>>.Failure(NexusError.Network("Stub offline")));
        public Task<Result<PanelProfile>> SaveRemoteProfileAsync(PanelProfile profile, CancellationToken ct = default)
            => Task.FromResult(Result<PanelProfile>.Success(profile));
        public Task<Result<PanelProfile>> ActivateRemoteProfileAsync(Guid profileId, CancellationToken ct = default)
            => Task.FromResult(Result<PanelProfile>.Failure(NexusError.Network("Stub offline")));
        public Task<Result<AutomationEngineSettings>> GetRemoteEngineSettingsAsync(CancellationToken ct = default)
            => Task.FromResult(Result<AutomationEngineSettings>.Success(new AutomationEngineSettings()));
        public Task<Result<AutomationEngineSettings>> SaveRemoteEngineSettingsAsync(AutomationEngineSettings settings, CancellationToken ct = default)
            => Task.FromResult(Result<AutomationEngineSettings>.Success(settings));
        public Task<Result<IReadOnlyList<AutomationRule>>> GetRemoteRulesAsync(CancellationToken ct = default)
            => Task.FromResult(Result<IReadOnlyList<AutomationRule>>.Success(Array.Empty<AutomationRule>()));
        public Task<Result<AutomationRule>> SaveRemoteRuleAsync(AutomationRule rule, CancellationToken ct = default)
            => Task.FromResult(Result<AutomationRule>.Success(rule));
        public Task<Result> DeleteRemoteRuleAsync(Guid ruleId, CancellationToken ct = default)
            => Task.FromResult(Result.Success());
        public Task<Result<IReadOnlyList<AutomationExecutionRecord>>> GetRemoteHistoryAsync(int limit = 100, CancellationToken ct = default)
            => Task.FromResult(Result<IReadOnlyList<AutomationExecutionRecord>>.Success(Array.Empty<AutomationExecutionRecord>()));
        public Task<Result<IReadOnlyList<AutomationExecutionRecord>>> DispatchRemoteTriggerAsync(AutomationTriggerDispatchRequest request, CancellationToken ct = default)
            => Task.FromResult(Result<IReadOnlyList<AutomationExecutionRecord>>.Success(Array.Empty<AutomationExecutionRecord>()));
    }

    private sealed class StubModulesClient : IIntegrationModulesClient
    {
        public Task<Result<IReadOnlyList<IntegrationModuleStatusSnapshot>>> GetRemoteModuleStatusesAsync(CancellationToken ct = default)
            => Task.FromResult(Result<IReadOnlyList<IntegrationModuleStatusSnapshot>>.Success(Array.Empty<IntegrationModuleStatusSnapshot>()));
        public Task<Result<IntegrationModuleStatusSnapshot>> SetRemoteModuleEnabledAsync(UpdateModuleToggleRequest request, CancellationToken ct = default)
            => Task.FromResult(Result<IntegrationModuleStatusSnapshot>.Failure(NexusError.Network("Stub offline")));
        public Task<Result<RemoteControlExecutionReceipt>> SendControlCommandAsync(RemoteControlCommandRequest request, CancellationToken ct = default)
            => Task.FromResult(Result<RemoteControlExecutionReceipt>.Success(new RemoteControlExecutionReceipt { CommandId = request.CommandId, Action = request.Action, Executed = true, Message = "OK" }));
        public Task<Result<ControlModuleSettings>> GetRemoteControlSettingsAsync(CancellationToken ct = default)
            => Task.FromResult(Result<ControlModuleSettings>.Success(new ControlModuleSettings()));
        public Task<Result<ControlModuleSettings>> SaveRemoteControlSettingsAsync(ControlModuleSettings settings, CancellationToken ct = default)
            => Task.FromResult(Result<ControlModuleSettings>.Success(settings));
        public Task<Result<PushMobileNotificationReceipt>> PushMobileNotificationAsync(PushMobileNotificationRequest request, CancellationToken ct = default)
            => Task.FromResult(Result<PushMobileNotificationReceipt>.Success(new PushMobileNotificationReceipt { NotificationId = request.NotificationId, Accepted = true }));
        public Task<Result<IReadOnlyList<MobileNotificationRecord>>> GetRemoteNotificationHistoryAsync(int limit = 100, CancellationToken ct = default)
            => Task.FromResult(Result<IReadOnlyList<MobileNotificationRecord>>.Success(Array.Empty<MobileNotificationRecord>()));
        public Task<Result> DismissRemoteNotificationAsync(Guid notificationId, CancellationToken ct = default)
            => Task.FromResult(Result.Success());
        public Task<Result<HostTelemetrySnapshot>> GetRemoteMonitorSnapshotAsync(CancellationToken ct = default)
            => Task.FromResult(Result<HostTelemetrySnapshot>.Failure(NexusError.Network("Offline")));
        public Task<Result<MediaPlaybackStateContract>> GetRemoteMediaStateAsync(CancellationToken ct = default)
            => Task.FromResult(Result<MediaPlaybackStateContract>.Failure(NexusError.Network("Offline")));
        public Task<Result<MediaPlaybackStateContract>> SendMediaCommandAsync(MediaControlCommandRequest request, CancellationToken ct = default)
            => Task.FromResult(Result<MediaPlaybackStateContract>.Success(new MediaPlaybackStateContract { IsAvailable = true, PlaybackStatus = MediaPlaybackStatus.Playing }));
    }

    private sealed class StubRouteOrchestrator : INexusRouteOrchestrator
    {
        public ConnectivityStatusReport CurrentStatus { get; } = new() { State = ConnectionState.Connected, DeviceName = "PC Test", RouteKind = ConnectionRouteKind.LocalLan, LatencyMs = 4 };
#pragma warning disable CS0067
        public event EventHandler<ConnectivityStatusReport>? StatusChanged;
#pragma warning restore CS0067
        public Task<Result<ConnectivityStatusReport>> InitializeAsync(CancellationToken ct = default) => Task.FromResult(Result<ConnectivityStatusReport>.Success(CurrentStatus));
        public Task<Result<DeviceEndpointRecord>> RegisterEndpointAsync(RegisterEndpointRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Result> RemoveEndpointAsync(Guid endpointId, CancellationToken ct = default) => Task.FromResult(Result.Success());
        public Task<Result<DeviceEndpointRecord>> SetEndpointEnabledAsync(Guid endpointId, bool enabled, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Result<IReadOnlyList<MdnsDiscoveredEndpoint>>> DiscoverLocalEndpointsAsync(TimeSpan? timeout = null, CancellationToken ct = default) => Task.FromResult(Result<IReadOnlyList<MdnsDiscoveredEndpoint>>.Success(Array.Empty<MdnsDiscoveredEndpoint>()));
        public Task<Result<ConnectivityStatusReport>> SelectAndConnectBestRouteAsync(SelectRouteRequest? request = null, CancellationToken ct = default) => Task.FromResult(Result<ConnectivityStatusReport>.Success(CurrentStatus));
        public Task<Result<IReadOnlyList<RouteProbeResult>>> ProbeAllEndpointsAsync(CancellationToken ct = default) => Task.FromResult(Result<IReadOnlyList<RouteProbeResult>>.Success(Array.Empty<RouteProbeResult>()));
        public Task<Result<ConnectivityStatusReport>> HandleNetworkChangedAsync(CancellationToken ct = default) => Task.FromResult(Result<ConnectivityStatusReport>.Success(CurrentStatus));
        public Task<Result<ConnectivityStatusReport>> DisconnectAsync(string? reason = null, CancellationToken ct = default) => Task.FromResult(Result<ConnectivityStatusReport>.Success(CurrentStatus));
        public void SetAutoReconnect(bool enabled) { }
        public Task<Result<IOperationLease>> BeginOperationScopeAsync(string operationName, CancellationToken ct = default) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class StubTransferManager : ITransferManager
    {
        public TransferQueueOptions Options { get; } = new();
        public IReadOnlyList<FileTransferRecord> ActiveTransfers { get; } = Array.Empty<FileTransferRecord>();
        public IReadOnlyList<FileTransferRecord> History { get; } = Array.Empty<FileTransferRecord>();
#pragma warning disable CS0067
        public event EventHandler<FileTransferRecord>? TransferUpdated;
#pragma warning restore CS0067
        public Task<Result> InitializeAsync(CancellationToken ct = default) => Task.FromResult(Result.Success());
        public Task<Result<IReadOnlyList<FileTransferRecord>>> EnqueueSendFilesAsync(Guid targetDeviceId, string targetDeviceName, IReadOnlyList<string> localFilePaths, TransferDuplicatePolicy? duplicatePolicy = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Result<FileTransferRecord>> EnqueueSendFileAsync(Guid targetDeviceId, string targetDeviceName, string localFilePath, TransferDuplicatePolicy? duplicatePolicy = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Result<FileTransferRecord>> EnqueueReceiveTransferAsync(FileTransferRecord remoteTransfer, bool startImmediately = true, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Result<FileTransferRecord>> PauseTransferAsync(Guid transferId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Result<FileTransferRecord>> ResumeTransferAsync(Guid transferId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Result<FileTransferRecord>> CancelTransferAsync(Guid transferId, string? reason = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Result<int>> ResumeInterruptedTransfersAsync(CancellationToken ct = default) => Task.FromResult(Result<int>.Success(0));
        public Task<Result<int>> SyncIncomingTransfersAsync(CancellationToken ct = default) => Task.FromResult(Result<int>.Success(0));
        public Task<Result> UpdateConfigurationAsync(TransferQueueOptions options, CancellationToken ct = default) => Task.FromResult(Result.Success());
        public Task<Result<IReadOnlyList<FileTransferRecord>>> GetAllTransfersAsync(CancellationToken ct = default) => Task.FromResult(Result<IReadOnlyList<FileTransferRecord>>.Success(Array.Empty<FileTransferRecord>()));
        public Task<Result<int>> ClearHistoryAsync(CancellationToken ct = default) => Task.FromResult(Result<int>.Success(0));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class StubNotesService : INotesSyncService
    {
#pragma warning disable CS0067
        public event EventHandler? NotesOrConflictsChanged;
#pragma warning restore CS0067
        public Task<Result<NexusNoteContract>> CreateNoteAsync(string title, string body, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Result<NexusNoteContract>> UpdateNoteAsync(Guid noteId, string title, string body, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Result<NexusNoteContract>> DeleteNoteAsync(Guid noteId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Result<IReadOnlyList<NexusNoteContract>>> ListNotesAsync(string? searchQuery = null, bool includeDeleted = false, CancellationToken ct = default)
            => Task.FromResult(Result<IReadOnlyList<NexusNoteContract>>.Success(Array.Empty<NexusNoteContract>()));
        public Task<Result<IReadOnlyList<NoteRevisionSnapshot>>> GetRevisionsAsync(Guid noteId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Result<NexusNoteContract>> RestoreRevisionAsync(Guid noteId, Guid revisionId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Result<IReadOnlyList<SyncConflictRecord>>> ListConflictsAsync(bool unresolvedOnly = true, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Result<NexusNoteContract>> ResolveConflictAsync(ResolveConflictRequest request, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class StubRealtimeChannel : IRealtimeClientChannel
    {
        public TransportKind Transport => TransportKind.WebSocket;
        public bool IsConnected => true;
        public ConnectionState State => ConnectionState.Connected;
        public RealtimeSessionSnapshot? CurrentSession => null;
        public IReadOnlyList<DevicePresenceSnapshot> LivePresence => Array.Empty<DevicePresenceSnapshot>();
        public IReadOnlyList<RealtimeMessageEnvelope> RecentMessages => Array.Empty<RealtimeMessageEnvelope>();
        public IReadOnlyList<RealtimeMessageAck> RecentAcknowledgements => Array.Empty<RealtimeMessageAck>();
        public string? LastError => null;
        public DateTimeOffset? LastHeartbeatUtc => null;

#pragma warning disable CS0067
        public event EventHandler<ConnectionStateSnapshot>? StateChanged;
        public event EventHandler<RealtimeMessageEnvelope>? MessageReceived;
        public event EventHandler<RealtimeMessageAck>? AckReceived;
        public event EventHandler<DeviceCommand>? CommandReceived;
        public event EventHandler<CommandCancellationRequest>? CommandCancelled;
        public event EventHandler<CommandExecutionResult>? CommandResultReceived;
        public event EventHandler<IReadOnlyList<DevicePresenceSnapshot>>? PresenceUpdated;
        public event EventHandler<ClipboardEntryContract>? ClipboardReceived;
        public event EventHandler<NexusNoteContract>? NoteChangeReceived;
        public event EventHandler<FileTransferProgressEvent>? TransferProgressReceived;
        public event EventHandler<FileTransferRecord>? TransferAvailableReceived;
        public event EventHandler<MobileNotificationRecord>? MobileNotificationReceived;
        public event EventHandler<MediaPlaybackStateContract>? MediaStateReceived;
        public event EventHandler<IntegrationModuleStatusSnapshot>? ModuleStatusChangedReceived;
        public event EventHandler<PanelProfile>? PanelProfileUpdatedReceived;
        public event EventHandler<AutomationExecutionRecord>? AutomationExecutionReceived;
#pragma warning restore CS0067

        public Task<Result> OpenAsync(string endpoint, CancellationToken ct = default) => Task.FromResult(Result.Success());
        public Task<Result> CloseAsync(CancellationToken ct = default) => Task.FromResult(Result.Success());
        public Task<Result<NexusResponse<TResponse>>> SendAsync<TRequest, TResponse>(NexusRequest<TRequest> request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Result> PublishEventAsync(IntegrationEvent integrationEvent, CancellationToken ct = default) => Task.FromResult(Result.Success());
        public Task<Result<int>> SendHeartbeatAsync(CancellationToken ct = default) => Task.FromResult(Result<int>.Success(0));
        public Task<Result<RealtimeSessionSnapshot>> OpenAuthenticatedAsync(string? endpointOverride = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Result<RealtimeMessageAck>> SendEnvelopeAsync(RealtimeMessageEnvelope envelope, bool waitForAck = true, TimeSpan? ackTimeout = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Result<RealtimeMessageAck>> SendCommandAsync(DeviceCommand command, bool waitForAck = true, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Result> CancelCommandAsync(Guid commandId, Guid targetDeviceId, string? reason = null, CancellationToken ct = default) => Task.FromResult(Result.Success());
        public Task<Result> SendCommandResultAsync(CommandExecutionResult result, Guid? targetDeviceId = null, Guid? correlationId = null, CancellationToken ct = default) => Task.FromResult(Result.Success());
        public Task<Result> AcknowledgeAsync(RealtimeMessageAck ack, CancellationToken ct = default) => Task.FromResult(Result.Success());
        public Task<Result<RealtimeMessageAck>> PublishIntegrationEventAsync(IntegrationEvent integrationEvent, Guid? targetDeviceId = null, bool ordered = true, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Result<IReadOnlyList<DevicePresenceSnapshot>>> RefreshPresenceAsync(CancellationToken ct = default) => Task.FromResult(Result<IReadOnlyList<DevicePresenceSnapshot>>.Success(Array.Empty<DevicePresenceSnapshot>()));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
