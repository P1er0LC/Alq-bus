// Lee una búsqueda de Marketplace ya abierta en la pestaña. Baja la página para que cargue más avisos
// y devuelve, por aviso, las líneas de su tarjeta: precio, precio anterior (si bajó), título y ubicación.
// No depende de clases de Facebook (cambian seguido): solo de los enlaces /marketplace/item/<id>.
async (P) => {
  const esperar = ms => new Promise(r => setTimeout(r, ms));
  const tarjetas = () => document.querySelectorAll('a[href*="/marketplace/item/"]');

  for (let i = 0; i < 40 && tarjetas().length === 0; i++) await esperar(250);

  let antes = -1;
  for (let i = 0; i < P.vueltas && tarjetas().length !== antes; i++) {
    antes = tarjetas().length;
    window.scrollTo(0, document.body.scrollHeight);
    await esperar(1600);
  }

  const avisos = new Map();
  for (const a of tarjetas()) {
    const id = (a.getAttribute('href').match(/item\/(\d+)/) || [])[1];
    if (!id) continue;
    // Facebook repite cada tarjeta: una visible y otra sin saltos de línea. Se queda la que tenga más líneas.
    const lineas = a.innerText.split('\n').map(s => s.trim()).filter(Boolean);
    const previo = avisos.get(id);
    if (previo && previo.lineas.length >= lineas.length) continue;
    avisos.set(id, { id, lineas, imagen: a.querySelector('img')?.src || '' });
  }
  return JSON.stringify({ url: location.href, avisos: [...avisos.values()] });
}
