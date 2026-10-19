namespace FinancialTransactions.Consolidation;

/// <summary>
/// Dia de negócio (H15): data civil do instante na zona nomeada <c>America/Sao_Paulo</c>, não um offset fixo. Início às 00:00 local,
/// fim exclusivo às 00:00 local do dia seguinte. A conversão usa os dados de fuso da máquina e vale para qualquer data histórica.
/// </summary>
public static class BusinessDay
{
    public const string TimeZoneId = "America/Sao_Paulo";

    private static readonly Lazy<TimeZoneInfo> Zone = new(() => TimeZoneInfo.FindSystemTimeZoneById(TimeZoneId));

    public static TimeZoneInfo TimeZone => Zone.Value;

    /// <summary>Dia de negócio do instante informado.</summary>
    public static DateOnly Of(DateTimeOffset instant)
    {
        var local = TimeZoneInfo.ConvertTime(instant, TimeZone);
        return DateOnly.FromDateTime(local.DateTime);
    }

    /// <summary>Dia de negócio corrente.</summary>
    public static DateOnly Today(TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        return Of(clock.GetUtcNow());
    }

    /// <summary>Primeiro instante (UTC) do dia: 00:00 local. Se a hora local não existe (transição de horário de verão à meia-noite), é o fim da lacuna.</summary>
    public static DateTimeOffset StartUtc(DateOnly day)
    {
        var local = day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        var offset = TimeZone.GetUtcOffset(local);
        return new DateTimeOffset(local.Ticks - offset.Ticks, TimeSpan.Zero);
    }

    /// <summary>Fim exclusivo do dia (UTC): 00:00 local do dia seguinte.</summary>
    public static DateTimeOffset EndExclusiveUtc(DateOnly day) => StartUtc(day.AddDays(1));

    /// <summary>Verifica se a zona nomeada existe nesta máquina; usado na validação de inicialização.</summary>
    public static bool IsTimeZoneAvailable(out string error)
    {
        try
        {
            _ = TimeZone;
            error = string.Empty;
            return true;
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            error = $"Zona de fuso '{TimeZoneId}' indisponível nesta máquina ({ex.GetType().Name}). Instale os dados de fuso (tzdata/ICU).";
            return false;
        }
    }
}
