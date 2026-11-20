using Pharma.Domain.Reglages;

namespace Pharma.Domain.Tests;

/// <summary>Validation d'un modèle complet mutu-v1, tout ou rien par table (D9) : un modèle valide et des modèles invalides pour chacune des 11 tables.</summary>
public class ValidationModeleTests
{
    private static readonly DateOnly Jour = new(2026, 11, 16);

    private const string Surveillance = """
        {"seuil_lignes_retirees":5,"seuil_tiroir":3,"seuil_attentes_abandonnees":3,"seuil_remises":5,"seuil_remises_patient":3,"seuil_ajustements":5,
         "seuil_casses":3,"seuil_echantillons":3,"jours_comptage_sensibles":7,"reception_aveugle":true,"jours_avoir_retour":30,
         "seuil_credit_nouveau_patient":5000,"jours_patient_recent":7,"seuil_credits_jour":20000,"jours_credit_sans_remboursement":60,
         "seuil_autorisations_valideur":10,"seuil_autorisations_employe":5,"minutes_postes_simultanes":10,"seuil_changements_poste":3,
         "seuil_baisses_prix":1,"seuil_sorties_tiroir":5}
        """;

    private const string Regles = """
        {"regles":[
          {"code":"patient_repete","active":true,"seuil":3,"periode_jours":30,"exiger_valideur":false,"unites_par_jour":null},
          {"code":"ordonnance_reutilisee","active":true,"seuil":2,"periode_jours":30,"exiger_valideur":true,"unites_par_jour":null},
          {"code":"quantite_anormale","active":true,"seuil":1,"periode_jours":1,"exiger_valideur":false,"unites_par_jour":4},
          {"code":"prescripteur_inhabituel","active":false,"seuil":5,"periode_jours":90,"exiger_valideur":false,"unites_par_jour":null},
          {"code":"prescripteur_hors_zone","active":true,"seuil":1,"periode_jours":1,"exiger_valideur":false,"unites_par_jour":null}]}
        """;

    public static IEnumerable<object[]> Valides() =>
    [
        [TablesMutualisables.Fiscal, """{"conservation_annees":10,"format_export_dgi":"csv-v1","separateur_csv":";","fuseau":"Africa/Algiers"}"""],
        [TablesMutualisables.AlertesPsychotropes, Regles],
        // Script 153 : « traitement_en_cours » présente (période vide) et ordonnance réutilisée sans limite ; absente : modèle plus ancien, valable
        [TablesMutualisables.AlertesPsychotropes, Regles
            .Replace("\"seuil\":2,\"periode_jours\":30", "\"seuil\":2,\"periode_jours\":null")
            .Replace("\"unites_par_jour\":null}]}", "\"unites_par_jour\":null},\n  {\"code\":\"traitement_en_cours\",\"active\":true,\"seuil\":1,\"periode_jours\":null,\"exiger_valideur\":true,\"unites_par_jour\":null}]}")],
        [TablesMutualisables.Tracabilite, """{"boite_obligatoire":true,"patient_obligatoire":"psychotrope"}"""],
        [TablesMutualisables.Serialisation, """{"actif":true,"factice_hors_ligne":false}"""],
        [TablesMutualisables.Substitution, """{"equivalent_exact_seulement":true}"""],
        [TablesMutualisables.Motifs, """
            {"motifs":[
              {"liste":"tiroir","code":"c_000000000001","libelle":"Fonds de caisse","actif":true,"ordre":10,"effet":"entree","detail":"fond"},
              {"liste":"charge","code":"c_000000000002","libelle":"Fonds de caisse","actif":true,"ordre":10,"effet":"aucun","detail":"aucun"},
              {"liste":"sortie_stock","code":"casse","libelle":"Casse","actif":true,"ordre":20,"effet":"aucun","detail":"aucun"}]}
            """],
        [TablesMutualisables.FormulaireSms, """{"texte":"Mon texte"}"""],
        [TablesMutualisables.FormulaireSms, """{"texte":null}"""],
        [TablesMutualisables.Surveillance, Surveillance],
        [TablesMutualisables.Alertes, """
            {"jours_orange":180,"jours_rouge":90,"horizon_risque_jours":365,"delai_fournisseur_jours":2,"jours_securite":7,
             "jours_cible_commande":30,"jours_dormant":90,"seuil_lot_delaisse":5,"seuil_lot_delaisse_collectif":10}
            """],
        [TablesMutualisables.Inventaire, """{"seuil_boites":2,"seuil_montant":2000.50}"""],
        [TablesMutualisables.Remises, """
            {"remises":[
              {"cle":"c_a1b2c3d4e5f6","categorie":"otc","pourcentage":10,"date_debut":"2026-12-01","date_fin":"2026-12-31","motif":"Fêtes"},
              {"cle":"c_a1b2c3d4e5f7","categorie":"otc","pourcentage":15,"date_debut":"2027-01-01","date_fin":"2027-01-31","motif":null},
              {"cle":"c_a1b2c3d4e5f8","categorie":"parapharmacie","pourcentage":5,"date_debut":"2026-12-15","date_fin":"2027-01-15","motif":null}]}
            """]
    ];

    private static string Element(string table, string valeur) =>
        $$"""
        {"format":"mutu-v1","mode":"impose","version_modele":4,{{(ValidationModele.PorteAjoutsLocaux(table) ? "\"ajouts_locaux\":true," : "")}}
         "publie_le":"2026-11-20T09:12:00Z","auteur":"admin-central-1","valeur":{{valeur}}}
        """;

    private static IReadOnlyList<string> Valider(string table, string valeur, DateOnly? jour = null) =>
        ValidationModele.ValiderElement(table, Element(table, valeur), jour);

    [Theory]
    [MemberData(nameof(Valides))]
    public void Un_modele_valide_pour_chacune_des_onze_tables_ne_donne_aucune_erreur(string table, string valeur) =>
        Assert.Empty(Valider(table, valeur, Jour));

    [Fact]
    public void Les_onze_tables_ont_un_modele_valide() =>
        Assert.Equal(TablesMutualisables.Toutes.Order(), Valides().Select(v => (string)v[0]).Distinct().Order());

    // ---- un modèle invalide pour chacune des 11 tables ----------------------------------

    public static IEnumerable<object[]> Invalides() =>
    [
        [TablesMutualisables.Fiscal, """{"conservation_annees":3,"format_export_dgi":"csv-v1","separateur_csv":";","fuseau":"Africa/Algiers"}""", "6 à 30"],
        [TablesMutualisables.Fiscal, """{"conservation_annees":10,"format_export_dgi":"xml","separateur_csv":"|","fuseau":""}""", "csv-v1"],
        [TablesMutualisables.AlertesPsychotropes, Regles.Replace("\"seuil\":3", "\"seuil\":0"), "seuil entre 1 et 1000"],
        [TablesMutualisables.AlertesPsychotropes, """{"regles":[]}""", "absente"],
        [TablesMutualisables.AlertesPsychotropes, Regles.Replace("\"seuil\":3,\"periode_jours\":30", "\"seuil\":3,\"periode_jours\":null"), "période obligatoire"],
        [TablesMutualisables.Tracabilite, """{"boite_obligatoire":true,"patient_obligatoire":"souvent"}""", "Patient obligatoire"],
        [TablesMutualisables.Serialisation, """{"actif":true,"factice_hors_ligne":false,"connecteur":"factice"}""", "champ inconnu « connecteur »"],
        [TablesMutualisables.Substitution, """{"equivalent_exact_seulement":"oui"}""", "vrai ou faux"],
        [TablesMutualisables.Motifs, """{"motifs":[{"liste":"inconnue","code":"c_x","libelle":"A","actif":true,"ordre":1,"effet":"aucun","detail":"aucun"}]}""", "Liste de motifs inconnue"],
        [TablesMutualisables.Motifs, """{"motifs":[{"liste":"sortie_stock","code":"c_x","libelle":"A","actif":true,"ordre":1,"effet":"aucun","detail":"aucun"}]}""", "ne reçoit pas de nouveau motif"],
        [TablesMutualisables.FormulaireSms, "{\"texte\":\"" + new string('a', 4001) + "\"}", "4000"],
        [TablesMutualisables.Surveillance, Surveillance.Replace("\"seuil_tiroir\":3", "\"seuil_tiroir\":0"), "au moins 1"],
        [TablesMutualisables.Alertes, """
            {"jours_orange":90,"jours_rouge":180,"horizon_risque_jours":365,"delai_fournisseur_jours":2,"jours_securite":7,
             "jours_cible_commande":30,"jours_dormant":90,"seuil_lot_delaisse":5,"seuil_lot_delaisse_collectif":10}
            """, "plus courte"],
        [TablesMutualisables.Inventaire, """{"seuil_boites":-1,"seuil_montant":10}""", "négatifs"],
        [TablesMutualisables.Remises, """{"remises":[{"cle":"c_a1b2c3d4e5f6","categorie":"otc","pourcentage":0,"date_debut":"2026-12-01","date_fin":"2026-12-31","motif":null}]}""", "entier de 1 à 100"],
    ];

    [Theory]
    [MemberData(nameof(Invalides))]
    public void Un_modele_invalide_est_refuse_avec_un_message_clair(string table, string valeur, string extrait)
    {
        var erreurs = Valider(table, valeur, Jour);
        Assert.NotEmpty(erreurs);
        Assert.Contains(erreurs, e => e.Contains(extrait, StringComparison.Ordinal));
    }

    [Fact]
    public void Les_onze_tables_ont_un_modele_invalide()
    {
        var couvertes = Invalides().Select(v => (string)v[0]).Distinct().Order().ToList();
        Assert.Equal(TablesMutualisables.Toutes.Order(), couvertes);
    }

    // ---- règles propres aux modèles ---------------------------------------------------------

    [Fact]
    public void Les_horaires_d_ouverture_ne_font_pas_partie_d_un_modele()
    {
        var avecHoraires = Surveillance.Replace("\"seuil_tiroir\":3,", "\"seuil_tiroir\":3,\"heure_ouverture\":\"08:00\",");
        var erreurs = Valider(TablesMutualisables.Surveillance, avecHoraires);
        Assert.Contains(erreurs, e => e.Contains("propre à chaque officine"));
    }

    [Fact]
    public void Aucun_identifiant_propre_a_une_officine()
    {
        var produit = """{"remises":[{"cle":"c_a1b2c3d4e5f6","categorie":"otc","produit_id":12,"pourcentage":10,"date_debut":"2026-12-01","date_fin":"2026-12-31","motif":null}]}""";
        Assert.Contains(Valider(TablesMutualisables.Remises, produit), e => e.Contains("produit_id") && e.Contains("propre à chaque officine"));
        var auteur = """{"remises":[{"cle":"c_a1b2c3d4e5f6","categorie":"otc","cree_par":1,"pourcentage":10,"date_debut":"2026-12-01","date_fin":"2026-12-31","motif":null}]}""";
        Assert.Contains(Valider(TablesMutualisables.Remises, auteur), e => e.Contains("cree_par"));
        Assert.Contains(Valider(TablesMutualisables.Substitution, """{"equivalent_exact_seulement":true,"id":1}"""), e => e.Contains("« id »"));
        Assert.Contains(Valider(TablesMutualisables.Tracabilite, """{"boite_obligatoire":true,"patient_obligatoire":"jamais","officine_id":"x"}"""),
            e => e.Contains("officine_id"));
    }

    [Fact]
    public void Un_champ_manquant_ou_inconnu_ou_mal_type_est_une_erreur()
    {
        Assert.Contains(Valider(TablesMutualisables.Inventaire, """{"seuil_boites":2}"""), e => e.Contains("« seuil_montant » est manquant"));
        Assert.Contains(Valider(TablesMutualisables.Inventaire, """{"seuil_boites":2,"seuil_montant":1,"x":1}"""), e => e.Contains("champ inconnu « x »"));
        Assert.Contains(Valider(TablesMutualisables.Inventaire, """{"seuil_boites":"2","seuil_montant":1}"""), e => e.Contains("nombre entier"));
        Assert.Contains(Valider(TablesMutualisables.Inventaire, """{"seuil_boites":2,"seuil_montant":1.234}"""), e => e.Contains("deux décimales"));
        Assert.Contains(Valider(TablesMutualisables.Inventaire, "[]"), e => e.Contains("objet JSON"));
    }

    [Fact]
    public void Tout_ou_rien_toutes_les_erreurs_de_la_table_sont_rendues()
    {
        var erreurs = Valider(TablesMutualisables.Fiscal, """{"conservation_annees":3,"format_export_dgi":"xml","separateur_csv":"|","fuseau":""}""");
        Assert.Equal(4, erreurs.Count);
    }

    [Fact]
    public void Psychotropes_regle_en_double_ou_absente()
    {
        var double_ = Regles.Replace("ordonnance_reutilisee", "patient_repete");
        var erreurs = Valider(TablesMutualisables.AlertesPsychotropes, double_);
        Assert.Contains(erreurs, e => e.Contains("en double"));
        Assert.Contains(erreurs, e => e.Contains("« ordonnance_reutilisee » absente"));
        Assert.Contains(Valider(TablesMutualisables.AlertesPsychotropes, Regles.Replace("patient_repete", "autre_regle")), e => e.Contains("Règle inconnue"));
    }

    [Fact]
    public void Motifs_doublon_de_cle_et_de_libelle()
    {
        var m = """{"liste":"tiroir","code":"c_a","libelle":"Fonds","actif":true,"ordre":1,"effet":"aucun","detail":"aucun"}""";
        var memeCle = $$"""{"motifs":[{{m}},{{m}}]}""";
        Assert.Contains(Valider(TablesMutualisables.Motifs, memeCle), e => e.Contains("en double"));
        var memeLibelle = $$"""{"motifs":[{{m}},{{m.Replace("\"c_a\"", "\"c_b\"").Replace("Fonds", "FONDS")}}]}""";
        Assert.Contains(Valider(TablesMutualisables.Motifs, memeLibelle), e => e.Contains("existe déjà dans cette liste"));
    }

    [Fact]
    public void Motifs_effet_hors_tiroir_code_invalide_et_ordre()
    {
        var hors = """{"motifs":[{"liste":"charge","code":"c_a","libelle":"A","actif":true,"ordre":1,"effet":"sortie","detail":"coffre"}]}""";
        Assert.Contains(Valider(TablesMutualisables.Motifs, hors), e => e.Contains("Seuls les motifs du tiroir"));
        var incoherent = """{"motifs":[{"liste":"tiroir","code":"c_a","libelle":"A","actif":true,"ordre":1,"effet":"entree","detail":"depense"}]}""";
        Assert.Contains(Valider(TablesMutualisables.Motifs, incoherent), e => e.Contains("incohérent"));
        var code = """{"motifs":[{"liste":"tiroir","code":"Mauvais Code","libelle":"A","actif":true,"ordre":1,"effet":"aucun","detail":"aucun"}]}""";
        Assert.Contains(Valider(TablesMutualisables.Motifs, code), e => e.Contains("Code du motif"));
        var ordre = """{"motifs":[{"liste":"tiroir","code":"c_a","libelle":"A","actif":true,"ordre":-1,"effet":"aucun","detail":"aucun"}]}""";
        Assert.Contains(Valider(TablesMutualisables.Motifs, ordre), e => e.Contains("Ordre"));
    }

    [Fact]
    public void Remises_chevauchement_sur_la_meme_categorie_et_cle_invalide()
    {
        var chevauche = """
            {"remises":[
              {"cle":"c_a1b2c3d4e5f6","categorie":"otc","pourcentage":10,"date_debut":"2026-12-01","date_fin":"2026-12-31","motif":null},
              {"cle":"c_a1b2c3d4e5f7","categorie":"otc","pourcentage":10,"date_debut":"2026-12-31","date_fin":"2027-01-10","motif":null}]}
            """;
        Assert.Contains(Valider(TablesMutualisables.Remises, chevauche), e => e.Contains("déjà une remise automatique"));
        var cle = """{"remises":[{"cle":"abc","categorie":"otc","pourcentage":10,"date_debut":"2026-12-01","date_fin":"2026-12-31","motif":null}]}""";
        Assert.Contains(Valider(TablesMutualisables.Remises, cle), e => e.Contains("identifiant"));
    }

    [Fact]
    public void Remises_fin_passee_refusee_seulement_a_la_publication()
    {
        var passee = """{"remises":[{"cle":"c_a1b2c3d4e5f6","categorie":"otc","pourcentage":10,"date_debut":"2026-01-01","date_fin":"2026-02-01","motif":null}]}""";
        Assert.Contains(Valider(TablesMutualisables.Remises, passee, Jour), e => e.Contains("déjà passée"));
        Assert.Empty(Valider(TablesMutualisables.Remises, passee));
    }

    [Fact]
    public void Remises_motif_trop_long_date_inversee()
    {
        var motif = "{\"remises\":[{\"cle\":\"c_a1b2c3d4e5f6\",\"categorie\":\"otc\",\"pourcentage\":10,\"date_debut\":\"2026-12-01\",\"date_fin\":\"2026-12-31\",\"motif\":\""
            + new string('x', 201) + "\"}]}";
        Assert.Contains(Valider(TablesMutualisables.Remises, motif), e => e.Contains("200"));
        var inversee = """{"remises":[{"cle":"c_a1b2c3d4e5f6","categorie":"otc","pourcentage":10,"date_debut":"2026-12-31","date_fin":"2026-12-01","motif":null}]}""";
        Assert.Contains(Valider(TablesMutualisables.Remises, inversee), e => e.Contains("jour du début"));
    }

    // ---- enveloppe ---------------------------------------------------------------------------

    [Fact]
    public void Enveloppe_mode_local_sans_valeur()
    {
        Assert.Empty(ValidationModele.ValiderElement(TablesMutualisables.Fiscal, """{"format":"mutu-v1","mode":"local","version_modele":3}"""));
        var avecValeur = ValidationModele.ValiderElement(TablesMutualisables.Fiscal,
            """{"format":"mutu-v1","mode":"local","version_modele":3,"valeur":{}}""");
        Assert.Contains(avecValeur, e => e.Contains("Mode « local »"));
    }

    [Theory]
    [InlineData("""{"format":"mutu-v2","mode":"propose","version_modele":1,"publie_le":"2026-11-20T09:12:00Z","auteur":"a","valeur":{"equivalent_exact_seulement":true}}""", "mutu-v2")]
    [InlineData("""{"format":"mutu-v1","mode":"obligatoire","version_modele":1,"publie_le":"2026-11-20T09:12:00Z","auteur":"a","valeur":{"equivalent_exact_seulement":true}}""", "Mode")]
    [InlineData("""{"format":"mutu-v1","mode":"propose","version_modele":0,"publie_le":"2026-11-20T09:12:00Z","auteur":"a","valeur":{"equivalent_exact_seulement":true}}""", "Version du modèle")]
    [InlineData("""{"format":"mutu-v1","mode":"propose","version_modele":1,"publie_le":"hier","auteur":"a","valeur":{"equivalent_exact_seulement":true}}""", "Date de publication")]
    [InlineData("""{"format":"mutu-v1","mode":"propose","version_modele":1,"publie_le":"2026-11-20T09:12:00Z","auteur":"","valeur":{"equivalent_exact_seulement":true}}""", "Auteur")]
    [InlineData("""{"format":"mutu-v1","mode":"propose","version_modele":1,"publie_le":"2026-11-20T09:12:00Z","auteur":"a"}""", "« valeur » est manquant")]
    [InlineData("""{"format":"mutu-v1","mode":"propose","version_modele":1,"ajouts_locaux":true,"publie_le":"2026-11-20T09:12:00Z","auteur":"a","valeur":{"equivalent_exact_seulement":true}}""", "ajouts_locaux")]
    [InlineData("""{"format":"mutu-v1","mode":"propose","version_modele":1,"publie_le":"2026-11-20T09:12:00Z","auteur":"a","valeur":{"equivalent_exact_seulement":true},"extra":1}""", "champ inconnu « extra »")]
    [InlineData("pas du json", "JSON valide")]
    [InlineData("", "vide")]
    public void Enveloppe_invalide(string json, string extrait) =>
        Assert.Contains(ValidationModele.ValiderElement(TablesMutualisables.Substitution, json), e => e.Contains(extrait, StringComparison.Ordinal));

    [Fact]
    public void Motifs_et_remises_exigent_ajouts_locaux()
    {
        var sans = """{"format":"mutu-v1","mode":"propose","version_modele":1,"publie_le":"2026-11-20T09:12:00Z","auteur":"a","valeur":{"motifs":[]}}""";
        Assert.Contains(ValidationModele.ValiderElement(TablesMutualisables.Motifs, sans), e => e.Contains("ajouts_locaux"));
    }

    [Fact]
    public void Table_hors_groupe_deux_refusee()
    {
        foreach (var t in new[] { "parametre_caisse", "wilaya", "" })
            Assert.Contains(ValidationModele.ValiderElement(t, "{}"), e => e.Contains("ne fait pas partie des réglages mutualisables"));
    }

    [Fact]
    public void Taille_bornee_a_64_kio()
    {
        var limite = ValidationModele.TailleMax;
        var gros = Element(TablesMutualisables.FormulaireSms, "{\"texte\":\"" + new string('a', limite) + "\"}");
        Assert.Contains(ValidationModele.ValiderElement(TablesMutualisables.FormulaireSms, gros), e => e.Contains("64 Kio"));
        // Octets, pas caractères : « é » prend deux octets
        var accents = Element(TablesMutualisables.FormulaireSms, "{\"texte\":\"" + new string('é', 3500) + "\"}");
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(accents) < limite);
        Assert.DoesNotContain(ValidationModele.ValiderElement(TablesMutualisables.FormulaireSms, accents), e => e.Contains("64 Kio"));
    }
}
