using Nexus.Contracts.Automation;
using Nexus.Contracts.Security;
using Nexus.Core.Errors;

namespace Nexus.Core.Automation;

public interface IDeviceBatteryProvider
{
    Task<BatteryStateSnapshot> ReadBatteryStateAsync(CancellationToken cancellationToken = default);
}

public interface IApprovedAppLauncher
{
    bool IsSupportedOnCurrentOs { get; }
    Task<Result<string>> LaunchApprovedAppAsync(
        ApprovedAppShortcut shortcut,
        CancellationToken cancellationToken = default);
}

public interface IPanelProfileRepository
{
    Task<Result<IReadOnlyList<PanelProfile>>> ListAsync(CancellationToken cancellationToken = default);
    Task<Result<PanelProfile?>> GetByIdAsync(Guid profileId, CancellationToken cancellationToken = default);
    Task<Result<PanelProfile?>> GetActiveAsync(CancellationToken cancellationToken = default);
    Task<Result> UpsertAsync(PanelProfile profile, CancellationToken cancellationToken = default);
    Task<Result> DeleteAsync(Guid profileId, CancellationToken cancellationToken = default);
}

public interface IAutomationRuleRepository
{
    Task<Result<IReadOnlyList<AutomationRule>>> ListAsync(CancellationToken cancellationToken = default);
    Task<Result<AutomationRule?>> GetByIdAsync(Guid ruleId, CancellationToken cancellationToken = default);
    Task<Result> UpsertAsync(AutomationRule rule, CancellationToken cancellationToken = default);
    Task<Result> DeleteAsync(Guid ruleId, CancellationToken cancellationToken = default);
}

public interface IAutomationExecutionRepository
{
    Task<Result> AppendAsync(AutomationExecutionRecord record, CancellationToken cancellationToken = default);
    Task<Result<IReadOnlyList<AutomationExecutionRecord>>> ListRecentAsync(int limit = 100, CancellationToken cancellationToken = default);
    Task<Result<int>> ClearAsync(CancellationToken cancellationToken = default);
}

public interface IPanelLayoutService
{
    event EventHandler<PanelProfile>? ActiveProfileChanged;

    Task<Result<IReadOnlyList<PanelProfile>>> GetProfilesAsync(CancellationToken cancellationToken = default);
    Task<Result<PanelProfile>> GetActiveProfileAsync(CancellationToken cancellationToken = default);
    Task<Result<PanelProfile>> SaveProfileAsync(PanelProfile profile, CancellationToken cancellationToken = default);
    Task<Result<PanelProfile>> CreateProfileAsync(string name, PanelThemePreset theme = PanelThemePreset.MidnightNavy, PanelAccentColor accent = PanelAccentColor.Cyan, CancellationToken cancellationToken = default);
    Task<Result<PanelProfile>> ActivateProfileAsync(Guid profileId, CancellationToken cancellationToken = default);
    Task<Result> DeleteProfileAsync(Guid profileId, CancellationToken cancellationToken = default);

    Task<Result<PanelProfile>> AddWidgetAsync(PanelWidgetType widgetType, string? customTitle = null, PanelWidgetSize size = PanelWidgetSize.Compact1x1, CancellationToken cancellationToken = default);
    Task<Result<PanelProfile>> RemoveWidgetAsync(Guid widgetId, CancellationToken cancellationToken = default);
    Task<Result<PanelProfile>> ResizeWidgetAsync(Guid widgetId, PanelWidgetSize newSize, CancellationToken cancellationToken = default);
    Task<Result<PanelProfile>> MoveWidgetAsync(Guid widgetId, int targetIndex, CancellationToken cancellationToken = default);
    Task<Result<PanelProfile>> UpdateAppearanceAsync(PanelThemePreset theme, PanelAccentColor accent, int gridColumns, CancellationToken cancellationToken = default);
}

public interface IAutomationEngine
{
    event EventHandler<AutomationExecutionRecord>? ExecutionLogged;
    event EventHandler<PanelRuntimeIndicator>? IndicatorEmitted;
    event EventHandler<AutomationEngineSettings>? SettingsChanged;

    IReadOnlyList<PanelRuntimeIndicator> ActiveIndicators { get; }

    Task<Result<AutomationEngineSettings>> GetSettingsAsync(CancellationToken cancellationToken = default);
    Task<Result<AutomationEngineSettings>> SaveSettingsAsync(AutomationEngineSettings settings, CancellationToken cancellationToken = default);
    Task<Result<AutomationEngineSettings>> SetEmergencyKillSwitchAsync(bool active, CancellationToken cancellationToken = default);

    Task<Result<IReadOnlyList<AutomationRule>>> GetRulesAsync(CancellationToken cancellationToken = default);
    Task<Result<AutomationRule>> SaveRuleAsync(AutomationRule rule, CancellationToken cancellationToken = default);
    Task<Result<AutomationRule>> SetRuleEnabledAsync(Guid ruleId, bool enabled, CancellationToken cancellationToken = default);
    Task<Result> DeleteRuleAsync(Guid ruleId, CancellationToken cancellationToken = default);
    Task<Result<IReadOnlyList<AutomationExecutionRecord>>> GetExecutionHistoryAsync(int limit = 100, CancellationToken cancellationToken = default);
    Task<Result<int>> ClearExecutionHistoryAsync(CancellationToken cancellationToken = default);

    Task<Result<IReadOnlyList<AutomationExecutionRecord>>> EvaluateTriggerAsync(
        AutomationTriggerEvent triggerEvent,
        NexusPermission callerPermissions = NexusPermission.All,
        CancellationToken cancellationToken = default);

    Task<Result<AutomationExecutionRecord>> TriggerRuleManuallyAsync(
        Guid ruleId,
        NexusPermission callerPermissions = NexusPermission.All,
        bool approvedSensitiveExecution = false,
        CancellationToken cancellationToken = default);
}

public interface IPanelAndAutomationClient
{
    Task<Result<IReadOnlyList<PanelProfile>>> GetRemoteProfilesAsync(CancellationToken cancellationToken = default);
    Task<Result<PanelProfile>> SaveRemoteProfileAsync(PanelProfile profile, CancellationToken cancellationToken = default);
    Task<Result<PanelProfile>> ActivateRemoteProfileAsync(Guid profileId, CancellationToken cancellationToken = default);

    Task<Result<AutomationEngineSettings>> GetRemoteEngineSettingsAsync(CancellationToken cancellationToken = default);
    Task<Result<AutomationEngineSettings>> SaveRemoteEngineSettingsAsync(AutomationEngineSettings settings, CancellationToken cancellationToken = default);
    Task<Result<IReadOnlyList<AutomationRule>>> GetRemoteRulesAsync(CancellationToken cancellationToken = default);
    Task<Result<AutomationRule>> SaveRemoteRuleAsync(AutomationRule rule, CancellationToken cancellationToken = default);
    Task<Result> DeleteRemoteRuleAsync(Guid ruleId, CancellationToken cancellationToken = default);
    Task<Result<IReadOnlyList<AutomationExecutionRecord>>> GetRemoteHistoryAsync(int limit = 100, CancellationToken cancellationToken = default);
    Task<Result<IReadOnlyList<AutomationExecutionRecord>>> DispatchRemoteTriggerAsync(AutomationTriggerDispatchRequest request, CancellationToken cancellationToken = default);
}

