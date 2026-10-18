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

## 2. Gerar e exportar a chave de assinatura do JWT

A API recusa iniciar sem `Auth:SigningKey` com pelo menos 32 bytes. A chave nunca fica em arquivo versionado.

```powershell
$key = [Convert]::ToBase64String((1..48 | ForEach-Object { Get-Random -Maximum 256 }) -as [byte[]])
$env:FT__Auth__SigningKey = $key
```

Alternativa persistente para a máquina de desenvolvimento, fora do repositório:

```powershell
dotnet user-secrets set "Auth:SigningKey" "$key" --project src/FinancialTransactions.Api
```

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

Perfil `http` em `http://localhost:5100` (ambiente `Development`). A variável `FT__Auth__SigningKey` precisa estar definida na mesma sessão.

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

Repetir o mesmo POST com a mesma `Idempotency-Key` e o mesmo corpo devolve `200` com o mesmo `movementId`; a mesma chave com corpo diferente devolve `422 idempotency_key_conflict` sem efeito.

### Contrato

Prefixo `/api/v1`; todas as rotas exigem `Authorization: Bearer <jwt>`. Erros são `ProblemDetails` com a extensão `code`.

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
| Corpo, cabeçalho ou parâmetro inválido | 422 | `validation_error` |
| `effectiveAt` mais de 5 minutos no futuro | 422 | `effective_at_in_future` |
| `effectiveAt` anterior à abertura da conta | 422 | `effective_at_before_opening` |
| `Idempotency-Key` ausente ou inválida | 422 | `idempotency_key_required` |
| Mesma chave com conteúdo diferente | 422 | `idempotency_key_conflict` |
| `asOf` ausente, ilegível ou mais de 5 minutos no futuro | 422 | `validation_error` |
| `asOf` antes da abertura da conta, ou nenhuma conta do cliente aberta em `asOf` | 422 | `historico_insuficiente` |
| Conflito concorrente cuja linha ainda não ficou visível (raro) | 409 | `retry_later` |
| Falha inesperada | 500 | `internal_error` |

Instantes: envie `effectiveAt` e `asOf` em ISO 8601 com offset ou `Z`. As respostas devolvem todos os instantes em UTC (o offset recebido não é preservado). Em query string, prefira `Z` ou `-03:00`; um `+` precisa ser codificado como `%2B`. `asOf` sem offset é interpretado como UTC. A posição inclui movimentos com `effectiveAt <= asOf`.

## 7. Compilar e testar

```powershell
dotnet build FinancialTransactions.sln -c Release
dotnet test FinancialTransactions.sln -c Release --no-build --settings tests/vstest.runsettings
```

Os testes de integração sobem a API real (`WebApplicationFactory`) contra um banco PostgreSQL descartável por execução (`ft_test_<hex>`), criado e migrado no início e removido ao final. Eles usam por padrão a conexão administrativa `Host=127.0.0.1;Port=15432;Database=postgres;Username=ft;Password=ft_local_dev_only`; para outro servidor, defina `FT_TEST_ADMIN_CONNECTION`. A chave JWT dos testes é gerada a cada execução. Para rodar só os testes de unidade (sem banco): `dotnet test tests/FinancialTransactions.UnitTests -c Release --no-build --settings tests/vstest.runsettings`.

## Estrutura

- `src/FinancialTransactions.Domain`: entidades, `MovementAmount`, regras temporais e de sinal, chave de idempotência e fingerprint.
- `src/FinancialTransactions.Infrastructure`: `DbContext`, configurações snake_case, migration com seed, `PositionQueries` (agregação por `SUM`/`COUNT` no banco).
- `src/FinancialTransactions.Api`: endpoints, JWT, autorização por recurso, `ProblemDetails`, caso de uso `RegisterMovement`.
- `tools/FinancialTransactions.DevToken`: emissor local de tokens.
- `tests/`: unidade (xUnit) e integração (xUnit + PostgreSQL real).

## Estado atual

Implementado e verificado localmente:

- registro persistente de crédito e débito, com `effectiveAt` do cliente e `registeredAt` do servidor;
- posição por conta e por cliente em `asOf`, calculada no banco, com exclusão de contas abertas depois de `asOf`;
- idempotência sequencial por `(conta, Idempotency-Key)` com fingerprint do conteúdo;
- autenticação JWT local e autorização por recurso com base na claim `customer_id`.

Não implementado:

- concorrência adversa (mesma chave em chamadas simultâneas, operações distintas concorrentes);
- health checks e readiness;
- ensaio de carga;
- CI.
