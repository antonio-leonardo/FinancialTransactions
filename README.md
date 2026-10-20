# FinancialTransactions

Sistema de registro de movimentações financeiras (créditos e débitos) em contas de clientes, com consulta da posição consolidada por conta e por cliente em um instante (`asOf`) e **consolidado diário** por conta e por cliente. São **três processos** (API de lançamentos, Worker de consolidação e API do consolidado) e **dois bancos PostgreSQL**: o registro de lançamentos não depende de nada do consolidado. ASP.NET Core (Minimal APIs), EF Core, idempotência por chave e autenticação JWT local. O escopo e as hipóteses estão em [docs/escopo.md](docs/escopo.md); os componentes e as decisões, em [docs/arquitetura.md](docs/arquitetura.md).

## O que está implementado

Implementado e verificado localmente (a correspondência com os testes e os resultados datados estão em [docs/verificacao.md](docs/verificacao.md)):

1. **Registro** persistente de crédito e débito, com `effectiveAt` do cliente e `registeredAt` do servidor.
2. **Posição** por conta e por cliente em `asOf`, calculada no banco, com exclusão de contas abertas depois de `asOf`.
3. **Idempotência** por `(conta, Idempotency-Key)` com fingerprint do conteúdo, inclusive sob repetição simultânea: 20 chamadas com a mesma chave e corpo resultam em um `201`, dezenove `200` e um único registro; corpos diferentes sob a mesma chave resultam em um `201` e `422 idempotency_key_conflict` nas demais. Operações distintas simultâneas são todas preservadas, e a posição soma todas.
4. **Falhas antes e depois do commit:** falha antes do commit não deixa registro e a repetição cria; falha depois do commit e antes da resposta deixa o registro e a repetição devolve o mesmo movimento (o ponto de falha só existe no host de testes).
5. **Informação tardia:** um movimento com `effectiveAt` no passado, registrado depois, altera a posição histórica (a posição reflete o conhecimento atual).
6. **Autenticação e autorização:** JWT local e autorização por recurso com base na claim `customer_id`; tokens expirados, de outra audiência, sem `customer_id` ou assinados com outra chave recebem `401`; conta ou cliente de outro escopo respondem `404`.
7. **Indisponibilidade e saúde:** banco indisponível respondido com `503` dentro do prazo, `/health/live` e `/health/ready`; recuperação verificada com reinício do PostgreSQL contra a API real (dado anterior preservado, operações voltam).
8. **Limite de corpo** de 16 KB em `/api/v1` (`413`).
9. **Logs da aplicação** sem segredos, corpo de requisição nem connection string.
10. **Ensaio de carga** local (duas execuções, 60 s medidos cada, zero erro técnico) com reconciliação do banco e análise dos planos das consultas de posição, em [docs/carga.md](docs/carga.md); não representa produção. É do ciclo P04 (commit `4463e38`), **anterior** à Outbox e aos processos do consolidado: não mede o consolidado nem o POST com a segunda escrita.

Consolidado diário (ciclo P07; hipóteses do projeto, não do contratante, em [docs/escopo.md](docs/escopo.md) H15):

11. **Independência:** cada movimento grava, **na mesma transação**, um evento numa Outbox da origem. Worker parado, banco de consolidação fora do ar ou API do consolidado parada **não impedem** registrar movimentos, consultar a posição por instante nem a readiness da API de lançamentos (cenários C1 a C5 e C13).
12. **Consolidado por dia de negócio** (`America/Sao_Paulo`): créditos, débitos, líquido, contagem e fechamento acumulado por conta e por cliente, lidos de uma projeção própria que um Worker atualiza de forma idempotente (marcador e soma na mesma transação; reentrega não soma de novo).
13. **A resposta nunca afirma completude que não comprovou:** `freshness` (`complete`, `stale` ou `unknown`), `unresolvedEvents`, `failedEvents` e `observedAt` descrevem o instante em que a origem foi lida (ver "Consolidado diário" abaixo).
14. **Recuperação sem intervenção:** falhas transitórias e de ambiente não perdem nem duplicam eventos e entram em *backoff*; só defeito do próprio evento leva à quarentena (cinco tentativas).
15. **Carga inicial (bootstrap) com a escrita ativa:** conjunto finito por identidade, capturado numa visão consistente, aplicado e reconciliado nos dois bancos antes de a API do consolidado responder.

O estado da CI está na seção [CI](#ci). A matriz de cenários e a aplicabilidade de cada um estão em [docs/escopo.md](docs/escopo.md); o estado das adequações sugeridas, em [docs/verificacao.md](docs/verificacao.md).

## O que não está implementado

- cache e posição materializada (decisão registrada com condição de revisão em [docs/carga.md](docs/carga.md));
- correção ou estorno de movimentos;
- abertura de contas e clientes por API (os cadastros vêm da migration);
- broker de mensagens e publicação para consumidores externos (a Outbox serve só à consolidação interna; broker é alternativa documentada em [docs/arquitetura.md](docs/arquitetura.md), ADR-10);
- **ainda pendente (parte C do ciclo P08):** o ensaio dos 50 req/s com no máximo 5% de perda do consolidado. Os 50 req/s e os 5% são **requisito obrigatório** das orientações complementares; ainda não existe medição que o demonstre;
- **reconstrução online contínua, troca sem indisponibilidade das consultas e retorno transparente ao banco antigo:** a reconstrução em banco paralelo (parte B) exige consumidores e APIs conhecidos parados e não oferece rollback (ver [docs/recuperacao.md](docs/recuperacao.md));
- **purge da Outbox:** a retenção é só política (conservar) e medição do crescimento; nada é apagado automaticamente (ver [docs/arquitetura.md](docs/arquitetura.md), ADR-14);
- arquitetura de alta disponibilidade (pontos de falha, recuperação, escala horizontal).

Propostas de evolução ficam separadas do que está implementado, em [docs/arquitetura.md](docs/arquitetura.md).

## Pré-requisitos

- Git.
- SDK .NET 10 (linha 10.0, a partir de 10.0.301). O `rollForward: latestFeature` em `global.json` seleciona a maior feature band instalada da linha 10.0, não inferior à declarada.
- Docker com Compose v2.
- PowerShell 7.4 ou superior (os exemplos abaixo usam PowerShell).
- Acesso ao `nuget.org` para restaurar pacotes.

Nenhum arquivo pessoal ou externo ao repositório é necessário para executar o projeto.

## 1. Obter o código

```powershell
git clone https://github.com/antonio-leonardo/FinancialTransactions.git
cd FinancialTransactions
```

Os comandos seguintes são executados na raiz do repositório clonado.

## 2. Subir os bancos

```powershell
docker compose up -d --wait
```

Dois PostgreSQL 17, instâncias separadas de propósito (a queda de um não derruba o outro):

| Serviço | Container | Porta no host | Banco | Papel |
|---|---|---|---|---|
| `postgres` | `financialtransactions-postgres` | `127.0.0.1:15432` | `financialtransactions` | **origem**: movimentos, contas e a Outbox |
| `consolidation-postgres` | `financialtransactions-consolidation-postgres` | `127.0.0.1:15433` | `financialtransactions_consolidation` | **projeção** do consolidado diário |

Usuário `ft`, senha `ft_local_dev_only` nos dois (credencial sintética de laboratório, visível em `docker-compose.yml` e nos `appsettings.Development.json`). `appsettings.json` não traz connection string; fora do ambiente `Development`, informe por variável de ambiente (com o prefixo `FT__`) `FT__ConnectionStrings__FinancialTransactions` (origem) e, nos processos do consolidado, também `FT__ConnectionStrings__Consolidation` (projeção). Os processos do consolidado recusam iniciar se uma das duas estiver ausente, inválida ou se forem iguais.

A connection string de desenvolvimento termina em `Timeout=5;Command Timeout=15` (segundos para abrir a conexão e para executar um comando). Use os mesmos parâmetros em qualquer ambiente: sem eles, uma dependência indisponível seguraria a requisição por mais tempo do que o contrato de 503 promete (ver "Indisponibilidade" abaixo).

## 3. Gerar e exportar a chave de assinatura do JWT

As duas APIs recusam iniciar sem `Auth:SigningKey` com pelo menos 32 bytes (a **mesma** chave nas duas, para que um token valha em ambas). A chave nunca fica em arquivo versionado.

```powershell
$key = [Convert]::ToBase64String([System.Security.Cryptography.RandomNumberGenerator]::GetBytes(48))
$env:FT__Auth__SigningKey = $key
```

Alternativa persistente para a máquina de desenvolvimento, fora do repositório (o projeto declara `UserSecretsId`, um identificador do cofre local, não um segredo):

```powershell
dotnet user-secrets set "Auth:SigningKey" "$key" --project src/FinancialTransactions.Api
```

User-secrets só são lidos no ambiente `Development` (perfil `http` do `dotnet run`). Fora dele, e para a ferramenta de token, use a variável de ambiente.

## 4. Criar o esquema e a massa sintética

```powershell
dotnet tool restore
# Origem: InitialSchema (com o seed) e AddConsolidationOutbox (Outbox e registro de controle da captura)
dotnet ef database update --context FinancialTransactionsDbContext --project src/FinancialTransactions.Infrastructure --startup-project src/FinancialTransactions.Api
# Projeção: ConsolidationSchema
dotnet ef database update --context ConsolidationDbContext --project src/FinancialTransactions.Consolidation --startup-project src/FinancialTransactions.Consolidation.Worker
```

A migration `InitialSchema` cria `customers`, `accounts` e `movements` e semeia dois clientes e três contas (nenhum movimento, nenhum dado pessoal):

| Entidade | Id | Dados |
|---|---|---|
| Cliente A | `11111111-1111-1111-1111-111111111111` | "Cliente A" |
| Cliente B | `22222222-2222-2222-2222-222222222222` | "Cliente B" |
| Conta A1 | `aaaaaaa1-0000-0000-0000-000000000001` | cliente A, BRL, aberta em `2026-01-01T00:00:00Z` |
| Conta A2 | `aaaaaaa2-0000-0000-0000-000000000002` | cliente A, BRL, aberta em `2026-03-01T00:00:00Z` |
| Conta B1 | `bbbbbbb1-0000-0000-0000-000000000001` | cliente B, BRL, aberta em `2026-02-01T00:00:00Z` |

`AddConsolidationOutbox` acrescenta `consolidation_outbox` e `consolidation_bootstrap_capture` à origem; `ConsolidationSchema` cria `daily_account_consolidation`, `processed_events` e `consolidation_bootstrap` na projeção. Migrations não são aplicadas no startup de nenhum processo.

## 5. Executar os processos

São três processos, cada um num terminal. A variável `FT__Auth__SigningKey` precisa estar definida em cada sessão que executa uma API. O perfil `http` de cada projeto usa o ambiente `Development` (connection strings de desenvolvimento em `appsettings.Development.json`).

**Ordem.** A API de lançamentos com a Outbox sobe **antes** do coordenador do bootstrap (não pode haver outro escritor sem Outbox). Depois do coordenador, suba o Worker e, por último, a API do consolidado.

```powershell
# Terminal 1 - API de lançamentos, http://localhost:5100
dotnet run --project src/FinancialTransactions.Api

# Terminal 2 - coordenador do bootstrap (termina com código de saída; ver abaixo)
dotnet run --project src/FinancialTransactions.Consolidation.Worker -- --backfill

# Terminal 2 (depois do coordenador) - Worker em modo serviço, http://127.0.0.1:5120
dotnet run --project src/FinancialTransactions.Consolidation.Worker

# Terminal 3 - API do consolidado, http://127.0.0.1:5110
dotnet run --project src/FinancialTransactions.Consolidation.Api
```

`dotnet run` ocupa o terminal: gere o token e faça as chamadas das seções 6 e 7 em **outro terminal**, com a mesma `FT__Auth__SigningKey` exportada nele.

**Coordenador (`--backfill`).** Captura o conjunto inicial de movimentos (os que já existiam, mais os eventos online anteriores), registra a captura na origem e ativa o destino. Nunca consome eventos e não espera pelo consumo; pode ser reexecutado sem risco. Códigos de saída: `0` ativação feita, ou nada a fazer (conjunto "em andamento" ou "concluído"); `2` origem ou projeção inalcançável, ou configuração inválida; `3` outro coordenador em execução; `4` descritores divergentes ou projeção incoerente (nada foi alterado); `5` resultado do commit da captura desconhecido (a reexecução decide pelo registro de controle).

**Worker (serviço).** Pode haver **uma ou mais instâncias** sobre o mesmo par de bancos (ver "Duas instâncias do Worker" abaixo). Sem bootstrap ativado ele **espera sem consumir** (`/health/ready` responde `503 {"status":"Degraded"}`); em `loading` aplica só o conjunto inicial e o reconcilia contra a origem; em `live` aplica a fila inteira. A fase só avança (`loading` para `live`), nunca volta. A API de lançamentos nunca é pausada nem afetada por nenhum desses estados.

#### Duas instâncias do Worker

Cada instância tem uma identidade opaca gerada na inicialização (`w-` e 12 hexadecimais) e reserva **um evento por vez** na Outbox da origem: a reserva é gravada (dono, token da aquisição e vencimento de **45 s**, no relógio do banco de origem) antes de o evento tocar a projeção, e a confirmação só vale com o token e a reserva ainda válida. Uma tentativa tem orçamento de 30 s; a reserva não é renovada. Se um Worker morrer, outro assume o evento depois do vencimento; a soma no destino continua protegida pelo marcador `processed_events`, que a reserva **não** substitui. Para subir uma segunda instância, use outra porta:

```powershell
dotnet run --project src/FinancialTransactions.Consolidation.Worker -- --urls http://127.0.0.1:5121
```

`GET /diagnostics/instance` (somente leitura, sem payload, token nem connection string) devolve a identidade e os contadores da instância: reservas adquiridas, aplicações novas, duplicatas evitadas, confirmações sem resultado, perdas de posse, falhas por classe e quarentenas. Entregas e duplicatas evitadas **não** são movimentos financeiros distintos.

#### Comandos administrativos do Worker

São modos do mesmo executável, sem Kestrel e sem endpoint HTTP de administração. As connection strings vêm do ambiente (`FT__ConnectionStrings__FinancialTransactions` e `FT__ConnectionStrings__Consolidation`); a saída é JSON em stdout.

```powershell
# Estado operacional, somente leitura: pendentes (inclui os reservados), reservados válidos, expirados elegíveis, quarentena,
# idade do mais antigo, fase da projeção, tamanhos da Outbox e de processed_events. Dependência inalcançável = "unknown", nunca zero.
dotnet run --project src/FinancialTransactions.Consolidation.Worker -- --status

# Eventos em quarentena (sem payload)
dotnet run --project src/FinancialTransactions.Consolidation.Worker -- --list-quarantine --limit 50

# Reabrir eventos em quarentena. Dry-run por padrão; aplicar exige a lista finita, o motivo (lista fechada), a confirmação do banco de origem e o digest do plano.
dotnet run --project src/FinancialTransactions.Consolidation.Worker -- --requeue --operation op-2026-10-19-a --event <guid> --reason cause_corrected --confirm-origin-database <nome-do-banco>
dotnet run --project src/FinancialTransactions.Consolidation.Worker -- --requeue --operation op-2026-10-19-a --event <guid> --reason cause_corrected --confirm-origin-database <nome-do-banco> --apply --plan-digest <digest do dry-run>
```

Códigos de saída: `0` concluído (ou plano elegível, ou repetição de uma operação já aplicada); `2` banco inalcançável ou configuração inválida; `3` argumentos inválidos ou alvo não confirmado (recusado antes de alterar dados); `4` seleção incompatível com o plano, evento fora da quarentena, ou o mesmo identificador de operação com outra seleção; `5` resultado do commit desconhecido (repetir com o mesmo identificador decide pelo registro). A reabertura só altera eventos em quarentena: zera o contador corrente de tentativas depois de gravar, na mesma transação, a trilha (`consolidation_outbox_admin_log`) com o estado anterior; não apaga `processed_events`, não cria identificadores, não altera movimentos nem valores e **não edita payload**: se a causa não foi corrigida, o evento volta à quarentena. Os motivos aceitos são `cause_corrected`, `dependency_restored` e `false_positive_reviewed`.

#### Verificação financeira e reconstrução

```powershell
# Compara a projeção com os movimentos imutáveis da origem (somente leitura; 0 coerente, 4 divergente, 2 inalcançável)
dotnet run --project src/FinancialTransactions.Consolidation.Worker -- --verify --mode live          # ou convergence | rebuild

# Reconstrução em BANCO NOVO paralelo, em estágios retomáveis (dry-run por padrão; --apply altera dados)
dotnet run --project src/FinancialTransactions.Consolidation.Worker -- --rebuild --stage plan --operation op-2026-10-20 --confirm-target-database <banco-novo>
```

A string `Consolidation` do ambiente deve apontar para o **banco novo**, migrado e vazio. A reconstrução exige os Workers e APIs do consolidado parados, copia o conjunto finito de movimentos, reaplica-o de forma idempotente, reconcilia o mesmo conjunto e só promove sem consumidores conectados. Não é reconstrução online contínua nem tem retorno transparente. Todos os estágios, códigos de saída e limites estão em [docs/recuperacao.md](docs/recuperacao.md). O digest do plano inclui a versão da linha na origem: se o evento passou de novo pela quarentena depois do dry-run, o plano antigo é recusado (saída `4`) e é preciso gerar outro. Os comandos que alteram a operação são mutuamente exclusivos por destino (recusa imediata se outro está em andamento); um destino cuja promoção já foi registrada na origem não pode ser cancelado e é retomado repetindo `promote`; a promoção exige que o banco anterior aceite conexão no servidor configurado (a recuperação de um servidor definitivamente perdido não está demonstrada). Origem configurada diferente da registrada na operação (mesmo acessível e migrada) é recusada antes de qualquer decisão.

**API do consolidado.** Responde `503 consolidation_not_ready` até a fase chegar a `live`.

## 6. Gerar um token local

```powershell
$token = dotnet run --project tools/FinancialTransactions.DevToken -- --customer 11111111-1111-1111-1111-111111111111 --minutes 60
```

A ferramenta lê a chave de `FT__Auth__SigningKey` (ou `--key`) e emite um JWT HS256 com `iss=financialtransactions-dev`, `aud=financialtransactions-api`, `sub`, `customer_id` e `exp`. Sem chave, sai com código 2. A claim `customer_id` define o escopo: contas e clientes de outro escopo respondem 404. O mesmo token vale nas duas APIs (mesma chave, mesmo emissor e audiência).

## 7. Exemplos

```powershell
$h = @{ Authorization = "Bearer $token"; 'Content-Type' = 'application/json' }
$a1 = 'aaaaaaa1-0000-0000-0000-000000000001'

# Crédito de 100,00 com efeito em 01/09/2026 09:00 (-03:00)
Invoke-RestMethod -Method Post -Uri "http://localhost:5100/api/v1/accounts/$a1/movements" -Headers ($h + @{ 'Idempotency-Key' = 'exemplo-credito-001' }) -Body '{ "type": "Credit", "amount": 100.00, "effectiveAt": "2026-09-01T09:00:00-03:00", "description": "depósito" }'

# Débito de 40,00 com efeito em 01/09/2026 11:00 (-03:00)
Invoke-RestMethod -Method Post -Uri "http://localhost:5100/api/v1/accounts/$a1/movements" -Headers ($h + @{ 'Idempotency-Key' = 'exemplo-debito-001' }) -Body '{ "type": "Debit", "amount": 40.00, "effectiveAt": "2026-09-01T11:00:00-03:00" }'

# Posição da conta às 10:30 (-03:00): saldo 100,00 e 1 movimento
Invoke-RestMethod -Uri "http://localhost:5100/api/v1/accounts/$a1/position?asOf=2026-09-01T10:30:00-03:00" -Headers $h

# Posição da conta às 11:30 (-03:00): saldo 60,00 e 2 movimentos
Invoke-RestMethod -Uri "http://localhost:5100/api/v1/accounts/$a1/position?asOf=2026-09-01T11:30:00-03:00" -Headers $h

# Posição consolidada do cliente A em 01/10/2026: saldo 60,00, contas A1 e A2, nenhuma conta excluída
Invoke-RestMethod -Uri "http://localhost:5100/api/v1/customers/11111111-1111-1111-1111-111111111111/position?asOf=2026-10-01T00:00:00Z" -Headers $h
```

Repetição e conflito de idempotência (`-SkipHttpErrorCheck` permite ver o corpo do `422`; sem ele, `Invoke-RestMethod` lança exceção):

```powershell
# Repetição do primeiro POST (mesma chave, mesmo corpo): 200 com o mesmo movementId
Invoke-RestMethod -Method Post -Uri "http://localhost:5100/api/v1/accounts/$a1/movements" -Headers ($h + @{ 'Idempotency-Key' = 'exemplo-credito-001' }) -Body '{ "type": "Credit", "amount": 100.00, "effectiveAt": "2026-09-01T09:00:00-03:00", "description": "depósito" }'

# Mesma chave com valor diferente: 422 idempotency_key_conflict, sem efeito
Invoke-RestMethod -Method Post -SkipHttpErrorCheck -Uri "http://localhost:5100/api/v1/accounts/$a1/movements" -Headers ($h + @{ 'Idempotency-Key' = 'exemplo-credito-001' }) -Body '{ "type": "Credit", "amount": 101.00, "effectiveAt": "2026-09-01T09:00:00-03:00", "description": "depósito" }'
```

Para repetir um POST com a mesma `Idempotency-Key`, reenvie os campos da requisição original, **inclusive `description`**. O instante pode ser enviado na forma UTC com até seis casas fracionárias devolvida pela resposta. A resposta sozinha não permite reconstruir uma requisição que tinha descrição, porque a resposta não traz `description` e ela entra no fingerprint. Repetir com a mesma chave e o mesmo conteúdo devolve `200` com o mesmo `movementId`; a mesma chave com conteúdo diferente devolve `422 idempotency_key_conflict` sem efeito.

### Consolidado diário

Depois de o Worker aplicar os eventos, o consolidado do dia de negócio (`America/Sao_Paulo`) fica disponível na **API do consolidado** (porta 5110, mesmo token):

```powershell
$c = 'http://127.0.0.1:5110'

# Consolidado da conta A1 em 01/09/2026: créditos, débitos, líquido, contagem e fechamento acumulado até o dia
Invoke-RestMethod -Uri "$c/api/v1/accounts/$a1/consolidation/daily?day=2026-09-01" -Headers $h

# Consolidado do cliente A no mesmo dia (soma das contas incluídas; accounts[] e excludedAccounts[])
Invoke-RestMethod -Uri "$c/api/v1/customers/11111111-1111-1111-1111-111111111111/consolidation/daily?day=2026-09-01" -Headers $h
```

Resposta real da conta A1 no dia 2026-09-01, capturada na sondagem do ciclo P07 contra os processos reais (depois de o Worker aplicar, nesse banco descartável, um crédito de 10,00 e um débito de 4,00 daquele dia; sem movimentos anteriores):

```json
{
  "accountId": "aaaaaaa1-0000-0000-0000-000000000001",
  "day": "2026-09-01",
  "businessTimeZone": "America/Sao_Paulo",
  "dayStatus": "closed",
  "currency": "BRL",
  "credits": 10.00,
  "debits": 4.00,
  "net": 6.00,
  "movementCount": 2,
  "closingBalance": 6.00,
  "maxAppliedSequence": 2,
  "observedAt": "2026-10-19T19:27:02.105834+00:00",
  "unresolvedEvents": 0,
  "failedEvents": 0,
  "stale": false,
  "freshness": "complete"
}
```

(`observedAt` e `maxAppliedSequence` dependem do momento e do histórico do seu banco. A semântica de cada campo está abaixo.)

**O que cada campo afirma.** `credits`, `debits`, `net` e `movementCount` são **do dia**; `closingBalance` é a soma do `net` de todos os dias até o dia consultado. `dayStatus` é `open` para o dia corrente e `closed` para os anteriores (fechado no calendário: ainda pode receber informação tardia, H9). Uma informação tardia altera a linha do dia do `effectiveAt` e, por derivação, o `closingBalance` dos dias seguintes; as linhas dos dias seguintes não mudam. Contas abertas depois do fim do dia ficam em `excludedAccounts` (rota do cliente) ou dão `422 historico_insuficiente` (rota da conta).

**Estado de atualização (conservador e datado).** A API lê primeiro a origem e só depois abre a consulta à projeção:

- `observedAt`: o instante, na origem, em que o servidor recebeu a consulta que conta os eventos (início da observação; o snapshot da contagem é igual ou posterior). Não é tempo de commit.
- `unresolvedEvents`: eventos das contas do recorte, **de qualquer dia**, ainda não confirmados (pendentes) ou em quarentena. `failedEvents`: os em quarentena.
- `freshness: "complete"` afirma apenas que, **na visão da consulta iniciada em `observedAt`**, não foram observados eventos não confirmados para o recorte; a projeção foi lida **depois** disso. Não afirma simultaneidade exata entre o relógio, o snapshot da contagem e a resposta, e lançamentos posteriores podem não constar. É sempre `stale` com quarentena no recorte.
- `freshness: "stale"` (`stale: true`): havia eventos não confirmados (conservador: um evento já aplicado e ainda não confirmado conta como não resolvido).
- `freshness: "unknown"`: a leitura da origem falhou depois da autorização e a projeção ainda pôde ser lida; `unresolvedEvents`, `failedEvents`, `stale` e `observedAt` vêm nulos.
- `maxAppliedSequence` é o **máximo observado** da sequência aplicada nas contas do recorte, não uma fronteira de completude.

### Contrato

Prefixo `/api/v1`; todas as rotas de negócio exigem `Authorization: Bearer <jwt>`. Erros são `ProblemDetails` com a extensão `code`.

| Rota | Entrada | Saída |
|---|---|---|
| `POST /accounts/{accountId}/movements` | header `Idempotency-Key` (1 a 128 caracteres ASCII imprimíveis); corpo `{ "type": "Credit" \| "Debit", "amount": 10.50, "effectiveAt": "2026-03-01T12:00:00-03:00", "description": "opcional, até 140" }` | `201` + `Location` + `{ movementId, accountId, type, amount, currency, effectiveAt, registeredAt }`; `200` com o mesmo corpo na repetição |
| `GET /accounts/{accountId}/movements/{movementId}` | | `200` mesmo corpo; `404` |
| `GET /accounts/{accountId}/position?asOf=` | obrigatório | `200 { accountId, asOf, currency, balance, movementCount }` |
| `GET /customers/{customerId}/position?asOf=` | obrigatório | `200 { customerId, asOf, currency, balance, accounts: [{ accountId, balance, movementCount }], excludedAccounts: [{ accountId, openedAt }] }` |
| `GET /accounts/{accountId}/consolidation/daily?day=` (API do consolidado, :5110) | `day` obrigatório, `YYYY-MM-DD` estrito; não pode ser futuro | `200 { accountId, day, businessTimeZone, dayStatus, currency, credits, debits, net, movementCount, closingBalance, maxAppliedSequence, observedAt, unresolvedEvents, failedEvents, stale, freshness }` |
| `GET /customers/{customerId}/consolidation/daily?day=` (API do consolidado, :5110) | idem | os mesmos campos somados sobre as contas incluídas, mais `accounts: [{ accountId, credits, debits, net, movementCount, closingBalance, maxAppliedSequence }]` e `excludedAccounts: [{ accountId, openedAt }]` |

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
| Consolidado: `day` fora de `YYYY-MM-DD`, ausente ou futuro | 422 | `validation_error` |
| Consolidado: conta aberta depois do fim do dia, ou nenhuma conta do cliente incluída | 422 | `historico_insuficiente` |
| Consolidado: fase do bootstrap ainda não é `live` | 503 | `consolidation_not_ready` |
| Conflito concorrente cuja linha ainda não ficou visível (raro) | 409 | `retry_later` |
| Corpo com mais de 16 KB | 413 | `payload_too_large` (sem `Content-Length`, o servidor recusa com 413 sem corpo) |
| Banco indisponível ou sem resposta no prazo | 503 | `dependency_unavailable` |
| Falha inesperada | 500 | `internal_error` |

### Indisponibilidade, saúde e limites

- **Indisponibilidade:** se o PostgreSQL não responde (conexão recusada, `Timeout`, `Command Timeout`), qualquer rota que precise do banco devolve `503 dependency_unavailable` em ProblemDetails, com `traceId` e sem detalhes da exceção. Nunca `200` com saldo zero e nunca `201` sem commit. A API não repete POST sozinha: repita com a **mesma** `Idempotency-Key`. Se a tentativa que recebeu `503` não chegou ao commit, a repetição cria; se chegou, devolve `200` com o mesmo movimento. Dados confirmados sobrevivem ao reinício do banco (o volume do `docker-compose.yml` é preservado).
- **Saúde (anônima, fora de `/api/v1`):** `GET /health/live` devolve `200 {"status":"Healthy"}` sempre que o processo responde, sem tocar o banco. `GET /health/ready` executa `SELECT 1` em até 3 s e devolve `200 {"status":"Healthy"}` ou `503 {"status":"Unhealthy"}`. Nenhuma das respostas traz nome de dependência, mensagem de exceção ou connection string. As rotas financeiras seguem exigindo token.
- **Saúde do Worker e da API do consolidado (anônima):** `/health/live` responde `200 {"status":"Healthy"}` sempre que o processo responde. `/health/ready` do Worker devolve `200 {"status":"Healthy"}`, `503 {"status":"Degraded"}` (aguardando bootstrap, registro de controle divergente, reconciliação falhou ou última falha de ambiente ainda não desarmada) ou `503 {"status":"Unhealthy"}` (`SELECT 1` falhou na origem ou na projeção, prazo de 3 s). `/health/ready` da API do consolidado é `503 Unhealthy` enquanto a fase não é `live` ou a origem ou a projeção não respondem. A readiness da API de lançamentos **não** olha nenhum deles.
- **Indisponibilidade do consolidado:** se a projeção não responde, a API do consolidado devolve `503 dependency_unavailable` (nunca saldo zero ou substituto), com orçamento de 1,5 s por requisição e `Timeout=1;Command Timeout=1` nas suas conexões. A meta de resposta de falha em até 2 s é **hipótese do projeto**, exercitada para o modo conexão recusada; o modo "aceita e não responde" não foi exercitado. O Worker mantém `Timeout=5;Command Timeout=15`.
- **Limite de requisição:** corpos acima de 16 KB em `/api/v1` são recusados com `413` e nada é persistido. `Idempotency-Key` tem no máximo 128 caracteres.
- **Logs da aplicação:** nunca registram corpo, `Authorization`, `Idempotency-Key` bruta, connection string ou chave. O registro de um movimento gera o evento `MovementRegistered` com o `movementId`; indisponibilidade gera `DependencyUnavailable` (Warning) com rota e `traceId`.

Instantes (gramática fechada, cultura invariante):

| Campo | Aceito | Rejeitado (422 `validation_error`) |
|---|---|---|
| `effectiveAt` (POST) | `yyyy-MM-ddTHH:mm:ss` seguido de `Z` ou `±HH:mm`, com até 7 dígitos fracionários opcionais (ex.: `2026-09-01T09:00:00-03:00`, `2026-09-01T12:00:00.123Z`) | sem offset; só data; só hora; formato regional; texto; número JSON |
| `asOf` (GET) | o mesmo que acima, **mais** `yyyy-MM-ddTHH:mm:ss[.fff]` sem offset, interpretado como UTC | só data; só hora; formato regional; texto |

As respostas devolvem todos os instantes em UTC (o offset recebido não é preservado). Em query string, um `+` precisa ser codificado como `%2B`; prefira `Z` ou offset negativo. A posição inclui movimentos com `effectiveAt <= asOf`.

## 8. Compilar e testar

```powershell
dotnet build FinancialTransactions.sln -c Release
dotnet test FinancialTransactions.sln -c Release --no-build --settings tests/vstest.runsettings
```

Os testes de integração sobem a API de lançamentos real (`WebApplicationFactory`), o Worker e a API do consolidado (hosts reais, Kestrel em porta livre, no processo de teste) contra **dois** bancos PostgreSQL descartáveis por execução: `ft_test_<hex>` na origem e `ft_cons_test_<hex>` na consolidação, criados e migrados no início e removidos ao final. Os subcasos do bootstrap (C9a a C9h) usam um par de bancos próprio cada. Eles usam por padrão as conexões administrativas `Host=127.0.0.1;Port=15432;Database=postgres;Username=ft;Password=ft_local_dev_only` (origem, `FT_TEST_ADMIN_CONNECTION`) e `Host=127.0.0.1;Port=15433;Database=postgres;Username=ft;Password=ft_local_dev_only` (consolidação, `FT_TEST_CONSOLIDATION_ADMIN_CONNECTION`); para outros servidores, defina as variáveis. **Os dois containers precisam estar `healthy`** (`docker compose up -d --wait`). Os testes do consolidado esperam condições observáveis (consulta ao banco, eventos do consumidor) e usam intervalos reduzidos do laço; os de produção (500 ms, 2 s, backoff de 1 s a 30 s) são cobertos por teste de unidade. A chave JWT dos testes é gerada a cada execução. Para rodar só os testes de unidade (sem banco): `dotnet test tests/FinancialTransactions.UnitTests -c Release --no-build --settings tests/vstest.runsettings`.

Resultado esperado: **173 testes de unidade e 100 de integração (273)**, todos aprovados; os 137 dos ciclos anteriores continuam presentes, pelo nome, e passam. Fonte dos números: *execução local* do executor no ciclo P07 (19/10/2026, duas rodadas); a *governança* reexecuta por conta própria. Comandos, contagens por ciclo, a sondagem contra processos reais e as ressalvas estão em [docs/verificacao.md](docs/verificacao.md). A *CI* tem o seu próprio resultado, na seção [CI](#ci); os contadores locais não são atribuídos à CI nem o contrário.

## Ensaio de carga

> **Escopo desta evidência.** O ensaio abaixo e os números de [docs/carga.md](docs/carga.md) são do ciclo P04 (commit `4463e38`), feitos contra a API de lançamentos **antes** da Outbox e dos processos do consolidado. Não medem o consolidado diário nem o POST com a segunda escrita, e não devem ser lidos como medição de P07. O ensaio dos 50 req/s do consolidado continua pendente (parte C do ciclo P08).

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

O workflow [.github/workflows/ci.yml](.github/workflows/ci.yml) (nome `ci`) roda em `push` na `main` e em `pull_request`. Ele usa `ubuntu-latest` com **dois** serviços `postgres:17` (origem em `15432` e consolidação em `15433`, a mesma credencial sintética do `docker-compose.yml`, sem nenhum segredo, ambos com `POSTGRES_DB=postgres` e sonda `pg_isready -U ft -d postgres`), SDK .NET 10.0.x e executa:

- `dotnet msbuild FinancialTransactions.sln -target:Restore -warnaserror`;
- `dotnet build FinancialTransactions.sln -c Release --no-restore --no-incremental -warnaserror`;
- `dotnet test FinancialTransactions.sln -c Release --no-build --settings tests/vstest.runsettings`, com `FT_TEST_ADMIN_CONNECTION` e `FT_TEST_CONSOLIDATION_ADMIN_CONNECTION` apontando para os serviços.

**Estado verificado (fonte: CI, consulta à API pública do GitHub em 19/10/2026):** para o commit `abcdf19c073dc95505010a593126248dc2bcc79a` há uma execução, o [run 39201091893](https://github.com/antonio-leonardo/FinancialTransactions/actions/runs/39201091893) (número 3, tentativa 1, evento `push`, 19/10/2026 04:45 UTC), job `build-and-test` (id 117894019834), com conclusão `success`. Segundo a governança do projeto em 19/10/2026, o workflow também executou com sucesso nos commits `fe545e9` e `ccf7bc7` (não reconferidos na consulta feita para esta documentação). Os contadores do log do run de `abcdf19` (Ubuntu 24.04.5, SDK 10.0.402, PostgreSQL 17.11, 92 testes de unidade e 45 de integração aprovados) foram **lidos pelo auditor-master** em 19/10/2026; a API consultada não os devolve.

O restore e o build rodaram **sem diagnósticos de warning de compilação**. O log do workflow contém avisos de outra natureza (depreciação de módulo do Node, versão de Node declarada pelas actions e mensagens do serviço PostgreSQL sobre a sonda de saúde); estão registrados como pendências de manutenção do workflow em [docs/verificacao.md](docs/verificacao.md).

**Estado desta versão (ciclo P07):** o workflow ganhou o segundo serviço PostgreSQL e a variável `FT_TEST_CONSOLIDATION_ADMIN_CONNECTION`. Fonte: leitura do log da CI pelo auditor-master em 19/10/2026: para o commit `a055816243e29088f47c41d372f23178b4ae7512` há uma execução bem-sucedida, o [run 39321900737](https://github.com/antonio-leonardo/FinancialTransactions/actions/runs/39321900737) (número 9, job 118297790062), com 173 testes de unidade e 98 de integração aprovados e build com zero warnings. A CI **não** executou a sondagem contra processos reais, as mutações, o ensaio de carga nem o roteiro deste README. Para o commit `4f0f10e6d187a2921a906fb129a7d1cb50eed5ce` (publicação da parte A do ciclo P08) o auditor leu, na noite de 19/10/2026 (fuso −03), o [run 39345385454](https://github.com/antonio-leonardo/FinancialTransactions/actions/runs/39345385454) (número 11, tentativa 1, job 118376609399, `completed/success`): build Release de 11 assemblies sem warnings, 193 testes de unidade e 115 de integração aprovados; leitura histórica do auditor, que não cobre a sondagem, as mutações nem o lote complementar posterior. Para o commit `c7cdec1c0d92592416d3e4738677d4a5fa500199` (que contém a correção da seção 9.7 de [docs/verificacao.md](docs/verificacao.md)), o auditor leu em 19/10/2026 o [run 39329327946](https://github.com/antonio-leonardo/FinancialTransactions/actions/runs/39329327946) (número 10, tentativa 1, job 118323234355, `completed/success`): build Release de 11 assemblies sem warnings e 173 + 100 testes aprovados. **Leitura histórica do auditor deste SHA**: a CI não executa as alterações posteriores (ciclo P08), que terão a sua própria execução depois de publicadas. Os testes do consolidado têm esperas de tempo real e rodam em runner compartilhado: uma falha intermitente na CI seria registrada, não escondida.

O workflow **não** executa o ensaio de carga, as sondagens manuais (reinício do banco, limite de corpo contra Kestrel real) nem o roteiro deste README, e não publica relatórios de teste. A execução vale para o commit indicado; commits posteriores têm a sua própria execução na página de Actions do repositório.

## Dados sintéticos e proteção

- **Dados sintéticos:** o seed da migration (clientes "Cliente A" e "Cliente B" com ids fixos) e o do ensaio de carga (nomes "Carga nnn") são inventados. Nenhum dado pessoal ou real está no repositório.
- **Credencial de laboratório:** `ft_local_dev_only` é a senha do banco do laboratório local. Ela aparece, de propósito, no `docker-compose.yml`, no `appsettings.Development.json`, neste README, em [docs/carga.md](docs/carga.md), na conexão administrativa padrão dos testes e no workflow de CI. Não protege nada além de um container local; não a reutilize.
- **Chave JWT:** nunca no repositório. Vem de variável de ambiente ou user-secrets e tem no mínimo 32 bytes.
- **Logs da aplicação:** sem corpo de requisição, token, `Idempotency-Key` bruta, chave nem connection string. Isso vale para os logs da aplicação; os do PostgreSQL ou de qualquer infraestrutura não foram avaliados.
- **Texto livre:** `description` (até 140 caracteres) é o único campo de texto livre. A aplicação não o registra em log; em uso real, é o que o cliente enviar.
- **Retenção:** nenhuma política implementada. Os dados ficam no volume do `docker-compose.yml` até remoção manual.
- **Conformidade:** nenhuma conformidade regulatória é alegada. Proteções ausentes (criptografia em repouso própria, rotação de chave, auditoria de acesso, limitação de taxa) estão listadas em [docs/arquitetura.md](docs/arquitetura.md).

## Estrutura

- `src/FinancialTransactions.Domain`: entidades, `MovementAmount`, regras temporais e de sinal, chave de idempotência e fingerprint.
- `src/FinancialTransactions.Infrastructure`: `DbContext`, configurações snake_case, migration com seed, `PositionQueries` (agregação por `SUM`/`COUNT` no banco).
- `src/FinancialTransactions.Api`: endpoints, autorização por recurso, `ProblemDetails`, caso de uso `RegisterMovement` (grava o movimento e o evento da Outbox na mesma transação).
- `src/FinancialTransactions.Auth`: JWT local e escopo por cliente, compartilhados pelas duas APIs; não referencia nenhum outro projeto.
- `src/FinancialTransactions.Consolidation`: núcleo da consolidação (projeção, aplicação idempotente, consumo da Outbox, bootstrap, consultas, saúde).
- `src/FinancialTransactions.Consolidation.Worker`: executável com os modos serviço (um consumidor por instância, saúde e diagnóstico da instância), `--backfill` (coordenador) e os comandos administrativos `--status`, `--list-quarantine` e `--requeue`.
- `src/FinancialTransactions.Consolidation.Api`: API do consolidado diário.
- `tools/FinancialTransactions.DevToken`: emissor local de tokens.
- `tools/FinancialTransactions.LoadTest`: ferramenta do ensaio de carga e `seed_load.sql`.
- `tests/`: unidade (xUnit) e integração (xUnit + PostgreSQL real).
- `.github/workflows/ci.yml`: workflow de CI.

## Documentação

- [docs/escopo.md](docs/escopo.md): fontes e procedência, hipóteses H1 a H15, contrato do consolidado diário, decisões por ciclo e matriz de cenários T01 a T16.
- [docs/arquitetura.md](docs/arquitetura.md): componentes, fluxos, modelo, decisões (ADRs curtas), segurança e evoluções propostas (não implementadas).
- [docs/verificacao.md](docs/verificacao.md): requisito → implementação → teste, comandos e resultados datados com a fonte de cada um, ambiente, pendências, checklist de entrega e o estado das adequações sugeridas.
- [docs/carga.md](docs/carga.md): ensaio de carga, reconciliação e planos das consultas.
- [docs/recuperacao.md](docs/recuperacao.md): verificação financeira somente leitura e reconstrução da projeção em banco paralelo, com seus limites.

## Limites

- **Verificação local:** os testes e o ensaio rodaram em ambiente de desenvolvimento (Windows, Docker Desktop, API, ferramenta e banco na mesma máquina). A CI cobre build e testes em Linux, não o ensaio de carga nem as sondagens manuais.
- **Carga:** os números de [docs/carga.md](docs/carga.md) são de requisições mistas em laço fechado com 32 trabalhadores; não são capacidade máxima, não são transações financeiras por segundo e não valem para produção. A reconciliação não encontrou divergências nas execuções registradas; não foram realizados controles negativos por adulteração para validar integralmente a capacidade de detecção do reconciliador.
- **Consolidado diário:** é eventualmente consistente e diz o quanto (`freshness`). Com reserva durável, várias instâncias do Worker podem consumir a mesma Outbox (lease de 45 s por evento, um evento em voo por instância); a deduplicação financeira continua no destino (`processed_events`) e a reserva não elimina toda execução sobreposta. Duas instâncias no mesmo host demonstram o mecanismo, não alta disponibilidade de produção. O prazo de resposta de falha (≤ 2 s) é hipótese do projeto, exercitada só para conexão recusada. A API do consolidado depende da origem para autorizar (titularidade da conta). Não foi feito ensaio de carga do consolidado (50 req/s com no máximo 5% de perda é requisito das orientações complementares; o ensaio específico do consolidado está **pendente** (parte C do ciclo P08) e nada foi demonstrado). O dia de negócio usa os dados de fuso da máquina. As adequações sugeridas e o que falta de cada uma estão em [docs/verificacao.md](docs/verificacao.md).
- **Sistema de registro:** não autoriza nem limita saldo; um débito é registrado mesmo que o saldo fique negativo (H1). Não há correção nem estorno.
- **Tempo:** a posição reflete o conhecimento atual; não há consulta "como se sabia em T2" (H9). `registeredAt` é o instante em que o servidor montou o registro, não o do commit.
- **Cadastros:** clientes e contas vêm da migration; não há API de cadastro.
- **Segurança:** emissor de token e chave simétrica locais, próprios de laboratório; sem rotação de chave, sem auditoria de acesso, sem criptografia em repouso própria, sem retenção.
- **Idempotência:** as chaves não expiram. Cada replay gera dois registros de nível `fail` do EF Core (a violação `23505` esperada que protege a identidade); não são erros técnicos, mas alertas baseados em nível de log precisariam excluí-los.
- **Pendências conhecidas** (relógio controlável nos testes, ajustes do workflow, correção parcial T14): [docs/verificacao.md](docs/verificacao.md).
