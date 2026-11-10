using Pharma.Application.Commun;
using Pharma.Application.Securite;
using Pharma.Domain.Commun;
using Pharma.Domain.Factures;
using Pharma.Domain.Securite;

namespace Pharma.Application.Factures;

/// <summary>Ligne de vente proposée à la facturation, avec le patient, l'organisme et l'ordonnance de la vente.</summary>
public sealed record LigneVenteSource(LigneVendue Ligne, DateTime DateVente, long? PatientId, string? Patient, long? OrganismeId,
    long? OrdonnanceId, string? OrdonnanceNumero, bool DejaFacturee);

public sealed record LigneLibre(string Designation, decimal Quantite, decimal PrixTtc, decimal TauxTva);

/// <summary>Contenu demandé : ventes entières (toutes leurs lignes de la part concernée) et lignes libres.</summary>
public sealed record DemandeFacture(string Destinataire, long? PatientId, long? ClientId, long? OrganismeId, DateOnly? Date, DateOnly? Du, DateOnly? Au,
    IReadOnlyList<long> Ventes, IReadOnlyList<LigneLibre> Libres, string? Commentaire);

public sealed record FactureAEcrire(string Type, Destinataire Destinataire, long? PatientId, long? ClientId, long? OrganismeId, string DestinataireNom,
    string? DestinataireInfos, DateOnly Date, DateOnly? Du, DateOnly? Au, Totaux Totaux, decimal DejaPaye, IReadOnlyList<LigneFacture> Lignes,
    long? FactureOrigineId, string? Motif, string? Commentaire, long UtilisateurId);

public sealed record FactureResume(long Id, string Type, string Numero, DateOnly Date, string Destinataire, string DestinataireNom, decimal Ttc,
    decimal DejaPaye, decimal Avoirs, decimal Regle, decimal Reste, string Statut, int Version, bool HorsSuivi, string? MotifHorsSuivi,
    string? RemplaceNumero, string? RemplaceeParNumero, string? OrigineNumero, string? MotifRejet, long? OrganismeId);

public sealed record FactureDetail(FactureResume Resume, long? PatientId, long? ClientId, string? DestinataireInfos, DateOnly? Du, DateOnly? Au,
    decimal Ht, decimal Tva, string? Commentaire, string? Motif, string Employe, IReadOnlyList<LigneFacture> Lignes, IReadOnlyList<int> Versions);

public sealed record FiltreFactures(string? Recherche, string? Destinataire, string? Suivi, DateOnly? Du, DateOnly? Au);

public sealed record ClientFacture(long? Id, string RaisonSociale, string? Adresse, string? Telephone, string? Nif, string? Rc, string? Nis,
    string? ArticleImposition, bool Actif);

public sealed record OrganismeFiche(long? Id, string Code, string Nom, string? Adresse, string? Nif, bool Actif);

public interface IFactures
{
    Task<IReadOnlyList<LigneVenteSource>> CandidatesAsync(string part, long? patientId, long? organismeId, DateOnly du, DateOnly au, string? recherche,
        CancellationToken ct = default);
    /// <summary>Lignes des ventes demandées ; DejaFacturee ignore la facture à corriger ou remplacer (sauf).</summary>
    Task<IReadOnlyList<LigneVenteSource>> LignesDesVentesAsync(IReadOnlyList<long> ventes, string part, long? sauf, CancellationToken ct = default);
    /// <summary>Payé au comptoir (hors crédit) pour ces ventes, au plus leur part patient.</summary>
    Task<decimal> PayeAuComptoirAsync(IReadOnlyList<long> ventes, CancellationToken ct = default);
    Task<(string Nom, string? Infos)?> DestinataireAsync(Destinataire d, long id, CancellationToken ct = default);
    /// <summary>Numéro attribué sans trou ; remplaceId : l'ancienne facture passe « remplacée » dans la même transaction.</summary>
    Task<(long Id, string Numero)> EmettreAsync(FactureAEcrire f, long? remplaceId, CancellationToken ct = default);
    Task<IReadOnlyList<FactureResume>> ListerAsync(FiltreFactures filtre, CancellationToken ct = default);
    /// <summary>Version null : la version en vigueur ; sinon une version précédente (correction après rejet).</summary>
    Task<FactureDetail?> DetailAsync(long id, int? version, CancellationToken ct = default);
    Task RejeterAsync(long id, string motif, CancellationToken ct = default);
    /// <summary>Garde la version en vigueur dans l'historique, remplace contenu et totaux, version + 1, statut émise.</summary>
    Task CorrigerAsync(long id, FactureAEcrire f, string? motif, CancellationToken ct = default);
    Task HorsSuiviAsync(long id, bool horsSuivi, string? motif, CancellationToken ct = default);
    Task<IReadOnlyList<ClientFacture>> ClientsAsync(CancellationToken ct = default);
    Task<long> EnregistrerClientAsync(ClientFacture c, CancellationToken ct = default);
    Task<IReadOnlyList<OrganismeFiche>> OrganismesAsync(CancellationToken ct = default);
    Task<long> EnregistrerOrganismeAsync(OrganismeFiche o, CancellationToken ct = default);
}

public interface IDocumentsFactures
{
    byte[] Facture(FactureDetail f, Administration.IdentitePharmacie pharmacie);
}

public sealed class ConsulterFactures(IFactures factures, IPermissionService droits)
{
    public async Task<IReadOnlyList<LigneVenteSource>> CandidatesAsync(string? destinataire, long? patientId, long? organismeId, DateOnly du, DateOnly au,
        string? recherche, long utilisateurId, CancellationToken ct = default)
    {
        await droits.ExigerAsync(utilisateurId, Permissions.FacturesGerer, "préparer une facture", ct);
        var d = RegleFacture.Lire(destinataire);
        if (au < du) throw new RegleMetierException("La date de fin précède la date de début.");
        return await factures.CandidatesAsync(RegleFacture.Part(d), d == Destinataire.Patient ? patientId : null,
            d == Destinataire.Organisme ? organismeId : null, du, au, string.IsNullOrWhiteSpace(recherche) ? null : recherche.Trim(), ct);
    }

    public async Task<IReadOnlyList<FactureResume>> ListerAsync(FiltreFactures filtre, long utilisateurId, CancellationToken ct = default)
    {
        await droits.ExigerAsync(utilisateurId, Permissions.FacturesGerer, "consulter les factures", ct);
        return await factures.ListerAsync(filtre, ct);
    }

    public async Task<FactureDetail> DetailAsync(long id, int? version, long utilisateurId, CancellationToken ct = default)
    {
        await droits.ExigerAsync(utilisateurId, Permissions.FacturesGerer, "consulter les factures", ct);
        return await factures.DetailAsync(id, version, ct) ?? throw new RegleMetierException("Facture introuvable.");
    }

    public async Task<IReadOnlyList<ClientFacture>> ClientsAsync(long utilisateurId, CancellationToken ct = default)
    {
        await droits.ExigerAsync(utilisateurId, Permissions.FacturesGerer, "consulter les clients facturés", ct);
        return await factures.ClientsAsync(ct);
    }
}

/// <summary>Émission, rejet, correction, remplacement, avoir et suivi des factures clients (droit « factures.gerer »).</summary>
public sealed class Facturation(IFactures factures, IPermissionService droits, IHorloge horloge, IJournal journal)
{
    public async Task<(long Id, string Numero)> EmettreAsync(DemandeFacture demande, long utilisateurId, CancellationToken ct = default)
    {
        await droits.ExigerAsync(utilisateurId, Permissions.FacturesGerer, "émettre une facture", ct);
        var f = await PreparerAsync(demande, sauf: null, utilisateurId, ct);
        var r = await factures.EmettreAsync(f, null, ct);
        await Journaliser(utilisateurId, "facture_emise", r.Id, r.Numero, f);
        return r;
    }

    public async Task RejeterAsync(long id, string? motif, long utilisateurId, CancellationToken ct = default)
    {
        await droits.ExigerAsync(utilisateurId, Permissions.FacturesGerer, "enregistrer le rejet d’une facture", ct);
        var f = await Detail(id, ct);
        RegleFacture.VerifierRejet(f.Resume.Type, f.Resume.Statut, motif);
        await factures.RejeterAsync(id, motif!.Trim(), ct);
        await journal.EcrireAsync(utilisateurId, "facture_rejetee", "facture_client", id,
            new Dictionary<string, object?> { ["numero"] = f.Resume.Numero, ["motif"] = motif }, Permissions.FacturesGerer, ct);
    }

    /// <summary>Correction sur place après rejet : même numéro, version + 1 ; la version précédente reste consultable et imprimable.</summary>
    public async Task CorrigerAsync(long id, DemandeFacture demande, string? motif, long utilisateurId, CancellationToken ct = default)
    {
        await droits.ExigerAsync(utilisateurId, Permissions.FacturesGerer, "corriger une facture rejetée", ct);
        var ancienne = await Detail(id, ct);
        RegleFacture.VerifierReprise(ancienne.Resume.Statut);
        var f = await PreparerAsync(MemeDestinataire(demande, ancienne), sauf: id, utilisateurId, ct);
        await factures.CorrigerAsync(id, f, string.IsNullOrWhiteSpace(motif) ? null : motif.Trim(), ct);
        await Journaliser(utilisateurId, "facture_corrigee", id, ancienne.Resume.Numero, f);
    }

    /// <summary>Remplacement après rejet : nouvelle facture (nouveau numéro) ; l'ancienne passe « remplacée », hors comptabilité.</summary>
    public async Task<(long Id, string Numero)> RemplacerAsync(long id, DemandeFacture demande, long utilisateurId, CancellationToken ct = default)
    {
        await droits.ExigerAsync(utilisateurId, Permissions.FacturesGerer, "remplacer une facture rejetée", ct);
        var ancienne = await Detail(id, ct);
        RegleFacture.VerifierReprise(ancienne.Resume.Statut);
        var f = await PreparerAsync(MemeDestinataire(demande, ancienne), sauf: id, utilisateurId, ct);
        var r = await factures.EmettreAsync(f, id, ct);
        await Journaliser(utilisateurId, "facture_remplacee", r.Id, r.Numero, f, ancienne.Resume.Numero);
        return r;
    }

    /// <summary>Avoir sur une facture (montant et motif) : diminue ce qui reste à encaisser.</summary>
    public async Task<(long Id, string Numero)> AvoirAsync(long factureId, decimal montant, string? motif, long utilisateurId, CancellationToken ct = default)
    {
        await droits.ExigerAsync(utilisateurId, Permissions.FacturesGerer, "émettre un avoir", ct);
        var f = await Detail(factureId, ct);
        RegleFacture.VerifierAvoir(f.Resume.Type, f.Resume.Statut, f.Resume.Ttc, f.Resume.Avoirs, montant, motif);
        var ht = f.Resume.Ttc == 0 ? montant : decimal.Round(montant * f.Ht / f.Resume.Ttc, 2);
        var ligne = new LigneFacture(null, null, "libre", $"Avoir sur la facture {f.Resume.Numero} : {motif!.Trim()}", 1, montant, 0, montant);
        var avoir = new FactureAEcrire("avoir", RegleFacture.Lire(f.Resume.Destinataire), f.PatientId, f.ClientId, f.Resume.OrganismeId,
            f.Resume.DestinataireNom, f.DestinataireInfos, horloge.Aujourdhui, null, null, new Totaux(ht, montant - ht, montant), 0, [ligne],
            factureId, motif.Trim(), null, utilisateurId);
        var r = await factures.EmettreAsync(avoir, null, ct);
        await journal.EcrireAsync(utilisateurId, "avoir_emis", "facture_client", r.Id,
            new Dictionary<string, object?> { ["numero"] = r.Numero, ["facture"] = f.Resume.Numero, ["montant"] = montant, ["motif"] = motif },
            Permissions.FacturesGerer, ct);
        return r;
    }

    /// <summary>Le pharmacien retire une facture du suivi de trésorerie (motif) ou l'y remet ; elle reste valable et imprimable.</summary>
    public async Task SuiviAsync(long id, bool horsSuivi, string? motif, long utilisateurId, CancellationToken ct = default)
    {
        await droits.ExigerAsync(utilisateurId, Permissions.FacturesGerer, "retirer une facture du suivi", ct);
        var f = await Detail(id, ct);
        if (f.Resume.Type != "facture") throw new RegleMetierException("Un avoir n’est pas suivi en trésorerie.");
        RegleFacture.VerifierHorsSuivi(horsSuivi, motif);
        await factures.HorsSuiviAsync(id, horsSuivi, horsSuivi ? motif!.Trim() : null, ct);
        await journal.EcrireAsync(utilisateurId, horsSuivi ? "facture_hors_suivi" : "facture_remise_en_suivi", "facture_client", id,
            new Dictionary<string, object?> { ["numero"] = f.Resume.Numero, ["motif"] = motif }, Permissions.FacturesGerer, ct);
    }

    public async Task<long> EnregistrerClientAsync(ClientFacture c, long utilisateurId, CancellationToken ct = default)
    {
        await droits.ExigerAsync(utilisateurId, Permissions.FacturesGerer, "gérer les clients facturés", ct);
        if (string.IsNullOrWhiteSpace(c.RaisonSociale)) throw new RegleMetierException("Indiquez la raison sociale.");
        static string? N(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
        return await factures.EnregistrerClientAsync(c with
        {
            RaisonSociale = c.RaisonSociale.Trim(), Adresse = N(c.Adresse), Telephone = N(c.Telephone), Nif = N(c.Nif), Rc = N(c.Rc), Nis = N(c.Nis),
            ArticleImposition = N(c.ArticleImposition)
        }, ct);
    }

    /// <summary>Organismes (CNAS, CASNOS, militaires…) : réservé au titulaire, car ils servent aussi au tiers payant.</summary>
    public async Task<long> EnregistrerOrganismeAsync(OrganismeFiche o, long utilisateurId, CancellationToken ct = default)
    {
        await droits.ExigerTitulaireAsync(utilisateurId, "gérer les organismes", ct);
        if (string.IsNullOrWhiteSpace(o.Code) || string.IsNullOrWhiteSpace(o.Nom)) throw new RegleMetierException("Indiquez le sigle et le nom de l’organisme.");
        static string? N(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
        var id = await factures.EnregistrerOrganismeAsync(o with { Code = o.Code.Trim().ToUpperInvariant(), Nom = o.Nom.Trim(), Adresse = N(o.Adresse), Nif = N(o.Nif) }, ct);
        await journal.EcrireAsync(utilisateurId, o.Id is null ? "organisme_cree" : "organisme_modifie", "organisme", id,
            new Dictionary<string, object?> { ["code"] = o.Code, ["nom"] = o.Nom, ["actif"] = o.Actif }, null, ct);
        return id;
    }

    private async Task<FactureDetail> Detail(long id, CancellationToken ct) =>
        await factures.DetailAsync(id, null, ct) ?? throw new RegleMetierException("Facture introuvable.");

    private static DemandeFacture MemeDestinataire(DemandeFacture d, FactureDetail ancienne) => d with
    {
        Destinataire = ancienne.Resume.Destinataire, PatientId = ancienne.PatientId, ClientId = ancienne.ClientId, OrganismeId = ancienne.Resume.OrganismeId
    };

    /// <summary>Contrôle le destinataire et les ventes (du bon patient ou organisme, pas déjà facturées pour cette part), calcule lignes et totaux.</summary>
    private async Task<FactureAEcrire> PreparerAsync(DemandeFacture d, long? sauf, long utilisateurId, CancellationToken ct)
    {
        var dest = RegleFacture.Lire(d.Destinataire);
        var idDest = dest switch { Destinataire.Patient => d.PatientId, Destinataire.Entreprise => d.ClientId, _ => d.OrganismeId }
                     ?? throw new RegleMetierException("Choisissez le destinataire de la facture.");
        var (nom, infos) = await factures.DestinataireAsync(dest, idDest, ct) ?? throw new RegleMetierException("Destinataire introuvable ou inactif.");

        var ventes = d.Ventes.Distinct().ToList();
        var sources = ventes.Count == 0 ? [] : await factures.LignesDesVentesAsync(ventes, RegleFacture.Part(dest), sauf, ct);
        if (sources.Select(s => s.Ligne.VenteId).Distinct().Count() != ventes.Count)
            throw new RegleMetierException("Une des ventes choisies est introuvable ou n’est plus validée.");
        if (sources.FirstOrDefault(s => s.DejaFacturee) is { } dejaFait)
            throw new RegleMetierException($"La vente {dejaFait.Ligne.NumeroVente} est déjà facturée pour cette part.");
        if (dest == Destinataire.Patient && sources.FirstOrDefault(s => s.PatientId != idDest) is { } autre)
            throw new RegleMetierException($"La vente {autre.Ligne.NumeroVente} n’est pas au nom de ce patient.");
        if (dest == Destinataire.Organisme && sources.FirstOrDefault(s => s.OrganismeId != idDest) is { } horsOrg)
            throw new RegleMetierException($"La vente {horsOrg.Ligne.NumeroVente} n’est pas prise en charge par cet organisme.");

        var lignes = RegleFacture.DepuisVentes(dest, sources.Select(s => s.Ligne)).ToList();
        if (ventes.FirstOrDefault(v => lignes.All(l => l.VenteId != v)) is var vide && vide != 0)
            throw new RegleMetierException($"La vente {sources.First(s => s.Ligne.VenteId == vide).Ligne.NumeroVente} n’a rien à facturer pour cette part.");
        lignes.AddRange(d.Libres.Select(l => RegleFacture.Libre(l.Designation, l.Quantite, l.PrixTtc, l.TauxTva)));
        RegleFacture.VerifierEmission(lignes);

        var dejaPaye = dest == Destinataire.Organisme || ventes.Count == 0 ? 0 : await factures.PayeAuComptoirAsync(ventes, ct);
        var date = d.Date ?? horloge.Aujourdhui;
        if (date > horloge.Aujourdhui) throw new RegleMetierException("La date de la facture ne peut pas être dans le futur.");
        if (d.Du is { } du && d.Au is { } au && au < du) throw new RegleMetierException("La période facturée est inversée.");
        return new FactureAEcrire("facture", dest, dest == Destinataire.Patient ? idDest : null, dest == Destinataire.Entreprise ? idDest : null,
            dest == Destinataire.Organisme ? idDest : null, nom, infos, date, d.Du, d.Au, RegleFacture.Calculer(lignes), dejaPaye, lignes, null, null,
            string.IsNullOrWhiteSpace(d.Commentaire) ? null : d.Commentaire.Trim(), utilisateurId);
    }

    private Task Journaliser(long utilisateurId, string action, long id, string numero, FactureAEcrire f, string? remplace = null) =>
        journal.EcrireAsync(utilisateurId, action, "facture_client", id, new Dictionary<string, object?>
        {
            ["numero"] = numero, ["destinataire"] = f.DestinataireNom, ["ttc"] = f.Totaux.Ttc, ["lignes"] = f.Lignes.Count, ["remplace"] = remplace
        }, Permissions.FacturesGerer, CancellationToken.None);
}
