using Npgsql;
using NpgsqlTypes;
using Pharma.Cloud.Web.Codes;

namespace Pharma.Cloud.Web.Donnees;

/// <summary>Session distante lue en base (jamais le jeton : seulement son empreinte circule vers la base).</summary>
public sealed record SessionEcran(long Id, Guid Compte, string Identifiant, string Nom, string Role, string Etape, DateTime ExpireLe, bool Provisoire)
{
    public bool Ouverte => Etape == "ouverte";
    public bool Editeur => Role == "editeur";
    public bool Titulaire => Role == "titulaire";
}

/// <summary>Résultat tabulaire d'une fonction cloud_ecran_* : noms de colonnes (ceux de la base) et valeurs.</summary>
public sealed record TableEcran(IReadOnlyList<string> Colonnes, IReadOnlyList<object?[]> Lignes, long Total)
{
    public static readonly TableEcran Vide = new([], [], 0);

    public object? Valeur(object?[] ligne, string colonne) => Colonnes.ToList().IndexOf(colonne) is >= 0 and var i ? ligne[i] : null;
}

/// <summary>
/// Accès à la base centrale pour les écrans : connexion rattachée au rôle pharma_cloud_ecrans (script central 030), qui n'a AUCUN
/// droit sur les tables ; seulement des appels paramétrés aux fonctions cloud_ecran_* (noms fixés dans ce fichier, jamais saisis).
/// Pour une officine, cloud_ecran_ouvrir (droit vérifié par la base) puis la lecture, dans la même transaction.
/// </summary>
public sealed class DepotEcrans(NpgsqlDataSource db)
{
    public const string Controle = """
        SELECT r.rolname::text, r.rolsuper, r.rolbypassrls, pg_has_role(current_user, 'pharma_cloud_ecrans', 'MEMBER'),
               EXISTS (SELECT 1 FROM pg_tables t WHERE t.schemaname = 'public' AND t.tableowner = current_user)
        FROM pg_roles r WHERE r.rolname = current_user
        """;

    /// <summary>Refuse une connexion trop puissante (superutilisateur, BYPASSRLS, propriétaire) ou non rattachée au rôle des écrans.</summary>
    public async Task<string> ControlerRoleAsync(CancellationToken ct)
    {
        await using var cmd = db.CreateCommand(Controle);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) throw new InvalidOperationException("Rôle de connexion introuvable.");
        var (role, super, contourne, membre, proprietaire) = (r.GetString(0), r.GetBoolean(1), r.GetBoolean(2), r.GetBoolean(3), r.GetBoolean(4));
        if (super || contourne || proprietaire || !membre)
            throw new InvalidOperationException(
                $"Connexion refusée pour les écrans : le rôle {role} doit être membre de pharma_cloud_ecrans, ni superutilisateur, ni BYPASSRLS, ni propriétaire d'une table.");
        return role;
    }

    // ------------------------------------------------------------------ connexion et session
    public async Task<(Guid? Compte, string? Empreinte)> DebutConnexionAsync(string identifiant, CancellationToken ct)
    {
        await using var cmd = Commande("SELECT compte_id, empreinte_mot_de_passe FROM cloud_ecran_debut_connexion($1)", identifiant);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? (r.GetGuid(0), r.IsDBNull(1) ? null : r.GetString(1)) : (null, null);
    }

    public async Task<string> FinConnexionAsync(Guid? compte, bool reussi, string? empreinteJeton, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand("SELECT statut FROM cloud_ecran_fin_connexion($1, $2, $3)");
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)compte ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Uuid });
        cmd.Parameters.AddWithValue(reussi);
        cmd.Parameters.Add(Texte(empreinteJeton));
        return (string)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public async Task<SessionEcran?> SessionAsync(string empreinteJeton, CancellationToken ct)
    {
        await using var cmd = Commande("SELECT session_id, compte_id, identifiant, nom, role, etape, expire_le, mot_de_passe_provisoire FROM cloud_ecran_session($1)", empreinteJeton);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct)
            ? new SessionEcran(r.GetInt64(0), r.GetGuid(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5), r.GetDateTime(6), r.GetBoolean(7))
            : null;
    }

    public async Task<(string Statut, DestinataireCode? Destinataire)> CreerCodeAsync(string empreinteJeton, string objet, string empreinteCode, string canal, CancellationToken ct)
    {
        await using var cmd = Commande("SELECT statut, identifiant, email, telephone FROM cloud_ecran_creer_code($1, $2, $3, $4)", empreinteJeton, objet, empreinteCode, canal);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        await r.ReadAsync(ct);
        var statut = r.GetString(0);
        return statut == "envoye"
            ? (statut, new DestinataireCode(r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3)))
            : (statut, null);
    }

    public async Task<(string Statut, string? Etape)> ValiderCodeConnexionAsync(string empreinteJeton, string? empreinteCode, string nouvelleEmpreinte, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand("SELECT statut, etape FROM cloud_ecran_valider_code_connexion($1, $2, $3)");
        cmd.Parameters.Add(Texte(empreinteJeton));
        cmd.Parameters.Add(Texte(empreinteCode));
        cmd.Parameters.Add(Texte(nouvelleEmpreinte));
        await using var r = await cmd.ExecuteReaderAsync(ct);
        await r.ReadAsync(ct);
        return (r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1));
    }

    public async Task<string> ChangerMotDePasseAsync(string empreinteJeton, string empreinteBcrypt, CancellationToken ct)
    {
        await using var cmd = Commande("SELECT statut FROM cloud_ecran_changer_mot_de_passe($1, $2)", empreinteJeton, empreinteBcrypt);
        return (string)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public async Task DeconnexionAsync(string empreinteJeton, CancellationToken ct)
    {
        await using var cmd = Commande("SELECT cloud_ecran_deconnexion($1)", empreinteJeton);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // ------------------------------------------------------------------ lecture
    public Task<TableEcran> OfficinesAsync(string empreinteJeton, string? texte, string etat, int page, int taille, CancellationToken ct) =>
        LireAsync(null, null, "SELECT * FROM cloud_ecran_officines($1, $2, $3, $4, $5)", [Texte(empreinteJeton), Texte(texte), Texte(etat), Entier(page), Entier(taille)], ct);

    public Task<TableEcran> MonJournalAsync(string empreinteJeton, int page, int taille, CancellationToken ct) =>
        LireAsync(null, null, "SELECT * FROM cloud_ecran_mon_journal($1, $2, $3)", [Texte(empreinteJeton), Entier(page), Entier(taille)], ct);

    /// <summary>Lecture d'une rubrique de la fiche (fonction de la liste fixe Rubriques) ; null si l'ouverture est refusée par la base.</summary>
    public Task<TableEcran?> RubriqueAsync(string empreinteJeton, Guid officine, Rubrique rubrique, int page, int taille, CancellationToken ct) =>
        LireOuvertAsync(empreinteJeton, officine, rubrique.Pagine
            ? ($"SELECT * FROM {rubrique.Fonction}($1, $2, $3)", [Texte(empreinteJeton), Entier(page), Entier(taille)])
            : ($"SELECT * FROM {rubrique.Fonction}($1)", [Texte(empreinteJeton)]), ct);

    // ------------------------------------------------------------------ titulaire (script central 032)
    // L'officine est ouverte PAR LA BASE dans chaque fonction (cloud_ecran_ouvrir, rattachement et droit vérifiés) : aucune ligne si refusé.
    public Task<TableEcran> TitulaireOfficinesAsync(string empreinteJeton, CancellationToken ct) =>
        LireAsync(null, null, "SELECT * FROM cloud_ecran_titulaire_officines($1)", [Texte(empreinteJeton)], ct);

    public Task<TableEcran> TitulaireTableauAsync(string empreinteJeton, Guid officine, CancellationToken ct) =>
        LireAsync(null, null, "SELECT * FROM cloud_ecran_titulaire_tableau($1, $2)", [Texte(empreinteJeton), Uuid(officine)], ct);

    public Task<TableEcran> TitulaireJournalAsync(string empreinteJeton, Guid officine, int page, int taille, CancellationToken ct) =>
        LireAsync(null, null, "SELECT * FROM cloud_ecran_titulaire_journal($1, $2, $3, $4)", [Texte(empreinteJeton), Uuid(officine), Entier(page), Entier(taille)], ct);

    /// <summary>Rattachement par code : seule l'empreinte du code saisi part vers la base (null si la saisie n'est pas un code : refus compté).</summary>
    public async Task<(string Statut, Guid? Officine)> RattacherAsync(string empreinteJeton, string? empreinteCode, CancellationToken ct)
    {
        await using var cmd = Commande("SELECT statut, officine_id FROM cloud_ecran_rattacher($1, $2)", empreinteJeton, empreinteCode);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        await r.ReadAsync(ct);
        return (r.GetString(0), r.IsDBNull(1) ? null : r.GetGuid(1));
    }

    // ------------------------------------------------------------------ actions
    public async Task<(string Statut, Guid? Officine)> CreerOfficineAsync(string jeton, string? code, string? motif, string? libelle, string? confirmation,
        string serveur, string empreinteActivation, CancellationToken ct)
    {
        await using var cmd = Commande("SELECT statut, officine_id FROM cloud_ecran_creer_officine($1, $2, $3, $4, $5, $6, $7)",
            jeton, code, motif, libelle, confirmation, serveur, empreinteActivation);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        await r.ReadAsync(ct);
        return (r.GetString(0), r.IsDBNull(1) ? null : r.GetGuid(1));
    }

    public Task<string?> NouveauCodeActivationAsync(string jeton, Guid officine, string? code, string? motif, string? confirmation, string serveur,
        string empreinteActivation, CancellationToken ct) =>
        ActionOuverteAsync(jeton, officine, "SELECT statut FROM cloud_ecran_nouveau_code_activation($1, $2, $3, $4, $5, $6)",
            [jeton, code, motif, confirmation, serveur, empreinteActivation], ct);

    public Task<string?> RevoquerCertificatAsync(string jeton, Guid officine, string? code, string? motif, string? empreinteCourte, string? confirmation,
        CancellationToken ct) =>
        ActionOuverteAsync(jeton, officine, "SELECT statut FROM cloud_ecran_revoquer_certificat($1, $2, $3, $4, $5)",
            [jeton, code, motif, empreinteCourte, confirmation], ct);

    public Task<string?> RevoquerServeurAsync(string jeton, Guid officine, string? code, string? motif, string serveur, string? confirmation, CancellationToken ct) =>
        ActionOuverteAsync(jeton, officine, "SELECT statut FROM cloud_ecran_revoquer_serveur($1, $2, $3, $4, $5)",
            [jeton, code, motif, serveur, confirmation], ct);

    // ------------------------------------------------------------------ outils
    private async Task<string?> ActionOuverteAsync(string jeton, Guid officine, string sql, string?[] valeurs, CancellationToken ct)
    {
        var table = await LireOuvertAsync(jeton, officine, (sql, valeurs.Select(Texte).ToArray()), ct);
        return table?.Lignes.FirstOrDefault()?[0] as string;
    }

    private async Task<TableEcran?> LireOuvertAsync(string jeton, Guid officine, (string Sql, NpgsqlParameter[] Parametres) requete, CancellationToken ct)
    {
        await using var cnx = await db.OpenConnectionAsync(ct);
        await using var tx = await cnx.BeginTransactionAsync(ct);
        await using (var ouvrir = new NpgsqlCommand("SELECT cloud_ecran_ouvrir($1, $2)", cnx, tx))
        {
            ouvrir.Parameters.Add(Texte(jeton));
            ouvrir.Parameters.AddWithValue(officine);
            if (await ouvrir.ExecuteScalarAsync(ct) is not true)
            {
                await tx.CommitAsync(ct);   // le refus est journalisé par la base
                return null;
            }
        }
        var table = await LireAsync(cnx, tx, requete.Sql, requete.Parametres, ct);
        await tx.CommitAsync(ct);
        return table;
    }

    private async Task<TableEcran> LireAsync(NpgsqlConnection? cnx, NpgsqlTransaction? tx, string sql, NpgsqlParameter[] parametres, CancellationToken ct)
    {
        await using var propre = cnx is null ? await db.OpenConnectionAsync(ct) : null;
        await using var cmd = new NpgsqlCommand(sql, cnx ?? propre, tx);
        cmd.Parameters.AddRange(parametres);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var colonnes = Enumerable.Range(0, r.FieldCount).Select(r.GetName).ToList();
        var lignes = new List<object?[]>();
        long total = -1;
        var iTotal = colonnes.IndexOf("total");
        var iId = colonnes.IndexOf("id");
        while (await r.ReadAsync(ct))
        {
            var valeurs = new object?[r.FieldCount];
            for (var i = 0; i < r.FieldCount; i++) valeurs[i] = r.IsDBNull(i) ? null : r.GetValue(i);
            if (iTotal >= 0 && valeurs[iTotal] is long t) total = t;
            // Liste des officines : une page vide rend une ligne sans officine qui porte le total
            if (iTotal >= 0 && iId >= 0 && valeurs[iId] is null) continue;
            lignes.Add(valeurs);
        }
        return new TableEcran(colonnes, lignes, total >= 0 ? total : lignes.Count);
    }

    private NpgsqlCommand Commande(string sql, params string?[] valeurs)
    {
        var cmd = db.CreateCommand(sql);
        foreach (var v in valeurs) cmd.Parameters.Add(Texte(v));
        return cmd;
    }

    private static NpgsqlParameter Texte(string? v) => new() { Value = (object?)v ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text };

    private static NpgsqlParameter Entier(int v) => new() { Value = v, NpgsqlDbType = NpgsqlDbType.Integer };

    private static NpgsqlParameter Uuid(Guid v) => new() { Value = v, NpgsqlDbType = NpgsqlDbType.Uuid };
}
