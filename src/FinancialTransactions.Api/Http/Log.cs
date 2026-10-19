namespace FinancialTransactions.Api.Http;

internal static partial class Log
{
    [LoggerMessage(EventId = 1000, Level = LogLevel.Error, Message = "Falha inesperada ao processar {Method} {Path}.")]
    public static partial void UnhandledException(ILogger logger, Exception exception, string method, string path);

    [LoggerMessage(EventId = 2000, Level = LogLevel.Warning, Message = "Conflito UNIQUE na conta {AccountId} (chave SHA-256 {IdempotencyKeyHash}...), mas a releitura não encontrou a linha; respondido retry_later.")]
    public static partial void IdempotencyRereadMiss(ILogger logger, Guid accountId, string idempotencyKeyHash);
}
