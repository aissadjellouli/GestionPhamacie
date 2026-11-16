using System.Globalization;
using Pharma.Application.Commun;
using Pharma.Application.Financements;
using Pharma.Application.Notifications;
using Pharma.Application.Securite;
using Pharma.Domain.Commun;
using Pharma.Domain.Securite;

namespace Pharma.Application.Achats;

// ---- Multi-grossistes (lot 6, M3a) ------------------------------------------
// Catalogue et disponibilité par grossiste (import d'un fichier dont les colonnes se règlent par grossiste : formats des
// grossistes inconnus, rien n'est supposé), comparaison des grossistes pour la liste à commander, répartition d'une commande
// sur plusieurs grossistes (un bon par grossiste), envoi du bon par le SMTP de la pharmacie. Aucun service étranger.

/// <summary>
/// Correspondance des colonnes du fichier d'un grossiste : nom de l'en-tête (première ligne) de chaque information.
/// Le code produit du grossiste ou le code-barres est obligatoire (rapprochement avec les produits) ; le reste est facultatif.
/// Disponibilité : une quantité (0 = indisponible) ou un texte reconnu dans l'une des deux listes (« ; » entre les valeurs).
/// </summary>
public sealed record ProfilCatalogue(
    long FournisseurId, string? ColonneCode, string? ColonneCodeBarre, string? ColonneDesignation, string? ColonnePrixHt,
    string? ColonneDisponibilite, string? ColonneDelai, string? ValeursIndisponible, string? ValeursDisponible, DateTime? ModifieLe = null)
{
    public bool Utilisable => !string.IsNullOrWhiteSpace(ColonneCode) || !string.IsNullOrWhiteSpace(ColonneCodeBarre);
}

public sealed record FichierGrossiste(string NomFichier, byte[] Contenu);

/// <summary>Ligne lue du fichier d'un grossiste (n° de ligne du fichier, en-tête = 1). Null : information absente ou illisible.</summary>
public sealed record LigneCatalogueLue(
    int NumeroLigne, string? Code, string? CodeBarre, string? Designation, decimal? PrixHt, bool? Disponible, int? QuantiteDisponible, int? DelaiJours);

public sealed record AnomalieCatalogue(int NumeroLigne, string Message);

public sealed record CatalogueLu(IReadOnlyList<LigneCatalogueLue> Lignes, IReadOnlyList<AnomalieCatalogue> Anomalies);

/// <summary>Ligne lue et rapprochée d'un produit de l'officine.</summary>
public sealed record LigneCatalogueRapprochee(long ProduitId, LigneCatalogueLue Ligne);

/// <summary>Bon de commande à transmettre au grossiste.</summary>
public sealed record EnvoiBonCommande(Fournisseur Grossiste, string Numero, DocumentGenere Bon, string? Pharmacie);

/// <summary>
/// Port vers un grossiste. Adaptateurs de cette version : « fichier » (catalogue et disponibilité importés d'un fichier Excel ou
/// CSV) et « email » (bon de commande PDF envoyé par le serveur SMTP de la pharmacie). Une API de grossiste, si elle existe un jour,
/// sera un autre adaptateur.
/// </summary>
public interface IGrossisteConnecteur
{
    string Mode { get; }
    bool LitCatalogue { get; }
    bool EnvoieCommande { get; }
    /// <summary>Null si le connecteur est prêt, sinon la raison (ex. SMTP non configuré).</summary>
    Task<string?> IndisponibleAsync(CancellationToken ct = default);
    Task<CatalogueLu> LireCatalogueAsync(FichierGrossiste fichier, ProfilCatalogue profil, CancellationToken ct = default);
    /// <summary>Renvoie l'adresse à laquelle le bon est parti.</summary>
    Task<string> EnvoyerCommandeAsync(EnvoiBonCommande envoi, CancellationToken ct = default);
}

/// <summary>Envoi d'un e-mail avec pièce jointe par le serveur SMTP de la pharmacie.</summary>
public interface IEnvoiEmailPieceJointe
{
    Task EnvoyerAsync(ConnexionSmtp connexion, string adresse, string objet, string texte, string nomPieceJointe, byte[] pieceJointe,
        string typeMime, CancellationToken ct = default);
}

/// <summary>Ce que l'officine sait d'un grossiste pour un produit : dernier catalogue importé et dernier achat.</summary>
public sealed record OffreGrossiste(
    long ProduitId, long FournisseurId, string Grossiste, decimal? PrixHt, bool? Disponible, int? QuantiteDisponible, int? DelaiJours,
    DateTime? CatalogueLe, decimal? DernierPrixAchatHt, DateTime? DernierAchatLe)
{
    /// <summary>Prix retenu pour comparer : celui du catalogue, sinon celui du dernier achat.</summary>
    public decimal? PrixCompare => PrixHt ?? DernierPrixAchatHt;
}

public sealed record ImportCatalogue(long Id, long FournisseurId, string Grossiste, string NomFichier, DateTime ImporteLe, string? Utilisateur,
    int LignesLues, int LignesRapprochees, int LignesIgnorees);

public sealed record BilanImportCatalogue(long ImportId, int LignesLues, int LignesRapprochees, int LignesIgnorees,
    IReadOnlyList<string> NonRapprochees, IReadOnlyList<AnomalieCatalogue> Anomalies);

public interface ICatalogueGrossistes
{
    Task<ProfilCatalogue?> ProfilAsync(long fournisseurId, CancellationToken ct = default);
    Task EnregistrerProfilAsync(ProfilCatalogue profil, long utilisateurId, CancellationToken ct = default);
    /// <summary>Produits par code-barres (EAN-13, ou GTIN-14 commençant par 0).</summary>
    Task<IReadOnlyDictionary<string, long>> ProduitsParCodeBarreAsync(IReadOnlyCollection<string> codes, CancellationToken ct = default);
    /// <summary>Produits déjà rapprochés d'un code du grossiste lors d'un import précédent.</summary>
    Task<IReadOnlyDictionary<string, long>> ProduitsParCodeGrossisteAsync(long fournisseurId, IReadOnlyCollection<string> codes, CancellationToken ct = default);
    /// <summary>Import en une transaction : trace de l'import, puis ligne du catalogue remplacée pour chaque produit rapproché.</summary>
    Task<long> EnregistrerImportAsync(long fournisseurId, string nomFichier, long utilisateurId, int lignesLues,
        IReadOnlyList<LigneCatalogueRapprochee> lignes, int lignesIgnorees, CancellationToken ct = default);
    Task<Page<ImportCatalogue>> ImportsAsync(long? fournisseurId, DemandePage page, CancellationToken ct = default);
    /// <summary>Offres des grossistes actifs pour ces produits : ceux qui ont un catalogue ou un achat pour le produit.</summary>
    Task<IReadOnlyList<OffreGrossiste>> OffresAsync(IReadOnlyCollection<long> produitIds, CancellationToken ct = default);
    Task MarquerEnvoiEmailAsync(long commandeId, string adresse, DateTime quandUtc, CancellationToken ct = default);
}

// ---- Lecture du fichier d'un grossiste ---------------------------------------

/// <summary>Lecture des lignes d'un tableau selon le profil du grossiste (règle pure, sans base).</summary>
public static class LectureCatalogueGrossiste
{
    public const int AnomaliesMax = 50;

    public static CatalogueLu Lire(IReadOnlyList<IReadOnlyList<string?>> tableau, ProfilCatalogue profil)
    {
        if (!profil.Utilisable)
            throw new RegleMetierException("Réglez d’abord les colonnes du fichier de ce grossiste : code produit ou code-barres au moins.");
        if (tableau.Count == 0) throw new RegleMetierException("Le fichier est vide.");

        var entetes = tableau[0].Select(e => Cle(e)).ToList();
        int? Colonne(string? nom, string libelle)
        {
            if (string.IsNullOrWhiteSpace(nom)) return null;
            var i = entetes.IndexOf(Cle(nom));
            if (i < 0)
                throw new RegleMetierException($"Colonne « {nom.Trim()} » ({libelle}) absente de la première ligne du fichier. Colonnes lues : "
                    + string.Join(", ", tableau[0].Where(e => !string.IsNullOrWhiteSpace(e)).Select(e => $"« {e!.Trim()} »")) + ".");
            return i;
        }
        var cCode = Colonne(profil.ColonneCode, "code produit");
        var cBarre = Colonne(profil.ColonneCodeBarre, "code-barres");
        var cDesig = Colonne(profil.ColonneDesignation, "désignation");
        var cPrix = Colonne(profil.ColonnePrixHt, "prix HT");
        var cDispo = Colonne(profil.ColonneDisponibilite, "disponibilité");
        var cDelai = Colonne(profil.ColonneDelai, "délai");
        var indispo = Valeurs(profil.ValeursIndisponible);
        var dispo = Valeurs(profil.ValeursDisponible);

        var lignes = new List<LigneCatalogueLue>();
        var anomalies = new List<AnomalieCatalogue>();
        void Signaler(int n, string m) { if (anomalies.Count < AnomaliesMax) anomalies.Add(new AnomalieCatalogue(n, m)); }

        for (var i = 1; i < tableau.Count; i++)
        {
            var ligne = tableau[i];
            var n = i + 1;
            if (ligne.All(string.IsNullOrWhiteSpace)) continue;
            string? Cellule(int? c) => c is { } x && x < ligne.Count && !string.IsNullOrWhiteSpace(ligne[x]) ? ligne[x]!.Trim() : null;

            var code = Cellule(cCode);
            var barre = Cellule(cBarre);
            if (barre is not null && !barre.All(char.IsAsciiDigit))
            {
                Signaler(n, $"code-barres « {barre} » illisible (chiffres seulement ; une cellule Excel au format nombre peut l’avoir abîmé)");
                barre = null;
            }
            if (code is null && barre is null) { Signaler(n, "ni code produit ni code-barres : ligne ignorée"); continue; }

            decimal? prix = null;
            if (Cellule(cPrix) is { } p)
            {
                prix = Nombre(p);
                if (prix is null or < 0) { Signaler(n, $"prix « {p} » illisible"); prix = null; }
            }

            bool? disponible = null;
            int? quantite = null;
            if (Cellule(cDispo) is { } d)
            {
                if (int.TryParse(d.Replace(" ", "").Replace(" ", ""), NumberStyles.Integer, CultureInfo.InvariantCulture, out var q) && q >= 0)
                    (quantite, disponible) = (q, q > 0);
                else if (indispo.Contains(Cle(d))) disponible = false;
                else if (dispo.Contains(Cle(d))) disponible = true;
                else Signaler(n, $"disponibilité « {d} » non reconnue : ajoutez-la à la liste « disponible » ou « indisponible » du profil");
            }

            int? delai = null;
            if (Cellule(cDelai) is { } t)
            {
                if (int.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out var j) && j >= 0) delai = j;
                else Signaler(n, $"délai « {t} » illisible (nombre de jours attendu)");
            }

            lignes.Add(new LigneCatalogueLue(n, code, barre, Cellule(cDesig), prix, disponible, quantite, delai));
        }
        return new CatalogueLu(lignes, anomalies);
    }

    /// <summary>Nombre écrit à la française ou à l'anglaise (« 1 234,50 », « 1234.50 », « 1,234.50 ») ; null si illisible.</summary>
    public static decimal? Nombre(string texte)
    {
        var t = new string(texte.Where(c => !char.IsWhiteSpace(c) && c != ' ' && c != ' ').ToArray());
        if (t.EndsWith("DA", StringComparison.OrdinalIgnoreCase)) t = t[..^2];
        var virgule = t.LastIndexOf(',');
        var point = t.LastIndexOf('.');
        if (virgule >= 0 && point >= 0)
            t = virgule > point ? t.Replace(".", "").Replace(',', '.') : t.Replace(",", "");
        else if (virgule >= 0)
            t = t.Replace(',', '.');
        return decimal.TryParse(t, NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    private static string Cle(string? texte) => TexteRecherche.Normaliser((texte ?? "").Trim());

    private static HashSet<string> Valeurs(string? liste) =>
        (liste ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(Cle).ToHashSet();
}

// ---- Choix du grossiste --------------------------------------------------------

public sealed record PropositionGrossiste(long? FournisseurId, string Motif);

/// <summary>
/// Grossiste proposé pour un produit (le choix reste à l'utilisateur) : parmi les grossistes non signalés indisponibles,
/// le meilleur prix (catalogue, sinon dernier achat) ; à prix égal, le plus court délai, puis celui qui se dit disponible.
/// Sans aucun prix connu : un grossiste disponible au plus court délai. Indisponible partout : aucune proposition.
/// </summary>
public static class RegleChoixGrossiste
{
    public const string MeilleurPrix = "meilleur prix";
    public const string Disponibilite = "disponibilité";
    public const string IndisponiblePartout = "indisponible chez tous les grossistes connus";
    public const string AucuneInformation = "aucun catalogue ni achat connu";

    public static PropositionGrossiste Proposer(IReadOnlyList<OffreGrossiste> offres)
    {
        if (offres.Count == 0) return new(null, AucuneInformation);
        var candidats = offres.Where(o => o.Disponible != false).ToList();
        if (candidats.Count == 0) return new(null, IndisponiblePartout);

        var avecPrix = candidats.Where(o => o.PrixCompare is not null).ToList();
        if (avecPrix.Count > 0)
        {
            var meilleur = avecPrix.OrderBy(o => o.PrixCompare).ThenBy(o => o.DelaiJours ?? int.MaxValue)
                .ThenBy(o => o.Disponible == true ? 0 : 1).ThenBy(o => o.Grossiste, StringComparer.CurrentCulture).First();
            return new(meilleur.FournisseurId, MeilleurPrix);
        }
        var dispo = candidats.OrderBy(o => o.Disponible == true ? 0 : 1).ThenBy(o => o.DelaiJours ?? int.MaxValue)
            .ThenBy(o => o.Grossiste, StringComparer.CurrentCulture).First();
        return new(dispo.FournisseurId, Disponibilite);
    }
}

// ---- Cas d'usage ---------------------------------------------------------------

public sealed class ConsulterProfilCatalogue(ICatalogueGrossistes catalogue)
{
    public async Task<ProfilCatalogue> ExecuterAsync(long fournisseurId, CancellationToken ct = default) =>
        await catalogue.ProfilAsync(fournisseurId, ct) ?? new ProfilCatalogue(fournisseurId, null, null, null, null, null, null, null, null);
}

public sealed class EnregistrerProfilCatalogue(ICatalogueGrossistes catalogue, IAchats achats, IPermissionService droits, IJournal journal)
{
    public async Task ExecuterAsync(ProfilCatalogue p, long utilisateurId, CancellationToken ct = default)
    {
        await droits.ExigerAsync(utilisateurId, Permissions.AchatsCommander, "régler le fichier d’un grossiste", ct);
        if (await achats.FournisseurAsync(p.FournisseurId, ct) is null) throw new RegleMetierException("Grossiste introuvable.", "grossiste");
        static string? N(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
        var propre = new ProfilCatalogue(p.FournisseurId, N(p.ColonneCode), N(p.ColonneCodeBarre), N(p.ColonneDesignation), N(p.ColonnePrixHt),
            N(p.ColonneDisponibilite), N(p.ColonneDelai), N(p.ValeursIndisponible), N(p.ValeursDisponible));
        if (!propre.Utilisable)
            throw new RegleMetierException("Indiquez la colonne du code produit du grossiste ou celle du code-barres.");
        var colonnes = new[] { propre.ColonneCode, propre.ColonneCodeBarre, propre.ColonneDesignation, propre.ColonnePrixHt, propre.ColonneDisponibilite, propre.ColonneDelai }
            .Where(c => c is not null).Select(c => TexteRecherche.Normaliser(c!)).ToList();
        if (colonnes.Count != colonnes.Distinct().Count())
            throw new RegleMetierException("Une même colonne du fichier est choisie pour deux informations.");
        await catalogue.EnregistrerProfilAsync(propre, utilisateurId, ct);
        await journal.EcrireAsync(utilisateurId, "profil_catalogue_grossiste", "profil_catalogue_grossiste", p.FournisseurId,
            new Dictionary<string, object?> { ["code"] = propre.ColonneCode, ["code_barre"] = propre.ColonneCodeBarre, ["prix"] = propre.ColonnePrixHt,
                ["disponibilite"] = propre.ColonneDisponibilite, ["delai"] = propre.ColonneDelai }, Permissions.AchatsCommander, ct);
    }
}

/// <summary>En-têtes (première ligne) d'un fichier de grossiste : aide au réglage du profil.</summary>
public sealed class LireColonnesCatalogue(ILecteurTableau lecteur)
{
    public IReadOnlyList<string> Executer(FichierGrossiste f)
    {
        ImporterCatalogueGrossiste.VerifierTaille(f);
        var t = lecteur.Lire(f.NomFichier, f.Contenu);
        return t.Count == 0 ? [] : t[0].Where(c => !string.IsNullOrWhiteSpace(c)).Select(c => c!.Trim()).ToList();
    }
}

/// <summary>
/// Import du catalogue / de la disponibilité d'un grossiste : lecture par le connecteur « fichier » selon le profil du grossiste,
/// rapprochement par code-barres, sinon par le code du grossiste déjà rapproché lors d'un import précédent. Les lignes non
/// rapprochées sont comptées et citées (rien n'est créé) ; le dernier état connu de chaque produit rapproché est remplacé.
/// </summary>
public sealed class ImporterCatalogueGrossiste(ICatalogueGrossistes catalogue, IAchats achats, IEnumerable<IGrossisteConnecteur> connecteurs,
    IPermissionService droits, IJournal journal)
{
    public const int TailleMax = 10 * 1024 * 1024;
    public const int CitesMax = 20;

    public static void VerifierTaille(FichierGrossiste f)
    {
        if (f.Contenu.Length == 0) throw new RegleMetierException("Le fichier est vide.");
        if (f.Contenu.Length > TailleMax) throw new RegleMetierException("Fichier trop gros (10 Mo au plus).");
    }

    public async Task<BilanImportCatalogue> ExecuterAsync(long fournisseurId, FichierGrossiste fichier, long utilisateurId, CancellationToken ct = default)
    {
        await droits.ExigerAsync(utilisateurId, Permissions.AchatsCommander, "importer le catalogue d’un grossiste", ct);
        VerifierTaille(fichier);
        var grossiste = await achats.FournisseurAsync(fournisseurId, ct) ?? throw new RegleMetierException("Grossiste introuvable.", "grossiste");
        var profil = await catalogue.ProfilAsync(fournisseurId, ct);
        if (profil is not { Utilisable: true })
            throw new RegleMetierException($"Réglez d’abord les colonnes du fichier de {grossiste.RaisonSociale}.");
        var connecteur = connecteurs.FirstOrDefault(c => c.LitCatalogue) ?? throw new InvalidOperationException("Aucun connecteur de catalogue.");
        if (await connecteur.IndisponibleAsync(ct) is { } raison) throw new RegleMetierException(raison);

        var lu = await connecteur.LireCatalogueAsync(fichier, profil, ct);
        var parBarre = await catalogue.ProduitsParCodeBarreAsync(lu.Lignes.Where(l => l.CodeBarre is not null).Select(l => l.CodeBarre!).Distinct().ToList(), ct);
        var parCode = await catalogue.ProduitsParCodeGrossisteAsync(fournisseurId, lu.Lignes.Where(l => l.Code is not null).Select(l => l.Code!).Distinct().ToList(), ct);

        var anomalies = lu.Anomalies.ToList();
        var retenues = new Dictionary<long, LigneCatalogueRapprochee>();
        var nonRapprochees = new List<string>();
        var ignorees = 0;
        foreach (var l in lu.Lignes)
        {
            long? produit = l.CodeBarre is { } b && parBarre.TryGetValue(b, out var p1) ? p1
                : l.Code is { } c && parCode.TryGetValue(c, out var p2) ? p2 : null;
            if (produit is null)
            {
                ignorees++;
                if (nonRapprochees.Count < CitesMax)
                    nonRapprochees.Add($"ligne {l.NumeroLigne} : {l.Designation ?? l.CodeBarre ?? l.Code}");
                continue;
            }
            // Même produit deux fois dans le fichier : la dernière ligne l'emporte, signalée
            if (retenues.ContainsKey(produit.Value) && anomalies.Count < LectureCatalogueGrossiste.AnomaliesMax)
                anomalies.Add(new AnomalieCatalogue(l.NumeroLigne, "produit déjà présent plus haut dans le fichier : cette ligne l’emporte"));
            retenues[produit.Value] = new LigneCatalogueRapprochee(produit.Value, l);
        }

        var id = await catalogue.EnregistrerImportAsync(fournisseurId, fichier.NomFichier, utilisateurId, lu.Lignes.Count, retenues.Values.ToList(), ignorees, ct);
        await journal.EcrireAsync(utilisateurId, "import_catalogue_grossiste", "import_catalogue_grossiste", id,
            new Dictionary<string, object?> { ["grossiste"] = grossiste.RaisonSociale, ["fichier"] = fichier.NomFichier, ["lues"] = lu.Lignes.Count,
                ["rapprochees"] = retenues.Count, ["ignorees"] = ignorees }, Permissions.AchatsCommander, ct);
        return new BilanImportCatalogue(id, lu.Lignes.Count, retenues.Count, ignorees, nonRapprochees, anomalies);
    }
}

public sealed class ListerImportsCatalogue(ICatalogueGrossistes catalogue)
{
    public Task<Page<ImportCatalogue>> ExecuterAsync(long? fournisseurId, DemandePage page, CancellationToken ct = default) =>
        catalogue.ImportsAsync(fournisseurId, page, ct);
}

/// <summary>Produit à commander avec les offres des grossistes et le grossiste proposé.</summary>
public sealed record ComparaisonProduit(SuggestionCommande Produit, IReadOnlyList<OffreGrossiste> Offres, PropositionGrossiste Proposition);

/// <summary>
/// Écran « Commander » : liste à commander (règle des listes : paginée et filtrée par produit et état), comparaison par produit des
/// grossistes (prix, disponibilité, délai, dernier achat) et grossiste proposé.
/// </summary>
public sealed class ComparerGrossistes(IAchats achats, ICatalogueGrossistes catalogue)
{
    public async Task<Page<ComparaisonProduit>> PageAsync(string? recherche, string? statut, DemandePage page, CancellationToken ct = default)
    {
        var lignes = (await achats.SuggestionsAsync(ct))
            .Where(x => TexteRecherche.Contient(recherche, x.Produit) && (string.IsNullOrEmpty(statut) || x.Statut == statut)).ToList();
        var pageLignes = lignes.Skip(page.Decalage).Take(page.Taille).ToList();
        var offres = (await catalogue.OffresAsync(pageLignes.Select(s => s.ProduitId).ToList(), ct)).ToLookup(o => o.ProduitId);
        var r = pageLignes.Select(s =>
        {
            var o = offres[s.ProduitId].OrderBy(x => x.Grossiste, StringComparer.CurrentCulture).ToList();
            return new ComparaisonProduit(s, o, RegleChoixGrossiste.Proposer(o));
        }).ToList();
        return new Page<ComparaisonProduit>(r, lignes.Count, page.Page, page.Taille);
    }
}

public sealed record LigneRepartition(long ProduitId, long FournisseurId, int Quantite);
public sealed record BonReparti(long CommandeId, long FournisseurId, string Grossiste, int Lignes);

/// <summary>
/// Répartition d'une commande sur plusieurs grossistes : chaque ligne va au bon en brouillon de son grossiste (un bon par grossiste,
/// créé au besoin). Un même produit peut être partagé entre grossistes. Prix : celui du dernier catalogue du grossiste, sinon le
/// dernier prix d'achat (règle de l'ajout). Tout est contrôlé avant la première écriture.
/// </summary>
public sealed class RepartirCommande(IAchats achats, ICatalogueGrossistes catalogue, AjouterACommande ajouter, IPermissionService droits, IJournal journal)
{
    public const int LignesMax = 500;

    public async Task<IReadOnlyList<BonReparti>> ExecuterAsync(IReadOnlyList<LigneRepartition> lignes, long utilisateurId, CancellationToken ct = default)
    {
        await droits.ExigerAsync(utilisateurId, Permissions.AchatsCommander, "passer une commande", ct);
        if (lignes.Count == 0) throw new RegleMetierException("Choisissez au moins un produit à commander.");
        if (lignes.Count > LignesMax) throw new RegleMetierException($"{LignesMax} lignes au plus par répartition.");
        if (lignes.Any(l => l.Quantite is <= 0 or > 10_000)) throw new RegleMetierException("Quantité à commander entre 1 et 10 000.");

        // Même produit chez le même grossiste deux fois : quantités additionnées
        var groupees = lignes.GroupBy(l => (l.ProduitId, l.FournisseurId))
            .Select(g => new LigneRepartition(g.Key.ProduitId, g.Key.FournisseurId, g.Sum(x => x.Quantite))).ToList();

        var grossistes = new Dictionary<long, Fournisseur>();
        foreach (var fid in groupees.Select(l => l.FournisseurId).Distinct())
        {
            var f = await achats.FournisseurAsync(fid, ct) ?? throw new RegleMetierException("Grossiste introuvable.", "grossiste");
            if (!f.Actif) throw new RegleMetierException($"{f.RaisonSociale} est désactivé : réactivez-le dans l’onglet Grossistes.", "grossiste");
            grossistes[fid] = f;
        }
        var offres = (await catalogue.OffresAsync(groupees.Select(l => l.ProduitId).Distinct().ToList(), ct))
            .ToDictionary(o => (o.ProduitId, o.FournisseurId));
        var sansPrix = new List<string>();
        var prix = new Dictionary<(long, long), decimal?>();
        foreach (var l in groupees)
        {
            var p = offres.TryGetValue((l.ProduitId, l.FournisseurId), out var o) ? o.PrixHt : null;
            if (p is null && await achats.DernierAchatAsync(l.ProduitId, l.FournisseurId, ct) is null)
                sansPrix.Add(await achats.NomProduitAsync(l.ProduitId, ct) ?? $"produit {l.ProduitId}");
            prix[(l.ProduitId, l.FournisseurId)] = p;
        }
        if (sansPrix.Count > 0)
            throw new RegleMetierException($"Aucun prix connu (ni catalogue ni achat) pour : {string.Join(", ", sansPrix)}. "
                + "Ajoutez-les depuis « À commander » en saisissant le prix, ou importez le catalogue du grossiste.");

        var bons = new Dictionary<long, (long CommandeId, int Lignes)>();
        foreach (var l in groupees.OrderBy(l => grossistes[l.FournisseurId].RaisonSociale, StringComparer.CurrentCulture))
        {
            var id = await ajouter.ExecuterAsync(l.FournisseurId, l.ProduitId, l.Quantite, prix[(l.ProduitId, l.FournisseurId)], utilisateurId, ct);
            bons[l.FournisseurId] = (id, bons.TryGetValue(l.FournisseurId, out var b) ? b.Lignes + 1 : 1);
        }
        var resultat = bons.Select(b => new BonReparti(b.Value.CommandeId, b.Key, grossistes[b.Key].RaisonSociale, b.Value.Lignes))
            .OrderBy(b => b.Grossiste, StringComparer.CurrentCulture).ToList();
        await journal.EcrireAsync(utilisateurId, "commande_repartie", "commande_fournisseur", resultat[0].CommandeId,
            new Dictionary<string, object?> { ["bons"] = resultat.Select(b => b.CommandeId).ToList(), ["lignes"] = groupees.Count },
            Permissions.AchatsCommander, ct);
        return resultat;
    }
}

public sealed record BonEnvoye(DocumentGenere Bon, string Numero, string Adresse);

/// <summary>
/// Envoi du bon de commande au grossiste par e-mail (connecteur « email », SMTP de la pharmacie). Un brouillon est d'abord émis
/// (mêmes contrôles que « Émettre le bon ») ; un bon déjà émis est renvoyé tel quel. SMTP non configuré ou grossiste sans adresse :
/// refus avant toute émission. E-mail en échec après l'émission : le bon reste émis, le message le dit.
/// </summary>
public sealed class EnvoyerBonParEmail(IAchats achats, ICatalogueGrossistes catalogue, EmettreCommande emettre, ImprimerCommande imprimer,
    IEnumerable<IGrossisteConnecteur> connecteurs, Administration.IPharmacie pharmacie, IPermissionService droits, IJournal journal, IHorloge horloge)
{
    public async Task<BonEnvoye> ExecuterAsync(long commandeId, long utilisateurId, CancellationToken ct = default)
    {
        await droits.ExigerAsync(utilisateurId, Permissions.AchatsCommander, "envoyer un bon de commande", ct);
        var c = await achats.CommandeAsync(commandeId, ct) ?? throw new RegleMetierException("Commande introuvable.");
        if (c.Entete.Statut == "annulee") throw new RegleMetierException($"La commande {c.Entete.Numero} est annulée.");
        var f = await achats.FournisseurAsync(c.Entete.FournisseurId, ct) ?? throw new RegleMetierException("Grossiste introuvable.");
        if (string.IsNullOrWhiteSpace(f.Email))
            throw new RegleMetierException($"{f.RaisonSociale} n’a pas d’adresse e-mail : complétez sa fiche (onglet Grossistes) ou imprimez le bon.");
        var connecteur = connecteurs.FirstOrDefault(x => x.EnvoieCommande) ?? throw new InvalidOperationException("Aucun connecteur d’envoi.");
        if (await connecteur.IndisponibleAsync(ct) is { } raison) throw new RegleMetierException(raison);

        var bon = c.Entete.Statut == "brouillon"
            ? await emettre.ExecuterAsync(commandeId, utilisateurId, ct)
            : await imprimer.ExecuterAsync(commandeId, utilisateurId, ct) ?? throw new RegleMetierException("Commande introuvable.");
        string adresse;
        try
        {
            adresse = await connecteur.EnvoyerCommandeAsync(new EnvoiBonCommande(f, c.Entete.Numero, bon, (await pharmacie.LireAsync(ct)).Nom), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not RegleMetierException)
        {
            throw new RegleMetierException($"Bon {c.Entete.Numero} émis, mais l’e-mail n’est pas parti ({ex.Message}). Réessayez ou imprimez le bon.");
        }
        await catalogue.MarquerEnvoiEmailAsync(commandeId, adresse, horloge.MaintenantUtc, ct);
        await journal.EcrireAsync(utilisateurId, "commande_envoyee_email", "commande_fournisseur", commandeId,
            new Dictionary<string, object?> { ["numero"] = c.Entete.Numero, ["grossiste"] = f.RaisonSociale }, Permissions.AchatsCommander, ct);
        return new BonEnvoye(bon, c.Entete.Numero, adresse);
    }
}

// ---- Adaptateurs ---------------------------------------------------------------

/// <summary>Connecteur « fichier » : catalogue et disponibilité lus d'un classeur Excel (.xlsx) ou d'un CSV.</summary>
public sealed class GrossisteConnecteurFichier(ILecteurTableau lecteur) : IGrossisteConnecteur
{
    public string Mode => "fichier";
    public bool LitCatalogue => true;
    public bool EnvoieCommande => false;
    public Task<string?> IndisponibleAsync(CancellationToken ct = default) => Task.FromResult<string?>(null);

    public Task<CatalogueLu> LireCatalogueAsync(FichierGrossiste fichier, ProfilCatalogue profil, CancellationToken ct = default) =>
        Task.FromResult(LectureCatalogueGrossiste.Lire(lecteur.Lire(fichier.NomFichier, fichier.Contenu), profil));

    public Task<string> EnvoyerCommandeAsync(EnvoiBonCommande envoi, CancellationToken ct = default) =>
        throw new NotSupportedException("Le connecteur fichier n’envoie pas de commande.");
}

/// <summary>Connecteur « email » : bon de commande PDF envoyé à l'adresse du grossiste par le serveur SMTP de la pharmacie.</summary>
public sealed class GrossisteConnecteurEmail(INotificationsDepot reglages, IProtectionSecret secrets, IEnvoiEmailPieceJointe email) : IGrossisteConnecteur
{
    public string Mode => "email";
    public bool LitCatalogue => false;
    public bool EnvoieCommande => true;

    public async Task<string?> IndisponibleAsync(CancellationToken ct = default) =>
        (await reglages.ReglagesAsync(ct)).SmtpConfigure ? null
            : "Serveur d’envoi des e-mails non configuré (Administration › Notifications) : imprimez le bon ou configurez le SMTP.";

    public Task<CatalogueLu> LireCatalogueAsync(FichierGrossiste fichier, ProfilCatalogue profil, CancellationToken ct = default) =>
        throw new NotSupportedException("Le connecteur e-mail ne lit pas de catalogue.");

    public async Task<string> EnvoyerCommandeAsync(EnvoiBonCommande envoi, CancellationToken ct = default)
    {
        var r = await reglages.ReglagesAsync(ct);
        if (!r.SmtpConfigure) throw new RegleMetierException("Serveur d’envoi des e-mails non configuré.");
        var chiffre = await reglages.MotDePasseChiffreAsync(ct);
        var connexion = new ConnexionSmtp(r.SmtpServeur!, r.SmtpPort, r.SmtpTls, r.SmtpExpediteur!, r.SmtpIdentifiant,
            chiffre is null ? null : secrets.Dechiffrer(chiffre));
        var adresse = envoi.Grossiste.Email!.Trim();
        var de = string.IsNullOrWhiteSpace(envoi.Pharmacie) ? "la pharmacie" : envoi.Pharmacie;
        await email.EnvoyerAsync(connexion, adresse, $"Bon de commande {envoi.Numero}{(string.IsNullOrWhiteSpace(envoi.Pharmacie) ? "" : " · " + envoi.Pharmacie)}",
            $"Bonjour,\n\nVeuillez trouver ci-joint le bon de commande {envoi.Numero} de {de}.\n\nCordialement.",
            envoi.Bon.NomFichier, envoi.Bon.Contenu, "application/pdf", ct);
        return adresse;
    }
}
