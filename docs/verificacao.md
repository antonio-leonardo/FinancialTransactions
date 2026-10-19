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
| R3. Código em repositório público no GitHub | `https://github.com/antonio-leonardo/FinancialTransactions` | repositório público desde 18/10/2026 (constatado pela governança); a consulta anônima à API do GitHub desta tentativa (sem credencial) devolveu a execução da CI | implementado; **checkout novo não verificado nesta tentativa** (fica para P06) | 19/10/2026, esta tentativa (consulta); clone limpo: pendente |
| R4. README com instruções claras para executar localmente | `README.md` | roteiro do README executado contra banco descartável (seção 2.2); o `git clone` não foi executado (P06) | executado e aprovado, exceto o `git clone` (bloqueado/não verificado, P06) | 19/10/2026, esta tentativa |
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
| T14 | Correção e reconciliação | reconciliação da ferramenta `tools/FinancialTransactions.LoadTest/Reconciliation.cs` | reconciliação pós-carga ([carga.md](carga.md)); não há teste automatizado de correção | **parcial**: reconciliação pós-carga executada nas duas execuções do ensaio, sem divergências; correção e estorno fora de escopo (ver seção 6) | 19/10/2026, executor local e governança local (P04) |
| T15 | Mensageria | nenhum | nenhum | **não aplicável**: H12 (sem broker nem Outbox) | n/a |
| T16 | Carga e aceitação | `tools/FinancialTransactions.LoadTest`, [carga.md](carga.md), `.github/workflows/ci.yml` | ensaio de carga (duas execuções) e CI | carga: executado e aprovado (executor local e governança local, 19/10/2026, P04); CI: executada com sucesso para o SHA `abcdf19` (seção 5); **aceitação em cópia limpa: bloqueado/não verificado, fica para P06** | ver seções 4 e 5 |

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

**Adaptações de laboratório em relação ao texto do README**, todas declaradas: (1) banco descartável: `dotnet ef database update` com `--connection` e a variável `FT__ConnectionStrings__FinancialTransactions` apontando para ele, em vez do banco de desenvolvimento; (2) a API foi iniciada em processo separado, com saída em arquivo; (3) as chamadas HTTP foram feitas com `Invoke-WebRequest -SkipHttpErrorCheck` (para capturar status e corpo bruto, inclusive o do `422`), em vez de `Invoke-RestMethod`; (4) token e chave nunca foram gravados. O `git clone` não foi executado (verificação por checkout novo é de P06). As mesmas chamadas, na ordem do README mais a repetição e o conflito:

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

Fonte: *CI* (consulta anônima à API pública do GitHub, 19/10/2026 06:10 UTC, nesta tentativa) e *leitura do auditor-master* (log).

| Item | Valor |
|---|---|
| Workflow | `ci` (`.github/workflows/ci.yml`), disparado em `push` na `main` e em `pull_request` |
| SHA | `abcdf19c073dc95505010a593126248dc2bcc79a` (também o HEAD local desta tentativa) |
| Execuções para esse SHA | 1: [run 39201091893](https://github.com/antonio-leonardo/FinancialTransactions/actions/runs/39201091893), número 3, tentativa 1, evento `push`, criada em 19/10/2026 04:45 UTC, `status` `completed`, `conclusion` `success` |
| Job | `build-and-test` (id 117894019834), 04:45:41 a 04:46:42 UTC, `success`. Etapas: preparação do job, inicialização do contêiner PostgreSQL, checkout, setup do .NET, Restore, Build, Test e finalizações, todas `success` |
| Comandos do workflow | `dotnet msbuild FinancialTransactions.sln -target:Restore -warnaserror`; `dotnet build FinancialTransactions.sln -c Release --no-restore --no-incremental -warnaserror`; `dotnet test FinancialTransactions.sln -c Release --no-build --settings tests/vstest.runsettings`, com `FT_TEST_ADMIN_CONNECTION` para o serviço `postgres:17` |
| Ambiente e contadores (log, **leitura do auditor-master**, 19/10/2026) | Ubuntu 24.04.5, SDK 10.0.402, PostgreSQL 17.11; restore e build com `0 Warning(s)`; sete assemblies; `Passed! 92` (unidade) e `Passed! 45` (integração) |
| Outros commits | segundo a *governança local* em 19/10/2026, o workflow também executou com `success` em `fe545e9` e `ccf7bc7`; **não reconferido nesta tentativa** |

Redação sobre avisos: restore e build **sem diagnósticos de warning de compilação**. O log do workflow contém avisos de outra natureza, que **não** são warnings de build: depreciação do módulo `punycode` (Node); aviso de que `actions/checkout@v4` e `actions/setup-dotnet@v4` declaram Node.js 20 e rodaram em Node.js 24; e mensagens `FATAL: database "ft" does not exist` do serviço PostgreSQL, porque a sonda `pg_isready -U ft` não informa a base (`POSTGRES_DB=postgres`). Estão registrados como pendências de manutenção do workflow (seção 6).

**O que a CI não executa:** o ensaio de carga; as sondagens manuais (reinício do PostgreSQL, limite de corpo contra Kestrel real); o roteiro do README. Também não publica relatórios de teste. A CI de `abcdf19` é evidência **dessa versão** do código; o commit que contém esta documentação terá a sua própria execução.

## 6. Verificações que não existem ou são parciais (pendências)

| Item | Situação |
|---|---|
| T09 | **Não aplicável**: sistema de registro, sem regra sobre o agregado (H1). |
| T15 | **Não aplicável**: sem mensageria (H12). |
| T14 | **Parcial**: reconciliação pós-carga executada; não há fluxo de correção ou estorno (fora de escopo). |
| Reconciliação | A reconciliação não encontrou divergências nas execuções registradas; não foram realizados controles negativos por adulteração para validar integralmente a capacidade de detecção do reconciliador. |
| Relógio nos testes | `TimeProvider` é injetado e resolvido como `TimeProvider.System`; a fixture de testes não oferece controle ou congelamento sistemático do tempo. |
| Checkout novo | O `git clone` do README e a aceitação em cópia limpa **não foram verificados** nesta tentativa; ficam para P06. |
| Diagramas Mermaid | Conferidos por leitura; renderização no GitHub fica para P06. |
| `GOV-P04-CI-01` | Manutenção do workflow (P06): sonda do serviço PostgreSQL com `-d postgres` (`pg_isready -U ft -d postgres`) para eliminar as mensagens `FATAL: database "ft" does not exist`. Não alterado nesta tentativa. |
| `GOV-P04-CI-02` | Manutenção do workflow (P06): avaliar versões das actions (`checkout`, `setup-dotnet`) que declaram Node.js 20. Não alterado nesta tentativa. |
| Ruído de log no replay | Cada replay gera dois registros de nível `fail` do EF Core (a violação `23505` esperada); sem erro técnico associado (medido em [carga.md](carga.md)). Alertas por nível de log precisariam excluí-los. Não alterado. |
| Máximos de latência da execução 2 do ensaio | Picos isolados (até 401,7 ms) com causa não investigada (ver [carga.md](carga.md)). |
| Divergências README ↔ código desta revisão | Corrigidas no texto, sem alterar código (ver "Decisões do ciclo P05" em [escopo.md](escopo.md)): replay "reconstruído a partir da resposta"; exclusividade da credencial de laboratório; estado da CI; comentário de posição sem chamada às 11:30. Nenhum defeito de código foi encontrado nesta revisão. |

## 7. Checklist de entrega

| Item obrigatório | Estado | Evidência |
|---|---|---|
| Implementação em C# | executado e aprovado | build de 7 projetos, `rc` 0 (seção 2.1, P05) |
| Testes automatizados | executado e aprovado | 92 + 45 testes, duas rodadas, `rc` 0 (seção 2.1, P05); CI: 92 + 45 (leitura do auditor-master) |
| Repositório público | implementado; verificação por checkout novo **não feita**, pendente para P06 | `https://github.com/antonio-leonardo/FinancialTransactions`, público desde 18/10/2026; consulta anônima à API do GitHub nesta tentativa |
| README para executar localmente | executado e aprovado, exceto o `git clone` (P06) | roteiro executado contra banco descartável (seção 2.2) |
| Documentação no repositório | executado e aprovado | `README.md` e `docs/` (escopo, arquitetura, verificação, carga); verificador de links e privacidade (seção 2.3) |
| Build com zero erros e zero avisos de compilação | executado e aprovado | `build -warnaserror` `rc` 0, `0 Warning(s)` e `0 Error(s)` (esta tentativa); CI sem diagnósticos de warning de compilação (run 39201091893) |
