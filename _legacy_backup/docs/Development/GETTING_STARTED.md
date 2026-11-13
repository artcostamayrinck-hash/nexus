# Guia de Desenvolvimento e Compilação

## Pré-requisitos do Ambiente
- **.NET SDK 10.0** (validado em `10.0.403`).
- Para executar `Nexus.Desktop`: Windows 10/11 com `Microsoft.WindowsDesktop.App`.
- Para compilar `Nexus.Mobile` (Android/.NET MAUI):
  - Carga de trabalho .NET MAUI Android (`dotnet workload install maui-android`)
  - Android SDK (API 34/35+) e JDK 17+

## Comandos Úteis

### Restaurar e compilar toda a solução
```powershell
dotnet restore
dotnet build
```

### Executar suítes de testes separadamente
```powershell
dotnet test tests/Nexus.UnitTests/Nexus.UnitTests.csproj
dotnet test tests/Nexus.IntegrationTests/Nexus.IntegrationTests.csproj
dotnet test tests/Nexus.SecurityTests/Nexus.SecurityTests.csproj
```

### Armazenamento e Logs Locais em Execução
Por padrão, os dados SQLite e logs Serilog são gravados em:
- Servidor: `%LOCALAPPDATA%\NEXUS\server\` (`nexus-server.db`)
- Desktop Windows: `%LOCALAPPDATA%\NEXUS\desktop\` (`nexus-desktop.db`)
- Mobile Android: `FileSystem.AppDataDirectory/nexus/` (`nexus-mobile.db`)

