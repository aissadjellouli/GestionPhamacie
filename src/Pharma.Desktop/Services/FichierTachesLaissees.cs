using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Pharma.Shared;

namespace Pharma.Desktop.Services;

/// <summary>
/// Tâche longue laissée en cours au « Changer d'utilisateur » : le prochain employé connecté sur ce poste la voit dans un
/// bandeau et la reprend sans rien refaire. Type : reception (n° de la réception en attente sur le serveur), facture ou retour
/// (saisie en JSON). Droit : celui qu'il faut pour la reprendre. Comptage à l'aveugle : réservé à son auteur ou à qui décide des écarts.
/// </summary>
public sealed record TacheLaissee(string Type, string Libelle, string LaisseePar, string Droit, string Contenu, DateTime Le, bool ReserveAuteur = false);

/// <summary>
/// Fichier du poste qui garde la facture et le retour fournisseur laissés au « Changer d'utilisateur » : ils survivent au
/// redémarrage du poste. %LOCALAPPDATA%\PharmaOfficine\taches-laissees.dat.
/// Contenu : JSON chiffré par DPAPI, portée utilisateur Windows (CurrentUser) : la facture peut contenir le nom, le prénom et le
/// n° d'assuré d'un patient, rien n'est jamais écrit en clair (ni le fichier, ni son fichier temporaire).
/// Écriture atomique (fichier temporaire puis remplacement). Relu à la première connexion qui suit le démarrage ; tâches de plus de <see cref="DureeDeGarde"/> écartées.
/// Fichier illisible (abîmé, chiffré pour un autre compte Windows) : renommé à côté, ignoré, noté au journal du poste, jamais d'arrêt.
/// La réception n'y figure pas : elle est déjà en attente sur le serveur (liste des réceptions en attente).
/// Réglage de l'officine (<see cref="Mode"/>, titulaire, script 156), lu par le poste à chaque connexion : « chiffre » (ce fichier)
/// ou « desactive » (mémoire seule : rien n'est lu ni écrit, le fichier et ses copies mises de côté sont supprimés).
/// Tant que le réglage n'est pas connu (aucune connexion depuis le démarrage, ou serveur injoignable), rien n'est lu, écrit ni supprimé.
/// </summary>
public sealed class FichierTachesLaissees
{
    /// <summary>
    /// Durée de garde d'une tâche laissée : 7 jours après le « Changer d'utilisateur ». Au-delà, la saisie est jugée périmée
    /// (prix, lots, ventes ont pu changer) et elle est écartée à la relecture.
    /// </summary>
    public static readonly TimeSpan DureeDeGarde = TimeSpan.FromDays(7);

    public const string NomFichier = "taches-laissees.dat";

    /// <summary>Types gardés dans le fichier (saisies qui n'existent que sur le poste).</summary>
    public static readonly IReadOnlySet<string> TypesGardes = new HashSet<string> { "facture", "retour" };

    /// <summary>Complément DPAPI propre à ce fichier : un autre secret du même compte Windows ne s'y déchiffre pas.</summary>
    private static readonly byte[] Entropie = "PharmaOfficine.TachesLaissees"u8.ToArray();

    private const int VersionFormat = 1;

    private readonly string dossier;
    private readonly Action<string> journal;
    private readonly Func<DateTime> maintenant;

    /// <param name="dossier">Dossier du fichier (en service : <see cref="DossierParDefaut"/>).</param>
    /// <param name="journal">Message au journal du poste (par défaut : erreurs.log du même dossier). Jamais de donnée personnelle.</param>
    /// <param name="maintenant">Heure du poste (tests : heure fixée).</param>
    /// <param name="mode">Réglage déjà connu (tests) ; en service : null jusqu'à la première connexion (<see cref="DefinirMode"/>).</param>
    public FichierTachesLaissees(string dossier, Action<string>? journal = null, Func<DateTime>? maintenant = null, string? mode = null)
    {
        this.dossier = dossier;
        this.journal = journal ?? NoterAuJournal;
        this.maintenant = maintenant ?? (() => DateTime.Now);
        if (mode is not null) DefinirMode(mode);
    }

    /// <summary>Réglage en vigueur : « chiffre », « desactive », ou null tant qu'il n'est pas connu.</summary>
    public string? Mode { get; private set; }

    /// <summary>
    /// Applique le réglage de l'officine lu à la connexion. « desactive » : le fichier du poste (et ses copies mises de côté) est
    /// supprimé aussitôt. Valeur inconnue : ignorée (réglage précédent gardé), notée au journal du poste.
    /// </summary>
    public void DefinirMode(string mode)
    {
        if (mode is not (ReglageTachesLaisseesDto.Chiffre or ReglageTachesLaisseesDto.Desactive))
        {
            journal($"Tâches laissées : réglage inconnu ignoré ({mode.Length} caractère(s)).");
            return;
        }
        Mode = mode;
        if (mode == ReglageTachesLaisseesDto.Desactive) Supprimer();
    }

    // Fichier déjà relu depuis le démarrage : il ne l'est qu'une fois (ensuite, la liste du poste fait foi)
    private bool relu;

    /// <summary>
    /// À chaque connexion : applique le réglage lu et renvoie les tâches du fichier à ajouter à celles du poste (première fois en
    /// « chiffre » seulement, sans doublon). En « chiffre », le fichier est aussitôt réécrit avec l'ensemble (tâches périmées écartées,
    /// tâches laissées avant que le réglage soit connu enfin gardées). En « desactive » : fichier supprimé, rien d'ajouté.
    /// </summary>
    public IReadOnlyList<TacheLaissee> Appliquer(string mode, IReadOnlyCollection<TacheLaissee> enMemoire)
    {
        DefinirMode(mode);
        if (Mode != ReglageTachesLaisseesDto.Chiffre) return [];
        var ajout = relu ? [] : Lire().Where(t => !enMemoire.Contains(t)).ToList();
        relu = true;
        Ecrire(enMemoire.Concat(ajout).ToList());
        return ajout;
    }

    /// <summary>Réglage « désactivé » : fichier, fichier temporaire et fichiers mis de côté supprimés ; rien n'est remonté.</summary>
    private void Supprimer()
    {
        try
        {
            if (!Directory.Exists(dossier)) return;
            var supprimes = 0;
            foreach (var f in new[] { Chemin, Chemin + ".tmp" }.Where(File.Exists)
                         .Concat(Directory.EnumerateFiles(dossier, "taches-laissees.illisible-*.dat")))
            {
                File.Delete(f);
                supprimes++;
            }
            if (supprimes > 0)
                journal($"Tâches laissées : réglage « désactivé », {supprimes} fichier(s) du poste supprimé(s) ; facture et retour gardés en mémoire seulement.");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            journal($"Tâches laissées : réglage « désactivé », fichier {NomFichier} non supprimé ({e.GetType().Name}) ; nouvel essai à la prochaine écriture.");
        }
    }

    /// <summary>Dossier des fichiers locaux du poste : %LOCALAPPDATA%\PharmaOfficine (les journaux, eux, vont dans <see cref="JournauxPoste"/>).</summary>
    public static string DossierParDefaut() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PharmaOfficine");

    public string Chemin => Path.Combine(dossier, NomFichier);

    /// <summary>
    /// Relecture (première connexion après le démarrage) : tâches encore valables. Fichier absent : liste vide. Fichier illisible :
    /// renommé et ignoré. Réglage autre que « chiffre » (désactivé ou inconnu) : liste vide, fichier non lu.
    /// </summary>
    public IReadOnlyList<TacheLaissee> Lire()
    {
        if (Mode != ReglageTachesLaisseesDto.Chiffre) return [];
        PurgerIllisiblesAnciens();
        if (!File.Exists(Chemin)) return [];
        try
        {
            var clair = Dechiffrer(File.ReadAllBytes(Chemin));
            var contenu = JsonSerializer.Deserialize<Contenu>(clair)
                ?? throw new InvalidDataException("Fichier vide.");
            if (contenu.Version != VersionFormat || contenu.Taches is null)
                throw new InvalidDataException($"Version de fichier inconnue : {contenu.Version}.");
            var limite = maintenant() - DureeDeGarde;
            return contenu.Taches.Where(t => t is { Type: not null, Libelle: not null, LaisseePar: not null, Droit: not null, Contenu: not null } && TypesGardes.Contains(t.Type) && t.Le >= limite).ToList();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            MettreDeCote(ex);
            return [];
        }
    }

    /// <summary>
    /// Écrit les tâches gardées (facture, retour, non périmées) : fichier temporaire chiffré puis remplacement.
    /// Aucune tâche : le fichier est supprimé (tâches reprises purgées). Une erreur d'écriture est notée, jamais remontée.
    /// Réglage « désactivé » : rien n'est écrit, le fichier restant est supprimé. Réglage inconnu : rien n'est écrit ni supprimé.
    /// </summary>
    public void Ecrire(IEnumerable<TacheLaissee> taches)
    {
        if (Mode == ReglageTachesLaisseesDto.Desactive) { Supprimer(); return; }
        if (Mode != ReglageTachesLaisseesDto.Chiffre) return;
        var limite = maintenant() - DureeDeGarde;
        var gardees = taches.Where(t => TypesGardes.Contains(t.Type) && t.Le >= limite).ToList();
        var temporaire = Chemin + ".tmp";
        try
        {
            if (gardees.Count == 0)
            {
                if (File.Exists(Chemin)) File.Delete(Chemin);
                return;
            }
            Directory.CreateDirectory(dossier);
            var octets = Chiffrer(JsonSerializer.SerializeToUtf8Bytes(new Contenu(VersionFormat, gardees)));
            using (var flux = new FileStream(temporaire, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                flux.Write(octets);
                flux.Flush(flushToDisk: true);
            }
            File.Move(temporaire, Chemin, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException or PlatformNotSupportedException)
        {
            journal($"Tâches laissées : fichier {NomFichier} non enregistré ({ex.GetType().Name}) ; les tâches restent sur le poste jusqu'à sa fermeture.");
            try { if (File.Exists(temporaire)) File.Delete(temporaire); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    private static byte[] Chiffrer(byte[] clair)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("DPAPI : Windows seulement.");
        return ProtectedData.Protect(clair, Entropie, DataProtectionScope.CurrentUser);
    }

    private static byte[] Dechiffrer(byte[] protege)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("DPAPI : Windows seulement.");
        return ProtectedData.Unprotect(protege, Entropie, DataProtectionScope.CurrentUser);
    }

    /// <summary>Fichier illisible : renommé à côté (taches-laissees.illisible-AAAAMMJJ-HHMMSS.dat) et noté au journal.</summary>
    private void MettreDeCote(Exception ex)
    {
        var nom = $"taches-laissees.illisible-{maintenant():yyyyMMdd-HHmmss}.dat";
        try
        {
            var cible = Path.Combine(dossier, nom);
            File.Move(Chemin, cible, overwrite: true);
            // Date de mise de côté (le déplacement garde l'ancienne) : sert à la purge après la durée de garde
            File.SetLastWriteTime(cible, maintenant());
            journal($"Tâches laissées : fichier {NomFichier} illisible ({ex.GetType().Name}), renommé en {nom} et ignoré.");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            journal($"Tâches laissées : fichier {NomFichier} illisible ({ex.GetType().Name}) et non renommé ({e.GetType().Name}) ; ignoré.");
        }
    }

    /// <summary>Fichiers mis de côté depuis plus que la durée de garde : supprimés (contenu chiffré, plus d'usage).</summary>
    private void PurgerIllisiblesAnciens()
    {
        try
        {
            if (!Directory.Exists(dossier)) return;
            var limite = maintenant() - DureeDeGarde;
            foreach (var f in Directory.EnumerateFiles(dossier, "taches-laissees.illisible-*.dat"))
                if (File.GetLastWriteTime(f) < limite) File.Delete(f);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Journal local du poste (erreurs.log), même format que les erreurs imprévues ; message sans donnée personnelle.</summary>
    private void NoterAuJournal(string message)
    {
        try
        {
            // Dossier par défaut du fichier : journal dans le dossier des journaux du poste ; dossier fourni (essais) : journal à côté du fichier
            var journaux = string.Equals(dossier, DossierParDefaut(), StringComparison.OrdinalIgnoreCase) ? JournauxPoste.Dossier : dossier;
            Directory.CreateDirectory(journaux);
            File.AppendAllText(Path.Combine(journaux, "erreurs.log"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}{Environment.NewLine}");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    [System.Reflection.Obfuscation(Exclude = true, ApplyToMembers = true)]
    private sealed record Contenu(int Version, List<TacheLaissee>? Taches);
}
