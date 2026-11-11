# NEXUS — Guia de Conexão Remota via Tailscale

O NEXUS foi projetado para funcionar entre redes completamente diferentes sem expor portas na internet pública nem depender de servidores centrais de terceiros, utilizando **Tailscale** como malha privada criptografada (WireGuard).

---

## 1. Arquitetura de Rotas & Prioridade

O `NexusRouteOrchestrator` classifica automaticamente os endpoints cadastrados em ordem decrescente de preferência:

1. **Local LAN** (`192.168.x.x`, `10.x.x.x`, `*.local`) — menor latência
2. **Remote VPN Tailscale** (faixa CGNAT `100.64.0.0/10`, IPv6 `fd7a:115c:a1e0::/48` ou domínio MagicDNS `*.ts.net`) — alcance seguro fora de casa
3. **Remote Private** (endpoint HTTPS privado manual)

Quando você sai do Wi-Fi de casa e entra na rede móvel (4G/5G), o orquestrador detecta que a rota Local LAN ficou inacessível e alterna automaticamente para a rota Tailscale. Ao voltar para casa, se houver uma transferência de arquivos em andamento (`IOperationLease`), **a troca de volta para LAN é adiada (`HandoverDeferred`) até a transferência concluir**, evitando corromper o stream ativo.

---

## 2. Configuração Rápida

1. **Instale o Tailscale** no computador Windows e no smartphone Android com a mesma conta pessoal.
2. **Obtenha o endereço Tailscale do Windows**:
   - Na barra de status do Tailscale (ou executando `tailscale ip -4`), anote o IP na faixa `100.x.y.z` (ex.: `100.88.14.22`) ou o nome MagicDNS (`meu-pc.tailnet-xxx.ts.net`).
3. **Verifique o `Nexus.Server` em execução no Windows**:
   - Por padrão, quando `Nexus:Tls:BindKestrelHttps` é `true`, o Kestrel escuta na porta `5443` em `IPAddress.Any`, aceitando tanto a interface Wi-Fi/LAN quanto a interface virtual do Tailscale com o mesmo certificado TLS pinado.
4. **Cadastre a Rota Tailscale no NEXUS**:
   - Cadastre `https://100.88.14.22:5443` (ou o endereço MagicDNS equivalente) na aba **Rotas** com `Origin = TailscalePrivate`.
   - Sempre inclua o *fingerprint* da chave pública do host para bloquear qualquer spoofing de endpoint.

---

## 3. Boas Práticas de Segurança com Tailscale

- Mantenha `Nexus:Tls:RequireHttps = true` no arquivo `appsettings.Production.json` para impedir comunicação em texto claro mesmo sobre túnel VPN.
- Caso um dispositivo Android seja perdido, revogue tanto o acesso no painel **Dispositivos** do NEXUS quanto a chave da máquina no painel admin do Tailscale.
- Se desejar mais detalhes sobre configurações de ACL no Tailscale, consulte também [TAILSCALE_SETUP.md](../Development/TAILSCALE_SETUP.md).

