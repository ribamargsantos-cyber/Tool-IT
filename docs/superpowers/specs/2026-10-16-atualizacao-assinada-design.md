# Spec — Atualização assinada do Agent, anéis e instalador (task 012)

**Estado: PROPOSTA de design (RGS-7). Nada aqui está implementado nem aprovado.** Nenhum código de produção foi alterado. Decisões marcadas **[BOARD]** dependem de aprovação humana (seção 14). Contexto: `docs/tasks/012-instalacao-atualizacao-e-distribuicao.md`, `docs/threat-model.md` T8/T15, ADR-004, ADR-012, ADR-017, `docs/api/agent-api-v1.md` (`GET /updates/check`), limitações L16, L17, L18, L19, L8, L36.
ADRs associados (propostos): ADR-026 (formato, chave e verificação), ADR-027 (aplicação, rollback e anéis), ADR-028 (token fora da linha de comando).

## 1. Objetivo
Permitir que o Manager distribua novas versões do Agent a ~100 endpoints **sem que o comprometimento do Manager (servidor, banco, disco ou operador) baste para executar código como SYSTEM nos endpoints** (vetor T8), com verificação criptográfica no Agent, rollback automático e distribuição gradual por anéis.

Não-objetivos: atualizar o Manager; EDR/AV; atualizar o runtime .NET isoladamente (o pacote é autossuficiente, ADR-017: um novo runtime = nova versão do Agent); distribuição por GPO/SCCM (continua possível, mas fora deste fluxo, ver seção 11).

## 2. Estado atual (evidências no repositório, HEAD `83e9e3d`)
- `GET /updates/check` está **só documentado** (`agent-api-v1.md` §3); não há rota, controller nem modelo no Manager (`grep` em `manager/routes`). `GET /api/agent/v1/meta` devolve `min_agent_version` (config). Há regra de alerta `agent_outdated`.
- `agent_installations.agent_version` é preenchido pelo heartbeat (`X-Agent-Version`/`agent_version`). Versão do Agent: `<Version>0.1.0</Version>` em `agent/Toolit/Toolit/AgentToolit.csproj`.
- Agent: serviço LocalSystem, `AgentToolit.exe service install|uninstall` (`Core/Install/ServiceInstaller.cs`), dados em `%ProgramData%\Toolit\Agent` (`AgentPaths`), `agent.restart` sai com código 75 contando com a recuperação do SCM (L36, **não validado**). Não há updater.
- Instalador: MSI do Visual Studio (`ToolitSetup.vdproj`, ADR-017) com `ProductVersion` fixo `1.0.0` (L19), sem assinatura (L17), compilável só com Visual Studio (L18); token de enrollment viaja pela linha de comando/propriedade do MSI (L16).
- **Nunca se instalou o serviço de verdade (L8).** Toda a parte de troca de binário, parada/início de serviço e rollback é, hoje, hipótese.
- Respostas 426 `agent.upgrade_required` já existem no contrato de erros (versão de protocolo/Agent não suportada).

## 3. Modelo de ameaça específico (T8 detalhado)
Atacante → capacidade → controle:

| # | Atacante / cenário | Controle |
|---|---|---|
| U1 | Manager/banco/disco comprometido empurra pacote malicioso | Assinatura ECDSA P-256 com **chave offline**; Agent só confia na chave pública embutida; Manager nunca tem a chave privada |
| U2 | Man-in-the-middle / TLS interceptado | HTTPS obrigatório (`ServerUrlPolicy`) **e** assinatura independente do transporte |
| U3 | Downgrade para versão antiga vulnerável (assinada legitimamente) | `release_seq` monotônico assinado + versão semver; Agent persiste o maior `release_seq` visto e recusa menor; lista assinada de versões revogadas |
| U4 | Replay de manifesto antigo / "freeze attack" (servidor esconde atualização) | `issued_at` e `expires_at` assinados; manifesto expirado = recusado; ausência prolongada de manifesto fresco gera alerta no Manager (disponibilidade não é garantida pelo Agent, só a integridade) |
| U5 | Zip-slip, zip bomb, link simbólico no pacote | Seção 8 |
| U6 | Troca do pacote entre verificação e uso (TOCTOU) | Verifica hash do arquivo **já gravado em pasta de staging protegida** e extrai dele; staging com ACL SYSTEM/Administrators |
| U7 | Pacote válido que quebra o Agent (bug) | Healthcheck pós-troca + rollback automático; anéis com pausa por taxa de falha |
| U8 | Vazamento da chave de release | Procedimento de rotação/revogação (seção 6); recuperação de pior caso = reinstalação via MSI/GPO (assinado) |
| U9 | Operador malicioso publica versão | Publicação no anel `stable` exige segunda pessoa (separação de funções, igual à ADR-021) e o pacote precisa vir **já assinado offline**: o operador não consegue forjar |
| U10 | Updater explorado para escalar privilégio local | Updater e staging só gravam em diretórios restritos a SYSTEM/Administrators; nenhum caminho vem do servidor; sem DLL search-path (carga só de `%ProgramFiles%`) |

Risco residual aceito: o Manager pode **atrasar ou negar** atualizações (disponibilidade); e quem tem a chave privada de release controla a frota (concentração de risco → seção 6).

## 4. Arquitetura proposta

```
 Build/Release (máquina de release, offline p/ a chave)
   dotnet publish -> package.zip ; tools/ToolitRelease sign -> manifest.json + manifest.sig
                      │ (3 arquivos)
                      ▼
 Manager: upload (verifica assinatura com a pública) -> agent_versions (status draft)
        publicação por anel (approve p/ stable) -> update_rollouts
                      │  GET /updates/check  (assinado pelo Agent)  -> manifesto assinado
                      │  GET /updates/{version}/package (Range, HTTPS, assinado)
                      ▼
 Agent (serviço): UpdateChecker (a cada 6 h ± jitter, ou dica no heartbeat)
     1 verifica manifesto  2 baixa p/ staging  3 verifica sha256/size  4 extrai (zip-slip safe) p/ staging\new
     5 grava update-pending.json  6 dispara ToolitUpdater.exe apply
                      ▼
 ToolitUpdater.exe (processo separado, estável, em %ProgramFiles%\Toolit\Agent\updater\)
     para serviço -> move app\ -> app.prev\ -> move staging\new -> app\ -> inicia serviço
     espera healthcheck (arquivo/estado assinado pelo próprio Agent novo) até T
     ok: apaga app.prev (mantém 1 versão), report healthy
     falha/timeout: para serviço -> restaura app.prev -> inicia -> report rolled_back
```

Decisões-chave (justificativas nos ADRs):
1. **Pacote ZIP assinado, aplicado por updater separado** (opção A, ADR-027). Alternativas descartadas: (B) versões lado a lado com troca do `ImagePath` do serviço (mais seguro contra processo morto no meio, mas muda o layout do MSI e exige `sc config` — pode virar evolução); (C) `msiexec` do MSI novo (rollback nativo do Windows Installer só cobre a fase de instalação, não "o Agent novo não fica saudável", e downgrade de MSI exige desinstalar; amarra a atualização à resolução de L17/L18/L19).
2. **Assinatura do manifesto com ECDSA P-256/SHA-256** (mesma família da ADR-004, sem dependências novas: `System.Security.Cryptography` no .NET 8 e `openssl` no PHP). Ed25519 seria mais simples, mas o .NET 8 (alvo atual, L2) não o tem na BCL. Authenticode do executável é **camada adicional** (defesa em profundidade, SmartScreen/AppLocker), **não** substitui a assinatura do manifesto — a verificação do Agent não depende de certificado de CA.
3. **O pacote é servido pelo próprio Manager** (`/updates/{version}/package`), nunca por URL arbitrária. Isso mantém T15 (sem SSRF/fetch de URL de terceiros) e aproveita autenticação do Agent. O campo `url` do manifesto é informativo e **relativo** ao Manager configurado; o Agent ignora host diferente do `ServerUrl`.
4. O updater é um binário **separado e estável** para poder substituir o executável do serviço (um processo não troca o próprio arquivo em uso). Ele próprio só é atualizado por MSI ou por um passo explícito `updater/` do pacote (renomeação atômica `ToolitUpdater.exe.new` no próximo ciclo); isso é a parte mais delicada e vai para a entrega U5.

## 5. Manifesto e formato de assinatura

### 5.1 Manifesto (JSON, enviado ao Agent)
```json
{
  "schema": 1,
  "product": "toolit-agent",
  "version": "0.2.0",
  "release_seq": 7,
  "arch": "win-x64",
  "package_type": "zip",
  "sha256": "<hex64 do package.zip>",
  "size": 34567890,
  "url": "/api/agent/v1/updates/0.2.0/package",
  "min_protocol": "v1",
  "min_from_version": "0.1.0",
  "issued_at": "2026-10-20T12:00:00Z",
  "expires_at": "2027-04-20T12:00:00Z",
  "mandatory": false,
  "revoked_versions": ["0.1.3"],
  "notes": "texto livre, ≤ 2 KiB, tratado como dado (escape)",
  "key_id": "rel-2026-01",
  "signature": "<base64 ECDSA DER>"
}
```
`url`, `mandatory` e `notes` **não** são autoridade de segurança. Tudo que o Agent usa para decidir está na **string assinada**.

### 5.2 String assinada (não assina o JSON — evita ambiguidade de canonicalização)
Mesma ideia da ADR-004 (linhas separadas por `\n`, UTF-8, sem `\n` final):
```
toolit-update-v1
<product>
<version>
<release_seq>
<arch>
<package_type>
<sha256 hex minúsculo>
<size>
<min_protocol>
<min_from_version>
<issued_at UTC ISO-8601>
<expires_at UTC ISO-8601>
<revoked_versions ordenadas, separadas por vírgula; vazio se nenhuma>
<key_id>
```
Regras: o prefixo `toolit-update-v1` separa domínio (impede reutilizar assinatura de request do Agent ou de outro produto); campos validados por regex estrita antes de assinar/verificar; valores não podem conter `\n`. Vetor compartilhado `tests/contracts/update-manifest-vector.json` (casos válidos e adulterados), verificado em **PHP e C#** como o `signature-vector.json`.

### 5.3 Verificação no Agent (ordem obrigatória)
1. `schema`/`product`/`arch` esperados; `key_id` ∈ chaves embutidas; assinatura válida sobre a string reconstruída **a partir dos campos recebidos**.
2. `now` ≤ `expires_at` e `issued_at` ≤ `now + 5 min` (tolerância de relógio).
3. `release_seq` > maior `release_seq` já aplicado/visto (persistido em `state.json`, protegido só pela ACL do diretório de dados — quem escreve ali já é administrador local, ver L11) **e** `version` > versão atual (semver). Exceção única: rollback local, que nunca passa pelo manifesto.
4. `version` não está em `revoked_versions` de nenhum manifesto verificado; versão atual em `revoked_versions` ⇒ atualização obrigatória.
5. `min_from_version` ≤ versão atual (saltos que exigem etapa intermediária); `min_protocol` compatível com `/meta`.
6. Só então baixa. Depois do download: tamanho exato = `size`, SHA-256 = `sha256`.

Falhou qualquer passo: descarta, registra `reason` estável (`signature_invalid`, `expired`, `downgrade`, `revoked`, `hash_mismatch`, `size_mismatch`, `protocol`), reporta ao Manager (`POST /updates/report`) e **não tenta de novo o mesmo manifesto por 6 h**.

## 6. Gestão de chaves (chave offline)
- **Chave de release**: par ECDSA P-256 gerado em dispositivo dedicado (token de hardware/HSM, ou máquina isolada sem rede), **nunca** no Manager, no CI com segredos de rede, nem no repositório. A pública é embutida no Agent (arquivo de recurso `release-keys.json`, parte do código revisado em PR) com `key_id`, `not_before`, `not_after`.
- O Agent embute **até 2 chaves**: `current` e `next`. Rotação planejada: (1) lançar versão assinada com `current` que embute `next`; (2) esperar a frota atingir a versão (painel); (3) passar a assinar com `next`. Revogação de emergência: um manifesto assinado por uma chave ainda válida inclui `revoked_key_ids` (campo reservado para v2 do schema — **[BOARD]** decidir se entra já na v1).
- **Comprometimento da chave**: os Agents não conseguem confiar mais em atualizações; recuperação = nova instalação por canal confiável (MSI assinado por Authenticode/GPO) com a nova pública. Por isso Authenticode e o canal MSI/GPO são o *plano de recuperação*, não só estética (seção 12).
- **Custódia [BOARD]:** quem guarda a chave, backup (cofre físico), procedimento de assinatura com duas pessoas ("dois olhos" no `sign`) e registro em livro de assinaturas (versão, hash, quem, quando).
- Tooling: `installer/tools/ToolitRelease` (console .NET): `keygen`, `sign --key ... --package package.zip --version ... --seq ...`, `verify`. Saída: `manifest.json` (+ `package.zip` inalterado). O Manager também verifica a assinatura no upload com a pública (defesa em profundidade; ele não assina nada).

## 7. Modelo de dados (Manager)
Convenções do `docs/data-model.md` (uuid público, `bigint` interno).

| Tabela | Colunas-chave | Regras |
|---|---|---|
| `agent_versions` | uuid, `version` (semver), `release_seq`, `arch`, `package_path` (disco privado `storage/app/private/agent-packages/{uuid}/package.zip`), `sha256`, `size`, `manifest` (json, o manifesto assinado completo), `key_id`, `status` (`draft`, `published`, `paused`, `revoked`), `uploaded_by`, `approved_by` (nulo até aprovação p/ stable), timestamps | único `(version, arch)`; único `release_seq`; `release_seq` e `version` só crescem entre publicações (validado no upload) |
| `agent_update_rollouts` | uuid, `agent_version_id`, `ring` (`canary`,`pilot`,`stable`), `status` (`scheduled`,`active`,`paused`,`completed`,`aborted`), `target_group_id` nulo, `max_failure_ratio` (padrão 0,2), `min_sample` (padrão 3), `bake_hours`, `started_at`, `paused_reason`, `created_by` | um rollout ativo por (versão, anel); anel posterior só inicia após o anterior `completed` e `bake_hours` (ou ação manual auditada) |
| `endpoints.update_ring` (coluna) | `canary` / `pilot` / `stable` (padrão `stable`) | atribuição manual na tela do endpoint/grupo; **canary = ~5 máquinas escolhidas pelo dono** |
| `agent_update_jobs` | uuid, `endpoint_id`, `agent_installation_id`, `rollout_id`, `from_version`, `to_version`, `state` (`offered`, `downloading`, `verified`, `applying`, `healthy`, `rolled_back`, `failed`, `rejected`), `reason` (código estável), `attempts`, `offered_at`, `updated_at` | único `(endpoint_id, to_version)`; transições só por `UpdateService` (UPDATE condicional, como `TaskService`); máx. 3 tentativas por versão, depois `failed` e o endpoint sai do rollout |
| `system_settings` | `updates.check_interval_minutes`, `updates.healthcheck_seconds` | defaults seção 9 |

Retenção: pacotes de versões `revoked` e anteriores às 3 últimas `published` são apagados por `updates:prune`; `agent_update_jobs` seguem a política geral de retenção (L40: ainda inexistente — criar junto).

Permissões novas (adicionar a `docs/permissions-matrix.md` na implementação): `updates.view` (Admin, Operador, Auditor), `updates.manage` (upload, rollouts: Admin), `updates.approve_stable` (Admin; **pessoa diferente do `uploaded_by`**, imposto no serviço, não só na tela). Auditoria: `agent_version.uploaded|published|paused|revoked`, `update_rollout.started|paused|aborted|completed`, `update_job.*` (por endpoint, sem conteúdo).

## 8. Contratos de API

### 8.1 `GET /api/agent/v1/updates/check?current=0.1.0&arch=win-x64`
Assinado como qualquer rota do Agent (ADR-004). Resposta aditiva (compatível com v1):
- Nada a oferecer (anel sem rollout ativo, já na versão, ou job `failed`/limite de tentativas): `200 {"available": false, "server_time": "…"}`.
- Há oferta: `200 {"available": true, "manifest": { …5.1… }}` — **sempre o manifesto assinado inteiro**; o Manager não monta campos fora dele.
O Manager escolhe a oferta por: anel do endpoint, rollout `active`, `from_version` ≥ `min_from_version`; **uma só versão por vez** (a mais recente publicada para o anel), não salta a revogação. Compatibilidade: o exemplo atual de `agent-api-v1.md` (`version,url,sha256,signature,mandatory`) passa a ser um subconjunto; atualizar o doc na implementação.

### 8.2 `GET /api/agent/v1/updates/{version}/package`
Assinado; só para endpoint com job `offered`+ para aquela versão (senão `404 request.not_found`, sem diferenciar inexistente de não-ofertado — BOLA). Suporta `Range` (retomada de download), `Content-Type: application/octet-stream`, `Cache-Control: no-store`. Limite próprio `agent-update` de requisições/min por instalação (≈ 30; **valor inicial a validar**) e banda: os 33 MB × 100 máquinas = 3,3 GB — o rollout por anel e o `bake_hours` já escalonam; sem CDN neste ciclo.

### 8.3 `POST /api/agent/v1/updates/{version}/report`
`{ "state": "downloading|verified|applying|healthy|rolled_back|failed|rejected", "reason": "código|null", "from_version": "…" }`. Idempotente por `(job, state)`; transição inválida = `409 update.conflict`. `healthy` só é aceito de uma instalação cuja `X-Agent-Version` seja `to_version` (o heartbeat já informa a versão).

### 8.4 Painel (web)
`/updates`: lista de versões (estado, anel, hash), upload (3 arquivos), botões Publicar canary/pilot/stable (stable pede 2ª pessoa), Pausar, Revogar; por rollout: contadores por estado, taxa de falha, lista de endpoints com `reason`. Tela do endpoint: versão, anel, último job. Alerta `agent_outdated` passa a usar a versão alvo do anel em vez de só `min_version` (**[BOARD]** confirmar a semântica).

## 9. Fluxo no Agent e regras operacionais
- **Quando checar:** a cada 6 h ± 30 min de jitter (`updates.check_interval_minutes` do Manager, mín. 60); mais uma dica `update_available` no heartbeat para antecipar. Nunca durante outra atualização em andamento (arquivo de lock em `%ProgramData%\Toolit\Agent\update.lock`).
- **Staging:** `%ProgramData%\Toolit\Agent\updates\{version}\` com ACL SYSTEM/Administrators (`AgentPaths.RestrictedSecurity`); verificar espaço livre ≥ 3 × `size` antes de baixar; download com `.part` + retomada por `Range`.
- **Extração segura (zip-slip e afins)** — reaproveitar o `PathGuard` da task 009 (regras sintáticas + abertura por handle sem reparse point):
  - cada entrada: nome relativo, separadores `/` ou `\` normalizados, **recusar** `..`, caminho absoluto, `:` (ADS), nomes reservados do Windows, `~\d`, ponto/espaço final, componente > 120 ou total > 260 (mesmo vetor `path-guard-cases.json`) — **atenção:** o PathGuard atual restringe caracteres a `\p{L}\p{N}`, espaço e `.-_()`; nomes reais do .NET (`System.Private.CoreLib.dll`, `runtimes\win-x64\native\…`) passam, mas deve haver teste com a lista de arquivos do publish real; caso um nome legítimo seja recusado, usar *allowlist de arquivos esperados* do pacote (ver abaixo) em vez de relaxar o PathGuard;
  - destino calculado = `Path.GetFullPath(Combine(stagingNew, entrada))` e conferido por prefixo com `stagingNew\`; criar diretório/arquivo com `FileMode.CreateNew` (nunca sobrescreve) e sem seguir reparse points;
  - **recusar** entradas que sejam link simbólico (atributo no zip), ou com extensão fora da allowlist do pacote do Agent (`exe dll json config pdb txt` + manifest interno — **lista a fixar na implementação**);
  - limites: ≤ 2 000 entradas, tamanho total descompactado ≤ 4 × `size` e ≤ 300 MiB (anti zip-bomb), razão por entrada ≤ 100:1; contagem e tamanhos conferidos **antes** de gravar;
  - arquivo `package-files.json` dentro do zip com SHA-256 de cada arquivo (gerado e conferido pelo Agent após extrair) — como o zip inteiro já é autenticado pelo `sha256` do manifesto, isso serve para detectar corrupção de disco, não adulteração.
- **Aplicação:** Agent grava `update-pending.json` `{from, to, attempt_id, deadline}` e executa o updater fora do próprio processo (processo independente, não filho do job do serviço — **comportamento não validado sem VM**, ver seção 12). O updater, **como SYSTEM**, executa a sequência da seção 4. Operações atômicas por `MoveFileEx`/`Directory.Move` no mesmo volume; nada de cópia arquivo a arquivo na pasta viva.
- **Healthcheck:** o Agent novo, ao subir, só considera a atualização `healthy` após (a) carregar a identidade (DPAPI), (b) completar um heartbeat assinado com sucesso, (c) estar na versão `to`. Escreve `update-health.json`; o updater espera até `updates.healthcheck_seconds` (padrão 300 s — **hipótese a medir**). Sem Manager acessível (rede fora) o Agent novo **não** se declara saudável por rede: aceita saúde local `(a)+(c)` por 120 s para evitar rollback causado por indisponibilidade do servidor; a decisão está no ADR-027 e deve ser confirmada em teste.
- **Rollback:** automático (updater) se o serviço não subir, travar ou não ficar saudável no prazo; mantém `app.prev` até `healthy`. **Rollback de segurança:** se o próprio updater morrer no meio, a ação de recuperação do SCM (`sc failure … command=`) chama `ToolitUpdater.exe recover` (idempotente: decide pelo `update-pending.json`) — suposição **não testada** (L8/L36).
- **Persistência de downgrade:** `release_seq` máximo e versão aplicada ficam em `state.json`; rollback local **não** reduz o `release_seq` guardado (o Agent não volta a aceitar versão pior que a revertida, e a versão que falhou é marcada `bad` por 24 h para não entrar em laço).
- **Idempotência/concorrência:** `update.lock` + `attempt_id`; uma atualização por vez; reinício do PC no meio: na subida o updater/Agent lê `update-pending.json` e continua ou reverte.
- **Erros:** download interrompido (retoma), assinatura inválida (rejeita, reporta), disco cheio (`failed: disk_space`, antes de baixar), arquivo em uso (updater tenta parar o serviço com timeout, depois `failed` sem tocar em `app`), serviço não sobe (rollback), sem rede (tenta no próximo ciclo), instalação duplicada (lock).

## 10. Instalador, versão e token (L16/L17/L18/L19)
- **L19 — versão do MSI automatizada:** `Patch-ToolitMsi.ps1` (ou um passo pré-build) lê a `<Version>` de `AgentToolit.csproj` (única fonte da verdade), converte para `major.minor.build` (MSI ignora o 4º campo) e define `ProductVersion` + novo `ProductCode`, mantendo `UpgradeCode`. Teste: gerar dois MSIs e conferir que o segundo faz *major upgrade* sem deixar duas entradas (**exige Windows com Installer; não validável em Linux**). Também fixar `AssemblyVersion/FileVersion` e o `agent_version` informado ao Manager na mesma origem.
- **Interação MSI × ZIP:** arquivos trocados pelo updater divergem da tabela `File` do MSI. Regras: (1) MSI só para instalação inicial, reinstalação e recuperação; (2) *repair* do MSI restaura a versão do MSI (aceito; o próximo ciclo de update reaplica); (3) desinstalação do MSI remove também `app.prev`, `updates\` e a pasta `app\` residual por ação personalizada (testar). **[BOARD]** confirmar que o MSI instalado é aceito como "versão base" e que a frota evolui por ZIP.
- **L17 — Authenticode:** assinar `AgentToolit.exe`, `ToolitUpdater.exe` e o `.msi` (SHA-256 + timestamp RFC 3161) no pipeline de release, em máquina que tenha acesso ao certificado. O Agent **não** verifica Authenticode do pacote como condição de confiança (ver seção 5), mas o updater pode (opcional, `WinVerifyTrust`) exigir que o `AgentToolit.exe` extraído tenha assinatura válida **do editor esperado** quando o certificado existir — flag `RequireAuthenticode`, desligada enquanto não houver certificado. **[BOARD]:** adquirir certificado (OV/EV de CA pública, serviço de assinatura em nuvem ou CA interna distribuída por GPO como "Publicadores Confiáveis"). Para frota 100% gerida por GPO, CA interna cobre AppLocker/WDAC; SmartScreen só importa para instalação manual por download.
- **L18 — WiX × `.vdproj`:** o `.vdproj` exige Visual Studio no CI. WiX v4+ compila com `dotnet build`, tem `ServiceInstall`/`ServiceControl` nativos (eliminaria a automação `Patch-ToolitMsi.ps1` e a ADR-017 em parte) e versionamento por propriedade MSBuild — porém troca a ferramenta escolhida pelo dono e exige reescrever o instalador e validar tudo de novo em VM. **Recomendação:** manter `.vdproj` + patch até existir CI/VM; migrar para WiX **só** se o board exigir build sem Visual Studio. **[BOARD]**.
- **L16 — token fora da linha de comando (ADR-028):** (a) instalador: propriedade `ENROLLTOKENFILE` (caminho) em vez de `ENROLLTOKEN`; ação personalizada lê o arquivo, entrega ao processo `enroll` por **variável de ambiente do processo filho** (já suportado: `TOOLIT_ENROLLMENT_TOKEN`, `Commands.cs`) ou por *stdin*, e apaga/zera o arquivo; (b) GPO: arquivo de token em compartilhamento com ACL só para "Domain Computers" + token de **uso limitado e TTL curto**; (c) alternativa sem segredo: enrollment sem token com `status=pending` e aprovação no painel (ADR-005 já prevê aprovação opcional) — **[BOARD]** escolher (a)+(b) ou (c). `ENROLLTOKEN` por linha de comando permanece só como compatibilidade, deprecado.

## 11. GPO
Documentar em `docs/deployment/agent.md`: instalação via GPO (MSI + `.mst` com `SERVERURL` e `ENROLLTOKENFILE`), ordem (GPO instala; Manager atualiza), como pausar atualizações do Manager para uma OU (campo `update_ring` = `hold`, **[BOARD]** se desejado) e conflito MSI×ZIP (seção 10). Teste em domínio real: não validável sem VM de domínio.

## 12. Fatiamento em entregas incrementais (ordem e dependências)

| # | Entrega | Depende de | Validável sem VM Windows? |
|---|---|---|---|
| U0 | Decisões do board (seção 14) e geração da chave de release (cerimônia) | — | — |
| U1 | `ToolitRelease` (keygen/sign/verify) + string assinada + vetor compartilhado PHP/C# + testes de adulteração | U0 (formato) | **Sim** (unit, PHP e .NET) |
| U2 | Manager: `agent_versions`, upload com verificação, `updates/check`, `package` (Range), `report`, auditoria, permissões, testes de BOLA/rate limit; contrato JSON | U1 | **Sim** (Pest/PHPUnit; MySQL real parcial, L1) |
| U3 | Agent: `ManifestVerifier`, `ReleaseKeys`, anti-downgrade (`state.json`), downloader com `Range`, extração segura + testes de zip-slip/bomb/links/hash/size/expiração | U1 | **Sim** (testes com ZIPs hostis; criar junction só no Windows — o dev host já é Windows, L39) |
| U4 | `ToolitUpdater.exe`: troca atômica, healthcheck, rollback, `update-pending.json`, recuperação pelo SCM; testes com **diretórios temporários** e serviço fake | U3 | **Parcial:** lógica de arquivos testável; **parar/iniciar serviço real, SYSTEM, recuperação do SCM e reboot exigem VM + Administrador (L8)** |
| U5 | Autoatualização do updater + versão única (`csproj` → MSI/AssemblyVersion) (L19) + pacote do release reproduzível (`dotnet publish` determinístico, hash estável) | U4 | Parcial: versão e publish sim; MSI upgrade só em Windows com Installer |
| U6 | Anéis e rollouts: `update_rollouts`, `endpoints.update_ring`, pausa/aborto por taxa, `bake_hours`, `updates:evaluate` (agendador, L21), telas, alerta `agent_outdated` | U2 | **Sim** (testes de serviço); UI revisada em navegador (L24/L38) |
| U7 | Piloto: 5 endpoints reais (canary), atualizar 0.x→0.y, adulterar pacote, quebrar binário, reboot no meio — evidências em `docs/test-evidence.md` | U4, U6 + VM | **Não — exige VM/máquinas reais** |
| U8 | Token fora da linha de comando (L16) | independente | Parcial (agente lê `ENROLLTOKENFILE`: sim; ação do MSI: VM) |
| U9 | Authenticode (L17) + `RequireAuthenticode` | **certificado [BOARD]** | **Não** (precisa certificado e Windows) |
| U10 | GPO (`.mst`) documentada e testada | U8, U9 | **Não** (domínio) |
| (opcional) | Migração para WiX (L18) | decisão [BOARD] | Não |

U1→U2/U3 podem correr em paralelo (Manager e Agent); U4 depende de U3; U6 de U2; U7 fecha o ciclo. U8 pode começar a qualquer momento.

### Histórias sugeridas (com critério de aceite)
1. *Como release engineer, assino um pacote offline* — `sign` produz manifesto que `verify` aceita; alterar 1 byte do zip, do `version` ou do `release_seq` faz `verify` falhar; o vetor passa em PHP e C#.
2. *Como Manager, só aceito pacotes legítimos* — upload com assinatura inválida/`release_seq` ≤ último/versão repetida → 422; Auditor/Suporte → 403; stable sem 2ª pessoa → 409.
3. *Como Agent, rejeito oferta inválida* — tabela de testes: assinatura errada, `key_id` desconhecido, expirado, `issued_at` no futuro, `release_seq` menor, versão menor, versão revogada, `min_from_version` maior, hash/size divergente → rejeitado com `reason` e sem alterar nenhum arquivo fora do staging.
4. *Como Agent, extraio sem escapar do staging* — zip com `..\evil.dll`, `C:\x`, `a:b`, `CON`, link simbólico, 10 000 entradas e bomba 1000:1 → nenhum arquivo fora do staging; erro estável.
5. *Como updater, reverto se algo falhar* — com fakes: serviço novo não sobe / não fica saudável / updater morto no meio → `app` restaurado, `app.prev` consistente, relato `rolled_back`.
6. *Como administrador, libero por anéis* — rollout canary→pilot→stable; taxa de falha ≥ `max_failure_ratio` com `min_sample` pausa e alerta; endpoint fora do anel nunca recebe oferta (BOLA).
7. *Como instalador, não exponho o token* — `msiexec` com `ENROLLTOKENFILE` não deixa o token na linha de comando nem no log verboso; arquivo removido (VM).

## 13. Estratégia de testes
- **Unit/contrato (sem VM):** verificador de manifesto, vetor PHP↔C#, parser de versão semver, extração segura com ZIPs hostis gerados pelo teste, máquina de estados de `agent_update_jobs`, taxa de falha, BOLA (404 de outro endpoint/anel), rate limit, `Range`, auditoria.
- **Integração Agent↔Manager (como o E2E da 009, só arquivos descartáveis):** Agent real em processo de teste contra Manager local: oferta, download retomado, verificação, rejeição de adulterado; updater contra diretórios temporários e **serviço fake** (nunca serviço real em máquina de desenvolvimento).
- **Em VM Windows (U7, não substituível):** instalar MSI, atualizar via Manager, adulterar, quebrar binário, matar o updater no meio, reiniciar o PC durante a troca, desinstalar sem resíduos, SmartScreen/AppLocker/Defender interferindo, Windows 10 e ARM64 (L6).
- **Regra:** nenhum teste toca `%ProgramFiles%` nem serviços reais fora da VM; evidências registradas com data e comando; o que não rodar fica como limitação (padrão L8/L43).

## 14. Decisões que o board precisa tomar
1. **Chave de release:** quem custodia, onde (HSM/YubiKey/máquina offline), backup, assinatura com 2 pessoas, plano de rotação. Sem isso a entrega U1/U7 não fecha.
2. **Authenticode:** adquirir certificado (qual CA/serviço, custo, validade) ou usar CA interna + GPO; prazo; quem opera o pipeline de assinatura. Sem certificado o aviso "editor desconhecido" permanece (L17).
3. **WiX × `.vdproj`:** manter `.vdproj` (exige VS no build) ou migrar para WiX (CI sem VS).
4. **Formato de atualização:** confirmar ZIP + updater (recomendado) em vez de MSI via `msiexec`.
5. **Token (L16):** arquivo protegido + TTL curto (a/b) ou enrollment pendente com aprovação (c).
6. **Anéis:** definição do canary (~5 máquinas, quem), tempo de *bake*, limiar de falha, se `stable` exige 2ª pessoa (recomendado) e se haverá anel `hold` para OUs sem atualização automática.
7. **Janela de manutenção:** o Agent pode reiniciar a qualquer hora? Restringir a janela (não é crítico para serviço sem UI; o serviço fica fora ~10–60 s).
8. **Campo `revoked_key_ids` já no schema v1?** e política de expiração do manifesto (padrão proposto: 180 dias).
9. **Orçamento de tráfego:** 33 MB × frota; aceitável sem CDN? Escalonamento por anel basta?

## 15. Riscos
- **Updater não validado em Windows real (L8/L36):** o maior risco técnico é "processo independente sobrevive à parada do serviço" e "SCM chama o comando de recuperação". Mitigação: entrega U4 só avança com VM; opção B (lado a lado) é plano de contingência.
- **Concentração na chave de release:** ver seção 6; mitigação organizacional.
- **PathGuard × nomes do pacote .NET:** possível recusa de nome legítimo → usar allowlist de arquivos esperados (seção 9).
- **Relógio incorreto do endpoint** com `expires_at/issued_at`: tolerância de 5 min e janela de 180 dias; endpoint com relógio muito errado já falha na assinatura de requests (±120 s).
- **Atualização em massa quebra a frota:** anéis + pausa automática + rollback local.
- **Armazenamento do Manager (L46):** pacotes de 33 MB × versões; retenção `updates:prune`.

## 16. Critérios de aceite do design (RGS-7)
- Compreensível isoladamente; histórias da seção 12 com critério de aceite.
- Itens que dependem de VM/certificado marcados (seções 9, 12, 13, 15).
- Nenhum código de produção alterado.
