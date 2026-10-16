# Spec — Inventário de arquivos por política (task 006)

**Estado: PROPOSTA de design (RGS-7). Nada aqui está implementado nem aprovado.** Nenhum código de produção foi alterado. Decisões marcadas **[BOARD]** dependem de aprovação humana (seção 13). Contexto: `docs/tasks/006-inventario-hardware-software.md` (item "Política de inventário de arquivos — não iniciada"), `docs/threat-model.md` T4/T9/T14/T19, ADR-015, ADR-016, ADR-021, ADR-022, `docs/api/path-guard.md`, `docs/api/task-types.md`, `docs/permissions-matrix.md`.
ADRs associados (propostos): ADR-029 (modelo de execução e privacidade do inventário de arquivos).

## 1. Objetivo
Registrar **metadados** (caminho, nome, tamanho, datas, atributos e, opcionalmente, SHA-256) de arquivos dentro de **diretórios explicitamente autorizados**, para responder "o que existe nestas pastas e o que mudou", sem ler conteúdo para o Manager, sem varrer discos e com custo limitado no endpoint.

Não-objetivos: ler/transferir conteúdo (task 009); varredura de unidade inteira; busca de conteúdo; antivírus/DLP; monitoramento em tempo real de alterações (`FileSystemWatcher`); inventário de pastas de perfil de usuário por padrão (seção 8).

**Finalidade [BOARD]** — a documentação do projeto não declara o caso de uso. Hipóteses a confirmar: (a) achar instaladores/executáveis portáteis fora de `Program Files` (software não inventariado pelo Registro); (b) verificar presença/versão de arquivos de configuração e artefatos corporativos em pastas conhecidas; (c) conferir integridade (hash) de arquivos de uma pasta corporativa. LGPD exige finalidade determinada; **sem finalidade aprovada não há implementação** (decisão 1).

## 2. Estado atual (evidências, HEAD `83e9e3d`)
- Tabelas `file_inventory_policies/items` e o coletor `FileInventoryCollector` **não existem** (`docs/data-model.md`: "pendente"). Permissão `files.inventory_view` já existe na matriz (Admin, Operador, Suporte, Auditor).
- Reaproveitável da task 009 (implementada e validada nos limites descritos em `test-evidence.md`/L43–L44): `PathGuard` (PHP `manager/app/Support/Transfers/PathGuard.php` e C# `AgentToolit.Core/Transfers/PathGuard.cs`) com vetor `tests/contracts/path-guard-cases.json` — `ValidateDirectory`, `IsInside`, `forbiddenPolicyDirectory`, `OpenChecked` (recusa reparse point e hard link, confere caminho final do handle), `Throttle`, `DiskSpace`; política por grupo herdada pela árvore (`file_transfer_policies`, tela `/settings/file-policies`); veto local `AgentSettings.TransferRoots`; tarefas tipadas com `progress`/lease (ADR-021).
- Limites já existentes para manter: payload do Agent 1 MiB (inventário 4 MiB gzip), rate limit 120 req/min por instalação, fila SQLite local limitada, `Throttle` de banda, cancelamento de tarefa em execução via heartbeat (até ~60 s, L41).
- Lições registradas que o design evita repetir: L26 (remoção falsa por leitura parcial), L47 (remoção de grupo apaga política sem auditoria), L49 (queda transitória marca transferência `failed`), L46 (crescimento de disco sem monitoramento), L40 (sem retenção).
- O serviço roda como **SYSTEM**: ele consegue ler praticamente qualquer arquivo do disco, inclusive de todos os usuários. Por isso o veto local e a lista de exclusão fixa são controles de privacidade, não detalhe.

## 3. Modelo de ameaça / privacidade (T4, T14, T19)

| # | Risco | Controle |
|---|---|---|
| F1 | Política abusiva do Manager (Manager/operador comprometido) varre tudo | Política efetiva = política do Manager **∩ raízes locais** (`FileInventoryRoots`, padrão **vazio = desligado**) **∩** lista fixa de exclusão no código; raízes validadas por `PathGuard.ValidateDirectory` + `forbiddenPolicyDirectory` (sem raiz de unidade, `Windows*`, `Program Files*`, `X:\Users` inteiro) |
| F2 | Nomes e caminhos revelam dados pessoais (ex.: `Laudo_João.pdf`, nome do usuário no caminho) | Minimização: só metadados; sem proprietário, ACL nem conteúdo; pastas de perfil e dados de aplicativos fora por padrão; `files.inventory_view` para ver; auditoria de acesso (decisão 3); retenção curta; RIPD/aviso (decisão 2) |
| F3 | Hash serve para confirmar posse de arquivo específico / vazar | Hash só por regra explícita (extensão + tamanho máximo) e só de arquivos dentro das raízes; hash nunca vem de fora; sem busca global por hash na v1 (decisão 4) |
| F4 | Agent comprometido envia lixo/volume enorme | Limites no Manager: itens por lote e por execução, tamanhos de string, caracteres de controle, raízes só as da revisão da política; rejeição, nunca truncar silenciosamente; escape na tela (T14) |
| F5 | Varredura derruba o PC (CPU/disco) | Prioridade de thread/E/S de fundo, `Throttle` de entradas/s, orçamento de tempo e de itens, uma varredura por vez, nunca em unidades de rede/removíveis |
| F6 | Symlink/junction/loop leva a fora da raiz | Não seguir reparse points (listados só como contagem `skipped_reparse`); `IsInside` por diretório; profundidade limitada |
| F7 | Hidratação de arquivos em nuvem (OneDrive Files-On-Demand) dispara download | Ignorar hash e leitura de arquivos com `RECALL_ON_DATA_ACCESS`/`OFFLINE`; só metadados |
| F8 | Uso como vigilância (T19) | Escopo restrito, sem leitura de conteúdo, sem monitoramento contínuo, sem busca por nome de pessoa, aviso transparente, auditoria |

## 4. Arquitetura proposta

```
 Manager                                         Agent (SYSTEM)
 file_inventory_policies (por grupo, revisão)    GET /file-inventory/policy   (assinado)
        │ heartbeat: file_inventory{revision,    ─────────────►  política efetiva ∩ FileInventoryRoots ∩ exclusões fixas
        │            interval_hours}                              │
        │ task inventory.files (sob demanda)                      ▼
        ▼                                           FileInventoryCollector (BFS manual, orçamentos, cancelável)
 POST   /file-inventory/runs            ◄───────────  abre execução (run_id gerado pelo Agent, idempotente)
 PUT    /file-inventory/runs/{id}/batches/{n} ◄────  lotes ≤ 1 000 itens / ≤ 512 KiB (gzip), idempotentes por n
 POST   /file-inventory/runs/{id}/complete  ◄─────   contagens, status, raízes completas
        │ aplica (transação): upsert de itens, removed_at p/ raízes completas
        ▼
 file_inventory_runs / _items / telas / auditoria / retenção
```

Decisões-chave:
1. **Política viaja ao Agent por consulta assinada** (`GET /file-inventory/policy`), não por parâmetros de tarefa. Diferente da 009, onde a política ficava só no Manager e o Agent tinha apenas o veto local, aqui o Agent precisa saber *o que varrer*; por isso a política vira dado confiável-mas-limitado (interseção com o veto local, ADR-029).
2. **Duas formas de disparar:** agendada (intervalo da política; **padrão desligado**, mín. 24 h; dica `file_inventory` no heartbeat com `revision`) e sob demanda por tarefa tipada `inventory.files` (sem parâmetros; o Agent usa a política efetiva vigente).
3. **Resultado aplicado só no `complete`** (staging por lote → transação), com semântica de remoção cuidadosa (seção 6.3) para não repetir L26.
4. **Reuso do PathGuard com ajuste:** para *validar raízes* e *confinar* a varredura, uso integral. Para *nomes de arquivo encontrados no disco*, **não** se aplica `ValidateFileName` como filtro (recusaria nomes legítimos pela lista de caracteres permitidos): nomes são **dados não confiáveis** reportados, normalizados para NFC, com caracteres Cc/Cf/Zl/Zp e homógrafos de separador substituídos por `U+FFFD` e a flag `name_flags` (`control`, `homoglyph`, `reserved`, `ads_like`, `too_long`). Nunca se abre um arquivo a partir de nome vindo do Manager.
5. **Sem leitura de conteúdo para fora:** o conteúdo só é lido localmente, e apenas para o hash das regras; nunca sai do endpoint.

## 5. Modelo de dados (Manager)

| Tabela | Colunas-chave | Regras |
|---|---|---|
| `file_inventory_policies` | uuid, `endpoint_group_id` (nulo = global), `scope_key` (`global`/`group:<id>`), `enabled`, `revision` (int, incrementa a cada alteração), `roots` (json: lista de `{id, path, max_depth, include_extensions[]?, exclude_names[], exclude_extensions[]}`), `hash_rules` (json: `{extensions[], max_file_bytes, run_budget_bytes}`), `interval_hours` (0 = só sob demanda), `max_items`, `time_budget_seconds`, `updated_by`, timestamps | única por `scope_key`; política efetiva = a do grupo mais próximo na árvore (igual à 009); **sem política = desligado**; remoção do grupo **bloqueada** se houver política (corrige o padrão de L47) ou auditada |
| `file_inventory_runs` | uuid (gerado pelo Agent), endpoint_id (FK cascade), `policy_id`, `policy_revision`, `trigger` (`scheduled`/`task`), `remote_task_id` nulo, `state` (`running`,`completed`,`partial`,`cancelled`,`failed`), `reason` (código estável), contagens (`files_seen`, `dirs_scanned`, `denied`, `skipped_reparse`, `skipped_excluded`, `skipped_too_long`, `hashed`, `hash_skipped`), `roots_status` (json: por `root_id` `complete`/`partial`/`missing`/`denied`), `started_at`, `finished_at`, `truncated_reason` | índice `(endpoint_id, started_at)`; no máximo 1 `running` por endpoint (expira em `time_budget + 10 min`) |
| `file_inventory_items` | endpoint_id (FK cascade), `root_id` (int da revisão), `identity_hash` (sha256 de `root_id|rel_path_nfc_casefold_ascii`), `rel_path` (≤ 600 chars, relativo à raiz), `name` (≤ 255), `ext` (≤ 16), `size` (bigint), `mtime` (utc), `attrs` (bitmask: hidden, system, readonly, offline, compressed, encrypted), `name_flags`, `sha256` (nulo), `hash_state` (`none`,`ok`,`skipped_size`,`skipped_offline`,`error`), `first_seen_at`, `last_seen_at`, `last_run_id`, `missed_count`, `removed_at` | único `(endpoint_id, identity_hash)`; índices `(endpoint_id, removed_at)`, `(endpoint_id, ext)`; `sha256` indexável só se a busca global for aprovada (decisão 4) |
| `file_inventory_changes` (opcional na v1) | run_id, tipo (`added`,`removed`,`modified`), identity_hash, antes/depois (size, mtime, sha256), limitado a 200 por execução | histórico no estilo do `inventory_snapshots.changes` (L27: guarda diff, não snapshot completo) |

Volume: teto proposto de **50 000 itens ativos por endpoint** (`max_items` ≤ 50 000) → frota de 100 máquinas ≤ 5 milhões de linhas (**estimativa, a medir**). Retenção: itens com `removed_at` apagados após 30 dias (`file_inventory:prune`, agendador — L21); execuções por 90 dias; `toolit.file_inventory.retention_days` configurável. Staging dos lotes em `storage/app/private/file-inventory/{run}/` apagado ao aplicar/expirar (monitorar disco, L46).

**Política (validação no Manager, tela `/settings/file-inventory-policies`, `settings.manage` global, auditada):** cada raiz precisa passar `PathGuard::validateDirectory` e `forbiddenPolicyDirectory`; UNC recusado; `max_depth` 1–8; `max_items` ≤ 50 000; `interval_hours` 0 ou 24–720; extensões de hash ≤ 20, sem extensões vazias; `hash_rules.max_file_bytes` ≤ 256 MiB; `run_budget_bytes` ≤ 2 GiB; nomes de exclusão sem separadores. Qualquer alteração incrementa `revision` e gera `file_inventory_policy.created|updated|deleted`. Valores iniciais sugeridos ao criar: política **desabilitada**, sem raízes.

## 6. Contratos de API

### 6.1 Heartbeat (aditivo)
Resposta do heartbeat ganha `file_inventory: {"revision": n, "interval_hours": h}` (ausente quando desligado). O Agent compara com a revisão local e, se diferente, chama 6.2.

### 6.2 `GET /api/agent/v1/file-inventory/policy`
Assinado. `200 {"revision": n, "enabled": true, "interval_hours": 24, "max_items": 20000, "time_budget_seconds": 600, "roots": [{"id":1,"path":"C:\\Dados\\Doc","max_depth":4,"include_extensions":[],"exclude_names":["tmp","node_modules"],"exclude_extensions":["tmp"]}], "hash_rules": {"extensions":["exe","dll"],"max_file_bytes":67108864,"run_budget_bytes":536870912}}`. Sem política: `200 {"enabled": false}`. O Agent trata tudo como **não confiável**: revalida cada raiz com `PathGuard`, intersecta com `FileInventoryRoots`, aplica os mínimos/máximos locais (`Math.Clamp`, como `AgentSettings`).

### 6.3 Execução
- `POST /file-inventory/runs` `{"run_id": uuid, "policy_revision": n, "trigger": "scheduled|task", "task_id": uuid|null, "started_at": iso}` → `201`; repetição idempotente por `run_id`; revisão mais antiga que a atual → `409 file_inventory.stale_policy` (o Agent busca a política de novo).
- `PUT /file-inventory/runs/{id}/batches/{n}` corpo gzip JSON `{"items":[{"root":1,"path":"sub\\a.dll","name":"a.dll","size":123,"mtime":"2026-10-01T10:00:00Z","attrs":0,"flags":[],"sha256":null,"hash_state":"none"}]}` ≤ 1 000 itens e ≤ 512 KiB após descompressão; idempotente por `n` (substitui); rejeita item com `root` fora da revisão, strings acima do limite, controle, `size` negativo, `sha256` sem regra → `422 file_inventory.invalid`. Rate limit próprio `agent-file-inventory` (≈ 120 req/min por instalação; **valor inicial a validar**), separado do orçamento de 120 req/min geral.
- `POST /file-inventory/runs/{id}/complete` `{"status":"completed|partial|cancelled|failed","reason":null|"code","roots":[{"id":1,"state":"complete|partial|missing|denied"}],"counts":{…},"batches":N}`. Efeito: `completed`/`partial` → aplica (transação única): `INSERT … ON DUPLICATE KEY UPDATE` dos itens vistos (atualiza `last_seen_at`, zera `missed_count`/`removed_at`, grava mudanças); **ausência só conta para raízes `complete`**: itens daquela raiz não vistos na execução recebem `missed_count+1` e **`removed_at` apenas quando `missed_count ≥ 2`** em execuções `completed` consecutivas (cumpre a regra "não visto em 2 coletas" da task 006 e fecha L26 para este módulo). `partial`: só upsert, nunca remoção. `cancelled`/`failed`: descarta o staging, nada é aplicado. Lote faltando → `409`.
- Erros do Agent: sem lote perdido (`n` contínuo), retomada por `GET /file-inventory/runs/{id}` (lotes recebidos). **Queda transitória de rede não deve marcar a execução como `failed`** (lição de L49): o Agent mantém o estado local e continua no próximo ciclo; só falha definitiva (política inválida, raiz negada) reporta `failed`.
- Qualquer rota: execução de outro endpoint ou inexistente → `404 request.not_found` (BOLA).

### 6.4 Tarefa `inventory.files`
Entra no catálogo (`TaskCatalog` e `LocalTaskCatalog`, espelho): risco **low** (somente leitura de metadados; mesma lógica de `inventory.refresh`), permissões `tasks.create` + `files.inventory_view` (Auditor não cria tarefa — já garantido pela matriz), idempotente, 3 tentativas, timeout = `time_budget + 120 s`, **sem parâmetros** (`{}`), lista fechada de criação (só o botão da tela). **[BOARD]** confirmar risco `low` (alternativa: `medium` por expor nomes de arquivos). Sem política efetiva/raízes locais ⇒ o Agent responde `rejected` com motivo (`no_policy`/`no_local_roots`). Cancelamento: tarefa vira `cancelled`; o Agent recebe em até ~60 s (heartbeat, L41) e cancela o `CancellationToken` — **a varredura confere o token a cada diretório e a cada lote**; o orçamento de tempo local limita o pior caso.

### 6.5 Leitura (painel)
`GET /endpoints/{uuid}/file-inventory` (web, `files.inventory_view` + escopo; fora do escopo 404, sem permissão 403): última execução (estado, contagens, política/revisão), lista paginada (filtros: raiz, extensão, tamanho, alterado desde, removido) e mudanças da última execução. Escapar tudo (T14); `Cache-Control: no-store`. Exportar CSV **não** entra na v1 (decisão 4).

## 7. Agent — coletor e limites

`AgentToolit.Collectors/Inventory/FileInventoryCollector.cs` + `AgentToolit.Core/FileInventory/*` (política, exclusões fixas, orçamentos, cliente de lotes). Algoritmo:
1. Política efetiva = Manager ∩ `FileInventoryRoots` (padrão **vazio**; configurado em `config.json`/GPO/MSI) ∩ exclusões fixas. Raízes: `PathGuard.ValidateDirectory` + `IsInside` + `forbiddenPolicyDirectory`; conferir existência/dono/DACL como `TransferRootRegistry` (L44: mesma limitação TOCTOU conhecida) — raiz que falha é excluída e reportada `denied`.
2. **Varredura em largura manual** (sem `SearchOption.AllDirectories`): `EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = ReparsePoint, ReturnSpecialDirectories = false }` ou `FindFirstFileEx` com `FindExInfoBasic` + `FIND_FIRST_EX_LARGE_FETCH`; profundidade ≤ `max_depth`; pilha/fila limitada; nenhum componente existente que seja reparse point é seguido; diretório novo é confirmado dentro da raiz antes de descer.
3. **Metadados sem abrir o arquivo** (os dados vêm da enumeração; nenhum `CreateFile` para arquivos sem regra de hash).
4. **Hash (opcional):** só se `ext ∈ hash_rules.extensions`, `size ≤ max_file_bytes`, orçamento total não esgotado, arquivo sem `OFFLINE/RECALL_*`; abrir por `PathGuard.OpenChecked(Read)` (recusa reparse point e hard link, confere caminho final) com `FileShare.ReadWrite|Delete` e prioridade de E/S baixa; `Throttle` de bytes/s; arquivo em uso/negado ⇒ `hash_state=error`, a varredura continua. O conteúdo é lido só localmente.
5. **Orçamentos (padrões a medir, não metas validadas):** `time_budget_seconds` 600, `max_items` 20 000, `max_depth` ≤ 8, ≤ 2 000 entradas/s, prioridade de thread `BelowNormal` + modo de fundo de E/S (`THREAD_MODE_BACKGROUND_BEGIN`), um scan por vez (mutex), não iniciar scan agendado nos 5 min iniciais após o boot. Estourou orçamento ⇒ status `partial` com `truncated_reason` (`time`, `items`, `hash_budget`), raiz `partial`.
6. **Exclusões fixas no código** (nunca configuráveis): `\Windows\`, `\Program Files*`, `\ProgramData\Microsoft\Crypto\`, `\$Recycle.Bin`, `\System Volume Information`, `\AppData\`, `NTUSER*`, `\.ssh\`, `\.gnupg\`, perfis de navegador (Chrome/Edge/Firefox `User Data`/`Profiles`), `\Microsoft\Credentials\`, `\Microsoft\Protect\`, arquivos `*.pst`/`*.ost` (conteúdo de e-mail; decisão 5 pode liberar o nome). Pastas conhecidas de perfil (Desktop, Documentos, Imagens) **excluídas por padrão** mesmo se a raiz as incluir (decisão 5).
7. **Unidades:** só `DriveType.Fixed`; nunca rede/removível/CD.
8. **Envio:** lotes via `ManagerClient` (reuso da assinatura e do backoff); itens não passam pela fila SQLite (volume); estado mínimo local (`file-inventory-state.json`: `run_id`, `next_batch`, `last_completed`) para retomar.
9. **Saídas/logs:** o log local contém contagens e códigos, **nunca** caminhos completos de arquivos (nem o `output` da tarefa).
10. Falhas por diretório (acesso negado, caminho > 260, nome inválido) são **contadas** e a varredura segue; erro de infraestrutura (disco indisponível) encerra com `failed` + código.

## 8. Privacidade e LGPD (resumo para decisão)
- Dados tratados: caminho, nome, tamanho, datas, atributos e (opcional) hash de arquivos em pastas autorizadas. Nomes e caminhos **podem ser dados pessoais** (e raramente sensíveis, art. 5º II) mesmo sem ler conteúdo. Base legal e finalidade a registrar (decisão 1); relatório de impacto/RIPD recomendado antes do piloto (decisão 2).
- Controles técnicos propostos: padrão desligado (política + veto local), pastas de perfil fora, sem proprietário/ACL/conteúdo, exibição por permissão e escopo, auditoria de políticas e execuções, retenção de 30 d (removidos) / 90 d (execuções), aviso aos usuários dos endpoints (texto em política interna; sem notificação intrusiva no PC), direito de eliminação: apagar endpoint apaga em cascata; endpoint reinstalado preserva só o que a política permitir.
- Auditoria de **leitura** (`file_inventory.viewed` quando alguém abre a lista): recomendada por tratar dado potencialmente pessoal; gera volume — decisão 3.

## 9. Permissões e auditoria
- Ver inventário de arquivos: `files.inventory_view` (já existe; Admin, Operador, Suporte, Auditor). **[BOARD]** se Suporte/Auditor devem ver hashes e caminhos completos (alternativa: separar `files.inventory_view_sensitive`, como `inventory.view_sensitive` do software).
- Disparar varredura: `tasks.create` + `files.inventory_view`. Política: `settings.manage` global (mesma tela-padrão da 009).
- Auditoria: `file_inventory_policy.created|updated|deleted`, `file_inventory.run_started|completed|partial|cancelled|failed` (sem listar arquivos), `file_inventory.viewed` (se aprovado), `file_inventory.task_created`.

## 10. Fatiamento em entregas incrementais (ordem e dependências)

| # | Entrega | Depende de | Validável sem VM Windows? |
|---|---|---|---|
| F0 | Decisões do board (seção 13): finalidade, RIPD, exclusões, auditoria de leitura | — | — |
| F1 | Contrato: `tests/contracts/file-inventory.json` (política, lote, complete) + `docs/api/file-inventory.md`; atualizar `docs/data-model.md`, matriz de permissões | F0 | Sim |
| F2 | Manager: migrations, `FileInventoryPolicy` (herança por grupo), tela de política com `PathGuard`, auditoria, bloqueio de remoção de grupo, testes | F1; 009 (PathGuard PHP) | Sim (SQLite; MySQL: L1) |
| F3 | Manager: ingestão (`runs`/`batches`/`complete`), semântica `missed_count ≥ 2`, limites, BOLA, rate limit, retenção `file_inventory:prune`, testes de volume (50 000 itens) | F1 | Sim; **desempenho/índices em MySQL real não validados (L1)** |
| F4 | Agent: coletor com `IFileSystem`/`IFileEnumerator` abstraído; exclusões fixas, orçamentos, cancelamento, BFS, normalização de nomes; testes em diretório temporário criado pelo teste (inclui junction descartável, nomes hostis, > 260, ADS-like) | F1; 009 (PathGuard C#) | **Sim no host Windows de desenvolvimento** (junction, atributos). Linux: só lógica pura |
| F5 | Agent: hash seletivo (`OpenChecked`, `Throttle`), arquivos `OFFLINE`, em uso; envio em lotes e retomada | F4 | Parcial: `OFFLINE`/OneDrive e desempenho real exigem máquinas reais |
| F6 | Tarefa `inventory.files` (catálogo nos dois lados), cancelamento, agendamento por `revision`, interseção com `FileInventoryRoots`; teste de cancelamento no meio da varredura | F3, F4 (+ correção de L49 recomendada antes) | Sim (processo de teste); serviço real: VM |
| F7 | Painel: seção "Inventário de arquivos" (lista, filtros, mudanças, última execução, estados vazios sem dados fictícios), revisão no navegador | F3 | Sim (revisão em navegador real: L24/L38/L50) |
| F8 | E2E: Agent real × Manager local com pastas descartáveis; medição de custo (tempo, CPU, memória, tamanho de payload) em ≥ 1 máquina; piloto | F5–F7 + VM | **Não para serviço/SYSTEM, ACL elevada, Windows 10, ARM64, perfis reais** (L8, L43, L6) |

F2 e F3 (Manager) correm em paralelo com F4 (Agent) depois de F1. F6 une os dois lados.

### Histórias sugeridas (com critério de aceite)
1. *Como Admin, defino quais pastas podem ser inventariadas* — raiz inválida (`C:\`, `C:\Windows`, UNC, `..`, relativa, > 260) → 422 com motivo; alteração incrementa `revision` e audita; sem política = "desligado".
2. *Como Agent, nunca varro fora do permitido* — raiz fora de `FileInventoryRoots` ignorada e reportada; junction/symlink dentro da raiz não seguido; `max_depth` respeitado; exclusões fixas valem mesmo se a política as listar.
3. *Como Operador, vejo o inventário e o que mudou* — itens, filtros e removidos visíveis só com `files.inventory_view` no escopo (BOLA 404); nomes hostis escapados.
4. *Como Operador, cancelo uma varredura* — cancelar a tarefa interrompe o Agent em ≤ 1 ciclo de heartbeat + 1 diretório; execução `cancelled` não altera itens.
5. *Como Admin, controlo o custo* — estourar tempo/itens produz `partial` sem marcar remoções; medição de CPU/tempo registrada.
6. *Como DPO, garanto minimização* — nenhum conteúdo, proprietário ou ACL trafega; hash só para regras; retenção apaga removidos com 30 d.

## 11. Estratégia de testes
- **Manager (Pest):** herança de política por grupo; validação de raízes com o vetor `path-guard-cases.json`; tabela de payloads maliciosos (controle, strings gigantes, `root` inexistente, `sha256` sem regra, lote repetido/fora de ordem); semântica de remoção (1 execução ausente ≠ removido; 2 ausentes `completed` = removido; `partial` nunca remove; raiz `partial` não remove); BOLA e permissões × papéis; retenção; rate limit; 50 000 itens em tempo razoável (medido, registrado).
- **Agent (xUnit):** coletor sobre diretório temporário: profundidade, exclusões, nomes hostis (ADS-like, reservado, > 260, Unicode, controle), junction descartável, cancelamento, orçamentos, hash (arquivo grande, em uso, `OFFLINE` simulado via interface), conteúdo **não** lido sem regra (contagem de `CreateFile` com `IFileSystem` falso), interseção política ∩ local.
- **Contrato JSON compartilhado** em `tests/contracts/file-inventory.json`.
- **E2E (como o da 009):** só diretórios descartáveis criados pelo teste; nenhum teste varre diretórios reais do sistema/usuário.
- **Fora do alcance até haver VM:** serviço instalado como SYSTEM lendo árvores com ACL restritiva; antivírus interferindo na leitura para hash; OneDrive Files-On-Demand real; Windows 10 antigo e ARM64; medição em PCs modestos (L9).

## 12. Riscos
- **Finalidade indefinida (maior risco):** implementar sem finalidade/RIPD cria passivo LGPD. Mitigação: F0 bloqueia o resto.
- **Custo no endpoint** desconhecido (só uma máquina medida, L9): orçamentos conservadores e agendamento desligado por padrão.
- **Volume no Manager:** 5 M linhas e retenção: validar índices em MySQL; limites por endpoint.
- **TOCTOU residual do PathGuard (L44)** vale também aqui (somente leitura: impacto menor).
- **Cancelamento lento (L41)** e **L49** (falha transitória): dependências do mecanismo de tarefas; recomendo tratar L49 antes de F6.
- **SYSTEM lê tudo:** só o veto local e as exclusões fixas impedem excesso; revisar a lista fixa com segurança/DPO.

## 13. Decisões que o board precisa tomar
1. **Finalidade e base legal** do inventário de arquivos (quais perguntas ele responde). Sem isso, não implementar.
2. **RIPD / aviso aos usuários** e responsável (DPO/encarregado); texto da política interna.
3. **Auditoria de leitura** (`file_inventory.viewed`) e quem pode ver caminhos/hashes (`files.inventory_view_sensitive`?).
4. **Busca global por hash/nome entre endpoints e exportação CSV:** fora da v1 por risco de vigilância; aprovar ou manter fora.
5. **Pastas de perfil** (Desktop/Documentos/Downloads) e `*.pst`: mantidas excluídas por padrão (recomendado) ou liberadas por raiz explícita?
6. **Veto local obrigatório com padrão vazio** (recomendado, exige configurar `config.json`/GPO) × padrão com pasta dedicada como a 009.
7. **Risco da tarefa `inventory.files`:** `low` (proposto) ou `medium`.
8. **Retenção:** 30 d (removidos) / 90 d (execuções) são propostas.

## 14. Critérios de aceite do design (RGS-7)
- Compreensível isoladamente; histórias da seção 10 com critérios de aceite.
- Riscos de segurança/privacidade e itens não validáveis sem VM explicitados (seções 3, 10, 11, 12).
- Nenhum código de produção alterado.
