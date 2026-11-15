using System.Globalization;
using Npgsql;
using Pharma.Application.Contacts;

namespace Pharma.Infrastructure.Contacts;

/// <summary>
/// Fiches contact : coordonnées (patient, fournisseur, organisme, client_facture, utilisateur ; 041_contacts.sql pour celles
/// des employés et des organismes) et situation (dette, factures ouvertes de v_facture_client et v_facture_fournisseur).
/// </summary>
public sealed class ContactsSql(NpgsqlDataSource db) : IContacts
{
    private static readonly CultureInfo Fr = CultureInfo.GetCultureInfo("fr-FR");

    // Factures clients qui restent à encaisser (mêmes critères que le suivi des dettes, 039)
    private const string FacturesOuvertes = """
        SELECT count(*)::int, COALESCE(SUM(f.reste), 0) FROM v_facture_client f
        WHERE f.type = 'facture' AND f.statut <> 'remplacee' AND NOT f.hors_suivi AND f.reste > 0
        """;

    public Task<FicheContact?> LireAsync(string type, long id, bool coordonneesEmploye, CancellationToken ct = default) => type switch
    {
        TypesContact.Patient => PatientAsync(id, ct),
        TypesContact.Grossiste => GrossisteAsync(id, ct),
        TypesContact.Organisme => OrganismeAsync(id, ct),
        TypesContact.Client => ClientAsync(id, ct),
        TypesContact.Employe => EmployeAsync(id, coordonneesEmploye, ct),
        _ => Task.FromResult<FicheContact?>(null)
    };

    private async Task<FicheContact?> PatientAsync(long id, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand($"""
            SELECT trim(concat_ws(' ', p.nom, p.prenom)), p.telephone, p.num_assure, o.nom, o.code, p.date_naissance, p.est_chronique,
                   p.plafond_credit, p.solde_credit, p.cree_le,
                   (SELECT max(v.date_vente) FROM vente v WHERE v.patient_id = p.id AND v.statut = 'validee'),
                   o.id, p.email, p.assure_principal_id
            FROM patient p LEFT JOIN organisme o ON o.id = p.organisme_id
            WHERE p.id = $1
            """);
        cmd.Parameters.AddWithValue(id);
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            if (!await r.ReadAsync(ct)) return null;
            var coord = new List<InfoContact>();
            // Patient créé depuis un volet Chifa sans nom saisi : nom provisoire « Assuré n°/suffixe », à compléter (2026-11-15)
            if (Pharma.Application.Caisse.PatientVoletChifa.EstProvisoire(r.GetString(0))) coord.Add(new InfoContact("Nom", "à compléter", "manquant"));
            Ajouter(coord, "Téléphone", T(r, 1), "telephone");
            Email(coord, T(r, 12));
            var situation = new List<InfoContact> { new("Dette en cours", Da(r.GetDecimal(8)), r.GetDecimal(8) > 0 ? "montant_du" : null) };
            if (r.GetDecimal(7) > 0) situation.Add(new("Plafond de crédit", Da(r.GetDecimal(7))));
            Ajouter(situation, "Organisme", r.IsDBNull(3) ? null : $"{r.GetString(3)} ({r.GetString(4)})");
            Ajouter(situation, "N° d'assuré", T(r, 2));
            Ajouter(situation, "Date de naissance", r.IsDBNull(5) ? null : r.GetFieldValue<DateOnly>(5).ToString("dd/MM/yyyy"));
            if (r.GetBoolean(6)) situation.Add(new("Malade chronique", "oui"));
            Ajouter(situation, "Dernier achat", r.IsDBNull(10) ? null : Date(r.GetDateTime(10)));
            situation.Add(new("Fiche créée le", Date(r.GetDateTime(9))));
            var fiche = new FicheContact(TypesContact.Patient, id, r.GetString(0), "Patient", true, coord, situation,
                Saisie: new CoordonneesContact(T(r, 1), T(r, 12), null));
            var (numAssure, principalId) = (T(r, 2), r.IsDBNull(13) ? (long?)null : r.GetInt64(13));
            await r.CloseAsync();
            // Famille (2026-11-15, script 090) : « Ayant droit de … », « Famille : … (conjoint) », « Même carte : … » (même n° de base, non rattachées)
            var famille = Pharma.Application.Patients.FamillePatient.Affichage(id, numAssure, principalId,
                    await Pharma.Infrastructure.Patients.PatientsSql.Proches(db, id, ct))
                .Select(l => new InfoContact(l.Libelle, l.Valeur));
            return fiche with { Situation = [.. fiche.Situation, .. famille, .. await VentesRemisesAsync(id, ct), .. await FacturesAsync("f.patient_id = $1", id, ct)] };
        }
    }

    private async Task<FicheContact?> GrossisteAsync(long id, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand("""
            SELECT f.raison_sociale, f.telephone, f.adresse, f.nif, f.rc, f.solde,
                   (SELECT count(*)::int FROM v_facture_fournisseur x WHERE x.fournisseur_id = f.id AND x.reste > 0),
                   (SELECT COALESCE(SUM(x.reste), 0) FROM v_facture_fournisseur x WHERE x.fournisseur_id = f.id AND x.reste > 0),
                   (SELECT max(r.date_reception) FROM reception r WHERE r.fournisseur_id = f.id),
                   f.email
            FROM fournisseur f WHERE f.id = $1
            """);
        cmd.Parameters.AddWithValue(id);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        var coord = new List<InfoContact>();
        Ajouter(coord, "Téléphone", T(r, 1), "telephone");
        Email(coord, T(r, 9));
        Ajouter(coord, "Adresse", T(r, 2));
        Ajouter(coord, "NIF", T(r, 3));
        Ajouter(coord, "RC", T(r, 4));
        var solde = r.GetDecimal(5);
        var situation = new List<InfoContact>
        {
            new("Dette de la pharmacie", Da(solde), solde > 0 ? "montant_du" : null),
            new("Factures à payer", r.GetInt32(6) == 0 ? "aucune" : $"{r.GetInt32(6)} · {Da(r.GetDecimal(7))}")
        };
        Ajouter(situation, "Dernière livraison", r.IsDBNull(8) ? null : Date(r.GetDateTime(8)));
        return new FicheContact(TypesContact.Grossiste, id, r.GetString(0), "Grossiste", true, coord, situation,
            Saisie: new CoordonneesContact(T(r, 1), T(r, 9), T(r, 2)));
    }

    private async Task<FicheContact?> OrganismeAsync(long id, CancellationToken ct)
    {
        FicheContact fiche;
        await using (var cmd = db.CreateCommand("SELECT nom, code, telephone, email, adresse, nif, actif, delai_paiement_jours FROM organisme WHERE id = $1"))
        {
            cmd.Parameters.AddWithValue(id);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) return null;
            var coord = new List<InfoContact>();
            Ajouter(coord, "Téléphone", T(r, 2), "telephone");
            Ajouter(coord, "E-mail", T(r, 3), "email");
            Ajouter(coord, "Adresse", T(r, 4));
            Ajouter(coord, "NIF", T(r, 5));
            Ajouter(coord, "Délai de paiement", $"{r.GetInt32(7)} jours");
            fiche = new FicheContact(TypesContact.Organisme, id, r.GetString(0), $"Organisme · {r.GetString(1)}", r.GetBoolean(6), coord, []);
        }
        return fiche with { Situation = await FacturesAsync("f.organisme_id = $1", id, ct) };
    }

    private async Task<FicheContact?> ClientAsync(long id, CancellationToken ct)
    {
        FicheContact fiche;
        await using (var cmd = db.CreateCommand("SELECT raison_sociale, telephone, adresse, nif, rc, nis, article_imposition, actif, email, delai_paiement_jours FROM client_facture WHERE id = $1"))
        {
            cmd.Parameters.AddWithValue(id);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) return null;
            var coord = new List<InfoContact>();
            Ajouter(coord, "Téléphone", T(r, 1), "telephone");
            Email(coord, T(r, 8));
            Ajouter(coord, "Adresse", T(r, 2));
            Ajouter(coord, "NIF", T(r, 3));
            Ajouter(coord, "RC", T(r, 4));
            Ajouter(coord, "NIS", T(r, 5));
            Ajouter(coord, "Article d'imposition", T(r, 6));
            Ajouter(coord, "Délai de paiement", $"{r.GetInt32(9)} jours");
            fiche = new FicheContact(TypesContact.Client, id, r.GetString(0), "Entreprise cliente", r.GetBoolean(7), coord, [],
                Saisie: new CoordonneesContact(T(r, 1), T(r, 8), T(r, 2), r.GetInt32(9)));
        }
        return fiche with { Situation = await FacturesAsync("f.client_id = $1", id, ct) };
    }

    private async Task<FicheContact?> EmployeAsync(long id, bool avecCoordonnees, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand("""
            SELECT u.nom_complet, u.identifiant, ro.nom, u.actif, u.derniere_connexion, u.telephone, u.email, u.cree_le
            FROM utilisateur u JOIN role ro ON ro.id = u.role_id WHERE u.id = $1
            """);
        cmd.Parameters.AddWithValue(id);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        var coord = new List<InfoContact>();
        if (avecCoordonnees)
        {
            Ajouter(coord, "Téléphone", T(r, 5), "telephone");
            Ajouter(coord, "E-mail", T(r, 6), "email");
        }
        var situation = new List<InfoContact> { new("Identifiant", r.GetString(1)), new("Rôle", r.GetString(2)) };
        if (!r.GetBoolean(3)) situation.Add(new("Compte", "désactivé"));
        situation.Add(new("Dernière connexion", r.IsDBNull(4) ? "jamais" : DateHeure(r.GetDateTime(4))));
        situation.Add(new("Compte créé le", Date(r.GetDateTime(7))));
        return new FicheContact(TypesContact.Employe, id, r.GetString(0), r.GetString(2), r.GetBoolean(3), coord, situation, !avecCoordonnees);
    }

    /// <summary>
    /// Ventes « Remis à » / « Pour » (script 091, 2026-11-15) : celles au nom du patient avec un complément, et celles d'un autre membre
    /// qui le désignent ; les 5 plus récentes. Ex. « T-… · 15/11/2026 · Karim · remis à Ali ».
    /// </summary>
    private async Task<List<InfoContact>> VentesRemisesAsync(long id, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand($"""
            SELECT v.numero, v.date_vente, {Caisse.RemiseVenteSql.Titulaire}, {Caisse.RemiseVenteSql.RemisA}, {Caisse.RemiseVenteSql.Pour}
            FROM vente v
            WHERE v.statut = 'validee'
              AND ((v.patient_id = $1 AND (v.remis_a_patient_id IS NOT NULL OR v.remis_a_nom IS NOT NULL OR v.pour_patient_id IS NOT NULL OR v.pour_nom IS NOT NULL))
                   OR v.remis_a_patient_id = $1 OR v.pour_patient_id = $1)
            ORDER BY v.date_vente DESC, v.id DESC LIMIT 5
            """);
        cmd.Parameters.AddWithValue(id);
        var lignes = new List<InfoContact>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            lignes.Add(new InfoContact("Vente remise / pour", $"{r.GetString(0)} · {Date(r.GetDateTime(1))} · "
                + Pharma.Application.Caisse.RemiseVente.Libelle(T(r, 2), T(r, 3), T(r, 4))));
        return lignes;
    }

    private async Task<List<InfoContact>> FacturesAsync(string filtre, long id, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand(FacturesOuvertes + " AND " + filtre);
        cmd.Parameters.AddWithValue(id);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        await r.ReadAsync(ct);
        var n = r.GetInt32(0);
        return [new("Factures à encaisser", n == 0 ? "aucune" : $"{n} · {Da(r.GetDecimal(1))}", n > 0 ? "montant_du" : null)];
    }

    public async Task<bool> ModifierCoordonneesAsync(string type, long id, CoordonneesContact c, CancellationToken ct = default)
    {
        var sql = type switch
        {
            TypesContact.Patient => "UPDATE patient SET telephone = $2, email = $3 WHERE id = $1",
            TypesContact.Grossiste => "UPDATE fournisseur SET telephone = $2, email = $3, adresse = $4 WHERE id = $1",
            TypesContact.Client => "UPDATE client_facture SET telephone = $2, email = $3, adresse = $4, delai_paiement_jours = COALESCE($5, delai_paiement_jours) WHERE id = $1",
            _ => throw new ArgumentOutOfRangeException(nameof(type))
        };
        await using var cmd = db.CreateCommand(sql);
        cmd.Parameters.AddWithValue(id);
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)c.Telephone ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text });
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)c.Email ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text });
        if (type != TypesContact.Patient)
            cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)c.Adresse ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text });
        if (type == TypesContact.Client)
            cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)c.DelaiPaiementJours ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Integer });
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    /// <summary>E-mail : information essentielle mais facultative, signalée « à compléter » tant qu'elle manque.</summary>
    private static void Email(List<InfoContact> liste, string? email) =>
        liste.Add(string.IsNullOrWhiteSpace(email) ? new InfoContact("E-mail", "à compléter", "manquant") : new InfoContact("E-mail", email, "email"));

    private static void Ajouter(List<InfoContact> liste, string libelle, string? valeur, string? genre = null)
    {
        if (!string.IsNullOrWhiteSpace(valeur)) liste.Add(new InfoContact(libelle, valeur, genre));
    }

    private static string? T(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
    private static string Da(decimal montant) => montant.ToString("N2", Fr) + " DA";
    private static string Date(DateTime d) => (d.Kind == DateTimeKind.Utc ? d.ToLocalTime() : d).ToString("dd/MM/yyyy", Fr);
    private static string DateHeure(DateTime d) => (d.Kind == DateTimeKind.Utc ? d.ToLocalTime() : d).ToString("dd/MM/yyyy 'à' HH:mm", Fr);
}
