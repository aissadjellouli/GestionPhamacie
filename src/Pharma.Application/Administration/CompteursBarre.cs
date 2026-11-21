using Pharma.Application.Achats;
using Pharma.Application.Caisse;
using Pharma.Application.Chifa;
using Pharma.Application.Commun;
using Pharma.Application.Integrite;
using Pharma.Application.Securite;
using Pharma.Domain.Securite;

namespace Pharma.Application.Administration;

// ---- Barre d'état du poste : compteurs des pastilles (2026-11-21, maquette « Barre d'état » validée) ----
// Une seule lecture groupée et légère, ouverte à tout employé connecté : chaque compteur n'est rendu qu'à l'employé qui a le droit
// de l'écran correspondant (0 ou faux sinon). Aucune donnée personnelle ni montant : des nombres et un drapeau.

/// <summary>
/// Compteurs de la barre d'état. 0 (ou faux) : rien à signaler, ou pas le droit de le voir.
/// VentesEnAttente : paniers mis de côté sur ce poste (caisse.vendre). ProduitsDisponibles : demandes de patients pas encore prévenus et
/// produits dus des ventes en attente, redevenus disponibles (caisse.vendre). VoletsChifa : ventes Chifa en attente de volet (caisse.vendre).
/// ChifaADeposer : ventes Chifa à déposer en bordereau (chifa.bordereaux). TraitesAPayer : traites échues ou à échéance sous 7 jours
/// (achats.regler). JournauxEnAnomalie : dernière vérification d'une chaîne de journaux rompue ou en erreur (titulaire).
/// </summary>
public sealed record CompteursBarre(int VentesEnAttente, int ProduitsDisponibles, int VoletsChifa, int ChifaADeposer, int TraitesAPayer,
    bool JournauxEnAnomalie)
{
    public static readonly CompteursBarre Rien = new(0, 0, 0, 0, 0, false);
}

/// <summary>Droits utiles à la barre : seuls ces droits ouvrent un compteur (règle testée sans base).</summary>
public static class RegleCompteursBarre
{
    /// <summary>Compteurs bruts filtrés selon les droits de l'employé (le reste est remis à 0 / faux).</summary>
    public static CompteursBarre Filtrer(CompteursBarre bruts, IReadOnlyCollection<string> droits, bool titulaire)
    {
        var vendre = droits.Contains(Permissions.CaisseVendre);
        return new CompteursBarre(
            vendre ? bruts.VentesEnAttente : 0,
            vendre ? bruts.ProduitsDisponibles : 0,
            vendre ? bruts.VoletsChifa : 0,
            droits.Contains(Permissions.ChifaBordereaux) ? bruts.ChifaADeposer : 0,
            droits.Contains(Permissions.AchatsRegler) ? bruts.TraitesAPayer : 0,
            titulaire && bruts.JournauxEnAnomalie);
    }
}

/// <summary>
/// Mémoire de 30 secondes des compteurs : ceux de l'officine (identiques pour tous les postes) et les ventes en attente de chaque poste.
/// Chaque poste relit la barre toutes les minutes et à chaque changement d'écran : la base n'est interrogée qu'une fois par période.
/// </summary>
public sealed class MemoireCompteursBarre(IHorloge horloge)
{
    public static readonly TimeSpan Duree = TimeSpan.FromSeconds(30);
    /// <summary>Postes gardés au plus (au-delà, la mémoire des postes est vidée) : borne la mémoire du serveur.</summary>
    public const int PostesMax = 200;
    private readonly object verrou = new();
    private (DateTime Quand, CompteursBarre Officine)? officine;
    private readonly Dictionary<string, (DateTime Quand, int Nombre)> postes = new(StringComparer.Ordinal);

    private bool Frais(DateTime quand) => horloge.MaintenantUtc >= quand && horloge.MaintenantUtc - quand < Duree;

    public bool LireOfficine(out CompteursBarre c)
    {
        lock (verrou)
            if (officine is { } o && Frais(o.Quand)) { c = o.Officine; return true; }
        c = CompteursBarre.Rien;
        return false;
    }

    public void GarderOfficine(CompteursBarre c) { lock (verrou) officine = (horloge.MaintenantUtc, c); }

    public bool LirePoste(string poste, out int nombre)
    {
        lock (verrou)
            if (postes.TryGetValue(poste, out var p) && Frais(p.Quand)) { nombre = p.Nombre; return true; }
        nombre = 0;
        return false;
    }

    public void GarderPoste(string poste, int nombre)
    {
        lock (verrou)
        {
            if (postes.Count >= PostesMax && !postes.ContainsKey(poste)) postes.Clear();
            postes[poste] = (horloge.MaintenantUtc, nombre);
        }
    }
}

/// <summary>
/// Lecture des compteurs de la barre d'état pour l'employé connecté. Chaque compteur est lu à part : une erreur de lecture le laisse à 0
/// (rien n'est affiché) sans rien bloquer. Les compteurs de l'officine sont lus pour tous puis filtrés par les droits de l'employé.
/// </summary>
public sealed class ConsulterCompteursBarre(IVentesEnAttente ventesEnAttente, IDemandesPatients demandes, IRapprochementsVolet volets,
    IBordereaux bordereaux, ITraites traites, IIntegriteDepot integrite, IPermissionService droits, MemoireCompteursBarre memoire, IHorloge horloge,
    ITraceErreurs? trace = null)
{
    public async Task<CompteursBarre> ExecuterAsync(long utilisateurId, string? poste, CancellationToken ct = default)
    {
        var liste = await droits.ListerAsync(utilisateurId, ct);
        var titulaire = await droits.EstTitulaireAsync(utilisateurId, ct);
        if (!titulaire && !liste.Contains(Permissions.CaisseVendre) && !liste.Contains(Permissions.ChifaBordereaux)
            && !liste.Contains(Permissions.AchatsRegler))
            return CompteursBarre.Rien;

        if (!memoire.LireOfficine(out var officine))
        {
            officine = new CompteursBarre(0,
                await LireAsync("produits redevenus disponibles", async () =>
                    (await demandes.DisponiblesAsync(ct)).Count + (await demandes.ProduitsDusDisponiblesAsync(ct)).Count),
                await LireAsync("ventes Chifa en attente de volet", async () =>
                    (await volets.EnAttenteAsync(null, new DemandePage(1, DemandePage.TailleMin), ct)).Total),
                await LireAsync("ventes Chifa à déposer", async () => (await bordereaux.CompteursAsync(ct)).VentesADeposer),
                await LireAsync("traites à payer", async () =>
                    (await traites.ListerAsync(FiltreTraites.Lire("a_payer", null, horloge.Aujourdhui), new DemandePage(1, DemandePage.TailleMin), ct)).Total),
                await LireAsync("intégrité des journaux", async () =>
                    (await integrite.DernieresVerificationsAsync(ct)).Any(v => v.Statut != VerifierIntegrite.Ok) ? 1 : 0) > 0);
            memoire.GarderOfficine(officine);
        }

        var enAttente = 0;
        if (!string.IsNullOrWhiteSpace(poste) && liste.Contains(Permissions.CaisseVendre))
        {
            var p = poste.Trim();
            if (!memoire.LirePoste(p, out enAttente))
            {
                enAttente = await LireAsync("ventes en attente du poste", async () => (await ventesEnAttente.ListerAsync(p, ct)).Count);
                memoire.GarderPoste(p, enAttente);
            }
        }
        return RegleCompteursBarre.Filtrer(officine with { VentesEnAttente = enAttente }, liste, titulaire);
    }

    private async Task<int> LireAsync(string quoi, Func<Task<int>> lire)
    {
        try { return Math.Max(0, await lire()); }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            trace?.Erreur($"Compteur de la barre d'état illisible : {quoi}", e);
            return 0;
        }
    }
}
