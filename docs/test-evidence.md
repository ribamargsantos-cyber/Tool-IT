# Evidências de testes

Cada linha é um resultado **observado** (2026-10-14). Ambiente: Windows 11 Home Single Language 10.0.26300, **sessão sem privilégios de Administrador**, PHP 8.4.1, Composer 2.8.10, Laravel 13, PHPUnit 12 (SQLite em memória), .NET SDK 9.0.308 compilando `net8.0`, 12 CPUs lógicas, 36 GB de RAM. **Sem MySQL, Redis ou Laragon.**

## Manager — histórico de mudanças do inventário (2026-10-15)

| Teste | Comando | Resultado |
|---|---|---|
| Suíte do Manager | `cd manager && php artisan test` | **395 aprovados / 2 falhas** (1721 asserções; depois de fixar `APP_URL` no `phpunit.xml`: **397 aprovados / 0 falhas**), SQLite em memória. As 2 falhas (`TrustedProxiesTest` "ignored from anyone else" e `HeadersTest` "security headers are present") já ocorriam **antes** destas mudanças (baseline do mesmo dia: 375 aprovados / 2 falhas) e **não** foram investigadas — ver L28 |
| Diff do inventário | `tests/Unit/InventoryDiffTest` (10) | ignora espaço livre/uso, IP e boot; memória/disco/GPU adicionados, removidos e trocados; teto de 100 entradas |
| Ingestão | `InventoryIngestTest` (+4 novos) | `changes` com diff de memória/SO/rede, nomes de software (teto 50), `initial` na primeira coleta, `changes` nulo sem mudança, seção indisponível não gera diff. Um bug real foi pego pelo teste: nomes saíam como objeto JSON (chaveados por hash) |
| Aba Histórico | `EndpointTabsTest` (+5) | estado vazio, dados reais, escape de HTML, perfil Consulta não vê software/rede, escopo por grupo (404), paginação |
| Estilo | `vendor/bin/pint --test app tests` | limpo |
| **Não verificado** | — | aba Histórico não foi vista no navegador; MySQL (L1) não exercitado; nenhum teste com um Agent real |

## Manager — alertas e administração pela interface (2026-10-14)

| Teste | Comando | Resultado |
|---|---|---|
| Suíte do Manager | `cd manager && php artisan test` | **375 aprovados / 0 falhas** (1620 asserções), SQLite em memória |
| Estilo / vulnerabilidades | `vendor/bin/pint`; `composer audit` | aplicado; nenhuma vulnerabilidade |
| Avaliador de alertas | `AlertEvaluationTest` (24) + `SustainedConditionTest` | duração mínima, histerese, repetição, reconhecimento, resolução, dedupe, escopo, endpoint desativado/removido |
| Telas | `AlertsUiTest`, `AlertRulesUiTest`, `GroupsUiTest`, `UsersUiTest`, `AuditUiTest`, `EndpointManagementTest` | 403/404 por papel e escopo, validação, auditoria dos efeitos, escape de HTML, filtros server-side |
| **E2E com Agent real** (`AgentToolit.exe` publicado + `schedule:work`) | execução manual | alerta de memória abriu **2 min 05 s** após a 1ª amostra (regra de 2 min); alerta *agente offline* abriu **77 s** após o último contato e **resolveu sozinho 47 s** após o retorno; cadeia de auditoria íntegra; reconhecimento pela UI via curl (login + CSRF: 419 sem token, 302 com) |

### Bugs reais encontrados e corrigidos
1. **E2E:** regras curtas nunca abriam — a janela tinha só ~30 s de dados quando a duração era alcançada. `SustainedCondition` passou a tolerar `min(lacuna, duração/4)` no início da janela + testes de regressão.
2. **E2E:** a variável `DB_DATABASE` exportada no shell vazou para o PHPUnit e apagou o banco do E2E → `force="true"` no `phpunit.xml` e recusa no `TestCase` se o banco não for o de teste (verificado).
3. Título de alerta offline "há 1 min (limite 1 min)" → exibe segundos quando < 120 s.
4. **Criação de usuário:** e-mail igual a um existente diferindo só em maiúsculas passava na validação e causava erro 500 (SQLite diferencia maiúsculas; MySQL não) → e-mail normalizado antes de validar.
5. Rotas das telas administrativas sem `use` dos controladores (erro 500 em todos os testes de tela).

## Implantação em Docker — o que foi e o que NÃO foi verificado (2026-10-14)
**Verificado:** `sh -n` nos 3 scripts (entrypoint, init do MySQL, certificado); `compose.yaml` lido por um parser YAML (7 serviços, âncoras/merges resolvidos: `migrate` usa `DB_MIGRATE_*` e a aplicação `DB_*`; só o `web` publica portas); `make-selfsigned.sh` executado de verdade (gera EC P-256 com SAN; no Git Bash do Windows precisa de `MSYS_NO_PATHCONV=1`, que converte `/CN=` — no Linux não); `composer check-platform-reqs --no-dev` lista só extensões já presentes na imagem PHP; `TrustedProxiesTest` (2 testes: cabeçalhos X-Forwarded-* honrados só de proxy confiável); suíte do Manager completa (377 aprovados).
**NÃO verificado:** `docker build` (nenhum Docker disponível), `docker compose up`, `nginx -t`, init do MySQL, *healthchecks*, criação de *triggers* com o usuário de migração, MySQL e Redis reais. Ver L25.

### Revisão visual no navegador (2026-10-14)
Servidor `php artisan serve` com banco SQLite **separado e descartável** (4 computadores, 3 usuários, grupos em árvore, 3 alertas gerados pelo avaliador real; nada disso é dado do projeto). Login com senha + TOTP, depois painel, alertas, regras, grupos, usuários (lista e edição), auditoria (com "Verificar integridade da cadeia": *"Cadeia íntegra: 12 registros verificados"*), computadores e detalhe com "Gerenciar computador". Problemas encontrados e corrigidos:
1. `/alerts` ultrapassava a largura no celular (563 px em 375 px): `<span class="sr-only">` (posição absoluta) escapava do `overflow-x-auto` da tabela → `relative` no `<th>` (também na aba de alertas do computador). Depois: 9 páginas medidas a 375 px, nenhuma com rolagem horizontal.
2. Tempos relativos em inglês ("2 minutes ago") → `Carbon::setLocale('pt_BR')`.
3. "Último acesso: nunca UTC" → só acrescenta UTC quando há data.
4. Campos de data da auditoria espremidos (`dd/mm/…` cortado) → ocupam 2 colunas.
5. Nome clicável (grupos, regras, usuários) parecia texto simples → cor de link.
Observação: a barra de navegação passa para 2 linhas abaixo de ~1000 px (aceitável, sem quebra).

### Não validado
Chrome/Edge/Firefox reais e dispositivo físico (L24); MySQL/Redis (L1); encerramento de sessões com driver diferente de `database` (L20); agendador em produção (precisa de `schedule:run` a cada minuto, L21).

## Fase 2 — Agent + Manager

| Teste | Comando | Resultado |
|---|---|---|
| Suíte do Manager | `cd manager && php artisan test` | **278 aprovados / 0 falhas** (1092 asserções, ≈ 41 s) |
| Suíte do Agent | `cd agent/Toolit && dotnet test Toolit.sln` | **112 aprovados / 0 falhas** (inclui 6 testes de leitura real do Windows e os do instalador de serviço) |
| Vetor de contrato PHP↔C# | `SignatureContractTests` + `ContractVectorTest` | String canônica idêntica byte a byte; **assinatura gerada pelo OpenSSL do PHP verificada no .NET** |
| Vulnerabilidades PHP | `composer audit` | nenhuma |
| Vulnerabilidades .NET | `dotnet list package --vulnerable --include-transitive --source https://api.nuget.org/v3/index.json` | **nenhuma** após trocar o SQLite embutido pelo `winsqlite3` e fixar 2 pacotes transitivos de teste (antes: 3 avisos *High*). A fonte NuGet corporativa do host (`nexus.tce.go.gov.br`) é inacessível — usar a fonte pública para o scan. |
| Estilo | `vendor/bin/pint` | aplicado |

### Teste ponta a ponta com o executável real (sem simulação)
Manager (`php artisan serve` + SQLite temporário) e o `AgentToolit.exe` **publicado** (`dotnet publish -c Release`), modo console, intervalos de 15 s:

| Verificação | Resultado |
|---|---|
| `enroll` com token real | Registrado; segundo uso do mesmo token → recusado; HTTP não-loopback → recusado; segundo `enroll` sem `--force` → recusado |
| Segredos em disco | token: **0 ocorrências** nos arquivos do Agent; `identity.key` é blob DPAPI (cabeçalho `01 00 00 00 D0 8C 9D DF`) |
| Inventário recebido | 5/5 seções `ok`; 109 softwares; 9 interfaces; hardware (CPU, 36 565 MB, 2 volumes), `not_detected` vazio; fabricante/modelo/serial/SO preenchidos no endpoint |
| Métricas | amostras chegando a cada ciclo; CPU, memória e volumes coerentes com a máquina |
| **Queda do Manager (≈ 55 s)** | Agent continuou coletando; log mostra backoff 17 s → 41 s e fila 2 → 4 itens; ao religar, tudo entregue: **11 amostras, 0 lacunas > 25 s, 0 duplicatas** |
| **Revogação com o Agent rodando** | Agent suspendeu coleta e envio em ≈ 1 s, **preservou a fila**, gravou `Revogado=true` (sobrevive a reinício); o Manager não aceitou mais nenhuma amostra (11 → 11 em 40 s); auditoria registrou `agent.revoked` e `agent.revoked_access_attempt`; `audit:verify` íntegro (8 registros) |
| Reinstalação (`enroll --force`) | Nova identidade; fila antiga descartada (não é atribuída à nova instalação) |
| Pacote final sem SQLite embutido | publicação caiu de 28 MB para 3,7 MB; fila funciona com o `winsqlite3` do sistema |

### Consumo de recursos medido (processo real, operação normal: heartbeat/métricas 60 s, inventário no início)
Janela de 120 s em 12 CPUs lógicas: **CPU total 1,36 s** ⇒ média **0,09 % da máquina (1,1 % de um núcleo)**; **working set 66–68 MB**, memória privada ≈ 18 MB, 15 threads, 509 handles. A maior parte da CPU ocorre na coleta inicial do inventário (~1 s). **Não é uma promessa de capacidade:** medido em uma única máquina de desenvolvimento; falta medir em PCs modestos e sob carga (task 013).
Coleta isolada (esta máquina): inventário completo ≈ 1 s e 19 KiB; software (109 itens) ≈ 21 ms; amostra de métricas ≈ 513 ms na primeira (500 ms são a espera deliberada para ter dois pontos de CPU); as seguintes não foram cronometradas isoladamente.

### Bugs reais encontrados pelos testes e corrigidos nesta fase
1. `enroll` aplicava a ACL restrita (SYSTEM/Administradores) a um usuário comum e o **trancava fora do próprio diretório** depois de o token ser consumido → ACL só quando elevado; teste de escrita **antes** de gastar o token; mensagem de recuperação com `agent_id` se o estado local falhar depois do registro.
2. Item corrompido na fila derrubaria o envio com `JsonException` → `TryDeserialize` descarta o item.
3. Backoff inicial (5 s) acelerava as tentativas abaixo do intervalo normal (10 s) → piso no intervalo.
4. `enroll --force` reaproveitaria a fila da instalação anterior → fila limpa no registro.
5. Seção de auditoria do detalhe do endpoint não aparecia (merge de arrays) — pego pelo teste de isolamento.
6. SQLite nativo embutido com aviso de segurança alto → `winsqlite3` do sistema.

### Não validado (honestamente)
- **Serviço Windows real**: `sc create`, início automático, recuperação após falha, reinício do PC, instalação/atualização/desinstalação com o script. A sessão não é Administrador; o script só foi validado em `-DryRun` e nas guardas (HTTPS obrigatório, payload ausente, exige elevação).
- MySQL 8 / MariaDB e Redis (migrations, coluna gerada, triggers `SIGNAL`, `ESCAPE '!'`, filas).
- Concorrência real entre processos (token, snapshot).
- Windows 10, ARM64, PCs sem WMI/privilégios, máquinas com 1000+ softwares ou sem usuário conectado.
- Navegador real (layout das novas abas, SVG do gráfico) — apenas HTML verificado por testes.
- HTTPS com CA corporativa (os testes usaram `http://127.0.0.1`, permitido só em loopback).

## Fase 1 — Manager (histórico)
229 testes aprovados na época (RBAC, auditoria, API do Agent, web); smoke HTTP 7/7; `composer audit`/`npm audit` limpos; `audit:verify` íntegro. Hoje coberto pelos 278 testes acima.

## Instalador (ToolitSetup + serviço) — 2026-10-14
Sessão **sem Administrador**. Tudo abaixo foi observado; a instalação real do serviço **não** foi executada.

| Verificação | Resultado |
|---|---|
| Agent: `service install/uninstall` | 12 testes novos (sequência de `sc.exe`, `create` vs `config`, tolerância a 1056/1062/1060, falha de passo, timeout até `RUNNING`, validação do caminho, desinstalação); `sc.exe query` **real** de serviço inexistente detectado sem Administrador. Suíte do Agent: **112 aprovados / 0 falhas** |
| Guardas do comando no `.exe` publicado | sem Administrador → código 3; sem token / token fora do formato `tk_…` (`abc; calc.exe`) → código 2; verbo desconhecido → 2; `--dry-run` lista os comandos `sc.exe` sem executar; o token **não** aparece em `install.log` |
| Build do MSI headless (`devenv.com … /Project ToolitSetup\ToolitSetup.vdproj`) | 4 projetos OK; evento pós-build `Patch-ToolitMsi: OK (8 ações, diálogo com 12 controles)`; `ToolitSetup.msi` ≈ 33 MB |
| Tabelas do MSI final (lidas por script independente) | `Template=x64;1046`; `TARGETDIR=[ProgramFiles64Folder][Manufacturer]\[ProductName]`; 229 arquivos (runtime autossuficiente, `winsqlite3`); condição “.NET instalado” removida; 4 pares de ações (imediata + deferida/rollback) com tipos 3170/3362/3106 e sequências 3450/3950/4050/4100; fluxo Folder → **ToolitConfigForm** → Confirm; `SecureCustomProperties` e `MsiHiddenProperties` com o token; tabela Upgrade intacta |
| Idempotência do patch | executado 2× sobre o mesmo MSI → mesmo resultado, verificação interna OK |
| Extração real do pacote (`msiexec /a … /qn`, sem elevação) | código 0; 230 arquivos; o `AgentToolit.exe` extraído roda (`--version` = 0.1.0) **sem .NET instalado como dependência** e executa `service install --dry-run` |
| Varredura de pacotes .NET | nenhum pacote vulnerável nos 4 projetos (a solução não pode mais ser varrida de uma vez por causa do `.vdproj`: L18) |

### Não validado
`msiexec /i` de verdade (UI e silencioso), criação/início do serviço pelo SCM, recuperação automática após falha, reinício do PC, atualização para versão maior, desinstalação, rollback, GPO/`.mst`, comportamento do enroll sob a conta SYSTEM, e a aparência real do diálogo (só as tabelas foram inspecionadas).

## Tarefas remotas — painel (Tarefas, Aprovações, detalhe) — 2026-10-15
| Verificação | Resultado |
|---|---|
| `TasksUiTest` (Manager) | 26 testes / 244 asserções: aba só com `tasks.view`, formulário só com tipos permitidos, idempotência (mesma chave em outro endpoint/tipo é recusada), 13 parâmetros hostis → erro de validação sem criar tarefa, criador sem botão Aprovar e POST forçado vira erro, aprovação por outro admin auditada, BOLA (grupo A × grupo B → 404 em criar/ver/cancelar/aprovar/rejeitar), operador não aprova (403), stdout/params com HTML escapados, sem PowerShell/arquivos/tela na UI, paginação 20. Suíte completa do Manager: **571 aprovados / 0 falhas** |
| Navegador embutido (SQLite descartável com dados fictícios, apagado depois) | Vistos: Aprovações, aba Tarefas (formulários, lista, 2ª página), detalhe com stdout `<script>` (0 `<script>` no DOM, texto literal). 375 px sem overflow da página (tabela rola dentro do cartão). **Ressalva:** `npm run build` falha neste ambiente (rollup nativo); o CSS compilado é antigo e sem algumas classes novas, então o layout foi visto com o Tailwind CDN v3 injetado como aproximação. Refazer a conferência com o build real. |

## Tarefas remotas — E2E com Agent real (2026-10-15)
Executado em 2026-10-15 22:52–23:13 (horário local, UTC−4; 02:52–03:13 UTC de 16/10). Mesmo ambiente do topo (sessão **sem Administrador**).
**Montagem:** `AgentToolit.exe` **publicado** (`dotnet publish Toolit\AgentToolit.csproj -c Release -o <temp local>`), modo console (sem serviço), `TOOLIT_AGENT_DATA` em pasta temporária, `enroll --server http://127.0.0.1:8765` com token novo (`toolit:enrollment-token --user=<operador>`). Manager: `php artisan serve --port=8765` com `APP_ENV=e2etest` → arquivo `.env.e2etest` temporário (SQLite descartável `e2e-test.sqlite`, MFA de admin desligado só nesse processo; `manager/.env` intocado). Usuários de teste: 2 Admins (A, B) e 1 Operador TI, senhas geradas localmente. Tarefas criadas/aprovadas pelo `TaskService` (o mesmo que o painel chama) e, nos casos marcados, pelo **painel via HTTP** (login Fortify + CSRF). Primeira metade com o Manager lendo o código do compartilhamento `Z:` (2–37 s por requisição); depois de uma cópia local do mesmo commit (`robocopy`, sem `.env`): 0,2–1 s por requisição. Tudo apagado no fim (banco, cópia, `.env.e2etest`, publicação, dados do Agent); nenhum `php`/`AgentToolit`/`PING` ficou rodando e a porta 8765 ficou sem listener.

| Cenário | Resultado observado |
|---|---|
| Poll curto | `GET /tasks/next?wait=0` a cada **10–11 s** (log do servidor: 23:10:06, :16, :26, :37, :47) |
| (a) leitura, criada pelo Operador | `system.info` → `completed` em **8 s** (Manager em `Z:`) e **2 s / 11 s** (local); `process.list` **9 s** e **11 s** (16 291 bytes, 148 de 425 processos, `truncated` interno da lista = true); `service.list` **11 s** (16 365 bytes); `inventory.refresh` criada pelo painel HTTP → `completed` e inventário reenviado. Saída real (hostname, SO 10.0.26300, 12 CPUs). Latência = `created_at` → estado final no banco |
| (b) `process.kill` de `ping.exe` descartável (PID 9308) criado pelo Operador | nasceu `pending_approval`; Operador tentando aprovar → `AuthorizationException` (sem `tasks.approve`; 403 no painel); não foi entregue em 20 s pendente; Admin B aprovou → `completed` **15 s** após a aprovação (`Processo PING (PID 9308) encerrado.`), `tasklist` não acha mais o PID. Repetido com o Manager local (PID 16664, `name: "PING.EXE"`, aprovado pelo Admin A): **9 s**, processo encerrado |
| Separação de funções | Admin A criou e tentou aprovar a própria tarefa → `Quem criou a tarefa não pode aprová-la nem rejeitá-la.` |
| Guarda de PID reutilizado | `{pid: 16664, name: "notepad"}` aprovado → `rejected` `pid reuse: pid 16664 is now 'PING'`; processo intacto |
| (c) processo protegido | `{pid: <ping descartável 16664>, name: "lsass"}` criado pelo Admin A, aprovado pelo Admin B → `rejected` (`protected process`) **2 s** após a aprovação; o `ping` continuou rodando. **Nenhum PID real de `lsass`/`csrss`/`winlogon` foi usado** |
| (d) parâmetros hostis pelo painel HTTP (`Accept: application/json`) | 10 casos → **422** com o campo certo e nenhuma tarefa criada: tipo `shell.exec`, `powershell.run`, PID `4`, `1234; calc.exe`, `-1`, nome de processo com `\n`, serviço `Spooler & calc`, `WinDefend` (protegido), nome em array, sem `idempotency_key`. **Exceção:** `service.stop` com `"Spooler\n"` foi aparado pelo `TrimStrings` do Laravel para `Spooler` (allowlist, risco médio, sem aprovação) e **criou** a tarefa — ver incidente abaixo |
| Cancelamento de tarefa disponível | `system.info` criada e cancelada na sequência → `cancelled`; 15 s depois nenhuma tentativa (nunca entregue) |
| (e) revogação pelo painel HTTP | `POST /endpoints/{uuid}/revoke` (Operador) → tarefa pendente cancelada com `reason: agent_revoked`; instalação `revoked`; o Agent fez **uma** última consulta (403), registrou "tarefas suspensas" e não fez mais nenhuma requisição nos 67 s seguintes; `state.json` com `Revoked: true` |
| `tests/integration/tasks-smoke.php` (simulador independente do protocolo) | **14/14 PASS**: 204 sem tarefa, query diferente da assinada → 401, sem assinatura → 401, entrega com exatamente os 7 campos, não entrega duas vezes (204), attempt alheio → 409 `task.conflict`, resultado → 200 `completed`, reenvio idêntico → 200, divergente → 409, `progress` após o fim → 409, heartbeat `tasks_available=false`/`cancel_task_ids=[]` |
| Auditoria | `audit:verify`: *Cadeia íntegra: 65 registros*; `task.created`=16, `task.approved`=6, `task.claimed`=14, `task.completed`=9, `task.failed`=5, `task.cancelled`=2; metadados com tamanho da saída (`stdout_bytes`), sem conteúdo |

### Achados do E2E
1. **Resposta de `next` perdida (L37):** com o Manager lento em `Z:`, um `GET /tasks/next` levou 32 s; o Agent desistiu em 30 s, mas o servidor concluiu o claim. O `process.kill` aprovado (alvo: `ping` descartável) ficou `running` sem executor e virou `failed` (`lease_expired`) só quando `tasks:expire` foi rodado à mão (135 s após a criação). Comportamento seguro (não executa duas vezes), mas a tarefa se perde. Não corrigido: decisão de projeto (ADR-021), registrado.
2. **Incidente de teste (não é defeito do código):** o caso hostil `"Spooler\n"` virou um `service.stop Spooler` **real**, entregue ao Agent em 5 s, que falhou com *"Cannot open 'Spooler' service on computer '.'. Acesso negado."* (sessão sem Administrador). `sc query Spooler` depois: `RUNNING`. O aparo de espaços nas pontas é do Laravel e torna o nome válido; o catálogo recusa o mesmo valor quando chamado direto. Os cenários seguintes não usaram nomes de serviços reais. Este teste violou a regra do E2E de não tocar em serviços reais (só falhou porque a sessão não tinha Administrador); E2E de entrada hostil nunca deve usar nomes de serviços reais da lista permitida.
3. Nenhum defeito de código encontrado; nenhuma correção necessária.

### Não executado / não validado
- `process.kill` contra o `lsass` (ou `csrss`/`winlogon`) **real**: proibido aqui (um defeito derrubaria a máquina) — fazer em VM.
- `service.start|stop|restart` com sucesso em serviço real e `agent.restart` (recuperação pelo SCM, código 75): só *fakes* (L32, L36, L8).
- Cancelamento de tarefa **em execução** via `cancel_task_ids` e `progress`: só testes automatizados (as tarefas reais terminam em < 1 s; o Agent não envia `progress`) — L41.
- Corrida de dois consumidores no claim em MySQL (L30); agendador real (`tasks:expire` foi rodado manualmente).

### Suítes e varreduras (2026-10-15, depois do E2E)
| Verificação | Resultado |
|---|---|
| `cd manager && php artisan test` | **571 aprovados / 0 falhas** (2578 asserções, 351 s, no compartilhamento `Z:`) |
| Agent (`dotnet build` em `Z:` + `dotnet test <cópia local>/AgentToolit.Tests.dll` com `tests/contracts` copiado — L39) | **149 aprovados / 0 falhas** |
| `composer audit` | nenhuma vulnerabilidade |
| `dotnet list <projeto> package --vulnerable --include-transitive --source https://api.nuget.org/v3/index.json` (4 projetos, um por vez — L18) | nenhum pacote vulnerável |
| `php vendor/bin/pint --test` (Manager e `tests/integration/tasks-smoke.php`) | passou |

## Transferência de arquivos — E2E (2026-10-16)
Executado em 2026-10-16, 09:12–09:27 e 13:40–13:50 (horário local, UTC−4; a sessão foi interrompida entre os dois blocos: Manager e harness caíram, o banco de teste ficou; na volta as sessões do painel tinham expirado — `419` — e foi feito novo login). Sessão **sem Administrador**, Windows 11 (10.0.26300), PHP 8.4.1, .NET 8.0.22.

**Montagem (tudo descartável, apagado no fim):** cópia local do `manager/` (`robocopy`, **sem** `.env`) servida por `php artisan serve --host=127.0.0.1 --port=8791`; configuração só por variáveis de ambiente do processo (SQLite `e2e-test.sqlite` no diretório temporário, `SESSION_DRIVER=database`, `QUEUE_CONNECTION=sync`, MFA de admin **ligado**); `manager/.env` intocado. Banco: `migrate --seed` + script que criou grupos A e B, usuários de teste (Admin com TOTP, Operador/Auditor/Suporte do grupo A, Operador do grupo B; senhas geradas, nunca impressas) e tokens de registro do grupo A. "Navegador" = cliente HTTP real (cURL com cookies + CSRF, login Fortify; Admin passou pelo desafio TOTP). Pastas: só sob o `%TEMP%` da sessão (`…\e2e\h\root\Inbox|Outbox`, `…\e2e\h\outside` com um canário, `…\e2e\h\fora-da-raiz`); nenhum diretório do sistema, serviço real ou nome real de serviço/caminho do sistema foi usado. `C:\ProgramData\Toolit` foi listado antes e depois: inalterado (só a pasta `Agent` de 14/10).

**Política pela tela** (Admin, `POST /settings/file-policies`, grupo A): 9 diretórios hostis num pedido → **422** com um erro por linha (`C:\Windows`, `C:\`, `C:\Users`, `C:\Program Files (x86)\App`, `…\root\..\x`, `…\CON`, `\\servidor\share`, `…\a:b`, bidi U+202E); `exe` em extensões → **422**; política válida → **302** (send: Inbox, `Inbox\junction-out`, `fora-da-raiz`; fetch: Outbox, `fora-da-raiz`; `txt log zip pdf`; 100 MiB; sem sobrescrita).

### 1) `AgentToolit.exe` publicado, modo console (falha fechada)
`dotnet publish Toolit\AgentToolit.csproj -c Release -o <temp>\pub` (10 s, 75 MB) · `enroll --server http://127.0.0.1:8791` (token via `TOOLIT_ENROLLMENT_TOKEN`) · `config.json` editado para `TransferRoots` = duas pastas temporárias e `TransferFetchOnlyRoots = []` antes de iniciar (o padrão apontaria para `%ProgramData%`).
- Log na subida: `Pasta de transferência …\Inbox excluída: gravável por dono não confiável (BOOK-…\ribam) (ACL não aplicada: processo sem privilégio)` (idem Outbox) → `0 raiz(es) de envio, 0 de recuperação`.
- `file.send` (345 000 B, criado e enviado pelo painel HTTP) → `rejected` "Transferências desligadas neste computador" 13 s após a criação; `file.fetch` → `rejected` em 1 s. Transferências `failed`; Inbox vazio. Endpoint revogado pelo painel; o Agent registrou "instalação revogada; tarefas suspensas".

### 2) Handlers reais em processo, HTTP real, disco real, Manager real
`AgentToolit.Tests/TransferE2ETests.cs` (ignorado sem `TOOLIT_E2E_MANAGER`): `FileSendHandler`/`FileFetchHandler`, `TaskRunner`, `TaskPoller` e `ManagerClient` reais (assinatura ECDSA de verdade, enroll com token real), rodando dentro do `testhost` a partir de uma cópia local do `bin/` (L39). O teste primeiro prova que **sem** o seam as raízes temporárias são excluídas (`envio=0 recuperação=0`, asserção) e só então as aceita via `TrustedRootsForTests`; planta junctions descartáveis (`mklink /J` por `ArgumentList`) `Inbox\junction-out` e `Outbox\junction-out` → `…\outside` e as remove sem recursão no fim; banda `TOOLIT_E2E_BPS=1048576` (1 MiB/s) para dar tempo de interromper. **Não é o executável do serviço.**

| Cenário | Resultado observado |
|---|---|
| Envio > 5 MiB pelo navegador, retomável | `pacote-e2e.zip` 7 940 032 B (8 chunks de 1 MiB): PUT 5, 2, 0 fora de ordem; 2 repetido igual → 200 (idempotente); 2 com outro conteúdo → **409** `transfer.conflict`; n=8 → **422** `transfer.invalid`; "interrupção": `GET …/upload` → `received_chunks [0,2,5]`; concluir cedo → **422** `transfer.incomplete` `missing_chunks [1,3,4,6,7]`; resto enviado → concluir **200** `ready`, `sha256 efaa5486…f292` = `sha256sum` local. Cada PUT 430–860 ms. Agent pegou em 1 s; `completed` em **10 s** (handler 13:25:11→13:25:20, 4 chunks de 2 MiB a 1 MiB/s); arquivo no Inbox com o **mesmo SHA-256** |
| Escopo no upload | Operador do grupo B → **404**; Suporte A → **403**; Auditor A consultando o status → **403**; Suporte A cancelando o rascunho → **403** "Só quem iniciou o envio…" |
| Recuperação + download | `relatório final (v2).log` 6 020 657 B → `completed` em **8 s** (3 chunks; `begin` + PUTs com `X-Chunk-Sha256`), `sha256 4842acce…1b3c` = origem. Download (Operador A, 616 ms; e `curl -D -` com o mesmo cookie): `200`, `Content-Type: application/octet-stream`, `Content-Disposition: attachment; filename="relat_rio final (v2).log"; filename*=UTF-8''relat%C3%B3rio%20final%20%28v2%29.log`, `X-Content-Type-Options: nosniff`, `Cache-Control: no-store, private`, sem `ETag`/`Last-Modified`; SHA-256 igual. Auditor A → **403**, Operador B → **404**, Suporte A → 200 |
| Entradas hostis no Manager (`neg.php`, 28 casos sintéticos) | Todas **422**, nenhuma transferência criada: nome com `..\`, subnome `..`, ADS `notas.txt:oculto.txt`, `CON.txt`, `com1.log`, `ferramenta.exe`, `relatorio.pdf.exe`, `a.EXE`, `a.exe.`, `"a.exe "` (o `TrimStrings` apara para `a.exe`, que é bloqueado), bidi U+202E, zero-width U+200B, homógrafo U+FF0F, `RELATO~1.TXT`, `.docx` fora da política, nome só `.txt`, dir com `..`, subpasta não exata, dir fora da política, UNC, tamanho 104 857 601; recuperação com `..`, ADS, `NUL.txt`, UNC, fora da política, relativo, bidi |
| Permissão (pedido válido) | Envio: Auditor A **403**, Suporte A **403**, Operador B **404**. Recuperação: Auditor A **403**, Operador B **404**, Suporte A **201** (falhou depois: "Arquivo de origem: não existe.") |
| Veto local do Agent (Manager aceita, Agent recusa) | Envio para `fora-da-raiz` (na política, fora das `TransferRoots`) → `rejected` "O caminho está fora das pastas permitidas neste computador."; envio para `Inbox\junction-out` → `rejected` "O caminho passa por um link (junction/symlink)…"; recuperação de `Outbox\junction-out\segredo.txt` → `rejected` (nenhum byte do canário chegou ao Manager: nenhuma pasta criada em `storage/…/transfers/<uuid>`); recuperação de `fora-da-raiz\fora.log` → `rejected`. `outside` terminou só com o canário (asserção do teste) e `fora-da-raiz` só com o arquivo colocado pelo teste |
| Linhas forjadas direto no banco (simulando bug/comprometimento do Manager) | `Inbox\..\escape.txt`, `Inbox\nota.txt:ads`, `Inbox\CON.txt`, `Inbox\ferramenta.exe`: o Manager revalida em `GET /transfers/{id}` → `409` → o Agent encerra como `cancelled`, nada gravado. `Inbox\junction-out\via-link.txt` (passa na revalidação sintática) → o Agent recusa (`rejected`, link). A recusa **sintática** dentro do Agent foi exercida só pelos testes de unidade com o vetor compartilhado (o Manager não deixa esses caminhos chegarem lá) |
| Interrupção do Agent e retomada | `retomada-e2e.zip` 20 975 841 B (upload de 21 chunks em 6,9 s). Com o `.part` em 12 MiB o `testhost` foi morto (`taskkill /F`); `.part` ficou com 12 582 912 B (múltiplo de 2 MiB). **Atalho de teste:** `lease_expires_at` vencido no banco + `php artisan tasks:expire` → `task.requeued` (sem o atalho seriam ~15 min). Harness reiniciado: tentativa #2 pediu do **chunk 6** em diante (log do servidor: chunks 0–5 às 13:43:46–:55, 6–10 às 13:44:23–:30), `completed` em 9 s, SHA-256 `6167af4e…b0b6` igual ao local |
| Queda do Manager no meio | `queda-manager.zip` 16 MiB; com o `.part` em 4 MiB o `php artisan serve` foi derrubado. O Agent: "Manager inacessível" → resultado `failed` guardado; Manager reiniciado → resultado entregue → tarefa `failed` (1 tentativa) e transferência **`failed`**, sem nova tentativa; `.toolit-<id>.part` de 4 194 304 B **ficou** no Inbox. **Achado (L49):** não há retomada após falha de rede |
| Revogação no meio | `revogacao-e2e.zip` 16 MiB; com o `.part` em 4 MiB, `POST /endpoints/{uuid}/revoke` pelo Operador A. Em < 2 s o Agent recebeu `AgentRevokedException`, apagou o `.part` (pelo handle), marcou-se revogado e o laço parou; transferência e tarefa `cancelled` (`agent_revoked`) |
| Retenção | **Atalho de teste:** `updated_at` das 21 transferências finais recuado 8 dias no banco; `transfers:prune` → "21 apagados"; `storage/…/transfers` vazio, 21/21 com `purged_at`; download de uma recuperação concluída passou a **409**. (`--days=0` é recusado: mínimo 1 dia) |
| Auditoria | `audit:verify`: *Cadeia íntegra: 150 registros*. `transfer.created`=16, `upload_completed`=9, `completed`=6, `failed`=9, `cancelled`=6, `downloaded`=4, `transfer_policy.created`=1, `task.requeued`=1 |

### 3) `tests/integration/transfers-smoke.php` (simulador independente do protocolo, PHP)
`php tests/integration/transfers-smoke.php http://127.0.0.1:8791 <token>` com o envio e a recuperação criados pelo painel HTTP: **24/24 PASS** — `params` só `{transfer_id}`; sem assinatura → 401; metadados com exatamente as 10 chaves, `endpoint_id`/direção/chunk 2 MiB; uuid alheio → 404 `request.not_found`; n = `chunks_total` → 422; **130 GETs de chunk em 29,4 s, todos 200, nenhum 429** (acima dos 120/min de `agent-api`; limite `agent-transfer` 600/min); `X-Chunk-Sha256` confere em todas; SHA-256 do arquivo = metadados; `complete` → `completed`; `result` → 200; depois disso a transferência dá 409; recuperação: metadados nulos antes do `begin`, `begin` 200, PUT com hash errado → 422 `transfer.chunk_hash`, sem hash → 4xx, PUT 1, 0, 1 (fora de ordem/repetido) → 200, `complete` → `completed`; o arquivo baixado depois pelo painel tem o SHA-256 enviado. (A primeira execução abortou num erro do próprio script — argumentos do PUT trocados —, corrigido antes do commit.)

**Limpeza:** processos `php`, `AgentToolit`, `testhost` encerrados (nenhum restante; porta 8791 sem listener); junctions removidas pelo teste (nenhum reparse point restante, conferido por `Get-ChildItem -Attributes ReparsePoint`); apagados o banco, a cópia do Manager (incl. `storage/…/transfers`), a publicação, as pastas do Agent e as credenciais/cookies de teste.

### Não executado / não validado
- **Executável do serviço transferindo** e **ACL restrita aplicada como Administrador/SYSTEM** (`TransferAcl.ApplyRestricted`, `RestrictParent`): sessão sem elevação — o exe real só provou a falha fechada (L43).
- Compartilhamentos UNC reais (sempre recusados neste ciclo) e antivírus real interferindo no `.part`/rename (L43).
- Retomada da **recuperação** após matar o Agent (só testes de unidade); cancelamento pelo operador de uma transferência **em andamento** (só a revogação foi exercida ao vivo; cancelamento coberto por testes); destino existente com mesmo hash / sobrescrita (testes de unidade).
- Navegador real na aba Arquivos (o JS de fatiar/retomar não rodou num navegador; L50); nginx/Apache/PHP-FPM com os limites de corpo (só `artisan serve`).
- Agendador real (`tasks:expire` e `transfers:prune` rodados à mão; lease e `updated_at` adiantados por atalho no banco de teste).

### Suítes e varreduras (2026-10-16, depois do E2E)
| Verificação | Resultado |
|---|---|
| `cd manager && php artisan test` | **1060 aprovados / 0 falhas** (5143 asserções; 740 s e, na branch nova, 1141 s — no compartilhamento `Z:`) |
| Agent (`dotnet build` em `Z:` + `dotnet test <cópia local>/AgentToolit.Tests.dll` com `tests/contracts` — L39) | **278 aprovados / 0 falhas / 1 ignorado** (o E2E, sem `TOOLIT_E2E_MANAGER`) |
| `node --test manager/tests/js/transfer-upload.test.mjs` (Node 24.5) | **18/18** |
| `composer audit` | nenhuma vulnerabilidade |
| `dotnet list <projeto> package --vulnerable --include-transitive --source https://api.nuget.org/v3/index.json` (Core, Collectors, AgentToolit, Tests) | nenhum pacote vulnerável |
| `php vendor/bin/pint --test` (Manager e `tests/integration/transfers-smoke.php`) | passou |

## RGS-8 — retomada/falha transitória (L49) — 2026-10-16
**Status: NÃO EXECUTADO.** O ambiente de desenvolvimento desta entrega não tem `dotnet`, `php` nem Windows; nenhum teste foi rodado, então nada está marcado `[x]`.
Testes novos (C#, `AgentToolit.Tests/TransferResumeTests.cs`): runner marca `ManagerUnavailableException`/timeout de rede como transitório; erro permanente continua `failed`; poller não envia resultado em falha transitória; envio retoma do `.part` e conclui com hash correto; hash divergente continua `failed` sem `.part`; `PartSweeper` apaga só `.part` antigo de nome estrito e pula `.part` aberto.
A executar por quem tiver Windows/.NET: `dotnet test agent/Toolit/Toolit.sln` (contagem antes/depois a registrar aqui) e repetir o E2E de queda do Manager (`queda-manager.zip`).
Manager (PHP): nenhuma alteração nesta entrega (ADR-023 A não exige); A2 (lease curta por progresso) não feito — depende do board.
