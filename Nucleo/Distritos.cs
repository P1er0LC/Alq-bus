using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Depas.Nucleo;

/// <param name="Alias">Cómo aparece escrito en los avisos, ya normalizado (sin tildes, en minúsculas).</param>
public sealed record Distrito(string Nombre, string Provincia, bool Principal, params string[] Alias)
{
    /// <summary>Como lo escriben Adondevivir y Urbania en sus direcciones: «pueblo-libre», «brena».</summary>
    public string Slug => Distritos.Normalizar(Nombre).Replace(' ', '-');
}

public static class Distritos
{
    // Los cinco de la búsqueda de Piero primero; el resto son los de alrededor, por si quiere abrir el radio.
    public static readonly Distrito[] Todos =
    [
        new("Bellavista", "Callao", true, "bellavista"),
        new("La Perla", "Callao", true, "la perla"),
        new("Breña", "Lima", true, "brena"),
        new("Pueblo Libre", "Lima", true, "pueblo libre"),
        new("San Miguel", "Lima", true, "san miguel"),
        new("Jesús María", "Lima", false, "jesus maria"),
        new("Magdalena del Mar", "Lima", false, "magdalena del mar", "magdalena"),
        new("Lince", "Lima", false, "lince"),
        new("Cercado de Lima", "Lima", false, "cercado de lima", "lima cercado"),
        new("Callao", "Callao", false, "callao", "cercado del callao"),
        new("Carmen de la Legua", "Callao", false, "carmen de la legua", "carmen de la legua reynoso"),
        new("La Punta", "Callao", false, "la punta"),
        new("San Martín de Porres", "Lima", false, "san martin de porres", "smp"),
        new("Los Olivos", "Lima", false, "los olivos"),
        new("Rímac", "Lima", false, "rimac"),
        new("La Victoria", "Lima", false, "la victoria"),
        new("Surquillo", "Lima", false, "surquillo"),
        new("San Isidro", "Lima", false, "san isidro"),
        new("Miraflores", "Lima", false, "miraflores"),
        new("San Borja", "Lima", false, "san borja"),
        new("Barranco", "Lima", false, "barranco"),
        new("Santiago de Surco", "Lima", false, "santiago de surco", "surco"),
        new("Chorrillos", "Lima", false, "chorrillos"),
    ];

    /// <summary>
    /// Los otros distritos de Lima y Callao, solo para notar cuando un aviso dice «en Comas» aunque Facebook
    /// lo ubique en San Miguel (su ubicación es aproximada y a veces la pone el vendedor mal).
    /// </summary>
    static readonly string[] Resto =
    [
        "Ancón", "Ate", "Carabayllo", "Chaclacayo", "Cieneguilla", "Comas", "El Agustino", "Independencia", "La Molina",
        "Lurigancho", "Chosica", "Lurín", "Pachacámac", "Pucusana", "Puente Piedra", "Punta Hermosa", "Punta Negra",
        "San Bartolo", "San Juan de Lurigancho", "SJL", "San Juan de Miraflores", "SJM", "San Luis", "Santa Anita",
        "Santa María del Mar", "Santa Rosa", "Villa El Salvador", "VES", "Villa María del Triunfo", "Ventanilla", "Mi Perú",
    ];

    public static Distrito? Buscar(string nombre) => Todos.FirstOrDefault(d => d.Nombre == nombre);

    /// <summary>Un «en &lt;distrito&gt;» del texto que no sea ninguno de los elegidos: se muestra como alerta en la tarjeta.</summary>
    public static string? DiceOtro(string texto, IReadOnlyCollection<string> elegidos)
    {
        var normal = Normalizar(texto);
        var candidatos = Todos.Where(d => d.Nombre != "Callao" && !elegidos.Contains(d.Nombre)).SelectMany(d => d.Alias.Select(a => (d.Nombre, a)))
            .Concat(Resto.Select(n => (n, Normalizar(n))));
        foreach (var (nombre, alias) in candidatos)
            if (Regex.IsMatch(normal, $@"\ben {Regex.Escape(alias)}\b")) return nombre;
        return null;
    }

    public static string Normalizar(string texto)
    {
        var sb = new StringBuilder(texto.Length);
        foreach (var c in texto.Normalize(NormalizationForm.FormD))
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) sb.Append(char.ToLowerInvariant(c));
        // Los guiones como espacios: «Carmen de La Legua-Reynoso».
        return Regex.Replace(sb.ToString().Replace('-', ' '), @"\s+", " ").Trim();
    }

    /// <summary>
    /// El distrito de un aviso. Manda la ubicación («Bellavista, Callao, Peru», «Maranga Etapa III, San Miguel»):
    /// el primer tramo que sea un distrito conocido. Si solo dice «Callao» o «Lima», se mira el título y la
    /// descripción, porque Marketplace pone así muchos de La Perla y Bellavista.
    /// </summary>
    public static Distrito? De(string ubicacion, string texto)
    {
        var tramos = ubicacion.Split(',').Select(Normalizar).Where(t => t.Length > 0).ToArray();
        var generico = tramos.Length == 0 || tramos[0] is "callao" or "lima";
        if (generico && Mencionado(texto) is { } mencionado) return mencionado;
        foreach (var tramo in tramos)
            if (Todos.FirstOrDefault(d => d.Alias.Any(a => EsTramo(tramo, a))) is { } distrito) return distrito;
        return null;
    }

    /// <summary>«San Miguel», o «Departamento in San Miguel» / «in Bellavista» / «Apartamento en San Miguel» como escriben Trovit e Infocasas.</summary>
    static bool EsTramo(string tramo, string alias) =>
        tramo == alias || $" {tramo}".EndsWith($" en {alias}") || $" {tramo}".EndsWith($" in {alias}");

    /// <summary>El distrito que aparece primero en el texto: el título y la ubicación van antes que la descripción.</summary>
    static Distrito? Mencionado(string texto)
    {
        var normal = Normalizar(texto);
        // «Callao» a secas no cuenta: aparece en casi todos los avisos del Callao.
        return Todos.Where(d => d.Nombre != "Callao")
            .Select(d => (d, pos: d.Alias.Select(a => Regex.Match(normal, $@"\b{Regex.Escape(a)}\b")).Where(m => m.Success).Select(m => m.Index).DefaultIfEmpty(int.MaxValue).Min()))
            .Where(x => x.pos < int.MaxValue).OrderBy(x => x.pos).Select(x => x.d).FirstOrDefault();
    }
}
