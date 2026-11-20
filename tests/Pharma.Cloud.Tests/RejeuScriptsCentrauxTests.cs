using Npgsql;
using BaseCentraleJetable = Pharma.Cloud.Tests.DescendantsMutuV1Tests.BaseCentraleJetable;

namespace Pharma.Cloud.Tests;

/// <summary>
/// Rejeu des scripts centraux (database/cloud/NNN_*.sql) : après une installation complète, repasser tous les scripts un par un, dans l'ordre,
/// ne doit rien changer (définitions des fonctions du schéma public, déclencheurs et leurs fonctions, contraintes, droits d'exécution).
/// Garde-fou contre le retour d'une ancienne définition (gardes « DO $rejeu$ » des scripts anciens). Base centrale JETABLE
/// « pharmacie_cloud_jetable_m11_… », supprimée à la fin ; sans serveur PostgreSQL joignable, sans psql ou sans droit CREATEDB, le test ne fait rien.
/// </summary>
public class RejeuScriptsCentrauxTests
{
    /// <summary>
    /// Empreinte de l'état : fonctions (md5 de la définition), déclencheurs (table, nom, fonction appelée ; partitions de lot_recu exclues),
    /// contraintes (md5 de la définition) et droits d'exécution des fonctions.
    /// </summary>
    private const string Empreinte = """
        SELECT 'f', p.oid::regprocedure::text, md5(pg_get_functiondef(p.oid)) FROM pg_proc p
          WHERE p.pronamespace = 'public'::regnamespace AND p.prokind IN ('f', 'p')
        UNION ALL SELECT 't', t.tgrelid::regclass::text || '.' || t.tgname, t.tgfoid::regproc::text FROM pg_trigger t
          WHERE NOT t.tgisinternal AND t.tgrelid::regclass::text !~ '^lot_recu_'
        UNION ALL SELECT 'c', conrelid::regclass::text || '.' || conname, md5(pg_get_constraintdef(oid)) FROM pg_constraint
          WHERE connamespace = 'public'::regnamespace AND conrelid::regclass::text !~ '^lot_recu_'
        UNION ALL SELECT 'a', p.oid::regprocedure::text, coalesce(p.proacl::text, '') FROM pg_proc p WHERE p.pronamespace = 'public'::regnamespace
        ORDER BY 1, 2, 3
        """;

    [Fact]
    public async Task Rejouer_tous_les_scripts_un_par_un_ne_change_aucune_definition()
    {
        using var base_ = BaseCentraleJetable.Creer();
        if (base_ is null) return;   // pas de serveur, de psql ou de droit CREATEDB : rien à vérifier ici
        await using var db = NpgsqlDataSource.Create(base_.Connexion);
        var ct = CancellationToken.None;
        var avant = await Lire(db, ct);
        Assert.True(avant.Count > 200, $"empreinte trop courte ({avant.Count} lignes) : la requête ne lit pas la base");

        var scripts = Directory.GetFiles(Path.Combine(Depot.Racine(), "database", "cloud"), "0*.sql").Order(StringComparer.Ordinal).ToList();
        Assert.True(scripts.Count >= 28, $"scripts attendus : au moins 28, trouvés {scripts.Count}");
        foreach (var script in scripts) base_.Rejouer(script);

        var apres = await Lire(db, ct);
        var ecarts = avant.Except(apres).Concat(apres.Except(avant)).Distinct().Order(StringComparer.Ordinal).Take(10).ToList();
        Assert.True(ecarts.Count == 0, "Écarts après rejeu complet : " + string.Join(" | ", ecarts));
    }

    private static async Task<List<string>> Lire(NpgsqlDataSource db, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand(Empreinte);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var liste = new List<string>();
        while (await r.ReadAsync(ct)) liste.Add($"{r.GetString(0)}|{r.GetString(1)}|{r.GetString(2)}");
        return liste;
    }
}
