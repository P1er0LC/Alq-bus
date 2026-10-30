using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;

namespace Depas.Nucleo;

/// <summary>
/// Cliente mínimo del protocolo de DevTools de Chrome: manda comandos por el websocket de una pestaña
/// (o del navegador) y espera la respuesta con el mismo id. Los eventos se ignoran.
/// </summary>
public sealed class Cdp : IAsyncDisposable
{
    readonly ClientWebSocket ws = new();
    readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> pendientes = new();
    readonly SemaphoreSlim envio = new(1, 1);
    readonly CancellationTokenSource cierre = new();
    int ultimoId;
    Task? lector;

    public static async Task<Cdp> Conectar(string url, CancellationToken ct)
    {
        var cdp = new Cdp();
        await cdp.ws.ConnectAsync(new Uri(url), ct);
        cdp.lector = Task.Run(cdp.Leer);
        return cdp;
    }

    public async Task<JsonElement> Enviar(string metodo, object? parametros, CancellationToken ct)
    {
        var id = Interlocked.Increment(ref ultimoId);
        var respuesta = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        pendientes[id] = respuesta;
        var json = JsonSerializer.SerializeToUtf8Bytes(new { id, method = metodo, @params = parametros ?? new { } });
        await envio.WaitAsync(ct);
        try { await ws.SendAsync(json, WebSocketMessageType.Text, true, ct); }
        finally { envio.Release(); }
        using var _ = ct.Register(() => respuesta.TrySetCanceled(ct));
        return await respuesta.Task;
    }

    /// <summary>Corre JavaScript en la página y devuelve el texto que resuelva (los scripts devuelven JSON.stringify).</summary>
    public async Task<string?> Evaluar(string expresion, CancellationToken ct)
    {
        var r = await Enviar("Runtime.evaluate", new { expression = expresion, awaitPromise = true, returnByValue = true }, ct);
        if (r.TryGetProperty("exceptionDetails", out var fallo))
        {
            var detalle = fallo.TryGetProperty("exception", out var ex) && ex.TryGetProperty("description", out var d) ? d.GetString() : fallo.GetProperty("text").GetString();
            throw new InvalidOperationException(detalle);
        }
        var valor = r.GetProperty("result");
        return valor.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    }

    async Task Leer()
    {
        var buffer = new byte[64 * 1024];
        using var mensaje = new MemoryStream();
        try
        {
            while (ws.State == WebSocketState.Open)
            {
                mensaje.SetLength(0);
                WebSocketReceiveResult r;
                do
                {
                    r = await ws.ReceiveAsync(buffer, cierre.Token);
                    if (r.MessageType == WebSocketMessageType.Close) return;
                    mensaje.Write(buffer, 0, r.Count);
                } while (!r.EndOfMessage);

                using var doc = JsonDocument.Parse(mensaje.ToArray());
                var raiz = doc.RootElement;
                if (!raiz.TryGetProperty("id", out var id) || !pendientes.TryRemove(id.GetInt32(), out var respuesta)) continue;
                if (raiz.TryGetProperty("error", out var error))
                    respuesta.TrySetException(new InvalidOperationException(error.GetProperty("message").GetString()));
                else
                    respuesta.TrySetResult(raiz.GetProperty("result").Clone());
            }
        }
        catch (Exception) { }
        finally
        {
            foreach (var p in pendientes.Values) p.TrySetException(new IOException("Chrome cerró la conexión."));
        }
    }

    public async ValueTask DisposeAsync()
    {
        cierre.Cancel();
        try
        {
            if (ws.State == WebSocketState.Open)
                await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
        }
        catch (Exception) { }
        if (lector != null) await lector;
        ws.Dispose();
    }
}
