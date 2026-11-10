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

    public void Ouvrir(ConnexionReponse r, bool parCode = false)
    {
        (Jeton, NomComplet, ExpireLe, MotifDeconnexion, ParCode, DoitChangerMotDePasse) = (r.Jeton, r.NomComplet, r.ExpireLe, null, parCode, r.DoitChangerMotDePasse);
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

    public void Fermer(string? motif = null)
    {
        Affichage = "auto";
        (Jeton, NomComplet, MotifDeconnexion, ParCode) = (null, null, motif, false);
        Permissions = new HashSet<string>();
        Changee?.Invoke();
    }
}

/// <summary>Lot FEFO trouvé, ou raison de l'absence (rupture, lots écartés).</summary>
public sealed record ReponseLotFefo(LotFefoDto? Lot, IReadOnlyList<LotEcarteDto> Ecartes, string? Erreur);

/// <summary>Appels à l'API locale. Chaque méthode renvoie le résultat ou un message d'erreur lisible.</summary>
public sealed class ApiClient(HttpClient http, SessionPoste session)
{
    private const string Injoignable = "Serveur de la pharmacie injoignable : vérifiez que le PC serveur est allumé.";

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
        catch (HttpRequestException)
        {
            return Injoignable;
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
        catch (HttpRequestException)
        {
            return Injoignable;
        }
    }

    /// <summary>Employés proposés pour le changement d'utilisateur (vide si la caisse du poste est fermée).</summary>
    public Task<(ComptoirDto?, string?)> ComptoirAsync() => GetAsync<ComptoirDto?>("api/auth/comptoir", null);

    public async Task<string?> DefinirCodeComptoirAsync(string code) =>
        (await EnvoyerCorpsAsync<CodeComptoirRequete, object>(HttpMethod.Put, "api/auth/code-comptoir", new(code))).Item2;

    public async Task<string?> EffacerCodeComptoirAsync(long utilisateurId) =>
        (await EnvoyerCorpsAsync<object?, object>(HttpMethod.Delete, $"api/admin/utilisateurs/{utilisateurId}/code-comptoir", null)).Item2;

    public Task<(ReleveDto?, string?)> ReleveCaisseAsync(Dictionary<string, decimal> saisies) =>
        PostAsync<ClotureRequete, ReleveDto>("api/caisse/releve", new(saisies));

    // ---- Annulation d'une vente ----
    public Task<(List<VenteAnnulableDto>, string?)> VentesAnnulablesAsync(string? numero) =>
        GetAsync<List<VenteAnnulableDto>>($"api/caisse/ventes{(string.IsNullOrWhiteSpace(numero) ? "" : $"?numero={Uri.EscapeDataString(numero.Trim())}")}", []);

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
    public Task<(List<FactureResumeDto>, string?)> FacturesAsync(string? q, string? destinataire, string? suivi, DateOnly? du, DateOnly? au) =>
        GetAsync<List<FactureResumeDto>>("api/factures?" + string.Join("&", new[]
        {
            string.IsNullOrWhiteSpace(q) ? null : $"q={Uri.EscapeDataString(q.Trim())}", destinataire is null ? null : $"destinataire={destinataire}",
            suivi is null ? null : $"suivi={suivi}", du is { } d ? $"du={d:yyyy-MM-dd}" : null, au is { } a ? $"au={a:yyyy-MM-dd}" : null
        }.Where(x => x is not null)), []);
    public Task<(FactureDetailDto?, string?)> FactureAsync(long id, int? version = null) =>
        GetAsync<FactureDetailDto?>($"api/factures/{id}{(version is { } v ? $"?version={v}" : "")}", null);
    public Task<(byte[]?, string?, string?)> FacturePdfAsync(long id, string numero, int? version = null) =>
        FichierAsync(HttpMethod.Get, $"api/factures/{id}/pdf{(version is { } v ? $"?version={v}" : "")}", $"{numero}.pdf");
    public Task<(FactureEmiseDto?, string?)> EmettreFactureAsync(DemandeFactureDto d) => PostAsync<DemandeFactureDto, FactureEmiseDto>("api/factures", d);
    public async Task<string?> RejeterFactureAsync(long id, string? motif) =>
        (await EnvoyerCorpsAsync<MotifRequete, object>(HttpMethod.Post, $"api/factures/{id}/rejet", new(motif))).Item2;
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
    public Task<(List<CompteTresorerieDto>, string?)> ComptesTresorerieAsync() => GetAsync<List<CompteTresorerieDto>>("api/tresorerie/comptes", []);
    public Task<(List<MouvementTresorerieDto>, string?)> MouvementsTresorerieAsync(long compteId, DateOnly du, DateOnly au) =>
        GetAsync<List<MouvementTresorerieDto>>($"api/tresorerie/comptes/{compteId}/mouvements?du={du:yyyy-MM-dd}&au={au:yyyy-MM-dd}", []);
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

    // ---- Règlements des grossistes et crédits clients ----
    public Task<(List<GrossisteAReglementDto>, string?)> GrossistesAReglerAsync() => GetAsync<List<GrossisteAReglementDto>>("api/achats/reglements", []);
    public Task<(SituationGrossisteDto?, string?)> SituationGrossisteAsync(long id) => GetAsync<SituationGrossisteDto?>($"api/achats/reglements/{id}", null);
    public async Task<string?> EnregistrerReglementAsync(long fournisseurId, ReglementRequete r) =>
        (await PostAsync<ReglementRequete, IdentifiantDto>($"api/achats/reglements/{fournisseurId}", r)).Item2;

    public Task<(List<PatientEnCreditDto>, string?)> PatientsEnCreditAsync(string? recherche) =>
        GetAsync<List<PatientEnCreditDto>>($"api/caisse/credits{(string.IsNullOrWhiteSpace(recherche) ? "" : $"?q={Uri.EscapeDataString(recherche.Trim())}")}", []);
    public Task<(SituationCreditDto?, string?)> SituationCreditAsync(long patientId) => GetAsync<SituationCreditDto?>($"api/caisse/credits/{patientId}", null);
    public Task<(RecuCreditDto?, string?)> ReglerCreditAsync(long patientId, ReglementCreditRequete r) =>
        PostAsync<ReglementCreditRequete, RecuCreditDto>($"api/caisse/credits/{patientId}/reglement", r);

    // ---- Ventes en attente du poste ----
    public Task<(List<VenteEnAttenteDto>, string?)> VentesEnAttenteAsync() => GetAsync<List<VenteEnAttenteDto>>("api/caisse/attente", []);

    public async Task<string?> MettreEnAttenteAsync(MiseEnAttenteRequete r) =>
        (await PostAsync<MiseEnAttenteRequete, IdentifiantDto>("api/caisse/attente", r)).Item2;

    /// <summary>Contenu JSON du panier repris (retiré de l'attente).</summary>
    public async Task<(string?, string?)> ReprendreVenteEnAttenteAsync(long id)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, $"api/caisse/attente/{id}/reprendre");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.Jeton);
            using var rep = await http.SendAsync(req);
            if (rep.StatusCode == HttpStatusCode.Unauthorized) return (null, SessionExpiree());
            if (!rep.IsSuccessStatusCode) return (null, await MessageAsync(rep));
            return (await rep.Content.ReadAsStringAsync(), null);
        }
        catch (HttpRequestException) { return (null, Injoignable); }
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
        catch (HttpRequestException) { return (null, Injoignable); }
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

    public Task<(IdentifiantDto?, string?)> OuvrirCaisseAsync(string poste, decimal? fond) =>
        PostAsync<OuvrirCaisseRequete, IdentifiantDto>("api/caisse/ouvrir", new(poste, fond));

    public Task<(ApercuVenteDto?, string?)> CalculerVenteAsync(CommandeVenteDto commande) =>
        PostAsync<CommandeVenteDto, ApercuVenteDto>("api/caisse/calculer", commande);

    public Task<(VenteEncaisseeDto?, string?)> EncaisserAsync(CommandeVenteDto commande) =>
        PostAsync<CommandeVenteDto, VenteEncaisseeDto>("api/caisse/encaisser", commande);

    public Task<(ClotureDto?, string?)> CloturerCaisseAsync(Dictionary<string, decimal> saisies) =>
        PostAsync<ClotureRequete, ClotureDto>("api/caisse/cloturer", new(saisies));

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
        catch (HttpRequestException)
        {
            return (null, null, Injoignable);
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
        catch (HttpRequestException)
        {
            return (null, Injoignable);
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
    public Task<(NombreDto?, string?)> EnvoyerRapportMaintenantAsync() => PostAsync<object, NombreDto>("api/admin/notifications/rapport", new());
    public Task<(List<MessageSortantDto>, string?)> FileMessagesAsync() => GetAsync<List<MessageSortantDto>>("api/admin/notifications/file", []);

    public Task<(EtatSauvegardesDto?, string?)> SauvegardesAsync() => GetAsync<EtatSauvegardesDto?>("api/admin/sauvegardes", null);
    public async Task<string?> ModifierReglagesSauvegardeAsync(ReglagesSauvegardeDto r) =>
        (await EnvoyerCorpsAsync<ReglagesSauvegardeDto, object>(HttpMethod.Put, "api/admin/sauvegardes/reglages", r)).Item2;
    public Task<(ResultatSauvegardeDto?, string?)> LancerSauvegardeAsync() => PostAsync<object, ResultatSauvegardeDto>("api/admin/sauvegardes/lancer", new());

    public Task<(List<EntreeJournalDto>, string?)> JournalAsync() => GetAsync<List<EntreeJournalDto>>("api/admin/journal", []);

    // ---- Alertes, stock, traçabilité -------------------------------------------

    public Task<(List<AlerteDto>, string?)> AlertesAsync() => GetAsync<List<AlerteDto>>("api/alertes", []);
    public Task<(CompteDto?, string?)> AlertesUrgentesAsync() => GetAsync<CompteDto?>("api/alertes/urgentes", null);

    public Task<(List<ProduitDto>, string?)> RechercherProduitsAsync(string q) =>
        GetAsync<List<ProduitDto>>($"api/produits?q={Uri.EscapeDataString(q)}", []);

    // ---- Inventaire tournant ----
    public Task<(TableauInventaireDto?, string?)> TableauInventaireAsync() => GetAsync<TableauInventaireDto?>("api/inventaires", null);
    public Task<(IdentifiantDto?, string?)> CommencerInventaireAsync(string rayon) => PostAsync<CommencerInventaireRequete, IdentifiantDto>("api/inventaires", new(rayon));
    public Task<(InventaireDto?, string?)> InventaireAsync(long id) => GetAsync<InventaireDto?>($"api/inventaires/{id}", null);
    public async Task<string?> CompterLotAsync(long id, long ligneId, int compte, DateOnly? dateVerifiee) =>
        (await EnvoyerCorpsAsync<ComptageRequete, object>(HttpMethod.Put, $"api/inventaires/{id}/lignes/{ligneId}", new(compte, dateVerifiee))).Item2;
    public Task<(ScanInventaireDto?, string?)> ScannerInventaireAsync(long id, string code) => PostAsync<ScanInventaireRequete, ScanInventaireDto>($"api/inventaires/{id}/scan", new(code));
    public Task<(BilanInventaireDto?, string?)> TerminerInventaireAsync(long id) => PostAsync<object, BilanInventaireDto>($"api/inventaires/{id}/terminer", new());
    public async Task<string?> AnnulerInventaireAsync(long id) => (await PostAsync<object, object>($"api/inventaires/{id}/annuler", new())).Item2;
    public async Task<string?> DeciderEcartAsync(long ligneId, bool valider) => (await PostAsync<DecisionEcartRequete, object>($"api/inventaires/ecarts/{ligneId}", new(valider))).Item2;
    public async Task<string?> ParametresInventaireAsync(ParametresInventaireDto p) =>
        (await EnvoyerCorpsAsync<ParametresInventaireDto, object>(HttpMethod.Put, "api/inventaires/parametres", p)).Item2;

    public Task<(List<EmplacementDto>, string?)> EmplacementsAsync() => GetAsync<List<EmplacementDto>>("api/emplacements", []);
    public Task<(IdentifiantDto?, string?)> EnregistrerEmplacementAsync(long? id, SaisieEmplacementDto e) => id is null
        ? PostAsync<SaisieEmplacementDto, IdentifiantDto>("api/emplacements", e)
        : EnvoyerCorpsAsync<SaisieEmplacementDto, IdentifiantDto>(HttpMethod.Put, $"api/emplacements/{id}", e);
    public async Task<string?> RangerProduitAsync(long produitId, long? emplacementId, long? reserveId) =>
        (await EnvoyerCorpsAsync<RangementDto, object>(HttpMethod.Put, $"api/produits/{produitId}/emplacement", new(emplacementId, reserveId))).Item2;

    public Task<(IdentifiantDto?, string?)> CreerProduitAsync(NouveauProduitDto p) => PostAsync<NouveauProduitDto, IdentifiantDto>("api/produits", p);
    public Task<(List<string>, string?)> DciAsync(string q) => GetAsync<List<string>>($"api/dci?q={Uri.EscapeDataString(q)}", []);

    public Task<(List<ProduitAlternatifDto>, string?)> AlternativesAsync(long produitId) =>
        GetAsync<List<ProduitAlternatifDto>>($"api/produits/{produitId}/alternatives", []);

    public Task<(EtatStockDto?, string?)> StockAsync() => GetAsync<EtatStockDto?>("api/stock", null);

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

    /// <summary>Le 404 n'est pas une erreur ici : il signifie « aucun lot vendable » et liste les lots écartés.</summary>
    /// <summary>Boîte scannée : null si le code ne désigne pas de lot (code-barres du fabricant).</summary>
    public Task<(BoiteCaisseDto?, string?)> BoiteCaisseAsync(string code) => PostAsync<CodeRequete, BoiteCaisseDto>("api/caisse/boite", new(code));

    public async Task<ReponseLotFefo> LotFefoAsync(long produitId)
    {
        try
        {
            using var rep = await EnvoyerAsync($"api/produits/{produitId}/lot-fefo");
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
        catch (HttpRequestException)
        {
            return new(null, [], Injoignable);
        }
    }

    // ---- Achats ------------------------------------------------------------------

    public Task<(List<FournisseurDto>, string?)> FournisseursAsync() => GetAsync<List<FournisseurDto>>("api/achats/fournisseurs", []);
    public Task<(List<CommandeAchatDto>, string?)> CommandesAchatAsync() => GetAsync<List<CommandeAchatDto>>("api/achats/commandes", []);
    public Task<(CommandeAchatDetailDto?, string?)> CommandeAchatAsync(long id) => GetAsync<CommandeAchatDetailDto?>($"api/achats/commandes/{id}", null);
    public Task<(List<SuggestionCommandeDto>, string?)> SuggestionsCommandeAsync() => GetAsync<List<SuggestionCommandeDto>>("api/achats/suggestions", []);

    public Task<(IdentifiantDto?, string?)> AjouterACommandeAsync(long fournisseurId, long produitId, int quantite, decimal? prixHt) =>
        PostAsync<AjoutCommandeRequete, IdentifiantDto>("api/achats/commandes/lignes", new(fournisseurId, produitId, quantite, prixHt));

    public async Task<string?> ModifierLigneCommandeAsync(long commandeId, long ligneId, int quantite, decimal prixHt) =>
        (await EnvoyerCorpsAsync<ModificationLigneCommandeRequete, object>(HttpMethod.Put, $"api/achats/commandes/{commandeId}/lignes/{ligneId}", new(quantite, prixHt))).Item2;

    public Task<(byte[]?, string?, string?)> EmettreCommandeAsync(long id) => FichierAsync(HttpMethod.Post, $"api/achats/commandes/{id}/emettre", $"commande_{id}.pdf");
    public Task<(byte[]?, string?, string?)> BonCommandeAsync(long id) => FichierAsync(HttpMethod.Get, $"api/achats/commandes/{id}/pdf", $"commande_{id}.pdf");
    public async Task<string?> AnnulerCommandeAsync(long id) => (await PostAsync<object, object>($"api/achats/commandes/{id}/annuler", new())).Item2;

    public Task<(List<ReceptionResumeDto>, string?)> ReceptionsAsync() => GetAsync<List<ReceptionResumeDto>>("api/achats/receptions", []);
    public Task<(BoiteLueDto?, string?)> LireDataMatrixAsync(string code) => PostAsync<DataMatrixRequete, BoiteLueDto>("api/achats/datamatrix", new(code));
    public Task<(List<ConflitReceptionDto>?, string?)> ConflitsReceptionAsync(ReceptionRequete reception) =>
        PostAsync<ReceptionRequete, List<ConflitReceptionDto>>("api/achats/receptions/conflits", reception);
    public Task<(ReceptionEnregistreeDto?, string?)> ReceptionnerAsync(ReceptionRequete reception) =>
        PostAsync<ReceptionRequete, ReceptionEnregistreeDto>("api/achats/receptions", reception);

    public Task<(List<RetourDto>, string?)> RetoursAsync() => GetAsync<List<RetourDto>>("api/achats/retours", []);
    public Task<(List<LotRetournableDto>, string?)> LotsRetournablesAsync(long produitId, string? numeroLot) =>
        GetAsync<List<LotRetournableDto>>($"api/achats/lots?produitId={produitId}" + (numeroLot is null ? "" : "&numeroLot=" + Uri.EscapeDataString(numeroLot)), []);
    public Task<(LotRetournableDto?, string?)> LotRetournableAsync(long lotId) => GetAsync<LotRetournableDto?>($"api/achats/lots/{lotId}/retour", null);
    public Task<(IdentifiantDto?, string?)> PreparerRetourAsync(PreparationRetourRequete retour) =>
        PostAsync<PreparationRetourRequete, IdentifiantDto>("api/achats/retours", retour);
    public Task<(byte[]?, string?, string?)> EnvoyerRetourAsync(long id) => FichierAsync(HttpMethod.Post, $"api/achats/retours/{id}/envoyer", $"retour_{id}.pdf");
    public Task<(byte[]?, string?, string?)> BonRetourAsync(long id) => FichierAsync(HttpMethod.Get, $"api/achats/retours/{id}/pdf", $"retour_{id}.pdf");

    // ---- Reprise de données (titulaire) ----
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
    public Task<(byte[]?, string?, string?)> ExporterRapportAsync(string periode, DateOnly date, DateOnly du, DateOnly au, string format) =>
        FichierAsync(HttpMethod.Get, $"api/rapports/export?{FiltreRapport(periode, date, du, au)}&format={format}", $"rapport.{(format == "excel" ? "csv" : "pdf")}");
    public async Task<string?> EnregistrerAvoirAsync(long id, decimal montant) => (await PostAsync<AvoirRequete, object>($"api/achats/retours/{id}/avoir", new(montant))).Item2;
    public async Task<string?> RefuserRetourAsync(long id, string? motif) => (await PostAsync<RefusRetourRequete, object>($"api/achats/retours/{id}/refuser", new(motif))).Item2;

    public Task<(List<LotAEtiqueterDto>, string?)> LotsAEtiqueterAsync() => GetAsync<List<LotAEtiqueterDto>>("api/achats/etiquettes/lots", []);
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
            using var req = new HttpRequestMessage(methode, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.Jeton);
            using var rep = await http.SendAsync(req);
            if (rep.StatusCode == HttpStatusCode.Unauthorized) return (null, null, SessionExpiree());
            if (!rep.IsSuccessStatusCode) return (null, null, await MessageAsync(rep));
            var nom = rep.Content.Headers.ContentDisposition?.FileNameStar
                      ?? rep.Content.Headers.ContentDisposition?.FileName?.Trim('"') ?? nomParDefaut;
            return (await rep.Content.ReadAsByteArrayAsync(), nom, null);
        }
        catch (HttpRequestException)
        {
            return (null, null, Injoignable);
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
        catch (HttpRequestException)
        {
            return (defaut, Injoignable);
        }
    }

    /// <summary>POST JSON. Une réponse 204 (sans contenu) renvoie default.</summary>
    private Task<(TRep?, string?)> PostAsync<TReq, TRep>(string url, TReq corps) =>
        EnvoyerCorpsAsync<TReq, TRep>(HttpMethod.Post, url, corps);

    private async Task<(TRep?, string?)> EnvoyerCorpsAsync<TReq, TRep>(HttpMethod methode, string url, TReq corps)
    {
        try
        {
            using var req = new HttpRequestMessage(methode, url) { Content = JsonContent.Create(corps) };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.Jeton);
            using var rep = await http.SendAsync(req);
            if (rep.StatusCode == HttpStatusCode.Unauthorized) return (default, SessionExpiree());
            if (!rep.IsSuccessStatusCode) return (default, await MessageAsync(rep));
            if (rep.StatusCode == HttpStatusCode.NoContent) return (default, null);
            return (await rep.Content.ReadFromJsonAsync<TRep>(), null);
        }
        catch (HttpRequestException)
        {
            return (default, Injoignable);
        }
    }

    private Task<HttpResponseMessage> EnvoyerAsync(string url)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.Jeton);
        return http.SendAsync(req);
    }

    private string SessionExpiree()
    {
        session.Fermer("Session expirée : reconnectez-vous.");
        return "Session expirée : reconnectez-vous.";
    }

    private static async Task<string> MessageAsync(HttpResponseMessage rep)
    {
        try
        {
            // ErreurDto { message } ou ProblemDetails { detail }
            using var doc = System.Text.Json.JsonDocument.Parse(await rep.Content.ReadAsStringAsync());
            foreach (var nom in new[] { "message", "detail", "title" })
                if (doc.RootElement.TryGetProperty(nom, out var v) && v.GetString() is { Length: > 0 } texte)
                    return texte;
        }
        catch { /* corps vide ou non JSON */ }
        return $"Erreur {(int)rep.StatusCode}.";
    }
}
