using System.Text.Encodings.Web;
using System.Text.Json;

namespace Depas.Nucleo;

public sealed class Config
{
    public List<string> Distritos { get; set; } = Nucleo.Distritos.Todos.Where(d => d.Principal).Select(d => d.Nombre).ToList();
    public int PrecioMin { get; set; } = 1000;
    public int PrecioMax { get; set; } = 1200;
    /// <summary>Para 3 personas: 2 dormitorios o más.</summary>
    public int DormitoriosMin { get; set; } = 2;
    /// <summary>Muchos avisos de Marketplace no dicen cuántos dormitorios tienen: por defecto se muestran.</summary>
    public bool SinDormitorios { get; set; } = true;
    /// <summary>Las fuentes que Piero apagó: así las que se agreguen en versiones nuevas quedan prendidas.</summary>
    public List<string> FuentesApagadas { get; set; } = [];
    /// <summary>Si el título tiene alguna de estas, el aviso no se muestra: cuartos, minidepas, oficinas, ventas.</summary>
    public string Excluir { get; set; } =
        "roommate, solo habitación, alquilo cuarto, alquiler de cuarto, se alquila cuarto, cuarto para, habitación para, habitación con, " +
        "habitación c, habitación amoblada, habitación independiente, alquilo habitación, se alquila habitación, " +
        "alquiler de habitación, mini, minidepa, minidepartamento, nano, monoambiente, estudiante, para pareja, pareja sola, " +
        "busco, buscamos, necesito, " +
        "oficina, local comercial, consultorio, palco, cochera, almacén, terreno, en venta, para una persona, para 1 persona";
    public bool MostrarDescartados { get; set; }
    public string Vista { get; set; } = "Todos";
    public string Orden { get; set; } = "Recientes";
    public decimal TipoCambio { get; set; } = 3.4m;
    /// <summary>Lo que se vio en la última búsqueda, para mostrarlo al abrir la app sin despertar a Chrome.</summary>
    public bool? SesionFacebook { get; set; }
    /// <summary>Los avisos que aparecieron por primera vez desde aquí son los «nuevos».</summary>
    public DateTime InicioUltimaBusqueda { get; set; }

    public bool Usa(string fuente) => !FuentesApagadas.Contains(fuente);

    public const int VersionActual = 2;
    public int Version { get; set; }

    /// <summary>A una config de una versión anterior le suma las palabras a ocultar que se agregaron después.</summary>
    public void Actualizar()
    {
        if (Version < 2)
        {
            var tiene = PalabrasExcluidas().ToHashSet();
            var nuevas = new[] { "busco", "buscamos", "necesito", "nano" }.Where(p => !tiene.Contains(p)).ToList();
            if (nuevas.Count > 0) Excluir = Excluir.TrimEnd(' ', ',') + ", " + string.Join(", ", nuevas);
        }
        Version = VersionActual;
    }

    public IEnumerable<string> PalabrasExcluidas() =>
        Excluir.Split(',', '\n').Select(p => Nucleo.Distritos.Normalizar(p)).Where(p => p.Length > 0);
}

/// <summary>
/// Todo vive en %LOCALAPPDATA%\Depas (o en DEPAS_DATOS, para probar sin tocar lo de Piero):
/// config.json, avisos.json y el perfil de Chrome.
/// </summary>
public static class Almacen
{
    public static string Carpeta { get; } = Environment.GetEnvironmentVariable("DEPAS_DATOS") is { Length: > 0 } datos
        ? datos
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Depas");

    static readonly JsonSerializerOptions opciones = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    static string RutaConfig => Path.Combine(Carpeta, "config.json");
    static string RutaAvisos => Path.Combine(Carpeta, "avisos.json");

    public static Config LeerConfig()
    {
        if (Leer<Config>(RutaConfig) is not { } config) return new Config { Version = Config.VersionActual };
        if (config.Version < Config.VersionActual) config.Actualizar();
        return config;
    }
    public static void Guardar(Config config) => Escribir(RutaConfig, config);

    public static List<Aviso> LeerAvisos()
    {
        var avisos = Leer<List<Aviso>>(RutaAvisos) ?? [];
        // Lo que se saca del texto se vuelve a sacar al abrir: si la lectura mejora (el «1x1» ya no cuenta como
        // 1 dormitorio), los avisos guardados se corrigen sin tener que volver a encontrarlos.
        foreach (var a in avisos.Where(a => a.Fuente == Fuentes.Marketplace)) a.Releer();
        return avisos;
    }
    public static void Guardar(List<Aviso> avisos) => Escribir(RutaAvisos, avisos);

    static T? Leer<T>(string ruta)
    {
        try { return File.Exists(ruta) ? JsonSerializer.Deserialize<T>(File.ReadAllText(ruta), opciones) : default; }
        catch (JsonException) { return default; }
    }

    static void Escribir<T>(string ruta, T valor)
    {
        Directory.CreateDirectory(Carpeta);
        var temporal = ruta + ".tmp";
        File.WriteAllText(temporal, JsonSerializer.Serialize(valor, opciones));
        File.Move(temporal, ruta, overwrite: true);
    }

    /// <summary>Junta lo encontrado con lo guardado. Devuelve cuántos avisos no se habían visto nunca.</summary>
    public static int Mezclar(List<Aviso> guardados, IEnumerable<Aviso> encontrados)
    {
        var porClave = guardados.ToDictionary(a => a.Clave);
        var nuevos = 0;
        foreach (var aviso in encontrados)
        {
            if (porClave.TryGetValue(aviso.Clave, out var previo)) { previo.Actualizar(aviso); continue; }
            porClave[aviso.Clave] = aviso;
            guardados.Add(aviso);
            nuevos++;
        }
        return nuevos;
    }
}
