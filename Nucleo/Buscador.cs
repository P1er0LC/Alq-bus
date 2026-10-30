using System.Text.Json;
using System.Text.RegularExpressions;

namespace Depas.Nucleo;

public sealed record Resultado(List<Aviso> Avisos, List<string> Errores, bool? SesionFacebook);

/// <summary>
/// Busca en el Chrome de Depas: Marketplace en una pestaña (una búsqueda por distrito) y los portales en otra,
/// las dos a la vez. Trae todo lo del rango de precio; dormitorios y palabras se filtran en la app.
/// </summary>
public static class Buscador
{
    const int VueltasMarketplace = 6;
    const int MaxPaginasPortal = 5;

    static string Script(string nombre) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "recursos", "scripts", nombre));

    static string Llamar(string script, object parametros) => $"({Script(script).Trim()})({JsonSerializer.Serialize(parametros)})";

    /// <param name="conDetalle">Avisos de Marketplace cuya página ya se leyó antes: no se vuelven a abrir.</param>
    public static async Task<Resultado> Buscar(Config config, IReadOnlySet<string> conDetalle, IProgress<string> progreso, CancellationToken ct)
    {
        var distritos = config.Distritos.Select(Distritos.Buscar).OfType<Distrito>().ToList();
        var errores = new List<string>();
        var tareas = new List<Task<List<Aviso>>>();
        if (config.Fuentes.Contains(Fuentes.Marketplace))
            tareas.Add(Marketplace(config, distritos, conDetalle, errores, progreso, ct));
        if (config.Fuentes.Contains(Fuentes.Adondevivir) || config.Fuentes.Contains(Fuentes.Urbania))
            tareas.Add(Portales(config, distritos, errores, progreso, ct));

        var listas = await Task.WhenAll(tareas);
        bool? sesion = null;
        if (config.Fuentes.Contains(Fuentes.Marketplace))
        {
            try { sesion = await Chrome.SesionFacebook(ct); } catch (Exception) { }
        }

        // Adondevivir y Urbania publican los mismos avisos con ids distintos: se reconocen por precio y descripción.
        var avisos = listas.SelectMany(l => l)
            .GroupBy(a => a.Fuente == Fuentes.Marketplace || a.Descripcion.Length < 40 ? a.Clave : $"{a.Precio}|{Recorte(a.Descripcion)}")
            .Select(g => g.First()).ToList();
        return new Resultado(avisos, errores, sesion);
    }

    static string Recorte(string texto)
    {
        var normal = Regex.Replace(Distritos.Normalizar(texto), @"[^a-z0-9]", "");
        return normal[..Math.Min(80, normal.Length)];
    }

    static async Task<List<Aviso>> Marketplace(Config config, List<Distrito> distritos, IReadOnlySet<string> conDetalle, List<string> errores, IProgress<string> progreso, CancellationToken ct)
    {
        var avisos = new List<Aviso>();
        try
        {
            await using var pestana = await Chrome.AbrirPestana("about:blank", alFrente: false, ct);
            foreach (var distrito in distritos)
            {
                progreso.Report($"Marketplace: buscando en {distrito.Nombre}…");
                var consulta = Uri.EscapeDataString($"departamento {distrito.Nombre}");
                await pestana.Ir($"https://www.facebook.com/marketplace/lima/search?query={consulta}&minPrice={config.PrecioMin}&maxPrice={config.PrecioMax}&exact=false", ct);
                var json = await pestana.Evaluar(Llamar("facebook.js", new { vueltas = VueltasMarketplace }), ct);
                using var doc = JsonDocument.Parse(json ?? "{}");
                if (doc.RootElement.TryGetProperty("url", out var url) && url.GetString()!.Contains("/login"))
                {
                    lock (errores) errores.Add("Marketplace pidió iniciar sesión: entra a Facebook en el Chrome de Depas.");
                    break;
                }
                foreach (var crudo in doc.RootElement.GetProperty("avisos").EnumerateArray())
                {
                    var lineas = crudo.GetProperty("lineas").EnumerateArray().Select(l => l.GetString()!).ToArray();
                    if (DeMarketplace(crudo.GetProperty("id").GetString()!, lineas, crudo.GetProperty("imagen").GetString()!, config.TipoCambio) is { } aviso)
                        avisos.Add(aviso);
                }
            }

            // La lista solo trae título y precio. Los que podrían servir y no se leyeron antes se abren uno por uno:
            // la descripción dice los dormitorios que el título calla.
            avisos = avisos.GroupBy(a => a.Clave).Select(g => g.First()).ToList();
            var excluidas = Excluidas(config);
            var porLeer = avisos.Where(a => !conDetalle.Contains(a.Clave) && PasaBasico(a, config, excluidas)
                                            && (a.Dormitorios is null || a.Dormitorios >= config.DormitoriosMin)).ToList();
            for (var i = 0; i < porLeer.Count; i++)
            {
                progreso.Report($"Marketplace: leyendo aviso {i + 1} de {porLeer.Count}…");
                try
                {
                    await pestana.Ir(porLeer[i].Url, ct);
                    using var doc = JsonDocument.Parse(await pestana.Evaluar(Llamar("facebook-detalle.js", new { }), ct) ?? "{}");
                    porLeer[i].Completar(doc.RootElement.GetProperty("descripcion").GetString()!, doc.RootElement.GetProperty("detalles").GetString()!);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception) { }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { lock (errores) errores.Add($"Marketplace: {ex.Message}"); }
        return avisos;
    }

    public static Aviso? DeMarketplace(string id, string[] lineas, string imagen, decimal tipoCambio)
    {
        var precios = lineas.TakeWhile(Texto.EsPrecio).ToArray();
        var resto = lineas.Skip(precios.Length).ToArray();
        if (precios.Length == 0 || resto.Length == 0) return null;
        var titulo = resto[0];
        var ubicacion = resto.Length > 1 ? resto[^1] : "";
        var ahora = DateTime.Now;
        return new Aviso
        {
            Clave = $"fb:{id}",
            Fuente = Fuentes.Marketplace,
            Url = $"https://www.facebook.com/marketplace/item/{id}/",
            Titulo = titulo,
            Ubicacion = ubicacion,
            Imagen = imagen,
            Distrito = Distritos.De(ubicacion, titulo)?.Nombre,
            Precio = Texto.Precio(precios[0], tipoCambio),
            PrecioAnterior = precios.Length > 1 ? Texto.Precio(precios[1], tipoCambio) : null,
            Dormitorios = Texto.Dormitorios(titulo),
            Banos = Texto.Banos(titulo),
            Area = Texto.Area(titulo),
            PrimeraVez = ahora,
            UltimaVez = ahora,
        };
    }

    static async Task<List<Aviso>> Portales(Config config, List<Distrito> distritos, List<string> errores, IProgress<string> progreso, CancellationToken ct)
    {
        var avisos = new List<Aviso>();
        var rango = $"de-{config.PrecioMin}-a-{config.PrecioMax}-soles";
        var portales = new List<(string Fuente, string Inicio, Func<Distrito, object> Busqueda)>();
        if (config.Fuentes.Contains(Fuentes.Adondevivir))
            portales.Add((Fuentes.Adondevivir, "https://www.adondevivir.com/", d => new
            {
                distrito = d.Nombre,
                url = $"https://www.adondevivir.com/departamentos-en-alquiler-en-{d.Slug}-{rango}.html",
                pagina = $"https://www.adondevivir.com/departamentos-en-alquiler-en-{d.Slug}-{rango}-pagina-{{n}}.html",
            }));
        if (config.Fuentes.Contains(Fuentes.Urbania))
            portales.Add((Fuentes.Urbania, "https://urbania.pe/", d =>
            {
                var provincia = Distritos.Normalizar(d.Provincia);
                var url = $"https://urbania.pe/buscar/alquiler-de-departamentos-en-{d.Slug}--{provincia}--{provincia}?priceMin={config.PrecioMin}&priceMax={config.PrecioMax}&currencyId=6";
                return new { distrito = d.Nombre, url, pagina = url + "&page={n}" };
            }));

        try
        {
            await using var pestana = await Chrome.AbrirPestana("about:blank", alFrente: false, ct);
            foreach (var (fuente, inicio, busqueda) in portales)
            {
                progreso.Report($"{fuente}…");
                await pestana.Ir(inicio, ct);
                var json = await pestana.Evaluar(Llamar("navent.js", new { busquedas = distritos.Select(busqueda), maxPaginas = MaxPaginasPortal }), ct);
                using var doc = JsonDocument.Parse(json ?? "{}");
                foreach (var e in doc.RootElement.GetProperty("errores").EnumerateArray())
                    lock (errores) errores.Add($"{fuente}, {e.GetString()}");
                foreach (var crudo in doc.RootElement.GetProperty("avisos").EnumerateArray())
                    if (DePortal(fuente, inicio, crudo, config.TipoCambio) is { } aviso) avisos.Add(aviso);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { lock (errores) errores.Add($"Portales: {ex.Message}"); }
        return avisos;
    }

    static Aviso? DePortal(string fuente, string inicio, JsonElement crudo, decimal tipoCambio)
    {
        string Campo(string nombre) => crudo.GetProperty(nombre).GetString() ?? "";
        var id = Campo("id");
        if (id.Length == 0) return null;
        var rasgos = string.Join(" · ", crudo.GetProperty("rasgos").EnumerateArray().Select(r => r.GetString()));
        var descripcion = Campo("descripcion");
        var ubicacion = Campo("ubicacion");
        var direccion = Campo("direccion");
        var titulo = Campo("titulo");
        var ahora = DateTime.Now;
        return new Aviso
        {
            Clave = $"navent:{id}",
            Fuente = fuente,
            Url = new Uri(new Uri(inicio), Campo("ruta")).ToString(),
            Titulo = titulo.Length > 0 ? titulo : direccion.Length > 0 ? direccion : ubicacion,
            Descripcion = descripcion,
            Ubicacion = direccion.Length > 0 ? $"{direccion}, {ubicacion}" : ubicacion,
            Imagen = Campo("imagen"),
            Distrito = Distritos.De(ubicacion, descripcion)?.Nombre,
            Precio = Texto.Precio(Campo("precio"), tipoCambio),
            Mantenimiento = Texto.Precio(Campo("mantenimiento"), tipoCambio),
            Dormitorios = Texto.Dormitorios(rasgos),
            Banos = Texto.Banos(rasgos),
            Area = Texto.Area(rasgos),
            PrimeraVez = ahora,
            UltimaVez = ahora,
        };
    }

    /// <summary>Cada frase de «ocultar si el título dice», como palabras enteras: «mini» no oculta «ministerio».</summary>
    public static Regex[] Excluidas(Config c) =>
        c.PalabrasExcluidas().Select(p => new Regex($@"\b{Regex.Escape(p)}\b", RegexOptions.CultureInvariant)).ToArray();

    /// <summary>Los filtros que no van en la búsqueda: se aplican al momento sobre lo guardado.</summary>
    public static bool Pasa(Aviso a, Config c, Regex[] excluidas)
    {
        if (a.Descartado && !c.MostrarDescartados) return false;
        if (!PasaBasico(a, c, excluidas)) return false;
        return a.Dormitorios is int d ? d >= c.DormitoriosMin : c.SinDormitorios;
    }

    static bool PasaBasico(Aviso a, Config c, Regex[] excluidas)
    {
        if (!c.Fuentes.Contains(a.Fuente)) return false;
        if (a.Distrito is null || !c.Distritos.Contains(a.Distrito)) return false;
        if (a.Precio is not int precio || precio < c.PrecioMin || precio > c.PrecioMax) return false;
        var titulo = Distritos.Normalizar(a.Titulo);
        return !excluidas.Any(r => r.IsMatch(titulo));
    }
}
