# Ensaio de carga, consultas e reconciliação

Registro do ensaio de carga limitado executado em 19/10/2026 (ciclo P04). Tudo o que está aqui foi medido nesse dia, com os comandos da seção "Como reproduzir". **Não é meta do cliente**: as metas H-L1 a H-L4 são hipóteses do projeto. **Não extrapole para produção**: ambiente de desenvolvedor, API e banco na mesma máquina, Docker Desktop.

## Ambiente

| Item | Valor |
|---|---|
| Sistema | Windows 11 (build 10.0.26100), máquina de desenvolvimento |
| CPU exposta ao SO | Intel Core i5-12450H, 3 núcleos e 6 processadores lógicos (`Win32_Processor`) |
| Memória | 17.178.710.016 bytes (≈ 16 GiB) (`Win32_ComputerSystem`) |
| Docker | Docker Desktop, engine Linux 29.8.2; `docker info`: 6 CPUs, 8.325.816.320 bytes (≈ 7,75 GiB) |
| Banco | PostgreSQL 17.11 no container do projeto, `127.0.0.1:15432`; `shared_buffers = 128MB`, `random_page_cost = 4`, `effective_cache_size = 4GB` (padrões da imagem) |
| API | `dotnet FinancialTransactions.Api.dll` (Release, .NET 10.0.402) em Kestrel no host, `ASPNETCORE_ENVIRONMENT=Production`, `ASPNETCORE_URLS` em loopback, porta livre; connection string com `Timeout=5;Command Timeout=15` |
| Ferramenta | `tools/FinancialTransactions.LoadTest` (Release), mesma máquina |

API, ferramenta de carga e PostgreSQL competem pelos mesmos processadores. Os números abaixo incluem essa competição.

**Log da API no ensaio:** o padrão grava no console cada comando SQL do EF Core em `Information`. Para que o custo de I/O de log não domine a medida, o ensaio fixou a categoria `Microsoft.EntityFrameworkCore` em `Warning` por variável de ambiente (`Logging__LogLevel__Microsoft.EntityFrameworkCore`); o código e o `appsettings.json` não mudaram. Os demais logs ficaram como estão (evento `MovementRegistered` em `Information`, saída redirecionada para arquivo).

## Método

| Item | Valor |
|---|---|
| Banco | descartável `ft_load_1` e `ft_load_2` (um por execução), migration `InitialSchema` aplicada (`dotnet ef database update`), depois pré-carga por SQL |
| Pré-carga | [seed_load.sql](../tools/FinancialTransactions.LoadTest/seed_load.sql), uma transação: 200 clientes, 400 contas (2 por cliente, abertas em `2026-01-01Z`), 100.000 movimentos (250 por conta), `effective_at` uniforme em `2026-01-01` a `2026-09-30`, valor de `0,01` a `999,99` com duas casas, crédito/débito 50/50, `idempotency_key = 'seed-' \|\| n`, `request_fingerprint` hex de 64 caracteres. `ANALYZE` ao final |
| Contagens após a pré-carga | `customers` 202, `accounts` 403, `movements` 100.000 (os 2 clientes e 3 contas a mais são o seed da migration) |
| Tempo da pré-carga | `INSERT` dos movimentos: 2.268 ms (execução 1) e 2.513 ms (execução 2); script inteiro, incluindo `psql`: 2.610 ms e 2.948 ms |
| Concorrência | 32 trabalhadores assíncronos, cada um em laço fechado (envia, espera o corpo completo, envia a próxima; sem *think time*); um `HttpClient` compartilhado com `SocketsHttpHandler`, timeout de 30 s, sem retry |
| Duração | 10 s de aquecimento (descartado das métricas, mas as linhas que criou entram na reconciliação) e 60 s medidos |
| Mistura | 60% POST de movimento (chave nova, tipo, valor e instante aleatórios dentro das regras); 5% POST **replay** de uma chave já aceita pela própria ferramenta (mesmo corpo); 25% GET posição por conta com `asOf` aleatório; 10% GET posição por cliente |
| Determinismo | gerador por trabalhador com semente `20261019 + índice do trabalhador`; a sequência de cada trabalhador é reproduzível, o intercalamento entre trabalhadores não é |
| Identidade | a ferramenta emite um token por cliente com a chave da API (`FT__Auth__SigningKey`, gerada por execução); chave e tokens ficam só em memória |
| Classificação | **técnica**: qualquer 5xx, timeout, exceção de transporte. **Negócio esperado**: `200` em replay com o mesmo `movementId`. **Sucesso**: `201` em POST novo, `200` em GET. **Inesperado**: qualquer outra resposta (por exemplo `422`, `401`, `409`), tratada como defeito a investigar, nunca como esperada |
| Latência | do envio ao corpo completo recebido; só requisições com resposta (um timeout não tem latência) |
| Percentis | amostras ordenadas, índice `ceil(p × n) − 1`, sem interpolação; `n` registrado. Todas as amostras medidas estão em `amostras.csv` |
| Vazão | requisições medidas ÷ segundos entre o fim do aquecimento e o término da última requisição medida (≈ 60,03 s) |
| Recursos | `docker stats --no-stream` do container do PostgreSQL a cada 5 s, coletado em PowerShell (não pela ferramenta) |

## Resultados

Latências em ms. Nenhuma resposta técnica ou inesperada em nenhuma das duas execuções.

### Execução 1 (`ft_load_1`)

Requisições medidas: 37.658 em 60,03 s (627 req/s); 4.116 descartadas no aquecimento; 25.170 movimentos criados (2.443 no aquecimento).

| Operação | n | Status | p50 | p95 | p99 | máx | req/s |
|---|---:|---|---:|---:|---:|---:|---:|
| POST novo | 22.727 | 201: 22.727 | 59,6 | 89,2 | 106,0 | 153,1 | 378,6 |
| POST replay | 1.857 | 200: 1.857 | 79,2 | 116,3 | 137,2 | 166,2 | 30,9 |
| GET posição por conta | 9.357 | 200: 9.357 | 23,7 | 39,7 | 50,2 | 81,3 | 155,9 |
| GET posição por cliente | 3.717 | 200: 3.717 | 33,5 | 51,9 | 66,0 | 99,1 | 61,9 |

### Execução 2 (`ft_load_2`, banco novo)

Requisições medidas: 38.739 em 60,03 s (645 req/s); 4.227 descartadas no aquecimento; 25.901 movimentos criados (2.509 no aquecimento).

| Operação | n | Status | p50 | p95 | p99 | máx | req/s |
|---|---:|---|---:|---:|---:|---:|---:|
| POST novo | 23.392 | 201: 23.392 | 57,7 | 85,9 | 105,4 | 401,7 | 389,7 |
| POST replay | 1.909 | 200: 1.909 | 75,9 | 109,8 | 130,4 | 393,7 | 31,8 |
| GET posição por conta | 9.601 | 200: 9.601 | 23,1 | 38,1 | 49,3 | 180,2 | 159,9 |
| GET posição por cliente | 3.837 | 200: 3.837 | 33,1 | 51,3 | 61,9 | 246,1 | 63,9 |

As duas execuções ficaram na mesma ordem de grandeza (627 e 645 req/s; p95 de cada operação com diferença menor que 7%). Os máximos da execução 2 (até 401,7 ms) são picos isolados que não movem p95 nem p99; a causa não foi investigada.

### Recursos do container PostgreSQL

13 amostras por execução (inclui uma antes do início e uma depois do fim). CPU como o `docker stats` reporta (100% = um núcleo): média 93% e pico 181% na execução 1; média 96% e pico 166% na execução 2. Memória do container de 84 a 162 MiB (execução 1) e de 135 a 212 MiB (execução 2). Valores completos em `docker_stats.txt`. O ensaio não identifica o gargalo: API, ferramenta e banco dividem a mesma CPU, e este ensaio não decompõe o uso entre eles.

### Hipóteses do projeto

| Hipótese | Resultado |
|---|---|
| H-L1: zero erro técnico | atendida nas duas execuções (0 de 41.774 e 0 de 42.966 requisições, aquecimento incluído) |
| H-L2: reconciliação exata | atendida nas duas execuções (abaixo) |
| H-L3: p95 de GET posição por conta < 250 ms com 100 mil movimentos de histórico | atendida: 39,7 ms e 38,1 ms (n = 9.357 e 9.601) |
| H-L4: p95 de POST < 400 ms | atendida: POST novo 89,2 e 85,9 ms; POST (novo + replay) 93,0 ms (n = 24.584) e 89,6 ms (n = 25.301) |

## Reconciliação pós-carga

A ferramenta guarda em memória cada POST aceito (`201`) e, ao final, confere o banco por uma conexão administrativa.

| Verificação | Execução 1 | Execução 2 |
|---|---|---|
| (a) `COUNT(*)` de movimentos não-seed = número de `201` | 25.170 = 25.170 | 25.901 = 25.901 |
| (b) conjunto de `id` igual, sem ausentes nem extras; campos (conta, tipo, valor, `effective_at`, chave) iguais por linha | 0 ausentes, 0 extras, 0 campos divergentes | 0, 0, 0 |
| (c) por conta (400 contas), soma assinada e contagem do banco = as gravadas | 0 contas divergentes | 0 |
| (d) nenhuma `(conta, chave)` com mais de uma linha (replays não criaram linhas; cada resposta de replay trouxe o `movementId` original) | 0 | 0 |
| (e) nenhuma linha com `amount <= 0` ou escala > 2 (todas as linhas, seed incluído) | 0 | 0 |
| Seed intacto (100.000 linhas e soma assinada de −163.380,77 antes e depois; a soma é a mesma nas duas execuções porque a pré-carga usa `setseed`) | sim | sim |

A comparação por identidade e por conta existe porque um total correto não prova que cada movimento certo está no lugar certo: uma linha perdida e outra duplicada, ou um valor trocado entre contas, preservam a contagem total. A soma assinada global também esconde compensações entre contas; por isso a soma é conferida conta a conta e os campos linha a linha. Relatórios completos: `reconciliacao.json`.

## Planos das consultas

Banco `ft_load_1` após o ensaio (125.170 movimentos; a estatística do planejador ainda dizia 120.329). Conta com mais movimentos (343, dos quais 45 até o corte), `asOf = 2026-05-17T00:00:00Z`. O SQL é o de `PositionQueries` com os parâmetros trocados por literais. Saída completa em `explain.txt`; cada consulta foi executada duas vezes (a segunda com cache quente, reportada abaixo).

| Cenário | Plano | Buffers (hit) | Tempo de execução |
|---|---|---:|---:|
| Por conta, índice presente | Bitmap Index Scan em `ix_movements_account_id_effective_at` + Bitmap Heap Scan (45 linhas) | 48 | 0,093 ms |
| Por cliente (`ANY` de 2 contas), índice presente | idem, 75 linhas, `Sort` + `GroupAggregate` | 75 | 0,158 ms |
| Por conta, **sem** `ix_..._effective_at` | Bitmap Index Scan em `ux_movements_account_id_idempotency_key` (343 linhas) + filtro de `effective_at` (298 descartadas) | 345 | 0,324 ms |
| Por cliente, sem `ix_..._effective_at` | idem, 650 linhas lidas, 575 descartadas | 396 | 0,441 ms |
| Por conta, **sem nenhum índice iniciado por `account_id`** | Seq Scan, 125.125 linhas descartadas | 2.827 | 7,005 ms |
| Por cliente, sem nenhum índice iniciado por `account_id` | Seq Scan | 2.827 | 8,365 ms |

Os experimentos negativos foram feitos com `BEGIN; DROP INDEX ...; EXPLAIN (ANALYZE, BUFFERS) ...; ROLLBACK;` e a verificação ao fim confirmou os três índices de volta. Observações:

- Com o índice presente, nenhuma das duas consultas faz `Seq Scan`.
- Tirar só `ix_movements_account_id_effective_at` **não** causa varredura sequencial: o planejador usa o índice único `(account_id, idempotency_key)` pela primeira coluna e filtra `effective_at` depois, lendo a conta inteira (343 linhas em vez de 45; 345 buffers em vez de 48). Por isso o índice dedicado pesa pouco hoje (por conta: ≈ 3,5 vezes mais tempo e ≈ 7 vezes mais buffers, em valores de décimos de milissegundo), mas o ganho cresce com o número de movimentos futuros por conta.
- Sem nenhum dos dois índices, a consulta lê a tabela inteira (2.827 buffers, ≈ 75 vezes o tempo da consulta indexada por conta e ≈ 53 vezes por cliente).
- O tempo de execução da consulta (0,09 a 0,16 ms) é uma fração mínima da latência observada do GET (p50 de 23 a 34 ms). O restante é o caminho da API (autenticação, consulta de autorização da conta, serialização, HTTP) mais a competição por CPU; este ensaio não decompõe essa parcela.

## Decisão: índices, cache e posição materializada

- **Índices:** o conjunto atual é suficiente para este recorte. Nenhum índice foi criado e `PositionQueries` não foi alterado.
- **Cache e posição materializada não entram** (H12). A consulta indexada custa décimos de milissegundo, as metas H-L3 e H-L4 foram atendidas com folga e não há leitura repetida a poupar nesta carga. Um cache traria invalidação sobre dados que mudam a cada POST e posições por `asOf` arbitrário; uma posição materializada exigiria provar coerência com o histórico.
- **Condição de revisão:** reabrir a decisão se (1) o volume de movimentos por conta crescer por ordens de grandeza (aqui são 250 a 343 por conta; o custo da consulta cresce com as linhas da conta até `asOf`), (2) o p95 do GET de posição ultrapassar a meta de 250 ms em carga representativa, ou (3) as leituras passarem a dominar a carga. Nesses casos, avaliar primeiro um índice que cubra `type` e `amount` (varredura só de índice) e só depois uma posição derivada, com a reconciliação de identidade e soma por conta como critério de recuperação.

## Observação: ruído de log no replay

Cada replay executa o `INSERT`, recebe a violação `23505` da constraint de idempotência (o mecanismo que protege a identidade) e relê a linha. O EF Core registra por isso dois eventos de nível `fail` por replay (`Database.Command[20102]` e `Update[10000]`), sem valores de parâmetros. Em `ft_load_1` foram 2.066 replays e 4.132 registros `fail`; em `ft_load_2`, 2.121 e 4.242. Nenhum corresponde a erro técnico (todos os replays responderam `200`). O comportamento já estava registrado em `docs/escopo.md` (P02); a carga mostra a escala: em produção, alertas baseados em nível de log precisariam excluir essas categorias. Mudar isso fica fora do escopo deste ciclo.

## Limites

- Ambiente de desenvolvedor, mesma máquina para API, ferramenta de carga e banco, Docker Desktop; não extrapolar para produção.
- Laço fechado com 32 trabalhadores: a vazão e as latências medidas são as de uma carga que se autorregula (um trabalhador só envia depois de receber). Ele não mede o comportamento sob chegada fixa acima da capacidade, nem acha o ponto de saturação.
- Uma configuração de concorrência, uma duração de 60 s, duas execuções; sem rampa nem teste de resistência.
- Histórico de 100 mil movimentos em 400 contas (250 por conta); a distribuição é uniforme e não imita contas com volumes muito desiguais.
- Os planos são de um banco recém-carregado e analisado, com cache quente; um banco com muita escrita, bloat ou cache frio pode se comportar diferente.
- O ensaio não exercita falhas de dependência (isso foi verificado em P03) nem múltiplas instâncias da API.

## Como reproduzir

Pré-requisitos: container do projeto saudável (`docker compose up -d --wait`), solução compilada em Release e `dotnet tool restore`. Os comandos usam um banco descartável `ft_load_<n>`; nunca o de desenvolvimento.

```powershell
# 1. banco descartável com o esquema
$conn = 'Host=127.0.0.1;Port=15432;Database=ft_load_1;Username=ft;Password=ft_local_dev_only;Timeout=5;Command Timeout=15'
dotnet ef database update --connection $conn --project src/FinancialTransactions.Infrastructure --startup-project src/FinancialTransactions.Api --configuration Release --no-build

# 2. pré-carga (psql dentro do container)
Get-Content tools/FinancialTransactions.LoadTest/seed_load.sql -Raw | docker exec -i financialtransactions-postgres psql -U ft -d ft_load_1 -v ON_ERROR_STOP=1

# 3. API em Production, porta livre, mesma chave para a API e a ferramenta
$env:FT__Auth__SigningKey = [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(48))
$env:FT__ConnectionStrings__FinancialTransactions = $conn
$env:ASPNETCORE_ENVIRONMENT = 'Production'
$env:ASPNETCORE_URLS = 'http://127.0.0.1:5199'
dotnet src/FinancialTransactions.Api/bin/Release/net10.0/FinancialTransactions.Api.dll

# 4. em outro terminal (com a mesma FT__Auth__SigningKey): a ferramenta
$env:FT_LOAD_ADMIN_CONNECTION = $conn
dotnet tools/FinancialTransactions.LoadTest/bin/Release/net10.0/FinancialTransactions.LoadTest.dll --base-url http://127.0.0.1:5199 --out ./saida-carga
```

A ferramenta recusa `--base-url` que não seja `127.0.0.1` ou `localhost` e recusa banco cujo nome não comece com `ft_load_`; também recusa um banco que já tenha movimentos além do seed. Saída em `--out`: `resumo.json`, `aceitos.jsonl`, `amostras.csv`, `reconciliacao.json`. Código de saída: 0 ok; 1 uso; 3 pré-condição; 10 erro técnico ou resposta inesperada; 20 reconciliação divergente. Ao final, remova o banco: `DROP DATABASE ft_load_1 WITH (FORCE)`.
