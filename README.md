# AstroLab

Aplicação local **.NET 10 / Blazor Server** para processamento interativo de stacks
lineares de astrofotografia (`Autosave.tif` do DeepSkyStacker). Abres um TIF, ajustas o
processamento com sliders em tempo real sobre um preview, e exportas em full-res
(TIF 16-bit + JPEG).

## Funcionalidades

- **Prompt de drizzle ao abrir** — escolhe Nenhum / 2× / 3×; imagens drizzladas ou
  sobreamostradas são reamostradas (÷N, por área) antes do processamento.
- **Fase A** (1× ao abrir): normalização, crop, extração de background (polinómio de 2ª
  ordem + termos radiais para vinhetagem, com fit robusto), calibração de cor, e
  **redução de ruído linear opcional** (pré-stretch).
- **Fase B** (tempo real sobre proxy 1536 px, ~100 ms/render): stretch arcsinh + MTF,
  SCNR, saturação seletiva, black point.
- **Redução de ruído** mascarada (bilateral + gaussiano) com pré-visualização a 100%.
- **Inspetor de campo 1:1** — grelha 3×3 (cantos, bordas e centro) que se adapta à janela.
- **Separação de estrelas** (StarNet/darkstar via ONNX) com workflow de fundo:
  clone stamp, ganho, saturação e **contraste local** no fundo sem estrelas;
  **redução (erosão) e saturação** na camada de estrelas; recombinação por *screen*.
- **Deconvolução estelar por IA** (classe BlurXTerminator), via ONNX Runtime + DirectML (GPU).
- **Export** TIF 16-bit (deflate) + JPEG q93, com barra de progresso.
- Abertura por **caminho**, **diálogo nativo** ou **upload**; tema escuro; recentes.

## Requisitos

- **Windows x64** (depende de `OpenCvSharp4.runtime.win`, ONNX Runtime DirectML e do
  diálogo nativo `comdlg32`).
- **.NET 10 SDK** — https://dotnet.microsoft.com/download
- **GPU compatível com DirectX 12** (recomendado) para a deconvolução por IA e a separação
  de estrelas. Sem GPU, correm em CPU (lento); o resto da app não precisa de GPU.

## Compilar e correr

```bash
# a partir da raiz do projeto
dotnet run -c Release
```

A app abre automaticamente o browser em **http://localhost:5151**. Para parar: `Ctrl+C`.

Em Windows, podes também fazer **duplo-clique em `AstroLab.cmd`**.

O primeiro arranque restaura os pacotes NuGet e compila (~10–20 s); os seguintes são rápidos.

> **Nota:** a app fixa a porta `localhost:5151` e ativa os *static web assets* no código, por
> isso funciona em qualquer ambiente (Development ou Production) e em qualquer forma de
> lançamento (`dotnet run`, DLL, ou exe publicado).

### Publicar (opcional)

```bash
dotnet publish -c Release -r win-x64 --self-contained
```

## Utilização

1. Indica o caminho do `Autosave.tif` (ou usa **📂 Procurar** / **upload**) e clica **Abrir**.
2. Responde ao prompt de **drizzle** (Nenhum / 2× / 3×).
3. Aguarda a Fase A (barra de progresso no preview).
4. Ajusta os sliders (Stretch, Céu, Black point, SCNR, Saturação, NR, NR linear).
5. Opcional: **Separar estrelas** e afinar o fundo (clone stamp, ganho, saturação,
   contraste local) e as estrelas (redução, saturação).
6. Usa **Inspeção 1:1** para avaliar foco/estrelas e ligar a **Deconvolução IA**.
7. **Exportar** gera `{prefixo}_16bit.tif` e `{prefixo}.jpg`.

### Modos CLI de teste

```bash
dotnet run -c Release -- tiff-test    <Autosave.tif>   # I/O + round-trip
dotnet run -c Release -- phasea       <Autosave.tif>   # medianas pós-background
dotnet run -c Release -- phaseb       <Autosave.tif>   # JPEG do proxy (defaults)
dotnet run -c Release -- bench        <Autosave.tif>   # tempos da Fase B
dotnet run -c Release -- startest     <Autosave.tif>   # separação de estrelas
dotnet run -c Release -- resample                       # resample por drizzle (sintético)
dotnet run -c Release -- nrlinear                       # NR linear (sintético)
dotnet run -c Release -- starproc                       # contraste local (sintético)
dotnet run -c Release -- reducestars                    # redução de estrelas (sintético)
```

## Estrutura

```
Components/      páginas e componentes Blazor (Editor, SliderControl)
Services/        TiffIO, AstroPipeline (núcleo), PreviewRenderer, ExportService,
                 ProcessingSession, StarRemoval/StarWorkflow, AiSharpen (ONNX),
                 NativeFileDialog, SelfTest
Models/          modelos ONNX (separação de estrelas e deconvolução estelar)
SPEC/            especificação do projeto
wwwroot/         tema (app.css), favicon, JS
```

## Licença

Copyright (C) 2026 hyperbyte (https://github.com/hyperbyte)

Este programa é software livre: podes redistribuí-lo e/ou modificá-lo nos termos da
**GNU General Public License versão 3** (GPL-3.0), conforme publicada pela Free Software
Foundation. Este programa é distribuído na expectativa de ser útil, mas **SEM QUALQUER
GARANTIA**. Vê o ficheiro [`LICENSE`](LICENSE) para o texto completo.

Os componentes de terceiros abaixo mantêm as suas próprias licenças (todas compatíveis
com a GPL-3.0); os respetivos avisos de copyright são preservados.

### Atribuições e licenças de terceiros

Esta aplicação usa componentes de terceiros, com gratidão:

| Componente | Versão | Licença | Fonte |
|---|---|---|---|
| .NET / ASP.NET Core / Blazor | 10 | MIT | https://github.com/dotnet |
| OpenCvSharp4 (+ runtime.win) | 4.13.0 | Apache-2.0 | https://github.com/shimat/opencvsharp |
| OpenCV (binários nativos) | 4.x | Apache-2.0 | https://opencv.org |
| BitMiracle.LibTiff.NET | 2.4.660 | BSD (estilo libtiff) | https://github.com/BitMiracle/libtiff.net |
| Microsoft.ML.OnnxRuntime.DirectML | 1.24.4 | MIT | https://github.com/microsoft/onnxruntime |
| DirectML (redistribuível Microsoft) | — | Microsoft (via ONNX Runtime) | https://github.com/microsoft/DirectML |
| Bootstrap (template, em `wwwroot/lib`) | 5.x | MIT | https://github.com/twbs/bootstrap |

**Modelos de IA:** os modelos de separação de estrelas e de *sharpening* estelar em
`Models/` provêm do projeto **Cosmic Clarity** de Seti Astro (**licença MIT**), incluídos
para conveniência. Fonte: https://github.com/setiastro/cosmicclarity

> As licenças MIT/Apache/BSD exigem a preservação dos respetivos avisos de copyright. Os
> textos completos das licenças estão disponíveis nos repositórios acima.
