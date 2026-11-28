using System.Security.Cryptography;
using System.Text;

namespace Pharma.Application.Offre;

// ---- Renforcement de l'offre (décision du titulaire du 2026-11-27, point 5 : HORLOGE + MATÉRIEL, docs/offre-et-options.md) ----
// Matériel : chaque serveur de l'officine (principal, 2e serveur, PC de secours) a une clé non exportable (TPM, sinon logicielle) ; le code
// d'activation version 2 liste les empreintes des serveurs autorisés et l'API prouve à chaque évaluation qu'elle détient l'une de ces clés.
// Horloge : l'offre se calcule avec la date effective = max(date du serveur, date plancher) ; reculer l'horloge ne prolonge rien.

/// <summary>
/// Clé de CE serveur (création par « Pharma.Api.exe --creer-cle-serveur », en administrateur ; l'API ne fait que l'ouvrir). Doublure en mémoire
/// dans les tests.
/// </summary>
public interface ICleServeur
{
    /// <summary>Clé publique (SubjectPublicKeyInfo) de la clé de ce serveur ; null si elle n'existe pas ou n'est pas lisible par ce compte.</summary>
    byte[]? ClePublique();

    /// <summary>Signature ECDSA P-256 / SHA-256 (IEEE P1363) des données ; null si la clé n'existe pas ou n'est pas lisible.</summary>
    byte[]? Signer(byte[] donnees);

    /// <summary>Vrai si la clé est protégée par le TPM, faux si c'est une clé logicielle, null si elle est absente ou si on ne sait pas.</summary>
    bool? Materielle() => null;

    /// <summary>
    /// Vrai si la clé EXISTE mais que le compte qui fait tourner l'API n'a pas le droit de la lire (API lancée sans droits d'administrateur, par
    /// exemple depuis Visual Studio : la clé n'est lisible que par SYSTEM et les Administrateurs). Ne pas la recréer (essai réel du 2026-11-27).
    /// </summary>
    bool AccesRefuse() => false;
}

/// <summary>
/// Empreinte d'une clé de serveur : 16 premiers octets du SHA-256 de sa clé publique (SubjectPublicKeyInfo), en hexadécimal majuscule ;
/// affichée par groupes de 4 (« 1A2B-3C4D-… », 8 groupes). Comparée sans tirets ni espaces, sans distinction de casse.
/// </summary>
public static class EmpreinteServeur
{
    public const int Caracteres = 32;

    /// <summary>Empreinte normalisée (32 caractères hexadécimaux majuscules, sans tiret).</summary>
    public static string Calculer(byte[] clePublique) => Convert.ToHexString(SHA256.HashData(clePublique))[..Caracteres];

    /// <summary>Empreinte saisie normalisée, ou null si ce n'est pas une empreinte (32 caractères hexadécimaux une fois tirets et espaces ôtés).</summary>
    public static string? Normaliser(string? saisie)
    {
        var n = new string((saisie ?? "").Where(c => c is not ('-' or ' ' or '\t')).Select(char.ToUpperInvariant).ToArray());
        return n.Length == Caracteres && n.All(char.IsAsciiHexDigitUpper) ? n : null;
    }

    /// <summary>Empreinte normalisée affichée par groupes de 4 séparés par des tirets.</summary>
    public static string Afficher(string empreinte)
    {
        var n = Normaliser(empreinte) ?? empreinte;
        return string.Join('-', Enumerable.Range(0, (n.Length + 3) / 4).Select(i => n.Substring(i * 4, Math.Min(4, n.Length - i * 4))));
    }
}

/// <summary>Résultat de la preuve de détention de la clé du serveur.</summary>
/// <param name="Empreinte">Empreinte normalisée de la clé de ce serveur, null si la clé est absente.</param>
/// <param name="Autorisee">Vrai si l'empreinte est dans la liste ET si la signature d'un défi aléatoire par cette clé est juste.</param>
public sealed record PreuveServeur(string? Empreinte, bool Autorisee);

public static class PreuveCleServeur
{
    private static readonly byte[] Domaine = Encoding.UTF8.GetBytes("PharmaOfficine.Offre.DefiServeur.v1:");

    /// <summary>Empreinte de la clé de ce serveur (null si absente), sans preuve.</summary>
    public static string? Empreinte(ICleServeur? cle) => cle?.ClePublique() is { } spki ? EmpreinteServeur.Calculer(spki) : null;

    /// <summary>
    /// Prouve que CE serveur détient l'une des clés autorisées : l'empreinte de sa clé publique doit être dans la liste, puis un défi aléatoire
    /// (32 octets, préfixés d'un domaine propre à l'offre) signé par la clé doit se vérifier avec cette clé publique. Une clé publique copiée
    /// sans la clé privée (ou une doublure qui ment sur sa clé publique) ne passe pas.
    /// </summary>
    public static PreuveServeur Prouver(ICleServeur? cle, IReadOnlyCollection<string> autorisees)
    {
        byte[]? spki;
        try { spki = cle?.ClePublique(); }
        catch (CryptographicException) { spki = null; }
        if (spki is null) return new PreuveServeur(null, false);
        var empreinte = EmpreinteServeur.Calculer(spki);
        if (!autorisees.Contains(empreinte)) return new PreuveServeur(empreinte, false);
        var defi = new byte[Domaine.Length + 32];
        Domaine.CopyTo(defi, 0);
        RandomNumberGenerator.Fill(defi.AsSpan(Domaine.Length));
        try
        {
            if (cle!.Signer(defi) is not { } signature) return new PreuveServeur(empreinte, false);
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(spki, out _);
            return new PreuveServeur(empreinte,
                ecdsa.VerifyData(defi, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
        }
        catch (CryptographicException) { return new PreuveServeur(empreinte, false); }
    }
}

/// <summary>
/// Plus grande date déjà vue, gardée HORS de la base dans un fichier du serveur (C:\ProgramData\PharmaOfficine\Offre\date-plancher.json,
/// lisible et modifiable par le système et les administrateurs seulement). Une erreur de lecture rend null ; une erreur d'écriture est ignorée
/// (journal du serveur) : la caisse n'est jamais bloquée.
/// </summary>
public interface IDatePlancherFichier
{
    DateTime? Lire();
    void Ecrire(DateTime utc);

    /// <summary>Dernière date du serveur central mémorisée (correction du 2026-11-27, point 4) ; null : jamais reçue.</summary>
    DateTime? LireCentrale();
    /// <summary>Mémorise la date du serveur central (même fichier ; la date plancher du fichier n'est pas touchée).</summary>
    void NoterCentrale(DateTime utc);

    /// <summary>
    /// Dernière heure de l'horloge du serveur vue pendant un recul (correction du 2026-11-28) ; null : jamais notée. Sert à compter le
    /// temps passé pendant un recul quand l'API a redémarré entre-temps.
    /// </summary>
    DateTime? LireVue() => null;
    /// <summary>Mémorise l'heure de l'horloge du serveur vue pendant un recul (même fichier, les autres dates ne sont pas touchées).</summary>
    void NoterVue(DateTime utc) { }
}

/// <summary>
/// Horloge du serveur reculée (constat de revue du 2026-11-28) : tant que l'horloge retarde sur la date plancher, celle-ci avance du temps
/// réellement passé. Avant, elle restait figée : reculer l'horloge de 60 jours avant la fin de l'offre la prolongeait de 60 jours.
/// <para>
/// Mesure : pendant que l'API tourne, un compteur qui ne dépend pas de l'horloge de Windows (la changer ne trompe pas la mesure) ; après un
/// redémarrage, l'écart entre l'horloge et la dernière heure vue, écrite dans le fichier (écart négatif, ou de plus de
/// <see cref="DateEffectiveOffre.PasMax"/> : non compté). Le temps pendant lequel l'API est arrêtée et l'horloge reculée au redémarrage n'est
/// donc compté que s'il est cohérent avec l'horloge (décision du 2026-11-28 : aucun forfait). Le fichier est écrit au plus une fois par
/// <see cref="PasEcriture"/> pendant un recul ; entre deux écritures, le temps passé est compté en mémoire.
/// </para>
/// </summary>
public static class ReculHorlogeOffre
{
    public static readonly TimeSpan PasEcriture = TimeSpan.FromHours(1);

    private sealed class Suivi { public long? Tic; }
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<IDatePlancherFichier, Suivi> suivis = new();

    /// <summary>
    /// Date plancher du fichier à prendre pour ce calcul. Hors recul (aucune date plancher, ou horloge à l'heure) : la valeur du fichier,
    /// inchangée. Pendant un recul : la date plancher avancée du temps passé (écrite dans le fichier au plus une fois par heure).
    /// </summary>
    /// <param name="plancherUtc">Plus grande des dates plancher (journal fiscal, fichier, serveur central) ; null : aucune.</param>
    /// <param name="ticMs">Compteur monotone du processus, en millisecondes.</param>
    public static DateTime? Avancer(IDatePlancherFichier fichier, DateTime serveurUtc, DateTime? plancherUtc, DateTime? fichierUtc, long ticMs)
    {
        var suivi = suivis.GetOrCreateValue(fichier);
        lock (suivi)
        {
            if (plancherUtc is not { } p || serveurUtc >= p)
            {
                suivi.Tic = null;   // horloge à l'heure : rien à compter, la mesure repartira au prochain recul
                return fichierUtc;
            }
            TimeSpan passe;
            var premier = suivi.Tic is null;
            if (suivi.Tic is { } t) passe = TimeSpan.FromMilliseconds(Math.Max(0, ticMs - t));
            else passe = fichier.LireVue() is { } vue && serveurUtc > vue && serveurUtc - vue <= DateEffectiveOffre.PasMax ? serveurUtc - vue : TimeSpan.Zero;
            var avancee = p + passe;
            if (premier || passe >= PasEcriture)
            {
                if (passe > TimeSpan.Zero) fichier.Ecrire(avancee);
                fichier.NoterVue(serveurUtc);
                suivi.Tic = ticMs;
            }
            return avancee;
        }
    }
}

/// <summary>
/// Date du serveur central (décision du titulaire du 2026-11-27, point 4) : portée par chaque réponse de la voie descendante (échange léger,
/// accusé d'un lot ; champ facultatif <c>DateCentrale</c>), elle fait foi et sert de date plancher supplémentaire. Mémorisée au plus une fois
/// par heure et jamais vers le passé (un central restauré ou une réponse ancienne ne fait rien reculer). Sans central : rien ne change.
/// <para>
/// Saut borné (constat de revue du 2026-11-28) : une date du central plus de <see cref="DateEffectiveOffre.PasMax"/> au-delà de tout ce que
/// ce serveur connaît (sa propre date, la date du central déjà mémorisée, la date plancher du fichier) n'est pas mémorisée tout de suite :
/// une seule réponse datée par erreur dans le futur ferait juger échues les offres, sans retour possible. Elle n'est retenue que si le
/// central la confirme : une réponse suivante, datée par le central d'au moins <see cref="Pas"/> après la première, est encore au-delà de
/// la borne. Une horloge du central corrigée entre-temps n'y laisse rien ; une horloge de ce serveur reculée de plus de 2 jours est
/// rattrapée par le central à la deuxième réponse (une heure plus tard). L'attente est gardée en mémoire (perdue au redémarrage : elle
/// reprend à la réponse suivante).
/// </para>
/// </summary>
public static class DateCentraleOffre
{
    public static readonly TimeSpan Pas = TimeSpan.FromHours(1);

    private sealed class Attente { public DateTime? PremiereUtc; }
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<IDatePlancherFichier, Attente> attentes = new();

    /// <summary>Saut en attente de confirmation par le central (null : aucun) ; pour le diagnostic et les tests.</summary>
    public static DateTime? SautEnAttente(IDatePlancherFichier fichier) => attentes.TryGetValue(fichier, out var a) ? a.PremiereUtc : null;

    /// <summary>Vrai si la date reçue a été mémorisée. Jamais bloquant (une erreur d'écriture est ignorée par le fichier).</summary>
    /// <param name="serveurUtc">Date de ce serveur (borne du saut) ; null : seules la date centrale connue et le fichier bornent.</param>
    public static bool Noter(IDatePlancherFichier? fichier, DateTime? centraleUtc, DateTime? serveurUtc = null)
    {
        if (fichier is null || centraleUtc is not { } c) return false;
        c = DateTime.SpecifyKind(c, DateTimeKind.Utc);
        var deja = fichier.LireCentrale();
        if (deja is { } d && c < d + Pas) return false;
        var attente = attentes.GetOrCreateValue(fichier);
        lock (attente)
        {
            DateTime? connue = new[] { serveurUtc, deja, fichier.Lire() }.Max();
            if (connue is { } r && c - r > DateEffectiveOffre.PasMax)
            {
                // Saut : retenu seulement si le central le confirme (sa propre date a avancé d'au moins une heure depuis la première réponse)
                if (attente.PremiereUtc is not { } premiere || c < premiere) { attente.PremiereUtc = c; return false; }
                if (c - premiere < Pas) return false;
            }
            attente.PremiereUtc = null;
        }
        fichier.NoterCentrale(c);
        return true;
    }
}

/// <summary>Date avec laquelle l'offre se calcule (échéance, grâce, rappels).</summary>
/// <param name="ServeurUtc">Date du serveur.</param>
/// <param name="PlancherUtc">Date plancher : la plus grande de la dernière date du journal fiscal des ventes, de la date du fichier et de la
/// dernière date reçue du serveur central ; null si aucune.</param>
/// <param name="EffectiveUtc">max(date du serveur, date plancher).</param>
/// <param name="Aujourdhui">Jour effectif (jour du serveur, ou jour de la date plancher si elle est plus récente).</param>
/// <param name="Reculee">Date du serveur en retard de plus d'une heure sur la date plancher.</param>
/// <param name="Avance">Date du serveur en avance de plus de <see cref="PasMax"/> sur la date plancher (correction du 2026-11-27) : le
/// fichier n'est pas écrit, alerte « horloge du serveur en avance ».</param>
public sealed record DateEffectiveOffre(DateTime ServeurUtc, DateTime? PlancherUtc, DateTime EffectiveUtc, DateOnly Aujourdhui, bool Reculee,
    bool Avance = false)
{
    /// <summary>Retard toléré (changement d'heure, horloge un peu dérivée) avant l'alerte au titulaire.</summary>
    public static readonly TimeSpan Tolerance = TimeSpan.FromHours(1);

    /// <summary>
    /// Plus grand pas du fichier de la date plancher (correction du 2026-11-27) : une date du serveur plus de 2 jours au-delà de la date
    /// plancher (fichier, journal fiscal, date du central) n'est pas écrite. En marche normale le fichier avance d'un jour à la fois ; un
    /// serveur arrêté plus de 2 jours sans vente déclenche l'alerte jusqu'à la première vente ou au premier échange avec le central.
    /// </summary>
    public static readonly TimeSpan PasMax = TimeSpan.FromDays(2);

    public static DateEffectiveOffre Calculer(DateTime serveurUtc, DateOnly aujourdhuiServeur, DateTime? fiscaleUtc, DateTime? fichierUtc,
        DateTime? centraleUtc = null)
    {
        DateTime? plancher = new[] { fiscaleUtc, fichierUtc, centraleUtc }.Max();
        if (plancher is not { } p || p <= serveurUtc)
            return new DateEffectiveOffre(serveurUtc, plancher, serveurUtc, aujourdhuiServeur, false, plancher is { } q && serveurUtc - q > PasMax);
        var jourPlancher = DateOnly.FromDateTime(DateTime.SpecifyKind(p, DateTimeKind.Utc).ToLocalTime());
        return new DateEffectiveOffre(serveurUtc, p, p, jourPlancher > aujourdhuiServeur ? jourPlancher : aujourdhuiServeur, p - serveurUtc > Tolerance);
    }

    /// <summary>Nouvelle valeur à écrire dans le fichier (au plus une fois par jour : seulement quand le jour UTC avance), sinon null.</summary>
    public static DateTime? AEcrire(DateTime effectiveUtc, DateTime? fichierUtc) =>
        fichierUtc is { } f && DateOnly.FromDateTime(f) >= DateOnly.FromDateTime(effectiveUtc) ? null : effectiveUtc;

    /// <summary>
    /// Nouvelle valeur à écrire pour CE calcul : rien si l'horloge est en avance de plus de <see cref="PasMax"/> sur la date plancher (saut
    /// refusé), sinon <see cref="AEcrire(DateTime, DateTime?)"/>. Sans aucune date plancher (première mise en service) : la date est écrite.
    /// </summary>
    public DateTime? AEcrire(DateTime? fichierUtc) => Avance ? null : AEcrire(EffectiveUtc, fichierUtc);
}
