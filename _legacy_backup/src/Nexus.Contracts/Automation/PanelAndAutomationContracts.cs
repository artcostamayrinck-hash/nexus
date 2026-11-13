using System.ComponentModel.DataAnnotations;
using Nexus.Contracts.Common;
using Nexus.Contracts.Modules;
using Nexus.Contracts.Security;

namespace Nexus.Contracts.Automation;

public enum PanelWidgetType
{
    ConnectionStatus = 0,
    ComputerInfo = 1,
    CpuAndRam = 2,
    VolumeControl = 3,
    MediaControls = 4,
    AppShortcuts = 5,
    QuickActions = 6,
    TransferStatus = 7,
    RecentNotes = 8,
    NetworkInfo = 9
}

public enum PanelWidgetSize
{
    Compact1x1 = 0,
    Wide2x1 = 1,
    Tall1x2 = 2,
    Large2x2 = 3
}

public enum PanelThemePreset
{
    MidnightNavy = 0,
    OledBlack = 1,
    SlateGlass = 2,
    HighContrast = 3
}

public enum PanelAccentColor
{
    Cyan = 0,
    ElectricBlue = 1,
    Emerald = 2,
    Amber = 3,
    Rose = 4,
    Violet = 5
}

public sealed record PanelWidgetItem : IValidatableContract
{
    public Guid WidgetId { get; init; } = Guid.NewGuid();
    public PanelWidgetType WidgetType { get; init; }

    [Required]
    [StringLength(64, MinimumLength = 1)]
    public string Title { get; init; } = string.Empty;

    public PanelWidgetSize Size { get; init; } = PanelWidgetSize.Compact1x1;
    public int SortOrder { get; init; }
    public int GridColumn { get; init; }
    public int GridRow { get; init; }
    public bool IsVisible { get; init; } = true;
    public string? ActionTargetId { get; init; }
    public string? CustomSubtitle { get; init; }

    public int ColumnSpan => Size is PanelWidgetSize.Wide2x1 or PanelWidgetSize.Large2x2 ? 2 : 1;
    public int RowSpan => Size is PanelWidgetSize.Tall1x2 or PanelWidgetSize.Large2x2 ? 2 : 1;

    public IEnumerable<ContractValidationError> Validate()
    {
        if (WidgetId == Guid.Empty)
        {
            yield return new ContractValidationError(nameof(WidgetId), "PANEL_WIDGET_ID_EMPTY", "WidgetId cannot be empty.");
        }

        if (string.IsNullOrWhiteSpace(Title))
        {
            yield return new ContractValidationError(nameof(Title), "PANEL_WIDGET_TITLE_REQUIRED", "Widget Title is required.");
        }

        if (GridColumn < 0 || GridRow < 0)
        {
            yield return new ContractValidationError(nameof(GridColumn), "PANEL_WIDGET_COORD_INVALID", "Grid coordinates cannot be negative.");
        }
    }
}

public sealed record PanelProfile : IVersionedContract, IValidatableContract
{
    public Guid ProfileId { get; init; } = Guid.NewGuid();

    [Required]
    [StringLength(80, MinimumLength = 1)]
    public string Name { get; init; } = "Painel Padrão";

    public PanelThemePreset Theme { get; init; } = PanelThemePreset.MidnightNavy;
    public PanelAccentColor Accent { get; init; } = PanelAccentColor.Cyan;
    public int GridColumns { get; init; } = 2;
    public bool IsActive { get; init; }
    public IReadOnlyList<PanelWidgetItem> Widgets { get; init; } = Array.Empty<PanelWidgetItem>();
    public DateTimeOffset UpdatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public long Revision { get; init; } = 1;
    public string ContractVersion { get; init; } = ContractVersions.Current;

    public string AccentHex => Accent switch
    {
        PanelAccentColor.ElectricBlue => "#3B82F6",
        PanelAccentColor.Emerald => "#10B981",
        PanelAccentColor.Amber => "#F59E0B",
        PanelAccentColor.Rose => "#F43F5E",
        PanelAccentColor.Violet => "#8B5CF6",
        _ => "#22D3EE"
    };

    public string BackgroundHex => Theme switch
    {
        PanelThemePreset.OledBlack => "#000000",
        PanelThemePreset.SlateGlass => "#0F172A",
        PanelThemePreset.HighContrast => "#05070B",
        _ => "#07101F"
    };

    public IEnumerable<ContractValidationError> Validate()
    {
        if (ProfileId == Guid.Empty)
        {
            yield return new ContractValidationError(nameof(ProfileId), "PANEL_PROFILE_ID_EMPTY", "ProfileId cannot be empty.");
        }

        if (string.IsNullOrWhiteSpace(Name))
        {
            yield return new ContractValidationError(nameof(Name), "PANEL_PROFILE_NAME_REQUIRED", "Profile Name is required.");
        }

        if (GridColumns < 1 || GridColumns > 4)
        {
            yield return new ContractValidationError(nameof(GridColumns), "PANEL_GRID_COLS_INVALID", "GridColumns must be between 1 and 4.");
        }

        foreach (var widget in Widgets)
        {
            foreach (var err in widget.Validate())
            {
                yield return err;
            }
        }
    }
}

public sealed record ApprovedAppShortcut : IValidatableContract
{
    [Required]
    [StringLength(64, MinimumLength = 1)]
    public string ShortcutId { get; init; } = string.Empty;

    [Required]
    [StringLength(80, MinimumLength = 1)]
    public string DisplayName { get; init; } = string.Empty;

    [Required]
    [StringLength(500, MinimumLength = 1)]
    public string ExecutablePath { get; init; } = string.Empty;

    public string? Arguments { get; init; }
    public bool RequireConfirmation { get; init; }

    public IEnumerable<ContractValidationError> Validate()
    {
        if (string.IsNullOrWhiteSpace(ShortcutId))
        {
            yield return new ContractValidationError(nameof(ShortcutId), "SHORTCUT_ID_REQUIRED", "ShortcutId is required.");
        }

        if (string.IsNullOrWhiteSpace(DisplayName))
        {
            yield return new ContractValidationError(nameof(DisplayName), "SHORTCUT_NAME_REQUIRED", "DisplayName is required.");
        }

        if (string.IsNullOrWhiteSpace(ExecutablePath))
        {
            yield return new ContractValidationError(nameof(ExecutablePath), "SHORTCUT_PATH_REQUIRED", "ExecutablePath is required.");
        }
    }
}

public enum AutomationTriggerType
{
    Manual = 0,
    ConnectedToComputer = 1,
    DisconnectedFromComputer = 2,
    SessionStarted = 3,
    TransferStarted = 4,
    TransferCompleted = 5,
    BatteryLevelThreshold = 6,
    NetworkRouteChanged = 7
}

public enum AutomationConditionOperator
{
    RequireConnected = 0,
    RequireDisconnected = 1,
    RouteKindEquals = 2,
    BatteryAtOrBelow = 3,
    BatteryAtOrAbove = 4,
    IsChargingEquals = 5,
    TimeBetweenHours = 6,
    DeviceIdEquals = 7
}

public enum AutomationLogicMode
{
    All = 0,
    Any = 1
}

public sealed record AutomationCondition
{
    public AutomationConditionOperator Operator { get; init; }
    public string ExpectedValue { get; init; } = string.Empty;
    public string? SecondaryValue { get; init; }
}

public enum AutomationActionType
{
    ActivatePanelProfile = 0,
    ShowPanelIndicator = 1,
    SendNotification = 2,
    ExecuteMediaCommand = 3,
    ExecuteControlCommand = 4,
    LaunchApprovedApp = 5,
    PrepareFavoriteControls = 6
}

public sealed record AutomationAction : IValidatableContract
{
    public Guid ActionId { get; init; } = Guid.NewGuid();
    public AutomationActionType ActionType { get; init; }
    public string Label { get; init; } = string.Empty;
    public Guid? TargetProfileId { get; init; }
    public string? Title { get; init; }
    public string? Message { get; init; }
    public MediaControlAction? MediaAction { get; init; }
    public int? VolumePercent { get; init; }
    public RemoteControlActionType? ControlAction { get; init; }
    public string? HotkeyId { get; init; }
    public string? ShortcutId { get; init; }
    public bool IsSensitiveAction { get; init; }

    public IEnumerable<ContractValidationError> Validate()
    {
        if (ActionType == AutomationActionType.ActivatePanelProfile && (!TargetProfileId.HasValue || TargetProfileId.Value == Guid.Empty))
        {
            yield return new ContractValidationError(nameof(TargetProfileId), "AUTO_ACTION_PROFILE_REQUIRED", "TargetProfileId is required to activate a profile.");
        }

        if (ActionType == AutomationActionType.LaunchApprovedApp && string.IsNullOrWhiteSpace(ShortcutId))
        {
            yield return new ContractValidationError(nameof(ShortcutId), "AUTO_ACTION_SHORTCUT_REQUIRED", "ShortcutId is required to launch an approved app.");
        }
    }
}

public sealed record AutomationRule : IVersionedContract, IValidatableContract
{
    public Guid RuleId { get; init; } = Guid.NewGuid();

    [Required]
    [StringLength(100, MinimumLength = 1)]
    public string Name { get; init; } = string.Empty;

    [StringLength(400)]
    public string Description { get; init; } = string.Empty;

    public bool IsEnabled { get; init; } = true;
    public AutomationTriggerType Trigger { get; init; }
    public int? BatteryThresholdPercent { get; init; }
    public string? ExpectedRouteFilter { get; init; }
    public AutomationLogicMode ConditionLogic { get; init; } = AutomationLogicMode.All;
    public IReadOnlyList<AutomationCondition> Conditions { get; init; } = Array.Empty<AutomationCondition>();
    public IReadOnlyList<AutomationAction> Actions { get; init; } = Array.Empty<AutomationAction>();

    public int CooldownSeconds { get; init; } = 10;
    public int MaxExecutionsPerMinute { get; init; } = 6;
    public bool RequireSensitiveAuthorization { get; init; } = true;
    public bool IsSensitiveAuthorized { get; init; }

    public DateTimeOffset? LastExecutedAtUtc { get; init; }
    public DateTimeOffset CreatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public string ContractVersion { get; init; } = ContractVersions.Current;

    public bool ContainsSensitiveActions
        => Actions.Any(a =>
            a.IsSensitiveAction ||
            a.ActionType == AutomationActionType.LaunchApprovedApp ||
            (a.ActionType == AutomationActionType.ExecuteControlCommand &&
             a.ControlAction.HasValue &&
             RemoteControlCommandRequest.IsHighImpactAction(a.ControlAction.Value)));

    public IEnumerable<ContractValidationError> Validate()
    {
        if (RuleId == Guid.Empty)
        {
            yield return new ContractValidationError(nameof(RuleId), "AUTO_RULE_ID_EMPTY", "RuleId cannot be empty.");
        }

        if (string.IsNullOrWhiteSpace(Name))
        {
            yield return new ContractValidationError(nameof(Name), "AUTO_RULE_NAME_REQUIRED", "Rule Name is required.");
        }

        if (Actions.Count == 0)
        {
            yield return new ContractValidationError(nameof(Actions), "AUTO_RULE_ACTIONS_EMPTY", "Rule must define at least one action.");
        }

        if (CooldownSeconds < 0 || CooldownSeconds > 86_400)
        {
            yield return new ContractValidationError(nameof(CooldownSeconds), "AUTO_COOLDOWN_INVALID", "CooldownSeconds must be between 0 and 86400.");
        }

        if (MaxExecutionsPerMinute < 1 || MaxExecutionsPerMinute > 120)
        {
            yield return new ContractValidationError(nameof(MaxExecutionsPerMinute), "AUTO_RATE_LIMIT_INVALID", "MaxExecutionsPerMinute must be between 1 and 120.");
        }

        foreach (var action in Actions)
        {
            foreach (var err in action.Validate())
            {
                yield return err;
            }
        }
    }
}

public enum AutomationExecutionOutcome
{
    Succeeded = 0,
    ConditionsNotMet = 1,
    SkippedDisabled = 2,
    SkippedCooldown = 3,
    SkippedRateLimit = 4,
    SkippedLoopGuard = 5,
    BlockedPermission = 6,
    BlockedAuthorization = 7,
    Failed = 8
}

public sealed record AutomationExecutionRecord : IVersionedContract
{
    public Guid ExecutionId { get; init; } = Guid.NewGuid();
    public Guid RuleId { get; init; }
    public string RuleName { get; init; } = string.Empty;
    public AutomationTriggerType Trigger { get; init; }
    public AutomationExecutionOutcome Outcome { get; init; }
    public string Detail { get; init; } = string.Empty;
    public Guid CorrelationId { get; init; } = Guid.NewGuid();
    public long DurationMs { get; init; }
    public DateTimeOffset ExecutedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public string ContractVersion { get; init; } = ContractVersions.Current;
}

public sealed record AutomationTriggerEvent
{
    public Guid CorrelationId { get; init; } = Guid.NewGuid();
    public AutomationTriggerType Trigger { get; init; }
    public Guid? SourceDeviceId { get; init; }
    public bool IsConnected { get; init; }
    public string? ActiveRouteKind { get; init; }
    public BatteryStateSnapshot? Battery { get; init; }
    public string? TransferFileName { get; init; }
    public string? Detail { get; init; }
    public DateTimeOffset OccurredAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public int ChainDepth { get; init; }
    public IReadOnlyList<Guid> VisitedRuleIds { get; init; } = Array.Empty<Guid>();
}

public sealed record BatteryStateSnapshot
{
    public bool IsBatteryPresent { get; init; }
    public bool IsAvailable { get; init; }
    public int? ChargePercent { get; init; }
    public bool? IsCharging { get; init; }
    public string StatusMessage { get; init; } = string.Empty;
}

public sealed record PanelRuntimeIndicator(
    Guid IndicatorId,
    string Title,
    string Message,
    DateTimeOffset CreatedAtUtc,
    bool IsAlert = false);

public sealed record AutomationEngineSettings
{
    public bool GlobalAutomationsEnabled { get; init; } = true;
    public bool EmergencyKillSwitchActive { get; init; }
    public int MaxChainDepth { get; init; } = 4;
    public string AndroidBackgroundLimitationNote { get; init; } =
        "No Android, automações baseadas em sessão/conexão operam enquanto o NEXUS está em primeiro plano ou com conexão ativa. Restrições de Doze e economia de bateria do Android podem atrasar ou suspender execuções em segundo plano.";
    public IReadOnlyList<ApprovedAppShortcut> ApprovedApps { get; init; } = Array.Empty<ApprovedAppShortcut>();
    public DateTimeOffset UpdatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record AutomationTriggerDispatchRequest
{
    public Guid? RuleId { get; init; }
    public AutomationTriggerType Trigger { get; init; } = AutomationTriggerType.Manual;
    public string? Detail { get; init; }
    public bool ApprovedSensitiveExecution { get; init; }
}

