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

## 6. Verificações que não existem ou são parciais (pendências)

| Item | Situação |
|---|---|
| T09 | **Não aplicável**: sistema de registro, sem regra sobre o agregado (H1). |
| T15 | **Executado e aprovado** em P07 (comunicação assíncrona por trabalho durável no banco, sem broker); era "não aplicável" até P06 por H12. Ver a seção 9. |
| T14 | **Parcial**: reconciliação pós-carga executada; não há fluxo de correção ou estorno (fora de escopo). |
| Reconciliação | A reconciliação não encontrou divergências nas execuções registradas; não foram realizados controles negativos por adulteração para validar integralmente a capacidade de detecção do reconciliador. |
| Relógio nos testes | `TimeProvider` é injetado e resolvido como `TimeProvider.System`; a fixture de testes não oferece controle ou congelamento sistemático do tempo. |
| Checkout novo | **Verificado em A** (seção 8): clone novo da URL pública em contexto sem credencial, roteiro de aceitação com `rc` 0 e passo a passo do README. Ressalva: o comando `docker compose up -d --wait` não foi exercitado a partir do clone (ver seção 8). |
| Diagramas Mermaid | Conferidos por leitura; **renderização no GitHub: pendente de confirmação visual do responsável do projeto** (não informada até esta aceitação). Em P07 foram acrescentados três diagramas (componentes, sequência do consolidado e estados do bootstrap), também só conferidos por leitura. |
| Ciclo P07 | Pendências próprias na seção 9.6: CI sem execução desta versão, consumidor único, ensaio do consolidado, alta disponibilidade, fuso anterior a 2004. |
| `GOV-P04-CI-01` | **Aplicada e executada com sucesso** (seção 5.2). O desaparecimento da mensagem sobre a base `ft` está registrado por leitura de terceiro de um run de árvore idêntica; o log do run de A não foi lido. |
| `GOV-P04-CI-02` | **Aplicada e executada com sucesso** para as duas actions (`checkout@v7`, `setup-dotnet@v6`; seção 5.2). |
| Descrição do repositório (About) | **Pendente (ação do responsável do projeto).** Consulta anônima à API em 19/10/2026: a descrição vigente ainda não está em redação própria (reproduz texto de origem externa); `topics` vazio e `homepage` nulo. Não é corrigível por commit: é metadado do GitHub. Sugestão: "API .NET 10 com histórico de créditos e débitos, consultas temporais de saldo e operações idempotentes. PostgreSQL e JWT para demonstração local." |
| E-mail dos commits | **Feito**: o responsável reescreveu o histórico em 19/10/2026 (reautoria). No clone de A, 9 commits, 18 de 18 campos (autor e committer) com um único domínio de e-mail e um único nome. Commits antigos podem continuar acessíveis por SHA no cache do GitHub; **não verificado**. |
| Histórico: varredura de textos | Feita nas 9 revisões, com ressalva sobre a prova positiva (seção 8.5). |
| Ruído de log no replay | Cada replay gera dois registros de nível `fail` do EF Core (a violação `23505` esperada); sem erro técnico associado (medido em [carga.md](carga.md)). Alertas por nível de log precisariam excluí-los. Não alterado. |
| Máximos de latência da execução 2 do ensaio | Picos isolados (até 401,7 ms) com causa não investigada (ver [carga.md](carga.md)). |
| Divergências README ↔ código da consolidação anterior | Corrigidas no texto, sem alterar código (ver "Decisões do ciclo P05" em [escopo.md](escopo.md)): replay "reconstruído a partir da resposta"; exclusividade da credencial de laboratório; estado da CI; comentário de posição sem chamada às 11:30. Nenhum defeito de código foi encontrado. |

## 7. Checklist de entrega

> Os estados desta seção e da seção 8 são da **versão A** (anterior ao ciclo P07). O ciclo P07 altera código, testes, compose, workflow e documentos; a versão que o contiver precisa de um novo portão (seção 8 refeita). Os resultados do ciclo P07 estão na seção 9.

Todos os estados desta seção se referem à versão A (commit `87645ed9e3ed57328eabbd55917062188f370547`); os detalhes estão na seção 8.

| Item obrigatório | Estado | Evidência |
|---|---|---|
| Implementação em C# | executado e aprovado | build de 7 projetos no clone novo de A, `rc` 0 (seção 8.3) |
| Testes automatizados | executado e aprovado | clone novo de A: 92 + 45 executados e aprovados, 0 ignorados, TRX novos (seção 8.3); CI de A: etapa Test `success` (seção 5.2) |
| Repositório público | executado e aprovado | clone novo da URL pública `https://github.com/antonio-leonardo/FinancialTransactions` em contexto sem credencial, `HEAD` = SHA da `refs/heads/main` pública (seção 8.4); o roteiro de aceitação também confere o SHA público pela API sem credencial |
| README para executar localmente | executado e aprovado, com a ressalva do passo do `docker compose` | passo a passo do README seguido no clone de A, 8 chamadas HTTP e a contagem final, todas OK (seção 8.4); o `docker compose up` não foi exercitado a partir do clone |
| Documentação no repositório | executado e aprovado | verificador de links e privacidade no clone de A: 0 problemas e 0 ocorrências (seção 8.4) |
| Build com zero erros e zero avisos de compilação | executado e aprovado | `build -warnaserror` do roteiro `rc` 0 no clone de A; CI de A: Build `success` |

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

## 9. Ciclo P07: consolidado diário independente

Esta seção registra o que o ciclo P07 implementou e o que foi **executado** (fonte: *executor local*, 19/10/2026), separando o que está feito do que fica para os ciclos seguintes. A governança reexecuta por conta própria; nada aqui é parecer. As hipóteses H15 (consolidado diário por dia de negócio em `America/Sao_Paulo`) e a substituição de H12 são **decisões do responsável do projeto em 19/10/2026**, não requisitos do contratante.

### 9.1 Adequações sugeridas: estado por item

Fonte de cada item: as **adequações sugeridas** após a primeira entrega (ver "Fontes e procedência" em [escopo.md](escopo.md)). Estados segundo H14.

| # | Item | O que esta versão implementa | O que falta | Estado (H14) | Evidência |
|---|---|---|---|---|---|
| 1 | Independência dos lançamentos em relação ao consolidado diário | Processo e banco próprios para a consolidação; Outbox na transação do movimento; a escrita não conhece a projeção. Worker parado, projeção fora do ar e API do consolidado parada não impedem registrar, consultar a posição por instante nem a readiness da escrita | segunda instância do Worker e da API do consolidado (P08) | **executado e aprovado** nos cenários C1 a C5 (automatizados e sondagem contra processos reais), sem duas instâncias | seções 9.2, 9.4 e 9.5 |
| 2 | Estratégia de processamento e recuperação da consolidação | Classes de falha (transitória, ambiente, evento), *backoff* de 1 s a 30 s, quarentena por marcação (5 tentativas), reentrega idempotente, bootstrap reconciliado por identidade e saúde com desarme | reprocessamento administrativo da quarentena, métricas, reverificação ampla da projeção viva e reconstrução de destino perdido (P08) | **parcial** | C4, C6a, C6b, C7, C9a a C9h, C12a, C12b |
| 3 | Requisito de 50 req/s com no máximo 5% de perda, associando cada resultado ao cenário | nenhum ensaio do consolidado; o requisito está documentado como sugerido em [escopo.md](escopo.md) | ensaio do consolidado em cenário declarado (P08) | **proposto** | nenhuma (ensaio em P08) |
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

Fonte: *executor local*, 19/10/2026, na solução. SDK 10.0.402. Quatro rodadas de `dotnet test` (duas de cada projeto), com TRX por projeto e rodada.

| Etapa | Comando | Resultado |
|---|---|---|
| Restore | `dotnet msbuild FinancialTransactions.sln -target:Restore -warnaserror` | `rc` 0 |
| Build | `dotnet build FinancialTransactions.sln -c Release --no-restore --no-incremental -warnaserror` | `rc` 0; `0 Warning(s)`, `0 Error(s)`; 11 projetos |
| Testes de unidade (2 rodadas) | `dotnet test tests/FinancialTransactions.UnitTests -c Release --no-build --settings tests/vstest.runsettings` | `rc` 0; **173 de 173** aprovados nas duas |
| Testes de integração (2 rodadas) | idem, `IntegrationTests` | `rc` 0; **98 de 98** aprovados nas duas (cerca de 48 s) |
| Lista nominal | nomes do TRX | 271 nomes iguais nas duas rodadas; **os 137 nomes dos ciclos anteriores estão presentes**; 134 novos; nenhum ignorado |

A CI **não** executou esta versão (seção 9.6).

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

| Item | Situação |
|---|---|
| CI | O workflow foi alterado (segundo serviço PostgreSQL e variável de conexão administrativa), mas **não executou** esta versão. Os testes do consolidado têm esperas de tempo real e vão rodar em runner compartilhado: intermitência é possível e seria registrada. |
| Consumidor único | Reserva durável, mais de um Worker ou de uma API do consolidado, reprocessamento administrativo da quarentena, métricas, reverificação ampla da projeção e reconstrução de destino perdido: **P08**. |
| Carga do consolidado | 50 req/s com no máximo 5% de perda: **proposto**, sem ensaio. A meta de ≤ 2 s para a falha de dependência é hipótese do projeto, exercitada só para conexão recusada. |
| Alta disponibilidade | **P09**. |
| Fuso | Medido em 19/10/2026 (473.329 instantes, 2000 a 2026, passo de 30 min): o .NET desta máquina (Windows) e o PostgreSQL concordam de 2004 a 2026 e divergem em 212 instantes de 2000 a 2003. Faixa fora do uso do laboratório (as contas abrem em 2026); declarado em [arquitetura.md](arquitetura.md), ADR-12. O runner Linux da CI usa o `tzdata` do IANA e não foi verificado. |
| Mutações | As não executadas estão listadas na seção 9.4. |
| README | O passo a passo do consolidado no README (três processos, `--backfill`) foi **verificado pela sondagem** com os mesmos executáveis e variáveis, não por um roteiro de checkout novo: o `docker compose up -d --wait` e os `dotnet ef database update` contra os bancos de desenvolvimento **não** foram executados (apenas contra bancos descartáveis). O caminho `dotnet run` dos dois processos novos (perfil `http`, portas 5120 e 5110, `--backfill` com saída 2) foi exercitado com as connection strings apontando para uma porta fechada, sem tocar em nenhum banco: `live` 200 e `ready` 503 `Unhealthy` nos dois, e as portas liberadas depois. |
| Diagramas | Os diagramas novos (componentes, sequência e estados) foram conferidos por leitura; a renderização no GitHub permanece pendente. |
