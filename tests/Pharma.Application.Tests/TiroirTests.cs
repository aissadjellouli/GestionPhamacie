using Pharma.Application.Achats;
using Pharma.Application.Caisse;
using Pharma.Application.Commun;
using Pharma.Application.Referentiels;
using Pharma.Application.Securite;
using Pharma.Application.Surveillance;
using Pharma.Domain.Caisse;
using Pharma.Domain.Commun;
using Pharma.Domain.Referentiels;

namespace Pharma.Application.Tests;

/// <summary>
/// Écritures du tiroir en mémoire : reproduit l'effet de la base sur l'attendu (SessionsCaisseSql.SqlVentesEspeces) — une sortie retire
/// l'argent des espèces de la session, une entrée l'ajoute, quel que soit le détail (dépense, grossiste, coffre…).
/// </summary>
internal sealed class TiroirEnMemoire(SessionsEnMemoire sessions) : ITiroir
{
    public List<MouvementTiroirAEcrire> Ecrits { get; } = [];
    public List<MouvementTiroir> Mouvements { get; } = [];

    public Task<MouvementTiroirEcrit> EnregistrerAsync(MouvementTiroirAEcrire m, CancellationToken ct = default)
    {
        Ecrits.Add(m);
        sessions.Especes += m.Sens == RegleTiroir.Sortie ? -m.Montant : m.Montant;
        var id = Mouvements.Count + 1L;
        Mouvements.Add(new MouvementTiroir(id, $"BS-{id:000000}", DateTime.UtcNow, m.UtilisateurId, $"Employé {m.UtilisateurId}", m.Poste, m.SessionId,
            m.MotifCode, m.MotifLibelle, m.Sens, m.Detail, m.Montant, m.Saisie.Categorie, m.Saisie.Libelle, m.Saisie.Beneficiaire, m.Saisie.Reference,
            m.Saisie.PatientId?.ToString(), m.Saisie.Raison, m.ValidePar?.ToString(), null, null, false, m.Reglement is null ? null : 99));
        return Task.FromResult(new MouvementTiroirEcrit(id, $"BS-{id:000000}"));
    }

    public Task<MouvementTiroir?> LireAsync(long id, CancellationToken ct = default) => Task.FromResult(Mouvements.FirstOrDefault(m => m.Id == id));

    public Task<PageMouvementsTiroir> ListerAsync(FiltreMouvementsTiroir f, DemandePage page, CancellationToken ct = default) =>
        Task.FromResult(new PageMouvementsTiroir(new Page<MouvementTiroir>(Mouvements, Mouvements.Count, 1, 50), 0, 0));

    public Task<MouvementTiroirEcrit> CorrigerAsync(MouvementTiroir o, long sessionId, string poste, string motif, long utilisateurId, CancellationToken ct = default)
    {
        var i = Mouvements.FindIndex(m => m.Id == o.Id);
        Mouvements[i] = Mouvements[i] with { Corrigee = true };
        var sens = o.Sens == RegleTiroir.Sortie ? RegleTiroir.Entree : RegleTiroir.Sortie;
        sessions.Especes += sens == RegleTiroir.Sortie ? -o.Montant : o.Montant;
        var id = Mouvements.Count + 1L;
        Mouvements.Add(o with { Id = id, Numero = $"BS-{id:000000}", Sens = sens, CorrigeId = o.Id, MotifCorrection = motif, UtilisateurId = utilisateurId });
        return Task.FromResult(new MouvementTiroirEcrit(id, $"BS-{id:000000}"));
    }

    /// <summary>Lignes du récapitulatif, nettes des corrections (comme SessionsCaisseSql.LignesTiroirAsync).</summary>
    public IReadOnlyList<LigneTiroir> Lignes(long sessionId) => Mouvements.Where(m => m.SessionId == sessionId)
        .GroupBy(m => (m.Motif, Sens: m.CorrigeId is null ? m.Sens : m.Sens == RegleTiroir.Sortie ? RegleTiroir.Entree : RegleTiroir.Sortie))
        .Select(g => new LigneTiroir(g.Key.Motif, g.Key.Sens, g.Sum(m => m.CorrigeId is null ? m.Montant : -m.Montant)))
        .Where(l => l.Montant != 0).ToList();
}

/// <summary>Sessions en mémoire avec les lignes du tiroir du récapitulatif.</summary>
internal sealed class SessionsAvecTiroir(SessionsEnMemoire s, TiroirEnMemoire tiroir) : ISessionsCaisse
{
    public Task<SessionCaisse?> SessionDuPosteAsync(string poste, CancellationToken ct = default) => s.SessionDuPosteAsync(poste, ct);
    public Task<long> OuvrirAsync(long u, string poste, decimal fond, long? releveDe = null, CancellationToken ct = default) => s.OuvrirAsync(u, poste, fond, releveDe, ct);
    public Task<IReadOnlyList<Vendeur>> VendeursAsync(long id, CancellationToken ct = default) => s.VendeursAsync(id, ct);
    public Task<decimal> VentesEspecesAsync(long id, CancellationToken ct = default) => s.VentesEspecesAsync(id, ct);
    public Task CloturerAsync(long id, Comptage c, ResultatCloture r, CancellationToken ct = default) => s.CloturerAsync(id, c, r, ct);
    public Task<DetailSession?> DetailAsync(long id, CancellationToken ct = default) => s.DetailAsync(id, ct);
    public Task<string?> NomEmployeAsync(long u, CancellationToken ct = default) => s.NomEmployeAsync(u, ct);
    public Task<ParametresCaisse> ParametresAsync(CancellationToken ct = default) => s.ParametresAsync(ct);
    public Task ModifierParametresAsync(ParametresCaisse p, CancellationToken ct = default) => s.ModifierParametresAsync(p, ct);
    public Task<IReadOnlyList<LigneTiroir>> LignesTiroirAsync(long id, CancellationToken ct = default) => Task.FromResult(tiroir.Lignes(id));
}

public class TiroirTests
{
    private const string Poste = "Caisse 1";

    private sealed record Contexte(SignalerEvenementCaisse Cas, SessionsEnMemoire Sessions, TiroirEnMemoire Tiroir, SurveillanceEnMemoire Surveillance,
        JournalEspion Journal, AutorisationCourante Autorisation, AlertesTitulaireEspion Titulaire);

    /// <summary>Motifs du tiroir du script 096 (effet et détail) et catégories de charge.</summary>
    private static MotifsEnMemoire Motifs()
    {
        var m = new MotifsEnMemoire();
        m.Motifs.AddRange(
        [
            new MotifReglable(20, "tiroir", "petite_depense", "Petite dépense", true, false, 80, "sortie", "depense"),
            new MotifReglable(21, "tiroir", "paiement_fournisseur", "Paiement d’un livreur ou d’un fournisseur", true, false, 70, "sortie", "fournisseur"),
            new MotifReglable(22, "tiroir", "remboursement_client", "Remboursement d’un client", true, false, 90, "sortie", "patient"),
            new MotifReglable(23, "tiroir", "retrait_coffre", "Retrait vers le coffre", true, false, 60, "sortie", "coffre"),
            new MotifReglable(24, "tiroir", "remise_fond", "Remise d’un fonds de caisse", true, false, 50, "entree", "fond"),
            new MotifReglable(25, "charge", "fournitures", "Fournitures et consommables", true, false, 50)
        ]);
        return m;
    }

    private static Contexte Creer(DroitsFactices droits, decimal especes = 10000, params AnomalieNouvelle[] nouvelles)
    {
        var sessions = new SessionsEnMemoire { Especes = especes, Parametres = new(true, 5000, 500, 0, SeuilSortieTiroir: 2000) };
        var tiroir = new TiroirEnMemoire(sessions);
        var (surveillance, journal, autorisation, titulaire) = (new SurveillanceEnMemoire(nouvelles), new JournalEspion(), new AutorisationCourante(), new AlertesTitulaireEspion());
        var motifs = new MotifsActifs(Motifs());
        var grossiste = new SituationGrossiste(7, "GROSSISTE SUD", 30000,
            [new FactureFournisseur(70, "F-1", "BL-1", new DateTime(2026, 10, 1), 10000, 0, 10000), new FactureFournisseur(71, "F-2", "BL-2", new DateTime(2026, 11, 1), 20000, 0, 20000)], []);
        var avecEffet = new OuvrirTiroirAvecEffet(tiroir, sessions, droits, journal, motifs, new ReglementsEnMemoire(grossiste), autorisation,
            new HorlogeFixe(new DateOnly(2026, 11, 15)));
        var cas = new SignalerEvenementCaisse(surveillance, sessions, droits, new Surveiller(surveillance, titulaire), journal, motifs, avecEffet);
        return new Contexte(cas, sessions, tiroir, surveillance, journal, autorisation, titulaire);
    }

    private static DroitsFactices Vendeur(params string[] plus) => new(["caisse.vendre", "caisse.ouvrir_tiroir", .. plus]);

    private static Task<MouvementTiroirEcrit?> Ouvrir(Contexte c, string motif, SaisieTiroir? s) =>
        c.Cas.ExecuterAsync("tiroir_ouvert", null, null, null, null, 5, Poste, default, motif, null, s);

    [Theory]
    [InlineData("petite_depense", null)]
    [InlineData("petite_depense", 0)]
    [InlineData("retrait_coffre", -10)]
    [InlineData("remise_fond", null)]
    public async Task Montant_obligatoire_et_positif_pour_un_motif_a_effet(string motif, int? montant)
    {
        var c = Creer(Vendeur());
        var ex = await Assert.ThrowsAsync<RegleMetierException>(() => Ouvrir(c, motif, new SaisieTiroir(montant, "fournitures", "Café")));
        Assert.Equal("montant", ex.Champ);
        Assert.Empty(c.Tiroir.Ecrits);
    }

    [Fact]
    public async Task Pas_de_montant_pour_un_motif_sans_effet()
    {
        var c = Creer(Vendeur());
        await Assert.ThrowsAsync<RegleMetierException>(() => Ouvrir(c, "monnaie", new SaisieTiroir(500)));
        Assert.Null(await Ouvrir(c, "monnaie", null));                    // ouverture sans argent : événement seul, pas de bon
        Assert.Single(c.Surveillance.Evenements);
        Assert.Empty(c.Tiroir.Ecrits);
    }

    [Fact]
    public async Task Chaque_effet_ecrit_la_bonne_ecriture()
    {
        var c = Creer(Vendeur("achats.regler"));

        var depense = await Ouvrir(c, "petite_depense", new SaisieTiroir(800, " fournitures ", " Ampoules "));
        var e = c.Tiroir.Ecrits[^1];
        Assert.Equal(("sortie", "depense", "fournitures", "Ampoules", (ReglementAEcrire?)null), (e.Sens, e.Detail, e.Saisie.Categorie, e.Saisie.Libelle, e.Reglement));
        Assert.Equal((1L, 5L, Poste), (e.SessionId, e.UtilisateurId, e.Poste));         // rattachée à la session et à l'employé
        Assert.StartsWith("BS-", depense!.Numero);

        // Grossiste : règlement en espèces pris en entier dans le tiroir, imputé à la facture désignée par son BL
        await Ouvrir(c, "paiement_fournisseur", new SaisieTiroir(1500, FournisseurId: 7, Reference: "BL-2"));
        var g = c.Tiroir.Ecrits[^1].Reglement!;
        Assert.Equal((7L, 1500m, 1500m, 1L, Pharma.Domain.Achats.MoyenReglement.Especes), (g.FournisseurId, g.Montant, g.PartTiroir, g.SessionCaisseId!.Value, g.Moyen));
        Assert.Equal([(71L, 1500m)], g.Imputations);
        Assert.Contains("reglement_grossiste", c.Journal.Actions);

        // Livreur sans grossiste : nom libre, aucune dette touchée
        await Ouvrir(c, "paiement_fournisseur", new SaisieTiroir(300, Beneficiaire: "Livreur Yalidine"));
        Assert.Equal(("Livreur Yalidine", (ReglementAEcrire?)null), (c.Tiroir.Ecrits[^1].Saisie.Beneficiaire, c.Tiroir.Ecrits[^1].Reglement));

        await Ouvrir(c, "remboursement_client", new SaisieTiroir(400, PatientId: 12, Raison: "Boîte rendue"));
        Assert.Equal(("patient", 12L, "Boîte rendue"), (c.Tiroir.Ecrits[^1].Detail, c.Tiroir.Ecrits[^1].Saisie.PatientId!.Value, c.Tiroir.Ecrits[^1].Saisie.Raison));

        await Ouvrir(c, "retrait_coffre", new SaisieTiroir(1000));
        Assert.Equal(("sortie", "coffre"), (c.Tiroir.Ecrits[^1].Sens, c.Tiroir.Ecrits[^1].Detail));
        await Ouvrir(c, "remise_fond", new SaisieTiroir(2000));
        Assert.Equal(("entree", "fond"), (c.Tiroir.Ecrits[^1].Sens, c.Tiroir.Ecrits[^1].Detail));

        // Chaque ouverture est journalisée avec son montant ; le calcul des anomalies suit chaque écriture
        Assert.Equal(6, c.Journal.Actions.Count(a => a == "tiroir_ouvert"));
        Assert.Equal(6, c.Surveillance.Calculs.Count);
    }

    [Theory]
    [InlineData("petite_depense", null, "Café")]           // catégorie manquante
    [InlineData("petite_depense", "inconnue", "Café")]     // catégorie hors liste réglable
    [InlineData("petite_depense", "fournitures", " ")]     // libellé manquant
    public async Task Petite_depense_exige_categorie_reglable_et_libelle(string motif, string? categorie, string? libelle)
    {
        var c = Creer(Vendeur());
        await Assert.ThrowsAsync<RegleMetierException>(() => Ouvrir(c, motif, new SaisieTiroir(500, categorie, libelle)));
        Assert.Empty(c.Tiroir.Ecrits);
    }

    [Fact]
    public async Task Detail_exige_fournisseur_patient()
    {
        var c = Creer(Vendeur("achats.regler"));
        await Assert.ThrowsAsync<RegleMetierException>(() => Ouvrir(c, "paiement_fournisseur", new SaisieTiroir(500)));                       // ni grossiste ni nom
        await Assert.ThrowsAsync<RegleMetierException>(() => Ouvrir(c, "paiement_fournisseur", new SaisieTiroir(500, FournisseurId: 7)));    // grossiste sans BL
        await Assert.ThrowsAsync<RegleMetierException>(() => Ouvrir(c, "remboursement_client", new SaisieTiroir(500, Raison: "x")));         // sans patient
        await Assert.ThrowsAsync<RegleMetierException>(() => Ouvrir(c, "remboursement_client", new SaisieTiroir(500, PatientId: 3)));        // sans raison
        c.Sessions.Especes = 100000;
        await Assert.ThrowsAsync<RegleMetierException>(() => Ouvrir(c, "paiement_fournisseur",
            new SaisieTiroir(40000, FournisseurId: 7, Reference: "BL-1")));                                                                   // au-delà de la dette
        Assert.Empty(c.Tiroir.Ecrits);
    }

    [Fact]
    public async Task Sortie_superieure_aux_especes_du_tiroir_refusee()
    {
        // Fond 5 000 + espèces 1 000 : 6 000 DA au plus dans le tiroir
        var c = Creer(Vendeur("caisse.cloturer"), especes: 1000);
        var ex = await Assert.ThrowsAsync<RegleMetierException>(() => Ouvrir(c, "retrait_coffre", new SaisieTiroir(6000.01m)));
        Assert.Equal("montant", ex.Champ);
        await Ouvrir(c, "retrait_coffre", new SaisieTiroir(1500));
        Assert.Single(c.Tiroir.Ecrits);
        // Une entrée n'est pas bornée par le tiroir (le solde du coffre est contrôlé à l'écriture)
        await Ouvrir(c, "remise_fond", new SaisieTiroir(9000));
    }

    [Fact]
    public async Task Sans_caisse_ouverte_pas_de_sortie()
    {
        var c = Creer(Vendeur());
        c.Sessions.Ouverte = null;
        await Assert.ThrowsAsync<RegleMetierException>(() => Ouvrir(c, "petite_depense", new SaisieTiroir(100, "fournitures", "Café")));
    }

    [Fact]
    public async Task Au_dela_du_seuil_validation_d_un_ayant_droit()
    {
        // Jusqu'au seuil (2 000 DA) : pas de validation
        var c = Creer(Vendeur());
        await Ouvrir(c, "retrait_coffre", new SaisieTiroir(2000));
        Assert.Null(c.Tiroir.Ecrits.Single().ValidePar);

        // Au-delà, sans le droit : refus autorisable (« Faire autoriser » : code comptoir d'un employé qui a caisse.cloturer)
        var refus = await Assert.ThrowsAsync<DroitRefuseException>(() => Ouvrir(c, "retrait_coffre", new SaisieTiroir(2000.01m)));
        Assert.Equal("caisse.cloturer", refus.Permission);
        Assert.True(Pharma.Domain.Securite.CodeComptoir.AutorisableParCollegue(refus.Permission));
        Assert.Single(c.Tiroir.Ecrits);

        // Autorisé sur place par un collègue : il est noté comme valideur
        var autorise = Creer(new DroitsFactices("caisse.vendre", "caisse.ouvrir_tiroir", "caisse.cloturer"));
        autorise.Autorisation.AutoriseParId = 42;
        await Ouvrir(autorise, "retrait_coffre", new SaisieTiroir(3000));
        Assert.Equal(42L, autorise.Tiroir.Ecrits.Single().ValidePar);

        // Une entrée (remise d'un fonds) ne demande pas de validation
        await Ouvrir(c, "remise_fond", new SaisieTiroir(5000));
    }

    [Fact]
    public async Task Paiement_d_un_grossiste_sans_le_droit_refuse()
    {
        var c = Creer(Vendeur());
        var refus = await Assert.ThrowsAsync<DroitRefuseException>(() => Ouvrir(c, "paiement_fournisseur", new SaisieTiroir(500, FournisseurId: 7, Reference: "BL-1")));
        Assert.Equal("achats.regler", refus.Permission);
        Assert.Empty(c.Tiroir.Ecrits);
        // Un livreur payé sans grossiste ne demande pas ce droit
        await Ouvrir(c, "paiement_fournisseur", new SaisieTiroir(500, Beneficiaire: "Livreur"));
    }

    [Fact]
    public async Task Attendu_de_cloture_integre_entrees_et_sorties_sans_ecart()
    {
        var c = Creer(Vendeur("caisse.cloturer", "caisse.voir_ecart", "achats.regler"), especes: 10000);
        await Ouvrir(c, "petite_depense", new SaisieTiroir(1500, "fournitures", "Ampoules"));
        await Ouvrir(c, "paiement_fournisseur", new SaisieTiroir(1000, FournisseurId: 7, Reference: "BL-1"));
        await Ouvrir(c, "retrait_coffre", new SaisieTiroir(1000));
        await Ouvrir(c, "remise_fond", new SaisieTiroir(2000));

        // Dans le tiroir : fond 5 000 + ventes 10 000 − 1 500 − 1 000 − 1 000 + 2 000 = 13 500
        var sessions = new SessionsAvecTiroir(c.Sessions, c.Tiroir);
        var cloture = await new CloturerSessionCaisse(sessions, new DroitsFactices("caisse.cloturer", "caisse.voir_ecart"), new JournalEspion())
            .ExecuterAsync(5, Poste, new Dictionary<string, decimal> { ["autres"] = 13500 });
        Assert.Equal((13500m, 0m), (cloture.Attendu!.Value, cloture.Ecart!.Value));

        // Récapitulatif : lignes séparées, et fond + encaissées − sorties + entrées = attendu
        var lignes = cloture.LignesTiroir!;
        Assert.Equal(4, lignes.Count);
        Assert.Equal((3500m, 2000m), (RecapitulatifTiroir.Sorties(lignes), RecapitulatifTiroir.Entrees(lignes)));
        Assert.Equal(10000m, RecapitulatifTiroir.Encaissees(cloture.VentesEspeces!.Value, lignes));
        Assert.Equal(cloture.Attendu, cloture.FondCaisse + RecapitulatifTiroir.Encaissees(cloture.VentesEspeces.Value, lignes)
            - RecapitulatifTiroir.Sorties(lignes) + RecapitulatifTiroir.Entrees(lignes));
    }

    [Fact]
    public async Task Correction_par_ecriture_inverse_journalisee_sans_suppression()
    {
        var c = Creer(Vendeur(), especes: 10000);
        var bon = await Ouvrir(c, "petite_depense", new SaisieTiroir(700, "fournitures", "Erreur de saisie"));
        Assert.Equal(9300m, c.Sessions.Especes);

        var corriger = new CorrigerMouvementTiroir(c.Tiroir, c.Sessions, new DroitsFactices("caisse.cloturer"), c.Journal);
        // Sans le droit : refus (autorisable par code) ; sans motif : refus
        await Assert.ThrowsAsync<DroitRefuseException>(() =>
            new CorrigerMouvementTiroir(c.Tiroir, c.Sessions, new DroitsFactices("caisse.vendre"), c.Journal).ExecuterAsync(bon!.Id, "x", 5, Poste));
        await Assert.ThrowsAsync<RegleMetierException>(() => corriger.ExecuterAsync(bon!.Id, " ", 5, Poste));

        var inverse = await corriger.ExecuterAsync(bon!.Id, "Saisie en double", 5, Poste);
        Assert.Equal(10000m, c.Sessions.Especes);                                    // l'attendu retrouve sa valeur
        Assert.Equal(2, c.Tiroir.Mouvements.Count);                                  // l'original reste, l'inverse s'ajoute
        var ligne = c.Tiroir.Mouvements.Single(m => m.Id == inverse.Id);
        Assert.Equal(("entree", bon.Id, "Saisie en double"), (ligne.Sens, ligne.CorrigeId!.Value, ligne.MotifCorrection));
        Assert.Contains("correction_tiroir", c.Journal.Actions);
        Assert.Empty(c.Tiroir.Lignes(1));                                            // récapitulatif net : plus rien

        // Pas deux fois, ni une écriture inverse
        await Assert.ThrowsAsync<RegleMetierException>(() => corriger.ExecuterAsync(bon.Id, "encore", 5, Poste));
        await Assert.ThrowsAsync<RegleMetierException>(() => corriger.ExecuterAsync(inverse.Id, "encore", 5, Poste));
    }

    [Fact]
    public async Task Paiement_d_un_grossiste_ne_se_corrige_pas_depuis_la_caisse()
    {
        var c = Creer(Vendeur("achats.regler"));
        var bon = await Ouvrir(c, "paiement_fournisseur", new SaisieTiroir(500, FournisseurId: 7, Reference: "BL-1"));
        await Assert.ThrowsAsync<RegleMetierException>(() =>
            new CorrigerMouvementTiroir(c.Tiroir, c.Sessions, new DroitsFactices("caisse.cloturer"), c.Journal).ExecuterAsync(bon!.Id, "erreur", 5, Poste));
    }

    [Fact]
    public async Task Alerte_au_dela_de_N_sorties_par_jour()
    {
        // Le calcul du jour (surveiller, script 096 : surveillance_candidats_tiroir) suit chaque sortie ; l'anomalie nouvelle est notifiée
        var c = Creer(Vendeur(), 10000, new AnomalieNouvelle(9, "attention", "Vendeur : 5 sorties du tiroir le 15/11."));
        await Ouvrir(c, "petite_depense", new SaisieTiroir(100, "fournitures", "Café"));
        Assert.Single(c.Surveillance.Calculs);
        Assert.Contains(c.Titulaire.Surveillances, s => s.Message.Contains("sorties du tiroir"));

        // N réglable (défaut 5), au moins 1, changement journalisé
        Assert.Equal(5, new ParametresSurveillance(5, 3, 3, 5, 3, null, null).SeuilSortiesTiroir);
        var journal = new JournalEspion();
        var regler = new ReglerSurveillance(c.Surveillance, new DroitsFactices { Titulaire = true }, journal);
        await Assert.ThrowsAsync<RegleMetierException>(() => regler.EnregistrerAsync(c.Surveillance.Parametres with { SeuilSortiesTiroir = 0 }, 1));
        await regler.EnregistrerAsync(c.Surveillance.Parametres with { SeuilSortiesTiroir = 8 }, 1);
        Assert.Equal(8, c.Surveillance.Parametres.SeuilSortiesTiroir);
        Assert.True(journal.Details.Single().ContainsKey("sorties_tiroir"));
    }

    [Fact]
    public void Effet_du_motif_coherent()
    {
        Assert.Equal(("sortie", "depense"), RegleTiroir.VerifierEffet("sortie", "depense"));
        Assert.Equal(("aucun", "aucun"), RegleTiroir.VerifierEffet(null, null));
        Assert.Throws<RegleMetierException>(() => RegleTiroir.VerifierEffet("entree", "depense"));
        Assert.Throws<RegleMetierException>(() => RegleTiroir.VerifierEffet("sortie", "fond"));
        Assert.Throws<RegleMetierException>(() => RegleTiroir.VerifierEffet("aucun", "coffre"));
    }

    [Fact]
    public async Task Motif_ajoute_par_le_titulaire_avec_effet_dans_le_tiroir_seulement()
    {
        var depot = new MotifsEnMemoire();
        var enregistrer = new EnregistrerMotifReglable(depot, new DroitsFactices("admin.parametres"), new JournalEspion());
        await enregistrer.ExecuterAsync(null, "tiroir", "Achat de timbres", true, 1, default, "sortie", "depense");
        await Assert.ThrowsAsync<RegleMetierException>(() => enregistrer.ExecuterAsync(null, "charge", "Timbres", true, 1, default, "sortie", "depense"));
        await Assert.ThrowsAsync<RegleMetierException>(() => enregistrer.ExecuterAsync(null, "tiroir", "Bizarre", true, 1, default, "entree", "patient"));
    }

    [Fact]
    public void Bon_de_sortie_contient_motif_beneficiaire_et_details()
    {
        var m = new MouvementTiroir(3, "BS-000003", new DateTime(2026, 11, 15, 9, 0, 0, DateTimeKind.Utc), 5, "Vendeur A", Poste, 1, "paiement_fournisseur",
            "Paiement d’un livreur ou d’un fournisseur", "sortie", "fournisseur", 1500, null, null, "GROSSISTE SUD", "BL-2", null, null, "Chef B", null, null, false);
        var b = ObtenirBonSortie.Contenu(m, "Pharmacie du Centre");
        Assert.Equal(("BS-000003", 1500m, "GROSSISTE SUD", "Chef B", "Vendeur A"), (b.Numero, b.Montant, b.Beneficiaire, b.ValidePar, b.Employe));
        Assert.Contains("BL / facture : BL-2", b.Details);
    }
}
