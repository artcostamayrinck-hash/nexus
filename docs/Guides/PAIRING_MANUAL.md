# NEXUS — Manual de Pareamento Seguro (Windows ↔ Android)

O pareamento NEXUS utiliza criptografia **ECDSA P-256**, desafio efêmero com proteção anti-replay (`Nonce` + timestamp), segredo de uso único (*one-time secret*) no QR Code e **código de verificação mútua (SAS de 6 dígitos)**. Nenhum dispositivo é identificado apenas por IP ou nome.

---

## Fluxo Passo a Passo

1. **Verifique o `Nexus.Server` e o `Nexus.Desktop`**:
   - No computador Windows, abra o **NEXUS Desktop**.
   - Confirme no Dashboard que o `Nexus.Server` local está ativo em HTTPS (ex.: `https://192.168.1.x:5443` ou endereço Tailscale `100.x.x.x:5443`).
2. **Gere um QR Code Temporário no Windows**:
   - Na aba **Pareamento**, clique em **Gerar QR Code**.
   - O payload JSON assinado tem validade limitada (padrão: **120 segundos**).
   - Se desejar copiar o JSON manualmente para teste rápido ou colagem no Android, clique em **Copiar JSON** (a interface exibirá confirmação imediata na barra de status).
3. **Envie o Pedido no Android (`Nexus.Mobile`)**:
   - Abra a tela **Conexão** (`Pareamento & Conexão`) no app Android.
   - Cole ou leia o QR Code exibido no Windows e toque em **Parear Agora**.
   - O segredo de uso único é validado e imediatamente invalidado no servidor; o campo `QrPayloadJson` **não é mais retornado** por `GET /api/v1/pairing/sessions/{id}` nem transmitido via SignalR após a criação da sessão.
4. **Confirme o Código Mútuo de 6 Dígitos (SAS)**:
   - Compare o código de 6 dígitos exibido na tela **Pareamento** do Windows com o código exibido na tela **Conexão** do Android.
   - Antes de aprovar no Windows, ajuste as permissões granulares concedidas ao telefone:
     - `ViewStatus` (consulta de estado/telemetria)
     - `Notifications` (recebimento de notificações Android no PC)
     - `Clipboard` (sincronização da área de transferência e notas)
     - `FileTransfer` (envio/recebimento de arquivos)
     - `ControlInput` / `MediaControl` (controle remoto de mouse, teclado e mídia)
     - `SystemPower` (bloquear, suspender ou desligar — exige confirmação explícita)
     - `Automation` (execução de regras remotas)
     - `ManageDevices` (revogação e gerenciamento de dispositivos)
   - Clique em **Aprovar Pareamento** no Windows.
5. **Finalização Automática**:
   - O Android recebe `accessToken` e `refreshToken` vinculados ao seu `DeviceId` e armazenados no cofre seguro da plataforma.
   - Os dois dispositivos passam a constar na aba **Dispositivos**.

---

## Gerenciamento de Confiança e Revogação

- **Suspender / Revogar Imediatamente**: Na aba **Dispositivos** do NEXUS Desktop (ou endpoint `/api/v1/trusted-devices/{id}/revoke`), é possível revogar um celular a qualquer momento. A middleware de autenticação valida o status no SQLite em **toda requisição REST e handshake SignalR**, bloqueando imediatamente qualquer token já emitido (`HTTP 401 Unauthorized`).
- **Renovação de Token**: Tokens de acesso possuem validade curta e são renovados automaticamente via `POST /api/v1/auth/refresh` sempre que o servidor retornar `401`, sem precisar re-parear o aparelho.

