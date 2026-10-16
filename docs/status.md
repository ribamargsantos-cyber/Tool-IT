# Estado do projeto

Atualizado em 2026-10-16 (transferência de arquivos — task 009 — com E2E dos handlers reais contra Manager real). **Só consta como "concluído" o que foi executado e validado** (evidências em [`test-evidence.md`](test-evidence.md)).

## Fase 1 — Fundação (Manager) ✔
RBAC por grupo, auditoria encadeada, login + TOTP, API do Agent (enroll, heartbeat, rotação, revogação), painel mínimo. Detalhes e testes no histórico de [`test-evidence.md`](test-evidence.md).

## Fase 2 — MVP funcional

| Item | Estado | Observação |
|---|---|---|
| Agent: solução .NET (Core / Collectors / Host / Tests) sobre o template do usuário | ✔ | `agent/Toolit/` — ADR-014 |
| Identidade ECDSA P-256 protegida por DPAPI; `enroll` com token de uso único (token nunca em disco/log) | ✔ | Validado com o `.exe` publicado contra um Manager real |
| Requests assinados (vetor PHP↔C# verificado nos dois lados) | ✔ | |
| Heartbeat, fila SQLite limitada, backoff com jitter, reenvio idempotente | ✔ | Queda de 55 s: 0 lacunas, 0 duplicatas |
| Revogação detectada → coleta/envio suspensos, fila preservada, estado persistente | ✔ | Validado ao vivo |
| Inventário: sistema, hardware, software (Registro), rede, periféricos | ✔ | 5/5 seções reais nesta máquina em ~1 s, 19 KiB |
| Métricas: CPU, memória, volumes, uptime (lote, sem WMI) | ✔ | Temperatura: **não coletada** (sem fonte confiável) |
| Manager: ingestão de inventário/métricas, retenção (`metrics:prune`), abas Hardware/Software/Rede/Métricas | ✔ | Gráfico SVG gerado no servidor |
| Permissão `inventory.view_sensitive` (Consulta não vê software/IP/MAC/usuário) | ✔ | |
| Instalador MSI (`agent/Toolit/ToolitSetup`, projeto do usuário) com serviço: diálogo de URL/token, `service install/uninstall` no próprio Agent, ações do MSI via `installer/Patch-ToolitMsi.ps1` | ✔ MSI gerado (x64, autossuficiente, 33 MB) e validado nas tabelas e na extração · ⚠ **instalação real não executada** | Sessão sem Administrador; ver `installer/README.md` |
| Script alternativo `installer/Install-ToolitAgent.ps1` | ⚠ validado em `-DryRun` e guardas | Chama o mesmo `service install` |
| Serviço Windows real: criação, início automático, recuperação, reinício do PC, atualização do MSI | ❌ **não validado** | Exige Administrador (ver L8) — rodar `ToolitSetup.msi` e registrar em `test-evidence.md` |
| Alertas: regras (disco por volume, memória, CPU, agente offline, agente desatualizado) com limite, histerese, duração mínima, repetição; estados aberto/reconhecido/resolvido; avaliador agendado a cada minuto; telas de alertas e de regras | ✔ | Testes + E2E com o Agent real (ver `test-evidence.md`); ADR-018 |
| Eventos do Windows, serviços monitorados, rollups horários | ❌ pendente | task 007 (lado Agent) |
| Administração pela interface: auditoria (filtros + verificação da cadeia), grupos (árvore), mover/desativar computador, usuários (criar, papéis com escopo, senha, ativar/desativar, encerrar sessões), troca da própria senha | ✔ testes automatizados (375 no total) + revisão visual no navegador embutido (desktop e 375 px) · ⚠ não testado em Chrome/Edge/Firefox reais | ADR-019; task 011; L24 |
| Inventário de arquivos por política | ❌ pendente | task 006 |
| Histórico de mudanças do inventário (hardware/SO/rede/software) | ✔ testes automatizados (2026-10-15) · ⚠ sem revisão visual · aba **Histórico** | `InventoryDiff`; L26, L27 |
| Histórico/log do endpoint no painel | parcial | auditoria por endpoint + métricas 7 d; sem eventos do Windows |

## Fase 3 — Tarefas remotas (task 008)

| Item | Estado | Observação |
|---|---|---|
| Modelo, estados, catálogo de 9 tipos, claim condicional com lease, expiração/requeue, idempotência, auditoria | ✔ | Testes + E2E; ADR-021; [`api/task-types.md`](api/task-types.md) |
| API do Agent `next` (poll curto `wait=0`, 204) / `progress` / `result`; `tasks_available` e `cancel_task_ids` no heartbeat | ✔ | E2E com o `.exe` publicado + `tests/integration/tasks-smoke.php` (14/14) |
| Painel: aba Tarefas, Aprovações, detalhe; separação de funções (criador não aprova) | ✔ testes + E2E pelo serviço/HTTP · ⚠ CSS visto com Tailwind CDN (L38) | |
| Agent: leitura (`system.info`, `process.list`, `service.list`, `inventory.refresh`) | ✔ | E2E real: `completed` em 2–11 s |
| Agent: `process.kill` com processos protegidos, guarda de PID reutilizado e checagem de processo crítico | ✔ em processo descartável (`ping.exe`) | `lsass` real **não** testado (só em VM) |
| Agent: `service.start|stop|restart`, `agent.restart` | ⚠ só *fakes* | Serviço real do Windows **não validado** (L32, L36, L8) |
| Concorrência real do claim em MySQL | ❌ não validado | L30 (task 013) |
| PowerShell | fora deste ciclo | — |

## Fase 3 — Transferência de arquivos (task 009)

| Item | Estado | Observação |
|---|---|---|
| Política de diretórios por grupo (herança, sem política = desligado, executáveis sempre bloqueados) + tela `/settings/file-policies` | ✔ | Testes + E2E pela tela; ADR-022; [`manual-admin.md`](manual-admin.md) |
| `PathGuard` PHP + C# com vetor compartilhado | ✔ | [`api/path-guard.md`](api/path-guard.md); E2E: 28 entradas hostis → 422 |
| Upload pelo navegador em chunks de 1 MiB, retomável; `TransferService`, estados, auditoria | ✔ | E2E por cliente HTTP real (cookies + CSRF) · ⚠ aba Arquivos não vista em navegador real (L50) |
| API do Agent (metadados, chunks, `begin`/`complete`, limite `agent-transfer`) | ✔ | E2E + `tests/integration/transfers-smoke.php` (24/24; 130 req sem 429) |
| Agent: `file.send` / `file.fetch` (handle, `.part`, SHA-256, veto local, junction recusada, revogação no meio) | ✔ handlers reais em processo de teste contra Manager real | ⚠ executável do serviço só provou a falha fechada (sem Administrador as raízes são excluídas) — L43 |
| Retomada | ✔ upload do navegador; Agent morto → tarefa recolocada → continua do `.part` | ❌ após queda do Manager/rede a transferência falha e o `.part` fica órfão (L49) |
| Retenção (`transfers:prune`) | ✔ | E2E com `updated_at` recuado no banco de teste |
| Serviço real como SYSTEM, ACL restrita elevada, UNC, antivírus | ❌ não validado | L43 |

## Próximos passos (ordem)
1. **Instalar `ToolitSetup.msi` como Administrador** (interativo e `/qn`), reiniciar o PC, derrubar a rede, atualizar e desinstalar; registrar em `test-evidence.md`.
2. Validar Manager em MySQL + Redis (Laragon) — L1.
3. Task 007 lado Agent (serviços monitorados, eventos); validar handlers de serviço e `agent.restart` numa VM com o serviço instalado (L32, L36); depois o E2E de transferência com o serviço instalado e ACL elevada (L43).
4. Decidir a correção da retomada após falha de rede nas transferências (L49).
5. Migrar o Agent para `net10.0` quando o SDK 10 estiver instalado (L2).

## Riscos abertos
Ver [`known-limitations.md`](known-limitations.md).

## CI (RGS-10)
Workflows `manager.yml` e `agent.yml` criados (ver [`ci.md`](ci.md)). ⚠ **Não executados/validados** até o primeiro run no GitHub; perfil MySQL pendente.
