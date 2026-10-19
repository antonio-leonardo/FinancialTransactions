-- Pré-carga do ensaio de carga (P04). Executar com psql SOMENTE em um banco descartável ft_load_<n> já migrado:
--   psql -v ON_ERROR_STOP=1 -d ft_load_<n> -f seed_load.sql
-- Dados exclusivamente sintéticos. Falha (e nada é gravado) se já houver cliente de carga.
-- População: 200 clientes, 400 contas (2 por cliente, abertas em 2026-01-01Z), 100.000 movimentos.
-- Ids derivados do número: cliente cccccccc-0000-0000-0000-<n em 12 hex>, conta dddddddd-0000-0000-0000-<n em 12 hex>.
\set ON_ERROR_STOP on
\timing on

BEGIN;

DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM customers WHERE id::text LIKE 'cccccccc-0000-0000-0000-%') THEN
        RAISE EXCEPTION 'Clientes de carga já existem: use um banco vazio.';
    END IF;
END
$$;

-- Aleatoriedade reprodutível.
SELECT setseed(0.20261019);

INSERT INTO customers (id, name, created_at)
SELECT ('cccccccc-0000-0000-0000-' || lpad(to_hex(n), 12, '0'))::uuid,
       'Carga ' || lpad(n::text, 3, '0'),
       timestamptz '2026-01-01 00:00:00+00'
FROM generate_series(1, 200) AS n;

INSERT INTO accounts (id, customer_id, currency, opened_at)
SELECT ('dddddddd-0000-0000-0000-' || lpad(to_hex(n), 12, '0'))::uuid,
       ('cccccccc-0000-0000-0000-' || lpad(to_hex((n + 1) / 2), 12, '0'))::uuid,
       'BRL',
       timestamptz '2026-01-01 00:00:00+00'
FROM generate_series(1, 400) AS n;

-- Movimentos distribuídos uniformemente (250 por conta); effective_at uniforme em 2026-01-01..2026-09-30;
-- valor de 0.01 a 999.99 com duas casas; crédito (1) e débito (2) 50/50.
INSERT INTO movements (id, account_id, type, amount, currency, effective_at, registered_at, idempotency_key, request_fingerprint, description)
SELECT gen_random_uuid(),
       ('dddddddd-0000-0000-0000-' || lpad(to_hex(1 + ((n - 1) % 400)), 12, '0'))::uuid,
       (CASE WHEN random() < 0.5 THEN 1 ELSE 2 END)::smallint,
       (1 + floor(random() * 99999))::numeric / 100,
       'BRL',
       e.at,
       e.at,
       'seed-' || n,
       encode(sha256(convert_to('seed-' || n, 'UTF8')), 'hex'),
       NULL
FROM generate_series(1, 100000) AS n
CROSS JOIN LATERAL (
    SELECT timestamptz '2026-01-01 00:00:00+00'
           + (random() * (timestamptz '2026-09-30 23:59:59+00' - timestamptz '2026-01-01 00:00:00+00')) AS at
) AS e;

COMMIT;

-- Estatísticas para o planejador (fora da transação), como o autovacuum faria.
ANALYZE customers;
ANALYZE accounts;
ANALYZE movements;

SELECT 'customers' AS tabela, count(*) FROM customers
UNION ALL SELECT 'accounts', count(*) FROM accounts
UNION ALL SELECT 'movements', count(*) FROM movements;
