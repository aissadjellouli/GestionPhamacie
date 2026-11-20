using Npgsql;
using Pharma.Application.Commun;
using Pharma.Application.Patients;
using Pharma.Application.Tracabilite;
using Pharma.Infrastructure.Patients;
using Pharma.Infrastructure.Referentiels;
using Pharma.Infrastructure.Tracabilite;
using Xunit.Abstractions;

namespace Pharma.Architecture.Tests;

/// <summary>Copie jetable « pharmacie_jetable_sugg_… » (base neuve database/init, script 150 compris), supprimée à la fin.</summary>
public sealed class BaseJetableSuggestions : IDisposable
{
    public BaseJetableM4 Base { get; } = new("pharmacie_jetable_sugg_", true);
    public void Dispose() => Base.Dispose();
}

/// <summary>
/// Suggestions pendant la frappe (2026-11-18) sur une COPIE JETABLE : chaque champ de la liste blanche se lit (table et colonne
/// existent), valeurs sans accents ni majuscules, distinctes, non vides, 10 au plus ; recherche patient et médecin sans accents.
/// Sans serveur PostgreSQL joignable, les tests ne font rien (comme les autres tests de base de ce projet).
/// </summary>
public class SuggestionsSqlTests(BaseJetableSuggestions fixture, ITestOutputHelper sortie) : IClassFixture<BaseJetableSuggestions>
{
    private readonly BaseJetableM4 base_ = fixture.Base;

    /// <summary>
    /// Preuve que les tests de base ont tourné : nom de la copie jetable écrit dans la sortie du test (dotnet test, journal détaillé) ;
    /// avec PHARMA_EXIGER_BASE=1, un serveur injoignable fait échouer le test au lieu de le laisser passer sans rien vérifier.
    /// </summary>
    [Fact]
    public void La_copie_jetable_est_joignable()
    {
        sortie.WriteLine(base_.Disponible ? $"Base jetable utilisée : {base_.Nom}" : "PostgreSQL injoignable : tests de base non exécutés.");
        if (Environment.GetEnvironmentVariable("PHARMA_EXIGER_BASE") == "1") Assert.True(base_.Disponible, "PostgreSQL injoignable");
    }

    private async Task ExecuterAsync(string sql)
    {
        await using var cmd = base_.Source!.CreateCommand(sql);
        await cmd.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task Chaque_champ_de_la_liste_blanche_se_lit()
    {
        if (!base_.Disponible) return;
        var depot = new SuggestionsSql(base_.Source!);
        foreach (var champ in ChampsSuggestions.Liste.Values)
            Assert.NotNull(await depot.ValeursAsync(champ, "ab", ChampsSuggestions.Limite));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            depot.ValeursAsync(new ChampSuggestion("specialite_medecin", "patient", "nom", null, ["caisse.vendre"]), "ab", 10));
    }

    [Fact]
    public async Task Valeurs_sans_accents_distinctes_non_vides_et_dix_au_plus()
    {
        if (!base_.Disponible) return;
        await ExecuterAsync("""
            INSERT INTO medecin (nom, specialite) VALUES
              ('Dr Sugg 1', 'Pédiatrie'), ('Dr Sugg 2', 'Pédiatrie'), ('Dr Sugg 3', ' pediatrie '), ('Dr Sugg 4', '   '), ('Dr Sugg 5', NULL);
            INSERT INTO medecin (nom, specialite) SELECT 'Dr Sugg L' || g, 'Zzlogie ' || g FROM generate_series(1, 14) g;
            """);
        var depot = new SuggestionsSql(base_.Source!);
        var champ = ChampsSuggestions.Liste["specialite_medecin"];
        Assert.Equal(["Pédiatrie"], await depot.ValeursAsync(champ, "PEDIA", 10));
        Assert.Equal(["Pédiatrie"], await depot.ValeursAsync(champ, "édiat", 10));
        var nombreuses = await depot.ValeursAsync(champ, "zzlogie", 10);
        Assert.Equal(10, nombreuses.Count);
        Assert.Equal(10, nombreuses.Distinct().Count());
        Assert.DoesNotContain(nombreuses, string.IsNullOrWhiteSpace);
        // Saisie prise comme texte (paramètre lié), jamais comme du SQL
        Assert.Empty(await depot.ValeursAsync(champ, "'; DROP TABLE medecin; --", 10));
    }

    [Fact]
    public async Task Libelles_du_tiroir_limites_aux_depenses()
    {
        if (!base_.Disponible) return;
        var champ = ChampsSuggestions.Liste["libelle_depense_tiroir"];
        Assert.Equal("detail = 'depense'", champ.Condition);
        Assert.Empty(await new SuggestionsSql(base_.Source!).ValeursAsync(champ, "zz", 10));
    }

    [Fact]
    public async Task Libelles_de_tresorerie_et_de_financement_saisis_a_la_main_seulement()
    {
        if (!base_.Disponible) return;
        await ExecuterAsync("""
            INSERT INTO mouvement_tresorerie (compte_id, date_mouvement, sens, montant, nature, categorie, libelle, organisme_id, automatique, utilisateur_id)
            SELECT (SELECT id FROM compte_tresorerie WHERE type = 'coffre'), current_date, v.sens, 10, v.nature, v.categorie, v.libelle,
                   CASE WHEN v.nature = 'versement_organisme' THEN (SELECT min(id) FROM organisme) END, v.auto, (SELECT min(id) FROM utilisateur)
            FROM (VALUES ('sortie', 'depense', 'divers', 'Zzmouv Électricité', false),
                         ('entree', 'encaissement', NULL, 'Zzmouv apport', false),
                         ('sortie', 'depense', 'divers', 'Zzmouv automatique', true),
                         ('entree', 'transfert', NULL, 'Zzmouv Coffre → Banque', false),
                         ('entree', 'versement_organisme', NULL, 'Zzmouv Versement CNAS', false),
                         ('entree', 'versement_organisme', NULL, 'Versement ' || (SELECT code FROM organisme ORDER BY id LIMIT 1), false),
                         ('entree', 'financement', NULL, 'Zzmouv prêt · pour : loyer', false),
                         ('sortie', 'depense', 'divers', 'Zzmouv loyer (payée par : prêt)', false)) AS v(sens, nature, categorie, libelle, auto);
            INSERT INTO utilisation_financement (source, usage, date_usage, montant, libelle, mouvement_id, depense_id, utilisateur_id)
            SELECT 'titulaire', 'depense', current_date, 10, 'Zzusage loyer',
                   (SELECT id FROM mouvement_tresorerie WHERE libelle = 'Zzmouv prêt · pour : loyer'),
                   (SELECT id FROM mouvement_tresorerie WHERE libelle = 'Zzmouv loyer (payée par : prêt)'), (SELECT min(id) FROM utilisateur);
            INSERT INTO utilisation_financement (source, usage, date_usage, montant, libelle, mouvement_id, utilisateur_id)
            SELECT 'titulaire', 'tresorerie', current_date, 10, 'Apport du titulaire',
                   (SELECT id FROM mouvement_tresorerie WHERE libelle = 'Zzmouv apport'), (SELECT min(id) FROM utilisateur);
            INSERT INTO remboursement_titulaire (date_remboursement, montant, compte_id, mouvement_id, libelle, utilisateur_id)
            SELECT current_date, 10, (SELECT id FROM compte_tresorerie WHERE type = 'coffre'), (SELECT id FROM mouvement_tresorerie WHERE libelle = 'Zzmouv Électricité'),
                   v.libelle, (SELECT min(id) FROM utilisateur)
            FROM (VALUES ('Remboursement du titulaire'), ('Zzremb partiel')) AS v(libelle);
            """);
        var depot = new SuggestionsSql(base_.Source!);
        // Trésorerie (2026-11-20) : aucun libellé tapé à la main n'est proposé, ni dépense, ni encaissement, ni versement d'organisme
        // modifié ; le libellé de versement d'organisme mis par le logiciel (« Versement <code de l'organisme> ») l'est
        var versement = ChampsSuggestions.Liste["libelle_versement_organisme"];
        Assert.Empty(await depot.ValeursAsync(versement, "zzmouv", 10));
        await using (var cmd = base_.Source!.CreateCommand("SELECT 'Versement ' || code FROM organisme ORDER BY id LIMIT 1"))
            Assert.Contains((string)(await cmd.ExecuteScalarAsync())!, await depot.ValeursAsync(versement, "versement", 10));
        Assert.Equal(["Zzusage loyer"], await depot.ValeursAsync(ChampsSuggestions.Liste["libelle_usage_financement"], "loyer", 10));
        Assert.Empty(await depot.ValeursAsync(ChampsSuggestions.Liste["libelle_usage_financement"], "apport du", 10));
        Assert.Equal(["Zzremb partiel"], await depot.ValeursAsync(ChampsSuggestions.Liste["libelle_remboursement_titulaire"], "rem", 10));
    }

    [Fact]
    public async Task Recherche_patient_et_medecin_sans_accents_ni_majuscules()
    {
        if (!base_.Disponible) return;
        await ExecuterAsync("""
            INSERT INTO patient (nom, prenom) VALUES ('Bénaïssa', 'Éloïse'), ('Zzsuggestion', NULL);
            INSERT INTO medecin (nom, specialite) VALUES ('Dr Hélène Zzsugg', 'Cardiologie');
            """);
        IPatients patients = new PatientsSql(base_.Source!);
        IMedecins medecins = new PatientsSql(base_.Source!);
        Assert.Contains(await patients.RechercherAsync("eloise", 20), p => p.Nom == "Bénaïssa");
        Assert.Contains(await patients.RechercherAsync("BENAISSA", 20), p => p.Nom == "Bénaïssa");
        Assert.Contains(await patients.RechercherAsync("benaissa elo", 20), p => p.Nom == "Bénaïssa");
        Assert.Contains(await patients.RechercherAsync("zzsuggestion", 20), p => p.Nom == "Zzsuggestion");
        Assert.Contains(await medecins.RechercherAsync("helene zzsugg", 20), m => m.Nom == "Dr Hélène Zzsugg");
        Assert.Contains(await medecins.RechercherAsync("HÉLÈNE", 20), m => m.Nom == "Dr Hélène Zzsugg");
    }

    [Fact]
    public async Task Les_caracteres_speciaux_tapes_sont_pris_a_la_lettre()
    {
        // \, % et _ échappés (ESCAPE '\') : « __ » ou « % » ne renvoient pas tout (revue 2026-11-18)
        if (!base_.Disponible) return;
        await ExecuterAsync("""
            INSERT INTO patient (nom, prenom) VALUES ('Zzspecial', 'Ab'), ('Zz_sous', 'Cd'), ('Zz%cent', NULL);
            INSERT INTO medecin (nom, specialite) VALUES ('Dr Zzspecial', 'Spéc_iale'), ('Dr Zz_sous', NULL);
            """);
        IPatients patients = new PatientsSql(base_.Source!);
        IMedecins medecins = new PatientsSql(base_.Source!);
        foreach (var motif in new[] { "__", "%", "%%", "\\" })
        {
            Assert.DoesNotContain(await patients.RechercherAsync(motif, 50), p => p.Nom == "Zzspecial");
            Assert.DoesNotContain(await medecins.RechercherAsync(motif, 50), m => m.Nom == "Dr Zzspecial");
            Assert.DoesNotContain(await medecins.SansWilayaAsync(motif, 50), m => m.Nom == "Dr Zzspecial");
        }
        // Le caractère tapé reste cherché tel quel
        Assert.Equal(["Zz_sous"], (await patients.RechercherAsync("zz_", 50)).Select(p => p.Nom));
        Assert.Equal(["Zz%cent"], (await patients.RechercherAsync("zz%", 50)).Select(p => p.Nom));
        Assert.Equal(["Dr Zz_sous"], (await medecins.RechercherAsync("zz_", 50)).Select(m => m.Nom));
        var specialites = new SuggestionsSql(base_.Source!);
        Assert.Equal(["Spéc_iale"], await specialites.ValeursAsync(ChampsSuggestions.Liste["specialite_medecin"], "c_i", 10));
        Assert.DoesNotContain("Spéc_iale", await specialites.ValeursAsync(ChampsSuggestions.Liste["specialite_medecin"], "__", 10));
    }

    [Fact]
    public async Task Medecins_sans_wilaya_et_releve_de_dette_sans_accents()
    {
        if (!base_.Disponible) return;
        await ExecuterAsync("""
            INSERT INTO medecin (nom, specialite) VALUES ('Dr Zzwilaya Hélène', NULL);
            INSERT INTO patient (nom, prenom, solde_credit) VALUES ('Zzdette Bénaïssa', 'Éloïse', 150);
            """);
        IMedecins medecins = new PatientsSql(base_.Source!);
        Assert.Contains(await medecins.SansWilayaAsync("zzwilaya helene", 50), m => m.Nom == "Dr Zzwilaya Hélène");
        Assert.Contains(await medecins.SansWilayaAsync("HÉLÈNE", 50), m => m.Nom == "Dr Zzwilaya Hélène");
        var credits = new Pharma.Infrastructure.Caisse.CreditsClientsSql(base_.Source!);
        Assert.Contains(await credits.AvecCreditAsync("zzdette benaissa eloise"), p => p.Nom == "Zzdette Bénaïssa Éloïse");
        Assert.Contains(await credits.AvecCreditAsync("BENAISSA"), p => p.Nom == "Zzdette Bénaïssa Éloïse");
        Assert.DoesNotContain(await credits.AvecCreditAsync("__"), p => p.Nom == "Zzdette Bénaïssa Éloïse");
        var page = await credits.AvecCreditPageAsync("benaissa", new Pharma.Application.Commun.DemandePage(1, 10));
        Assert.Contains(page.Lignes, p => p.Nom == "Zzdette Bénaïssa Éloïse");
    }

    [Fact]
    public async Task Rayons_proposes_par_ordre_alphabetique_meme_sans_lettre_tapee()
    {
        if (!base_.Disponible) return;
        await ExecuterAsync("""
            INSERT INTO emplacement (code, rayon, etagere) VALUES
              ('ZZC-01', 'ZZC', '01'), ('ZZA-01', 'ZZA', '01'), ('ZZA-02', 'ZZA', '02'), ('ZZA-03', 'ZZA', '03'), ('ZZB-01', 'ZZB', '01');
            """);
        var depot = new SuggestionsSql(base_.Source!);
        var champ = ChampsSuggestions.Liste["rayon_emplacement"];
        // Tri alphabétique (et non par fréquence : ZZA, trois fois, ne passe pas devant pour cette raison), chaque rayon une fois
        Assert.Equal(["ZZA", "ZZB", "ZZC"], await depot.ValeursAsync(champ, "zz", 10));
        // Sans lettre tapée : tous les rayons (30 au plus, 2026-11-20), triés
        var tous = await depot.ValeursAsync(champ, "", champ.Limite);
        Assert.NotEmpty(tous);
        Assert.Equal(tous.OrderBy(v => v.ToLowerInvariant(), StringComparer.Ordinal), tous);
    }

    [Fact]
    public async Task Lots_proposes_au_rappel_epuises_compris_numeros_distincts_dix_au_plus()
    {
        if (!base_.Disponible) return;
        await ExecuterAsync("""
            INSERT INTO fournisseur (raison_sociale) VALUES ('Zzgros Un'), ('Zzgros Deux');
            INSERT INTO produit (nom_commercial) VALUES ('Zzlotprod'), ('Zzautreprod');
            INSERT INTO reception (fournisseur_id) SELECT id FROM fournisseur WHERE raison_sociale LIKE 'Zzgros%';
            -- Lot épuisé, lot en stock livré par deux grossistes (un seul numéro proposé), 12 autres lots en stock
            INSERT INTO lot (produit_id, reception_id, fournisseur_id, numero_lot, date_peremption, quantite_disponible)
            SELECT p.id, r.id, r.fournisseur_id, v.numero, v.peremption, v.quantite
            FROM (VALUES ('ZZEPUISE', 'Zzgros Un', DATE '2030-01-01', 0), ('ZZDOUBLE', 'Zzgros Un', DATE '2029-06-01', 3),
                         ('ZZDOUBLE', 'Zzgros Deux', DATE '2029-03-01', 2)) AS v(numero, grossiste, peremption, quantite)
            JOIN fournisseur f ON f.raison_sociale = v.grossiste JOIN reception r ON r.fournisseur_id = f.id
            CROSS JOIN produit p WHERE p.nom_commercial = 'Zzlotprod';
            INSERT INTO lot (produit_id, reception_id, fournisseur_id, numero_lot, date_peremption, quantite_disponible)
            SELECT p.id, r.id, r.fournisseur_id, 'ZZSERIE' || g, DATE '2028-01-01' + g, 1
            FROM generate_series(1, 12) g CROSS JOIN produit p
            JOIN reception r ON r.fournisseur_id = (SELECT id FROM fournisseur WHERE raison_sociale = 'Zzgros Un')
            WHERE p.nom_commercial = 'Zzlotprod';
            INSERT INTO lot (produit_id, reception_id, fournisseur_id, numero_lot, date_peremption, quantite_disponible)
            SELECT p.id, r.id, r.fournisseur_id, 'ZZAUTRE', DATE '2030-01-01', 5
            FROM produit p JOIN reception r ON r.fournisseur_id = (SELECT id FROM fournisseur WHERE raison_sociale = 'Zzgros Un')
            WHERE p.nom_commercial = 'Zzautreprod';
            """);
        long produit;
        await using (var cmd = base_.Source!.CreateCommand("SELECT id FROM produit WHERE nom_commercial = 'Zzlotprod'"))
            produit = (long)(await cmd.ExecuteScalarAsync())!;
        ILotsRappelables depot = new LotsRappelablesSql(base_.Source!);

        // Lot épuisé proposé, marqué épuisé (sans majuscules)
        var epuise = Assert.Single(await depot.LotsAsync(produit, "epuis", 10));
        Assert.Equal(("ZZEPUISE", 0, true), (epuise.NumeroLot, epuise.Quantite, epuise.Epuise));
        // Un numéro livré par deux grossistes : une seule proposition, boîtes additionnées, péremption la plus proche
        var double_ = Assert.Single(await depot.LotsAsync(produit, "zzdouble", 10));
        Assert.Equal((5, new DateOnly(2029, 3, 1), false), (double_.Quantite, double_.DatePeremption, double_.Epuise));
        // 14 numéros pour ce produit : 10 au plus, distincts, lots en stock d'abord ; jamais un lot d'un autre produit
        var tous = await depot.LotsAsync(produit, "", 10);
        Assert.Equal(10, tous.Count);
        Assert.Equal(10, tous.Select(l => l.NumeroLot).Distinct().Count());
        Assert.DoesNotContain(tous, l => l.NumeroLot == "ZZAUTRE");
        Assert.DoesNotContain(tous, l => l.Epuise);   // 13 numéros en stock passent avant le lot épuisé
        Assert.Empty(await depot.LotsAsync(produit, "zzautre", 10));
        // Saisie prise comme texte (paramètre lié)
        Assert.Empty(await depot.LotsAsync(produit, "'; DROP TABLE lot; --", 10));
        // « __ » et « % » pris à la lettre : aucun numéro (ils n'en contiennent pas)
        Assert.Empty(await depot.LotsAsync(produit, "__", 10));
        Assert.Empty(await depot.LotsAsync(produit, "%", 10));
    }
}
