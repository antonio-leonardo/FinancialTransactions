# Demonstração de independência: cenário Projection

Resultado: **APROVADO** (código de saída 0). Início 2026-10-22T00:17:10.913Z, fim 2026-10-22T00:18:02.066Z (UTC). Código-fonte: `48a08a05653c52f69465e2ab5e000ba8ba7bf962`.

## Critérios

| Critério | Resultado | Detalhe |
|---|---|---|
| sinais_de_falha: sinais de falha comprovados antes da janela e readiness da escrita 200 | aprovado | sinais=True; readiness da API de lançamentos=200 |
| escrita_durante_a_falha: escrita 10 de 10 com 201 durante a falha | aprovado | 10 de 10 com 201 |
| persistencia_na_origem: os 10 movimentos e as 10 linhas da Outbox (status 0) existem na origem durante a falha | aprovado | movimentos=10, outbox=10, pendentes=10 |
| posicao_na_origem_durante: posição por instante na origem responde 200 com os lançamentos da janela | aprovado | status=200 saldo=120.25 esperado=120.25 |
| ausencia_na_projecao: projeção inacessível durante a falha (registrada como inacessível, não como zero) | aprovado | consulta direta falhou=True; porta aberta=False |
| consulta_durante_a_falha: consultas ao consolidado respondem 503 dependency_unavailable (nunca saldo zero) | aprovado | 5 consultas: 503 dependency_unavailable |
| convergencia_no_prazo: pending = 0 e os 10 ids em processed_events em até 120 s desde a restauração | aprovado | 8.08 s |
| reconciliacao_exata: conjuntos (movements = Outbox processada = processed_events = 16 ids) e valores por conta (origem = projeção = esperado do script) | aprovado | movimentos=16 outbox=16 processed=16 duplicados=0;  |
| consolidado_completo: consolidado por conta e por cliente: valores esperados, freshness complete, unresolvedEvents 0, failedEvents 0 | aprovado | 5 consultas complete com os valores esperados |
| idempotencia: repetir uma operação da janela: 200 (não 201), mesmo movementId, nenhum movimento ou evento novo, consolidado inalterado | aprovado | status=200 mesmoId=True movimentos 16->16 outbox 16->16 |
| containers_do_projeto: containers do docker-compose.yml com os mesmos ids e healthy ao fim | aprovado | origem=healthy, projeção=healthy |

## Antes

| Item | Valor |
|---|---|
| apiStartedWithoutProjectionConnectionString | True |
| writeApiPid | 16648 |
| businessDay | 2026-10-21 |
| movementIds | ["01a160f8-54cb-7488-adb1-d9da4ff157ec","01a160f8-564c-7638-beb6-510c4169a5c2","01a160f8-5673-761e-9df3-4c4243b20564","01a160f8-568e-7565-8aa3-3358510b1ed4","01a160f8-569f-75a0-9f8a-dee84d4f7531","01a160f8-56b1-78bb-90b8-cf39a0f7799f"] |
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
| interruption | {"workerReadyStatus":503,"workerReadyBody":"{\"status\":\"Unhealthy\"}","consolidationApiReadyStatus":503,"consolidationApiReadyBody":"{\"status\":\"Unhealthy\"}","consolidationGetStatus":503,"consolidationGetCode":"dependency_unavailable","workerLogFailureClass":"Transient","outboxQuarantined":0} |
| interruptedAtUtc | 2026-10-22T00:17:37.119Z |
| windowStartUtc | 2026-10-22T00:17:41.137Z |
| created201 | 10 de 10 |
| originMovementsFound | 10 |
| originOutboxRows | 10 |
| originOutboxPending | 10 |
| originPositionA1 | {"status":200,"balance":120.25,"movementCount":7,"expectedBalance":120.25,"expectedCount":7,"note":"posição por instante lida na origem: prova da escrita, não da projeção"} |
| adminStatus | {"originState":"ok","originPending":10,"originQuarantined":0,"projectionState":"unknown","projectionProcessedEvents":null} |
| projectionContainsWindowIds | inacessível (não é zero) |
| projectionPortOpen | False |
| windowEndUtc | 2026-10-22T00:17:41.320Z |

Lançamentos da janela (início e fim em UTC):

| Chave | Conta | Tipo | Valor | Início | Fim | Status | movementId | Location |
|---|---|---|---:|---|---|---:|---|---|
| demo-pj3-w-01 | aaaaaaa1-0000-0000-0000-000000000001 | Credit | 40 | 2026-10-22T00:17:41.138Z | 2026-10-22T00:17:41.158Z | 201 | 01a160f8-8119-7483-9a7c-35da100a3b76 | /api/v1/accounts/aaaaaaa1-0000-0000-0000-000000000001/movements/01a160f8-8119-7483-9a7c-35da100a3b76 |
| demo-pj3-w-02 | aaaaaaa1-0000-0000-0000-000000000001 | Debit | 15 | 2026-10-22T00:17:41.158Z | 2026-10-22T00:17:41.174Z | 201 | 01a160f8-812b-7d70-8216-c6e38842c0ce | /api/v1/accounts/aaaaaaa1-0000-0000-0000-000000000001/movements/01a160f8-812b-7d70-8216-c6e38842c0ce |
| demo-pj3-w-03 | aaaaaaa2-0000-0000-0000-000000000002 | Credit | 25 | 2026-10-22T00:17:41.175Z | 2026-10-22T00:17:41.191Z | 201 | 01a160f8-813b-736d-bf06-3ccfd0603bf7 | /api/v1/accounts/aaaaaaa2-0000-0000-0000-000000000002/movements/01a160f8-813b-736d-bf06-3ccfd0603bf7 |
| demo-pj3-w-04 | bbbbbbb1-0000-0000-0000-000000000001 | Debit | 3 | 2026-10-22T00:17:41.192Z | 2026-10-22T00:17:41.208Z | 201 | 01a160f8-814c-7ae6-b92a-6d8f7704a7ed | /api/v1/accounts/bbbbbbb1-0000-0000-0000-000000000001/movements/01a160f8-814c-7ae6-b92a-6d8f7704a7ed |
| demo-pj3-w-05 | aaaaaaa1-0000-0000-0000-000000000001 | Credit | 12.5 | 2026-10-22T00:17:41.209Z | 2026-10-22T00:17:41.226Z | 201 | 01a160f8-815e-7a03-95b1-267d05ccc75f | /api/v1/accounts/aaaaaaa1-0000-0000-0000-000000000001/movements/01a160f8-815e-7a03-95b1-267d05ccc75f |
| demo-pj3-w-06 | aaaaaaa2-0000-0000-0000-000000000002 | Debit | 5.5 | 2026-10-22T00:17:41.226Z | 2026-10-22T00:17:41.244Z | 201 | 01a160f8-816f-7112-bf9b-d983ec034f5d | /api/v1/accounts/aaaaaaa2-0000-0000-0000-000000000002/movements/01a160f8-816f-7112-bf9b-d983ec034f5d |
| demo-pj3-w-07 | bbbbbbb1-0000-0000-0000-000000000001 | Credit | 60 | 2026-10-22T00:17:41.244Z | 2026-10-22T00:17:41.262Z | 201 | 01a160f8-8181-75cc-bd1f-aa13e0988167 | /api/v1/accounts/bbbbbbb1-0000-0000-0000-000000000001/movements/01a160f8-8181-75cc-bd1f-aa13e0988167 |
| demo-pj3-w-08 | aaaaaaa1-0000-0000-0000-000000000001 | Debit | 7.25 | 2026-10-22T00:17:41.262Z | 2026-10-22T00:17:41.281Z | 201 | 01a160f8-8194-75d3-ab23-ca61d3813704 | /api/v1/accounts/aaaaaaa1-0000-0000-0000-000000000001/movements/01a160f8-8194-75d3-ab23-ca61d3813704 |
| demo-pj3-w-09 | aaaaaaa2-0000-0000-0000-000000000002 | Credit | 9.99 | 2026-10-22T00:17:41.281Z | 2026-10-22T00:17:41.301Z | 201 | 01a160f8-81a7-76de-8760-52a42a58bfa5 | /api/v1/accounts/aaaaaaa2-0000-0000-0000-000000000002/movements/01a160f8-81a7-76de-8760-52a42a58bfa5 |
| demo-pj3-w-10 | bbbbbbb1-0000-0000-0000-000000000001 | Credit | 1.01 | 2026-10-22T00:17:41.301Z | 2026-10-22T00:17:41.320Z | 201 | 01a160f8-81ba-7f97-9d53-10b0031c6f04 | /api/v1/accounts/bbbbbbb1-0000-0000-0000-000000000001/movements/01a160f8-81ba-7f97-9d53-10b0031c6f04 |

Consultas ao consolidado (consolidationDuringFailure):

| Consulta | Status | code | freshness | unresolved | failed | créditos | débitos | líquido | contagem | fechamento |
|---|---:|---|---|---:|---:|---:|---:|---:|---:|---:|
| account:aaaaaaa1-0000-0000-0000-000000000001 | 503 | dependency_unavailable |  |  |  |  |  |  |  |  |
| account:aaaaaaa2-0000-0000-0000-000000000002 | 503 | dependency_unavailable |  |  |  |  |  |  |  |  |
| account:bbbbbbb1-0000-0000-0000-000000000001 | 503 | dependency_unavailable |  |  |  |  |  |  |  |  |
| customer:11111111-1111-1111-1111-111111111111 | 503 | dependency_unavailable |  |  |  |  |  |  |  |  |
| customer:22222222-2222-2222-2222-222222222222 | 503 | dependency_unavailable |  |  |  |  |  |  |  |  |

## Depois (recuperação e reconciliação)

| Item | Valor |
|---|---|
| restoreCommandIssuedAtUtc | 2026-10-22T00:17:50.900Z |
| convergedAtUtc | 2026-10-22T00:17:58.977Z |
| secondsFromRestoreToConvergence | 8.08 |
| reconciliation | {"movementsInOrigin":16,"outboxProcessed":16,"outboxTotal":16,"processedEvents":16,"duplicates":0,"setsEqual":true,"expectedPerAccount":{"A1":{"net":120.25,"credits":172.5,"count":7,"debits":52.25},"A2":{"net":71.49,"credits":84.99,"count":5,"debits":13.5},"B1":{"net":68.01,"credits":71.01,"count":4,"debits":3.0}},"valueProblems":[]} |
| adminStatus | {"originPending":0,"originQuarantined":0,"projectionPhase":"live","projectionProcessedEvents":16} |
| idempotency | {"operation":"demo-pj3-w-01","status":200,"sameMovementId":true,"countsBefore":{"movements":16,"outbox":16},"countsAfter":{"movements":16,"outbox":16},"consolidationUnchanged":true} |
| writeReadiness | 200 |
| writeApiSameProcess | {"pid":16648,"stillRunning":true,"restarted":false} |
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
| 2026-10-22T00:17:10.914Z | início | cenário=Projection sufixo=pj3 |
| 2026-10-22T00:17:17.606Z | pré-condições atendidas | containers do projeto healthy (ids registrados), portas 5100/5110/5120 livres |
| 2026-10-22T00:17:19.495Z | container dedicado da projeção pronto | ft-demo-cons-pj3 |
| 2026-10-22T00:17:23.153Z | migrations aplicadas | FinancialTransactionsDbContext |
| 2026-10-22T00:17:26.148Z | migrations aplicadas | ConsolidationDbContext |
| 2026-10-22T00:17:27.951Z | processo iniciado: api-lancamentos | PID 16648, porta 5100 |
| 2026-10-22T00:17:30.302Z | massa inicial registrada | 6 de 6 com 201 |
| 2026-10-22T00:17:30.816Z | coordenador --backfill | rc=0 |
| 2026-10-22T00:17:32.340Z | processo iniciado: worker-1 | PID 7296, porta 5120 |
| 2026-10-22T00:17:35.807Z | processo iniciado: api-consolidado | PID 8684, porta 5110 |
| 2026-10-22T00:17:36.611Z | sistema pronto | fase live, pending=0, API do consolidado pronta |
| 2026-10-22T00:17:37.107Z | S0 registrado | processed_events=6, consolidado complete com valores esperados, readiness da escrita=200 |
| 2026-10-22T00:17:37.734Z | container dedicado da projeção parado | ft-demo-cons-pj3 |
| 2026-10-22T00:17:41.126Z | interrupção comprovada | workerReadyStatus=503; workerReadyBody={"status":"Unhealthy"}; consolidationApiReadyStatus=503; consolidationApiReadyBody={"status":"Unhealthy"}; consolidationGetStatus=503; consolidationGetCode=dependency_unavailable; workerLogFailureClass=Transient; outboxQuarantined=0 |
| 2026-10-22T00:17:41.137Z | janela da falha: início | K = 10 lançamentos novos, um a um, por HTTP |
| 2026-10-22T00:17:41.323Z | janela da falha: último lançamento concluído | 10 de 10 com 201 |
| 2026-10-22T00:17:50.900Z | verificações durante a falha concluídas | a restauração é o passo seguinte |
| 2026-10-22T00:17:51.402Z | container dedicado da projeção religado | ft-demo-cons-pj3 |
| 2026-10-22T00:17:58.984Z | convergência observada | 8.08 s depois da restauração |

## Limpeza

- 2026-10-22T00:18:01.199Z: worker-1 (processo encerrado por PID)
- 2026-10-22T00:18:01.222Z: api-consolidado (processo encerrado por PID)
- 2026-10-22T00:18:01.252Z: api-lancamentos (processo encerrado por PID)
- 2026-10-22T00:18:01.462Z: ft_demo_pj3_src (DROP DATABASE)
- 2026-10-22T00:18:01.903Z: ft-demo-cons-pj3 (docker rm -f (container dedicado))
- 2026-10-22T00:18:01.905Z: ft_demo_pj3_cons (removido com o container dedicado)

