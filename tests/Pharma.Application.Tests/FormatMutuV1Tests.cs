using System.Text;
using Pharma.Domain.Reglages;
using Pharma.Shared;

namespace Pharma.Application.Tests;

/// <summary>Format d'échange mutu-v1 (lot M1) : aller-retour, déterminisme, bornes de taille, lecture stricte, accord avec les règles du domaine.</summary>
public class FormatMutuV1Tests
{
    private static readonly DateTime Publie = new(2026, 11, 20, 9, 12, 0, DateTimeKind.Utc);

    private static readonly MutuSurveillanceDto Surveillance = new(5, 3, 3, 5, 3, 5, 3, 3, 7, true, 30, 5000m, 7, 20000m, 60, 10, 5, 10, 3, 1, 5);

    private static readonly MutuRegleAlertePsychotropeDto[] Regles =
    [
        new("prescripteur_hors_zone", true, 1, 1, false, null), new("patient_repete", true, 3, 30, false, null),
        new("quantite_anormale", true, 1, 1, false, 4), new("ordonnance_reutilisee", true, 2, 30, true, null),
        new("prescripteur_inhabituel", false, 5, 90, false, null)
    ];

    public static Dictionary<string, ValeurMutuDto> Valeurs() => new()
    {
        ["parametre_fiscal"] = new MutuFiscalDto(10, "csv-v1", ";", "Africa/Algiers"),
        ["regle_alerte_psychotrope"] = new MutuAlertesPsychotropesDto(Regles),
        ["parametre_tracabilite"] = new MutuTracabiliteDto(true, "psychotrope"),
        ["parametre_serialisation"] = new MutuSerialisationDto(true, false),
        ["parametre_substitution"] = new MutuSubstitutionDto(true),
        ["motif_reglable"] = new MutuMotifsDto(
        [
            new("tiroir", "c_000000000001", "Fonds de caisse", true, 10, "entree", "fond"),
            new("sortie_stock", "casse", "Casse", true, 20, "aucun", "aucun")
        ]),
        ["parametre_formulaire_sms"] = new MutuFormulaireSmsDto("Mon texte"),
        ["parametre_surveillance"] = Surveillance,
        ["parametre_alerte"] = new MutuAlertesDto(180, 90, 365, 2, 7, 30, 90, 5, 10),
        ["parametre_inventaire"] = new MutuInventaireDto(2, 2000.50m),
        ["remise_automatique"] = new MutuRemisesDto(
        [
            new("c_a1b2c3d4e5f7", "otc", 15, new DateOnly(2027, 1, 1), new DateOnly(2027, 1, 31), null),
            new("c_a1b2c3d4e5f6", "otc", 10, new DateOnly(2026, 12, 1), new DateOnly(2026, 12, 31), "Fêtes")
        ])
    };

    public static IEnumerable<object[]> Tables() => ContratMutu.Tables.Select(t => new object[] { t });

    private static ElementMutuDto Element(string table, ValeurMutuDto? valeur = null, string mode = ContratMutu.ModeImpose) =>
        new(mode, 4, table is "motif_reglable" or "remise_automatique" ? true : null, Publie, "admin-central-1", valeur ?? Valeurs()[table]);

    [Fact]
    public void La_liste_des_tables_du_format_est_celle_du_domaine() =>
        Assert.Equal(TablesMutualisables.Toutes, ContratMutu.Tables);

    [Fact]
    public void Constantes_du_format()
    {
        Assert.Equal("mutu-v1", ContratMutu.Format);
        Assert.Equal(64 * 1024, ContratMutu.TailleMax);
        Assert.Equal(ContratMutu.TailleMax, ValidationModele.TailleMax);
        Assert.Equal("mutu_regle_alerte_psychotrope", ContratMutu.Cle("regle_alerte_psychotrope"));
        Assert.Equal(TablesMutualisables.Cle("motif_reglable"), ContratMutu.Cle("motif_reglable"));
    }

    [Theory]
    [MemberData(nameof(Tables))]
    public void Aller_retour_de_chaque_table(string table)
    {
        var avant = Element(table);
        var octets = SerialisationMutuV1.Serialiser(table, avant);
        var apres = SerialisationMutuV1.Lire(table, octets);

        Assert.Equal(avant.Mode, apres.Mode);
        Assert.Equal(avant.VersionModele, apres.VersionModele);
        Assert.Equal(avant.AjoutsLocaux, apres.AjoutsLocaux);
        Assert.Equal(avant.PublieLeUtc, apres.PublieLeUtc);
        Assert.Equal(avant.Auteur, apres.Auteur);
        Assert.Equal(apres.Valeur!.GetType(), avant.Valeur!.GetType());
        // Rien ne se perd : la relecture donne les mêmes octets
        Assert.Equal(octets, SerialisationMutuV1.Serialiser(table, apres));
    }

    [Fact]
    public void Aller_retour_des_valeurs_simples()
    {
        foreach (var table in new[] { "parametre_fiscal", "parametre_tracabilite", "parametre_serialisation", "parametre_substitution",
                     "parametre_formulaire_sms", "parametre_surveillance", "parametre_alerte", "parametre_inventaire" })
        {
            var avant = Valeurs()[table];
            var apres = SerialisationMutuV1.Lire(table, SerialisationMutuV1.Serialiser(table, Element(table))).Valeur;
            Assert.Equal(avant, apres);
        }
    }

    [Fact]
    public void Aller_retour_des_listes_rendues_dans_l_ordre_de_la_cle()
    {
        var regles = (MutuAlertesPsychotropesDto)SerialisationMutuV1.Lire("regle_alerte_psychotrope",
            SerialisationMutuV1.Serialiser("regle_alerte_psychotrope", Element("regle_alerte_psychotrope"))).Valeur!;
        Assert.Equal(["ordonnance_reutilisee", "patient_repete", "prescripteur_hors_zone", "prescripteur_inhabituel", "quantite_anormale"],
            regles.Regles.Select(r => r.Code));
        Assert.Equal(4, regles.Regles.Single(r => r.Code == "quantite_anormale").UnitesParJour);
        Assert.Null(regles.Regles.First().UnitesParJour);

        var remises = (MutuRemisesDto)SerialisationMutuV1.Lire("remise_automatique",
            SerialisationMutuV1.Serialiser("remise_automatique", Element("remise_automatique"))).Valeur!;
        Assert.Equal(["c_a1b2c3d4e5f6", "c_a1b2c3d4e5f7"], remises.Remises.Select(r => r.Cle));
        Assert.Equal(new DateOnly(2026, 12, 31), remises.Remises[0].DateFin);
        Assert.Equal("Fêtes", remises.Remises[0].Motif);

        var motifs = (MutuMotifsDto)SerialisationMutuV1.Lire("motif_reglable",
            SerialisationMutuV1.Serialiser("motif_reglable", Element("motif_reglable"))).Valeur!;
        Assert.Equal(["sortie_stock", "tiroir"], motifs.Motifs.Select(m => m.Liste));
    }

    [Fact]
    public void Mode_local_aller_retour_sans_valeur()
    {
        var octets = SerialisationMutuV1.Serialiser("parametre_fiscal", ElementMutuDto.Local(7));
        Assert.Equal("""{"format":"mutu-v1","mode":"local","version_modele":7}""", Encoding.UTF8.GetString(octets));
        var relu = SerialisationMutuV1.Lire("parametre_fiscal", octets);
        Assert.Equal(ElementMutuDto.Local(7), relu);
        Assert.Empty(ValidationModele.ValiderElement("parametre_fiscal", Encoding.UTF8.GetString(octets)));
    }

    [Fact]
    public void Forme_exacte_d_un_element()
    {
        var json = Encoding.UTF8.GetString(SerialisationMutuV1.Serialiser("parametre_inventaire", Element("parametre_inventaire")));
        Assert.Equal("""{"auteur":"admin-central-1","format":"mutu-v1","mode":"impose","publie_le":"2026-11-20T09:12:00Z","valeur":{"seuil_boites":2,"seuil_montant":2000.5},"version_modele":4}""", json);
    }

    // ---- déterminisme -------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(Tables))]
    public void Deux_serialisations_donnent_les_memes_octets(string table) =>
        Assert.Equal(SerialisationMutuV1.Serialiser(table, Element(table)), SerialisationMutuV1.Serialiser(table, Element(table)));

    [Fact]
    public void L_ordre_des_listes_et_l_echelle_des_decimaux_ne_changent_pas_les_octets()
    {
        var a = new MutuRemisesDto(
        [
            new("c_000000000001", "otc", 10, new DateOnly(2026, 12, 1), new DateOnly(2026, 12, 31), null),
            new("c_000000000002", "bio", 20.0m, new DateOnly(2026, 12, 1), new DateOnly(2026, 12, 31), null)
        ]);
        var b = new MutuRemisesDto([a.Remises[1] with { Pourcentage = 20m }, a.Remises[0] with { Pourcentage = 10.00m }]);
        Assert.Equal(SerialisationMutuV1.Serialiser("remise_automatique", Element("remise_automatique", a)),
            SerialisationMutuV1.Serialiser("remise_automatique", Element("remise_automatique", b)));

        var inv1 = new MutuInventaireDto(2, 2000.50m);
        var inv2 = new MutuInventaireDto(2, 2000.5m);
        Assert.Equal(SerialisationMutuV1.Serialiser("parametre_inventaire", Element("parametre_inventaire", inv1)),
            SerialisationMutuV1.Serialiser("parametre_inventaire", Element("parametre_inventaire", inv2)));
        var entier = Encoding.UTF8.GetString(SerialisationMutuV1.Serialiser("parametre_inventaire",
            Element("parametre_inventaire", new MutuInventaireDto(2, 5000.00m))));
        Assert.Contains("\"seuil_montant\":5000}", entier);
    }

    [Fact]
    public void La_date_de_publication_est_toujours_en_utc_a_la_seconde()
    {
        var local = Element("parametre_substitution") with { PublieLeUtc = new DateTime(2026, 11, 20, 9, 12, 0, 999, DateTimeKind.Utc) };
        Assert.Contains("\"publie_le\":\"2026-11-20T09:12:00Z\"", Encoding.UTF8.GetString(SerialisationMutuV1.Serialiser("parametre_substitution", local)));
    }

    [Fact]
    public void Les_accents_sont_ecrits_tels_quels_en_utf8()
    {
        var json = Encoding.UTF8.GetString(SerialisationMutuV1.Serialiser("parametre_formulaire_sms",
            Element("parametre_formulaire_sms", new MutuFormulaireSmsDto("Éphémère « test »"))));
        Assert.Contains("Éphémère « test »", json);
    }

    // ---- accord avec les règles du domaine ----------------------------------------------------

    [Theory]
    [MemberData(nameof(Tables))]
    public void Ce_que_le_format_ecrit_est_un_modele_valide(string table)
    {
        var json = Encoding.UTF8.GetString(SerialisationMutuV1.Serialiser(table, Element(table)));
        Assert.Empty(ValidationModele.ValiderElement(table, json, new DateOnly(2026, 11, 16)));
    }

    [Theory]
    [MemberData(nameof(Tables))]
    public void Aucun_identifiant_propre_a_une_officine_dans_le_format(string table)
    {
        var json = Encoding.UTF8.GetString(SerialisationMutuV1.Serialiser(table, Element(table)));
        foreach (var interdit in new[] { "produit_id", "cree_par", "officine_id", "modifie_par", "modifie_le", "heure_ouverture", "heure_fermeture",
                     "\"id\":", "connecteur", "libelle_local" })
            Assert.DoesNotContain(interdit, json);
    }

    // ---- bornes de taille ---------------------------------------------------------------------------

    [Fact]
    public void Un_element_juste_sous_64_kio_est_ecrit_et_relu()
    {
        // Libellés de 80 caractères : on remplit une liste de motifs presque jusqu'à la borne
        var motifs = Enumerable.Range(0, 340).Select(i => new MutuMotifDto("charge", $"c_{i:D10}", new string('a', 70) + $"{i:D4}", true, i, "aucun", "aucun")).ToList();
        var valeur = new MutuMotifsDto(motifs);
        var octets = SerialisationMutuV1.Serialiser("motif_reglable", Element("motif_reglable", valeur));
        Assert.InRange(octets.Length, 1, ContratMutu.TailleMax);
        Assert.Equal(340, ((MutuMotifsDto)SerialisationMutuV1.Lire("motif_reglable", octets).Valeur!).Motifs.Count);
    }

    [Fact]
    public void Un_element_de_plus_de_64_kio_est_refuse_a_l_ecriture_et_a_la_lecture()
    {
        var motifs = Enumerable.Range(0, 800).Select(i => new MutuMotifDto("charge", $"c_{i:D10}", new string('a', 70) + $"{i:D4}", true, i, "aucun", "aucun")).ToList();
        var ex = Assert.Throws<FormatMutuException>(() => SerialisationMutuV1.Serialiser("motif_reglable", Element("motif_reglable", new MutuMotifsDto(motifs))));
        Assert.Contains("64 Kio", ex.Message);

        var trop = new byte[ContratMutu.TailleMax + 1];
        Array.Fill(trop, (byte)' ');
        Assert.Contains("64 Kio", Assert.Throws<FormatMutuException>(() => SerialisationMutuV1.Lire("motif_reglable", trop)).Message);
    }

    [Fact]
    public void La_borne_est_exactement_64_kio_a_la_lecture()
    {
        var exact = new byte[ContratMutu.TailleMax];
        Array.Fill(exact, (byte)' ');
        // 64 Kio d'espaces : taille acceptée, mais ce n'est pas un JSON valide
        Assert.Contains("JSON valide", Assert.Throws<FormatMutuException>(() => SerialisationMutuV1.Lire("parametre_fiscal", exact)).Message);
    }

    // ---- lecture stricte ----------------------------------------------------------------------------------------

    [Theory]
    [InlineData("""{"format":"mutu-v1","mode":"impose","version_modele":1,"publie_le":"2026-11-20T09:12:00Z","auteur":"a","valeur":{"equivalent_exact_seulement":true,"x":1}}""")]
    [InlineData("""{"format":"mutu-v1","mode":"impose","version_modele":1,"publie_le":"2026-11-20T09:12:00Z","auteur":"a","valeur":{}}""")]
    [InlineData("""{"format":"mutu-v1","mode":"impose","version_modele":1,"publie_le":"2026-11-20T09:12:00Z","auteur":"a","valeur":{"equivalent_exact_seulement":"oui"}}""")]
    [InlineData("""{"format":"mutu-v2","mode":"impose","version_modele":1,"publie_le":"2026-11-20T09:12:00Z","auteur":"a","valeur":{"equivalent_exact_seulement":true}}""")]
    [InlineData("""{"format":"mutu-v1","mode":"autre","version_modele":1,"publie_le":"2026-11-20T09:12:00Z","auteur":"a","valeur":{"equivalent_exact_seulement":true}}""")]
    [InlineData("""{"format":"mutu-v1","mode":"impose","version_modele":1,"publie_le":"2026-11-20","auteur":"a","valeur":{"equivalent_exact_seulement":true}}""")]
    [InlineData("""{"format":"mutu-v1","mode":"impose","version_modele":1,"publie_le":"2026-11-20T09:12:00Z","auteur":"a"}""")]
    [InlineData("""{"format":"mutu-v1","mode":"impose","version_modele":1,"ajouts_locaux":true,"publie_le":"2026-11-20T09:12:00Z","auteur":"a","valeur":{"equivalent_exact_seulement":true}}""")]
    [InlineData("""{"format":"mutu-v1","mode":"impose","version_modele":1,"publie_le":"2026-11-20T09:12:00Z","auteur":"a","valeur":{"equivalent_exact_seulement":true},"extra":0}""")]
    [InlineData("""{"format":"mutu-v1","mode":"local","version_modele":1,"auteur":"a"}""")]
    [InlineData("[]")]
    [InlineData("pas du json")]
    public void Lecture_stricte_refuse_un_element_mal_forme(string json) =>
        Assert.Throws<FormatMutuException>(() => SerialisationMutuV1.Lire("parametre_substitution", Encoding.UTF8.GetBytes(json)));

    [Fact]
    public void Lecture_refuse_un_identifiant_de_produit_dans_une_remise()
    {
        var json = """{"ajouts_locaux":true,"auteur":"a","format":"mutu-v1","mode":"impose","publie_le":"2026-11-20T09:12:00Z","valeur":{"remises":[{"categorie":"otc","cle":"c_a1b2c3d4e5f6","date_debut":"2026-12-01","date_fin":"2026-12-31","motif":null,"pourcentage":10,"produit_id":9}]},"version_modele":1}""";
        Assert.Throws<FormatMutuException>(() => SerialisationMutuV1.Lire("remise_automatique", Encoding.UTF8.GetBytes(json)));
    }

    [Fact]
    public void Ecriture_refuse_les_elements_incoherents()
    {
        Assert.Throws<FormatMutuException>(() => SerialisationMutuV1.Serialiser("table_inconnue", ElementMutuDto.Local(1)));
        Assert.Throws<FormatMutuException>(() => SerialisationMutuV1.Serialiser("parametre_fiscal", Element("parametre_tracabilite")));
        Assert.Throws<FormatMutuException>(() => SerialisationMutuV1.Serialiser("parametre_fiscal", Element("parametre_fiscal") with { Auteur = " " }));
        Assert.Throws<FormatMutuException>(() => SerialisationMutuV1.Serialiser("parametre_fiscal", Element("parametre_fiscal") with { AjoutsLocaux = true }));
        Assert.Throws<FormatMutuException>(() => SerialisationMutuV1.Serialiser("motif_reglable", Element("motif_reglable") with { AjoutsLocaux = null }));
        Assert.Throws<FormatMutuException>(() => SerialisationMutuV1.Serialiser("parametre_fiscal", Element("parametre_fiscal") with { Mode = "autre" }));
        Assert.Throws<FormatMutuException>(() => SerialisationMutuV1.Serialiser("parametre_fiscal", ElementMutuDto.Local(1) with { Auteur = "a" }));
        Assert.Throws<FormatMutuException>(() => SerialisationMutuV1.Serialiser("parametre_fiscal", Element("parametre_fiscal") with { PublieLeUtc = null }));
    }

    [Fact]
    public void Un_modele_invalide_passe_le_format_mais_pas_la_validation_du_domaine()
    {
        // Le format ne porte que la forme : la règle « conservation de 6 à 30 ans » est celle du domaine
        var invalide = Element("parametre_fiscal", new MutuFiscalDto(2, "csv-v1", ";", "Africa/Algiers"));
        var octets = SerialisationMutuV1.Serialiser("parametre_fiscal", invalide);
        Assert.Equal(2, ((MutuFiscalDto)SerialisationMutuV1.Lire("parametre_fiscal", octets).Valeur!).ConservationAnnees);
        Assert.Contains(ValidationModele.ValiderElement("parametre_fiscal", Encoding.UTF8.GetString(octets)), e => e.Contains("6 à 30"));
    }
}
