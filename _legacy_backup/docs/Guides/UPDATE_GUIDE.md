# NEXUS — Guia de Atualização do Aplicativo (Nexus Update)

O **Nexus Update** verifica novas versões publicadas nas **GitHub Releases**, baixa o pacote `.apk`, valida assinatura e integridade criptográfica e inicia o instalador oficial do Android somente mediante aprovação explícita do usuário.

---

## 1. Modos de atualização configuráveis (`UpdateRepositoryVisibility`)

| Modo | Indicado para | Como funciona |
| --- | --- | --- |
| **Público Direto (`PublicDirect`)** | Repositório GitHub público | O app consulta a API pública de Releases do GitHub e baixa os artefatos `nexus-update-manifest.json` e `.apk` diretamente via HTTPS. |
| **Relay Privado (`PrivateServerRelay`)** | Repositório GitHub privado | O Android consulta apenas o **`Nexus.Server` pareado** (`/api/v1/updates/latest` e `/api/v1/updates/releases/{tag}/download`). O servidor lê `NEXUS_GITHUB_RELEASE_TOKEN` apenas do ambiente local e **nunca expõe o token ao celular**. |

> Referência técnica completa em [NEXUS_UPDATE_ARCHITECTURE.md](../Release/NEXUS_UPDATE_ARCHITECTURE.md) e [GITHUB_ACTIONS_AND_SIGNING.md](../Release/GITHUB_ACTIONS_AND_SIGNING.md).

---

## 2. Garantias de segurança antes da instalação (`ApkPackageVerifier`)

Antes de permitir qualquer instalação, o cliente executa obrigatoriamente todas as etapas abaixo:

1. **Origem HTTPS confiável**: aceita apenas domínios oficiais do GitHub (`github.com`, `api.github.com`, `objects.githubusercontent.com`, `release-assets.githubusercontent.com`) ou o próprio `Nexus.Server` pareado (com TLS pinado SHA-256).
2. **Comparação SemVer + `VersionCode`**: impede *downgrade* caso o `AndroidVersionCode` remoto seja menor que o instalado (`NexusSemanticVersion`).
3. **Digest SHA-256 integral**: compara o hash do `.apk` baixado contra o manifesto `nexus-update-manifest.json` (e o cabeçalho `X-Nexus-Apk-Sha256` quando servido via relay).
4. **Inspeção estrutural e assinatura APK v2/v3**: verifica `AndroidManifest.xml`, confirma `PackageId = com.nexus.ecosystem` e valida que o certificado X.509 signatário coincide com o do app instalado (ou com a impressão SHA-256 configurada). Em caso de divergência (`SignatureMismatch`), o `.apk` em staging é imediatamente descartado.

---

## 3. Fluxo de atualização passo a passo no Android

1. Abra a aba/página **Atualizações** no NEXUS Android.
2. Revise o repositório configurado (`Owner` / `Repository`) e se o modo **Repositório Privado (via Servidor)** está ativado quando aplicável.
3. Toque em **Verificar atualizações**.
4. Havendo versão superior, toque em **Baixar e Validar**. Acompanhe o progresso e o status da validação criptográfica.
5. Caso o Android ainda não tenha permissão para este app instalar pacotes (`REQUEST_INSTALL_PACKAGES`), toque em **Permitir instalação de apps** para abrir as configurações do sistema.
6. Toque em **Instalar atualização** para confirmar explicitamente e concluir pelo instalador nativo do Android.

---

## 4. Configurando repositório privado no `Nexus.Server`

1. Crie no GitHub um **Fine-grained Personal Access Token** com permissão somente leitura em *Contents* / *Metadata* apenas do repositório do NEXUS.
2. Defina no Windows a variável de ambiente de usuário antes de iniciar o `Nexus.Server`:
   ```powershell
   [Environment]::SetEnvironmentVariable("NEXUS_GITHUB_RELEASE_TOKEN", "SEU_TOKEN_AQUI", "User")
   ```
3. Configure `config/github-update.example.json` (copiado para `github-update.example.json` na pasta do servidor publicado) ou `appsettings.Production.json` definindo `RepositoryOwner`, `RepositoryName` e `"Visibility": "PrivateServerRelay"` (conforme `Nexus:Updates`).
4. **Nunca** salve tokens em `appsettings` versionados no Git nem os inclua no APK Android.

