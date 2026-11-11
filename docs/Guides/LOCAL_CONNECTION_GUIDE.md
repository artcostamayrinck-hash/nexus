# NEXUS — Guia de Conexão Local (LAN & mDNS)

Na mesma rede Wi-Fi ou Ethernet, o NEXUS prioriza automaticamente a rota **Local LAN** (`ConnectionRouteKind.LocalLan`) para obter menor latência e máxima vazão nas transferências de arquivos.

---

## 1. Como Funciona a Descoberta mDNS

- O `Nexus.Server` anuncia o serviço `_nexus._tcp.local.` na rede local quando `Nexus:Discovery:EnableMdns` está habilitado.
- O anúncio divulga o identificador criptográfico do host (`DeviceId`), porta TLS (`5443`) e *fingerprint* da chave pública.
- **Autenticação antes da conexão:** A descoberta local **não concede confiança**. Mesmo após localizar o PC via mDNS, o Android só conecta e troca dados se houver um relacionamento de pareamento válido e confirmação da impressão digital TLS (`ExpectedTlsThumbprint`) e da identidade do host (`ExpectedHostKeyFingerprint`).

---

## 2. Configurando o Firewall do Windows

Se o celular estiver no mesmo roteador Wi-Fi que o PC, mas o teste de conexão atingir tempo limite (*timeout*), verifique o perfil de rede e a porta TCP `5443`:

1. Confirme nas configurações do Windows que a rede atual está marcada como **Perfil Privado** (e não Público).
2. Libere a porta TCP `5443` pelo script oficial (em PowerShell como Administrador):
   ```powershell
   .\packaging\windows\manage-nexus-service.ps1 -Action install -OpenFirewallPort
   ```
   Ou manualmente:
   ```powershell
   New-NetFirewallRule -DisplayName "NEXUS Server HTTPS (5443)" -Direction Inbound -Protocol TCP -LocalPort 5443 -Profile Private -Action Allow
   ```

---

## 3. Conexão Manual por Endereço IP

Redes corporativas, *AP Isolation* de roteadores ou VPNs simultâneas podem bloquear pacotes UDP multicast do mDNS (`224.0.0.251:5353`). Nesse cenário:

1. No Windows Desktop, verifique o IP local listado na aba **Conectividade**.
2. No Android (aba **Rotas** ou **Conexão**), cadastre manualmente o endpoint HTTPS:
   `https://<IP-LOCAL-DO-PC>:5443`
3. O orquestrador NEXUS (`NexusRouteOrchestrator`) testará o endpoint `/api/v1/system/info`, validará o certificado TLS pinado e armazenará a rota no SQLite local para seleção automática nas próximas conexões.

