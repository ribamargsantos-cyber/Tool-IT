# 012 — Instalação, atualização assinada e distribuição do Agent

## Objetivo e contexto
Instalador reproduzível, atualização assinada com rollback, distribuição gradual (5 → grupo intermediário → frota), GPO.

## Problema
Sem segredos compartilhados no instalador; atualização é o vetor de maior risco (T8).

## Escopo
Incluído: publicação (`dotnet publish -c Release -r win-x64 --self-contained false`/single-file), script `installer/Install-ToolitAgent.ps1` (parâmetros `-ServerUrl -EnrollmentToken`), desinstalação limpa, MSI opcional (WiX) se justificado, `agent_versions`/`agent_update_jobs`, canal de update, anéis de implantação, doc GPO. Excluído: EDR/AV.

## Dependências
005; 004 (`/updates/check`).

## Análise da arquitetura atual
Agent sem updater. Manager sem `agent_versions`.

## Arquivos
`installer/*`, Agent `Updater/*`, Manager `AgentVersion`, `AgentUpdateJob`, `UpdatesController`, UI de versões, `docs/deployment/agent.md`.

## Requisitos funcionais
Instalar: copiar para `%ProgramFiles%\Toolit\Agent`, criar serviço (LocalSystem, início automático atrasado, recovery), ACLs, enroll, iniciar. Atualizar: baixar pacote, verificar SHA-256 + assinatura, parar serviço, trocar binários mantendo backup, iniciar, **healthcheck**; falha ⇒ rollback. Desinstalar: parar, remover serviço, binários, (opcional) dados e chave.

## Não funcionais
Idempotente (rodar 2× não quebra); log de instalação; sem interação.

## Regras
- Pacote assinado com chave offline; Agent tem a chave pública de verificação embutida; Authenticode do executável recomendado (certificado de assinatura de código da empresa).
- Anéis: `canary` (5), `pilot`, `stable`; atualização por anel e por grupo; pausa/abort por taxa de falha.
- Token de enrollment por máquina (ou multiuso limitado para GPO, com expiração curta).

## Contratos
`GET /updates/check`; manifesto `{version, sha256, size, url, signature, min_protocol, notes}`.

## Segurança
Rejeitar downgrade (exceto rollback local), HTTPS, verificação antes de qualquer extração, extração em diretório sem path traversal (zip-slip), tempo limite de rollback.

## Estratégia
1. Script de instalação/remoção. 2. Publicação reproduzível. 3. Manifesto + assinatura (ferramenta em `installer/tools`). 4. Updater + rollback. 5. UI/anéis. 6. GPO.

## Erros
Download interrompido, assinatura inválida, serviço não sobe, disco cheio, arquivo em uso, falta de rede, instalação duplicada.

## Testes
Unit: verificação de assinatura/hash/zip-slip/downgrade; integração em VM: instalar, atualizar, falhar (pacote corrompido e binário quebrado ⇒ rollback), desinstalar, reboot.

## Aceite
- [ ] Instalação sem interação em VM limpa - [ ] Update assinado OK - [ ] Pacote adulterado rejeitado - [ ] Binário quebrado reverte - [ ] Desinstalação sem resíduos - [ ] Piloto de 5 endpoints documentado

## Documentação
`deployment/agent.md` (instalação, atualização, GPO, anéis), manual do administrador.

## Riscos
Sem certificado Authenticode: usar assinatura do manifesto + documentar aviso do SmartScreen.

## Checklist
- [ ] Instalador - [ ] Desinstalador - [ ] Manifesto/assinatura - [ ] Updater - [ ] Rollback - [ ] Anéis - [ ] GPO - [ ] Testes - [ ] Docs

## Progresso (2026-10-14) — instalador MSI (ToolitSetup)
- [x] MSI do projeto do usuário (`agent/Toolit/ToolitSetup`) com serviço: x64, autossuficiente, diálogo de URL/token, propriedades `SERVERURL`/`ENROLLTOKEN`, token mascarado em log, ações como SYSTEM com cuidados de atualização/rollback (ADR-017).
- [x] `AgentToolit.exe service install|uninstall` (testado por unidade) e `Install-ToolitAgent.ps1` como alternativa.
- [ ] **Instalar de verdade como Administrador** (UI e `/qn`), reiniciar, atualizar (aumentar `ProductVersion`), desinstalar, rollback — registrar evidências.
- [ ] Assinatura do MSI/executável (Authenticode) · [ ] atualização assinada do Agent (manifesto + rollback) · [ ] GPO (`.mst`) documentada e testada · [ ] anéis 5 → intermediário → frota · [ ] versão do MSI automatizada · [ ] entrega do token sem linha de comando (L16).

## Design proposto (RGS-7, 2026-10-16) — não implementado
`docs/superpowers/specs/2026-10-16-atualizacao-assinada-design.md` + ADR-026/027/028 (propostas, aguardam aprovação do board). Nenhum item deste checklist foi marcado: é só design.
