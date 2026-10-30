using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
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
            var chip = new ToggleButton { Content = fuente, Tag = fuente, IsChecked = config.Usa(fuente) };
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
        config.FuentesApagadas = FuentesPanel.Children.OfType<ToggleButton>().Where(b => b.IsChecked != true).Select(b => (string)b.Tag).ToList();
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
        var lista = Buscador.JuntarParaMostrar(visibles).Select(j => new Fila(j.Aviso, config.InicioUltimaBusqueda, config.Distritos, j.Tambien));
        if (config.Vista == "Nuevos") lista = lista.Where(f => f.EsNuevo);
        lista = config.Orden == "Precio"
            ? lista.OrderBy(f => f.Aviso.Precio).ThenByDescending(f => f.Aviso.PrimeraVez)
            : lista.OrderByDescending(f => f.Aviso.PrimeraVez).ThenBy(f => f.Aviso.Precio);

        filas.Clear();
        foreach (var fila in lista) filas.Add(fila);
        Contar();
        Sugerir();
    }

    /// <summary>
    /// Lo que ya se encontró y queda afuera por un solo filtro: «+30 de 1 dormitorio», «+16 en Callao».
    /// Cada uno se pulsa para sumarlo. Los distritos cuentan solo lo encontrado de rebote: con Buscar trae más.
    /// </summary>
    void Sugerir()
    {
        Sugerencias.Children.Clear();
        var opciones = new List<(string Texto, Action Aplicar)>();
        if (config.Vista != "Favoritos")
        {
            int Cuantos(Action<Config> cambio)
            {
                var otra = JsonSerializer.Deserialize<Config>(JsonSerializer.Serialize(config))!;
                cambio(otra);
                var excluidas = Buscador.Excluidas(otra);
                return Buscador.JuntarParaMostrar(avisos.Where(a => Buscador.Pasa(a, otra, excluidas))).Count;
            }
            var ahora = Cuantos(_ => { });
            if (config.DormitoriosMin > 1 && Cuantos(c => c.DormitoriosMin = config.DormitoriosMin - 1) - ahora is var masDormitorio and > 0)
            {
                var menos = config.DormitoriosMin - 1;
                opciones.Add(($"+{masDormitorio} de {menos} dormitorio{(menos == 1 ? "" : "s")}", () =>
                {
                    config.DormitoriosMin = menos;
                    Marcar(Dormitorios, menos.ToString());
                }));
            }
            var cercanos = Distritos.Todos.Where(d => !config.Distritos.Contains(d.Nombre))
                .Select(d => (d.Nombre, Cuantos: Cuantos(c => c.Distritos = [d.Nombre])))
                .Where(x => x.Cuantos > 0).OrderByDescending(x => x.Cuantos).Take(4);
            foreach (var (nombre, cuantos) in cercanos)
                opciones.Add(($"+{cuantos} en {nombre}", () =>
                {
                    foreach (var chip in DistritosPrincipales.Children.OfType<ToggleButton>().Concat(DistritosOtros.Children.OfType<ToggleButton>()))
                        if ((string)chip.Tag == nombre) chip.IsChecked = true;
                    if (DistritosOtros.Visibility != Visibility.Visible) MasDistritos_Click(this, new RoutedEventArgs());
                }));
        }

        Sugerencias.Visibility = opciones.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (opciones.Count == 0) return;
        Sugerencias.Children.Add(new TextBlock
        {
            Text = "también hay:", FontFamily = (FontFamily)FindResource("Mono"), FontSize = 11,
            Foreground = (Brush)FindResource("TextoSuave"), Margin = new Thickness(0, 2, 10, 0),
        });
        foreach (var (texto, aplicar) in opciones)
        {
            var boton = new Button { Content = texto, Style = (Style)FindResource("Enlace"), Foreground = (Brush)FindResource("Nuevo"), Margin = new Thickness(0, 0, 14, 0) };
            boton.Click += (_, e) => { aplicar(); Filtro_Cambio(boton, e); };
            Sugerencias.Children.Add(boton);
        }
    }

    void Contar()
    {
        var nuevos = filas.Count(f => f.EsNuevo);
        Resumen.Text = filas.Count == 1 ? "1 departamento" : $"{filas.Count} departamentos";
        if (nuevos > 0 && config.Vista != "Nuevos") Resumen.Text += nuevos == 1 ? " · 1 nuevo" : $" · {nuevos} nuevos";
        Vacio.Text = filas.Count > 0 ? "" : avisos.Count == 0
            ? "Elige los distritos y el precio, y pulsa Buscar.\nDepas busca con un Chrome sin ventana en Marketplace, los grupos de Facebook y once portales."
            : "Nada con estos filtros. Prueba con más distritos, «1 o más» dormitorios o un precio más amplio (el precio se vuelve a buscar con Buscar).";
    }

    void MostrarSesion(bool? sesion) => Sesion.Text = sesion switch
    {
        true => "Facebook: con sesión en el Chrome de Depas.",
        false => "Facebook: sin sesión. Marketplace trae menos avisos y los grupos no se buscan; entra una vez y queda guardado.",
        null => "Facebook: se revisa la sesión al buscar.",
    };

    async void Buscar_Click(object sender, RoutedEventArgs e)
    {
        if (busqueda != null) { busqueda.Cancel(); return; }
        LeerFiltros();
        Almacen.Guardar(config);
        if (config.Distritos.Count == 0 || !Fuentes.Todas.Any(config.Usa))
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
            await Chrome.CerrarSiSinVentana();
        }
    }

    /// <summary>
    /// Lo único que abre una ventana de Chrome: entrar a Facebook. Mira cada 3 segundos si ya hay sesión y,
    /// cuando la hay, la guarda y cierra esa ventana; las búsquedas siguen sin ventanas.
    /// </summary>
    async void Facebook_Click(object sender, RoutedEventArgs e)
    {
        // Para abrir la ventana hay que cerrar el Chrome sin ventana, y eso cortaría la búsqueda.
        if (busqueda != null)
        {
            Sesion.Text = "Espera a que termine la búsqueda para entrar a Facebook.";
            return;
        }
        try
        {
            BotonFacebook.IsEnabled = false;
            var pestana = await Chrome.AbrirVentana("https://www.facebook.com/login/?next=https%3A%2F%2Fwww.facebook.com%2Fmarketplace%2F", CancellationToken.None);
            await pestana.Soltar();
            Sesion.Text = "Entra a Facebook en la ventana de Chrome que se abrió. Se cierra sola cuando termines.";
            for (var i = 0; i < 400; i++)
            {
                await Task.Delay(3000);
                var sesion = await Chrome.SesionFacebook(CancellationToken.None);
                if (sesion is null) break;
                if (sesion != true) continue;
                config.SesionFacebook = true;
                Almacen.Guardar(config);
                Sesion.Text = "Listo: la sesión de Facebook quedó guardada. Cierro esa ventana…";
                await Task.Delay(4000);
                await Chrome.Cerrar();
                break;
            }
            MostrarSesion(config.SesionFacebook);
        }
        catch (Exception ex) { Sesion.Text = ex.Message; }
        finally { BotonFacebook.IsEnabled = true; }
    }

    /// <summary>Si se cierra la app a mitad de una búsqueda, no queda un Chrome sin ventana dando vueltas.</summary>
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        busqueda?.Cancel();
        Task.Run(Chrome.CerrarSiSinVentana).Wait(TimeSpan.FromSeconds(4));
        base.OnClosing(e);
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
