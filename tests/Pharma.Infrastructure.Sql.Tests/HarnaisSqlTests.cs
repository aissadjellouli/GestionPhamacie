using Xunit.Abstractions;

namespace Pharma.Infrastructure.Sql.Tests;

/// <summary>Test de fumée du harnais : copie, isolement entre copies, bloqueur de concurrence, garde-fou de nom.</summary>
[Trait("Categorie", "Sql")]
public sealed class HarnaisSqlTests(CopieSql copie, ITestOutputHelper sortie) : IClassFixture<CopieSql>
{
    [FactSql]
    public async Task Copie_creee_contient_le_schema_et_le_referentiel()
    {
        sortie.WriteLine($"Copie : {copie.Nom} ; base modèle : {copie.DureeModele.TotalSeconds:0.00} s ; copie : {copie.DureeCopie.TotalSeconds:0.00} s");

        Assert.StartsWith(NomsJetables.Prefixe, copie.Nom);
        Assert.True(await copie.Donnees.LireAsync<long>("SELECT count(*) FROM pg_tables WHERE schemaname = 'public'") >= 10);
        Assert.True(await copie.Donnees.LireAsync<long>("SELECT count(*) FROM role") > 0, "le référentiel (seed) est absent de la copie");
        Assert.Equal(copie.Nom, await copie.Donnees.LireAsync<string>("SELECT current_database()"));
    }

    [FactSql]
    public async Task Fabrique_cree_utilisateur_patient_produit_et_lot()
    {
        var patient = await copie.Donnees.PatientAsync(solde: 100m);
        var produit = await copie.Donnees.ProduitAsync();
        var lot = await copie.Donnees.LotAsync(produit, quantite: 5);
        await copie.Donnees.UtilisateurAsync();
        await copie.Donnees.ProduitAsync(psychotrope: true);

        Assert.Equal(100m, await copie.Donnees.LireAsync<decimal>("SELECT solde_credit FROM patient WHERE id = @id", ("id", patient)));
        Assert.Equal(5, await copie.Donnees.LireAsync<int>("SELECT quantite_disponible FROM lot WHERE id = @id", ("id", lot)));
    }

    [FactSql]
    public async Task Copies_isolees_une_ecriture_n_est_pas_vue_par_une_autre_copie()
    {
        var autre = new CopieSql();
        await autre.InitializeAsync();
        try
        {
            sortie.WriteLine($"Seconde copie : {autre.Nom} ({autre.DureeCopie.TotalSeconds:0.00} s)");
            var patient = await copie.Donnees.PatientAsync();

            Assert.Equal(1, await copie.Donnees.LireAsync<long>("SELECT count(*) FROM patient WHERE id = @id", ("id", patient)));
            Assert.Equal(0, await autre.Donnees.LireAsync<long>("SELECT count(*) FROM patient WHERE nom LIKE 'Patient Essai%'"));
        }
        finally { await autre.DisposeAsync(); }
    }

    [FactSql]
    public async Task Bloqueur_constate_l_attente_d_un_FOR_UPDATE_puis_la_libere()
    {
        var patient = await copie.Donnees.PatientAsync(solde: 100m);
        await using var bloqueur = await Bloqueur.DemarrerAsync(copie);
        await bloqueur.ExecuterAsync("SELECT id FROM patient WHERE id = @id FOR UPDATE", ("id", patient));

        var suite = Task.Run(async () =>
        {
            await using var cx = await copie.Source.OpenConnectionAsync();
            await using var tx = await cx.BeginTransactionAsync();
            await using var cmd = new Npgsql.NpgsqlCommand("SELECT solde_credit FROM patient WHERE id = @id FOR UPDATE", cx, tx);
            cmd.Parameters.AddWithValue("id", patient);
            var solde = (decimal)(await cmd.ExecuteScalarAsync())!;
            await tx.CommitAsync();
            return solde;
        });

        await bloqueur.AttendreBloquesAsync(1, TimeSpan.FromSeconds(5));
        Assert.False(suite.IsCompleted, "la lecture FOR UPDATE aurait dû attendre le bloqueur");
        await bloqueur.ExecuterAsync("UPDATE patient SET solde_credit = 120 WHERE id = @id", ("id", patient));
        await bloqueur.ValiderAsync();

        Assert.Equal(120m, await suite.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [FactSql]
    public async Task Bloqueur_echoue_lisiblement_quand_personne_n_attend()
    {
        await using var bloqueur = await Bloqueur.DemarrerAsync(copie);

        var erreur = await Assert.ThrowsAsync<Xunit.Sdk.XunitException>(() => bloqueur.AttendreBloquesAsync(1, TimeSpan.FromMilliseconds(300)));

        Assert.Contains("n'a pas attendu le verrou", erreur.Message);
    }

    [Theory]
    [InlineData("pharmacie_dev")]
    [InlineData("pharmacie_test")]
    [InlineData("pharmacie_cloud_dev")]
    [InlineData("postgres")]
    [InlineData("pharmacie_jetable_sql_20260101000000_copie_abcdef01; DROP DATABASE x")]
    [InlineData("pharmacie_jetable_m4_abcdef01")]
    [InlineData("")]
    public void Garde_fou_refuse_tout_nom_hors_bases_jetables_sql(string nom)
    {
        Assert.Throws<InvalidOperationException>(() => NomsJetables.Verifier(nom));
    }

    [Fact]
    public void Garde_fou_accepte_un_nom_jetable_genere_et_en_lit_l_horodatage()
    {
        var suite = new DateTime(2026, 11, 23, 10, 30, 0, DateTimeKind.Utc);

        var nom = NomsJetables.Nouveau(suite, "copie");

        Assert.Equal(suite, NomsJetables.Horodatage(nom));
        Assert.Null(NomsJetables.Horodatage("pharmacie_dev"));
    }
}
