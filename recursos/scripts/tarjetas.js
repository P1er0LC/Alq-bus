// Lector general de listados, para los portales sin lector propio. Encuentra cada aviso por su enlace
// (P.enlace, una expresión regular), sube hasta el bloque más grande que no tenga enlaces a otro aviso
// (esa es la tarjeta) y devuelve sus textos, la foto y el título. No depende de las clases de cada sitio.
async (P) => {
  const esperar = ms => new Promise(r => setTimeout(r, ms));
  const patron = new RegExp(P.enlace);
  // Sin la parte de rastreo (?…), salvo en las páginas .php de Facebook, donde el id va ahí (permalink.php?story_fbid=…).
  const limpio = h => {
    try {
      const u = new URL(h, location.href);
      if (!u.pathname.endsWith('.php')) return u.origin + u.pathname;
      const id = ['story_fbid', 'id'].map(k => u.searchParams.get(k)).filter(Boolean).join('&');
      return `${u.origin}${u.pathname}?${id}`;
    } catch (e) { return ''; }
  };
  // Los portales de Lifull (Mitula, Nuroa) no usan enlaces: el destino va en base64 en data-clickdestination.
  const destino = el => {
    if (el.matches('a[href]')) return el.href;
    try { return new URL(atob(el.getAttribute('data-clickdestination')), location.href).href; } catch (e) { return ''; }
  };
  const enlaces = (raiz = document) => [...raiz.querySelectorAll('a[href], [data-clickdestination]')].filter(e => patron.test(destino(e)));

  for (let i = 0; i < 48 && enlaces().length === 0; i++) await esperar(250);
  // Bajar de a poco hace que carguen las fotos diferidas y, en algunos sitios, más avisos.
  for (let i = 1; i <= 5; i++) { window.scrollTo(0, document.body.scrollHeight * i / 5); await esperar(450); }
  // En las listas sin fin (las publicaciones de Facebook) se sigue bajando mientras aparezcan más.
  let antes = -1;
  for (let i = 0; i < (P.vueltas || 0) && enlaces().length !== antes; i++) {
    antes = enlaces().length;
    window.scrollTo(0, document.body.scrollHeight);
    await esperar(1800);
  }
  // Las publicaciones largas vienen cortadas: «Ver más» las abre (el precio y el distrito suelen ir al final).
  if (P.vueltas) for (const b of document.querySelectorAll('[role="button"]')) if (/^(Ver más|See more)$/.test((b.innerText || '').trim())) b.click();
  if (P.vueltas) await esperar(600);

  const otros = el => new Set(enlaces(el).map(e => limpio(destino(e)))).size;
  const esPagina = el => el.querySelector('h1, nav, header, form, select');
  // Texto por nodos y no innerText: hay sitios que pegan el precio con la fecha («US$ 42030 oct.»).
  const textos = el => {
    const recorrido = document.createTreeWalker(el, NodeFilter.SHOW_TEXT, {
      acceptNode: n => n.parentElement.closest('script, style, noscript, svg') ? NodeFilter.FILTER_REJECT : NodeFilter.FILTER_ACCEPT,
    });
    const lineas = [];
    for (let n = recorrido.nextNode(); n; n = recorrido.nextNode()) {
      const t = n.textContent.replace(/\s+/g, ' ').trim();
      if (t) lineas.push(t);
    }
    return lineas.join('\n');
  };

  const avisos = new Map();
  for (const e of enlaces()) {
    const url = limpio(destino(e));
    if (!url || avisos.has(url)) continue;
    let tarjeta = e;
    while (tarjeta.parentElement && tarjeta.parentElement !== document.body && !tarjeta.matches('li, article, [role="listitem"]')
           && otros(tarjeta.parentElement) <= 1 && !esPagina(tarjeta.parentElement)) tarjeta = tarjeta.parentElement;
    const texto = textos(tarjeta);
    if (!/\d/.test(texto)) continue;
    const img = [...tarjeta.querySelectorAll('img')].find(i => !/logo|avatar|icon/i.test(i.className + i.alt + (i.src || '')));
    let foto = img ? (img.currentSrc || img.getAttribute('data-src') || img.getAttribute('data-lazy') || img.src || '') : '';
    if (foto.startsWith('data:')) foto = (img.getAttribute('srcset') || img.getAttribute('data-srcset') || '').split(' ')[0];
    const titulo = tarjeta.querySelector('h1, h2, h3, h4')?.innerText.trim() || e.getAttribute('title') || img?.alt || '';
    avisos.set(url, { url, titulo, texto: texto.slice(0, 2500), imagen: foto || '' });
  }
  // La página siguiente: la que el sitio anuncia para los buscadores, o el enlace «Siguiente» / «›».
  const siguiente = document.querySelector('link[rel="next"], a[rel="next"]')?.href
    || [...document.querySelectorAll('a[href]')].find(a => /^(siguiente|next|›|»)$/i.test((a.innerText || '').trim())
         || /siguiente|next page|página siguiente/i.test((a.getAttribute('aria-label') || '') + (a.getAttribute('title') || '')))?.href
    || null;
  return JSON.stringify({ url: location.href, titulo: document.title, siguiente, avisos: [...avisos.values()] });
}
