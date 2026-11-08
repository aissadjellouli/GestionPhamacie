using Npgsql;
using NpgsqlTypes;
using Pharma.Application.Administration;
using Pharma.Domain.Commun;

namespace Pharma.Infrastructure.Administration;

public sealed class AdministrationSql(NpgsqlDataSource db) : ISeuilsAdmin, IUtilisateursAdmin, IPharmacie
{
    // ---- Seuils ------------------------------------------------------------

    public async Task<ReglagesSeuils> LireAsync(CancellationToken ct = default)
    {
        ParametresAlerte general;
        await using (var cmd = db.CreateCommand("""
            SELECT jours_orange, jours_rouge, horizon_risque_jours, delai_fournisseur_jours, jours_securite,
                   jours_cible_commande, jours_dormant
            FROM parametre_alerte WHERE id = 1
            """))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            await r.ReadAsync(ct);
            general = new ParametresAlerte(r.GetInt32(0), r.GetInt32(1), r.GetInt32(2), r.GetInt32(3), r.GetInt32(4), r.GetInt32(5), r.GetInt32(6));
        }

        var grossistes = new List<SeuilGrossiste>();
        await using (var cmd = db.CreateCommand(
            "SELECT id, raison_sociale, delai_reprise_jours, jours_orange, jours_rouge FROM fournisseur ORDER BY raison_sociale"))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
                grossistes.Add(new SeuilGrossiste(r.GetInt64(0), r.GetString(1), Entier(r, 2), Entier(r, 3), Entier(r, 4)));

        var exceptions = new List<ExceptionProduit>();
        await using (var cmd = db.CreateCommand("""
            SELECT id, nom_commercial, jours_orange, jours_rouge FROM produit
            WHERE jours_orange IS NOT NULL OR jours_rouge IS NOT NULL
            ORDER BY nom_commercial
            """))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
                exceptions.Add(new ExceptionProduit(r.GetInt64(0), r.GetString(1), Entier(r, 2), Entier(r, 3)));

        return new ReglagesSeuils(general, grossistes, exceptions);
    }

    public async Task EnregistrerAsync(ReglagesSeuils s, CancellationToken ct = default)
    {
        await using var cx = await db.OpenConnectionAsync(ct);
        await using var tx = await cx.BeginTransactionAsync(ct);

        await using (var cmd = new NpgsqlCommand("""
            UPDATE parametre_alerte
            SET jours_orange = $1, jours_rouge = $2, horizon_risque_jours = $3, delai_fournisseur_jours = $4,
                jours_securite = $5, jours_cible_commande = $6, jours_dormant = $7
            WHERE id = 1
            """, cx, tx))
        {
            var g = s.General;
            foreach (var v in new[] { g.JoursOrange, g.JoursRouge, g.HorizonRisqueJours, g.DelaiFournisseurJours, g.JoursSecurite, g.JoursCibleCommande, g.JoursDormant })
                cmd.Parameters.AddWithValue(v);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        foreach (var f in s.Grossistes)
        {
            await using var cmd = new NpgsqlCommand(
                "UPDATE fournisseur SET delai_reprise_jours = $2, jours_orange = $3, jours_rouge = $4 WHERE id = $1", cx, tx);
            cmd.Parameters.AddWithValue(f.FournisseurId);
            cmd.Parameters.Add(Nul(f.DelaiRepriseJours));
            cmd.Parameters.Add(Nul(f.JoursOrange));
            cmd.Parameters.Add(Nul(f.JoursRouge));
            await cmd.ExecuteNonQueryAsync(ct);
        }

        // Produits retirés des exceptions : retour au réglage du grossiste ou général
        await using (var cmd = new NpgsqlCommand("""
            UPDATE produit SET jours_orange = NULL, jours_rouge = NULL
            WHERE (jours_orange IS NOT NULL OR jours_rouge IS NOT NULL) AND NOT (id = ANY($1))
            """, cx, tx))
        {
            cmd.Parameters.AddWithValue(s.Exceptions.Select(e => e.ProduitId).ToArray());
            await cmd.ExecuteNonQueryAsync(ct);
        }
        foreach (var e in s.Exceptions)
        {
            await using var cmd = new NpgsqlCommand("UPDATE produit SET jours_orange = $2, jours_rouge = $3 WHERE id = $1", cx, tx);
            cmd.Parameters.AddWithValue(e.ProduitId);
            cmd.Parameters.Add(Nul(e.JoursOrange));
            cmd.Parameters.Add(Nul(e.JoursRouge));
            await cmd.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
    }

    // ---- Utilisateurs et droits ---------------------------------------------

    async Task<EtatDroits> IUtilisateursAdmin.LireAsync(CancellationToken ct)
    {
        var utilisateurs = new List<UtilisateurAdmin>();
        await using (var cmd = db.CreateCommand("""
            SELECT u.id, u.nom_complet, u.identifiant, r.id, r.nom, r.systeme, u.actif, u.derniere_connexion
            FROM utilisateur u JOIN role r ON r.id = u.role_id
            ORDER BY r.systeme DESC, u.actif DESC, u.nom_complet
            """))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
                utilisateurs.Add(new UtilisateurAdmin(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetInt64(3), r.GetString(4),
                    r.GetBoolean(5), r.GetBoolean(6), r.IsDBNull(7) ? null : r.GetDateTime(7)));

        var roles = new List<RoleAdmin>();
        await using (var cmd = db.CreateCommand("""
            SELECT r.id, r.code, r.nom, r.systeme, r.remise_max_pct,
                   COALESCE(array_agg(rp.permission_code ORDER BY rp.permission_code) FILTER (WHERE rp.permission_code IS NOT NULL), '{}')
            FROM role r LEFT JOIN role_permission rp ON rp.role_id = r.id
            GROUP BY r.id ORDER BY r.systeme DESC, r.id
            """))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
                roles.Add(new RoleAdmin(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetBoolean(3), r.GetDecimal(4), r.GetFieldValue<string[]>(5)));

        var permissions = new List<PermissionAdmin>();
        await using (var cmd = db.CreateCommand("SELECT code, module, libelle, sensible FROM permission ORDER BY module, code"))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
                permissions.Add(new PermissionAdmin(r.GetString(0), r.GetString(1), r.GetString(2), r.GetBoolean(3)));

        return new EtatDroits(utilisateurs, roles, permissions);
    }

    public async Task<long> CreerAsync(string nomComplet, string identifiant, string hash, long roleId, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("""
            INSERT INTO utilisateur (nom_complet, identifiant, mot_de_passe_hash, role_id) VALUES ($1, $2, $3, $4) RETURNING id
            """);
        cmd.Parameters.AddWithValue(nomComplet);
        cmd.Parameters.AddWithValue(identifiant);
        cmd.Parameters.AddWithValue(hash);
        cmd.Parameters.AddWithValue(roleId);
        try
        {
            return (long)(await cmd.ExecuteScalarAsync(ct))!;
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            throw new RegleMetierException($"L’identifiant « {identifiant} » est déjà utilisé.");
        }
    }

    public async Task ModifierAsync(long id, string nomComplet, long roleId, bool actif, string? nouveauHash, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("""
            UPDATE utilisateur
            SET nom_complet = $2, role_id = $3, actif = $4, mot_de_passe_hash = COALESCE($5, mot_de_passe_hash)
            WHERE id = $1
            """);
        cmd.Parameters.AddWithValue(id);
        cmd.Parameters.AddWithValue(nomComplet);
        cmd.Parameters.AddWithValue(roleId);
        cmd.Parameters.AddWithValue(actif);
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)nouveauHash ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task EnregistrerDroitsAsync(IReadOnlyList<DroitsRole> droits, CancellationToken ct = default)
    {
        await using var cx = await db.OpenConnectionAsync(ct);
        await using var tx = await cx.BeginTransactionAsync(ct);
        foreach (var d in droits)
        {
            var codes = d.Permissions.ToArray();
            await using (var cmd = new NpgsqlCommand(
                "DELETE FROM role_permission WHERE role_id = $1 AND NOT (permission_code = ANY($2))", cx, tx))
            {
                cmd.Parameters.AddWithValue(d.RoleId);
                cmd.Parameters.AddWithValue(codes);
                await cmd.ExecuteNonQueryAsync(ct);
            }
            await using (var cmd = new NpgsqlCommand("""
                INSERT INTO role_permission (role_id, permission_code)
                SELECT $1, c FROM unnest($2::text[]) AS c
                WHERE NOT EXISTS (SELECT 1 FROM role_permission x WHERE x.role_id = $1 AND x.permission_code = c)
                """, cx, tx))
            {
                cmd.Parameters.AddWithValue(d.RoleId);
                cmd.Parameters.AddWithValue(codes);
                await cmd.ExecuteNonQueryAsync(ct);
            }
            await using (var cmd = new NpgsqlCommand("UPDATE role SET remise_max_pct = $2 WHERE id = $1 AND NOT systeme", cx, tx))
            {
                cmd.Parameters.AddWithValue(d.RoleId);
                cmd.Parameters.AddWithValue(d.RemiseMaxPct);
                await cmd.ExecuteNonQueryAsync(ct);
            }
        }
        await tx.CommitAsync(ct);
    }

    // ---- Pharmacie -----------------------------------------------------------

    async Task<IdentitePharmacie> IPharmacie.LireAsync(CancellationToken ct)
    {
        await using var cmd = db.CreateCommand(
            "SELECT nom, titulaire, adresse, telephone, nif, chifa_officine, langue_defaut FROM pharmacie WHERE id = 1");
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return new IdentitePharmacie(null, null, null, null, null, null, "fr");
        string? T(int i) => r.IsDBNull(i) ? null : r.GetString(i);
        return new IdentitePharmacie(T(0), T(1), T(2), T(3), T(4), T(5), r.GetString(6));
    }

    public async Task EnregistrerAsync(IdentitePharmacie i, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("""
            UPDATE pharmacie SET nom = $1, titulaire = $2, adresse = $3, telephone = $4, nif = $5, chifa_officine = $6, langue_defaut = $7
            WHERE id = 1
            """);
        foreach (var v in new[] { i.Nom, i.Titulaire, i.Adresse, i.Telephone, i.Nif, i.ChifaOfficine })
            cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)v ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
        cmd.Parameters.AddWithValue(i.LangueDefaut);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static int? Entier(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetInt32(i);
    private static NpgsqlParameter Nul(int? v) => new() { Value = (object?)v ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Integer };
}
