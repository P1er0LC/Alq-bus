using System.Diagnostics;
using System.Text.Json;
using Microsoft.Win32;

namespace Depas.Nucleo;

/// <summary>
/// El Chrome de Depas: el Chrome instalado, con un perfil propio en la carpeta de datos.
/// Desde Chrome 136 no se puede controlar el perfil de todos los días, así que en este se inicia sesión
/// en Facebook una vez y la sesión queda guardada.
/// Las búsquedas van en un Chrome sin ventana que se cierra al terminar; la ventana solo se abre para entrar a Facebook.
/// </summary>
public static class Chrome
{
    /// <summary>DEPAS_PUERTO es para las pruebas: así nunca tocan el Chrome de Depas que Piero tenga abierto.</summary>
    public static readonly int Puerto = int.TryParse(Environment.GetEnvironmentVariable("DEPAS_PUERTO"), out var puerto) ? puerto : 9244;
    static readonly HttpClient http = new() { BaseAddress = new($"http://127.0.0.1:{Puerto}/"), Timeout = TimeSpan.FromSeconds(5) };

    public static string Perfil => Path.Combine(Almacen.Carpeta, "chrome");

    /// <summary>
    /// Chrome sin ventana se presenta igual que uno con ventana («Chrome/156»), así que Depas anota el proceso
    /// del que abrió sin ventana para saber cuál cerrar.
    /// </summary>
    static string Marca => Path.Combine(Almacen.Carpeta, "chrome-sin-ventana.pid");

    static bool CorreSinVentana()
    {
        try { return File.Exists(Marca) && int.TryParse(File.ReadAllText(Marca), out var pid) && !Process.GetProcessById(pid).HasExited; }
        catch (ArgumentException) { return false; }
    }

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

    /// <summary>
    /// Abre el Chrome de Depas si no está abierto y devuelve el websocket del navegador. Para buscar sirve el que
    /// esté abierto, con ventana o sin ella; para entrar a Facebook hace falta ventana, así que si corre uno sin
    /// ventana se cierra primero (los dos no pueden usar el mismo perfil a la vez).
    /// </summary>
    static async Task<string> Asegurar(bool ventana, CancellationToken ct)
    {
        if (await UrlNavegador() is { } url)
        {
            if (!ventana || !CorreSinVentana()) return url;
            await Cerrar();
            for (var i = 0; i < 40 && await UrlNavegador() != null; i++) await Task.Delay(250, ct);
            await Task.Delay(800, ct);
        }

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
        if (!ventana)
        {
            inicio.ArgumentList.Add("--headless=new");
            // Sin esto el navegador se anuncia como «HeadlessChrome» y algunos sitios lo tratan como robot.
            var version = FileVersionInfo.GetVersionInfo(ruta).ProductMajorPart;
            inicio.ArgumentList.Add($"--user-agent=Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/{version}.0.0.0 Safari/537.36");
        }
        Process.Start(inicio);
        Directory.CreateDirectory(Almacen.Carpeta);
        if (ventana) File.Delete(Marca);

        for (var i = 0; i < 80; i++)
        {
            await Task.Delay(250, ct);
            if (await UrlNavegador() is not { } lista) continue;
            // El chrome.exe que se lanza se vuelve a abrir como otro proceso: el número bueno lo da el propio Chrome.
            if (!ventana) File.WriteAllText(Marca, (await ProcesoNavegador(lista, ct)).ToString());
            return lista;
        }
        throw new InvalidOperationException("Chrome no respondió. Ciérralo y vuelve a intentar.");
    }

    static async Task<int> ProcesoNavegador(string url, CancellationToken ct)
    {
        await using var navegador = await Cdp.Conectar(url, ct);
        var info = await navegador.Enviar("SystemInfo.getProcessInfo", null, ct);
        return info.GetProperty("processInfo").EnumerateArray()
            .First(p => p.GetProperty("type").GetString() == "browser").GetProperty("id").GetInt32();
    }

    /// <summary>Una pestaña de fondo para buscar: en el Chrome sin ventana, o en el que esté abierto.</summary>
    public static Task<Pestana> AbrirPestana(string url, CancellationToken ct) => Abrir(url, ventana: false, ct);

    /// <summary>Una pestaña al frente en un Chrome con ventana, para que Piero entre a Facebook.</summary>
    public static Task<Pestana> AbrirVentana(string url, CancellationToken ct) => Abrir(url, ventana: true, ct);

    static async Task<Pestana> Abrir(string url, bool ventana, CancellationToken ct)
    {
        var navegadorUrl = await Asegurar(ventana, ct);
        await using var navegador = await Cdp.Conectar(navegadorUrl, ct);
        var creada = await navegador.Enviar("Target.createTarget", new { url, background = !ventana }, ct);
        var id = creada.GetProperty("targetId").GetString()!;
        if (ventana) await navegador.Enviar("Target.activateTarget", new { targetId = id }, ct);
        return new Pestana(await Cdp.Conectar($"ws://127.0.0.1:{Puerto}/devtools/page/{id}", ct));
    }

    public static async Task Cerrar()
    {
        if (await UrlNavegador() is not { } url) return;
        await using var navegador = await Cdp.Conectar(url, CancellationToken.None);
        try { await navegador.Enviar("Browser.close", null, new CancellationTokenSource(5000).Token); }
        catch (Exception) { }
    }

    /// <summary>Al terminar de buscar: cierra el Chrome sin ventana; si Piero tiene abierta la ventana de Facebook, la deja.</summary>
    public static async Task CerrarSiSinVentana()
    {
        if (!CorreSinVentana()) return;
        await Cerrar();
        try { File.Delete(Marca); } catch (IOException) { }
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
