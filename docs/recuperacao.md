# Recuperação do consolidado: verificação financeira e reconstrução em banco paralelo

Este documento descreve o que a parte B do ciclo P08 acrescenta ao consolidado diário: uma **verificação financeira somente leitura** e uma **reconstrução controlada da projeção em um banco novo**. É operação administrativa de laboratório. Os resultados e seus limites estão em [verificacao.md](verificacao.md); as decisões, na ADR-15 de [arquitetura.md](arquitetura.md).

## O que isto é e o que não é

| É | Não é |
|---|---|
| Um comando de verificação que compara a projeção com os movimentos imutáveis da origem, por conta e dia, por identidade e por fechamento. | Prova de que todo evento novo da origem já chegou (a projeção é eventualmente consistente). |
| Uma reconstrução em **banco novo ao lado do antigo**, com captura finita selada, replay idempotente, reconciliação do mesmo conjunto e promoção administrativa. | Reconstrução **online contínua** nem replay de uma geração por requisição. |
| Compatível com os lançamentos disponíveis o tempo todo (a API de lançamentos nunca é pausada). | Troca **sem indisponibilidade das consultas**: os consumidores conhecidos ficam parados durante a operação e há uma janela entre desligar as APIs antigas e subir as novas. |
| Uma promoção que **recusa** se houver consumidor ou API conhecidos conectados. | Alta disponibilidade de produção, orquestrador ou promoção automática (ciclo P09). |
| Um registro que impede usar de novo o banco antigo depois da promoção. | **Rollback transparente**: voltar ao banco antigo trocando a connection string não é seguro e é bloqueado. |

## 1. Verificação financeira (`--verify`)

```powershell
dotnet <Worker.dll> --verify --mode live          # padrão
dotnet <Worker.dll> --verify --mode convergence
dotnet <Worker.dll> --verify --mode rebuild
```

As connection strings vêm do ambiente (`FT__ConnectionStrings__FinancialTransactions` e `FT__ConnectionStrings__Consolidation`). A saída é um JSON com o alcance, o banco e a fase do destino, o registro de controle da captura, as contagens, as divergências por tipo e as primeiras 50 divergências. Códigos de saída: `0` coerente, `4` divergente, `2` banco inalcançável (nunca contagem zero), `3` argumento inválido.

**Não escreve nada.** O destino e a origem são lidos em transações `REPEATABLE READ` com `SET TRANSACTION READ ONLY`; o destino primeiro, a origem depois. O esperado vem dos movimentos imutáveis, agregados em SQL com a zona nomeada `America/Sao_Paulo` do próprio PostgreSQL. Não usa o applier, `BusinessDay.Of` nem a API. Os valores armazenados, inclusive `net`, são lidos como estão. Igualdade de soma global nunca basta.

| Modo | Escopo | Aprova quando |
|---|---|---|
| `live` | Os recibos de `processed_events` (lidos num snapshot) definem os movimentos esperados. | Todo recibo tem movimento e conta/sequência coerentes; cada agregado por conta e dia, o fechamento acumulado e o descritor do bootstrap conferem. Descreve a **coerência interna da projeção observada**; não afirma que tudo o que existe na origem já chegou. |
| `convergence` | Todos os movimentos da origem contra os recibos do destino. Exige produtor encerrado e nada em voo. | Além do `live`: nenhum movimento sem recibo, nenhum pendente nem em quarentena, nenhuma reserva válida. |
| `rebuild` | O conjunto R selado no destino novo. | Os recibos são **exatamente** R; contagem e digest de R conferem; o conjunto histórico do bootstrap está contido em R; valores, fechamentos e descritor conferem com a origem. Vale até a promoção: depois dela chegam eventos posteriores a R e o modo acusa `receipt_not_in_capture`, como esperado. |

Tipos de divergência (identificadores estáveis): `receipt_without_movement`, `receipt_account_mismatch`, `sequence_mismatch`, `value_mismatch`, `closing_mismatch`, `missing_aggregate`, `extra_aggregate`, `missing_receipt`, `pending_in_scope`, `work_in_flight`, `descriptor_mismatch`, `receipt_not_in_capture`, `capture_item_without_receipt`, `capture_digest_mismatch`, `historical_member_missing`, `capture_not_sealed`. O verificador **não corrige nada**.

Diferenças de fuso entre ambientes são classificadas, não escondidas: o .NET deste laboratório e o PostgreSQL divergem no dia de negócio apenas em 2000 a 2003 (ADR-12), faixa fora do uso.

## 2. Reconstrução em banco paralelo (`--rebuild`)

### Pré-condições

- O **banco de destino é novo**, migrado (`AddRebuildRecovery`) e vazio. Ele é o que a connection string de projeção aponta, e o comando exige `--confirm-target-database <nome>` igual a esse banco. A origem nunca pode ser o destino, e um destino que já tem dados de uma projeção ativa é recusado.
- A origem tem o registro de controle do bootstrap (o coordenador `--backfill` já rodou) e a migration `AddProjectionRegistry`.
- **Todos os Workers e APIs do consolidado conhecidos estão parados** e as reservas venceram. O comando verifica, em `pg_stat_activity`, que não há sessão com `Application Name` `ft-consolidation-worker*` ou `ft-consolidation-api*` na origem nem no destino, e que não há reserva válida. Processos que não se identificam não são vistos por esse controle: é a restrição operacional que o comando não esconde.
- O número de movimentos não excede o teto declarado desta versão (250.000), verificado **antes** da cópia; acima dele a captura é recusada, não truncada.

### Estágios

Dry-run é o padrão; os estágios que alteram dados exigem `--apply`. Todos recebem `--operation <id>` e `--confirm-target-database <banco>`.

```powershell
dotnet <Worker.dll> --rebuild --stage plan      --operation op-2026-10-20 --confirm-target-database <novo>
dotnet <Worker.dll> --rebuild --stage capture   --operation op-2026-10-20 --confirm-target-database <novo> --apply
dotnet <Worker.dll> --rebuild --stage replay    --operation op-2026-10-20 --confirm-target-database <novo> --apply
dotnet <Worker.dll> --rebuild --stage reconcile --operation op-2026-10-20 --confirm-target-database <novo> --apply
dotnet <Worker.dll> --rebuild --stage enable    --operation op-2026-10-20 --confirm-target-database <novo> --apply
dotnet <Worker.dll> --rebuild --stage promote   --operation op-2026-10-20 --confirm-target-database <novo> --confirm-previous-database <antigo> --apply
dotnet <Worker.dll> --rebuild --stage status    --operation op-2026-10-20 --confirm-target-database <novo>
dotnet <Worker.dll> --rebuild --stage cancel    --operation op-2026-10-20 --confirm-target-database <novo> --apply
```

| Estágio | O que faz | Estado resultante |
|---|---|---|
| `capture` | Abre uma transação `REPEATABLE READ` somente leitura na origem, estabelece o snapshot, valida o descritor histórico (cobertura e cardinalidade), copia o conjunto finito R dos **movimentos canônicos** (não só os eventos pendentes) para `rebuild_capture_item` no destino e, **na mesma transação local do destino**, grava a marca "captura concluída" com contagem, digest e snapshot. Movimento sem evento na Outbox recusa a captura. Divergência entre o payload da Outbox e o movimento é **relatada** (`payloadDivergences`), não transportada. Conjunto vazio é capturado. | `captured` |
| `replay` | Reapresenta cada item de R ao applier no destino (marcador e agregado na mesma transação), independentemente do `status` da Outbox. **Não** marca a Outbox como processada nem altera `processed_at`. Retoma pelos recibos do destino, sem reset global e sem identificadores novos. | `replayed` |
| `reconcile` | Executa `--verify --mode rebuild`. Só passa se os recibos forem exatamente R e tudo conferir. | `reconciled` |
| `enable` | Reconcilia de novo e, em uma transação local, grava a linha de bootstrap com o **descritor histórico** da origem (não a contagem de R) e `live`, mais a proveniência na operação. Não falsifica o descritor nem marca `live` para contornar o reconciliador. | `enabled` (ainda **não promovido**) |
| `promote` | Ver abaixo. | `promoted` |

**Retomada.** Cada estágio é repetível. Se a execução cai antes do commit da selagem, não existe conjunto concluído e a repetição captura de novo, em visão nova (declarado como nova tentativa). Se cai depois, a repetição encontra o conjunto selado e **retoma exatamente o mesmo R**: nunca o substitui nem mistura duas capturas, mesmo que a origem tenha ganhado movimentos. Se o resultado do commit é desconhecido, a operação é relida antes de decidir (saída `5` quando nada foi selado).

**Lançamentos.** A API de lançamentos continua aceitando durante a captura e o replay. Os movimentos posteriores ao snapshot ficam na Outbox, fora de R, e chegam ao destino novo depois da promoção; os itens de R ainda pendentes na origem são **deduplicados** no destino novo (marcador) antes da confirmação.

### Promoção

A promoção só ocorre se: o destino está `enabled`; a reconciliação de R continua verdadeira; **nenhuma** sessão de Worker ou API do consolidado está conectada à origem, ao destino novo ou ao banco anterior (`--confirm-previous-database`); não há reserva válida; e **todo evento já confirmado na origem está coberto por R** (nenhum consumidor confirmou trabalho depois da captura). Do contrário recusa (saída `4`) e não altera nada.

Aceita, ela registra na origem (`consolidation_projection_registry`) qual identidade de projeção é a ativa e marca o destino como `promoted`. Depois disso o operador sobe as **APIs e os Workers novos apontando só para o banco reconstruído** (nunca altere as strings globais nem as de desenvolvimento para o ensaio).

### Depois da promoção, e o retorno

- Um Worker ou uma API apontados para um destino que **não é** a projeção ativa registrada deixam de consumir e de servir (`ProjectionSuperseded`; a API responde `503 projection_superseded`). O banco antigo continua intacto e **preservado**, mas não pode simplesmente voltar: ele pode estar atrasado enquanto a origem já considera processados os eventos que o Worker novo confirmou.
- Um destino habilitado e ainda não promovido também não é consumido nem servido (`RebuildInProgress`, `503 consolidation_not_ready`).
- **Não há rollback transparente.** Voltar exige nova reconstrução e reconciliação de um destino, ou o procedimento permanece bloqueado. Antes da promoção, cancelar (`--stage cancel`) não apaga o banco antigo nem a origem.

## 3. Retenção

Esta fatia **conserva** movimentos, Outbox, marcadores e o conjunto de captura. Não há purge automático: uma exclusão por idade destruiria a deduplicação, o replay ou a reconciliação. O crescimento é medido por `--status` (ADR-14). Qualquer estratégia futura de arquivamento precisa provar que não os destrói.

## 4. Custos e limites conhecidos

- A captura mantém **um snapshot longo** na origem enquanto copia R, e grava R numa **única transação** do destino. O teto desta versão é de 250.000 movimentos; o maior volume exercitado nesta parte foi pequeno (dezenas de movimentos), de modo que a **escala de 100.000 movimentos não foi medida aqui** (é objeto da parte C).
- O controle de consumidores é por nome de aplicação nas sessões do PostgreSQL. É um controle comprovável, não onisciente.
- Duas instâncias e um único host demonstram os mecanismos; não são alta disponibilidade de produção.
- A reconstrução não torna os 50 req/s uma propriedade demonstrada: esse ensaio está pendente.
