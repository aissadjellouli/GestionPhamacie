using Pharma.Application.Commun;
using Pharma.Application.Patients;
using Pharma.Application.Securite;
using Pharma.Domain.Commun;
using Pharma.Domain.Securite;
using Pharma.Domain.Ventes;

namespace Pharma.Application.Caisse;

// ---- Reprise d'une vente Chifa par le QR code du volet (éviter la double saisie) ----
// Format du QR déduit d'un seul volet réel : à confirmer avec l'éditeur de Chifa (docs/chifa.md, docs/a-faire.md).

/// <summary>Produit du catalogue associé à un code Chifa : nom et prix public.</summary>
public sealed record ProduitChifa(long Id, string Nom, decimal PrixPublic);

/// <summary>Ligne du volet ; Produit null : code Chifa pas encore associé à un produit du catalogue.</summary>
public sealed record LigneVoletChifa(string Code, int Quantite, decimal PrixUnitaire, ProduitChifa? Produit);

/// <summary>Volet Chifa lu à la caisse : facture, assuré (et patient retrouvé), lignes rapprochées du catalogue.</summary>
/// <summary>PatientNumAssure : n° d'assuré tel qu'enregistré dans la fiche du patient retrouvé.</summary>
/// <summary>AssurePrincipalPropose* : assuré inconnu, fiche de même n° de base au lien « assure » proposée comme assuré principal (2026-11-15).</summary>
public sealed record VoletChifaLu(QrChifa Qr, IReadOnlyList<LigneVoletChifa> Lignes, long? PatientId, string? Patient, long? OrganismeId,
    string? PatientNumAssure = null, long? AssurePrincipalProposeId = null, string? AssurePrincipalPropose = null);

/// <summary>Fiche dont le n° d'assuré commence par le même n° de base que l'assuré du volet (recherche au scan).</summary>
public sealed record CandidatVolet(long Id, string Nom, string NumAssure, string? Organisme, long? OrganismeId, string? Lien, long? AssurePrincipalId);

/// <summary>
/// Patient d'un volet scanné (règle du titulaire, 2026-11-15) : recherche STRICTE sur le n° ET le suffixe (chiffres seuls). Plus de repli sur
/// le n° seul, qui pouvait rattacher la vente d'un ayant droit à l'assuré. Ayant droit inconnu : assuré principal proposé = fiche de même
/// n° de base dont le lien est « assure » (le vendeur peut le changer) ; rien n'est déduit du suffixe.
/// </summary>
public static class PatientScanVolet
{
    public static string Chiffres(string numeroAssure, string suffixe) => RegleNumAssure.Chiffres(numeroAssure + suffixe) ?? "";

    /// <summary>Préfixe de recherche des candidats : n° de base (12 chiffres), sinon tous les chiffres du n° et du suffixe.</summary>
    public static string Prefixe(string numeroAssure, string suffixe) =>
        FamillePatient.NumeroBase(numeroAssure + suffixe) ?? Chiffres(numeroAssure, suffixe);

    /// <summary>Fiche du n° + suffixe exactement (la plus récente en cas de doublon ancien) ; null sinon, même si l'assuré existe.</summary>
    public static CandidatVolet? Patient(IEnumerable<CandidatVolet> candidats, string numeroAssure, string suffixe)
    {
        var c = Chiffres(numeroAssure, suffixe);
        return c.Length == 0 ? null : candidats.Where(p => RegleNumAssure.Chiffres(p.NumAssure) == c).OrderByDescending(p => p.Id).FirstOrDefault();
    }

    /// <summary>Assuré principal proposé pour un ayant droit inconnu : fiche de même n° de base, lien « assure », sans assuré principal.</summary>
    public static CandidatVolet? AssurePrincipalPropose(IEnumerable<CandidatVolet> candidats, string numeroAssure, string suffixe)
    {
        var c = Chiffres(numeroAssure, suffixe);
        if (FamillePatient.NumeroBase(c) is not { } b) return null;
        return candidats.Where(p => p.Lien == FamillePatient.Assure && p.AssurePrincipalId is null
                                    && FamillePatient.NumeroBase(p.NumAssure) == b && RegleNumAssure.Chiffres(p.NumAssure) != c)
            .OrderBy(p => p.Id).FirstOrDefault();
    }
}

public interface IVoletsChifa
{
    /// <summary>Numéro de la vente (non annulée) qui a déjà repris ce volet, sinon null.</summary>
    Task<string?> VenteDuVoletAsync(string numeroFacture, CancellationToken ct = default);
    Task<IReadOnlyDictionary<string, ProduitChifa>> ProduitsAsync(IEnumerable<string> codes, CancellationToken ct = default);
    /// <summary>
    /// Patient dont le n° d'assuré (chiffres seuls) vaut exactement le n° et son suffixe (<see cref="PatientScanVolet.Patient"/>) ;
    /// Organisme : code de l'organisme de sa fiche.
    /// </summary>
    Task<(long Id, string Nom, string NumAssure, string? Organisme)?> PatientAsync(string numeroAssure, string suffixe, CancellationToken ct = default);
    /// <summary>Ayant droit inconnu : assuré principal proposé (<see cref="PatientScanVolet.AssurePrincipalPropose"/>), null s'il n'y en a pas.</summary>
    Task<(long Id, string Nom)?> AssurePrincipalProposeAsync(string numeroAssure, string suffixe, CancellationToken ct = default) =>
        Task.FromResult<(long Id, string Nom)?>(null);
    Task<long?> OrganismeAsync(string code, CancellationToken ct = default);
    /// <summary>Code de l'organisme d'identifiant donné, null s'il n'existe pas.</summary>
    Task<string?> CodeOrganismeAsync(long id, CancellationToken ct = default);
    /// <summary>Associe (ou réassocie) un code Chifa à un produit actif ; nom du produit et ancien produit associé, ou null si le produit n'existe pas.</summary>
    Task<(string Produit, string? Ancien)?> AssocierAsync(string code, long produitId, long utilisateurId, CancellationToken ct = default);
    /// <summary>
    /// Patient de l'assuré du volet : celui qui existe déjà (même recherche que <see cref="PatientAsync"/>), sinon créé avec ce nom, ce n°
    /// d'assuré et cet organisme. Recherche et création dans une même transaction, sous un verrou propre au n° d'assuré : deux scans
    /// simultanés du même assuré ne créent jamais deux fiches. Cree : faux si la fiche existait déjà. Lien et assuré principal : ceux de
    /// la fiche créée (déjà contrôlés).
    /// </summary>
    Task<(long Id, string Nom, string NumAssure, long? OrganismeId, bool Cree)> PatientOuCreerAsync(string numeroAssure, string suffixe, string nom,
        long organismeId, long creePar, string? lien, long? assurePrincipalId, CancellationToken ct = default);
    /// <summary>
    /// Catalogue d'après le volet (règle du titulaire, 2026-11-15) : chaque produit (id → prix unitaire Chifa) devient remboursable et
    /// prend le prix Chifa pour tarif de référence, en une transaction ; seuls les produits réellement changés sont rendus (valeurs avant).
    /// Le prix public ne change pas.
    /// </summary>
    Task<IReadOnlyList<MajCatalogueChifa>> MettreAJourCatalogueAsync(IReadOnlyDictionary<long, decimal> tarifs, CancellationToken ct = default);
}

/// <summary>Produit du catalogue mis à jour d'après un volet Chifa : remboursable et tarif de référence avant, tarif après.</summary>
public sealed record MajCatalogueChifa(long ProduitId, string Produit, bool RemboursableAvant, decimal? TarifAvant, decimal TarifApres);

/// <summary>
/// Règle du titulaire (2026-11-15) : à l'encaissement d'une vente reprise d'un volet Chifa, chaque produit associé à un code du volet
/// devient remboursable et prend le prix unitaire Chifa pour tarif de référence (source officielle CNAS/CASNOS), quel que soit le
/// vendeur : seule exception au droit prix.modifier. Prix public inchangé. Chaque changement est inscrit au journal
/// (catalogue_maj_volet_chifa) ; rien à changer : ni écriture ni journal.
/// </summary>
public static class CatalogueVoletChifa
{
    public const string Action = "catalogue_maj_volet_chifa";

    /// <summary>Met à jour le catalogue et renvoie le message pour le vendeur, ou null si rien n'a changé.</summary>
    public static async Task<string?> AppliquerAsync(IVoletsChifa volets, IJournal? journal, QrChifa qr, long utilisateurId, CancellationToken ct = default)
    {
        var associes = await volets.ProduitsAsync(qr.Lignes.Select(l => l.Code).Distinct(), ct);
        var tarifs = new Dictionary<long, decimal>();
        foreach (var l in qr.Lignes)
            if (l.PrixUnitaire > 0 && associes.TryGetValue(l.Code, out var p)) tarifs.TryAdd(p.Id, l.PrixUnitaire);
        if (tarifs.Count == 0) return null;
        var majs = await volets.MettreAJourCatalogueAsync(tarifs, ct);
        if (majs.Count == 0) return null;
        if (journal is not null)
            foreach (var m in majs)
                await journal.EcrireAsync(utilisateurId, Action, "produit", m.ProduitId,
                    new Dictionary<string, object?>
                    {
                        ["produit"] = m.Produit, ["volet"] = qr.NumeroFacture,
                        ["remboursable"] = new { avant = m.RemboursableAvant, apres = true },
                        ["tarif_reference"] = new { avant = m.TarifAvant, apres = m.TarifApres }
                    }, Permissions.CaisseVendre, ct);
        return Message(qr.NumeroFacture, majs);
    }

    public static string Message(string numeroFacture, IEnumerable<MajCatalogueChifa> majs)
    {
        var parties = new List<string>();
        foreach (var m in majs)
        {
            if (!m.RemboursableAvant) parties.Add($"{m.Produit} remboursable");
            if (m.TarifAvant != m.TarifApres)
                parties.Add(m.TarifAvant is { } avant ? $"tarif de {m.Produit} {avant:N2} → {m.TarifApres:N2} DA" : $"tarif de {m.Produit} {m.TarifApres:N2} DA");
        }
        return $"Catalogue mis à jour d’après le volet Chifa n° {numeroFacture} : {string.Join(" ; ", parties)}";
    }
}

/// <summary>
/// Volet prérempli (règle du titulaire, 2026-11-15) : assuré inconnu au scan, le vendeur saisit le nom du patient (ordonnance en main) ;
/// sans nom, la fiche est créée avec le nom provisoire « Assuré n°/suffixe », à compléter. La vente continue sans autre ressaisie.
/// </summary>
public static class PatientVoletChifa
{
    public const int NomMax = 120;
    private const string Prefixe = "Assuré ";
    public static string NomProvisoire(string numeroAssure, string suffixe) => $"{Prefixe}{numeroAssure}/{suffixe}";
    /// <summary>Nom provisoire d'un patient créé depuis un volet sans nom saisi : fiche « à compléter ».</summary>
    public static bool EstProvisoire(string? nom) =>
        nom is not null && nom.StartsWith(Prefixe, StringComparison.Ordinal) && nom.Length > Prefixe.Length
        && nom[Prefixe.Length..].All(c => char.IsAsciiDigit(c) || c == '/');
}

/// <summary>Patient du volet : fiche retrouvée ou créée ; ACompleter : nom provisoire (à compléter dans la fiche du patient).</summary>
public sealed record PatientVoletCree(long Id, string Nom, string NumAssure, long? OrganismeId, bool Cree, bool ACompleter);

/// <summary>
/// Création du patient d'un volet Chifa à la caisse (droit de vendre, comme toute création de patient à la caisse). Le n° d'assuré et
/// son suffixe sont relus du QR par le serveur ; organisme CNAS ou CASNOS (CNAS par défaut). Création inscrite au journal.
/// </summary>
public sealed class CreerPatientVoletChifa(IVoletsChifa volets, IPermissionService droits, IJournal journal, Patients.IPatients? patients = null)
{
    public Task<PatientVoletCree> ExecuterAsync(string? texteQr, string? nom, long? organismeId, long utilisateurId, CancellationToken ct = default) =>
        ExecuterAsync(texteQr, nom, organismeId, null, null, utilisateurId, ct);

    /// <summary>Lien (vide proposé) et assuré principal (proposé : fiche de même n° de base au lien « assure ») choisis par le vendeur.</summary>
    public async Task<PatientVoletCree> ExecuterAsync(string? texteQr, string? nom, long? organismeId, string? lien, long? assurePrincipalId,
        long utilisateurId, CancellationToken ct = default)
    {
        await droits.ExigerAsync(utilisateurId, Permissions.CaisseVendre, "créer le patient d’un volet Chifa", ct);
        var qr = LectureVoletChifa.Lire(texteQr)
                 ?? throw new RegleMetierException("Ce code n’est pas un QR de volet Chifa reconnu.", "volet_chifa");
        var saisi = string.IsNullOrWhiteSpace(nom) ? null : string.Join(' ', nom.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (saisi?.Length > PatientVoletChifa.NomMax)
            throw new RegleMetierException($"Nom du patient : {PatientVoletChifa.NomMax} caractères au plus.", "nom_patient");
        var nomFiche = saisi ?? PatientVoletChifa.NomProvisoire(qr.NumeroAssure, qr.SuffixeAssure);
        var code = organismeId is { } o ? await volets.CodeOrganismeAsync(o, ct) : null;
        var org = OrganismeVoletChifa.Accepte(code) ? organismeId!.Value
            : await volets.OrganismeAsync(OrganismeVoletChifa.Cnas, ct)
              ?? throw new RegleMetierException("Organisme CNAS introuvable : vérifiez la liste des organismes.", "organisme");
        var lienFiche = Patients.FamillePatient.Lien(lien);
        var principal = assurePrincipalId is > 0 ? assurePrincipalId : null;
        if (principal is not null || lienFiche is not null)
        {
            if (patients is null) throw new InvalidOperationException("Contrôle du lien impossible : fiches des patients non fournies.");
            await Patients.FamillePatient.VerifierAsync(patients, null, lienFiche, principal, ct);
        }
        var p = await volets.PatientOuCreerAsync(qr.NumeroAssure, qr.SuffixeAssure, nomFiche, org, utilisateurId, lienFiche, principal, ct);
        if (p.Cree)
            await journal.EcrireAsync(utilisateurId, "patient_cree", "patient", p.Id,
                new Dictionary<string, object?>
                {
                    ["patient"] = p.Nom, ["plafond_credit"] = 0m, ["origine"] = "volet_chifa", ["volet"] = qr.NumeroFacture,
                    ["num_assure"] = p.NumAssure, ["a_completer"] = saisi is null, ["lien"] = lienFiche, ["assure_principal_id"] = principal
                }, Permissions.CaisseVendre, ct);
        return new PatientVoletCree(p.Id, p.Nom, p.NumAssure, p.OrganismeId, p.Cree, PatientVoletChifa.EstProvisoire(p.Nom));
    }
}

public static class LectureVoletChifa
{
    /// <summary>Texte scanné relu (douchette réglée en QWERTY sur un poste AZERTY), puis lu comme un QR de volet Chifa.</summary>
    public static QrChifa? Lire(string? texte) =>
        QrChifa.TryParse(Achats.RegleDocumentReception.Normaliser(texte), out var qr) ? qr : null;
}

/// <summary>
/// Organisme d'un volet Chifa (2026-11-15) : CNAS et CASNOS partagent la carte Chifa et le même QR, qui ne contient pas l'organisme.
/// Proposé : celui de la fiche du patient s'il est CNAS ou CASNOS, sinon CNAS ; le vendeur peut le changer ; le serveur n'accepte qu'eux.
/// </summary>
public static class OrganismeVoletChifa
{
    public const string Cnas = "CNAS";
    public const string Casnos = "CASNOS";
    public static bool Accepte(string? code) => code is not null && (code.Equals(Cnas, StringComparison.OrdinalIgnoreCase) || code.Equals(Casnos, StringComparison.OrdinalIgnoreCase));
    public static string Propose(string? organismePatient) => Accepte(organismePatient) ? organismePatient!.Trim().ToUpperInvariant() : Cnas;
}

/// <summary>QR du volet scanné à la caisse : la vente est reprise (assuré, produits, quantités) sans ressaisie, en tiers payant CNAS ou CASNOS.</summary>
public sealed class LireVoletChifa(IVoletsChifa volets, IPermissionService droits)
{
    public async Task<VoletChifaLu> ExecuterAsync(string? texte, long utilisateurId, CancellationToken ct = default)
    {
        await droits.ExigerAsync(utilisateurId, Permissions.CaisseVendre, "reprendre un volet Chifa", ct);
        var qr = LectureVoletChifa.Lire(texte)
                 ?? throw new RegleMetierException("Ce code n’est pas un QR de volet Chifa reconnu.", "volet_chifa");
        if (!FormatFactureChifa.EstValide(qr.NumeroFacture))
            throw new RegleMetierException($"Le n° de facture du volet ({qr.NumeroFacture}) ne compte pas 8 chiffres : volet non repris.", "volet_chifa");
        if (await volets.VenteDuVoletAsync(qr.NumeroFacture, ct) is { } vente)
            throw new RegleMetierException($"Le volet Chifa n° {qr.NumeroFacture} a déjà été repris par la vente {vente}.", "volet_chifa");
        var produits = await volets.ProduitsAsync(qr.Lignes.Select(l => l.Code).Distinct(), ct);
        var patient = await volets.PatientAsync(qr.NumeroAssure, qr.SuffixeAssure, ct);
        var principal = patient is null ? await volets.AssurePrincipalProposeAsync(qr.NumeroAssure, qr.SuffixeAssure, ct) : null;
        return new VoletChifaLu(qr, qr.Lignes.Select(l => new LigneVoletChifa(l.Code, l.Quantite, l.PrixUnitaire, produits.GetValueOrDefault(l.Code))).ToList(),
            patient?.Id, patient?.Nom, await volets.OrganismeAsync(OrganismeVoletChifa.Propose(patient?.Organisme), ct), patient?.NumAssure,
            principal?.Id, principal?.Nom);
    }
}

/// <summary>Code produit Chifa associé à un produit du catalogue (une fois, puis reconnu à chaque volet) ; inscrit au journal.</summary>
public sealed class AssocierCodeChifa(IVoletsChifa volets, IPermissionService droits, IJournal journal)
{
    public async Task<string> ExecuterAsync(string? code, long produitId, long utilisateurId, CancellationToken ct = default)
    {
        await droits.ExigerAsync(utilisateurId, Permissions.CaisseVendre, "associer un code Chifa à un produit", ct);
        var c = code?.Trim() ?? "";
        if (c.Length is 0 or > 20 || !c.All(char.IsAsciiDigit))
            throw new RegleMetierException("Code Chifa invalide : des chiffres seulement (20 au plus).", "code_chifa");
        var (produit, ancien) = await volets.AssocierAsync(c, produitId, utilisateurId, ct)
                                ?? throw new RegleMetierException("Produit introuvable ou inactif.", "code_chifa");
        await journal.EcrireAsync(utilisateurId, "code_chifa_associe", "produit", produitId,
            new Dictionary<string, object?> { ["code"] = c, ["produit"] = ancien is null || ancien == produit ? produit : new { avant = ancien, apres = produit } },
            Permissions.CaisseVendre, ct);
        return produit;
    }
}

/// <summary>
/// Canal de facturation de la part organisme d'une vente (2026-11-15, script 089) : « logiciel » (facture d'organisme de Pharma Officine),
/// « chifa » (facturée par le logiciel Chifa : volet scanné, ou case « facturée par Chifa » avec le n° de facture Chifa) ou « mdn »
/// (organisme militaire, code MDN). Une vente « chifa » ou « mdn » n'est jamais refacturée à l'organisme par le logiciel.
/// </summary>
public static class CanalOrganisme
{
    public const string Logiciel = "logiciel";
    public const string Chifa = "chifa";
    public const string Mdn = "mdn";
    public const string CodeMdn = "MDN";
    public const int NumeroFactureMax = 30;

    public static bool EstMdn(string? codeOrganisme) => codeOrganisme?.Trim().Equals(CodeMdn, StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>Volet scanné ou case « facturée par Chifa » : chifa ; organisme MDN : mdn ; sinon (ou sans tiers payant) : logiciel.</summary>
    public static string Determiner(bool tiersPayant, bool voletScanne, bool factureeParChifa, string? codeOrganisme) =>
        !tiersPayant ? Logiciel
        : voletScanne || factureeParChifa ? Chifa
        : EstMdn(codeOrganisme) ? Mdn
        : Logiciel;

    public static bool Externe(string? canal) => canal is Chifa or Mdn;

    /// <summary>
    /// N° de facture Chifa saisi (vente Chifa sans volet) : 30 caractères au plus, espaces retirés. Depuis le 2026-11-15 (circuit « la caisse
    /// d'abord »), la case cochée sans n° est acceptée par l'encaissement (vente en attente de volet) : cette lecture ne sert qu'à un n° saisi.
    /// </summary>
    public static string NumeroFacture(string? saisie)
    {
        var n = saisie?.Trim() ?? "";
        if (n.Length == 0) throw new RegleMetierException("Vente facturée par Chifa : saisissez le n° de facture Chifa.", "facture_chifa");
        if (n.Length > NumeroFactureMax)
            throw new RegleMetierException($"N° de facture Chifa : {NumeroFactureMax} caractères au plus.", "facture_chifa");
        // Script 101 (2026-11-15) : chifa_facture compte exactement 8 chiffres (contrôle ck_vente_chifa_facture_format)
        if (!FormatFactureChifa.EstValide(n))
            throw new RegleMetierException("N° de facture Chifa : 8 chiffres exactement.", "facture_chifa");
        return n;
    }
}
