using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Pharma.Shared;

namespace Pharma.Desktop.Services;

/// <summary>Session de l'employé connecté sur ce poste.</summary>
public sealed class SessionPoste
{
    public string? Jeton { get; private set; }
    public string? NomComplet { get; private set; }
    public DateTime ExpireLe { get; private set; }
    public bool EstConnecte => Jeton is not null && DateTime.UtcNow < ExpireLe;

    /// <summary>Connexion par code comptoir : seuls les écrans du comptoir sont accessibles.</summary>
    public bool ParCode { get; private set; }

    /// <summary>Droits de l'employé, pour griser ce qui lui est refusé (le serveur revérifie toujours).</summary>
    public IReadOnlySet<string> Permissions { get; private set; } = new HashSet<string>();

    /// <summary>Déconnexion automatique après ce nombre de minutes d'inactivité (0 = jamais).</summary>
    public int DeconnexionMinutes { get; private set; }

    /// <summary>Message affiché sur l'écran de connexion (ex. déconnexion automatique).</summary>
    public string? MotifDeconnexion { get; private set; }

    public bool A(string permission) => Permissions.Contains(permission);

    /// <summary>Prévient l'en-tête quand l'employé se connecte, se déconnecte ou que ses droits sont chargés.</summary>
    public event Action? Changee;

    /// <summary>Mot de passe provisoire : seul l'écran de changement est ouvert (le serveur refuse tout le reste).</summary>
    public bool DoitChangerMotDePasse { get; private set; }

    /// <summary>Compte super admin : seulement la reprise de données (et la restauration), chaque action validée par un code.</summary>
    public bool SuperAdmin { get; private set; }

    public void Ouvrir(ConnexionReponse r, bool parCode = false, bool superAdmin = false)
    {
        (Jeton, NomComplet, ExpireLe, MotifDeconnexion, ParCode, DoitChangerMotDePasse, SuperAdmin) = (r.Jeton, r.NomComplet, r.ExpireLe, null, parCode, r.DoitChangerMotDePasse, superAdmin);
        Changee?.Invoke();
    }

    /// <summary>Nom de la pharmacie enregistré par le titulaire (Administration, Langue et pharmacie).</summary>
    public string? NomPharmacie { get; private set; }

    public void DefinirDroits(IEnumerable<string> permissions, int deconnexionMinutes)
    {
        Permissions = permissions.ToHashSet();
        DeconnexionMinutes = deconnexionMinutes;
        Changee?.Invoke();
    }

    public void DefinirPharmacie(string? nom)
    {
        NomPharmacie = string.IsNullOrWhiteSpace(nom) ? null : nom;
        Changee?.Invoke();
    }

    /// <summary>Affichage choisi par l'employé connecté : auto (suit Windows), clair ou sombre. Sans employé : auto.</summary>
    public string Affichage { get; private set; } = "auto";

    public void DefinirAffichage(string affichage)
    {
        Affichage = affichage is "clair" or "sombre" ? affichage : "auto";
        Changee?.Invoke();
    }

    /// <summary>
    /// Juste avant la fermeture de la session (déconnexion, changement d'utilisateur, inactivité), jeton encore valable : la caisse
    /// y signale son panier abandonné, les fenêtres ouvertes se ferment.
    /// </summary>
    public event Action? AvantFermeture;

    /// <summary>
    /// Vrai pendant AvantFermeture quand la session se ferme par « Changer d'utilisateur » (pas une déconnexion ni l'inactivité) :
    /// la Caisse met alors son panier en attente au lieu de l'abandonner.
    /// </summary>
    public bool ChangementUtilisateur { get; private set; }

    /// <summary>Vente mise en attente au changement d'utilisateur, reprise par le prochain employé connecté sur ce poste.</summary>
    private (long Id, string LaisseePar)? venteAReprendre;
    public void RetenirVenteAReprendre(long id, string laisseePar) => venteAReprendre = (id, laisseePar);
    public bool AVenteAReprendre => venteAReprendre is not null;
    public (long Id, string LaisseePar)? PrendreVenteAReprendre() { var v = venteAReprendre; venteAReprendre = null; return v; }

    /// <summary>Relève de caisse faite par cet employé : « Changer d'utilisateur » ne la repropose pas.</summary>
    public bool ReleveFaite { get; private set; }
    public void MarquerReleveFaite() => ReleveFaite = true;

    public void Fermer(string? motif = null, bool changementUtilisateur = false)
    {
        ReleveFaite = false;
        ChangementUtilisateur = changementUtilisateur;
        try { if (Jeton is not null) AvantFermeture?.Invoke(); }
        finally { ChangementUtilisateur = false; }
        Affichage = "auto";
        (Jeton, NomComplet, MotifDeconnexion, ParCode, SuperAdmin) = (null, null, motif, false, false);
        Permissions = new HashSet<string>();
        Changee?.Invoke();
    }
}

/// <summary>Lot FEFO trouvé, ou raison de l'absence (rupture, lots écartés).</summary>
public sealed record ReponseLotFefo(LotFefoDto? Lot, IReadOnlyList<LotEcarteDto> Ecartes, string? Erreur);

/// <summary>Appels à l'API locale. Chaque méthode renvoie le résultat ou un message d'erreur lisible.</summary>
public sealed class ApiClient(HttpClient http, SessionPoste session, DemandesAutorisation? autorisations = null)
{
    private const string Injoignable = "Serveur de la pharmacie injoignable : vérifiez que le PC serveur est allumé.";

    /// <summary>Échecs attendus d'un appel : serveur injoignable, délai dépassé, réponse illisible, fichier local inaccessible.</summary>
    private static bool EstEchecAppel(Exception ex) =>
        ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or IOException or UnauthorizedAccessException;

    /// <summary>Délai d'attente dépassé (SuiviAppels) : l'écran peut le reconnaître pour adapter son message (ex. encaissement).</summary>
    public const string MessageDelaiDepasse =
        "Le serveur met trop de temps à répondre : l’opération continue peut-être sur le serveur. Attendez un instant, puis rechargez l’écran avant de recommencer.";

    private static string MessageEchec(Exception ex) => ex switch
    {
        TaskCanceledException => MessageDelaiDepasse,
        System.Text.Json.JsonException => "Réponse du serveur illisible : le poste et le serveur n’ont peut-être pas la même version du logiciel.",
        IOException or UnauthorizedAccessException => $"Fichier inaccessible : {ex.Message}",
        _ => Injoignable
    };

    /// <summary>Déconnexion automatique du super admin après 10 minutes sans activité.</summary>
    public const int DeconnexionSuperAdminMinutes = 10;

    /// <summary>Connexion du super admin (compte du serveur, hors de la base) : seuls ses écrans sont ouverts.</summary>
    public async Task<string?> ConnecterSuperAdminAsync(string identifiant, string motDePasse)
    {
        try
        {
            var rep = await http.PostAsJsonAsync("api/super-admin/connexion", new ConnexionRequete(identifiant, motDePasse));
            if (!rep.IsSuccessStatusCode) return await MessageAsync(rep);
            session.Ouvrir((await rep.Content.ReadFromJsonAsync<ConnexionReponse>())!, superAdmin: true);
            session.DefinirDroits(["role.super_admin"], DeconnexionSuperAdminMinutes);
            return null;
        }
        catch (Exception ex) when (EstEchecAppel(ex))
        {
            return MessageEchec(ex);
        }
    }

    /// <summary>Mot de passe ressaisi : le serveur envoie un code au téléphone du super admin (e-mail en secours).</summary>
    public Task<(DemandeValidationDto?, string?)> DemanderValidationSuperAdminAsync(string action, string motDePasse) =>
        PostAsync<ValidationSuperAdminRequete, DemandeValidationDto>("api/super-admin/validations", new(action, motDePasse));

    public Task<(CompteSuperAdminDto?, string?)> CompteSuperAdminAsync() => GetAsync<CompteSuperAdminDto?>("api/super-admin/compte", null);

    // ---- Restauration (super admin ; chaque restauration validée par code) ----
    public Task<(ChoixRestaurationDto?, string?)> ChoixRestaurationAsync() => GetAsync<ChoixRestaurationDto?>("api/super-admin/restauration", null);
    public Task<(EtatRestaurationDto?, string?)> EtatRestaurationAsync() => GetAsync<EtatRestaurationDto?>("api/super-admin/restauration/etat", null);
    public Task<(EtatRestaurationDto?, string?)> RestaurerBaseAsync(string fichier) =>
        PostAsync<RestaurationBaseRequete, EtatRestaurationDto>("api/super-admin/restauration/base", new(fichier));
    public Task<(EtatRestaurationDto?, string?)> RestaurerGedAsync() => PostAsync<object, EtatRestaurationDto>("api/super-admin/restauration/ged", new());

    // Code saisi : joint à la requête suivante seulement (en-tête X-Pharma-Validation), puis oublié
    private string? validationEnAttente;

    public void ValiderProchaineAction(Guid demande, string code) => validationEnAttente = $"{demande}:{code.Trim()}";

    public async Task<string?> ConnecterAsync(string identifiant, string motDePasse)
    {
        try
        {
            var rep = await http.PostAsJsonAsync("api/auth/connexion", new ConnexionRequete(identifiant, motDePasse));
            if (!rep.IsSuccessStatusCode) return await MessageAsync(rep);
            session.Ouvrir((await rep.Content.ReadFromJsonAsync<ConnexionReponse>())!);
            await ChargerDroitsAsync();
            return null;
        }
        catch (Exception ex) when (EstEchecAppel(ex))
        {
            return MessageEchec(ex);
        }
    }

    /// <summary>Changement d'utilisateur au comptoir (caisse du poste ouverte) avec le code personnel.</summary>
    /// <summary>Change le mot de passe de l'employé connecté ; le nouveau jeton remplace l'ancien (droits du rôle).</summary>
    public async Task<string?> ChangerMotDePasseAsync(string actuel, string nouveau, string confirmation)
    {
        var (r, erreur) = await PostAsync<ChangementMotDePasseRequete, ConnexionReponse>("api/auth/mot-de-passe", new(actuel, nouveau, confirmation));
        if (erreur is not null) return erreur;
        session.Ouvrir(r!, session.ParCode);
        await ChargerDroitsAsync();
        return null;
    }

    public async Task<string?> ConnecterParCodeAsync(string identifiant, string code)
    {
        try
        {
            var rep = await http.PostAsJsonAsync("api/auth/connexion-code", new ConnexionCodeRequete(identifiant, code));
            if (!rep.IsSuccessStatusCode) return await MessageAsync(rep);
            session.Ouvrir((await rep.Content.ReadFromJsonAsync<ConnexionReponse>())!, parCode: true);
            await ChargerDroitsAsync();
            return null;
        }
        catch (Exception ex) when (EstEchecAppel(ex))
        {
            return MessageEchec(ex);
        }
    }

    /// <summary>Employés proposés pour le changement d'utilisateur (vide si la caisse du poste est fermée).</summary>
    public Task<(ComptoirDto?, string?)> ComptoirAsync() => GetAsync<ComptoirDto?>("api/auth/comptoir", null);

    public async Task<string?> DefinirCodeComptoirAsync(string code) =>
        (await EnvoyerCorpsAsync<CodeComptoirRequete, object>(HttpMethod.Put, "api/auth/code-comptoir", new(code))).Item2;

    public async Task<string?> EffacerCodeComptoirAsync(long utilisateurId) =>
        (await EnvoyerCorpsAsync<object?, object>(HttpMethod.Delete, $"api/admin/utilisateurs/{utilisateurId}/code-comptoir", null)).Item2;

    public Task<(ReleveDto?, string?)> ReleveCaisseAsync(Dictionary<string, decimal> saisies, bool confirmerZero = false) =>
        PostAsync<ClotureRequete, ReleveDto>("api/caisse/releve", new(saisies, confirmerZero));

    // ---- Annulation d'une vente ----
    /// <summary>Remboursement sans ticket : ventes retrouvées par la boîte rapportée (sans patient, ou de ce patient).</summary>
    public Task<(VentesParBoiteDto?, string?)> VentesParBoiteAsync(string code, int jours, long? patientId, string? lot) =>
        GetAsync<VentesParBoiteDto?>($"api/caisse/ventes/par-boite?code={Uri.EscapeDataString(code.Trim())}&jours={jours}"
            + (patientId is { } p ? $"&patientId={p}" : "") + (string.IsNullOrWhiteSpace(lot) ? "" : $"&lot={Uri.EscapeDataString(lot.Trim())}"), null);

    /// <summary>Document du poste rangé dans la GED du serveur ; renvoie son chemin « Type\nom » dans la GED.</summary>
    public async Task<(string?, string?)> DeposerGedAsync(string type, string nomFichier, byte[] contenu)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, $"api/ged/{Uri.EscapeDataString(type)}") { Content = new ByteArrayContent(contenu) };
            req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            req.Headers.Add("X-Nom-Fichier", Uri.EscapeDataString(nomFichier));
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.Jeton);
            using var rep = await http.SendAsync(req);
            if (rep.StatusCode == HttpStatusCode.Unauthorized) return (null, SessionExpiree());
            if (!rep.IsSuccessStatusCode) return (null, await MessageAsync(rep));
            return ((await rep.Content.ReadFromJsonAsync<DocumentGedDto>())?.Chemin, null);
        }
        catch (Exception ex) when (EstEchecAppel(ex)) { return (null, MessageEchec(ex)); }
    }

    public Task<(List<VenteAnnulableDto>, string?)> VentesAnnulablesAsync(string? numero) =>
        GetAsync<List<VenteAnnulableDto>>($"api/caisse/ventes{(string.IsNullOrWhiteSpace(numero) ? "" : $"?numero={Uri.EscapeDataString(numero.Trim())}")}", []);

    public Task<(PageDto<VenteAnnulableDto>?, string?)> VentesAnnulablesPageAsync(string? numero, int page, int taille) =>
        GetAsync<PageDto<VenteAnnulableDto>?>($"api/caisse/ventes/page?page={page}&taille={taille}"
            + (string.IsNullOrWhiteSpace(numero) ? "" : $"&numero={Uri.EscapeDataString(numero.Trim())}"), null);

    public Task<(AnnulationVenteDto?, string?)> AnnulerVenteAsync(long venteId, AnnulationVenteRequete r) =>
        PostAsync<AnnulationVenteRequete, AnnulationVenteDto>($"api/caisse/ventes/{venteId}/annuler", r);

    public Task<(VenteRetournableDto?, string?)> VenteRetournableAsync(long venteId) =>
        GetAsync<VenteRetournableDto?>($"api/caisse/ventes/{venteId}/retour", null);

    public Task<(RetourClientDto?, string?)> RetourClientAsync(long venteId, RetourClientRequete r) =>
        PostAsync<RetourClientRequete, RetourClientDto>($"api/caisse/ventes/{venteId}/retour", r);

    public Task<(List<VendeurComptoirDto>, string?)> ValidateursRetourAsync() =>
        GetAsync<List<VendeurComptoirDto>>("api/caisse/validateurs-retour", []);

    public Task<(List<DetenteurDroitDto>, string?)> DelivrancePsychotropesAsync() =>
        GetAsync<List<DetenteurDroitDto>>("api/caisse/delivrance-psychotropes", []);
    public Task<(List<VendeurComptoirDto>, string?)> ValidateursAnnulationAsync() =>
        GetAsync<List<VendeurComptoirDto>>("api/caisse/validateurs-annulation", []);

    // ---- Inventaire complet ----
    public Task<(InventaireCompletDto?, string?)> InventaireCompletAsync() => GetAsync<InventaireCompletDto?>("api/inventaire-complet", null);
    public async Task<string?> CommencerInventaireCompletAsync(string mode) =>
        (await PostAsync<CommencerCampagneRequete, IdentifiantDto>("api/inventaire-complet", new(mode))).Item2;
    public async Task<string?> CloturerInventaireCompletAsync(long id, string? commentaire) =>
        (await EnvoyerCorpsAsync<CloturerCampagneRequete, object>(HttpMethod.Post, $"api/inventaire-complet/{id}/cloture", new(commentaire))).Item2;
    public async Task<string?> AnnulerInventaireCompletAsync(long id) =>
        (await EnvoyerCorpsAsync<object?, object>(HttpMethod.Post, $"api/inventaire-complet/{id}/annulation", null)).Item2;
    public Task<(byte[]?, string?, string?)> EtatInventaireAsync(long id) =>
        FichierAsync(HttpMethod.Get, $"api/inventaire-complet/{id}/etat", $"etat_inventaire_{id}.pdf");

    // ---- Factures clients ----
    public Task<(List<LigneVenteSourceDto>, string?)> VentesAFacturerAsync(string destinataire, long? patientId, long? organismeId, DateOnly du, DateOnly au, string? q) =>
        GetAsync<List<LigneVenteSourceDto>>($"api/factures/candidates?destinataire={destinataire}&du={du:yyyy-MM-dd}&au={au:yyyy-MM-dd}"
            + (patientId is { } p ? $"&patientId={p}" : "") + (organismeId is { } o ? $"&organismeId={o}" : "")
            + (string.IsNullOrWhiteSpace(q) ? "" : $"&q={Uri.EscapeDataString(q.Trim())}"), []);
    public Task<(FacturesPageDto?, string?)> FacturesPageAsync(string? q, string? destinataire, string? suivi, DateOnly? du, DateOnly? au, int page, int taille) =>
        GetAsync<FacturesPageDto?>("api/factures/page?" + Requete(("q", q?.Trim()), ("destinataire", destinataire), ("suivi", suivi), ("du", du), ("au", au),
            ("page", page), ("taille", taille)), null);
    public Task<(List<FactureResumeDto>, string?)> FacturesAsync(string? q, string? destinataire, string? suivi, DateOnly? du, DateOnly? au) =>
        GetAsync<List<FactureResumeDto>>("api/factures?" + string.Join("&", new[]
        {
            string.IsNullOrWhiteSpace(q) ? null : $"q={Uri.EscapeDataString(q.Trim())}", destinataire is null ? null : $"destinataire={destinataire}",
            suivi is null ? null : $"suivi={suivi}", du is { } d ? $"du={d:yyyy-MM-dd}" : null, au is { } a ? $"au={a:yyyy-MM-dd}" : null
        }.Where(x => x is not null)), []);
    public Task<(FactureDetailDto?, string?)> FactureAsync(long id, int? version = null) =>
        GetAsync<FactureDetailDto?>($"api/factures/{id}{(version is { } v ? $"?version={v}" : "")}", null);
    public Task<(List<EvenementFactureDto>, string?)> HistoriqueFactureAsync(long id) =>
        GetAsync<List<EvenementFactureDto>>($"api/factures/{id}/historique", []);
    public Task<(byte[]?, string?, string?)> FacturePdfAsync(long id, string numero, int? version = null) =>
        FichierAsync(HttpMethod.Get, $"api/factures/{id}/pdf{(version is { } v ? $"?version={v}" : "")}", $"{numero}.pdf");
    public Task<(FactureEmiseDto?, string?)> EmettreFactureAsync(DemandeFactureDto d) => PostAsync<DemandeFactureDto, FactureEmiseDto>("api/factures", d);
    public async Task<string?> RejeterFactureAsync(long id, string? motif) =>
        (await EnvoyerCorpsAsync<MotifRequete, object>(HttpMethod.Post, $"api/factures/{id}/rejet", new(motif))).Item2;
    public async Task<string?> LeverRejetFactureAsync(long id, string? motif) =>
        (await EnvoyerCorpsAsync<MotifRequete, object>(HttpMethod.Post, $"api/factures/{id}/lever-rejet", new(motif))).Item2;
    public async Task<string?> CorrigerFactureAsync(long id, DemandeFactureDto d) =>
        (await EnvoyerCorpsAsync<DemandeFactureDto, object>(HttpMethod.Post, $"api/factures/{id}/correction", d)).Item2;
    public Task<(FactureEmiseDto?, string?)> RemplacerFactureAsync(long id, DemandeFactureDto d) =>
        PostAsync<DemandeFactureDto, FactureEmiseDto>($"api/factures/{id}/remplacement", d);
    public Task<(FactureEmiseDto?, string?)> AvoirFactureAsync(long id, decimal montant, string? motif) =>
        PostAsync<AvoirClientRequete, FactureEmiseDto>($"api/factures/{id}/avoir", new(montant, motif));
    public async Task<string?> SuiviFactureAsync(long id, bool horsSuivi, string? motif) =>
        (await EnvoyerCorpsAsync<SuiviRequete, object>(HttpMethod.Post, $"api/factures/{id}/suivi", new(horsSuivi, motif))).Item2;
    public Task<(List<ClientFactureDto>, string?)> ClientsFactureAsync() => GetAsync<List<ClientFactureDto>>("api/factures/clients", []);
    public Task<(IdentifiantDto?, string?)> EnregistrerClientFactureAsync(ClientFactureDto c) => PostAsync<ClientFactureDto, IdentifiantDto>("api/factures/clients", c);
    public Task<(List<OrganismeFicheDto>, string?)> OrganismesFichesAsync() => GetAsync<List<OrganismeFicheDto>>("api/admin/organismes", []);
    public Task<(IdentifiantDto?, string?)> EnregistrerOrganismeAsync(OrganismeFicheDto o) => PostAsync<OrganismeFicheDto, IdentifiantDto>("api/admin/organismes", o);

    // ---- Trésorerie ----
    public Task<(SuiviDettesDto?, string?)> SuiviDettesAsync() => GetAsync<SuiviDettesDto?>("api/suivi-dettes", null);
    /// <summary>Sommes dues filtrées et paginées par le serveur (catégorie, tiers, état, période).</summary>
    public Task<(SuiviDettesLignesDto?, string?)> SuiviDettesLignesAsync(string? categorie, string? tiers, string? etat, DateOnly? du, DateOnly? au, int page, int taille, string? recherche = null)
    {
        var q = new List<string> { $"page={page}", $"taille={taille}" };
        if (!string.IsNullOrEmpty(categorie)) q.Add($"categorie={categorie}");
        if (!string.IsNullOrEmpty(tiers)) q.Add($"tiers={Uri.EscapeDataString(tiers)}");
        if (!string.IsNullOrEmpty(etat)) q.Add($"etat={etat}");
        if (du is { } d) q.Add($"du={d:yyyy-MM-dd}");
        if (au is { } x) q.Add($"au={x:yyyy-MM-dd}");
        if (!string.IsNullOrWhiteSpace(recherche)) q.Add($"recherche={Uri.EscapeDataString(recherche.Trim())}");
        return GetAsync<SuiviDettesLignesDto?>("api/suivi-dettes/lignes?" + string.Join('&', q), null);
    }
    public async Task<string?> DelaiAlerteDettesAsync(int jours) =>
        (await EnvoyerCorpsAsync<DelaiAlerteDettesRequete, object>(HttpMethod.Put, "api/suivi-dettes/delai", new(jours))).Item2;
    public Task<(List<CompteTresorerieDto>, string?)> ComptesTresorerieAsync() => GetAsync<List<CompteTresorerieDto>>("api/tresorerie/comptes", []);
    public Task<(MouvementsTresoreriePageDto?, string?)> MouvementsTresorerieAsync(long compteId, DateOnly du, DateOnly au, int page, int taille, string? employe) =>
        GetAsync<MouvementsTresoreriePageDto?>($"api/tresorerie/comptes/{compteId}/mouvements?du={du:yyyy-MM-dd}&au={au:yyyy-MM-dd}&page={page}&taille={taille}"
            + (string.IsNullOrWhiteSpace(employe) ? "" : $"&employe={Uri.EscapeDataString(employe)}"), null);
    public Task<(AVerserDto?, string?)> AVerserAsync() => GetAsync<AVerserDto?>("api/tresorerie/a-verser", null);
    public Task<(List<CodeLibelleDto>, string?)> CategoriesDepensesAsync() => GetAsync<List<CodeLibelleDto>>("api/tresorerie/categories", []);
    public async Task<string?> EnregistrerCompteTresorerieAsync(long? id, CompteTresorerieRequete r) => id is { } i
        ? (await EnvoyerCorpsAsync<CompteTresorerieRequete, IdentifiantDto>(HttpMethod.Put, $"api/tresorerie/comptes/{i}", r)).Item2
        : (await PostAsync<CompteTresorerieRequete, IdentifiantDto>("api/tresorerie/comptes", r)).Item2;
    public async Task<string?> SaisirMouvementTresorerieAsync(MouvementTresorerieRequete r) =>
        (await PostAsync<MouvementTresorerieRequete, IdentifiantDto>("api/tresorerie/mouvements", r)).Item2;
    public async Task<string?> TransfererAsync(TransfertRequete r) => (await PostAsync<TransfertRequete, IdentifiantDto>("api/tresorerie/transferts", r)).Item2;
    public async Task<string?> RemettreChequesAsync(RemiseChequesRequete r) =>
        (await PostAsync<RemiseChequesRequete, IdentifiantDto>("api/tresorerie/remises-cheques", r)).Item2;
    public async Task<string?> VerserCaisseClotureeAsync(long sessionId, decimal montant) =>
        (await PostAsync<MontantRequete, IdentifiantDto>($"api/tresorerie/caisses/{sessionId}/versement", new(montant))).Item2;
    public async Task<string?> VerserCaisseDuPosteAsync(decimal montant) =>
        (await PostAsync<MontantRequete, IdentifiantDto>("api/tresorerie/caisse-du-poste/versement", new(montant))).Item2;
    public Task<(List<CompteBancaireDto>, string?)> BanquesReglementAsync() => GetAsync<List<CompteBancaireDto>>("api/achats/reglements/banques", []);

    // ---- Financements (titulaire seul) ----
    public Task<(PageDto<PretDto>?, string?)> PretsAsync(string? etat, string? texte, long? banque, int page, int taille) =>
        GetAsync<PageDto<PretDto>?>("api/financements/prets?" + Requete(("etat", etat), ("q", texte), ("banque", banque), ("page", page), ("taille", taille)), null);
    public Task<(PretDetailDto?, string?)> PretAsync(long id) => GetAsync<PretDetailDto?>($"api/financements/prets/{id}", null);
    public Task<(IdentifiantDto?, string?)> CreerPretAsync(PretRequete r) => PostAsync<PretRequete, IdentifiantDto>("api/financements/prets", r);
    public async Task<string?> ModifierPretAsync(long id, PretRequete r) =>
        (await EnvoyerCorpsAsync<PretRequete, object>(HttpMethod.Put, $"api/financements/prets/{id}", r)).Item2;
    public async Task<string?> JoindreDocumentPretAsync(long id, string chemin) =>
        (await EnvoyerCorpsAsync<DocumentPretRequete, object>(HttpMethod.Put, $"api/financements/prets/{id}/document", new(chemin))).Item2;
    public Task<(List<AnomalieEcheancierDto>?, string?)> ControlerEcheancierAsync(long pretId, EcheancierRequete r) =>
        PostAsync<EcheancierRequete, List<AnomalieEcheancierDto>>($"api/financements/prets/{pretId}/echeancier/controle", r);
    public async Task<string?> EnregistrerEcheancierAsync(long pretId, EcheancierRequete r) =>
        (await EnvoyerCorpsAsync<EcheancierRequete, object>(HttpMethod.Put, $"api/financements/prets/{pretId}/echeancier", r)).Item2;
    public Task<(TableauLuDto?, string?)> LireTableauFichierAsync(long pretId, TableauFichierRequete r) =>
        PostAsync<TableauFichierRequete, TableauLuDto>($"api/financements/prets/{pretId}/tableau/fichier", r);
    public Task<(TableauLuDto?, string?)> LireTableauTexteAsync(long pretId, TableauTexteRequete r) =>
        PostAsync<TableauTexteRequete, TableauLuDto>($"api/financements/prets/{pretId}/tableau/texte", r);
    public Task<(ApercuEcheancierDto?, string?)> ApercuEcheancierAsync(long pretId, ApercuRequete r) =>
        PostAsync<ApercuRequete, ApercuEcheancierDto>($"api/financements/prets/{pretId}/tableau/apercu", r);
    public async Task<string?> RetenirCorrespondanceAsync(long pretId, CorrespondanceDto c) =>
        (await EnvoyerCorpsAsync<CorrespondanceDto, object>(HttpMethod.Put, $"api/financements/prets/{pretId}/correspondance", c)).Item2;
    public Task<(PageDto<EcheancePretDto>?, string?)> EcheancesPretsAsync(string? etat, string? texte, int page, int taille) =>
        GetAsync<PageDto<EcheancePretDto>?>("api/financements/echeances?" + Requete(("etat", etat), ("q", texte), ("page", page), ("taille", taille)), null);
    public async Task<string?> PayerEcheancePretAsync(long echeanceId, PaiementEcheanceRequete r) =>
        (await PostAsync<PaiementEcheanceRequete, IdentifiantDto>($"api/financements/echeances/{echeanceId}/paiement", r)).Item2;
    public Task<(List<SourceFinancementDto>, string?)> SourcesFinancementAsync() => GetAsync<List<SourceFinancementDto>>("api/financements/sources", []);
    public async Task<string?> UtiliserFinancementAsync(UtilisationRequete r) =>
        (await PostAsync<UtilisationRequete, IdentifiantDto>("api/financements/utilisations", r)).Item2;
    public Task<(CompteTitulaireDto?, string?)> CompteTitulaireAsync(string? sens, DateOnly? du, DateOnly? au, int page, int taille) =>
        GetAsync<CompteTitulaireDto?>("api/financements/titulaire?" + Requete(("sens", sens), ("du", du), ("au", au), ("page", page), ("taille", taille)), null);
    public async Task<string?> RembourserTitulaireAsync(RemboursementTitulaireRequete r) =>
        (await PostAsync<RemboursementTitulaireRequete, IdentifiantDto>("api/financements/titulaire/remboursements", r)).Item2;

    // ---- Règlements des grossistes et crédits clients ----
    public Task<(List<GrossisteAReglementDto>, string?)> GrossistesAReglerAsync() => GetAsync<List<GrossisteAReglementDto>>("api/achats/reglements", []);
    public Task<(SituationGrossisteDto?, string?)> SituationGrossisteAsync(long id) => GetAsync<SituationGrossisteDto?>($"api/achats/reglements/{id}", null);
    public async Task<string?> EnregistrerReglementAsync(long fournisseurId, ReglementRequete r) =>
        (await PostAsync<ReglementRequete, IdentifiantDto>($"api/achats/reglements/{fournisseurId}", r)).Item2;
    /// <summary>Traites filtrées (état, grossiste ou n°) et paginées par le serveur (solde du coffre pour le titulaire).</summary>
    public Task<(TraitesAPayerDto?, string?)> TraitesAPayerAsync(string? etat, string? texte, int page, int taille) =>
        GetAsync<TraitesAPayerDto?>("api/achats/traites?" + Requete(("etat", etat), ("q", texte), ("page", page), ("taille", taille)), null);
    /// <summary>Paiement d’une traite confirmé par le titulaire.</summary>
    public async Task<string?> PayerTraiteAsync(long reglementId, PaiementTraiteRequete r) =>
        (await PostAsync<PaiementTraiteRequete, IdentifiantDto>($"api/achats/traites/{reglementId}/paiement", r)).Item2;

    public Task<(List<PatientEnCreditDto>, string?)> PatientsEnCreditAsync(string? recherche) =>
        GetAsync<List<PatientEnCreditDto>>($"api/caisse/credits{(string.IsNullOrWhiteSpace(recherche) ? "" : $"?q={Uri.EscapeDataString(recherche.Trim())}")}", []);
    public Task<(PageDto<PatientEnCreditDto>?, string?)> PatientsEnCreditPageAsync(string? recherche, int page, int taille) =>
        GetAsync<PageDto<PatientEnCreditDto>?>($"api/caisse/credits/page?page={page}&taille={taille}"
            + (string.IsNullOrWhiteSpace(recherche) ? "" : $"&q={Uri.EscapeDataString(recherche.Trim())}"), null);
    public Task<(SituationCreditDto?, string?)> SituationCreditAsync(long patientId) => GetAsync<SituationCreditDto?>($"api/caisse/credits/{patientId}", null);
    public Task<(PageDto<MouvementCreditDto>?, string?)> MouvementsCreditAsync(long patientId, int page, int taille, DateOnly? du = null, DateOnly? au = null, string? type = null) =>
        GetAsync<PageDto<MouvementCreditDto>?>($"api/caisse/credits/{patientId}/mouvements?" + Requete(("du", du), ("au", au), ("type", type), ("page", page), ("taille", taille)), null);
    public Task<(PageDto<MouvementGrossisteDto>?, string?)> MouvementsGrossisteAsync(long fournisseurId, int page, int taille, DateOnly? du = null, DateOnly? au = null, string? type = null) =>
        GetAsync<PageDto<MouvementGrossisteDto>?>($"api/achats/reglements/{fournisseurId}/mouvements?" + Requete(("du", du), ("au", au), ("type", type), ("page", page), ("taille", taille)), null);
    public Task<(byte[]?, string?, string?)> ReleveCreditAsync(long patientId) => FichierAsync(HttpMethod.Get, $"api/caisse/credits/{patientId}/releve", "releve_credit.pdf");
    public async Task<string?> CorrigerCreditAsync(long patientId, decimal nouvelleDette, string? motif) =>
        (await PostAsync<CorrectionCreditRequete, object>($"api/caisse/credits/{patientId}/correction", new(nouvelleDette, motif))).Item2;
    public Task<(RecuCreditDto?, string?)> ReglerCreditAsync(long patientId, ReglementCreditRequete r) =>
        PostAsync<ReglementCreditRequete, RecuCreditDto>($"api/caisse/credits/{patientId}/reglement", r);

    // ---- Ventes en attente du poste ----
    public Task<(List<VenteEnAttenteDto>, string?)> VentesEnAttenteAsync() => GetAsync<List<VenteEnAttenteDto>>("api/caisse/attente", []);

    // ---- Réceptions en attente (Achats › Réceptions) ----
    public Task<(List<ReceptionEnAttenteDto>, string?)> ReceptionsEnAttenteAsync() =>
        GetAsync<List<ReceptionEnAttenteDto>>("api/achats/receptions/attente", []);

    /// <summary>Numéro de la réception mise en attente, ou l'erreur. Le jeton est lu avant le premier « await » (utilisable juste avant une déconnexion).</summary>
    public async Task<(long? Id, string? Erreur)> MettreReceptionEnAttenteAsync(MiseEnAttenteReceptionRequete r)
    {
        var (id, e) = await PostAsync<MiseEnAttenteReceptionRequete, IdentifiantDto>("api/achats/receptions/attente", r);
        return (id?.Id, e);
    }

    /// <summary>Contenu JSON de la réception reprise (retirée de l'attente).</summary>
    public Task<(string?, string?)> ReprendreReceptionEnAttenteAsync(long id) => ReprendreJsonAsync($"api/achats/receptions/attente/{id}/reprendre");

    public async Task<string?> AbandonnerReceptionEnAttenteAsync(long id) =>
        (await EnvoyerCorpsAsync<object?, object>(HttpMethod.Delete, $"api/achats/receptions/attente/{id}", null)).Item2;

    /// <summary>Numéro de la vente mise en attente (pour la reprendre au retour sur la Caisse), ou l'erreur.</summary>
    public async Task<(long? Id, string? Erreur)> MettreEnAttenteAsync(MiseEnAttenteRequete r)
    {
        var (id, e) = await PostAsync<MiseEnAttenteRequete, IdentifiantDto>("api/caisse/attente", r);
        return (id?.Id, e);
    }

    /// <summary>Contenu JSON du panier repris (retiré de l'attente).</summary>
    public Task<(string?, string?)> ReprendreVenteEnAttenteAsync(long id) => ReprendreJsonAsync($"api/caisse/attente/{id}/reprendre");

    /// <summary>Reprise d'un élément en attente (vente, réception) : contenu JSON rendu tel quel, ou l'erreur.</summary>
    private async Task<(string?, string?)> ReprendreJsonAsync(string url)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.Jeton);
            using var rep = await http.SendAsync(req);
            if (rep.StatusCode == HttpStatusCode.Unauthorized) return (null, SessionExpiree());
            if (!rep.IsSuccessStatusCode) return (null, await MessageAsync(rep));
            return (await rep.Content.ReadAsStringAsync(), null);
        }
        catch (Exception ex) when (EstEchecAppel(ex)) { return (null, MessageEchec(ex)); }
    }

    public async Task<string?> SupprimerVenteEnAttenteAsync(long id) =>
        (await EnvoyerCorpsAsync<object?, object>(HttpMethod.Delete, $"api/caisse/attente/{id}", null)).Item2;

    public Task<(TicketDto?, string?)> TicketAsync(long venteId) => GetAsync<TicketDto?>($"api/caisse/ventes/{venteId}/ticket", null);

    // ---- Registre des psychotropes ---------------------------------------------

    private static string FiltreRegistre(long? produit, DateOnly du, DateOnly au) =>
        $"du={du:yyyy-MM-dd}&au={au:yyyy-MM-dd}{(produit is { } p ? $"&produit={p}" : "")}";

    public Task<(List<ProduitRegistreDto>, string?)> ProduitsRegistreAsync() => GetAsync<List<ProduitRegistreDto>>("api/registre/produits", []);

    public Task<(RegistreDto?, string?)> RegistreAsync(long? produit, DateOnly du, DateOnly au) =>
        GetAsync<RegistreDto?>($"api/registre?{FiltreRegistre(produit, du, au)}", null);

    public Task<(InscriptionsRegistreDto?, string?)> InscriptionsRegistreAsync(long? produit, DateOnly du, DateOnly au, string? operation, int page, int taille) =>
        GetAsync<InscriptionsRegistreDto?>($"api/registre/inscriptions?{FiltreRegistre(produit, du, au)}&page={page}&taille={taille}"
            + (string.IsNullOrEmpty(operation) ? "" : $"&operation={Uri.EscapeDataString(operation)}"), null);

    public Task<(byte[]?, string?, string?)> ExporterRegistreAsync(long? produit, DateOnly du, DateOnly au, string format) =>
        FichierAsync(HttpMethod.Get, $"api/registre/export?format={format}&{FiltreRegistre(produit, du, au)}", $"registre_psychotropes.{format}");

    // ---- Nomenclature nationale ------------------------------------------------

    public Task<(EtatNomenclatureDto?, string?)> EtatNomenclatureAsync() => GetAsync<EtatNomenclatureDto?>("api/admin/nomenclature", null);

    /// <summary>Envoie le fichier Excel officiel (corps brut) ; le serveur le lit et remplace la nomenclature.</summary>
    public async Task<(VersionNomenclatureDto?, string?)> ImporterNomenclatureAsync(string chemin)
    {
        try
        {
            await using var fichier = File.OpenRead(chemin);
            using var req = new HttpRequestMessage(HttpMethod.Post, "api/admin/nomenclature") { Content = new StreamContent(fichier) };
            req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            req.Headers.Add("X-Nom-Fichier", Uri.EscapeDataString(Path.GetFileName(chemin)));
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.Jeton);
            using var rep = await http.SendAsync(req);
            if (rep.StatusCode == HttpStatusCode.Unauthorized) return (null, SessionExpiree());
            if (!rep.IsSuccessStatusCode) return (null, await MessageAsync(rep));
            return (await rep.Content.ReadFromJsonAsync<VersionNomenclatureDto>(), null);
        }
        catch (Exception ex) when (EstEchecAppel(ex)) { return (null, MessageEchec(ex)); }
        catch (IOException e) { return (null, $"Fichier illisible : {e.Message}"); }
    }

    public Task<(List<MedicamentNomenclatureDto>, string?)> RechercherNomenclatureAsync(string texte) =>
        GetAsync<List<MedicamentNomenclatureDto>>($"api/nomenclature?q={Uri.EscapeDataString(texte)}", []);

    public async Task<string?> ChangerClasseProduitAsync(long produitId, string classe) =>
        (await EnvoyerCorpsAsync<ClasseProduitRequete, object>(HttpMethod.Put, $"api/admin/produits/{produitId}/classe", new(classe))).Item2;

    public async Task<string?> DefinirAffichageAsync(string affichage) =>
        (await EnvoyerCorpsAsync<AffichageDto, object>(HttpMethod.Put, "api/auth/affichage", new(affichage))).Item2;

    /// <summary>Droits de l'employé et réglage de déconnexion automatique, juste après la connexion.</summary>
    public async Task ChargerDroitsAsync()
    {
        // Affichage retrouvé à chaque connexion (mot de passe ou code comptoir)
        var (affichage, _) = await GetAsync<AffichageDto?>("api/auth/affichage", null);
        session.DefinirAffichage(affichage?.Affichage ?? "auto");
        var (droits, _) = await GetAsync<List<string>>("api/auth/permissions", []);
        var deconnexion = 0;
        if (droits.Contains("caisse.vendre") && (await EtatCaisseAsync()).Item1 is { } etat)
            deconnexion = etat.DeconnexionMinutes;
        session.DefinirDroits(droits, deconnexion);
        if ((await PharmacieAsync()).Item1 is { } pharmacie)
            session.DefinirPharmacie(pharmacie.Nom);
    }

    // ---- Caisse ---------------------------------------------------------------

    public Task<(EtatCaisseDto?, string?)> EtatCaisseAsync() => GetAsync<EtatCaisseDto?>("api/caisse/etat", null);

    /// <summary>État intermédiaire de la caisse du poste : récapitulatif sans espèces et comptages intermédiaires.</summary>
    public Task<(EtatIntermediaireDto?, string?)> EtatIntermediaireAsync() => GetAsync<EtatIntermediaireDto?>("api/caisse/etat-intermediaire", null);

    /// <summary>Comptage du tiroir sans clôture : la caisse reste ouverte.</summary>
    public Task<(ClotureDto?, string?)> ComptageIntermediaireAsync(Dictionary<string, decimal> saisies) =>
        PostAsync<ClotureRequete, ClotureDto>("api/caisse/comptage-intermediaire", new ClotureRequete(saisies));

    public Task<(IdentifiantDto?, string?)> OuvrirCaisseAsync(string poste, decimal? fond) =>
        PostAsync<OuvrirCaisseRequete, IdentifiantDto>("api/caisse/ouvrir", new(poste, fond));

    public Task<(ApercuVenteDto?, string?)> CalculerVenteAsync(CommandeVenteDto commande) =>
        PostAsync<CommandeVenteDto, ApercuVenteDto>("api/caisse/calculer", commande);

    public Task<(VenteEncaisseeDto?, string?)> EncaisserAsync(CommandeVenteDto commande) =>
        PostAsync<CommandeVenteDto, VenteEncaisseeDto>("api/caisse/encaisser", commande);

    /// <param name="confirmerZero">Clôture avec 0 DA compté confirmée par l'employé (sinon refusée par le serveur).</param>
    public Task<(ClotureDto?, string?)> CloturerCaisseAsync(Dictionary<string, decimal> saisies, bool confirmerZero = false) =>
        PostAsync<ClotureRequete, ClotureDto>("api/caisse/cloturer", new(saisies, confirmerZero));

    /// <summary>Rapport de clôture PDF : contenu et nom de fichier proposé par le serveur.</summary>
    public async Task<(byte[]?, string?, string?)> RapportClotureAsync(long sessionId)
    {
        try
        {
            using var rep = await EnvoyerAsync($"api/caisse/sessions/{sessionId}/rapport");
            if (rep.StatusCode == HttpStatusCode.Unauthorized) return (null, null, SessionExpiree());
            if (!rep.IsSuccessStatusCode) return (null, null, await MessageAsync(rep));
            var nom = rep.Content.Headers.ContentDisposition?.FileNameStar
                      ?? rep.Content.Headers.ContentDisposition?.FileName?.Trim('"')
                      ?? $"cloture_{sessionId}.pdf";
            return (await rep.Content.ReadAsByteArrayAsync(), nom, null);
        }
        catch (Exception ex) when (EstEchecAppel(ex))
        {
            return (null, null, MessageEchec(ex));
        }
    }

    // ---- Ordonnance scannée ------------------------------------------------------

    public Task<(AnalyseOrdonnanceDto?, string?)> AnalyserOrdonnanceAsync(IReadOnlyList<string> lignes) =>
        PostAsync<AnalyseOrdonnanceRequete, AnalyseOrdonnanceDto>("api/ordonnances/analyser", new(lignes));

    /// <summary>Envoie l'image au serveur ; le jeton renvoyé lie ensuite l'image à la vente.</summary>
    public async Task<(string?, string?)> EnvoyerScanOrdonnanceAsync(ImageOrdonnance image)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, "api/ordonnances/scans") { Content = new ByteArrayContent(image.Contenu) };
            req.Content.Headers.ContentType = new MediaTypeHeaderValue(image.TypeMime);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.Jeton);
            using var rep = await http.SendAsync(req);
            if (rep.StatusCode == HttpStatusCode.Unauthorized) return (null, SessionExpiree());
            if (!rep.IsSuccessStatusCode) return (null, await MessageAsync(rep));
            return ((await rep.Content.ReadFromJsonAsync<ScanOrdonnanceDto>())!.Jeton, null);
        }
        catch (Exception ex) when (EstEchecAppel(ex))
        {
            return (null, MessageEchec(ex));
        }
    }

    // ---- Patients, médecins, organismes ------------------------------------

    public Task<(List<PatientDto>, string?)> RechercherPatientsAsync(string q) =>
        GetAsync<List<PatientDto>>($"api/patients?q={Uri.EscapeDataString(q)}", []);

    public Task<(IdentifiantDto?, string?)> CreerPatientAsync(NouveauPatientDto patient) =>
        PostAsync<NouveauPatientDto, IdentifiantDto>("api/patients", patient);

    public Task<(List<MedecinDto>, string?)> RechercherMedecinsAsync(string q) =>
        GetAsync<List<MedecinDto>>($"api/medecins?q={Uri.EscapeDataString(q)}", []);

    public Task<(IdentifiantDto?, string?)> CreerMedecinAsync(NouveauMedecinDto medecin) =>
        PostAsync<NouveauMedecinDto, IdentifiantDto>("api/medecins", medecin);

    public Task<(FichePatientDto?, string?)> FichePatientAsync(long id) => GetAsync<FichePatientDto?>($"api/patients/{id}/fiche", null);
    public async Task<string?> ModifierPatientAsync(FichePatientDto f) =>
        (await EnvoyerCorpsAsync<FichePatientDto, object>(HttpMethod.Put, $"api/patients/{f.Id}", f)).Item2;
    public Task<(List<VendeurComptoirDto>, string?)> ValidateursBlocageAsync() =>
        GetAsync<List<VendeurComptoirDto>>("api/produits/validateurs-blocage", []);
    public Task<(FicheProduitModifiableDto?, string?)> FicheProduitAsync(long id) => GetAsync<FicheProduitModifiableDto?>($"api/produits/{id}/fiche", null);
    /// <summary>Information : message à afficher (classe imposée, produit à vérifier), sinon null ; Erreur : refus.</summary>
    public async Task<(string? Information, string? Erreur)> ModifierProduitAsync(FicheProduitModifiableDto f)
    {
        var (r, e) = await EnvoyerCorpsAsync<FicheProduitModifiableDto, ProduitEnregistreDto>(HttpMethod.Put, $"api/produits/{f.Id}", f);
        return (r?.Information, e);
    }
    public Task<(List<LotEnStockDto>, string?)> LotsEnStockAsync(long produitId) => GetAsync<List<LotEnStockDto>>($"api/stock/produits/{produitId}/lots", []);
    public Task<(LotEnStockDto?, string?)> LotEnStockAsync(long lotId) => GetAsync<LotEnStockDto?>($"api/stock/lots/{lotId}", null);
    public Task<(NombreDto?, string?)> SortirDuStockAsync(SortieStockRequete r) => PostAsync<SortieStockRequete, NombreDto>("api/stock/sorties", r);
    /// <param name="remise">Remise refusée (pourcentage) : seuls les employés dont le maximum de remise la couvre.</param>
    public Task<(List<VendeurComptoirDto>, string?)> ValideursAsync(string? permission, decimal? remise = null) =>
        GetAsync<List<VendeurComptoirDto>>("api/auth/valideurs?" + string.Join("&", new[]
        {
            permission is null ? null : "permission=" + Uri.EscapeDataString(permission),
            remise is null ? null : "remise=" + remise.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)
        }.Where(p => p is not null)), []);
    public Task<(List<BlocageProduitDto>, string?)> ProduitsBloquesAsync() => GetAsync<List<BlocageProduitDto>>("api/produits/bloques", []);
    public Task<(BlocageProduitDto?, string?)> BlocageProduitAsync(long produitId) => GetAsync<BlocageProduitDto?>($"api/produits/{produitId}/blocage", null);
    public async Task<string?> ModifierBlocageProduitAsync(long produitId, BlocageProduitRequete r) =>
        (await EnvoyerCorpsAsync<BlocageProduitRequete, object>(HttpMethod.Put, $"api/produits/{produitId}/blocage", r)).Item2;
    public Task<(List<string>, string?)> MentionsFactureManquantesAsync() => GetAsync<List<string>>("api/factures/mentions-manquantes", []);
    public Task<(List<RetourEssaiDto>, string?)> RetoursEssaiAsync() => GetAsync<List<RetourEssaiDto>>("api/retours-essai", []);
    public async Task<string?> EnvoyerRetourEssaiAsync(NouveauRetourEssaiDto d) =>
        (await EnvoyerCorpsAsync<NouveauRetourEssaiDto, object>(HttpMethod.Post, "api/retours-essai", d)).Item2;
    public Task<(List<CategorieProduitDto>, string?)> CategoriesProduitsAsync() => GetAsync<List<CategorieProduitDto>>("api/categories-produits", []);

    // Remises automatiques (produit ou catégorie, sur une période)
    public Task<(List<RemiseAutomatiqueDto>, string?)> RemisesAutomatiquesAsync() => GetAsync<List<RemiseAutomatiqueDto>>("api/remises-automatiques", []);

    public Task<(IdentifiantDto?, string?)> CreerRemiseAutomatiqueAsync(RemiseAutomatiqueSaisieDto saisie) =>
        PostAsync<RemiseAutomatiqueSaisieDto, IdentifiantDto>("api/remises-automatiques", saisie);

    public async Task<string?> ModifierRemiseAutomatiqueAsync(long id, RemiseAutomatiqueSaisieDto saisie) =>
        (await EnvoyerCorpsAsync<RemiseAutomatiqueSaisieDto, object>(HttpMethod.Put, $"api/remises-automatiques/{id}", saisie)).Item2;

    public async Task<string?> ArreterRemiseAutomatiqueAsync(long id) =>
        (await PostAsync<object, object>($"api/remises-automatiques/{id}/arreter", new { })).Item2;
    public Task<(ClassementProduitDto?, string?)> ClassementProduitAsync(long produitId) => GetAsync<ClassementProduitDto?>($"api/produits/{produitId}/classement", null);
    public async Task<string?> ModifierClassementProduitAsync(long produitId, string categorie, string? listePsychotrope) =>
        (await EnvoyerCorpsAsync<ClassementProduitRequete, object>(HttpMethod.Put, $"api/produits/{produitId}/classement", new(categorie, listePsychotrope))).Item2;
    public async Task<string?> ModifierMedecinAsync(long id, string nom, string? specialite) =>
        (await EnvoyerCorpsAsync<NouveauMedecinDto, object>(HttpMethod.Put, $"api/medecins/{id}", new(nom, specialite))).Item2;
    public Task<(List<OrganismeDto>, string?)> OrganismesAsync() => GetAsync<List<OrganismeDto>>("api/organismes", []);

    // ---- Administration -------------------------------------------------------

    public Task<(PharmacieDto?, string?)> PharmacieAsync() => GetAsync<PharmacieDto?>("api/pharmacie", null);

    public async Task<string?> ModifierPharmacieAsync(PharmacieDto pharmacie)
    {
        var (_, erreur) = await EnvoyerCorpsAsync<PharmacieDto, object>(HttpMethod.Put, "api/admin/pharmacie", pharmacie);
        if (erreur is null) session.DefinirPharmacie(pharmacie.Nom);
        return erreur;
    }

    public Task<(ReglagesSeuilsDto?, string?)> SeuilsAsync() => GetAsync<ReglagesSeuilsDto?>("api/admin/seuils", null);
    public Task<(List<SeuilStockDto>, string?)> SeuilsStockManuelsAsync() => GetAsync<List<SeuilStockDto>>("api/admin/seuils-stock", []);
    public Task<(SeuilStockDto?, string?)> SeuilStockAsync(long produitId) => GetAsync<SeuilStockDto?>($"api/admin/seuils-stock/{produitId}", null);
    public async Task<(SeuilStockDto?, string?)> DefinirSeuilStockAsync(long produitId, int? seuil)
    {
        var r = await EnvoyerCorpsAsync<SeuilStockRequete, SeuilStockDto>(HttpMethod.Put, $"api/admin/seuils-stock/{produitId}", new(seuil));
        if (r.Item2 is null) AlertesModifiees?.Invoke();
        return r;
    }

    public async Task<(EnregistrementSeuilsDto?, string?)> EnregistrerSeuilsAsync(ReglagesSeuilsDto seuils)
    {
        var resultat = await EnvoyerCorpsAsync<ReglagesSeuilsDto, EnregistrementSeuilsDto>(HttpMethod.Put, "api/admin/seuils", seuils);
        if (resultat.Item2 is null) AlertesModifiees?.Invoke();
        return resultat;
    }

    public Task<(EtatDroitsDto?, string?)> DroitsAsync() => GetAsync<EtatDroitsDto?>("api/admin/droits", null);

    public async Task<string?> EnregistrerDroitsAsync(List<DroitsRoleDto> droits) =>
        (await EnvoyerCorpsAsync<List<DroitsRoleDto>, object>(HttpMethod.Put, "api/admin/droits", droits)).Item2;

    public Task<(IdentifiantDto?, string?)> CreerUtilisateurAsync(NouvelUtilisateurDto utilisateur) =>
        PostAsync<NouvelUtilisateurDto, IdentifiantDto>("api/admin/utilisateurs", utilisateur);

    public async Task<string?> ModifierUtilisateurAsync(long id, ModificationUtilisateurDto modification) =>
        (await EnvoyerCorpsAsync<ModificationUtilisateurDto, object>(HttpMethod.Put, $"api/admin/utilisateurs/{id}", modification)).Item2;

    /// <summary>Don et remise d'un employé (titulaire seul).</summary>
    public async Task<string?> ReglerDonRemiseAsync(long id, DonRemiseEmployeDto reglage) =>
        (await EnvoyerCorpsAsync<DonRemiseEmployeDto, object>(HttpMethod.Put, $"api/admin/utilisateurs/{id}/don-remise", reglage)).Item2;

    public Task<(ParametresCaisseDto?, string?)> ParametresCaisseAsync() =>
        GetAsync<ParametresCaisseDto?>("api/admin/caisse", null);

    public async Task<string?> ModifierParametresCaisseAsync(ParametresCaisseDto parametres)
    {
        var (_, erreur) = await EnvoyerCorpsAsync<ParametresCaisseDto, object>(HttpMethod.Put, "api/admin/caisse", parametres);
        return erreur;
    }

    // ---- Notifications (titulaire) et sauvegardes ---------------------------------

    public Task<(ReglagesNotificationDto?, string?)> ReglagesNotificationAsync() => GetAsync<ReglagesNotificationDto?>("api/admin/notifications/reglages", null);
    public async Task<string?> ModifierReglagesNotificationAsync(ModificationReglagesNotificationDto r) =>
        (await EnvoyerCorpsAsync<ModificationReglagesNotificationDto, object>(HttpMethod.Put, "api/admin/notifications/reglages", r)).Item2;
    public Task<(List<DestinataireDto>, string?)> DestinatairesAsync() => GetAsync<List<DestinataireDto>>("api/admin/notifications/destinataires", []);
    public Task<(IdentifiantDto?, string?)> EnregistrerDestinataireAsync(long? id, SaisieDestinataireDto d) => id is null
        ? PostAsync<SaisieDestinataireDto, IdentifiantDto>("api/admin/notifications/destinataires", d)
        : EnvoyerCorpsAsync<SaisieDestinataireDto, IdentifiantDto>(HttpMethod.Put, $"api/admin/notifications/destinataires/{id}", d);
    public async Task<string?> ValiderDestinataireAsync(long id, bool valide) =>
        (await PostAsync<ValidationDestinataireDto, object>($"api/admin/notifications/destinataires/{id}/validation", new(valide))).Item2;
    public Task<(NombreDto?, string?)> EnvoyerTestAsync(long id) => PostAsync<object, NombreDto>($"api/admin/notifications/destinataires/{id}/test", new());
    /// <summary>« Vérifier » : code à 6 chiffres envoyé au destinataire.</summary>
    public Task<(NombreDto?, string?)> EnvoyerCodeVerificationAsync(long id) => PostAsync<object, NombreDto>($"api/admin/notifications/destinataires/{id}/code", new());
    public async Task<string?> VerifierCodeDestinataireAsync(long id, string code) =>
        (await PostAsync<CodeVerificationDto, object>($"api/admin/notifications/destinataires/{id}/verification", new(code))).Item2;
    public Task<(NombreDto?, string?)> EnvoyerRapportMaintenantAsync() => PostAsync<object, NombreDto>("api/admin/notifications/rapport", new());
    /// <summary>Alertes qui n'ont pas pu partir, non encore vues (bandeau de l'accueil du titulaire).</summary>
    public Task<(AlertesNonEnvoyeesDto?, string?)> AlertesNonEnvoyeesAsync() => GetAsync<AlertesNonEnvoyeesDto?>("api/admin/notifications/non-envoyees", null);
    public Task<(NombreDto?, string?)> MarquerAlertesNonEnvoyeesVuesAsync(long jusquA) =>
        PostAsync<AlertesVuesRequete, NombreDto>("api/admin/notifications/non-envoyees/vues", new(jusquA));
    /// <summary>File des messages, paginée ; filtres : type (objet), statut, période de création.</summary>
    public Task<(PageDto<MessageSortantDto>?, string?)> FileMessagesAsync(int page, int taille, string? type = null, string? statut = null, DateOnly? du = null, DateOnly? au = null) =>
        GetAsync<PageDto<MessageSortantDto>?>("api/admin/notifications/file?" + Requete(("type", type), ("statut", statut), ("du", du), ("au", au), ("page", page), ("taille", taille)), null);

    public Task<(EtatSauvegardesDto?, string?)> SauvegardesAsync() => GetAsync<EtatSauvegardesDto?>("api/admin/sauvegardes", null);
    public Task<(PageDto<EntreeSauvegardeDto>?, string?)> JournalSauvegardesAsync(string? statut, DateOnly? du, DateOnly? au, int page, int taille) =>
        GetAsync<PageDto<EntreeSauvegardeDto>?>("api/admin/sauvegardes/journal?" + Requete(("statut", statut), ("du", du), ("au", au), ("page", page), ("taille", taille)), null);
    public async Task<string?> ModifierReglagesSauvegardeAsync(ReglagesSauvegardeDto r) =>
        (await EnvoyerCorpsAsync<ReglagesSauvegardeDto, object>(HttpMethod.Put, "api/admin/sauvegardes/reglages", r)).Item2;
    public Task<(ResultatSauvegardeDto?, string?)> LancerSauvegardeAsync() => PostAsync<object, ResultatSauvegardeDto>("api/admin/sauvegardes/lancer", new());

    /// <summary>Paramètres de pagination et filtres facultatifs d'une liste, en chaîne de requête.</summary>
    private static string Requete(params (string Nom, object? Valeur)[] parametres) =>
        string.Join("&", parametres.Where(p => p.Valeur is not null && p.Valeur as string != "")
            .Select(p => $"{p.Nom}={Uri.EscapeDataString(p.Valeur switch { DateOnly d => d.ToString("yyyy-MM-dd"), var v => Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture)! })}"));

    /// <summary>Employés présents dans le journal : liste préremplie du filtre « Employé ».</summary>
    public Task<(List<string>, string?)> EmployesJournalAsync() => GetAsync<List<string>>("api/admin/journal/employes", []);

    public Task<(PageDto<EntreeJournalDto>?, string?)> JournalAsync(DateOnly? du, DateOnly? au, string? employe, int page, int taille, string? categorie = null) =>
        GetAsync<PageDto<EntreeJournalDto>?>("api/admin/journal?" + Requete(("du", du), ("au", au), ("employe", employe?.Trim()), ("categorie", categorie),
            ("page", page), ("taille", taille)), null);

    // ---- Alertes, stock, traçabilité -------------------------------------------

    public Task<(List<AlerteDto>, string?)> AlertesAsync() => GetAsync<List<AlerteDto>>("api/alertes", []);

    /// <summary>Alertes paginées et filtrées par le serveur (toutes, urgent, peremption, stock), avec les nombres des tuiles et des onglets.</summary>
    public Task<(AlertesPageDto?, string?)> AlertesPageAsync(string filtre, int page, int taille) =>
        GetAsync<AlertesPageDto?>($"api/alertes/page?filtre={Uri.EscapeDataString(filtre)}&page={page}&taille={taille}", null);
    public Task<(IndicateursAlertesDto?, string?)> IndicateursAlertesAsync() => GetAsync<IndicateursAlertesDto?>("api/alertes/indicateurs", null);
    public Task<(CompteDto?, string?)> AlertesUrgentesAsync() => GetAsync<CompteDto?>("api/alertes/urgentes", null);

    /// <summary>Recherche de la caisse : en stock d'abord, ruptures en bas avec leur remplaçant, équivalent moins cher.</summary>
    public Task<(List<ProduitComptoirDto>, string?)> RechercherAuComptoirAsync(string q) =>
        GetAsync<List<ProduitComptoirDto>>($"api/caisse/produits?q={Uri.EscapeDataString(q)}", []);
    public Task<(List<ProduitDto>, string?)> RechercherProduitsAsync(string q, bool inclureInactifs = false) =>
        GetAsync<List<ProduitDto>>($"api/produits?q={Uri.EscapeDataString(q)}{(inclureInactifs ? "&inactifs=true" : "")}", []);

    // ---- Inventaire tournant ----
    public Task<(TableauInventaireDto?, string?)> TableauInventaireAsync() => GetAsync<TableauInventaireDto?>("api/inventaires", null);
    public Task<(PageDto<InventaireResumeDto>?, string?)> HistoriqueInventairesAsync(string? rayon, string? statut, DateOnly? du, DateOnly? au, int page, int taille) =>
        GetAsync<PageDto<InventaireResumeDto>?>("api/inventaires/historique?" + Requete(("rayon", rayon), ("statut", statut), ("du", du), ("au", au), ("page", page), ("taille", taille)), null);
    public Task<(IdentifiantDto?, string?)> CommencerInventaireAsync(string rayon) => PostAsync<CommencerInventaireRequete, IdentifiantDto>("api/inventaires", new(rayon));
    /// <summary>Inventaire d'un produit (tous ses lots), depuis l'alerte « ancien lot délaissé » : titulaire seul.</summary>
    // ---- Écarts dans le temps et risques par employé (titulaire) ----
    public Task<(TableauRisquesDto?, string?)> RisquesAsync(DateOnly? du, DateOnly? au) =>
        GetAsync<TableauRisquesDto?>("api/surveillance/risques?" + Requete(("du", du), ("au", au)), null);

    // ---- Produits sensibles (titulaire) et leur comptage ----
    public Task<(IdentifiantDto?, string?)> CommencerInventaireSensiblesAsync() => PostAsync<object, IdentifiantDto>("api/inventaires/sensibles", new());
    public Task<(List<ProduitSensibleDto>, string?)> ProduitsSensiblesAsync() => GetAsync<List<ProduitSensibleDto>>("api/surveillance/sensibles", []);
    public async Task<string?> MarquerSensibleAsync(long produitId, bool sensible) =>
        (await EnvoyerCorpsAsync<MarquerSensibleRequete, object>(HttpMethod.Put, $"api/surveillance/sensibles/{produitId}", new(sensible))).Item2;

    // ---- Surveillance de la caisse ----
    /// <summary>Ligne retirée du panier ou tiroir ouvert : signalé au serveur (surveillance du titulaire).</summary>
    public async Task<string?> SignalerCaisseAsync(EvenementCaisseRequete e) => (await PostAsync<EvenementCaisseRequete, object>("api/caisse/evenements", e)).Item2;
    public Task<(List<AlerteSurveillanceDto>, string?)> SurveillanceAsync(bool traitees) =>
        GetAsync<List<AlerteSurveillanceDto>>($"api/surveillance?traitees={(traitees ? "true" : "false")}", []);
    public async Task<string?> TraiterSurveillanceAsync(long id, string? action) =>
        (await PostAsync<TraiterAlerteRequete, object>($"api/surveillance/{id}/traiter", new(action))).Item2;
    public Task<(ParametresSurveillanceDto?, string?)> ParametresSurveillanceAsync() => GetAsync<ParametresSurveillanceDto?>("api/surveillance/parametres", null);
    public async Task<string?> EnregistrerParametresSurveillanceAsync(ParametresSurveillanceDto p) =>
        (await EnvoyerCorpsAsync<ParametresSurveillanceDto, object>(HttpMethod.Put, "api/surveillance/parametres", p)).Item2;

    public Task<(IdentifiantDto?, string?)> CommencerInventaireProduitAsync(long produitId) =>
        PostAsync<object, IdentifiantDto>($"api/inventaires/produit/{produitId}", new());
    public Task<(InventaireDto?, string?)> InventaireAsync(long id) => GetAsync<InventaireDto?>($"api/inventaires/{id}", null);
    public async Task<string?> CompterLotAsync(long id, long ligneId, int compte, DateOnly? dateVerifiee) =>
        (await EnvoyerCorpsAsync<ComptageRequete, object>(HttpMethod.Put, $"api/inventaires/{id}/lignes/{ligneId}", new(compte, dateVerifiee))).Item2;
    public Task<(ScanInventaireDto?, string?)> ScannerInventaireAsync(long id, string code) => PostAsync<ScanInventaireRequete, ScanInventaireDto>($"api/inventaires/{id}/scan", new(code));
    public Task<(BilanInventaireDto?, string?)> TerminerInventaireAsync(long id) => PostAsync<object, BilanInventaireDto>($"api/inventaires/{id}/terminer", new());
    public async Task<string?> AnnulerInventaireAsync(long id) => (await PostAsync<object, object>($"api/inventaires/{id}/annuler", new())).Item2;
    public async Task<string?> DeciderEcartAsync(long ligneId, bool valider) => (await PostAsync<DecisionEcartRequete, object>($"api/inventaires/ecarts/{ligneId}", new(valider))).Item2;
    public async Task<string?> ParametresInventaireAsync(ParametresInventaireDto p) =>
        (await EnvoyerCorpsAsync<ParametresInventaireDto, object>(HttpMethod.Put, "api/inventaires/parametres", p)).Item2;
    /// <summary>TVA d'achat à reverser par motif de sortie sans vente (titulaire).</summary>
    public async Task<string?> TvaSortiesAsync(TvaSortiesDto t) =>
        (await EnvoyerCorpsAsync<TvaSortiesDto, object>(HttpMethod.Put, "api/rapports/tva-sorties", t)).Item2;

    public Task<(List<EmplacementDto>, string?)> EmplacementsAsync() => GetAsync<List<EmplacementDto>>("api/emplacements", []);
    public Task<(IdentifiantDto?, string?)> EnregistrerEmplacementAsync(long? id, SaisieEmplacementDto e) => id is null
        ? PostAsync<SaisieEmplacementDto, IdentifiantDto>("api/emplacements", e)
        : EnvoyerCorpsAsync<SaisieEmplacementDto, IdentifiantDto>(HttpMethod.Put, $"api/emplacements/{id}", e);
    public async Task<string?> RangerProduitAsync(long produitId, long? emplacementId, long? reserveId) =>
        (await EnvoyerCorpsAsync<RangementDto, object>(HttpMethod.Put, $"api/produits/{produitId}/emplacement", new(emplacementId, reserveId))).Item2;

    public Task<(ProduitEnregistreDto?, string?)> CreerProduitAsync(NouveauProduitDto p) => PostAsync<NouveauProduitDto, ProduitEnregistreDto>("api/produits", p);
    public Task<(List<string>, string?)> DciAsync(string q) => GetAsync<List<string>>($"api/dci?q={Uri.EscapeDataString(q)}", []);

    public Task<(List<ProduitAlternatifDto>, string?)> AlternativesAsync(long produitId) =>
        GetAsync<List<ProduitAlternatifDto>>($"api/produits/{produitId}/alternatives", []);

    public Task<(EtatStockDto?, string?)> StockAsync() => GetAsync<EtatStockDto?>("api/stock", null);

    /// <summary>Tableau de stock paginé et filtré par le serveur, avec les propositions pour la saisie de la recherche.</summary>
    public Task<(StockPageDto?, string?)> StockPageAsync(int page, int taille, string? recherche, string? filtre, string? rayon) =>
        GetAsync<StockPageDto?>($"api/stock/page?page={page}&taille={taille}"
            + (string.IsNullOrWhiteSpace(recherche) ? "" : $"&q={Uri.EscapeDataString(recherche)}")
            + (string.IsNullOrWhiteSpace(filtre) || filtre == "tous" ? "" : $"&filtre={Uri.EscapeDataString(filtre)}")
            + (string.IsNullOrWhiteSpace(rayon) ? "" : $"&rayon={Uri.EscapeDataString(rayon)}"), null);

    public Task<(EtatEtiquetageDto?, string?)> EtiquetageAsync() =>
        GetAsync<EtatEtiquetageDto?>("api/tracabilite/etiquetage", null);

    public Task<(ParcoursBoiteDto?, string?)> ParcoursBoiteAsync(string code) =>
        GetAsync<ParcoursBoiteDto?>($"api/tracabilite/boites/{Uri.EscapeDataString(code)}", null);

    /// <summary>Prévient l'en-tête (badge) quand une alerte est reportée ou traitée.</summary>
    public event Action? AlertesModifiees;

    public async Task<string?> ReporterAlerteAsync(long id, DateOnly jusquAu)
    {
        var (_, erreur) = await PostAsync<ReporterAlerteRequete, object>($"api/alertes/{id}/reporter", new(jusquAu));
        if (erreur is null) AlertesModifiees?.Invoke();
        return erreur;
    }

    public async Task<string?> TraiterAlerteAsync(long id, string? actionPrise)
    {
        var (_, erreur) = await PostAsync<TraiterAlerteRequete, object>($"api/alertes/{id}/traiter", new(actionPrise));
        if (erreur is null) AlertesModifiees?.Invoke();
        return erreur;
    }

    public Task<(RappelLotDto?, string?)> RappelerLotAsync(long produitId, string numeroLot, string motif) =>
        PostAsync<RappelLotRequete, RappelLotDto>("api/tracabilite/rappels", new(produitId, numeroLot, motif));

    /// <summary>État d'un lot (déjà bloqué ou non) et patients concernés, sans rien bloquer.</summary>
    public Task<(RappelLotDto?, string?)> ConsulterRappelLotAsync(long produitId, string numeroLot) =>
        GetAsync<RappelLotDto?>($"api/tracabilite/rappels?produit={produitId}&lot={Uri.EscapeDataString(numeroLot.Trim())}", null);

    /// <summary>Le 404 n'est pas une erreur ici : il signifie « aucun lot vendable » et liste les lots écartés.</summary>
    /// <summary>Boîte scannée : null si le code ne désigne pas de lot (code-barres du fabricant).</summary>
    public Task<(BoiteCaisseDto?, string?)> BoiteCaisseAsync(string code) => PostAsync<CodeRequete, BoiteCaisseDto>("api/caisse/boite", new(code));

    /// <param name="lotsExclus">Lots déjà au panier : le serveur renvoie le lot suivant dans l'ordre FEFO.</param>
    public async Task<ReponseLotFefo> LotFefoAsync(long produitId, IEnumerable<long>? lotsExclus = null)
    {
        try
        {
            var exclus = string.Join("&", (lotsExclus ?? []).Select(id => $"exclure={id}"));
            using var rep = await EnvoyerAsync($"api/produits/{produitId}/lot-fefo{(exclus.Length > 0 ? "?" + exclus : "")}");
            if (rep.StatusCode == HttpStatusCode.Unauthorized) return new(null, [], SessionExpiree());
            if (rep.StatusCode == HttpStatusCode.NotFound)
            {
                var aucun = await rep.Content.ReadFromJsonAsync<AucunLotDto>();
                return new(null, aucun?.Ecartes ?? [], null);
            }
            if (!rep.IsSuccessStatusCode) return new(null, [], await MessageAsync(rep));
            var lot = (await rep.Content.ReadFromJsonAsync<LotFefoDto>())!;
            return new(lot, lot.Ecartes, null);
        }
        catch (Exception ex) when (EstEchecAppel(ex))
        {
            return new(null, [], MessageEchec(ex));
        }
    }

    // ---- Achats ------------------------------------------------------------------

    /// <summary>Barre d'état : le serveur répond-il ? (route publique /api/sante, 4 secondes au plus)</summary>
    public async Task<bool> ServeurJoignableAsync()
    {
        try
        {
            using var delai = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            using var rep = await http.GetAsync("api/sante", delai.Token);
            return rep.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is OperationCanceledException || EstEchecAppel(ex))
        {
            return false;
        }
    }

    public Task<(EtatPosteDto?, string?)> EtatPosteAsync() => GetAsync<EtatPosteDto?>("api/etat-poste", null);

    /// <summary>Fiche contact d'un tiers : type patient, grossiste, organisme, client ou employe.</summary>
    public Task<(FicheContactDto?, string?)> ContactAsync(string type, long id) => GetAsync<FicheContactDto?>($"api/contacts/{type}/{id}", null);
    public async Task<string?> ModifierCoordonneesContactAsync(string type, long id, CoordonneesContactDto c) =>
        (await EnvoyerCorpsAsync<CoordonneesContactDto, object>(HttpMethod.Put, $"api/contacts/{type}/{id}/coordonnees", c)).Item2;

    public Task<(IdentifiantDto?, string?)> EnregistrerGrossisteAsync(FicheGrossisteDto f) => PostAsync<FicheGrossisteDto, IdentifiantDto>("api/achats/fournisseurs", f);
    public Task<(List<FournisseurDto>, string?)> FournisseursAsync() => GetAsync<List<FournisseurDto>>("api/achats/fournisseurs", []);
    public Task<(PageDto<CommandeAchatDto>?, string?)> CommandesAchatAsync(int page = 1, int taille = 10, long? fournisseurId = null, string? statut = null,
        DateOnly? du = null, DateOnly? au = null) =>
        GetAsync<PageDto<CommandeAchatDto>?>("api/achats/commandes?" + Requete(("fournisseurId", fournisseurId), ("statut", statut), ("du", du), ("au", au),
            ("page", page), ("taille", taille)), null);
    public Task<(CommandeAchatDetailDto?, string?)> CommandeAchatAsync(long id) => GetAsync<CommandeAchatDetailDto?>($"api/achats/commandes/{id}", null);
    public Task<(List<SuggestionCommandeDto>, string?)> SuggestionsCommandeAsync() => GetAsync<List<SuggestionCommandeDto>>("api/achats/suggestions", []);
    public Task<(SuggestionsCommandePageDto?, string?)> SuggestionsCommandePageAsync(string? recherche, string? statut, long? grossiste, int page, int taille) =>
        GetAsync<SuggestionsCommandePageDto?>($"api/achats/suggestions/page?page={page}&taille={taille}"
            + (string.IsNullOrWhiteSpace(recherche) ? "" : $"&recherche={Uri.EscapeDataString(recherche.Trim())}")
            + (string.IsNullOrEmpty(statut) ? "" : $"&statut={Uri.EscapeDataString(statut)}") + (grossiste is { } g ? $"&grossiste={g}" : ""), null);

    public Task<(IdentifiantDto?, string?)> AjouterACommandeAsync(long fournisseurId, long produitId, int quantite, decimal? prixHt) =>
        PostAsync<AjoutCommandeRequete, IdentifiantDto>("api/achats/commandes/lignes", new(fournisseurId, produitId, quantite, prixHt));

    public async Task<string?> ModifierLigneCommandeAsync(long commandeId, long ligneId, int quantite, decimal prixHt) =>
        (await EnvoyerCorpsAsync<ModificationLigneCommandeRequete, object>(HttpMethod.Put, $"api/achats/commandes/{commandeId}/lignes/{ligneId}", new(quantite, prixHt))).Item2;

    public Task<(byte[]?, string?, string?)> EmettreCommandeAsync(long id) => FichierAsync(HttpMethod.Post, $"api/achats/commandes/{id}/emettre", $"commande_{id}.pdf");
    public Task<(byte[]?, string?, string?)> BonCommandeAsync(long id) => FichierAsync(HttpMethod.Get, $"api/achats/commandes/{id}/pdf", $"commande_{id}.pdf");
    public async Task<string?> AnnulerCommandeAsync(long id) => (await PostAsync<object, object>($"api/achats/commandes/{id}/annuler", new())).Item2;

    public Task<(List<InfoAchatProduitDto>, string?)> InfosAchatAsync(IEnumerable<long> produitIds) =>
        GetAsync<List<InfoAchatProduitDto>>("api/achats/produits/infos?ids=" + string.Join(",", produitIds.Distinct()), []);

    public Task<(DocumentReceptionDto?, string?)> DocumentReceptionAsync(long fournisseurId, string numero) =>
        GetAsync<DocumentReceptionDto?>("api/achats/receptions/document?" + Requete(("fournisseurId", fournisseurId), ("numero", numero)), null);

    public Task<(PageDto<ReceptionResumeDto>?, string?)> ReceptionsAsync(int page = 1, int taille = 10, long? fournisseurId = null, DateOnly? du = null,
        DateOnly? au = null, string? q = null) =>
        GetAsync<PageDto<ReceptionResumeDto>?>("api/achats/receptions?" + Requete(("fournisseurId", fournisseurId), ("du", du), ("au", au), ("q", q?.Trim()),
            ("page", page), ("taille", taille)), null);
    public Task<(BoiteLueDto?, string?)> LireDataMatrixAsync(string code) => PostAsync<DataMatrixRequete, BoiteLueDto>("api/achats/datamatrix", new(code));
    public Task<(List<ConflitReceptionDto>?, string?)> ConflitsReceptionAsync(ReceptionRequete reception) =>
        PostAsync<ReceptionRequete, List<ConflitReceptionDto>>("api/achats/receptions/conflits", reception);
    public Task<(ReceptionEnregistreeDto?, string?)> ReceptionnerAsync(ReceptionRequete reception) =>
        PostAsync<ReceptionRequete, ReceptionEnregistreeDto>("api/achats/receptions", reception);

    // ---- Volet Chifa repris par son QR code ----
    public Task<(VoletChifaDto?, string?)> LireVoletChifaAsync(string texte) => PostAsync<LireVoletChifaRequete, VoletChifaDto>("api/caisse/volet-chifa", new(texte));
    public async Task<string?> AssocierCodeChifaAsync(string code, long produitId) =>
        (await PostAsync<AssocierCodeChifaRequete, object>("api/caisse/codes-chifa", new(code, produitId))).Item2;

    // ---- Réception à l'aveugle : comptage, facture comparée, écarts ----
    public async Task<bool> ReceptionAveugleAsync() => (await GetAsync<ModeReceptionDto?>("api/achats/receptions/mode", null)).Item1?.Aveugle ?? false;
    public Task<(IdentifiantDto?, string?)> CompterLivraisonAsync(CompterLivraisonRequete r) =>
        PostAsync<CompterLivraisonRequete, IdentifiantDto>("api/achats/receptions/comptages", r);
    public Task<(List<ComptageResumeDto>, string?)> ComptagesReceptionAsync() => GetAsync<List<ComptageResumeDto>>("api/achats/receptions/comptages", []);
    public Task<(ComptageAFacturerDto?, string?)> ComptageAFacturerAsync(long id) => GetAsync<ComptageAFacturerDto?>($"api/achats/receptions/comptages/{id}", null);
    public Task<(ResultatFacturationDto?, string?)> FacturerComptageAsync(long id, ReceptionRequete reception) =>
        PostAsync<ReceptionRequete, ResultatFacturationDto>($"api/achats/receptions/comptages/{id}/facturer", reception);
    public Task<(NombreDto?, string?)> RecompterLivraisonAsync(long id, IReadOnlyList<LigneCompteeDto> lignes) =>
        PostAsync<RecompterLivraisonRequete, NombreDto>($"api/achats/receptions/comptages/{id}/recompter", new(lignes));
    public async Task<string?> AnnulerComptageAsync(long id) => (await PostAsync<object, object>($"api/achats/receptions/comptages/{id}/annuler", new())).Item2;
    public Task<(ReceptionEnregistreeDto?, string?)> DeciderEcartReceptionAsync(long id, bool valider) =>
        PostAsync<DecisionEcartReceptionRequete, ReceptionEnregistreeDto>($"api/achats/receptions/comptages/{id}/decision", new(valider));

    public Task<(PageDto<RetourDto>?, string?)> RetoursAsync(int page = 1, int taille = 10, long? fournisseurId = null, string? statut = null,
        DateOnly? du = null, DateOnly? au = null) =>
        GetAsync<PageDto<RetourDto>?>("api/achats/retours?" + Requete(("fournisseurId", fournisseurId), ("statut", statut), ("du", du), ("au", au),
            ("page", page), ("taille", taille)), null);
    public Task<(List<LotRetournableDto>, string?)> LotsRetournablesAsync(long produitId, string? numeroLot) =>
        GetAsync<List<LotRetournableDto>>($"api/achats/lots?produitId={produitId}" + (numeroLot is null ? "" : "&numeroLot=" + Uri.EscapeDataString(numeroLot)), []);
    public Task<(LotRetournableDto?, string?)> LotRetournableAsync(long lotId) => GetAsync<LotRetournableDto?>($"api/achats/lots/{lotId}/retour", null);
    public Task<(IdentifiantDto?, string?)> PreparerRetourAsync(PreparationRetourRequete retour) =>
        PostAsync<PreparationRetourRequete, IdentifiantDto>("api/achats/retours", retour);
    public Task<(byte[]?, string?, string?)> EnvoyerRetourAsync(long id) => FichierAsync(HttpMethod.Post, $"api/achats/retours/{id}/envoyer", $"retour_{id}.pdf");
    public Task<(byte[]?, string?, string?)> BonRetourAsync(long id) => FichierAsync(HttpMethod.Get, $"api/achats/retours/{id}/pdf", $"retour_{id}.pdf");

    // ---- Reprise de données (super admin ; extraction, chargement et annulation validés par code) ----
    public Task<(List<SourceRepriseDto>?, string?)> SourcesRepriseAsync(string? dossier) =>
        PostAsync<DossierRepriseRequete, List<SourceRepriseDto>>("api/admin/reprise/sources", new(dossier));
    public Task<(List<string>, string?)> PilotesOdbcAsync() => GetAsync<List<string>>("api/admin/reprise/pilotes", []);
    public Task<(ScanRepriseDto?, string?)> ScannerRepriseAsync(string chaine) =>
        PostAsync<ConnexionRepriseRequete, ScanRepriseDto>("api/admin/reprise/scan", new(chaine));
    public Task<(ExtractionRepriseDto?, string?)> ExtraireRepriseAsync(ExtractionRepriseRequete requete) =>
        PostAsync<ExtractionRepriseRequete, ExtractionRepriseDto>("api/admin/reprise/extraire", requete);
    public Task<(List<SessionRepriseDto>, string?)> SessionsRepriseAsync() => GetAsync<List<SessionRepriseDto>>("api/admin/reprise/sessions", []);
    public Task<(EtatRepriseDto?, string?)> EtatRepriseAsync(long id) => GetAsync<EtatRepriseDto?>($"api/admin/reprise/sessions/{id}", null);
    public Task<(NombreDto?, string?)> DeciderProduitRepriseAsync(long id, DecisionProduitRepriseRequete d) =>
        PostAsync<DecisionProduitRepriseRequete, NombreDto>($"api/admin/reprise/sessions/{id}/produits", d);
    public Task<(EtatRepriseDto?, string?)> ChargerRepriseAsync(long id) => PostAsync<object, EtatRepriseDto>($"api/admin/reprise/sessions/{id}/charger", new());
    public async Task<string?> AnnulerRepriseAsync(long id) => (await PostAsync<object, object>($"api/admin/reprise/sessions/{id}/annuler", new())).Item2;

    // ---- Rapports ----
    /// <summary>Période « dates » : du et au choisis dans le calendrier ; sinon jour, semaine ou mois autour de date.</summary>
    private static string FiltreRapport(string periode, DateOnly date, DateOnly du, DateOnly au) =>
        periode == "dates" ? $"periode=dates&du={du:yyyy-MM-dd}&au={au:yyyy-MM-dd}" : $"periode={periode}&date={date:yyyy-MM-dd}";
    public Task<(RapportActiviteDto?, string?)> RapportAsync(string periode, DateOnly date, DateOnly du, DateOnly au) =>
        GetAsync<RapportActiviteDto?>($"api/rapports?{FiltreRapport(periode, date, du, au)}", null);

    /// <summary>Clôtures de la période, paginées et filtrées par le serveur (employé, poste, avec un écart seulement).</summary>
    public Task<(CloturesPageDto?, string?)> CloturesRapportAsync(string periode, DateOnly date, DateOnly du, DateOnly au, string? employe, string? poste,
        bool avecEcart, int page, int taille) =>
        GetAsync<CloturesPageDto?>($"api/rapports/clotures?{FiltreRapport(periode, date, du, au)}&page={page}&taille={taille}"
            + (string.IsNullOrWhiteSpace(employe) ? "" : $"&employe={Uri.EscapeDataString(employe)}")
            + (string.IsNullOrWhiteSpace(poste) ? "" : $"&poste={Uri.EscapeDataString(poste)}") + (avecEcart ? "&ecart=true" : ""), null);
    public Task<(byte[]?, string?, string?)> ExporterRapportAsync(string periode, DateOnly date, DateOnly du, DateOnly au, string format) =>
        // « csv » ou « excel » : fichier CSV ; « pdf » : PDF (mêmes formats que le serveur)
        FichierAsync(HttpMethod.Get, $"api/rapports/export?{FiltreRapport(periode, date, du, au)}&format={format}", $"rapport.{(format is "excel" or "csv" ? "csv" : "pdf")}");
    public async Task<string?> EnregistrerAvoirAsync(long id, decimal montant) => (await PostAsync<AvoirRequete, object>($"api/achats/retours/{id}/avoir", new(montant))).Item2;
    public async Task<string?> RefuserRetourAsync(long id, string? motif) => (await PostAsync<RefusRetourRequete, object>($"api/achats/retours/{id}/refuser", new(motif))).Item2;

    public Task<(List<LotAEtiqueterDto>, string?)> LotsAEtiqueterAsync() => GetAsync<List<LotAEtiqueterDto>>("api/achats/etiquettes/lots", []);
    public Task<(PageDto<LotAEtiqueterDto>?, string?)> LotsAEtiqueterPageAsync(string? recherche, DateOnly? perimeAvant, long? lot, int page, int taille) =>
        GetAsync<PageDto<LotAEtiqueterDto>?>($"api/achats/etiquettes/lots/page?page={page}&taille={taille}"
            + (string.IsNullOrWhiteSpace(recherche) ? "" : $"&recherche={Uri.EscapeDataString(recherche.Trim())}")
            + (perimeAvant is { } d ? $"&perimeAvant={d:yyyy-MM-dd}" : "") + (lot is { } l ? $"&lot={l}" : ""), null);
    public Task<(List<ScanReceptionDto>, string?)> ScansMobileAsync() => GetAsync<List<ScanReceptionDto>>("api/mobile/reception/scans", []);
    public Task<(NombreDto?, string?)> RemettreScansMobileAsync(IReadOnlyList<long> ids) =>
        PostAsync<IdsRequete, NombreDto>("api/mobile/reception/scans/remettre", new(ids));
    public Task<(List<LigneScanneeDto>?, string?)> ImporterScansMobileAsync() =>
        PostAsync<object, List<LigneScanneeDto>>("api/mobile/reception/scans/importer", new());
    public Task<(EtiquettesDto?, string?)> EtiquettesReceptionAsync(long receptionId) =>
        GetAsync<EtiquettesDto?>($"api/achats/receptions/{receptionId}/etiquettes", null);
    public Task<(EtiquettesDto?, string?)> CreerEtiquettesAsync(long lotId, int nombre) =>
        PostAsync<EtiquettesRequete, EtiquettesDto>("api/achats/etiquettes", new(lotId, nombre));

    /// <summary>Document PDF renvoyé par le serveur : contenu, nom de fichier proposé, ou erreur.</summary>
    private async Task<(byte[]?, string?, string?)> FichierAsync(HttpMethod methode, string url, string nomParDefaut)
    {
        try
        {
            using var rep = await EnvoyerAutoriseAsync(() => new HttpRequestMessage(methode, url), proposer: methode != HttpMethod.Get);
            if (rep.StatusCode == HttpStatusCode.Unauthorized) return (null, null, SessionExpiree());
            if (!rep.IsSuccessStatusCode) return (null, null, await MessageAsync(rep));
            var nom = rep.Content.Headers.ContentDisposition?.FileNameStar
                      ?? rep.Content.Headers.ContentDisposition?.FileName?.Trim('"') ?? nomParDefaut;
            return (await rep.Content.ReadAsByteArrayAsync(), nom, null);
        }
        catch (Exception ex) when (EstEchecAppel(ex))
        {
            return (null, null, MessageEchec(ex));
        }
    }

    private async Task<(T, string?)> GetAsync<T>(string url, T defaut)
    {
        try
        {
            using var rep = await EnvoyerAsync(url);
            if (rep.StatusCode == HttpStatusCode.Unauthorized) return (defaut, SessionExpiree());
            if (!rep.IsSuccessStatusCode) return (defaut, await MessageAsync(rep));
            return ((await rep.Content.ReadFromJsonAsync<T>())!, null);
        }
        catch (Exception ex) when (EstEchecAppel(ex))
        {
            return (defaut, MessageEchec(ex));
        }
    }

    /// <summary>POST JSON. Une réponse 204 (sans contenu) renvoie default.</summary>
    private Task<(TRep?, string?)> PostAsync<TReq, TRep>(string url, TReq corps) =>
        EnvoyerCorpsAsync<TReq, TRep>(HttpMethod.Post, url, corps);

    private async Task<(TRep?, string?)> EnvoyerCorpsAsync<TReq, TRep>(HttpMethod methode, string url, TReq corps)
    {
        try
        {
            using var rep = await EnvoyerAutoriseAsync(() => new HttpRequestMessage(methode, url) { Content = JsonContent.Create(corps) });
            if (rep.StatusCode == HttpStatusCode.Unauthorized) return (default, SessionExpiree());
            if (!rep.IsSuccessStatusCode) return (default, await MessageAsync(rep));
            if (rep.StatusCode == HttpStatusCode.NoContent) return (default, null);
            return (await rep.Content.ReadFromJsonAsync<TRep>(), null);
        }
        catch (Exception ex) when (EstEchecAppel(ex))
        {
            return (default, MessageEchec(ex));
        }
    }

    // Lecture : pas de fenêtre « Faire autoriser » (un écran interdit est refusé par la mise en page, pas par une demande de code)
    private Task<HttpResponseMessage> EnvoyerAsync(string url) => EnvoyerAutoriseAsync(() => new HttpRequestMessage(HttpMethod.Get, url), proposer: false);

    /// <summary>
    /// Envoie la requête ; si le serveur la refuse faute de droit en proposant l'autorisation sur place, la fenêtre « Faire autoriser »
    /// demande l'employé habilité et son code, puis la requête est rejouée avec ce code (nouvel essai si le code est faux).
    /// </summary>
    private async Task<HttpResponseMessage> EnvoyerAutoriseAsync(Func<HttpRequestMessage> creer, bool proposer = true)
    {
        (string Identifiant, string Code)? accord = null;
        // Droits présentés au valideur : le serveur n'accorde que ceux-là (jamais d'autres contrôles de la même opération)
        var droits = new List<string>();
        var validation = validationEnAttente;
        validationEnAttente = null;
        for (var essai = 0; ; essai++)
        {
            var req = creer();
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.Jeton);
            if (validation is not null) req.Headers.Add("X-Pharma-Validation", validation);
            if (accord is { } a)
            {
                req.Headers.Add("X-Pharma-Autorise-Par", Uri.EscapeDataString(a.Identifiant));
                req.Headers.Add("X-Pharma-Autorise-Code", a.Code);
                req.Headers.Add("X-Pharma-Autorise-Droits", string.Join(",", droits));
            }
            var rep = await http.SendAsync(req);
            if (rep.StatusCode != HttpStatusCode.Forbidden || autorisations is null || !proposer || essai >= 3) return rep;
            var corps = await rep.Content.ReadAsStringAsync();
            var (message, autorisable, permission, seuil) = LireRefus(corps);
            HttpResponseMessage Garder() { var n = new HttpResponseMessage(rep.StatusCode) { Content = new StringContent(corps, System.Text.Encoding.UTF8, "application/json") }; rep.Dispose(); return n; }
            if (!autorisable) return Garder();
            accord = await autorisations.DemanderAsync(message, permission, seuil);
            if (accord is null) return Garder();
            var demande = permission ?? "role.titulaire";
            if (!droits.Contains(demande)) droits.Add(demande);
            rep.Dispose();
        }
    }

    /// <returns>Seuil : remise refusée (pourcentage demandé), pour ne proposer que les employés dont le maximum la couvre.</returns>
    private static (string Message, bool Autorisable, string? Permission, decimal? Seuil) LireRefus(string corps)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(corps);
            var racine = doc.RootElement;
            string? Texte(string nom) => racine.TryGetProperty(nom, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String ? v.GetString() : null;
            var autorisable = racine.TryGetProperty("autorisable", out var a) && a.ValueKind == System.Text.Json.JsonValueKind.True;
            decimal? seuil = racine.TryGetProperty("seuil", out var s) && s.ValueKind == System.Text.Json.JsonValueKind.Number && s.TryGetDecimal(out var d) ? d : null;
            return (Texte("message") ?? Texte("detail") ?? "Action non autorisée.", autorisable, Texte("permission"), seuil);
        }
        catch (System.Text.Json.JsonException) { return ("Action non autorisée.", false, null, null); }
    }

    private string SessionExpiree()
    {
        session.Fermer("Session expirée : reconnectez-vous.");
        return "Session expirée : reconnectez-vous.";
    }

    /// <summary>
    /// Le serveur a désigné le champ en cause d'un refus (ErreurDto.Champ) : la mise en page le fait passer en rouge
    /// (data-champ), avec le message dans une bulle à côté.
    /// </summary>
    public event Action<string, string>? ChampEnErreur;

    /// <summary>Contrôle fait sur le poste : désigne le champ en cause comme le ferait un refus du serveur (champ data-champ).</summary>
    public string SignalerChamp(string champ, string message)
    {
        ChampEnErreur?.Invoke(champ, message);
        return message;
    }

    private async Task<string> MessageAsync(HttpResponseMessage rep)
    {
        try
        {
            // ErreurDto { message, champ } ou ProblemDetails { detail }
            using var doc = System.Text.Json.JsonDocument.Parse(await rep.Content.ReadAsStringAsync());
            foreach (var nom in new[] { "message", "detail", "title" })
                if (doc.RootElement.TryGetProperty(nom, out var v) && v.GetString() is { Length: > 0 } texte)
                {
                    if (doc.RootElement.TryGetProperty("champ", out var c) && c.ValueKind == System.Text.Json.JsonValueKind.String
                        && c.GetString() is { Length: > 0 } champ)
                        ChampEnErreur?.Invoke(champ, texte);
                    return texte;
                }
        }
        catch { /* corps vide ou non JSON */ }
        return $"Erreur {(int)rep.StatusCode}.";
    }
}
