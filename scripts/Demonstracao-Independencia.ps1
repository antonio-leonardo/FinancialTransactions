<#
.SYNOPSIS
    Demonstra que registrar lançamentos independe da consolidação diária e mostra a recuperação depois da falha.

.DESCRIPTION
    Sobe, em bancos descartáveis próprios, a API de lançamentos (5100), o Worker (5120) e a API do consolidado (5110),
    interrompe a consolidação, registra 10 lançamentos novos durante a interrupção, restaura só o que foi interrompido e
    reconcilia origem, Outbox e projeção por conjuntos de identificadores e por valores.

    -Scenario Workers     encerra o processo do Worker (nada mais é tocado).
    -Scenario Projection  para um container PostgreSQL DEDICADO da demonstração que guarda a projeção. Os containers do
                          docker-compose.yml nunca são parados nem recriados. (CONNECTION LIMIT 0 não serve: o usuário ft é
                          superusuário e superusuários são isentos do limite; ver docs/demonstracao-independencia.md.)

    Pré-requisitos: solução compilada em Release (dotnet build FinancialTransactions.sln -c Release), dotnet tool restore,
    os dois containers do docker-compose.yml 'healthy' e as portas 5100, 5110 e 5120 livres.

    Códigos de saída: 0 = demonstração válida e aprovada; 10 = algum critério reprovado (resultado preservado);
    30 = pré-condição não atendida ou execução inválida (grava invalido.json, nunca resultado parcial).

    Recursos criados (e removidos no finally pela lista exata): bancos ft_demo_<Suffix>_src e ft_demo_<Suffix>_cons,
    processos próprios (por PID) e, no cenário Projection, o container ft-demo-cons-<Suffix>.

.PARAMETER Password
    Senha do usuário 'ft'. O padrão é a credencial SINTÉTICA de laboratório do docker-compose.yml; não a reutilize.
#>
#Requires -Version 7.4
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('Workers', 'Projection')][string]$Scenario,
    [ValidatePattern('^[a-z0-9]{3,24}$')][string]$Suffix = (Get-Date -Format 'yyyyMMddHHmmss'),
    [string]$OutDir,
    [int]$OriginPort = 15432,
    [int]$ProjectionPort = 15433,
    [string]$Password = 'ft_local_dev_only',
    [string]$OriginContainer = 'financialtransactions-postgres',
    [string]$ProjectionContainer = 'financialtransactions-consolidation-postgres',
    [int]$DedicatedPort = 15439
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$Repo = Split-Path $PSScriptRoot -Parent
if (-not $OutDir) { $OutDir = Join-Path ([IO.Path]::GetTempPath()) "ft-demo-$Suffix" }
$OutDir = [IO.Path]::GetFullPath($OutDir)

# ---------- constantes ----------
$A1 = 'aaaaaaa1-0000-0000-0000-000000000001'
$A2 = 'aaaaaaa2-0000-0000-0000-000000000002'
$B1 = 'bbbbbbb1-0000-0000-0000-000000000001'
$CA = '11111111-1111-1111-1111-111111111111'
$CB = '22222222-2222-2222-2222-222222222222'
$OwnerOf = @{ $A1 = $CA; $A2 = $CA; $B1 = $CB }
$ApiUrl = 'http://127.0.0.1:5100'; $ConsUrl = 'http://127.0.0.1:5110'; $WorkerUrl = 'http://127.0.0.1:5120'
$SrcDb = "ft_demo_${Suffix}_src"; $ConsDb = "ft_demo_${Suffix}_cons"
$DedicatedName = "ft-demo-cons-$Suffix"
$UsesDedicated = $Scenario -eq 'Projection'
$ConsContainer = if ($UsesDedicated) { $DedicatedName } else { $ProjectionContainer }
$ConsPort = if ($UsesDedicated) { $DedicatedPort } else { $ProjectionPort }
$Bin = @{
    Api      = Join-Path $Repo 'src/FinancialTransactions.Api/bin/Release/net10.0/FinancialTransactions.Api.dll'
    Worker   = Join-Path $Repo 'src/FinancialTransactions.Consolidation.Worker/bin/Release/net10.0/FinancialTransactions.Consolidation.Worker.dll'
    ConsApi  = Join-Path $Repo 'src/FinancialTransactions.Consolidation.Api/bin/Release/net10.0/FinancialTransactions.Consolidation.Api.dll'
    DevToken = Join-Path $Repo 'tools/FinancialTransactions.DevToken/bin/Release/net10.0/FinancialTransactions.DevToken.dll'
}
$EnvNames = 'ASPNETCORE_ENVIRONMENT', 'ASPNETCORE_URLS', 'FT__Auth__SigningKey', 'FT__ConnectionStrings__FinancialTransactions', 'FT__ConnectionStrings__Consolidation'
$SavedEnv = @{}; foreach ($n in $EnvNames) { $SavedEnv[$n] = [Environment]::GetEnvironmentVariable($n) }

# ---------- estado ----------
$script:Secrets = @($Password)
$script:Timeline = [System.Collections.Generic.List[object]]::new()
$script:Criteria = [System.Collections.Generic.List[object]]::new()
$script:Resources = [System.Collections.Generic.List[object]]::new()
$script:Cleanup = [System.Collections.Generic.List[object]]::new()
$script:Procs = @{}
$script:T0 = $null
$script:OutReady = $false
$Res =[ordered]@{ before = [ordered]@{}; during = [ordered]@{}; after = [ordered]@{} }

class DemoAbort : System.Exception { DemoAbort([string]$m) : base($m) {} }

# ---------- utilitários ----------
function Get-Utc { [DateTimeOffset]::UtcNow }
function Format-Utc($t) { $t.ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ss.fffZ') }

function Clean([string]$s) {
    foreach ($x in $script:Secrets) { if ($x) { $s = $s.Replace($x, '<segredo>') } }
    $s
}

function Add-Event([string]$what, $detail = $null) {
    $now = Get-Utc
    $script:Timeline.Add([ordered]@{ tUtc = Format-Utc $now; event = $what; detail = $detail })
    Write-Host ("[{0}] {1}{2}" -f (Format-Utc $now), $what, $(if ($null -ne $detail) { ' | ' + (Clean ([string]$detail)) } else { '' }))
}

function Add-Criterion([string]$id, [string]$description, [bool]$passed, $detail) {
    $script:Criteria.Add([ordered]@{ id = $id; description = $description; passed = $passed; detail = $detail })
    Write-Host ("  criterio {0}: {1} - {2}" -f $id, $(if ($passed) { 'APROVADO' } else { 'REPROVADO' }), (Clean ([string]$detail)))
}

function Wait-Condition([scriptblock]$Condition, [string]$What, [int]$TimeoutSec = 90, [int]$IntervalMs = 500) {
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    while ((Get-Date) -lt $deadline) {
        $ok = $false
        try { $ok = [bool](& $Condition) } catch { $ok = $false }
        if ($ok) { return }
        Start-Sleep -Milliseconds $IntervalMs
    }
    throw "Condição não observada em ${TimeoutSec}s: $What"
}

function Test-PortOpen([int]$Port) {
    $client = [Net.Sockets.TcpClient]::new()
    try {
        $task = $client.ConnectAsync('127.0.0.1', $Port)
        return ($task.Wait(1500) -and $client.Connected)
    } catch { return $false } finally { $client.Dispose() }
}

function Invoke-Http([string]$Method, [string]$Url, [hashtable]$Headers = $null, [string]$Body = $null, [int]$TimeoutSec = 20) {
    $started = Get-Utc
    $p = @{ Method = $Method; Uri = $Url; SkipHttpErrorCheck = $true; TimeoutSec = $TimeoutSec }
    if ($Headers) { $p.Headers = $Headers }
    if ($Body) { $p.Body = $Body; $p.ContentType = 'application/json' }
    try {
        $r = Invoke-WebRequest @p
        $text = if ($r.Content -is [byte[]]) { [Text.Encoding]::UTF8.GetString($r.Content) } else { [string]$r.Content }
        $loc = $null; if ($r.Headers.ContainsKey('Location')) { $loc = [string]($r.Headers['Location'] | Select-Object -First 1) }
        [pscustomobject]@{ Status = [int]$r.StatusCode; Body = $text; Location = $loc; Start = $started; End = Get-Utc }
    } catch {
        [pscustomobject]@{ Status = -1; Body = $_.Exception.GetType().Name; Location = $null; Start = $started; End = Get-Utc }
    }
}

function ConvertFrom-Body($http) { try { $http.Body | ConvertFrom-Json } catch { $null } }
function Get-Prop($obj, [string]$name) { if ($null -ne $obj -and $obj.PSObject.Properties[$name]) { $obj.$name } else { $null } }

function Invoke-Psql([string]$Container, [string]$Db, [string]$Sql) {
    $out = $Sql | docker exec -i $Container psql -U ft -d $Db -At -F '|' -v ON_ERROR_STOP=1 2>&1
    if ($LASTEXITCODE -ne 0) { throw "psql falhou (rc=$LASTEXITCODE) em ${Container}/${Db}: $((($out | Out-String).Trim()))" }
    (($out | Out-String).Trim())
}
function Invoke-PsqlOrNull([string]$Container, [string]$Db, [string]$Sql) { try { Invoke-Psql $Container $Db $Sql } catch { $null } }
function Query-Origin([string]$Sql) { Invoke-Psql $OriginContainer $SrcDb $Sql }
function Query-Proj([string]$Sql) { Invoke-Psql $ConsContainer $ConsDb $Sql }
function Split-Lines([string]$Text) { if ([string]::IsNullOrWhiteSpace($Text)) { @() } else { @($Text -split "\r?\n" | Where-Object { $_ -ne '' }) } }
function Sql-List($ids) { ($ids | ForEach-Object { "'$_'" }) -join ',' }

function Register-Resource([string]$kind, [string]$name, [string]$where) {
    $script:Resources.Add([ordered]@{ kind = $kind; name = $name; where = $where; createdUtc = Format-Utc (Get-Utc); removed = $false })
    $script:Resources | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $OutDir 'recursos_proprios.json') -Encoding utf8
}
function Mark-Removed([string]$name, [string]$how) {
    foreach ($r in $script:Resources) { if ($r.name -eq $name) { $r.removed = $true } }
    $script:Cleanup.Add([ordered]@{ resource = $name; action = $how; tUtc = Format-Utc (Get-Utc) })
    $script:Resources | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $OutDir 'recursos_proprios.json') -Encoding utf8
}

function Set-ProcessEnv([hashtable]$Values) {
    foreach ($n in $EnvNames) { [Environment]::SetEnvironmentVariable($n, $(if ($Values.ContainsKey($n)) { $Values[$n] } else { $null })) }
}

function Start-Own([string]$Name, [string]$Dll, [string]$Url, [hashtable]$Env) {
    $port = ([uri]$Url).Port
    if (Test-PortOpen $port) { throw "Porta $port ocupada; a demonstração não encerra processo alheio." }
    $Env['ASPNETCORE_ENVIRONMENT'] = 'Production'; $Env['ASPNETCORE_URLS'] = $Url
    Set-ProcessEnv $Env
    $logs = Join-Path $OutDir 'logs'
    $p = Start-Process -FilePath 'dotnet' -ArgumentList @((Split-Path $Dll -Leaf)) -WorkingDirectory (Split-Path $Dll) -PassThru -NoNewWindow `
        -RedirectStandardOutput (Join-Path $logs "$Name.out.log") -RedirectStandardError (Join-Path $logs "$Name.err.log")
    $script:Procs[$Name] = $p
    Register-Resource 'process' $Name "PID $($p.Id), porta $port"
    Add-Event "processo iniciado: $Name" "PID $($p.Id), porta $port"
    $p
}

function Stop-Own([string]$Name) {
    $p = $script:Procs[$Name]
    if ($p -and -not $p.HasExited) {
        Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
        $null = $p.WaitForExit(15000)
    }
}

function Invoke-Admin([string[]]$Arguments, [hashtable]$Env) {
    Set-ProcessEnv $Env
    [Environment]::SetEnvironmentVariable('ASPNETCORE_ENVIRONMENT', 'Production')
    $out = & dotnet $Bin.Worker @Arguments 2>> (Join-Path $OutDir 'logs/admin.err.log')
    $rc = $LASTEXITCODE
    $text = (($out | ForEach-Object { "$_" }) -join "`n")
    [pscustomobject]@{ ExitCode = $rc; Text = $text; Json = $(try { $text | ConvertFrom-Json } catch { $null }) }
}

# Lê um log que o processo filho ainda mantém aberto (compartilhamento de leitura e escrita).
function Read-LogLines([string]$Path) {
    if (-not (Test-Path $Path)) { return @() }
    $fs = [IO.FileStream]::new($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
    try {
        $sr = [IO.StreamReader]::new($fs)
        $lines = [System.Collections.Generic.List[string]]::new()
        while ($null -ne ($l = $sr.ReadLine())) { $lines.Add($l) }
        , $lines.ToArray()
    } finally { $fs.Dispose() }
}

function Get-DockerInfo([string]$Name) {
    $o = docker inspect -f '{{.Id}}|{{if .State.Health}}{{.State.Health.Status}}{{else}}{{.State.Status}}{{end}}' $Name 2>$null
    if ($LASTEXITCODE -ne 0 -or -not $o) { return $null }
    $parts = ([string]$o).Trim() -split '\|'
    [pscustomobject]@{ Id = $parts[0]; State = $parts[1] }
}

function Wait-PgTcp([string]$Container) {
    Wait-Condition { docker exec $Container pg_isready -h 127.0.0.1 -U ft -d postgres *> $null; $LASTEXITCODE -eq 0 } "PostgreSQL de $Container aceitando conexões TCP" 90 1000
}

# ---------- massa e valores esperados (calculados pelo script, nunca lidos do banco) ----------
$script:EffectiveAt = (Get-Utc).AddMinutes(-2)
function New-Op([string]$Key, [string]$Account, [string]$Type, [decimal]$Amount, [string]$Label) {
    $amountText = $Amount.ToString('0.00', [Globalization.CultureInfo]::InvariantCulture)
    $body = '{"type":"' + $Type + '","amount":' + $amountText + ',"effectiveAt":"' + (Format-Utc $script:EffectiveAt) + '","description":"' + $Label + '"}'
    [pscustomobject]@{ Key = $Key; Account = $Account; Customer = $OwnerOf[$Account]; Type = $Type; Amount = $Amount; Body = $body; Label = $Label
        Status = $null; MovementId = $null; Location = $null; StartUtc = $null; EndUtc = $null }
}
$InitialOps = @(
    New-Op "demo-$Suffix-i-01" $A1 'Credit' 100.00 'i-01'
    New-Op "demo-$Suffix-i-02" $A1 'Debit' 30.00 'i-02'
    New-Op "demo-$Suffix-i-03" $A1 'Credit' 20.00 'i-03'
    New-Op "demo-$Suffix-i-04" $A2 'Credit' 50.00 'i-04'
    New-Op "demo-$Suffix-i-05" $A2 'Debit' 8.00 'i-05'
    New-Op "demo-$Suffix-i-06" $B1 'Credit' 10.00 'i-06'
)
$WindowOps = @(
    New-Op "demo-$Suffix-w-01" $A1 'Credit' 40.00 'w-01'
    New-Op "demo-$Suffix-w-02" $A1 'Debit' 15.00 'w-02'
    New-Op "demo-$Suffix-w-03" $A2 'Credit' 25.00 'w-03'
    New-Op "demo-$Suffix-w-04" $B1 'Debit' 3.00 'w-04'
    New-Op "demo-$Suffix-w-05" $A1 'Credit' 12.50 'w-05'
    New-Op "demo-$Suffix-w-06" $A2 'Debit' 5.50 'w-06'
    New-Op "demo-$Suffix-w-07" $B1 'Credit' 60.00 'w-07'
    New-Op "demo-$Suffix-w-08" $A1 'Debit' 7.25 'w-08'
    New-Op "demo-$Suffix-w-09" $A2 'Credit' 9.99 'w-09'
    New-Op "demo-$Suffix-w-10" $B1 'Credit' 1.01 'w-10'
)

function Get-Expected($Ops) {
    $acc = @{}
    foreach ($a in $A1, $A2, $B1) { $acc[$a] = @{ credits = [decimal]0; debits = [decimal]0; count = 0 } }
    foreach ($o in $Ops) {
        if ($o.Type -eq 'Credit') { $acc[$o.Account].credits += $o.Amount } else { $acc[$o.Account].debits += $o.Amount }
        $acc[$o.Account].count++
    }
    $cust = @{}
    foreach ($c in $CA, $CB) { $cust[$c] = @{ credits = [decimal]0; debits = [decimal]0; count = 0 } }
    foreach ($a in $acc.Keys) { $c = $OwnerOf[$a]; $cust[$c].credits += $acc[$a].credits; $cust[$c].debits += $acc[$a].debits; $cust[$c].count += $acc[$a].count }
    foreach ($x in @($acc.Values) + @($cust.Values)) { $x.net = $x.credits - $x.debits }
    @{ account = $acc; customer = $cust }
}

function Test-Money($Actual, [decimal]$Expected) { $null -ne $Actual -and ([decimal]$Actual) -eq $Expected }

# ---------- chamadas ao produto ----------
function Send-Op($Op, [hashtable]$Tokens) {
    $h = @{ Authorization = "Bearer $($Tokens[$Op.Customer])"; 'Idempotency-Key' = $Op.Key }
    $r = Invoke-Http 'POST' "$ApiUrl/api/v1/accounts/$($Op.Account)/movements" $h $Op.Body
    $j = ConvertFrom-Body $r
    $Op.Status = $r.Status; $Op.MovementId = [string](Get-Prop $j 'movementId'); $Op.Location = $r.Location
    $Op.StartUtc = Format-Utc $r.Start; $Op.EndUtc = Format-Utc $r.End
    $r
}

function Get-Consolidation([string]$Kind, [string]$Id, [string]$Customer, [string]$Day, [hashtable]$Tokens) {
    $route = if ($Kind -eq 'account') { "accounts/$Id" } else { "customers/$Id" }
    $r = Invoke-Http 'GET' "$ConsUrl/api/v1/$route/consolidation/daily?day=$Day" @{ Authorization = "Bearer $($Tokens[$Customer])" }
    $j = ConvertFrom-Body $r
    [pscustomobject]@{
        Status = $r.Status; Code = Get-Prop $j 'code'; Freshness = Get-Prop $j 'freshness'; Unresolved = Get-Prop $j 'unresolvedEvents'
        Failed = Get-Prop $j 'failedEvents'; Credits = Get-Prop $j 'credits'; Debits = Get-Prop $j 'debits'; Net = Get-Prop $j 'net'
        Count = Get-Prop $j 'movementCount'; Closing = Get-Prop $j 'closingBalance'; ObservedAt = Get-Prop $j 'observedAt'
    }
}

function Read-AllConsolidation([string]$Day, [hashtable]$Tokens) {
    $o = [ordered]@{}
    foreach ($a in $A1, $A2, $B1) { $o["account:$a"] = Get-Consolidation 'account' $a $OwnerOf[$a] $Day $Tokens }
    foreach ($c in $CA, $CB) { $o["customer:$c"] = Get-Consolidation 'customer' $c $c $Day $Tokens }
    $o
}

function Compare-Consolidation($Reads, $Expected, [string]$Freshness) {
    $problems = @()
    foreach ($k in $Reads.Keys) {
        $kind, $id = $k -split ':'
        $e = $Expected[$kind][$id]; $g = $Reads[$k]
        if ($g.Status -ne 200) { $problems += "$k status=$($g.Status)"; continue }
        if (-not (Test-Money $g.Credits $e.credits) -or -not (Test-Money $g.Debits $e.debits) -or -not (Test-Money $g.Net $e.net) -or -not (Test-Money $g.Closing $e.net) -or $g.Count -ne $e.count) {
            $problems += "$k valores diferentes (obtido credits=$($g.Credits) debits=$($g.Debits) net=$($g.Net) closing=$($g.Closing) count=$($g.Count); esperado credits=$($e.credits) debits=$($e.debits) net=$($e.net) count=$($e.count))"
        }
        if ($g.Freshness -ne $Freshness -or $g.Unresolved -ne 0 -or $g.Failed -ne 0) { $problems += "$k freshness=$($g.Freshness) unresolved=$($g.Unresolved) failed=$($g.Failed)" }
    }
    $problems
}

function Summarize-Reads($Reads) {
    $o = [ordered]@{}
    foreach ($k in $Reads.Keys) {
        $g = $Reads[$k]
        $o[$k] = [ordered]@{ status = $g.Status; code = $g.Code; freshness = $g.Freshness; unresolvedEvents = $g.Unresolved; failedEvents = $g.Failed
            credits = $g.Credits; debits = $g.Debits; net = $g.Net; movementCount = $g.Count; closingBalance = $g.Closing }
    }
    $o
}

function Get-OriginPending { [int](Query-Origin 'SELECT COUNT(*) FROM consolidation_outbox WHERE status = 0') }

# ---------- resultado ----------
function Write-Results([bool]$Valid, [string]$Reason, [int]$ExitCode) {
    $finished = Get-Utc
    if (-not $script:OutReady) { Write-Host "Execução inválida: $(Clean $Reason)"; return }
    if (-not $Valid) {
        $inv =[ordered]@{ valid = $false; reason = (Clean $Reason); scenario = $Scenario; suffix = $Suffix; exitCode = 30; finishedUtc = Format-Utc $finished; timeline = $script:Timeline; cleanup = $script:Cleanup }
        $inv | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $OutDir 'invalido.json') -Encoding utf8
        return
    }
    $sha = $null; try { $sha = (git -C $Repo rev-parse HEAD 2>$null); if ($LASTEXITCODE -ne 0) { $sha = $null } } catch { $sha = $null }
    $passed = (@($script:Criteria | Where-Object { -not $_.passed }).Count -eq 0) -and $script:Criteria.Count -gt 0
    $result = [ordered]@{
        valid = $true; passed = $passed; exitCode = $ExitCode; scenario = $Scenario; suffix = $Suffix; sourceCommit = $sha
        startedUtc = Format-Utc $script:T0; finishedUtc = Format-Utc $finished
        environment = [ordered]@{ os = [Runtime.InteropServices.RuntimeInformation]::OSDescription; dotnet = (dotnet --version); powershell = $PSVersionTable.PSVersion.ToString() }
        resources = [ordered]@{ originDatabase = $SrcDb; projectionDatabase = $ConsDb; projectionHost = $(if ($UsesDedicated) { "container dedicado $DedicatedName (porta $DedicatedPort)" } else { "container do compose (porta $ProjectionPort)" }); originPort = $OriginPort }
        before = $Res.before; during = $Res.during; after = $Res.after
        timeline = $script:Timeline; criteria = $script:Criteria; cleanup = $script:Cleanup
    }
    $result | ConvertTo-Json -Depth 10 | Set-Content (Join-Path $OutDir 'resultado.json') -Encoding utf8
    Write-Markdown $result
}

function Write-Markdown($Result) {
    $sb = [System.Text.StringBuilder]::new()
    $null = $sb.AppendLine("# Demonstração de independência: cenário $($Result.scenario)")
    $null = $sb.AppendLine()
    $null = $sb.AppendLine("Resultado: **$(if ($Result.passed) { 'APROVADO' } else { 'REPROVADO' })** (código de saída $($Result.exitCode)). Início $($Result.startedUtc), fim $($Result.finishedUtc) (UTC). Código-fonte: ``$($Result.sourceCommit)``.")
    $null = $sb.AppendLine()
    $null = $sb.AppendLine('## Critérios')
    $null = $sb.AppendLine(); $null = $sb.AppendLine('| Critério | Resultado | Detalhe |'); $null = $sb.AppendLine('|---|---|---|')
    foreach ($c in $Result.criteria) { $null = $sb.AppendLine("| $($c.id): $($c.description) | $(if ($c.passed) { 'aprovado' } else { 'REPROVADO' }) | $(([string]$c.detail) -replace '\|', '/') |") }
    foreach ($phase in 'before', 'during', 'after') {
        $title = @{ before = 'Antes'; during = 'Durante (janela da falha)'; after = 'Depois (recuperação e reconciliação)' }[$phase]
        $null = $sb.AppendLine(); $null = $sb.AppendLine("## $title"); $null = $sb.AppendLine()
        $null = $sb.AppendLine('| Item | Valor |'); $null = $sb.AppendLine('|---|---|')
        $ops = $null; $reads = [ordered]@{}
        foreach ($k in $Result[$phase].Keys) {
            $v = $Result[$phase][$k]
            if ($k -eq 'operations') { $ops = $v; continue }
            if ($v -is [System.Collections.IDictionary] -and @($v.Values | Where-Object { $_ -is [System.Collections.IDictionary] -and $_.Contains('freshness') }).Count -gt 0) { $reads[$k] = $v; continue }
            if ($v -is [System.Collections.IDictionary] -or ($v -is [System.Collections.IEnumerable] -and $v -isnot [string])) { $v = ($v | ConvertTo-Json -Depth 8 -Compress) }
            $null = $sb.AppendLine("| $k | $(([string]$v) -replace '\|', '/') |")
        }
        if ($ops) {
            $null = $sb.AppendLine(); $null = $sb.AppendLine('Lançamentos da janela (início e fim em UTC):'); $null = $sb.AppendLine()
            $null = $sb.AppendLine('| Chave | Conta | Tipo | Valor | Início | Fim | Status | movementId | Location |'); $null = $sb.AppendLine('|---|---|---|---:|---|---|---:|---|---|')
            foreach ($o in $ops) { $null = $sb.AppendLine("| $($o.key) | $($o.account) | $($o.type) | $($o.amount) | $($o.startUtc) | $($o.endUtc) | $($o.status) | $($o.movementId) | $($o.location) |") }
        }
        foreach ($rk in $reads.Keys) {
            $null = $sb.AppendLine(); $null = $sb.AppendLine("Consultas ao consolidado (${rk}):"); $null = $sb.AppendLine()
            $null = $sb.AppendLine('| Consulta | Status | code | freshness | unresolved | failed | créditos | débitos | líquido | contagem | fechamento |'); $null = $sb.AppendLine('|---|---:|---|---|---:|---:|---:|---:|---:|---:|---:|')
            foreach ($q in $reads[$rk].Keys) { $g = $reads[$rk][$q]; $null = $sb.AppendLine("| $q | $($g.status) | $($g.code) | $($g.freshness) | $($g.unresolvedEvents) | $($g.failedEvents) | $($g.credits) | $($g.debits) | $($g.net) | $($g.movementCount) | $($g.closingBalance) |") }
        }
    }
    $null = $sb.AppendLine(); $null = $sb.AppendLine('## Linha do tempo (UTC)'); $null = $sb.AppendLine()
    $null = $sb.AppendLine('| Instante | Evento | Detalhe |'); $null = $sb.AppendLine('|---|---|---|')
    foreach ($e in $Result.timeline) { $null = $sb.AppendLine("| $($e.tUtc) | $($e.event) | $((([string]$e.detail)) -replace '\|', '/') |") }
    $null = $sb.AppendLine(); $null = $sb.AppendLine('## Limpeza'); $null = $sb.AppendLine()
    foreach ($c in $Result.cleanup) { $null = $sb.AppendLine("- $($c.tUtc): $($c.resource) ($($c.action))") }
    Set-Content (Join-Path $OutDir 'resultado.md') ($sb.ToString()) -Encoding utf8
}

# =====================================================================================================================
$exitCode = 30
$invalidReason = $null
$containersBefore = $null
try {
    # ---------- pré-condições ----------
    if ($OutDir.StartsWith($Repo, [StringComparison]::OrdinalIgnoreCase)) { throw 'OutDir precisa ficar fora do repositório.' }
    if ((Test-Path $OutDir) -and (Get-ChildItem $OutDir -Force | Select-Object -First 1)) { throw 'OutDir já existe e não está vazio; a evidência nunca é sobrescrita.' }
    New-Item -ItemType Directory -Force (Join-Path $OutDir 'logs') | Out-Null
    $script:OutReady = $true
    $script:T0 = Get-Utc
    Add-Event 'início' "cenário=$Scenario sufixo=$Suffix"

    foreach ($tool in 'dotnet', 'docker') { if (-not (Get-Command $tool -ErrorAction SilentlyContinue)) { throw "Ferramenta ausente: $tool." } }
    foreach ($k in $Bin.Keys) { if (-not (Test-Path $Bin[$k])) { throw "Binário ausente ($k). Compile em Release: dotnet build FinancialTransactions.sln -c Release." } }
    $o = Get-DockerInfo $OriginContainer; $c = Get-DockerInfo $ProjectionContainer
    if (-not $o -or $o.State -ne 'healthy') { throw "Container $OriginContainer ausente ou não healthy." }
    if (-not $c -or $c.State -ne 'healthy') { throw "Container $ProjectionContainer ausente ou não healthy." }
    $containersBefore = [ordered]@{ origin = $o.Id; projection = $c.Id }
    foreach ($port in 5100, 5110, 5120) { if (Test-PortOpen $port) { throw "Porta $port ocupada." } }
    if ($UsesDedicated) {
        if (Test-PortOpen $DedicatedPort) { throw "Porta $DedicatedPort ocupada." }
        if (Get-DockerInfo $DedicatedName) { throw "Container $DedicatedName já existe." }
        docker image inspect postgres:17 *> $null
        if ($LASTEXITCODE -ne 0) { throw 'Imagem postgres:17 ausente localmente (suba o docker-compose.yml uma vez).' }
    }
    if (Invoke-Psql $OriginContainer 'postgres' "SELECT 1 FROM pg_database WHERE datname = '$SrcDb'") { throw "Banco $SrcDb já existe." }
    if (-not $UsesDedicated -and (Invoke-Psql $ProjectionContainer 'postgres' "SELECT 1 FROM pg_database WHERE datname = '$ConsDb'")) { throw "Banco $ConsDb já existe." }
    Add-Event 'pré-condições atendidas' "containers do projeto healthy (ids registrados), portas 5100/5110/5120 livres"

    # ---------- recursos próprios (registrados antes de criar) ----------
    Register-Resource 'database' $SrcDb $OriginContainer
    Register-Resource 'database' $ConsDb $ConsContainer
    if ($UsesDedicated) {
        Register-Resource 'container' $DedicatedName "porta $DedicatedPort"
        docker run -d --name $DedicatedName -e POSTGRES_USER=ft -e "POSTGRES_PASSWORD=$Password" -e POSTGRES_DB=postgres -p "127.0.0.1:${DedicatedPort}:5432" postgres:17 *> $null
        if ($LASTEXITCODE -ne 0) { throw "docker run falhou para $DedicatedName." }
        Wait-PgTcp $DedicatedName
        Add-Event 'container dedicado da projeção pronto' $DedicatedName
    }
    Invoke-Psql $OriginContainer 'postgres' "CREATE DATABASE `"$SrcDb`"" | Out-Null
    Invoke-Psql $ConsContainer 'postgres' "CREATE DATABASE `"$ConsDb`"" | Out-Null
    $originConn = "Host=127.0.0.1;Port=$OriginPort;Database=$SrcDb;Username=ft;Password=$Password;Timeout=5;Command Timeout=15"
    $consConn = "Host=127.0.0.1;Port=$ConsPort;Database=$ConsDb;Username=ft;Password=$Password;Timeout=5;Command Timeout=15"
    $script:Secrets += @($originConn, $consConn)

    Push-Location $Repo
    try {
        foreach ($m in @(
                @($originConn, 'FinancialTransactionsDbContext', 'src/FinancialTransactions.Infrastructure', 'src/FinancialTransactions.Api'),
                @($consConn, 'ConsolidationDbContext', 'src/FinancialTransactions.Consolidation', 'src/FinancialTransactions.Consolidation.Worker'))) {
            $log = & dotnet ef database update --connection $m[0] --context $m[1] --project $m[2] --startup-project $m[3] --configuration Release --no-build 2>&1
            Set-Content (Join-Path $OutDir "logs/ef-$($m[1]).log") (Clean (($log | Out-String))) -Encoding utf8
            if ($LASTEXITCODE -ne 0) { throw "dotnet ef falhou para $($m[1]) (rc=$LASTEXITCODE)." }
            Add-Event "migrations aplicadas" $m[1]
        }
    } finally { Pop-Location }

    # ---------- chave (só em memória) e tokens ----------
    $key = [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(48))
    $script:Secrets += $key
    $tokens = @{}
    foreach ($cust in $CA, $CB) {
        [Environment]::SetEnvironmentVariable('FT__Auth__SigningKey', $key)
        $t = ((& dotnet $Bin.DevToken --customer $cust --minutes 90) | Out-String).Trim()
        if (($t -split '\.').Count -ne 3) { throw 'Token inválido emitido pela ferramenta DevToken.' }
        $tokens[$cust] = $t; $script:Secrets += $t
    }

    $envApi = @{ 'FT__Auth__SigningKey' = $key; 'FT__ConnectionStrings__FinancialTransactions' = $originConn }   # sem a connection string da projeção
    $envCons = @{ 'FT__Auth__SigningKey' = $key; 'FT__ConnectionStrings__FinancialTransactions' = $originConn; 'FT__ConnectionStrings__Consolidation' = $consConn }

    # ---------- processos: API de lançamentos primeiro (a Outbox precisa existir antes de outro escritor) ----------
    $apiProc = Start-Own 'api-lancamentos' $Bin.Api $ApiUrl $envApi
    Wait-Condition { (Invoke-Http 'GET' "$ApiUrl/health/ready").Status -eq 200 } 'API de lançamentos pronta' 90
    $Res.before.apiStartedWithoutProjectionConnectionString = $true
    $Res.before.writeApiPid = $apiProc.Id

    # ---------- massa inicial: 6 lançamentos pela própria API ----------
    foreach ($op in $InitialOps) {
        $r = Send-Op $op $tokens
        if ($r.Status -ne 201 -or -not $op.MovementId) { throw "Massa inicial: $($op.Key) respondeu $($r.Status)." }
    }
    Add-Event 'massa inicial registrada' '6 de 6 com 201'
    $day = [TimeZoneInfo]::ConvertTime($script:EffectiveAt, [TimeZoneInfo]::FindSystemTimeZoneById('America/Sao_Paulo')).ToString('yyyy-MM-dd')

    # ---------- coordenador, Worker e API do consolidado ----------
    $bf = Invoke-Admin @('--backfill') $envCons
    Add-Event 'coordenador --backfill' "rc=$($bf.ExitCode)"
    if ($bf.ExitCode -ne 0) { throw "--backfill terminou com rc=$($bf.ExitCode)." }
    $workerPid1 = (Start-Own 'worker-1' $Bin.Worker $WorkerUrl $envCons).Id
    Wait-Condition { $s = (Invoke-Admin @('--status') $envCons).Json; $s -and $s.projection.phase -eq 'live' -and $s.origin.pending -eq 0 } 'fase live e pending=0' 120 1000
    Start-Own 'api-consolidado' $Bin.ConsApi $ConsUrl $envCons | Out-Null
    Wait-Condition { (Invoke-Http 'GET' "$ConsUrl/health/ready").Status -eq 200 } 'API do consolidado pronta' 90
    Add-Event 'sistema pronto' 'fase live, pending=0, API do consolidado pronta'

    # ---------- S0 (antes) ----------
    $expected0 = Get-Expected $InitialOps
    $ids0 = @($InitialOps | ForEach-Object { $_.MovementId } | Sort-Object)
    $processed0 = @(Split-Lines (Query-Proj 'SELECT event_id FROM processed_events ORDER BY 1') | Sort-Object)
    $reads0 = Read-AllConsolidation $day $tokens
    $probs0 = @(Compare-Consolidation $reads0 $expected0 'complete')
    $writeReady0 = (Invoke-Http 'GET' "$ApiUrl/health/ready").Status
    $Res.before.businessDay = $day
    $Res.before.movementIds = $ids0
    $Res.before.processedEventsCount = $processed0.Count
    $Res.before.processedEventsMatchMovementIds = (($ids0 -join ',') -eq ($processed0 -join ','))
    $Res.before.consolidation = Summarize-Reads $reads0
    $Res.before.consolidationMatchesExpected = ($probs0.Count -eq 0)
    $Res.before.writeReadiness = $writeReady0
    if ($probs0.Count -gt 0 -or -not $Res.before.processedEventsMatchMovementIds -or $writeReady0 -ne 200) { throw "Estado inicial (S0) incoerente: $($probs0 -join '; ')" }
    Add-Event 'S0 registrado' "processed_events=$($processed0.Count), consolidado complete com valores esperados, readiness da escrita=200"

    # ---------- interrupção comprovada ----------
    $workerLog = Join-Path $OutDir 'logs/worker-1.out.log'
    $logLinesBefore = (Read-LogLines $workerLog).Count
    $interruptedAt = Get-Utc
    $signals = [ordered]@{}
    if ($Scenario -eq 'Workers') {
        Stop-Own 'worker-1'
        Add-Event 'Worker encerrado' "PID $workerPid1"
        $exited = $script:Procs['worker-1'].HasExited
        Wait-Condition { -not (Test-PortOpen 5120) } 'porta 5120 fechada' 30 500
        $live = Invoke-Http 'GET' "$WorkerUrl/health/live" $null $null 5
        $signals.workerProcessExited = $exited; $signals.workerPortClosed = -not (Test-PortOpen 5120); $signals.workerHealthLiveStatus = $live.Status
        $signalsOk = $exited -and $signals.workerPortClosed -and $live.Status -eq -1
    } else {
        docker stop $DedicatedName *> $null
        if ($LASTEXITCODE -ne 0) { throw "docker stop falhou para $DedicatedName." }
        Add-Event 'container dedicado da projeção parado' $DedicatedName
        $workerReady = $null; $apiReady = $null
        Wait-Condition { $script:wr = Invoke-Http 'GET' "$WorkerUrl/health/ready" $null $null 8; $script:wr.Status -eq 503 } 'Worker /health/ready = 503' 60 1000
        Wait-Condition { $script:ar = Invoke-Http 'GET' "$ConsUrl/health/ready" $null $null 8; $script:ar.Status -eq 503 } 'API do consolidado /health/ready = 503' 60 1000
        $g = Get-Consolidation 'account' $A1 $CA $day $tokens
        $pattern = 'Tentativa do consumidor falhou \(classe (Transient|Environment)\)'
        $logHit = $null
        Wait-Condition {
            $lines = Read-LogLines $workerLog
            $script:hit = @($lines | Select-Object -Skip $logLinesBefore | Where-Object { $_ -match $pattern } | Select-Object -First 1)
            $script:hit.Count -gt 0
        } 'log do Worker com classe de falha transitória/ambiente' 60 1000
        $cls = [regex]::Match($script:hit[0], $pattern).Groups[1].Value
        $quarantine = [int](Query-Origin 'SELECT COUNT(*) FROM consolidation_outbox WHERE status = 2')
        $signals.workerReadyStatus = $script:wr.Status; $signals.workerReadyBody = $script:wr.Body
        $signals.consolidationApiReadyStatus = $script:ar.Status; $signals.consolidationApiReadyBody = $script:ar.Body
        $signals.consolidationGetStatus = $g.Status; $signals.consolidationGetCode = $g.Code
        $signals.workerLogFailureClass = $cls; $signals.outboxQuarantined = $quarantine
        $signalsOk = ($script:wr.Status -eq 503) -and ($script:ar.Status -eq 503) -and ($g.Status -eq 503 -and $g.Code -eq 'dependency_unavailable') -and $cls -and $quarantine -eq 0
    }
    $Res.during.interruption = $signals
    $Res.during.interruptedAtUtc = Format-Utc $interruptedAt
    Add-Event 'interrupção comprovada' (($signals.GetEnumerator() | ForEach-Object { "$($_.Key)=$($_.Value)" }) -join '; ')
    $apiReadyBefore = (Invoke-Http 'GET' "$ApiUrl/health/ready").Status
    Add-Criterion 'sinais_de_falha' 'sinais de falha comprovados antes da janela e readiness da escrita 200' ([bool]$signalsOk -and $apiReadyBefore -eq 200) "sinais=$([bool]$signalsOk); readiness da API de lançamentos=$apiReadyBefore"
    if (-not $signalsOk -or $apiReadyBefore -ne 200) { throw [DemoAbort]::new('Os sinais de falha não foram comprovados; a janela não é executada.') }

    # ---------- janela da falha ----------
    $windowStart = Get-Utc
    $Res.during.windowStartUtc = Format-Utc $windowStart
    Add-Event 'janela da falha: início' 'K = 10 lançamentos novos, um a um, por HTTP'
    foreach ($op in $WindowOps) { $null = Send-Op $op $tokens }
    $windowEnd = Get-Utc
    Add-Event 'janela da falha: último lançamento concluído' "$(@($WindowOps | Where-Object { $_.Status -eq 201 }).Count) de $($WindowOps.Count) com 201"
    $status201 = @($WindowOps | Where-Object { $_.Status -eq 201 }).Count
    $Res.during.operations = @($WindowOps | ForEach-Object { [ordered]@{ key = $_.Key; account = $_.Account; type = $_.Type; amount = $_.Amount; startUtc = $_.StartUtc; endUtc = $_.EndUtc; status = $_.Status; movementId = $_.MovementId; location = $_.Location } })
    $Res.during.created201 = "$status201 de $($WindowOps.Count)"
    Add-Criterion 'escrita_durante_a_falha' 'escrita 10 de 10 com 201 durante a falha' ($status201 -eq 10) "$status201 de 10 com 201"

    $winIds = @($WindowOps | Where-Object { $_.MovementId } | ForEach-Object { $_.MovementId })
    $inList = Sql-List $winIds
    $movFound = if ($winIds.Count) { [int](Query-Origin "SELECT COUNT(*) FROM movements WHERE id IN ($inList)") } else { 0 }
    $outRows = @(if ($winIds.Count) { Split-Lines (Query-Origin "SELECT event_id, status FROM consolidation_outbox WHERE event_id IN ($inList)") })
    $outPending = @($outRows | Where-Object { $_ -match '\|0$' }).Count
    $Res.during.originMovementsFound = $movFound; $Res.during.originOutboxRows = $outRows.Count; $Res.during.originOutboxPending = $outPending
    Add-Criterion 'persistencia_na_origem' 'os 10 movimentos e as 10 linhas da Outbox (status 0) existem na origem durante a falha' ($movFound -eq 10 -and $outRows.Count -eq 10 -and $outPending -eq 10) "movimentos=$movFound, outbox=$($outRows.Count), pendentes=$outPending"

    $projDuring = Invoke-PsqlOrNull $ConsContainer $ConsDb "SELECT COUNT(*) FROM processed_events WHERE event_id IN ($inList)"
    $reads1 = Read-AllConsolidation $day $tokens
    $Res.during.consolidationDuringFailure = Summarize-Reads $reads1
    $pos = Invoke-Http 'GET' ("$ApiUrl/api/v1/accounts/$A1/position?asOf=" + (Format-Utc (Get-Utc))) @{ Authorization = "Bearer $($tokens[$CA])" }
    $posJson = ConvertFrom-Body $pos
    $expectedAll = Get-Expected ($InitialOps + $WindowOps)
    $Res.during.originPositionA1 = [ordered]@{ status = $pos.Status; balance = Get-Prop $posJson 'balance'; movementCount = Get-Prop $posJson 'movementCount'; expectedBalance = $expectedAll.account[$A1].net; expectedCount = $expectedAll.account[$A1].count; note = 'posição por instante lida na origem: prova da escrita, não da projeção' }
    Add-Criterion 'posicao_na_origem_durante' 'posição por instante na origem responde 200 com os lançamentos da janela' ($pos.Status -eq 200 -and (Test-Money (Get-Prop $posJson 'balance') $expectedAll.account[$A1].net) -and (Get-Prop $posJson 'movementCount') -eq $expectedAll.account[$A1].count) "status=$($pos.Status) saldo=$(Get-Prop $posJson 'balance') esperado=$($expectedAll.account[$A1].net)"
    $statusDuring = (Invoke-Admin @('--status') $envCons).Json
    $Res.during.adminStatus = [ordered]@{ originState = Get-Prop $statusDuring.origin 'state'; originPending = Get-Prop $statusDuring.origin 'pending'; originQuarantined = Get-Prop $statusDuring.origin 'quarantined'; projectionState = Get-Prop $statusDuring.projection 'state'; projectionProcessedEvents = Get-Prop $statusDuring.projection 'processedEvents' }

    if ($Scenario -eq 'Workers') {
        $inProj = [int]$projDuring
        $Res.during.projectionContainsWindowIds = "$inProj de 10"
        Add-Criterion 'ausencia_na_projecao' 'nenhum dos 10 ids em processed_events durante a falha (consulta direta)' ($inProj -eq 0) "$inProj de 10 presentes"
        $expWin = @{ $A1 = 4; $A2 = 3; $B1 = 3; $CA = 7; $CB = 3 }
        $bad = @(); foreach ($k in $reads1.Keys) { $id = ($k -split ':')[1]; $g = $reads1[$k]; if ($g.Status -ne 200 -or $g.Freshness -ne 'stale' -or $g.Unresolved -ne $expWin[$id]) { $bad += "$k status=$($g.Status) freshness=$($g.Freshness) unresolved=$($g.Unresolved) esperado=$($expWin[$id])" } }
        $liveCons = (Invoke-Http 'GET' "$ConsUrl/health/live").Status
        $Res.during.consolidationApiLiveStatus = $liveCons
        Add-Criterion 'consulta_durante_a_falha' 'API do consolidado segue no ar: 200 stale com unresolvedEvents = lançamentos da janela por conta e por cliente' ($bad.Count -eq 0 -and $liveCons -eq 200) $(if ($bad.Count) { $bad -join '; ' } else { 'A1=4, A2=3, B1=3, cliente A=7, cliente B=3; todos stale' })
    } else {
        $Res.during.projectionContainsWindowIds = $(if ($null -eq $projDuring) { 'inacessível (não é zero)' } else { "$projDuring de 10" })
        $Res.during.projectionPortOpen = Test-PortOpen $DedicatedPort
        Add-Criterion 'ausencia_na_projecao' 'projeção inacessível durante a falha (registrada como inacessível, não como zero)' ($null -eq $projDuring -and -not $Res.during.projectionPortOpen) "consulta direta falhou=$($null -eq $projDuring); porta aberta=$($Res.during.projectionPortOpen)"
        $bad = @(); foreach ($k in $reads1.Keys) { $g = $reads1[$k]; if ($g.Status -ne 503 -or $g.Code -ne 'dependency_unavailable') { $bad += "$k status=$($g.Status) code=$($g.Code)" } }
        Add-Criterion 'consulta_durante_a_falha' 'consultas ao consolidado respondem 503 dependency_unavailable (nunca saldo zero)' ($bad.Count -eq 0) $(if ($bad.Count) { $bad -join '; ' } else { '5 consultas: 503 dependency_unavailable' })
    }
    $windowEndStamp = Get-Utc
    $Res.during.windowEndUtc = Format-Utc $windowEnd
    Add-Event 'verificações durante a falha concluídas' 'a restauração é o passo seguinte'

    # ---------- retomada: restaura só o que foi interrompido ----------
    $restoreAt = Get-Utc
    $Res.after.restoreCommandIssuedAtUtc = Format-Utc $restoreAt
    if ($Scenario -eq 'Workers') {
        $workerPid2 = (Start-Own 'worker-2' $Bin.Worker $WorkerUrl $envCons).Id
        Add-Event 'Worker restaurado (novo processo, mesmos binários e configuração)' "PID $workerPid2"
    } else {
        docker start $DedicatedName *> $null
        if ($LASTEXITCODE -ne 0) { throw "docker start falhou para $DedicatedName." }
        Add-Event 'container dedicado da projeção religado' $DedicatedName
    }
    $convergedAt = $null
    $converged = $false
    $deadline = (Get-Date).AddSeconds(120)
    while ((Get-Date) -lt $deadline) {
        try {
            $pend = Get-OriginPending
            $inProjNow = [int](Query-Proj "SELECT COUNT(*) FROM processed_events WHERE event_id IN ($inList)")
            if ($pend -eq 0 -and $inProjNow -eq 10) { $converged = $true; $convergedAt = Get-Utc; break }
        } catch { }
        Start-Sleep -Milliseconds 500
    }
    $secs = if ($converged) { [math]::Round(($convergedAt - $restoreAt).TotalSeconds, 2) } else { $null }
    $Res.after.convergedAtUtc = $(if ($converged) { Format-Utc $convergedAt } else { $null })
    $Res.after.secondsFromRestoreToConvergence = $secs
    Add-Criterion 'convergencia_no_prazo' 'pending = 0 e os 10 ids em processed_events em até 120 s desde a restauração' $converged $(if ($converged) { "$secs s" } else { 'não convergiu em 120 s' })
    if (-not $converged) { throw [DemoAbort]::new('Sem convergência dentro do prazo.') }
    Add-Event 'convergência observada' "$secs s depois da restauração"

    # ---------- reconciliação ----------
    $allOps = $InitialOps + $WindowOps
    $allIds = @($allOps | ForEach-Object { $_.MovementId } | Sort-Object)
    $movIds = @(Split-Lines (Query-Origin 'SELECT id FROM movements ORDER BY 1') | Sort-Object)
    $outIds = @(Split-Lines (Query-Origin 'SELECT event_id FROM consolidation_outbox WHERE status = 1 ORDER BY 1') | Sort-Object)
    $outAll = [int](Query-Origin 'SELECT COUNT(*) FROM consolidation_outbox')
    $prIds = @(Split-Lines (Query-Proj 'SELECT event_id FROM processed_events ORDER BY 1') | Sort-Object)
    $dupes = @($movIds | Group-Object | Where-Object Count -gt 1).Count + @($prIds | Group-Object | Where-Object Count -gt 1).Count
    $setsOk = ($movIds.Count -eq 16) -and (($movIds -join ',') -eq ($allIds -join ',')) -and (($outIds -join ',') -eq ($allIds -join ',')) -and (($prIds -join ',') -eq ($allIds -join ',')) -and $outAll -eq 16 -and $dupes -eq 0

    $originRows = @{}; foreach ($l in Split-Lines (Query-Origin "SELECT account_id, COALESCE(SUM(amount) FILTER (WHERE type = 1), 0), COALESCE(SUM(amount) FILTER (WHERE type = 2), 0), COUNT(*) FROM movements GROUP BY account_id")) { $f = $l -split '\|'; $originRows[$f[0]] = @([decimal]::Parse($f[1], [Globalization.CultureInfo]::InvariantCulture), [decimal]::Parse($f[2], [Globalization.CultureInfo]::InvariantCulture), [int]$f[3]) }
    $projRows = @{}; foreach ($l in Split-Lines (Query-Proj 'SELECT account_id, SUM(credits), SUM(debits), SUM(movement_count) FROM daily_account_consolidation GROUP BY account_id')) { $f = $l -split '\|'; $projRows[$f[0]] = @([decimal]::Parse($f[1], [Globalization.CultureInfo]::InvariantCulture), [decimal]::Parse($f[2], [Globalization.CultureInfo]::InvariantCulture), [int]$f[3]) }
    $valueProblems = @()
    foreach ($a in $A1, $A2, $B1) {
        $e = $expectedAll.account[$a]
        foreach ($pair in @(@('origem', $originRows), @('projeção', $projRows))) {
            $row = $pair[1][$a]
            if (-not $row -or $row[0] -ne $e.credits -or $row[1] -ne $e.debits -or $row[2] -ne $e.count) { $valueProblems += "$($pair[0]) $a diferente do esperado" }
        }
    }
    $Res.after.reconciliation = [ordered]@{ movementsInOrigin = $movIds.Count; outboxProcessed = $outIds.Count; outboxTotal = $outAll; processedEvents = $prIds.Count; duplicates = $dupes; setsEqual = $setsOk
        expectedPerAccount = [ordered]@{ A1 = $expectedAll.account[$A1]; A2 = $expectedAll.account[$A2]; B1 = $expectedAll.account[$B1] }; valueProblems = $valueProblems }
    Add-Criterion 'reconciliacao_exata' 'conjuntos (movements = Outbox processada = processed_events = 16 ids) e valores por conta (origem = projeção = esperado do script)' ($setsOk -and $valueProblems.Count -eq 0) "movimentos=$($movIds.Count) outbox=$($outIds.Count) processed=$($prIds.Count) duplicados=$dupes; $($valueProblems -join '; ')"

    $reads2 = Read-AllConsolidation $day $tokens
    $probs2 = @(Compare-Consolidation $reads2 $expectedAll 'complete')
    $Res.after.consolidation = Summarize-Reads $reads2
    $statusAfter = (Invoke-Admin @('--status') $envCons).Json
    $Res.after.adminStatus = [ordered]@{ originPending = Get-Prop $statusAfter.origin 'pending'; originQuarantined = Get-Prop $statusAfter.origin 'quarantined'; projectionPhase = Get-Prop $statusAfter.projection 'phase'; projectionProcessedEvents = Get-Prop $statusAfter.projection 'processedEvents' }
    Add-Criterion 'consolidado_completo' 'consolidado por conta e por cliente: valores esperados, freshness complete, unresolvedEvents 0, failedEvents 0' ($probs2.Count -eq 0) $(if ($probs2.Count) { $probs2 -join '; ' } else { '5 consultas complete com os valores esperados' })

    # ---------- idempotência ----------
    $rep = $WindowOps[0]
    $before = [ordered]@{ movements = [int](Query-Origin 'SELECT COUNT(*) FROM movements'); outbox = [int](Query-Origin 'SELECT COUNT(*) FROM consolidation_outbox') }
    $h = @{ Authorization = "Bearer $($tokens[$rep.Customer])"; 'Idempotency-Key' = $rep.Key }
    $rr = Invoke-Http 'POST' "$ApiUrl/api/v1/accounts/$($rep.Account)/movements" $h $rep.Body
    $rj = ConvertFrom-Body $rr
    $afterCounts = [ordered]@{ movements = [int](Query-Origin 'SELECT COUNT(*) FROM movements'); outbox = [int](Query-Origin 'SELECT COUNT(*) FROM consolidation_outbox') }
    $reads3 = Read-AllConsolidation $day $tokens
    $probs3 = @(Compare-Consolidation $reads3 $expectedAll 'complete')
    $Res.after.idempotency = [ordered]@{ operation = $rep.Key; status = $rr.Status; sameMovementId = ([string](Get-Prop $rj 'movementId') -eq $rep.MovementId); countsBefore = $before; countsAfter = $afterCounts; consolidationUnchanged = ($probs3.Count -eq 0) }
    Add-Criterion 'idempotencia' 'repetir uma operação da janela: 200 (não 201), mesmo movementId, nenhum movimento ou evento novo, consolidado inalterado' ($rr.Status -eq 200 -and $Res.after.idempotency.sameMovementId -and $afterCounts.movements -eq $before.movements -and $afterCounts.outbox -eq $before.outbox -and $probs3.Count -eq 0) "status=$($rr.Status) mesmoId=$($Res.after.idempotency.sameMovementId) movimentos $($before.movements)->$($afterCounts.movements) outbox $($before.outbox)->$($afterCounts.outbox)"
    $Res.after.writeReadiness = (Invoke-Http 'GET' "$ApiUrl/health/ready").Status
    $Res.after.writeApiSameProcess = [ordered]@{ pid = $apiProc.Id; stillRunning = (-not $apiProc.HasExited); restarted = $false }
    $exitCode = if (@($script:Criteria | Where-Object { -not $_.passed }).Count -eq 0) { 0 } else { 10 }
}
catch [DemoAbort] {
    Add-Event 'demonstração interrompida por critério' $_.Exception.Message
    $exitCode = 10
}
catch {
    $invalidReason = "$($_.Exception.GetType().Name): $($_.Exception.Message)"
    Add-Event 'execução inválida' $invalidReason
    $exitCode = 30
}
finally {
    # ---------- limpeza: só o que esta execução criou, pela lista exata ----------
    foreach ($name in @($script:Procs.Keys)) {
        try { Stop-Own $name; Mark-Removed $name 'processo encerrado por PID' } catch { Write-Host "falha ao encerrar $name" }
    }
    try {
        if (Get-Command docker -ErrorAction SilentlyContinue) {
            if ($script:Resources | Where-Object { $_.name -eq $SrcDb }) { Invoke-Psql $OriginContainer 'postgres' "DROP DATABASE IF EXISTS `"$SrcDb`" WITH (FORCE)" | Out-Null; Mark-Removed $SrcDb 'DROP DATABASE' }
            if ($UsesDedicated) {
                if ($script:Resources | Where-Object { $_.name -eq $DedicatedName }) { docker rm -f $DedicatedName *> $null; Mark-Removed $DedicatedName 'docker rm -f (container dedicado)'; Mark-Removed $ConsDb 'removido com o container dedicado' }
            } elseif ($script:Resources | Where-Object { $_.name -eq $ConsDb }) { Invoke-Psql $ProjectionContainer 'postgres' "DROP DATABASE IF EXISTS `"$ConsDb`" WITH (FORCE)" | Out-Null; Mark-Removed $ConsDb 'DROP DATABASE' }
        }
    } catch { Write-Host "limpeza incompleta: $($_.Exception.Message)" }
    foreach ($n in $EnvNames) { [Environment]::SetEnvironmentVariable($n, $SavedEnv[$n]) }
    if ($containersBefore) {
        $o2 = Get-DockerInfo $OriginContainer; $c2 = Get-DockerInfo $ProjectionContainer
        $same = $o2 -and $c2 -and $o2.Id -eq $containersBefore.origin -and $c2.Id -eq $containersBefore.projection -and $o2.State -eq 'healthy' -and $c2.State -eq 'healthy'
        $Res.after.containersUnchanged = [ordered]@{ sameIds = [bool]$same; originId = $containersBefore.origin.Substring(0, 12); projectionId = $containersBefore.projection.Substring(0, 12); stateAfter = "$($o2.State)/$($c2.State)" }
        if ($exitCode -ne 30) { Add-Criterion 'containers_do_projeto' 'containers do docker-compose.yml com os mesmos ids e healthy ao fim' ([bool]$same) "origem=$($o2.State), projeção=$($c2.State)" }
        if ($exitCode -eq 0 -and -not $same) { $exitCode = 10 }
    }
    if ($exitCode -eq 30) { Write-Results $false $invalidReason 30 } else { Write-Results $true '' $exitCode }
}
exit $exitCode
