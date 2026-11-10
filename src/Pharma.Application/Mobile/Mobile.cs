using Pharma.Application.Achats;
using Pharma.Application.Commun;
using Pharma.Application.Securite;
using Pharma.Domain.Commun;
using Pharma.Domain.Securite;
using Pharma.Domain.Stock;

namespace Pharma.Application.Mobile;

// ---- Modèles -------------------------------------------------------------------

/// <summary>Lot en stock d'un produit. Niveau : perime, rouge, orange, risque_ecoulement ou ok (seuils réglés dans l'Administration).</summary>
public sealed record LotFiche(
    long Id, string NumeroLot, DateOnly DatePeremption, bool DateAVerifier, int Quantite,
    bool Bloque, string? MotifBlocage, string Fournisseur, string Niveau);

public sealed record FicheProduit(
    long ProduitId, string Nom, string? Dci, string? Dosage, string? Forme, decimal PrixPublic, int Stock,
    long? EmplacementId, string? Emplacement, long? ReserveId, string? Reserve, IReadOnlyList<LotFiche> Lots);

/// <summary>Ce que désigne le code scanné. Type : boite, lot ou produit (code-barres du fabricant, sans lot).</summary>
public sealed record FicheCode(string Type, FicheProduit Produit, long? LotId, string? StatutBoite, IReadOnlyList<string> Alertes);

/// <summary>Résolution d'un code : produit, lot éventuel et statut de la boîte quand le code désigne une boîte.</summary>
public sealed record CodeResolu(long ProduitId, long? LotId, string Type, string? StatutBoite);

public sealed record LotAConfirmer(long Id, string Produit, string NumeroLot, bool DateAVerifier);

public sealed record NouveauScanReception(long UtilisateurId, long ProduitId, string CodeBrut, string? Gtin, string? NumeroLot, DateOnly? Peremption, string? NumeroSerie);

public sealed record ScanReception(
    long Id, long ProduitId, string Produit, string? Gtin, string? NumeroLot, DateOnly? Peremption, string? NumeroSerie,
    string ScannePar, DateTime ScanneLe);

/// <summary>Scans regroupés par produit, lot et péremption : une ligne de réception sur le poste.</summary>
/// <summary>ScanIds : scans repris dans la ligne (pour les remettre en attente si la réception est abandonnée).</summary>
public sealed record LigneScannee(long ProduitId, string Produit, string? Gtin, string? NumeroLot, DateOnly? Peremption, int Quantite, IReadOnlyList<string> Series,
    IReadOnlyList<long>? ScanIds = null);

public interface IMobile
{
    /// <summary>Étiquette interne de boîte, code interne de lot ou code-barres du fabricant.</summary>
    Task<CodeResolu?> ResoudreCodeAsync(string code, CancellationToken ct = default);
    /// <summary>Data Matrix : produit par le GTIN, lot par son numéro, boîte par sa série.</summary>
    Task<CodeResolu?> ResoudreDataMatrixAsync(string gtin, string? numeroLot, string? numeroSerie, CancellationToken ct = default);
    Task<FicheProduit?> FicheAsync(long produitId, CancellationToken ct = default);

    Task<LotAConfirmer?> LotAsync(long lotId, CancellationToken ct = default);
    Task ConfirmerPeremptionAsync(long lotId, DateOnly date, CancellationToken ct = default);

    Task<long> AjouterScanAsync(NouveauScanReception scan, CancellationToken ct = default);
    Task<IReadOnlyList<ScanReception>> ScansEnAttenteAsync(CancellationToken ct = default);
    Task<bool> SupprimerScanAsync(long id, CancellationToken ct = default);
    /// <summary>Marque les scans en attente comme importés et les renvoie (une seule transaction).</summary>
    Task<IReadOnlyList<ScanReception>> ImporterScansAsync(CancellationToken ct = default);
    /// <summary>Réception abandonnée sur le poste : les scans importés redeviennent en attente (sauf boîte entrée en stock entre-temps).</summary>
    Task<int> RemettreEnAttenteAsync(IReadOnlyList<long> ids, CancellationToken ct = default);
}

// ---- Cas d'usage -----------------------------------------------------------------

/// <summary>Contrôle en rayon : ce que désigne un code scanné, avec le stock, les emplacements et l'état de chaque lot.</summary>
public sealed class IdentifierCode(IMobile mobile, IPermissionService droits, IHorloge horloge)
{
    public async Task<FicheCode> ExecuterAsync(string? code, long utilisateurId, CancellationToken ct = default)
    {
        await droits.ExigerAsync(utilisateurId, Permissions.StockConsulter, "consulter le stock", ct);
        var brut = code?.Trim() ?? "";
        if (brut.Length == 0) throw new RegleMetierException("Code vide.");

        CodeResolu? resolu;
        var alertes = new List<string>();
        if (Gs1DataMatrix.TryParse(brut, out var dm) && dm is not null)
        {
            resolu = await mobile.ResoudreDataMatrixAsync(dm.Gtin, dm.Lot is null ? null : RegleLot.Normaliser(dm.Lot), dm.NumeroSerie, ct)
                ?? throw new RegleMetierException($"Produit inconnu du catalogue (GTIN {dm.Gtin}) : créez-le sur le poste.");
            if (dm.Lot is not null && resolu.LotId is null)
                alertes.Add($"Lot {RegleLot.Normaliser(dm.Lot)} absent du stock : boîte non réceptionnée ?");
        }
        else
        {
            resolu = await mobile.ResoudreCodeAsync(brut, ct)
                ?? throw new RegleMetierException($"Code {brut} inconnu : ni étiquette interne, ni code-barres du catalogue.");
        }

        var fiche = await mobile.FicheAsync(resolu.ProduitId, ct) ?? throw new RegleMetierException("Produit introuvable.");
        // Le motif d'un blocage est saisi librement et peut citer un patient : il ne sort pas vers le téléphone
        fiche = fiche with { Lots = fiche.Lots.Select(l => l with { MotifBlocage = null }).ToList() };
        if (resolu.Type == "produit") resolu = resolu with { LotId = null };   // le code fabricant ne désigne pas de lot
        if (resolu.StatutBoite is { } s && s != "en_stock")
            alertes.Add($"Cette boîte n’est plus en stock ({LibelleStatutBoite(s)}).");
        if (fiche.Lots.FirstOrDefault(l => l.Id == resolu.LotId) is { } lot)
        {
            if (lot.Bloque) alertes.Add("Lot bloqué (rappel) : retirez la boîte du rayon. Le motif est sur le poste.");
            if (lot.DatePeremption <= horloge.Aujourdhui) alertes.Add("Lot périmé : retirez la boîte du rayon.");
            if (lot.DateAVerifier) alertes.Add("Date de péremption à vérifier : lisez-la sur la boîte et confirmez-la.");
        }
        else if (resolu.LotId is null)
        {
            // Code du fabricant : la boîte peut appartenir à n'importe quel lot, on signale ceux à retirer ou à dater
            foreach (var l in fiche.Lots.Where(l => l.Bloque))
                alertes.Add($"Lot {l.NumeroLot} bloqué (rappel) : lisez le n° de lot sur la boîte et retirez-la si c’est celui-ci.");
            foreach (var l in fiche.Lots.Where(l => !l.Bloque && l.DatePeremption <= horloge.Aujourdhui))
                alertes.Add($"Lot {l.NumeroLot} périmé depuis le {l.DatePeremption:dd/MM/yyyy} : retirez ses boîtes.");
            if (fiche.Lots.Any(l => l.DateAVerifier))
                alertes.Add("Un lot a une date de péremption à vérifier : lisez-la sur la boîte et confirmez-la ci-dessous.");
        }
        return new FicheCode(resolu.Type, fiche, resolu.LotId, resolu.StatutBoite, alertes);
    }

    private static string LibelleStatutBoite(string s) => s switch
    {
        "vendue" => "vendue", "retournee_fournisseur" => "retournée au grossiste", "detruite" => "détruite",
        "retour_client" => "retour client", _ => s
    };
}

/// <summary>Date lue sur la boîte pour un lot repris de l'ancien logiciel (« date à vérifier »).</summary>
public sealed class ConfirmerPeremption(IMobile mobile, IPermissionService droits, IHorloge horloge, IJournal journal)
{
    public async Task ExecuterAsync(long lotId, DateOnly date, long utilisateurId, CancellationToken ct = default)
    {
        await droits.ExigerAsync(utilisateurId, Permissions.StockAjuster, "confirmer une date de péremption", ct);
        var lot = await mobile.LotAsync(lotId, ct) ?? throw new RegleMetierException("Lot introuvable.");
        if (!lot.DateAVerifier)
            throw new RegleMetierException($"Lot {lot.NumeroLot} : la date a déjà été confirmée (réception ou inventaire).");
        RegleInventaire.VerifierDate(date, horloge.Aujourdhui);
        await mobile.ConfirmerPeremptionAsync(lotId, date, ct);
        await journal.EcrireAsync(utilisateurId, "peremption_verifiee", "lot", lotId,
            new Dictionary<string, object?> { ["produit"] = lot.Produit, ["lot"] = lot.NumeroLot, ["date"] = date.ToString("yyyy-MM-dd") },
            Permissions.StockAjuster, ct);
    }
}

/// <summary>Boîte scannée au déballage : mise en attente jusqu'à l'import dans la réception du poste.</summary>
public sealed class ScannerBoiteReception(IMobile mobile, IAchats achats, IPermissionService droits)
{
    public async Task<ScanReception> ExecuterAsync(string? code, long utilisateurId, CancellationToken ct = default)
    {
        await droits.ExigerAsync(utilisateurId, Permissions.AchatsReceptionner, "réceptionner une livraison", ct);
        var brut = code?.Trim() ?? "";
        if (brut.Length == 0) throw new RegleMetierException("Code vide.");

        NouveauScanReception scan;
        if (Gs1DataMatrix.TryParse(brut, out var dm) && dm is not null)
        {
            var produit = await achats.ProduitParGtinAsync(dm.Gtin, ct)
                ?? throw new RegleMetierException($"Produit inconnu du catalogue (GTIN {dm.Gtin}) : créez-le sur le poste.");
            if (dm.NumeroSerie is { } serie &&
                (await mobile.ScansEnAttenteAsync(ct)).Any(s => s.Gtin == dm.Gtin && s.NumeroSerie == serie))
                throw new RegleMetierException($"Boîte {serie} déjà scannée.");
            scan = new(utilisateurId, produit.Id, brut, dm.Gtin, dm.Lot is null ? null : RegleLot.Normaliser(dm.Lot), dm.Peremption, dm.NumeroSerie);
        }
        else
        {
            var resolu = await mobile.ResoudreCodeAsync(brut, ct)
                ?? throw new RegleMetierException($"Code {brut} inconnu du catalogue : créez le produit sur le poste.");
            if (resolu.Type != "produit")
                throw new RegleMetierException("Cette boîte porte déjà une étiquette interne : elle est déjà en stock.");
            scan = new(utilisateurId, resolu.ProduitId, brut, null, null, null, null);
        }

        var id = await mobile.AjouterScanAsync(scan, ct);
        return (await mobile.ScansEnAttenteAsync(ct)).First(s => s.Id == id);
    }
}

public sealed class ListerScansReception(IMobile mobile, IPermissionService droits)
{
    public async Task<IReadOnlyList<ScanReception>> ExecuterAsync(long utilisateurId, CancellationToken ct = default)
    {
        await droits.ExigerAsync(utilisateurId, Permissions.AchatsReceptionner, "réceptionner une livraison", ct);
        return await mobile.ScansEnAttenteAsync(ct);
    }
}

public sealed class SupprimerScanReception(IMobile mobile, IPermissionService droits)
{
    public async Task ExecuterAsync(long id, long utilisateurId, CancellationToken ct = default)
    {
        await droits.ExigerAsync(utilisateurId, Permissions.AchatsReceptionner, "réceptionner une livraison", ct);
        if (!await mobile.SupprimerScanAsync(id, ct))
            throw new RegleMetierException("Scan introuvable ou déjà importé sur le poste.");
    }
}

/// <summary>Le poste reprend les scans en attente : une ligne par produit, lot et péremption, avec les séries lues.</summary>
public sealed class ImporterScansReception(IMobile mobile, IPermissionService droits)
{
    public async Task<IReadOnlyList<LigneScannee>> ExecuterAsync(long utilisateurId, CancellationToken ct = default)
    {
        await droits.ExigerAsync(utilisateurId, Permissions.AchatsReceptionner, "réceptionner une livraison", ct);
        return Regrouper(await mobile.ImporterScansAsync(ct));
    }

    public static IReadOnlyList<LigneScannee> Regrouper(IEnumerable<ScanReception> scans) =>
        scans.GroupBy(s => (s.ProduitId, s.NumeroLot, s.Peremption))
             .Select(g => new LigneScannee(g.Key.ProduitId, g.First().Produit, g.Select(s => s.Gtin).FirstOrDefault(x => x is not null),
                 g.Key.NumeroLot, g.Key.Peremption, g.Count(),
                 g.Select(s => s.NumeroSerie).OfType<string>().Distinct().ToList(), g.Select(s => s.Id).ToList()))
             .OrderBy(l => l.Produit).ThenBy(l => l.NumeroLot)
             .ToList();
}

/// <summary>Le poste abandonne sa réception : les boîtes scannées sur le mobile reviennent dans la liste d'attente.</summary>
public sealed class RemettreScansEnAttente(IMobile mobile, IPermissionService droits)
{
    public async Task<int> ExecuterAsync(IReadOnlyList<long>? ids, long utilisateurId, CancellationToken ct = default)
    {
        await droits.ExigerAsync(utilisateurId, Permissions.AchatsReceptionner, "réceptionner une livraison", ct);
        return ids is { Count: > 0 } ? await mobile.RemettreEnAttenteAsync(ids.Distinct().ToList(), ct) : 0;
    }
}
