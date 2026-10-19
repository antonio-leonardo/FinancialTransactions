using System.Net.Sockets;
using FinancialTransactions.Consolidation;
using Npgsql;

namespace FinancialTransactions.UnitTests;

/// <summary>Classificação das falhas do consumidor (4.3): transitória, cancelamento do host, ambiente e evento.</summary>
public sealed class ConsolidationFailureClassifierTests
{
    private static PostgresException Postgres(string sqlState, string? constraint = null) =>
        new(messageText: "x", severity: "ERROR", invariantSeverity: "ERROR", sqlState: sqlState, constraintName: constraint);

    private static FailureClass Classify(Exception exception) => FailureClassifier.Classify(exception, CancellationToken.None);

    [Theory(DisplayName = "Classifier_PostgresEnvironmentStates_AreEnvironment")]
    [InlineData("42P01")] // tabela inexistente
    [InlineData("42703")] // coluna inexistente
    [InlineData("28P01")] // autenticação
    [InlineData("28000")]
    [InlineData("3D000")] // banco inexistente
    [InlineData("53300")] // recursos
    [InlineData("53200")]
    [InlineData("22P02")] // 22 fora do contexto de um valor do evento
    [InlineData("22007")]
    [InlineData("XX000")] // desconhecido
    public void PostgresEnvironmentStatesAreEnvironment(string sqlState)
    {
        Assert.Equal(FailureClass.Environment, Classify(Postgres(sqlState)));
    }

    [Fact(DisplayName = "Classifier_NumericOverflow_IsEvent")]
    public void NumericOverflowIsEvent()
    {
        Assert.Equal(FailureClass.Event, Classify(Postgres(PostgresErrorCodes.NumericValueOutOfRange)));
    }

    [Theory(DisplayName = "Classifier_CheckViolationOnClosedListConstraint_IsEvent")]
    [InlineData("ck_daily_account_consolidation_credits")]
    [InlineData("ck_daily_account_consolidation_debits")]
    [InlineData("ck_daily_account_consolidation_movement_count")]
    [InlineData("ck_daily_account_consolidation_net")]
    public void CheckViolationOnClosedListConstraintIsEvent(string constraint)
    {
        Assert.Equal(FailureClass.Event, Classify(Postgres(PostgresErrorCodes.CheckViolation, constraint)));
    }

    [Theory(DisplayName = "Classifier_ConstraintViolationOutsideTheList_IsEnvironment")]
    [InlineData(PostgresErrorCodes.CheckViolation, "ck_consolidation_bootstrap_phase")]
    [InlineData(PostgresErrorCodes.CheckViolation, null)]
    [InlineData(PostgresErrorCodes.UniqueViolation, "pk_processed_events")]
    [InlineData(PostgresErrorCodes.NotNullViolation, null)]
    [InlineData(PostgresErrorCodes.ForeignKeyViolation, "fk_qualquer")]
    public void ConstraintViolationOutsideTheListIsEnvironment(string sqlState, string? constraint)
    {
        Assert.Equal(FailureClass.Environment, Classify(Postgres(sqlState, constraint)));
    }

    [Theory(DisplayName = "Classifier_ShutdownAndSerializationStates_AreTransient")]
    [InlineData("57P01")]
    [InlineData("57P03")]
    [InlineData("40001")]
    [InlineData("40P01")]
    [InlineData("08006")]
    public void ShutdownAndSerializationStatesAreTransient(string sqlState)
    {
        Assert.Equal(FailureClass.Transient, Classify(Postgres(sqlState)));
    }

    [Fact(DisplayName = "Classifier_Infrastructure_IsTransient")]
    public void InfrastructureIsTransient()
    {
        Assert.Equal(FailureClass.Transient, Classify(new TimeoutException()));
        Assert.Equal(FailureClass.Transient, Classify(new SocketException()));
        Assert.Equal(FailureClass.Transient, Classify(new NpgsqlException("x", new SocketException())));
    }

    [Fact(DisplayName = "Classifier_HostTokenSignaledCancellation_IsHostCancellation")]
    public void HostTokenSignaledCancellationIsHostCancellation()
    {
        using var host = new CancellationTokenSource();
        host.Cancel();

        Assert.Equal(FailureClass.HostCancellation, FailureClassifier.Classify(new OperationCanceledException(host.Token), host.Token));
    }

    [Fact(DisplayName = "Classifier_BudgetCancellationWithHostTokenNotSignaled_IsTransient")]
    public void BudgetCancellationWithHostTokenNotSignaledIsTransient()
    {
        using var budget = new CancellationTokenSource();
        budget.Cancel();
        using var host = new CancellationTokenSource();

        Assert.Equal(FailureClass.Transient, FailureClassifier.Classify(new OperationCanceledException(budget.Token), host.Token));
        Assert.Equal(FailureClass.Transient, FailureClassifier.Classify(new TaskCanceledException(), host.Token));
    }

    [Fact(DisplayName = "Classifier_OwnEventValidationAndOverflow_AreEvent")]
    public void OwnEventValidationAndOverflowAreEvent()
    {
        Assert.Equal(FailureClass.Event, Classify(new InvalidEventException("type inválido (9).")));
        Assert.Equal(FailureClass.Event, Classify(new OverflowException()));
    }

    [Fact(DisplayName = "Classifier_UnknownException_IsEnvironment_NeverEvent")]
    public void UnknownExceptionIsEnvironmentNeverEvent()
    {
        Assert.Equal(FailureClass.Environment, Classify(new InvalidOperationException("falha injetada")));
        Assert.Equal(FailureClass.Environment, Classify(new NotSupportedException()));
    }

    [Fact(DisplayName = "Classifier_PostgresExceptionWrappedByAnotherException_IsStillRecognized")]
    public void PostgresExceptionWrappedByAnotherExceptionIsStillRecognized()
    {
        var wrapped = new InvalidOperationException("wrap", Postgres("42P01"));

        Assert.Equal(FailureClass.Environment, Classify(wrapped));
    }

    [Fact(DisplayName = "Sanitize_NeverCarriesConnectionStringsOrRawMessages")]
    public void SanitizeNeverCarriesConnectionStringsOrRawMessages()
    {
        var secret = "Host=10.0.0.1;Password=segredo";
        var postgres = new PostgresException(secret, "ERROR", "ERROR", "42P01", constraintName: "ck_x");

        Assert.DoesNotContain("segredo", FailureClassifier.Sanitize(postgres), StringComparison.Ordinal);
        Assert.Equal("PostgresException: SQLSTATE 42P01 (ck_x)", FailureClassifier.Sanitize(postgres));
        Assert.Equal("InvalidOperationException", FailureClassifier.Sanitize(new InvalidOperationException(secret)));
        Assert.Equal("InvalidEventException: type inválido (9).", FailureClassifier.Sanitize(new InvalidEventException("type inválido (9).")));
        Assert.True(FailureClassifier.Sanitize(new InvalidEventException(new string('x', 500))).Length <= 200);
    }
}
