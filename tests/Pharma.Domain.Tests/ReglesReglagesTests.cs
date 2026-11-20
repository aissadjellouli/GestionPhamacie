using Pharma.Domain.Alertes;
using Pharma.Domain.Commun;
using Pharma.Domain.Referentiels;
using Pharma.Domain.Reglages;

namespace Pharma.Domain.Tests;

/// <summary>Règles de contrôle des 11 tables du groupe 2 extraites des cas d'usage (lot M1) : cas valide et cas invalide de chacune.</summary>
public class ReglesReglagesTests
{
    private static string Message(Action a) => Assert.Throws<RegleMetierException>(a).Message;

    // ---- parametre_fiscal -------------------------------------------------------------

    [Theory]
    [InlineData(6)]
    [InlineData(10)]
    [InlineData(30)]
    public void Fiscal_conservation_valide(int annees) => RegleFiscale.VerifierConservation(annees);

    [Theory]
    [InlineData(5)]
    [InlineData(31)]
    [InlineData(0)]
    public void Fiscal_conservation_invalide(int annees) => Assert.Contains("6 à 30", Message(() => RegleFiscale.VerifierConservation(annees)));

    [Fact]
    public void Fiscal_format_et_separateur()
    {
        RegleFiscale.VerifierFormat("csv-v1");
        Assert.Contains("csv-v1", Message(() => RegleFiscale.VerifierFormat("xml")));
        foreach (var s in new[] { ";", ",", "\t" }) RegleFiscale.VerifierSeparateur(s);
        Assert.Contains("Séparateur", Message(() => RegleFiscale.VerifierSeparateur("|")));
        Assert.Contains("Séparateur", Message(() => RegleFiscale.VerifierSeparateur(null)));
    }

    [Theory]
    [InlineData("Africa/Algiers", true)]
    [InlineData("UTC", true)]
    [InlineData("America/Argentina/Buenos_Aires", true)]
    [InlineData("", false)]
    [InlineData("  ", false)]
    [InlineData("Africa/", false)]
    [InlineData("a b", false)]
    [InlineData("1/2", false)]
    public void Fiscal_fuseau(string fuseau, bool valide)
    {
        if (valide) RegleFiscale.VerifierFuseau(fuseau);
        else Assert.Contains("Fuseau", Message(() => RegleFiscale.VerifierFuseau(fuseau)));
    }

    [Fact]
    public void Fiscal_fuseau_trop_long() =>
        Assert.Contains("Fuseau", Message(() => RegleFiscale.VerifierFuseau(new string('a', 65))));

    // ---- parametre_tracabilite --------------------------------------------------------

    [Theory]
    [InlineData("jamais")]
    [InlineData("psychotrope")]
    [InlineData("ordonnance")]
    [InlineData("toujours")]
    public void Tracabilite_patient_obligatoire_valide(string valeur) => RegleTracabilite.VerifierPatientObligatoire(valeur);

    [Theory]
    [InlineData("souvent")]
    [InlineData("")]
    [InlineData(null)]
    public void Tracabilite_patient_obligatoire_invalide(string? valeur) =>
        Assert.Contains("Patient obligatoire", Message(() => RegleTracabilite.VerifierPatientObligatoire(valeur)));

    // ---- regle_alerte_psychotrope -----------------------------------------------------

    [Fact]
    public void Psychotropes_regle_valide()
    {
        RegleAlertesPsychotropes.VerifierRegle("patient_repete", "Patient", 3, 30, null);
        RegleAlertesPsychotropes.VerifierRegle("quantite_anormale", "Quantité", 1000, 3650, 100);
        // Script 153 : période vide (« sans limite » ou sans objet) pour ces deux règles seulement
        RegleAlertesPsychotropes.VerifierRegle("ordonnance_reutilisee", "Ordonnance", 1, null, null);
        RegleAlertesPsychotropes.VerifierRegle("traitement_en_cours", "En cours", 1, null, null);
    }

    [Theory]
    [InlineData("patient_repete")]
    [InlineData("quantite_anormale")]
    [InlineData("prescripteur_inhabituel")]
    [InlineData("prescripteur_hors_zone")]
    public void Psychotropes_periode_vide_refusee_hors_ordonnance_et_traitement_en_cours(string code) =>
        Assert.Equal("L : période obligatoire (1 à 3650 jours).", Message(() => RegleAlertesPsychotropes.VerifierRegle(code, "L", 1, null, null)));

    [Theory]
    [InlineData("inconnue", 3, 30, null, "Règle inconnue : inconnue.")]
    [InlineData("patient_repete", 0, 30, null, "L : seuil entre 1 et 1000.")]
    [InlineData("patient_repete", 1001, 30, null, "L : seuil entre 1 et 1000.")]
    [InlineData("patient_repete", 3, 0, null, "L : période entre 1 et 3650 jours.")]
    [InlineData("patient_repete", 3, 3651, null, "L : période entre 1 et 3650 jours.")]
    [InlineData("patient_repete", 3, 30, 0, "L : unités par jour entre 1 et 100 (ou vide).")]
    [InlineData("patient_repete", 3, 30, 101, "L : unités par jour entre 1 et 100 (ou vide).")]
    public void Psychotropes_regle_invalide(string code, int seuil, int periode, int? unites, string attendu) =>
        Assert.Equal(attendu, Message(() => RegleAlertesPsychotropes.VerifierRegle(code, "L", seuil, periode, unites)));

    // ---- parametre_serialisation et parametre_substitution : deux booléens, aucune règle ---------

    [Fact]
    public void Serialisation_et_substitution_n_ont_que_des_booleens_sans_regle_de_valeur() =>
        Assert.Empty(ValidationModele.ValiderValeur(TablesMutualisables.Serialisation,
            System.Text.Json.JsonDocument.Parse("""{"actif":true,"factice_hors_ligne":false}""").RootElement));

    // ---- motif_reglable -----------------------------------------------------------------

    [Fact]
    public void Motifs_code()
    {
        RegleMotifs.VerifierCode("c_abc123");
        RegleMotifs.VerifierCode("autre");
        Assert.Contains("Code du motif", Message(() => RegleMotifs.VerifierCode("Majuscule")));
        Assert.Contains("Code du motif", Message(() => RegleMotifs.VerifierCode("")));
        Assert.Contains("Code du motif", Message(() => RegleMotifs.VerifierCode(new string('a', 41))));
    }

    [Fact]
    public void Motifs_libelle_unique_sans_tenir_compte_de_la_casse()
    {
        RegleMotifs.ExigerLibelleUnique("Casse", ["Vol", "Perte"]);
        Assert.Contains("existe déjà", Message(() => RegleMotifs.ExigerLibelleUnique("vol", ["Vol", "Perte"])));
    }

    [Fact]
    public void Motifs_ajout_permis_seulement_dans_certaines_listes()
    {
        RegleMotifs.ExigerAjoutPermis(RegleMotifs.Tiroir);
        Assert.Contains("ne reçoit pas de nouveau motif", Message(() => RegleMotifs.ExigerAjoutPermis(RegleMotifs.SortieStock)));
        Assert.Contains("ne reçoit pas de nouveau motif", Message(() => RegleMotifs.ExigerAjoutPermis(RegleMotifs.RetourFournisseur)));
    }

    [Fact]
    public void Motifs_effet_seulement_pour_le_tiroir()
    {
        RegleMotifs.ExigerEffetAutorise(RegleMotifs.Tiroir, "sortie");
        RegleMotifs.ExigerEffetAutorise(RegleMotifs.Attente, "aucun");
        Assert.Contains("tiroir", Message(() => RegleMotifs.ExigerEffetAutorise(RegleMotifs.Attente, "sortie")));
    }

    [Fact]
    public void Motifs_liste_libelle_et_effet_restent_ceux_de_la_saisie_locale()
    {
        Assert.Contains("inconnue", Message(() => RegleMotifs.ExigerListe("xx")));
        Assert.Contains("80", Message(() => RegleMotifs.VerifierLibelle(new string('a', 81))));
        Assert.Contains("incohérent", Message(() => Caisse.RegleTiroir.VerifierEffet("entree", "depense")));
        Assert.Equal(("sortie", "coffre"), Caisse.RegleTiroir.VerifierEffet("sortie", "coffre"));
    }

    [Theory]
    [InlineData("C Test")]
    [InlineData("c_test")]
    [InlineData("  C_Autre  ")]
    public void Motifs_un_code_local_ne_commence_jamais_par_c_underscore(string libelle)
    {
        var code = RegleMotifs.CodeDepuisLibelle(libelle, []);
        Assert.False(RegleMotifs.EstCodeCentral(code), code);
        RegleMotifs.VerifierCode(code);
    }

    [Fact]
    public void Motifs_codes_locaux_existants_inchanges()
    {
        Assert.Equal("perte_de_stock", RegleMotifs.CodeDepuisLibelle("Perte de stock", []));
        Assert.Equal("casse_2", RegleMotifs.CodeDepuisLibelle("Casse", ["casse"]));
        Assert.Equal("coffre_interne", RegleMotifs.CodeDepuisLibelle("Coffre interné", []));
    }

    // ---- parametre_formulaire_sms -------------------------------------------------------

    [Fact]
    public void Formulaire_texte()
    {
        Assert.Null(RegleFormulaireSms.Normaliser("  ", "défaut"));
        Assert.Null(RegleFormulaireSms.Normaliser(" défaut ", "défaut"));
        Assert.Equal("Mon texte", RegleFormulaireSms.Normaliser(" Mon texte ", "défaut"));
        RegleFormulaireSms.Normaliser(new string('a', 4000), "défaut");
        Assert.Contains("4000", Message(() => RegleFormulaireSms.Normaliser(new string('a', 4001), "défaut")));
        Assert.Contains("4000", Message(() => RegleFormulaireSms.VerifierLongueur(new string('a', 4001))));
    }

    // ---- parametre_surveillance ---------------------------------------------------------

    private static SeuilsSurveillance Seuils(Func<SeuilsSurveillance, SeuilsSurveillance>? f = null)
    {
        var s = new SeuilsSurveillance(5, 3, 3, 5, 3, 5, 3, 3, 7, true, 30, 5000, 7, 20000, 60, 10, 5, 10, 3, 1, 5);
        return f?.Invoke(s) ?? s;
    }

    [Fact]
    public void Surveillance_seuils_par_defaut_valides() => RegleSurveillance.VerifierSeuils(Seuils());

    [Fact]
    public void Surveillance_seuils_invalides()
    {
        Assert.Equal("Chaque seuil doit être d’au moins 1.", Message(() => RegleSurveillance.VerifierSeuils(Seuils(s => s with { SeuilTiroir = 0 }))));
        Assert.Equal("Chaque seuil doit être d’au moins 1.", Message(() => RegleSurveillance.VerifierSeuils(Seuils(s => s with { SeuilSortiesTiroir = 0 }))));
        Assert.Contains("crédit", Message(() => RegleSurveillance.VerifierSeuils(Seuils(s => s with { SeuilCreditsJour = 0 }))));
        Assert.Contains("1 à 90", Message(() => RegleSurveillance.VerifierSeuils(Seuils(s => s with { JoursComptageSensibles = 91 }))));
        Assert.Contains("1 à 365", Message(() => RegleSurveillance.VerifierSeuils(Seuils(s => s with { JoursAvoirRetour = 0 }))));
        Assert.Contains("1 à 90", Message(() => RegleSurveillance.VerifierSeuils(Seuils(s => s with { JoursPatientRecent = 0 }))));
        Assert.Contains("1 à 365", Message(() => RegleSurveillance.VerifierSeuils(Seuils(s => s with { JoursCreditSansRemboursement = 366 }))));
        Assert.Contains("1 à 120", Message(() => RegleSurveillance.VerifierSeuils(Seuils(s => s with { MinutesPostesSimultanes = 121 }))));
    }

    [Fact]
    public void Surveillance_horaires_restent_une_regle_locale()
    {
        RegleSurveillance.VerifierHoraires(null, null);
        RegleSurveillance.VerifierHoraires(new TimeOnly(8, 0), new TimeOnly(0, 30));
        Assert.Contains("ou aucune des deux", Message(() => RegleSurveillance.VerifierHoraires(new TimeOnly(8, 0), null)));
        Assert.Contains("différentes", Message(() => RegleSurveillance.VerifierHoraires(new TimeOnly(8, 0), new TimeOnly(8, 0))));
    }

    // ---- parametre_alerte ---------------------------------------------------------------

    [Fact]
    public void Alertes_reglage_general_valide() => RegleSeuils.VerifierReglageGeneral(180, 90, 365, 2, 7, 30, 90, 5, 10);

    [Theory]
    [InlineData(0, 90, 365, 2, 7, 30, 90, 5, 10, "au moins 1 jour")]
    [InlineData(90, 90, 365, 2, 7, 30, 90, 5, 10, "plus courte")]
    [InlineData(180, 90, 0, 2, 7, 30, 90, 5, 10, "risque d’écoulement")]
    [InlineData(180, 90, 365, 2, 7, 0, 90, 5, 10, "risque d’écoulement")]
    [InlineData(180, 90, 365, 2, 7, 30, 0, 5, 10, "risque d’écoulement")]
    [InlineData(180, 90, 365, 2, 7, 30, 90, 0, 10, "lot délaissé")]
    [InlineData(180, 90, 365, 2, 7, 30, 90, 5, 0, "lot délaissé")]
    [InlineData(180, 90, 365, -1, 7, 30, 90, 5, 10, "négatifs")]
    [InlineData(180, 90, 365, 2, -1, 30, 90, 5, 10, "négatifs")]
    public void Alertes_reglage_general_invalide(int o, int r, int h, int d, int s, int c, int dor, int l, int lc, string extrait) =>
        Assert.Contains(extrait, Message(() => RegleSeuils.VerifierReglageGeneral(o, r, h, d, s, c, dor, l, lc)));

    // ---- parametre_inventaire -----------------------------------------------------------

    [Fact]
    public void Inventaire_seuils()
    {
        RegleInventaire.VerifierSeuils(0, 0);
        RegleInventaire.VerifierSeuils(2, 2000.50m);
        Assert.Contains("négatifs", Message(() => RegleInventaire.VerifierSeuils(-1, 0)));
        Assert.Contains("négatifs", Message(() => RegleInventaire.VerifierSeuils(0, -0.01m)));
    }

    // ---- remise_automatique -------------------------------------------------------------

    [Fact]
    public void Remises_cible_exclusive()
    {
        RegleRemisesAutomatiques.VerifierCibleExclusive(true, null);
        RegleRemisesAutomatiques.VerifierCibleExclusive(false, "otc");
        Assert.Contains("l’un ou l’autre", Message(() => RegleRemisesAutomatiques.VerifierCibleExclusive(true, "otc")));
        Assert.Contains("l’un ou l’autre", Message(() => RegleRemisesAutomatiques.VerifierCibleExclusive(false, " ")));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(50)]
    [InlineData(100)]
    public void Remises_pourcentage_valide(int p) => RegleRemisesAutomatiques.VerifierPourcentage(p);

    [Theory]
    [InlineData(-5, "négative")]
    [InlineData(0, "entier de 1 à 100")]
    [InlineData(101, "entier de 1 à 100")]
    [InlineData(10.5, "entier de 1 à 100")]
    public void Remises_pourcentage_invalide(double p, string extrait) =>
        Assert.Contains(extrait, Message(() => RegleRemisesAutomatiques.VerifierPourcentage((decimal)p)));

    [Fact]
    public void Remises_periode()
    {
        var jour = new DateOnly(2026, 11, 16);
        RegleRemisesAutomatiques.VerifierPeriode(jour, jour, jour);
        RegleRemisesAutomatiques.VerifierPeriode(jour.AddDays(-10), jour.AddDays(-5), null);
        Assert.Contains("jour du début", Message(() => RegleRemisesAutomatiques.VerifierPeriode(jour, jour.AddDays(-1), jour)));
        Assert.Contains("déjà passée", Message(() => RegleRemisesAutomatiques.VerifierPeriode(jour.AddDays(-5), jour.AddDays(-1), jour)));
    }

    [Fact]
    public void Remises_motif_produit_et_chevauchement()
    {
        Assert.Null(RegleRemisesAutomatiques.NettoyerMotif("  "));
        Assert.Equal("Fête", RegleRemisesAutomatiques.NettoyerMotif(" Fête "));
        Assert.Contains("200", Message(() => RegleRemisesAutomatiques.NettoyerMotif(new string('x', 201))));
        RegleRemisesAutomatiques.VerifierProduit("Doliprane", false);
        Assert.Contains("remboursable", Message(() => RegleRemisesAutomatiques.VerifierProduit("Doliprane", true)));

        var d = new DateOnly(2026, 12, 1);
        Assert.True(RegleRemisesAutomatiques.Chevauche(d, d.AddDays(10), d.AddDays(10), d.AddDays(20)));
        Assert.False(RegleRemisesAutomatiques.Chevauche(d, d.AddDays(9), d.AddDays(10), d.AddDays(20)));
        Assert.True(RegleRemisesAutomatiques.Chevauche(d, d.AddDays(30), d.AddDays(5), d.AddDays(6)));
        RegleRemisesAutomatiques.ExigerSansChevauchement("OTC", false);
        Assert.Contains("OTC", Message(() => RegleRemisesAutomatiques.ExigerSansChevauchement("OTC", true)));
    }

    // ---- liste fermée --------------------------------------------------------------------

    [Fact]
    public void Liste_fermee_de_onze_tables_sans_le_groupe_trois()
    {
        Assert.Equal(11, TablesMutualisables.Toutes.Count);
        Assert.Equal(11, TablesMutualisables.Toutes.Distinct().Count());
        Assert.True(TablesMutualisables.Connue("motif_reglable"));
        foreach (var local in new[] { "parametre_caisse", "parametre_sauvegarde", "parametre_notification", "parametre_rapport", "serveur_officine",
                     "compteur_ticket", "chaine_integrite", "parametre_integrite", "wilaya", null })
            Assert.False(TablesMutualisables.Connue(local), local);
        Assert.Equal("mutu_parametre_fiscal", TablesMutualisables.Cle("parametre_fiscal"));
    }
}
