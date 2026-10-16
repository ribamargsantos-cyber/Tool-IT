# CI (GitHub Actions)

Workflows em `.github/workflows/`: `manager.yml` e `agent.yml`. Disparo: `push` em `main`, `pull_request` e manual (`workflow_dispatch`). Permissões: `contents: read`; nenhum segredo; ações fixadas por versão exata.

| Job | Runner | O que roda | Local equivalente |
|---|---|---|---|
| Manager / test | ubuntu | `composer install`, `vendor/bin/pint --test`, `php artisan test` (SQLite em memória), `npm ci && npm run build`, `node --test tests/js/transfer-upload.test.mjs` | mesmos comandos em `manager/` |
| Manager / audit | ubuntu | `composer audit --locked`, `npm audit --audit-level=high` | idem |
| Agent / test | windows-latest | `dotnet test agent/Toolit/AgentToolit.Tests/AgentToolit.Tests.csproj` (DPAPI/WMI/winsqlite3 exigem Windows; sem `.vdproj`, L18) | idem, em Windows; clonar em disco local (L39) |
| Agent / audit | windows-latest | `dotnet list <projeto> package --vulnerable --include-transitive --source https://api.nuget.org/v3/index.json` por projeto (L15, L18) | idem |

## Limiar das auditorias
- Composer: qualquer advisory falha o job.
- npm: severidade `high` ou `critical` falha; `moderate`/`low` só no log.
- NuGet: qualquer pacote vulnerável (direto/transitivo) ou falha ao consultar a fonte falha o job.

## Lacunas
- **Perfil MySQL não incluído:** `manager/phpunit.xml` força SQLite (`force="true"`); rodar a suíte em MySQL exige parametrizar o phpunit.xml e um service container — pendente (task 003).
- O E2E do Agent (`TOOLIT_E2E_MANAGER`) não roda no CI (fica ignorado).
- **Run do PR (2026-10-16):** Manager (test + audit) e Agent/audit **verdes**; Agent/test **vermelho legítimo**: 277 aprovados, 1 falha — `FileHandlersTests.Registry_excludes_a_root_writable_by_normal_users` (`Assert.Empty` com coleção não vazia). Hipótese não verificada: o runner do GitHub executa como Administrador, o que muda as ACLs que o teste assume. Pendente: investigar o teste (novo item a priorizar). Links: https://github.com/ribamargsantos-cyber/Tool-IT/actions/runs/38955949690 (Agent), https://github.com/ribamargsantos-cyber/Tool-IT/actions/runs/38955949737 (Manager).
