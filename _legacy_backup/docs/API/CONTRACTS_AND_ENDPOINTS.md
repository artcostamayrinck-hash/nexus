# Contratos Compartilhados e Endpoints (v1.0)

## Contratos (`Nexus.Contracts`)
Todos os contratos críticos implementam `IVersionedContract` (`ContractVersion = "1.0"`) e são validados via `ContractValidator.Validate(...)` combinando `DataAnnotations` e invariantes de domínio (`IValidatableContract`):

| Domínio | Principais Contratos |
|---|---|
| Dispositivos | `DeviceInfo`, `DeviceRegistrationContract`, `DevicePlatform`, `DeviceType`, `DeviceStatus`, `DeviceCapability` |
| Segurança & Confiança | `NexusPermission`, `DeviceTrustStatus`, `DeviceCryptographicIdentity`, `TrustedDeviceRecord`, `DeviceAuthTokenPair`, `RefreshTokenRequest`, `RenameDeviceRequest`, `UpdateDevicePermissionsRequest`, `RevokeDeviceRequest` |
| Pareamento QR | `PairingSessionState`, `PairingQrPayload`, `CreatePairingRequest`, `SubmitPairingClaimRequest`, `ConfirmPairingRequest`, `FinalizePairingTokenRequest`, `PairingSessionSnapshot` |
| Identidade & Sessões | `UserProfile`, `SessionInfo`, `SessionStatus` |
| Mensageria & Tempo Real | `NexusRequest<TPayload>`, `NexusResponse<TPayload>`, `ContractError`, `RealtimeMessageKind`, `RealtimeAckStatus`, `RealtimeMessageEnvelope`, `RealtimeMessageAck`, `CommandCancellationRequest`, `DevicePresenceSnapshot`, `RealtimeSessionSnapshot`, `BulkPayloadTransferReceipt` |
| Comandos | `DeviceCommand`, `CommandExecutionResult`, `CommandPriority`, `CommandExecutionState` |
| Eventos | `IntegrationEvent`, `EventSeverity`, `NexusEventTypes` |
| Transferências | `FileTransferMetadata`, `TransferDirection`, `TransferState` |
| Notificações | `DeviceNotification`, `NotificationAction`, `NotificationUrgency` |
| Conectividade | `ConnectionStateSnapshot`, `ConnectionState`, `TransportKind`, `ConnectionRouteKind`, `VpnStatus`, `RouteFailureReason`, `DeviceEndpointRecord`, `MdnsDiscoveredEndpoint`, `NetworkEnvironmentSnapshot`, `RouteProbeResult`, `ConnectivityStatusReport`, `RegisterEndpointRequest`, `SelectRouteRequest`, `ServerConnectivityAddressesDto` |
| Sincronização | `SyncBatchContract`, `SyncItemMetadata`, `SyncDirection`, `SyncStatus`, `ConflictResolutionPolicy`, `SyncOperationKind`, `SyncQueueStatus`, `ConflictResolutionChoice`, `SyncPrivacyPreferences`, `ClipboardEntryContract`, `NexusNoteContract`, `NoteRevisionSnapshot`, `SyncConflictRecord`, `SyncQueueItemRecord`, `NoteSyncRequest`, `NoteSyncResponse`, `ResolveConflictRequest`, `SyncOverviewSnapshot` |

## Fluxo de Pareamento QR em 7 Etapas
1. **Solicitação no Windows**: Host envia `CreatePairingRequest` assinado por ECDSA P-256 para `POST /api/v1/pairing/sessions`.
2. **QR Temporário**: Servidor cria sessão (`AwaitingScan`), nonce/segredo de uso único e `PairingQrPayload` assinado (com endpoint TLS e `TlsCertThumbprint`).
3. **Leitura no Android**: Android valida estrutura, validade temporal (`ExpiresAtUtc`) e assinatura criptográfica do QR.
4. **Reivindicação & Validação**: Android envia `SubmitPairingClaimRequest` assinado para `POST /api/v1/pairing/claim`; ambos computam o código de verificação mútua de 6 dígitos (SAS).
5. **Confirmação do Usuário**: Windows exibe dispositivo, impressão digital, código SAS e permissões; usuário aprova via `POST /api/v1/pairing/sessions/{pairingId}/confirm`.
6. **Estabelecimento de Identidades**: Servidor registra `TrustedDeviceRecord` e emite par `AccessToken` + `RefreshToken`.
7. **Reconhecimento Mútuo**: Android coleta tokens uma única vez em `POST /api/v1/pairing/sessions/{pairingId}/finalize` e grava a identidade confiável do host.

## Endpoints ASP.NET Core (`Nexus.Server`)
- `GET /health/live` & `GET /health/ready` — Sondas de vivacidade e prontidão SQLite/Runtime.
- `GET /api/v1/system/info` — Retorna `SystemInfoDto` com `TlsThumbprint` e `HostKeyFingerprint`.
- `GET /api/v1/system/health` — Relatório de saúde interno.
- `POST /api/v1/pairing/sessions` · `GET /api/v1/pairing/sessions/{pairingId}` · `POST /api/v1/pairing/claim` · `POST /api/v1/pairing/sessions/{pairingId}/confirm` · `POST /api/v1/pairing/sessions/{pairingId}/finalize`.
- `POST /api/v1/auth/refresh` — Rotaciona `RefreshToken` e revoga família em caso de reutilização.
- `GET /api/v1/trusted-devices` & `GET /api/v1/trusted-devices/{deviceId}` *(requer `NexusBearer` + `ViewStatus`)*.
- `PATCH /api/v1/trusted-devices/{deviceId}/name` · `PATCH /api/v1/trusted-devices/{deviceId}/permissions` · `POST /api/v1/trusted-devices/{deviceId}/revoke` · `DELETE /api/v1/trusted-devices/{deviceId}` *(requer `NexusBearer` + `ManageDevices`)*.
- `GET /api/v1/devices` · `POST /api/v1/devices` · `GET /api/v1/connections/state` *(requer `NexusBearer`)*.
- `GET /api/v1/connectivity/addresses` · `GET /api/v1/connectivity/status` · `GET /api/v1/connectivity/endpoints` · `POST /api/v1/connectivity/probe` *(requer `NexusBearer` + `ViewStatus`)*.
- `POST /api/v1/connectivity/endpoints` · `DELETE /api/v1/connectivity/endpoints/{endpointId}` · `POST /api/v1/connectivity/select-route` *(requer `NexusBearer` + `ManageDevices`)*.
- `GET /api/v1/realtime/presence` · `GET /api/v1/realtime/sessions` · `POST /api/v1/realtime/transfers/payload` *(upload HTTP bulk até 50 MB com recibo SHA-256; requer `NexusBearer` + `ViewStatus`)*.
- `POST /api/v1/realtime/commands` · `POST /api/v1/realtime/sessions/{connectionId}/disconnect` *(requer `NexusBearer` + `ManageDevices`)*.
- `GET /api/v1/sync/clipboard` & `POST /api/v1/sync/clipboard` *(requer `NexusBearer` + `NexusPermission.Clipboard`; retorna `403 PERMISSION_DENIED_CLIPBOARD` sem a flag)*.
- `GET /api/v1/sync/notes` · `POST /api/v1/sync/notes/sync` · `GET /api/v1/sync/notes/{noteId}/revisions` · `POST /api/v1/sync/notes/{noteId}/restore/{revisionId}` · `GET /api/v1/sync/conflicts` · `POST /api/v1/sync/conflicts/{conflictId}/resolve` *(requer `NexusBearer` + `NexusPermission.Notes`; retorna `403 PERMISSION_DENIED_NOTES` sem a flag)*.
- `SignalR /hubs/nexus` *(requer autenticação `[Authorize]`)*:
  - **Hub Server Methods**: `AnnouncePresenceAsync`, `ResetSessionStreamAsync`, `PingAsync`, `GetPresenceSnapshotAsync`, `SendRealtimeMessageAsync`, `SendCommandAsync`, `CancelCommandAsync`, `SendCommandResultAsync`, `AcknowledgeMessageAsync`.
  - **Hub Client Events**: `ReceiveRealtimeMessage`, `ReceiveMessageAck`, `ReceiveDeviceCommand`, `ReceiveCommandCancellation`, `ReceiveCommandResult`, `ReceivePresenceSnapshot`, `ReceiveSessionNotice`, `ReceiveConnectionSnapshot`, `ReceiveIntegrationEvent`, `ReceivePairingSnapshot`, `ReceiveRevocationNotice`, `ReceivePong`, `ReceiveClipboardEntry`, `ReceiveNoteUpdated`.

Cabeçalhos opcionais de anti-repetição assinada por requisição (`X-Nexus-Timestamp`, `X-Nexus-Nonce`, `X-Nexus-Signature`) são verificados por `NexusDeviceAuthenticationHandler`, que também atualiza `LastAccessAtUtc` e bloqueia imediatamente dispositivos com status `Revoked`.

## Endpoints e Contratos — NEXUS Transfer (Fase 07)
Todos os endpoints abaixo exigem token Bearer `Authorized` com permissão `NexusPermission.FileTransfer` (`1 << 3`), validada tanto para o remetente quanto para o destinatário:
- `POST /api/v1/transfers/initiate` (`InitiateFileTransferRequest` -> `InitiateFileTransferResponse`): sanitiza nome do arquivo, verifica espaço disponível em staging (retorna `507 Insufficient Storage` se insuficiente) e devolve `AcceptedOffsetBytes` para retomada idempotente de upload.
- `PUT /api/v1/transfers/{transferId}/chunks?offset={offset}` (`ChunkUploadResponse`): stream HTTP gravado incrementalmente em `.part` no disco sem buffering em memória.
- `POST /api/v1/transfers/{transferId}/finalize-upload` (`FileTransferRecord`): valida completude e hash SHA-256 antes de liberar para download e notificar o destinatário via `ReceiveTransferAvailable` no `NexusHub`.
- `GET /api/v1/transfers/{transferId}/download`: streaming HTTP com `Accept-Ranges: bytes` (`200 OK` ou `206 Partial Content`) exclusivo para o `TargetDeviceId`.
- `POST /api/v1/transfers/{transferId}/acknowledge` (`AcknowledgeFileTransferRequest`): confirma recebimento verificado, notifica o remetente via SignalR e limpa staging temporário no servidor.
- `POST /api/v1/transfers/{transferId}/pause` · `POST /api/v1/transfers/{transferId}/cancel` · `GET /api/v1/transfers/{transferId}` · `GET /api/v1/transfers/incoming`.

## Endpoints e Contratos — Módulos de Integração (Fase 08)
- **Permissões Granulares Independentes (`NexusPermission`)**: `ControlInput` (`1 << 7`), `SystemPower` (`1 << 8`), `HardwareMonitor` (`1 << 9`), `MediaControl` (`1 << 10`), `Notifications` (`1 << 1`).
- `GET /api/v1/modules/status` *(requer `ViewStatus`)* e `POST /api/v1/modules/toggle` *(requer `ManageDevices`, propaga `ReceiveModuleStatusChanged`)*.
- **Nexus Control**:
  - `GET /api/v1/modules/control/settings` *(`ControlInput`)* · `PUT /api/v1/modules/control/settings` *(`ManageDevices`)*
  - `POST /api/v1/modules/control/execute` (`RemoteControlCommandRequest` -> `RemoteControlExecutionReceipt`; requer `ControlInput`, ou `SystemPower` para `PowerLock`/`PowerSleep`/`PowerShutdown`, além de `ConfirmedByUser = true` quando `RequirePowerActionConfirmation` está ativo).
- **Nexus Notify** *(requer `Notifications`)*:
  - `GET/PUT /api/v1/modules/notify/settings` · `POST /api/v1/modules/notify/push` (`PushMobileNotificationRequest` -> `PushMobileNotificationReceipt`, retransmite `ReceiveMobileNotification`) · `GET /api/v1/modules/notify/history` · `POST /api/v1/modules/notify/{notificationId}/dismiss` · `POST /api/v1/modules/notify/clear`.
- **Nexus Monitor** *(requer `HardwareMonitor`)*:
  - `GET/PUT /api/v1/modules/monitor/settings` · `GET /api/v1/modules/monitor/snapshot` (`HostTelemetrySnapshot` com `SensorReading<T>` indicando disponibilidade real de cada sensor).
- **Nexus Media** *(requer `MediaControl`)*:
  - `GET/PUT /api/v1/modules/media/settings` · `GET /api/v1/modules/media/state` (`MediaPlaybackStateContract`) · `POST /api/v1/modules/media/command` (`MediaControlCommandRequest`, propaga `ReceiveMediaState`).

## Endpoints e Contratos — Nexus Panel & Nexus Automate (Fase 09)
- **Permissão Dedicada**: `NexusPermission.Automation` (`1 << 11`) somada às permissões específicas de cada ação executada (`ControlInput`, `SystemPower`, `MediaControl`, `Notifications`).
- **Principais Contratos (`Nexus.Contracts.Automation`)**: `PanelProfile`, `PanelWidgetItem` (`PanelWidgetType` com 10 widgets, `PanelWidgetSize`, `PanelThemePreset`, `PanelAccentColor`), `ApprovedAppShortcut`, `AutomationRule`, `AutomationCondition`, `AutomationAction`, `AutomationExecutionRecord`, `AutomationTriggerEvent`, `BatteryStateSnapshot`, `PanelRuntimeIndicator`, `AutomationEngineSettings`, `AutomationTriggerDispatchRequest`.
- **Nexus Panel**:
  - `GET /api/v1/panel/profiles` *(requer `ViewStatus`)* · `PUT /api/v1/panel/profiles` · `POST /api/v1/panel/profiles/{profileId}/activate` *(requer `Automation`; propaga `ReceivePanelProfileUpdated` no SignalR)*.
- **Nexus Automate** *(requer `Automation`)*:
  - `GET/PUT /api/v1/automations/settings` · `GET/PUT /api/v1/automations/rules` · `DELETE /api/v1/automations/rules/{ruleId}` · `GET /api/v1/automations/history?limit=` · `POST /api/v1/automations/trigger` *(propaga `ReceiveAutomationExecution` no SignalR)*.

## Endpoints e Contratos — NEXUS Update (Fase 10)
- **Contratos (`Nexus.Contracts.Updates`)**: `NexusSemanticVersion`, `UpdateRepositoryVisibility` (`PublicDirect`, `PrivateServerRelay`), `UpdateStatusKind`, `UpdateFailureReason`, `InstalledAppVersionInfo`, `UpdateConfiguration`, `NexusReleaseManifest`, `UpdateAvailabilityReport`, `UpdateDownloadProgress`, `VerifiedUpdatePackage`, `UpdateInstallLaunchResult`, `UpdateRelayStatusContract`, `UpdateHistoryRecord`.
- **Relay Autenticado para Repositórios Privados (`Nexus.Server` — requer `NexusBearer` + `NexusPermission.ViewStatus`)**:
  - `GET /api/v1/updates/status` — Diagnóstico público do estado do relay (sem expor tokens).
  - `GET /api/v1/updates/latest?owner=&repo=&includePrereleases=` — Consulta release privada no servidor usando token de ambiente (`NEXUS_GITHUB_RELEASE_TOKEN`) e devolve `NexusReleaseManifest` validado.
  - `GET /api/v1/updates/releases/{tag}/download` — Faz streaming HTTPS autenticado do `.apk` para o Android pareado (removendo credenciais em redirecionamentos para CDNs externos), informando `X-Nexus-Apk-Sha256`.

