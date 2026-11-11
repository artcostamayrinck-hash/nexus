# NEXUS — Manual de Instalação (Windows, Servidor e Android)

Este manual descreve como publicar, instalar, configurar para início automático e desinstalar o ecossistema **NEXUS** em ambiente pessoal de produção.

---

## 1. Requisitos de Ambiente

### Computador Windows (Host + Servidor)
- **Sistema Operacional**: Windows 10 (1903+) ou Windows 11 (`x64` ou `arm64`)
- **Runtime**: .NET 10 Desktop Runtime / ASP.NET Core Runtime 10 (desnecessário se publicado com `-SelfContained`)
- **Permissões**: Conta de usuário padrão (a instalação padrão usa `%LOCALAPPDATA%\Programs\NEXUS` e não exige privilégios administrativos, exceto para abrir regra opcional no Firewall do Windows)
- **Rede**: Rede local (Wi-Fi/Ethernet) para mDNS (`_nexus._tcp.local.`) e/ou **Tailscale** para acesso remoto privado

### Smartphone Android (Cliente)
- **Sistema Operacional**: Android 8.0 Oreo (API 26) ou superior
- **Compilação de Release / APK**:
  - Por GitHub Actions (`.github/workflows/android-release.yml`) **sem necessidade de SDK local**; ou
  - Localmente após instalar o workload MAUI Android (`dotnet workload install maui-android`) e o Android SDK

---

## 2. Publicação e Instalação no Windows

### Passo 1 — Gerar pacote de produção
Na raiz do repositório, execute via PowerShell:

```powershell
# Publicação framework-dependent (requer .NET 10 instalado no Windows de destino)
.\packaging\windows\publish-windows.ps1

# Opcional: publicação autocontida (sem depender de runtime .NET pré-instalado)
.\packaging\windows\publish-windows.ps1 -SelfContained
```

Os artefatos compilados serão gerados em `dist\windows\`:
- `dist\windows\server\Nexus.Server.exe` + bibliotecas e `appsettings.Production.example.json`
- `dist\windows\desktop\Nexus.Desktop.exe` + ícone e manifesto DPI (`PerMonitorV2`)
- `dist\windows\SHA256SUMS.txt` e `dist\windows\release-metadata.json`

### Passo 2 — Instalar e registrar atalhos
Execute o instalador incluído na própria pasta `dist\windows\` (ou em `packaging\windows\`):

```powershell
# Instala em %LOCALAPPDATA%\Programs\NEXUS, cria atalhos no Menu Iniciar
# e registra a tarefa agendada NEXUS-Server-AutoStart no logon do usuário
.\dist\windows\install-nexus.ps1 -AutoStartServer
```

O banco SQLite e o cofre criptográfico DPAPI permanecem isolados em `%LOCALAPPDATA%\NEXUS\` para que atualizações futuras nunca sobrescrevam dispositivos pareados, notas ou certificados TLS.

### Passo 3 — Configuração de Firewall (LAN) e Serviço
Para liberar a porta TLS padrão (`5443`) na rede privada local e gerenciar a execução em segundo plano:

```powershell
# Como Administrador, caso deseje criar regra de entrada no Firewall LAN (perfil Private):
.\packaging\windows\manage-nexus-service.ps1 -Action install -OpenFirewallPort

# Iniciar, verificar status ou parar o Nexus.Server:
.\packaging\windows\manage-nexus-service.ps1 -Action start
.\packaging\windows\manage-nexus-service.ps1 -Action status
.\packaging\windows\manage-nexus-service.ps1 -Action stop
```

O `Nexus.Server` também aceita parâmetros diretos via CLI para execução manual ou portável:
```powershell
Nexus.Server.exe --data-dir "D:\Dados\NEXUS\server" --port 5443
```

---

## 3. Instalação do Aplicativo Android (`Nexus.Mobile`)

### Via GitHub Actions (Recomendado)
1. Configure os *Secrets* de assinatura (`ANDROID_KEYSTORE_BASE64`, `ANDROID_KEY_ALIAS`, `ANDROID_KEY_PASSWORD`, `ANDROID_STORE_PASSWORD`) no GitHub.
2. Execute o workflow `.github/workflows/android-release.yml` manualmente (`workflow_dispatch`) ou publique uma tag `v*` (ex.: `v1.0.0`).
3. Transfira o APK assinado (`com.nexus.ecosystem-<versão>-Signed.apk`) para o telefone ou instale futuras atualizações diretamente pela aba **Atualizações** no próprio app.

### Via Compilação Local (Opcional)
> **Nota importante:** Caso sua máquina de desenvolvimento não tenha o workload `maui-android` instalado, `dotnet workload list` indicará a ausência e o script `packaging/android/build-release-apk.ps1` encerrará com código de orientação (`exit 2`). Instale o workload antes se quiser compilar APKs offline:

```powershell
dotnet workload install maui-android
$env:NEXUS_ANDROID_KEYSTORE_PATH = "C:\caminho\seguro\nexus-release.keystore"
$env:NEXUS_ANDROID_KEY_ALIAS    = "nexus-release"
$env:NEXUS_ANDROID_KEY_PASS     = "<senha-chave>"
$env:NEXUS_ANDROID_STORE_PASS   = "<senha-keystore>"

.\packaging\android\build-release-apk.ps1
```

---

## 4. Desinstalação Segura

```powershell
# Remove binários, atalhos e tarefa agendada, PRESERVANDO banco SQLite e chaves:
.\packaging\windows\uninstall-nexus.ps1

# Para remover também dados locais, credenciais DPAPI e histórico SQLite:
.\packaging\windows\uninstall-nexus.ps1 -PurgeUserData
```

