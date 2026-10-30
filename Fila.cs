using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Depas.Nucleo;

namespace Depas;

/// <summary>Lo que muestra una tarjeta de la lista, armado a partir de un aviso guardado.</summary>
public sealed class Fila(Aviso aviso, DateTime inicioUltimaBusqueda, IReadOnlyCollection<string> elegidos) : INotifyPropertyChanged
{
    static readonly CultureInfo Numeros = CultureInfo.InvariantCulture;
    ImageSource? miniatura;
    readonly string? diceOtro = Distritos.DiceOtro(aviso.Titulo + "\n" + aviso.Descripcion, elegidos);

    public Aviso Aviso => aviso;
    public event PropertyChangedEventHandler? PropertyChanged;

    public bool EsNuevo => aviso.PrimeraVez >= inicioUltimaBusqueda && inicioUltimaBusqueda != default && !aviso.Abierto;
    public Visibility VerNuevo => EsNuevo ? Visibility.Visible : Visibility.Collapsed;

    public string Precio => aviso.Precio is int p ? "S/ " + p.ToString("N0", Numeros) : "Sin precio";

    public string Extra => string.Join("  ·  ", new[]
    {
        aviso.PrecioAnterior is int antes && antes > (aviso.Precio ?? 0) ? "antes S/ " + antes.ToString("N0", Numeros) : null,
        aviso.Mantenimiento is int m ? "+ S/ " + m.ToString("N0", Numeros) + " de mantenimiento" : null,
    }.OfType<string>());

    public string Titulo => aviso.Titulo;

    public string Rasgos => string.Join("  ·  ", new[]
    {
        aviso.Dormitorios is int d ? $"{d} dorm." : "dorm. ?",
        aviso.Banos is int b ? (b == 1 ? "1 baño" : $"{b} baños") : null,
        aviso.Area is int a ? $"{a} m²" : null,
    }.OfType<string>());

    public string Lugar => $"{aviso.Distrito}  ·  {aviso.Fuente}  ·  {Hace(aviso.PrimeraVez)}";

    public string Alerta => diceOtro is null ? "" : $"ojo: el aviso dice «en {diceOtro}»";
    public Visibility VerAlerta => diceOtro is null ? Visibility.Collapsed : Visibility.Visible;

    public string Descripcion => aviso.Descripcion;
    public Visibility VerDescripcion => aviso.Descripcion.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    public string Estrella => aviso.Favorito ? "★ favorito" : "☆ favorito";
    public string TextoDescartar => aviso.Descartado ? "recuperar" : "descartar";
    public double Opacidad => aviso.Descartado ? 0.45 : 1;

    public ImageSource? Miniatura => miniatura ??= Cargar(aviso.Imagen);

    /// <summary>Se baja en segundo plano (BitmapImage lo hace solo con las URL http).</summary>
    static ImageSource? Cargar(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) return null;
        try
        {
            var imagen = new BitmapImage();
            imagen.BeginInit();
            imagen.UriSource = uri;
            imagen.DecodePixelWidth = 360;
            imagen.CacheOption = BitmapCacheOption.OnLoad;
            imagen.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            imagen.EndInit();
            return imagen;
        }
        catch (Exception) { return null; }
    }

    static string Hace(DateTime cuando)
    {
        var lapso = DateTime.Now - cuando;
        if (lapso.TotalMinutes < 60) return "encontrado recién";
        if (lapso.TotalHours < 24) return $"encontrado hace {(int)lapso.TotalHours} h";
        var dias = (int)lapso.TotalDays;
        return dias == 1 ? "encontrado ayer" : $"encontrado hace {dias} días";
    }

    public void Refrescar() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
}
