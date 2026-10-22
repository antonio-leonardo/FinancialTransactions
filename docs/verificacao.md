# Verificação

Este documento diz **o que foi verificado, como e quando**, e o que não foi. Cada resultado traz a sua **fonte**:

| Fonte | Significado |
|---|---|
| *executor local* | Execução do ciclo de desenvolvimento, registrada no checkpoint do ciclo (comandos, `rc`, contagens do relatório de testes). |
| *governança local* | Reexecução independente, na máquina de desenvolvimento, registrada no parecer do ciclo. |
| *CI* | Execução do workflow no GitHub Actions, com SHA, run e job. |
| *leitura do auditor-master* | Contadores e ambiente lidos do **log** da CI por quem audita o projeto; a API pública do GitHub não os devolve. |
| *esta tentativa* | Execução feita para consolidar esta documentação, em 19/10/2026. |

Contadores locais não são atribuídos à CI, nem o contrário. Os estados seguem a hipótese H14 de [escopo.md](escopo.md): proposto, implementado, executado e aprovado, reprovado, bloqueado/não verificado, e, quando cabe, não aplicável ou parcial. "Executado e aprovado" significa execução local registrada, não CI. Horários locais são UTC−03:00.

**Onde está o estado atual.** A visão única está na seção [0](#0-estado-atual-da-entrega). As seções 1 a 8 registram a versão A (anterior ao ciclo P07) e as seções 9 a 14 registram cada ciclo na data dele: onde dizem "pendente" ou "não executado", pode ter sido resolvido depois. O **estado final** dos cinco apontamentos, as pendências vigentes e o portão da versão final estão nas seções [6](#6-verificações-que-não-existem-ou-são-parciais-pendências), [7](#7-checklist-de-entrega), [8](#8-portão-de-aceitação) e [15](#15-ciclo-p09-documentação-consolidada-e-matriz-final).

## 0. Estado atual da entrega

Visão única, em 20/10/2026, sobre a baseline `15a657f` com a documentação do ciclo P09. As seções seguintes são o histórico datado de cada ciclo; quem lê só esta seção não precisa reconstruir a cronologia. Responsáveis: **executor** = execução local registrada no checkpoint do ciclo; **governança** = reexecução ou conferência registrada em parecer privado (data e escopo na própria célula); **CI** = execução do workflow, só para o SHA indicado. "Fonte" do requisito: enunciado (E), orientações complementares (O), adequações sugeridas (A), hipótese do projeto (H).

| Requisito e fonte | Implementação | Cenário e evidência | Resultado e responsável | Limite ou pendência |
|---|---|---|---|---|
| **Código em C#** (E, R1) | 11 projetos `net10.0` em `src/`, `tools/`, `tests/` | restore e build `-warnaserror` de 11 projetos (seção 15.3) | Executor, 20/10/2026, rc 0, 0 avisos. Governança: build reexecutado em P08 C (rc 0). Aceitação por checkout novo: versão A (seção 8) e versão final `5c3beb0` (seção 8.9, executor, 20/10/2026: `rc` 0, 11 projetos) | Reprodução da governança sobre a versão final pendente |
| **Testes automatizados** (E, R2) | `tests/` unidade e integração (xUnit, dois PostgreSQL reais) | 220 + 145 = 365 testes (seção 15.3) | Executor, 20/10/2026: 365 aprovados, 0 ignorados. Governança: 220 + 145 reexecutados em P08 C, 365 nomes iguais. **CI** (`15a657f`, run 16; `5c3beb0`, run 17): `success`, contadores não lidos. Clone novo de `5c3beb0` (seção 8.9, executor, 20/10/2026): 220 + 145 aprovados, 0 ignorados | Testes do consolidado têm esperas de tempo real; intermitência seria registrada. A CI não executa sondagens, mutações nem ensaios de carga |
| **Repositório público no GitHub** (E, R3) | repositório público | consulta anônima à API e clone sem credencial na versão A (seção 8) | Executor, 19/10/2026, versão A; versão final: `HEAD` do clone = `main` pública = `5c3beb0` (seção 8.9, 20/10/2026) | About do repositório pendente (ação do responsável); a descrição ainda é a original em 20/10/2026 |
| **README com execução local** (E, R4) | `README.md` | passo a passo seguido num clone novo da versão A (seção 8.4) e da versão final (seção 8.9.2) | Executor, 19/10/2026, versão A; versão final `5c3beb0` (20/10/2026): README reproduzido no clone, 19 verificações, 0 divergentes (seção 8.9.2) | `docker compose up` não exercitado a partir de nenhum clone; reprodução da governança pendente |
| **Documentação no repositório** (E, R5) | `README.md` e 7 documentos em `docs/` | verificador de links, âncoras e privacidade (seções 2.3 e 15.3) | Executor, 20/10/2026: 0 problemas. Governança: controle de reprodução e varredura sobre a Parte 1 (parecer de P09, privado) | Os 7 diagramas Mermaid só foram conferidos por leitura; renderização no GitHub pendente do responsável |
| **Compila sem erros e sem avisos** (E, R6) | `Directory.Build.props` (`TreatWarningsAsErrors`) | build `-warnaserror` | Executor, 20/10/2026, rc 0. CI (`15a657f`): etapa Build `success` | Escopo: compilação, não qualidade |
| **Registro de movimentações e posição por instante** (E, H1–H11) | API de lançamentos; posição por `SUM` no banco, sem saldo materializado | T01 a T14 (seção 1.2); ensaio do ciclo P04 (laço fechado, anterior à Outbox) | Executor e governança em P00–P05 (datas na seção 2.1); T09 não aplicável (H1) | Carga do P04 não vale para o POST atual (Outbox); sem correção nem estorno (T14 parcial) |
| **Consolidado diário** (O, H15) | Outbox, Worker, projeção em outro banco, API do consolidado; materializado, eventualmente consistente com `freshness` | C1 a C15 (seção 9.2), R01 a R13j (seções 10 a 13) | Executor, 19–20/10/2026; testes reexecutados pela governança (344 e 365 nomes, 20/10/2026) | Dia de negócio depende dos dados de fuso da máquina (divergência 2000–2003) |
| **Independência da escrita** (O; A1) | movimento e evento na mesma transação; readiness da escrita sem a projeção | C1–C5, C13; sondagem real (P07); F08 a/b/c (600/600 POST); [demonstração](demonstracao-independencia.md) com script público (seções 1 e 2) | Executor; testes reexecutados pela governança; sondagem e F08 conferidos por leitura, não reproduzidos. **Demonstração:** executado pelo executor em 21/10/2026 sobre `48a08a0` (cenários `Workers` e `Projection`, 10 de 10 `201` durante a falha); reproduzida pela governança em 21/10/2026 (script público, dois cenários, rc 0, 11/11; permalinks e vínculo de versão conferidos) | Origem saudável é pressuposto |
| **Processamento e recuperação** (O; A2) | classes de falha, quarentena, lease, bootstrap, `--verify`, reconstrução em banco paralelo | C4, C6–C12, R01–R13j; sondagens reais; recuperação observada em [demonstracao-independencia.md](demonstracao-independencia.md) (seção 3) | Executor; testes reexecutados pela governança. **Demonstração:** executado pelo executor em 21/10/2026 sobre `48a08a0` (convergência sem intervenção em 3,47 s e 8,08 s; 16 = 16 = 16 ids); reproduzida pela governança em 21/10/2026 (script público, dois cenários, rc 0, 11/11; permalinks e vínculo de versão conferidos) | Reconstrução exercitada em dezenas de movimentos; 100.000 não medido; sem retorno transparente |
| **50 req/s com perda de até 5%** (O; A3) | gerador de chegadas abertas; L1, L2, L3, F08 | [carga-consolidado.md](carga-consolidado.md); `LoadGeneratorTests`; quadro e L1 novo em [demonstracao-independencia.md](demonstracao-independencia.md) (seção 4) | Executor, 20/10/2026. **Governança: L1 reproduzido** (uma repetição, golden próprio, 3.000/3.000, parecer de P08 C, 20/10/2026); L2, L3, F08, mutações e EXPLAIN conferidos por leitura. **Demonstração:** L1 novo executado pelo executor em 21/10/2026 sobre `48a08a0` (3.000 úteis de 3.000, p99 23,8 ms); reproduzida pela governança em 21/10/2026 (script público, dois cenários, rc 0, 11/11; permalinks e vínculo de versão conferidos) | Máquina única; critérios (1.000 ms, 60 s, 120 s, massa) são hipóteses de laboratório; não vale para produção |
| **Alta disponibilidade** (A4) | **nenhuma** (proposta) | [alta-disponibilidade.md](alta-disponibilidade.md); fronteira de falha declarada em [demonstracao-independencia.md](demonstracao-independencia.md) (seções 1.4 e 3.4) | Documento escrito e aprovado no conteúdo pela governança (parecer de P09 Parte 1, 20/10/2026). Nada executado. A demonstração do consolidado (executada em 21/10/2026 sobre `48a08a0`) não é alta disponibilidade; reproduzida pela governança em 21/10/2026 (script público, dois cenários, rc 0, 11/11; permalinks e vínculo de versão conferidos) | Proposta; balanceador, réplicas, failover e rede real não exercitados; sem RTO/RPO medidos |
| **Aderência ao enunciado** (A5) | [escopo.md](escopo.md): requisitos × orientações × hipóteses | revisão documental | Executor, 20/10/2026 | — |
| **Segurança** (O) | JWT com escopo por cliente, `404` fora do escopo, validação, limite de 16 KB, logs sem segredos | T11, T11c, T13, `Logs_DoNotContainSecretsOrBodies`; varredura do histórico (seção 8.5) | Executor; varredura do histórico na versão A (19/10/2026) | Chave simétrica de laboratório; sem rotação, auditoria de acesso nem criptografia em repouso própria |

A matriz dos cinco apontamentos, com quem executou cada item, está na seção 15.1; as pendências vigentes, na seção 6.

## 1. Requisitos → implementação → teste

Nomes de teste: o `DisplayName` (como aparece no relatório de testes) seguido do método C# entre parênteses. Testes de unidade sem `DisplayName` aparecem como `Classe.Método`. A "última execução" dos testes é a desta tentativa (19/10/2026, duas rodadas, ver seção 2), exceto onde outra fonte é indicada.

### 1.1 Requisitos obrigatórios da entrega

| Requisito (em redação própria) | Onde está | Evidência | Estado | Última execução |
|---|---|---|---|---|
| R1. A implementação é em C# | `src/`, `tools/`, `tests/` (`net10.0`, `Directory.Build.props`) | restore e build de 7 projetos | executado e aprovado | 19/10/2026, esta tentativa |
| R2. Há testes automatizados | `tests/FinancialTransactions.UnitTests` (92) e `tests/FinancialTransactions.IntegrationTests` (45) | duas rodadas, 137 testes aprovados, nenhum ignorado | executado e aprovado | 19/10/2026, esta tentativa |
| R3. Código em repositório público no GitHub | `https://github.com/antonio-leonardo/FinancialTransactions` | repositório público desde 18/10/2026 (constatado pela governança); clone novo em contexto sem credencial e roteiro de aceitação (seção 8) | executado e aprovado | 19/10/2026, aceitação da versão A (seção 8) |
| R4. README com instruções claras para executar localmente | `README.md` | clone novo em contexto sem credencial e roteiro de aceitação (seção 8); passo a passo do README seguido no clone (seção 8.4) | executado e aprovado, com a ressalva de que o `docker compose up -d --wait` não foi exercitado a partir do clone (seção 8.4) | 19/10/2026, aceitação da versão A (seção 8) |
| R5. Toda a documentação dentro do repositório | `README.md`, `docs/escopo.md`, `docs/arquitetura.md`, `docs/verificacao.md`, `docs/carga.md` | verificação de links, âncoras e privacidade (seção 2.3) | executado e aprovado | 19/10/2026, esta tentativa |
| R6. Compila sem erros e sem warnings | `Directory.Build.props` (`TreatWarningsAsErrors`, `latest-recommended`) | `restore` e `build -warnaserror` com `rc` 0; `0 Warning(s)`, `0 Error(s)`; CI: restore e build sem diagnósticos de warning de compilação | executado e aprovado | 19/10/2026, esta tentativa e CI (run 39201091893) |

### 1.2 Cenários T01 a T16

| ID | Cenário | Onde está | Testes | Estado | Última execução |
|---|---|---|---|---|---|
| T01 | Fluxo básico | `Api/Endpoints/MovementEndpoints.cs`, `Api/Movements/RegisterMovementHandler.cs`, `Infrastructure/PositionQueries.cs` | `T01_CreditAndDebit_PersistOnce_AndPositionMatches` (`T01CreditAndDebitPersistOnceAndPositionMatches`); roteiro do README (seção 2.2) | executado e aprovado | 19/10/2026, esta tentativa |
| T02 | Dois cortes históricos e consolidação por cliente | `Infrastructure/PositionQueries.cs`, `Api/Endpoints/PositionEndpoints.cs` | `T02_TwoHistoricalCuts_ReturnDifferentBalances` (`T02TwoHistoricalCutsReturnDifferentBalances`), `T02b_SameInstantDifferentOffset_SameBalance` (`T02bSameInstantDifferentOffsetSameBalance`), `T02c_BeforeOpening_IsInsufficientHistory_NotZero` (`T02cBeforeOpeningIsInsufficientHistoryNotZero`), `T02d_CustomerPosition_SumsOpenAccounts` (`T02dCustomerPositionSumsOpenAccounts`), `T02e_CustomerPosition_ExcludesAccountsOpenedAfterAsOf` (`T02eCustomerPositionExcludesAccountsOpenedAfterAsOf`), `T02f_CustomerPosition_OpenAccountWithoutMovementsIsZero` (`T02fCustomerPositionOpenAccountWithoutMovementsIsZero`) | executado e aprovado | 19/10/2026, esta tentativa |
| T03 | Fronteiras, origem e entradas | `Domain/MovementRules.cs`, `Domain/MovementAmount.cs`, `Domain/IdempotencyKey.cs`, `Api/Movements/RegisterMovementHandler.cs` | `T03_EffectiveAtEqualToOpenedAt_IsAccepted` (`T03EffectiveAtEqualToOpenedAtIsAccepted`), `T03_Description_140Accepted_141Rejected` (`T03Description140Accepted141Rejected`), `T03_Amount_CeilingAccepted_CeilingPlusCentRejected` (`T03AmountCeilingAcceptedCeilingPlusCentRejected`), `T03_IdempotencyKey_128Accepted_129Rejected` (`T03IdempotencyKey128Accepted129Rejected`), `T03_NonexistentAccount_Is404` (`T03NonexistentAccountIs404`), `T03_ExistingAccountWithoutMovements_IsZero` (`T03ExistingAccountWithoutMovementsIsZero`), `Validation_RejectsScaleFutureAndBeforeOpening` (`ValidationRejectsScaleFutureAndBeforeOpening`); unidade: `MovementRulesTests.EffectiveAtExactlyAtToleranceIsAccepted`, `MovementRulesTests.EffectiveAtOneSecondBeyondToleranceIsRejectedAsFuture`, `MovementRulesTests.EffectiveAtBeforeOpeningIsRejected`, `MovementRulesTests.EffectiveAtEqualToOpeningIsAccepted`, `MovementRulesTests.AsOfWithinToleranceIsAccepted`, `MovementRulesTests.AsOfBeyondToleranceIsRejectedAsValidationError`, `IdempotencyKeyTests.TryValidateAccepts128Chars`, `IdempotencyKeyTests.TryValidateRejects129Chars` | executado e aprovado | 19/10/2026, esta tentativa |
| T04 | Informação tardia | `Api/Movements/RegisterMovementHandler.cs`, `Infrastructure/PositionQueries.cs` | `T04_LateInformation_ChangesHistoricalPositions` (`T04LateInformationChangesHistoricalPositions`) | executado e aprovado | 19/10/2026, esta tentativa |
| T05 | Repetição sequencial | `Api/Movements/RegisterMovementHandler.cs` (releitura após `23505`), `Domain/IdempotencyKey.cs` (fingerprint) | `T05_SameKeySameBody_ReplaysSameMovement` (`T05SameKeySameBodyReplaysSameMovement`), `T05b_SameKeyDifferentBody_IsConflict_NoEffect` (`T05bSameKeyDifferentBodyIsConflictNoEffect`), `Fingerprint_ReplayWithSixDigitsReturnedBy201_Is200` (`FingerprintReplayWithSixDigitsReturnedBy201Is200`), `F1c_PostZAndOffsetSameInstant_ReplayAcrossRepresentations` (`F1cPostZAndOffsetSameInstantReplayAcrossRepresentations`); unidade: `IdempotencyKeyTests.FingerprintIsSameForSameInstantInDifferentOffsets`, `IdempotencyKeyTests.FingerprintDiffersWhenDescriptionDiffers`; roteiro do README (replay e conflito) | executado e aprovado | 19/10/2026, esta tentativa |
| T06 | Repetição simultânea | UNIQUE `ux_movements_account_id_idempotency_key` (`Infrastructure/Configurations/MovementConfiguration.cs`) e o tratamento em `RegisterMovementHandler` | `T06_SameKeySameBody_Concurrent_OneCreatedRestReplayed` (`T06SameKeySameBodyConcurrentOneCreatedRestReplayed`), `T06b_SameKeyDifferentBodies_Concurrent_OneCreatedRestConflict` (`T06bSameKeyDifferentBodiesConcurrentOneCreatedRestConflict`); unidade: `IsIdempotencyConflict_TrueOnlyForIdempotencyConstraint` (`IsIdempotencyConflictTrueForIdempotencyConstraint`), `IsIdempotencyConflict_FalseForPrimaryKeyViolation` (`IsIdempotencyConflictFalseForPrimaryKeyViolation`), `IsIdempotencyConflict_FalseForOtherSqlState` (`IsIdempotencyConflictFalseForOtherSqlState`), `IsIdempotencyConflict_FalseWithoutInnerException` (`IsIdempotencyConflictFalseWithoutInnerException`) | executado e aprovado | 19/10/2026, esta tentativa (duas rodadas). Limite: a largada coordenada prova a coordenação no teste, não todos os interleavings possíveis; intermitência não se elimina com duas rodadas |
| T07 | Chave reutilizada e conteúdo igual legítimo | idem T05 | `T05b_SameKeyDifferentBody_IsConflict_NoEffect` (mesma chave, conteúdo diferente) e `T07b_DistinctKeysSameContent_AreTwoLegitimateMovements` (`T07bDistinctKeysSameContentAreTwoLegitimateMovements`) | executado e aprovado | 19/10/2026, esta tentativa |
| T08 | Operações distintas concorrentes | `RegisterMovementHandler` (inserções independentes), `PositionQueries` | `T08_DistinctOperations_Concurrent_BothPreserved` (`T08DistinctOperationsConcurrentBothPreserved`), `T08b_TwentyDistinctCredits_Concurrent_AllPreserved` (`T08bTwentyDistinctCreditsConcurrentAllPreserved`) | executado e aprovado | 19/10/2026, esta tentativa |
| T09 | Regra sobre agregado | nenhum | nenhum | **não aplicável**: o sistema é de registro e permite saldo negativo (H1); sem limite sobre o agregado, não há regra que dois débitos concorrentes possam violar. Passa a valer se H1 mudar | n/a |
| T10 | Falha antes e depois do commit | `Api/Movements/IRegisterMovementFailurePoints.cs` e `RegisterMovementHandler` (antes e depois do `Commit`) | `T10a_FailureBeforeCommit_NothingPersisted_RetryCreates` (`T10aFailureBeforeCommitNothingPersistedRetryCreates`), `T10b_FailureAfterCommit_Persisted_RetryReplays` (`T10bFailureAfterCommitPersistedRetryReplays`), `Composition_ProductionHost_ResolvesNoFailurePoints` (`CompositionProductionHostResolvesNoFailurePoints`) | executado e aprovado | 19/10/2026, esta tentativa |
| T11 | Autenticação e autorização | `Api/Program.cs` (JWT), `Api/Auth/` (`AccountAccess`, `CustomerScope`) | `T11_NoToken_Is401` (`T11NoTokenIs401`), `T11b_OtherCustomer_Is404_EvenOnReplay` (`T11bOtherCustomerIs404EvenOnReplay`), `T11c_InvalidTokens_Are401_NothingPersisted` (`T11cInvalidTokensAre401NothingPersisted`), `T11c_ControlToken_IsAccepted` (`T11cControlTokenIsAccepted`) | executado e aprovado | 19/10/2026, esta tentativa |
| T12 | Precisão e seleção | `Domain/MovementAmount.cs`, `Infrastructure/PositionQueries.cs` | `T12_HundredCents_SumExactlyOneReal` (`T12HundredCentsSumExactlyOneReal`), `T12_Selection_OtherAccountAndOtherCustomerDoNotLeak` (`T12SelectionOtherAccountAndOtherCustomerDoNotLeak`), `PositionQuery_AggregatesInDatabase` (`PositionQueryAggregatesInDatabase`); unidade: `MovementAmountTests.TryCreateRejectsInvalidAmounts` (casos `"10.005"` e `"0.001"` rejeitados por escala), `MovementAmountTests.TryCreateAcceptsValidAmounts`, `MovementRulesTests.SignedAmountIsPositiveForCreditAndNegativeForDebit` | executado e aprovado | 19/10/2026, esta tentativa |
| T13 | Indisponibilidade e recuperação | `Api/Http/ApiExceptionHandler.cs`, `Api/Health/`, `Api/Http/RequestBodyLimit.cs` | `T13a_PostWhenDatabaseUnreachable_Is503_WithinDeadline` (`T13aPostWhenDatabaseUnreachableIs503WithinDeadline`), `T13b_GetPositionWhenDatabaseUnreachable_Is503_NotZero` (`T13bGetPositionWhenDatabaseUnreachableIs503NotZero`), `T13c_ReadyIs503_LiveIs200_WhenDatabaseUnreachable` (`T13cReadyIs503LiveIs200WhenDatabaseUnreachable`), `T13d_ReadyIs200_WhenDatabaseAvailable` (`T13dReadyIs200WhenDatabaseAvailable`), `Health_IsAnonymous_ApiIsNot` (`HealthIsAnonymousApiIsNot`), `RequestBody_Over16KB_Is413_NothingPersisted` (`RequestBodyOver16KbIs413NothingPersisted`), `Logs_DoNotContainSecretsOrBodies` (`LogsDoNotContainSecretsOrBodies`); unidade: `IsDependencyUnavailable_TrueForTimeoutException`, `IsDependencyUnavailable_TrueForSocketException`, `IsDependencyUnavailable_TrueForTransientNpgsqlException`, `IsDependencyUnavailable_FalseForInvalidOperationException`, `IsDependencyUnavailable_FalseForUniqueViolation`; sondagem de reinício (seção 4) | executado e aprovado | testes: 19/10/2026, esta tentativa; sondagem: 19/10/2026, executor local (P03) e governança local (P03) |
| T14 | Correção e reconciliação | reconciliação da ferramenta `tools/FinancialTransactions.LoadTest/Reconciliation.cs` e, desde P07, `Consolidation/BootstrapReconciler.cs` | reconciliação pós-carga ([carga.md](carga.md)); reconciliação do conjunto inicial por identidade (C2, C9a a C9h); não há teste automatizado de correção | **parcial**: reconciliação pós-carga executada nas duas execuções do ensaio, sem divergências; reconciliação do consolidado (contagem, créditos, débitos, líquido e ids por conta e dia) executada no bootstrap; correção e estorno fora de escopo (ver seção 6) | 19/10/2026, executor local e governança local (P04) |
| T15 | Mensageria (comunicação assíncrona) | `Domain/OutboxEvent.cs`, `Api/Movements/RegisterMovementHandler.cs`, `Consolidation/` (consumidor, applier), `Consolidation.Worker` | cenários C1 a C15 (seção 9.2) | **executado e aprovado** nos cenários do ciclo P07: comunicação assíncrona por trabalho durável no banco, sem broker; broker é alternativa documentada (ADR-10 em [arquitetura.md](arquitetura.md)). **Delta:** até P06 esta linha dizia "não aplicável" por H12, que vedava broker e Outbox | seção 9.3 |
| T15 (histórico, versão A) | Mensageria | nenhum | nenhum | **não aplicável**: H12 (sem broker nem Outbox). Preservada para o histórico; substituída pela linha acima a partir de P07 | n/a |
| T16 | Carga e aceitação | `tools/FinancialTransactions.LoadTest`, [carga.md](carga.md), `.github/workflows/ci.yml` | ensaio de carga (duas execuções) e CI | carga executada; CI executada com sucesso em A (run 39219056456); aceitação por checkout novo em A (seção 8) | carga: executor local e governança local, 19/10/2026, P04; CI e aceitação: ver seções 5, 5.2 e 8 |

Outros comportamentos do contrato com teste próprio:

| Comportamento | Testes |
|---|---|
| Gramática de instantes na entrada | `F1a_PostEffectiveAtWithoutOffset_Is422_NothingPersisted` (`F1aPostEffectiveAtWithoutOffsetIs422NothingPersisted`), `F1b_PostEffectiveAtNumber_Is422` (`F1bPostEffectiveAtNumberIs422`), `F1d_GetAsOfHourOnly_DateOnly_Regional_Text_Are422` (`F1dGetAsOfHourOnlyDateOnlyRegionalTextAre422`), `F1e_GetAsOfCompleteWithoutOffset_IsUtc` (`F1eGetAsOfCompleteWithoutOffsetIsUtc`); unidade: `InstantParserTests.WithOffsetAcceptsZAndNumericOffsetsWithOptionalFraction`, `InstantParserTests.WithOffsetRejectsMissingOffsetPartialRegionalTextAndInvalid`, `InstantParserTests.UtcOrOffsetAcceptsCompleteWithoutOffsetAsUtc`, `InstantParserTests.UtcOrOffsetRejectsDateOnlyHourOnlyRegionalAndText` |
| Normalização de instantes | `MovementTests.CreateNormalizesInstantsToUtcWithMicrosecondPrecision`, `MovementTests.NormalizeForPersistenceKeepsAlreadyNormalizedInstants`, `IdempotencyKeyTests.FingerprintIsStableAcrossMicrosecondTruncation` |

## 2. Comandos e resultados datados

### 2.1 Por ciclo

Os dados de P00 a P04 vêm dos checkpoints e dos pareceres de cada ciclo e dos relatórios de teste arquivados; a coluna "o que a fonte comprova" não preenche o que a fonte não registra. Datas locais (UTC−03:00); a data do parecer da governança pode ser posterior à da execução.

| Ciclo | Data (execução) | Fonte e executor | Comandos | Rodadas de teste que a fonte registra e contagens | Observações |
|---|---|---|---|---|---|
| P00 | 18/10/2026 17:29 | *executor local* (checkpoint do ciclo); *governança local* 18/10/2026 (parecer) | `msbuild -target:Restore -warnaserror`, `build -c Release -warnaserror`, `dotnet test` (unidade) | executor: 1 rodada, 12/12 (só unidade; ainda não havia integração). Governança: reexecução rc 0, 12/12 | `rc` 0/0/0. A primeira execução do build reprovou por CA1822; a segunda é a válida. Cópia limpa também compilou (governança) |
| P01 | 18/10/2026 19:11 | *executor local*; *governança local* 18/10/2026 | `msbuild -target:Restore -warnaserror`, `build -c Release --no-restore --no-incremental -warnaserror`, `dotnet test` | executor: 1 rodada, 45/45 (unidade) e 12/12 (integração). Governança: 45/45 e 12/12 | `rc` 0/0/0; migration aplicada em banco novo (governança) |
| P01fix | 18/10/2026 22:14 (19/10/2026 01:14 UTC); parecer 19/10/2026 | *executor local*; *governança local* | idem | executor: 1 rodada, 82/82 e 18/18. Governança: 82/82 e 18/18 | `rc` 0/0/0; contrato temporal (F1a a F1e) |
| P02 | 18/10/2026 23:39 (19/10/2026 02:39 UTC); parecer 19/10/2026 | *executor local*; *governança local* | idem | executor: 2 rodadas, 89/89 e 37/37 em ambas. Governança: 2 rodadas iguais | `rc` 0 em todas; os cenários concorrentes passaram nas 4 rodadas (limite descrito em T06) |
| P03 | 19/10/2026 00:30 | *executor local*; *governança local* 19/10/2026 | idem, com TRX por projeto | executor: 2 rodadas, 92/92 e 45/45 (a primeira passagem tinha 94 de unidade, incluindo dois testes de preenchimento, removidos depois). Governança: 2 rodadas, 92/92 e 45/45, 137 nominais | `rc` 0 em todas; sondagem de reinício (seção 4) |
| P04 | 19/10/2026 01:24–01:25 | *executor local*; *governança local* 19/10/2026 | idem; ensaio de carga | executor: 2 rodadas, 92/92 e 45/45; 137 nominais idênticos aos de P03. Governança: 2 rodadas iguais | `rc` 0 em todas; 7 projetos compilados |
| P05 | 19/10/2026 03:11–03:12 | *esta tentativa* | `dotnet msbuild FinancialTransactions.sln -target:Restore -warnaserror`; `dotnet build FinancialTransactions.sln -c Release --no-restore --no-incremental -warnaserror`; `dotnet test tests/FinancialTransactions.<UnitTests\|IntegrationTests> -c Release --no-build --settings tests/vstest.runsettings` (TRX por projeto) | 2 rodadas: unidade 92/92 e integração 45/45 em ambas; 137 nominais idênticos entre as rodadas e aos de P04 | `rc_restore` 0, `rc_build` 0 (`0 Warning(s)`, `0 Error(s)`), `rc` dos quatro `dotnet test` 0; SDK 10.0.402 |

Nenhum dos contadores acima é da CI. Os da CI estão na seção 5.

### 2.2 Roteiro do README (esta tentativa)

Executado em 19/10/2026 contra um banco descartável criado e removido pela própria execução, com a API iniciada por ela (processo próprio, porta `5100` livre antes e depois). O banco `financialtransactions` e qualquer banco alheio não foram tocados. Antes do primeiro POST, o destino foi confirmado: o banco descartável tinha 0 movimentos e 3 contas.

**Adaptações de laboratório em relação ao texto do README**, todas declaradas: (1) banco descartável: `dotnet ef database update` com `--connection` e a variável `FT__ConnectionStrings__FinancialTransactions` apontando para ele, em vez do banco de desenvolvimento; (2) a API foi iniciada em processo separado, com saída em arquivo; (3) as chamadas HTTP foram feitas com `Invoke-WebRequest -SkipHttpErrorCheck` (para capturar status e corpo bruto, inclusive o do `422`), em vez de `Invoke-RestMethod`; (4) token e chave nunca foram gravados. A verificação por checkout novo, incluindo o `git clone`, está na seção 8.4. As mesmas chamadas, na ordem do README mais a repetição e o conflito:

| # | Chamada | Status | Campos-chave | Esperado | Resultado |
|---|---|---|---|---|---|
| 1 | `POST` crédito 100,00, chave `exemplo-credito-001` | 201 | `Location` para a conta A1; `amount` 100.00 | 201 + `Location` | OK |
| 2 | `GET` da `Location` | 200 | mesmo `movementId` do POST | 200 | OK |
| 3 | `POST` débito 40,00, chave `exemplo-debito-001` | 201 | `type` Debit, `amount` 40.00 | 201 | OK |
| 4 | `GET` posição da conta às 10:30 (−03:00) | 200 | `balance` 100.00, `movementCount` 1 | 100.00 e 1 | OK |
| 5 | `GET` posição da conta às 11:30 (−03:00) | 200 | `balance` 60.00, `movementCount` 2 | 60.00 e 2 | OK |
| 6 | `GET` consolidação do cliente A em 01/10/2026 | 200 | `balance` 60.00; contas A1 (60.00, 2) e A2 (0, 0); `excludedAccounts` vazio | 60.00, A1 e A2, vazio | OK |
| 7 | `POST` do passo 1 repetido (mesmo corpo, inclusive `description`) | 200 | mesmo `movementId` do passo 1 | 200 e mesmo id | OK |
| 8 | mesma chave, valor 101,00 | 422 | `code` `idempotency_key_conflict` | 422 e esse `code` | OK |
| 9 | contagem de movimentos no banco descartável | n/a | 2 (o replay e o conflito não criaram linha) | 2 | OK |

No fim da execução, o processo da API foi encerrado, o ambiente restaurado e o banco descartável removido; nenhum banco `ft_doc_*` ficou, e o container permaneceu `healthy`. Uma primeira execução do mesmo auxiliar apresentou um defeito **do auxiliar** (a `Location`, que é um caminho relativo, foi usada de forma que a chamada 2 foi enviada à raiz e recebeu `404`; as outras 8 chamadas passaram); o auxiliar foi corrigido e o roteiro foi executado de novo do zero, com banco novo. O resultado acima é o da segunda execução. Na segunda execução, o auxiliar terminou com código de saída 1 por uma falha na varredura de vazamento que roda depois dos resultados e da limpeza (leitura de um arquivo de log vazio); todos os resultados da tabela e a limpeza já estavam concluídos e a varredura já havia coberto os dois arquivos com conteúdo, sem ocorrência. O defeito foi corrigido no auxiliar.

### 2.3 Links, âncoras, diagramas e privacidade (esta tentativa)

Resultado do verificador de links, âncoras, títulos únicos, tipos de diagrama Mermaid e privacidade, executado em 19/10/2026, com o resultado ao fim desta seção.

Limites declarados do verificador: não é um analisador de Markdown; cobre apenas links Markdown em linha, com caminho relativo e âncora opcional, fora de cercas de código; exige títulos únicos por documento; confere de cada diagrama Mermaid apenas a palavra inicial (`flowchart` ou `sequenceDiagram`), sem validar a sintaxe. Os diagramas foram conferidos **por leitura**; não há renderizador nesta máquina e a renderização no GitHub é de P06.

**Resultado (19/10/2026, esta tentativa):** 5 documentos examinados (`README.md` e os quatro de `docs/`); **0** problemas de link, âncora, título repetido ou tipo de diagrama; **3** diagramas Mermaid, todos em [arquitetura.md](arquitetura.md) (um `flowchart` e dois `sequenceDiagram`); **0** ocorrências de privacidade fora das URLs permitidas do repositório. A primeira execução do verificador devolveu 1 problema, que era uma falsa detecção: a frase que descreve os limites do verificador continha, literalmente, a sintaxe de um link; foi reescrita e o verificador foi executado de novo. O verificador não detecta conteúdo reproduzido do enunciado; essa conferência é por leitura.

## 3. Ambiente

| Item | Valor | Fonte |
|---|---|---|
| SDK .NET | 10.0.402 (`global.json` declara 10.0.301 com `rollForward: latestFeature`) | esta tentativa |
| Linguagem e destino | C#, `net10.0`, `Nullable` e `ImplicitUsings` habilitados | `Directory.Build.props` |
| EF Core | 10.0.13 (`Microsoft.EntityFrameworkCore`, `Relational`, `Design`); ferramenta `dotnet-ef` 10.0.13 | arquivos de projeto, `.config/dotnet-tools.json` |
| Provider PostgreSQL | `Npgsql.EntityFrameworkCore.PostgreSQL` 10.0.3; `Npgsql` 10.0.3 (testes e ferramenta de carga) | arquivos de projeto |
| Autenticação | `Microsoft.AspNetCore.Authentication.JwtBearer` 10.0.13; `Microsoft.IdentityModel.JsonWebTokens` 8.23.0 (ferramentas) | arquivos de projeto |
| Testes | xUnit 2.9.3, `xunit.runner.visualstudio` 3.1.4, `Microsoft.NET.Test.Sdk` 17.14.1, `Microsoft.AspNetCore.Mvc.Testing` 10.0.13, runner VSTest | arquivos de projeto |
| PostgreSQL | 17.11, imagem `postgres:17`, no container do projeto, `127.0.0.1:15432` | esta tentativa |
| Docker | engine 29.8.2 (Docker Desktop, engine Linux) | esta tentativa |
| Sistema operacional | Windows 11, build 10.0.26100 | esta tentativa |
| PowerShell | 7.6.7 (o README pede 7.4 ou superior) | esta tentativa |
| CI | Ubuntu 24.04.5, SDK 10.0.402, PostgreSQL 17.11 | leitura do auditor-master (log do run) |

O ensaio de carga tem o seu ambiente próprio (CPU, memória, configuração do PostgreSQL) em [carga.md](carga.md).

## 4. Cenários de falha e carga

### 4.1 T13: sondagem de reinício do PostgreSQL

Fonte: *executor local* (P03, 19/10/2026), com a sondagem repetida pela *governança local* contra Kestrel real. Não foi repetida nesta tentativa. A sondagem usa a API em `Production` contra o container do projeto, em banco descartável:

1. crédito `p03-antes` → `201`; readiness `200`;
2. `docker compose stop postgres`;
3. com o banco parado: readiness `503` em 0,03 s; liveness `200`; `POST` `p03-durante` → `503 dependency_unavailable` em 2,17 s; `GET` de posição → `503` em 2,05 s, sem campo de saldo;
4. `docker compose start`; container de volta a `healthy`;
5. readiness `200` já na primeira sonda; posição com `balance` 100 e `movementCount` 1 (o dado confirmado sobreviveu); `p03-depois` → `201`; repetição de `p03-antes` → `200` (replay); repetição de `p03-durante` → `201` (a tentativa que recebeu `503` nunca chegou ao commit);
6. corpo de 64 KB com `Content-Length` → `413 payload_too_large`; corpo de 64 KB em transferência fragmentada → `413` sem corpo (camada do servidor); `COUNT(*)` final 3 (o roteiro original previa 2: era inconsistência do roteiro, não da API);
7. API encerrada, banco removido.

Os prazos refletem `Timeout=5;Command Timeout=15` e um prazo de 2 s configurado nos testes de integração; não são garantias de produção.

### 4.2 Ensaio de carga

> **Escopo:** este ensaio é do ciclo P04 (commit `4463e38`), anterior à Outbox e aos processos do consolidado diário. Não mede o consolidado nem o POST com a segunda escrita (a Outbox); não é evidência de P07. O ensaio de 50 req/s do consolidado está pendente (P08).

Detalhes, método, ambiente e limites em [carga.md](carga.md). Fonte: *executor local* e *governança local* (P04, 19/10/2026; a governança repetiu o ensaio com os mesmos parâmetros). Duas execuções de 60 s medidos, 32 trabalhadores em laço fechado, banco com 100.000 movimentos pré-carregados em 400 contas:

| Execução | Requisições medidas | Vazão (mistura de operações) | Erros técnicos | Reconciliação |
|---|---:|---:|---:|---|
| 1 | 37.658 em 60,03 s | 627 req/s | 0 | sem divergências |
| 2 | 38.739 em 60,03 s | 645 req/s | 0 | sem divergências |

Percentis p95 (ms), execuções 1 e 2: POST novo 89,2 e 85,9; POST replay 116,3 e 109,8; GET posição por conta 39,7 e 38,1; GET posição por cliente 51,9 e 51,3.

**Redação delimitada:** no ensaio local descrito, a aplicação sustentou a mistura de operações e apresentou os resultados registrados, sem divergências detectadas na reconciliação. A taxa é de **requisições mistas em laço fechado**: não é capacidade máxima, não é de transações financeiras por segundo e não vale para produção. As metas H-L1 a H-L4 são hipóteses do projeto, não requisitos de cliente.

## 5. CI

Fonte: *CI* (consulta anônima à API pública do GitHub, sem credencial) e *leitura do auditor-master* (log). Em 19/10/2026 o histórico do repositório foi reescrito pelo responsável do projeto (reautoria dos commits): **todos os SHAs mudaram e as árvores são idênticas**. Os registros da seção 5.1 usam os SHAs do histórico anterior; a árvore de cada um foi confrontada com o commit equivalente do histórico atual pela API (`abcdf19…` e `4463e38`: mesma árvore `47822e0742bde2fd30f662f2e8604d65a744e6a2`; `9ded7b0…` e `47909c9`: mesma árvore `3d38c79308fd3e1c388bf0ee0d6952ac0068fb5b`).

### 5.1 Versão anterior (histórico anterior à reescrita)

| Item | Valor |
|---|---|
| Workflow | `ci` (`.github/workflows/ci.yml`), disparado em `push` na `main` e em `pull_request` |
| SHA | `abcdf19c073dc95505010a593126248dc2bcc79a` (SHA do histórico anterior; commit equivalente atual: `4463e38`) |
| Execuções para esse SHA | 1: [run 39201091893](https://github.com/antonio-leonardo/FinancialTransactions/actions/runs/39201091893), número 3, tentativa 1, evento `push`, criada em 19/10/2026 04:45 UTC, `status` `completed`, `conclusion` `success` |
| Job | `build-and-test` (id 117894019834), 04:45:41 a 04:46:42 UTC, `success`. Etapas: preparação do job, inicialização do contêiner PostgreSQL, checkout, setup do .NET, Restore, Build, Test e finalizações, todas `success` |
| Comandos do workflow | `dotnet msbuild FinancialTransactions.sln -target:Restore -warnaserror`; `dotnet build FinancialTransactions.sln -c Release --no-restore --no-incremental -warnaserror`; `dotnet test FinancialTransactions.sln -c Release --no-build --settings tests/vstest.runsettings`, com `FT_TEST_ADMIN_CONNECTION` para o serviço `postgres:17` |
| Ambiente e contadores (log, **leitura do auditor-master**, 19/10/2026) | Ubuntu 24.04.5, SDK 10.0.402, PostgreSQL 17.11; restore e build com `0 Warning(s)`; sete assemblies; `Passed! 92` (unidade) e `Passed! 45` (integração) |
| Outros commits | segundo a *governança local* em 19/10/2026, o workflow também executou com `success` nos commits anteriores a esse; **não reconferido** |

Redação sobre avisos, **nessa versão do workflow**: restore e build **sem diagnósticos de warning de compilação**. O log continha avisos de outra natureza, que não são warnings de build: depreciação do módulo `punycode` (Node); aviso de que `actions/checkout@v4` e `actions/setup-dotnet@v4` declaram Node.js 20 e rodaram em Node.js 24; e mensagens `FATAL: database "ft" does not exist` do serviço PostgreSQL, porque a sonda `pg_isready -U ft` não informa a base (`POSTGRES_DB=postgres`). Foram tratados na manutenção da seção 5.2.

### 5.2 Execução da versão A e manutenção do workflow

Versão A: commit `87645ed9e3ed57328eabbd55917062188f370547`, branch `main`. Fonte: *CI* (consulta pela API pública em 19/10/2026, 08:03 UTC, por esta aceitação) e *leitura do auditor-master* onde indicado.

| Item | Valor |
|---|---|
| Execuções para o SHA de A | 1 (todas as execuções e tentativas do workflow `ci` para esse SHA): [run 39219056456](https://github.com/antonio-leonardo/FinancialTransactions/actions/runs/39219056456), número 6, tentativa 1, evento `push`, criada em 19/10/2026 07:54 UTC, `status` `completed`, `conclusion` `success` |
| Job | `build-and-test` (id 117949777273), 07:54:40 a 07:55:38 UTC, `success`. Etapas, todas `success`: preparação do job, inicialização do contêiner, `Run actions/checkout@v7`, `Run actions/setup-dotnet@v6`, Restore, Build, Test, finalizações |
| Artefatos | 0 (no run e no repositório) |
| O que a API não mostra | o conteúdo do log; o log do run de A **não foi lido** (exige credencial) |

| Decisão de manutenção | Mudança em `ci.yml` | Estado | Evidência |
|---|---|---|---|
| `GOV-P04-CI-01` (sonda) | `pg_isready -U ft` → `pg_isready -U ft -d postgres` | **aplicada e executada com sucesso**: o serviço ficou saudável e restore, build e testes passaram | CI de A. Efeito no log: *leitura do auditor-master*, em 19/10/2026, do [run 39217752456](https://github.com/antonio-leonardo/FinancialTransactions/actions/runs/39217752456) (número 5, commit do histórico anterior à reescrita, **árvore idêntica à de A**, confirmada pela API): sem a mensagem periódica sobre a base `ft`; permanecem mensagens esperadas de outros cenários (base `ft_test_*` inexistente, violações de unicidade). É leitura de execução remota por terceiro, de um run com árvore idêntica, não do run de A |
| `GOV-P04-CI-02` (`actions/checkout`) | `@v4` → `@v7` | **aplicada e executada com sucesso** | etapa `Run actions/checkout@v7` `success`. Razão da decisão: major maior que 4, `runs.using` `node24`, nenhuma entrada usada pelo workflow, gatilhos `push` e `pull_request` (a mudança de comportamento do v7 é só para `pull_request_target` e `workflow_run`) |
| `GOV-P04-CI-02` (`actions/setup-dotnet`) | `@v4` → `@v6` | **aplicada e executada com sucesso** | etapa `Run actions/setup-dotnet@v6` `success`. Razão: major maior que 4, `node24`, entrada `dotnet-version` inalterada, notas do v6 sem mudança de entradas, saídas ou comportamento |

Da *leitura do auditor-master* do run 39217752456 (mesma árvore): runner 2.337.0, `actions/checkout@v7` e `actions/setup-dotnet@v6` resolvidos, restore e build com `0 Warning(s)`, 92 + 45 testes aprovados, sem os avisos de `punycode` nem de Node.js 20.

**O que a CI não executa:** o ensaio de carga; as sondagens manuais (reinício do PostgreSQL, limite de corpo contra Kestrel real); o roteiro do README. Também não publica relatórios de teste. A CI de A é evidência **dessa versão**; o commit que contiver esta documentação terá a sua própria execução.

### 5.3 CI da baseline do ciclo P09 (fato histórico)

Fonte: *CI* (consulta anônima à API pública do GitHub, sem credencial, feita em 20/10/2026 por esta tentativa, que consultou o run e os jobs do SHA abaixo).

| Item | Valor |
|---|---|
| SHA | `15a657fceb054650839fa945e1ade5a8eaaa31cb` (publicação da parte C do ciclo P08; baseline do ciclo P09) |
| Execuções para esse SHA | 1: [run 39510771565](https://github.com/antonio-leonardo/FinancialTransactions/actions/runs/39510771565), número 16, tentativa 1, evento `push`, criada em 20/10/2026 22:48:46 UTC, `status` `completed`, `conclusion` `success` |
| Job | `build-and-test` (id 118930935468), 22:48:48 a 22:50:54 UTC, `success`. Etapas `Run actions/checkout@v7`, `Run actions/setup-dotnet@v6`, Restore, Build e Test, todas `success` |
| O que a API não mostra | o conteúdo do log; os contadores de testes desse run **não foram lidos** (exigem credencial) |

Este registro é **histórico**: vale para o SHA `15a657f`. Os commits que contêm a documentação do ciclo P09 terão a execução própria deles, e esta documentação não afirma nada sobre ela.

### 5.4 CI do SHA aceito do ciclo P09

Fonte: *CI* (consulta anônima à API pública do GitHub, sem credencial, em 20/10/2026 às 22:21, UTC−03:00, isto é, 21/10/2026 01:21 UTC).

| Item | Valor |
|---|---|
| SHA | `5c3beb0d564050640790ab04c31aac1b6f984f4f` (publicação da parte 1 do ciclo P09; é o SHA aceito na seção 8.9) |
| Execuções para esse SHA | 1 (todas as execuções de todos os workflows): [run 39524995573](https://github.com/antonio-leonardo/FinancialTransactions/actions/runs/39524995573), número 17, tentativa 1, evento `push`, criada em 21/10/2026 01:02:54 UTC, `status` `completed`, `conclusion` `success` |
| Job | `build-and-test` (id 118977434311), 01:02:57 a 01:05:37 UTC, `success`. Etapas `Run actions/checkout@v7`, `Run actions/setup-dotnet@v6`, Restore, Build e Test, todas `success` |
| Artefatos | 0 |
| O que a API não mostra | o conteúdo do log; os contadores de testes desse run **não foram lidos** (exigem credencial). Os contadores 220 + 145 desse SHA vêm do clone (seção 8.9), não da CI |

A CI cobre restore, build `-warnaserror` e testes. Ela **não** executa as sondagens com processos reais, as mutações, os ensaios de carga nem o roteiro do README. O commit que registra esta aceitação é posterior e terá a execução própria dele, sobre a qual esta documentação nada afirma.

## 6. Verificações que não existem ou são parciais (pendências)

| Item | Situação |
|---|---|
| T09 | **Não aplicável**: sistema de registro, sem regra sobre o agregado (H1). |
| T14 | **Parcial**: reconciliação pós-carga executada; não há fluxo de correção ou estorno (fora de escopo). |
| Reconciliação | A reconciliação da ferramenta de carga do ciclo P04 não encontrou divergências nas execuções registradas e **não** teve controles negativos por adulteração. O verificador independente do Worker (`--verify`) tem sete controles negativos (R09c, seção 11.1). |
| Relógio nos testes | `TimeProvider` é injetado e resolvido como `TimeProvider.System`; a fixture de testes não oferece controle ou congelamento sistemático do tempo. |
| Checkout novo | **Verificado em A** (seção 8) e **na versão final** `5c3beb0` (seção 8.9, 20/10/2026): clone novo em contexto Git isolado, roteiro de aceitação com `rc` 0 (220 + 145), README reproduzido com `-SkipComposeUp`. Ressalva que continua: o `docker compose up -d --wait` não foi exercitado a partir de nenhum clone. A reprodução da aceitação final pela governança está pendente. |
| Diagramas Mermaid | Conferidos por leitura; **renderização no GitHub: pendente de confirmação visual do responsável do projeto** (não informada até esta aceitação). Em P07 foram acrescentados três diagramas (componentes, sequência do consolidado e estados do bootstrap) e no ciclo P09 dois diagramas `flowchart` em [alta-disponibilidade.md](alta-disponibilidade.md) (topologia atual e arquitetura proposta): são 7 no total, todos só conferidos por leitura e pela palavra inicial do bloco. |
| `GOV-P04-CI-01` | **Aplicada e executada com sucesso** (seção 5.2). O desaparecimento da mensagem sobre a base `ft` está registrado por leitura de terceiro de um run de árvore idêntica; o log do run de A não foi lido. |
| `GOV-P04-CI-02` | **Aplicada e executada com sucesso** para as duas actions (`checkout@v7`, `setup-dotnet@v6`; seção 5.2). |
| Descrição do repositório (About) | **Pendente (ação do responsável do projeto).** Consulta anônima à API em 19/10/2026: a descrição vigente ainda não está em redação própria (reproduz texto de origem externa); `topics` vazio e `homepage` nulo. Não é corrigível por commit: é metadado do GitHub. Sugestão: "API .NET 10 com histórico de créditos e débitos, consultas temporais de saldo e operações idempotentes. PostgreSQL e JWT para demonstração local." |
| E-mail dos commits | **Feito**: o responsável reescreveu o histórico em 19/10/2026 (reautoria). No clone de A, 9 commits, 18 de 18 campos (autor e committer) com um único domínio de e-mail e um único nome. Commits antigos podem continuar acessíveis por SHA no cache do GitHub; **não verificado**. |
| Histórico: varredura de textos | Feita nas 9 revisões, com ressalva sobre a prova positiva (seção 8.5). |
| Ruído de log no replay | Cada replay gera dois registros de nível `fail` do EF Core (a violação `23505` esperada); sem erro técnico associado (medido em [carga.md](carga.md)). Alertas por nível de log precisariam excluí-los. Não alterado. |
| Máximos de latência da execução 2 do ensaio | Picos isolados (até 401,7 ms) com causa não investigada (ver [carga.md](carga.md)). |
| Divergências README ↔ código da consolidação anterior | Corrigidas no texto, sem alterar código (ver "Decisões do ciclo P05" em [escopo.md](escopo.md)): replay "reconstruído a partir da resposta"; exclusividade da credencial de laboratório; estado da CI; comentário de posição sem chamada às 11:30. Nenhum defeito de código foi encontrado. |
| Alta disponibilidade | **Proposta, não implementada** ([alta-disponibilidade.md](alta-disponibilidade.md)). A topologia atual roda num único host e **não** é alta disponibilidade; balanceador health-aware, réplicas, failover e rede real não foram exercitados. |
| Dois coordenadores em processos separados | A exclusividade da reconstrução foi exercitada por testes (processo único, sessões distintas) e por um concorrente simulado por sessão SQL nas sondagens; dois coordenadores de **reconstrução** em processos reais não foram exercitados. Dois `--backfill` simultâneos sim (saídas 0 e 3, seção 9.5). |
| Reconstrução em escala | O maior conjunto reconstruído tinha dezenas de movimentos; a captura e o replay em 100.000 movimentos **não foram medidos** (teto de captura 250.000). O bootstrap de 100.000 eventos levou cerca de 27 min com um Worker (≈ 61 eventos/s; reproduzido pela governança em 1.591,5 s). Recuperação de banco ou servidor definitivamente perdido não está demonstrada. |
| Limites do ensaio de carga do consolidado (P08 C) | Máquina única (6 processadores lógicos); critérios de 1.000 ms, 60 s, 120 s e massa são hipóteses de laboratório (D3-P08); cada repetição parte de um par de bancos clonado de um par que passou pelo bootstrap real (desvio declarado); F08 com uma execução por caso; os 6 a 8 primeiros POST da janela foram lentos (até ~1,7 s), causa provável partida a frio, **não investigada**; o modo "aceita e não responde" da dependência não foi exercitado. |
| Migrations de P08 nos bancos de desenvolvimento | `AddOutboxLeaseAndAdminTrail`, `AddProjectionRegistry` e `AddRebuildRecovery` **não** foram aplicadas nos bancos de desenvolvimento do autor; `dotnet ef database update` as aplica no ambiente de quem executa o README. |
| Fuso anterior a 2004 | Medido em 19/10/2026 (473.329 instantes de 2000 a 2026): o .NET desta máquina (Windows) e o PostgreSQL concordam de 2004 a 2026 e divergem em 212 instantes de 2000 a 2003. Faixa fora do uso (as contas abrem em 2026; H5). O runner Linux da CI usa o `tzdata` do IANA e esse caso **não foi verificado** (ADR-12). |
| CI da versão final | `5c3beb0` (aceito): run 17, `completed/success` (seção 5.4), contadores de teste não lidos. O commit que registra a aceitação é posterior; esta documentação não afirma nada sobre a CI dele. |

## 7. Checklist de entrega

> Os estados desta seção e da seção 8 são da **versão A** (anterior ao ciclo P07). O ciclo P07 altera código, testes, compose, workflow e documentos; a versão que o contiver precisa de um novo portão. **O portão da versão A é histórico**; o checklist e o portão da versão final estão em 7.1 e 8.9, em redação própria. Os resultados do ciclo P07 estão na seção 9.

Todos os estados desta seção se referem à versão A (commit `87645ed9e3ed57328eabbd55917062188f370547`); os detalhes estão na seção 8.

| Item obrigatório | Estado | Evidência |
|---|---|---|
| Implementação em C# | executado e aprovado | build de 7 projetos no clone novo de A, `rc` 0 (seção 8.3) |
| Testes automatizados | executado e aprovado | clone novo de A: 92 + 45 executados e aprovados, 0 ignorados, TRX novos (seção 8.3); CI de A: etapa Test `success` (seção 5.2) |
| Repositório público | executado e aprovado | clone novo da URL pública `https://github.com/antonio-leonardo/FinancialTransactions` em contexto sem credencial, `HEAD` = SHA da `refs/heads/main` pública (seção 8.4); o roteiro de aceitação também confere o SHA público pela API sem credencial |
| README para executar localmente | executado e aprovado, com a ressalva do passo do `docker compose` | passo a passo do README seguido no clone de A, 8 chamadas HTTP e a contagem final, todas OK (seção 8.4); o `docker compose up` não foi exercitado a partir do clone |
| Documentação no repositório | executado e aprovado | verificador de links e privacidade no clone de A: 0 problemas e 0 ocorrências (seção 8.4) |
| Build com zero erros e zero avisos de compilação | executado e aprovado | `build -warnaserror` do roteiro `rc` 0 no clone de A; CI de A: Build `success` |

### 7.1 Checklist da versão final (ciclo P09)

A versão final é o commit `5c3beb0d564050640790ab04c31aac1b6f984f4f`, que contém a documentação do ciclo P09 (parte 1). Os estados abaixo vêm de um **clone novo** dessa versão publicada, feito em 20/10/2026 pelo executor; a reprodução independente da governança está **pendente**. O commit que registra esta tabela é posterior e só altera `docs/verificacao.md`, o README e a correção de limpeza em `docs/carga-consolidado.md`.

| Item obrigatório | Estado | Evidência |
|---|---|---|
| Implementação em C# e build sem erros e sem avisos | **executado pelo executor; reprodução da governança pendente** | restore e build `-warnaserror` da solução e de cada um dos 11 projetos no clone, `rc` 0 (seção 8.9) |
| Testes automatizados | **executado pelo executor; reprodução da governança pendente** | TRX novos do clone: 220 de unidade e 145 de integração executados e aprovados, 0 ignorados; CI do SHA: etapa Test `success` (seção 5.4) |
| README executável sem o kit | **executado pelo executor; reprodução da governança pendente** | passo a passo do README no clone, em dois bancos descartáveis próprios: migrations dos dois contextos, API de lançamentos, coordenador, Worker, API do consolidado, consolidado por conta e por cliente, `--status` e `--verify`; 19 verificações, 0 divergentes. `docker compose up` não exercitado |
| Repositório público, documentação no repositório | **executado pelo executor; reprodução da governança pendente** | `HEAD` do clone = `refs/heads/main` pública = `5c3beb0`; verificador de links e privacidade sobre os 8 documentos do clone: 0 problemas, 0 ocorrências |
Conferência já feita **nesta árvore de trabalho** (não é a aceitação): build `-warnaserror` com `rc` 0 e uma rodada de testes, verificador de links e privacidade, varredura de nomes; resultados na seção 15.3.

## 8. Portão de aceitação

Modelo de fechamento do guia, preenchido para a versão A em 19/10/2026. Fonte: *executor local*, em clone novo da URL pública, em pastas temporárias próprias desta aceitação; *CI* e *leitura do auditor-master* onde indicado. A saída zero do roteiro de aceitação **não é aprovação**: ela encerra as etapas técnicas no escopo declarado; o fechamento é desta seção, com revisão humana.

### 8.1 Identidade e ambiente

| Item | Valor |
|---|---|
| Versão | commit `87645ed9e3ed57328eabbd55917062188f370547`, branch `main` (histórico reescrito em 19/10/2026) |
| Arquivos pendentes | nenhum: o clone ficou com a árvore limpa antes e depois do roteiro |
| Data | 19/10/2026 |
| Ambiente | Windows 11 10.0.26100; SDK .NET 10.0.402; PowerShell 7.6.7; Git 2.56.0; Docker com o container do projeto (PostgreSQL 17) saudável |
| Inventário | 7 projetos, todos `net10.0` (3 bibliotecas/app em `src/`, 2 ferramentas em `tools/`, 2 de teste em `tests/`); nenhum excluído; runner VSTest; mínimos de execução 92 (unidade) e 45 (integração), as contagens reais da versão anterior aceita (regressão, não quota) |

### 8.2 Requisitos, critérios e evidências

| Requisito | Critério que pode reprovar | Evidência real | Estado |
|---|---|---|---|
| Linguagem C# | Projeto sem decisão no inventário ou fora da solução | `dotnet sln list` e `.csproj` em disco conferidos pelo roteiro: 7 de 7 | executado e aprovado |
| Testes automatizados | Zero testes descobertos não demonstra execução | TRX novos: 92 executados/92 aprovados e 45/45, 0 ignorados, 0 falhos | executado e aprovado |
| Repositório público | Usuário sem credencial não consegue acessar a versão | clone da URL literal em contexto isolado; `ls-remote` = `HEAD` do clone; API pública confirma repositório público e `refs/heads/main` = SHA | executado e aprovado |
| Execução local | Checkout novo não segue o README | passo a passo seguido no clone (seção 8.4) | executado e aprovado, com a ressalva do `docker compose` |
| Documentação | Caminho relativo ou documento da versão não existe | `requiredFiles` (7) presentes; verificador de links, âncoras e privacidade 0/0 | executado e aprovado |
| Build e diagnósticos | Erro ou warning | restore e build `-warnaserror` da solução e de cada projeto, `rc` 0 | executado e aprovado |

### 8.3 Execução do roteiro de aceitação

Comando: o roteiro do guia (`Verificar-Entrega.ps1`, cópia com o mesmo SHA-256 do original, lida por inteiro antes de executar) em processo filho com `TEMP`/`TMP` apontando para pasta própria, sobre o clone, com o inventário acima, o repositório público e a branch `main`. **Código de saída 0**, 61 s. Nenhum TRX foi reutilizado: o roteiro grava TRX novos em pasta temporária nova.

| Etapa | Resultado |
|---|---|
| Árvore limpa antes e depois | sim |
| Identidade | `HEAD` do clone = SHA de A = SHA público da `main` |
| `dotnet msbuild … -target:Restore -warnaserror` | `rc` 0 |
| `dotnet build` da solução (`-c Release --no-restore --no-incremental -warnaserror`) e de cada um dos 7 projetos | `rc` 0 em todos |
| `dotnet test` da unidade | 92 executados, 92 aprovados, 0 ignorados |
| `dotnet test` da integração | 45 executados, 45 aprovados, 0 ignorados |
| `resultado.json` | `technicalChecks` `CONCLUIDAS_NO_ESCOPO_DECLARADO`; `delivery` `PENDENTE_REVISAO_E_REPRODUCAO_INDEPENDENTE`, como esperado: o roteiro não aprova entrega |

Política de diagnósticos: warnings promovidos a erro em restore e build (`-warnaserror`); `Directory.Build.props` trata avisos como erros; não há supressão generalizada.

### 8.4 Reprodutibilidade e segurança

- **Clone em contexto isolado.** O `ls-remote` e o `git clone` da URL pública literal rodaram em um processo filho: diretório neutro (fora de qualquer repositório); variáveis de ambiente `GIT_*`/`GCM_*` de repositório, configuração, proxy, SSH e rastreamento removidas e as de configuração global/sistema, prompt e askpass definidas só nesse processo; configuração global vazia e do sistema desativada. O diagnóstico de configuração (`config --list` com valores omitidos) antes do acesso à rede teve **0 entradas**; depois do clone, só as 10 entradas locais esperadas (`core.*`, `remote.origin.*`, `branch.main.*`), com a URL igual à literal e sem credencial embutida. Nenhuma credencial foi pedida nem fornecida; nenhum valor de configuração foi registrado. Uma variável herdada do ambiente do executor (editor de mensagens do Git), fora da lista prevista, foi removida só no processo filho, por decisão registrada do responsável.
- **README seguido no clone.** Banco descartável próprio criado e removido pela execução (nenhum `ft_doc_*` ficou), API própria na porta `5100` (livre antes e depois), chave e token só em memória. Chave JWT, `dotnet tool restore`, `dotnet ef database update`, `dotnet run` da API, token pela ferramenta e os exemplos: 8 chamadas HTTP (`201`, `200`, `201`, `200`, `200`, `200`, `200` no replay, `422 idempotency_key_conflict` no conflito) e a contagem final de 2 movimentos, **9 de 9 OK**, com status e corpos registrados. Nenhum segredo apareceu nas evidências. Adaptações de isolamento, declaradas: banco descartável via `--connection` e variável de ambiente; chamadas com `Invoke-WebRequest -SkipHttpErrorCheck` para capturar o corpo. **Ressalva:** o passo 2 do README (`docker compose up -d --wait`) **não foi executado a partir do clone**, porque o `docker-compose.yml` fixa `container_name` e a porta 15432 e um `up` em outro diretório criaria um segundo projeto Compose em conflito com o container do projeto; no lugar, verificou-se que o `docker-compose.yml` do clone é igual, byte a byte (SHA-256), ao usado para criar o container em execução, e que o container estava saudável. Etapas indispensáveis que o README não explique: nenhuma encontrada. Nenhum arquivo de segredos locais do desenvolvedor (user-secrets) continha chaves.
- **Documentos relativos.** Verificador de links, âncoras, títulos, tipos de diagrama e privacidade, sobre o README e os quatro documentos de `docs/` do clone: 0 problemas, 0 ocorrências de privacidade, 3 diagramas Mermaid. Limites do verificador como na seção 2.3. A renderização dos diagramas no GitHub **não foi verificada** (seção 6).
- **Dados sintéticos e segredos.** Ver a revisão do histórico na seção 8.5.

### 8.5 Revisão do que está publicado

- **Histórico (arquivos de texto).** 9 revisões enumeradas (`rev-list --all`, `rc` 0), cada uma varrida com cada padrão, uma de cada vez (`git grep -n -I -E -e <padrão> <revisão> --`; `rc` 0 = ocorrência, 1 = nenhuma, outro = erro; nenhum erro). Padrões: JWT, chave privada, token de provedor, `Password=`, `SigningKey` com literal, caminhos pessoais e referências a material privado, mais um padrão sabidamente ausente. O padrão ausente devolveu `rc` 1 nas 9 revisões. Resultado: **nenhum** JWT, chave privada, token de provedor, caminho pessoal ou referência a material privado em qualquer revisão. As ocorrências de `Password=` são só `ft_local_dev_only` (credencial sintética de laboratório, declarada no compose, no workflow, na configuração de desenvolvimento, na fixture de testes e na documentação) e `x` (connection string inalcançável de um teste); as de `SigningKey` são um único literal sintético de 46 caracteres em um teste negativo de logs. `-I` exclui binários: a conclusão é sobre os textos pesquisados.
- **Ressalva sobre a prova positiva.** Esperava-se que `Password=` desse ocorrência em `docker-compose.yml` no commit inicial, para provar que o mecanismo encontra a credencial sintética. Não deu: lá a credencial está na sintaxe YAML `POSTGRES_PASSWORD: …`, que esse padrão não cobre (`rc` 1, sem erro). O mecanismo foi então provado de outra forma, e a cobertura ampliada: o padrão `ft_local_dev_only` devolveu `rc` 0 com ocorrência em `docker-compose.yml` no commit inicial e em todas as 9 revisões, e um padrão `PASSWORD:` (sintaxe YAML) devolveu ocorrências apenas no compose e no workflow, sempre com o valor sintético declarado. O mecanismo também devolveu `rc` 0 com ocorrências para `Password=` nas outras 8 revisões. **A prova positiva nos termos originais não foi obtida**; a alternativa acima fica registrada como complemento, não como substituta.
- **Mensagens.** 9 mensagens (só assunto, nenhuma com corpo), sem nome de contratante, de pessoa ou de processo.
- **Autoria.** Autor e committer iguais em 9 de 9 commits; 18 de 18 campos de e-mail com **um único domínio**, um único endereço e um único nome. O e-mail profissional que constava nos dois primeiros commits do histórico anterior não existe no histórico atual.
- **Metadados do repositório.** Público; branch padrão `main`; `topics` vazio; `homepage` nulo; descrição **pendente** (seção 6).
- **Artefatos e logs da CI.** 0 artefatos. Os logs exigem credencial e não foram lidos por esta aceitação; a ausência de artefatos não certifica os logs.

### 8.6 Controles negativos

Cada controle roda em uma cópia de laboratório própria do clone (com `.git`, sem `bin`, `obj` e `TestResults`); o clone de aceitação não foi alterado. A regra: o caso saudável passa e o defeituoso falha **pelo motivo previsto**; reprovação por outro motivo seria falha do controle.

| Controle | Defeito introduzido | `rc` | Mensagem que reprovou | Causa e o que prova |
|---|---|---|---|---|
| N1, build | linha `int unused;` em um método de `MovementRules.cs` | sem mutação: 0 e 0; com mutação: restore 0, build **1** | `error CS0168: The variable 'unused' is declared but never used` (2 ocorrências), promovido por `-warnaserror` | Prova que a etapa de build, com a mesma captura do roteiro, reprova warning promovido a erro. É um harness da etapa de build, **não** a execução integral do roteiro |
| N1b, árvore suja | a mesma mutação, executando o roteiro integral | 1 | `Árvore não limpa. Preserve as alterações…` | Prova a guarda de integridade: reprova antes de qualquer build (só os dois passos de `git` foram executados). O motivo é a árvore suja, não o warning |
| N2, projeto omitido | inventário de laboratório sem a ferramenta de carga | 1 | `Projeto sem decisão:` seguido do caminho do projeto omitido | Prova que um `.csproj` em disco sem decisão no inventário interrompe antes de compilar |
| N3, SHA público diferente | cópia em outro commit (`47909c9`, do histórico atual; com os documentos obrigatórios e árvore limpa), remoto em A | 1 | `SHA público diferente do checkout.` | O roteiro compilou e testou (45 e 92 aprovados) e só então reprovou a identidade: prova a conferência do SHA público, 44 s |
| N4, contagem abaixo do mínimo | mínimo 1000 para a unidade no inventário de laboratório | 1 | `Executados 92; mínimo exigido 1000.` | Prova só a contagem abaixo do mínimo. **Não cobre** descoberta vazia, módulo sem casos, todos ignorados nem TRX ausente |
| N5, helper do roteiro | processo filho que escreve em `stderr` e sai com 0 / sai com 7 | 0 do harness | caso `exit 0`: não lançou, `stderr` capturado no log; caso `exit 7`: `pwsh falhou (7); consulte …` | Prova a captura e a decisão do `Invoke-Checked` copiado (stderr não reprova; código diferente de zero reprova). Não certifica o roteiro inteiro. PowerShell 7.6.7 |

Nenhum controle é promovido a certificação do roteiro inteiro. Controles do guia **não** exercitados nesta aceitação: módulo sem casos, todos os casos ignorados, módulo de teste omitido, segundo módulo falho, TRX antigo em outra pasta, indisponibilidade da rede ou limite da API do GitHub.

### 8.7 O que o roteiro prova e o que não prova

Prova: que, em checkout limpo da versão pública, todos os projetos inventariados compilam sem warnings, os dois módulos de teste executam e passam com ao menos o mínimo declarado, e a `main` pública aponta para o commit verificado. Não prova: a classificação humana dos projetos no inventário, a qualidade das asserções, o funcionamento fora do ambiente de teste, a renderização dos diagramas, a ausência de conteúdo reproduzido de fontes externas (conferência humana) nem a segurança de produção. Dados são sintéticos; nenhum requisito regulatório é alegado.

### 8.8 Decisão

- **Aprovação técnica:** **aprovado no escopo verificado** (versão A, 19/10/2026): clone novo em contexto sem credencial; roteiro de aceitação com `rc` 0; 92 + 45 testes novos aprovados; build sem warnings; README seguido (com a ressalva do passo do `docker compose`); controles negativos reprovando pelas causas previstas. Ressalvas: prova positiva da varredura do histórico nos termos originais não obtida (seção 8.5); log da CI de A não lido; renderização dos diagramas não verificada.
- **Condições de publicação:** descrição do repositório (About): **pendente** (responsável: dono do repositório; trocar por redação própria); e-mail dos commits: **feito** (histórico reescrito em 19/10/2026, 18 de 18 campos com um único domínio); renderização dos diagramas no GitHub: **pendente** de confirmação visual do responsável; nenhum risco aceito formalmente até esta data.
- **Próxima ação:** o responsável troca a descrição do repositório, confirma a renderização dos três diagramas, comita e envia esta documentação (a versão que a contém será a versão final B) e informa o SHA de B; a verificação final de B repete o roteiro de aceitação em clone novo e confere o que mudou em relação a A.

### 8.9 Portão de aceitação da versão final (ciclo P09)

Fechamento da **versão final**, em redação própria, preenchido em 20/10/2026 com os resultados do clone novo. **Refere-se ao SHA aceito `5c3beb0d564050640790ab04c31aac1b6f984f4f`.** O commit que registra este portão é posterior e só altera `docs/verificacao.md`, o README e, por uma correção de texto da limpeza do roteiro, `docs/carga-consolidado.md`; nenhum código de produto ou teste muda nele. Fonte: *executor local* (esta aceitação), mais a *CI* onde indicado. A saída zero do roteiro de aceitação não é aprovação: `delivery` fica `PENDENTE_REVISAO_E_REPRODUCAO_INDEPENDENTE`.

| Item | Critério que pode reprovar | Resultado no clone | Estado |
|---|---|---|---|
| Identidade | `HEAD` do clone diferente do SHA publicado, ou árvore suja antes ou depois | `ls-remote` em contexto isolado e API pública: `main` = `5c3beb0`; `HEAD` do clone = `5c3beb0`; árvore limpa antes e depois (também depois do README) | executado pelo executor; reprodução da governança pendente |
| Build | qualquer erro ou aviso em restore ou build `-warnaserror` da solução ou de um dos 11 projetos | `Verificar-Entrega.ps1` (SHA-256 `eabc1b84…5070`, inalterado), `rc` 0, 192 s: restore, build da solução e dos 11 projetos, todos `rc` 0 | executado pelo executor; reprodução da governança pendente |
| Testes | menos de 220 de unidade ou de 145 de integração executados, algum reprovado ou ignorado, TRX reaproveitado | TRX novos: unidade 220/220/220 e integração 145/145/145 (total, executados, aprovados); falhos, erro, tempo esgotado, abortados, inconclusivos e não executados: 0 | executado pelo executor; reprodução da governança pendente |
| README | etapa do passo a passo que falhe ou que dependa de algo fora do repositório | 19 verificações, 0 divergentes, `rc` 0, 67 s (seção 8.9.2) | executado pelo executor; reprodução da governança pendente |
| Histórico | segredo, caminho pessoal, nome, empresa ou processo em qualquer revisão; autoria não uniforme | 20 revisões varridas: nenhum JWT, chave privada, token de provedor, caminho pessoal, referência a material privado, nome, empresa ou processo (seção 8.9.3); autoria uniforme em 20 de 20 commits | executado pelo executor; reprodução da governança pendente |
| CI | `conclusion` diferente de `success` para o SHA aceito (consulta à API pública, com o id do run) | run 39524995573 (número 17), `completed/success`, job 118977434311 (seção 5.4) | executado na CI; contadores de teste não lidos |

#### 8.9.1 Execução do roteiro de aceitação

`Verificar-Entrega.ps1` em processo filho com `-ExecutionPolicy Bypass` **só nele** e `TEMP`/`TMP` apontando para pasta própria, sobre o clone, com o inventário de 11 projetos (`Domain`, `Infrastructure`, `Api`, `Auth`, `Consolidation`, `Consolidation.Worker`, `Consolidation.Api`, `DevToken`, `LoadTest`, `UnitTests`, `IntegrationTests`), 10 arquivos obrigatórios (os 7 anteriores mais `docs/recuperacao.md`, `docs/carga-consolidado.md` e `docs/alta-disponibilidade.md`) e mínimos 220 e 145. O clone foi feito por `Git-Isolado.ps1` (diretório neutro, variáveis `GIT_*`/`GCM_*` tratadas só no filho, diagnóstico de configuração com 0 entradas antes da rede, clone com 10 entradas locais esperadas e URL sem credencial; nenhuma credencial pedida nem fornecida). Os dois containers do projeto estavam `healthy` para os testes de integração, com os bancos descartáveis `ft_test_*` e `ft_cons_test_*` criados e removidos pelos próprios testes. `resultado.json`: `technicalChecks` `CONCLUIDAS_NO_ESCOPO_DECLARADO`, `delivery` `PENDENTE_REVISAO_E_REPRODUCAO_INDEPENDENTE`, como esperado.

#### 8.9.2 README reproduzido no clone

Dois bancos descartáveis próprios (`ft_doc_*`, origem e projeção), criados e removidos pela execução; processos em portas livres (5100, 5110 e 5120) e encerrados pelos PID registrados; chave e token só em memória. Os bancos de desenvolvimento não foram tocados (mesmas migrations e contagens antes e depois). Passos: migrations dos dois contextos (as 4 da origem e as 2 da projeção aplicadas), API de lançamentos, token, crédito de 100,00 e débito de 40,00, posição por instante, repetição (`200`) e conflito (`422`), coordenador `--backfill` (`rc` 0, repetido `rc` 0), Worker até `live` com 2 membros no conjunto inicial, API do consolidado, consolidado da conta e do cliente em 01/09/2026 (créditos 100, débitos 40, líquido 60, 2 movimentos, `freshness` `complete`), um novo crédito depois de `live` aplicado pelo Worker (créditos 110, líquido 70, 3 movimentos), `--status` (`pending` 0, `quarantined` 0, 3 linhas na Outbox, `processedEvents` 3) e `--verify` nos modos `live` e `convergence` (`rc` 0). **Adaptação declarada:** o `docker compose up -d --wait` não foi exercitado a partir do clone (o `container_name` e as portas conflitam com os containers do projeto); verificou-se que o `docker-compose.yml` do clone tem o mesmo blob Git que o da solução e que os dois containers estavam `healthy`. Uma primeira execução do auxiliar parou antes de qualquer recurso por comparar o arquivo por SHA-256 em vez de pelo blob (a cópia de trabalho do clone tem fins de linha CRLF); o auxiliar foi corrigido e a execução refeita do zero.

#### 8.9.3 Revisão do histórico publicado

20 revisões enumeradas (`rev-list --all`, `rc` 0; primeira `605e3da`), cada uma varrida com cada padrão (`git grep -n -I -E`, uma revisão e um padrão por vez; `rc` 0 = ocorrência, 1 = nenhuma, outro = erro; nenhum erro). Prova de ausência: um padrão sabidamente inexistente devolveu `rc` 1 nas 20 revisões. JWT, chave privada, token de provedor, caminho pessoal e referência a material privado: `rc` 1 em 20 de 20. As ocorrências de `Password=` (179 linhas) e de `SigningKey` (16 linhas) foram classificadas: `ft_local_dev_only` (credencial sintética de laboratório), `Password=x`, `Password=p` e `Password=segredo` (literais inventados em testes de sanitização e de configuração), menções textuais nos documentos e o único literal sintético de `SigningKey` de um teste negativo de logs. **Prova positiva:** `ft_local_dev_only` em `docker-compose.yml` do commit inicial (`rc` 0) e em 20 de 20 revisões; o padrão original `Password=` continua sem ocorrência no commit inicial (a credencial está na sintaxe YAML `POSTGRES_PASSWORD:`), o que já estava registrado na seção 8.5. Varredura de nomes, empresa e processo em 20 revisões: 0. Autoria: 20 commits, 0 merges, um único nome e um único endereço de e-mail em autor e committer (80 de 80 campos, um domínio); mensagens só com assunto. **Não verificado:** commits antigos podem continuar acessíveis por SHA no cache do GitHub; o log da CI não foi lido; o About do repositório continua com a descrição original (pendente, ação do responsável).

#### 8.9.4 O que esta aceitação não prova

Não prova a classificação humana dos projetos no inventário, a qualidade das asserções, o funcionamento fora do ambiente de teste, a renderização dos 7 diagramas (só conferidos por leitura), a ausência de conteúdo reproduzido de fontes externas (conferência humana, da governança), nem a segurança ou a alta disponibilidade de produção (que é uma proposta, [alta-disponibilidade.md](alta-disponibilidade.md)). Controles negativos do roteiro não foram repetidos nesta aceitação; valem os da versão A (seção 8.6), porque o script de aceitação não mudou (mesmo SHA-256).
## 9. Ciclo P07: consolidado diário independente

> As seções 9 a 14 são o registro de cada ciclo **na data dele**. Onde dizem "pendente", "não executado" ou "P09", o estado atual está nas seções 6 e 15.

Esta seção registra o que o ciclo P07 implementou e o que foi **executado** (fonte: *executor local*, 19/10/2026), separando o que está feito do que fica para os ciclos seguintes. A governança reexecuta por conta própria; nada aqui é parecer. As hipóteses H15 (consolidado diário por dia de negócio em `America/Sao_Paulo`) e a substituição de H12 são **decisões do responsável do projeto em 19/10/2026**, não requisitos do contratante.

### 9.1 Adequações sugeridas: estado por item

> **Nota datada.** Esta tabela é o estado de **19/10/2026** (fim do ciclo P07). Ela ainda diz que faltam a segunda API do consolidado, a reverificação ampla, a reconstrução, a medição de carga e a alta disponibilidade: isso foi resolvido ou tratado nos ciclos P08 e P09. **Estado atualizado em 20/10/2026: seção 15.1.**

Fonte de cada item: as **adequações sugeridas** após a primeira entrega (ver "Fontes e procedência" em [escopo.md](escopo.md)). Estados segundo H14.

| # | Item | O que esta versão implementa | O que falta | Estado (H14) | Evidência |
|---|---|---|---|---|---|
| 1 | Independência dos lançamentos em relação ao consolidado diário | Processo e banco próprios para a consolidação; Outbox na transação do movimento; a escrita não conhece a projeção. Worker parado, projeção fora do ar e API do consolidado parada não impedem registrar, consultar a posição por instante nem a readiness da escrita | segunda instância da **API** do consolidado (parte B do ciclo P08; duas APIs só no ensaio de carga) | **executado e aprovado** nos cenários C1 a C5 (automatizados e sondagem contra processos reais); a parte A do P08 acrescentou dois Workers (seção 10, aguardando auditoria) | seções 9.2, 9.4, 9.5 e 10 |
| 2 | Estratégia de processamento e recuperação da consolidação | Classes de falha (transitória, ambiente, evento), *backoff* de 1 s a 30 s, quarentena por marcação (5 tentativas), reentrega idempotente, bootstrap reconciliado por identidade e saúde com desarme | reverificação ampla da projeção viva e reconstrução de destino perdido (parte B do ciclo P08). Desde a parte A: reserva durável, reprocessamento administrativo da quarentena e comando de status (seção 10) | **parcial** | C4, C6a, C6b, C7, C9a a C9h, C12a, C12b, R01 a R08 |
| 3 | Requisito de 50 req/s com no máximo 5% de perda, associando cada resultado ao cenário | medido em laboratório na parte C do P08 (L1, L2, L3 e F08; ver [carga-consolidado.md](carga-consolidado.md) e a seção 14), com as hipóteses D3 declaradas; vale para a máquina e a topologia do ensaio, não para produção | ensaio do consolidado em cenário declarado, com oracle financeiro e reconciliação | **executado pelo executor** (aguarda auditoria; não é aprovação) | `LoadGeneratorTests` (21) e os `resumo.json` dos ensaios (evidência privada) |
| 4 | Arquitetura de alta disponibilidade (pontos de falha, recuperação, escala horizontal) | pontos de falha parciais descritos em [arquitetura.md](arquitetura.md); **uma** instância de cada processo | `docs/alta-disponibilidade.md` e a escala horizontal (P09) | **proposto** | nenhuma (P09) |
| 5 | Aderência ao enunciado, distinguindo obrigatório de hipótese | seção "Fontes e procedência" e rótulos "hipótese do projeto" / "decisão do responsável" em [escopo.md](escopo.md) e [arquitetura.md](arquitetura.md) | revisão final da aderência (P09) | **implementado** | [escopo.md](escopo.md) |

Delta de T15 (mensageria): até P06 era "não aplicável" (H12); passa a **comunicação assíncrona por trabalho durável no banco, sem broker**, com broker como alternativa documentada (ADR-10). A linha histórica está preservada na seção 1.2.

### 9.2 Cenários C1 a C15 → testes

Esperados vindos de valores enviados e de SQL direto, não de código de produção. "Automatizado" = hosts reais (API de lançamentos, Worker e API do consolidado) no processo de teste, sobre dois PostgreSQL reais. A **sondagem** (seção 9.5) é outra evidência, contra processos reais.

| ID | Cenário | Testes | Estado | Última execução |
|---|---|---|---|---|
| C1 | Worker parado; 3 POST; consolidado com pendência, `stale`, sem vazamento entre clientes | `C1_WorkerDown_WritesAccepted_ConsolidatedShowsPendingAndStale` | executado e aprovado | 19/10/2026, duas rodadas |
| C2 | Worker em host novo aplica exatamente os 3 eventos; reconciliação por conta e dia | `C2_WorkerRestartedInNewHost_AppliesExactlyTheThreeEvents_AndReconcilesWithOrigin` | executado e aprovado | idem |
| C3 | Projeção inalcançável: escrita segue; consolidado `503` em ≤ 2 s (modo conexão recusada), sem saldo | `C3_ProjectionUnreachable_WritesStayUp_ConsolidatedAnswers503WithinTwoSeconds` | executado e aprovado | idem |
| C4 | ≥ 6 tentativas falhas observadas no mesmo host, *backoff* crescente, recuperação sem intervenção | `C4_ProjectionBackAfterSixFailedAttempts_SameHostRecoversWithoutIntervention` | executado e aprovado | idem |
| C5 | API do consolidado parada: escrita, posição e readiness inalteradas; depois, `200` | `C5_ConsolidationApiDown_WritesPositionAndReadinessUnaffected_ThenBack` | executado e aprovado | idem |
| C6a | Falha dentro da transação da projeção: nada persistido; tentativa seguinte aplica uma vez | `C6a_FailureInsideProjectionTransaction_NothingPersisted_NextAttemptAppliesExactlyOnce` | executado e aprovado | idem |
| C6b | Falha depois do commit e antes da marcação: reentrega não soma de novo | `C6b_FailureAfterProjectionCommitBeforeMark_Redelivery_DoesNotSumAgain` | executado e aprovado | idem |
| C7 | O mesmo evento reapresentado ao applier é *no-op*; estouro de `numeric(24,2)` é falha do evento | `C7_SameEventPresentedAgainInNewTransaction_IsNoOp`, `C7b_AggregateOverflow_IsAnEventFailure_AndPersistsNothing` | executado e aprovado | idem |
| C8 | Informação tardia: linha de D−3 muda, linhas seguintes não, `closingBalance` derivado muda | `C8_LateInformation_ChangesTheEventDayRow_AndDerivedClosingBalances_NotTheLaterRows` | executado e aprovado | idem |
| C9a | Captura com controle causal: `member_count = 12`, 13.º fora do conjunto, aplicado em `live` | `C9a_CaptureWithCausalControl_ExactlyTwelveMembers_ThirteenthAppliedInLive` | executado e aprovado | idem |
| C9b | O 13.º confirma antes: `member_count = 13` | `C9b_ThirteenthCommitsBeforeTheCoordinator_ThirteenMembers_LiveOnlyAfterAllThirteenReconciled` | executado e aprovado | idem |
| C9c | Interrupção entre os bancos; reexecução ativa a partir do registro de controle da origem | `C9c_InterruptionBetweenTheDatabases_RerunActivatesFromTheOriginControlRecord_WithoutRemarking` | executado e aprovado | idem |
| C9d | Conjunto vazio com a mesma interrupção | `C9d_EmptySetWithTheSameInterruption_ControlRecordWithZeroMembers_ActivatesAndLivesWithoutMembers` | executado e aprovado | idem |
| C9e | Reexecução concorrente com avanço para `live`: nada é alterado | `C9e_ConcurrentRerunReadLoading_ServicePromotesLive_CoordinatorReleased_ChangesNothing` | executado e aprovado | idem |
| C9f | Serviço antes do coordenador: aguarda sem consumir; `ready` `503 Degraded` | `C9f_ServiceStartedBeforeTheCoordinator_WaitsWithoutConsuming_ThenLoadsAndGoesLive` | executado e aprovado | idem |
| C9g | Serviço interrompido no meio de `loading`: retoma sem duplicata | `C9g_ServiceInterruptedMidLoading_RestartedInNewHost_NoDuplicates_ReachesLive` | executado e aprovado | idem |
| C9h | Dois coordenadores: o segundo sai com código 3 pelo lock | `C9h_TwoSimultaneousCoordinators_SecondGetsCode3ByTheLockWithinFiveSeconds_ThirdChangesNothing` | executado e aprovado | idem |
| C10 | Replay HTTP: `200` e nenhuma Outbox nova | `C10_ReplayHttp_ReturnsOkAndCreatesNoNewOutbox` | executado e aprovado | idem |
| C11 | Autorização das rotas novas | `C11a_Authorization_NoToken401_OtherCustomer404_UnknownAccount404`, `C11b_UnresolvedEvents_NeverIncludeAnotherCustomersEvents`, `C11c_OriginUnreachableDuringAuthorization_Is503DependencyUnavailable` | executado e aprovado | idem |
| C12 | Evento inválido vai à quarentena; falha de ambiente não (e `Degraded` com desarme) | `C12a_InvalidEventIsQuarantinedAfterFiveAttempts_ValidOneAppliedAndWorkerAlive`, `C12b_EnvironmentFailure_NoEventQuarantined_ReadyDegraded503_ThenDisarmedAfterFirstSuccessfulCycle` | executado e aprovado | idem |
| C13 | Regressão: os 137 testes anteriores presentes e aprovados; readiness da escrita independe do consolidado | `C13_Regression_WritesReadinessIsIndependentOfWorkerProjectionAndConsolidationApi` e os 137 anteriores | executado e aprovado | idem |
| C14 | Corrida do estado de atualização: contadores da observação inicial, valor da consulta posterior | `C14_RaceOnRefreshState_StaleCountersFromTheOriginReading_FinancialFieldsFromTheLaterProjectionQuery` | executado e aprovado | idem |
| C15 | Movimento e evento sempre juntos | `C15a_FailureAfterCommit_MovementAndEventExistTogether`, `C15b_FailureBeforeCommit_NeitherMovementNorEventExist`, `C15c_ManyAccepted_EveryMovementHasExactlyOneEvent` | executado e aprovado | idem |

Outros testes do ciclo: contrato da API (`DayParameter_*`, `DayBoundary_*`, `CustomerRoute_*`, `Freshness_*`, `RequestBudget_*`, `RequestAborted_*`, `NotReady_*`), composição e configuração (`Composition_*`, `Config_*`, `BackfillMode_*`, `ProjectGraph_*`, `SourceHygiene_*`) e unidade (`BusinessDay_*`, `Classifier_*`, `CoordinatorDecision_*`, `Reconcile_*`, `HealthMapping_*`, `HealthState_*`, `Backoff_*`, `Capacity_*`, `Totals_*`, `ConnectionOptionsValidator_*`, `OutboxEventTests`).

### 9.3 Comandos e resultados

Fonte: *executor local*, 19/10/2026, na solução, **sobre o código do commit `a055816`** (os resultados depois da rodada de correção estão na seção 9.7: 173 e 100 testes, 273 nomes). SDK 10.0.402. Quatro rodadas de `dotnet test` (duas de cada projeto), com TRX por projeto e rodada.

| Etapa | Comando | Resultado |
|---|---|---|
| Restore | `dotnet msbuild FinancialTransactions.sln -target:Restore -warnaserror` | `rc` 0 |
| Build | `dotnet build FinancialTransactions.sln -c Release --no-restore --no-incremental -warnaserror` | `rc` 0; `0 Warning(s)`, `0 Error(s)`; 11 projetos |
| Testes de unidade (2 rodadas) | `dotnet test tests/FinancialTransactions.UnitTests -c Release --no-build --settings tests/vstest.runsettings` | `rc` 0; **173 de 173** aprovados nas duas |
| Testes de integração (2 rodadas) | idem, `IntegrationTests` | `rc` 0; **98 de 98** aprovados nas duas (cerca de 48 s) |
| Lista nominal | nomes do TRX | 271 nomes iguais nas duas rodadas; **os 137 nomes dos ciclos anteriores estão presentes**; 134 novos; nenhum ignorado |

Execução remota (fonte: *leitura do auditor-master* do log da CI em 19/10/2026, não uma execução do executor): SHA `a055816243e29088f47c41d372f23178b4ae7512`, [run 39321900737](https://github.com/antonio-leonardo/FinancialTransactions/actions/runs/39321900737) (número 9, tentativa 1, `push`, `completed/success`), job `build-and-test` 118297790062; Ubuntu 24.04.5, runner 2.337.0, SDK 10.0.402, dois serviços PostgreSQL 17.11; restore, build Release `-warnaserror` (11 assemblies, `0 Warning(s)`, `0 Error(s)`) e testes **173/173** (unidade) e **98/98** (integração). A CI **não** executou a sondagem contra processos reais, as mutações, o ensaio de carga nem o roteiro do README; esses resultados continuam sendo do executor (seções 9.4 e 9.5). A correção descrita na seção 9.7 é posterior a esse SHA. Para o SHA seguinte, `c7cdec1c0d92592416d3e4738677d4a5fa500199`, o auditor leu em 19/10/2026 o [run 39329327946](https://github.com/antonio-leonardo/FinancialTransactions/actions/runs/39329327946) (número 10, tentativa 1, job 118323234355, `completed/success`): build Release de 11 assemblies, zero warnings e 173 + 100 testes aprovados. **Leitura histórica do auditor para esse SHA**; não é execução do executor e não cobre o ciclo P08.

### 9.4 Verificação de sensibilidade dos testes (mutações do executor)

Mutações compiláveis de poucas linhas, uma por vez, em cópia do arquivo de produção restaurada e conferida byte a byte depois de cada uma. Todas foram **detectadas** pelo teste que deveria detectá-las:

| Mutação | Teste que detectou |
|---|---|
| Ordem invertida (projeção antes da origem, pausa mantida) | C14 |
| Outbox gravada em `SaveChanges` separado, depois do commit | C15a |
| `unresolvedEvents` contando só `status = 0` | C12a |
| Reconciliação com `OR bootstrap_id IS NULL` | C9a |
| Serviço que consome sem linha de bootstrap | C9f |
| `Degraded` com HTTP 200 | C9f e C12b |
| Coordenador que faz `UPDATE ... phase = 'loading'` | C9e |
| `pg_advisory_lock` bloqueante | C9h |
| Marcador de `processed_events` fora da transação | C6a |
| Coordenador que recaptura ignorando o registro de controle | C9c e C9d |
| (rodada de correção) `observedAt` avaliado com `clock_timestamp()` depois da agregação | EX01 (reprova pela asserção do instante: `observedAt` posterior ao commit concorrente não contado) |
| (rodada de correção) unlock incondicional, mesmo sem ter adquirido o lock | EX02 (reprova pela asserção de que nenhuma liberação de lock alheio foi tentada) |

Uma primeira tentativa da mutação da reconciliação produziu SQL malformado (erro de escape no próprio *script* de mutação) e foi descartada; a mutação válida foi refeita. Não foram executadas por esta verificação: `attempts` incrementado em falha transitória (C4), readiness da escrita incluindo a projeção (C13) e a marcação de pertença em transação própria `READ COMMITTED`.

### 9.5 Sondagem contra processos reais (C1 a C5 e `--backfill`)

Fonte: *executor local*, 19/10/2026. Executáveis `Release` dos três processos (Kestrel real nas portas 5100, 5120 e 5110), bancos descartáveis `ft_p07_src_<carimbo>` e `ft_p07_cons_<carimbo>` (e um segundo par só para a concorrência de coordenadores), conexões e chave só em memória. A parada da consolidação foi **real**: `docker stop` do container `financialtransactions-consolidation-postgres`, religado em `finally`. Este é um tipo de evidência distinto dos testes automatizados da seção 9.2. Uma primeira execução do *script* foi interrompida por defeito do próprio *script* (montagem de argumentos do `docker exec` que segura o lock, no passo B4); os 18 passos anteriores tinham passado; o *script* foi corrigido e **toda** a sondagem foi refeita. O resultado abaixo é o da segunda execução.

| Passo | Ação | Status | Campos-chave |
|---|---|---|---|
| S0 | API de lançamentos sobe sem a connection string da projeção | 200 | readiness `Healthy` |
| B1 | `--backfill` do executável real, primeira execução (3 movimentos legados) | saída **0** | `loading`, `member_count = 3`; registro de controle na origem com o mesmo `bootstrap_id` |
| B2 | `--backfill`, segunda execução | saída **0** | linha do destino e `xmin` idênticos (nada tocado) |
| S1 | Worker em modo serviço | 200 | fase chega a `live`; `ready` `{"status":"Healthy"}`; `processed_events = 3` |
| S2, S3 | API do consolidado e `GET` da conta A1 em 2026-09-01 | 200 | `credits 10.00`, `debits 4.00`, `movementCount 2`, `complete` |
| C1 | Worker encerrado; 3 `POST` | 201, 201, 201 | Outbox com 3 pendentes; `GET` A1: `unresolvedEvents 2`, `stale true`, `freshness stale`; A2: 1; cliente A: 3; cliente B: 0 |
| C2 | Worker em **novo processo** (PID diferente) | 200 | `processed_events` 3 → 6 (delta 3); A1 `credits 100.00`, `debits 30.00`, `complete`; reconciliação por conta (contagem:soma) origem = projeção |
| C3 | `docker stop` da consolidação; `POST`; `GET` consolidado | 201; 503 em 0,04 s | `dependency_unavailable`, sem campos de saldo; posição por instante 200; readiness da escrita 200 |
| C4 | o Worker segue vivo; 6 tentativas falhas no log; `docker start` | 200 | nenhum evento em quarentena; `attempts` máximo 0; **mesmo PID**; `credits` de A1 = 110,00; `complete`; `ready` do Worker `Healthy` |
| C5 | API do consolidado encerrada | 201 | posição 200; readiness da escrita 200; `GET` consolidado com a API fora: conexão recusada. Religada: 200, `credits` 115,00 |
| B3 | dois `--backfill` disparados juntos (30.000 movimentos legados, par próprio) | saídas **0 e 3** | sobreposição real: um terminou pelo lock |
| B4 | `--backfill` com o lock consultivo mantido por uma sessão externa | saída **3** | `xmin` do destino inalterado |
| B5 | `--backfill` depois de liberado | saída **0** | sem alteração |

Limpeza: processos próprios encerrados pelo PID registrado; os quatro bancos removidos pela lista exata; **o id do container da origem é o mesmo antes e depois**; os dois containers `healthy` ao final; varredura dos logs dos processos (chave, tokens, `Password=`): 0 ocorrências. O tempo de 0,04 s do `503` reflete conexão **recusada**; o modo "aceita e não responde" não foi exercitado.

### 9.6 Limites e pendências do ciclo

> **Nota datada.** As linhas desta tabela são de 19/10/2026. A linha 'Instâncias do consumidor' (que lista duas APIs, reverificação e reconstrução como pendentes), a de carga ('aguardando auditoria') e a de alta disponibilidade ('P09') foram superadas em 20/10/2026; estado atualizado nas seções 6 e 15.1.

| Item | Situação |
|---|---|
| CI | Há uma execução bem-sucedida da versão `a055816` (seção 9.3, leitura do auditor-master). A correção da seção 9.7 tem o run 10 do SHA `c7cdec1` (leitura histórica do auditor, seção 9.3); as alterações do ciclo P08 ainda não têm execução na CI. Os testes do consolidado têm esperas de tempo real e rodam em runner compartilhado: intermitência é possível e seria registrada. O log da CI contém a mensagem do servidor `you don't own a lock of type ExclusiveLock`, compatível com o unlock sem posse do coordenador que perdeu a disputa; corrigido em 9.7. |
| Instâncias do consumidor | Desde a parte A do ciclo P08 há reserva durável, mais de um Worker, reprocessamento administrativo da quarentena e comando de status (seção 10). Seguem pendentes: duas APIs do consolidado exercitadas em ensaio, reverificação ampla da projeção e reconstrução de destino perdido (partes B e C do P08). |
| Carga do consolidado | 50 req/s com no máximo 5% de perda: **requisito obrigatório** das orientações complementares; **medido em laboratório** na parte C do P08 (execução local do executor, aguardando auditoria; [carga-consolidado.md](carga-consolidado.md)). A meta de ≤ 2 s para a falha de dependência é hipótese do projeto, exercitada só para conexão recusada. |
| Alta disponibilidade | **P09**. |
| Fuso | Medido em 19/10/2026 (473.329 instantes, 2000 a 2026, passo de 30 min): o .NET desta máquina (Windows) e o PostgreSQL concordam de 2004 a 2026 e divergem em 212 instantes de 2000 a 2003. Faixa fora do uso do laboratório (as contas abrem em 2026); declarado em [arquitetura.md](arquitetura.md), ADR-12. O runner Linux da CI usa o `tzdata` do IANA e não foi verificado. |
| Mutações | As não executadas estão listadas na seção 9.4. |
| README | O passo a passo do consolidado no README (três processos, `--backfill`) foi **verificado pela sondagem** com os mesmos executáveis e variáveis, não por um roteiro de checkout novo: o `docker compose up -d --wait` e os `dotnet ef database update` contra os bancos de desenvolvimento **não** foram executados (apenas contra bancos descartáveis). O caminho `dotnet run` dos dois processos novos (perfil `http`, portas 5120 e 5110, `--backfill` com saída 2) foi exercitado com as connection strings apontando para uma porta fechada, sem tocar em nenhum banco: `live` 200 e `ready` 503 `Unhealthy` nos dois, e as portas liberadas depois. |
| Diagramas | Os diagramas novos (componentes, sequência e estados) foram conferidos por leitura; a renderização no GitHub permanece pendente. |

### 9.7 Rodada de correção pós-auditoria (P07-EX-01 a EX-03)

Origem: parecer do auditor-master sobre o commit `a055816243e29088f47c41d372f23178b4ae7512`. A rodada partiu desse mesmo SHA, com árvore limpa e índice vazio, e **não** fez commit nem publicação. Fonte dos resultados abaixo: *executor local*, 19/10/2026; é execução do executor, não auditoria independente. O desenho foi preservado (contrato diário, bootstrap, ordem origem → projeção, C14 e lifetimes não mudaram).

| Achado | Correção | Controle novo | Resultado |
|---|---|---|---|
| **EX-01** (P1): `observedAt` avaliado depois da agregação podia anunciar um corte posterior ao snapshot da contagem | `observedAt` passou a ser o `statement_timestamp()` da instrução de contagem (início da observação, anterior ou igual ao snapshot); a descrição foi ajustada em [arquitetura.md](arquitetura.md) (ADR-11) e no README | `EX01_ObservedAt_IsNotLaterThanTheSnapshot_ACommitDuringTheObservationIsNotCoveredByTheAnnouncedInstant`: num banco descartável, uma view de teste faz a **própria instrução SQL de produção** esperar por um lock consultivo **depois** do snapshot; confirmada a espera (`pg_stat_activity`: evento `Lock`/`advisory`), um commit concorrente é feito e o instante anterior ao `COMMIT` é lido; só então a barreira é liberada. Premissa asserida: a contagem não inclui o commit (0). Propriedade: `observedAt` não é posterior ao commit não contado | aprovado. Mutação com `clock_timestamp()` avaliado depois da agregação reprova **pela asserção do instante** (`observedAt` 21:29:48,0704680 posterior ao commit concorrente 21:29:48,0484860); uma primeira mutação reprovou por *timeout* do meu critério de localização da instrução e foi descartada como prova, o critério foi corrigido e a mutação refeita |
| **EX-02** (P2): `pg_advisory_unlock` emitido por sessão que não adquiriu o lock | o coordenador guarda se adquiriu; só então libera (e registra se o servidor devolver falso); a sessão própria é descartada em todos os caminhos; disputa e código 3 preservados | `EX02_Coordinator_ThatDidNotAcquireTheLock_DoesNotUnlock_AndDisposesItsSession_TheOwnerKeepsTheLock`: o dono (outra sessão) mantém o lock; o coordenador sai com 3, não tenta liberar, o dono continua dono (mesmo `pid` em `pg_locks`) e o número de sessões do banco volta ao anterior; a execução seguinte sai com 0 e não deixa lock | aprovado. Mutação com unlock incondicional reprova pela asserção de que nenhuma liberação de lock alheio foi tentada |
| **EX-03** (P2): documentação | seções 9.1, 9.3 e 9.6 deste documento (CI do SHA examinado, com a fonte); requisito de 50 req/s e 5% tratado como **requisito** (ensaio pendente em P08); notas de escopo em ADR-1, README, [carga.md](carga.md) e seção 4.2 (o ensaio é do ciclo P04, commit `4463e38`, anterior à Outbox e aos processos novos); ADR-13 corrigida: depois do primeiro sucesso o *backoff* volta a 1 s, e os 500 ms são a espera quando não há pendentes | n/a (documentação) | verificador de links, âncoras, diagramas e privacidade: ver o resultado no checkpoint da rodada |

**Regressão (Release):** restore `rc` 0; build `-warnaserror` `rc` 0, `0 Warning(s)`, `0 Error(s)`; duas rodadas de cada projeto, `rc` 0: **173/173** (unidade) e **100/100** (integração), **273 nomes**, idênticos nas duas rodadas; os 271 nomes de `a055816` e os 137 de P06 estão presentes; os 2 novos são `EX01_*` e `EX02_*`. Nenhum teste existente foi removido ou enfraquecido.

**Limites:** na data deste registro (pré-publicação) a CI não havia executado esta correção; o run 10 do SHA `c7cdec1` é a leitura posterior do auditor (seção 9.3). O controle de EX-01 depende de uma view e de uma função criadas só no banco descartável do teste; a propriedade `statement_timestamp()` ≤ snapshot em READ COMMITTED vem da semântica do PostgreSQL e é exercitada (premissa e propriedade asseridas) nesse cenário, não demonstrada para toda intercalação possível. As mutações da seção 9.4 que o executor não realizou continuam não realizadas.

## 10. Ciclo P08, parte A: reserva durável, duas instâncias, quarentena e observação

**Estado:** implementado e **executado pelo executor** (execução local, 19/10/2026), com um lote complementar em 20/10/2026 (seção 10.4) depois do parecer parcial do auditor. Os contadores e as sondagens desta seção são registros do executor; não são aprovação da governança. Cronologia da CI: no checkpoint pré-publicação a CI não havia executado a parte A; depois da publicação do commit `4f0f10e`, o auditor leu o run 11 (seção 10.4). As partes B (reconciliação ampla e reconstrução) e C (carga dos 50 req/s) **não foram executadas**; os critérios correspondentes continuam não demonstrados. Os 50 req/s com no máximo 5% de perda seguem como **requisito obrigatório sem medição**.

### 10.1 Controles R01 a R08

| ID | O que prova | Testes |
|---|---|---|
| R01 | Dois adquirentes sincronizados disputam um evento: uma reserva válida (25 rodadas, mais a prova determinística de que uma linha travada é saltada e não esperada) | `R01_*` |
| R02 | A reserva de A vence, B recebe token novo; confirmar, falhar e liberar de A afetam zero linhas e não alteram a posse de B; o mesmo evento alcança o applier duas vezes e dinheiro e recibo entram uma vez | `R02_*` |
| R03 | Dois Workers aplicam eventos diferentes da mesma conta e dia (créditos e débitos), com falha depois do commit do destino; totais exatos; barreira garante a participação dos dois; real: 60 POST consumidos por dois processos (32 e 28) | `R03_*`, sondagem `R03r` |
| R04 | Dono morre depois da reserva e antes da projeção; outro Worker recupera só depois do vencimento, sem tentativa contada; real: PID encerrado, recuperação em ~47 s com a reserva de 45 s | `R04_*`, sondagem `R04a` a `R04c` |
| R05 | Falha transitória prolongada com dois Workers (12 falhas injetadas): nenhuma tentativa de evento gasta, nenhuma quarentena, convergência sem edição de status | `R05_*` |
| R06 | Dry-run sem efeito; aplicar exige digest; trilha transacional; repetição idempotente; outra seleção com o mesmo identificador é conflito; causa persistente volta à quarentena; nenhum recibo apagado; alvo não confirmado, entrada malformada, seleção vazia ou desconhecida são recusados | `R06a_*`, `R06b_*`, `R06c_*`, sondagem `A3r` |
| R07 | Bootstrap com dois Workers: conjunto exato, evento novo só depois de `live`, fase sem regressão; quem encontra `live` sob a exclusividade devolve "já concluído"; descritor com contagem menor, maior ou caso vazio adulterado (nos dois lados) impede a promoção | `R07a_*`, `R07b_*`, `R07c_*`, `VerifyDescriptor_*` |
| R08 | `--status` distingue pendente, reservado válido, expirado, adiado e quarentena com massa conhecida; origem fora do ar é "unknown" com saída 2, nunca zero; diagnóstico por instância sem segredo nem nome de máquina ou de usuário | `R08a_*`, `R08b_*`, sondagem `A4s` |

Testes de autenticação, EX01, EX02 e C14 continuam na suíte e passaram.

### 10.2 Comandos e resultados (fonte: executor)

- Restore `rc=0`; build Release `--no-incremental -warnaserror` `rc=0`, 0 avisos, 11 assemblies.
- Duas rodadas completas: unidade **193/193** e integração **115/115** em ambas (nomes idênticos nas duas). Referência anterior: 173 + 100 = 273 nomes, **todos presentes**; 35 novos (20 de unidade, 15 de integração). Ajustes em testes existentes, com justificativa: `C9g` deixou de usar `BatchSize` (removido) e ganhou reserva de 5 s e orçamento de 3 s, porque o primeiro host morre com uma reserva que o segundo precisa esperar vencer; os quatro testes `C9a/C9b/C9c/C9d` passaram a esperar o callback de promoção, que roda na thread do consumidor um instante depois de a fase `live` ficar visível (condição de corrida do teste, agravada pela liberação do lock); o rig de teste usa `RetryDelay` de 200 ms.
- Uma primeira regressão reprovou no teste de higiene de fonte existente (`SourceHygiene_...`) por um operador `!` em `ConsolidationAdmin.cs`; corrigido, e a rodada reprovada ficou preservada no registro privado. As duas rodadas acima são posteriores à correção.
- Sondagem com **processos reais** (executáveis Release, bancos descartáveis `ft_p08_*`, nenhum container parado): `rc=0`, 9 passos, 0 falhos, 0 ocorrências na varredura de vazamento. Ela foi executada antes da correção do `!`, que não altera o comportamento.
- Mutações dirigidas em cópia descartável: remover a verificação do token na confirmação (`R02`), ignorar o vencimento (`R04`), promover sem reler a fase (`R07b`), promover sem verificar o descritor (`R07c`) e adquirir sem `FOR UPDATE SKIP LOCKED` (`R01`) reprovaram pelas causas previstas. Uma primeira mutação de `R01` atingiu só um comentário e foi descartada como prova; a repetição reprovou por *timeout* ao esperar a linha travada.

### 10.3 Limites

A CI não executou nada disto. As duas instâncias rodam na mesma máquina e no mesmo par de bancos: demonstram o mecanismo, não alta disponibilidade. A reserva não elimina toda execução sobreposta (ADR-14). Nenhuma migration foi aplicada nos bancos de desenvolvimento (`financialtransactions` e `financialtransactions_consolidation`); a migration `AddOutboxLeaseAndAdminTrail` precisa ser aplicada por quem os usa (`dotnet ef database update`). A vazão por instância não foi medida (parte C).

### 10.4 Lote complementar da parte A (depois do parecer parcial sobre o commit `4f0f10e`)

**Fonte e cronologia.** O commit `4f0f10e6d187a2921a906fb129a7d1cb50eed5ce` ("Inicialização do P08") publicou a parte A depois do checkpoint pré-publicação, no qual a CI ainda não a havia executado. O auditor leu o [run 39345385454](https://github.com/antonio-leonardo/FinancialTransactions/actions/runs/39345385454) (número 11, tentativa 1, job 118376609399, `completed/success`, Ubuntu 24.04.5, SDK 10.0.402, dois PostgreSQL 17.11): build Release de 11 assemblies, zero warnings, 193 testes de unidade e 115 de integração aprovados (308). É **leitura histórica do auditor** para esse SHA; não cobre a sondagem com processos reais, as mutações nem o lote abaixo, e não prova nominalmente os 273 casos herdados (isso vem dos TRX locais).

**Achados tratados** (parecer do auditor sobre `4f0f10e`):

| Achado | Tratamento | Controle |
|---|---|---|
| P08A-01 (P1): a liberação com adiamento não verificava o vencimento e zero linhas não tinha diagnóstico | `ReleaseSql` passou a exigir `lease_expires_at > statement_timestamp()`; o consumidor trata o retorno falso como perda de posse (contador, diagnóstico `LeaseLost` na etapa "liberação", log) e não como liberação | `R02b` (válida libera e adia; vencida com o token antigo e sem B não altera nada, nem a próxima elegibilidade; tomada por B permanece intacta) e `R02c` (no Worker, a liberação recusada vira diagnóstico e o evento converge); mutações MX-A01 e MX-A04 |
| P08A-02 (P2): R07a podia pular a asserção central | barreira causal: um membro aplicado e retido no Worker 1 impede a reconciliação; com a barreira fechada, o Worker 2 confirma exatamente os 11 membros restantes e, durante 15 observações, o não membro não é reservado nem aplicado e a fase permanece `loading`; depois da liberação, convergência sem regressão. Não depende de qual Worker promove | `R07a` reescrito (o nome anterior foi substituído); mutação MX-A02 |
| P08A-03 (P2): o digest não distinguia uma nova passagem pela quarentena | o plano passou a incluir a versão da linha (`xmin`); disposição: `xmin` é token de versão de curta duração, não identidade histórica | `R06d` (plano antigo recusado, sem trilha nem efeito; plano novo aplicado; repetição idempotente); mutação MX-A03 |
| P08A-04 (P2): procedência do roteiro | o arquivo original v1.0 (390 linhas, SHA-256 `8c1abf64c08ad6dba197231b7a67980aa76450d210aae86ff6ee8d0f317bcad9`) foi copiado para o kit privado em 19/10/2026, com autorização do usuário, e o hash conferido; a parte A foi executada com o texto equivalente colado na conversa | registro no checkpoint complementar |

**Resultados (executor).** Restore e build Release `-warnaserror` `rc=0`, 0 avisos, 11 assemblies. Duas rodadas completas: unidade **193/193** e integração **118/118** em ambas, com nomes idênticos entre as rodadas. Dos 308 nomes da parte A, um foi substituído de propósito (o antigo `R07a_...`); os 273 nomes anteriores à parte A continuam todos presentes; os novos são `R02b`, `R02c`, `R06d` e o `R07a` reescrito. Mutações em cópia descartável, cada uma com o positivo antes: MX-A01 (`Assert.False` falhou: a liberação vencida teve efeito), MX-A02 (`Assert.Equal(11, 12)`: o não membro foi consumido), MX-A03 (o plano antigo foi aceito) e MX-A04 (o diagnóstico de perda não foi emitido; reprova por ausência do evento esperado). Duas tentativas anteriores foram invalidadas e ficaram preservadas: MX-A02 em `c1` reprovou por *timeout* de uma barreira, e a asserção foi ajustada para comparar o valor exato; MX-A04 em `c1` não localizou o trecho (codificação no auxiliar).

**Limites.** A sondagem com processos reais da parte A não foi repetida depois deste lote (a alteração é uma condição no SQL de liberação e testes). O lote não executa as partes B e C, a CI não o executou, e a migration continua sem aplicação nos bancos de desenvolvimento.

## 11. Ciclo P08, parte B: verificação independente e reconstrução em banco paralelo

**Estado:** implementado e **executado pelo executor** (execução local, 20/10/2026), aguardando auditoria independente. Não é aprovação da governança nem execução da CI (a CI ainda não executou estas alterações). A parte C (ensaio dos 50 req/s) **não foi executada**: o requisito segue obrigatório e sem medição. Escopo e limites operacionais: [recuperacao.md](recuperacao.md) e ADR-15.

> **Nota datada (20/10/2026).** Esta parte foi conduzida por outro auditor; a governança atual **não a reabre nem a endossa**. Os testes dela estão na suíte completa reexecutada nos pareceres seguintes (344 e 365 nomes). Estado vigente: seção 15.1.

### 11.1 Controles R09 a R14

| ID | O que prova | Testes / evidência |
|---|---|---|
| R09 | Verificador positivo em estado conhecido (viva e convergência, `--verify` sem alterar nenhuma linha: `xmin` de todas as tabelas igual antes e depois); sete controles negativos que respeitam as CHECKs, confirmados no banco antes de invocar o verificador, cada um reprovando **só** pelo tipo de divergência pretendido: crédito e `net` correspondente, erros compensados em duas contas com soma global preservada, recibo removido (convergência: `missing_receipt`), recibo sem movimento, agregado extra válido, descritor adulterado só no destino e nos dois lados | `R09a`, `R09b` (saídas 0, 4 e 2 pelo executável), `R09c` (7 cenários) |
| R10 | Captura interrompida antes do commit da selagem: nenhum conjunto concluído e nenhum item copiado; depois do commit: a repetição retoma exatamente o mesmo R (digest e snapshot iguais) mesmo com movimentos novos na origem; conjunto vazio capturado, reaplicado e reconciliado; teto de massa validado antes da cópia | `R10a` a `R10d` |
| R11 | Queda no meio do replay: depois de um commit (retoma só o que faltava, recibos já gravados não reaplicados) e dentro da transação do item (nada persistido, aplica uma vez na retomada); a Outbox da origem fica idêntica (status, `processed_at`, `xmin`) | `R11a`, `R11b` |
| R12 | Movimentos já confirmados, informação tardia e eventos ainda pendentes na origem entram em R; POST aceito durante a captura (retida por barreira) e durante o replay fica fora de R; antes da promoção um Worker no destino habilitado não consome; depois, os itens de R pendentes são deduplicados (2 duplicatas evitadas) e os posteriores chegam (2 aplicações novas); verificação viva e de convergência consistentes | `R12` |
| R13 | Alvo errado, origem como destino e projeção ativa por engano recusados sem escrita; promoção antes de reconciliar recusada; promoção recusada com Worker conectado e aceita sem ele; repetição idempotente; depois da promoção o Worker no banco antigo não consome, a API no banco antigo responde `503 projection_superseded` e a API no banco novo responde 200 com os valores certos; nenhum retorno seguro é anunciado | `R13a`, `R13b` |
| R14 | Processos reais (executáveis Release): dois Workers e duas APIs, parada de um componente por vez, captura recusada com API conectada, reconstrução, promoção recusada e depois aceita, troca para o banco reconstruído e bloqueio do banco antigo; nenhuma parada da origem | sondagem `Sondagem-P08B.ps1`, 17 passos, 0 falhos |

### 11.2 Comandos e resultados (fonte: executor)

- Restore `rc=0`; build Release `--no-incremental -warnaserror` `rc=0`, 0 avisos, 11 assemblies.
- Duas rodadas completas: unidade **199/199** e integração **136/136** em ambas, nomes idênticos entre as rodadas (335). Referência: os **311 nomes** conferidos na parte A complementar, todos presentes; 24 novos (6 de unidade e 18 de integração: R09a, R09b, sete casos de R09c, R10a a R10d, R11a, R11b, R12, R13a, R13b).
- Sondagem com processos reais: 17 passos, 0 falhos, 0 ocorrências na varredura de vazamento; containers com o mesmo identificador antes e depois; bancos `ft_p08_*` removidos. Números observados: 40 POST consumidos por dois Workers (esperado 107,00 de crédito e 20 movimentos no dia, igual nas duas APIs); R de 55 movimentos; Outbox da origem inalterada pelo replay (13 pendentes e 45 confirmados antes e depois); duplicatas evitadas 10 (esperado 10) e aplicações novas 3 (esperado 3) depois da promoção. Uma primeira execução da sondagem foi invalidada por formatação decimal da cultura local no auxiliar (todos os POST responderam 422) e ficou preservada.
- Mutações dirigidas, em cópia descartável, cada uma com o positivo antes: verificador sempre consistente (R09c reprova), capturar só eventos pendentes (R12 reprova: conjunto R diferente), promover sem recusar sessões de consumidores (R13b reprova: promoção aceita com Worker conectado), Worker consumindo projeção que não é a ativa e API servindo projeção que não é a ativa (R13b reprova nas duas). Uma mutação inicial da recusa de sessões foi invalidada por erro de compilação e repetida.

### 11.3 Limites

Mesmo host, mesmo par de bancos: demonstra mecanismos, não alta disponibilidade. A reconstrução exige consumidores e APIs conhecidos parados, mantém um snapshot longo na origem e copia R em uma única transação do destino; o maior volume exercitado foi de 55 movimentos, e a escala de 100.000 **não foi medida**. O controle de consumidores só enxerga processos que se identificam pelo nome de aplicação. O resultado desconhecido do commit da selagem é tratado por releitura, mas só a falha injetada antes e depois do commit foi exercitada. Os cinco itens do README (ensaio de carga etc.) não foram atendidos por esta parte. A migration `AddProjectionRegistry` (origem) e a `AddRebuildRecovery` (projeção) não foram aplicadas nos bancos de desenvolvimento.

## 12. Ciclo P08, parte B complementar: exclusividade administrativa, promoção parcial e teto

**Estado:** implementado e **executado pelo executor** (execução local, 20/10/2026), aguardando auditoria independente. Parte da referência publicada `93f7698` (parte B). Não é parecer independente nem execução da CI sobre estas alterações; a parte C **não** foi executada e o requisito dos 50 req/s com no máximo 5% de perda segue obrigatório e sem medição. A execução 13 da CI (id `39443000111`, `completed/success`, SHA `93f7698`) é **histórica** da parte B publicada; foi consultada na API pública do GitHub em 20/10/2026 e não cobre esta correção. Comportamento e limites: [recuperacao.md](recuperacao.md) e o adendo da ADR-15.

> **Nota datada (20/10/2026).** Este lote foi conduzido por outro auditor; a governança atual **não o reabre nem o endossa** (parecer privado do vínculo da origem). Os testes dele foram reexecutados depois, como parte da suíte completa, nos pareceres de P08 B (vínculo) e P08 C. Estado vigente: seção 15.1.

### 12.1 Controles

| ID | Consequência exposta antes da correção (Red) | Teste |
|---|---|---|
| P08B-01 | Duas capturas com identidades diferentes: a segunda selava a operação preparada pela primeira e respondia sucesso; cancelamento aceito no meio do replay e durante a promoção | `R13c`, `R13d`, `R13e` |
| P08B-02 | Cancelar um destino `enabled` cuja promoção já estava registrada na origem; origem ilegível não impedia o cancelamento; zero linhas na marca local ainda anunciava `promoted=true` | `R13f`, `R13g`, `R13h` |
| P08B-03 | Um lançamento confirmado entre o preflight e o snapshot fazia a captura exceder o teto e selar | `R10e` |

Os sete casos foram escritos primeiro, com pontos de sincronização nomeados (instrumentação sem mudança de comportamento) e **sem dormir como condição de prontidão**: o teste espera o sinal de que o comando alcançou o ponto. No código anterior os sete reprovaram pela causa pretendida (sete falhos de sete; saídas em `retomada-04\parte-B-complementar\red`); depois da correção os sete passaram. Natureza: banco PostgreSQL real descartável e coordenadores reais num mesmo processo de teste (as sessões e o lock são de verdade); `R13h` usa SQL direto declarado para provocar o zero de linhas. Os pontos de falha existem apenas nos hosts de teste (`IRebuildFailurePoints`, padrão sem efeito).

### 12.2 Comandos e resultados (fonte: executor)

- Restore `rc=0`; build Release `-warnaserror` `rc=0`, 0 avisos, 11 assemblies.
- Duas rodadas completas: unidade **199/199** e integração **143/143** em ambas (quatro execuções com identificadores distintos), 342 nomes idênticos entre as rodadas; os **335** da referência publicada todos presentes; **7 novos** (`R10e`, `R13c` a `R13h`). Nenhum caso foi alterado ou removido.
- Sondagem complementar com processos reais (9 passos, 0 falhos, 0 vazamentos, containers com os mesmos identificadores e saudáveis, bancos `ft_p08_*` removidos): comando real de captura recusado (saída 4, nada preparado) enquanto uma sessão SQL segura a exclusividade do alvo (simulação do concorrente); captura de 12 movimentos, replay, reconciliação e habilitação; cancelamento recusado (saída 4) com o registro da origem já confirmado (criado por SQL, declarado); retomada do `promote` completa a marca sem alterar a geração do registro; repetição idempotente; `--verify` viva e de reconstrução consistentes; Worker no banco antigo permanece em `ProjectionSuperseded`. Não repete a sondagem de 17 passos da parte B.
- Mutações dirigidas em cópia descartável, cada uma com positivo antes: chaves de exclusividade diferentes por comando (R13c, R13d e R13e reprovam), cancelamento ignorando o registro da origem (R13f), promoção sem conferir linha e estado (R13h), teto não revalidado no snapshot (R10e), cancelamento sem ler a origem (R13g). A primeira tentativa da mutação do R13h foi invalidada por erro de compilação (código inalcançável) e foi repetida.

### 12.3 Disposição e limites

- **P08B-01, P08B-02, P08B-03:** corrigidos e testados como acima. A exclusividade é um lock consultivo de sessão no banco de destino: coordena comandos deste coordenador entre processos, não impede SQL direto nem outros programas, e depende de o destino aceitar conexão. A concorrência real entre dois coordenadores foi exercitada por testes (processo único, sessões distintas); com processos separados só o concorrente simulado por SQL.
- **Promoção parcial:** a decisão é por releitura do registro da origem, sem estado novo nem migration. O resultado desconhecido do commit do registro continua tratado por releitura; não há transação distribuída, rollback transparente nem retorno ao banco antigo. A redundância das verificações de identidade (rótulo conferido de novo na preparação e sob `FOR UPDATE`) não tem mutação própria porque a exclusividade as cobre antes.
- **P08B-04 (documentação apenas):** a verificação de sessões exige acesso ao banco anterior, resolvido no servidor configurado para o destino; recuperação de banco ou servidor definitivamente perdido **não está demonstrada**. Nada foi implementado para isso.
- **P08B-05:** ver o adendo de procedência do checkpoint complementar. Nesta rodada não houve `git add`, nem `-N`, nem `git reset`.
- Continuam valendo os limites da parte B: escala de 100.000 movimentos não medida (teto de 250.000, revalidado no snapshot), controle de consumidores por nome de aplicação, dois processos num host não são alta disponibilidade, e as migrations `AddProjectionRegistry` e `AddRebuildRecovery` não foram aplicadas nos bancos de desenvolvimento (esta rodada não criou migration).

## 13. Ciclo P08, parte B: vínculo da origem da operação (P08B2-01)

**Estado:** implementado e **executado pelo executor** (execução local, 20/10/2026), aguardando auditoria independente. Parte da referência publicada `7bc7463` (pai `93f7698`). Não é parecer independente; a parte C **não** foi executada e os 50 req/s com no máximo 5% de perda seguem obrigatórios e sem medição.

> **Nota datada (20/10/2026).** A governança aprovou este lote no parecer privado do vínculo da origem: restore, build e testes reexecutados (199 + 145, 344 nomes iguais aos do executor) e leitura integral do diff. A aprovação é do conteúdo desse lote; não endossa os lotes anteriores. Estado vigente: seção 15.1.

**Correspondência de governança.** A mensagem do commit `7bc7463`, "P08 parte C", não corresponde ao conteúdo: o diff tem exatamente os sete arquivos do complemento da parte B (§12). A parte C não foi executada. O histórico não foi alterado.

**CI.** A execução 14 (id `39468539719`, `completed/success`, 199 + 143 = 342 testes) é **histórica**: refere-se ao SHA `7bc7463`, não a esta correção. Esta correção não tem execução remota; qualquer menção futura exige nova consulta.

### 13.1 Defeito e controle

O coordenador persistia `source_database`, mas o cancelamento, a promoção, a reconciliação e a captura retomada usavam a origem configurada sem conferir se era a origem **desta** operação. Uma origem errada, porém acessível e migrada, devolve registro vazio, sem erro de conexão, e isso era lido como "não promovido".

| ID | Consequência no código anterior (Red) | Teste |
|---|---|---|
| P08B2-01 | Com a promoção já confirmada na origem verdadeira, `cancel` configurado com outra origem acessível cancelava a operação e respondia sucesso | `R13i` |
| P08B2-01 | Preparação retomada com outra origem acessível (com cópia do registro de controle e zero movimentos) selava um conjunto R vazio e respondia sucesso | `R13j` |

Os dois testes foram escritos primeiro e reprovaram no código anterior pela causa pretendida (cancelamento aceito; captura selada a partir da origem errada). A origem errada é um banco real descartável (`ft_test_decoy_*`), nunca uma adulteração do alvo. Depois da correção passaram.

### 13.2 O que é validado, e o limite

Sob a exclusividade e antes de usar a origem como autoridade: o nome efetivo do banco (`current_database()`) precisa ser igual ao `source_database` persistido na preparação e, depois da selagem, o identificador histórico do bootstrap do registro de controle da origem precisa ser igual ao `origin_bootstrap_id` persistido. Divergência acessível recusa (saída 4) sem alterar o destino, o registro verdadeiro, a origem divergente nem R; origem ilegível segue desconhecida (saída 2). O descritor nunca é reescrito, nenhuma credencial é lida ou gravada, não há migration nem opção de contorno, e o `replay`, que só opera no destino, não consulta a origem.

**Limite:** o nome de um banco não é identidade global entre servidores. Duas instâncias com bancos homônimos só se distinguem, depois da selagem, pelo identificador histórico; antes da selagem não se distinguem. A verificação do identificador histórico não tem teste isolado (a origem errada dos testes difere também no nome), apenas no conjunto coberto pela mutação MB3-02.

### 13.3 Comandos e resultados (fonte: executor)

- Restore `rc=0`; build Release `-warnaserror` `rc=0`, 0 avisos, 11 assemblies.
- Duas rodadas completas: unidade **199/199** e integração **145/145** em ambas (quatro execuções com identificadores distintos), 344 nomes idênticos entre as rodadas; os **342** da referência presentes; 2 novos (`R13i`, `R13j`); nenhum caso alterado ou removido.
- Sondagem com processos reais (10 passos, 0 falhos, 0 vazamentos, containers iguais e saudáveis, bancos `ft_p08_*` removidos): repete os passos da §12 com a validação ativa e acrescenta X5, `cancel` e `promote` pelo CLI real com uma origem errada acessível e migrada: ambos saem com 4, o destino continua `enabled`, a origem errada não recebe registro e a geração do registro verdadeiro não muda; a retomada com a origem correta conclui sem incrementá-la. O estado parcial continua criado por SQL, como declarado na §12.
- Mutações dirigidas, em cópia descartável, cada uma com positivo antes: nome do banco não comparado (R13j reprova) e nenhuma conferência de vínculo (R13i e R13j reprovam).

### 13.4 Redação sobre zero linhas

A decisão da marca local da promoção é pela pós-condição relida (estado `promoted` e registro da origem igual à projeção), não por uma regra de recusar qualquer zero: uma repetição idempotente que afeta zero linhas sobre um estado já concluído é sucesso. A frase absoluta da §12.1 ("zero linhas ... nunca sucesso") deve ser lida por esta redação.

### 13.5 Limites que continuam

Escala de 100.000 movimentos não medida; exclusividade só entre comandos deste coordenador; dois processos reais do coordenador só simulados por sessão SQL; recuperação de banco/servidor perdido não demonstrada; migrations `AddProjectionRegistry` e `AddRebuildRecovery` não aplicadas nos bancos de desenvolvimento (esta rodada não criou migration).

## 14. Ciclo P08, parte C: ensaio do consolidado em chegadas abertas

**Estado:** implementado e **executado pelo executor** (execução local, 20/10/2026), aguardando auditoria independente. Parte da referência publicada `7977b1e` (a CI desse SHA, run 15, é fato histórico dele e **não** cobre nada da parte C). Não é aprovação da governança nem execução da CI. Método, critérios e resultados completos: [carga-consolidado.md](carga-consolidado.md). A alta disponibilidade de produção (P09) não foi tratada.

> **Nota datada (20/10/2026).** A governança aprovou esta parte no parecer privado de P08 C: build e uma rodada completa reexecutados (220 + 145, 365 nomes iguais aos do executor) e **L1 reproduzido** (golden próprio, uma repetição, 3.000 úteis de 3.000, 50 emissões em cada segundo). **Reproduzido pela governança: L1; demais cenários (L2, L3, F08), mutações e EXPLAIN: execução do executor conferida por leitura das evidências.** Isto não é auditoria independente do que não foi reexecutado e nada aqui é atribuído à CI. Estado vigente: seção 15.1.

### 14.1 O que foi medido

| Cenário | Repetições | Resultado por repetição (cada uma por si) |
|---|---|---|
| L1: 3.000 GET por conta a 50/s | 2 | válidas; 3.000 úteis (perda 0%); todos os corpos `complete` e iguais ao oracle; 50 emissões por segundo |
| L2: 3.000 GET por cliente a 50/s | 2 | idem, com as duas contas e os totais conferidos |
| L3: 3.000 GET (80% conta, 20% cliente) a 50/s + 600 POST a 10/s, 2 APIs e 2 Workers | 2 | válidas; 3.000 úteis; 1.500 GET por instância; 23 e 27 respostas `stale` dentro do contrato, nenhuma `unknown`; 600 de 600 POST 201; convergência em menos de 1 s; reconciliação exata (origem, projeção, ledger e 100 leituras finais pela API) |
| F08 (a) Worker 1 encerrado por PID e religado | 1 | 600 de 600 POST 201; convergência em 0,34 s; reconciliação exata |
| F08 (b) API 1 encerrada e religada | 1 | idem; 17 sondas daquela URL sem resposta durante a falha, a outra URL respondeu todas |
| F08 (c) container da consolidação parado e religado | 1 | idem; 34 sondas de 60 com 503 por URL; backlog de até 396 eventos (pendência mais antiga 39,5 s); convergência em 0,61 s |

Cada repetição usou um par de bancos novo, clonado de um par de referência que passou pelo bootstrap real da massa de 100.000 movimentos (seed e validação independente em 4,6 s; coordenador 2,5 s; bootstrap até `live` em 1.645 s com um Worker; `--verify` live e convergence em 1,6 s e 1,5 s). A fonte de cada número é o `resumo.json` da ferramenta (evidência privada), calculado pela própria ferramenta; o verificador independente do Worker confirmou live e convergence antes e depois de cada repetição.

### 14.2 Controles do gerador e mutações

- 21 testes novos (`LoadGeneratorTests`): ver a seção 2 de [carga-consolidado.md](carga-consolidado.md). Natureza: servidor sintético em memória, sem o produto; o relógio é controlável (um provedor que "congela" o laço de programação).
- Mutações dirigidas, em cópia descartável, cada uma com positivo antes e detectada por teste específico: gerador que espera a requisição anterior (reprova `SlowResponses`), tardias e não emitidas fora do denominador (reprova o limite de 150/151), slot atrasado emitido em rajada (reprova `SlotLateBeyondTheTolerance`).
- Regressão: restore e build `-warnaserror` com rc 0 e 0 avisos; duas rodadas completas de **220 testes de unidade e 145 de integração (365)**, 365 nomes idênticos entre as rodadas, os 344 anteriores presentes, 21 novos.

### 14.3 Limites e desvios

- Máquina única (3 núcleos, 6 processadores lógicos): API, Workers, gerador e os dois PostgreSQL dividem os recursos. Duas APIs e dois Workers num host **não são** alta disponibilidade. Os critérios (1.000 ms, 60 s, 120 s, massa) são hipóteses de laboratório, não requisitos do contratante.
- A primeira rodada dos cenários foi executada com uma versão anterior da ferramenta; a execução F08 (c) dessa rodada falhou por defeito da própria ferramenta (exceção não tratada ao reler o banco de projeção logo após o religamento do container). Foi corrigido apenas o tratamento de leituras transitórias na espera de convergência e na reconciliação, e **todos** os cenários foram repetidos com a versão final. Os registros da primeira rodada e da execução inválida foram preservados e não entram nos números acima.
- Os 6 a 8 primeiros POST da janela foram mais lentos (até ~1,7 s), compatível com partida a frio da API de lançamentos; não entram no orçamento de 5% (das consultas) e não foram investigados.
- A captura e o replay da **reconstrução** em banco paralelo na escala de 100.000 movimentos não foram medidos; foram medidos seed, bootstrap e verificação nessa escala. A escala da reconstrução continua um limite declarado.
- Nenhuma mudança de índice, consulta, pool ou migration foi necessária: L1, L2 e L3 foram aprovados. O diagnóstico de pendências tem custo proporcional ao histórico da conta (a Outbox é conservada); um índice parcial é a opção se o histórico crescer.
- O estado deste item da matriz da seção 5 passa a **executado pelo executor**, não a "aprovado".

## 15. Ciclo P09: documentação consolidada e matriz final

Ciclo de **documentação e aceitação**: nenhum código de produto foi alterado e nenhum teste foi removido. A parte 1 (esta árvore) consolida os documentos; a parte 2 (aceitação por checkout novo da versão publicada) preenche o portão da seção 8.9.

### 15.1 Matriz final dos cinco apontamentos

Origem dos apontamentos: "Fontes e procedência" em [escopo.md](escopo.md). Estados segundo H14; a coluna "Quem executou" diz o que cada fonte comprova e o que não. A seção 9.1 mostra o estado de P07 na data dele; esta tabela o substitui.

| # | Apontamento | Estado final | Quem executou | Evidência | Documento |
|---|---|---|---|---|---|
| 1 | Independência dos lançamentos em relação ao consolidado | **implementado; executado pelo executor** | Testes C1 a C5 e C13, sondagem com processos reais (P07) e F08 a, b e c com 600/600 POST aceitos (P08 C): executor. Os testes automatizados foram **reexecutados pela governança** (pareceres de P08 B vínculo e P08 C). A sondagem e o F08 foram conferidos por leitura das evidências, **não reproduzidos** pela governança | seções 9.2, 9.5 e 14; [carga-consolidado.md](carga-consolidado.md), seção 5 | [arquitetura.md](arquitetura.md) (ADR-1, ADR-10, ADR-16), [alta-disponibilidade.md](alta-disponibilidade.md) (seções 2 e 3) |
| 2 | Estratégia de processamento e recuperação | **implementado; executado pelo executor** | Retomada sem duplicata, classes de falha, quarentena e reprocessamento, reserva durável com dois Workers, verificação independente e reconstrução em banco paralelo (P07, P08 A e B): executor, com testes reexecutados pela governança. **A reconstrução foi executada em pequena escala** (dezenas de movimentos) | seções 9, 10, 11, 12 e 13; [recuperacao.md](recuperacao.md) | [arquitetura.md](arquitetura.md) (ADR-10, ADR-13 a ADR-15), [recuperacao.md](recuperacao.md) |
| 3 | 50 req/s com perda de até 5%, cada resultado ligado ao cenário | **executado pelo executor; reproduzido pela governança (L1)** | L1, L2, L3 e F08 (P08 C): executor. A governança reproduziu **L1** com golden próprio (3.000 úteis de 3.000, p99 desde a programação 25,4 ms) e conferiu L2, L3 e F08 por leitura das evidências, sem reproduzi-los. Máquina única; critérios são hipóteses de laboratório (D3-P08) | seção 14; parecer de P08 C | [carga-consolidado.md](carga-consolidado.md) |
| 4 | Alta disponibilidade (pontos de falha, recuperação, escala horizontal) | **proposto** | Nenhuma execução: é um desenho. O que está **medido** são os pontos únicos de falha da versão atual e as mitigações existentes (independência, lease, retomada, reconstrução) | seções 9, 10, 11 e 14 | [alta-disponibilidade.md](alta-disponibilidade.md) |
| 5 | Aderência ao enunciado (obrigatório × hipótese) | **executado** (revisão documental) | Esta tentativa: escopo com requisitos, orientações complementares e hipóteses H1 a H15 com a procedência de cada uma; verificador de links e privacidade (seção 15.3) | [escopo.md](escopo.md) | [escopo.md](escopo.md) |

Leitura honesta da matriz: nenhum item está "aprovado" por terceiros nem foi verificado pela CI no que tem de laboratório (sondagens, mutações, ensaios de carga). O item 4 é uma proposta e continua sendo, mesmo depois da aceitação por checkout novo.

### 15.2 O que mudou na parte 1

| Arquivo | Mudança |
|---|---|
| `docs/alta-disponibilidade.md` | **Novo.** Topologia atual, pontos únicos de falha, matriz de modos de falha (detecção, impacto, recuperação, limite), política de dados, arquitetura proposta, objetivos propostos, escala horizontal, broker e tabela de evidências, com cada afirmação rotulada |
| `docs/arquitetura.md` | Seção "Requisitos não funcionais e trade-offs", ADR-16, seção "Local × proposto", "Evoluções" sem texto de estado de ciclo e com as evoluções que continuam propostas |
| `docs/escopo.md` | "Procedência de cada hipótese", "Fora de escopo" revisado, "Decisões do ciclo P09" e correção de frases de estado de ciclo já desatualizadas |
| `docs/verificacao.md` | Seção 0 (estado atual da entrega), notas datadas nas seções 9 a 14, esta seção, seção 5.3 (CI da baseline), pendências revistas (seção 6), checklist e portão da versão final (7.1 e 8.9) |
| `docs/carga-consolidado.md` | Seção "Como reproduzir sem o kit" com comandos completos (bancos por `psql`, variáveis por processo, portas, espera por `live` via `--status`); taxa por rota em L3 (≈ 40/s conta, ≈ 10/s cliente); nenhum número do ensaio mudou |
| `README.md` | Índice com `alta-disponibilidade.md`, seção "Estado da verificação", seção de CI reescrita, itens de P08 no que está implementado, frases desatualizadas corrigidas |

Nenhum arquivo em `src/`, `tools/`, `tests/`, `.github/` ou de configuração foi alterado.

### 15.3 Verificação da árvore da parte 1

Fonte: *esta tentativa* (executor), 20/10/2026, sobre a baseline `15a657f` com as mudanças acima ainda sem commit. **Não é a aceitação por checkout novo** (seção 8.9) e não é execução da CI.

| Verificação | Resultado |
|---|---|
| Restore `dotnet msbuild FinancialTransactions.sln -target:Restore -warnaserror` | `rc` 0 |
| Build `dotnet build … -c Release --no-restore --no-incremental -warnaserror` | `rc` 0; `0 Warning(s)`, `0 Error(s)` |
| Testes de unidade (uma rodada) | `rc` 0; **220 de 220** aprovados, 0 ignorados |
| Testes de integração (uma rodada) | `rc` 0; **145 de 145** aprovados, 0 ignorados |
| Nomes de teste | 365 nomes, iguais aos da última rodada do ciclo P08 (nenhum ausente, nenhum a mais): nenhum teste foi removido |
| Verificador de links, âncoras, títulos únicos, tipos de diagrama e privacidade, sobre o README e os sete documentos de `docs/` | **0** problemas; **0** ocorrências de privacidade fora das URLs permitidas; 7 diagramas Mermaid. Limites do verificador como na seção 2.3 |
| Varredura de nomes, e-mails, empresa, processo, caminhos pessoais e material privado nos seis arquivos alterados | 0 ocorrências novas |

Uma única rodada de testes é suficiente aqui porque nada de código mudou: a rodada prova que a árvore compila e que a suíte continua inteira, não que o código é estável.

**Complemento (v1.1 do prompt), 20/10/2026.** Depois do parecer da governança sobre a parte 1 foram acrescentados a matriz de modos de falha e a política de dados (`alta-disponibilidade.md`), a seção 0 e as notas datadas (este documento), a distinção posição por instante × consolidado materializado (README) e os comandos completos e a taxa por rota (`carga-consolidado.md`). Repetidos sobre a árvore resultante: restore e build `-warnaserror` `rc` 0 (0 avisos, 0 erros), verificador 0 problemas e 0 ocorrências de privacidade, varredura de nomes sem ocorrência nova. **Sem nova rodada de testes**: nada de código mudou desde a rodada de 220 + 145 acima. Os comandos de reprodução de `carga-consolidado.md` foram escritos a partir do código e dos documentos e **não foram reexecutados** nesta etapa.

### 15.4 Parte 2: aceitação por checkout novo

A parte 2 foi executada em 20/10/2026 sobre o SHA publicado 5c3beb0 e está registrada nas seções 5.4, 7.1 e 8.9: roteiro de aceitação c 0 (220 + 145), README reproduzido no clone (19 verificações, 0 divergentes), histórico de 20 revisões varrido, autoria uniforme e CI do SHA success (run 17). O commit que registra esses resultados altera só docs/verificacao.md, o README e docs/carga-consolidado.md (correção do passo de limpeza do roteiro de reprodução, que apaga os arquivos e pastas que o roteiro cria na raiz do repositório). Nenhum código de produto e nenhum teste mudou.
