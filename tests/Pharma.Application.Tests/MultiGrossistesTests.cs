using Pharma.Application.Achats;
using Pharma.Application.Commun;
using Pharma.Application.Financements;
using Pharma.Application.Notifications;
using Pharma.Domain.Commun;
using Fournisseur = Pharma.Application.Achats.Fournisseur;

namespace Pharma.Application.Tests;

/// <summary>Catalogue des grossistes en mémoire (lot 6).</summary>
internal sealed class CatalogueGrossistesEnMemoire : ICatalogueGrossistes
{
    public Dictionary<long, ProfilCatalogue> Profils { get; } = [];
    public Dictionary<string, long> CodesBarres { get; } = [];
    public Dictionary<(long Fournisseur, string Code), long> CodesGrossiste { get; } = [];
    public List<OffreGrossiste> Offres { get; } = [];
    public List<LigneCatalogueRapprochee>? Importees { get; private set; }
    public (int Lues, int Ignorees)? Compteurs { get; private set; }
    public (long Commande, string Adresse)? EnvoiNote { get; private set; }

    public Task<ProfilCatalogue?> ProfilAsync(long fournisseurId, CancellationToken ct = default) => Task.FromResult(Profils.GetValueOrDefault(fournisseurId));
    public Task EnregistrerProfilAsync(ProfilCatalogue profil, long utilisateurId, CancellationToken ct = default)
    {
        Profils[profil.FournisseurId] = profil;
        return Task.CompletedTask;
    }
    public Task<IReadOnlyDictionary<string, long>> ProduitsParCodeBarreAsync(IReadOnlyCollection<string> codes, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyDictionary<string, long>>(codes.Where(CodesBarres.ContainsKey).ToDictionary(c => c, c => CodesBarres[c]));
    public Task<IReadOnlyDictionary<string, long>> ProduitsParCodeGrossisteAsync(long fournisseurId, IReadOnlyCollection<string> codes, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyDictionary<string, long>>(codes.Where(c => CodesGrossiste.ContainsKey((fournisseurId, c))).ToDictionary(c => c, c => CodesGrossiste[(fournisseurId, c)]));
    public Task<long> EnregistrerImportAsync(long fournisseurId, string nomFichier, long utilisateurId, int lignesLues,
        IReadOnlyList<LigneCatalogueRapprochee> lignes, int lignesIgnorees, CancellationToken ct = default)
    {
        (Importees, Compteurs) = (lignes.ToList(), (lignesLues, lignesIgnorees));
        return Task.FromResult(42L);
    }
    public Task<Page<ImportCatalogue>> ImportsAsync(long? fournisseurId, DemandePage page, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<IReadOnlyList<OffreGrossiste>> OffresAsync(IReadOnlyCollection<long> produitIds, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<OffreGrossiste>>(Offres.Where(o => produitIds.Contains(o.ProduitId)).ToList());
    public Task MarquerEnvoiEmailAsync(long commandeId, string adresse, DateTime quandUtc, CancellationToken ct = default)
    {
        EnvoiNote = (commandeId, adresse);
        return Task.CompletedTask;
    }
}

internal sealed class TableauFactice(params string?[][] lignes) : ILecteurTableau
{
    public IReadOnlyList<IReadOnlyList<string?>> Lire(string nomFichier, byte[] contenu) => lignes;
}

/// <summary>Envoi e-mail factice : rien ne part réellement.</summary>
internal sealed class PieceJointeEspion(bool enPanne = false) : IEnvoiEmailPieceJointe
{
    public List<(string Adresse, string Objet, string NomPieceJointe, byte[] Contenu, string Type)> Envoyes { get; } = [];
    public Task EnvoyerAsync(ConnexionSmtp connexion, string adresse, string objet, string texte, string nomPieceJointe, byte[] pieceJointe,
        string typeMime, CancellationToken ct = default)
    {
        if (enPanne) throw new InvalidOperationException("serveur injoignable");
        Envoyes.Add((adresse, objet, nomPieceJointe, pieceJointe, typeMime));
        return Task.CompletedTask;
    }
}

public class MultiGrossistesTests
{
    private static readonly ProfilCatalogue Profil = new(1, "Réf", "EAN", "Désignation", "Prix HT", "Stock", "Délai", "rupture;non", "oui;dispo");

    private static OffreGrossiste Offre(long produit, long grossiste, decimal? prix, bool? dispo, int? delai = null, decimal? dernier = null) =>
        new(produit, grossiste, $"Grossiste {grossiste}", prix, dispo, null, delai, prix is null ? null : DateTime.UtcNow, dernier, dernier is null ? null : DateTime.UtcNow);

    // ---- Lecture du fichier selon le profil ----

    [Fact]
    public void Lecture_suit_le_profil_du_grossiste_sans_tenir_compte_des_accents_ni_des_majuscules()
    {
        var lu = LectureCatalogueGrossiste.Lire([
            ["ref", "ean", "DESIGNATION", "prix ht", "stock", "delai"],
            ["A1", "6130000000011", "Amlodipine 5", "1 234,50", "12", "2"],
            ["A2", "", "Metformine", "99.9", "rupture", ""],
            ["", "", "", "", "", ""],
            ["A3", "6130000000035", "Doliprane", "abc", "Oui", "demain"]
        ], Profil);

        Assert.Equal(3, lu.Lignes.Count);
        var a1 = lu.Lignes[0];
        Assert.Equal(("A1", "6130000000011", 1234.50m, true, 12, 2), (a1.Code, a1.CodeBarre, a1.PrixHt!.Value, a1.Disponible!.Value, a1.QuantiteDisponible!.Value, a1.DelaiJours!.Value));
        Assert.Equal((false, (int?)null, 99.9m), (lu.Lignes[1].Disponible!.Value, lu.Lignes[1].QuantiteDisponible, lu.Lignes[1].PrixHt!.Value));
        // Prix et délai illisibles : null et signalés ; « Oui » reconnu (liste « disponible »)
        Assert.Equal(5, lu.Lignes[2].NumeroLigne);
        Assert.Null(lu.Lignes[2].PrixHt);
        Assert.True(lu.Lignes[2].Disponible);
        Assert.Equal(2, lu.Anomalies.Count(a => a.NumeroLigne == 5));
    }

    [Fact]
    public void Lecture_ne_suppose_rien_disponibilite_non_reconnue_inconnue_et_signalee()
    {
        var lu = LectureCatalogueGrossiste.Lire([["Réf", "Stock"], ["A1", "sur commande"]], Profil with { ColonneCodeBarre = null, ColonneDesignation = null,
            ColonnePrixHt = null, ColonneDelai = null });
        Assert.Null(lu.Lignes.Single().Disponible);
        Assert.Contains("non reconnue", lu.Anomalies.Single().Message);
    }

    [Fact]
    public void Lecture_refusee_sans_colonne_de_rapprochement_ou_si_une_colonne_manque()
    {
        Assert.Contains("Réglez d’abord", Assert.Throws<RegleMetierException>(() =>
            LectureCatalogueGrossiste.Lire([["a"]], new ProfilCatalogue(1, null, null, "Désignation", null, null, null, null, null))).Message);
        var e = Assert.Throws<RegleMetierException>(() => LectureCatalogueGrossiste.Lire([["Réf", "EAN"], ["A1", "1"]], Profil));
        Assert.Contains("« Désignation »", e.Message);
        Assert.Contains("Colonnes lues : « Réf », « EAN »", e.Message);
    }

    [Theory]
    [InlineData("1 234,50", 1234.50)]
    [InlineData("1234.5", 1234.5)]
    [InlineData("1,234.50", 1234.50)]
    [InlineData("1.234,50", 1234.50)]
    [InlineData("250 DA", 250)]
    public void Nombre_ecrit_a_la_francaise_ou_a_l_anglaise(string texte, double attendu) =>
        Assert.Equal((decimal)attendu, LectureCatalogueGrossiste.Nombre(texte));

    // ---- Grossiste proposé ----

    [Fact]
    public void Proposition_meilleur_prix_parmi_les_grossistes_non_indisponibles()
    {
        var p = RegleChoixGrossiste.Proposer([Offre(1, 1, 80m, false), Offre(1, 2, 100m, true), Offre(1, 3, null, null, dernier: 95m)]);
        Assert.Equal((3L, RegleChoixGrossiste.MeilleurPrix), (p.FournisseurId!.Value, p.Motif));
    }

    [Fact]
    public void Proposition_a_prix_egal_plus_court_delai_sans_prix_disponibilite_indisponible_partout_aucune()
    {
        Assert.Equal(2L, RegleChoixGrossiste.Proposer([Offre(1, 1, 100m, true, 5), Offre(1, 2, 100m, true, 1)]).FournisseurId);
        var sansPrix = RegleChoixGrossiste.Proposer([Offre(1, 1, null, null), Offre(1, 2, null, true)]);
        Assert.Equal((2L, RegleChoixGrossiste.Disponibilite), (sansPrix.FournisseurId!.Value, sansPrix.Motif));
        Assert.Equal(new PropositionGrossiste(null, RegleChoixGrossiste.IndisponiblePartout), RegleChoixGrossiste.Proposer([Offre(1, 1, 50m, false)]));
        Assert.Equal(new PropositionGrossiste(null, RegleChoixGrossiste.AucuneInformation), RegleChoixGrossiste.Proposer([]));
    }

    [Fact]
    public async Task Comparaison_page_des_produits_a_commander_avec_offres_et_proposition()
    {
        var achats = new AchatsEnMemoire();
        achats.Suggestions.AddRange([new SuggestionCommande(4, "Amlodipine", "rupture", 0, 30, null, null, null),
            new SuggestionCommande(5, "Metformine", "critique", 2, 10, null, null, null)]);
        var catalogue = new CatalogueGrossistesEnMemoire();
        catalogue.Offres.AddRange([Offre(4, 1, 120m, true), Offre(4, 2, 110m, true), Offre(5, 1, 50m, true)]);
        var page = await new ComparerGrossistes(achats, catalogue).PageAsync(null, "rupture", new DemandePage(1, 10));
        var ligne = Assert.Single(page.Lignes);
        Assert.Equal((1, 2, 2L), (page.Total, ligne.Offres.Count, ligne.Proposition.FournisseurId!.Value));
    }

    // ---- Import ----

    private static ImporterCatalogueGrossiste Importer(CatalogueGrossistesEnMemoire catalogue, JournalEspion journal, params string?[][] lignes) =>
        new(catalogue, new AchatsEnMemoire(), [new GrossisteConnecteurFichier(new TableauFactice(lignes))], new DroitsFactices("achats.commander"), journal);

    [Fact]
    public async Task Import_rapproche_par_code_barres_puis_par_code_du_grossiste_et_ne_cree_rien()
    {
        var catalogue = new CatalogueGrossistesEnMemoire();
        catalogue.Profils[1] = Profil;
        catalogue.CodesBarres["6130000000011"] = 4;
        catalogue.CodesGrossiste[(1, "B2")] = 5;
        var journal = new JournalEspion();
        var bilan = await Importer(catalogue, journal,
            ["Réf", "EAN", "Désignation", "Prix HT", "Stock", "Délai"],
            ["A1", "6130000000011", "Amlodipine", "100", "3", "1"],
            ["B2", "", "Metformine", "50", "0", ""],
            ["Z9", "9999999999999", "Inconnu", "10", "1", ""],
            ["A1", "6130000000011", "Amlodipine", "90", "4", "1"])
            .ExecuterAsync(1, new FichierGrossiste("catalogue.csv", [1]), 5);

        Assert.Equal((4, 2, 1), (bilan.LignesLues, bilan.LignesRapprochees, bilan.LignesIgnorees));
        Assert.Equal("ligne 4 : Inconnu", bilan.NonRapprochees.Single());
        // Même produit deux fois : la dernière ligne l'emporte, signalée
        Assert.Equal(90m, catalogue.Importees!.Single(l => l.ProduitId == 4).Ligne.PrixHt);
        Assert.Contains(bilan.Anomalies, a => a.NumeroLigne == 5);
        Assert.False(catalogue.Importees!.Single(l => l.ProduitId == 5).Ligne.Disponible);
        Assert.Equal(["import_catalogue_grossiste"], journal.Actions);
    }

    [Fact]
    public async Task Import_refuse_sans_profil_ou_sans_droit()
    {
        var catalogue = new CatalogueGrossistesEnMemoire();
        var e = await Assert.ThrowsAsync<RegleMetierException>(() =>
            Importer(catalogue, new JournalEspion(), ["Réf"]).ExecuterAsync(1, new FichierGrossiste("c.csv", [1]), 5));
        Assert.Contains("Réglez d’abord", e.Message);
        await Assert.ThrowsAnyAsync<Exception>(() => new ImporterCatalogueGrossiste(catalogue, new AchatsEnMemoire(), [], new DroitsFactices(), new JournalEspion())
            .ExecuterAsync(1, new FichierGrossiste("c.csv", [1]), 5));
        Assert.Null(catalogue.Importees);
    }

    [Fact]
    public async Task Profil_refuse_sans_code_ni_code_barres_ou_avec_une_colonne_choisie_deux_fois()
    {
        var catalogue = new CatalogueGrossistesEnMemoire();
        var enregistrer = new EnregistrerProfilCatalogue(catalogue, new AchatsEnMemoire(), new DroitsFactices("achats.commander"), new JournalEspion());
        await Assert.ThrowsAsync<RegleMetierException>(() => enregistrer.ExecuterAsync(new ProfilCatalogue(1, " ", null, "D", null, null, null, null, null), 5));
        await Assert.ThrowsAsync<RegleMetierException>(() => enregistrer.ExecuterAsync(new ProfilCatalogue(1, "Réf", "ref", null, null, null, null, null, null), 5));
        await enregistrer.ExecuterAsync(Profil with { ColonneDelai = "  " }, 5);
        Assert.Null(catalogue.Profils[1].ColonneDelai);
    }

    // ---- Répartition : une commande répartie sur plusieurs grossistes ----

    private static RepartirCommande Repartir(AchatsEnMemoire achats, CatalogueGrossistesEnMemoire catalogue, JournalEspion journal)
    {
        var droits = new DroitsFactices("achats.commander");
        return new RepartirCommande(achats, catalogue, new AjouterACommande(achats, droits), droits, journal);
    }

    [Fact]
    public async Task Une_commande_peut_etre_repartie_entre_plusieurs_grossistes_un_bon_par_grossiste()
    {
        var achats = new AchatsEnMemoire { Dernier = new DernierAchat(70m, 2) };
        var catalogue = new CatalogueGrossistesEnMemoire();
        catalogue.Offres.Add(Offre(4, 1, 100m, true));
        var journal = new JournalEspion();
        var bons = await Repartir(achats, catalogue, journal).ExecuterAsync([
            new LigneRepartition(4, 1, 10), new LigneRepartition(5, 2, 3), new LigneRepartition(4, 2, 5), new LigneRepartition(5, 2, 2)], 5);

        Assert.Equal([(11L, 1L, 1), (12L, 2L, 2)], bons.Select(b => (b.CommandeId, b.FournisseurId, b.Lignes)).ToList());
        // Prix du catalogue du grossiste, sinon dernier achat ; même produit chez le même grossiste : quantités additionnées
        Assert.Contains((11L, 4L, 10, 100m), achats.LignesAjoutees);
        Assert.Contains((12L, 5L, 5, 70m), achats.LignesAjoutees);
        Assert.Contains((12L, 4L, 5, 70m), achats.LignesAjoutees);
        Assert.Equal(["commande_repartie"], journal.Actions);
    }

    [Fact]
    public async Task Repartition_refusee_avant_toute_ecriture_si_un_prix_est_inconnu()
    {
        var achats = new AchatsEnMemoire();
        var catalogue = new CatalogueGrossistesEnMemoire();
        catalogue.Offres.Add(Offre(4, 1, 100m, true));
        var e = await Assert.ThrowsAsync<RegleMetierException>(() =>
            Repartir(achats, catalogue, new JournalEspion()).ExecuterAsync([new LigneRepartition(4, 1, 1), new LigneRepartition(5, 2, 1)], 5));
        Assert.Contains("Produit 5", e.Message);
        Assert.Empty(achats.LignesAjoutees);
        await Assert.ThrowsAsync<RegleMetierException>(() => Repartir(achats, catalogue, new JournalEspion()).ExecuterAsync([], 5));
    }

    // ---- Envoi par e-mail (aucun e-mail réel : envoi factice) ----

    private sealed class DocumentsFactices : IDocumentsAchats
    {
        public byte[] BonCommande(CommandeDetail commande, Fournisseur fournisseur, string? pharmacie, bool avecPrix) => [7, 7];
        public byte[] BonRetour(RetourResume retour, string? pharmacie, bool avecPrix) => [];
        public byte[] PlancheEtiquettes(IReadOnlyList<EtiquetteBoite> etiquettes) => [];
        public string EtiquettesZpl(IReadOnlyList<EtiquetteBoite> etiquettes) => "";
    }

    private static (EnvoyerBonParEmail Envoi, CatalogueGrossistesEnMemoire Catalogue, PieceJointeEspion Email, JournalEspion Journal) Envoi(
        AchatsEnMemoire achats, NotificationsEnMemoire? reglages = null, bool enPanne = false)
    {
        var droits = new DroitsFactices("achats.commander");
        var (catalogue, email, journal, docs, pharmacie) = (new CatalogueGrossistesEnMemoire(), new PieceJointeEspion(enPanne), new JournalEspion(),
            new DocumentsFactices(), new PharmacieFactice());
        var connecteurs = new IGrossisteConnecteur[]
        {
            new GrossisteConnecteurFichier(new TableauFactice()),
            new GrossisteConnecteurEmail(reglages ?? new NotificationsEnMemoire(), new SecretsFactices(), email)
        };
        return (new EnvoyerBonParEmail(achats, catalogue, new EmettreCommande(achats, droits, docs, pharmacie, journal),
            new ImprimerCommande(achats, docs, pharmacie, droits), connecteurs, pharmacie, droits, journal, new HorlogeFixe(new DateOnly(2026, 11, 16))),
            catalogue, email, journal);
    }

    private static CommandeDetail Brouillon() =>
        new(new CommandeResume(3, "C-000003", 2, "Grossiste 2", DateTime.UtcNow, "brouillon", 1, 500m), [new LigneCommandeAchat(1, 4, "Metformine", 5, 0, 100m)]);

    [Fact]
    public async Task Envoi_email_emet_le_brouillon_et_joint_le_bon_pdf()
    {
        var achats = new AchatsEnMemoire { Commande = Brouillon() };
        achats.Emails[2] = "commandes@grossiste2.dz";
        var (envoi, catalogue, email, journal) = Envoi(achats);
        var r = await envoi.ExecuterAsync(3, 5);

        Assert.Equal("envoyee", achats.StatutEnBase);
        var e = Assert.Single(email.Envoyes);
        Assert.Equal(("commandes@grossiste2.dz", "C-000003.pdf", "application/pdf"), (e.Adresse, e.NomPieceJointe, e.Type));
        Assert.Contains("C-000003", e.Objet);
        Assert.Equal((3L, "commandes@grossiste2.dz"), catalogue.EnvoiNote);
        Assert.Equal("commandes@grossiste2.dz", r.Adresse);
        Assert.Equal(["commande_emise", "commande_envoyee_email"], journal.Actions);
    }

    [Fact]
    public async Task Envoi_email_refuse_avant_emission_sans_adresse_ou_sans_smtp()
    {
        var achats = new AchatsEnMemoire { Commande = Brouillon() };
        var e = await Assert.ThrowsAsync<RegleMetierException>(() => Envoi(achats).Envoi.ExecuterAsync(3, 5));
        Assert.Contains("pas d’adresse e-mail", e.Message);

        achats.Emails[2] = "commandes@grossiste2.dz";
        var sansSmtp = new NotificationsEnMemoire();
        sansSmtp.Reglages = sansSmtp.Reglages with { SmtpServeur = null };
        e = await Assert.ThrowsAsync<RegleMetierException>(() => Envoi(achats, sansSmtp).Envoi.ExecuterAsync(3, 5));
        Assert.Contains("non configuré", e.Message);
        Assert.Null(achats.StatutEnBase);
    }

    [Fact]
    public async Task Envoi_email_en_echec_le_bon_reste_emis_et_le_message_le_dit()
    {
        var achats = new AchatsEnMemoire { Commande = Brouillon() };
        achats.Emails[2] = "commandes@grossiste2.dz";
        var (envoi, catalogue, _, _) = Envoi(achats, enPanne: true);
        var e = await Assert.ThrowsAsync<RegleMetierException>(() => envoi.ExecuterAsync(3, 5));
        Assert.Contains("émis, mais l’e-mail n’est pas parti", e.Message);
        Assert.Equal("envoyee", achats.StatutEnBase);
        Assert.Null(catalogue.EnvoiNote);
    }
}
