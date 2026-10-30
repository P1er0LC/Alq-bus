namespace Depas.Nucleo;

/// <summary>
/// Un portal que se lee con tarjetas.js: cómo se arma su búsqueda para un distrito y cómo son los enlaces a sus avisos.
/// Si el sitio filtra por precio en la dirección, todas sus páginas sirven y se leen varias; si no, se leen dos
/// (vienen mezclados con avisos caros) y el precio se filtra en la app.
/// </summary>
/// <param name="Paginar">Para los sitios que no anuncian la página siguiente: cómo se arma («{url}/{n}»).</param>
/// <param name="Publicaciones">Publicaciones de Facebook: necesitan la sesión, se leen bajando la página y el título sale del texto.</param>
public sealed record Portal(string Nombre, string Enlace, Func<Distrito, Config, string> Busqueda, bool FiltraPrecio = false,
                            string? Paginar = null, bool Publicaciones = false)
{
    public int Paginas => Publicaciones ? 1 : FiltraPrecio ? 5 : 2;
}

public static class Portales
{
    static string Prov(Distrito d) => Distritos.Normalizar(d.Provincia);

    // Primero los que publican avisos propios; al final los agregadores (Doomos, Trovit, Mitula, Nuroa),
    // que repiten avisos de los demás: cuando un aviso sale en varios, se queda el de más arriba.
    public static readonly Portal[] Todos =
    [
        new("Properati", "/detalle/",
            (d, c) => $"https://www.properati.com.pe/s/{d.Slug}/departamento/alquiler/precio:{c.PrecioMin}-{c.PrecioMax}", FiltraPrecio: true, Paginar: "{url}/{n}"),
        new("Mercado Libre", @"MPE-\d+",
            (d, c) => $"https://listado.mercadolibre.com.pe/inmuebles/departamentos/alquiler/{Prov(d)}-{d.Slug}_PriceRange_{c.PrecioMin}PEN-{c.PrecioMax}PEN_NoIndex_True", FiltraPrecio: true),
        new("Infocasas", @"infocasas\.com\.pe/[^/]+/\d{6,}$",
            (d, c) => $"https://www.infocasas.com.pe/alquiler/departamentos/{Prov(d)}/{d.Slug}"),
        new("Ubicasa", @"ubicasa\.pe/propiedades/",
            (d, c) => $"https://ubicasa.pe/buscar/alquiler/departamento/{d.Slug}-{Prov(d)}-{Prov(d)}"),
        new("Real Estate Perú", @"realestateperu\.net/propiedades/",
            (d, c) => $"https://realestateperu.net/buscar/propiedades-en-{d.Slug}"),
        new("Doomos", "/propiedad/",
            (d, c) => $"https://www.doomos.com.pe/alquiler/departamentos/{d.Slug}"),
        // El filtro de precio de Trovit solo existe en /find (es lo que arma su desplegable al hacer clic).
        new("Trovit", "/detail/",
            (d, c) => $"https://casas.trovit.com.pe/find?type=2&text={Uri.EscapeDataString(d.Nombre)}&price_min={c.PrecioMin}&price_max={c.PrecioMax}&property_type=Departamento&order_by=source_date", FiltraPrecio: true),
        // Mitula no filtra por precio desde la dirección (ignora min_price) y repite el inventario de Trovit.
        new("Mitula", "/adform/",
            (d, c) => $"https://casas.mitula.pe/casas/alquiler-departamentos-{d.Slug}"),
        new("Nuroa", "/adform/",
            (d, c) => $"https://www.nuroa.pe/alquiler/departamentos-{d.Slug}-{Prov(d)}?min_price={c.PrecioMin}&max_price={c.PrecioMax}", FiltraPrecio: true),
        // Publicaciones recientes de Facebook (grupos públicos y los de Piero), con el enlace de cada publicación.
        new("Grupos de Facebook", @"facebook\.com/(groups/[^/]+/(posts|permalink)/|[^/?]+/posts/|permalink\.php|story\.php)",
            (d, c) => $"https://www.facebook.com/search/posts/?q={Uri.EscapeDataString($"alquiler departamento {d.Nombre}")}&filters={Recientes}",
            Publicaciones: true),
    ];

    /// <summary>El filtro «Publicaciones recientes» de la búsqueda de Facebook, como lo arma su propia interfaz.</summary>
    static readonly string Recientes = Uri.EscapeDataString(Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(
        "{\"recent_posts:0\":\"{\\\"name\\\":\\\"recent_posts\\\",\\\"args\\\":\\\"\\\"}\"}")));
}
