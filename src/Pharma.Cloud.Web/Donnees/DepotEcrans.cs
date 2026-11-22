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

    /// <summary>Nouveau code (script central 037 : canal fichier, email, sms ou les_deux) ; contact rendu pour l'envoi seulement.</summary>
    public async Task<(string Statut, DestinataireCode? Destinataire)> CreerCodeAsync(string empreinteJeton, string objet, string empreinteCode, string canal, CancellationToken ct)
    {
        await using var cmd = Commande("SELECT statut, identifiant, email, telephone FROM cloud_ecran_facteur_demander_code($1, $2, $3, $4)", empreinteJeton, objet, empreinteCode, canal);
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

    /// <summary>Lot S7.5 (script central 036) : résumé des lignes absentes de l'officine (une ligne : nombres, dernier chargement, cause
    /// la plus fréquente) ; null si l'ouverture est refusée par la base.</summary>
    public Task<TableEcran?> LignesAbsentesResumeAsync(string empreinteJeton, Guid officine, CancellationToken ct) =>
        LireOuvertAsync(empreinteJeton, officine, ("SELECT * FROM cloud_ecran_lignes_absentes_resume($1)", [Texte(empreinteJeton)]), ct);

    // ------------------------------------------------------------------ titulaire (script central 032)
    // L'officine est ouverte PAR LA BASE dans chaque fonction (cloud_ecran_ouvrir, rattachement et droit vérifiés) : aucune ligne si refusé.
    public Task<TableEcran> TitulaireOfficinesAsync(string empreinteJeton, CancellationToken ct) =>
        LireAsync(null, null, "SELECT * FROM cloud_ecran_titulaire_officines($1)", [Texte(empreinteJeton)], ct);

    public Task<TableEcran> TitulaireTableauAsync(string empreinteJeton, Guid officine, CancellationToken ct) =>
        LireAsync(null, null, "SELECT * FROM cloud_ecran_titulaire_tableau($1, $2)", [Texte(empreinteJeton), Uuid(officine)], ct);

    public Task<TableEcran> TitulaireJournalAsync(string empreinteJeton, Guid officine, int page, int taille, CancellationToken ct) =>
        LireAsync(null, null, "SELECT * FROM cloud_ecran_titulaire_journal($1, $2, $3, $4)", [Texte(empreinteJeton), Uuid(officine), Entier(page), Entier(taille)], ct);

    // Lot S7.3 (script central 034) : vue d'ensemble (une ligne par officine, total fait par VueEnsemble) et ordres du titulaire
    public Task<TableEcran> TitulaireEnsembleAsync(string empreinteJeton, CancellationToken ct) =>
        LireAsync(null, null, "SELECT * FROM cloud_ecran_titulaire_ensemble($1)", [Texte(empreinteJeton)], ct);

    /// <summary>Lot S7.5 (script central 036) : lignes absentes et tables concernées, d'une officine ou (null) de toutes celles du compte
    /// avec le droit « consulter » ; NULL de la base = non disponible (aucun chargement complet terminé).</summary>
    public Task<TableEcran> TitulaireLignesAbsentesAsync(string empreinteJeton, Guid? officine, CancellationToken ct) =>
        LireAsync(null, null, "SELECT id, lignes, tables FROM cloud_ecran_titulaire_lignes_absentes($1, $2)",
            [Texte(empreinteJeton), new NpgsqlParameter { Value = officine is { } o ? (object)o : DBNull.Value, NpgsqlDbType = NpgsqlDbType.Uuid }], ct);

    public Task<TableEcran> TitulaireOrdresAsync(string empreinteJeton, Guid officine, int page, int taille, CancellationToken ct) =>
        LireAsync(null, null, "SELECT * FROM cloud_ecran_titulaire_ordres($1, $2, $3, $4)", [Texte(empreinteJeton), Uuid(officine), Entier(page), Entier(taille)], ct);

    /// <summary>Droit accordé au compte sur une officine rattachée (lu par cloud_ecran_titulaire_officines) : distingue « aucun ordre » d'un refus.</summary>
    public async Task<bool> TitulaireDroitAsync(string empreinteJeton, Guid officine, string droit, CancellationToken ct)
    {
        var t = await TitulaireOfficinesAsync(empreinteJeton, ct);
        return t.Lignes.Any(l => t.Valeur(l, "id") is Guid g && g == officine && t.Valeur(l, "droits") is string d && d.Split(',').Contains(droit));
    }

    /// <summary>Ordre du titulaire (liste fermée, droit « ordres », code de confirmation : seule son empreinte part vers la base).</summary>
    public async Task<string> CreerOrdreTitulaireAsync(string empreinteJeton, Guid officine, string? type, string? empreinteCode, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand("SELECT statut FROM cloud_ecran_titulaire_creer_ordre($1, $2, $3, $4)");
        cmd.Parameters.Add(Texte(empreinteJeton));
        cmd.Parameters.Add(Uuid(officine));
        cmd.Parameters.Add(Texte(type is { Length: <= 40 } ? type : null));
        cmd.Parameters.Add(Texte(empreinteCode));
        return (string)(await cmd.ExecuteScalarAsync(ct))!;
    }

    // Lot S7.4 (script central 035) : réglages communs du titulaire (liste fermée, droit « ordres » sur chaque officine, tout ou rien)
    /// <summary>Envoi d'un jeu (données reglages-v1 déjà contrôlées par l'écran, revérifiées par la base) ; seule l'empreinte du code part.</summary>
    public async Task<(string Statut, Guid? Officine, int Ordres)> EnvoyerReglagesAsync(string empreinteJeton, IReadOnlyList<Guid> officines, string reglagesJson,
        string? empreinteCode, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand("SELECT statut, officine_id, ordres FROM cloud_ecran_titulaire_envoyer_reglages($1, $2, $3, $4)");
        cmd.Parameters.Add(Texte(empreinteJeton));
        cmd.Parameters.Add(new NpgsqlParameter { Value = officines.ToArray(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Uuid });
        cmd.Parameters.Add(new NpgsqlParameter { Value = reglagesJson, NpgsqlDbType = NpgsqlDbType.Jsonb });
        cmd.Parameters.Add(Texte(empreinteCode));
        await using var r = await cmd.ExecuteReaderAsync(ct);
        await r.ReadAsync(ct);
        return (r.GetString(0), r.IsDBNull(1) ? null : r.GetGuid(1), r.GetInt32(2));
    }

    /// <summary>Mes envois de réglages communs (officines encore rattachées avec le droit « ordres ») : une ligne par officine.</summary>
    public Task<TableEcran> TitulaireReglagesEnvoisAsync(string empreinteJeton, int page, int taille, CancellationToken ct) =>
        LireAsync(null, null, "SELECT * FROM cloud_ecran_titulaire_reglages_envois($1, $2, $3)", [Texte(empreinteJeton), Entier(page), Entier(taille)], ct);

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

    // ------------------------------------------------------------------ envoi des codes et comptes (lot S7.6, script central 037)
    /// <summary>Réglage lu par l'éditeur : jamais le mot de passe, seulement « renseigné oui/non » (une ligne).</summary>
    public Task<TableEcran> ReglageEnvoiCodesAsync(string empreinteJeton, CancellationToken ct) =>
        LireAsync(null, null, "SELECT * FROM cloud_ecran_facteur_reglage_lire($1)", [Texte(empreinteJeton)], ct);

    /// <summary>Réglage pour l'envoi d'un code (mot de passe CHIFFRÉ seulement) ; null sans session valable.</summary>
    public async Task<ReglageEnvoiCodes?> ReglageEnvoiAsync(string empreinteJeton, CancellationToken ct)
    {
        await using var cmd = Commande("""
            SELECT canal, smtp_serveur, smtp_port, smtp_tls, smtp_identifiant, smtp_expediteur, smtp_mot_de_passe_chiffre, complet
            FROM cloud_ecran_facteur_reglage_envoi($1)
            """, empreinteJeton);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        string? T(int i) => r.IsDBNull(i) ? null : r.GetString(i);
        return new ReglageEnvoiCodes(r.GetString(0), T(1), r.IsDBNull(2) ? null : r.GetInt32(2), r.GetString(3), T(4), T(5), T(6), r.GetBoolean(7));
    }

    /// <summary>Enregistrement du réglage (code de second facteur ; mot de passe CHIFFRÉ ou null = inchangé) ; rend le statut de la base.</summary>
    public async Task<string> EnregistrerReglageEnvoiCodesAsync(string empreinteJeton, string? empreinteCode, string? canal, string? serveur, int? port,
        string? tls, string? identifiant, string? expediteur, string? motDePasseChiffre, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand("SELECT statut FROM cloud_ecran_facteur_reglage_enregistrer($1, $2, $3, $4, $5, $6, $7, $8, $9)");
        foreach (var v in new[] { empreinteJeton, empreinteCode, canal, serveur }) cmd.Parameters.Add(Texte(v));
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)port ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Integer });
        foreach (var v in new[] { tls, identifiant, expediteur, motDePasseChiffre }) cmd.Parameters.Add(Texte(v));
        return (string)(await cmd.ExecuteScalarAsync(ct))!;
    }

    /// <summary>Résultat d'un envoi au journal des accès (événement et objet de la liste fermée du script 037).</summary>
    public async Task NoterEnvoiAsync(string empreinteJeton, string evenement, string objet, CancellationToken ct)
    {
        await using var cmd = Commande("SELECT cloud_ecran_facteur_noter_envoi($1, $2, $3)", empreinteJeton, evenement, objet);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Comptes distants vus par l'éditeur (contact masqué ; script central 040 : état du mot de passe), page par page.</summary>
    public Task<TableEcran> ComptesAsync(string empreinteJeton, int page, int taille, CancellationToken ct) =>
        LireAsync(null, null, "SELECT * FROM cloud_ecran_comptes_liste($1, $2, $3)", [Texte(empreinteJeton), Entier(page), Entier(taille)], ct);

    /// <summary>Contact d'un compte (vide = inchangé, « - » = retiré ; code de second facteur) ; rend le statut de la base.</summary>
    public async Task<string> ModifierContactAsync(string empreinteJeton, string? empreinteCode, Guid compte, string? email, string? telephone, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand("SELECT statut FROM cloud_ecran_facteur_compte_contact($1, $2, $3, $4, $5)");
        cmd.Parameters.Add(Texte(empreinteJeton));
        cmd.Parameters.Add(Texte(empreinteCode));
        cmd.Parameters.Add(Uuid(compte));
        cmd.Parameters.Add(Texte(email is { Length: <= 254 } ? email : email is null ? null : "invalide"));
        cmd.Parameters.Add(Texte(telephone is { Length: <= 40 } ? telephone : telephone is null ? null : "invalide"));
        return (string)(await cmd.ExecuteScalarAsync(ct))!;
    }

    // ------------------------------------------------------------------ reste S7.2 et S7.3 (script central 038)
    /// <summary>
    /// Remise à zéro du mot de passe d'un autre compte par l'éditeur : seule l'empreinte BCrypt du mot de passe provisoire part vers
    /// la base (jamais le mot de passe) ; motif, identifiant ressaisi, empreinte du code de confirmation. Rend le statut de la base.
    /// </summary>
    public async Task<string> RemettreMotDePasseAsync(string empreinteJeton, string? empreinteCode, Guid compte, string? motif, string? confirmation,
        string empreinteBcrypt, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand("SELECT statut FROM cloud_ecran_remise_mot_de_passe($1, $2, $3, $4, $5, $6)");
        cmd.Parameters.Add(Texte(empreinteJeton));
        cmd.Parameters.Add(Texte(empreinteCode));
        cmd.Parameters.Add(Uuid(compte));
        cmd.Parameters.Add(Texte(motif is { Length: <= 400 } ? motif : null));
        cmd.Parameters.Add(Texte(confirmation is { Length: <= 100 } ? confirmation : null));
        cmd.Parameters.Add(Texte(empreinteBcrypt));
        return (string)(await cmd.ExecuteScalarAsync(ct))!;
    }

    /// <summary>Script central 039 : compte distant créé par l'éditeur (mot de passe provisoire : empreinte BCrypt seulement) ; statut et n° du compte.</summary>
    public async Task<(string Statut, Guid? Compte)> CreerCompteAsync(string empreinteJeton, string? empreinteCode, string? identifiant, string? nom, string? role,
        string? telephone, string? email, string empreinteBcrypt, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand("SELECT statut, compte FROM cloud_ecran_compte_creer($1, $2, $3, $4, $5, $6, $7, $8)");
        cmd.Parameters.Add(Texte(empreinteJeton));
        cmd.Parameters.Add(Texte(empreinteCode));
        cmd.Parameters.Add(Texte(identifiant is { Length: <= 100 } ? identifiant : null));
        cmd.Parameters.Add(Texte(nom is { Length: <= 200 } ? nom : null));
        cmd.Parameters.Add(Texte(role is { Length: <= 20 } ? role : null));
        cmd.Parameters.Add(Texte(telephone is { Length: <= 30 } ? telephone : null));
        cmd.Parameters.Add(Texte(email is { Length: <= 300 } ? email : null));
        cmd.Parameters.Add(Texte(empreinteBcrypt));
        await using var r = await cmd.ExecuteReaderAsync(ct);
        await r.ReadAsync(ct);
        return (r.GetString(0), r.IsDBNull(1) ? null : r.GetGuid(1));
    }

    /// <summary>Retrait d'un rattachement par le titulaire lui-même (nom de l'officine ressaisi, code) ; statut et nombre d'ordres annulés.</summary>
    public async Task<(string Statut, int OrdresAnnules)> RetirerRattachementAsync(string empreinteJeton, Guid officine, string? confirmation, string? empreinteCode,
        CancellationToken ct)
    {
        await using var cmd = db.CreateCommand("SELECT statut, ordres_annules FROM cloud_ecran_titulaire_retirer_officine($1, $2, $3, $4)");
        cmd.Parameters.Add(Texte(empreinteJeton));
        cmd.Parameters.Add(Uuid(officine));
        cmd.Parameters.Add(Texte(confirmation is { Length: <= 300 } ? confirmation : null));
        cmd.Parameters.Add(Texte(empreinteCode));
        await using var r = await cmd.ExecuteReaderAsync(ct);
        await r.ReadAsync(ct);
        return (r.GetString(0), r.GetInt32(1));
    }

    /// <summary>Message à afficher sur les postes d'une officine (texte déjà contrôlé par l'écran, revérifié par la base) ; rend le statut.</summary>
    public async Task<string> EnvoyerMessageAsync(string empreinteJeton, Guid officine, string? texte, int jours, string? empreinteCode, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand("SELECT statut FROM cloud_ecran_titulaire_envoyer_message($1, $2, $3, $4, $5)");
        cmd.Parameters.Add(Texte(empreinteJeton));
        cmd.Parameters.Add(Uuid(officine));
        cmd.Parameters.Add(Texte(texte is { Length: <= 1000 } ? texte : null));
        cmd.Parameters.Add(Entier(jours));
        cmd.Parameters.Add(Texte(empreinteCode));
        return (string)(await cmd.ExecuteScalarAsync(ct))!;
    }

    // ------------------------------------------------------------------ import de comptes et durée du provisoire (script central 040)
    /// <summary>Aperçu d'un import (aucun code) : statut de chaque ligne (n° de ligne du fichier → statut de la base).</summary>
    public async Task<Dictionary<int, string>> ApercuImportAsync(string empreinteJeton, string lignesJson, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand("SELECT numero, statut FROM cloud_ecran_comptes_apercu($1, $2)");
        cmd.Parameters.Add(Texte(empreinteJeton));
        cmd.Parameters.Add(Json(lignesJson));
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var statuts = new Dictionary<int, string>();
        while (await r.ReadAsync(ct)) statuts.TryAdd(r.GetInt32(0), r.GetString(1));
        return statuts;
    }

    /// <summary>
    /// Import tout ou rien (un seul code) : chaque ligne porte l'empreinte BCrypt de son mot de passe provisoire. Rend les lignes de la
    /// base : (n°, ok, compte) à la réussite ; (n°, statut, null) si une ligne est refusée ; (null, statut du code) sinon.
    /// </summary>
    public async Task<IReadOnlyList<(int? Numero, string Statut, Guid? Compte)>> ImporterComptesAsync(string empreinteJeton, string? empreinteCode,
        string lignesJson, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand("SELECT numero, statut, compte FROM cloud_ecran_comptes_importer($1, $2, $3)");
        cmd.Parameters.Add(Texte(empreinteJeton));
        cmd.Parameters.Add(Texte(empreinteCode));
        cmd.Parameters.Add(Json(lignesJson));
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var lignes = new List<(int?, string, Guid?)>();
        while (await r.ReadAsync(ct)) lignes.Add((r.IsDBNull(0) ? null : r.GetInt32(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetGuid(2)));
        return lignes;
    }

    /// <summary>Contact d'un compte remis à zéro il y a 2 minutes au plus (envoi de l'identifiant et du mot de passe) ; null sinon.</summary>
    public async Task<(string Identifiant, string Role, string? Email, string? Telephone)?> DestinataireRemiseAsync(string empreinteJeton, Guid compte,
        CancellationToken ct)
    {
        await using var cmd = db.CreateCommand("SELECT identifiant, role, email, telephone FROM cloud_ecran_comptes_destinataire_remise($1, $2)");
        cmd.Parameters.Add(Texte(empreinteJeton));
        cmd.Parameters.Add(Uuid(compte));
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        return (r.GetString(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3));
    }

    /// <summary>Résultat de l'envoi de l'identifiant (e-mail) ou du mot de passe (SMS) d'un compte au journal des accès (liste fermée).</summary>
    public async Task NoterEnvoiCompteAsync(string empreinteJeton, Guid compte, string evenement, string objet, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand("SELECT cloud_ecran_comptes_noter_envoi($1, $2, $3, $4)");
        cmd.Parameters.Add(Texte(empreinteJeton));
        cmd.Parameters.Add(Uuid(compte));
        cmd.Parameters.Add(Texte(evenement));
        cmd.Parameters.Add(Texte(objet));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Durée de vie (jours) d'un mot de passe provisoire non utilisé.</summary>
    public async Task<int> DureeProvisoireAsync(string empreinteJeton, CancellationToken ct)
    {
        await using var cmd = Commande("SELECT cloud_ecran_provisoire_duree_lire($1)", empreinteJeton);
        return (int)(await cmd.ExecuteScalarAsync(ct))!;
    }

    /// <summary>Durée enregistrée (1 à 30 jours, code de confirmation) ; rend le statut de la base.</summary>
    public async Task<string> EnregistrerDureeProvisoireAsync(string empreinteJeton, string? empreinteCode, int? jours, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand("SELECT statut FROM cloud_ecran_provisoire_duree_enregistrer($1, $2, $3)");
        cmd.Parameters.Add(Texte(empreinteJeton));
        cmd.Parameters.Add(Texte(empreinteCode));
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)jours ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Integer });
        return (string)(await cmd.ExecuteScalarAsync(ct))!;
    }

    /// <summary>Déconnexion après inactivité (minutes) : éditeur, titulaire.</summary>
    public async Task<(int Editeur, int Titulaire)> InactiviteAsync(string empreinteJeton, CancellationToken ct)
    {
        await using var cmd = Commande("SELECT inactivite_editeur_minutes, inactivite_titulaire_minutes FROM cloud_ecran_inactivite_lire($1)", empreinteJeton);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? (r.GetInt32(0), r.GetInt32(1)) : (15, 30);
    }

    /// <summary>Inactivité enregistrée (5 à 60 minutes par rôle, code de confirmation) ; rend le statut de la base.</summary>
    public async Task<string> EnregistrerInactiviteAsync(string empreinteJeton, string? empreinteCode, int? editeur, int? titulaire, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand("SELECT statut FROM cloud_ecran_inactivite_enregistrer($1, $2, $3, $4)");
        cmd.Parameters.Add(Texte(empreinteJeton));
        cmd.Parameters.Add(Texte(empreinteCode));
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)editeur ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Integer });
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)titulaire ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Integer });
        return (string)(await cmd.ExecuteScalarAsync(ct))!;
    }

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

    private static NpgsqlParameter Json(string v) => new() { Value = v, NpgsqlDbType = NpgsqlDbType.Jsonb };

    private static NpgsqlParameter Uuid(Guid v) => new() { Value = v, NpgsqlDbType = NpgsqlDbType.Uuid };
}
