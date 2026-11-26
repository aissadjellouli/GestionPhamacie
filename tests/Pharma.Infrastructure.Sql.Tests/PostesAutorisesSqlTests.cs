using Pharma.Application.Commun;
using Pharma.Application.Postes;
using Pharma.Infrastructure.Postes;

namespace Pharma.Infrastructure.Sql.Tests;

/// <summary>Liste des postes autorisés (script 167) sur une copie jetable : création, unicité du nom, enrôlement à usage unique, échecs de code, renommage, révocation.</summary>
[Trait("Categorie", "Sql")]
public sealed class PostesAutorisesSqlTests(CopieSql copie) : IClassFixture<CopieSql>
{
    private async Task<(PostesAutorisesSql Sql, long Titulaire)> PreparerAsync()
    {
        await using (var cmd = copie.Verif.CreateCommand("DELETE FROM presence_poste; DELETE FROM poste_autorise; UPDATE parametre_postes SET mode = 'transition', maximum_postes = 20, duree_code_minutes = 15"))
            await cmd.ExecuteNonQueryAsync();
        return (new PostesAutorisesSql(copie.Source), await copie.Donnees.UtilisateurAsync());
    }

    private static readonly byte[] Cle = [0x30, 0x59, 1, 2, 3];
    private const int Maximum = 20;

    [FactSql]
    public async Task Maximum_tenu_meme_avec_des_autorisations_simultanees_et_libere_par_une_revocation()
    {
        var (sql, titulaire) = await PreparerAsync();
        // Dix autorisations lancées ensemble pour un maximum de 3 : le comptage et la création sont faits sous verrou
        var essais = await Task.WhenAll(Enumerable.Range(1, 10).Select(i =>
            Task.Run(() => sql.CreerAsync($"CAISSE-{i}", CodeEnrolement.Hacher($"AAAA-AAA{i % 10}"), DateTime.UtcNow.AddMinutes(15), titulaire, 3))));
        Assert.Equal(3, essais.Count(e => e.Id is not null));
        Assert.Equal(7, essais.Count(e => e.MaximumAtteint));
        Assert.Equal(3, await sql.NombreNonRevoquesAsync());

        // Une révocation libère une place
        Assert.True(await sql.RevoquerAsync(essais.First(e => e.Id is not null).Id!.Value, titulaire, "essai de révocation"));
        Assert.NotNull((await sql.CreerAsync("CAISSE-11", CodeEnrolement.Hacher("BBBB-BBBB"), DateTime.UtcNow.AddMinutes(15), titulaire, 3)).Id);
        Assert.True((await sql.CreerAsync("CAISSE-12", CodeEnrolement.Hacher("CCCC-CCCC"), DateTime.UtcNow.AddMinutes(15), titulaire, 3)).MaximumAtteint);
    }

    [FactSql]
    public async Task Reglages_par_defaut_transition_20_postes_15_minutes_et_enregistrement()
    {
        var (sql, _) = await PreparerAsync();
        Assert.Equal(ReglagesPostes.ParDefaut, await sql.ReglagesAsync());
        await sql.EnregistrerReglagesAsync(new ReglagesPostes("exige", 5, 30));
        Assert.Equal(new ReglagesPostes("exige", 5, 30), await sql.ReglagesAsync());
    }

    [FactSql]
    public async Task Nom_unique_sans_la_casse_parmi_les_postes_non_revoques()
    {
        var (sql, titulaire) = await PreparerAsync();
        var id = (await sql.CreerAsync("CAISSE-1", CodeEnrolement.Hacher("AAAA-AAAA"), DateTime.UtcNow.AddMinutes(15), titulaire, Maximum)).Id;
        Assert.NotNull(id);
        Assert.Null((await sql.CreerAsync("caisse-1", CodeEnrolement.Hacher("BBBB-BBBB"), DateTime.UtcNow.AddMinutes(15), titulaire, Maximum)).Id);
        Assert.True(await sql.RevoquerAsync(id!.Value, titulaire, "essai de révocation"));
        Assert.NotNull((await sql.CreerAsync("caisse-1", CodeEnrolement.Hacher("CCCC-CCCC"), DateTime.UtcNow.AddMinutes(15), titulaire, Maximum)).Id);
        Assert.Equal(["caisse-1"], (await sql.NomsReservesAsync()).ToArray());
    }

    [FactSql]
    public async Task Le_code_n_enrole_qu_une_fois_et_jamais_apres_expiration_ou_cinq_echecs()
    {
        var (sql, titulaire) = await PreparerAsync();
        var code = CodeEnrolement.Hacher("AAAA-AAAA");
        var id = (await sql.CreerAsync("CAISSE-1", code, DateTime.UtcNow.AddMinutes(15), titulaire, Maximum)).Id!.Value;
        var lu = await sql.EnrolerAsync(code, Cle, "0123456789ABCDEF", "PC-1", CodeEnrolement.EssaisMax);
        Assert.Equal(new EnrolementLu(id, "CAISSE-1", titulaire), lu);
        Assert.Null(await sql.EnrolerAsync(code, Cle, "0123456789ABCDEF", "PC-1", CodeEnrolement.EssaisMax));
        var cle = await sql.LireCleAsync(id);
        Assert.Equal(("actif", 1), (cle!.Statut, cle.Generation));
        Assert.Equal(Cle, cle.ClePublique);

        // Expiré
        var codeExpire = CodeEnrolement.Hacher("BBBB-BBBB");
        await sql.CreerAsync("CAISSE-2", codeExpire, DateTime.UtcNow.AddMinutes(-1), titulaire, Maximum);
        Assert.Null(await sql.EnrolerAsync(codeExpire, Cle, "0123456789ABCDEF", null, CodeEnrolement.EssaisMax));

        // Cinq échecs : invalidé ; un nouveau code remet le compteur à zéro
        var code3 = CodeEnrolement.Hacher("CCCC-CCCC");
        var id3 = (await sql.CreerAsync("CAISSE-3", code3, DateTime.UtcNow.AddMinutes(15), titulaire, Maximum)).Id!.Value;
        for (var i = 0; i < 5; i++) await sql.NoterEchecCodeAsync();
        Assert.Null(await sql.EnrolerAsync(code3, Cle, "0123456789ABCDEF", null, CodeEnrolement.EssaisMax));
        var code4 = CodeEnrolement.Hacher("DDDD-DDDD");
        Assert.True(await sql.NouveauCodeAsync(id3, code4, DateTime.UtcNow.AddMinutes(15)));
        Assert.NotNull(await sql.EnrolerAsync(code4, Cle, "0123456789ABCDEF", null, CodeEnrolement.EssaisMax));
    }

    [FactSql]
    public async Task Remplacer_le_PC_retire_la_cle_et_renommer_refuse_un_nom_pris()
    {
        var (sql, titulaire) = await PreparerAsync();
        var code = CodeEnrolement.Hacher("AAAA-AAAA");
        var id = (await sql.CreerAsync("CAISSE-1", code, DateTime.UtcNow.AddMinutes(15), titulaire, Maximum)).Id!.Value;
        await sql.EnrolerAsync(code, Cle, "0123456789ABCDEF", "PC-1", CodeEnrolement.EssaisMax);
        Assert.True(await sql.RemplacerAsync(id, CodeEnrolement.Hacher("BBBB-BBBB"), DateTime.UtcNow.AddMinutes(15)));
        var cle = await sql.LireCleAsync(id);
        Assert.Equal(("en_attente", 2), (cle!.Statut, cle.Generation));
        Assert.Null(cle.ClePublique);

        await sql.CreerAsync("CAISSE-2", CodeEnrolement.Hacher("CCCC-CCCC"), DateTime.UtcNow.AddMinutes(15), titulaire, Maximum);
        Assert.False(await sql.RenommerAsync(id, "caisse-2"));
        Assert.True(await sql.RenommerAsync(id, "CAISSE-9"));
        Assert.Null(await sql.RenommerAsync(999_999, "X"));
        var page = await sql.ListerAsync(null, "caisse-9", new DemandePage(1, 10));
        Assert.Equal(1, page.Total);
        Assert.Equal("CAISSE-9", page.Lignes.Single().Nom);
    }

    [FactSql]
    public async Task Poste_occupe_par_une_vente_en_attente()
    {
        var (sql, titulaire) = await PreparerAsync();
        Assert.False(await sql.PosteOccupeAsync("CAISSE-OCC"));
        await using (var cmd = copie.Verif.CreateCommand(
            "INSERT INTO panier_attente (poste, utilisateur_id, libelle, lignes, contenu) VALUES ('Caisse-Occ', @u, 'essai', 1, '[]')"))
        {
            cmd.Parameters.AddWithValue("u", titulaire);
            await cmd.ExecuteNonQueryAsync();
        }
        Assert.True(await sql.PosteOccupeAsync("CAISSE-OCC"));
        await using (var nettoyage = copie.Verif.CreateCommand("DELETE FROM panier_attente WHERE poste = 'Caisse-Occ'"))
            await nettoyage.ExecuteNonQueryAsync();
    }
}
