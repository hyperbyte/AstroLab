// AstroLab — diálogo nativo "Abrir ficheiro" do Windows via comdlg32 (P/Invoke).
// Corre no servidor = máquina local do utilizador (app single-user). Sem WinForms,
// sem alterar o TFM/SDK web. Devolve o caminho absoluto escolhido (ou null).

using System.Runtime.InteropServices;

namespace AstroLab.Services;

public static class NativeFileDialog
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct OpenFileName
    {
        public int lStructSize;
        public IntPtr hwndOwner;
        public IntPtr hInstance;
        public string? lpstrFilter;
        public string? lpstrCustomFilter;
        public int nMaxCustFilter;
        public int nFilterIndex;
        public IntPtr lpstrFile;          // buffer in/out → IntPtr não-gerido
        public int nMaxFile;
        public string? lpstrFileTitle;
        public int nMaxFileTitle;
        public string? lpstrInitialDir;
        public string? lpstrTitle;
        public int Flags;
        public short nFileOffset;
        public short nFileExtension;
        public string? lpstrDefExt;
        public IntPtr lCustData;
        public IntPtr lpfnHook;
        public string? lpTemplateName;
        public IntPtr pvReserved;
        public int dwReserved;
        public int FlagsEx;
    }

    [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool GetOpenFileNameW(ref OpenFileName ofn);

    const int OFN_FILEMUSTEXIST = 0x00001000;
    const int OFN_PATHMUSTEXIST = 0x00000800;
    const int OFN_NOCHANGEDIR = 0x00000008;
    const int OFN_EXPLORER = 0x00080000;

    /// <summary>Abre o diálogo nativo (thread STA) e devolve o caminho ou null.</summary>
    public static string? Pick(string? initialDir = null)
    {
        string? result = null;
        var t = new Thread(() =>
        {
            const int max = 1024;
            IntPtr buffer = Marshal.AllocHGlobal(max * sizeof(char));
            try
            {
                for (int i = 0; i < max; i++) Marshal.WriteInt16(buffer, i * 2, 0); // zerar

                var ofn = new OpenFileName
                {
                    lStructSize = Marshal.SizeOf<OpenFileName>(),
                    lpstrFilter = "TIFF (*.tif;*.tiff)\0*.tif;*.tiff\0Todos (*.*)\0*.*\0\0",
                    nFilterIndex = 1,
                    lpstrFile = buffer,
                    nMaxFile = max,
                    lpstrTitle = "Escolher Autosave.tif",
                    lpstrInitialDir = initialDir,
                    Flags = OFN_FILEMUSTEXIST | OFN_PATHMUSTEXIST | OFN_NOCHANGEDIR | OFN_EXPLORER
                };

                if (GetOpenFileNameW(ref ofn))
                    result = Marshal.PtrToStringUni(buffer);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.IsBackground = true;
        t.Start();
        t.Join();
        return string.IsNullOrEmpty(result) ? null : result;
    }
}
