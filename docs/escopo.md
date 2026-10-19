# Escopo e hipóteses

## Objetivo

Um sistema interno de um banco digital precisa registrar movimentações financeiras (créditos e débitos) nas contas de clientes e informar, sob demanda, a posição consolidada (saldo) de um cliente em um determinado instante. O sistema deve se comportar de forma confiável diante de instabilidade, alta demanda e falhas parciais, e deve proteger dados sensíveis.

## Requisitos obrigatórios da entrega

Os seis requisitos são independentes entre si e cada um é eliminatório:

1. a implementação é escrita em C#;
2. há testes automatizados;
3. o código está em um repositório público no GitHub;
4. o README traz instruções claras para executar o projeto localmente;
5. toda a documentação fica dentro do repositório;
6. o código compila sem erros e sem warnings.

## Procedência das hipóteses

O enunciado dá liberdade de estrutura e tecnologia e não determina moeda, SDK, banco de dados, mecanismo de autenticação nem política de saldo. As hipóteses abaixo foram propostas pela governança do projeto em 18/10/2026 para tornar o trabalho determinístico. Estado de aprovação: aprovadas pelo responsável do projeto em 18/10/2026. Qualquer alteração posterior exige revisão deste documento.

## Hipóteses H1 a H14

| # | Tema | Hipótese |
|---|---|---|
| H1 | Natureza do sistema | Sistema de **registro** de movimentações, não de autorização. Um débito é registrado mesmo que leve o saldo abaixo de zero, porque o fato financeiro já ocorreu. Saldo negativo é permitido. Proteção de limite sobre o agregado é evolução documentada, não implementada. |
| H2 | Clientes e contas | Um cliente possui uma ou mais contas, todas em BRL. Cadastros são sintéticos e semeados por migration (em P01); não há CRUD de clientes. |
| H3 | Valor de movimento | `MovementAmount`: `decimal` com escala 2, **estritamente positivo**, limite 1.000.000.000,00 por movimento (hipótese de laboratório). Tipo `Credit`/`Debit` separado do valor. Excesso de escala é **rejeitado**, nunca arredondado; a verificação é de representabilidade em centavos (`10.100m` equivale a `10.10m` e é aceito). Este tipo representa a magnitude de **um movimento**; saldos são `decimal` livres, podem ser negativos e não passam por essa fábrica. |
| H4 | Tempo | Instantes com offset (`DateTimeOffset`) na API; `timestamptz` (UTC) no banco. `effectiveAt` é informado pelo cliente da API; `registeredAt` é do servidor. Posição em `asOf` inclui movimentos com `effectiveAt <= asOf` (fronteira inclusiva). |
| H5 | Limites de `effectiveAt` | Rejeitado se `effectiveAt > agora + 5 min` (`effective_at_in_future`) ou se `effectiveAt < openedAt` da conta (`effective_at_before_opening`). `effectiveAt == openedAt` é aceito. |
| H6 | Origem por conta | Conta tem `openedAt` e saldo inicial comprovado zero nesse instante. Posição por conta com `asOf < openedAt` → 422 `historico_insuficiente`, nunca zero. Conta sem movimentos e `asOf >= openedAt` → zero. |
| H7 | Consolidação por cliente | Posição do cliente em `asOf` = soma das posições das contas do cliente **com `openedAt <= asOf`**. Contas abertas depois de `asOf` ficam fora do conjunto e são listadas como excluídas na resposta. Se nenhuma conta do cliente estiver aberta em `asOf` → 422 `historico_insuficiente`. Titularidade é fixa (sem transferência de conta entre clientes). |
| H8 | Limites de `asOf` | Rejeitado se `asOf > agora + 5 min` (`validation_error`), para manter coerência com H5. |
| H9 | Informação tardia | Admitida: movimento com `effectiveAt` no passado (respeitando H5) pode ser registrado depois; a posição histórica passa a refletir o conhecimento atual. Não há consulta "como se sabia em k". |
| H10 | Repetição | Header obrigatório `Idempotency-Key`. Identidade = (conta, chave). Conteúdo normalizado = fingerprint SHA-256 de tipo, valor com duas casas invariantes, `effectiveAt` em UTC e descrição. Protocolo em P01: inserir o movimento com a identidade na mesma linha e transação; conflito UNIQUE → reler o existente; fingerprint igual → responder o mesmo movimento; diferente → 422 sem efeito. Autorização é verificada também na repetição. Retenção: sem expiração neste recorte. |
| H11 | Segurança | JWT bearer com emissor local de desenvolvimento, chave simétrica fornecida por variável de ambiente ou user-secrets, nunca em arquivo versionado. Token carrega o escopo de cliente. Toda rota exige token e autorização por recurso. |
| H12 | Arquitetura | Uma API ASP.NET Core (Minimal APIs), EF Core com Npgsql, migrations, posição calculada por agregação no banco. Sem broker, Outbox, cache, Aspire ou microsserviços. |
| H13 | Stack | .NET 10 com SDK fixado em `global.json`. PostgreSQL 17 em container Docker, porta publicada no host em `127.0.0.1:15432`. xUnit no runner **VSTest**. |
| H14 | Estados | Use sempre: proposto; implementado; executado e aprovado; reprovado; bloqueado/não verificado. |

## Contrato financeiro e temporal

Cada movimento tem um tipo (crédito ou débito) e uma magnitude positiva em BRL com no máximo duas casas decimais; valores com mais casas são recusados em vez de arredondados (H3). Como o sistema só registra fatos já ocorridos, um débito nunca é barrado por falta de saldo e o saldo pode ficar negativo (H1).

Cada movimento carrega dois instantes: `effectiveAt`, informado pelo cliente e que define em qual posição histórica o movimento conta, e `registeredAt`, atribuído pelo servidor (H4). Um movimento não pode ser efetivo antes da abertura da conta nem mais de cinco minutos no futuro (H5). Movimentos com data efetiva passada podem chegar depois, e a posição histórica passa a refletir tudo o que se sabe no momento da consulta (H9). A consulta de posição em `asOf` soma os movimentos com `effectiveAt <= asOf`, e `asOf` não pode estar mais de cinco minutos no futuro (H4, H8).

Toda conta nasce com saldo zero em `openedAt`. Consultar uma conta antes da abertura é um erro (`historico_insuficiente`), nunca um saldo zero (H6). Na consulta por cliente, entram apenas as contas já abertas em `asOf`; as demais são listadas como excluídas (H7). Exemplo: um cliente tem a conta A aberta em 01/09 e a conta B aberta em 01/10. Uma consulta em 15/09 soma somente a conta A e informa B como excluída. Se nenhuma conta estivesse aberta em `asOf`, a resposta seria `historico_insuficiente`.

## Fora de escopo

- transferências entre contas;
- juros;
- câmbio e multimoeda;
- autorização de limite;
- partidas dobradas;
- mensageria;
- event sourcing;
- CQRS distribuído;
- front-end;
- IAM corporativo;
- transferência de titularidade;
- expiração de chave de idempotência.

## Pontos de revisão

- **H1 (sistema de registro):** mudaria se o banco exigisse que o próprio sistema recusasse débitos acima de um limite ou do saldo disponível; isso transformaria o registro em autorização e exigiria controle de concorrência sobre o agregado.
- **Posição materializada:** seria introduzida se a agregação no banco deixasse de atender a latência esperada com o volume real de movimentos por conta, ou se leituras passassem a dominar a carga.
- **Consulta por corte de conhecimento:** seria introduzida se auditoria ou conciliação precisasse reproduzir "o que o sistema sabia em determinado instante" (hoje excluído por H9). Exigiria modelar um corte de conhecimento com semântica de confirmação/visibilidade e completude. `registeredAt` pode compor esse desenho, mas não é automaticamente tempo de commit nem prova suficiente da história conhecida: um timestamp atribuído antes do commit pode anteceder a visibilidade do dado, e no PostgreSQL `now()` é o início da transação.

## Decisões de implementação do ciclo P01

Registradas aqui porque afetam o contrato observável; nenhuma altera H1 a H14.

- **Gramática temporal de entrada (decisão de governança, P01fix):** validação lexical em `InstantParser` (Domain), com `TryParseExact`, lista fechada de formatos e cultura invariante; nenhum `TryParse` genérico no caminho de entrada. `effectiveAt` (POST) aceita só `yyyy-MM-ddTHH:mm:ss` seguido de `Z` ou `±HH:mm`, com até 7 dígitos fracionários opcionais; rejeita (422 `validation_error`) texto sem offset, só data, só hora, formato regional, texto livre e número JSON. `asOf` (GET) aceita o mesmo **mais** `yyyy-MM-ddTHH:mm:ss[.fff...]` sem offset, interpretado como UTC (compatibilidade documentada em P01); rejeita só data, só hora, regional e texto. O corpo transporta `effectiveAt` como texto e só o converte depois de validado, porque o desserializador converteria texto sem offset no fuso da máquina.
- **Instantes na resposta:** `effectiveAt`, `registeredAt`, `asOf` e `openedAt` são devolvidos em UTC. O offset recebido do cliente não é preservado: o banco guarda `timestamptz`, e a normalização para UTC acontece na fábrica `Movement.Create` (fronteira do domínio) e na leitura de `asOf`. Um `+` em query string vira espaço no HTTP; use `Z`, offset negativo ou `%2B`.
- **Corpo ilegível:** JSON malformado ou de tipo errado é convertido pelo tratador de exceções em 422 `validation_error`, sem detalhes internos. Campos ausentes ou semanticamente inválidos também são 422 `validation_error`, com mensagem curta em `detail`.
- **`description` vazia:** tratada como ausente (nula) antes do fingerprint e da gravação, para que `""` e ausência sejam a mesma intenção.
- **Protocolo de idempotência (H10):** inserção em transação explícita; violação `23505` **da constraint nomeada** `ux_movements_account_id_idempotency_key` (filtro por `ConstraintName`; outra violação única é falha inesperada, 500) → rollback da transação abortada, limpeza do rastreamento e releitura por (conta, chave) fora dela; fingerprint igual → 200 com o movimento existente; diferente → 422 `idempotency_key_conflict`. Linha ausente após o conflito: estado inesperado, causa não determinada (por exemplo, exclusão concorrente); respondido 409 `retry_later` sem repetir o insert, com log que leva um prefixo do hash da chave, não a chave.
- **Limite do agregado:** cada movimento cabe em `numeric(18,2)` com teto de 1.000.000.000,00 (H3). `SUM` em PostgreSQL é `numeric` sem limite prático e chega ao .NET como `decimal` (28 dígitos significativos), suficiente para a soma de qualquer volume realista de movimentos no teto. O valor agregado nunca é materializado em coluna; se um dia for, a coluna de posição precisará de precisão maior que `numeric(18,2)`.
- **Sinal:** a convenção crédito positivo / débito negativo existe em um único ponto do código C# (`MovementRules.SignedAmount`) e é espelhada em SQL (`CASE m.type WHEN 1 THEN m.amount ELSE -m.amount END`) em `PositionQueries`, porque a agregação acontece no banco (H12). Desde P02 o fragmento SQL é uma única constante (`SignedAmountSql`) usada pelas duas consultas; conta e corte continuam parâmetros posicionais (`@p0`, `@p1`), provado por teste de tradução. Ambas as formas do sinal são cobertas por teste.
- **Rota com id inválido:** `{accountId:guid}` que não é GUID não casa com a rota e devolve 404 sem corpo, coerente com não revelar existência.
- **Precisão dos instantes:** `timestamptz` resolve microssegundos e `DateTimeOffset` resolve 100 ns. `Movement.Create` trunca `effectiveAt` e `registeredAt` a microssegundos antes de persistir, para que a resposta `201` e qualquer releitura (inclusive a repetição idempotente, `200`) carreguem exatamente o mesmo valor. (Desde P02 o fingerprint usa o mesmo `effectiveAt` normalizado; ver abaixo.)

## Decisões de implementação do ciclo P02

Registradas aqui porque afetam o contrato observável ou a leitura dos testes; nenhuma altera H1 a H14.

- **Fingerprint normalizado (decisão de governança):** o fingerprint passa a aplicar `Movement.NormalizeForPersistence` (UTC, microssegundos) ao `effectiveAt`. Um corpo reconstruído a partir da resposta `201`, que devolve seis casas, replaya com `200` e o mesmo `movementId`, em vez de `422`. Movimentos de laboratório gravados antes desta mudança não são migrados: um replay com 7 dígitos contra um registro antigo seria visto como conteúdo diferente.
- **Ponto de falha controlado:** `IRegisterMovementFailurePoints` (antes do commit, depois do commit) tem em produção apenas `NoFailurePoints`, registrado como singleton. Nenhuma configuração, cabeçalho ou endpoint ativa outra implementação; os testes substituem o singleton no host de teste.
- **Filtro da constraint:** `RegisterMovementHandler.IsIdempotencyConflict` é verdadeiro só para `23505` na constraint `ux_movements_account_id_idempotency_key`; outra violação única (por exemplo `pk_movements`) segue como falha inesperada. Há teste de unidade nas duas direções.
- **Ruído de log no replay concorrente:** o caminho de replay concorrente gera registros `fail:` do EF Core (`Update[10000]`, `Database.Command[20102]`) por desenho, porque a violação `23505` é o mecanismo que protege a identidade. O protocolo não muda. Uma falha real continua visível por `Log.UnhandledException` (evento 1000).
- **Arquivos gerados:** `.editorconfig` marca `*.Designer.cs` e `*ModelSnapshot.cs` de `Migrations/` com `generated_code = true`.
- **Evoluções registradas, não feitas:** constraints de defesa em profundidade no banco (teto de valor, domínio de `type`, moeda); `TimeProvider` congelável na fixture de testes.

## Decisões de implementação do ciclo P03

Registradas aqui porque afetam o contrato observável e a operação; nenhuma altera H1 a H14.

- **Contrato sob indisponibilidade:** qualquer rota que precise do banco e não obtenha resposta no prazo responde `503` `dependency_unavailable` (ProblemDetails com `traceId`), sem corpo de posição e sem detalhes da exceção. Nunca `200` com saldo zero, nunca `201` sem commit. A repetição é do cliente, com a mesma `Idempotency-Key`; a API não faz retry automático em POST. Uma chave cuja tentativa recebeu `503` antes do commit nunca foi registrada e cria normalmente na repetição.
- **Classificação (um único lugar, `ApiExceptionHandler.IsDependencyUnavailable`):** `NpgsqlException` com `IsTransient`, `TimeoutException`, `SocketException` e `NpgsqlException` cuja `InnerException` seja uma dessas, na própria exceção ou na `InnerException` (um nível). Violações de constraint e demais falhas continuam `500 internal_error`.
- **Prazos:** `Timeout=5;Command Timeout=15` na connection string de desenvolvimento (e a recomendar em qualquer ambiente). Os testes de indisponibilidade usam `Timeout=2;Command Timeout=2` por configuração do host de teste.
- **Sondas:** `GET /health/live` responde `200 {"status":"Healthy"}` sem tocar o banco. `GET /health/ready` executa `SELECT 1` com prazo de 3 s e responde `200 {"status":"Healthy"}` ou `503 {"status":"Unhealthy"}`, sem nome de dependência, mensagem de exceção ou connection string. **Acesso anônimo, por decisão:** orquestradores precisam sondar sem credencial, e a resposta não carrega informação financeira nem de ambiente. As rotas financeiras continuam sob `RequireAuthorization`.
- **Limite de corpo:** 16 KB para `/api/v1`, em middleware (e não em `KestrelServerOptions.Limits`), para valer só no grupo financeiro e para que o host de testes exerça o mesmo código. Duas camadas: `Content-Length` acima do limite é recusado antes de qualquer leitura (`413 payload_too_large`, ProblemDetails); o limite do servidor (`IHttpMaxRequestBodySizeFeature`) cobre corpo sem `Content-Length`. O `TestServer` não aplica o limite do servidor; por isso a camada do servidor é evidenciada pela sondagem contra Kestrel real (corpo chunked de 64 KB recusado com `413`, sem corpo de resposta). Nada é persistido.
- **Logs:** logging estruturado do host. Eventos próprios: `MovementRegistered` (Information, com `movementId` e `accountId`), `DependencyUnavailable` (Warning, método, rota e `traceId`), `ReadinessFailed` (Warning, só o tipo da falha) e `UnhandledException` (Error, preservado). Nada registra corpo, `Authorization`, `Idempotency-Key` bruta, connection string ou chave; teste de integração captura os logs de um POST, uma repetição, um `422` e um `401` e procura esses valores.
- **CI:** `.github/workflows/ci.yml` (ubuntu-latest, serviço `postgres:17` com a credencial sintética do `docker-compose.yml`, restore e build com `-warnaserror`, testes com `FT_TEST_ADMIN_CONNECTION`). Nenhum segredo. **Estado em 19/10/2026: executado com sucesso na CI.** Para o commit `abcdf19` há uma execução (run `39201091893`, job `build-and-test`, `success`, 19/10/2026 04:45 UTC); segundo a governança do projeto, o workflow também executou com sucesso em `fe545e9` e `ccf7bc7`. Restore e build sem diagnósticos de warning de compilação. O workflow não executa o ensaio de carga, as sondagens manuais nem o roteiro do README; evidência e pendências de manutenção do workflow em [verificacao.md](verificacao.md).
- **Sondagem de reinício:** contra Kestrel real (`Production`) e o container do projeto, em banco descartável: crédito, parada do PostgreSQL (`503` em ~2 s nas rotas de dados, readiness `503` imediato, liveness `200`), religamento, dado anterior preservado (saldo 100,00, 1 movimento), operação nova `201`, replay da chave anterior `200` e a chave usada durante a indisponibilidade criando `201`. O registro está no checkpoint do ciclo.

## Decisões do ciclo P04

Registradas aqui porque afetam a operação e a evolução; nenhuma altera H1 a H14. Método, números e limites em [carga.md](carga.md).

- **Ensaio de carga:** ferramenta `tools/FinancialTransactions.LoadTest` e `seed_load.sql`; banco descartável `ft_load_<n>` com 100.000 movimentos pré-carregados; 32 trabalhadores em laço fechado, 10 s de aquecimento e 60 s medidos; mistura 60/5/25/10 (POST novo, POST replay, GET por conta, GET por cliente). Metas H-L1 a H-L4 são hipóteses do projeto, não requisitos do cliente; as quatro foram atendidas nas duas execuções.
- **Reconciliação:** compara identidades (conjunto de `id` e campos de cada linha) e soma assinada por conta, não só o total, porque um total correto não prova que cada movimento certo está no lugar certo.
- **Índices:** o conjunto atual (`pk_movements`, `ux_movements_account_id_idempotency_key`, `ix_movements_account_id_effective_at`) é suficiente para este recorte. O experimento negativo mostrou o plano com e sem o índice por `(account_id, effective_at)`. Nenhum índice novo foi criado e `PositionQueries` não mudou.
- **Cache e posição materializada não entram (H12).** Condição de revisão: volume de movimentos por conta ordens de grandeza acima de 250 a 343, p95 do GET de posição acima de 250 ms em carga representativa, ou leituras dominando a carga.
- **Ruído de log no replay (P2-6) em escala:** cada replay produz dois registros `fail` do EF Core (23505 esperado); sem erro técnico associado. Registrado em [carga.md](carga.md), sem mudança de código.

## Decisões do ciclo P05

Registradas aqui porque tratam da documentação pública; nenhuma altera H1 a H14, o contrato ou o código.

- **Documentação consolidada:** `README.md` reorganizado (implementado e não implementado, obtenção do código, CI, dados sintéticos e proteção, documentação, limites); novos [arquitetura.md](arquitetura.md) (componentes, fluxos, decisões e uma seção separada de evoluções não implementadas) e [verificacao.md](verificacao.md) (requisito → implementação → teste, resultados datados com a fonte de cada um, pendências e checklist de entrega).
- **Divergências README ↔ código, corrigidas no texto:** (a) o replay com "o corpo reconstruído a partir da resposta" só devolve `200` quando a requisição original não tinha `description`, porque a resposta não traz `description` e ela entra no fingerprint (o mesmo vale para a frase do ciclo P02 acima); (b) a credencial sintética do banco aparece também no README, em `docs/carga.md`, na conexão administrativa dos testes e no workflow, não só no compose e na configuração de desenvolvimento; (c) a CI deixou de ser descrita como "não executada"; (d) o exemplo de posição passou a ter a chamada às 11:30 em vez de um comentário. Nenhum defeito de código foi encontrado.
- **`docs/carga.md`:** dois ajustes editoriais (latência com timeouts e falhas de transporte; limite sobre a reconciliação), sem alteração de números.
- **Pendências de manutenção do workflow** (sonda do serviço PostgreSQL e versões das actions): ver [verificacao.md](verificacao.md).

## Cenários da matriz e aplicabilidade

Estados segundo H14. "Executado e aprovado" refere-se à execução local registrada no checkpoint do ciclo, não a CI.

| ID | Cenário | Estado | Observação |
|---|---|---|---|
| T01 | Fluxo básico | executado e aprovado | P01 |
| T02 | Dois cortes históricos, consolidação por cliente | executado e aprovado | P01 e P01fix (T02a a T02f) |
| T03 | Fronteiras, origem e entradas | executado e aprovado | P01 e P02: `effectiveAt == openedAt`, descrição 140/141, teto e teto + 0,01, chave 128/129, conta inexistente, conta vazia |
| T04 | Informação tardia | executado e aprovado | P02: +5 às 09h30 muda 130 para 135 e 110 para 115 (H9) |
| T05 | Repetição sequencial | executado e aprovado | P01 (T05, T05b) |
| T06 | Repetição simultânea | executado e aprovado | P02: 20 chamadas, 1 × `201`, 19 × `200`, um registro; T06b com 20 corpos diferentes: 1 × `201`, 19 × `422` |
| T07 | Chave reutilizada / conteúdo igual legítimo | executado e aprovado | T05b (mesma chave, conteúdo diferente) e T07b (chaves distintas, conteúdo igual: dois movimentos) |
| T08 | Operações distintas concorrentes | executado e aprovado | P02: 10 + 20 sobre 100 resulta 130; 20 × 1,00 resulta 120 |
| T09 | Regra sobre agregado | não aplicável | H1: sistema de registro, saldo negativo permitido, nenhum limite sobre o agregado. Sem política de saldo, não há regra que dois débitos concorrentes possam violar; T08 cobre a concorrência que existe. Se H1 mudar, T09 passa a valer e exige controle de concorrência sobre o agregado |
| T10 | Falha antes e depois do commit | executado e aprovado | P02: antes do commit nada persiste e a repetição cria; depois do commit a repetição devolve o mesmo movimento. Um `500` não é tomado como prova de rollback |
| T11 | Autenticação e autorização | executado e aprovado | P01 (sem token, outro cliente, inclusive na repetição) e P02 (T11c: expirado, audiência errada, sem `customer_id`, outra chave) |
| T12 | Precisão e seleção | executado e aprovado | P01 e P02: 100 × 0,01 = 1,00; contas e clientes não vazam; escala de 3 casas rejeitada |
| T13 | Indisponibilidade e recuperação | executado e aprovado | P03: banco inalcançável → `503 dependency_unavailable` em ~2 s, nunca saldo zero (T13a, T13b); readiness `503`/liveness `200` com o banco fora e readiness `200` com o banco disponível (T13c, T13d); sondas anônimas, API não (`Health_IsAnonymous_ApiIsNot`); limite de corpo; logs sem segredos. Sondagem de reinício do PostgreSQL contra Kestrel real: dado confirmado sobrevive, operações voltam, replay e chave nova se comportam como esperado |
| T14 | Correção e reconciliação | parcial: reconciliação pós-carga executada (identidades e somas) | P04: após a carga, conjunto de ids, campos por linha e soma assinada por conta conferem com o que a ferramenta registrou como aceito, nas duas execuções. Correção de movimentos continua fora de escopo (sem estorno; a posição é derivada e a identidade de cada movimento é preservada pela UNIQUE) |
| T15 | Mensageria | não aplicável | H12: sem broker nem Outbox |
| T16 | Carga e aceitação | carga executada; CI executada com sucesso em `abcdf19c073dc95505010a593126248dc2bcc79a` (run `39201091893`); aceitação em cópia limpa em P06 | P04: duas execuções de 60 s com 32 trabalhadores contra API e PostgreSQL reais, zero erro técnico, reconciliação exata (ver [carga.md](carga.md)). Compilação limpa e testes executados em P04. A CI do commit `abcdf19` executou com sucesso (ver [verificacao.md](verificacao.md)); a aceitação em cópia limpa fica para P06 |
