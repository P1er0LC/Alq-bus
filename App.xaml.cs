using System.Text.Encodings.Web;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Depas.Nucleo;

namespace Depas;

/// <summary>
/// Además de la ventana, modos para probar sin tocar lo de Piero (con DEPAS_DATOS y DEPAS_PUERTO, así usan otro perfil y otro Chrome):
///   Depas.exe --buscar --salida resultado.json   busca con la config guardada y deja lo encontrado en un JSON
///   Depas.exe --captura ventana.png              dibuja la ventana fuera de la pantalla y la guarda
/// </summary>
public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var fuentes = new Uri(Path.Combine(AppContext.BaseDirectory, "recursos", "fuentes") + Path.DirectorySeparatorChar);
        Resources["Cuerpo"] = new FontFamily(fuentes, "./#Archivo, Segoe UI");
        Resources["Mono"] = new FontFamily(fuentes, "./#JetBrains Mono, Consolas");

        var args = e.Args;
        string? Valor(string nombre) => Array.IndexOf(args, nombre) is var i and >= 0 && i + 1 < args.Length ? args[i + 1] : null;

        if (Valor("--sondear") is { } sondeo)
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Shutdown(await Sondear(sondeo, Valor("--js"), Valor("--salida"), Valor("--js2")));
            return;
        }

        if (args.Contains("--buscar"))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Shutdown(await BuscarSinVentana(Valor("--salida")));
            return;
        }

        var ventana = new MainWindow();
        if (Valor("--captura") is { } png)
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            await Capturar(ventana, png);
            Shutdown();
            return;
        }
        MainWindow = ventana;
        ventana.Show();
    }

    static async Task<int> BuscarSinVentana(string? salida)
    {
        var config = Almacen.LeerConfig();
        var pasos = new List<string>();
        var reloj = System.Diagnostics.Stopwatch.StartNew();
        var inicio = DateTime.Now;
        try
        {
            var avisos = Almacen.LeerAvisos();
            var conDetalle = avisos.Where(a => a.Descripcion.Length > 0).Select(a => a.Clave).ToHashSet();
            var r = await Task.Run(() => Buscador.Buscar(config, conDetalle, new Progress<string>(pasos.Add), CancellationToken.None));
            var nuevos = Almacen.Mezclar(avisos, r.Avisos);
            config.InicioUltimaBusqueda = inicio;
            Almacen.Guardar(avisos);
            Almacen.Guardar(config);
            var excluidas = Buscador.Excluidas(config);
            // Lo guardado ya tiene lo que se leyó de la página de cada aviso en búsquedas anteriores.
            var guardados = avisos.ToDictionary(a => a.Clave);
            r = r with { Avisos = r.Avisos.Select(a => guardados.GetValueOrDefault(a.Clave, a)).ToList() };
            if (salida != null)
            {
                var informe = new
                {
                    segundos = (int)reloj.Elapsed.TotalSeconds,
                    leidos = r.Avisos.Count,
                    nuevos,
                    pasan = r.Avisos.Count(a => Buscador.Pasa(a, config, excluidas)),
                    porFuente = r.Avisos.GroupBy(a => a.Fuente).ToDictionary(g => g.Key, g => g.Count()),
                    porDistrito = r.Avisos.GroupBy(a => a.Distrito ?? "(otro)").ToDictionary(g => g.Key, g => g.Count()),
                    r.Errores,
                    r.SesionFacebook,
                    r.Registro,
                    avisos = r.Avisos.Select(a => new { pasa = Buscador.Pasa(a, config, excluidas), a.Fuente, a.TambienEn, a.Distrito, a.Precio, a.Dormitorios, a.Banos, a.Area, a.Mantenimiento, a.Titulo, a.Ubicacion, a.Url, a.Imagen, a.Descripcion }),
                };
                File.WriteAllText(salida, JsonSerializer.Serialize(informe, new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
            }
            return 0;
        }
        catch (Exception ex)
        {
            if (salida != null) File.WriteAllText(salida, JsonSerializer.Serialize(new { error = ex.ToString(), pasos }));
            return 1;
        }
        finally
        {
            await Chrome.CerrarSiSinVentana();
        }
    }

    /// <summary>
    /// Para estudiar un sitio nuevo desde el Chrome de Depas: abre la url, espera y corre el JS (una función async).
    /// Con --js2, después espera a que la página navegue (si el primer JS hizo clic en algo) y corre el segundo.
    /// </summary>
    static async Task<int> Sondear(string url, string? js, string? salida, string? js2)
    {
        try
        {
            await using var pestana = await Chrome.AbrirPestana("about:blank", CancellationToken.None);
            await pestana.Ir(url, CancellationToken.None);
            await Task.Delay(3000);
            var codigo = js != null ? File.ReadAllText(js) : "async () => JSON.stringify({ url: location.href, titulo: document.title, texto: document.body.innerText.slice(0, 3000) })";
            string? resultado;
            try { resultado = await pestana.Evaluar($"({codigo.Trim()})()", CancellationToken.None); }
            catch (InvalidOperationException ex) when (js2 != null) { resultado = ex.Message; }
            if (js2 != null)
            {
                await Task.Delay(6000);
                resultado = $"[{JsonSerializer.Serialize(resultado)}, {await pestana.Evaluar($"({File.ReadAllText(js2).Trim()})()", CancellationToken.None)}]";
            }
            if (salida != null) File.WriteAllText(salida, resultado ?? "null");
            return 0;
        }
        catch (Exception ex)
        {
            if (salida != null) File.WriteAllText(salida, JsonSerializer.Serialize(new { error = ex.ToString() }));
            return 1;
        }
        finally
        {
            await Chrome.CerrarSiSinVentana();
        }
    }

    /// <summary>Fuera de la pantalla y sin activarse: no le quita el teclado a nadie.</summary>
    static async Task Capturar(Window ventana, string png)
    {
        ventana.WindowStartupLocation = WindowStartupLocation.Manual;
        ventana.Left = -20000;
        ventana.Top = -20000;
        ventana.ShowActivated = false;
        ventana.ShowInTaskbar = false;
        ventana.Show();
        await Task.Delay(5000);
        ventana.UpdateLayout();
        var contenido = (FrameworkElement)ventana.Content;
        var dpi = VisualTreeHelper.GetDpi(contenido);
        var imagen = new RenderTargetBitmap((int)(contenido.ActualWidth * dpi.DpiScaleX), (int)(contenido.ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        imagen.Render(contenido);
        var codificador = new PngBitmapEncoder();
        codificador.Frames.Add(BitmapFrame.Create(imagen));
        using var archivo = File.Create(png);
        codificador.Save(archivo);
        ventana.Close();
    }
}
