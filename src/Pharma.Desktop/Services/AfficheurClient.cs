using System.Globalization;
using System.IO.Ports;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace Pharma.Desktop.Services;

/// <summary>
/// Afficheur tourné vers le client, réglé pour chaque poste (Administration › Imprimantes et étiquettes) :
/// écran secondaire (fenêtre plein écran sur l'écran choisi) et/ou afficheur 2 lignes × 20 caractères sur port série.
/// L'afficheur 2 lignes utilise le jeu de commandes le plus répandu (« epson » : ESC @, effacement, retour en haut,
/// 40 caractères) ou le jeu « cd5220 » (ESC Q A / ESC Q B) ; à vérifier sur l'appareil réel.
/// Une panne d'afficheur ne bloque jamais la caisse : les erreurs sont gardées dans DerniereErreur.
/// </summary>
public sealed class AfficheurClient(ReglagesPoste reglages, ConfigurationPoste poste, SessionPoste session) : IDisposable
{
    private static readonly CultureInfo Fr = CultureInfo.GetCultureInfo("fr-FR");
    private readonly object verrou = new();
    private SerialPort? port;
    private string? portOuvert;
    private FenetreClient? fenetre;
    private CancellationTokenSource? retourAccueil;

    public string? DerniereErreur { get; private set; }

    private string NomPharmacie => session.NomPharmacie ?? poste.Pharmacie ?? "Pharma Officine";

    private string Mode => reglages.Actuelles.Afficheur ?? "aucun";
    private bool AvecEcran => Mode is "ecran" or "les_deux";
    private bool AvecVfd => Mode is "vfd" or "les_deux";

    /// <summary>Message d'accueil : nom de la pharmacie.</summary>
    public void Accueil()
    {
        AnnulerRetour();
        var nom = NomPharmacie;
        Ecran(f => f.Accueil(nom));
        Vfd("Bienvenue", nom);
    }

    /// <summary>Panier en cours : dernières lignes et total à payer par le patient.</summary>
    public void Panier(IReadOnlyList<(string Nom, int Quantite, decimal Montant)> lignes, decimal aPayer)
    {
        AnnulerRetour();
        if (lignes.Count == 0) { Accueil(); return; }
        Ecran(f => f.Panier(lignes, aPayer));
        var d = lignes[^1];
        Vfd($"{d.Quantite}x {d.Nom}", Aligner("TOTAL", Montant(aPayer)), Aligner($"{d.Quantite}x {d.Nom}", Montant(d.Montant)));
    }

    /// <summary>Après l'encaissement : payé, reçu et rendu ; retour à l'accueil après 20 secondes.</summary>
    public void Paiement(decimal aPayer, decimal? recu, decimal rendu)
    {
        Ecran(f => f.Paiement(aPayer, recu, rendu));
        Vfd(recu is { } r ? Aligner("RECU", Montant(r)) : Aligner("PAYE", Montant(aPayer)), rendu > 0 ? Aligner("RENDU", Montant(rendu)) : "Merci, a bientot");
        AnnulerRetour();
        var jeton = (retourAccueil = new CancellationTokenSource()).Token;
        _ = Task.Delay(TimeSpan.FromSeconds(20), jeton).ContinueWith(t => { if (!t.IsCanceled) Accueil(); }, TaskScheduler.Default);
    }

    /// <summary>Réglages changés : ferme ce qui ne sert plus, puis affiche l'accueil (essai).</summary>
    public void Reappliquer()
    {
        lock (verrou) { FermerPort(); }
        Application.Current?.Dispatcher.Invoke(() => { fenetre?.Close(); fenetre = null; });
        DerniereErreur = null;
        Accueil();
    }

    /// <summary>Écrans branchés : (index, libellé « 1920 × 1080, principal »).</summary>
    public static IReadOnlyList<(int Index, string Libelle)> Ecrans()
    {
        var liste = new List<(int, string)>();
        foreach (var (r, principal) in Moniteurs())
            liste.Add((liste.Count + 1, $"Écran {liste.Count + 1} : {r.Right - r.Left} × {r.Bottom - r.Top}{(principal ? " (principal)" : "")}"));
        return liste;
    }

    public static string[] Ports() => SerialPort.GetPortNames().OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToArray();

    // ---- Écran secondaire ----------------------------------------------------------------

    private void Ecran(Action<FenetreClient> action)
    {
        if (!AvecEcran || Application.Current is null) return;
        Application.Current.Dispatcher.InvokeAsync(() =>
        {
            try
            {
                if (fenetre is null || !fenetre.IsLoaded)
                {
                    var moniteurs = Moniteurs();
                    var index = reglages.Actuelles.EcranClient ?? 0;
                    // Écran choisi, sinon le premier qui n'est pas le principal
                    (Rect Zone, bool Principal) cible;
                    if (index >= 1 && index <= moniteurs.Count) cible = moniteurs[index - 1];
                    else if (moniteurs.Count(m => !m.Principal) > 0) cible = moniteurs.First(m => !m.Principal);
                    else
                    {
                        // Jamais par-dessus l'écran du caissier sans choix explicite
                        DerniereErreur = "Écran client : aucun écran secondaire branché.";
                        return;
                    }
                    fenetre = new FenetreClient(cible.Zone, NomPharmacie);
                    fenetre.Show();
                }
                action(fenetre);
            }
            catch (Exception e) { DerniereErreur = $"Écran client : {e.Message}"; }
        });
    }

    // ---- Afficheur 2 lignes ------------------------------------------------------------

    private void Vfd(string ligne1, string ligne2, string? ligne1Detail = null)
    {
        if (!AvecVfd) return;
        var nomPort = reglages.Actuelles.PortAfficheur;
        if (string.IsNullOrWhiteSpace(nomPort)) { DerniereErreur = "Afficheur 2 lignes : port COM non choisi."; return; }
        var l1 = Ligne(ligne1Detail ?? ligne1);
        var l2 = Ligne(ligne2);
        Task.Run(() =>
        {
            lock (verrou)
            {
                try
                {
                    if (port is null || portOuvert != nomPort || !port.IsOpen)
                    {
                        FermerPort();
                        port = new SerialPort(nomPort, reglages.Actuelles.VitesseAfficheur ?? 9600, Parity.None, 8, StopBits.One) { WriteTimeout = 1000 };
                        port.Open();
                        portOuvert = nomPort;
                    }
                    var octets = reglages.Actuelles.ProtocoleAfficheur == "cd5220"
                        ? [.. Encoding.ASCII.GetBytes("\u001BQA" + l1 + "\r"), .. Encoding.ASCII.GetBytes("\u001BQB" + l2 + "\r")]
                        : (byte[])[0x1B, 0x40, 0x0C, 0x0B, .. Encoding.ASCII.GetBytes(l1 + l2)];
                    port.Write(octets, 0, octets.Length);
                    DerniereErreur = null;
                }
                catch (Exception e)
                {
                    DerniereErreur = $"Afficheur 2 lignes ({nomPort}) : {e.Message}";
                    FermerPort();
                }
            }
        });
    }

    private void FermerPort()
    {
        try { port?.Dispose(); } catch { /* port déjà perdu */ }
        (port, portOuvert) = (null, null);
    }

    private void AnnulerRetour() { retourAccueil?.Cancel(); retourAccueil = null; }

    private static string Montant(decimal m) => m.ToString("N2", Fr).Replace(' ', ' ').Replace(' ', ' ');

    /// <summary>Texte à gauche, montant à droite, sur 20 caractères.</summary>
    private static string Aligner(string gauche, string droite)
    {
        var place = Math.Max(20 - droite.Length - 1, 0);
        var g = Sans(gauche);
        return (g.Length > place ? g[..place] : g).PadRight(place) + " " + droite;
    }

    /// <summary>20 caractères ASCII : accents retirés (les afficheurs n'ont pas tous la même page de code).</summary>
    private static string Ligne(string s)
    {
        var t = Sans(s);
        return t.Length > 20 ? t[..20] : t.PadRight(20);
    }

    private static string Sans(string s)
    {
        var sb = new StringBuilder();
        foreach (var c in s.Normalize(NormalizationForm.FormD))
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) sb.Append(c is >= ' ' and <= '~' ? c : c == '’' ? '\'' : ' ');
        return sb.ToString();
    }

    public void Dispose()
    {
        lock (verrou) { FermerPort(); }
    }

    // ---- Moniteurs (Win32) : WPF ne liste pas les écrans --------------------------------

    [StructLayout(LayoutKind.Sequential)]
    internal struct Rect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct InfoMoniteur { public int Taille; public Rect Zone; public Rect Travail; public uint Options; }

    private delegate bool RappelMoniteur(IntPtr moniteur, IntPtr dc, ref Rect zone, IntPtr donnees);

    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, RappelMoniteur rappel, IntPtr donnees);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr moniteur, ref InfoMoniteur info);

    private static List<(Rect Zone, bool Principal)> Moniteurs()
    {
        var liste = new List<(Rect, bool)>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr m, IntPtr _, ref Rect _, IntPtr _) =>
        {
            var info = new InfoMoniteur { Taille = Marshal.SizeOf<InfoMoniteur>() };
            if (GetMonitorInfo(m, ref info)) liste.Add((info.Zone, (info.Options & 1) != 0));
            return true;
        }, IntPtr.Zero);
        return liste;
    }

    /// <summary>Fenêtre plein écran sans bordure, placée en pixels physiques sur le moniteur choisi.</summary>
    private sealed class FenetreClient : Window
    {
        private readonly StackPanel lignes = new();
        private readonly TextBlock titre = new() { FontSize = 40, FontWeight = FontWeights.Bold, Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap };
        private readonly TextBlock total = new() { FontSize = 72, FontWeight = FontWeights.Bold, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Right };
        private readonly TextBlock sousTotal = new() { FontSize = 32, Foreground = new SolidColorBrush(Color.FromRgb(0xC8, 0xE6, 0xD2)), HorizontalAlignment = HorizontalAlignment.Right };

        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr h, IntPtr apres, int x, int y, int l, int ht, uint options);

        public FenetreClient(Rect zone, string pharmacie)
        {
            Title = $"{pharmacie} · écran client";
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            ShowActivated = false;
            Topmost = true;   // toujours visible côté client, sans prendre le focus de la caisse
            Background = new SolidColorBrush(Color.FromRgb(0x0E, 0x3B, 0x24));
            var grille = new Grid { Margin = new Thickness(48) };
            grille.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grille.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grille.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grille.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(titre, 0);
            var defile = new ScrollViewer { Content = lignes, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, Margin = new Thickness(0, 24, 0, 24) };
            Grid.SetRow(defile, 1);
            Grid.SetRow(sousTotal, 2);
            Grid.SetRow(total, 3);
            grille.Children.Add(titre);
            grille.Children.Add(defile);
            grille.Children.Add(sousTotal);
            grille.Children.Add(total);
            Content = grille;
            SourceInitialized += (_, _) =>
                SetWindowPos(new WindowInteropHelper(this).Handle, IntPtr.Zero, zone.Left, zone.Top, zone.Right - zone.Left, zone.Bottom - zone.Top, 0x0040);
        }

        public void Accueil(string pharmacie)
        {
            titre.Text = pharmacie;
            lignes.Children.Clear();
            lignes.Children.Add(new TextBlock { Text = "Bienvenue", FontSize = 56, Foreground = Brushes.White, Margin = new Thickness(0, 80, 0, 0) });
            (sousTotal.Text, total.Text) = ("", "");
        }

        public void Panier(IReadOnlyList<(string Nom, int Quantite, decimal Montant)> l, decimal aPayer)
        {
            lignes.Children.Clear();
            foreach (var (nom, qte, montant) in l.TakeLast(10))
            {
                var g = new Grid { Margin = new Thickness(0, 6, 0, 6) };
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var n = new TextBlock { Text = $"{qte} × {nom}", FontSize = 30, Foreground = Brushes.White, TextTrimming = TextTrimming.CharacterEllipsis };
                var m = new TextBlock { Text = $"{montant.ToString("N2", Fr)} DA", FontSize = 30, Foreground = Brushes.White, Margin = new Thickness(24, 0, 0, 0) };
                Grid.SetColumn(m, 1);
                g.Children.Add(n);
                g.Children.Add(m);
                lignes.Children.Add(g);
            }
            sousTotal.Text = "À payer";
            total.Text = $"{aPayer.ToString("N2", Fr)} DA";
        }

        public void Paiement(decimal aPayer, decimal? recu, decimal rendu)
        {
            lignes.Children.Clear();
            lignes.Children.Add(new TextBlock { Text = "Merci de votre visite", FontSize = 48, Foreground = Brushes.White, Margin = new Thickness(0, 40, 0, 0) });
            if (recu is { } r)
                lignes.Children.Add(new TextBlock { Text = $"Reçu : {r.ToString("N2", Fr)} DA", FontSize = 34, Foreground = Brushes.White, Margin = new Thickness(0, 24, 0, 0) });
            sousTotal.Text = rendu > 0 ? $"Payé : {aPayer.ToString("N2", Fr)} DA · rendu" : "Payé";
            total.Text = $"{(rendu > 0 ? rendu : aPayer).ToString("N2", Fr)} DA";
        }
    }
}
