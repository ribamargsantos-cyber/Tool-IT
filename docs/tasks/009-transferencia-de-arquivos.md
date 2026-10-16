# 009 — Transferência de arquivos

## Objetivo e contexto
Enviar arquivos do Manager ao endpoint e recuperar arquivos do endpoint, com integridade, limites e auditoria. Transferir ≠ executar.

## Problema
Path traversal, sobrescrita acidental, exfiltração e execução automática.

## Escopo
Incluído: transferências em partes (chunks) retomáveis, hash SHA-256 obrigatório, diretórios permitidos por política, quarentena/retenção. Excluído: execução do arquivo (permissão e tarefa próprias `files.execute`, desligada por padrão).

## Dependências
008.

## Análise da arquitetura atual
Tarefas tipadas existentes (008). **Implementado (2026-10-16):** política por grupo, `PathGuard` nos dois lados, `TransferService`, rotas do navegador e do Agent, aba Arquivos, handlers `file.send`/`file.fetch` (ADR-022).

## Arquivos
Manager: `FileTransfer`, `FileTransferService`, `PathValidator`, storage privado `storage/app/transfers` (fora do webroot), `PruneTransfersJob`. Agent: `Handlers/FileSend|FileFetch`, `PathGuard`.

## Requisitos funcionais
Seleção explícita de endpoint e arquivo; tipo/tamanho validados; chunks de 1–4 MiB com hash por chunk e total; retomada; registro de origem/destino/tamanho/operador/resultado.

## Não funcionais
Limite padrão 100 MiB por arquivo (configurável), 1 transferência por endpoint; banda limitada; retenção de temporários 7 dias.

## Regras
- Nome do arquivo é dado não confiável: o Manager gera nome interno (UUID); nome original só como metadado escapado.
- Destino deve estar dentro de diretório permitido pela política do grupo (ex.: `C:\ProgramData\Toolit\Inbox`); caminho canonicalizado (`GetFullPath`, resolve links/junctions) **antes** de comparar; rejeitar `..`, caminhos UNC não autorizados, ADS (`:`), nomes reservados (CON, NUL…), caracteres de controle.
- Sem sobrescrita por padrão (`overwrite=false`; sobrescrever exige flag + permissão); gravação em arquivo temporário + rename atômico após hash OK.
- Recuperação (`fetch`) só de diretórios permitidos; conteúdo de documentos pessoais não é inventariado.

## Contratos
Endpoints do Agent: `GET /transfers/{id}/chunks/{n}` (download), `PUT /transfers/{id}/chunks/{n}` (upload) + `POST /transfers/{id}/complete`; documentar em `agent-api-v1.md` ao implementar.

## Segurança
Autorização por endpoint+operador na criação e no acesso aos chunks (BOLA); URL de download só para o agente dono; antivírus do host não é contornado; limite de tipo (allowlist de extensões por política); download pelo navegador com `Content-Disposition: attachment`, `nosniff`.

## Estratégia
1. PathValidator (Manager e Agent) com testes extensivos. 2. Upload operador→Manager. 3. Entrega ao agente. 4. Fetch inverso. 5. Retomada. 6. Retenção.

## Erros
Hash divergente (descartar), interrupção (retomar), disco cheio, arquivo em uso, destino fora da política, tamanho excedido, agente revogado no meio.

## Testes
Tabela de caminhos maliciosos (traversal, UNC, ADS, reservados, symlink/junction, Unicode confusables), sobrescrita, hash, retomada, limites, permissões, auditoria, retenção.

## Aceite
Evidências: `docs/test-evidence.md` §"Transferência de arquivos — E2E (2026-10-16)".
- [x] Envio e recuperação reais com hash verificado — handlers reais **em processo de teste**, HTTP assinado real, disco real, Manager real: envio 7 940 032 B e recuperação 6 020 657 B com SHA-256 igual nas duas pontas; download com `attachment`/`nosniff`/`no-store`
- [ ] Envio e recuperação pelo **serviço instalado** (`AgentToolit.exe` como SYSTEM, ACL elevada) — não validado: sem Administrador o exe real excluiu as raízes temporárias e recusou tudo (falha fechada comprovada) — L43
- [x] Traversal impossível por teste — vetor compartilhado PHP/C# + E2E: 28 entradas hostis → 422; junction plantada na raiz e pasta fora das raízes → `rejected`; linhas forjadas no banco barradas (409/`rejected`); canário fora da raiz intacto
- [x] Sem execução automática — nenhuma tarefa/rota executa arquivo; extensões executáveis sempre bloqueadas (tela, criação, revalidação e Agent)
- [x] Operador sem permissão recebe 403 (Auditor/Suporte no envio) e fora do escopo 404 (Operador do grupo B) — testes + E2E
- [x] Retomada comprovada — upload do navegador (status + `missing_chunks`) e Agent morto no meio (tentativa #2 continuou do chunk 6, hash igual; lease vencida por atalho no banco de teste)
- [ ] Retomada após **queda do Manager/rede** (L49): implementada no Agent (ADR-023 A: falha transitória não reportada, lease vence e a tarefa é recolocada; `PartSweeper` limpa `.part` órfão > 24 h) — **falta executar** a suíte C# e repetir o E2E "queda do Manager"
- [x] Retenção comprovada — `transfers:prune` (com `updated_at` recuado no banco de teste): blobs apagados, metadados com `purged_at`, download passa a 409
- [ ] UNC real e antivírus real interferindo — não validados (L43)

## Documentação
[`manual-admin.md`](../manual-admin.md) (política de diretórios), threat model T9, [`deployment/server.md`](../deployment/server.md) §5.1 (limites de proxy/PHP).

## Riscos
Arquivos grandes sobre Laragon/Apache (limites `post_max_size`/`upload_max_filesize`): usar chunks e documentar.

## Checklist
- [x] PathValidator (`PathGuard` PHP + C#, vetor `tests/contracts/path-guard-cases.json`, regras em `docs/api/path-guard.md`)
- [x] Política por grupo + tela `/settings/file-policies` (E2E pela tela, 9 diretórios hostis recusados)
- [x] Envio (E2E: navegador HTTP → Manager → handler real em processo)
- [x] Recuperação + download (E2E)
- [x] Retomada (upload do navegador; Agent morto e tarefa recolocada) · [ ] após queda do Manager (L49)
- [x] Retenção (`transfers:prune`, E2E com atalho no banco de teste)
- [x] Revogação no meio cancela a transferência e o Agent para (E2E)
- [ ] Serviço real como SYSTEM + ACL restrita aplicada (L43)
- [ ] UNC real, antivírus real (L43)
- [ ] Aba Arquivos num navegador real (L50)
- [x] Testes (Manager 1060, Agent 278 + E2E opcional, Node 18, `transfers-smoke.php` 24/24)
- [x] Docs (`agent-api-v1.md` §transfers, `task-types.md`, `path-guard.md`, modelo de dados, matriz, T9, ADR-022, L42–L50, `manual-admin.md`, `deployment/server.md` §5.1)
