// AstroLab — clone stamp client-side sobre um <canvas>.
// Fluxo: definir fonte (1 clique) → arrastar sobre o halo para clonar céu limpo.
// O offset fonte→destino fixa-se no início de cada traço (como Photoshop/PixInsight).
(() => {
    let canvas, ctx, off, octx, img;
    let brush = 40, source = null, settingSource = false;
    let painting = false, offset = null, last = null;

    function toCanvas(e) {
        const r = canvas.getBoundingClientRect();
        return {
            x: (e.clientX - r.left) * (canvas.width / r.width),
            y: (e.clientY - r.top) * (canvas.height / r.height)
        };
    }

    function stamp(dx, dy) {
        const rad = brush, d = rad * 2;
        const sx = dx - offset.x, sy = dy - offset.y;
        octx.clearRect(0, 0, d, d);
        octx.save();
        octx.beginPath(); octx.arc(rad, rad, rad, 0, Math.PI * 2); octx.clip();
        octx.drawImage(canvas, sx - rad, sy - rad, d, d, 0, 0, d, d);
        octx.restore();
        octx.globalCompositeOperation = 'destination-in';
        const g = octx.createRadialGradient(rad, rad, rad * 0.5, rad, rad, rad);
        g.addColorStop(0, 'rgba(0,0,0,1)'); g.addColorStop(1, 'rgba(0,0,0,0)');
        octx.fillStyle = g; octx.fillRect(0, 0, d, d);
        octx.globalCompositeOperation = 'source-over';
        ctx.drawImage(off, dx - rad, dy - rad);
    }

    function strokeTo(p) {
        if (!last) { stamp(p.x, p.y); last = p; return; }
        const dist = Math.hypot(p.x - last.x, p.y - last.y);
        const step = Math.max(2, brush * 0.25);
        for (let t = step; t <= dist; t += step) {
            const k = t / dist;
            stamp(last.x + (p.x - last.x) * k, last.y + (p.y - last.y) * k);
        }
        stamp(p.x, p.y); last = p;
    }

    function down(e) {
        const p = toCanvas(e);
        if (settingSource || !source) { source = p; settingSource = false; window.astroClone._cursor('fonte definida — arrasta sobre o halo'); return; }
        painting = true; offset = { x: p.x - source.x, y: p.y - source.y }; last = null;
        strokeTo(p); e.preventDefault();
    }
    function move(e) { if (painting) { strokeTo(toCanvas(e)); e.preventDefault(); } }
    function up() { painting = false; last = null; }

    window.astroClone = {
        _cursor: () => { },
        init(cv, dataUrl, statusCb) {
            canvas = cv; ctx = canvas.getContext('2d');
            this._cursor = statusCb || (() => { });
            source = null; settingSource = false;
            img = new Image();
            img.onload = () => {
                canvas.width = img.naturalWidth; canvas.height = img.naturalHeight;
                ctx.drawImage(img, 0, 0);
                off = document.createElement('canvas'); off.width = off.height = 1024;
                octx = off.getContext('2d');
            };
            img.src = dataUrl;
            canvas.onpointerdown = down; canvas.onpointermove = move;
            window.onpointerup = up;
        },
        setSource() { settingSource = true; this._cursor('clica numa zona de céu limpo (fonte)'); },
        setBrush(r) { brush = +r; },
        reset() { if (img) ctx.drawImage(img, 0, 0); source = null; this._cursor('reposto'); },
        dataUrl() { return canvas.toDataURL('image/jpeg', 0.95); }
    };
})();
