using Pharma.Application.Commun;
using Pharma.Application.Securite;
using Pharma.Domain.Commun;

namespace Pharma.Application.Offre;

/// <summary>Activation rangée dans la base (table activation_offre, script 172).</summary>
public sealed record ActivationEnregistree(long Id, string Code, DateTime EmiseLeUtc, string Source, DateTime SaisieLeUtc, string? SaisiePar,
    Guid? OrdreCentral, DateTime? RefuseeLeUtc, string? MotifRefus);

/// <summary>Identité de la pharmacie à laquelle une activation doit correspondre : code de l'officine et NIF (fiche de la pharmacie).</summary>
public sealed record IdentiteOffre(Guid Officine, string? Nif);

public interface IActivationsOffreDepot
{
    Task<IdentiteOffre> IdentiteAsync(CancellationToken ct = default);
    /// <summary>Activations, les plus récentes (date de signature) d'abord.</summary>
    Task<IReadOnlyList<ActivationEnregistree>> ListerAsync(CancellationToken ct = default);
    /// <summary>Range une activation contrôlée ; faux si ce code est déjà rangé.</summary>
    Task<bool> AjouterAsync(string code, DateTime emiseLeUtc, string source, long? saisiePar, Guid? ordreCentral, CancellationToken ct = default);
    /// <summary>Note une seule fois qu'une activation rangée ne passe plus le contrôle (refusee_le, motif) ; vrai si c'est la première fois.</summary>
    Task<bool> SignalerRefusAsync(long id, string motif, CancellationToken ct = default);
    /// <summary>Rappel de fin d'offre (étape « 90j » … « 1j » ou « grace ») déjà noté au journal des actions pour cette date de fin.</summary>
    Task<bool> RappelNoteAsync(string etape, DateOnly fin, CancellationToken ct = default);
    /// <summary>
    /// Note le rappel au journal des actions (offre_rappel), APRÈS sa mise en file : vrai s'il ne l'était pas encore. Noter d'abord
    /// perdrait le rappel si l'API s'arrêtait entre les deux (revue de code du 2026-11-27) ; au pire, un arrêt entre la mise en file et la
    /// note donne un rappel en double, jamais un rappel perdu.
    /// </summary>
    Task<bool> NoterRappelAsync(string etape, DateOnly fin, CancellationToken ct = default);
    /// <summary>Dernière date du journal fiscal des ventes (journal_vente, chaîné et scellé) ; null si le journal est vide.</summary>
    Task<DateTime?> DerniereDateFiscaleAsync(CancellationToken ct = default);
    /// <summary>
    /// Note au journal des actions (offre_horloge_reculee, sans utilisateur) que la date du serveur retarde sur la date plancher, une seule fois
    /// pour une même date plancher ; vrai si c'est la première fois.
    /// </summary>
    Task<bool> SignalerHorlogeReculeeAsync(DateTime plancherUtc, DateTime serveurUtc, CancellationToken ct = default);
    /// <summary>
    /// Note au journal des actions (offre_horloge_avance, sans utilisateur) que la date du serveur est en avance de plus de 2 jours sur la
    /// date plancher (correction du 2026-11-27), une seule fois pour une même date plancher ; vrai si c'est la première fois. Jamais bloquant.
    /// </summary>
    Task<bool> SignalerHorlogeAvanceAsync(DateTime plancherUtc, DateTime serveurUtc, CancellationToken ct = default);
    /// <summary>
    /// Lève la marque de refus (refusee_le, motif) d'une activation rangée qui repasse le contrôle (correction du 2026-11-27), avec une ligne
    /// au journal des actions (offre_activation_refus_leve) : un nouveau refus ouvrira un nouveau délai de grâce. Vrai si une marque a été
    /// levée ; jamais bloquant (base en lecture seule : rien n'est fait).
    /// </summary>
    Task<bool> LeverRefusAsync(long id, CancellationToken ct = default);
}

/// <summary>Limites de l'offre en vigueur, lues par les cas d'usage concernés (premier cas : le maximum de postes autorisés).</summary>
public interface ILimitesOffre
{
    Task<int> MaximumPostesAsync(CancellationToken ct = default);
}

/// <summary>Packs de l'offre en vigueur (lot O6) : routes, écrans et tâches d'un pack non inclus sont fermés.</summary>
public interface IPacksOffre
{
    Task<bool> InclusAsync(string pack, CancellationToken ct = default);
    Task<int> ScannersMobilesAsync(CancellationToken ct = default);
}

/// <summary>Action d'un pack non inclus dans l'offre : refusée (403, « Option non incluse — voir Mon offre »).</summary>
public sealed class OptionNonIncluseException(string pack) : Exception(PacksOffre.MessageNonInclus(pack))
{
    public string Pack { get; } = pack;
}

public static class PacksOffreExtensions
{
    /// <summary>Refuse l'action si le pack n'est pas inclus dans l'offre en vigueur.</summary>
    public static async Task ExigerPackAsync(this IPacksOffre packs, string pack, CancellationToken ct = default)
    {
        if (!await packs.InclusAsync(pack, ct)) throw new OptionNonIncluseException(pack);
    }
}

/// <summary>
/// Offre en vigueur gardée une minute (une lecture de la base et un contrôle de signature par minute au plus, pas à chaque requête) ;
/// vidée à chaque saisie ou réception d'un code. Singleton.
/// </summary>
public sealed class CacheOffre
{
    public static readonly TimeSpan Duree = TimeSpan.FromMinutes(1);
    private (EtatOffre Etat, DateTime LeUtc)? valeur;
    private readonly object verrou = new();

    public EtatOffre? Lire(DateTime maintenantUtc)
    {
        lock (verrou) return valeur is { } v && maintenantUtc - v.LeUtc < Duree && maintenantUtc >= v.LeUtc ? v.Etat : null;
    }

    private long generation;

    /// <summary>Change à chaque <see cref="Vider"/> : un calcul commencé avant une saisie ne range pas son résultat périmé après elle.</summary>
    public long Generation { get { lock (verrou) return generation; } }

    public void Ecrire(EtatOffre etat, DateTime maintenantUtc, long? generationLue = null)
    {
        lock (verrou) { if (generationLue is null || generationLue == generation) valeur = (etat, maintenantUtc); }
    }

    public void Vider() { lock (verrou) { valeur = null; generation++; } }
}

/// <summary>Offre en vigueur.</summary>
/// <param name="Etat">base (aucune activation valable), active, grace (échue depuis moins de 15 jours).</param>
/// <param name="Alerte">Avertissement à montrer au titulaire (échéance proche, délai de grâce, activation refusée), sinon null.</param>
/// <param name="Jour">Jour effectif du calcul (<see cref="DateEffectiveOffre"/>) : les rappels et la saisie s'en servent aussi.</param>
/// <param name="EmpreinteServeur">Empreinte normalisée de la clé de CE serveur, null si la clé est absente.</param>
/// <param name="AlerteServeur">Code version 1 en vigueur (« non lié au serveur ») ; sinon null (serveur non déclaré : dans <paramref name="Alerte"/>).</param>
/// <param name="AlerteHorloge">Horloge du serveur reculée de plus d'une heure, ou en avance de plus de 2 jours, sur la date plancher ; sinon null.</param>
/// <param name="AlerteCle">Clé de CE serveur logicielle (non protégée par le TPM), ou présente mais illisible par le compte de l'API :
/// avertissement court, sans blocage ; sinon null.</param>
public sealed record EtatOffre(string Etat, IReadOnlyList<string> Packs, int Postes, int ScannersMobiles, DateOnly? Fin, bool Transition,
    DateTime? EmiseLeUtc, string? Alerte, DateOnly? Jour = null, string? EmpreinteServeur = null, string? AlerteServeur = null,
    string? AlerteHorloge = null, string? AlerteCle = null);

public static class EtatsOffre
{
    public const string Base = "base", Active = "active", Grace = "grace";
}

/// <summary>
/// Offre en vigueur (décisions du titulaire du 2026-11-26) : la plus récente activation rangée qui passe le contrôle (signature de
/// l'éditeur, même officine, même NIF) et n'est pas échue depuis plus de 15 jours. Sans activation valable : offre de base, 2 postes ;
/// une activation effacée ou modifiée dans la base ramène à l'offre de base, jamais à tout ouvert.
/// Renforcement (décision du 2026-11-27, point 5) : le calcul se fait à la date effective (<see cref="DateEffectiveOffre"/>, reculer l'horloge ne
/// prolonge rien) ; un code version 2 n'est valable que sur un serveur qui prouve détenir l'une des clés qu'il déclare (<see cref="PreuveCleServeur"/>),
/// sinon il suit le chemin d'une activation refusée après un délai de grâce de 15 jours depuis la première constatation.
/// </summary>
public sealed class OffreCourante(IActivationsOffreDepot depot, IHorloge horloge, IReadOnlyList<byte[]>? clesPubliques = null, CacheOffre? cache = null,
    ICleServeur? cleServeur = null, IDatePlancherFichier? plancher = null, Func<long>? compteurMs = null)
    : ILimitesOffre, IPacksOffre
{
    public const string ActionHorlogeReculee = "offre_horloge_reculee", ActionHorlogeAvance = "offre_horloge_avance",
        ActionRefusLeve = "offre_activation_refus_leve";

    /// <summary>Avertissement de Mon offre pour une clé de serveur logicielle (correction du 2026-11-27, point 4).</summary>
    public const string MessageCleLogicielle = "clé logicielle, sans TPM : moins bien protégée contre la copie du serveur (rien n’est bloqué)";

    /// <summary>Offre de base : 2 postes inclus (proposition du 2026-11-26, docs/offre-et-options.md).</summary>
    public const int PostesBase = 2;
    public const int GraceJours = 15;
    /// <summary>Rappels avant la fin (« go rappels » du 2026-11-27) : 3 mois, 2 mois, 1 mois, 15 jours, 7 jours, 1 jour ; le message de Mon offre
    /// s'affiche dès le premier.</summary>
    public static readonly IReadOnlyList<int> RappelsJours = [90, 60, 30, 15, 7, 1];
    public static int RappelJours => RappelsJours[0];

    private readonly IReadOnlyList<byte[]> cles = clesPubliques ?? CleEditeur.Publiques;

    public async Task<int> MaximumPostesAsync(CancellationToken ct = default) => (await LireAsync(ct)).Postes;

    public async Task<bool> InclusAsync(string pack, CancellationToken ct = default) => (await LireAsync(ct)).Packs.Contains(pack);

    public async Task<int> ScannersMobilesAsync(CancellationToken ct = default) => (await LireAsync(ct)).ScannersMobiles;

    /// <summary>Vide le cache (après une saisie ou une réception de code).</summary>
    public void Invalider() => cache?.Vider();

    /// <summary>Message de Mon offre pour un code version 1 (toujours accepté).</summary>
    public const string MessageNonLie = "Code non lié au serveur : demandez un nouveau code à l’éditeur (avec l’empreinte de ce serveur).";

    /// <summary>
    /// Clé du serveur présente mais illisible par le compte de l'API (essai réel du 2026-11-27 : API lancée sans droits d'administrateur) ;
    /// surtout ne pas la recréer : un nouveau code serait lié à une autre empreinte.
    /// </summary>
    public const string MessageCleIllisible =
        "Clé de ce serveur présente mais illisible par le compte de l’API : lancez l’API en service ou en administrateur (ne recréez pas la clé).";

    /// <summary>Message d'un code version 2 qui ne déclare pas ce serveur (ou clé du serveur absente, ou illisible).</summary>
    public static string MessageServeurNonDeclare(string? empreinte, bool accesRefuse = false) => empreinte is null
        ? accesRefuse ? MessageCleIllisible : "Clé de ce serveur absente : créez-la (Pharma.Api.exe --creer-cle-serveur, en administrateur), puis demandez un nouveau code à l’éditeur avec son empreinte (Pharma.Api.exe --empreinte-serveur)."
        : $"Ce serveur n’est pas déclaré dans le code de l’offre : demandez un nouveau code à l’éditeur avec l’empreinte {EmpreinteServeur.Afficher(empreinte)}.";

    /// <summary>Preuve de détention de la clé de ce serveur pour un code version 2 (null : code version 1, rien à prouver).</summary>
    public PreuveServeur? ProuverServeur(ActivationOffre a) => a.Serveurs is { } s ? PreuveCleServeur.Prouver(cleServeur, s) : null;

    /// <summary>
    /// Date effective : la date du serveur, ou la date plancher si elle est plus récente (dernière date du journal fiscal des ventes, plus grande
    /// date déjà vue gardée dans le fichier du serveur, dernière date reçue du serveur central). Met le fichier à jour au plus une fois par
    /// jour, jamais d'un saut de plus de 2 jours (correction du 2026-11-27 : une horloge mise par erreur dans le futur n'y laisse rien) ;
    /// jamais bloquant. Pendant un recul de l'horloge, la date plancher avance du temps passé (correction du 2026-11-28).
    /// </summary>
    public async Task<DateEffectiveOffre> DateEffectiveAsync(CancellationToken ct = default)
    {
        var maintenant = horloge.MaintenantUtc;
        var fiscale = await depot.DerniereDateFiscaleAsync(ct);
        var fichier = plancher?.Lire();
        var centrale = plancher?.LireCentrale();
        // Horloge reculée : la date plancher avance du temps réellement passé (ReculHorlogeOffre), elle ne reste plus figée
        if (plancher is not null)
            fichier = ReculHorlogeOffre.Avancer(plancher, maintenant, new[] { fiscale, fichier, centrale }.Max(), fichier,
                (compteurMs ?? (() => Environment.TickCount64))());
        var d = DateEffectiveOffre.Calculer(maintenant, horloge.Aujourdhui, fiscale, fichier, centrale);
        if (plancher is not null && d.AEcrire(fichier) is { } nouvelle) plancher.Ecrire(nouvelle);
        return d;
    }

    /// <summary>Contrôle une activation pour CETTE pharmacie : signature, officine, NIF. Lève <see cref="ActivationInvalideException"/>.</summary>
    public ActivationOffre Controler(string code, IdentiteOffre identite)
    {
        var a = CodeActivation.Lire(code, cles);
        if (a.Officine != identite.Officine)
            throw new ActivationInvalideException("Ce code a été émis pour une autre pharmacie (code de l’officine différent).");
        var nif = CodeActivation.NormaliserNif(identite.Nif);
        if (nif.Length == 0) throw new ActivationInvalideException("NIF de la pharmacie non saisi : saisissez-le avant l’activation.");
        if (a.Nif != nif) throw new ActivationInvalideException("Ce code a été émis pour un autre NIF que celui de la pharmacie.");
        return a;
    }

    /// <summary>Jours restants avant la fin du délai de grâce (négatif : échue depuis plus de 15 jours).</summary>
    public static int JoursAvantFermeture(DateOnly fin, DateOnly aujourdhui) => fin.AddDays(GraceJours).DayNumber - aujourdhui.DayNumber;

    public async Task<EtatOffre> LireAsync(CancellationToken ct = default)
    {
        if (cache?.Lire(horloge.MaintenantUtc) is { } garde) return garde;
        var generation = cache?.Generation;
        var etat = await CalculerAsync(ct);
        cache?.Ecrire(etat, horloge.MaintenantUtc, generation);
        return etat;
    }

    private async Task<EtatOffre> CalculerAsync(CancellationToken ct)
    {
        var identite = await depot.IdentiteAsync(ct);
        var date = await DateEffectiveAsync(ct);
        var aujourdhui = date.Aujourdhui;
        string? alerteHorloge = null;
        if (date is { Reculee: true, PlancherUtc: { } p })
        {
            // Alerte une seule fois au journal ; le message reste dans Mon offre tant que l'horloge retarde. Rien ne bloque (la caisse continue).
            await depot.SignalerHorlogeReculeeAsync(p, date.ServeurUtc, ct);
            alerteHorloge = $"L’horloge de ce serveur retarde sur la dernière date déjà enregistrée ({p.ToLocalTime():dd/MM/yyyy HH:mm}) : "
                            + "remettez-le à l’heure.";
        }
        else if (date is { Avance: true, PlancherUtc: { } pa })
        {
            // Correction du 2026-11-27 : saut de plus de 2 jours non écrit dans le fichier ; alerte une fois au journal, message tant que dure l'écart
            await depot.SignalerHorlogeAvanceAsync(pa, date.ServeurUtc, ct);
            alerteHorloge = MessageHorlogeAvance(pa, date.ServeurUtc);
        }
        var empreinte = PreuveCleServeur.Empreinte(cleServeur);
        var alerteCle = empreinte is not null ? (CleMaterielle() is false ? MessageCleLogicielle : null)
            : CleIllisible() ? MessageCleIllisible : null;
        string? refus = null, alerteServeur = null;
        // Délai de grâce « serveur non déclaré » : un seul, celui du code le plus récent concerné (constat de revue du 2026-11-28 : il
        // se renouvelait pour chaque code plus ancien encore valable)
        var graceServeurEpuisee = false;
        // Décision du titulaire du 2026-11-28 : un code version 1 (non lié aux serveurs) plus ancien qu'un code version 2 n'est plus pris.
        // Sinon, sur une base restaurée sur un autre PC, l'ancien code (de transition, par exemple) redevenait actif une fois le code
        // version 2 refusé.
        var codeLieVu = false;
        // Ordre des codes : la date d'émission SIGNÉE dans le code, jamais la colonne emise_le de la base (correction du 2026-11-28 : une
        // date modifiée dans la table remettait devant un ancien code plus large). Un code effacé de la table reste hors de portée.
        var valables = new List<(ActivationEnregistree E, ActivationOffre A)>();
        foreach (var lu in await depot.ListerAsync(ct))
        {
            try { valables.Add((lu, Controler(lu.Code, identite))); }
            catch (ActivationInvalideException x)
            {
                // Q4 : signature absente ou fausse, ou autre pharmacie : offre de base, signalé au titulaire (noté une fois au journal)
                await depot.SignalerRefusAsync(lu.Id, x.Message, ct);
                refus ??= $"Activation du {lu.EmiseLeUtc:dd/MM/yyyy} refusée : {x.Message}";
            }
        }
        foreach (var (e, a) in valables.OrderByDescending(v => v.A.EmiseLeUtc).ThenByDescending(v => v.E.Id))
        {
            if (!a.LieeAuxServeurs && codeLieVu)
            {
                refus ??= $"Activation du {e.EmiseLeUtc:dd/MM/yyyy} ignorée : un code plus récent, lié aux serveurs, la remplace.";
                continue;
            }
            if (a.LieeAuxServeurs) codeLieVu = true;
            if (a.Fin is { } fin && JoursAvantFermeture(fin, aujourdhui) < 0) continue;   // échue depuis plus de 15 jours
            string etat = EtatsOffre.Active;
            string? alerte = null;
            if (ProuverServeur(a) is { Autorisee: false } preuve)
            {
                // Point 5 (matériel) : serveur non déclaré ou clé absente → chemin d'une activation refusée, après 15 jours de grâce depuis la
                // première constatation (refusee_le, noté une fois au journal)
                var motif = MessageServeurNonDeclare(preuve.Empreinte, CleIllisible());
                await depot.SignalerRefusAsync(e.Id, motif, ct);
                var depuis = e.RefuseeLeUtc is { } r ? DateOnly.FromDateTime(r.ToLocalTime()) : aujourdhui;
                var finGrace = depuis.AddDays(GraceJours);
                // Première constatation datée dans le futur (horloge avancée au moment de la constatation, ou date modifiée dans la base) :
                // elle ne prolonge rien, la grâce est terminée (un jour d'écart toléré : horloges de l'API et de la base autour de minuit)
                var debutFutur = depuis > aujourdhui.AddDays(1);
                if (graceServeurEpuisee || debutFutur || aujourdhui > finGrace)
                {
                    graceServeurEpuisee = true;
                    refus ??= $"Activation du {e.EmiseLeUtc:dd/MM/yyyy} refusée : {motif}";
                    continue;
                }
                return new EtatOffre(EtatsOffre.Grace, a.Packs, a.Postes, a.ScannersMobiles, a.Fin, a.Transition, a.EmiseLeUtc,
                    $"{motif} Délai de grâce jusqu’au {finGrace:dd/MM/yyyy}, puis retour à l’offre de base (aucune donnée effacée).",
                    aujourdhui, empreinte, null, alerteHorloge, alerteCle);   // le message du serveur est déjà dans l'alerte principale
            }
            // Correction du 2026-11-27 (a) : un code rangé qui redevient valable perd sa marque de refus (journal) ; un nouveau refus ouvrira
            // un nouveau délai de grâce, compté depuis cette nouvelle constatation
            if (e.RefuseeLeUtc is not null) await depot.LeverRefusAsync(e.Id, ct);
            if (!a.LieeAuxServeurs) alerteServeur = MessageNonLie;
            if (a.Fin is { } f)
            {
                var restants = f.DayNumber - aujourdhui.DayNumber;
                if (restants < 0)
                {
                    etat = EtatsOffre.Grace;
                    alerte = $"Offre échue le {f:dd/MM/yyyy} : délai de grâce jusqu’au {f.AddDays(GraceJours):dd/MM/yyyy}, puis retour à l’offre de base "
                             + "(aucune donnée effacée). Demandez un nouveau code à l’éditeur.";
                }
                else if (restants <= RappelJours)
                    alerte = $"{(a.Transition ? "Période de transition" : "Offre")} valable jusqu’au {f:dd/MM/yyyy} ({restants} jour(s)) : demandez un nouveau code à l’éditeur.";
            }
            return new EtatOffre(etat, a.Packs, a.Postes, a.ScannersMobiles, a.Fin, a.Transition, a.EmiseLeUtc, alerte ?? refus,
                aujourdhui, empreinte, alerteServeur, alerteHorloge, alerteCle);
        }
        return new EtatOffre(EtatsOffre.Base, [], PostesBase, 0, null, false, null,
            refus ?? "Aucune activation : offre de base (2 postes). Saisissez le code d’activation fourni par l’éditeur.",
            aujourdhui, empreinte, null, alerteHorloge, alerteCle);
    }

    /// <summary>Message de Mon offre quand l'horloge du serveur est en avance de plus de 2 jours sur la date plancher.</summary>
    public static string MessageHorlogeAvance(DateTime plancherUtc, DateTime serveurUtc) =>
        $"L’horloge de ce serveur est en avance de {(int)(serveurUtc - plancherUtc).TotalDays} jour(s) sur la dernière date enregistrée "
        + $"({DateTime.SpecifyKind(plancherUtc, DateTimeKind.Utc).ToLocalTime():dd/MM/yyyy HH:mm}) : vérifiez-la avant d’encaisser.";

    /// <summary>Clé du serveur présente mais illisible par ce compte (jamais d'exception).</summary>
    public bool CleIllisible()
    {
        try { return cleServeur?.AccesRefuse() ?? false; }
        catch (System.Security.Cryptography.CryptographicException) { return false; }
    }

    private bool? CleMaterielle()
    {
        try { return cleServeur?.Materielle(); }
        catch (System.Security.Cryptography.CryptographicException) { return null; }
    }
}

/// <summary>
/// Rappels de fin d'offre au titulaire (décision Q5 du 2026-11-26, puis « go rappels » du 2026-11-27) : SMS ou e-mail à 90, 60, 30, 15, 7 et
/// 1 jour(s) avant la fin, puis au début du délai de grâce, une seule fois chacun pour une date de fin donnée ; seule l'étape la plus proche
/// est envoyée (une offre saisie à 10 jours de sa fin n'envoie que le rappel des 15 jours). L'écran Mon offre garde son message dès 90 jours.
/// Une étape n'est notée que si l'alerte a vraiment été mise en file (au moins un message) : sans destinataire ou en panne, elle est
/// retentée le lendemain. Appelé une fois par jour par la tâche des notifications.
/// </summary>
public sealed class RappelsOffre(OffreCourante offre, IActivationsOffreDepot depot, Notifications.IAlertesTitulaire alertes, IHorloge horloge)
{
    public const string EtapeGrace = "grace";

    /// <summary>Nom de l'étape d'un rappel avant la fin (« 90j », « 60j » … « 1j »).</summary>
    public static string EtapeAvant(int jours) => $"{jours}j";

    /// <summary>
    /// Étape due pour cette date de fin ce jour-là, ou null : le plus petit seuil (90, 60, 30, 15, 7, 1 jours) qui couvre les jours restants
    /// (le dernier jour compte dans « 1j ») ; « grace » pendant le délai de grâce ; null au-delà de 90 jours ou après la grâce.
    /// </summary>
    public static string? Etape(DateOnly fin, DateOnly aujourdhui)
    {
        var restants = fin.DayNumber - aujourdhui.DayNumber;
        if (restants < 0) return OffreCourante.JoursAvantFermeture(fin, aujourdhui) >= 0 ? EtapeGrace : null;
        string? etape = null;
        foreach (var seuil in OffreCourante.RappelsJours)
            if (restants <= seuil) etape = EtapeAvant(seuil);
        return etape;
    }

    /// <returns>Étape envoyée (au moins un message mis en file), ou null.</returns>
    public async Task<string?> ExecuterAsync(CancellationToken ct = default)
    {
        var e = await offre.LireAsync(ct);
        var aujourdhui = e.Jour ?? horloge.Aujourdhui;   // jour effectif : reculer l'horloge ne retarde aucun rappel
        if (e.Fin is not { } fin || Etape(fin, aujourdhui) is not { } etape) return null;
        if (await depot.RappelNoteAsync(etape, fin, ct)) return null;
        var quoi = e.Transition ? "La période de transition" : "Votre offre";
        var (objet, detail, sms) = etape switch
        {
            EtapeGrace => ("Offre échue : délai de grâce",
                $"{quoi} a pris fin le {fin:dd/MM/yyyy}. Délai de grâce jusqu’au {fin.AddDays(OffreCourante.GraceJours):dd/MM/yyyy}, puis retour à l’offre de base (2 postes, options fermées, aucune donnée effacée). Demandez un nouveau code à l’éditeur (Administration › Mon offre).",
                $"Pharmacie : offre echue le {fin:dd/MM}, grace jusqu au {fin.AddDays(OffreCourante.GraceJours):dd/MM}. Demandez un code a l editeur."),
            _ => ("Offre bientôt échue",
                $"{quoi} prend fin le {fin:dd/MM/yyyy} ({fin.DayNumber - aujourdhui.DayNumber} jour(s)). Demandez un nouveau code à l’éditeur (Administration › Mon offre) ; après la fin, {OffreCourante.GraceJours} jours de grâce puis retour à l’offre de base.",
                $"Pharmacie : offre valable jusqu au {fin:dd/MM}. Demandez un code a l editeur.")
        };
        // Mise en file d'abord, note ensuite : un arrêt entre les deux donne au pire un rappel en double, jamais un rappel perdu ;
        // aucun message mis en file (aucun destinataire, panne) : l'étape n'est pas notée, nouvel essai au passage du lendemain
        if (await alertes.OffreAsync(objet, detail, sms, ct) <= 0) return null;
        await depot.NoterRappelAsync(etape, fin, ct);
        return etape;
    }
}

/// <summary>Saisie d'une activation (écran « Mon offre », titulaire) ou réception par le serveur central (ordre activation_offre).</summary>
public sealed class GererOffre(IActivationsOffreDepot depot, OffreCourante offre, IPermissionService droits, IJournal journal, IHorloge horloge)
{
    public const string ActionActivee = "offre_activee";
    public const string SourceCode = "code", SourceCentral = "central";

    public async Task<(EtatOffre Etat, IdentiteOffre Identite, IReadOnlyList<ActivationEnregistree> Historique, int PostesComptes)> LireAsync(
        long utilisateurId, Func<CancellationToken, Task<int>> postesComptes, CancellationToken ct = default)
    {
        await droits.ExigerTitulaireAsync(utilisateurId, "consulter l’offre de la pharmacie", ct);
        return (await offre.LireAsync(ct), await depot.IdentiteAsync(ct), await depot.ListerAsync(ct), await postesComptes(ct));
    }

    /// <summary>Saisie d'un code par le titulaire (ou par l'éditeur lors de la mise en place, connecté en titulaire).</summary>
    public async Task<ActivationOffre> SaisirAsync(long utilisateurId, string? code, CancellationToken ct = default)
    {
        await droits.ExigerTitulaireAsync(utilisateurId, "saisir un code d’activation", ct);
        var a = await RangerAsync(code, SourceCode, utilisateurId, null, ct);
        await journal.EcrireAsync(utilisateurId, ActionActivee, "activation_offre", null, Details(a, SourceCode), null, ct);
        return a;
    }

    /// <summary>
    /// Activation transmise par le serveur central (Q2, Q7) : contrôlée ici comme une saisie ; le central ne peut pas en créer. Même entrée
    /// de journal « offre_activee » que la saisie, sans utilisateur local, avec la source « central » et l'identifiant de l'ordre.
    /// </summary>
    public async Task<ActivationOffre> RecevoirDuCentralAsync(string? code, Guid ordre, CancellationToken ct = default)
    {
        var a = await RangerAsync(code, SourceCentral, null, ordre, ct);
        var details = Details(a, SourceCentral);
        details["ordre"] = ordre.ToString("D");
        await journal.EcrireSansUtilisateurAsync(ActionActivee, "activation_offre", null, details, ct);
        return a;
    }

    private async Task<ActivationOffre> RangerAsync(string? code, string source, long? par, Guid? ordre, CancellationToken ct)
    {
        ActivationOffre a;
        try { a = offre.Controler(code ?? "", await depot.IdentiteAsync(ct)); }
        catch (ActivationInvalideException x) { throw new RegleMetierException(x.Message, "code"); }
        // Point 5 (matériel) : un code version 2 qui ne déclare pas ce serveur n'est pas pris (rien ne change dans l'offre en vigueur)
        if (offre.ProuverServeur(a) is { Autorisee: false } preuve)
            throw new RegleMetierException(OffreCourante.MessageServeurNonDeclare(preuve.Empreinte, offre.CleIllisible()), "code");
        var enVigueur = await offre.LireAsync(ct);
        // Jour effectif (point 5, horloge) : reculer l'horloge ne rend pas valable un code échu
        if (a.Fin is { } fin && OffreCourante.JoursAvantFermeture(fin, enVigueur.Jour ?? horloge.Aujourdhui) < 0)
            throw new RegleMetierException($"Code échu depuis le {fin:dd/MM/yyyy} (délai de grâce passé) : demandez un nouveau code à l’éditeur.", "code");
        if (enVigueur.EmiseLeUtc is { } derniere && a.EmiseLeUtc < derniere)
            throw new RegleMetierException($"Code plus ancien que l’activation en place (signée le {derniere:dd/MM/yyyy}) : il n’est pas pris.", "code");
        if (!await depot.AjouterAsync(code!.Trim(), a.EmiseLeUtc, source, par, ordre, ct))
            throw new RegleMetierException("Ce code est déjà enregistré.", "code");
        offre.Invalider();
        return a;
    }

    private static Dictionary<string, object?> Details(ActivationOffre a, string source) => new()
    {
        ["source"] = source, ["signee_le"] = a.EmiseLeUtc, ["packs"] = string.Join(",", a.Packs), ["postes"] = a.Postes,
        ["scanners_mobiles"] = a.ScannersMobiles, ["fin"] = a.Fin?.ToString("yyyy-MM-dd"), ["transition"] = a.Transition,
        ["serveurs"] = a.Serveurs is { } s ? string.Join(",", s) : null
    };
}
