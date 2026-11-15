using Pharma.Application.Caisse;
using Pharma.Application.Commun;
using Pharma.Domain.Commun;
using Pharma.Domain.Ventes;
using S = Pharma.Shared;

namespace Pharma.Application.Tests;

internal sealed class VoletsChifaEnMemoire : IVoletsChifa
{
    public Dictionary<string, ProduitChifa> Codes { get; } = [];
    public string? VenteExistante { get; set; }
    public Task<string?> VenteDuVoletAsync(string n, CancellationToken ct = default) => Task.FromResult(VenteExistante);
    public Task<IReadOnlyDictionary<string, ProduitChifa>> ProduitsAsync(IEnumerable<string> codes, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyDictionary<string, ProduitChifa>>(codes.Where(Codes.ContainsKey).ToDictionary(c => c, c => Codes[c]));
    /// <summary>Organisme de la fiche du patient retrouvé (n° 690755003842).</summary>
    public string? OrganismePatient { get; set; } = "CNAS";
    public Task<(long Id, string Nom, string NumAssure, string? Organisme)?> PatientAsync(string a, string s, CancellationToken ct = default) =>
        Task.FromResult<(long, string, string, string?)?>(a == "690755003842" ? (12L, "Benali Amine", "690755003842/80", OrganismePatient) : null);
    /// <summary>Assuré principal proposé pour un ayant droit inconnu.</summary>
    public (long Id, string Nom)? PrincipalPropose { get; set; }
    public Task<(long Id, string Nom)?> AssurePrincipalProposeAsync(string a, string s, CancellationToken ct = default) => Task.FromResult(PrincipalPropose);
    public Task<long?> OrganismeAsync(string code, CancellationToken ct = default) =>
        Task.FromResult<long?>(code switch { "CNAS" => 1L, "CASNOS" => 2L, "MIL" => 3L, _ => null });
    public Task<string?> CodeOrganismeAsync(long id, CancellationToken ct = default) =>
        Task.FromResult<string?>(id switch { 1 => "CNAS", 2 => "CASNOS", 3 => "MIL", 4 => "MDN", _ => null });
    /// <summary>Fiches créées depuis un volet (n° d'assuré complet, nom, organisme).</summary>
    public List<(long Id, string Nom, string NumAssure, long OrganismeId)> Crees { get; } = [];
    /// <summary>Lien et assuré principal reçus à la dernière création.</summary>
    public (string? Lien, long? AssurePrincipalId) DerniereFamille { get; private set; }
    public Task<(long Id, string Nom, string NumAssure, long? OrganismeId, bool Cree)> PatientOuCreerAsync(string a, string s, string nom, long org,
        long creePar, string? lien, long? assurePrincipalId, CancellationToken ct = default)
    {
        DerniereFamille = (lien, assurePrincipalId);
        if (a == "690755003842") return Task.FromResult<(long, string, string, long?, bool)>((12L, "Benali Amine", "690755003842/80", 1L, false));
        if (Crees.FirstOrDefault(c => c.NumAssure == $"{a}/{s}") is { Id: > 0 } deja)
            return Task.FromResult<(long, string, string, long?, bool)>((deja.Id, deja.Nom, deja.NumAssure, deja.OrganismeId, false));
        var cree = (100L + Crees.Count, nom, $"{a}/{s}", org);
        Crees.Add(cree);
        return Task.FromResult<(long, string, string, long?, bool)>((cree.Item1, nom, cree.Item3, org, true));
    }
    public Task<(string Produit, string? Ancien)?> AssocierAsync(string code, long produitId, long u, CancellationToken ct = default)
    {
        if (produitId != 18) return Task.FromResult<(string, string?)?>(null);
        var ancien = Codes.GetValueOrDefault(code)?.Nom;
        Codes[code] = new ProduitChifa(18, "VIT D3", 156.48m);
        return Task.FromResult<(string, string?)?>(("VIT D3", ancien));
    }
    /// <summary>Fiches du catalogue (remboursable, tarif, prix public) vues par la mise à jour d'après le volet ; absentes : ignorées.</summary>
    public Dictionary<long, (string Nom, bool Remboursable, decimal? Tarif, decimal PrixPublic)> Catalogue { get; } = [];
    public Task<IReadOnlyList<MajCatalogueChifa>> MettreAJourCatalogueAsync(IReadOnlyDictionary<long, decimal> tarifs, CancellationToken ct = default)
    {
        var majs = new List<MajCatalogueChifa>();
        foreach (var (id, tarif) in tarifs)
        {
            if (!Catalogue.TryGetValue(id, out var p) || (p.Remboursable && p.Tarif == tarif)) continue;
            Catalogue[id] = p with { Remboursable = true, Tarif = tarif };
            majs.Add(new MajCatalogueChifa(id, p.Nom, p.Remboursable, p.Tarif, tarif));
        }
        return Task.FromResult<IReadOnlyList<MajCatalogueChifa>>(majs);
    }
}

public class VoletChifaTests
{
    private const string Volet = "06930126;11/11/2026;690755003842;80;24;2;09/11/2026;07607,3,156.48;";

    [Fact]
    public async Task Volet_lu_assure_retrouve_code_inconnu_a_associer()
    {
        var volets = new VoletsChifaEnMemoire();
        var v = await new LireVoletChifa(volets, new DroitsFactices("caisse.vendre")).ExecuterAsync(Volet, 3);
        Assert.Equal(("06930126", 12L, 1L), (v.Qr.NumeroFacture, v.PatientId, v.OrganismeId));
        Assert.Null(v.Lignes.Single().Produit);                     // code 07607 pas encore associé

        var journal = new JournalEspion();
        await new AssocierCodeChifa(volets, new DroitsFactices("caisse.vendre"), journal).ExecuterAsync("07607", 18, 3);
        Assert.Equal(["code_chifa_associe"], journal.Actions);
        v = await new LireVoletChifa(volets, new DroitsFactices("caisse.vendre")).ExecuterAsync(Volet, 3);
        Assert.Equal(18, v.Lignes.Single().Produit!.Id);             // reconnu au volet suivant
    }

    [Fact]
    public async Task Volet_scanne_en_clavier_qwerty_relu()
    {
        // « 06930126;… » tapé par une douchette QWERTY sur un poste AZERTY : chiffres de la rangée AZERTY, « ; » devenu « m »
        var qwerty = new string(Volet.Select(c => c switch
        {
            '0' => 'à', '1' => '&', '2' => 'é', '3' => '"', '4' => '\'', '5' => '(', '6' => '-', '7' => 'è', '8' => '_', '9' => 'ç',
            ';' => 'm', '/' => '!', '.' => ':', ',' => ';', _ => c
        }).ToArray());
        var v = await new LireVoletChifa(new VoletsChifaEnMemoire(), new DroitsFactices("caisse.vendre")).ExecuterAsync(qwerty, 3);
        Assert.Equal(156.48m, v.Lignes.Single().PrixUnitaire);
    }

    [Fact]
    public async Task Volet_deja_repris_ou_illisible_refuse()
    {
        var lire = new LireVoletChifa(new VoletsChifaEnMemoire { VenteExistante = "T-000042" }, new DroitsFactices("caisse.vendre"));
        var ex = await Assert.ThrowsAsync<RegleMetierException>(() => lire.ExecuterAsync(Volet, 3));
        Assert.Contains("T-000042", ex.Message);
        await Assert.ThrowsAsync<RegleMetierException>(() => lire.ExecuterAsync("26/F-015695", 3));
        await Assert.ThrowsAsync<DroitRefuseException>(() =>
            new LireVoletChifa(new VoletsChifaEnMemoire(), new DroitsFactices("stock.consulter")).ExecuterAsync(Volet, 3));
        await Assert.ThrowsAsync<RegleMetierException>(() =>
            new AssocierCodeChifa(new VoletsChifaEnMemoire(), new DroitsFactices("caisse.vendre"), new JournalEspion()).ExecuterAsync("07607", 99, 3));
    }

    [Fact]
    public async Task Volet_garde_sur_la_vente_seulement_en_tiers_payant()
    {
        var ventes = new VentesEspion();
        var cas = new Encaisser(new CatalogueEnMemoire(), new PatientsEnMemoire(), ventes, new SessionsEnMemoire(), new DroitsFactices("caisse.vendre"),
            new HorlogeFixe(CatalogueEnMemoire.Aujourdhui), new ScansEnMemoire());
        var commande = new CommandeVente([new LigneCommande(2, 20, 1)], false, null, null, null, null, 0, ModePaiement.Especes, null, VoletChifa: Volet);
        var ex = await Assert.ThrowsAsync<RegleMetierException>(() => cas.ExecuterAsync(commande, 5, "Caisse 1"));
        Assert.Equal("volet_chifa", ex.Champ);
        Assert.Null(ventes.Recu);
    }

    // ---- Organisme du volet : celui de la fiche (CNAS, CASNOS), sinon CNAS ; seuls CNAS et CASNOS acceptés (2026-11-15) ----

    [Theory]
    [InlineData("CNAS", 1L)]
    [InlineData("CASNOS", 2L)]
    [InlineData("casnos", 2L)]
    [InlineData(null, 1L)]      // patient sans organisme : CNAS
    [InlineData("MIL", 1L)]     // autre organisme sur la fiche : CNAS
    public async Task Organisme_propose_celui_de_la_fiche_sinon_CNAS(string? organismePatient, long attendu)
    {
        var volets = new VoletsChifaEnMemoire { OrganismePatient = organismePatient };
        var v = await new LireVoletChifa(volets, new DroitsFactices("caisse.vendre")).ExecuterAsync(Volet, 3);
        Assert.Equal(attendu, v.OrganismeId);
    }

    [Fact]
    public async Task Patient_inconnu_organisme_CNAS_par_defaut()
    {
        var v = await new LireVoletChifa(new VoletsChifaEnMemoire(), new DroitsFactices("caisse.vendre"))
            .ExecuterAsync("06930126;11/11/2026;111111111111;80;24;2;09/11/2026;07607,3,156.48;", 3);
        Assert.Equal((null, 1L), (v.PatientId, v.OrganismeId));
    }

    [Theory]
    [InlineData(1L)]
    [InlineData(2L)]
    public async Task Vente_de_volet_acceptee_en_CNAS_ou_CASNOS(long organismeId)
    {
        var (cas, ventes, _) = CasChifa();
        await cas.ExecuterAsync(CommandeChifa(350) with { OrganismeId = organismeId }, 5, "Caisse 1");
        Assert.Equal(organismeId, ventes.Recu!.Vente.OrganismeId);
    }

    [Theory]
    [InlineData(3L)]     // autre organisme
    [InlineData(99L)]    // organisme inconnu
    public async Task Vente_de_volet_refusee_pour_un_autre_organisme(long organismeId)
    {
        var (cas, ventes, _) = CasChifa();
        var ex = await Assert.ThrowsAsync<RegleMetierException>(() =>
            cas.ExecuterAsync(CommandeChifa(350) with { OrganismeId = organismeId }, 5, "Caisse 1"));
        Assert.Equal("organisme", ex.Champ);
        Assert.Null(ventes.Recu);
    }

    // ---- Écart entre le prix Chifa du volet et notre prix (Amoxicilline : 350 DA, lot sans PPA), décisions du 2026-11-15 ----

    private static string VoletAuPrix(decimal prix, int qte = 2) =>
        FormattableString.Invariant($"06930126;11/11/2026;690755003842;80;24;2;09/11/2026;07607,{qte},{prix:0.00};");

    private static (Encaisser Cas, VentesEspion Ventes, JournalEspion Journal) CasChifa(params string[] droits)
    {
        var volets = new VoletsChifaEnMemoire();
        volets.Codes["07607"] = new ProduitChifa(2, "Amoxicilline", 350);
        var (ventes, journal) = (new VentesEspion(), new JournalEspion());
        var cas = new Encaisser(new CatalogueEnMemoire(), new PatientsEnMemoire(), ventes, new SessionsEnMemoire(), new DroitsFactices(["caisse.vendre", .. droits]),
            new HorlogeFixe(CatalogueEnMemoire.Aujourdhui), new ScansEnMemoire(), journal: journal, voletsChifa: volets);
        return (cas, ventes, journal);
    }

    private static CommandeVente CommandeChifa(decimal prixChifa, params ChoixPrixChifa[] choix) =>
        new([new LigneCommande(2, 20, 2)], true, 1, 80, 12, null, 0, ModePaiement.Especes, null, VoletChifa: VoletAuPrix(prixChifa),
            ChoixChifa: choix.Length == 0 ? null : choix);

    [Theory]
    [InlineData(346.00)]   // écart de 4 DA par boîte, sous le seuil de 5 DA
    [InlineData(354.99)]
    public async Task Ecart_sous_le_seuil_aligne_d_office_sur_le_prix_Chifa_et_journalise(decimal prixChifa)
    {
        var (cas, ventes, journal) = CasChifa();
        await cas.ExecuterAsync(CommandeChifa(prixChifa), 5, "Caisse 1");
        var ligne = ventes.Recu!.Vente.Lignes.Single();
        Assert.Equal((prixChifa, prixChifa, EcartPrixChifa.AligneAuto), (ligne.PrixUnitaire, ligne.PrixChifa, ligne.ChoixPrixChifa));
        Assert.Equal(0, ventes.Recu.Vente.EcartChifaPatient);
        Assert.Contains("ecart_prix_chifa", journal.Actions);
        Assert.Equal(350m, CatalogueEnMemoire.Amoxicilline.PrixPublic);   // catalogue jamais modifié
    }

    [Fact]
    public async Task Ecart_au_dela_du_seuil_avec_le_droit_le_vendeur_choisit_aligner_ou_garder()
    {
        // Prix Chifa plus bas (300) : aligné, la ligne passe à 300 ; gardé, l'écart de 50 x 2 est payé par le patient
        var (cas, ventes, _) = CasChifa("prix.modifier");
        await cas.ExecuterAsync(CommandeChifa(300, new ChoixPrixChifa(2, EcartPrixChifa.Aligne, 300)), 5, "Caisse 1");
        var v = ventes.Recu!.Vente;
        Assert.Equal((300m, EcartPrixChifa.Aligne, 0m), (v.Lignes[0].PrixUnitaire, v.Lignes[0].ChoixPrixChifa, v.EcartChifaPatient));

        (cas, ventes, _) = CasChifa("prix.modifier");
        var encaissee = await cas.ExecuterAsync(CommandeChifa(300, new ChoixPrixChifa(2, EcartPrixChifa.Garde, 350)), 5, "Caisse 1");
        v = ventes.Recu!.Vente;
        Assert.Equal((350m, EcartPrixChifa.Garde, 100m), (v.Lignes[0].PrixUnitaire, v.Lignes[0].ChoixPrixChifa, v.EcartChifaPatient));
        Assert.Equal(100m, encaissee.EcartChifaPatient);
        // Part de l'organisme sur le prix Chifa : 300 x 2 x 80 % = 480 ; le patient paie le reste (dont l'écart)
        Assert.Equal((480m, 220m), (v.PartOrganisme, v.PartPatient));

        // Prix Chifa plus haut (400) : même choix
        (cas, ventes, _) = CasChifa("prix.modifier");
        await cas.ExecuterAsync(CommandeChifa(400, new ChoixPrixChifa(2, EcartPrixChifa.Garde, 350)), 5, "Caisse 1");
        Assert.Equal(350m, ventes.Recu!.Vente.Lignes[0].PrixUnitaire);
    }

    [Fact]
    public async Task Ecart_au_dela_du_seuil_avec_le_droit_sans_choix_refuse()
    {
        var (cas, ventes, _) = CasChifa("prix.modifier");
        var ex = await Assert.ThrowsAsync<RegleMetierException>(() => cas.ExecuterAsync(CommandeChifa(300), 5, "Caisse 1"));
        Assert.Equal("prix_chifa", ex.Champ);
        Assert.Null(ventes.Recu);
    }

    [Theory]
    [InlineData(300, 350)]   // prix Chifa plus bas : notre prix, écart payé par le patient
    [InlineData(400, 400)]   // prix Chifa plus haut : le prix Chifa
    public async Task Sans_le_droit_le_prix_le_plus_eleve_d_office(decimal prixChifa, decimal attendu)
    {
        var (cas, ventes, journal) = CasChifa();
        await cas.ExecuterAsync(CommandeChifa(prixChifa), 5, "Caisse 1");
        var ligne = ventes.Recu!.Vente.Lignes.Single();
        Assert.Equal((attendu, EcartPrixChifa.PlusEleve), (ligne.PrixUnitaire, ligne.ChoixPrixChifa));
        Assert.Equal(Math.Max(0, (350 - prixChifa) * 2), ventes.Recu.Vente.EcartChifaPatient);
        Assert.Contains("ecart_prix_chifa", journal.Actions);
    }

    [Fact]
    public async Task Sans_le_droit_un_choix_demande_exige_la_validation_d_un_ayant_droit()
    {
        var (cas, ventes, _) = CasChifa();
        var ex = await Assert.ThrowsAsync<DroitRefuseException>(() =>
            cas.ExecuterAsync(CommandeChifa(300, new ChoixPrixChifa(2, EcartPrixChifa.Aligne, 300)), 5, "Caisse 1"));
        // Action dédiée, accordable par le code d'un ayant droit « prix.modifier » même sur une connexion par code comptoir
        Assert.Equal(Pharma.Domain.Securite.CodeComptoir.ActionChoixPrixChifa, ex.Permission);
        Assert.True(Pharma.Domain.Securite.CodeComptoir.AutorisableParCollegue(ex.Permission));
        Assert.Equal("prix.modifier", Pharma.Domain.Securite.CodeComptoir.DroitDuValideur(ex.Permission));
        Assert.Null(ventes.Recu);
        // Aperçu : le choix non autorisé est ignoré, le plus élevé s'applique en attendant
        var apercu = await cas.CalculerAsync(CommandeChifa(300, new ChoixPrixChifa(2, EcartPrixChifa.Aligne, 300)), 5);
        Assert.Equal(350m, apercu.PrixLignes![0]);
        Assert.True(apercu.EcartsChifa!.Single().AChoisir);
    }

    [Fact]
    public async Task Choix_du_prix_Chifa_accepte_avec_le_code_d_un_ayant_droit_vendeur_et_valideur_journalises()
    {
        // Le code d'un collègue accorde l'action (PermissionsComptoir) ; AutorisationCourante nomme le valideur
        var volets = new VoletsChifaEnMemoire();
        volets.Codes["07607"] = new ProduitChifa(2, "Amoxicilline", 350);
        var (ventes, journal) = (new VentesEspion(), new JournalEspion());
        var cas = new Encaisser(new CatalogueEnMemoire(), new PatientsEnMemoire(), ventes, new SessionsEnMemoire(),
            new DroitsFactices(["caisse.vendre", Pharma.Domain.Securite.CodeComptoir.ActionChoixPrixChifa]),
            new HorlogeFixe(CatalogueEnMemoire.Aujourdhui), new ScansEnMemoire(), journal: journal, voletsChifa: volets,
            autorisation: new Pharma.Application.Securite.AutorisationCourante { AutoriseParId = 8, AutoriseParNom = "Assistante Fictive" });
        await cas.ExecuterAsync(CommandeChifa(300, new ChoixPrixChifa(2, EcartPrixChifa.Aligne, 300)), 5, "Caisse 1");
        Assert.Equal((300m, EcartPrixChifa.Aligne), (ventes.Recu!.Vente.Lignes[0].PrixUnitaire, ventes.Recu.Vente.Lignes[0].ChoixPrixChifa));
        var i = journal.Actions.IndexOf("ecart_prix_chifa");
        var d = journal.Details[i];
        Assert.Equal((5L, 8L, "Assistante Fictive"), ((long)d["vendeur"]!, (long)d["valide_par"]!, (string)d["valideur"]!));
    }

    [Fact]
    public void Prix_modifier_reste_ferme_au_code_comptoir_seule_l_action_du_choix_Chifa_est_ouverte()
    {
        // Connexion par code : prix.modifier n'est pas un droit du comptoir ; l'action dédiée exige un valideur qui a prix.modifier
        Assert.False(Pharma.Domain.Securite.CodeComptoir.Autorise("prix.modifier"));
        Assert.False(Pharma.Domain.Securite.CodeComptoir.Autorise(Pharma.Domain.Securite.CodeComptoir.ActionChoixPrixChifa));
        Assert.True(Pharma.Domain.Securite.CodeComptoir.ActionsAutorisables.ContainsKey(Pharma.Domain.Securite.CodeComptoir.ActionChoixPrixChifa));
        Assert.Equal(Pharma.Domain.Securite.CodeComptoir.ActionChoixPrixChifa, S.DetailAutorisationChifa.ActionChoixPrixChifa);
    }

    [Fact]
    public async Task Choix_du_prix_Chifa_valideurs_proposes_seulement_ceux_qui_ont_prix_modifier()
    {
        // Code d'un employé sans prix.modifier : jamais proposé (et refusé par AutorisationParCode, même règle DroitDuValideur)
        var codes = new CodesEnMemoire();
        codes.Comptes[8] = new Pharma.Application.Securite.CompteComptoir(8, "assistante", "Assistante Fictive", "hash:4826", 0, null);
        codes.Comptes[9] = new Pharma.Application.Securite.CompteComptoir(9, "vendeur.b", "Vendeur B", "hash:2580", 0, null);
        var droits = new DroitsParEmploye(new() { [8] = ["prix.modifier"], [9] = ["caisse.vendre"] });
        var liste = await new Pharma.Application.Securite.ListerValidateurs(codes, droits)
            .AutorisablesAsync(Pharma.Domain.Securite.CodeComptoir.ActionChoixPrixChifa, 5, null);
        Assert.Equal(["assistante"], liste.Select(v => v.Identifiant));
    }

    [Fact]
    public async Task Part_de_l_organisme_calculee_sur_le_prix_Chifa_jamais_sur_notre_prix()
    {
        // Sans le droit, prix Chifa 300 < 350 : vendu 350, organisme 300 x 2 x 80 % = 480 (et non 350 x 2 x 80 % = 560)
        var (cas, ventes, _) = CasChifa();
        await cas.ExecuterAsync(CommandeChifa(300), 5, "Caisse 1");
        var v = ventes.Recu!.Vente;
        Assert.Equal((480m, 220m, 300m), (v.PartOrganisme, v.PartPatient, v.Lignes[0].BaseRemboursement));
    }

    [Theory]
    [InlineData(EcartPrixChifa.Aligne, 350.0)]   // prix attendu différent du prix applicable (300)
    [InlineData("baisse", null)]               // choix inconnu
    public async Task Prix_non_conforme_a_la_regle_refuse_par_le_serveur(string choix, double? attendu)
    {
        var (cas, ventes, _) = CasChifa("prix.modifier");
        var ex = await Assert.ThrowsAsync<RegleMetierException>(() =>
            cas.ExecuterAsync(CommandeChifa(300, new ChoixPrixChifa(2, choix, (decimal?)attendu)), 5, "Caisse 1"));
        Assert.Equal("prix_chifa", ex.Champ);
        Assert.Null(ventes.Recu);
    }

    // ---- Volet prérempli (règle du titulaire, 2026-11-15) ----

    private const string VoletInconnu = "06930127;11/11/2026;111111111111;80;24;2;09/11/2026;07607,3,156.48;";

    [Fact]
    public async Task Patient_inconnu_cree_avec_le_nom_saisi_et_journalise()
    {
        var (volets, journal) = (new VoletsChifaEnMemoire(), new JournalEspion());
        var p = await new CreerPatientVoletChifa(volets, new DroitsFactices("caisse.vendre"), journal)
            .ExecuterAsync(VoletInconnu, "  Haddad   Karim ", 2, 3);
        Assert.Equal(("Haddad Karim", "111111111111/80", 2L, true, false), (p.Nom, p.NumAssure, p.OrganismeId, p.Cree, p.ACompleter));
        Assert.Equal(["patient_cree"], journal.Actions);
        Assert.Equal(false, journal.Details.Single()["a_completer"]);
        Assert.Equal("volet_chifa", journal.Details.Single()["origine"]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    public async Task Sans_nom_fiche_creee_au_nom_provisoire_a_completer(string? nom)
    {
        var journal = new JournalEspion();
        var p = await new CreerPatientVoletChifa(new VoletsChifaEnMemoire(), new DroitsFactices("caisse.vendre"), journal)
            .ExecuterAsync(VoletInconnu, nom, 3, 3);   // organisme 3 (MIL) refusé : CNAS
        Assert.Equal(("Assuré 111111111111/80", 1L, true), (p.Nom, p.OrganismeId, p.ACompleter));
        Assert.True(PatientVoletChifa.EstProvisoire(p.Nom));
        Assert.False(PatientVoletChifa.EstProvisoire("Assuré Benali"));
        Assert.Equal(true, journal.Details.Single()["a_completer"]);
    }

    [Fact]
    public async Task Meme_numero_d_assure_jamais_deux_fiches()
    {
        var (volets, journal) = (new VoletsChifaEnMemoire(), new JournalEspion());
        var creer = new CreerPatientVoletChifa(volets, new DroitsFactices("caisse.vendre"), journal);
        var a = await creer.ExecuterAsync(VoletInconnu, "Haddad", 1, 3);
        var b = await creer.ExecuterAsync(VoletInconnu, null, 1, 3);
        Assert.Equal((a.Id, false), (b.Id, b.Cree));
        Assert.Single(volets.Crees);
        Assert.Single(journal.Actions);
        var connu = await creer.ExecuterAsync(Volet, "Autre nom", 1, 3);   // assuré déjà en base : repris, rien créé
        Assert.Equal((12L, false), (connu.Id, connu.Cree));
    }

    [Fact]
    public async Task Creation_exige_le_droit_de_vendre()
    {
        await Assert.ThrowsAsync<DroitRefuseException>(() =>
            new CreerPatientVoletChifa(new VoletsChifaEnMemoire(), new DroitsFactices(), new JournalEspion()).ExecuterAsync(VoletInconnu, "X", 1, 3));
    }

    [Fact]
    public void Date_de_l_ordonnance_reprise_de_la_seconde_date_du_QR()
    {
        var qr = LectureVoletChifa.Lire(Volet)!;
        Assert.Equal(new DateOnly(2026, 11, 9), qr.DateOrdonnance);
        Assert.Equal(new DateOnly(2026, 11, 11), qr.DateFacture);
    }

    [Fact]
    public async Task Liste_I_reprise_d_un_volet_sans_controle_vente_sans_ordonnance()
    {
        var (cas, ventes, journal) = CasChifa();
        var commande = new CommandeVente([new LigneCommande(9, 90, 1)], true, 1, 80, 12, null, 0, ModePaiement.Especes, null, VoletChifa: Volet);
        await cas.ExecuterAsync(commande, 5, "Caisse 1");   // ni médecin ni droit « vendre sans ordonnance »
        Assert.NotNull(ventes.Recu);
        Assert.DoesNotContain("vente_sans_ordonnance", journal.Actions);

        // Sans volet, la même liste I demande toujours la validation
        var (sansVolet, _, _) = CasChifa();
        await Assert.ThrowsAsync<DroitRefuseException>(() => sansVolet.ExecuterAsync(
            new CommandeVente([new LigneCommande(9, 90, 1)], false, null, null, 12, null, 0, ModePaiement.Especes, null), 5, "Caisse 1"));
    }

    [Fact]
    public async Task Psychotrope_d_un_volet_garde_la_regle_du_registre()
    {
        var (cas, ventes, _) = CasChifa(Pharma.Domain.Securite.Permissions.PsychotropesDelivrer);
        var commande = new CommandeVente([new LigneCommande(7, 70, 1)], true, 1, 80, 12, null, 0, ModePaiement.Especes, null, VoletChifa: Volet);
        await Assert.ThrowsAsync<RegleMetierException>(() => cas.ExecuterAsync(commande, 5, "Caisse 1"));
        Assert.Null(ventes.Recu);
    }

    // ---- Catalogue mis à jour d'après le volet (règle du titulaire, 2026-11-15) ----

    private static (Encaisser Cas, VoletsChifaEnMemoire Volets, JournalEspion Journal) CasCatalogue(bool remboursable, decimal? tarif, params string[] droits)
    {
        var volets = new VoletsChifaEnMemoire();
        volets.Codes["07607"] = new ProduitChifa(2, "Amoxicilline", 350);
        volets.Catalogue[2] = ("Amoxicilline", remboursable, tarif, 350);
        var journal = new JournalEspion();
        var cas = new Encaisser(new CatalogueEnMemoire(), new PatientsEnMemoire(), new VentesEspion(), new SessionsEnMemoire(),
            new DroitsFactices(["caisse.vendre", .. droits]), new HorlogeFixe(CatalogueEnMemoire.Aujourdhui), new ScansEnMemoire(),
            journal: journal, voletsChifa: volets);
        return (cas, volets, journal);
    }

    [Fact]
    public async Task Volet_encaisse_produit_non_remboursable_devient_remboursable_au_tarif_Chifa_sans_prix_modifier()
    {
        var (cas, volets, journal) = CasCatalogue(remboursable: false, tarif: null);   // vendeur sans prix.modifier
        var v = await cas.ExecuterAsync(CommandeChifa(352), 5, "Caisse 1");
        Assert.Equal((true, (decimal?)352m, 350m), (volets.Catalogue[2].Remboursable, volets.Catalogue[2].Tarif, volets.Catalogue[2].PrixPublic));
        Assert.Contains("volet Chifa n° 06930126", v.CatalogueChifa);
        Assert.Contains("Amoxicilline remboursable", v.CatalogueChifa);
        Assert.Contains("tarif de Amoxicilline", v.CatalogueChifa);
        var i = journal.Actions.IndexOf(CatalogueVoletChifa.Action);
        Assert.True(i >= 0);
        Assert.Equal(("Amoxicilline", "06930126"), (journal.Details[i]["produit"], journal.Details[i]["volet"]));
        Assert.Equal(350m, CatalogueEnMemoire.Amoxicilline.PrixPublic);   // prix de vente inchangé
    }

    [Fact]
    public async Task Volet_encaisse_tarif_different_mis_a_jour_prix_public_inchange()
    {
        var (cas, volets, journal) = CasCatalogue(remboursable: true, tarif: 150);
        var v = await cas.ExecuterAsync(CommandeChifa(352), 5, "Caisse 1");
        Assert.Equal((true, (decimal?)352m, 350m), (volets.Catalogue[2].Remboursable, volets.Catalogue[2].Tarif, volets.Catalogue[2].PrixPublic));
        Assert.DoesNotContain("Amoxicilline remboursable", v.CatalogueChifa);
        Assert.Contains("→", v.CatalogueChifa);
        Assert.Single(journal.Actions, a => a == CatalogueVoletChifa.Action);
        // La règle des écarts s'applique comme avant (352 vs 350, sous le seuil : aligné d'office)
        Assert.Contains("ecart_prix_chifa", journal.Actions);
    }

    [Fact]
    public async Task Volet_encaisse_catalogue_deja_a_jour_rien_ni_journal()
    {
        var (cas, volets, journal) = CasCatalogue(remboursable: true, tarif: 352);
        var v = await cas.ExecuterAsync(CommandeChifa(352), 5, "Caisse 1");
        Assert.Null(v.CatalogueChifa);
        Assert.DoesNotContain(CatalogueVoletChifa.Action, journal.Actions);
        Assert.Equal((decimal?)352m, volets.Catalogue[2].Tarif);
    }

    [Fact]
    public async Task Volet_seulement_scanne_ne_touche_pas_le_catalogue()
    {
        var volets = new VoletsChifaEnMemoire();
        volets.Codes["07607"] = new ProduitChifa(2, "Amoxicilline", 350);
        volets.Catalogue[2] = ("Amoxicilline", false, null, 350);
        await new LireVoletChifa(volets, new DroitsFactices("caisse.vendre")).ExecuterAsync(Volet, 3);
        Assert.False(volets.Catalogue[2].Remboursable);
    }

    // ---- Décisions du titulaire du 2026-11-15 : choix gardés en attente, reste saisi journalisé, détail de l'autorisation ----

    private static S.PanierAttenteDto PanierVolet(string? volet, IReadOnlyList<S.ChoixChifaAttenteDto>? choix) =>
        new([new S.LignePanierAttenteDto(2, "Amoxicilline", 350, null, 2, false, 10)], true, 1, 80, null, null,
            new DateOnly(2026, 11, 15), null, 0, null, null, volet, ChoixChifa: choix);

    private static S.PanierAttenteDto AllerRetourJson(S.PanierAttenteDto p) =>
        System.Text.Json.JsonSerializer.Deserialize<S.PanierAttenteDto>(System.Text.Json.JsonSerializer.Serialize(p))!;

    [Fact]
    public async Task Vente_en_attente_garde_ses_choix_Chifa_retablis_a_la_reprise_et_revérifies_par_le_serveur()
    {
        var choix = new Dictionary<long, string> { [2] = EcartPrixChifa.Garde };
        var repris = S.ChoixChifaAttente.Restaurer(AllerRetourJson(PanierVolet(VoletAuPrix(300), S.ChoixChifaAttente.Garder(VoletAuPrix(300), choix))), voletRelu: true);
        Assert.Equal(choix, repris);

        // Rétablis sans les refaire : encaissés avec le droit
        var (cas, ventes, _) = CasChifa("prix.modifier");
        await cas.ExecuterAsync(CommandeChifa(300, new ChoixPrixChifa(2, repris[2], 350)), 5, "Caisse 1");
        Assert.Equal((350m, EcartPrixChifa.Garde), (ventes.Recu!.Vente.Lignes[0].PrixUnitaire, ventes.Recu.Vente.Lignes[0].ChoixPrixChifa));

        // Revérifiés : repris par un vendeur sans le droit, le choix exige le code d'un ayant droit
        (cas, ventes, _) = CasChifa();
        await Assert.ThrowsAsync<DroitRefuseException>(() => cas.ExecuterAsync(CommandeChifa(300, new ChoixPrixChifa(2, repris[2], 350)), 5, "Caisse 1"));
        Assert.Null(ventes.Recu);
        // Revérifiés : prix attendu qui ne correspond plus (prix du lot changé entre-temps) : refusé
        (cas, _, _) = CasChifa("prix.modifier");
        await Assert.ThrowsAsync<RegleMetierException>(() => cas.ExecuterAsync(CommandeChifa(300, new ChoixPrixChifa(2, repris[2], 320)), 5, "Caisse 1"));
    }

    [Fact]
    public void Vente_en_attente_d_un_format_ancien_reprise_sans_les_choix()
    {
        // Contenu enregistré avant le changement : pas de « ChoixChifa » dans le JSON
        var ancien = System.Text.Json.JsonSerializer.Serialize(PanierVolet(VoletAuPrix(300), null)).Replace(",\"ChoixChifa\":null", "");
        Assert.DoesNotContain("ChoixChifa", ancien);
        var p = System.Text.Json.JsonSerializer.Deserialize<S.PanierAttenteDto>(ancien)!;
        Assert.Single(p.Lignes);
        Assert.Empty(S.ChoixChifaAttente.Restaurer(p, voletRelu: true));
    }

    [Fact]
    public void Choix_Chifa_rétablis_seulement_avec_le_volet_relu_connus_et_produits_du_panier()
    {
        Assert.Null(S.ChoixChifaAttente.Garder(null, new Dictionary<long, string> { [2] = "aligne" }));   // sans volet : rien à garder
        var p = PanierVolet(VoletAuPrix(300), [new(2, "plus_eleve"), new(9, "aligne"), new(2, "inconnu")]);
        Assert.Equal(new Dictionary<long, string> { [2] = "plus_eleve" }, S.ChoixChifaAttente.Restaurer(p, voletRelu: true));
        Assert.Empty(S.ChoixChifaAttente.Restaurer(p, voletRelu: false));   // volet illisible à la reprise : choix abandonnés
    }

    [Fact]
    public async Task Reste_a_charge_saisi_sur_un_volet_journalise_avec_la_part_calculee_sur_le_prix_Chifa()
    {
        // Prix Chifa 300 < 350, sans le droit : vendu 350 x 2 = 700 ; organisme calculé 300 x 2 x 80 % = 480, reste calculé 220 ; saisi 250
        var (cas, ventes, journal) = CasChifa();
        await cas.ExecuterAsync(CommandeChifa(300) with { ResteACharge = 250 }, 5, "Caisse 1");
        var i = journal.Actions.IndexOf(Encaisser.ResteChargeManuelVolet);
        Assert.True(i >= 0);
        var d = journal.Details[i];
        Assert.Equal("06930126", d["volet"]);
        Assert.Equal((480m, 220m, 250m, 450m, 30m),
            ((decimal)d["part_organisme_calculee"]!, (decimal)d["reste_calcule"]!, (decimal)d["reste_saisi"]!, (decimal)d["part_organisme_retenue"]!, (decimal)d["ecart"]!));
        Assert.Equal(450m, ventes.Recu!.Vente.PartOrganisme);   // la saisie reste permise
    }

    [Fact]
    public async Task Sans_reste_saisi_ou_sans_volet_pas_d_entree_reste_manuel()
    {
        var (cas, _, journal) = CasChifa();
        await cas.ExecuterAsync(CommandeChifa(300), 5, "Caisse 1");
        Assert.DoesNotContain(Encaisser.ResteChargeManuelVolet, journal.Actions);

        (cas, _, journal) = CasChifa();
        // Tiers payant sans volet (médecin facultatif seulement avec un volet : ici, l'ordonnance du médecin n° 1)
        await cas.ExecuterAsync(CommandeChifa(300) with { VoletChifa = null, ResteACharge = 250,
            Ordonnance = new OrdonnanceSaisie(1, CatalogueEnMemoire.Aujourdhui, null) }, 5, "Caisse 1");        Assert.DoesNotContain(Encaisser.ResteChargeManuelVolet, journal.Actions);
    }

    [Fact]
    public void Detail_de_l_autorisation_d_un_choix_de_prix_Chifa()
    {
        S.EcartChifaLigneDto E(long id, string nom, bool aChoisir) => new(0, id, nom, 300, 350, 350, "plus_eleve", aChoisir);
        var detail = S.DetailAutorisationChifa.Construire([E(2, "Amoxicilline", true), E(3, "Doliprane", true), E(4, "Vit C", false)],
            new Dictionary<long, string> { [2] = "aligne", [3] = "garde", [4] = "aligne" }, m => FormattableString.Invariant($"{m:0.00} DA"))!;
        Assert.Equal(Pharma.Domain.Securite.CodeComptoir.ActionChoixPrixChifa, detail.Permission);   // même action que le refus du serveur
        Assert.Equal(["Produit", "Prix Chifa", "Notre prix", "Écart par boîte", "Choix demandé"], detail.Colonnes);
        Assert.Equal(2, detail.Lignes.Count);   // ligne sous le seuil (aligné d'office) : rien à autoriser
        Assert.Equal(["Amoxicilline", "300.00 DA", "350.00 DA", "50.00 DA", "S’aligner sur Chifa"], detail.Lignes[0]);
        Assert.Equal("Garder notre prix", detail.Lignes[1][4]);
        Assert.Null(S.DetailAutorisationChifa.Construire([E(2, "Amoxicilline", true)], new Dictionary<long, string>(), m => ""));
    }

    [Fact]
    public void Produit_servi_sur_deux_lots_au_meme_prix_une_seule_ligne_d_autorisation()
    {
        S.EcartChifaLigneDto E(int index) => new(index, 2, "Amoxicilline", 300, 350, 350, "plus_eleve", true);
        var detail = S.DetailAutorisationChifa.Construire([E(0), E(1)], new Dictionary<long, string> { [2] = "aligne" }, m => $"{m}")!;
        Assert.Single(detail.Lignes);
    }

    [Fact]
    public void Message_au_vendeur()
    {
        var m = CatalogueVoletChifa.Message("06930126", [new MajCatalogueChifa(2, "VIT D3", false, 150m, 156.48m)]);
        Assert.StartsWith("Catalogue mis à jour d’après le volet Chifa n° 06930126 : VIT D3 remboursable ; tarif de VIT D3 ", m);
        Assert.EndsWith(" DA", m);
    }
}
