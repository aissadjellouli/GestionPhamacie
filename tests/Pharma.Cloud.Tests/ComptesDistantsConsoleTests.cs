using Npgsql;
using Pharma.Cloud.Api;
using Pharma.Cloud.Api.Comptes;
using BaseCentraleJetable = Pharma.Cloud.Tests.DescendantsMutuV1Tests.BaseCentraleJetable;

namespace Pharma.Cloud.Tests;

/// <summary>Lot S7.1 : commandes console des comptes distants (script central 030).</summary>
public class ComptesDistantsConsoleTests
{
    [Fact]
    public void Commandes_reconnues_et_arguments_controles()
    {
        foreach (var c in new[] { "--creer-compte-editeur", "--comptes-distants", "--desactiver-compte", "--deverrouiller-compte" })
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
}
