using System.Text.RegularExpressions;

namespace Depas.Nucleo;

public static class Fuentes
{
    public const string Marketplace = "Marketplace";
    public const string Adondevivir = "Adondevivir";
    public const string Urbania = "Urbania";
    /// <summary>En orden de preferencia: cuando un aviso sale en varias, se muestra el de la primera.</summary>
    public static readonly string[] Todas = [Marketplace, Adondevivir, Urbania, .. Portales.Todos.Select(p => p.Nombre)];
}

public sealed class Aviso
{
    /// <summary>Fuente e id. Adondevivir y Urbania comparten avisos e ids: se guarda el primero que llega.</summary>
    public string Clave { get; set; } = "";
    public string Fuente { get; set; } = "";
    public string Url { get; set; } = "";
    public string Titulo { get; set; } = "";
    public string Descripcion { get; set; } = "";
    public string Ubicacion { get; set; } = "";
    public string Imagen { get; set; } = "";
    public string? Distrito { get; set; }
    public int? Precio { get; set; }
    public int? PrecioAnterior { get; set; }
    public int? Mantenimiento { get; set; }
    public int? Dormitorios { get; set; }
    public int? Banos { get; set; }
    public int? Area { get; set; }
    /// <summary>Otras fuentes donde salió el mismo aviso (los agregadores repiten los de los portales).</summary>
    public List<string> TambienEn { get; set; } = [];
    public DateTime PrimeraVez { get; set; }
    public DateTime UltimaVez { get; set; }
    public bool Abierto { get; set; }
    public bool Favorito { get; set; }
    public bool Descartado { get; set; }

    /// <summary>
    /// Lo que trae una búsqueda nueva pisa lo leído antes, salvo lo que solo sale de la página del aviso
    /// (la lista de Marketplace no trae descripción). Lo que marcó Piero se queda.
    /// </summary>
    public void Actualizar(Aviso nuevo)
    {
        Url = nuevo.Url;
        Titulo = nuevo.Titulo;
        if (nuevo.Descripcion.Length > 0) Descripcion = nuevo.Descripcion;
        Ubicacion = nuevo.Ubicacion;
        if (nuevo.Imagen.Length > 0) Imagen = nuevo.Imagen;
        Distrito = nuevo.Distrito;
        Precio = nuevo.Precio;
        PrecioAnterior = nuevo.PrecioAnterior;
        Mantenimiento = nuevo.Mantenimiento ?? Mantenimiento;
        Dormitorios = nuevo.Dormitorios ?? Dormitorios;
        Banos = nuevo.Banos ?? Banos;
        Area = nuevo.Area ?? Area;
        TambienEn = nuevo.TambienEn;
        UltimaVez = nuevo.UltimaVez;
    }

    /// <summary>Completa con la página del aviso lo que el título no decía.</summary>
    public void Completar(string descripcion, string detalles)
    {
        Descripcion = descripcion;
        var todo = detalles + "\n" + descripcion;
        Dormitorios ??= Texto.Dormitorios(todo);
        Banos ??= Texto.Banos(todo);
        Area ??= Texto.Area(todo);
        Mantenimiento ??= Texto.Mantenimiento(descripcion);
    }
}

/// <summary>Saca números de los textos de los avisos: precios, dormitorios, baños y metros.</summary>
public static partial class Texto
{
    [GeneratedRegex(@"^(S/|S/\.|US\$|USD|\$)\s?([\d.,]+)", RegexOptions.IgnoreCase)]
    private static partial Regex RxPrecio();

    [GeneratedRegex(@"(\d+)\s*(?:habitaci[oó]n(?:es)?|habs?\b|dormitorios?|dorms?\b|dor\b|cuartos\b|rec[aá]maras?|beds?\b|bedrooms?)", RegexOptions.IgnoreCase)]
    private static partial Regex RxDormitorios();

    [GeneratedRegex(@"\b(un|una|dos|tres|cuatro)\s+(?:habitaci[oó]n(?:es)?|dormitorios?|cuartos?)\b", RegexOptions.IgnoreCase)]
    private static partial Regex RxDormitoriosEnLetras();

    /// <summary>«1x1», «2 x 2»: dormitorios por baños, como se escribe en los avisos de Lima.</summary>
    [GeneratedRegex(@"\b([1-4])\s?x\s?([1-3])\b", RegexOptions.IgnoreCase)]
    private static partial Regex RxPorBanos();

    [GeneratedRegex(@"mantenimiento[^\d\n]{0,16}(\d{2,4})", RegexOptions.IgnoreCase)]
    private static partial Regex RxMantenimiento();

    /// <summary>«+ S/ 150 mant.», «incluye S/ 220 mantenimiento»: el monto antes de la palabra.</summary>
    [GeneratedRegex(@"S/\.?\s?(\d{2,4})\s*(?:de\s+)?(?:mantenimiento|mant\b)", RegexOptions.IgnoreCase)]
    private static partial Regex RxMantenimientoAntes();

    /// <summary>Un precio en cualquier parte del texto, para las tarjetas que no lo traen en su propia línea.</summary>
    [GeneratedRegex(@"(S/\.?|US\$|USD|\$)\s?(\d[\d.,]*)", RegexOptions.IgnoreCase)]
    private static partial Regex RxPrecioSuelto();

    [GeneratedRegex(@"(\d+)\s*(?:baños?|banos?|baths?)\b", RegexOptions.IgnoreCase)]
    private static partial Regex RxBanos();

    [GeneratedRegex(@"(\d+(?:[.,]\d+)?)\s*m(?:²|2)", RegexOptions.IgnoreCase)]
    private static partial Regex RxArea();

    public static bool EsPrecio(string linea) => RxPrecio().IsMatch(linea.Trim());

    /// <summary>«S/1.200», «S/ 1,100 · USD 320», «USD 350». Los dólares se pasan a soles con el tipo de cambio.</summary>
    public static int? Precio(string texto, decimal tipoCambio)
    {
        var tramo = texto.Split('·')[0].Trim();
        var m = RxPrecio().Match(tramo);
        if (!m.Success) return null;
        var digitos = Regex.Replace(m.Groups[2].Value, @"[.,]\d{1,2}$", "").Replace(".", "").Replace(",", "");
        if (!int.TryParse(digitos, out var monto)) return null;
        return m.Groups[1].Value.StartsWith("S", StringComparison.OrdinalIgnoreCase) ? monto : (int)Math.Round(monto * tipoCambio);
    }

    public static int? Dormitorios(string texto)
    {
        var m = RxDormitorios().Match(texto);
        if (m.Success && int.TryParse(m.Groups[1].Value, out var n) && n is > 0 and < 10) return n;
        var letras = RxDormitoriosEnLetras().Match(texto);
        if (letras.Success) return letras.Groups[1].Value.ToLowerInvariant() switch { "un" or "una" => 1, "dos" => 2, "tres" => 3, _ => 4 };
        var por = RxPorBanos().Match(texto);
        return por.Success ? int.Parse(por.Groups[1].Value) : null;
    }

    public static int? Mantenimiento(string texto)
    {
        var m = RxMantenimientoAntes().Match(texto);
        if (!m.Success) m = RxMantenimiento().Match(texto);
        return m.Success && int.TryParse(m.Groups[1].Value, out var n) && n < 2000 ? n : null;
    }

    /// <summary>
    /// El primer precio del texto que no sea el mantenimiento («+ S/ 150 mant.») ni un precio por metro («17 PEN/m²»).
    /// </summary>
    public static int? PrecioEn(string texto, decimal tipoCambio)
    {
        foreach (Match m in RxPrecioSuelto().Matches(texto))
        {
            var despues = texto.Substring(m.Index + m.Length, Math.Min(16, texto.Length - m.Index - m.Length)).ToLowerInvariant();
            var antes = texto.Substring(Math.Max(0, m.Index - 3), Math.Min(3, m.Index));
            var porMetro = despues.TrimStart().StartsWith("/m²") || despues.TrimStart().StartsWith("/m2");
            if (despues.Contains("mant") || porMetro || antes.Contains('+')) continue;
            if (Precio(m.Value, tipoCambio) is int precio and > 0) return precio;
        }
        return null;
    }

    public static int? Banos(string texto)
    {
        if (RxBanos().Match(texto) is { Success: true } m && int.TryParse(m.Groups[1].Value, out var n) && n < 10) return n;
        return RxPorBanos().Match(texto) is { Success: true } por ? int.Parse(por.Groups[2].Value) : null;
    }

    public static int? Area(string texto) =>
        RxArea().Match(texto) is { Success: true } m && decimal.TryParse(m.Groups[1].Value.Replace(',', '.'), System.Globalization.CultureInfo.InvariantCulture, out var n) && n is > 5 and < 2000 ? (int)n : null;
}
