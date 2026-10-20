# Ensaio do consolidado diário: 50 GET/s com perda de até 5% (P08, parte C)

> **Natureza e escopo.** Este documento registra a medição **do consolidado diário** em chegadas abertas, feita pelo executor em 20/10/2026 sobre o commit `7977b1e`. Os números são de uma máquina de desenvolvimento, com todos os processos e os dois PostgreSQL no mesmo host, e **não valem para produção**. Os **50 req/s com no máximo 5% de perda** são **requisito obrigatório** das orientações complementares. O prazo útil de 1.000 ms, a janela de 60 s, os 3.000 slots, a massa e os 120 s de convergência são **hipóteses de laboratório do responsável do projeto** (D3-P08), não requisitos do contratante nem um objetivo de nível de serviço (RTO/RPO). Os números do ciclo P04 em [carga.md](carga.md) são de outro cenário (API de lançamentos antes da Outbox, laço fechado) e não substituem estes. Nada aqui foi executado pela CI nem aprovado por terceiros: é **execução local do executor, aguardando auditoria independente**.

## 1. Critérios (D3-P08)

| Aspecto | Valor |
|---|---|
| Alvo | 50 GET/s ao consolidado diário; perda de consultas de até 5% |
| Janela | 10 s de aquecimento (só leituras, fora do aceite) e 60 s medidos: **3.000** consultas programadas |
| Modelo | Chegadas **abertas**: o slot `i` é programado em `i / 50` s pelo relógio monotônico e não espera a resposta anterior |
| Resposta útil | HTTP 200, corpo que atende ao contrato do cenário, concluída em até **1.000 ms desde o instante programado** (inclui o atraso de emissão) |
| Perda | `(programadas − úteis no prazo) / programadas`; as categorias são exclusivas: útil, 200 tardio, erro HTTP, corpo inválido, falha de transporte/timeout, não emitido |
| Orçamento | até **150** não úteis em 3.000 aprova; **151** reprova (aritmética inteira; teste automatizado) |
| Validade do gerador | todos os 3.000 slots emitidos dentro da tolerância de 100 ms, sem rajada compensatória, e entre 45 e 55 emissões em **cada** segundo; senão o ensaio é **inválido** (não prova nem refuta) |
| Integridade | qualquer corpo financeiro diferente do oracle reprova, mesmo dentro dos 5% |

## 2. Ferramenta e prova do gerador

`tools/FinancialTransactions.LoadTest`, modo `--mode consolidation-open` (o modo de laço fechado do P04 continua existindo e está identificado como histórico). Parâmetros: `--scenario L1|L2|L3|F08`, `--arrival-rate`, `--duration`, `--warmup`, `--read-url` (uma ou duas vezes), `--write-url`, `--max-inflight-get 128`, `--max-inflight-post 64`, `--get-transport-timeout 2s`, `--useful-deadline 1000ms`, `--emission-tolerance 100ms`, `--manifest`, `--out`. A ferramenta recusa hosts fora de loopback, URL com credencial ou caminho, redirecionamentos (não os segue e invalida o ensaio), bancos que não correspondam ao manifesto do ensaio e pasta de saída já existente.

Códigos de saída: `0` somente para ensaio **válido e aprovado**; `10` válido e reprovado (perda acima de 5% ou falha de integridade/escrita); `20` reconciliação ou convergência divergente; `30` ensaio inválido ou inconclusivo (gerador não provou a taxa, cancelamento, sem amostra); `3` pré-condição ou recusa de segurança; `1` uso. O `resumo.json` registra o mesmo valor.

Controles automatizados do gerador (21 casos em `LoadGeneratorTests`, contra um servidor sintético, separados do ensaio): resposta imediata; resposta lenta que **não** reduz a agenda; 500; 200 com corpo inválido (integridade); capacidade em voo insuficiente (slots não emitidos contados e ensaio inválido); nenhuma amostra; percentil de valores conhecidos; **150 perdas em 3.000 aprova e 151 reprova** (ponta a ponta, com 200 tardio, 500 e corpo inválido misturados); timeout após 200 incompleto; ensaio cancelado inválido; slot atrasado além da tolerância não emitido e **sem rajada**; redirecionamento; recusas de segurança. Três mutações dirigidas (gerador que espera a requisição anterior, tardias e não emitidas fora do denominador, slot atrasado emitido em rajada) são detectadas por esses controles; ver [verificacao.md](verificacao.md).

## 3. Massa e preparação

| Item | Valor |
|---|---|
| População | 200 clientes, 400 contas (2 por cliente, abertas em 01/01/2026), **100.000 movimentos** |
| Datas | entre 01/09/2026 e 30/09/2026 (horário de Brasília), todas passadas |
| Determinismo | tudo é função do número `n` do movimento (fórmulas em `Mass.cs`, idênticas ao SQL do seed); contas quentes: `n` múltiplo de 5 vai às contas 1 a 10 (20% dos movimentos) |
| Distribuição observada | 12.000 linhas conta/dia; ver `seed_validacao.json` na evidência privada para mín./máx. por conta e por conta/dia |
| Escrita do seed | `movements` e `consolidation_outbox` na **mesma transação**, por SQL em lote declarado (`--mode seed-consolidation`) |
| Validação | agregação SQL por dia `America/Sao_Paulo` × fórmulas em C# (caminhos independentes): 12.000 de 12.000 linhas idênticas, 100.000 eventos, nenhum movimento sem evento |
| Bootstrap | coordenador real (`--backfill`), um Worker até `live`, zero pendentes e zero quarentena, `--verify` live e convergence consistentes **antes** do aquecimento |
| Repetições | cada repetição usa um par de bancos **novo** (`CREATE DATABASE … TEMPLATE`) clonado de um par de referência que passou pelo bootstrap real; a mesma massa, nunca sobre o volume aumentado por outra repetição |

Medida de escala (um Worker, um banco de origem e um de projeção, no mesmo host): migrações dos dois contextos 8,3 s; seed e validação 4,6 s; coordenador 2,5 s; **bootstrap dos 100.000 eventos até `live`: 1.645 (27,4 min) s** (≈ 61 eventos/s por Worker); `--verify --mode live` 1,6 s e `--verify --mode convergence` 1,5 s. Tamanhos: banco de origem 89,3 MiB (Outbox 47,2 MiB, movimentos 34,1 MiB); banco de projeção 21,7 MiB (`processed_events` 12,3 MiB). A retenção desta fatia **conserva** movimentos, Outbox e marcadores (sem purge): o crescimento acima é a base para planejar arquivamento futuro. Esta é a medida de 100.000 movimentos do **bootstrap e da verificação**; a captura e o replay da **reconstrução** em banco paralelo nessa escala não foram medidos.

## 4. Cenários

| ID | Programação | Configuração |
|---|---|---|
| L1 | 3.000 GET por conta a 50/s, 60 s | 1 API do consolidado + 1 Worker; sem escrita; todo corpo `complete` e igual ao oracle (créditos, débitos, líquido, contagem, fechamento) |
| L2 | 3.000 GET por cliente a 50/s, 60 s | idem, rota por cliente: as duas contas e os totais conferidos |
| L3 | 3.000 GET (80% conta, 20% cliente) a 50/s **mais** 600 POST a 10/s | 2 APIs do consolidado + 2 Workers sobre o mesmo par de bancos; GET alternado entre as duas URLs (contagem por instância); contrato validado em cada corpo; `stale` não é perda se o contrato é respeitado; reconciliação exata ao fim |
| F08 | 10 POST/s por 60 s; falha aos 10 s, retirada aos 45 s | três casos isolados: (a) Worker 1 encerrado por PID e religado; (b) API 1 encerrada e religada; (c) container da consolidação parado e religado. Fora do orçamento de 5% |

Ambiente: Intel Core i5-12450H (3 núcleos, 6 processadores lógicos), 15,0 GiB de memória, Windows 11 (10.0.26100), .NET SDK 10.0.402, dois PostgreSQL em containers Docker na mesma máquina. A duplicação de URLs mostra o atendimento por duas instâncias; **não** é balanceador, failover nem alta disponibilidade de produção.

## 5. Resultados

Cada linha é uma repetição com par de bancos próprio e é aprovada ou reprovada por si, sem média entre repetições. Latências: método dos percentis por ordenação, índice `ceil(p·n) − 1`, sobre as requisições **com resposta** (a amostra tem o tamanho da coluna "Emitidas" menos as falhas de transporte). "HTTP" é do envio ao corpo completo; "desde a programação" é do instante programado ao corpo completo (é a medida do prazo de 1.000 ms).

### GET por repeticao (L1, L2, L3)

| Repetição | Rota | Programadas | Emitidas | Uteis | Tardias | Erro HTTP | Corpo invalido | Transporte | Nao emitidas | Perda | complete/stale/unknown | HTTP p50/p95/p99/max (ms) | Desde a programacao p50/p95/p99/max (ms) | Taxa alcancada (req/s) | Gerador | Resultado |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|---|---|---:|---|---|
| L1 rep. 1 | account | 3000 | 3000 | 3000 | 0 | 0 | 0 | 0 | 0 | 0.00% | 3000/0/0 | 9.3/14.3/20.9/65.4 | 14.7/23.0/30.8/76.8 | 50.00 | valido | Approved |
| L1 rep. 2 | account | 3000 | 3000 | 3000 | 0 | 0 | 0 | 0 | 0 | 0.00% | 3000/0/0 | 8.6/12.6/17.4/25.7 | 14.0/22.1/26.8/54.9 | 50.01 | valido | Approved |
| L2 rep. 1 | customer | 3000 | 3000 | 3000 | 0 | 0 | 0 | 0 | 0 | 0.00% | 3000/0/0 | 8.4/11.8/15.6/31.8 | 13.8/21.5/26.3/48.0 | 49.99 | valido | Approved |
| L2 rep. 2 | customer | 3000 | 3000 | 3000 | 0 | 0 | 0 | 0 | 0 | 0.00% | 3000/0/0 | 8.4/12.1/16.1/26.8 | 13.9/21.6/25.6/33.5 | 50.00 | valido | Approved |
| L3 rep. 1 | account | 2400 | 2400 | 2400 | 0 | 0 | 0 | 0 | 0 | 0.00% | 2382/18/0 | 9.7/14.0/17.1/63.0 | 14.9/23.2/26.8/68.6 | 50.01 | valido | Approved |
| L3 rep. 1 | customer | 600 | 600 | 600 | 0 | 0 | 0 | 0 | 0 | 0.00% | 595/5/0 | 9.1/13.4/17.4/31.8 | 14.6/22.8/27.1/37.3 | 50.01 | valido | Approved |
| L3 rep. 1 | total | 3000 | 3000 | 3000 | 0 | 0 | 0 | 0 | 0 | 0.00% | 2977/23/0 | 9.6/14.0/17.3/63.0 | 14.9/23.1/26.8/68.6 | 50.01 | valido | Approved |
| L3 rep. 2 | account | 2400 | 2400 | 2400 | 0 | 0 | 0 | 0 | 0 | 0.00% | 2377/23/0 | 9.6/14.1/18.9/44.2 | 15.0/23.2/26.7/52.2 | 50.01 | valido | Approved |
| L3 rep. 2 | customer | 600 | 600 | 600 | 0 | 0 | 0 | 0 | 0 | 0.00% | 596/4/0 | 9.0/13.7/17.7/25.8 | 14.8/23.6/29.6/37.4 | 50.01 | valido | Approved |
| L3 rep. 2 | total | 3000 | 3000 | 3000 | 0 | 0 | 0 | 0 | 0 | 0.00% | 2973/27/0 | 9.5/14.0/18.9/44.2 | 14.9/23.2/27.4/52.2 | 50.01 | valido | Approved |

### Emissoes por segundo (GET) e por instancia (L3)

- **L1 rep. 1**: 60 segundos, min 49, max 51; por instancia: {"0":3000}
- **L1 rep. 2**: 60 segundos, min 50, max 50; por instancia: {"0":3000}
- **L2 rep. 1**: 60 segundos, min 50, max 50; por instancia: {"0":3000}
- **L2 rep. 2**: 60 segundos, min 50, max 50; por instancia: {"0":3000}
- **L3 rep. 1**: 60 segundos, min 50, max 50; por instancia: {"0":1500,"1":1500}
- **L3 rep. 2**: 60 segundos, min 50, max 50; por instancia: {"0":1500,"1":1500}

### Escrita (L3 e F08)

| Repetição | POST planejados | 201 na janela | Tardios (>1000 ms desde o slot) | Erro HTTP | Transporte | Nao emitidos | Resolvidos depois | Sem resolucao | POST p50/p95/p99/max HTTP (ms) | Backlog max (pend.) | Pendencia mais antiga max (s) | Quarentena max | Convergencia apos o produtor (s) | Reconciliacao exata | Conferencias finais pela API | Divergencias |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---|---:|---:|---:|---:|---|---:|---:|
| L3 rep. 1 | 600 | 600 | 8 | 0 | 0 | 0 | 0 | 0 | 13.5/24.2/1122.2/1725.4 | 20 | 0.8 | 0 | 0.64 | True | 100 | origem 0, projecao 0, API 0 |
| L3 rep. 2 | 600 | 600 | 8 | 0 | 0 | 0 | 0 | 0 | 13.7/22.9/1138.0/1741.9 | 20 | 0.8 | 0 | 0.34 | True | 100 | origem 0, projecao 0, API 0 |
| F08 (a) | 600 | 600 | 7 | 0 | 0 | 0 | 0 | 0 | 14.0/25.8/1015.3/1633.0 | 20 | 0.9 | 0 | 0.34 | True | 100 | origem 0, projecao 0, API 0 |
| F08 (b) | 600 | 600 | 6 | 0 | 0 | 0 | 0 | 0 | 14.2/24.5/987.9/1581.0 | 20 | 0.9 | 0 | 0.61 | True | 100 | origem 0, projecao 0, API 0 |
| F08 (c) | 600 | 600 | 7 | 0 | 0 | 0 | 0 | 0 | 14.5/23.4/1075.4/1695.4 | 396 | 39.5 | 0 | 0.61 | True | 100 | origem 0, projecao 0, API 0 |

### F08: sondas de leitura por alvo (1 por segundo por URL, fora do orcamento de 5%)

- **F08 (a)**, alvo 0: 60 sondas; 200=60 (freshness {"complete":59,"stale":1}); 503=0; sem resposta=0; outros=0; latencia maxima 222.3 ms
- **F08 (a)**, alvo 1: 60 sondas; 200=60 (freshness {"complete":59,"stale":1}); 503=0; sem resposta=0; outros=0; latencia maxima 209.1 ms
  - eventos: 21:51:24.784 ferramenta iniciada (PID 19944); 21:51:25.621 janela medida iniciada; 21:51:35.635 Worker 1 encerrado (PID 18112); 21:52:10.885 Worker 1 religado (PID 11560); 21:52:27.099 ferramenta terminou rc=0
- **F08 (b)**, alvo 0: 43 sondas; 200=26 (freshness {"complete":26}); 503=0; sem resposta=17; outros=0; latencia maxima 2015.5 ms
- **F08 (b)**, alvo 1: 43 sondas; 200=43 (freshness {"complete":43}); 503=0; sem resposta=0; outros=0; latencia maxima 222.4 ms
  - eventos: 21:52:41.123 ferramenta iniciada (PID 2252); 21:52:41.952 janela medida iniciada; 21:52:51.984 API do consolidado 1 encerrada (PID 10264); 21:53:27.246 API do consolidado 1 religada (PID 3816); 21:53:43.743 ferramenta terminou rc=0
- **F08 (c)**, alvo 0: 60 sondas; 200=26 (freshness {"complete":20,"stale":6}); 503=34; sem resposta=0; outros=0; latencia maxima 1027.7 ms
- **F08 (c)**, alvo 1: 60 sondas; 200=26 (freshness {"complete":20,"stale":6}); 503=34; sem resposta=0; outros=0; latencia maxima 1027.5 ms
  - eventos: 21:33:06.859 ferramenta iniciada (PID 8132); 21:33:07.699 janela medida iniciada; 21:33:17.715 container da consolidacao parado; 21:33:53.044 container da consolidacao religado; 21:33:58.510 container da consolidacao: saude=healthy; 21:34:09.590 ferramenta terminou rc=0

Leitura dos resultados: todas as repetições foram **válidas e aprovadas por si**: nenhum slot não emitido, nenhuma consulta fora do prazo, nenhum erro HTTP, nenhum corpo inválido e nenhuma divergência financeira em L1, L2 e L3; a taxa alcançada ficou em 49,99 a 50,01 req/s e cada segundo recebeu 50 emissões (um único segundo com 49 e outro com 51 em L1 rep. 1, dentro da faixa de 45 a 55). O p99 do tempo **desde a programação** ficou entre 25,6 e 30,8 ms e o máximo em 77 ms, contra o prazo de 1.000 ms: a margem é grande nesta massa. Em L3, 23 e 27 das 3.000 consultas (0,8% e 0,9%) vieram `stale` com contrato respeitado (há pendência real no recorte durante a escrita) e nenhuma `unknown`; as duas APIs receberam 1.500 consultas cada. Nas escritas, 600 de 600 POST foram 201 em todas as execuções; o backlog da Outbox ficou em até 20 eventos (pendência mais antiga de até 0,9 s) e a convergência ocorreu em menos de 0,7 s depois do fim do produtor. Os 6 a 8 POST "tardios" (acima de 1.000 ms desde o slot) são os primeiros da janela, com latência decrescente de cerca de 1,7 s a 1,0 s, compatível com partida a frio da API de lançamentos recém-iniciada (hipótese: inicialização e JIT; não investigada). No F08: (a) com um Worker encerrado, o outro continuou consumindo (backlog de até 20 eventos) e as leituras seguiram completas; (b) com a API 1 fora por cerca de 35 s, as 17 sondas daquela URL no período ficaram sem resposta (conexão recusada) e a outra URL respondeu todas as 43; (c) com o container da consolidação parado por cerca de 35 s, cada URL respondeu 503 em 34 das 60 sondas, o backlog chegou a 396 eventos (pendência mais antiga de 39,5 s) e tudo convergiu 0,6 s depois do fim do produtor, com reconciliação exata. Em nenhum caso de F08 houve perda de lançamento.

## 6. Integridade e reconciliação

L1 e L2 comparam **cada corpo** com o oracle calculado em SQL na origem (agregação independente por dia de negócio); nenhuma divergência foi aceita. L3 e F08 não afirmam oracle exato dos instantâneos intermediários: cada corpo é validado por contrato (identidade, dia, moeda, seleção de contas, tipos, aritmética interna, regras de `freshness`). Ao fim, com o produtor encerrado, aguarda-se a convergência (até 120 s) e reconcilia-se **ledger de intenções × origem × projeção**: esperado = fórmulas da massa + operações confirmadas; por conta e dia, a origem e a projeção igualam o esperado; cada chave de idempotência existe uma vez na origem; todos os identificadores do ledger existem e não há extras; Outbox sem pendentes nem quarentena; `processed_events` igual ao número de movimentos; e 100 leituras finais pela API iguais ao esperado e `complete`. POST sem resposta ou com 5xx seria resolvido depois da janela repetindo a **mesma** chave e jamais contaria como sucesso de desempenho.

## 7. Consultas e planos (C4)

`EXPLAIN (ANALYZE, BUFFERS)` com a massa do ensaio, em par próprio, duas execuções (fria e quente): titularidade por conta e por cliente (índice da chave primária e da titularidade), diagnóstico de pendências e consulta única da projeção. Tempos de execução quentes: titularidade por conta 0,05 ms e por cliente 0,07 ms; diagnóstico de pendências 0,9 ms (cliente) e 3,1 ms (conta mais quente); consulta única da projeção 0,3 a 0,4 ms (conta quente, conta comum e cliente). Nenhuma mudança de índice, consulta, pool ou migration foi necessária porque L1, L2 e L3 foram aprovados. **Observação de crescimento:** o diagnóstico de pendências conta por `account_id` todas as linhas da Outbox da conta (inclusive as já processadas, que são conservadas), de modo que seu custo cresce com o histórico da conta; com os 2.200 eventos da conta mais quente o custo foi de 3,1 ms. Um índice parcial sobre os eventos não confirmados resolveria isso se o histórico crescer; não foi criado porque nenhuma medição o exigiu.

## 8. Limites

- Máquina única de desenvolvimento (6 processadores lógicos): API, Workers, gerador e os dois PostgreSQL competem pelos mesmos processadores. Os resultados não valem para outra máquina nem para produção.
- Duas APIs e dois Workers num host demonstram a concorrência do protocolo de reserva, **não** alta disponibilidade (nenhuma proteção contra falha do host, do banco de origem ou do disco).
- Os 1.000 ms, a janela e a massa são hipóteses de laboratório; o ensaio não é RTO nem RPO contratado.
- A origem saudável é pressuposto: a independência da escrita não cobre a perda do próprio banco de origem.
- Os 6 a 8 primeiros POST da janela foram mais lentos (até ~1,7 s); é compatível com a partida a frio da API de lançamentos (o aquecimento é só de leituras, por decisão do cenário), hipótese não investigada; não entram no orçamento de 5%, que é das consultas.
- Cada repetição parte de um par clonado do par de referência com a mesma massa; o bootstrap real foi medido uma vez, no par de referência.
- Não há cache de resposta, balanceador, autoscaling nem broker.

## 9. Como reproduzir sem o kit

Pré-requisitos: .NET 10, Docker com dois PostgreSQL (`docker compose up -d`), solução compilada em Release. Use bancos descartáveis com o prefixo `ft_load_p08_` (a ferramenta recusa outros) e nunca os bancos de desenvolvimento.

```powershell
dotnet build FinancialTransactions.sln -c Release
$o = 'Host=127.0.0.1;Port=15432;Database=ft_load_p08_demo_src;Username=ft;Password=<senha local>'
$p = 'Host=127.0.0.1;Port=15433;Database=ft_load_p08_demo_cons;Username=ft;Password=<senha local>'
# 1. crie os dois bancos vazios (psql) e aplique as migrations dos dois contextos
dotnet ef database update --connection $o --context FinancialTransactionsDbContext --project src/FinancialTransactions.Infrastructure --startup-project src/FinancialTransactions.Api --configuration Release --no-build
dotnet ef database update --connection $p --context ConsolidationDbContext --project src/FinancialTransactions.Consolidation --startup-project src/FinancialTransactions.Consolidation.Worker --configuration Release --no-build
# 2. manifesto próprio (antes de tudo) e seed + validação independente
'{"runId":"demo","originDatabase":"ft_load_p08_demo_src","projectionDatabase":"ft_load_p08_demo_cons","expectedSeedMovements":100000}' | Set-Content manifesto.json
$env:FT_LOAD_ORIGIN_CONNECTION = $o; $env:FT_LOAD_PROJECTION_CONNECTION = $p
dotnet tools/FinancialTransactions.LoadTest/bin/Release/net10.0/FinancialTransactions.LoadTest.dll --mode seed-consolidation --manifest manifesto.json --out ./saida-seed
# 3. coordenador e um Worker até live (FT__ConnectionStrings__FinancialTransactions = $o, FT__ConnectionStrings__Consolidation = $p)
dotnet src/FinancialTransactions.Consolidation.Worker/bin/Release/net10.0/FinancialTransactions.Consolidation.Worker.dll --backfill
# subir o Worker (ASPNETCORE_URLS=http://127.0.0.1:5121) e a API do consolidado (…:5111) com a mesma FT__Auth__SigningKey; esperar fase live e zero pendentes
# 4. ensaio L1 (use --scenario L2 ou L3; em L3 suba também a segunda API e o segundo Worker e a API de lançamentos em :5100)
dotnet tools/FinancialTransactions.LoadTest/bin/Release/net10.0/FinancialTransactions.LoadTest.dll --mode consolidation-open --scenario L1 --arrival-rate 50 --duration 60 --warmup 10 --read-url http://127.0.0.1:5111 --manifest manifesto.json --out ./saida-l1
```

A chave de assinatura (`FT__Auth__SigningKey`) é a mesma das APIs e fica só no ambiente. O código de saída da ferramenta é o veredito do ensaio (seção 2); o `resumo.json` traz as contagens por categoria, os percentis, as emissões por segundo e, em L3 e F08, a convergência e a reconciliação.
