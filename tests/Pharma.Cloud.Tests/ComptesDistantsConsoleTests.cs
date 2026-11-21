using Npgsql;
using Pharma.Cloud.Api;
using Pharma.Cloud.Api.Comptes;
using BaseCentraleJetable = Pharma.Cloud.Tests.DescendantsMutuV1Tests.BaseCentraleJetable;

namespace Pharma.Cloud.Tests;

/// <summary>Lots S7.1 et S7.2a : commandes console des comptes distants (scripts centraux 030 et 032).</summary>
public class ComptesDistantsConsoleTests
{
    [Fact]
    public void Commandes_reconnues_et_arguments_controles()
    {
        foreach (var c in new[] { "--creer-compte-editeur", "--creer-compte-titulaire", "--code-rattachement", "--comptes-distants", "--desactiver-compte", "--deverrouiller-compte" })
            Assert.True(ConsoleAdministration.Concerne([c]));
        Assert.Equal(("editeur.dev", "Éditeur", (string?)null, (string?)"e@exemple.org"),
            ComptesDistantsAdministration.LireCreation(["--creer-compte-editeur", "Editeur.Dev", "--nom", " Éditeur ", "--email", "e@exemple.org"]));
        Assert.Equal("+213555000001", ComptesDistantsAdministration.LireCreation(["--creer-compte-editeur", "a.b.c", "--nom", "A", "--telephone", "+213 555 000 001"]).Telephone);
        Assert.Throws<ArgumentException>(() => ComptesDistantsAdministration.LireCreation(["--creer-compte-editeur", "ab", "--nom", "A", "--email", "e@exemple.org"]));
        Assert.Throws<ArgumentException>(() => ComptesDistantsAdministration.LireCreation(["--creer-compte-editeur", "a.b.c", "--email", "e@exemple.org"]));
        Assert.Throws<ArgumentException>(() => ComptesDistantsAdministration.LireCreation(["--creer-compte-editeur", "a.b.c", "--nom", "A"]));
        Assert.Throws<ArgumentException>(() => ComptesDistantsAdministration.LireCreation(["--creer-compte-editeur", "a.b.c", "--nom", "A", "--telephone", "12"]));
        Assert.Throws<ArgumentException>(() => ComptesDistantsAdministration.LireCreation(["--creer-compte-editeur", "a.b.c", "--nom", "A", "--email", "pas une adresse"]));
    }

    [Fact]
    public async Task Creation_liste_desactivation_et_deblocage_sur_une_base_jetable()
    {
        using var base_ = BaseCentraleJetable.Creer();
        if (base_ is null) return;   // pas de serveur, de psql ou de droit CREATEDB : rien à vérifier ici
        await using var db = NpgsqlDataSource.Create(base_.Connexion);
        var sortie = new StringWriter();
        Assert.Equal(0, await ComptesDistantsAdministration.ExecuterAsync(db, ["--creer-compte-editeur", "editeur.console", "--nom", "Éditeur console", "--email", "c@exemple.org"], sortie));
        var provisoire = sortie.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Last();
        Assert.Equal(16, provisoire.Length);
        await using (var cmd = db.CreateCommand("SELECT empreinte_mot_de_passe, mot_de_passe_provisoire, role FROM compte_distant WHERE identifiant = 'editeur.console'"))
        await using (var r = await cmd.ExecuteReaderAsync())
        {
            Assert.True(await r.ReadAsync());
            Assert.StartsWith("$2", r.GetString(0));
            Assert.True(BCrypt.Net.BCrypt.Verify(provisoire, r.GetString(0)));
            Assert.True(r.GetBoolean(1));
            Assert.Equal("editeur", r.GetString(2));
        }
        await Assert.ThrowsAsync<ArgumentException>(() => ComptesDistantsAdministration.ExecuterAsync(db,
            ["--creer-compte-editeur", "editeur.console", "--nom", "Doublon", "--email", "d@exemple.org"], new StringWriter()));

        sortie = new StringWriter();
        await ComptesDistantsAdministration.ExecuterAsync(db, ["--comptes-distants"], sortie);
        Assert.Contains("editeur.console", sortie.ToString());
        Assert.DoesNotContain("$2", sortie.ToString());
        Assert.DoesNotContain("c@exemple.org", sortie.ToString());

        await using (var cmd = db.CreateCommand("UPDATE compte_distant SET verrouille_jusqu_au = now() + interval '1 hour', echecs = 3 WHERE identifiant = 'editeur.console'"))
            await cmd.ExecuteNonQueryAsync();
        Assert.Equal(0, await ComptesDistantsAdministration.ExecuterAsync(db, ["--deverrouiller-compte", "editeur.console"], new StringWriter()));
        Assert.Equal(0, await ComptesDistantsAdministration.ExecuterAsync(db, ["--desactiver-compte", "editeur.console"], new StringWriter()));
        Assert.Equal(1, await ComptesDistantsAdministration.ExecuterAsync(db, ["--desactiver-compte", "editeur.console"], new StringWriter()));
        await using (var cmd = db.CreateCommand("SELECT actif::text || '|' || (verrouille_jusqu_au IS NULL)::text FROM compte_distant WHERE identifiant = 'editeur.console'"))
            Assert.Equal("false|true", await cmd.ExecuteScalarAsync());
        // Journal des accès : création, déblocage, désactivation, sans identifiant
        await using (var cmd = db.CreateCommand("SELECT string_agg(evenement || ':' || coalesce(objet, ''), ',' ORDER BY id) FROM journal_acces_distant"))
            Assert.Equal("compte_cree:console:editeur,compte_deverrouille:console,compte_desactive:console", await cmd.ExecuteScalarAsync());
    }

    [Fact]
    public void Arguments_du_titulaire_et_du_code_de_rattachement_controles()
    {
        var officine = Guid.NewGuid();
        Assert.Equal(("titulaire.dev", officine, "Mme Essai"),
            ComptesDistantsAdministration.LireCodeRattachement(["--code-rattachement", "Titulaire.Dev", officine.ToString(), "--auteur", " Mme Essai "]));
        Assert.Throws<ArgumentException>(() => ComptesDistantsAdministration.LireCodeRattachement(["--code-rattachement", "titulaire.dev", officine.ToString()]));
        Assert.Throws<ArgumentException>(() => ComptesDistantsAdministration.LireCodeRattachement(["--code-rattachement", "titulaire.dev", "pas-un-uuid", "--auteur", "A"]));
        Assert.Throws<ArgumentException>(() => ComptesDistantsAdministration.LireCodeRattachement(["--code-rattachement", "--auteur", "A"]));
        Assert.Throws<ArgumentException>(() => ComptesDistantsAdministration.Auteur(["--creer-compte-titulaire", "t.a", "--auteur", new string('a', 101)]));
        Assert.Equal("Essai", ComptesDistantsAdministration.Auteur(["--creer-compte-titulaire", "t.a", "--auteur", "Essai"]));
    }

    [Fact]
    public async Task Titulaire_et_code_de_rattachement_sur_une_base_jetable_puis_tests_sql_030_a_037()
    {
        using var base_ = BaseCentraleJetable.Creer();
        if (base_ is null) return;   // pas de serveur, de psql ou de droit CREATEDB : rien à vérifier ici
        await using var db = NpgsqlDataSource.Create(base_.Connexion);
        // Sans --auteur : refus, rien n'est créé
        await Assert.ThrowsAsync<ArgumentException>(() => ComptesDistantsAdministration.ExecuterAsync(db,
            ["--creer-compte-titulaire", "titulaire.console", "--nom", "Titulaire console", "--email", "t@exemple.org"], new StringWriter()));
        var sortie = new StringWriter();
        Assert.Equal(0, await ComptesDistantsAdministration.ExecuterAsync(db,
            ["--creer-compte-titulaire", "titulaire.console", "--nom", "Titulaire console", "--email", "t@exemple.org", "--auteur", "Auteur essai"], sortie));
        var provisoire = sortie.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Last();
        Assert.Equal(16, provisoire.Length);
        Assert.Contains("Compte titulaire", sortie.ToString());
        await using (var cmd = db.CreateCommand("SELECT role || '|' || cree_par FROM compte_distant WHERE identifiant = 'titulaire.console'"))
            Assert.Equal("titulaire|Auteur essai", await cmd.ExecuteScalarAsync());

        Guid officine;
        await using (var cmd = db.CreateCommand("INSERT INTO officine (libelle) VALUES ('Officine console') RETURNING id"))
            officine = (Guid)(await cmd.ExecuteScalarAsync())!;
        sortie = new StringWriter();
        Assert.Equal(0, await ComptesDistantsAdministration.ExecuterAsync(db, ["--code-rattachement", "titulaire.console", officine.ToString(), "--auteur", "Auteur essai"], sortie));
        var code = sortie.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Last();
        Assert.Matches("^[A-HJ-NP-Z2-9]{4}-[A-HJ-NP-Z2-9]{4}-[A-HJ-NP-Z2-9]{4}$", code);
        // Empreinte seule en base, jamais le code ; 30 minutes ; journaux
        await using (var cmd = db.CreateCommand("SELECT empreinte_code FROM code_rattachement WHERE officine_id = $1"))
        {
            cmd.Parameters.AddWithValue(officine);
            Assert.Equal(Pharma.Shared.ComptesDistants.EmpreinteCodeRattachement(code), await cmd.ExecuteScalarAsync());
        }
        await using (var cmd = db.CreateCommand("""
            SELECT count(*) FROM journal_securite WHERE detail LIKE '%' || $1 || '%' OR detail LIKE '%' || replace($1, '-', '') || '%'
            """))
        {
            cmd.Parameters.AddWithValue(code);
            Assert.Equal(0L, await cmd.ExecuteScalarAsync());
        }
        await using (var cmd = db.CreateCommand("SELECT string_agg(evenement || ':' || coalesce(objet, ''), ',' ORDER BY id) FROM journal_acces_distant"))
            Assert.Equal("compte_cree:console:titulaire,code_rattachement_cree:console", await cmd.ExecuteScalarAsync());
        // Refus : éditeur, officine inconnue
        Assert.Equal(0, await ComptesDistantsAdministration.ExecuterAsync(db, ["--creer-compte-editeur", "editeur.console2", "--nom", "Éditeur", "--email", "e@exemple.org"], new StringWriter()));
        sortie = new StringWriter();
        Assert.Equal(1, await ComptesDistantsAdministration.ExecuterAsync(db, ["--code-rattachement", "editeur.console2", officine.ToString(), "--auteur", "A"], sortie));
        Assert.Contains("n'est pas un compte titulaire", sortie.ToString());
        Assert.Equal(1, await ComptesDistantsAdministration.ExecuterAsync(db, ["--code-rattachement", "titulaire.console", Guid.NewGuid().ToString(), "--auteur", "A"], new StringWriter()));

        // Tests SQL des écrans (transaction annulée) sur la même base : 030 à 038
        foreach (var t in new[] { "030_comptes_distants_tests.sql", "031_ecrans_editeur_tests.sql", "032_ecrans_titulaire_tests.sql", "033_code_rattachement_poste_tests.sql",
                     "034_titulaire_multi_officines_ordres_tests.sql", "035_reglages_communs_titulaire_tests.sql",
                     "036_lignes_absentes_ecrans_tests.sql", "037_envoi_codes_reglage_tests.sql",
                     "038_remise_mot_de_passe_retrait_message_tests.sql", "039_creation_compte_ecran_tests.sql" })
            base_.Rejouer(Path.Combine(Depot.Racine(), "database", "cloud", "tests", t));
    }

    [Fact]
    public void Contact_compte_arguments_controles()
    {
        Assert.Equal(("editeur.x", "n@exemple.org", (string?)null, "installateur"),
            ComptesDistantsAdministration.LireContact(["--contact-compte", "editeur.x", "--email", "n@exemple.org", "--auteur", "installateur"]));
        Assert.Equal(("editeur.x", "", "0555000099", "a"),
            ComptesDistantsAdministration.LireContact(["--contact-compte", "editeur.x", "--email", "-", "--telephone", "0555 000 099", "--auteur", "a"]));
        Assert.Throws<ArgumentException>(() => ComptesDistantsAdministration.LireContact(["--contact-compte", "editeur.x", "--auteur", "a"]));
        Assert.Throws<ArgumentException>(() => ComptesDistantsAdministration.LireContact(["--contact-compte", "editeur.x", "--email", "pas-une-adresse", "--auteur", "a"]));
        Assert.Throws<ArgumentException>(() => ComptesDistantsAdministration.LireContact(["--contact-compte", "editeur.x", "--email", "n@exemple.org"]));
    }

    [Fact]
    public async Task Contact_compte_change_sur_le_serveur_sans_adresse_au_journal()
    {
        using var base_ = BaseCentraleJetable.Creer();
        if (base_ is null) return;   // pas de serveur, de psql ou de droit CREATEDB : rien à vérifier ici
        await using var db = NpgsqlDataSource.Create(base_.Connexion);
        var sortie = new StringWriter();
        Assert.Equal(0, await ComptesDistantsAdministration.ExecuterAsync(db, ["--creer-compte-editeur", "editeur.contact", "--nom", "Éditeur contact", "--email", "ancien@exemple.org"], sortie));

        Assert.Equal(0, await ComptesDistantsAdministration.ExecuterAsync(db, ["--contact-compte", "editeur.contact", "--email", "nouveau@exemple.org", "--auteur", "installateur"], sortie));
        async Task<string> Contact()
        {
            await using var cmd = db.CreateCommand("SELECT coalesce(email, '-') || '|' || coalesce(telephone, '-') FROM compte_distant WHERE identifiant = 'editeur.contact'");
            return (string)(await cmd.ExecuteScalarAsync())!;
        }
        Assert.Equal("nouveau@exemple.org|-", await Contact());

        // Aucun contact restant : refusé, rien ne change ; avec un téléphone, l'e-mail peut être retiré
        Assert.Equal(1, await ComptesDistantsAdministration.ExecuterAsync(db, ["--contact-compte", "editeur.contact", "--email", "-", "--auteur", "installateur"], sortie));
        Assert.Equal("nouveau@exemple.org|-", await Contact());
        Assert.Equal(0, await ComptesDistantsAdministration.ExecuterAsync(db, ["--contact-compte", "editeur.contact", "--telephone", "0555000099", "--email", "-", "--auteur", "installateur"], sortie));
        Assert.Equal("-|0555000099", await Contact());
        Assert.Equal(1, await ComptesDistantsAdministration.ExecuterAsync(db, ["--contact-compte", "inconnu.x", "--email", "a@exemple.org", "--auteur", "installateur"], sortie));

        // Journaux : jamais l'adresse ni le numéro ; journal des accès toujours chaîné
        await using (var cmd = db.CreateCommand("""
            SELECT (SELECT count(*) FROM journal_securite WHERE evenement = 'compte_contact_modifie' AND (detail LIKE '%@%' OR detail LIKE '%0555000099%'))
                 + (SELECT count(*) FROM journal_acces_distant WHERE evenement = 'compte_contact_modifie' AND (objet LIKE '%@%' OR objet LIKE '%0555000099%'))
            """))
            Assert.Equal(0L, await cmd.ExecuteScalarAsync());
        await using (var cmd = db.CreateCommand("SELECT count(*) FROM journal_securite WHERE evenement = 'compte_contact_modifie' AND detail LIKE 'console (installateur)%'"))
            Assert.Equal(2L, await cmd.ExecuteScalarAsync());
        await using (var cmd = db.CreateCommand("SELECT premiere_rupture FROM cloud_ecran_verifier_journal_acces()"))
            Assert.Equal(DBNull.Value, await cmd.ExecuteScalarAsync());
    }
}