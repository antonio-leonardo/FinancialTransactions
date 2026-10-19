using Microsoft.Extensions.Logging;

namespace FinancialTransactions.Consolidation;

/// <summary>Sink de eventos do consumidor. Em produção só registra log; a fixture de teste coleta os eventos para observar o laço real.</summary>
public interface IConsolidationDiagnostics
{
    /// <summary>Uma tentativa do laço falhou, com a classe atribuída (transitória, ambiente ou do evento).</summary>
    void AttemptFailed(FailureClass failureClass, string failureType);

    void BatchApplied(int applied, int alreadyApplied);

    void PhaseChanged(string phase);

    void WaitingForBootstrap();

    void ReconciliationFailed(string firstDeviation);

    void EventQuarantined(long sequence);
}

public sealed partial class LoggingConsolidationDiagnostics(ILogger<LoggingConsolidationDiagnostics> logger) : IConsolidationDiagnostics
{
    public void AttemptFailed(FailureClass failureClass, string failureType) => LogAttemptFailed(logger, failureClass, failureType);

    public void BatchApplied(int applied, int alreadyApplied) => LogBatchApplied(logger, applied, alreadyApplied);

    public void PhaseChanged(string phase) => LogPhaseChanged(logger, phase);

    public void WaitingForBootstrap()
    {
        // O aviso "aguardando bootstrap" tem limite de um por minuto e é emitido pelo laço.
    }

    public void ReconciliationFailed(string firstDeviation) => LogReconciliationFailed(logger, firstDeviation);

    public void EventQuarantined(long sequence) => LogEventQuarantined(logger, sequence);

    [LoggerMessage(EventId = 4000, Level = LogLevel.Debug, Message = "Tentativa do consumidor falhou (classe {FailureClass}, {FailureType}).")]
    private static partial void LogAttemptFailed(ILogger logger, FailureClass failureClass, string failureType);

    [LoggerMessage(EventId = 4001, Level = LogLevel.Information, Message = "Lote aplicado: {Applied} somados, {AlreadyApplied} já aplicados.")]
    private static partial void LogBatchApplied(ILogger logger, int applied, int alreadyApplied);

    [LoggerMessage(EventId = 4002, Level = LogLevel.Information, Message = "Fase da consolidação: {Phase}.")]
    private static partial void LogPhaseChanged(ILogger logger, string phase);

    [LoggerMessage(EventId = 4003, Level = LogLevel.Error, Message = "Reconciliação do conjunto inicial divergente: {FirstDeviation}.")]
    private static partial void LogReconciliationFailed(ILogger logger, string firstDeviation);

    [LoggerMessage(EventId = 4004, Level = LogLevel.Error, Message = "Evento {Sequence} em quarentena depois de tentativas esgotadas.")]
    private static partial void LogEventQuarantined(ILogger logger, long sequence);
}
