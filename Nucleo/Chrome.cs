using System.Diagnostics;
using System.Text.Json;
using Microsoft.Win32;

namespace Depas.Nucleo;

/// <summary>
/// El Chrome de Depas: el Chrome instalado, con un perfil propio en la carpeta de datos.
/// Desde Chrome 136 no se puede controlar el perfil de todos los días, así que en este se inicia sesión
/// en Facebook una vez y la sesión queda guardada.
/// </summary>
public static class Chrome
{
    public const int Puerto = 9244;
    static readonly HttpClient http = new() { BaseAddress = new($"http://127.0.0.1:{Puerto}/"), Timeout = TimeSpan.FromSeconds(5) };

    public static string Perfil => Path.Combine(Almacen.Carpeta, "chrome");

    /// <summary>Para las pruebas: sin ventana, así no le quita el teclado a nadie.</summary>
    static bool Oculto => Environment.GetEnvironmentVariable("DEPAS_OCULTO") == "1";

    public static string? Ruta()
    {
        var registrada = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\chrome.exe", null, null) as string
                      ?? Registry.GetValue(@"HKEY_CURRENT_USER\SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\chrome.exe", null, null) as string;
        string[] candidatas =
        [
            registrada ?? "",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Google\Chrome\Application\chrome.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"Google\Chrome\Application\chrome.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Google\Chrome\Application\chrome.exe"),
        ];
        return candidatas.FirstOrDefault(File.Exists);
    }

    static async Task<string?> UrlNavegador()
    {
        try
        {
            using var doc = JsonDocument.Parse(await http.GetStringAsync("json/version"));
            return doc.RootElement.GetProperty("webSocketDebuggerUrl").GetString();
        }
        catch (Exception) { return null; }
    }

    /// <summary>Abre el Chrome de Depas si no está abierto y devuelve el websocket del navegador.</summary>
    static async Task<string> Asegurar(CancellationToken ct)
    {
        if (await UrlNavegador() is { } url) return url;

        var ruta = Ruta() ?? throw new InvalidOperationException("No encontré Chrome instalado.");
        var inicio = new ProcessStartInfo(ruta) { UseShellExecute = false };
        inicio.ArgumentList.Add($"--remote-debugging-port={Puerto}");
        inicio.ArgumentList.Add($"--user-data-dir={Perfil}");
        inicio.ArgumentList.Add("--no-first-run");
        inicio.ArgumentList.Add("--no-default-browser-check");
        // Las búsquedas van en pestañas de fondo: sin esto Chrome las frena y Marketplace no carga más al bajar.
        inicio.ArgumentList.Add("--disable-background-timer-throttling");
        inicio.ArgumentList.Add("--disable-backgrounding-occluded-windows");
        inicio.ArgumentList.Add("--disable-renderer-backgrounding");
        if (Oculto)
        {
            inicio.ArgumentList.Add("--headless=new");
            var version = FileVersionInfo.GetVersionInfo(ruta).ProductMajorPart;
            inicio.ArgumentList.Add($"--user-agent=Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/{version}.0.0.0 Safari/537.36");
        }
        Process.Start(inicio);

        for (var i = 0; i < 80; i++)
        {
            await Task.Delay(250, ct);
            if (await UrlNavegador() is { } lista) return lista;
        }
        throw new InvalidOperationException("Chrome no respondió. Ciérralo y vuelve a intentar.");
    }

    public static async Task<Pestana> AbrirPestana(string url, bool alFrente, CancellationToken ct)
    {
        var navegadorUrl = await Asegurar(ct);
        await using var navegador = await Cdp.Conectar(navegadorUrl, ct);
        var creada = await navegador.Enviar("Target.createTarget", new { url, background = !alFrente }, ct);
        var id = creada.GetProperty("targetId").GetString()!;
        if (alFrente) await navegador.Enviar("Target.activateTarget", new { targetId = id }, ct);
        return new Pestana(await Cdp.Conectar($"ws://127.0.0.1:{Puerto}/devtools/page/{id}", ct));
    }

    public static async Task Cerrar()
    {
        if (await UrlNavegador() is not { } url) return;
        await using var navegador = await Cdp.Conectar(url, CancellationToken.None);
        try { await navegador.Enviar("Browser.close", null, new CancellationTokenSource(5000).Token); }
        catch (Exception) { }
    }

    /// <summary>Si ya hay sesión de Facebook en el perfil de Depas (cookie c_user), sin abrir Chrome si está cerrado.</summary>
    public static async Task<bool?> SesionFacebook(CancellationToken ct)
    {
        if (await UrlNavegador() is not { } url) return null;
        await using var navegador = await Cdp.Conectar(url, ct);
        var r = await navegador.Enviar("Storage.getCookies", null, ct);
        return r.GetProperty("cookies").EnumerateArray()
            .Any(c => c.GetProperty("name").GetString() == "c_user" && c.GetProperty("domain").GetString()!.EndsWith("facebook.com"));
    }
}

public sealed class Pestana(Cdp cdp) : IAsyncDisposable
{
    /// <summary>Navega y espera a que la página termine de cargar (lo que arma JavaScript lo esperan los scripts).</summary>
    public async Task Ir(string url, CancellationToken ct)
    {
        var r = await cdp.Enviar("Page.navigate", new { url }, ct);
        if (r.TryGetProperty("errorText", out var error) && error.GetString() is { Length: > 0 } texto)
            throw new InvalidOperationException($"No se pudo abrir {url}: {texto}");
        for (var i = 0; i < 120; i++)
        {
            if (await cdp.Evaluar("JSON.stringify(document.readyState)", ct) == "\"complete\"") return;
            await Task.Delay(250, ct);
        }
    }

    public Task<string?> Evaluar(string expresion, CancellationToken ct) => cdp.Evaluar(expresion, ct);

    /// <summary>Deja la pestaña abierta para Piero (la de iniciar sesión) y corta solo la conexión.</summary>
    public ValueTask Soltar() => cdp.DisposeAsync();

    public async ValueTask DisposeAsync()
    {
        try { await cdp.Enviar("Page.close", null, new CancellationTokenSource(3000).Token); }
        catch (Exception) { }
        await cdp.DisposeAsync();
    }
}
