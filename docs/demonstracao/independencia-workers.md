# Demonstração de independência: cenário Workers

Resultado: **APROVADO** (código de saída 0). Início 2026-10-22T00:16:34.824Z, fim 2026-10-22T00:17:10.153Z (UTC). Código-fonte: `48a08a05653c52f69465e2ab5e000ba8ba7bf962`.

## Critérios

| Critério | Resultado | Detalhe |
|---|---|---|
| sinais_de_falha: sinais de falha comprovados antes da janela e readiness da escrita 200 | aprovado | sinais=True; readiness da API de lançamentos=200 |
| escrita_durante_a_falha: escrita 10 de 10 com 201 durante a falha | aprovado | 10 de 10 com 201 |
| persistencia_na_origem: os 10 movimentos e as 10 linhas da Outbox (status 0) existem na origem durante a falha | aprovado | movimentos=10, outbox=10, pendentes=10 |
| posicao_na_origem_durante: posição por instante na origem responde 200 com os lançamentos da janela | aprovado | status=200 saldo=120.25 esperado=120.25 |
| ausencia_na_projecao: nenhum dos 10 ids em processed_events durante a falha (consulta direta) | aprovado | 0 de 10 presentes |
| consulta_durante_a_falha: API do consolidado segue no ar: 200 stale com unresolvedEvents = lançamentos da janela por conta e por cliente | aprovado | A1=4, A2=3, B1=3, cliente A=7, cliente B=3; todos stale |
| convergencia_no_prazo: pending = 0 e os 10 ids em processed_events em até 120 s desde a restauração | aprovado | 3.47 s |
| reconciliacao_exata: conjuntos (movements = Outbox processada = processed_events = 16 ids) e valores por conta (origem = projeção = esperado do script) | aprovado | movimentos=16 outbox=16 processed=16 duplicados=0;  |
| consolidado_completo: consolidado por conta e por cliente: valores esperados, freshness complete, unresolvedEvents 0, failedEvents 0 | aprovado | 5 consultas complete com os valores esperados |
| idempotencia: repetir uma operação da janela: 200 (não 201), mesmo movementId, nenhum movimento ou evento novo, consolidado inalterado | aprovado | status=200 mesmoId=True movimentos 16->16 outbox 16->16 |
| containers_do_projeto: containers do docker-compose.yml com os mesmos ids e healthy ao fim | aprovado | origem=healthy, projeção=healthy |

## Antes

| Item | Valor |
|---|---|
| apiStartedWithoutProjectionConnectionString | True |
| writeApiPid | 11796 |
| businessDay | 2026-10-21 |
| movementIds | ["01a160f7-ba2c-79c9-84b5-a920e996dac8","01a160f7-bb90-716b-80af-733b46af43a6","01a160f7-bbba-727b-8234-0f8b99e22895","01a160f7-bbd7-7e38-97ea-99e086a5c90a","01a160f7-bbf2-7518-95f8-443d7749fe33","01a160f7-bc06-7ab4-85ae-bea3a851e45c"] |
| processedEventsCount | 6 |
| processedEventsMatchMovementIds | True |
| consolidationMatchesExpected | True |
| writeReadiness | 200 |

Consultas ao consolidado (consolidation):

| Consulta | Status | code | freshness | unresolved | failed | créditos | débitos | líquido | contagem | fechamento |
|---|---:|---|---|---:|---:|---:|---:|---:|---:|---:|
| account:aaaaaaa1-0000-0000-0000-000000000001 | 200 |  | complete | 0 | 0 | 120 | 30 | 90 | 3 | 90 |
| account:aaaaaaa2-0000-0000-0000-000000000002 | 200 |  | complete | 0 | 0 | 50 | 8 | 42 | 2 | 42 |
| account:bbbbbbb1-0000-0000-0000-000000000001 | 200 |  | complete | 0 | 0 | 10 | 0 | 10 | 1 | 10 |
| customer:11111111-1111-1111-1111-111111111111 | 200 |  | complete | 0 | 0 | 170 | 38 | 132 | 5 | 132 |
| customer:22222222-2222-2222-2222-222222222222 | 200 |  | complete | 0 | 0 | 10 | 0 | 10 | 1 | 10 |

## Durante (janela da falha)

| Item | Valor |
|---|---|
| interruption | {"workerProcessExited":true,"workerPortClosed":true,"workerHealthLiveStatus":-1} |
| interruptedAtUtc | 2026-10-22T00:16:57.564Z |
| windowStartUtc | 2026-10-22T00:17:02.658Z |
| created201 | 10 de 10 |
| originMovementsFound | 10 |
| originOutboxRows | 10 |
| originOutboxPending | 10 |
| originPositionA1 | {"status":200,"balance":120.25,"movementCount":7,"expectedBalance":120.25,"expectedCount":7,"note":"posição por instante lida na origem: prova da escrita, não da projeção"} |
| adminStatus | {"originState":"ok","originPending":10,"originQuarantined":0,"projectionState":"ok","projectionProcessedEvents":6} |
| projectionContainsWindowIds | 0 de 10 |
| consolidationApiLiveStatus | 200 |
| windowEndUtc | 2026-10-22T00:17:02.847Z |

Lançamentos da janela (início e fim em UTC):

| Chave | Conta | Tipo | Valor | Início | Fim | Status | movementId | Location |
|---|---|---|---:|---|---|---:|---|---|
| demo-wk5-w-01 | aaaaaaa1-0000-0000-0000-000000000001 | Credit | 40 | 2026-10-22T00:17:02.660Z | 2026-10-22T00:17:02.684Z | 201 | 01a160f7-eacc-744e-bd15-8513f31b6f5d | /api/v1/accounts/aaaaaaa1-0000-0000-0000-000000000001/movements/01a160f7-eacc-744e-bd15-8513f31b6f5d |
| demo-wk5-w-02 | aaaaaaa1-0000-0000-0000-000000000001 | Debit | 15 | 2026-10-22T00:17:02.684Z | 2026-10-22T00:17:02.717Z | 201 | 01a160f7-eaf1-7dc9-93da-8a324053981c | /api/v1/accounts/aaaaaaa1-0000-0000-0000-000000000001/movements/01a160f7-eaf1-7dc9-93da-8a324053981c |
| demo-wk5-w-03 | aaaaaaa2-0000-0000-0000-000000000002 | Credit | 25 | 2026-10-22T00:17:02.717Z | 2026-10-22T00:17:02.733Z | 201 | 01a160f7-eb02-70f8-b376-6c6440118050 | /api/v1/accounts/aaaaaaa2-0000-0000-0000-000000000002/movements/01a160f7-eb02-70f8-b376-6c6440118050 |
| demo-wk5-w-04 | bbbbbbb1-0000-0000-0000-000000000001 | Debit | 3 | 2026-10-22T00:17:02.733Z | 2026-10-22T00:17:02.749Z | 201 | 01a160f7-eb12-734a-bfa7-2ba769794be1 | /api/v1/accounts/bbbbbbb1-0000-0000-0000-000000000001/movements/01a160f7-eb12-734a-bfa7-2ba769794be1 |
| demo-wk5-w-05 | aaaaaaa1-0000-0000-0000-000000000001 | Credit | 12.5 | 2026-10-22T00:17:02.749Z | 2026-10-22T00:17:02.765Z | 201 | 01a160f7-eb22-7489-8ba7-2890bf07be7e | /api/v1/accounts/aaaaaaa1-0000-0000-0000-000000000001/movements/01a160f7-eb22-7489-8ba7-2890bf07be7e |
| demo-wk5-w-06 | aaaaaaa2-0000-0000-0000-000000000002 | Debit | 5.5 | 2026-10-22T00:17:02.765Z | 2026-10-22T00:17:02.782Z | 201 | 01a160f7-eb33-75c7-bd60-4a22e34f1489 | /api/v1/accounts/aaaaaaa2-0000-0000-0000-000000000002/movements/01a160f7-eb33-75c7-bd60-4a22e34f1489 |
| demo-wk5-w-07 | bbbbbbb1-0000-0000-0000-000000000001 | Credit | 60 | 2026-10-22T00:17:02.783Z | 2026-10-22T00:17:02.798Z | 201 | 01a160f7-eb43-7df4-bce2-0665d668a6bb | /api/v1/accounts/bbbbbbb1-0000-0000-0000-000000000001/movements/01a160f7-eb43-7df4-bce2-0665d668a6bb |
| demo-wk5-w-08 | aaaaaaa1-0000-0000-0000-000000000001 | Debit | 7.25 | 2026-10-22T00:17:02.799Z | 2026-10-22T00:17:02.814Z | 201 | 01a160f7-eb53-7c6b-b2dc-9796eea0eb2f | /api/v1/accounts/aaaaaaa1-0000-0000-0000-000000000001/movements/01a160f7-eb53-7c6b-b2dc-9796eea0eb2f |
| demo-wk5-w-09 | aaaaaaa2-0000-0000-0000-000000000002 | Credit | 9.99 | 2026-10-22T00:17:02.815Z | 2026-10-22T00:17:02.830Z | 201 | 01a160f7-eb64-7c5a-b1ad-e6d78ca18a81 | /api/v1/accounts/aaaaaaa2-0000-0000-0000-000000000002/movements/01a160f7-eb64-7c5a-b1ad-e6d78ca18a81 |
| demo-wk5-w-10 | bbbbbbb1-0000-0000-0000-000000000001 | Credit | 1.01 | 2026-10-22T00:17:02.831Z | 2026-10-22T00:17:02.847Z | 201 | 01a160f7-eb74-7a49-981f-22727174e7a7 | /api/v1/accounts/bbbbbbb1-0000-0000-0000-000000000001/movements/01a160f7-eb74-7a49-981f-22727174e7a7 |

Consultas ao consolidado (consolidationDuringFailure):

| Consulta | Status | code | freshness | unresolved | failed | créditos | débitos | líquido | contagem | fechamento |
|---|---:|---|---|---:|---:|---:|---:|---:|---:|---:|
| account:aaaaaaa1-0000-0000-0000-000000000001 | 200 |  | stale | 4 | 0 | 120 | 30 | 90 | 3 | 90 |
| account:aaaaaaa2-0000-0000-0000-000000000002 | 200 |  | stale | 3 | 0 | 50 | 8 | 42 | 2 | 42 |
| account:bbbbbbb1-0000-0000-0000-000000000001 | 200 |  | stale | 3 | 0 | 10 | 0 | 10 | 1 | 10 |
| customer:11111111-1111-1111-1111-111111111111 | 200 |  | stale | 7 | 0 | 170 | 38 | 132 | 5 | 132 |
| customer:22222222-2222-2222-2222-222222222222 | 200 |  | stale | 3 | 0 | 10 | 0 | 10 | 1 | 10 |

## Depois (recuperação e reconciliação)

| Item | Valor |
|---|---|
| restoreCommandIssuedAtUtc | 2026-10-22T00:17:03.933Z |
| convergedAtUtc | 2026-10-22T00:17:07.407Z |
| secondsFromRestoreToConvergence | 3.47 |
| reconciliation | {"movementsInOrigin":16,"outboxProcessed":16,"outboxTotal":16,"processedEvents":16,"duplicates":0,"setsEqual":true,"expectedPerAccount":{"A1":{"net":120.25,"count":7,"debits":52.25,"credits":172.5},"A2":{"net":71.49,"count":5,"debits":13.5,"credits":84.99},"B1":{"net":68.01,"count":4,"debits":3.0,"credits":71.01}},"valueProblems":[]} |
| adminStatus | {"originPending":0,"originQuarantined":0,"projectionPhase":"live","projectionProcessedEvents":16} |
| idempotency | {"operation":"demo-wk5-w-01","status":200,"sameMovementId":true,"countsBefore":{"movements":16,"outbox":16},"countsAfter":{"movements":16,"outbox":16},"consolidationUnchanged":true} |
| writeReadiness | 200 |
| writeApiSameProcess | {"pid":11796,"stillRunning":true,"restarted":false} |
| containersUnchanged | {"sameIds":true,"originId":"04aef5bc2b0b","projectionId":"b2c2046f4763","stateAfter":"healthy/healthy"} |

Consultas ao consolidado (consolidation):

| Consulta | Status | code | freshness | unresolved | failed | créditos | débitos | líquido | contagem | fechamento |
|---|---:|---|---|---:|---:|---:|---:|---:|---:|---:|
| account:aaaaaaa1-0000-0000-0000-000000000001 | 200 |  | complete | 0 | 0 | 172.5 | 52.25 | 120.25 | 7 | 120.25 |
| account:aaaaaaa2-0000-0000-0000-000000000002 | 200 |  | complete | 0 | 0 | 84.99 | 13.5 | 71.49 | 5 | 71.49 |
| account:bbbbbbb1-0000-0000-0000-000000000001 | 200 |  | complete | 0 | 0 | 71.01 | 3 | 68.01 | 4 | 68.01 |
| customer:11111111-1111-1111-1111-111111111111 | 200 |  | complete | 0 | 0 | 257.49 | 65.75 | 191.74 | 12 | 191.74 |
| customer:22222222-2222-2222-2222-222222222222 | 200 |  | complete | 0 | 0 | 71.01 | 3 | 68.01 | 4 | 68.01 |

## Linha do tempo (UTC)

| Instante | Evento | Detalhe |
|---|---|---|
| 2026-10-22T00:16:34.826Z | início | cenário=Workers sufixo=wk5 |
| 2026-10-22T00:16:39.902Z | pré-condições atendidas | containers do projeto healthy (ids registrados), portas 5100/5110/5120 livres |
| 2026-10-22T00:16:43.625Z | migrations aplicadas | FinancialTransactionsDbContext |
| 2026-10-22T00:16:46.601Z | migrations aplicadas | ConsolidationDbContext |
| 2026-10-22T00:16:48.401Z | processo iniciado: api-lancamentos | PID 11796, porta 5100 |
| 2026-10-22T00:16:50.707Z | massa inicial registrada | 6 de 6 com 201 |
| 2026-10-22T00:16:51.244Z | coordenador --backfill | rc=0 |
| 2026-10-22T00:16:52.764Z | processo iniciado: worker-1 | PID 16996, porta 5120 |
| 2026-10-22T00:16:56.251Z | processo iniciado: api-consolidado | PID 9308, porta 5110 |
| 2026-10-22T00:16:57.048Z | sistema pronto | fase live, pending=0, API do consolidado pronta |
| 2026-10-22T00:16:57.551Z | S0 registrado | processed_events=6, consolidado complete com valores esperados, readiness da escrita=200 |
| 2026-10-22T00:16:57.586Z | Worker encerrado | PID 16996 |
| 2026-10-22T00:17:02.646Z | interrupção comprovada | workerProcessExited=True; workerPortClosed=True; workerHealthLiveStatus=-1 |
| 2026-10-22T00:17:02.659Z | janela da falha: início | K = 10 lançamentos novos, um a um, por HTTP |
| 2026-10-22T00:17:02.849Z | janela da falha: último lançamento concluído | 10 de 10 com 201 |
| 2026-10-22T00:17:03.932Z | verificações durante a falha concluídas | a restauração é o passo seguinte |
| 2026-10-22T00:17:05.463Z | processo iniciado: worker-2 | PID 2884, porta 5120 |
| 2026-10-22T00:17:05.464Z | Worker restaurado (novo processo, mesmos binários e configuração) | PID 2884 |
| 2026-10-22T00:17:07.414Z | convergência observada | 3.47 s depois da restauração |

## Limpeza

- 2026-10-22T00:17:09.585Z: api-consolidado (processo encerrado por PID)
- 2026-10-22T00:17:09.613Z: api-lancamentos (processo encerrado por PID)
- 2026-10-22T00:17:09.615Z: worker-1 (processo encerrado por PID)
- 2026-10-22T00:17:09.639Z: worker-2 (processo encerrado por PID)
- 2026-10-22T00:17:09.826Z: ft_demo_wk5_src (DROP DATABASE)
- 2026-10-22T00:17:10.006Z: ft_demo_wk5_cons (DROP DATABASE)

