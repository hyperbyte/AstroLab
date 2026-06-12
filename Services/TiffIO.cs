// AstroLab — I/O de TIFF (Tarefa 1).
// Leitura: float32 OU uint16, RGB, CONTIG ou SEPARATE. Fallback OpenCV.
// Escrita: 16-bit uint RGB, compressão deflate. Ver SPEC/05-TASKS §1, SPEC/03 §8.

using BitMiracle.LibTiff.Classic;
using OpenCvSharp;

namespace AstroLab.Services;

public static class TiffIO
{
    /// <summary>
    /// Lê um TIFF RGB para <see cref="LinearImage"/> (float RGB intercalado).
    /// uint16 é dividido por 65535; float32 é mantido tal-e-qual (a normalização
    /// global é a Etapa 1 da Fase A). Tenta LibTiff e, em caso de falha, cai
    /// para <c>Cv2.ImRead(AnyDepth|AnyColor)</c> (SPEC/05 §1 ⚠).
    /// </summary>
    public static LinearImage LoadFloat(string path)
    {
        try
        {
            return LoadWithLibTiff(path);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                $"[TiffIO] LibTiff não leu '{path}' ({ex.Message}); a tentar OpenCV ImRead.");
            return LoadWithOpenCv(path);
        }
    }

    static LinearImage LoadWithLibTiff(string path)
    {
        using Tiff tiff = Tiff.Open(path, "r")
            ?? throw new IOException($"Tiff.Open devolveu null para '{path}'.");

        if (tiff.IsTiled())
            throw new NotSupportedException("TIFF tiled não suportado (leitor por scanlines).");

        int w = tiff.GetField(TiffTag.IMAGEWIDTH)[0].ToInt();
        int h = tiff.GetField(TiffTag.IMAGELENGTH)[0].ToInt();
        int spp = Field(tiff, TiffTag.SAMPLESPERPIXEL, 1);
        int bps = Field(tiff, TiffTag.BITSPERSAMPLE, 8);
        var planar = (PlanarConfig)Field(tiff, TiffTag.PLANARCONFIG, (int)PlanarConfig.CONTIG);
        var sf = (SampleFormat)Field(tiff, TiffTag.SAMPLEFORMAT, (int)SampleFormat.UINT);

        if (spp < 3)
            throw new NotSupportedException($"Esperado RGB (≥3 canais); spp={spp}.");

        int bytesPerSample = bps / 8;
        float Decode(byte[] b, int off) => (bps, sf) switch
        {
            (32, SampleFormat.IEEEFP) => BitConverter.ToSingle(b, off),
            (64, SampleFormat.IEEEFP) => (float)BitConverter.ToDouble(b, off),
            (16, _) => BitConverter.ToUInt16(b, off) / 65535f,
            (8, _) => b[off] / 255f,
            _ => throw new NotSupportedException($"bps={bps} sampleFormat={sf} não suportado.")
        };

        var data = new float[(long)w * h * 3];
        var buf = new byte[tiff.ScanlineSize()];

        if (planar == PlanarConfig.CONTIG)
        {
            for (int y = 0; y < h; y++)
            {
                tiff.ReadScanline(buf, y);
                int rowBase = y * w * 3;
                for (int x = 0; x < w; x++)
                    for (int c = 0; c < 3; c++)
                        data[rowBase + x * 3 + c] = Decode(buf, (x * spp + c) * bytesPerSample);
            }
        }
        else // SEPARATE: um plano por canal
        {
            for (int c = 0; c < 3; c++)
                for (int y = 0; y < h; y++)
                {
                    tiff.ReadScanline(buf, y, (short)c);
                    int rowBase = y * w * 3;
                    for (int x = 0; x < w; x++)
                        data[rowBase + x * 3 + c] = Decode(buf, x * bytesPerSample);
                }
        }

        return new LinearImage { Width = w, Height = h, Data = data };
    }

    static LinearImage LoadWithOpenCv(string path)
    {
        using var src = Cv2.ImRead(path, ImreadModes.AnyDepth | ImreadModes.AnyColor);
        if (src.Empty())
            throw new IOException($"OpenCV ImRead falhou para '{path}'.");

        double scale = src.Depth() switch
        {
            (int)MatType.CV_16U => 1.0 / 65535,
            (int)MatType.CV_8U => 1.0 / 255,
            _ => 1.0   // CV_32F / CV_64F: valor já em escala nativa
        };

        using var f = new Mat();
        src.ConvertTo(f, MatType.CV_32F, scale);

        // OpenCV devolve BGR; split → reordenar para RGB.
        Mat[] ch = Cv2.Split(f);
        try
        {
            int w = f.Width, h = f.Height, n = w * h;
            int[] order = ch.Length >= 3 ? new[] { 2, 1, 0 } : new[] { 0, 0, 0 };
            var planes = new float[3][];
            for (int c = 0; c < 3; c++) ch[order[c]].GetArray(out planes[c]);

            var data = new float[(long)n * 3];
            for (int i = 0; i < n; i++)
                for (int c = 0; c < 3; c++)
                    data[i * 3 + c] = planes[c][i];

            return new LinearImage { Width = w, Height = h, Data = data };
        }
        finally
        {
            foreach (var m in ch) m.Dispose();
        }
    }

    /// <summary>Escreve TIF 16-bit uint RGB com deflate (SPEC/03 §8).</summary>
    public static void Save16Bit(LinearImage img, string path)
    {
        using Tiff tiff = Tiff.Open(path, "w")
            ?? throw new IOException($"Não foi possível abrir '{path}' para escrita.");

        int w = img.Width, h = img.Height;
        tiff.SetField(TiffTag.IMAGEWIDTH, w);
        tiff.SetField(TiffTag.IMAGELENGTH, h);
        tiff.SetField(TiffTag.SAMPLESPERPIXEL, 3);
        tiff.SetField(TiffTag.BITSPERSAMPLE, 16);
        tiff.SetField(TiffTag.SAMPLEFORMAT, SampleFormat.UINT);
        tiff.SetField(TiffTag.ORIENTATION, Orientation.TOPLEFT);
        tiff.SetField(TiffTag.PLANARCONFIG, PlanarConfig.CONTIG);
        tiff.SetField(TiffTag.PHOTOMETRIC, Photometric.RGB);
        tiff.SetField(TiffTag.COMPRESSION, Compression.DEFLATE);
        tiff.SetField(TiffTag.ROWSPERSTRIP, tiff.DefaultStripSize(0));

        var d = img.Data;
        var row = new byte[w * 3 * 2];
        for (int y = 0; y < h; y++)
        {
            int rowBase = y * w * 3;
            for (int s = 0; s < w * 3; s++)
            {
                ushort u = (ushort)Math.Round(Math.Clamp(d[rowBase + s], 0f, 1f) * 65535f);
                row[s * 2] = (byte)(u & 0xFF);
                row[s * 2 + 1] = (byte)(u >> 8);
            }
            tiff.WriteScanline(row, y);
        }
        tiff.FlushData();
    }

    static int Field(Tiff tiff, TiffTag tag, int fallback)
    {
        var f = tiff.GetField(tag);
        return f != null ? f[0].ToInt() : fallback;
    }
}
