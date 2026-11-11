# Guia de Instalação e Configuração do Tailscale para o NEXUS (Windows + Android)

O **NEXUS** utiliza uma estratégia híbrida de conectividade:
1. **Modo Local (prioritário):** conexão direta via Wi-Fi / LAN com descoberta automática por mDNS (`_nexus._tcp.local.`) ou IP local manual.
2. **Modo Remoto (fallback seguro):** rede privada virtual ponto-a-ponto via **Tailscale**, sem precisar abrir portas públicas no roteador (CGNAT/NAT amigável).

> [!IMPORTANT]
> O NEXUS **não instala nem controla automaticamente** a conta ou o serviço do Tailscale. A instalação, login e ativação da VPN devem ser feitos diretamente pelo aplicativo oficial do Tailscale no Windows e no Android.

---

## 1. Requisitos e Arquitetura da Rede Privada

- Uma conta pessoal no Tailscale (pode usar conta Google, Microsoft ou GitHub gratuita).
- Os dois dispositivos (**PC Windows** onde roda o `Nexus.Server` e o **Android** onde roda o `Nexus.Mobile`) logados na **mesma Tailnet** (mesma conta).
- Faixas de endereço reconhecidas automaticamente pelo NEXUS como rota Tailscale:
  - IPv4 CGNAT `100.64.0.0/10` (endereços típicos `100.64.x.x` até `100.127.x.x`)
  - IPv6 ULA `fd7a:115c:a1e0::/48`
  - Domínios MagicDNS `*.ts.net`

---

## 2. Instalação e Configuração no Windows (Host / Servidor)

1. **Baixe e instale o Tailscale para Windows:**
   - Acesse <https://tailscale.com/download/windows> e execute o instalador oficial.
   - Após instalar, clique no ícone do Tailscale na bandeja do sistema (próximo ao relógio do Windows).
2. **Faça login na sua conta pessoal:**
   - Clique em **Log in** e conclua a autenticação no navegador.
3. **Anote o endereço Tailscale do computador:**
   - No menu do Tailscale na bandeja, passe o mouse ou clique sobre o nome da máquina para copiar o IP `100.x.y.z` (exemplo: `100.89.14.22`).
   - Se o **MagicDNS** estiver habilitado no painel admin do Tailscale (<https://login.tailscale.com/admin/dns>), você também verá um nome como `meu-pc.tailnet-xxxx.ts.net`.
4. **Verifique no aplicativo NEXUS Desktop:**
   - Inicie o `Nexus.Server` e abra a aba **Conectividade** no **NEXUS Desktop**.
   - Na caixa **Estado da VPN (Tailscale) & Interfaces deste Computador**, o NEXUS detectará automaticamente:
     - Interface Tailscale ativa e o endereço pronto (ex.: `https://100.89.14.22:5443`).
     - Endereços da LAN local (ex.: `https://192.168.1.50:5443`).
5. **Firewall do Windows (se solicitado):**
   - Ao executar o `Nexus.Server` pela primeira vez com HTTPS na porta padrão `5443` (ou HTTP `5280`), permita o acesso em redes **Privadas** e para a interface do **Tailscale** no Firewall do Windows.
   - **Nenhuma porta precisa ser aberta no roteador da sua casa.**

---

## 3. Instalação e Configuração no Android (Cliente Móvel)

1. **Instale o aplicativo oficial do Tailscale:**
   - Baixe o **Tailscale** na Google Play Store no seu dispositivo Android.
2. **Autentique-se na mesma conta:**
   - Abra o Tailscale no Android, toque em **Sign in** e faça login com a **mesma conta / Tailnet** usada no computador Windows.
   - Aceite a permissão do Android para criar uma conexão VPN do sistema operacional (`VpnService`).
3. **Ative o túnel no Android:**
   - Deixe a chave principal no aplicativo Tailscale ligada (**Active / Connected**).
4. **Cadastre o endpoint remoto no NEXUS Android:**
   - Primeiro, faça o **Pareamento por QR Code** estando no mesmo Wi-Fi (Recomendado), para que ambos os dispositivos já troquem identidade ECDSA P-256 e TLS Thumbprint.
   - Abra a aba **Rotas** no aplicativo NEXUS Android:
     - Toque em **Descobrir mDNS** quando estiver no Wi-Fi local (ou cadastre manualmente `https://<IP-LAN-WINDOWS>:5443`).
     - Em **Cadastrar Endpoint (LAN ou Tailscale)**, preencha:
       - **Nome da rota:** `PC Tailscale`
       - **Endpoint:** `https://<IP-TAILSCALE-WINDOWS>:5443` (exemplo: `https://100.89.14.22:5443` ou `https://meu-pc.tailnet-xxxx.ts.net:5443`)
       - **Tipo de Conexão:** `RemoteVpnTailscale`
     - Toque em **Salvar Endpoint**.

---

## 4. Como o NEXUS Seleciona a Rota Automaticamente

| Cenário | Comportamento do NEXUS |
| --- | --- |
| **Mesmo Wi-Fi (LAN + Tailscale ligados)** | O NEXUS prioriza a rota **Local (Wi-Fi / LAN)** pela menor latência e tráfego direto. |
| **Android em 4G/5G (fora de casa) + Tailscale ligado** | A rota LAN falha com segurança e o NEXUS seleciona automaticamente **Remoto (VPN Tailscale)**. |
| **Android em 4G/5G + Tailscale desligado** | A interface exibe claramente **Tailscale desconectado / VPN indisponível**, sem travar a aplicação. |
| **Retorno para o Wi-Fi de casa** | O monitor verifica se há operações ativas em andamento; quando estiver ocioso (ou após concluir a operação), promove novamente para **Local (Wi-Fi / LAN)** sem interromper tarefas. |
| **Perda momentânea de internet** | O estado muda para **Reconectando** e, assim que a rede retornar (se a reconexão automática estiver ligada), o NEXUS restabelece a melhor rota disponível. |

---

## 5. Checklist de Solução de Problemas

- **Status aparece "Tailscale não detectado" ou "Tailscale desconectado":**
  - Verifique se o aplicativo Tailscale está aberto e com a chave **Connected** nos **dois** dispositivos (Windows e Android).
- **"Certificado TLS rejeitado" ou "Falha de identidade":**
  - Refaça o pareamento por QR Code ou confirme que você não apontou o IP para outra máquina diferente da pareada.
- **Conexão remota por 4G/5G dá Timeout mesmo com Tailscale ativo:**
  - Confirme no `Nexus.Server` se `Nexus:Tls:BindKestrelHttps` está habilitado na porta `5443` e se o Firewall do Windows permite entrada na porta `5443` via interface Tailscale.
  - Em `100.64.x.y`, teste no próprio Android se ambos os dispositivos aparecem como **Connected** no app do Tailscale.

