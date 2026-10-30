using System.Text.Json;
using System.Text.RegularExpressions;

namespace Depas.Nucleo;

/// <param name="Registro">Qué devolvió cada página (fuente · distrito: tarjetas · título), para revisar cuando un sitio cambia.</param>
public sealed record Resultado(List<Aviso> Avisos, List<string> Errores, bool? SesionFacebook, List<string> Registro);

/// <summary>
/// Busca en el Chrome de Depas, cada fuente en su pestaña y hasta cuatro a la vez: Marketplace (una búsqueda por
/// distrito y después la página de cada aviso nuevo), Adondevivir y Urbania con su lector propio, y el resto de los
/// portales con el lector general. Trae todo lo del rango de precio; dormitorios y palabras se filtran en la app.
/// </summary>
public static class Buscador
{
    const int VueltasMarketplace = 6;
    const int MaxPaginasPortal = 5;
    const int PestanasALaVez = 5;

    static string Script(string nombre) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "recursos", "scripts", nombre));

    static string Llamar(string script, object parametros) => $"({Script(script).Trim()})({JsonSerializer.Serialize(parametros)})";

    /// <param name="conDetalle">Avisos de Marketplace cuya página ya se leyó antes: no se vuelven a abrir.</param>
    public static async Task<Resultado> Buscar(Config config, IReadOnlySet<string> conDetalle, IProgress<string> progreso, CancellationToken ct)
    {
        var distritos = config.Distritos.Select(Distritos.Buscar).OfType<Distrito>().ToList();
        var errores = new List<string>();
        var registro = new List<string>();
        var turnos = new SemaphoreSlim(PestanasALaVez);
        async Task<List<Aviso>> EnTurno(Func<Task<List<Aviso>>> fuente)
        {
            await turnos.WaitAsync(ct);
            try { return await fuente(); }
            finally { turnos.Release(); }
        }

        var tareas = new List<Task<List<Aviso>>>();
        if (config.Usa(Fuentes.Marketplace))
            tareas.Add(EnTurno(() => Marketplace(config, distritos, conDetalle, errores, progreso, ct)));
        if (config.Usa(Fuentes.Adondevivir) || config.Usa(Fuentes.Urbania))
            tareas.Add(EnTurno(() => Navent(config, distritos, errores, progreso, ct)));
        foreach (var portal in Portales.Todos.Where(p => config.Usa(p.Nombre)))
            tareas.Add(EnTurno(() => Portal(portal, config, distritos, errores, registro, progreso, ct)));

        var listas = await Task.WhenAll(tareas);
        bool? sesion = null;
        if (config.Usa(Fuentes.Marketplace))
        {
            try { sesion = await Chrome.SesionFacebook(ct); } catch (Exception) { }
        }
        return new Resultado(Juntar(listas.SelectMany(l => l)), errores, sesion, registro);
    }

    /// <summary>
    /// Un mismo departamento sale en varias fuentes: Adondevivir y Urbania con ids distintos (se reconocen por precio
    /// y descripción), y los agregadores repiten los de los portales (mismo distrito, precio, metros y dormitorios).
    /// Queda el de la fuente preferida y las otras se anotan en «también en».
    /// </summary>
    static List<Aviso> Juntar(IEnumerable<Aviso> encontrados)
    {
        var avisos = new List<Aviso>();
        var porHuella = new Dictionary<string, Aviso>();
        foreach (var aviso in encontrados.OrderBy(a => Array.IndexOf(Fuentes.Todas, a.Fuente)))
        {
            var huellas = Huellas(aviso).ToList();
            if (huellas.Select(h => porHuella.GetValueOrDefault(h)).FirstOrDefault(a => a != null) is { } previo)
            {
                if (previo.Fuente != aviso.Fuente && !previo.TambienEn.Contains(aviso.Fuente)) previo.TambienEn.Add(aviso.Fuente);
                continue;
            }
            foreach (var h in huellas) porHuella[h] = aviso;
            avisos.Add(aviso);
        }
        return avisos;
    }

    /// <summary>
    /// Lo mismo para la lista guardada, sin tocarla: si en una búsqueda falló una fuente, el mismo departamento pudo
    /// quedar guardado dos veces (de Properati en una búsqueda y de Trovit en otra). Se muestra uno con «también en».
    /// </summary>
    public static List<(Aviso Aviso, List<string> Tambien)> JuntarParaMostrar(IEnumerable<Aviso> avisos)
    {
        var grupos = new List<(Aviso Aviso, List<string> Tambien)>();
        var porHuella = new Dictionary<string, int>();
        foreach (var aviso in avisos.OrderBy(a => Array.IndexOf(Fuentes.Todas, a.Fuente)))
        {
            var huellas = Huellas(aviso).ToList();
            var otras = aviso.TambienEn.Prepend(aviso.Fuente);
            if (huellas.Select(h => porHuella.TryGetValue(h, out var i) ? i : -1).FirstOrDefault(i => i >= 0, -1) is var previo and >= 0)
            {
                var g = grupos[previo];
                g.Tambien.AddRange(otras.Where(f => f != g.Aviso.Fuente && !g.Tambien.Contains(f)));
                continue;
            }
            foreach (var h in huellas) porHuella[h] = grupos.Count;
            grupos.Add((aviso, [.. aviso.TambienEn]));
        }
        return grupos;
    }

    static IEnumerable<string> Huellas(Aviso a)
    {
        yield return a.Clave;
        if (a.Fuente != Fuentes.Marketplace && a.Descripcion.Length >= 40) yield return $"texto|{a.Precio}|{Recorte(a.Descripcion)}";
        if (a.Distrito != null && a.Precio != null && a.Area != null && a.Dormitorios != null)
            yield return $"datos|{a.Distrito}|{a.Precio}|{a.Area}|{a.Dormitorios}";
    }

    static string Recorte(string texto)
    {
        var normal = Regex.Replace(Distritos.Normalizar(texto), @"[^a-z0-9]", "");
        return normal[..Math.Min(80, normal.Length)];
    }

    static async Task<List<Aviso>> Marketplace(Config config, List<Distrito> distritos, IReadOnlySet<string> conDetalle, List<string> errores, IProgress<string> progreso, CancellationToken ct)
    {
        var avisos = new List<Aviso>();
        var precio = $"minPrice={config.PrecioMin}&maxPrice={config.PrecioMax}&exact=false";
        // Sin sesión, Facebook corta cada búsqueda en unos 24 avisos: varias búsquedas distintas encuentran más.
        // La categoría Alquileres trae también los avisos cuyo título no dice «departamento» y filtra por dormitorios.
        var busquedas = distritos.SelectMany(d => new[] { "departamento", "alquiler" }
                .Select(palabra => ($"{d.Nombre} ({palabra})", $"https://www.facebook.com/marketplace/lima/search?query={Uri.EscapeDataString($"{palabra} {d.Nombre}")}&{precio}")))
            .Append(("Alquileres de Lima", $"https://www.facebook.com/marketplace/lima/propertyrentals?{precio}&minBedrooms={config.DormitoriosMin}&sortBy=creation_time_descend"));
        try
        {
            await using var pestana = await Chrome.AbrirPestana("about:blank", ct);
            foreach (var (nombre, direccion) in busquedas)
            {
                progreso.Report($"Marketplace: {nombre}…");
                await pestana.Ir(direccion, ct);
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
            // la descripción dice los dormitorios que el título calla, y el distrito de los que solo dicen «Callao» o «Lima».
            avisos = avisos.GroupBy(a => a.Clave).Select(g => g.First()).ToList();
            var excluidas = Excluidas(config);
            var porLeer = avisos.Where(a => !conDetalle.Contains(a.Clave)
                                            && (PasaBasico(a, config, excluidas) || (a.UbicacionGenerica && PasaPrecioYTitulo(a, config, excluidas)))
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
            Distrito = Distritos.De(ubicacion, titulo, marketplace: true)?.Nombre,
            Precio = Texto.Precio(precios[0], tipoCambio),
            PrecioAnterior = precios.Length > 1 ? Texto.Precio(precios[1], tipoCambio) : null,
            Dormitorios = Texto.Dormitorios(titulo),
            Banos = Texto.Banos(titulo),
            Area = Texto.Area(titulo),
            PrimeraVez = ahora,
            UltimaVez = ahora,
        };
    }

    static async Task<List<Aviso>> Navent(Config config, List<Distrito> distritos, List<string> errores, IProgress<string> progreso, CancellationToken ct)
    {
        var avisos = new List<Aviso>();
        var rango = $"de-{config.PrecioMin}-a-{config.PrecioMax}-soles";
        var portales = new List<(string Fuente, string Inicio, Func<Distrito, object> Busqueda)>();
        if (config.Usa(Fuentes.Adondevivir))
            portales.Add((Fuentes.Adondevivir, "https://www.adondevivir.com/", d => new
            {
                distrito = d.Nombre,
                url = $"https://www.adondevivir.com/departamentos-en-alquiler-en-{d.Slug}-{rango}.html",
                pagina = $"https://www.adondevivir.com/departamentos-en-alquiler-en-{d.Slug}-{rango}-pagina-{{n}}.html",
            }));
        if (config.Usa(Fuentes.Urbania))
            portales.Add((Fuentes.Urbania, "https://urbania.pe/", d =>
            {
                var provincia = Distritos.Normalizar(d.Provincia);
                var url = $"https://urbania.pe/buscar/alquiler-de-departamentos-en-{d.Slug}--{provincia}--{provincia}?priceMin={config.PrecioMin}&priceMax={config.PrecioMax}&currencyId=6";
                return new { distrito = d.Nombre, url, pagina = url + "&page={n}" };
            }));

        try
        {
            await using var pestana = await Chrome.AbrirPestana("about:blank", ct);
            foreach (var (fuente, inicio, busqueda) in portales)
            {
                progreso.Report($"{fuente}…");
                await pestana.Ir(inicio, ct);
                var json = await pestana.Evaluar(Llamar("navent.js", new { busquedas = distritos.Select(busqueda), maxPaginas = MaxPaginasPortal }), ct);
                using var doc = JsonDocument.Parse(json ?? "{}");
                foreach (var e in doc.RootElement.GetProperty("errores").EnumerateArray())
                    lock (errores) errores.Add($"{fuente}, {e.GetString()}");
                foreach (var crudo in doc.RootElement.GetProperty("avisos").EnumerateArray())
                    if (DeNavent(fuente, inicio, crudo, config.TipoCambio) is { } aviso) avisos.Add(aviso);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { lock (errores) errores.Add($"Adondevivir y Urbania: {ex.Message}"); }
        return avisos;
    }

    static Aviso? DeNavent(string fuente, string inicio, JsonElement crudo, decimal tipoCambio)
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

    static async Task<List<Aviso>> Portal(Portal portal, Config config, List<Distrito> distritos, List<string> errores, List<string> registro, IProgress<string> progreso, CancellationToken ct)
    {
        var avisos = new List<Aviso>();
        var fallas = 0;
        try
        {
            await using var pestana = await Chrome.AbrirPestana("about:blank", ct);
            if (portal.Publicaciones && await Chrome.SesionFacebook(ct) != true)
            {
                lock (errores) errores.Add($"{portal.Nombre} necesita la sesión de Facebook en el Chrome de Depas");
                return avisos;
            }
            foreach (var distrito in distritos)
            {
                progreso.Report($"{portal.Nombre}: {distrito.Nombre}…");
                var primera = portal.Busqueda(distrito, config);
                string? url = primera;
                for (var pagina = 1; url != null && pagina <= portal.Paginas; pagina++)
                {
                    try
                    {
                        using var doc = await LeerPagina(pestana, url, portal.Enlace, portal.Publicaciones ? 5 : 0, ct);
                        var leidos = 0;
                        var delDistrito = 0;
                        foreach (var crudo in doc.RootElement.GetProperty("avisos").EnumerateArray())
                            if (DeTarjeta(portal.Nombre, crudo, config.TipoCambio, portal.Publicaciones) is { } aviso)
                            {
                                avisos.Add(aviso);
                                leidos++;
                                if (aviso.Distrito == distrito.Nombre) delDistrito++;
                            }
                        lock (registro) registro.Add($"{portal.Nombre} · {distrito.Nombre} · p{pagina}: {leidos} ({delDistrito} del distrito) · {doc.RootElement.GetProperty("titulo").GetString()} · {url}");
                        // Sin nada del distrito, lo que sigue es relleno de otros lados (Properati y Mitula lo hacen).
                        var siguiente = doc.RootElement.GetProperty("siguiente").GetString()
                                        ?? portal.Paginar?.Replace("{url}", primera).Replace("{n}", (pagina + 1).ToString());
                        url = delDistrito > 0 && siguiente != url ? siguiente : null;
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        if (pagina == 1) fallas++;
                        lock (registro) registro.Add($"{portal.Nombre} · {distrito.Nombre} · p{pagina}: {ex.Message} · {url}");
                        url = null;
                    }
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { fallas = distritos.Count; lock (registro) registro.Add($"{portal.Nombre}: {ex.Message}"); }
        if (fallas == distritos.Count && fallas > 0) lock (errores) errores.Add($"{portal.Nombre} no respondió");
        return avisos;
    }

    /// <summary>Hay sitios que se recargan solos al abrir (Mercado Libre): si la lectura se corta, se intenta otra vez.</summary>
    static async Task<JsonDocument> LeerPagina(Pestana pestana, string url, string enlace, int vueltas, CancellationToken ct)
    {
        for (var intento = 1; ; intento++)
        {
            try
            {
                await pestana.Ir(url, ct);
                return JsonDocument.Parse(await pestana.Evaluar(Llamar("tarjetas.js", new { enlace, vueltas }), ct) ?? "{}");
            }
            catch (InvalidOperationException) when (intento < 2) { await Task.Delay(1500, ct); }
        }
    }

    static readonly Regex RxLimaCallao = new(@"\b(lima|callao)\b");

    /// <summary>
    /// Arma el aviso con los textos sueltos de una tarjeta: el primer precio, la línea de ubicación («…, San Miguel, Lima»),
    /// el título y lo que digan de dormitorios, baños y metros (también en la dirección: «departamento-2-dorm-54-m»).
    /// En una publicación de Facebook las primeras líneas son el grupo y el autor: el aviso es el texto más largo.
    /// </summary>
    public static Aviso? DeTarjeta(string fuente, JsonElement crudo, decimal tipoCambio, bool publicacion = false)
    {
        var url = crudo.GetProperty("url").GetString()!;
        var lineas = crudo.GetProperty("texto").GetString()!.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToArray();
        var cuerpo = publicacion ? lineas.OrderByDescending(l => l.Length).FirstOrDefault() ?? "" : "";
        var plano = publicacion ? cuerpo : string.Join(" ", lineas);
        if (Texto.PrecioEn(plano, tipoCambio) is not int precio) return null;

        var titulo = publicacion ? (cuerpo.Length > 90 ? cuerpo[..90] + "…" : cuerpo)
                   : crudo.GetProperty("titulo").GetString() is { Length: > 0 } t ? t : lineas.FirstOrDefault(l => l.Length >= 15 && !Texto.EsPrecio(l)) ?? "";
        var ubicacion = publicacion ? "" : lineas.FirstOrDefault(l => l.Length < 110 && l.Contains(',') && RxLimaCallao.IsMatch(Distritos.Normalizar(l))) ?? "";
        var descripcion = publicacion ? cuerpo : lineas.Where(l => l.Length > 60 && l != titulo).OrderByDescending(l => l.Length).FirstOrDefault() ?? "";
        var ruta = Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Segments[^1].Replace('-', ' ') : "";
        var datos = $"{plano} {ruta}";
        var ahora = DateTime.Now;
        return new Aviso
        {
            Clave = $"{fuente}:{url}",
            Fuente = fuente,
            Url = url,
            Titulo = titulo,
            Descripcion = descripcion,
            Ubicacion = ubicacion,
            Imagen = crudo.GetProperty("imagen").GetString() ?? "",
            Distrito = Distritos.De(ubicacion, $"{titulo}\n{descripcion}")?.Nombre,
            Precio = precio,
            Mantenimiento = Texto.Mantenimiento(plano),
            Dormitorios = Texto.Dormitorios(datos),
            Banos = Texto.Banos(datos),
            Area = Texto.Area(plano),
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
        if (!c.Usa(a.Fuente) && !a.TambienEn.Any(c.Usa)) return false;
        if (a.Distrito is null || !c.Distritos.Contains(a.Distrito)) return false;
        return PasaPrecioYTitulo(a, c, excluidas);
    }

    static bool PasaPrecioYTitulo(Aviso a, Config c, Regex[] excluidas)
    {
        if (a.Precio is not int precio || precio < c.PrecioMin || precio > c.PrecioMax) return false;
        var titulo = Distritos.Normalizar(a.Titulo);
        return !excluidas.Any(r => r.IsMatch(titulo));
    }
}
