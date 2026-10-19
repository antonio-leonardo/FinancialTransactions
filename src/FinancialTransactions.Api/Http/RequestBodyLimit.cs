using Microsoft.AspNetCore.Http.Features;

namespace FinancialTransactions.Api.Http;

/// <summary>
/// Limite de corpo de 16 KB para o grupo <c>/api/v1</c> (nenhum payload legítimo se aproxima: o maior campo é a
/// descrição de 140 caracteres). Duas camadas: <c>Content-Length</c> acima do limite é recusado antes de qualquer leitura;
/// e o limite do servidor (<see cref="IHttpMaxRequestBodySizeFeature"/>) cobre corpo sem <c>Content-Length</c> (chunked).
/// Escolhido no middleware, e não em <c>KestrelServerOptions.Limits</c>, para valer só no grupo financeiro e para que
/// o host de testes exerça o mesmo código.
/// </summary>
public static class RequestBodyLimit
{
    public const long MaxBytes = 16 * 1024;

    public static IApplicationBuilder UseApiRequestBodyLimit(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        return app.UseWhen(
            context => context.Request.Path.StartsWithSegments("/api/v1", StringComparison.OrdinalIgnoreCase),
            branch => branch.Use(async (context, next) =>
            {
                if (context.Request.ContentLength > MaxBytes)
                {
                    await ApiProblems.PayloadTooLarge().ExecuteAsync(context).ConfigureAwait(false);
                    return;
                }

                var feature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
                if (feature is { IsReadOnly: false })
                {
                    feature.MaxRequestBodySize = MaxBytes;
                }

                await next(context).ConfigureAwait(false);
            }));
    }
}
