using Npgsql;

namespace Pharma.Cloud.Api.Etat;

public sealed record OfficineEtat(Guid Id, string Libelle, DateTimeOffset CreeLe, DateTimeOffset? ResilieeLe);

/// <summary>Certificat : début d'empreinte seulement (12 caractères), jamais l'empreinte complète.</summary>
public sealed record CertificatEtat(Guid Officine, string Serveur, string DebutEmpreinte, DateTimeOffset EmisLe, DateTimeOffset Echeance, DateTimeOffset? RevoqueLe);

/// <summary>Code d'activation : ni le code ni son empreinte (la colonne n'est pas lisible par pharma_cloud_etat, script 029).</summary>
public sealed record CodeActivationEtat(Guid Officine, string Serveur, DateTimeOffset CreeLe, DateTimeOffset ExpireLe, DateTimeOffset? UtiliseLe);

public sealed record ChargementEtat(int Numero, string Motif, string Etat, int Epoque, DateTimeOffset DebutLe, DateTimeOffset? FinLe, long Lots, long Lignes,
    long OctetsRecus, long? Absentes);

public sealed record LotEtat(int Epoque, long Numero, int Lignes, int Octets, DateTimeOffset RecuLe);

public sealed record EpoqueEtat(int Epoque, long DernierLot, DateTimeOffset? DernierRecuLe);

public sealed record DetailOfficineEtat(Guid Officine, IReadOnlyList<ChargementEtat> Chargements, IReadOnlyList<EpoqueEtat> Epoques, IReadOnlyList<LotEtat> Lots);

public sealed record JournalEtat(long Id, DateTimeOffset Le, Guid? Officine, string? DebutCertificat, string Evenement, string? Detail);

public sealed record EtatCentral(DateTimeOffset LuLe, string Role, IReadOnlyList<OfficineEtat> Officines, IReadOnlyList<CertificatEtat> Certificats,
    IReadOnlyList<CodeActivationEtat> Codes, IReadOnlyList<DetailOfficineEtat> Details, IReadOnlyList<JournalEtat> Journal);

public interface ILecteurEtatCentral
{
    Task<EtatCentral> LireAsync(CancellationToken ct);
}

/// <summary>
/// Lecture de la page d'état de développement (connexion PHARMA_CLOUD_ETAT_CONNEXION, rôle pharma_cloud_etat du script 029) : SELECT
/// paramétrés seulement, transaction en lecture seule, tables techniques seulement (jamais une table miroir, jamais un contenu de lot,
/// jamais un code d'activation ni son empreinte). Pour les tables cloisonnées, l'officine courante est posée dans chaque transaction
/// (set_config('app.officine_id', …, true)), comme DepotCentral : la RLS s'applique, elle n'est jamais contournée. La connexion est
/// refusée si son rôle est superutilisateur, BYPASSRLS, propriétaire d'une table ou non membre de pharma_cloud_etat.
/// </summary>
public sealed class LecteurEtatCentral(NpgsqlDataSource db, TimeProvider horloge) : ILecteurEtatCentral
{
    public const int MaxOfficines = 100;
    public const int LotsParOfficine = 20;
    public const int LignesJournal = 50;

    internal const string LectureSeule = "SET TRANSACTION READ ONLY";

    internal const string Controle = """
        SELECT r.rolname::text, r.rolsuper, r.rolbypassrls, pg_has_role(current_user, 'pharma_cloud_etat', 'MEMBER'),
               EXISTS (SELECT 1 FROM pg_tables t WHERE t.schemaname = 'public' AND t.tableowner = current_user)
        FROM pg_roles r WHERE r.rolname = current_user
        """;

    internal const string Officines = """
        SELECT o.id, o.libelle, o.cree_le, o.resiliee_le FROM officine o ORDER BY o.cree_le, o.id LIMIT $1
        """;

    internal const string Certificats = """
        SELECT c.officine_id, c.serveur, left(c.empreinte, 12), c.emis_le, c.echeance, c.revoque_le
        FROM certificat c ORDER BY c.emis_le DESC LIMIT $1
        """;

    internal const string Codes = """
        SELECT a.officine_id, a.serveur, a.cree_le, a.expire_le, a.utilise_le FROM code_activation a ORDER BY a.cree_le DESC LIMIT $1
        """;

    internal const string Journal = """
        SELECT j.id, j.le, j.officine_id, left(j.certificat_empreinte, 12), j.evenement, j.detail
        FROM journal_securite j ORDER BY j.id DESC LIMIT $1
        """;

    internal const string PoserOfficine = "SELECT set_config('app.officine_id', $1, true)";

    internal const string Chargements = """
        SELECT c.numero, c.motif, c.etat, c.epoque, c.debut_le, c.fin_le,
               (SELECT count(*) FROM lot_recu l WHERE l.officine_id = c.officine_id AND l.epoque = c.epoque AND l.recu_le >= c.debut_le
                  AND (c.fin_le IS NULL OR l.recu_le <= c.fin_le)),
               (SELECT coalesce(sum(l.nb_lignes), 0) FROM lot_recu l WHERE l.officine_id = c.officine_id AND l.epoque = c.epoque
                  AND l.recu_le >= c.debut_le AND (c.fin_le IS NULL OR l.recu_le <= c.fin_le)),
               c.octets_recus, c.absentes
        FROM chargement c WHERE c.officine_id = $1 ORDER BY c.numero DESC LIMIT 3
        """;

    internal const string Epoques = """
        SELECT e.epoque, e.dernier_lot, e.dernier_recu_le FROM officine_epoque e WHERE e.officine_id = $1 ORDER BY e.epoque DESC
        """;

    internal const string Lots = """
        SELECT l.epoque, l.numero, l.nb_lignes, l.octets, l.recu_le FROM lot_recu l WHERE l.officine_id = $1
        ORDER BY l.recu_le DESC, l.numero DESC LIMIT $2
        """;

    /// <summary>Toutes les requêtes de la page (contrôlées par les tests : SELECT seulement, tables de la liste blanche).</summary>
    public static IReadOnlyList<string> Requetes { get; } = [LectureSeule, Controle, Officines, Certificats, Codes, Journal, PoserOfficine, Chargements, Epoques, Lots];

    public async Task<EtatCentral> LireAsync(CancellationToken ct)
    {
        await using var cnx = await db.OpenConnectionAsync(ct);
        string role;
        List<OfficineEtat> officines;
        List<CertificatEtat> certificats;
        List<CodeActivationEtat> codes;
        List<JournalEtat> journal;
        await using (var tx = await cnx.BeginTransactionAsync(ct))
        {
            await Executer(cnx, tx, LectureSeule, ct);
            role = await ControlerAsync(cnx, tx, ct);
            officines = await Lister(cnx, tx, Officines, [MaxOfficines], r => new OfficineEtat(r.GetGuid(0), r.GetString(1), Date(r, 2), DateOuNull(r, 3)), ct);
            certificats = await Lister(cnx, tx, Certificats, [500], r => new CertificatEtat(r.GetGuid(0), r.GetString(1), r.GetString(2), Date(r, 3), Date(r, 4), DateOuNull(r, 5)), ct);
            codes = await Lister(cnx, tx, Codes, [200], r => new CodeActivationEtat(r.GetGuid(0), r.GetString(1), Date(r, 2), Date(r, 3), DateOuNull(r, 4)), ct);
            journal = await Lister(cnx, tx, Journal, [LignesJournal], r => new JournalEtat(r.GetInt64(0), Date(r, 1), r.IsDBNull(2) ? null : r.GetGuid(2),
                r.IsDBNull(3) ? null : r.GetString(3), r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5)), ct);
            await tx.CommitAsync(ct);
        }

        var details = new List<DetailOfficineEtat>();
        foreach (var o in officines)
        {
            // Une transaction par officine : l'officine courante est posée (SET LOCAL) et la RLS cloisonne lots, époques et chargements
            await using var tx = await cnx.BeginTransactionAsync(ct);
            await Executer(cnx, tx, LectureSeule, ct);
            await using (var poser = new NpgsqlCommand(PoserOfficine, cnx, tx))
            {
                poser.Parameters.AddWithValue(o.Id.ToString("D"));
                await poser.ExecuteScalarAsync(ct);
            }
            var chargements = await Lister(cnx, tx, Chargements, [o.Id], r => new ChargementEtat(r.GetInt32(0), r.GetString(1), r.GetString(2), r.GetInt32(3),
                Date(r, 4), DateOuNull(r, 5), r.GetInt64(6), Convert.ToInt64(r.GetValue(7)), r.GetInt64(8), r.IsDBNull(9) ? null : r.GetInt64(9)), ct);
            var epoques = await Lister(cnx, tx, Epoques, [o.Id], r => new EpoqueEtat(r.GetInt32(0), r.GetInt64(1), DateOuNull(r, 2)), ct);
            var lots = await Lister(cnx, tx, Lots, [o.Id, LotsParOfficine], r => new LotEtat(r.GetInt32(0), r.GetInt64(1), r.GetInt32(2), r.GetInt32(3), Date(r, 4)), ct);
            await tx.CommitAsync(ct);
            details.Add(new DetailOfficineEtat(o.Id, chargements, epoques, lots));
        }
        return new EtatCentral(horloge.GetUtcNow(), role, officines, certificats, codes, details, journal);
    }

    private static async Task<string> ControlerAsync(NpgsqlConnection cnx, NpgsqlTransaction tx, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(Controle, cnx, tx);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) throw new InvalidOperationException("Rôle de connexion introuvable.");
        var (role, super, contourne, membre, proprietaire) = (r.GetString(0), r.GetBoolean(1), r.GetBoolean(2), r.GetBoolean(3), r.GetBoolean(4));
        if (super || contourne || proprietaire || !membre)
            throw new InvalidOperationException(
                $"Connexion refusée pour la page d'état : le rôle {role} doit être membre de pharma_cloud_etat, ni superutilisateur, ni BYPASSRLS, ni propriétaire d'une table.");
        return role;
    }

    private static async Task Executer(NpgsqlConnection cnx, NpgsqlTransaction tx, string sql, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(sql, cnx, tx);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<List<T>> Lister<T>(NpgsqlConnection cnx, NpgsqlTransaction tx, string sql, object[] parametres, Func<NpgsqlDataReader, T> lire,
        CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(sql, cnx, tx);
        foreach (var p in parametres) cmd.Parameters.AddWithValue(p);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var liste = new List<T>();
        while (await r.ReadAsync(ct)) liste.Add(lire(r));
        return liste;
    }

    private static DateTimeOffset Date(NpgsqlDataReader r, int i) => r.GetFieldValue<DateTimeOffset>(i);

    private static DateTimeOffset? DateOuNull(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetFieldValue<DateTimeOffset>(i);
}
