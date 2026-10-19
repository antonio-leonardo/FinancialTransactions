# FinancialTransactions

Sistema de registro de movimentações financeiras (créditos e débitos) em contas de clientes, com consulta da posição consolidada por conta e por cliente em um instante (`asOf`). API ASP.NET Core (Minimal APIs) sobre PostgreSQL, com EF Core, idempotência por chave e autenticação JWT local. O escopo e as hipóteses estão em [docs/escopo.md](docs/escopo.md).

## Pré-requisitos

- SDK .NET 10 (linha 10.0, a partir de 10.0.301). O `rollForward: latestFeature` em `global.json` seleciona a maior feature band instalada da linha 10.0, não inferior à declarada.
- Docker com Compose v2.
- PowerShell 7.4 ou superior (os exemplos abaixo usam PowerShell).
- Acesso ao `nuget.org` para restaurar pacotes.

## 1. Subir o banco

```powershell
docker compose up -d --wait
```

PostgreSQL 17 publicado em `127.0.0.1:15432`, banco `financialtransactions`, usuário `ft`, senha `ft_local_dev_only` (credencial sintética de laboratório, visível em `docker-compose.yml` e em `appsettings.Development.json`). `appsettings.json` não traz connection string; fora do ambiente `Development`, informe `ConnectionStrings__FinancialTransactions` por variável de ambiente ou user-secrets.

A connection string de desenvolvimento termina em `Timeout=5;Command Timeout=15` (segundos para abrir a conexão e para executar um comando). Use os mesmos parâmetros em qualquer ambiente: sem eles, uma dependência indisponível seguraria a requisição por mais tempo do que o contrato de 503 promete (ver "Indisponibilidade" abaixo).

## 2. Gerar e exportar a chave de assinatura do JWT

A API recusa iniciar sem `Auth:SigningKey` com pelo menos 32 bytes. A chave nunca fica em arquivo versionado.

```powershell
$key = [Convert]::ToBase64String([System.Security.Cryptography.RandomNumberGenerator]::GetBytes(48))
$env:FT__Auth__SigningKey = $key
```

Alternativa persistente para a máquina de desenvolvimento, fora do repositório (o projeto declara `UserSecretsId`, um identificador do cofre local, não um segredo):

```powershell
dotnet user-secrets set "Auth:SigningKey" "$key" --project src/FinancialTransactions.Api
```

User-secrets só são lidos no ambiente `Development` (perfil `http` do `dotnet run`). Fora dele, e para a ferramenta de token, use a variável de ambiente.

## 3. Criar o esquema e a massa sintética

```powershell
dotnet tool restore
dotnet ef database update --project src/FinancialTransactions.Infrastructure --startup-project src/FinancialTransactions.Api
```

A migration `InitialSchema` cria `customers`, `accounts` e `movements` e semeia dois clientes e três contas (nenhum movimento, nenhum dado pessoal):

| Entidade | Id | Dados |
|---|---|---|
| Cliente A | `11111111-1111-1111-1111-111111111111` | "Cliente A" |
| Cliente B | `22222222-2222-2222-2222-222222222222` | "Cliente B" |
| Conta A1 | `aaaaaaa1-0000-0000-0000-000000000001` | cliente A, BRL, aberta em `2026-01-01T00:00:00Z` |
| Conta A2 | `aaaaaaa2-0000-0000-0000-000000000002` | cliente A, BRL, aberta em `2026-03-01T00:00:00Z` |
| Conta B1 | `bbbbbbb1-0000-0000-0000-000000000001` | cliente B, BRL, aberta em `2026-02-01T00:00:00Z` |

Migrations não são aplicadas no startup da API.

## 4. Executar a API

```powershell
dotnet run --project src/FinancialTransactions.Api
```

Perfil `http` em `http://localhost:5100` (ambiente `Development`). A variável `FT__Auth__SigningKey` precisa estar definida na sessão que executa a API. O `dotnet run` ocupa o terminal: gere o token e faça as chamadas das seções 5 e 6 em **outro terminal**, com a mesma `FT__Auth__SigningKey` exportada nele.

## 5. Gerar um token local

```powershell
$token = dotnet run --project tools/FinancialTransactions.DevToken -- --customer 11111111-1111-1111-1111-111111111111 --minutes 60
```

A ferramenta lê a chave de `FT__Auth__SigningKey` (ou `--key`) e emite um JWT HS256 com `iss=financialtransactions-dev`, `aud=financialtransactions-api`, `sub`, `customer_id` e `exp`. Sem chave, sai com código 2. A claim `customer_id` define o escopo: contas e clientes de outro escopo respondem 404.

## 6. Exemplos

```powershell
$h = @{ Authorization = "Bearer $token"; 'Content-Type' = 'application/json' }
$a1 = 'aaaaaaa1-0000-0000-0000-000000000001'

# Crédito de 100,00 com efeito em 01/09/2026 09:00 (-03:00)
Invoke-RestMethod -Method Post -Uri "http://localhost:5100/api/v1/accounts/$a1/movements" -Headers ($h + @{ 'Idempotency-Key' = 'exemplo-credito-001' }) -Body '{ "type": "Credit", "amount": 100.00, "effectiveAt": "2026-09-01T09:00:00-03:00", "description": "depósito" }'

# Débito de 40,00 com efeito em 01/09/2026 11:00 (-03:00)
Invoke-RestMethod -Method Post -Uri "http://localhost:5100/api/v1/accounts/$a1/movements" -Headers ($h + @{ 'Idempotency-Key' = 'exemplo-debito-001' }) -Body '{ "type": "Debit", "amount": 40.00, "effectiveAt": "2026-09-01T11:00:00-03:00" }'

# Posição da conta às 10:30 (-03:00): 100,00. Às 11:30: 60,00.
Invoke-RestMethod -Uri "http://localhost:5100/api/v1/accounts/$a1/position?asOf=2026-09-01T10:30:00-03:00" -Headers $h

# Posição consolidada do cliente A em 01/10/2026
Invoke-RestMethod -Uri "http://localhost:5100/api/v1/customers/11111111-1111-1111-1111-111111111111/position?asOf=2026-10-01T00:00:00Z" -Headers $h
```

Repetir o mesmo POST com a mesma `Idempotency-Key` e o mesmo corpo (ou o corpo reconstruído a partir da resposta, cujo `effectiveAt` tem no máximo seis casas fracionárias) devolve `200` com o mesmo `movementId`; a mesma chave com corpo diferente devolve `422 idempotency_key_conflict` sem efeito.

### Contrato

Prefixo `/api/v1`; todas as rotas de negócio exigem `Authorization: Bearer <jwt>`. Erros são `ProblemDetails` com a extensão `code`.

| Rota | Entrada | Saída |
|---|---|---|
| `POST /accounts/{accountId}/movements` | header `Idempotency-Key` (1 a 128 caracteres ASCII imprimíveis); corpo `{ "type": "Credit" \| "Debit", "amount": 10.50, "effectiveAt": "2026-03-01T12:00:00-03:00", "description": "opcional, até 140" }` | `201` + `Location` + `{ movementId, accountId, type, amount, currency, effectiveAt, registeredAt }`; `200` com o mesmo corpo na repetição |
| `GET /accounts/{accountId}/movements/{movementId}` | | `200` mesmo corpo; `404` |
| `GET /accounts/{accountId}/position?asOf=` | obrigatório | `200 { accountId, asOf, currency, balance, movementCount }` |
| `GET /customers/{customerId}/position?asOf=` | obrigatório | `200 { customerId, asOf, currency, balance, accounts: [{ accountId, balance, movementCount }], excludedAccounts: [{ accountId, openedAt }] }` |

| Situação | HTTP | `code` |
|---|---|---|
| Sem token ou token inválido | 401 | padrão |
| Conta ou cliente fora do escopo do token, ou inexistente | 404 | `not_found` |
| Corpo, cabeçalho ou parâmetro inválido (inclusive `effectiveAt` sem offset e `asOf` fora da gramática abaixo) | 422 | `validation_error` |
| `effectiveAt` mais de 5 minutos no futuro | 422 | `effective_at_in_future` |
| `effectiveAt` anterior à abertura da conta | 422 | `effective_at_before_opening` |
| `Idempotency-Key` ausente ou inválida | 422 | `idempotency_key_required` |
| Mesma chave com conteúdo diferente | 422 | `idempotency_key_conflict` |
| `asOf` ausente, ilegível ou mais de 5 minutos no futuro | 422 | `validation_error` |
| `asOf` antes da abertura da conta, ou nenhuma conta do cliente aberta em `asOf` | 422 | `historico_insuficiente` |
| Conflito concorrente cuja linha ainda não ficou visível (raro) | 409 | `retry_later` |
| Corpo com mais de 16 KB | 413 | `payload_too_large` (sem `Content-Length`, o servidor recusa com 413 sem corpo) |
| Banco indisponível ou sem resposta no prazo | 503 | `dependency_unavailable` |
| Falha inesperada | 500 | `internal_error` |

### Indisponibilidade, saúde e limites

- **Indisponibilidade:** se o PostgreSQL não responde (conexão recusada, `Timeout`, `Command Timeout`), qualquer rota que precise do banco devolve `503 dependency_unavailable` em ProblemDetails, com `traceId` e sem detalhes da exceção. Nunca `200` com saldo zero e nunca `201` sem commit. A API não repete POST sozinha: repita com a **mesma** `Idempotency-Key`. Se a tentativa que recebeu `503` não chegou ao commit, a repetição cria; se chegou, devolve `200` com o mesmo movimento. Dados confirmados sobrevivem ao reinício do banco (o volume do `docker-compose.yml` é preservado).
- **Saúde (anônima, fora de `/api/v1`):** `GET /health/live` devolve `200 {"status":"Healthy"}` sempre que o processo responde, sem tocar o banco. `GET /health/ready` executa `SELECT 1` em até 3 s e devolve `200 {"status":"Healthy"}` ou `503 {"status":"Unhealthy"}`. Nenhuma das respostas traz nome de dependência, mensagem de exceção ou connection string. As rotas financeiras seguem exigindo token.
- **Limite de requisição:** corpos acima de 16 KB em `/api/v1` são recusados com `413` e nada é persistido. `Idempotency-Key` tem no máximo 128 caracteres.
- **Logs:** nunca registram corpo, `Authorization`, `Idempotency-Key` bruta, connection string ou chave. O registro de um movimento gera o evento `MovementRegistered` com o `movementId`; indisponibilidade gera `DependencyUnavailable` (Warning) com rota e `traceId`.

Instantes (gramática fechada, cultura invariante):

| Campo | Aceito | Rejeitado (422 `validation_error`) |
|---|---|---|
| `effectiveAt` (POST) | `yyyy-MM-ddTHH:mm:ss` seguido de `Z` ou `±HH:mm`, com até 7 dígitos fracionários opcionais (ex.: `2026-09-01T09:00:00-03:00`, `2026-09-01T12:00:00.123Z`) | sem offset; só data; só hora; formato regional; texto; número JSON |
| `asOf` (GET) | o mesmo que acima, **mais** `yyyy-MM-ddTHH:mm:ss[.fff]` sem offset, interpretado como UTC | só data; só hora; formato regional; texto |

As respostas devolvem todos os instantes em UTC (o offset recebido não é preservado). Em query string, um `+` precisa ser codificado como `%2B`; prefira `Z` ou offset negativo. A posição inclui movimentos com `effectiveAt <= asOf`.

## 7. Compilar e testar

```powershell
dotnet build FinancialTransactions.sln -c Release
dotnet test FinancialTransactions.sln -c Release --no-build --settings tests/vstest.runsettings
```

Os testes de integração sobem a API real (`WebApplicationFactory`) contra um banco PostgreSQL descartável por execução (`ft_test_<hex>`), criado e migrado no início e removido ao final. Eles usam por padrão a conexão administrativa `Host=127.0.0.1;Port=15432;Database=postgres;Username=ft;Password=ft_local_dev_only`; para outro servidor, defina `FT_TEST_ADMIN_CONNECTION`. A chave JWT dos testes é gerada a cada execução. Para rodar só os testes de unidade (sem banco): `dotnet test tests/FinancialTransactions.UnitTests -c Release --no-build --settings tests/vstest.runsettings`.

## Ensaio de carga

Ferramenta de console em `tools/FinancialTransactions.LoadTest` e pré-carga em `tools/FinancialTransactions.LoadTest/seed_load.sql`. O ensaio roda contra a API real e um banco descartável `ft_load_<n>` (nunca o de desenvolvimento), com 200 clientes, 400 contas e 100.000 movimentos pré-carregados, 32 trabalhadores, 10 s de aquecimento e 60 s medidos, e termina com uma reconciliação do banco contra o que a ferramenta registrou como aceito. Resultados, ambiente, planos de consulta e limites: [docs/carga.md](docs/carga.md).

Resumo do passo a passo (comandos completos em [docs/carga.md](docs/carga.md#como-reproduzir)):

```powershell
$conn = 'Host=127.0.0.1;Port=15432;Database=ft_load_1;Username=ft;Password=ft_local_dev_only;Timeout=5;Command Timeout=15'
dotnet ef database update --connection $conn --project src/FinancialTransactions.Infrastructure --startup-project src/FinancialTransactions.Api --configuration Release --no-build
Get-Content tools/FinancialTransactions.LoadTest/seed_load.sql -Raw | docker exec -i financialtransactions-postgres psql -U ft -d ft_load_1 -v ON_ERROR_STOP=1
# Subir a API em Production com FT__Auth__SigningKey e FT__ConnectionStrings__FinancialTransactions, e então:
$env:FT_LOAD_ADMIN_CONNECTION = $conn
dotnet tools/FinancialTransactions.LoadTest/bin/Release/net10.0/FinancialTransactions.LoadTest.dll --base-url http://127.0.0.1:<porta> --out ./saida-carga
```

A ferramenta usa a mesma `FT__Auth__SigningKey` da API para emitir os tokens, só aceita `--base-url` em `127.0.0.1`/`localhost` e só aceita bancos `ft_load_*`. É um ensaio de desenvolvimento: os números de `docs/carga.md` não valem para produção.

## CI

O workflow [.github/workflows/ci.yml](.github/workflows/ci.yml) está **escrito, não executado**: a primeira execução acontecerá depois da publicação do repositório. Ele roda em `ubuntu-latest` com um serviço `postgres:17` (a mesma credencial sintética do `docker-compose.yml`, sem nenhum segredo), SDK .NET 10.0.x, `dotnet msbuild -target:Restore -warnaserror`, `dotnet build -c Release --no-restore --no-incremental -warnaserror` e `dotnet test -c Release --no-build --settings tests/vstest.runsettings`, com `FT_TEST_ADMIN_CONNECTION` apontando para o serviço.

## Estrutura

- `src/FinancialTransactions.Domain`: entidades, `MovementAmount`, regras temporais e de sinal, chave de idempotência e fingerprint.
- `src/FinancialTransactions.Infrastructure`: `DbContext`, configurações snake_case, migration com seed, `PositionQueries` (agregação por `SUM`/`COUNT` no banco).
- `src/FinancialTransactions.Api`: endpoints, JWT, autorização por recurso, `ProblemDetails`, caso de uso `RegisterMovement`.
- `tools/FinancialTransactions.DevToken`: emissor local de tokens.
- `tools/FinancialTransactions.LoadTest`: ferramenta do ensaio de carga e `seed_load.sql`.
- `tests/`: unidade (xUnit) e integração (xUnit + PostgreSQL real).

## Estado atual

Implementado e verificado localmente:

- registro persistente de crédito e débito, com `effectiveAt` do cliente e `registeredAt` do servidor;
- posição por conta e por cliente em `asOf`, calculada no banco, com exclusão de contas abertas depois de `asOf`;
- idempotência por `(conta, Idempotency-Key)` com fingerprint do conteúdo, inclusive sob repetição simultânea: 20 chamadas com a mesma chave e corpo resultam em um `201`, dezenove `200` e um único registro; corpos diferentes sob a mesma chave resultam em um `201` e `422 idempotency_key_conflict` nas demais;
- operações distintas simultâneas preservadas (chaves diferentes, conteúdo igual ou não), com a posição somando todas;
- falha antes do commit não deixa registro e a repetição cria; falha depois do commit e antes da resposta deixa o registro e a repetição devolve o mesmo movimento (o ponto de falha só existe no host de testes);
- informação tardia: um movimento com `effectiveAt` no passado, registrado depois, altera a posição histórica (a posição reflete o conhecimento atual);
- autenticação JWT local e autorização por recurso com base na claim `customer_id`; tokens expirados, de outra audiência, sem `customer_id` ou assinados com outra chave recebem `401`.

A matriz de cenários e a aplicabilidade de cada um estão em [docs/escopo.md](docs/escopo.md).

- indisponibilidade do banco respondida com `503` dentro do prazo, readiness e liveness, limite de corpo de 16 KB e logs sem segredos; recuperação verificada com reinício do PostgreSQL contra a API real (dado anterior preservado, operações voltam).

- ensaio de carga local (duas execuções, 60 s medidos cada, zero erro técnico) com reconciliação exata do banco e análise dos planos das consultas de posição, em [docs/carga.md](docs/carga.md); não representa produção.

Não implementado:

- cache e posição materializada (decisão registrada com condição de revisão em [docs/carga.md](docs/carga.md));
- execução da CI (o workflow existe, mas ainda não rodou).
