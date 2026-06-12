// AstroLab — utilitários de cliente.
window.astroLab = {
    // Fração [fx,fy] (0–1) do ponto clicado DENTRO do conteúdo da imagem,
    // corrigindo a letterbox do object-fit: contain.
    clickFrac: (img, clientX, clientY) => {
        const r = img.getBoundingClientRect();
        const nW = img.naturalWidth, nH = img.naturalHeight;
        if (!nW || !nH) return [0.5, 0.5];
        const scale = Math.min(r.width / nW, r.height / nH);
        const cW = nW * scale, cH = nH * scale;
        const ox = clientX - r.left - (r.width - cW) / 2;
        const oy = clientY - r.top - (r.height - cH) / 2;
        const clamp = v => Math.max(0, Math.min(1, v));
        return [clamp(ox / cW), clamp(oy / cH)];
    },

    // Tamanho de célula (px) para o inspetor 3×3 caber no maior quadrado da área
    // disponível, reservando espaço para o chrome do modal (cabeçalho+rodapé).
    inspectorCell: (gutter) => {
        const availW = window.innerWidth * 0.96 - 8;
        const availH = window.innerHeight * 0.94 - 130;
        const side = Math.max(180, Math.min(availW, availH));
        return Math.floor((side - 2 * gutter) / 3);
    }
};
