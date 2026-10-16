# API do Agent — v1

Base: `https://<manager>/api/agent/v1`. JSON UTF-8. Sempre HTTPS. Iniciada pelo Agent.

## 1. Autenticação por request (todas as rotas exceto `POST /enroll`)

Cabeçalhos obrigatórios:

| Header | Conteúdo |
|---|---|
| `X-Agent-Id` | UUID da instalação (`agent_installations.uuid`) |
| `X-Timestamp` | Unix epoch em segundos (UTC) |
| `X-Nonce` | 16–64 chars `[A-Za-z0-9_-]`, único por request |
| `X-Signature` | Base64 da assinatura ECDSA P-256/SHA-256 (DER, RFC 3279) |
| `X-Agent-Version` | SemVer do Agent (informativo) |

String assinada (UTF-8, `\n` separador, sem `\n` final):

```
v1
<METHOD em maiúsculas>
<PATH exato, ex.: /api/agent/v1/heartbeat>
<QUERY ordenada ou vazio>
<X-Timestamp>
<X-Nonce>
<hex minúsculo de SHA-256 do corpo bruto (vazio → hash de string vazia)>
```

Regras de canonicalização (implementadas e testadas; vetor em `tests/contracts/signature-vector.json`):
- `QUERY`: a query string **crua** (sem decodificar) dividida em `&`, ordenada em ordem ordinal de bytes e reunida com `&`; vazia se não houver.
- `PATH`: caminho exato do request (sem host, sem query).
- Corpo: bytes exatamente como enviados (o Agent deve assinar os mesmos bytes que transmite).
- **Um nonce novo a cada tentativa, inclusive em retentativas** (nonce repetido = replay → 401).

Validação no servidor (ordem real implementada): formato dos cabeçalhos → instalação existe → timestamp dentro de ±120 s → assinatura válida com a chave vigente (ou a anterior, durante a janela de rotação) → **só então** nonce inédito (guardado 5 min), estado revogado/desativado e rate limit por instalação. Antes de a assinatura ser comprovada, toda falha devolve a mesma resposta `401 auth.invalid` (sem revelar se a instalação existe ou está revogada); falhas de relógio acrescentam `details.server_time`. As rejeições são registradas em log estruturado; tentativas **autenticadas** de uso após revogação vão para a trilha de auditoria (`agent.revoked_access_attempt`).

`Content-Length` máximo: 1 MiB (inventário: 4 MiB via `Content-Encoding: gzip` opcional, limite aplicado após descompressão).

## 2. Formato de erro

```json
{ "error": { "code": "auth.invalid", "message": "…", "details": {}, "request_id": "uuid" } }
```

| HTTP | code | Significado | Ação do Agent |
|---|---|---|---|
| 400 | `request.malformed` | JSON inválido | descartar, logar |
| 401 | `auth.invalid` | Assinatura/timestamp/nonce inválidos | corrigir relógio; se persistir, alertar |
| 403 | `agent.revoked` | Instalação revogada | **parar** todas as operações, manter apenas serviço ocioso e log |
| 403 | `endpoint.disabled` | Endpoint desativado pelo administrador | ocioso; tentar novamente em 15 min |
| 403 | `enrollment.invalid` | Token inválido/expirado/esgotado/revogado (resposta idêntica para todos) | abortar instalação |
| 404 | `request.not_found` / 405 `request.method_not_allowed` | Rota/método inexistente | corrigir cliente |
| 409 | `task.conflict` | Tentativa/estado divergente | descartar resultado |
| 413 | `payload.too_large` | Corpo acima do limite | reduzir/chunk |
| 422 | `validation.failed` | Schema inválido (`details` por campo) | corrigir; não repetir igual |
| 426 | `agent.upgrade_required` | Versão do protocolo/agente não suportada | atualizar |
| 429 | `rate.limited` | `Retry-After` | respeitar com jitter |
| 503 | `server.unavailable` | Indisponível | backoff exponencial 5 s → 15 min, jitter ±20 % |

Compatibilidade: mudanças aditivas mantêm `v1`; mudanças incompatíveis criam `v2` e `v1` permanece por ≥ 2 versões de Agent. `GET /api/agent/v1/meta` (não autenticado) retorna `{ "protocol":"v1", "min_agent_version":"…", "server_time":"…" }`.

## 3. Endpoints

### POST /enroll  (não assinado; autenticado pelo token)
Request:
```json
{
  "enrollment_token": "tk_…",
  "public_key": "<base64 SubjectPublicKeyInfo DER, ECDSA P-256>",
  "hostname": "PC-001",
  "os": { "name": "Windows 11 Pro", "version": "10.0.26100", "build": "26100", "arch": "x64" },
  "agent_version": "0.1.0",
  "machine_fingerprint": "sha256-hex"   // dado auxiliar, NÃO é identidade
}
```
Resposta `201`:
```json
{
  "agent_id": "uuid",
  "endpoint_id": "uuid",
  "server_time": "2026-10-14T12:00:00Z",
  "config": { "heartbeat_seconds": 60, "metrics_seconds": 60, "inventory_hours": 24, "max_payload_bytes": 1048576 }
}
```
Erros: `403 enrollment.invalid`, `422`, `429`. Rate limit: 10/min por IP.
Reativação/substituição: token emitido com `replaces_endpoint_id` associa a nova instalação ao endpoint existente e revoga a anterior (auditado).

### POST /heartbeat
```json
{ "uptime_seconds": 12345, "service_state": "running", "agent_version": "0.1.0",
  "current_user": "DOMINIO\\usuario" , "ip_addresses": ["10.0.0.5"],
  "pending_events": 0 }
```
Resposta: `{ "server_time": "…", "config_version": 3, "tasks_available": true, "cancel_task_ids": [], "update_available": null }` — `tasks_available` é real (há tarefa pronta para o claim) e `cancel_task_ids` lista as tarefas `running` com cancelamento pedido (§tasks).

### POST /inventory   (idempotente por `snapshot_id`; corpo ≤ 4 MiB)
Cada seção traz seu estado de coleta (`ok | unavailable | error`); **seções omitidas não alteram nada**. Implementado e testado (`InventoryRequest`): campos fora da lista abaixo são descartados, textos sem caracteres de controle, limites por lista (software ≤ 5000 itens, interfaces ≤ 64, etc.).
```json
{ "snapshot_id": "uuid", "collected_at": "2026-10-14T12:00:00Z",
  "sections": {
    "system":   { "status":"ok", "data": { "hostname":"", "manufacturer":"", "model":"", "serial":"", "os":{"name":"","version":"","build":"","arch":"x64"}, "boot_time":"…", "domain":"", "workgroup":"" } },
    "hardware": { "status":"ok", "data": { "cpus":[{"name":"","cores":10,"threads":12,"max_mhz":1700}], "memory_total_mb":16384,
                  "disks":[{"model":"","serial":"","size_gb":476.9,"interface_type":"","media_type":""}],
                  "volumes":[{"mount":"C:","label":"","file_system":"NTFS","total_gb":475.9,"free_gb":210.4,"used_percent":55.8}],
                  "gpus":[{"name":"","driver_version":"","vram_mb":128}], "baseboard":{"manufacturer":"","product":"","serial":""},
                  "bios":{"vendor":"","version":"","release_date":"2024-03-01"},
                  "not_detected":[{"component":"gpus","reason":"access_denied"}] } },
    "software": { "status":"ok", "data": { "items":[ { "name":"", "version":"", "publisher":"", "install_date":"2025-01-01", "arch":"x64", "source":"HKLM64|HKLM32|HKU" } ], "truncated": false } },
    "network":  { "status":"ok", "data": { "interfaces":[ { "name":"", "description":"", "type":"Ethernet", "mac":"AA:BB:CC:DD:EE:FF", "ips":[""], "up":true, "speed_mbps":1000 } ] } },
    "peripherals": { "status":"ok", "data": { "monitors":[{"name":"","manufacturer":"","serial":"","year":2023}], "printers":[{"name":"","driver":"","port":"","is_default":true,"is_network":false}], "not_detected":[] } }
  } }
```
Semântica no Manager:
- `ok` **substitui** o dado da seção; `unavailable`/`error` **mantém o último dado bom** e registra o estado da coleta (a UI mostra "Indisponível (motivo)" e o último dado válido).
- `not_detected` separa "consultado e não existe" de "não pôde ser consultado".
- Software: item que some de uma coleta **completa** recebe `removed_at`; se reaparecer, volta a ativo. Lista com `truncated: true` nunca marca remoções.
- Rede: a lista de interfaces é substituída por inteiro.
- Resposta `202 { "accepted": true, "snapshot_id": "…" }`; mesmo `snapshot_id` já recebido → `200` sem reprocessar; `snapshot_id` de outro endpoint → `422`.
- Corpo acima do limite → `413 payload.too_large` (antes de ler/hashear o corpo).

### POST /metrics   (lote, ≤ 120 amostras; corpo ≤ 1 MiB)
```json
{ "samples": [ { "sample_id":"uuid", "at":"…", "cpu_percent":12.5, "mem_total_mb":16384, "mem_available_mb":8000,
  "uptime_seconds":1234, "volumes":[{"mount":"C:","total_gb":476,"free_gb":210}], "temperature_c": null } ] }
```
Idempotência por `(endpoint, sample_id)`. Amostras mais antigas que a retenção (7 dias) ou com `at` mais de 10 min no futuro são recusadas **individualmente**. Resposta `202 { "accepted": n, "duplicates": m, "rejected": k }`. Estado de serviços Windows monitorados entra na task 007 (não implementado). `temperature_c` só é enviado quando houver leitura confiável (hoje sempre `null`/omitido).
### POST /events   (lote)
`{ "events":[ { "event_id":"uuid", "at":"…", "level":"error", "source":"System", "code":"7031", "message":"…(≤2 KiB)" } ] }`

### GET /tasks/next?wait=0   (poll curto — ADR-021)
**Sem long-poll**: a resposta é sempre imediata. `wait` é aceito por compatibilidade (inteiro 0–30, senão 422) e ignorado; o Agent envia `wait=0`. A query **entra na assinatura** (`QUERY` = `wait=0`); query diferente da assinada → 401. O Agent consulta a cada `TaskPollSeconds` (padrão 10 s, limites 5–300) e imediatamente quando o heartbeat traz `tasks_available: true`.
Faz *claim* atômico (UPDATE condicional `state = available`) da tarefa disponível mais antiga do endpoint. No claim o Manager reconfere: endpoint ativo, instalação ativa, tarefa não expirada, aprovação presente se alto risco, e se criador/aprovador **ainda** têm as permissões (senão cancela a tarefa com `authorization_revoked` e tenta a próxima). Resposta `200` com **exatamente** 7 campos (`tests/contracts/task-next.json`):
```json
{ "task_id":"uuid","attempt_id":"uuid","type":"service.restart","params":{"name":"Spooler"},
  "timeout_seconds":60,"lease_expires_at":"2026-10-15T12:01:30+00:00","risk":"medium" }
```
`params` é sempre objeto JSON (`{}` quando vazio). Lease = timeout do tipo + 30 s. `204` (sem corpo) se nada disponível. O Agent valida `type` contra o catálogo local e `params` contra o schema local; fora dele → resultado `rejected` sem executar. Tipos e schemas: [`task-types.md`](task-types.md).

### POST /tasks/{task_id}/progress  (opcional, renova lease)
`{ "attempt_id":"…","percent":40 }` (`percent` opcional, 0–100) → `200 { "lease_expires_at":"…" }` (nova lease = agora + timeout + 30 s). Attempt que não é o vigente, já encerrado ou com lease vencida → `409 task.conflict`. Tarefa de outro endpoint/inexistente → `404 request.not_found`. **O Agent atual não envia `progress`** (os timeouts locais cabem na lease).

### POST /tasks/{task_id}/result
```json
{ "attempt_id":"uuid","status":"completed|failed|rejected|cancelled","exit_code":0,
  "stdout":"…","stderr":"…","truncated":false,"started_at":"…","finished_at":"…","duration_ms":1200,"error":null }
```
Resposta `200 { "task_id":"…","state":"completed" }`. Semântica:
- Só o `attempt_id` vigente, com lease válida, é aceito; outro → `409 task.conflict`. Reenvio **idêntico** (status, exit_code, stdout, stderr, error) de um attempt já encerrado → `200` com o estado atual (idempotente); divergente → 409.
- stdout/stderr: aceitos até 64 KiB na validação, sanitizados (UTF-8 válido, sem caracteres de controle exceto `\n`/`\t`) e **cortados em 16 KiB** (fronteira de caractere) com `truncated = true`. `error` ≤ 255 caracteres.
- `completed` → tarefa `completed`; `failed` e `rejected` → `failed`; `cancelled` → `cancelled`. Horários do Agent são informativos (descartados se fora de ±24 h do relógio do servidor); duração e lease usam só o relógio do servidor.
- A auditoria registra só tamanhos da saída, nunca o conteúdo.

### Cancelamento e revogação
Tarefa `available`/`pending_approval` cancelada no painel vai direto a `cancelled`. Em `running`, o painel só grava `cancel_requested_at`; o heartbeat devolve o id em `cancel_task_ids` e o Agent cancela o `CancellationToken` do handler. Revogação (inclusive a feita por novo enrollment) e desativação do endpoint cancelam todas as tarefas abertas; reativar não as restaura. Tarefa não pega em 30 min (`toolit.tasks.pending_ttl_minutes`) expira por `tasks:expire` (agendado a cada minuto), que também fecha leases vencidas.

### Transferências de arquivos (`file.send` / `file.fetch`, task 009)
A tarefa traz só `params.transfer_id`; caminho e metadados vêm destas rotas, todas assinadas e escopadas ao endpoint da instalação. Elas só funcionam com a tarefa `file.*` **desta** transferência em `running` para o endpoint; senão a resposta é `409 transfer.conflict` (tarefa não pega, cancelada, expirada ou já concluída).

| Rota | Corpo / resposta |
|---|---|
| `GET /transfers/{id}` | `200` com exatamente 10 chaves: `transfer_id, endpoint_id, direction, size, sha256, chunk_size, chunks_total, remote_path, overwrite, received_chunks` (contratos `tests/contracts/transfer-metadata.json` e `transfer-metadata-fetch.json`). `endpoint_id` é o mesmo uuid recebido no enroll; o Agent confere endpoint e direção. Na recuperação, antes do `begin`, `size`, `sha256` e `chunks_total` são `null`, e `sha256` é sempre `null`. |
| `GET /transfers/{id}/chunks/{n}` (envio) | `200`, corpo binário `application/octet-stream` + cabeçalho `X-Chunk-Sha256` (hex minúsculo). Chunks de `chunk_size` (2 MiB) exceto o último. |
| `PUT /transfers/{id}/chunks/{n}` (recuperação) | Corpo bruto `application/octet-stream` (senão `415`), ≤ 2,5 MiB (senão `413`, antes da assinatura). `X-Chunk-Sha256` é obrigatório: ausente ou divergente dá `422 transfer.chunk_hash` e nada é gravado. Reenvio idêntico é idempotente. → `200 {"chunks_received": int}` |
| `POST /transfers/{id}/begin` (recuperação) | `{"size": int}` → `200 {"status","chunk_size","chunks_total"}`. Tamanho acima do `max_bytes` da política dá `422 transfer.too_large` e a transferência falha. Repetir com o mesmo tamanho mantém o que já chegou; com outro tamanho, recomeça. |
| `POST /transfers/{id}/complete` | `{"sha256": hex64, "size": int}` → `200 {"status"}`. Hash ou tamanho divergente **não** é erro HTTP: `200 {"status":"failed"}` (bytes descartados). Parte faltando dá `409`. Reenvio idêntico após `completed` é idempotente. |

- Erros: transferência de outro endpoint ou inexistente → `404 request.not_found`; estado ou direção inválidos → `409 transfer.conflict`; `n` fora do intervalo ou tamanho de chunk errado → `422 transfer.invalid`.
- **Chame `complete` ANTES de enviar o `result` da tarefa.** Depois que a tarefa sai de `running`, um novo `complete` recebe `409`.
- Limite próprio `agent-transfer`, por instalação: 600 req/min (`toolit.transfers.rate_limit_per_minute`, env `TOOLIT_TRANSFER_RATE_LIMIT`). Não consome o orçamento de 120 req/min das demais rotas.
- Retomada: no envio o Agent continua do tamanho do `.toolit-{id}.part` (múltiplo de `chunk_size`); na recuperação usa `received_chunks`. Só acontece quando a **mesma** tarefa volta a ser entregue (lease vencida → `tasks:expire` recoloca a idempotente na fila). Agent novo: falha de rede/5xx/timeout durante `file.*` NÃO é reportada (resultado descartado); a lease vence e o Manager recoloca a tarefa idempotente (ADR-023 A; contrato HTTP inalterado). Agent antigo ainda reporta `failed` e o Manager encerra sem nova tentativa (L49).
- Verificado em 2026-10-16 contra Manager real (`docs/test-evidence.md` §"Transferência de arquivos — E2E"): handlers reais em processo e `tests/integration/transfers-smoke.php` (24/24).

### POST /keys/rotate
`{ "new_public_key":"…" }` assinado com a chave atual; a antiga permanece válida por 5 min.

### GET /updates/check?current=0.1.0  → `{ "available":false }` ou `{ "version":"…","url":"…","sha256":"…","signature":"…","mandatory":false }`

## 4. Esquemas de validação
Implementados em `App\Http\Requests\Api\Agent\*`; contratos espelhados em `Toolit.Agent.Contracts`. Testes de contrato em ambos os lados usam os mesmos JSONs em `tests/contracts/`.

## 5. Limites padrão (configuráveis em `system_settings`)
payload 1 MiB (PUT de chunk de transferência: 2,5 MiB) · 120 amostras/lote · heartbeat 60 s · métricas 60 s · inventário 24 h · rate limit 120 req/min por instalação (rotas de transferência: 600 req/min, limite `agent-transfer` separado) · nonce TTL 5 min · chunk do Agent 2 MiB, banda do Agent 4 MiB/s (`TransferBytesPerSecond`).

## 6. Indisponibilidade do servidor
Agent mantém fila local (SQLite, limitada por contagem e bytes; descarte do mais antigo de menor prioridade), reenvia em ordem com backoff exponencial + jitter, e nunca bloqueia coleta. Resultados de tarefas expiradas no servidor são rejeitados com 409 e descartados.
