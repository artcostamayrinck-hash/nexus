# NEXUS — Guia de Solução de Problemas (Troubleshooting)

Diagnóstico objetivo para os problemas mais comuns de instalação, rede, pareamento, transferências e atualizações no **NEXUS**.

---

## 1. Diagnóstico rápido no Windows

### Verificar saúde local do servidor
Com o `Nexus.Server` em execução na porta padrão (`5443`):

```powershell
# Verifica liveness, readiness e metadados/impressão TLS
Invoke-RestMethod "https://localhost:5443/health/live" -SkipCertificateCheck
Invoke-RestMethod "https://localhost:5443/health/ready" -SkipCertificateCheck
Invoke-RestMethod "https://localhost:5443/api/v1/system/info" -SkipCertificateCheck
```

### Verificar serviço/tarefa agendada e processos
```powershell
.\packaging\windows\manage-nexus-service.ps1 -Action status
```

---

## 2. Problemas de conexão local (LAN / mDNS)

- **Celular não encontra o PC automaticamente por mDNS (`_nexus._tcp.local.`)**:
  - Verifique se celular e PC estão na mesma sub-rede Wi-Fi/Ethernet (redes "Wi-Fi Convidado" ou *AP Isolation* bloqueiam multicast UDP `5353`).
  - Confira no Windows se o perfil da rede está definido como **Rede Privada** (redes Públicas bloqueiam conexões de entrada por padrão).
  - Abra a porta TCP `5443` no Firewall do Windows (requer PowerShell como Administrador):
    ```powershell
    .\packaging\windows\manage-nexus-service.ps1 -Action install -OpenFirewallPort
    ```
  - **Contorno imediato**: cadastre o IP LAN manual do computador (ex.: `https://192.168.1.50:5443`) no aplicativo Android; a descoberta automática por mDNS não é obrigatória.

---

## 3. Problemas de conexão remota (Tailscale / VPN)

- **Conexão funciona em casa, mas falha fora da rede Wi-Fi**:
  - Confirme no app **Tailscale** (Windows e Android) que ambos estão conectados na mesma *tailnet* e com status ativo.
  - Teste se o endereço Tailscale (`100.x.y.z` ou nome MagicDNS) responde a partir do celular.
  - Verifique no `Nexus.Desktop` / Android se o endpoint Tailscale cadastrado aponta para a porta `5443` em **HTTPS**.

---

## 4. Problemas de pareamento por QR Code

- **"QR Code expirado" ou "Nonce já utilizado"**:
  - Cada QR Code possui validade temporária limitada (TTL configurável, padrão curto) e proteção anti-reuso. Gere um **novo QR Code** no Windows e leia-o imediatamente.
- **Erro de validação TLS / Thumbprint divergente**:
  - Se os dados do servidor ou certificado local em `%LOCALAPPDATA%\NEXUS` foram apagados/recriados após um pareamento anterior, revogue o dispositivo antigo nas Configurações e execute novo pareamento por QR Code para atualizar a pinagem SHA-256.
- **Autenticação falha após revogação (`401 Unauthorized` / `DEVICE_REVOKED`)**:
  - Dispositivos revogados têm sessões e refresh tokens encerrados imediatamente. É necessário um novo pareamento autorizado pelo usuário.

---

## 5. Sincronização de Área de Transferência no Android

- **Texto copiado em outros apps não sincroniza automaticamente no Android**:
  - A partir do **Android 10 (API 29+)**, o sistema operacional restringe a leitura da área de transferência em segundo plano a apps com foco em tela ou teclado ativo (IME).
  - Abra o **NEXUS Android** em primeiro plano e toque em **Enviar área de transferência agora** (ou utilize a ação rápida correspondente). Isso é uma restrição intencional de privacidade do Android, não uma falha de rede.

---

## 6. Transferências e Atualizações (APK)

- **Transferência interrompida**:
  - Verifique se há espaço livre suficiente no destino (`SafeTransferStorageProvider` exige espaço para o arquivo + margem de segurança).
  - Após restabelecer a conexão, utilize **Retomar** no item pausado/falho em vez de iniciar do zero quando já houver blocos no servidor.
- **Atualização bloqueada com `SignatureMismatch` ou erro de downgrade**:
  - `SignatureMismatch`: o APK baixado não foi assinado pela mesma keystore usada no APK instalado, ou houve divergência de digest SHA-256. Confira os segredos de assinatura no GitHub Actions ou recompile com a keystore correta.
  - Downgrade bloqueado: certifique-se de que `-p:NexusAndroidVersionCode` da nova build é estritamente maior que o instalado.
- **`NoSuchFieldError` / erro de permissão ao instalar APK**:
  - Conceda a permissão **"Instalar apps desconhecidos"** para o NEXUS nas configurações do Android (o próprio app abre essa tela quando necessário).
- **Compilação local do `Nexus.Mobile` falha por falta de workload**:
  - O projeto Android exige o workload `maui-android` (`dotnet workload install maui-android` + Android SDK) ou geração via GitHub Actions em `.github/workflows/android-release.yml`.

