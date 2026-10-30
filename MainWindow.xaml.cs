using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Depas.Nucleo;

namespace Depas;

public partial class MainWindow : Window
{
    readonly Config config = Almacen.LeerConfig();
    readonly List<Aviso> avisos = Almacen.LeerAvisos();
    readonly ObservableCollection<Fila> filas = [];
    CancellationTokenSource? busqueda;
    bool cargando = true;

    public MainWindow()
    {
        InitializeComponent();
        Lista.ItemsSource = filas;

        foreach (var distrito in Distritos.Todos)
        {
            var chip = new ToggleButton { Content = distrito.Nombre, Tag = distrito.Nombre, IsChecked = config.Distritos.Contains(distrito.Nombre) };
            chip.Click += Filtro_Cambio;
            (distrito.Principal ? DistritosPrincipales : DistritosOtros).Children.Add(chip);
        }
        if (Distritos.Todos.Any(d => !d.Principal && config.Distritos.Contains(d.Nombre))) MasDistritos_Click(this, new RoutedEventArgs());
        foreach (var fuente in Fuentes.Todas)
        {
            var chip = new ToggleButton { Content = fuente, Tag = fuente, IsChecked = config.Fuentes.Contains(fuente) };
            chip.Click += Filtro_Cambio;
            FuentesPanel.Children.Add(chip);
        }

        PrecioMin.Text = config.PrecioMin.ToString();
        PrecioMax.Text = config.PrecioMax.ToString();
        SinDormitorios.IsChecked = config.SinDormitorios;
        Excluir.Text = config.Excluir;
        MostrarDescartados.IsChecked = config.MostrarDescartados;
        Marcar(Dormitorios, config.DormitoriosMin.ToString());
        Marcar(Vistas, config.Vista);
        Marcar(Ordenes, config.Orden);
        MostrarSesion(config.SesionFacebook);
        Estado.Text = config.InicioUltimaBusqueda == default
            ? "Todavía no buscaste nada."
            : $"Última búsqueda: {config.InicioUltimaBusqueda:dd/MM HH:mm}";

        cargando = false;
        Refrescar();
    }

    static void Marcar(Panel grupo, string valor)
    {
        foreach (var boton in grupo.Children.OfType<ToggleButton>()) boton.IsChecked = (string)boton.Tag == valor;
    }

    void LeerFiltros()
    {
        config.Distritos = DistritosPrincipales.Children.OfType<ToggleButton>().Concat(DistritosOtros.Children.OfType<ToggleButton>())
            .Where(b => b.IsChecked == true).Select(b => (string)b.Tag).ToList();
        config.Fuentes = FuentesPanel.Children.OfType<ToggleButton>().Where(b => b.IsChecked == true).Select(b => (string)b.Tag).ToList();
        if (int.TryParse(PrecioMin.Text.Trim(), out var min)) config.PrecioMin = min;
        if (int.TryParse(PrecioMax.Text.Trim(), out var max)) config.PrecioMax = max;
        config.SinDormitorios = SinDormitorios.IsChecked == true;
        config.Excluir = Excluir.Text;
        config.MostrarDescartados = MostrarDescartados.IsChecked == true;
    }

    void Filtro_Cambio(object sender, RoutedEventArgs e)
    {
        if (cargando) return;
        LeerFiltros();
        Almacen.Guardar(config);
        Refrescar();
    }

    void Dormitorios_Click(object sender, RoutedEventArgs e)
    {
        config.DormitoriosMin = int.Parse((string)((FrameworkElement)sender).Tag);
        Marcar(Dormitorios, config.DormitoriosMin.ToString());
        Filtro_Cambio(sender, e);
    }

    void Vista_Click(object sender, RoutedEventArgs e)
    {
        config.Vista = (string)((FrameworkElement)sender).Tag;
        Marcar(Vistas, config.Vista);
        Filtro_Cambio(sender, e);
    }

    void Orden_Click(object sender, RoutedEventArgs e)
    {
        config.Orden = (string)((FrameworkElement)sender).Tag;
        Marcar(Ordenes, config.Orden);
        Filtro_Cambio(sender, e);
    }

    void MasDistritos_Click(object sender, RoutedEventArgs e)
    {
        var abrir = DistritosOtros.Visibility != Visibility.Visible;
        DistritosOtros.Visibility = abrir ? Visibility.Visible : Visibility.Collapsed;
        BotonMasDistritos.Content = abrir ? "− menos distritos" : "+ más distritos";
    }

    void Refrescar()
    {
        var excluidas = Buscador.Excluidas(config);
        IEnumerable<Aviso> visibles = config.Vista == "Favoritos"
            ? avisos.Where(a => a.Favorito && (!a.Descartado || config.MostrarDescartados))
            : avisos.Where(a => Buscador.Pasa(a, config, excluidas));
        var lista = visibles.Select(a => new Fila(a, config.InicioUltimaBusqueda, config.Distritos));
        if (config.Vista == "Nuevos") lista = lista.Where(f => f.EsNuevo);
        lista = config.Orden == "Precio"
            ? lista.OrderBy(f => f.Aviso.Precio).ThenByDescending(f => f.Aviso.PrimeraVez)
            : lista.OrderByDescending(f => f.Aviso.PrimeraVez).ThenBy(f => f.Aviso.Precio);

        filas.Clear();
        foreach (var fila in lista) filas.Add(fila);
        Contar();
    }

    void Contar()
    {
        var nuevos = filas.Count(f => f.EsNuevo);
        Resumen.Text = filas.Count == 1 ? "1 departamento" : $"{filas.Count} departamentos";
        if (nuevos > 0 && config.Vista != "Nuevos") Resumen.Text += nuevos == 1 ? " · 1 nuevo" : $" · {nuevos} nuevos";
        Vacio.Text = filas.Count > 0 ? "" : avisos.Count == 0
            ? "Elige los distritos y el precio, y pulsa Buscar.\nDepas abre su propio Chrome para buscar en Marketplace, Adondevivir y Urbania."
            : "Nada con estos filtros. Prueba con más distritos, «1 o más» dormitorios o un precio más amplio (el precio se vuelve a buscar con Buscar).";
    }

    void MostrarSesion(bool? sesion) => Sesion.Text = sesion switch
    {
        true => "Facebook: con sesión en el Chrome de Depas.",
        false => "Facebook: sin sesión. Marketplace muestra menos avisos; entra una vez y queda guardado.",
        null => "Facebook: se revisa la sesión al buscar.",
    };

    async void Buscar_Click(object sender, RoutedEventArgs e)
    {
        if (busqueda != null) { busqueda.Cancel(); return; }
        LeerFiltros();
        Almacen.Guardar(config);
        if (config.Distritos.Count == 0 || config.Fuentes.Count == 0)
        {
            Estado.Text = "Elige al menos un distrito y una fuente.";
            return;
        }

        busqueda = new CancellationTokenSource();
        BotonBuscar.Content = "Detener";
        var inicio = DateTime.Now;
        var progreso = new Progress<string>(texto => Estado.Text = texto);
        try
        {
            var configBusqueda = Almacen.LeerConfig();
            var conDetalle = avisos.Where(a => a.Descripcion.Length > 0).Select(a => a.Clave).ToHashSet();
            var resultado = await Task.Run(() => Buscador.Buscar(configBusqueda, conDetalle, progreso, busqueda.Token));
            var nuevos = Almacen.Mezclar(avisos, resultado.Avisos);
            config.InicioUltimaBusqueda = inicio;
            if (resultado.SesionFacebook is bool sesion) config.SesionFacebook = sesion;
            Almacen.Guardar(avisos);
            Almacen.Guardar(config);
            MostrarSesion(config.SesionFacebook);
            Estado.Text = $"{inicio:HH:mm} · {resultado.Avisos.Count} avisos leídos, {nuevos} nunca vistos"
                        + (resultado.Errores.Count > 0 ? " · " + string.Join(" · ", resultado.Errores) : "");
            Estado.ToolTip = resultado.Errores.Count > 0 ? string.Join("\n", resultado.Errores) : null;
        }
        catch (OperationCanceledException) { Estado.Text = "Búsqueda detenida."; }
        catch (Exception ex) { Estado.Text = ex.Message; }
        finally
        {
            busqueda = null;
            BotonBuscar.Content = "Buscar";
            Refrescar();
        }
    }

    async void Facebook_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var pestana = await Chrome.AbrirPestana("https://www.facebook.com/marketplace/", alFrente: true, CancellationToken.None);
            await pestana.Soltar();
            Sesion.Text = "Inicia sesión en la ventana de Chrome que se abrió. Al terminar, vuelve y pulsa Buscar.";
        }
        catch (Exception ex) { Sesion.Text = ex.Message; }
    }

    static Fila? FilaDe(object sender) => (sender as FrameworkElement)?.DataContext as Fila;

    void Abrir(Fila fila)
    {
        Process.Start(new ProcessStartInfo(fila.Aviso.Url) { UseShellExecute = true });
        fila.Aviso.Abierto = true;
        Almacen.Guardar(avisos);
        fila.Refrescar();
    }

    void Abrir_Click(object sender, RoutedEventArgs e)
    {
        if (FilaDe(sender) is { } fila) Abrir(fila);
    }

    void Lista_DobleClic(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.OriginalSource is FrameworkElement { DataContext: Fila fila }) Abrir(fila);
    }

    void Favorito_Click(object sender, RoutedEventArgs e)
    {
        if (FilaDe(sender) is not { } fila) return;
        fila.Aviso.Favorito = !fila.Aviso.Favorito;
        Almacen.Guardar(avisos);
        fila.Refrescar();
        if (config.Vista == "Favoritos" && !fila.Aviso.Favorito) { filas.Remove(fila); Contar(); }
    }

    void Descartar_Click(object sender, RoutedEventArgs e)
    {
        if (FilaDe(sender) is not { } fila) return;
        fila.Aviso.Descartado = !fila.Aviso.Descartado;
        Almacen.Guardar(avisos);
        fila.Refrescar();
        if (fila.Aviso.Descartado && !config.MostrarDescartados) { filas.Remove(fila); Contar(); }
    }
}
