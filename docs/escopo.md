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

- **Instantes na resposta:** `effectiveAt`, `registeredAt`, `asOf` e `openedAt` são devolvidos em UTC. O offset recebido do cliente não é preservado: o banco guarda `timestamptz`, e a normalização para UTC acontece na fábrica `Movement.Create` (fronteira do domínio) e na leitura de `asOf`.
- **`asOf` na query string:** aceito em ISO 8601; sem offset, assume-se UTC. Ausente ou ilegível → 422 `validation_error`. Um `+` em query string vira espaço no HTTP; use `Z`, offset negativo ou `%2B`.
- **Corpo ilegível:** JSON malformado ou de tipo errado é convertido pelo tratador de exceções em 422 `validation_error`, sem detalhes internos. Campos ausentes ou semanticamente inválidos também são 422 `validation_error`, com mensagem curta em `detail`.
- **`description` vazia:** tratada como ausente (nula) antes do fingerprint e da gravação, para que `""` e ausência sejam a mesma intenção.
- **Protocolo de idempotência (H10):** inserção em transação explícita; violação de `UNIQUE (account_id, idempotency_key)` (`23505`) → rollback da transação abortada, limpeza do rastreamento e releitura por (conta, chave) fora dela; fingerprint igual → 200 com o movimento existente; diferente → 422 `idempotency_key_conflict`. Se a releitura não encontrar a linha (a transação concorrente foi revertida depois do conflito), a resposta é 409 `retry_later`, sem repetir o insert.
- **Limite do agregado:** cada movimento cabe em `numeric(18,2)` com teto de 1.000.000.000,00 (H3). `SUM` em PostgreSQL é `numeric` sem limite prático e chega ao .NET como `decimal` (28 dígitos significativos), suficiente para a soma de qualquer volume realista de movimentos no teto. O valor agregado nunca é materializado em coluna; se um dia for, a coluna de posição precisará de precisão maior que `numeric(18,2)`.
- **Sinal:** a convenção crédito positivo / débito negativo existe em um único ponto do código C# (`MovementRules.SignedAmount`) e é espelhada em SQL (`CASE type WHEN 1 THEN amount ELSE -amount END`) em `PositionQueries`, porque a agregação acontece no banco (H12). Ambas são cobertas por teste.
- **Rota com id inválido:** `{accountId:guid}` que não é GUID não casa com a rota e devolve 404 sem corpo, coerente com não revelar existência.
- **Precisão dos instantes:** `timestamptz` resolve microssegundos e `DateTimeOffset` resolve 100 ns. `Movement.Create` trunca `effectiveAt` e `registeredAt` a microssegundos antes de persistir, para que a resposta `201` e qualquer releitura (inclusive a repetição idempotente, `200`) carreguem exatamente o mesmo valor. O fingerprint usa o `effectiveAt` como recebido, normalizado para UTC.
