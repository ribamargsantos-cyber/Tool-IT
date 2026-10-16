# 006 — Inventário (hardware, software, rede, periféricos, arquivos)

## Objetivo e contexto
Coletar e persistir inventário completo e confiável, distinguindo "coletado", "indisponível" e "erro".

## Problema
Evitar WMI excessivo; não varrer discos; não enviar conteúdo.

## Escopo
Incluído: coletores system/hardware/software/network/peripherals, `POST /inventory`, upsert transacional, histórico de mudanças relevante (software instalado/removido), política de inventário de arquivos (metadados em diretórios autorizados). Excluído: conteúdo de arquivos (task 009).

## Dependências
004, 005.

## Análise da arquitetura atual
Agent com heartbeat e fila; Manager com `endpoints`.

## Arquivos
Agent: `Collectors/{System,Hardware,Software,Network,Peripheral,FileInventory}Collector.cs`. Manager: `InventoryController`, `InventoryIngestService`, migrations de `hardware_inventory`, `software_inventory`, `network_interfaces`, `peripheral_inventory`, `inventory_snapshots`, `file_inventory_policies/items`, telas de detalhe.

## Requisitos funcionais
Conforme enunciado §4. Software: chaves `Uninstall` em HKLM (64/32 bits) e por usuário carregado; **não** executar desinstaladores; **não** usar `Win32_Product` (dispara reconfiguração MSI). WMI/CIM: consultas pontuais com timeout (≤10 s) e campos explícitos.

## Não funcionais
Inventário completo < 30 s e < 5 % CPU em máquina de referência (medir). Intervalo padrão 24 h + sob demanda. Payload ≤ 4 MiB (gzip).

## Regras
Mudança no software gera evento só se diferente do snapshot anterior; item não visto em 2 coletas → `removed_at`. Arquivos: apenas caminhos permitidos pela política, profundidade e exclusões configuráveis, cancelável, limite de itens e de taxa; hash opcional só para arquivos selecionados.

## Contratos
`agent-api-v1.md` §/inventory; JSON de contrato em `tests/contracts/inventory.json`.

## Segurança
Dados do endpoint são não confiáveis (escape, limites de tamanho de strings, número de itens); campos `sensitive` ocultos para perfil Consulta; política de arquivos exige `files.inventory_view` e auditoria de criação/alteração.

## Estratégia
1. Coletores por seção com interface comum e resultado `{status,data,reason}`. 2. Ingestão idempotente por `snapshot_id`. 3. Telas. 4. Política de arquivos.

## Erros
Seção indisponível (permissão/hardware ausente) → `unavailable` + motivo; timeout; registro corrompido; payload > limite; snapshot duplicado.

## Testes obrigatórios
Unit dos parsers (Registro simulado via interface), contrato JSON, ingestão idempotente, diff de software, limites, exclusões e cancelamento da varredura de arquivos, teste real em Windows 10/11.

## Aceite
- [ ] Inventário real de uma máquina aparece no painel - [ ] Seções indisponíveis sinalizadas - [ ] Varredura de arquivos respeita política e não lê conteúdo - [ ] Medição de custo registrada

## Documentação
`data-model.md`, manual do administrador (política de arquivos), `test-evidence.md`.

## Riscos
Variação entre versões do Windows; ARM64 sem testes (documentar).

## Checklist (estado em 2026-10-14)
- [x] Coletores system/hardware/software/rede/periféricos (reais, testados) - [x] Ingestão idempotente - [x] UI (abas Hardware, Software, Rede) - [x] Testes (Manager 25+ e Agent) - [x] Medição (≈ 1 s, 19 KiB, 1 máquina) - [x] Docs de contrato
- [ ] **Política de inventário de arquivos** (diretórios autorizados, exclusões, profundidade, hash opcional, cancelamento) — não iniciada - [x] Histórico de mudanças (2026-10-15): diff de sistema/hardware/periféricos/rede e nomes de software em `inventory_snapshots.changes` (`App\Support\Inventory\InventoryDiff`) + aba **Histórico** no endpoint (software e rede só com `inventory.view_sensitive`). Plano: `docs/superpowers/plans/2026-10-15-historico-de-inventario.md`
- [ ] Validar em Windows 10 e ARM64
- ⚠ **Desvio da regra** "item não visto em 2 coletas → `removed_at`": o Manager marca `removed_at` já na primeira coleta **completa** (não truncada) sem o item. Uma coleta parcial por erro de leitura do Registro pode gerar remoção + reaparecimento falsos (aparecem no histórico). Ver L26.

## Design proposto da política de arquivos (RGS-7, 2026-10-16) — não implementado
`docs/superpowers/specs/2026-10-16-inventario-de-arquivos-design.md` + ADR-029 (proposta, aguarda finalidade/RIPD e aprovação do board). Item "Política de inventário de arquivos" continua **não iniciado** no checklist.
