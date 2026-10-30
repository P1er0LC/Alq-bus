// Lee la página de un aviso de Marketplace: la descripción completa y, si los tiene, los detalles de la unidad
// (habitaciones, baños). Sirve con Facebook en español o en inglés.
async (P) => {
  const esperar = ms => new Promise(r => setTimeout(r, ms));
  const principal = () => document.querySelector('[role="main"]');
  const listo = () => principal() && /\n(Descripción|Description|Detalles|Details)\n/.test(principal().innerText);
  for (let i = 0; i < 40 && !listo(); i++) await esperar(250);
  const main = principal();
  if (!main) return JSON.stringify({ descripcion: '', detalles: '' });

  // «Ver más» abre el resto de la descripción.
  const ver = [...main.querySelectorAll('[role="button"], span')].find(e => /^(Ver más|See more)$/.test((e.innerText || '').trim()));
  if (ver) { ver.click(); await esperar(500); }

  const texto = '\n' + main.innerText + '\n';
  const entre = (desde, hasta) => {
    const m = texto.match(desde);
    if (!m) return '';
    const resto = texto.slice(m.index + m[0].length);
    const fin = resto.search(hasta);
    return (fin < 0 ? resto : resto.slice(0, fin)).trim();
  };
  return JSON.stringify({
    descripcion: entre(/\n(Descripción|Description)\n/, /\n(Ver menos|See less|Información del vendedor|Seller information|Detalles del vendedor|Seller details)\s*\n/),
    detalles: entre(/\n(Detalles de la unidad|Unit details|Detalles|Details)\n/, /\n(Descripción|Description|Ubicación|Location)/),
  });
}
