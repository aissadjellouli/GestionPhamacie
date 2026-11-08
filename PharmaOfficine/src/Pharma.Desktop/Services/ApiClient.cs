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

    /// <summary>Droits de l'employé, pour griser ce qui lui est refusé (le serveur revérifie toujours).</summary>
    public IReadOnlySet<string> Permissions { get; private set; } = new HashSet<string>();

    /// <summary>Déconnexion automatique après ce nombre de minutes d'inactivité (0 = jamais).</summary>
    public int DeconnexionMinutes { get; private set; }

    /// <summary>Message affiché sur l'écran de connexion (ex. déconnexion automatique).</summary>
    public string? MotifDeconnexion { get; private set; }

    public bool A(string permission) => Permissions.Contains(permission);

    /// <summary>Prévient l'en-tête quand l'employé se connecte, se déconnecte ou que ses droits sont chargés.</summary>
    public event Action? Changee;

    public void Ouvrir(ConnexionReponse r)
    {
        (Jeton, NomComplet, ExpireLe, MotifDeconnexion) = (r.Jeton, r.NomComplet, r.ExpireLe, null);
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

    public void Fermer(string? motif = null)
    {
        (Jeton, NomComplet, MotifDeconnexion) = (null, null, motif);
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

    /// <summary>Droits de l'employé et réglage de déconnexion automatique, juste après la connexion.</summary>
    public async Task ChargerDroitsAsync()
    {
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
    public async Task<string?> EnregistrerAvoirAsync(long id, decimal montant) => (await PostAsync<AvoirRequete, object>($"api/achats/retours/{id}/avoir", new(montant))).Item2;
    public async Task<string?> RefuserRetourAsync(long id, string? motif) => (await PostAsync<RefusRetourRequete, object>($"api/achats/retours/{id}/refuser", new(motif))).Item2;

    public Task<(List<LotAEtiqueterDto>, string?)> LotsAEtiqueterAsync() => GetAsync<List<LotAEtiqueterDto>>("api/achats/etiquettes/lots", []);
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
