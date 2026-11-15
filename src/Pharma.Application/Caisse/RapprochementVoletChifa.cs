using Pharma.Application.Commun;
using Pharma.Application.Patients;
using Pharma.Application.Securite;
using Pharma.Domain.Commun;
using Pharma.Domain.Securite;
using Pharma.Domain.Ventes;

namespace Pharma.Application.Caisse;

// ---- Circuit « la caisse d'abord » (règle du titulaire, 2026-11-15) : le volet Chifa scanné est rapproché d'une vente déjà encaissée ----

/// <summary>
/// Vente Chifa encaissée en attente de son volet : canal chifa, sans n° de facture Chifa, non annulée, organisme CNAS ou CASNOS.
/// Taux : taux de prise en charge de la vente (null : reste à charge saisi à la main). Lignes : vides dans les listes paginées.
/// </summary>
/// <remarks>
/// PartPatient, DettePatient : pour la régularisation de la part organisme (script 097). MotifAttente : motif de la vente encaissée
/// sans n° de facture Chifa (libellé, ou « Autre : … » ; null avant 097).
/// </remarks>
public sealed record VenteAttenteVolet(long Id, string Numero, DateTime Date, long? PatientId, string? Patient, string? NumAssure, string? Organisme,
    decimal? Taux, decimal Total, decimal PartOrganisme, string Vendeur, IReadOnlyList<LigneVenteRapprochee> Lignes,
    decimal PartPatient = 0, decimal DettePatient = 0, string? MotifAttente = null, bool EnRetard = false, decimal Majoration = 0);

/// <summary>
/// Volet rattaché malgré des différences ou avec une régularisation de part organisme : motif, résumé des différences, écart de part
/// organisme, qui et quand ; Regularisation : libellé de la régularisation (script 097), null sans régularisation.
/// ARendre : trop-payé à rendre au patient (avoir) ; BonRendu : bon du tiroir qui l'a rendu (script 099), null tant qu'il ne l'est pas.
/// </summary>
public sealed record VoletAvecEcart(long VenteId, string Numero, DateTime DateVente, string? Patient, string NumeroFacture, DateTime? RattacheLe,
    string? RattachePar, string Motif, string? Detail, decimal? EcartPartOrganisme, string? Regularisation = null,
    long? PatientId = null, decimal ARendre = 0, string? BonRendu = null);

/// <summary>Ce qui s'écrit sur la vente au rattachement (n° de facture, date, QR, date d'ordonnance, qui, motif et différences éventuels).</summary>
/// <param name="Regularisation">Écart de part organisme régularisé (script 097), écrit dans la même transaction ; null : écart nul.</param>
public sealed record RattachementVolet(long VenteId, QrChifa Qr, long UtilisateurId, string? Motif, string? DetailEcarts, decimal? EcartPartOrganisme,
    RegularisationCalculee? Regularisation = null, long? PatientId = null, long? OrganismeId = null, string? Reference = null);

public interface IRapprochementsVolet
{
    /// <summary>Ventes en attente de volet encaissées depuis cette date (UTC), avec leurs lignes (plus récentes d'abord, <paramref name="max"/> au plus).</summary>
    Task<IReadOnlyList<VenteAttenteVolet>> CandidatesAsync(DateTime depuisUtc, int max, CancellationToken ct = default);
    /// <summary>Vente encore en attente de volet (avec ses lignes), sinon null.</summary>
    Task<VenteAttenteVolet?> VenteAsync(long venteId, CancellationToken ct = default);
    /// <summary>Écrit le volet sur la vente si elle l'attend toujours (faux sinon) ; volet déjà rattaché ailleurs : RegleMetierException.</summary>
    Task<bool> RattacherAsync(RattachementVolet r, CancellationToken ct = default);
    /// <summary>Liste « Ventes Chifa en attente de volet » (sans lignes), filtrée (n° de vente, patient, n° d'assuré) et paginée, plus anciennes d'abord.</summary>
    Task<(IReadOnlyList<VenteAttenteVolet> Lignes, int Total)> EnAttenteAsync(string? recherche, DemandePage page, CancellationToken ct = default);
    /// <summary>Liste « Volets avec écart », filtrée et paginée, plus récents d'abord.</summary>
    Task<(IReadOnlyList<VoletAvecEcart> Lignes, int Total)> AvecEcartAsync(string? recherche, DemandePage page, CancellationToken ct = default);
}

/// <summary>
/// Vente candidate au rattachement : comparaison ligne à ligne ; MemeAssure : patient de même n° d'assuré + suffixe que le volet ;
/// MemeCarte : même n° de base, autre suffixe (ayant droit) ; EnRetard : encaissée avant le délai réglable (toujours rattachable).
/// </summary>
public sealed record CandidateVolet(VenteAttenteVolet Vente, bool MemeAssure, ComparaisonVolet Comparaison, bool MemeCarte = false, bool EnRetard = false);

/// <summary>Volet lu et ventes candidates, la meilleure d'abord (même assuré, même carte, dans le délai, proximité, la plus récente).</summary>
public sealed record PropositionRapprochement(VoletChifaLu Volet, decimal Seuil, IReadOnlyList<CandidateVolet> Candidates, int DelaiJours = RapprocherVoletChifa.DelaiParDefaut);

/// <summary>
/// Résultat du rattachement : n° de la vente, différences acceptées (motif), message de mise à jour du catalogue, régularisation (libellé) ;
/// ARendre : trop-payé à rendre au patient par le tiroir (« Rembourser », recette du 2026-11-15, D4), avec la vente et le patient.
/// </summary>
public sealed record VoletRattacheResultat(string Vente, bool AvecEcart, decimal EcartPartOrganisme, string? Catalogue, string? Regularisation = null,
    decimal ARendre = 0, long VenteId = 0, long? PatientId = null, string? Patient = null);

/// <summary>
/// Rapprochement d'un volet Chifa avec une vente déjà encaissée (règle du titulaire, 2026-11-15). Candidates : ventes en attente de volet
/// encaissées, toutes (aucune expiration, décision du titulaire du 2026-11-15) : celles d'avant le délai réglable (Administration › Caisse,
/// 7 jours par défaut) sont « en retard », proposées après les autres et choisies à la main. Patient de même n° d'assuré + suffixe :
/// seulement ses ventes ; à défaut, toutes, celles de la même carte (même n° de base, ayants droit) d'abord ; puis proximité.
/// Rattacher n'ajoute ni ne retire rien à la vente (stock, montant total, part enregistrée à la caisse inchangés) : n° de facture Chifa,
/// date, QR, date d'ordonnance, auteur et date du rattachement ; puis mise à jour du catalogue d'après le volet (règle existante).
/// Différences en rouge : rattachement réservé au droit prix.modifier (ou code comptoir d'un ayant droit), motif obligatoire, journalisé.
/// Écart de part organisme : régularisé (<see cref="RegularisationPartOrganisme"/>, journal <see cref="ActionRegularisation"/>).
/// </summary>
public sealed class RapprocherVoletChifa(IVoletsChifa volets, IRapprochementsVolet ventes, ISessionsCaisse sessions, IPermissionService droits,
    IJournal journal, IHorloge horloge, ITraceErreurs? trace = null)
{
    /// <summary>Délai par défaut des ventes candidates (parametre_caisse.delai_volet_chifa_jours, script 097).</summary>
    public const int DelaiParDefaut = 7;
    public const int CandidatesMax = 200;
    public const int MotifMax = 500;
    public const string Action = "volet_chifa_rattache";
    public const string ActionRegularisation = "regularisation_part_organisme";
    /// <summary>Aucune expiration : les ventes en attente sont lues depuis cette date.</summary>
    public static readonly DateTime SansLimite = new(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public async Task<PropositionRapprochement> ProposerAsync(string? texte, long utilisateurId, CancellationToken ct = default)
    {
        // Droit de vendre, QR reconnu, volet pas déjà repris ni rattaché : mêmes contrôles que la reprise d'un volet
        var lu = await new LireVoletChifa(volets, droits).ExecuterAsync(texte, utilisateurId, ct);
        var parametres = await sessions.ParametresAsync(ct);
        var seuil = parametres.SeuilEcartChifa;
        var delai = parametres.DelaiVoletChifaJours is >= 1 and <= 365 ? parametres.DelaiVoletChifaJours : DelaiParDefaut;
        var limite = horloge.MaintenantUtc.AddDays(-delai);
        var toutes = await ventes.CandidatesAsync(SansLimite, CandidatesMax, ct);
        var assure = PatientScanVolet.Chiffres(lu.Qr.NumeroAssure, lu.Qr.SuffixeAssure);
        var lignesVolet = LignesVolet(lu);
        var candidates = toutes
            .Select(v =>
            {
                var meme = assure.Length > 0 && RegleNumAssure.Chiffres(v.NumAssure) == assure;
                return new CandidateVolet(v, meme,
                    RapprochementVolet.AvecAssure(RapprochementVolet.Comparer(lignesVolet, v.Lignes, seuil, v.Taux, v.PartOrganisme, v.Majoration), lu.Qr.AssureComplet, v.NumAssure, v.Patient),
                    !meme && RapprochementVolet.MemeCarte(lu.Qr.NumeroAssure, v.NumAssure), DateTime.SpecifyKind(v.Date, DateTimeKind.Utc) < limite);
            })
            .ToList();
        if (candidates.Any(c => c.MemeAssure)) candidates = candidates.Where(c => c.MemeAssure).ToList();
        return new PropositionRapprochement(lu, seuil,
            candidates.OrderByDescending(c => c.MemeAssure).ThenByDescending(c => c.MemeCarte).ThenBy(c => c.EnRetard)
                .ThenByDescending(c => c.Comparaison.Score).ThenByDescending(c => c.Vente.Date).ToList(), delai);
    }

    /// <param name="regularisation">
    /// Écart de part organisme où la caisse paiera moins : « charge_patient » ou « perte_pharmacie » (droit prix.modifier ou code comptoir) ;
    /// ignoré sinon.
    /// </param>
    public async Task<VoletRattacheResultat> RattacherAsync(string? texte, long venteId, string? motif, long utilisateurId, string? regularisation = null,
        CancellationToken ct = default)
    {
        var lu = await new LireVoletChifa(volets, droits).ExecuterAsync(texte, utilisateurId, ct);
        var vente = await ventes.VenteAsync(venteId, ct)
                    ?? throw new RegleMetierException("Cette vente n’attend plus de volet Chifa (déjà rattachée, annulée ou hors Chifa).", "vente");
        var seuil = (await sessions.ParametresAsync(ct)).SeuilEcartChifa;
        var comparaison = RapprochementVolet.AvecAssure(RapprochementVolet.Comparer(LignesVolet(lu), vente.Lignes, seuil, vente.Taux, vente.PartOrganisme, vente.Majoration),
            lu.Qr.AssureComplet, vente.NumAssure, vente.Patient);
        var m = string.IsNullOrWhiteSpace(motif) ? null : string.Join(' ', motif.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        string? resume = null;
        if (!comparaison.Concorde)
        {
            // Différences : ayant droit prix.modifier (ou son code comptoir), motif obligatoire ; la vente n'est pas modifiée
            await ExigerPrixAsync(utilisateurId, "rattacher un volet Chifa qui ne correspond pas à la vente", ct);
            if (m is null) throw new RegleMetierException("Le volet ne correspond pas à la vente : saisissez le motif du rattachement.", "motif");
            if (m.Length > MotifMax) throw new RegleMetierException($"Motif : {MotifMax} caractères au plus.", "motif");
            resume = RapprochementVolet.Resume(comparaison);
        }
        else m = null;
        var ecartPart = comparaison.EcartPartOrganisme;
        // Écart de part organisme (décision du titulaire, 2026-11-15) : créance ramenée à la part Chifa ; la caisse paiera moins :
        // choix d'un ayant droit (prix.modifier ou code comptoir) entre « à la charge du patient » et « perte pharmacie »
        RegularisationCalculee? regul = null;
        // Majoration comprise des deux côtés (2026-11-15) : jamais d'écart dû à elle ; la régularisation garde la part Chifa HORS majoration
        // (v_vente_part_organisme y ajoute la majoration)
        if (ecartPart != 0 && comparaison.PartOrganismeChifaHorsMajoration is { } partChifa)
        {
            if (RegularisationPartOrganisme.ChoixExige(ecartPart))
                await ExigerPrixAsync(utilisateurId, "régulariser l’écart de part organisme du volet Chifa", ct);
            regul = RegularisationPartOrganisme.Calculer(ecartPart, partChifa, vente.Total, vente.PartPatient, vente.PatientId is not null,
                vente.DettePatient, regularisation?.Trim());
        }
        var reference = $"{RegularisationPartOrganisme.PrefixeReference} · vente {vente.Numero} · volet {lu.Qr.NumeroFacture}";
        if (!await ventes.RattacherAsync(new RattachementVolet(vente.Id, lu.Qr, utilisateurId, m, resume, ecartPart == 0 ? null : ecartPart,
                regul, vente.PatientId, null, reference), ct))
            throw new RegleMetierException("Cette vente vient de recevoir un volet ou d’être annulée : rescannez le volet.", "vente");
        string? libelleRegul = null;
        if (regul is not null)
        {
            libelleRegul = RegularisationPartOrganisme.Libelle(regul.Sort, regul.MontantPatient, regul.DetteDiminuee, regul.ARendre, regul.MontantPharmacie);
            await journal.EcrireAsync(utilisateurId, ActionRegularisation, "vente", vente.Id,
                new Dictionary<string, object?>
                {
                    ["ticket"] = vente.Numero, ["volet"] = lu.Qr.NumeroFacture, ["organisme"] = vente.Organisme, ["patient"] = vente.Patient,
                    ["part_organisme_vente"] = comparaison.PartOrganismeVente, ["part_organisme_chifa"] = regul.PartOrganismeChifa,
                    ["ecart"] = regul.Ecart, ["sort"] = regul.Sort, ["montant_patient"] = regul.MontantPatient,
                    ["dette_diminuee"] = regul.DetteDiminuee, ["a_rendre"] = regul.ARendre, ["montant_pharmacie"] = regul.MontantPharmacie
                }, RegularisationPartOrganisme.ChoixExige(regul.Ecart) ? Permissions.PrixModifier : Permissions.CaisseVendre, ct);
        }

        await journal.EcrireAsync(utilisateurId, Action, "vente", vente.Id,
            new Dictionary<string, object?>
            {
                ["ticket"] = vente.Numero, ["volet"] = lu.Qr.NumeroFacture, ["date_facture"] = lu.Qr.DateFacture.ToString("yyyy-MM-dd"),
                ["date_ordonnance"] = lu.Qr.DateOrdonnance?.ToString("yyyy-MM-dd"), ["organisme"] = vente.Organisme,
                ["concorde"] = comparaison.Concorde, ["motif"] = m, ["ecarts"] = resume,
                ["lignes"] = comparaison.Lignes.Where(l => l.Ecart != RapprochementVolet.Concorde)
                    .Select(l => new { produit = l.Produit, code = l.Code, ecart = l.Ecart, qte_volet = l.QuantiteVolet, qte_vente = l.QuantiteVente, prix_chifa = l.PrixChifa, notre_prix = l.NotrePrix })
                    .ToList(),
                ["part_organisme_vente"] = comparaison.PartOrganismeVente, ["part_organisme_chifa"] = comparaison.PartOrganismeChifa,
                ["ecart_part_organisme"] = ecartPart, ["majoration"] = comparaison.Majoration
            }, comparaison.Concorde ? Permissions.CaisseVendre : Permissions.PrixModifier, ct);

        // Catalogue d'après le volet (règle existante) : un échec n'annule pas le rattachement
        string? catalogue = null;
        try { catalogue = await CatalogueVoletChifa.AppliquerAsync(volets, journal, lu.Qr, utilisateurId, CancellationToken.None); }
        catch (Exception e)
        {
            trace?.Erreur($"Mise à jour du catalogue d’après le volet Chifa n° {lu.Qr.NumeroFacture} impossible (rattachement)", e);
        }
        return new VoletRattacheResultat(vente.Numero, !comparaison.Concorde, ecartPart, catalogue, libelleRegul,
            regul?.ARendre ?? 0, vente.Id, vente.PatientId, vente.Patient);
    }

    /// <summary>
    /// Droit « modifier les prix » de l'employé, sinon le code comptoir d'un ayant droit accordé pour cette seule action
    /// (<see cref="CodeComptoir.ActionEcartVoletChifa"/>, recette du 2026-11-15, D1) : le poste ouvre « Faire autoriser ».
    /// </summary>
    private async Task ExigerPrixAsync(long utilisateurId, string action, CancellationToken ct)
    {
        if (await droits.APermissionAsync(utilisateurId, Permissions.PrixModifier, ct)) return;
        if (await droits.APermissionAsync(utilisateurId, CodeComptoir.ActionEcartVoletChifa, ct)) return;
        throw new DroitRefuseException(CodeComptoir.ActionEcartVoletChifa, action,
            $"Le droit « Modifier un prix de vente » est nécessaire pour {action} : faites autoriser avec le code d’un collègue qui l’a.");
    }

    private static List<LigneVoletRapprochee> LignesVolet(VoletChifaLu lu) =>
        lu.Lignes.Select(l => new LigneVoletRapprochee(l.Code, l.Quantite, l.PrixUnitaire, l.Produit?.Id, l.Produit?.Nom)).ToList();
}

/// <summary>
/// Volet scanné alors qu'un autre patient est déjà choisi à la caisse (recette du 2026-11-15) : le poste avertit (« Le volet appartient à
/// l'assuré …, pas à … ») et le vendeur choisit ; « garder le patient choisi » est journalisé ici (volet_chifa_patient_garde).
/// </summary>
public sealed class GarderPatientVoletChifa(IPermissionService droits, IJournal journal)
{
    public const string Action = "volet_chifa_patient_garde";

    /// <summary>Faux si l'assuré du volet est bien celui de la fiche (chiffres du n° + suffixe) : rien n'est journalisé.</summary>
    public async Task<bool> ExecuterAsync(string? texteQr, long patientId, string? patientNom, string? patientNumAssure, long utilisateurId,
        CancellationToken ct = default)
    {
        await droits.ExigerAsync(utilisateurId, Permissions.CaisseVendre, "garder le patient choisi pour un volet Chifa", ct);
        var qr = LectureVoletChifa.Lire(texteQr) ?? throw new RegleMetierException("Ce code n’est pas un QR de volet Chifa reconnu.", "volet_chifa");
        if (AssureVolet.Meme(qr, patientNumAssure)) return false;
        await journal.EcrireAsync(utilisateurId, Action, "patient", patientId,
            new Dictionary<string, object?>
            {
                ["volet"] = qr.NumeroFacture, ["assure_volet"] = qr.AssureComplet, ["patient"] = patientNom, ["num_assure_patient"] = patientNumAssure
            }, Permissions.CaisseVendre, ct);
        return true;
    }
}

/// <summary>Assuré du volet (n° + suffixe, chiffres seuls) comparé au n° d'assuré d'une fiche ou d'une vente.</summary>
public static class AssureVolet
{
    public static bool Meme(QrChifa qr, string? numAssure) =>
        PatientScanVolet.Chiffres(qr.NumeroAssure, qr.SuffixeAssure) == (RegleNumAssure.Chiffres(numAssure) ?? "");

    public static string Avertissement(string assureVolet, string patient) => $"Le volet appartient à l’assuré {assureVolet}, pas à {patient}.";
}

/// <summary>Listes du circuit « la caisse d'abord » : ventes Chifa en attente de volet, volets rattachés avec écart (droit de vendre).</summary>
public sealed class ConsulterVoletsChifa(IRapprochementsVolet ventes, IPermissionService droits)
{
    public async Task<(IReadOnlyList<VenteAttenteVolet> Lignes, int Total)> EnAttenteAsync(string? recherche, DemandePage page, long utilisateurId,
        CancellationToken ct = default)
    {
        await droits.ExigerAsync(utilisateurId, Permissions.CaisseVendre, "voir les ventes Chifa en attente de volet", ct);
        return await ventes.EnAttenteAsync(Filtre(recherche), page, ct);
    }

    public async Task<(IReadOnlyList<VoletAvecEcart> Lignes, int Total)> AvecEcartAsync(string? recherche, DemandePage page, long utilisateurId,
        CancellationToken ct = default)
    {
        await droits.ExigerAsync(utilisateurId, Permissions.CaisseVendre, "voir les volets Chifa avec écart", ct);
        return await ventes.AvecEcartAsync(Filtre(recherche), page, ct);
    }

    private static string? Filtre(string? q) => string.IsNullOrWhiteSpace(q) ? null : q.Trim()[..Math.Min(q.Trim().Length, 60)];
}
