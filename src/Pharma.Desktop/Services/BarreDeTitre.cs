using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Pharma.Desktop.Services;

/// <summary>Barre de titre de la fenêtre Windows en sombre ou en clair, assortie à l'affichage du poste.</summary>
public static class BarreDeTitre
{
    private const int DwmaUtiliserModeSombre = 20;   // DWMWA_USE_IMMERSIVE_DARK_MODE (Windows 10 20H1 et suivants)

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr fenetre, int attribut, ref int valeur, int taille);

    public static void Appliquer(bool sombre) =>
        Application.Current?.Dispatcher.Invoke(() =>
        {
            if (Application.Current.MainWindow is not { } fenetre) return;
            var poignee = new WindowInteropHelper(fenetre).Handle;
            if (poignee == IntPtr.Zero) return;
            var valeur = sombre ? 1 : 0;
            _ = DwmSetWindowAttribute(poignee, DwmaUtiliserModeSombre, ref valeur, sizeof(int));
        });
}
