namespace Pharma.Domain.Ventes;

/// <summary>Ligne d'une vente déjà encaissée, vue pour le rapprochement d'un volet : produit, boîtes servies, prix unitaire appliqué.</summary>
public sealed record LigneVenteRapprochee(long ProduitId, string Produit, int Quantite, decimal PrixUnitaire);

/// <summary>Ligne du volet Chifa : code, boîtes, prix Chifa ; produit du catalogue associé au code (null : code non associé).</summary>
public sealed record LigneVoletRapprochee(string Code, int Quantite, decimal PrixChifa, long? ProduitId, string? Produit);

/// <summary>
/// Ligne comparée volet / vente. Ecart : <see cref="RapprochementVolet.Concorde"/>, <see cref="RapprochementVolet.PrixSousSeuil"/> (acceptés)
/// ou une différence en rouge (prix au-delà du seuil, quantité, produit absent d'un côté, code Chifa non associé).
/// </summary>
public sealed record LigneComparee(string? Code, long? ProduitId, string Produit, int? QuantiteVolet, int? QuantiteVente, decimal? PrixChifa,
    decimal? NotrePrix, string Ecart)
{
    public bool EnRouge => Ecart is not (RapprochementVolet.Concorde or RapprochementVolet.PrixSousSeuil);
}

/// <summary>
/// Comparaison d'un volet avec une vente : lignes, part de l'organisme enregistrée sur la vente et part recalculée sur le prix Chifa
/// (null : taux de la vente inconnu, reste à charge saisi à la main).
/// </summary>
public sealed record ComparaisonVolet(IReadOnlyList<LigneComparee> Lignes, decimal PartOrganismeVente, decimal? PartOrganismeChifa, decimal Majoration = 0)
{
    /// <summary>Aucune différence en rouge : seuls des écarts de prix sous le seuil, ou rien.</summary>
    public bool Concorde => Lignes.All(l => !l.EnRouge);
    /// <summary>
    /// Part Chifa sans la majoration (2026-11-15) : base de la régularisation (regularisation_part_organisme.part_organisme_chifa), la
    /// créance retenue y ajoutant la majoration (v_vente_part_organisme).
    /// </summary>
    public decimal? PartOrganismeChifaHorsMajoration => PartOrganismeChifa - Majoration;
    /// <summary>Part de l'organisme de la vente moins celle calculée sur le prix Chifa (0 si elle ne se calcule pas).</summary>
    public decimal EcartPartOrganisme => PartOrganismeChifa is { } c ? PartOrganismeVente - c : 0;
    /// <summary>Proximité avec le volet : +2 par produit présent des deux côtés, +1 si même quantité, −1 par produit d'un seul côté.</summary>
    public int Score => Lignes.Where(l => l.Ecart != RapprochementVolet.AssureDifferent).Sum(l => l.QuantiteVolet is not null && l.QuantiteVente is not null ? 2 + (l.QuantiteVolet == l.QuantiteVente ? 1 : 0) : -1);
}

/// <summary>
/// Rapprochement d'un volet Chifa avec une vente déjà encaissée (circuit « la caisse d'abord », règle du titulaire du 2026-11-15) :
/// produits Chifa (codes associés) face aux produits servis, quantités, prix Chifa face au nôtre. Les lignes d'un même produit (plusieurs
/// lots) sont regroupées : quantités additionnées, prix moyen pondéré. Rien n'est modifié sur la vente.
/// </summary>
public static class RapprochementVolet
{
    public const string Concorde = "concorde";
    public const string PrixSousSeuil = "prix_sous_seuil";
    public const string Prix = "prix";
    public const string Quantite = "quantite";
    public const string AbsentVente = "absent_vente";
    public const string AbsentVolet = "absent_volet";
    public const string CodeNonAssocie = "code_non_associe";
    /// <summary>Assuré de la vente (n° + suffixe) différent de celui du volet (recette du 2026-11-15) : différence en rouge.</summary>
    public const string AssureDifferent = "assure_different";

    /// <summary>Ligne « assuré » ajoutée quand la vente n'est pas au nom de l'assuré du volet (chiffres du n° + suffixe comparés).</summary>
    public static ComparaisonVolet AvecAssure(ComparaisonVolet c, string assureVolet, string? assureVente, string? patientVente)
    {
        static string Chiffres(string? s) => new((s ?? "").Where(char.IsAsciiDigit).ToArray());
        if (Chiffres(assureVolet) == Chiffres(assureVente)) return c;
        var ligne = new LigneComparee(null, null, $"Assuré : volet {assureVolet}, vente {(assureVente ?? "sans n°")} ({patientVente ?? "patient inconnu"})",
            null, null, null, null, AssureDifferent);
        return c with { Lignes = [ligne, .. c.Lignes] };
    }

    public static string Libelle(string ecart) => ecart switch
    {
        Concorde => "Concorde",
        PrixSousSeuil => "Écart de prix sous le seuil",
        Prix => "Écart de prix au-delà du seuil",
        Quantite => "Quantité différente",
        AbsentVente => "Sur le volet, pas dans la vente",
        AbsentVolet => "Dans la vente, pas sur le volet",
        CodeNonAssocie => "Code Chifa non associé à un produit",
        AssureDifferent => "Assuré différent de celui du volet",
        _ => ecart
    };

    /// <param name="taux">
    /// Taux de prise en charge de la vente (%) ; null : part Chifa non calculée. Part Chifa : prix Chifa × boîtes servies (au plus celles
    /// du volet), l'écart de quantité restant une différence en rouge.
    /// </param>
    public static ComparaisonVolet Comparer(IEnumerable<LigneVoletRapprochee> volet, IEnumerable<LigneVenteRapprochee> vente, decimal seuil,
        decimal? taux, decimal partOrganismeVente, decimal majoration = 0)
    {
        var servis = vente.GroupBy(l => l.ProduitId)
            .Select(g => (Id: g.Key, Nom: g.First().Produit, Qte: g.Sum(l => l.Quantite),
                Prix: g.Sum(l => l.Quantite) is > 0 and var q ? Math.Round(g.Sum(l => l.PrixUnitaire * l.Quantite) / q, 2, MidpointRounding.AwayFromZero) : 0m))
            .ToDictionary(x => x.Id);
        var lignes = new List<LigneComparee>();
        var vus = new HashSet<long>();
        decimal partChifa = 0;
        foreach (var g in volet.GroupBy(l => l.Code))
        {
            var qteVolet = g.Sum(l => l.Quantite);
            var prixChifa = g.First().PrixChifa;
            var produitId = g.First().ProduitId;
            // Part Chifa sur la quantité SERVIE (décision du titulaire, 2026-11-15) : jamais facturer à l'organisme une boîte non servie ;
            // code non associé : produit inconnu, quantité du volet (rien ne permet de dire qu'il n'a pas été servi)
            void AjouterPart(int quantite)
            {
                if (taux is { } t) partChifa += Math.Round(prixChifa * quantite * t / 100m, 2, MidpointRounding.AwayFromZero);
            }
            if (produitId is not { } id)
            {
                AjouterPart(qteVolet);
                lignes.Add(new LigneComparee(g.Key, null, $"Code Chifa {g.Key}", qteVolet, null, prixChifa, null, CodeNonAssocie));
                continue;
            }
            var nom = g.First().Produit ?? $"Code Chifa {g.Key}";
            if (!servis.TryGetValue(id, out var s))
            {
                // Sur le volet, pas dans la vente : aucune boîte servie, aucune part pour l'organisme
                lignes.Add(new LigneComparee(g.Key, id, nom, qteVolet, null, prixChifa, null, AbsentVente));
                continue;
            }
            AjouterPart(Math.Min(qteVolet, s.Qte));
            vus.Add(id);
            var ecart = s.Qte != qteVolet ? Quantite
                : s.Prix == prixChifa ? Concorde
                : EcartPrixChifa.AChoisir(prixChifa, s.Prix, seuil) ? Prix
                : PrixSousSeuil;
            lignes.Add(new LigneComparee(g.Key, id, s.Nom, qteVolet, s.Qte, prixChifa, s.Prix, ecart));
        }
        foreach (var s in servis.Values.Where(s => !vus.Contains(s.Id)))
            lignes.Add(new LigneComparee(null, s.Id, s.Nom, null, s.Qte, null, s.Prix, AbsentVolet));
        // Majoration Chifa de la vente (2026-11-15) : comprise des DEUX côtés de la part organisme comparée (vente et volet), elle ne crée
        // jamais d'écart à elle seule
        return new ComparaisonVolet(lignes, partOrganismeVente + majoration, taux is null ? null : partChifa + majoration, majoration);
    }

    /// <summary>Résumé des différences en rouge, pour le journal et la liste « Volets avec écart ».</summary>
    public static string Resume(ComparaisonVolet c)
    {
        var parties = c.Lignes.Where(l => l.EnRouge).Select(l => l.Ecart switch
        {
            Quantite => $"{l.Produit} : {l.QuantiteVolet} sur le volet, {l.QuantiteVente} servie(s)",
            AssureDifferent => l.Produit,
            Prix => $"{l.Produit} : prix Chifa {l.PrixChifa:N2} DA, notre prix {l.NotrePrix:N2} DA",
            AbsentVente => $"{l.Produit} : sur le volet ({l.QuantiteVolet}), pas dans la vente",
            AbsentVolet => $"{l.Produit} : servi ({l.QuantiteVente}), pas sur le volet",
            _ => $"{l.Produit} : {Libelle(l.Ecart).ToLowerInvariant()}"
        }).ToList();
        if (c.EcartPartOrganisme != 0)
            parties.Add($"part organisme {c.PartOrganismeVente:N2} DA, sur le prix Chifa {c.PartOrganismeChifa:N2} DA");
        return string.Join(" ; ", parties);
    }

    /// <summary>
    /// Même carte Chifa (décision du titulaire, 2026-11-15) : n° d'assuré de la vente = n° de base du volet suivi d'un autre suffixe
    /// (ayant droit). Chiffres seuls.
    /// </summary>
    public static bool MemeCarte(string numeroBaseVolet, string? numAssureVente)
    {
        static string Chiffres(string? s) => new((s ?? "").Where(char.IsAsciiDigit).ToArray());
        var b = Chiffres(numeroBaseVolet);
        var v = Chiffres(numAssureVente);
        return b.Length > 0 && v.Length > b.Length && v.StartsWith(b, StringComparison.Ordinal);
    }
}

/// <summary>Régularisation calculée : sort, écart (part caisse − part Chifa), part du patient, dette diminuée, à rendre, effet pharmacie (+ gain, − perte).</summary>
public sealed record RegularisationCalculee(string Sort, decimal Ecart, decimal PartOrganismeChifa, decimal MontantPatient, decimal DetteDiminuee,
    decimal ARendre, decimal MontantPharmacie);

/// <summary>
/// Écart de part organisme au rattachement d'un volet Chifa (décision du titulaire, 2026-11-15 ; mêmes règles CNAS et CASNOS). La créance
/// de l'organisme sur la vente devient la part calculée sur le prix Chifa (ce que la caisse paiera) ; la vente n'est jamais modifiée.
/// Écart &gt; 0 (la caisse paiera MOINS) : un ayant droit choisit « à la charge du patient » (ajouté à sa dette, rattaché à la vente)
/// ou « perte pharmacie ». Écart &lt; 0 (la caisse paiera PLUS) : rendu au patient seulement ce qu'il a payé au-delà de sa part Chifa
/// (total − part Chifa, jamais négative), au plus le surplus : sa dette est d'abord diminuée, le reste est « à rendre » ; le surplus non
/// rendu est un gain organisme tracé. Écart nul : aucune régularisation.
/// </summary>
/// <summary>
/// Effet de l'annulation d'une régularisation « à la charge du patient » : DetteAnnulee (part impayée, annulée par écriture inverse),
/// TropPaye (part réellement payée, soumise au choix du patient).
/// </summary>
public sealed record AnnulationRegularisation(decimal DetteAnnulee, decimal TropPaye);

/// <summary>
/// Choix du patient sur le trop-payé : parts imputées sur ses autres ventes à crédit, remboursement par le tiroir, et part laissée en
/// avoir (script 106).
/// </summary>
public sealed record RepartitionTropPaye(IReadOnlyList<(long VenteId, decimal Montant)> Imputations, decimal Rembourse, decimal Avoir = 0)
{
    public decimal Impute => Imputations.Sum(i => i.Montant);
}

public static class RegularisationPartOrganisme
{
    public const string ChargePatient = "charge_patient";
    public const string PertePharmacie = "perte_pharmacie";
    public const string AvoirPatient = "avoir_patient";
    public const string GainOrganisme = "gain_organisme";

    /// <summary>Début de la référence du mouvement de dette écrit par une régularisation (relevé de la dette).</summary>
    public const string PrefixeReference = "Régularisation part organisme Chifa";

    /// <summary>
    /// Libellé du relevé de la dette pour un mouvement issu d'une régularisation Chifa (recette du 2026-11-15, D7) : « Régularisation
    /// Chifa (T-…) » ; null pour un autre mouvement (libellé habituel).
    /// </summary>
    public static string? LibelleReleve(string? reference, string? vente) =>
        reference is not null && reference.StartsWith(PrefixeReference, StringComparison.Ordinal)
            ? vente is null ? "Régularisation Chifa" : $"Régularisation Chifa ({vente})"
            : null;

    /// <summary>Sort en clair (journal, listes).</summary>
    public static string LibelleSort(string sort) => sort switch
    {
        ChargePatient => "à la charge du patient", PertePharmacie => "perte pharmacie", AvoirPatient => "rendu au patient",
        GainOrganisme => "gain organisme", _ => sort
    };

    /// <summary>Choix exigé de l'ayant droit : la caisse paiera moins que la part enregistrée.</summary>
    public static bool ChoixExige(decimal ecart) => ecart > 0;

    public static RegularisationCalculee? Calculer(decimal ecart, decimal partOrganismeChifa, decimal montantTotal, decimal partPatient,
        bool patientConnu, decimal dettePatient, string? choix)
    {
        if (ecart == 0) return null;
        if (ecart > 0)
        {
            return choix switch
            {
                ChargePatient when !patientConnu => throw new Commun.RegleMetierException("Vente sans patient : l’écart ne peut pas être mis à sa charge.", "regularisation"),
                ChargePatient => new RegularisationCalculee(ChargePatient, ecart, partOrganismeChifa, ecart, 0, 0, 0),
                PertePharmacie => new RegularisationCalculee(PertePharmacie, ecart, partOrganismeChifa, 0, 0, 0, -ecart),
                _ => throw new Commun.RegleMetierException(
                    $"La caisse paiera {ecart:N2} DA de moins que la part enregistrée : choisissez « à la charge du patient » ou « perte pharmacie ».", "regularisation")
            };
        }
        var surplus = -ecart;
        var partPatientChifa = Math.Max(0, montantTotal - partOrganismeChifa);
        var trop = Math.Max(0, partPatient - partPatientChifa);
        var rendu = patientConnu ? Math.Min(surplus, trop) : 0;
        if (rendu <= 0) return new RegularisationCalculee(GainOrganisme, ecart, partOrganismeChifa, 0, 0, 0, surplus);
        var dette = Math.Min(rendu, Math.Max(0, dettePatient));
        return new RegularisationCalculee(AvoirPatient, ecart, partOrganismeChifa, rendu, dette, rendu - dette, surplus - rendu);
    }

    /// <summary>
    /// Annulation de la vente régularisée (règles du titulaire, 2026-11-15, script 098). « À la charge du patient » : 1) la part encore due
    /// (elle concerne la vente annulée) est simplement annulée par écriture inverse, jamais remboursée ; 2) seule la part réellement payée
    /// (trop-payé) est soumise au choix du patient (<see cref="Repartir"/>). Autres sorts : neutralisée, sans mouvement d'argent ni de dette.
    /// </summary>
    /// <param name="payeSurRegularisation">Part de la régularisation réellement payée par le patient (bornée à 0 et au montant).</param>
    /// <param name="dettePatient">Dette du patient au moment d'annuler la régularisation (part impayée de la régularisation comprise).</param>
    public static AnnulationRegularisation Annuler(string sort, decimal montantPatient, decimal payeSurRegularisation, decimal dettePatient)
    {
        if (sort != ChargePatient || montantPatient <= 0) return new AnnulationRegularisation(0, 0);
        var paye = Math.Clamp(payeSurRegularisation, 0, montantPatient);
        return new AnnulationRegularisation(Math.Min(montantPatient - paye, Math.Max(0, dettePatient)), paye);
    }

    /// <summary>
    /// Choix obligatoire du vendeur avec le patient sur le trop-payé (2026-11-15, scripts 098 et 106) : solder ses autres dettes (ventes
    /// choisies, chacune au plus son reste), être remboursé par le tiroir et/ou le laisser en avoir ; répartition partielle possible,
    /// somme exactement égale au trop-payé. Sans autre dette : remboursement ou avoir. Vente sans patient : pas d'avoir.
    /// Jamais de remboursement au-delà de ce qui a été encaissé.
    /// </summary>
    /// <param name="autresDettes">Restes des autres ventes à crédit du patient (hors vente annulée).</param>
    public static RepartitionTropPaye Repartir(decimal tropPaye, IReadOnlyList<(long VenteId, decimal Montant)>? imputations, decimal? rembourse,
        IReadOnlyDictionary<long, decimal> autresDettes, long venteAnnulee, decimal? avoir = null, bool patientConnu = true)
    {
        if (tropPaye <= 0) return new RepartitionTropPaye([], 0);
        if (imputations is null && rembourse is null && avoir is null)
            throw new Commun.RegleMetierException(
                $"Ce patient a payé {tropPaye:N2} DA : choisissez avec lui de solder ses autres dettes, de le rembourser ou de laisser un avoir.",
                "trop_paye");
        var parts = (imputations ?? []).Where(p => p.Montant != 0).ToList();
        foreach (var (vente, montant) in parts)
        {
            if (montant < 0) throw new Commun.RegleMetierException("Un montant imputé est négatif.", "trop_paye");
            if (vente == venteAnnulee || !autresDettes.TryGetValue(vente, out var reste))
                throw new Commun.RegleMetierException("Le trop-payé ne solde que d’autres ventes à crédit de ce patient.", "trop_paye");
            if (montant > reste) throw new Commun.RegleMetierException($"Le montant imputé dépasse le reste dû de la vente ({reste:N2} DA).", "trop_paye");
        }
        if (parts.GroupBy(p => p.VenteId).Any(g => g.Count() > 1)) throw new Commun.RegleMetierException("Une vente est choisie deux fois.", "trop_paye");
        var r = rembourse ?? 0;
        if (r < 0) throw new Commun.RegleMetierException("Le remboursement est négatif.", "trop_paye");
        var a = avoir ?? 0;
        if (a < 0) throw new Commun.RegleMetierException("L’avoir est négatif.", "trop_paye");
        if (a > 0 && !patientConnu) throw new Commun.RegleMetierException("Vente sans patient : aucun avoir possible.", "trop_paye");
        if (parts.Sum(p => p.Montant) + r + a != tropPaye)
            throw new Commun.RegleMetierException(
                $"Répartissez exactement {tropPaye:N2} DA entre les autres dettes, le remboursement et l’avoir.", "trop_paye");
        return new RepartitionTropPaye(parts, r, a);
    }

    public static string Libelle(string sort, decimal montantPatient, decimal detteDiminuee, decimal aRendre, decimal montantPharmacie) => sort switch
    {
        ChargePatient => $"À la charge du patient : {montantPatient:N2} DA ajoutés à sa dette",
        PertePharmacie => $"Perte pharmacie : {-montantPharmacie:N2} DA",
        AvoirPatient => $"Rendu au patient : {montantPatient:N2} DA (dette diminuée de {detteDiminuee:N2} DA, à rendre {aRendre:N2} DA)"
                        + (montantPharmacie > 0 ? $", gain organisme {montantPharmacie:N2} DA" : ""),
        GainOrganisme => $"Gain organisme : {montantPharmacie:N2} DA",
        _ => sort
    };
}
