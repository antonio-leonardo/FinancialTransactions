namespace FinancialTransactions.Api.Http;

internal static partial class Log
{
    [LoggerMessage(EventId = 1000, Level = LogLevel.Error, Message = "Falha inesperada ao processar {Method} {Path}.")]
    public static partial void UnhandledException(ILogger logger, Exception exception, string method, string path);

    [LoggerMessage(EventId = 2001, Level = LogLevel.Warning, Message = "Dependência indisponível ao processar {Method} {Path} (traceId {TraceId}); respondido 503.")]
    public static partial void DependencyUnavailable(ILogger logger, string method, string path, string traceId);

    [LoggerMessage(EventId = 3000, Level = LogLevel.Information, Message = "Movimento {MovementId} registrado na conta {AccountId}.")]
    public static partial void MovementRegistered(ILogger logger, Guid movementId, Guid accountId);

    [LoggerMessage(EventId = 2002, Level = LogLevel.Warning, Message = "Readiness: banco não respondeu ({FailureType}).")]
    public static partial void ReadinessFailed(ILogger logger, string failureType);

    [LoggerMessage(EventId = 2000, Level = LogLevel.Warning, Message = "Conflito UNIQUE na conta {AccountId} (chave SHA-256 {IdempotencyKeyHash}...), mas a releitura não encontrou a linha; respondido retry_later.")]
    public static partial void IdempotencyRereadMiss(ILogger logger, Guid accountId, string idempotencyKeyHash);
}
