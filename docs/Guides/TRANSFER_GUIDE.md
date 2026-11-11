# NEXUS — Guia de Transferência de Arquivos (Nexus Transfer)

O **Nexus Transfer** permite enviar e receber arquivos bidirecionalmente entre **Android** e **Windows** via HTTP/HTTPS com streaming em blocos (*chunks*), verificação de integridade **SHA-256**, controle de concorrência, pausa/retomada e proteção contra *path traversal*.

---

## 1. Como funciona o transporte

- **Streaming HTTP com blocos configuráveis**: os arquivos não são carregados inteiramente em memória RAM (`HttpTransferTransport`). O tamanho padrão de bloco é **256 KiB** (`ChunkSizeBytes = 256 * 1024` em `FileTransferRecord`).
- **Controle de fila e paralelismo**: o `TransferManagerService` gerencia fila de envio/recebimento, limite configurável de transferências simultâneas e histórico persistido localmente em SQLite (`SqliteTransferRepository`).
- **Integridade ponta a ponta**: cada transferência calcula e valida o digest **SHA-256** integral do arquivo antes de finalizar (`TransferStage.Verifying` -> `TransferStage.Completed`).
- **Armazenamento seguro (`SafeTransferStorageProvider`)**: higieniza nomes de arquivos contra traversal (`../`), verifica espaço livre em disco com margem configurável (`MinimumFreeSpaceSafetyMarginBytes`) e grava em arquivo temporário até a verificação de integridade.

---

## 2. Política de arquivos duplicados (`TransferDuplicatePolicy`)

Ao receber um arquivo cujo nome já existe na pasta de destino, o usuário ou configuração pode aplicar uma das três políticas:

| Política | Comportamento |
| --- | --- |
| `Rename` (padrão) | Salva o arquivo recebido com sufixo numérico sequencial sem sobrescrever o existente. |
| `Overwrite` | Substitui o arquivo existente somente após concluir e validar o checksum do novo download. |
| `Skip` | Mantém o arquivo existente e conclui o registro sem sobrescrita. |

---

## 3. Pausa, retomada e cancelamento

- **Pausar (`TransferState.Paused`)**: interrompe o envio ou download preservando os bytes já gravados (`ServerUploadedBytes` / arquivo parcial `.part`).
- **Retomar**: consulta o deslocamento já confirmado no servidor (`offset`) e envia ou baixa apenas os blocos restantes até concluir.
- **Cancelar (`TransferState.Cancelled`)**: aborta a transmissão via `CancellationToken`, remove arquivos parciais de staging e notifica o outro dispositivo via SignalR.

---

## 4. Endpoints utilizados (`TransferEndpoints`)

Todas as rotas exigem dispositivo autenticado (`NexusBearer` + TLS pinado) e permissão `FileTransfer`:

- `POST /api/v1/transfers/initiate`: cria sessão de transferência com metadados e checksum esperado.
- `PUT /api/v1/transfers/{transferId:guid}/chunks`: envia bloco em streaming com `offset` validado.
- `POST /api/v1/transfers/{transferId:guid}/finalize-upload`: conclui envio ao servidor e valida integridade SHA-256.
- `GET /api/v1/transfers/{transferId:guid}/download`: baixa arquivo completo ou parcial (suporta retomada por Range/offset).
- `POST /api/v1/transfers/{transferId:guid}/acknowledge`: confirma recebimento e integridade no destinatário.
- `POST /api/v1/transfers/{transferId:guid}/pause` · `cancel`: pausa ou cancela a transmissão na fila.
- `GET /api/v1/transfers/{transferId:guid}` · `GET /api/v1/transfers/incoming`: consulta status ou transferências pendentes para o dispositivo.

---

## 5. Boas práticas de uso

1. **Arquivos muito grandes**: dê preferência à rota **LAN direta** (Wi-Fi 5 GHz / Ethernet) antes de recorrer ao túnel remoto Tailscale para maximizar vazão.
2. **Android em segundo plano**: mantenha a tela ativa ou desative restrições agressivas de economia de bateria para o NEXUS durante transferências de vários gigabytes.
3. **Falha de rede durante o envio**: aguarde a reconexão automática do NEXUS e utilize **Retomar** no item interrompido na tela de Transferências.

