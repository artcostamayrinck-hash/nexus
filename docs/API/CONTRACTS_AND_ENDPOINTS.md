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
| Sincronização | `SyncBatchContract`, `SyncItemMetadata`, `SyncDirection`, `SyncStatus`, `ConflictResolutionPolicy` |

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
- `SignalR /hubs/nexus` *(requer autenticação `[Authorize]`)*:
  - **Hub Server Methods**: `AnnouncePresenceAsync`, `ResetSessionStreamAsync`, `PingAsync`, `GetPresenceSnapshotAsync`, `SendRealtimeMessageAsync`, `SendCommandAsync`, `CancelCommandAsync`, `SendCommandResultAsync`, `AcknowledgeMessageAsync`.
  - **Hub Client Events**: `ReceiveRealtimeMessage`, `ReceiveMessageAck`, `ReceiveDeviceCommand`, `ReceiveCommandCancellation`, `ReceiveCommandResult`, `ReceivePresenceSnapshot`, `ReceiveSessionNotice`, `ReceiveConnectionSnapshot`, `ReceiveIntegrationEvent`, `ReceivePairingSnapshot`, `ReceiveRevocationNotice`, `ReceivePong`.

Cabeçalhos opcionais de anti-repetição assinada por requisição (`X-Nexus-Timestamp`, `X-Nexus-Nonce`, `X-Nexus-Signature`) são verificados por `NexusDeviceAuthenticationHandler`, que também atualiza `LastAccessAtUtc` e bloqueia imediatamente dispositivos com status `Revoked`.
