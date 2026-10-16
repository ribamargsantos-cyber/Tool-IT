# Registro de decisões arquiteturais (ADR)

Formato: contexto → decisão → consequências. Novas decisões entram no fim; decisões substituídas são marcadas, não apagadas.

## ADR-001 — Laravel 13 / PHP 8.3+ (dev: 8.4)
**Contexto:** exigência de versão estável e suportada. Laravel 13 é a versão estável corrente; requer PHP ≥ 8.3 (host tem 8.4.1).
**Nota operacional:** o Composer global deste host tem `platform.php=8.1` forçado, o que faz `create-project` resolver Laravel 10 (EOL). O scaffold foi feito com `COMPOSER_HOME` isolado e o `composer.json` do projeto fixa `config.platform.php`.
**Consequências:** testes com PHPUnit 12 (padrão do skeleton).

## ADR-002 — Banco: MySQL 8.0+ (ou MariaDB 10.6+) em produção — decisão do dono do projeto
**Contexto:** o dono do projeto definiu MySQL como banco alvo, executado em Laragon (Windows). Substitui a proposta inicial de PostgreSQL.
**Decisão:** MySQL 8.0+ / InnoDB, `utf8mb4`, `utf8mb4_unicode_ci`. SQLite em memória para testes rápidos. As migrations usam apenas recursos portáveis entre MySQL e SQLite:
- unicidade "no máximo 1 instalação ativa por endpoint" via **coluna gerada** (`virtualAs`) + índice único (MySQL não tem índice parcial);
- `audit_logs` imutável via **triggers** `BEFORE UPDATE/DELETE` (MySQL: `SIGNAL SQLSTATE '45000'`; SQLite: `RAISE(ABORT)`), criados por migration condicional ao driver;
- claim de tarefas por `UPDATE … WHERE status='available'` atômico (não depende de `SKIP LOCKED`);
- JSON nativo (`json`), datas `datetime(6)` onde precisão importa.
**Consequências:** a suíte deve ser executada contra MySQL real antes de produção (task 013). Nenhum MySQL/Laragon existe no host de desenvolvimento atual, então **a validação em MySQL está pendente** (ver `known-limitations.md`); o usuário do app em produção não deve ter `UPDATE/DELETE` em `audit_logs`, e a migration exige privilégio `TRIGGER`.

## ADR-013 — Hospedagem do Manager no Laragon (Windows)
**Contexto:** o Manager rodará no Laragon (Apache ou Nginx + PHP + MySQL).
**Decisão/consequências:** (1) PHP ≥ 8.3 com extensões `openssl`, `pdo_mysql`, `sodium`, `intl`, `mbstring`, `curl`, `zip`; (2) HTTPS obrigatório para os Agents — usar certificado de CA interna (Laragon gera certificado autoassinado para dev; **o Agent não aceita certificado não confiável**, então a CA deve ser instalada nos endpoints, p.ex. por GPO); (3) `queue:work`, `schedule:run` e `reverb:start` como processos persistentes do Windows (NSSM ou Agendador de Tarefas), documentados em `docs/deployment/server.md`; (4) MySQL com senha no `root`, `bind-address=127.0.0.1` se app e banco estiverem na mesma máquina, usuário dedicado da aplicação; (5) Laragon é voltado a desenvolvimento: para produção, rodar Apache/Nginx e MySQL como serviços do Windows, firewall liberando apenas 443 (e porta do Reverb se separada), backups agendados do MySQL (`mysqldump`/`mysqlpump`). Reverb funciona no Windows, mas deve ser validado sob carga (task 010/013).

## ADR-003 — Redis para fila, cache, sessão e nonces (produção); banco/array em dev e testes
**Contexto:** o ambiente Laragon do dono do projeto terá Redis e Memcached disponíveis (a escala de ~100 endpoints não os exigiria, mas já existem).
**Decisão:** `QUEUE_CONNECTION=redis`, `CACHE_STORE=redis`, `SESSION_DRIVER=redis` em produção. Redis também serve de store para: nonces anti-replay (`Cache::add`, atômico), rate limiting e Laravel Reverb (escala horizontal opcional). **Memcached não é usado**: manter um único store reduz peças a operar (Memcached não oferece filas nem persistência e o Laravel não o usa para `queue`). Cliente: extensão `phpredis` se disponível no PHP do Laragon, senão `predis/predis`.
**Regras:** Redis com senha e `bind 127.0.0.1`; persistência (AOF ou RDB) habilitada para não perder jobs; **nada que precise ser durável vive só no Redis** — estado de tarefas, sessões remotas e auditoria ficam no MySQL (Redis é transporte/cache). Perda do Redis não pode corromper estado: leases expiram e tarefas voltam a `available`.
**Dev/testes neste host:** sem servidor Redis nem extensão `phpredis`; usar `database`/`array` via `.env`/`phpunit.xml`. Validar com Redis real é pendência (task 013).

## ADR-004 — Autenticação do Agent: chave assimétrica por instalação + requests assinados
**Alternativas:** (a) mTLS com certificados de cliente; (b) bearer token de longa duração; (c) token de curta duração obtido por desafio; (d) **assinatura por request com ECDSA P-256**.
**Decisão:** (d). Chave privada nunca sai do endpoint (DPAPI máquina). Servidor guarda só a chave pública. Cada request leva `X-Agent-Id`, `X-Timestamp`, `X-Nonce`, `X-Signature` sobre `METHOD\nPATH\nQUERY\nTS\nNONCE\nSHA256(body)`.
**Por quê não mTLS agora:** exige PKI interna, distribuição e renovação de certificados e terminação TLS configurada para pedir certificado de cliente (complexidade operacional alta para a escala, especialmente atrás de proxy). Pode ser adicionado depois como camada extra sem alterar o protocolo.
**Por quê não bearer longo:** segredo reutilizável interceptável/vazável; revogação depende de lista.
**Consequências:** replay mitigado por janela de ±120 s + nonce único (cache); revogação imediata; rotação de chave via endpoint assinado; sem segredos em instalador (apenas token de enrollment de uso único por máquina). Implementação .NET: `ECDsa` com `DSASignatureFormat.Rfc3279DerSequence` (compatível com `openssl_verify`).

## ADR-005 — Enrollment por token de uso único
Token de 256 bits, mostrado uma única vez, armazenado como SHA-256, expira (padrão 24 h), limite de usos (padrão 1), vinculado a grupo de endpoints e criador. Instalador recebe o token por parâmetro (GPO pode usar tokens de múltiplos usos com limite e validade curta, explicitamente criados com permissão `enrollment.create_multi`).

## ADR-006 — RBAC próprio com escopo por grupo
**Alternativa:** spatie/laravel-permission. **Decisão:** modelo próprio mínimo (`roles`, `permissions`, `role_permission`, `role_assignments` com `endpoint_group_id` nulo = global) porque o escopo por grupo é requisito central e a lib não o modela nativamente. Autorização via `Gate::before`-free: `AccessResolver::can($user, $permission, ?Endpoint)`; Policies a consomem. Permissões são constantes em `App\Enums\Permission`.

## ADR-007 — Autenticação do operador: Laravel Fortify + TOTP
Fortify (headless) fornece login, 2FA TOTP, códigos de recuperação, confirmação de senha. Telas em Blade. MFA obrigatório para Administrador (configurável em `system_settings`).

## ADR-008 — Tela remota: capturas periódicas via helper de sessão; somente leitura
Ver `architecture.md` §6. Reavaliar para streaming contínuo se medições mostrarem necessidade.

## ADR-009 — Auditoria encadeada
`audit_logs` é append-only: sem `updated_at`, bloqueio de UPDATE/DELETE por *trigger* (MySQL e SQLite) e hash encadeado (`prev_hash`, `hash` = SHA-256 do registro canônico + prev). A sequência (`seq`) é atribuída pela aplicação sob lock na linha única de `audit_chain` (evita lacunas do auto-incremento do InnoDB em rollback). Verificação com `php artisan audit:verify`. Retenção só via comando privilegiado que arquiva por intervalo e registra a própria remoção.

## ADR-010 — .NET para o Agent
Alvo desejado: **.NET 10 (LTS)**. O host só tem SDK 9.0.308 (STS) e runtimes 8 e 10; o SDK 9 não compila `net10.0`. .NET 8 (LTS) sai de suporte em 10/11/2026. **Decisão provisória:** alvo `net8.0` até instalar o SDK 10, com `TargetFramework` centralizado em `Directory.Build.props` para migrar com uma linha. Registrado como pendência em task 005.

## ADR-011 — Fila local do Agent: SQLite
Arquivo em `%ProgramData%\Toolit\Agent\queue.db`, ACL restrita a SYSTEM/Administrators, limite de linhas/bytes, descarte do mais antigo por prioridade.

## ADR-012 — Atualização do Agent assinada
Pacote ZIP + manifesto assinado (ECDSA P-256 ou Authenticode no executável). Chave pública de verificação embutida no Agent; chave privada de release fora do servidor Manager. Rollback automático se o novo serviço não reportar saúde em N minutos.

## ADR-014 — Layout real do Agent e SQLite do sistema (Fase 2)
**Contexto:** o dono do projeto iniciou o Agent com o template *Worker Service* do Visual Studio em `agent/Toolit/` (solução `Toolit.sln`, projeto `AgentToolit`, `net8.0`). Preservamos essa estrutura em vez de recriá-la.
**Decisão:** `agent/Toolit/{Toolit (host/serviço), AgentToolit.Core, AgentToolit.Collectors, AgentToolit.Tests}` + `Directory.Build.props` (TFM central, ADR-010). Core = protocolo, identidade, cliente HTTP, fila, logs, serviços de heartbeat/envio; Collectors = Windows (WMI, Registro, P/Invoke). A lógica fica em serviços testáveis; os `BackgroundService` são cascas finas.
**SQLite:** a fila local usa `Microsoft.Data.Sqlite.Core` + `SQLitePCLRaw.bundle_winsqlite3` (o `winsqlite3.dll` do Windows). Motivo: o SQLite nativo embutido (`e_sqlite3`) está sujeito a aviso de severidade alta (GHSA-2m69-gcr7-jv3q) mesmo na última versão do pacote; o do sistema é atualizado pelo Windows Update e reduz o pacote de 28 MB para ~4 MB. Requer Windows 10 1607+.

## ADR-015 — Semântica do inventário
Estado **atual** em `endpoint_inventories` (JSON por seção) + histórico enxuto (`inventory_snapshots` só com metadados). Seção `ok` substitui; `unavailable`/`error` preserva o último dado bom e registra o estado da coleta (a UI nunca mostra "vazio" quando o problema foi a coleta). Software em tabela própria com `removed_at` (remoção só concluída em coleta completa). Dados do equipamento são não confiáveis: validação estrita no Manager, campos desconhecidos descartados, escape na UI.
**Privacidade:** `inventory.view_sensitive` separa software completo, IPs/MACs e usuário logado do perfil Consulta.

## ADR-016 — Coleta leve (sem WMI desnecessário)
Métricas frequentes sem WMI nem contadores de desempenho localizados (`GetSystemTimes`, `GlobalMemoryStatusEx`, `DriveInfo`). WMI apenas no inventário (consultas pontuais, propriedades explícitas, timeout de 10 s por consulta e 30 s por seção). Software pelo Registro (nunca `Win32_Product`, que dispara reconfiguração MSI). Temperatura/sensores: não coletados (sem fonte confiável) — o campo existe e vai vazio.

## ADR-017 — Instalador: MSI do Visual Studio + serviço instalado pelo próprio Agent
**Contexto:** o dono do projeto criou `agent/Toolit/ToolitSetup` (Visual Studio Installer Project, `.vdproj`) e quer que ele instale o `AgentToolit` como serviço. Esse tipo de projeto só cria serviços via classes `Installer` do .NET Framework (inexistente no .NET 8) e não expõe tabela `ServiceInstall`.
**Alternativas:** (a) migrar para WiX (`ServiceInstall` nativo) — troca a ferramenta que o dono escolheu; (b) editar o `.vdproj` à mão para ações personalizadas — o formato interno não é documentado e arriscava corromper o projeto; (c) **o Agent instala a si mesmo** (`service install/uninstall`) + o MSI recebe ações personalizadas por um passo pós-build.
**Decisão:** (c). `Patch-ToolitMsi.ps1` (evento pós-build, API oficial `WindowsInstaller.Installer`) adiciona ao MSI: diálogo de URL/token, propriedades públicas `SERVERURL`/`ENROLLTOKEN`, ações deferidas como SYSTEM e a verificação das tabelas (o build falha se algo faltar). A lógica de serviço (sc.exe, recuperação, registro, idempotência) fica em C#, testada por unidade, e é a mesma usada pelo script `Install-ToolitAgent.ps1`.
**Pacote:** x64, autossuficiente (não exige .NET nos PCs), TargetPlatform x64 (`ProgramFiles64Folder`), condição de “.NET instalado” removida. Custo: ≈ 33 MB e o runtime não é atualizado pelo Windows Update (reempacotar o Agent a cada correção do .NET — task 012).
**Cuidados de upgrade:** `RemoveExistingProducts` do vdproj roda *depois* de instalar a versão nova; por isso a desinstalação do serviço exige `NOT UPGRADINGPRODUCTCODE`. O rollback não remove o serviço em atualização.
**Riscos:** o token passa pela linha de comando do instalador (mascarado em log verboso do MSI); MSI sem assinatura; o `.vdproj` só compila no Visual Studio/`devenv.com` com a extensão Installer Projects.

## ADR-018 — Alertas: avaliação no servidor, estado por regra+endpoint
**Decisão:** o Agent só envia métricas; quem decide é o Manager (`alerts:evaluate` a cada minuto). Cada regra tem limite, **limite de recuperação** (histerese), duração mínima, duração de recuperação e intervalo de repetição. A condição é "sustentada" quando as amostras da janela satisfazem o limite sem lacuna maior que 150 s (`toolit.alerts.gap_seconds`); no início da janela tolera-se `min(lacuna, duração/4)` (sem isso, regras curtas nunca abriam — bug achado no E2E). O alerta ativo é único por (regra, endpoint, volume) via coluna virtual `active_dedupe` com índice único: concorrência não duplica. Estados: aberto → reconhecido (operador, auditado) → resolvido (recuperação, regra desativada/removida ou endpoint inativo). *Agente offline* usa `last_seen_at` (calculado pelo servidor); *desatualizado* compara com a maior versão vista.
**Escopo:** regra global ou de um grupo (com descendentes); gerir regras exige `settings.manage` global (permissão de sistema); reconhecer exige `alerts.manage` no escopo do endpoint.

## ADR-019 — Administração pela interface
Tudo passa pelas mesmas policies/`AccessResolver` e gera auditoria (ator, alvo, antes/depois, sem segredos). Grupos: árvore sem ciclos, nomes únicos entre irmãos, exclusão só de grupo vazio (sem computadores, subgrupos, atribuições nem regras) e com confirmação. Mover computador exige poder gerir **origem e destino**; "sem grupo" exige escopo global. Desativar computador faz o Agent receber `endpoint.disabled` (403) e fecha seus alertas. Usuários: papéis com escopo, senha forte, e-mail normalizado em minúsculas, desativar encerra sessões. A verificação da cadeia de auditoria (`AuditVerifier`) é compartilhada pelo comando `audit:verify` e pela tela; cada verificação é registrada na própria trilha.

## ADR-020 — Implantação em Docker Compose (nginx + PHP-FPM, MySQL, Redis)
**Contexto:** o Manager precisa ser implantado num servidor com Docker, e o guia anterior era só para Laragon/Windows.
**Decisão:** uma imagem multi-estágio (assets Vite → vendor Composer sem dev → PHP-FPM Alpine) usada em cinco papéis (`app`, `migrate`, `queue`, `scheduler`; `web` é outro alvo com nginx sem privilégios). TLS termina no nginx (certificado da CA corporativa, o Agent exige). MySQL 8.4 e Redis 7 (senha, AOF) ficam em rede interna sem portas publicadas. Sessão, cache (inclui o anti-replay de nonce do Agent) e fila em Redis.
**Segurança:** `migrate` usa usuário com DDL/`TRIGGER`; a aplicação só tem DML. nginx com FS somente leitura, `cap_drop: ALL`, `no-new-privileges`; segredos só por ambiente do contêiner (a imagem não contém `.env`; caches de config são gerados na partida). O nginx **sobrescreve** `X-Forwarded-For` e o app confia nele por `TRUSTED_PROXIES` (novo; padrão: não confiar em ninguém), para que limites de taxa, bloqueio de login e auditoria usem o IP real.
**Alternativas descartadas:** Apache+mod_php (superfície maior, sem isolamento de processos); Caddy (TLS automático não serve: a CA é corporativa); Laravel Octane/Swoole (complexidade sem ganho para ~100 endpoints); um único contêiner com supervisord (não escala e mistura papéis).
**Limites:** não validado em Docker real (L25).

## ADR-021 — Tarefas remotas: poll curto, máquina de estados, claim condicional e aprovação com separação de funções
**Contexto:** a task 008 previa long-poll em `GET /tasks/next`. Com `php-fpm`/`artisan serve`, cada long-poll prende um worker PHP por agente (100 agentes = 100 workers parados).
**Decisão:** (1) **poll curto** `GET /tasks/next?wait=0` a cada ~10 s (`AgentSettings.TaskPollSeconds`, 5–300 s), antecipado quando o heartbeat traz `tasks_available`; cancelamento de tarefa em execução chega por `cancel_task_ids` no heartbeat. (2) Estados `pending_approval → available → running → completed | failed | cancelled | expired` (+ `running → available` só para tipo idempotente, até 3 tentativas), toda transição pelo `TaskService` e auditada. (3) **Claim por UPDATE condicional** (`WHERE id = ? AND state = 'available' AND expires_at > now`): só quem afetou a linha cria o attempt — funciona igual em SQLite e MySQL, sem `SELECT … FOR UPDATE`. Lease = timeout + 30 s; resultado só do attempt vigente (409 caso contrário). (4) Alto risco exige aprovação de **outro** usuário com `tasks.approve`; criador e aprovador são reconferidos no claim. (5) O Agent é a última barreira: catálogo/schema locais e lista de processos/serviços protegidos que nenhuma aprovação contorna.
**Consequências:** latência de entrega de até ~10 s (medido 2–11 s no E2E); tráfego de 6 requisições/min por agente ocioso; uma resposta de `next` perdida (cliente que desistiu por timeout) deixa a tarefa `running` até a lease vencer — não idempotente vira `failed`, nunca é executada duas vezes (L37). Corrida real de dois consumidores ainda não testada em MySQL (L30).

## ADR-022 — Transferência de arquivos: risco médio por decisão do dono, política por grupo e veto local do Agent
**Contexto:** a task 009 move arquivos operador → endpoint (`file.send`) e endpoint → operador (`file.fetch`). A recomendação técnica era envio = alto risco (aprovação por segunda pessoa); o dono do projeto escolheu **médio nos dois sentidos**, sem aprovação.
**Decisão:** (1) `file.send`/`file.fetch` são **medium**, idempotentes (3 tentativas), timeout 900 s, parâmetro único `transfer_id`; só o `TransferService` as cria (lista fechada). (2) Como não há segunda pessoa, os controles principais são a **política de diretórios por grupo** (`file_transfer_policies`, herdada pela árvore; sem política = desligado; extensões executáveis sempre bloqueadas; tela só para `settings.manage`, auditada) e o **veto local do Agent** (`TransferRoots`: vale política ∩ raízes locais; raiz que falha a conferência de dono/DACL é excluída na subida). (3) `PathGuard` com as mesmas regras em PHP (sintaxe) e C# (sintaxe + disco), provadas pelo mesmo vetor (`tests/contracts/path-guard-cases.json`); o Agent exige normalização Unicode real (`InvariantGlobalization` desligado no host e nos testes) e falha fechado sem ela. (4) No Agent, toda operação de arquivo é **por handle** (`OpenChecked`, hash, rename e delete pelo handle aberto) para fechar a janela checagem/uso. (5) Rotas de chunk do Agent têm limitador próprio (`agent-transfer`, 600/min) e teto de 2,5 MiB no PUT antes da assinatura; navegador envia chunks de 1 MiB retomáveis.
**Consequências:** um operador com `files.transfer_send` pode gravar, sem revisão, qualquer arquivo de extensão permitida nas pastas da política (L42). Sem elevação o Agent real recusa tudo (raízes temporárias excluídas) — o E2E dos handlers rodou em processo, com seam de teste; serviço real e ACL elevada não validados (L43). Queda de rede/Manager no meio falha a transferência em vez de retomar (L49).

## ADR-026 — Atualização do Agent: manifesto assinado ECDSA P-256 com chave offline (PROPOSTA — não aprovada)
**Status:** proposta (RGS-7). Detalha e substitui, se aprovada, o esboço da ADR-012. Spec: `docs/superpowers/specs/2026-10-16-atualizacao-assinada-design.md`.
**Contexto:** T8 — um Manager comprometido não pode conseguir execução como SYSTEM nos endpoints. Não há updater nem `/updates/check` no Manager.
**Alternativas:** (a) só HTTPS + hash informado pelo servidor (rejeitada: servidor comprometido controla ambos); (b) Authenticode como única verificação (rejeitada: depende de certificado ainda inexistente, L17, e de `WinVerifyTrust` sem pin de editor); (c) **assinatura própria do manifesto, ECDSA P-256/SHA-256, chave privada offline, pública embutida (2 chaves: current/next)**, com Authenticode como camada adicional; (d) Ed25519 (rejeitada por ora: ausente na BCL do .NET 8, L2).
**Decisão proposta:** (c). String assinada em linhas (domínio `toolit-update-v1`, versão, `release_seq` monotônico, sha256, size, min_protocol, min_from_version, issued/expires, revoked_versions, key_id), vetor compartilhado PHP/C#; anti-downgrade por `release_seq` persistido; pacote servido só pelo Manager (sem URL arbitrária, T15).
**Consequências:** custódia da chave de release vira ponto crítico (decisão do board); comprometimento da chave exige reinstalação por canal confiável (MSI/GPO); o Manager pode atrasar/negar atualizações (disponibilidade), não forjá-las.

## ADR-027 — Aplicação da atualização: ZIP + ToolitUpdater.exe separado, rollback e anéis (PROPOSTA — não aprovada)
**Status:** proposta (RGS-7).
**Contexto:** o serviço não pode substituir o próprio executável em uso; o MSI atual (ADR-017) tem `ProductVersion` fixo (L19), não é assinado (L17) e depende de Visual Studio (L18). Serviço nunca instalado de fato (L8).
**Alternativas:** (A) **ZIP verificado + updater estável em processo separado, troca atômica de diretório, healthcheck e rollback**; (B) versões lado a lado com troca do `ImagePath` via `sc config`; (C) `msiexec` do MSI novo (rollback nativo só cobre a instalação; downgrade exige desinstalar; acopla a L17/L18/L19).
**Decisão proposta:** (A), com (B) como contingência se o updater não sobreviver à parada do serviço em VM. Anéis `canary`/`pilot`/`stable` por endpoint, rollout com pausa automática por taxa de falha, `stable` com segunda pessoa; MSI só para instalação inicial/recuperação/GPO.
**Consequências:** arquivos fora da tabela `File` do MSI (repair/uninstall precisam de tratamento); updater precisa se autoatualizar; sobrevivência do updater e recuperação pelo SCM são **suposições a validar em VM**.

## ADR-028 — Token de enrollment fora da linha de comando (PROPOSTA — não aprovada)
**Status:** proposta (RGS-7). Trata L16.
**Decisão proposta:** o MSI aceita `ENROLLTOKENFILE` (arquivo com ACL restrita, token de TTL curto/uso limitado); a ação personalizada entrega o token ao processo `enroll` por variável de ambiente do processo filho (já suportado) e remove o arquivo; `ENROLLTOKEN` por linha de comando fica deprecado. Alternativa para o board: enrollment sem token com aprovação manual (ADR-005 prevê aprovação opcional).
**Consequências:** requer teste do MSI em Windows; GPO exige compartilhamento com ACL para contas de computador.

## ADR-029 — Inventário de arquivos: política efetiva ∩ veto local, metadados apenas, aplicação no `complete` (PROPOSTA — não aprovada)
**Status:** proposta (RGS-7). Spec: `docs/superpowers/specs/2026-10-16-inventario-de-arquivos-design.md`.
**Contexto:** a task 006 pede inventário de metadados em diretórios autorizados, com exclusões, profundidade, hash opcional e cancelamento. O serviço roda como SYSTEM (acesso a todos os perfis); nomes de arquivo podem ser dados pessoais (LGPD).
**Decisão proposta:** (1) reutilizar `PathGuard`/`forbiddenPolicyDirectory` da 009 para validar raízes e confinar a varredura; (2) o Agent busca a política por consulta assinada e aplica **política ∩ `FileInventoryRoots` (padrão vazio = desligado) ∩ exclusões fixas no código** (perfis, AppData, credenciais, navegadores); (3) varredura BFS manual, sem seguir reparse points, orçamentos de tempo/itens/hash, prioridade de fundo; (4) só metadados; hash apenas por regra explícita, lido localmente; (5) resultado enviado em lotes idempotentes e **aplicado só no `complete`**; remoção exige raiz completa e `missed_count ≥ 2` (evita L26); (6) tarefa `inventory.files` sem parâmetros, risco `low` (a confirmar), permissão `files.inventory_view`.
**Consequências:** novo volume de dados (≤ 50 000 itens/endpoint, retenção 30/90 d); finalidade e RIPD são pré-requisito (board); custo no endpoint e comportamento como SYSTEM não validados até haver VM (L8, L9).
