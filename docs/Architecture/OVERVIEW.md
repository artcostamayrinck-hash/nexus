# Arquitetura do NEXUS

## Princípios de Design
1. **Grafo Acíclico Direcionado (DAG)**: Nenhuma dependência circular entre projetos.
2. **Contratos Compartilhados**: `Nexus.Contracts` define modelos versionados (`1.0`) e validáveis (`ContractValidator`) para domínio, pareamento QR e segurança consumidos por Windows, Android e Servidor.
3. **Baixo Acoplamento e Inversão de Dependência**: `Nexus.Core` define abstrações (`IPairingService`, `ITrustedDeviceService`, `IDeviceRepository`, `ISettingsStore`, `IFileStorageService`, `IConnectionManager`, `IEventBus`) implementadas em `Nexus.Infrastructure`, `Nexus.Security` e `Nexus.Core`.
4. **Falhas Explícitas com `Result` / `NexusError`**: Erros esperados de validação, rede, autorização ou armazenamento retornam `Result<T>` estruturado.
5. **Confiança Baseada em Criptografia (nunca apenas IP/Nome)**: Cada instalação mantém par de chaves **ECDSA P-256** persistente em cofre criptografado (`DPAPI` no Windows / `AES-256-GCM`), identificação por `DeviceId` UUIDv7 e impressão digital SHA-256.

## Diagrama de Dependências

```
                 Nexus.Contracts
                        ▲
                    Nexus.Core
                 ▲              ▲
       Nexus.Security      Nexus.Modules
                 ▲
        Nexus.Infrastructure
          ▲        ▲          ▲
Nexus.Server  Desktop.Services  Mobile.Services
                   ▲                 ▲
             Nexus.Desktop     Nexus.Mobile
```

## Camadas Principais
- **Domínio e Contratos (`Nexus.Contracts`, `Nexus.Core`)**: Contratos versionados, `Result`/`NexusError`, `InProcessEventBus`, `InMemoryStateManager<NexusRuntimeState>`, `IPairingService`, `ITrustedDeviceService`, `INexusRouteOrchestrator`, `IMdnsServiceDiscovery`, `INetworkEnvironmentInspector`, `IRealtimeClientChannel`, `IRealtimeCredentialProvider`, `IHttpBulkTransferChannel`, `RealtimeMessagePipeline` (`IMessageOrderingPipeline` com deduplicação e reordenação por sequência) e abstrações futuras (`ISignalingChannel`, `IRelayTransport`, `IVpsGatewayProvider`, `INatTraversalProbe`, `IStunTurnClient`).
- **Segurança (`Nexus.Security`)**:
  - `CryptographicIdentityProvider`: identidade persistente com par de chaves ECDSA P-256, assinatura/verificação e código de verificação mútua de 6 dígitos (SAS).
  - `PlatformSecureCredentialStore`: armazenamento cifrado em repouso via `ProtectedData` (Windows DPAPI CurrentUser) ou `AES-256-GCM`.
  - `DeviceTokenService`: tokens de acesso assinados com HMAC-SHA256 (`v1.*`) e refresh tokens CSPRNG com rotação e detecção de reutilização.
  - `InMemoryNonceReplayProtector`: proteção anti-repetição (`TimestampUtc` ±5 min + nonces de uso único).
  - `LocalTlsCertificateManager`: geração e persistência de certificado X.509 ECDSA e handler HTTP com pinagem por thumbprint SHA-256.
  - `QrCodeGeneratorService`: geração de QR Code PNG temporário via `QRCoder`.
- **Infraestrutura (`Nexus.Infrastructure`)**: Persistência SQLite esquema v3 (`Devices`, `DeviceEndpoints`, `TrustedDevices`, `PairingSessions`, `RefreshTokens`, `UserSessions`, `Notifications`, `SyncCheckpoints`), `MdnsServiceDiscovery` (`_nexus._tcp.local.`), `NetworkEnvironmentInspector` (detecção LAN + CGNAT/MagicDNS Tailscale), `HttpRouteProber` (RTT + pinning TLS/ECDSA), `NexusRouteOrchestrator` (prioridade Local -> Remoto/Tailscale, reconexão automática e troca segura sem interromper operações ativas), `SignalRRealtimeClient` (estados `Disconnected -> Connecting -> Authenticating -> Connected`, `Reconnecting`, `Faulted`, ACKs fim-a-fim, cancelamento remoto e refresh de token), `HttpBulkTransferClient`, `HostBridgeRealtimeCredentialProvider`, `PairingCoordinatorService`, `TrustedDeviceManagementService`, `NexusPairingProtocolClient` e `LocalHostTokenBridge`.
- **Servidor (`Nexus.Server`)**: Autenticação `NexusBearer`, autorização granular por flags `NexusPermission`, `TlsEnforcementMiddleware` (`HTTP 426 TLS_REQUIRED`), anúncio mDNS (`NexusMdnsAdvertisementService`), endpoints `/api/v1/connectivity/*` e `/api/v1/realtime/*` e Hub SignalR autenticado (`NexusHub` + `ServerRealtimeCoordinator` mesclando confiança em SQLite com presença real ativa — dispositivos apenas cadastrados sem sessão ativa são reportados como `Disconnected` / `Offline`).
- **Apresentação**:
  - **Windows (`Nexus.Desktop` / WPF)**: Dashboard, Conectividade (LAN, mDNS, Tailscale), Tempo Real (estado SignalR, presença real, envio/cancelamento de comandos, eventos, ACKs e teste HTTP bulk), Pareamento QR Code, Dispositivos Confiáveis (badge explícito de conexão real vs apenas cadastrado), Status do Sistema e Configurações.
  - **Android (`Nexus.Mobile` / .NET MAUI + `Nexus.Mobile.Services`)**: Tela Inicial com identidade ECDSA, Conexão & Pareamento QR TLS, Rotas & VPN Tailscale, Tempo Real (`MobileRealtimeViewModel` + `MobileRealtimeCredentialProvider` com renovação de tokens), Dispositivos Confiáveis (estado real online vs offline) e Configurações.
