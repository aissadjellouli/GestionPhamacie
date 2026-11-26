using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Pharma.Shared;

namespace Pharma.Desktop.Services;

/// <summary>Session de l'employé connecté sur ce poste.</summary>
public sealed class SessionPoste : ISessionVigie
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

    /// <summary>Caisse du poste ouverte, relevée ou clôturée : la barre d'état relit son état tout de suite (2026-11-15).</summary>
    public event Action? CaisseChangee;
    public void SignalerCaisse() => CaisseChangee?.Invoke();

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

    /// <summary>
    /// Préparations faites avant « Changer d'utilisateur », session encore ouverte et réponse du serveur attendue (la Caisse y met
    /// son panier en attente). Chacune renvoie null quand tout est confié au serveur, sinon le message à montrer : la session
    /// n'est alors pas fermée et l'employé choisit « Réessayer » ou « Rester sur la vente » (aucun panier perdu en silence).
    /// </summary>
    private readonly List<Func<Task<string?>>> preparationsChangement = [];
    public void AvantChangementUtilisateur(Func<Task<string?>> preparation) => preparationsChangement.Add(preparation);
    public void OublierAvantChangement(Func<Task<string?>> preparation) => preparationsChangement.Remove(preparation);

    /// <summary>Null : le changement d'utilisateur peut se faire ; sinon le premier échec (message bloquant, rien n'est fermé).</summary>
    public async Task<string?> PreparerChangementUtilisateurAsync()
    {
        foreach (var preparation in preparationsChangement.ToList())
            if (await preparation() is { } echec) return echec;
        return null;
    }

    /// <summary>Vente mise en attente au changement d'utilisateur, reprise par le prochain employé connecté sur ce poste.</summary>
    private (long Id, string LaisseePar)? venteAReprendre;
    public void RetenirVenteAReprendre(long id, string laisseePar) => venteAReprendre = (id, laisseePar);
    public bool AVenteAReprendre => venteAReprendre is not null;
    public (long Id, string LaisseePar)? PrendreVenteAReprendre() { var v = venteAReprendre; venteAReprendre = null; return v; }

    /// <summary>Relève de caisse faite par cet employé : « Changer d'utilisateur » ne la repropose pas.</summary>
    public bool ReleveFaite { get; private set; }
    public void MarquerReleveFaite() => ReleveFaite = true;

    void ISessionVigie.FermerPourRepli(string motif) => Fermer(motif);

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
public sealed class ApiClient(HttpClient http, SessionPoste session, DemandesAutorisation? autorisations = null, TachesLaissees? tachesLaissees = null,
    AdresseApiCourante? adresseCourante = null, IBasculePoste? bascule = null, IdentitePoste? identite = null, HorlogePoste? horlogeServeur = null)
{
    /// <summary>Serveur injoignable : l'écran peut le reconnaître (ex. encaissement mis en suspens jusqu'à la bascule).</summary>
    public const string Injoignable = "Serveur de la pharmacie injoignable : vérifiez que le PC serveur est allumé.";

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

    /// <summary>Mot de passe ressaisi : le serveur envoie le même code par SMS au téléphone et par e-mail à l'adresse du super admin.</summary>
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

    // Autorisation demandée d'avance (« Faire autoriser » d'un écran, ex. prix à la création d'un produit) : jointe à la requête
    // suivante seulement (X-Pharma-Autorise-Par), puis oubliée ; un code faux rouvre la fenêtre comme d'habitude
    private (string Identifiant, string Code, string Droit)? accordEnAttente;

    public void AutoriserProchaineAction(string identifiant, string code, string droit) => accordEnAttente = (identifiant, code, droit);

    // Détail joint à la fenêtre « Faire autoriser » de l'écriture suivante seulement, puis oublié ; montré seulement si le refus
    // porte sur le droit du détail (DetailAutorisationDto.Permission ; aucun : tout refus)
    private DetailAutorisationDto? detailEnAttente;

    public void DetaillerProchaineAutorisation(DetailAutorisationDto? detail) => detailEnAttente = detail;

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
    public Task<(PageDto<OrganismeFicheDto>?, string?)> OrganismesFichesPageAsync(string? etat, string? texte, int page, int taille) =>
        GetAsync<PageDto<OrganismeFicheDto>?>("api/admin/organismes/page?" + Requete(("etat", etat), ("q", texte), ("page", page), ("taille", taille)), null);
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

    /// <summary>Remboursement anticipé d'une partie du capital (script 170).</summary>
    public async Task<string?> RembourserPretParAnticipationAsync(long pretId, RemboursementAnticipeRequete r) =>
        (await PostAsync<RemboursementAnticipeRequete, IdentifiantDto>($"api/financements/prets/{pretId}/remboursement-anticipe", r)).Item2;
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
    /// <summary>Ticket en PDF (vue à l'écran quand l'imprimante de tickets enregistre un fichier).</summary>
    public Task<(byte[]?, string?, string?)> TicketPdfAsync(long venteId, decimal? rendu, int largeurMm) =>
        FichierAsync(HttpMethod.Get, $"api/caisse/ventes/{venteId}/ticket.pdf?largeur={largeurMm}{(rendu is { } r ? $"&rendu={r.ToString(System.Globalization.CultureInfo.InvariantCulture)}" : "")}", "ticket.pdf");
    /// <summary>Reçu de règlement de crédit en PDF, format rouleau (2026-11-15).</summary>
    public Task<(byte[]?, string?, string?)> RecuCreditPdfAsync(string numero, int largeurMm) =>
        FichierAsync(HttpMethod.Get, $"api/caisse/credits/recus/{Uri.EscapeDataString(numero)}/recu.pdf?largeur={largeurMm}", "recu.pdf");
    public Task<(byte[]?, string?, string?)> ReleveCreditAsync(long patientId) => FichierAsync(HttpMethod.Get, $"api/caisse/credits/{patientId}/releve", "releve_credit.pdf");
    public async Task<string?> CorrigerCreditAsync(long patientId, decimal nouvelleDette, string? motif) =>
        (await PostAsync<CorrectionCreditRequete, object>($"api/caisse/credits/{patientId}/correction", new(nouvelleDette, motif))).Item2;
    public Task<(RecuCreditDto?, string?)> ReglerCreditAsync(long patientId, ReglementCreditRequete r) =>
        PostAsync<ReglementCreditRequete, RecuCreditDto>($"api/caisse/credits/{patientId}/reglement", r);

    // ---- Avoir du patient (script 106) ----
    public Task<(AvoirPatientDto?, string?)> AvoirPatientAsync(long patientId, int page = 1, int taille = 10) =>
        GetAsync<AvoirPatientDto?>($"api/patients/{patientId}/avoir?page={page}&taille={taille}", null);
    public Task<(OuvertureTiroirDto?, string?)> RembourserAvoirAsync(long patientId, decimal montant, string? motif) =>
        PostAsync<RemboursementAvoirRequete, OuvertureTiroirDto>($"api/patients/{patientId}/avoir/remboursement", new(montant, motif));

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

    public Task<(byte[]?, string?, string?)> ExporterRegistreAsync(long? produit, DateOnly du, DateOnly au, string format,
        long? patient = null, string? prescripteur = null, long? medecin = null) =>
        FichierAsync(HttpMethod.Get, $"api/registre/export?format={format}&{FiltreRegistre(produit, du, au)}"
            + (patient is { } pa ? $"&patient={pa}" : "")
            + (medecin is { } me ? $"&medecin={me}" : "")
            + (string.IsNullOrWhiteSpace(prescripteur) ? "" : $"&prescripteur={Uri.EscapeDataString(prescripteur.Trim())}"),
            $"registre_psychotropes.{format}");

    // ---- Registre renforcé (lot 8) ---------------------------------------------

    public async Task<string?> AnnulerEcritureRegistreAsync(long numero, string motif) =>
        (await PostAsync<AnnulationEcritureRequete, object>($"api/registre/inscriptions/{numero}/annulation", new(motif))).Item2;

    public Task<(PageDto<ForcageAlerteDto>?, string?)> ForcagesPsychotropesAsync(DateOnly du, DateOnly au, string? code, int page, int taille) =>
        GetAsync<PageDto<ForcageAlerteDto>?>($"api/registre/forcages?du={du:yyyy-MM-dd}&au={au:yyyy-MM-dd}&page={page}&taille={taille}"
            + (string.IsNullOrEmpty(code) ? "" : $"&code={Uri.EscapeDataString(code)}"), null);

    public Task<(ControleDelivranceDto?, string?)> ControlerDelivrancePsychotropeAsync(DelivrancePsychotropeDto d) =>
        PostAsync<DelivrancePsychotropeDto, ControleDelivranceDto>("api/psychotropes/controle", d);

    public async Task<string?> ForcerAlertesPsychotropesAsync(DelivrancePsychotropeDto d, string motif) =>
        (await PostAsync<ForcageRequete, IdentifiantDto>("api/psychotropes/forcer", new(d, motif))).Item2;

    public Task<(List<RegleAlertePsychotropeDto>, string?)> ReglesAlertesPsychotropesAsync() =>
        GetAsync<List<RegleAlertePsychotropeDto>>("api/psychotropes/regles", []);

    public async Task<string?> EnregistrerReglesAlertesPsychotropesAsync(List<RegleAlertePsychotropeDto> regles) =>
        (await EnvoyerCorpsAsync<List<RegleAlertePsychotropeDto>, object>(HttpMethod.Put, "api/psychotropes/regles", regles)).Item2;

    // Zone de l'officine et référentiel des wilayas (script 118)
    public Task<(ZoneOfficineDto?, string?)> ZoneOfficineAsync() => GetAsync<ZoneOfficineDto?>("api/psychotropes/zone", null);

    public async Task<string?> EnregistrerZoneOfficineAsync(ZoneOfficineDto zone) =>
        (await EnvoyerCorpsAsync<ZoneOfficineDto, object>(HttpMethod.Put, "api/psychotropes/zone", zone)).Item2;

    public Task<(List<WilayaDto>, string?)> WilayasAsync() => GetAsync<List<WilayaDto>>("api/wilayas", []);

    public Task<(List<ClasseSuivieDto>, string?)> ClassesSuiviesAsync() => GetAsync<List<ClasseSuivieDto>>("api/psychotropes/classes", []);

    public Task<(List<DciClasseDto>, string?)> DciClasseAsync(string classe) =>
        GetAsync<List<DciClasseDto>>($"api/psychotropes/classes/{Uri.EscapeDataString(classe)}/dci", []);

    public Task<(List<DciClasseDto>, string?)> ChercherDciClasseAsync(string q) =>
        GetAsync<List<DciClasseDto>>($"api/psychotropes/dci?q={Uri.EscapeDataString(q)}", []);

    public async Task<string?> ModifierDciClasseAsync(string classe, long dciId, bool ajouter) =>
        (await EnvoyerCorpsAsync<object, object>(ajouter ? HttpMethod.Put : HttpMethod.Delete,
            $"api/psychotropes/classes/{Uri.EscapeDataString(classe)}/dci/{dciId}", new())).Item2;

    public Task<(List<LigneSuiviClasseDto>, string?)> SuiviClasseAsync(string classe, DateOnly du, DateOnly au) =>
        GetAsync<List<LigneSuiviClasseDto>>($"api/psychotropes/classes/{Uri.EscapeDataString(classe)}/suivi?du={du:yyyy-MM-dd}&au={au:yyyy-MM-dd}", []);

    public Task<(byte[]?, string?, string?)> ExporterSuiviClasseAsync(string classe, DateOnly du, DateOnly au) =>
        FichierAsync(HttpMethod.Get, $"api/psychotropes/classes/{Uri.EscapeDataString(classe)}/suivi?du={du:yyyy-MM-dd}&au={au:yyyy-MM-dd}&format=xlsx",
            $"suivi_{classe}.xlsx");

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

    /// <summary>Vrai si une version de la nomenclature est importée (droit catalogue.creer).</summary>
    public Task<(bool, string?)> NomenclatureImporteeAsync() => GetAsync<bool>("api/nomenclature/importee", true);

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
        // Facture et retour laissés (réglage de l'officine, script 156) : fichier chiffré du poste ou mémoire seule. Lecture
        // impossible : réglage précédent gardé (aucun avant la première lecture : rien n'est lu, écrit ni supprimé sur le poste)
        if (tachesLaissees is not null && (await ReglageTachesLaisseesAsync()).Item1 is { } reglage)
            tachesLaissees.AppliquerReglage(reglage.Mode);
    }

    /// <summary>Réglage « facture et retour laissés » de l'officine (tout employé connecté).</summary>
    public Task<(ReglageTachesLaisseesDto?, string?)> ReglageTachesLaisseesAsync() =>
        GetAsync<ReglageTachesLaisseesDto?>("api/reglage-taches-laissees", null);

    /// <summary>Changé par le titulaire (journal avant / après) ; appliqué aussitôt sur ce poste, sur les autres à leur prochaine connexion.</summary>
    public async Task<string?> ModifierReglageTachesLaisseesAsync(string mode)
    {
        var (_, erreur) = await EnvoyerCorpsAsync<ReglageTachesLaisseesDto, object>(HttpMethod.Put, "api/admin/reglage-taches-laissees", new(mode));
        if (erreur is null) tachesLaissees?.AppliquerReglage(mode);
        return erreur;
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

    public async Task<(VenteEncaisseeDto?, string?)> EncaisserAsync(CommandeVenteDto commande)
    {
        var generation = bascule?.Generation ?? 0;
        var resultat = await PostAsync<CommandeVenteDto, VenteEncaisseeDto>("api/caisse/encaisser", commande);
        // Q25 : encaissement interrompu par la panne du serveur principal : si le poste a basculé sur le 2e serveur, la vente est renvoyée
        // UNE fois, avec la même clé d'encaissement (le serveur rend la vente déjà arrivée par réplication au lieu d'en créer une seconde).
        // Un seul serveur : rien de tout cela, comportement inchangé.
        if (resultat.Item2 is null || bascule is not { Active: true } || !session.EstConnecte) return resultat;
        if (bascule.Generation == generation)
        {
            if (resultat.Item2 != Injoignable && resultat.Item2 != MessageDelaiDepasse) return resultat;
            await bascule.ApresEchecAsync();
        }
        if (bascule.Generation == generation || !session.EstConnecte) return resultat;
        return await PostAsync<CommandeVenteDto, VenteEncaisseeDto>("api/caisse/encaisser", commande);
    }

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

    /// <summary>
    /// Valeurs déjà saisies d'un champ libre (suggestions pendant la frappe) : clé de la liste blanche du serveur (ChampsSuggestions),
    /// 10 valeurs au plus, sans accents ni majuscules.
    /// </summary>
    public Task<(List<string>, string?)> SuggestionsAsync(string champ, string q) =>
        GetAsync<List<string>>($"api/suggestions/{Uri.EscapeDataString(champ)}?q={Uri.EscapeDataString(q)}", []);

    public Task<(List<MedecinDto>, string?)> RechercherMedecinsAsync(string q) =>
        GetAsync<List<MedecinDto>>($"api/medecins?q={Uri.EscapeDataString(q)}", []);

    /// <summary>Fiche d'un médecin par son identifiant (404 : erreur « introuvable »).</summary>
    public Task<(MedecinDto?, string?)> MedecinAsync(long id) => GetAsync<MedecinDto?>($"api/medecins/{id}", null);

    /// <summary>Médecins sans wilaya (filtre « wilaya à renseigner »), par nom si <paramref name="q"/> est donné.</summary>
    public Task<(List<MedecinDto>, string?)> MedecinsSansWilayaAsync(string? q) =>
        GetAsync<List<MedecinDto>>($"api/medecins?sansWilaya=true&q={Uri.EscapeDataString(q ?? "")}", []);

    public Task<(IdentifiantDto?, string?)> CreerMedecinAsync(NouveauMedecinDto medecin) =>
        PostAsync<NouveauMedecinDto, IdentifiantDto>("api/medecins", medecin);

    public Task<(List<ProchePatientDto>?, string?)> ProchesPatientAsync(long id) => GetAsync<List<ProchePatientDto>?>($"api/patients/{id}/proches", null);
    public Task<(FichePatientDto?, string?)> FichePatientAsync(long id) => GetAsync<FichePatientDto?>($"api/patients/{id}/fiche", null);
    public async Task<string?> ModifierPatientAsync(FichePatientDto f) =>
        (await EnvoyerCorpsAsync<FichePatientDto, object>(HttpMethod.Put, $"api/patients/{f.Id}", f)).Item2;
    /// <summary>Date de fin de droits Chifa du patient (journalisée, patient_modifie).</summary>
    public async Task<string?> FinDroitPatientAsync(long patientId, DateOnly? date) =>
        (await EnvoyerCorpsAsync<FinDroitDto, object>(HttpMethod.Put, $"api/patients/{patientId}/fin-droit", new(date))).Item2;
    /// <summary>Patients › « Droits expirés ou bientôt expirés » ; etat : expires, bientot ou null.</summary>
    public Task<(PageDto<SuiviDroitChifaDto>?, string?)> SuiviDroitsChifaAsync(string? etat, string? organisme, DateOnly? du, DateOnly? au, int page, int taille) =>
        GetAsync<PageDto<SuiviDroitChifaDto>?>($"api/patients/droits-chifa?page={page}&taille={taille}"
            + (etat is null ? "" : $"&etat={etat}") + (organisme is null ? "" : $"&organisme={organisme}")
            + (du is { } d ? $"&du={d:yyyy-MM-dd}" : "") + (au is { } a ? $"&au={a:yyyy-MM-dd}" : ""), null);
    /// <summary>Patients › « Fichier des patients » ; organisme : id ou « aucun » ; droits : expires_ou_bientot, expires, bientot.</summary>
    public Task<(PageDto<PatientFichierDto>?, string?)> FichierPatientsAsync(string? texte, string? organisme, bool? chronique, bool? credit,
        string? droits, bool? sms, int page, int taille) =>
        GetAsync<PageDto<PatientFichierDto>?>("api/patients/fichier?" + Requete(("texte", texte?.Trim()), ("organisme", organisme), ("chronique", chronique),
            ("credit", credit), ("droits", droits), ("sms", sms), ("page", page), ("taille", taille)), null);
    public Task<(List<VendeurComptoirDto>, string?)> ValidateursBlocageAsync() =>
        GetAsync<List<VendeurComptoirDto>>("api/produits/validateurs-blocage", []);
    public Task<(FicheProduitModifiableDto?, string?)> FicheProduitAsync(long id) => GetAsync<FicheProduitModifiableDto?>($"api/produits/{id}/fiche", null);

    // ---- Indications thérapeutiques (script 104) ----
    public Task<(PageDto<IndicationDto>?, string?)> IndicationsAsync(bool? actif, string? q, int page, int taille) =>
        GetAsync<PageDto<IndicationDto>?>($"api/indications?page={page}&taille={taille}&" + Requete(("actif", actif), ("q", q)), null);
    public async Task<string?> EnregistrerIndicationAsync(long? id, IndicationSaisie s) => id is { } i
        ? (await EnvoyerCorpsAsync<IndicationSaisie, IndicationDto>(HttpMethod.Put, $"api/indications/{i}", s)).Item2
        : (await PostAsync<IndicationSaisie, IndicationDto>("api/indications", s)).Item2;
    public Task<(List<IndicationDto>, string?)> IndicationsDuProduitAsync(long produitId) =>
        GetAsync<List<IndicationDto>>($"api/produits/{produitId}/indications", []);
    public async Task<string?> AssocierIndicationsAsync(long produitId, IReadOnlyList<long> ids) =>
        (await EnvoyerCorpsAsync<IndicationsProduitRequete, object>(HttpMethod.Put, $"api/produits/{produitId}/indications", new(ids))).Item2;
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
    public Task<(List<ValideurDto>, string?)> ValideursAsync(string? permission, decimal? remise = null) =>
        GetAsync<List<ValideurDto>>("api/auth/valideurs?" + string.Join("&", new[]
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
    /// <summary>Produits à classer (hors nomenclature, encore médicaments, non classés d'office), filtrés et paginés par le serveur.</summary>
    public Task<(PageDto<ProduitAClasserDto>?, string?)> ProduitsAClasserAsync(string? texte, int page, int taille) =>
        GetAsync<PageDto<ProduitAClasserDto>?>("api/produits/a-classer?" + Requete(("q", texte?.Trim()), ("page", page), ("taille", taille)), null);
    /// <summary>Reclassement groupé des produits cochés dans une catégorie : nombre de produits reclassés.</summary>
    public Task<(NombreDto?, string?)> ReclasserProduitsAsync(List<long> produits, string categorie) =>
        PostAsync<ReclassementProduitsRequete, NombreDto>("api/produits/reclassement", new(produits, categorie));
    public async Task<string?> ModifierMedecinAsync(long id, NouveauMedecinDto medecin) =>
        (await EnvoyerCorpsAsync<NouveauMedecinDto, object>(HttpMethod.Put, $"api/medecins/{id}", medecin)).Item2;
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

    public async Task<string?> EnregistrerDroitsAsync(List<DroitsRoleDto> droits, string motDePasse) =>
        (await EnvoyerCorpsAsync<EnregistrementDroitsDto, object>(HttpMethod.Put, "api/admin/droits", new(droits, motDePasse))).Item2;

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
    public async Task<string?> ModifierReglagesNotificationAsync(ModificationReglagesNotificationDto r, string motDePasseTitulaire) =>
        (await EnvoyerCorpsAsync<ModificationReglagesNotificationDto, object>(HttpMethod.Put, "api/admin/notifications/reglages",
            r with { MotDePasseTitulaire = motDePasseTitulaire })).Item2;
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
    public Task<(AlertesNonEnvoyeesDto?, string?)> AlertesNonEnvoyeesAsync(string? texte = null, int page = 1, int taille = 10) =>
        GetAsync<AlertesNonEnvoyeesDto?>("api/admin/notifications/non-envoyees?" + Requete(("q", texte), ("page", page), ("taille", taille)), null);
    public Task<(NombreDto?, string?)> MarquerAlertesNonEnvoyeesVuesAsync(long jusquA) =>
        PostAsync<AlertesVuesRequete, NombreDto>("api/admin/notifications/non-envoyees/vues", new(jusquA));
    /// <summary>Écarts de caisse récents non vus (titulaire seul) ; marqués vus jusqu'à la session la plus récente affichée.</summary>
    public Task<(EcartsCaisseRecentsDto?, string?)> EcartsCaisseRecentsAsync(string? texte = null, int page = 1, int taille = 10) =>
        GetAsync<EcartsCaisseRecentsDto?>("api/caisse/ecarts-recents?" + Requete(("q", texte), ("page", page), ("taille", taille)), null);
    public Task<(NombreDto?, string?)> MarquerEcartsCaisseVusAsync(long jusquA) =>
        PostAsync<AlertesVuesRequete, NombreDto>("api/caisse/ecarts-recents/vus", new(jusquA));
    /// <summary>File des messages, paginée ; filtres : type (objet), statut, période de création.</summary>
    public Task<(PageDto<MessageSortantDto>?, string?)> FileMessagesAsync(int page, int taille, string? type = null, string? statut = null, DateOnly? du = null, DateOnly? au = null) =>
        GetAsync<PageDto<MessageSortantDto>?>("api/admin/notifications/file?" + Requete(("type", type), ("statut", statut), ("du", du), ("au", au), ("page", page), ("taille", taille)), null);

    public Task<(EtatSauvegardesDto?, string?)> SauvegardesAsync() => GetAsync<EtatSauvegardesDto?>("api/admin/sauvegardes", null);
    public Task<(PageDto<EntreeSauvegardeDto>?, string?)> JournalSauvegardesAsync(string? statut, DateOnly? du, DateOnly? au, int page, int taille) =>
        GetAsync<PageDto<EntreeSauvegardeDto>?>("api/admin/sauvegardes/journal?" + Requete(("statut", statut), ("du", du), ("au", au), ("page", page), ("taille", taille)), null);
    public async Task<string?> ModifierReglagesSauvegardeAsync(ReglagesSauvegardeDto r) =>
        (await EnvoyerCorpsAsync<ReglagesSauvegardeDto, object>(HttpMethod.Put, "api/admin/sauvegardes/reglages", r)).Item2;
    public Task<(ResultatSauvegardeDto?, string?)> LancerSauvegardeAsync() => PostAsync<object, ResultatSauvegardeDto>("api/admin/sauvegardes/lancer", new());
    // Copie de secours (PC de secours) ; le test de restauration est réservé au titulaire
    public Task<(CopieSecoursDto?, string?)> CopieSecoursAsync() => GetAsync<CopieSecoursDto?>("api/admin/sauvegardes/secours", null);
    public async Task<string?> ModifierCopieSecoursAsync(ReglagesCopieSecoursDto r) =>
        (await EnvoyerCorpsAsync<ReglagesCopieSecoursDto, object>(HttpMethod.Put, "api/admin/sauvegardes/secours", r)).Item2;
    public Task<(ResultatTestRestaurationDto?, string?)> TesterRestaurationCopieAsync() =>
        PostAsync<object, ResultatTestRestaurationDto>("api/admin/sauvegardes/secours/tester", new());

    // ---- 2e serveur (titulaire, script 122) ----
    // Synchronisation (lot S2) : écran Admin › Synchronisation
    public Task<(EtatSynchronisationDto?, string?)> SynchronisationAsync() => GetAsync<EtatSynchronisationDto?>("api/admin/synchronisation", null);
    public async Task<string?> ReglerSynchronisationAsync(ReglagesSynchronisationDto d) =>
        (await EnvoyerCorpsAsync<ReglagesSynchronisationDto, object>(HttpMethod.Put, "api/admin/synchronisation/reglages", d)).Item2;
    public async Task<string?> ActiverSynchronisationAsync(string code) =>
        (await PostAsync<ActivationSynchronisationDto, object>("api/admin/synchronisation/activation", new(code))).Item2;
    public Task<(ResultatEnvoiSynchronisationDto?, string?)> EnvoyerSynchronisationAsync() =>
        PostAsync<object, ResultatEnvoiSynchronisationDto>("api/admin/synchronisation/envoyer", new());
    // Synchronisation (lot S3) : seuils du bandeau et des alertes, conservation des lots accusés ; bandeau de la barre d'état
    public async Task<string?> ReglerSupervisionSynchronisationAsync(ReglagesSupervisionSynchronisationDto d) =>
        (await EnvoyerCorpsAsync<ReglagesSupervisionSynchronisationDto, object>(HttpMethod.Put, "api/admin/synchronisation/supervision", d)).Item2;
    /// <summary>Script 152 : secondes avant que l'encaissement soit permis (contrôle de démarrage du serveur central).</summary>
    public Task<(AttenteEncaissementDto?, string?)> AttenteEncaissementAsync() => GetAsync<AttenteEncaissementDto?>("api/caisse/attente-encaissement", null);
    public Task<(AttenteSynchronisationDto?, string?)> AttenteSynchronisationAsync() => GetAsync<AttenteSynchronisationDto?>("api/synchronisation/attente", null);
    /// <summary>Barre d'état : liaison avec le serveur central et avec le 2e serveur (tout employé connecté ; élément seulement en anomalie).</summary>
    public Task<(EtatLiaisonsPosteDto?, string?)> EtatLiaisonsPosteAsync() => GetAsync<EtatLiaisonsPosteDto?>("api/synchronisation/etat-poste", null);
    /// <summary>Mode démonstration du serveur (route sans connexion) : bandeau permanent de la barre d'état.</summary>
    public Task<(EtatDemoDto?, string?)> EtatDemoAsync() => GetAsync<EtatDemoDto?>("api/demo", null);
    // Lots S5.1 et S5.5 : ordres du serveur central (liste ; confirmation et refus par le titulaire)
    public Task<(PageOrdresCentrauxDto?, string?)> OrdresCentrauxAsync(string? statut, int page, int taille, string? type = null) =>
        GetAsync<PageOrdresCentrauxDto?>($"api/admin/synchronisation/ordres?page={page}&taille={taille}{(statut is null ? "" : "&statut=" + Uri.EscapeDataString(statut))}{(type is null ? "" : "&type=" + Uri.EscapeDataString(type))}", null);
    public async Task<string?> ConfirmerOrdreCentralAsync(Guid id, string? motDePasse, string? motDePasseTitulaire = null) =>
        (await PostAsync<ConfirmerOrdreCentralDto, object>($"api/admin/synchronisation/ordres/{id}/confirmer", new(motDePasse, motDePasseTitulaire))).Item2;
    public async Task<string?> RefuserOrdreCentralAsync(Guid id, string motif, string? motDePasseTitulaire = null) =>
        (await PostAsync<RefuserOrdreCentralDto, object>($"api/admin/synchronisation/ordres/{id}/refuser", new(motif, motDePasseTitulaire))).Item2;
    // Mutualisation des réglages (lots M5 et M7) : état des 11 tables, « Adopter », réglage fiscal et traçabilité
    public Task<(List<ReglageMutualiseDto>, string?)> ReglagesMutualisesAsync() => GetAsync<List<ReglageMutualiseDto>>("api/reglages-mutualises", []);
    public Task<(ReglageMutualiseDto?, string?)> ReglageMutualiseAsync(string table) =>
        GetAsync<ReglageMutualiseDto?>($"api/reglages-mutualises/{Uri.EscapeDataString(table)}", null);
    public async Task<string?> AdopterReglageAsync(string table, long versionModele) =>
        (await PostAsync<AdopterReglageRequete, object>($"api/reglages-mutualises/{Uri.EscapeDataString(table)}/adopter", new(versionModele))).Item2;
    public Task<(ParametreFiscalDto?, string?)> ParametreFiscalAsync() => GetAsync<ParametreFiscalDto?>("api/fiscal/parametres", null);

    // Taux de TVA réglables (script 171)
    public Task<(ListeTauxTvaDto?, string?)> TauxTvaAsync() => GetAsync<ListeTauxTvaDto?>("api/fiscal/taux-tva", null);
    // Offre de base et options (script 172) : écran « Mon offre »
    public Task<(OffreDto?, string?)> OffreAsync() => GetAsync<OffreDto?>("api/offre", null);
    public Task<(PacksOffreDto?, string?)> PacksOffreAsync() => GetAsync<PacksOffreDto?>("api/offre/packs", null);
    public async Task<string?> SaisirActivationOffreAsync(string code) =>
        (await EnvoyerCorpsAsync<ActivationOffreRequete, object>(HttpMethod.Post, "api/offre/activation", new(code))).Item2;
    public async Task<string?> AjouterTauxTvaAsync(TauxTvaRequete r) => (await PostAsync<TauxTvaRequete, IdentifiantDto>("api/fiscal/taux-tva", r)).Item2;
    public async Task<string?> ModifierTauxTvaAsync(long id, TauxTvaModificationRequete r) =>
        (await EnvoyerCorpsAsync<TauxTvaModificationRequete, object>(HttpMethod.Put, $"api/fiscal/taux-tva/{id}", r)).Item2;
    public async Task<string?> DefinirTauxTvaCategorieAsync(string code, decimal? taux) =>
        (await EnvoyerCorpsAsync<TauxTvaDefautRequete, object>(HttpMethod.Put, $"api/fiscal/taux-tva/categories/{Uri.EscapeDataString(code)}", new(taux))).Item2;
    public async Task<string?> EnregistrerParametreFiscalAsync(ParametreFiscalDto d) =>
        (await EnvoyerCorpsAsync<ParametreFiscalDto, object>(HttpMethod.Put, "api/fiscal/parametres", d)).Item2;
    public Task<(ParametreTracabiliteDto?, string?)> ParametreTracabiliteAsync() => GetAsync<ParametreTracabiliteDto?>("api/tracabilite/parametres", null);
    public async Task<string?> EnregistrerParametreTracabiliteAsync(ParametreTracabiliteDto d) =>
        (await EnvoyerCorpsAsync<ParametreTracabiliteDto, object>(HttpMethod.Put, "api/tracabilite/parametres", d)).Item2;

    // État de chaque table de réglages, retenu depuis l'en-tête « X-Reglage-Central » des lectures des écrans de réglage (aucun appel de plus)
    private readonly Dictionary<string, EtatReglageEntete> reglagesCentraux = [];
    /// <summary>Levé quand une lecture vient de renseigner l'état d'une table : le bandeau « serveur central » de l'écran se met à jour.</summary>
    public event Action? ReglageCentralChange;
    /// <summary>État connu de la table (local et sans écart tant qu'aucune lecture ne l'a renseigné : comportement sans serveur central).</summary>
    public EtatReglageEntete ReglageCentral(string table) => reglagesCentraux.GetValueOrDefault(table) ?? EtatReglageEntete.Aucun with { Table = table };

    private void RetenirReglageCentral(HttpResponseMessage rep)
    {
        if (!rep.Headers.TryGetValues(EtatReglageEntete.Nom, out var valeurs) || EtatReglageEntete.Lire(valeurs.FirstOrDefault()) is not { } etat) return;
        reglagesCentraux[etat.Table] = etat;
        ReglageCentralChange?.Invoke();
    }

    // Synchronisation (lot S6) : rechargement complet
    public async Task<string?> RechargerSynchronisationAsync() =>
        (await PostAsync<object, object>("api/admin/synchronisation/recharger", new())).Item2;
    /// <summary>Lot S6.2 : « Repartir sur une nouvelle époque » (titulaire), confirmée avec l'époque affichée et le mot de passe du titulaire.</summary>
    public Task<(ResultatNouvelleEpoqueSynchronisationDto?, string?)> NouvelleEpoqueSynchronisationAsync(int epoqueAffichee, string motDePasse) =>
        PostAsync<NouvelleEpoqueSynchronisationDto, ResultatNouvelleEpoqueSynchronisationDto>("api/admin/synchronisation/nouvelle-epoque",
            new NouvelleEpoqueSynchronisationDto(epoqueAffichee, motDePasse));
    /// <summary>Lot S7.2b : code d'accès à distance (titulaire, mot de passe ressaisi) ; montré une fois, jamais gardé par le poste.</summary>
    public Task<(CodeAccesDistantDto?, string?)> CodeAccesDistantAsync(string compte, string motDePasse) =>
        PostAsync<DemandeCodeAccesDistantDto, CodeAccesDistantDto>("api/admin/synchronisation/acces-distant/code",
            new DemandeCodeAccesDistantDto(compte, motDePasse));
    /// <summary>Lot S7.3 : comptes distants rattachés à l'officine (titulaire).</summary>
    public Task<(List<CompteDistantRattacheDto>?, string?)> ComptesAccesDistantAsync() =>
        GetAsync<List<CompteDistantRattacheDto>?>("api/admin/synchronisation/acces-distant/comptes", null);
    /// <summary>Lot S7.3 : droits d'un compte rattaché (titulaire, mot de passe ressaisi).</summary>
    public Task<(ReponseCompteDistantDto?, string?)> DroitsAccesDistantAsync(string compte, IReadOnlyList<string> droits, string motDePasse) =>
        PostAsync<DemandeDroitsAccesDistantDto, ReponseCompteDistantDto>("api/admin/synchronisation/acces-distant/droits",
            new DemandeDroitsAccesDistantDto(compte, droits, motDePasse));
    /// <summary>Lot S7.3 : retrait d'un compte rattaché (titulaire, mot de passe ressaisi ; effet immédiat).</summary>
    public Task<(ReponseCompteDistantDto?, string?)> RetirerAccesDistantAsync(string compte, string motDePasse) =>
        PostAsync<DemandeRetraitAccesDistantDto, ReponseCompteDistantDto>("api/admin/synchronisation/acces-distant/retrait",
            new DemandeRetraitAccesDistantDto(compte, motDePasse));
    /// <summary>Lot S6.5.4 : prise d'acte du titulaire d'une restauration constatée par le serveur central (cause).</summary>
    public async Task<string?> PrendreActeRestaurationAsync(int nouvelleEpoque, string cause) =>
        (await EnvoyerCorpsAsync<PriseActeRestaurationDto, object>(HttpMethod.Post, "api/admin/synchronisation/integrite/prise-acte",
            new PriseActeRestaurationDto(nouvelleEpoque, cause))).Item2;
    public Task<(SecondServeurDto?, string?)> SecondServeurAsync() => GetAsync<SecondServeurDto?>("api/admin/second-serveur", null);
    public async Task<string?> ModifierServeurOfficineAsync(ModifierServeurOfficineDto d) =>
        (await EnvoyerCorpsAsync<ModifierServeurOfficineDto, object>(HttpMethod.Put, "api/admin/second-serveur/serveurs", d)).Item2;
    public async Task<string?> ModifierCopieGedAsync(CopieGedDto d) =>
        (await EnvoyerCorpsAsync<CopieGedDto, object>(HttpMethod.Put, "api/admin/second-serveur/copie-ged", d)).Item2;
    public async Task<string?> ModifierSeuilsReplicationAsync(SeuilsReplicationDto d) =>
        (await EnvoyerCorpsAsync<SeuilsReplicationDto, object>(HttpMethod.Put, "api/admin/second-serveur/seuils", d)).Item2;
    public Task<(FichierCleDto?, string?)> ExporterCleScellementAsync(string motDePasse, string confirmation, string motDePasseTitulaire) =>
        PostAsync<ExportCleDto, FichierCleDto>("api/admin/second-serveur/cle/exporter", new(motDePasse, confirmation, motDePasseTitulaire));

    /// <summary>
    /// Bascule d'un poste : état de la liaison avec l'API du 2e serveur (elle ne démarre qu'une fois sa base promue : jamais un poste
    /// dirigé vers une copie en lecture seule). Port puis /api/healthcheck, 4 secondes au plus. Le certificat est toujours vérifié par Windows :
    /// un refus est seulement reconnu (EtatLiaison.CertificatRefuse) pour afficher la bonne consigne.
    /// </summary>
    public static async Task<EtatLiaison> VerifierApiAsync(string adresse)
    {
        if (!Uri.TryCreate(adresse, UriKind.Absolute, out var uri)) return EtatLiaison.Injoignable;
        if (!await PortOuvertAsync(uri.Host, uri.Port, TimeSpan.FromSeconds(4)))
        {
            JournalLiaison.Noter("2e serveur", EtatLiaison.Injoignable, null);
            return EtatLiaison.Injoignable;
        }
        try
        {
            using var client = new HttpClient { BaseAddress = uri, Timeout = TimeSpan.FromSeconds(4) };
            using var rep = await client.GetAsync("api/healthcheck");
            var etat = LiaisonServeur.Classer(rep.StatusCode);
            JournalLiaison.Noter("2e serveur", etat, null);
            return etat;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Security.Authentication.AuthenticationException)
        {
            var etat = LiaisonServeur.Classer(ex);
            JournalLiaison.Noter("2e serveur", etat, ex);
            return etat;
        }
    }

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
    public Task<(List<ProduitComptoirDto>, string?)> RechercherAuComptoirAsync(string q, int? limite = null) =>
        GetAsync<List<ProduitComptoirDto>>($"api/caisse/produits?q={Uri.EscapeDataString(q)}{(limite is { } l ? $"&limite={l}" : "")}", []);
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

    // ---- Sorties et entrées d'argent par le tiroir (script 096) ----
    /// <summary>Ouverture du tiroir (F8) : bon de sortie (ou d'entrée) rendu pour un motif à effet ; « Faire autoriser » au-delà du seuil.</summary>
    public Task<(OuvertureTiroirDto?, string?)> OuvrirTiroirAsync(EvenementCaisseRequete e) => PostAsync<EvenementCaisseRequete, OuvertureTiroirDto>("api/caisse/evenements", e);
    public Task<(byte[]?, string?, string?)> BonSortiePdfAsync(long id, int largeurMm) =>
        FichierAsync(HttpMethod.Get, $"api/caisse/tiroir/{id}/bon.pdf?largeur={largeurMm}", "bon_sortie.pdf");
    public Task<(OuvertureTiroirDto?, string?)> CorrigerTiroirAsync(long id, string? motif) =>
        PostAsync<CorrectionTiroirRequete, OuvertureTiroirDto>($"api/caisse/tiroir/{id}/correction", new(motif));
    /// <summary>Annulation d'un règlement de grossiste payé depuis le tiroir (script 098) : entrée de correction rendue.</summary>
    public Task<(OuvertureTiroirDto?, string?)> AnnulerReglementTiroirAsync(long id, string? motif) =>
        PostAsync<CorrectionTiroirRequete, OuvertureTiroirDto>($"api/caisse/tiroir/{id}/annulation-reglement", new(motif));
    public Task<(PageMouvementsTiroirDto?, string?)> MouvementsTiroirAsync(DateOnly? du, DateOnly? au, long? employe, string? motif, string? sens, int page, int taille) =>
        GetAsync<PageMouvementsTiroirDto?>("api/surveillance/tiroir?" + Requete(("du", du?.ToString("yyyy-MM-dd")), ("au", au?.ToString("yyyy-MM-dd")),
            ("employe", employe), ("motif", motif), ("sens", sens), ("page", page), ("taille", taille)), null);

    // ---- Motifs réglables (script 094) ----
    /// <summary>Motifs actifs d'une liste (tiroir, attente, charge, sortie_stock, retour_fournisseur), proposés à la saisie.</summary>
    public Task<(List<MotifReglableDto>, string?)> MotifsAsync(string liste) => GetAsync<List<MotifReglableDto>>($"api/motifs/{Uri.EscapeDataString(liste)}", []);
    public Task<(PageDto<MotifReglableDto>?, string?)> MotifsPageAsync(string? liste, string? etat, string? texte, int page, int taille) =>
        GetAsync<PageDto<MotifReglableDto>?>("api/motifs?" + Requete(("liste", liste), ("etat", etat), ("q", texte), ("page", page), ("taille", taille)), null);
    public Task<(IdentifiantDto?, string?)> EnregistrerMotifAsync(long? id, SaisieMotifReglableDto m) => id is null
        ? PostAsync<SaisieMotifReglableDto, IdentifiantDto>("api/motifs", m)
        : EnvoyerCorpsAsync<SaisieMotifReglableDto, IdentifiantDto>(HttpMethod.Put, $"api/motifs/{id}", m);
    public Task<(List<AlerteSurveillanceDto>, string?)> SurveillanceAsync(bool traitees) =>
        GetAsync<List<AlerteSurveillanceDto>>($"api/surveillance?traitees={(traitees ? "true" : "false")}", []);
    public async Task<string?> TraiterSurveillanceAsync(long id, string? action, string motDePasseTitulaire) =>
        (await PostAsync<TraiterSurveillanceRequete, object>($"api/surveillance/{id}/traiter", new(action, motDePasseTitulaire))).Item2;
    // ---- Intégrité des journaux (titulaire) ----
    public Task<(TableauIntegriteDto?, string?)> IntegriteAsync() => GetAsync<TableauIntegriteDto?>("api/surveillance/integrite", null);
    public Task<(TableauIntegriteDto?, string?)> VerifierIntegriteAsync() => PostAsync<object, TableauIntegriteDto>("api/surveillance/integrite/verifier", new());
    // ---- Journal fiscal des ventes (lot 3, titulaire) ----
    public Task<(JournalFiscalDto?, string?)> JournalFiscalAsync(DateOnly du, DateOnly au, string? ticket, int page, int taille) =>
        GetAsync<JournalFiscalDto?>("api/surveillance/journal-fiscal?" + Requete(("du", du.ToString("yyyy-MM-dd")), ("au", au.ToString("yyyy-MM-dd")),
            ("ticket", ticket), ("page", page), ("taille", taille)), null);
    public Task<(byte[]?, string?, string?)> ExportJournalFiscalAsync(DateOnly du, DateOnly au, string format) =>
        FichierAsync(HttpMethod.Get, "api/surveillance/journal-fiscal/export?" + Requete(("du", du.ToString("yyyy-MM-dd")), ("au", au.ToString("yyyy-MM-dd")),
            ("format", format)), $"journal_fiscal.{format}");

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

    public Task<(PageDto<EmplacementDto>?, string?)> EmplacementsPageAsync(string? zone, string? etat, string? texte, int page, int taille) =>
        GetAsync<PageDto<EmplacementDto>?>("api/emplacements/page?" + Requete(("zone", zone), ("etat", etat), ("q", texte), ("page", page), ("taille", taille)), null);
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

    /// <summary>Entrées de stock (lots) paginées et filtrées : produit, n° de lot ou code-barres ; épuisés compris ou non.</summary>
    public Task<(PageDto<EntreeStockDto>?, string?)> EntreesStockAsync(string? recherche, bool epuises, int page, int taille) =>
        GetAsync<PageDto<EntreeStockDto>?>("api/stock/entrees?" + Requete(("q", recherche), ("epuises", epuises ? "true" : null), ("page", page), ("taille", taille)), null);
    public Task<(List<CorrectionLotDto>, string?)> CorrectionsLotAsync(long lotId) => GetAsync<List<CorrectionLotDto>>($"api/lots/{lotId}/corrections", []);
    public Task<(EntreeStockDto?, string?)> CorrigerEntreeStockAsync(long lotId, CorrectionLotSaisie c) =>
        PostAsync<CorrectionLotSaisie, EntreeStockDto>($"api/lots/{lotId}/corrections", c);

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

    /// <summary>Numéros de lot du produit proposés au rappel, épuisés compris, contenant <paramref name="q"/> (10 au plus ; stock.declarer_perime).</summary>
    public Task<(List<LotRappelDto>, string?)> LotsRappelAsync(long produitId, string q) =>
        GetAsync<List<LotRappelDto>>($"api/tracabilite/produits/{produitId}/lots?q={Uri.EscapeDataString(q)}", []);

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

    /// <summary>
    /// Barre d'état : le serveur répond-il ? (route publique /api/healthcheck, 4 secondes au plus). Sans exception (2026-11-15) : le port est
    /// d'abord testé par une connexion qui renvoie un code d'erreur au lieu de lever une exception ; la requête n'est jamais annulée
    /// pendant qu'on l'attend (on cesse seulement de l'attendre après 4 secondes) ; son issue, même en échec, est relevée à part. Le
    /// débogueur ne s'arrête donc plus sur une TaskCanceledException quand l'API est arrêtée, en pause ou lente à démarrer.
    /// </summary>
    /// <summary>Adresse de l'API utilisée par ce poste (affichage).</summary>
    public string AdresseServeur => (adresseCourante?.Actuelle ?? http.BaseAddress)?.ToString() ?? "?";

    public async Task<bool> ServeurJoignableAsync()
    {
        var delai = TimeSpan.FromSeconds(4);
        if ((adresseCourante?.Actuelle ?? http.BaseAddress) is not { } adresse || !await PortOuvertAsync(adresse.Host, adresse.Port, delai))
        {
            Liaison = EtatLiaison.Injoignable;
            JournalLiaison.Noter("serveur", Liaison, null);
            return false;
        }

        // Requête bornée à 30 s en arrière-plan (jamais attendue au-delà de 4 s) ; issue relevée sans relancer d'exception
        var borne = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        // Jeton joint quand un employé est connecté : le serveur ne compte un poste « vu » que sur un jeton valide (sans employé, rien ne change)
        var message = new HttpRequestMessage(HttpMethod.Get, "api/healthcheck");
        if (session.EstConnecte && session.Jeton is { Length: > 0 } jeton) message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", jeton);
        var requete = http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, borne.Token);
        _ = requete.ContinueWith(t =>
        {
            if (t.IsCompletedSuccessfully) t.Result.Dispose();
            // Certificat refusé : relevé ici (la requête échoue avant les 4 secondes) ; le certificat n'est jamais contourné
            if (t.IsFaulted) { Liaison = LiaisonServeur.Classer(t.Exception); JournalLiaison.Noter("serveur", Liaison, t.Exception!.GetBaseException()); }
            borne.Dispose();
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

        var premier = await Task.WhenAny(requete, Task.Delay(delai));
        var joignable = premier == requete && requete.IsCompletedSuccessfully && requete.Result.IsSuccessStatusCode;
        if (joignable) { Liaison = EtatLiaison.Joignable; JournalLiaison.Noter("serveur", Liaison, null); }
        else if (!requete.IsFaulted) { Liaison = EtatLiaison.Injoignable; JournalLiaison.Noter("serveur", Liaison, null); }
        else Liaison = LiaisonServeur.Classer(requete.Exception);
        return joignable;
    }

    /// <summary>Dernier état de la liaison avec le serveur de ce poste (Injoignable ou CertificatRefuse quand la vérification échoue).</summary>
    public EtatLiaison Liaison { get; private set; } = EtatLiaison.Joignable;

    /// <summary>Connexion TCP de test sans exception (code d'erreur du socket) ; abandonnée après le délai.</summary>
    private static async Task<bool> PortOuvertAsync(string hote, int port, TimeSpan delai)
    {
        var fin = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var args = new System.Net.Sockets.SocketAsyncEventArgs { RemoteEndPoint = new DnsEndPoint(hote, port) };
        args.Completed += (_, e) => fin.TrySetResult(e.SocketError == System.Net.Sockets.SocketError.Success);
        if (!System.Net.Sockets.Socket.ConnectAsync(System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp, args))
            fin.TrySetResult(args.SocketError == System.Net.Sockets.SocketError.Success);
        var premier = await Task.WhenAny(fin.Task, Task.Delay(delai));
        if (premier != fin.Task) System.Net.Sockets.Socket.CancelConnectAsync(args);
        var ouvert = premier == fin.Task && fin.Task.Result;
        args.ConnectSocket?.Close();
        return ouvert;
    }

    public Task<(EtatPosteDto?, string?)> EtatPosteAsync() => GetAsync<EtatPosteDto?>("api/etat-poste", null);
    /// <summary>Compteurs des pastilles de la barre d'état (filtrés par les droits au serveur).</summary>
    public Task<(CompteursBarreDto?, string?)> CompteursBarreAsync() => GetAsync<CompteursBarreDto?>("api/etat-poste/compteurs", null);
    /// <summary>Reste du lot S7.3 : message du titulaire à distance à afficher dans la barre d'état (null : rien).</summary>
    public async Task<(MessageCentralDto?, string?)> MessageCentralAsync()
    {
        var (etat, erreur) = await GetAsync<MessageCentralEtatDto?>("api/etat-poste/message-central", null);
        return (etat?.Message, erreur);
    }
    /// <summary>Fermeture du message par le titulaire de l'officine (il disparaît de tous les postes).</summary>
    public async Task<string?> FermerMessageCentralAsync(Guid id) =>
        (await EnvoyerCorpsAsync<object?, object>(HttpMethod.Post, $"api/etat-poste/message-central/{id}/fermer", null)).Item2;

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

    public async Task<string?> ModifierLigneCommandeAsync(long commandeId, long ligneId, int quantite, decimal? prixHt) =>
        (await EnvoyerCorpsAsync<ModificationLigneCommandeRequete, object>(HttpMethod.Put, $"api/achats/commandes/{commandeId}/lignes/{ligneId}", new(quantite, prixHt))).Item2;

    public Task<(byte[]?, string?, string?)> EmettreCommandeAsync(long id) => FichierAsync(HttpMethod.Post, $"api/achats/commandes/{id}/emettre", $"commande_{id}.pdf");
    public Task<(byte[]?, string?, string?)> BonCommandeAsync(long id) => FichierAsync(HttpMethod.Get, $"api/achats/commandes/{id}/pdf", $"commande_{id}.pdf");
    public async Task<string?> AnnulerCommandeAsync(long id) => (await PostAsync<object, object>($"api/achats/commandes/{id}/annuler", new())).Item2;

    // ---- Multi-grossistes (Achats › Commander, Catalogues des grossistes) ----
    public Task<(ProfilCatalogueDto?, string?)> ProfilCatalogueAsync(long fournisseurId) =>
        GetAsync<ProfilCatalogueDto?>($"api/achats/catalogues/profils/{fournisseurId}", null);
    public async Task<string?> EnregistrerProfilCatalogueAsync(ProfilCatalogueDto p) =>
        (await EnvoyerCorpsAsync<ProfilCatalogueDto, object>(HttpMethod.Put, $"api/achats/catalogues/profils/{p.FournisseurId}", p)).Item2;
    public Task<(List<string>?, string?)> ColonnesCatalogueAsync(FichierCatalogueRequete f) =>
        PostAsync<FichierCatalogueRequete, List<string>>("api/achats/catalogues/colonnes", f);
    public Task<(BilanImportCatalogueDto?, string?)> ImporterCatalogueAsync(long fournisseurId, FichierCatalogueRequete f) =>
        PostAsync<FichierCatalogueRequete, BilanImportCatalogueDto>($"api/achats/catalogues/{fournisseurId}/import", f);
    public Task<(PageDto<ImportCatalogueDto>?, string?)> ImportsCatalogueAsync(long? grossiste, int page, int taille) =>
        GetAsync<PageDto<ImportCatalogueDto>?>("api/achats/catalogues/imports?" + Requete(("grossiste", grossiste), ("page", page), ("taille", taille)), null);
    public Task<(PageDto<ComparaisonProduitDto>?, string?)> ComparaisonGrossistesAsync(string? recherche, string? statut, int page, int taille) =>
        GetAsync<PageDto<ComparaisonProduitDto>?>("api/achats/commander?" + Requete(("recherche", recherche?.Trim()), ("statut", statut), ("page", page), ("taille", taille)), null);
    public Task<(List<BonRepartiDto>?, string?)> RepartirCommandeAsync(RepartitionRequete r) =>
        PostAsync<RepartitionRequete, List<BonRepartiDto>>("api/achats/commander/repartir", r);
    public Task<(BonEnvoyeDto?, string?)> EnvoyerBonParEmailAsync(long commandeId) =>
        PostAsync<object, BonEnvoyeDto>($"api/achats/commandes/{commandeId}/envoyer-email", new());

    // ---- Ruptures et substitution (lot 7) ----
    public Task<(PageDto<RuptureProduitDto>?, string?)> RupturesPartoutAsync(string? recherche, int page, int taille) =>
        GetAsync<PageDto<RuptureProduitDto>?>("api/achats/ruptures/partout?" + Requete(("recherche", recherche?.Trim()), ("page", page), ("taille", taille)), null);
    public Task<(PageDto<PeriodeRuptureDto>?, string?)> HistoriqueRupturesAsync(string? recherche, string? etat, int page, int taille) =>
        GetAsync<PageDto<PeriodeRuptureDto>?>("api/achats/ruptures/historique?" + Requete(("recherche", recherche?.Trim()), ("etat", etat), ("page", page), ("taille", taille)), null);
    public Task<(PageDto<DemandePatientDto>?, string?)> DemandesPatientsAsync(string? recherche, string? statut, int page, int taille) =>
        GetAsync<PageDto<DemandePatientDto>?>("api/caisse/demandes-patients?" + Requete(("recherche", recherche?.Trim()), ("statut", statut), ("page", page), ("taille", taille)), null);
    public Task<(AlertesDisponibiliteDto?, string?)> AlertesDisponibiliteAsync() =>
        GetAsync<AlertesDisponibiliteDto?>("api/caisse/demandes-patients/alertes", null);
    public Task<(long, string?)> CreerDemandePatientAsync(NouvelleDemandePatientRequete r) =>
        PostAsync<NouvelleDemandePatientRequete, long>("api/caisse/demandes-patients", r);
    public Task<(string?, string?)> PrevenirPatientAsync(long id, bool parSms) =>
        PostAsync<PrevenirPatientRequete, string>($"api/caisse/demandes-patients/{id}/prevenir", new(parSms));
    public async Task<string?> CloturerDemandePatientAsync(long id, bool servie) =>
        (await EnvoyerCorpsAsync<CloturerDemandeRequete, object>(HttpMethod.Post, $"api/caisse/demandes-patients/{id}/cloturer", new(servie))).Item2;
    public Task<(ConsentementSmsDto?, string?)> ConsentementSmsAsync(long patientId) =>
        GetAsync<ConsentementSmsDto?>($"api/patients/{patientId}/consentement-sms", null);
    public async Task<string?> EnregistrerConsentementSmsAsync(long patientId, bool accord) =>
        (await EnvoyerCorpsAsync<ConsentementSmsDto, object>(HttpMethod.Put, $"api/patients/{patientId}/consentement-sms", new(accord, null))).Item2;
    // Formulaire d'accord SMS (2026-11-16, script 123)
    public Task<(byte[]?, string?, string?)> FormulaireAccordSmsPdfAsync(long patientId) =>
        FichierAsync(HttpMethod.Get, $"api/patients/{patientId}/formulaire-sms.pdf", $"accord_sms_{patientId}.pdf");
    public async Task<string?> EnregistrerFormulaireSmsRecuAsync(long patientId, bool recu) =>
        (await EnvoyerCorpsAsync<FormulaireSmsRecuRequete, object>(HttpMethod.Put, $"api/patients/{patientId}/formulaire-sms", new(recu))).Item2;
    public Task<(TexteFormulaireSmsDto?, string?)> TexteFormulaireSmsAsync() =>
        GetAsync<TexteFormulaireSmsDto?>("api/parametres/formulaire-sms", null);
    public async Task<string?> EnregistrerTexteFormulaireSmsAsync(string? texte) =>
        (await EnvoyerCorpsAsync<TexteFormulaireSmsDto, object>(HttpMethod.Put, "api/parametres/formulaire-sms", new(texte ?? "", false))).Item2;
    public Task<(ParametreSubstitutionDto?, string?)> ParametreSubstitutionAsync() =>
        GetAsync<ParametreSubstitutionDto?>("api/substitution/parametre", null);
    public async Task<string?> EnregistrerParametreSubstitutionAsync(bool equivalentExactSeulement) =>
        (await EnvoyerCorpsAsync<ParametreSubstitutionDto, object>(HttpMethod.Put, "api/substitution/parametre", new(equivalentExactSeulement, null, null))).Item2;
    public Task<(PageDto<LienListeBlancheDto>?, string?)> ListeBlancheAsync(string? recherche, int page, int taille) =>
        GetAsync<PageDto<LienListeBlancheDto>?>("api/substitution/liste-blanche?" + Requete(("recherche", recherche?.Trim()), ("page", page), ("taille", taille)), null);
    public async Task<string?> AjouterListeBlancheAsync(long produitId, long remplacantId) =>
        (await EnvoyerCorpsAsync<LienListeBlancheRequete, object>(HttpMethod.Post, "api/substitution/liste-blanche", new(produitId, remplacantId))).Item2;
    public async Task<string?> RetirerListeBlancheAsync(long produitId, long remplacantId) =>
        (await EnvoyerCorpsAsync<object?, object>(HttpMethod.Delete, $"api/substitution/liste-blanche/{produitId}/{remplacantId}", null)).Item2;
    public Task<(PageDto<ProduitFabricationDto>?, string?)> FabricationLocaleAsync(string? recherche, bool? locale, int page, int taille) =>
        GetAsync<PageDto<ProduitFabricationDto>?>("api/substitution/fabrication?" + Requete(("recherche", recherche?.Trim()),
            ("locale", locale is null ? null : locale.Value ? "true" : "false"), ("page", page), ("taille", taille)), null);
    public async Task<string?> ModifierFabricationLocaleAsync(long produitId, bool? valeur) =>
        (await EnvoyerCorpsAsync<FabricationLocaleRequete, object>(HttpMethod.Put, $"api/substitution/fabrication/{produitId}", new(valeur))).Item2;

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
    // Circuit « la caisse d'abord » (2026-11-15) : ventes candidates au rattachement du volet, rattachement, listes
    // ---- Volet Chifa : compléments (script 100) ----
    // Types de prestation (script 105)
    public Task<(PageDto<TypePrestationDto>?, string?)> TypesPrestationAsync(bool? actif, string? q, int page, int taille) =>
        GetAsync<PageDto<TypePrestationDto>?>($"api/types-prestation?page={page}&taille={taille}&" + Requete(("actif", actif), ("q", q)), null);
    public async Task<string?> EnregistrerTypePrestationAsync(long? id, TypePrestationSaisie s) => id is { } i
        ? (await EnvoyerCorpsAsync<TypePrestationSaisie, TypePrestationDto>(HttpMethod.Put, $"api/types-prestation/{i}", s)).Item2
        : (await PostAsync<TypePrestationSaisie, TypePrestationDto>("api/types-prestation", s)).Item2;
    public Task<(PageDto<CentrePayeurDto>?, string?)> CentresPayeursAsync(long? organismeId, bool? actif, string? q, int page, int taille) =>
        GetAsync<PageDto<CentrePayeurDto>?>($"api/centres-payeurs?page={page}&taille={taille}&" + Requete(("organismeId", organismeId), ("actif", actif), ("q", q)), null);
    public async Task<string?> EnregistrerCentrePayeurAsync(long? id, CentrePayeurSaisie s) => id is { } i
        ? (await EnvoyerCorpsAsync<CentrePayeurSaisie, CentrePayeurDto>(HttpMethod.Put, $"api/centres-payeurs/{i}", s)).Item2
        : (await PostAsync<CentrePayeurSaisie, CentrePayeurDto>("api/centres-payeurs", s)).Item2;
    public Task<(PageDto<VenteChifaDto>?, string?)> VentesChifaAsync(string? q, long? centrePayeurId, string exclues, int page, int taille) =>
        GetAsync<PageDto<VenteChifaDto>?>($"api/chifa/ventes?page={page}&taille={taille}&" + Requete(("q", q), ("centrePayeurId", centrePayeurId), ("exclues", exclues)), null);
    public Task<(VenteChifaDto?, string?)> VenteChifaAsync(long id) => GetAsync<VenteChifaDto?>($"api/chifa/ventes/{id}", null);
    public Task<(List<EmployeChoixDto>, string?)> EmployesChifaAsync() => GetAsync<List<EmployeChoixDto>>("api/chifa/employes", []);
    public async Task<string?> EnregistrerInfosChifaAsync(long venteId, ChifaInfosSaisie s) =>
        (await EnvoyerCorpsAsync<ChifaInfosSaisie, object>(HttpMethod.Put, $"api/ventes/{venteId}/chifa", s)).Item2;
    public async Task<string?> ExclureVenteChifaAsync(long venteId, string motifCode, string? texte) =>
        (await PostAsync<ExclusionChifaRequete, object>($"api/ventes/{venteId}/chifa/exclusion", new(motifCode, texte))).Item2;
    public Task<(List<ConflitFactureDto>, string?)> ConflitsFactureChifaAsync(bool aTrancher) =>
        GetAsync<List<ConflitFactureDto>>($"api/chifa/conflits-facture?aTrancher={(aTrancher ? "true" : "false")}", []);
    public async Task<string?> TrancherConflitFactureAsync(long venteId, string valeurRetenue) =>
        (await PostAsync<TrancherConflitFactureRequete, object>($"api/chifa/conflits-facture/{venteId}", new(valeurRetenue))).Item2;
    public async Task<string?> RetablirVenteChifaAsync(long venteId) =>
        (await EnvoyerCorpsAsync<object?, object>(HttpMethod.Delete, $"api/ventes/{venteId}/chifa/exclusion", null)).Item2;
    // Retours avec part organisme à corriger dans CHIFA-Officine (script 155) : etat = a_corriger, corriges, tous
    public Task<(PageDto<RetourACorrigerChifaDto>?, string?)> RetoursACorrigerChifaAsync(string? q, string etat, int page, int taille) =>
        GetAsync<PageDto<RetourACorrigerChifaDto>?>($"api/chifa/retours-a-corriger?page={page}&taille={taille}&" + Requete(("q", q), ("etat", etat)), null);
    public async Task<string?> MarquerRetourCorrigeChifaAsync(long retourId) =>
        (await PostAsync<object?, object>($"api/chifa/retours-a-corriger/{retourId}/corrige", null)).Item2;

    // ---- Sérialisation DataMatrix : file hors ligne (script 120) ----
    public Task<(EtatSerialisationDto?, string?)> EtatSerialisationAsync() => GetAsync<EtatSerialisationDto?>("api/serialisation/etat", null);
    public Task<(PageDto<OperationSerialisationDto>?, string?)> FileSerialisationAsync(string? statut, int page, int taille) =>
        GetAsync<PageDto<OperationSerialisationDto>?>($"api/serialisation/file?page={page}&taille={taille}&" + Requete(("statut", statut)), null);
    public Task<(RelanceSerialisationDto?, string?)> RelancerSerialisationAsync(long? id) =>
        PostAsync<object?, RelanceSerialisationDto>("api/serialisation/relance" + (id is null ? "" : $"?id={id}"), null);
    public async Task<string?> EnregistrerParametresSerialisationAsync(bool actif, bool facticeHorsLigne) =>
        (await EnvoyerCorpsAsync<ParametresSerialisationRequete, object>(HttpMethod.Put, "api/serialisation/parametres", new(actif, facticeHorsLigne))).Item2;

    // ---- Bordereaux Chifa / MDN (script 110) ----
    public Task<(PageDto<BordereauDto>?, string?)> BordereauxAsync(long? organismeId, string? etat, string? q, int page, int taille) =>
        GetAsync<PageDto<BordereauDto>?>($"api/chifa/bordereaux?page={page}&taille={taille}&" + Requete(("organismeId", organismeId), ("etat", etat), ("q", q)), null);
    public Task<(CompteursBordereauxDto?, string?)> CompteursBordereauxAsync() => GetAsync<CompteursBordereauxDto?>("api/chifa/bordereaux/compteurs", null);
    public Task<(PageDto<VenteADeposerDto>?, string?)> VentesADeposerAsync(long? organismeId, long? centrePayeurId, string? q, bool retard, int page, int taille,
        bool sansCentre = false) =>
        GetAsync<PageDto<VenteADeposerDto>?>($"api/chifa/bordereaux/a-deposer?page={page}&taille={taille}&"
            + Requete(("organismeId", organismeId), ("centrePayeurId", centrePayeurId), ("q", q), ("retard", retard ? true : null),
                ("sansCentre", sansCentre ? true : null)), null);
    public Task<(FicheBordereauDto?, string?)> BordereauAsync(long id) => GetAsync<FicheBordereauDto?>($"api/chifa/bordereaux/{id}", null);
    public Task<(List<HistoriqueBordereauDto>, string?)> HistoriqueBordereauAsync(long id) =>
        GetAsync<List<HistoriqueBordereauDto>>($"api/chifa/bordereaux/{id}/historique", []);
    public Task<(byte[]?, string?, string?)> BordereauPdfAsync(long id) => FichierAsync(HttpMethod.Get, $"api/chifa/bordereaux/{id}/pdf", "bordereau.pdf");
    public Task<(BordereauDto?, string?)> CreerBordereauAsync(BordereauSaisie s) => PostAsync<BordereauSaisie, BordereauDto>("api/chifa/bordereaux", s);
    public async Task<string?> ModifierBordereauAsync(long id, BordereauSaisie s) =>
        (await EnvoyerCorpsAsync<BordereauSaisie, object>(HttpMethod.Put, $"api/chifa/bordereaux/{id}", s)).Item2;
    public Task<(IdentifiantDto?, string?)> AjouterVentesBordereauAsync(long id, AjoutVentesBordereauRequete r) =>
        PostAsync<AjoutVentesBordereauRequete, IdentifiantDto>($"api/chifa/bordereaux/{id}/ventes", r);
    public Task<(CodeLibelleDto?, string?)> AjouterFactureBordereauAsync(long id, string facture) =>
        PostAsync<AjoutFactureBordereauRequete, CodeLibelleDto>($"api/chifa/bordereaux/{id}/factures", new(facture));
    public async Task<string?> RetirerVenteBordereauAsync(long id, long venteId) =>
        (await EnvoyerCorpsAsync<object?, object>(HttpMethod.Delete, $"api/chifa/bordereaux/{id}/ventes/{venteId}", null)).Item2;
    public async Task<string?> DeposerBordereauAsync(long id, DateOnly? date) =>
        (await PostAsync<DepotBordereauRequete, object>($"api/chifa/bordereaux/{id}/depot", new(date))).Item2;
    public async Task<string?> RejeterFactureChifaAsync(long id, long venteId, RejetChifaRequete r) =>
        (await PostAsync<RejetChifaRequete, object>($"api/chifa/bordereaux/{id}/ventes/{venteId}/rejet", r)).Item2;
    public async Task<string?> TraiterRejetChifaAsync(long id, long venteId, string suite) =>
        (await PostAsync<SuiteRejetRequete, object>($"api/chifa/bordereaux/{id}/ventes/{venteId}/suite", new(suite))).Item2;

    public Task<(RapprochementVoletDto?, string?)> RapprochementVoletAsync(string texte) =>
        PostAsync<LireVoletChifaRequete, RapprochementVoletDto>("api/caisse/volet-chifa/rapprochement", new(texte));
    public async Task<string?> GarderPatientVoletAsync(string texte, long patientId, string? patient, string? numAssure) =>
        (await PostAsync<GarderPatientVoletRequete, System.Text.Json.JsonElement>("api/caisse/volet-chifa/patient-garde", new(texte, patientId, patient, numAssure))).Item2;
    public Task<(VoletRattacheDto?, string?)> RattacherVoletAsync(string texte, long venteId, string? motif, string? regularisation = null) =>
        PostAsync<RattacherVoletRequete, VoletRattacheDto>("api/caisse/volet-chifa/rattacher", new(texte, venteId, motif, regularisation));
    public Task<(PageDto<VenteAttenteVoletDto>?, string?)> VentesAttenteVoletAsync(string? q, int page, int taille) =>
        GetAsync<PageDto<VenteAttenteVoletDto>?>($"api/caisse/ventes-attente-volet?page={page}&taille={taille}"
            + (string.IsNullOrWhiteSpace(q) ? "" : $"&q={Uri.EscapeDataString(q.Trim())}"), null);
    public Task<(PageDto<VoletAvecEcartDto>?, string?)> VoletsAvecEcartAsync(string? q, int page, int taille) =>
        GetAsync<PageDto<VoletAvecEcartDto>?>($"api/caisse/volets-ecart?page={page}&taille={taille}"
            + (string.IsNullOrWhiteSpace(q) ? "" : $"&q={Uri.EscapeDataString(q.Trim())}"), null);
    public Task<(PatientVoletDto?, string?)> CreerPatientVoletAsync(string texte, string? nom, long? organismeId, string? lien = null,
        long? assurePrincipalId = null) =>
        PostAsync<CreerPatientVoletRequete, PatientVoletDto>("api/caisse/volet-chifa/patient", new(texte, nom, organismeId, lien, assurePrincipalId));
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
    // Profils de reprise par logiciel précédent et historique Chifa repris (114)
    public Task<(PageDto<ProfilRepriseDto>?, string?)> ProfilsRepriseAsync(string? texte, string? statut, int page, int taille) =>
        GetAsync<PageDto<ProfilRepriseDto>?>("api/admin/reprise/profils?" + Requete(("q", texte), ("statut", statut), ("page", page), ("taille", taille)), null);
    public Task<(ProfilRepriseDto?, string?)> ProfilRepriseAsync(long id) => GetAsync<ProfilRepriseDto?>($"api/admin/reprise/profils/{id}", null);
    public Task<(FormatsProfilRepriseDto?, string?)> FormatsProfilRepriseAsync() => GetAsync<FormatsProfilRepriseDto?>("api/admin/reprise/profils/formats", null);
    public Task<(IdentifiantDto?, string?)> EnregistrerProfilRepriseAsync(ProfilRepriseDto profil) =>
        PostAsync<ProfilRepriseDto, IdentifiantDto>("api/admin/reprise/profils", profil);
    public Task<(PageDto<HistoriqueChifaDto>?, string?)> HistoriqueChifaAsync(string? texte, DateOnly? du, DateOnly? au, int page, int taille, string? organisme = null) =>
        GetAsync<PageDto<HistoriqueChifaDto>?>("api/reprise/historique-chifa?" + Requete(("q", texte), ("du", du?.ToString("yyyy-MM-dd")), ("au", au?.ToString("yyyy-MM-dd")),
            ("organisme", organisme), ("page", page), ("taille", taille)), null);
    public Task<(PageDto<DoublonImportDto>?, string?)> DoublonsImportAsync(string? statut, string? texte, int page, int taille) =>
        GetAsync<PageDto<DoublonImportDto>?>("api/reprise/doublons-patients?" + Requete(("statut", statut), ("q", texte), ("page", page), ("taille", taille)), null);
    public Task<(IdentifiantDto?, string?)> DeciderDoublonImportAsync(long id, DecisionDoublonImportRequete d) =>
        PostAsync<DecisionDoublonImportRequete, IdentifiantDto>($"api/reprise/doublons-patients/{id}/decision", d);

    // ---- Rapports ----
    /// <summary>Période « dates » : du et au choisis dans le calendrier ; sinon jour, semaine ou mois autour de date.</summary>
    private static string FiltreRapport(string periode, DateOnly date, DateOnly du, DateOnly au) =>
        periode == "dates" ? $"periode=dates&du={du:yyyy-MM-dd}&au={au:yyyy-MM-dd}" : $"periode={periode}&date={date:yyyy-MM-dd}";
    /// <summary>Accueil : ventes du jour et même jour de la semaine précédente à la même heure.</summary>
    public Task<(VentesDuJourDto?, string?)> VentesDuJourAsync() => GetAsync<VentesDuJourDto?>("api/rapports/jour", null);
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

    // ---- Tableau de bord financier (lot 11) : titulaire ou « rapports.marges » ----
    private static string Dormant(int? jours) => jours is { } j ? $"&dormant={j}" : "";
    public Task<(TableauFinancierDto?, string?)> TableauFinancierAsync(string periode, DateOnly date, DateOnly du, DateOnly au, int? dormant) =>
        GetAsync<TableauFinancierDto?>($"api/pilotage/financier?{FiltreRapport(periode, date, du, au)}{Dormant(dormant)}", null);
    public Task<(PageDto<MargeRegroupeeDto>?, string?)> MargesFinancierAsync(string periode, DateOnly date, DateOnly du, DateOnly au, string par,
        string? recherche, int page, int taille) =>
        GetAsync<PageDto<MargeRegroupeeDto>?>($"api/pilotage/financier/marges?{FiltreRapport(periode, date, du, au)}&par={par}&page={page}&taille={taille}"
            + (string.IsNullOrWhiteSpace(recherche) ? "" : $"&recherche={Uri.EscapeDataString(recherche)}"), null);
    public Task<(PageDto<ProduitDormantDto>?, string?)> DormantsFinancierAsync(int? dormant, string? recherche, int page, int taille) =>
        GetAsync<PageDto<ProduitDormantDto>?>($"api/pilotage/financier/dormants?page={page}&taille={taille}{Dormant(dormant)}"
            + (string.IsNullOrWhiteSpace(recherche) ? "" : $"&recherche={Uri.EscapeDataString(recherche)}"), null);
    public Task<(byte[]?, string?, string?)> ExporterTableauFinancierAsync(string periode, DateOnly date, DateOnly du, DateOnly au, int? dormant, string format) =>
        FichierAsync(HttpMethod.Get, $"api/pilotage/financier/export?{FiltreRapport(periode, date, du, au)}{Dormant(dormant)}&format={format}",
            $"tableau_financier.{(format == "excel" ? "xlsx" : "pdf")}");
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

    // ---- Liste des postes autorisés (contrat-api.md § 4 et § 5) -------------------------------------------------------------------------

    /// <summary>État du poste face à la liste (mode, preuve acceptée ou refusée) ; null si le serveur ne répond pas ou ne connaît pas la liste.</summary>
    public async Task<EtatPosteAutoriseDto?> EtatAutorisationPosteAsync()
    {
        try
        {
            using var rep = await http.GetAsync("api/postes/etat");
            var etat = rep.IsSuccessStatusCode ? await rep.Content.ReadFromJsonAsync<EtatPosteAutoriseDto>() : null;
            // Poste renommé par le titulaire : le nom donné par le serveur à ce poste prouvé remplace celui écrit à l'autorisation
            if (identite is not null && NomPosteServeur.NouveauNom(identite.PosteId, identite.Nom, etat) is { } nouveauNom) identite.Renommer(nouveauNom);
            return etat;
        }
        catch (Exception ex) when (EstEchecAppel(ex)) { return null; }
    }

    /// <summary>
    /// Autorise ce poste avec le code donné par le titulaire : nouvelle clé non exportable, preuve de possession, envoi au serveur. Accepté : la clé devient
    /// celle du poste (l'ancienne est supprimée) ; refusé : la nouvelle clé est supprimée. Rend null si accepté, sinon le message à afficher.
    /// </summary>
    public async Task<string?> AutoriserCePosteAsync(string code)
    {
        if (identite is null || horlogeServeur is null) return "Autorisation du poste indisponible sur ce poste.";
        (byte[] Spki, System.Security.Cryptography.ECDsa Cle, string Nom, string Fournisseur) nouvelle;
        // Hors du fil de l'interface : sur un compte standard, la demande d'élévation de Windows attend la réponse de l'utilisateur
        try { nouvelle = await Task.Run(identite.PreparerCle); }
        catch (InvalidOperationException e) { App.NoterErreur(e); return e.Message; }
        try
        {
            // Heure du serveur d'abord (la preuve de possession est horodatée) : l'état du poste porte l'en-tête Date
            await EtatAutorisationPosteAsync();
            var horodatage = horlogeServeur.Unix;
            var preuve = PreuvePoste.Signer(nouvelle.Cle, PreuvePoste.ChaineEnrolement(code, horodatage, nouvelle.Spki));
            using var rep = await http.PostAsJsonAsync("api/postes/enrolement",
                new EnrolementPosteRequete(code, Convert.ToBase64String(nouvelle.Spki), Environment.MachineName, horodatage, preuve));
            if (!rep.IsSuccessStatusCode)
            {
                identite.Abandonner(nouvelle.Nom, nouvelle.Cle);
                return await MessageAsync(rep);
            }
            var r = (await rep.Content.ReadFromJsonAsync<EnrolementPosteReponse>())!;
            identite.Valider(r.PosteId, r.Nom, nouvelle.Nom, nouvelle.Cle, nouvelle.Fournisseur);
            return null;
        }
        catch (Exception ex) when (EstEchecAppel(ex) || ex is IOException or UnauthorizedAccessException)
        {
            identite.Abandonner(nouvelle.Nom, nouvelle.Cle);
            App.NoterErreur(ex);
            return ex is UnauthorizedAccessException or IOException
                ? "Identité du poste non enregistrée (dossier ProgramData › PharmaOfficine › Poste non accessible) : lancez Tracodex Officine une fois en administrateur."
                : MessageEchec(ex);
        }
    }

    public Task<(PageDto<PosteAutoriseDto>?, string?)> PostesAutorisesAsync(string? statut, string? recherche, int page, int taille) =>
        GetAsync<PageDto<PosteAutoriseDto>?>("api/admin/postes?" + Requete(("statut", statut), ("recherche", recherche), ("page", page), ("taille", taille)), null);

    // Stock › Contenance manquante (2026-11-26)
    public Task<(PageDto<ContenanceManquanteDto>?, string?)> ContenancesManquantesAsync(string? recherche, int page, int taille, bool toutes = false) =>
        GetAsync<PageDto<ContenanceManquanteDto>?>("api/catalogue/contenances-manquantes?" + Requete(("recherche", recherche), ("page", page), ("taille", taille),
            ("toutes", toutes ? "true" : null)), null);

    public Task<(byte[]?, string?, string?)> ExporterContenancesManquantesAsync() =>
        FichierAsync(HttpMethod.Get, "api/catalogue/contenances-manquantes/export", "contenances_manquantes.csv");

    public Task<(ApercuImportContenancesDto?, string?)> ApercuImportContenancesAsync(ImportContenancesFichierRequete r) =>
        PostAsync<ImportContenancesFichierRequete, ApercuImportContenancesDto>("api/catalogue/contenances-manquantes/import/apercu", r);

    public Task<(ImportContenancesResultatDto?, string?)> ImporterContenancesAsync(ImportContenancesRequete r) =>
        PostAsync<ImportContenancesRequete, ImportContenancesResultatDto>("api/catalogue/contenances-manquantes/import", r);

    public async Task<string?> EnregistrerContenanceAsync(long produitId, int? contenance, int? avant = null) =>
        (await EnvoyerCorpsAsync<ContenanceSaisieDto, object>(HttpMethod.Put, $"api/catalogue/produits/{produitId}/contenance", new(contenance, avant))).Item2;

    public Task<(ReglagesPostesDto?, string?)> ReglagesPostesAsync() => GetAsync<ReglagesPostesDto?>("api/admin/postes/reglages", null);

    /// <summary>Erreur, ou avertissement du serveur quand le maximum est baissé sous le nombre de postes déjà comptés.</summary>
    public async Task<(string? Erreur, string? Avertissement)> ModifierReglagesPostesAsync(ReglagesPostesDto reglages)
    {
        var (r, e) = await EnvoyerCorpsAsync<ReglagesPostesDto, ReglagesPostesEnregistresDto>(HttpMethod.Put, "api/admin/postes/reglages", reglages with { NomsSansPreuve = null });
        return (e, r?.Avertissement);
    }

    public Task<(CodeEnrolementDto?, string?)> AutoriserPosteAsync(string nom) => PostAsync<NomPosteRequete, CodeEnrolementDto>("api/admin/postes", new(nom));

    public Task<(CodeEnrolementDto?, string?)> NouveauCodePosteAsync(long id) => PostAsync<object, CodeEnrolementDto>($"api/admin/postes/{id}/code", new { });

    public Task<(CodeEnrolementDto?, string?)> RemplacerPostePcAsync(long id) => PostAsync<object, CodeEnrolementDto>($"api/admin/postes/{id}/remplacer", new { });

    public async Task<string?> RenommerPosteAsync(long id, string nom) =>
        (await EnvoyerCorpsAsync<NomPosteRequete, object>(HttpMethod.Put, $"api/admin/postes/{id}/nom", new(nom))).Item2;

    public async Task<string?> RevoquerPosteAsync(long id, string motif) =>
        (await PostAsync<RevocationPosteRequete, object>($"api/admin/postes/{id}/revoquer", new(motif))).Item2;

    private async Task<(T, string?)> GetAsync<T>(string url, T defaut)
    {
        try
        {
            using var rep = await EnvoyerAsync(url);
            if (rep.StatusCode == HttpStatusCode.Unauthorized) return (defaut, SessionExpiree());
            if (!rep.IsSuccessStatusCode) return (defaut, await MessageAsync(rep));
            RetenirReglageCentral(rep);
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
    private const int MaxEssaisAutorisation = 12;

    private async Task<HttpResponseMessage> EnvoyerAutoriseAsync(Func<HttpRequestMessage> creer, bool proposer = true)
    {
        (string Identifiant, string Code)? accord = null;
        // Droits présentés au valideur : le serveur n'accorde que ceux-là (jamais d'autres contrôles de la même opération)
        var droits = new List<string>();
        var validation = validationEnAttente;
        validationEnAttente = null;
        var detail = proposer ? detailEnAttente : null;
        if (proposer) detailEnAttente = null;
        // Lectures (proposer = false) : l'autorisation d'avance attend l'écriture suivante
        if (proposer && accordEnAttente is { } avance)
        {
            accord = (avance.Identifiant, avance.Code);
            droits.Add(avance.Droit);
            accordEnAttente = null;
        }
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
            // Code faux : la fenêtre reste ouverte avec le message du serveur jusqu'au blocage (5 erreurs, refus non autorisable)
            // ou à l'annulation ; la borne ne sert que de garde-fou (plusieurs droits demandés à la suite)
            if (rep.StatusCode != HttpStatusCode.Forbidden || autorisations is null || !proposer || essai >= MaxEssaisAutorisation) return rep;
            var corps = await rep.Content.ReadAsStringAsync();
            var (message, autorisable, permission, seuil) = LireRefus(corps);
            HttpResponseMessage Garder() { var n = new HttpResponseMessage(rep.StatusCode) { Content = new StringContent(corps, System.Text.Encoding.UTF8, "application/json") }; rep.Dispose(); return n; }
            if (!autorisable) return Garder();
            accord = await autorisations.DemanderAsync(message, permission, seuil,
                detail is not null && (detail.Permission is null || detail.Permission == permission) ? detail : null);
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
                    // Réglage devenu imposé depuis l'affichage de l'écran : dire quoi faire
                    if (doc.RootElement.TryGetProperty("champ", out var cr) && cr.GetString() == "reglage_impose")
                        return texte + " Actualisez l’écran : la valeur du serveur central s’affiche et n’est plus modifiable ici.";
                    return texte;
                }
        }
        catch { /* corps vide ou non JSON */ }
        return $"Erreur {(int)rep.StatusCode}.";
    }
}
