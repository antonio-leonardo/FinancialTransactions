# FinancialTransactions

Sistema de registro de movimentações financeiras (créditos e débitos) em contas de clientes, com consulta da posição consolidada em um instante.

## Pré-requisitos

- SDK .NET 10.0.301 ou superior na linha 10.0. O `rollForward: latestFeature` em `global.json` seleciona a maior feature band instalada da linha 10.0, não inferior à declarada.
- Docker com Compose v2.
- PowerShell 7.4 ou superior.

## Comandos

```powershell
docker compose up -d --wait
dotnet build FinancialTransactions.sln -c Release
dotnet test tests/FinancialTransactions.UnitTests --settings tests/vstest.runsettings
```

O banco é um PostgreSQL 17 publicado em `127.0.0.1:15432`. O escopo e as hipóteses do projeto estão em [docs/escopo.md](docs/escopo.md).

## Estado atual

- host da API criado, sem endpoints de negócio;
- projeto de testes de integração criado, sem casos;
- persistência, migrations e autenticação não implementadas.
