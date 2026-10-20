using System.Collections.Concurrent;
using System.Net;
using System.Text;
using FinancialTransactions.LoadTest;

namespace FinancialTransactions.UnitTests;

/// <summary>Relógio do sistema com um "congelamento" injetável: simula o laço de programação parado por um tempo, sem alterar o relógio da máquina.</summary>
internal sealed class StallableTimeProvider : TimeProvider
{
    private long _skewTicks;

    public override long GetTimestamp() => System.GetTimestamp() + Interlocked.Read(ref _skewTicks);

    public override DateTimeOffset GetUtcNow() => System.GetUtcNow();

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) => System.CreateTimer(callback, state, dueTime, period);

    public void Stall(TimeSpan duration) => Interlocked.Add(ref _skewTicks, (long)(duration.TotalSeconds * TimestampFrequency));
}

/// <summary>Servidor local sintético: o comportamento de cada slot é decidido pelo teste, sem rede e sem o produto.</summary>
internal sealed class SyntheticServer(Func<int, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
{
    public int Calls;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref Calls);
        var path = request.RequestUri?.AbsolutePath ?? string.Empty;
        var index = int.Parse(path[(path.LastIndexOf('/') + 1)..], System.Globalization.CultureInfo.InvariantCulture);
        return await respond(index, cancellationToken).ConfigureAwait(false);
    }

    public static HttpResponseMessage Text(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
}

/// <summary>Controles do gerador de chegadas abertas (C1), executados contra o servidor sintético, nunca contra o produto.</summary>
public sealed class LoadGeneratorTests
{
    private static readonly Uri Base = new("http://127.0.0.1:5999/");
    private static readonly HashSet<int> Success200 = [200];

    private static List<SlotSpec> Slots(int count, Func<int, BodyVerdict>? validate = null) =>
        Enumerable.Range(0, count)
            .Select(i => new SlotSpec(
                "account",
                i % 2,
                "g" + i,
                Success200,
                () => new HttpRequestMessage(HttpMethod.Get, new Uri(Base, "/slot/" + i)),
                (_, _) => validate?.Invoke(i) ?? BodyVerdict.Ok("complete")))
            .ToList();

    private static OpenLoopSettings Settings(double rate, int tolerance = 1000, int deadline = 1000, int transport = 2000, int inFlight = 128) =>
        new(rate, TimeSpan.FromMilliseconds(tolerance), TimeSpan.FromMilliseconds(deadline), TimeSpan.FromMilliseconds(transport), inFlight);

    private static async Task<OpenLoopResult> RunAsync(SyntheticServer server, IReadOnlyList<SlotSpec> slots, OpenLoopSettings settings, TimeProvider? time = null, CancellationToken cancellationToken = default)
    {
        using var client = new HttpClient(server) { Timeout = Timeout.InfiniteTimeSpan };
        return await new OpenLoopRunner(client, time ?? TimeProvider.System, settings).RunAsync(slots, cancellationToken);
    }

    [Fact(DisplayName = "LoadGenerator_ImmediateResponses_AreAllUsefulOnTime_AndTheRunIsApproved")]
    public async Task ImmediateResponses()
    {
        using var server = new SyntheticServer((_, _) => Task.FromResult(SyntheticServer.Text(HttpStatusCode.OK, "{}")));
        var result = await RunAsync(server, Slots(60), Settings(600));
        var evaluation = Verdicts.Evaluate(result, requireProfile: false);

        Assert.Equal(60, evaluation.Total.Programmed);
        Assert.Equal(60, evaluation.Total.Useful);
        Assert.Equal(0, evaluation.Total.NotEmitted);
        Assert.Equal(RunOutcome.Approved, evaluation.Outcome);
        Assert.Equal(ExitCodes.Approved, ExitCodes.For(evaluation.Outcome, reconciliationOrResolutionFailed: false));
    }

    [Fact(DisplayName = "LoadGenerator_SlowResponses_DoNotReduceTheSchedule_TheyBecomeLateNotMissingSlots")]
    public async Task SlowResponsesDoNotReduceTheSchedule()
    {
        // Cada resposta leva 400 ms; um gerador de laço fechado levaria 40 × 0,4 s = 16 s e perderia slots. O aberto emite todos no prazo da agenda.
        using var server = new SyntheticServer(async (_, ct) =>
        {
            await Task.Delay(400, ct);
            return SyntheticServer.Text(HttpStatusCode.OK, "{}");
        });
        var started = DateTime.UtcNow;
        var result = await RunAsync(server, Slots(40), Settings(200, tolerance: 150, deadline: 100));
        var evaluation = Verdicts.Evaluate(result, requireProfile: false);

        Assert.Equal(40, evaluation.Total.Emitted);
        Assert.Equal(0, evaluation.Total.NotEmitted);
        Assert.Equal(40, evaluation.Total.Late); // 400 ms > prazo útil de 100 ms desde o slot
        Assert.Equal(0, evaluation.Total.Useful);
        Assert.True(result.Slots.Max(s => s.EmissionLatenessMs) < 150, "a emissão acompanhou a agenda, não a resposta anterior");
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(4));
        Assert.Equal(RunOutcome.Rejected, evaluation.Outcome); // gerador válido, perda de 100%
    }

    [Fact(DisplayName = "LoadGenerator_Http500_IsAnHttpErrorNotAUsefulResponse_AndTheRunIsRejected")]
    public async Task Http500IsNotUseful()
    {
        using var server = new SyntheticServer((_, _) => Task.FromResult(SyntheticServer.Text(HttpStatusCode.InternalServerError, "{}")));
        var evaluation = Verdicts.Evaluate(await RunAsync(server, Slots(30), Settings(300)), requireProfile: false);

        Assert.Equal(30, evaluation.Total.HttpErrors);
        Assert.Equal(0, evaluation.Total.Useful);
        Assert.Equal(RunOutcome.Rejected, evaluation.Outcome);
        Assert.Equal(ExitCodes.Rejected, ExitCodes.For(evaluation.Outcome, false));
    }

    [Fact(DisplayName = "LoadGenerator_200WithAnInvalidBody_IsNotUseful_AndAFinancialMismatchIsAnIntegrityFailure")]
    public async Task InvalidBodyIsNotUseful()
    {
        using var server = new SyntheticServer((_, _) => Task.FromResult(SyntheticServer.Text(HttpStatusCode.OK, "{}")));
        var evaluation = Verdicts.Evaluate(
            await RunAsync(server, Slots(30, _ => BodyVerdict.Bad("valor financeiro diferente do oracle", integrity: true)), Settings(300)),
            requireProfile: false);

        Assert.Equal(30, evaluation.Total.InvalidBodies);
        Assert.Equal(30, evaluation.Total.IntegrityFailures);
        Assert.Equal(0, evaluation.Total.Useful);
        Assert.Equal(RunOutcome.Rejected, evaluation.Outcome);
    }

    [Fact(DisplayName = "LoadGenerator_InsufficientInFlightCapacity_CountsTheNonEmittedSlots_AndInvalidatesTheRun")]
    public async Task InsufficientCapacityIsCountedAndInvalidates()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var server = new SyntheticServer(async (_, ct) =>
        {
            await release.Task.WaitAsync(ct);
            return SyntheticServer.Text(HttpStatusCode.OK, "{}");
        });
        var run = RunAsync(server, Slots(20), Settings(100, inFlight: 2));
        await Task.Delay(600);
        release.SetResult();
        var evaluation = Verdicts.Evaluate(await run, requireProfile: false);

        Assert.Equal(2, evaluation.Total.Emitted);
        Assert.Equal(18, evaluation.Total.NotEmitted);
        Assert.Equal(RunOutcome.Invalid, evaluation.Outcome); // a taxa não foi provada: nem aprovado nem reprovado
        Assert.Contains(evaluation.InvalidReasons, r => r.Contains("não emitido", StringComparison.Ordinal));
        Assert.Equal(ExitCodes.Invalid, ExitCodes.For(evaluation.Outcome, false));
        Assert.NotEqual(ExitCodes.Rejected, ExitCodes.Invalid);
    }

    [Fact(DisplayName = "LoadGenerator_NoSlots_IsInvalid_NeverApproved")]
    public async Task NoSamplesIsInvalid()
    {
        using var server = new SyntheticServer((_, _) => Task.FromResult(SyntheticServer.Text(HttpStatusCode.OK, "{}")));
        var evaluation = Verdicts.Evaluate(await RunAsync(server, [], Settings(50)), requireProfile: false);

        Assert.Equal(RunOutcome.Invalid, evaluation.Outcome);
        Assert.Contains(evaluation.InvalidReasons, r => r.Contains("nenhuma amostra", StringComparison.Ordinal));
        Assert.False(Verdicts.WithinBudget(0, 0));
    }

    [Fact(DisplayName = "LoadGenerator_Percentiles_UseCeilPTimesNOnTheSortedSample_KnownValues")]
    public void PercentileKnownValues()
    {
        var sample = Enumerable.Range(1, 100).Select(i => (double)i).ToList();

        Assert.Equal(50, Stats.Percentile(sample, 0.50));
        Assert.Equal(95, Stats.Percentile(sample, 0.95));
        Assert.Equal(99, Stats.Percentile(sample, 0.99));
        Assert.Null(Stats.Percentile([], 0.95));
    }

    [Fact(DisplayName = "LoadGenerator_Budget_150LossesIn3000Approve_151Reject_Boundary")]
    public async Task BudgetBoundary()
    {
        Assert.True(Verdicts.WithinBudget(3000, 2850));
        Assert.False(Verdicts.WithinBudget(3000, 2849));

        // Ponta a ponta: 3.000 slots a alta taxa; exatamente 150 (depois 151) NÃO ÚTEIS, em três categorias misturadas
        // (200 tardio, HTTP 500, 200 com corpo inválido). Tardias contam no denominador: retirá-las faria o 151.º passar.
        foreach (var (failures, expected) in new[] { (150, RunOutcome.Approved), (151, RunOutcome.Rejected) })
        {
            using var server = new SyntheticServer(async (i, ct) =>
            {
                if (i < failures && i % 3 == 0)
                {
                    await Task.Delay(1300, ct); // depois do prazo útil de 1.000 ms desde o slot
                }

                return SyntheticServer.Text(i < failures && i % 3 == 1 ? HttpStatusCode.InternalServerError : HttpStatusCode.OK, i < failures && i % 3 == 2 ? "invalido" : "{}");
            });
            var slots = Slots(3000, i => i < 3000 && i < failures && i % 3 == 2 ? BodyVerdict.Bad("corpo inválido") : BodyVerdict.Ok("complete"));
            var result = await RunAsync(server, slots, Settings(30_000, tolerance: 10_000, deadline: 1000, transport: 5000, inFlight: 3000));
            var evaluation = Verdicts.Evaluate(result, requireProfile: false);

            Assert.Equal(3000, evaluation.Total.Programmed);
            Assert.Equal(3000 - failures, evaluation.Total.Useful);
            Assert.True(evaluation.Total.Late > 0 && evaluation.Total.HttpErrors > 0 && evaluation.Total.InvalidBodies > 0, "as três categorias aparecem");
            Assert.Equal(expected, evaluation.Outcome);
        }
    }
    [Fact(DisplayName = "LoadGenerator_TransportTimeoutAfterAnIncomplete200_IsATransportFailure")]
    public async Task TimeoutAfterIncompleteBody()
    {
        using var server = new SyntheticServer((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new HangingStream()) }));
        var evaluation = Verdicts.Evaluate(await RunAsync(server, Slots(10), Settings(100, transport: 150)), requireProfile: false);

        Assert.Equal(10, evaluation.Total.Transport);
        Assert.Equal(0, evaluation.Total.Useful);
        Assert.Equal(RunOutcome.Rejected, evaluation.Outcome);
    }

    [Fact(DisplayName = "LoadGenerator_CancelledRun_IsInvalid_NeverACompletedWindow")]
    public async Task CancelledRunIsInvalid()
    {
        using var cancellation = new CancellationTokenSource();
        using var server = new SyntheticServer((_, _) => Task.FromResult(SyntheticServer.Text(HttpStatusCode.OK, "{}")));
        var run = RunAsync(server, Slots(500), Settings(100), cancellationToken: cancellation.Token);
        await Task.Delay(300);
        await cancellation.CancelAsync();
        var result = await run;
        var evaluation = Verdicts.Evaluate(result, requireProfile: false);

        Assert.True(result.Cancelled);
        Assert.Equal(RunOutcome.Invalid, evaluation.Outcome);
        Assert.Contains("ensaio cancelado", evaluation.InvalidReasons);
    }

    [Fact(DisplayName = "LoadGenerator_SlotLateBeyondTheTolerance_IsNotEmitted_AndNeverFiredInABurst")]
    public async Task LateSlotsAreNotBurstCompensated()
    {
        // O laço de programação "congela" por 500 ms no slot 10: os slots já vencidos além da tolerância (50 ms) não são emitidos nem recuperados em rajada.
        var clock = new StallableTimeProvider();
        var slots = Slots(40).Select((s, i) => i == 10
            ? s with
            {
                CreateRequest = () =>
                {
                    clock.Stall(TimeSpan.FromMilliseconds(500));
                    return s.CreateRequest();
                },
            }
            : s).ToList();
        using var server = new SyntheticServer((_, _) => Task.FromResult(SyntheticServer.Text(HttpStatusCode.OK, "{}")));
        var result = await RunAsync(server, slots, Settings(100, tolerance: 50), clock);
        var evaluation = Verdicts.Evaluate(result, requireProfile: false);

        Assert.Equal(11, evaluation.Total.Emitted);
        Assert.Equal(29, evaluation.Total.NotEmitted);
        Assert.Equal(11, server.Calls); // nenhuma rajada compensatória
        Assert.Equal(RunOutcome.Invalid, evaluation.Outcome);
    }

    [Fact(DisplayName = "LoadGenerator_RedirectIsNeverFollowed_AndInvalidatesTheRun")]
    public async Task RedirectIsRefused()
    {
        using var server = new SyntheticServer((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Redirect);
            response.Headers.Location = new Uri("http://example.invalid/outro");
            return Task.FromResult(response);
        });
        var result = await RunAsync(server, Slots(10), Settings(100));
        var evaluation = Verdicts.Evaluate(result, requireProfile: false);

        Assert.True(result.RedirectSeen);
        Assert.Equal(10, evaluation.Total.HttpErrors);
        Assert.Equal(RunOutcome.Invalid, evaluation.Outcome);
    }

    [Theory(DisplayName = "LoadGenerator_Safety_RefusesNonLoopbackHostsAndCredentialsInTheUrl")]
    [InlineData("http://example.com:5000")]
    [InlineData("http://10.0.0.5:5000")]
    [InlineData("https://127.0.0.1:5000")]
    [InlineData("http://user:pass@127.0.0.1:5000")]
    [InlineData("http://127.0.0.1:5000/api")]
    public void UrlsOutsideLoopbackAreRefused(string url)
    {
        Assert.False(ConsolidationOptions.TryLoopback(url, out _, out var error));
        Assert.NotEmpty(error);
    }

    [Theory(DisplayName = "LoadGenerator_Safety_AcceptsOnlyLoopbackBaseUrls")]
    [InlineData("http://127.0.0.1:5111")]
    [InlineData("http://localhost:5111")]
    public void LoopbackUrlsAreAccepted(string url) => Assert.True(ConsolidationOptions.TryLoopback(url, out _, out _));

    [Fact(DisplayName = "LoadGenerator_Safety_RefusesADatabaseThatDoesNotMatchTheOwnManifest")]
    public void DatabaseMustMatchTheManifest()
    {
        const string good = "Host=127.0.0.1;Port=15432;Database=ft_load_p08_x_src;Username=u;Password=p";
        Assert.True(ConsolidationOptions.CheckDatabase(good, "ft_load_p08_x_src", "origem", out _));
        Assert.False(ConsolidationOptions.CheckDatabase(good, "ft_load_p08_y_src", "origem", out var mismatch));
        Assert.Contains("manifesto", mismatch, StringComparison.Ordinal);
        Assert.False(ConsolidationOptions.CheckDatabase("Host=127.0.0.1;Database=financialtransactions;Username=u;Password=p", "financialtransactions", "origem", out _));
        Assert.False(ConsolidationOptions.CheckDatabase("Host=10.1.1.1;Database=ft_load_p08_x_src;Username=u;Password=p", "ft_load_p08_x_src", "origem", out _));
    }

    [Fact(DisplayName = "LoadGenerator_Mass_FormulasProduce100000MovementsOver30DaysAnd400Accounts_AndMatchTheSeedSql")]
    public void MassIsConsistentWithTheSeedSql()
    {
        var expected = Mass.Expected();

        Assert.Equal(Mass.Movements, expected.Values.Sum(t => t.Count));
        Assert.All(expected.Keys, k => Assert.InRange(k.Account, 1, Mass.Accounts));
        Assert.All(expected.Keys, k => Assert.InRange(k.Day, 1, Mass.Days));
        Assert.Equal(Mass.Accounts, expected.Keys.Select(k => k.Account).Distinct().Count());
        // As constantes das fórmulas aparecem no SQL do seed: a mudança de uma sem a outra é percebida aqui e na validação contra o banco.
        foreach (var literal in new[] { "n % 5 = 0", "(n - n / 5) * 37 + 11", "n * 13) % 7 < 4", "n * 7919 + 17", "n * 104729 + 12345", "2592000", "'2026-09-01 00:00:00-03'" })
        {
            Assert.Contains(literal, SeedSql.Movements, StringComparison.Ordinal);
        }
    }

    /// <summary>Corpo que começa e nunca termina: simula um 200 com corpo incompleto.</summary>
    private sealed class HangingStream : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
