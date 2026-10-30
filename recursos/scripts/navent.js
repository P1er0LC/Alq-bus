// Adondevivir y Urbania son la misma plataforma (Navent): mismas tarjetas con data-qa.
// Corre dentro de una página del portal, así fetch va con las cookies del sitio y pasa su protección.
// P.busquedas: [{ distrito, url, pagina }] donde pagina es la url con {n} para las siguientes páginas.
async (P) => {
  const avisos = [];
  const errores = [];
  const leer = async (url, distrito) => {
    const r = await fetch(url, { credentials: 'include' });
    if (!r.ok) throw new Error('HTTP ' + r.status);
    const doc = new DOMParser().parseFromString(await r.text(), 'text/html');
    // Si el portal no reconoce el distrito, redirige a todo el país: eso no sirve.
    if (/\bPer[uú]\b/.test(doc.title.split(' - ')[0] + doc.title.split(' - ')[1])) return { total: 0, cantidad: 0, generico: true };
    const total = parseInt((doc.title.match(/^([\d,]+)\s/) || ['', '0'])[1].replace(/,/g, ''), 10);
    const tarjetas = doc.querySelectorAll('[data-qa="posting PROPERTY"]');
    for (const c of tarjetas) {
      const texto = qa => c.querySelector(`[data-qa="${qa}"]`)?.textContent.trim() || '';
      const hojas = qa => [...(c.querySelector(`[data-qa="${qa}"]`)?.querySelectorAll('*') || [])]
        .filter(e => !e.children.length).map(e => e.textContent.trim()).filter(Boolean);
      const img = c.querySelector('img');
      // El alt de la foto trae el título del aviso: «Departamento de 1 habitación, Lima · Alquilo Mono Ambiente…».
      const alt = (img?.getAttribute('alt') || '').split(' · ');
      avisos.push({
        id: c.dataset.id,
        ruta: c.dataset.toPosting || '',
        titulo: alt.length > 1 ? alt.slice(1).join(' · ').trim() : '',
        precio: texto('POSTING_CARD_PRICE'),
        mantenimiento: texto('expensas'),
        rasgos: hojas('POSTING_CARD_FEATURES'),
        direccion: c.querySelector('[class*="location-address"]')?.textContent.trim() || '',
        ubicacion: texto('POSTING_CARD_LOCATION'),
        descripcion: texto('POSTING_CARD_DESCRIPTION'),
        imagen: img?.getAttribute('src') || img?.getAttribute('data-src') || '',
        distrito,
      });
    }
    return { total, cantidad: tarjetas.length };
  };

  for (const b of P.busquedas) {
    try {
      const primera = await leer(b.url, b.distrito);
      if (primera.generico) { errores.push(`${b.distrito}: el portal no reconoce el distrito`); continue; }
      const paginas = Math.min(P.maxPaginas, Math.ceil(primera.total / Math.max(primera.cantidad, 1)));
      for (let n = 2; n <= paginas; n++) await leer(b.pagina.replace('{n}', n), b.distrito);
    } catch (e) {
      errores.push(`${b.distrito}: ${e.message}`);
    }
  }
  return JSON.stringify({ avisos, errores });
}
