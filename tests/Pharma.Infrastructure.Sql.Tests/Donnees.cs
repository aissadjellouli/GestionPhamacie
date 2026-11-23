using Npgsql;

namespace Pharma.Infrastructure.Sql.Tests;

/// <summary>
/// Fabrique de données fictives sur la copie (SQL paramétré, noms « Essai », aucune donnée réelle). Chaque appel crée ses propres
/// lignes avec un numéro unique : les tests ne lisent que les identifiants qu'ils ont reçus.
/// Socle du lot 0 : <see cref="UtilisateurAsync"/>, <see cref="PatientAsync"/>, <see cref="ProduitAsync"/>, <see cref="FournisseurAsync"/>,
/// <see cref="ReceptionAsync"/>, <see cref="LotAsync"/>. Les lots suivants complètent la classe dans leur propre fichier (Donnees.Caisse.cs...).
/// </summary>
public sealed partial class Donnees(NpgsqlDataSource source)
{
    private static int compteur;

    private NpgsqlDataSource Source { get; } = source;

    /// <summary>Numéro unique sur 4 chiffres (« 0001 ») pour les noms fictifs.</summary>
    public static string Suivant() => Interlocked.Increment(ref compteur).ToString("0000");

    /// <summary>Exécute une requête qui renvoie un identifiant (INSERT ... RETURNING id).</summary>
    internal async Task<long> InsererAsync(string sql, params (string Nom, object? Valeur)[] parametres)
    {
        await using var cmd = Source.CreateCommand(sql);
        foreach (var (nom, valeur) in parametres) cmd.Parameters.AddWithValue(nom, valeur ?? DBNull.Value);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    /// <summary>Exécute une commande sans résultat (UPDATE de mise en place d'un scénario).</summary>
    internal async Task<int> ExecuterAsync(string sql, params (string Nom, object? Valeur)[] parametres)
    {
        await using var cmd = Source.CreateCommand(sql);
        foreach (var (nom, valeur) in parametres) cmd.Parameters.AddWithValue(nom, valeur ?? DBNull.Value);
        return await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>Lit une valeur unique (relecture de l'état final).</summary>
    public async Task<T> LireAsync<T>(string sql, params (string Nom, object? Valeur)[] parametres)
    {
        await using var cmd = Source.CreateCommand(sql);
        foreach (var (nom, valeur) in parametres) cmd.Parameters.AddWithValue(nom, valeur ?? DBNull.Value);
        return (T)Convert.ChangeType((await cmd.ExecuteScalarAsync())!, typeof(T));
    }

    /// <summary>Employé d'essai (rôle : le premier du référentiel).</summary>
    public Task<long> UtilisateurAsync(string? nomComplet = null)
    {
        var n = Suivant();
        return InsererAsync(
            "INSERT INTO utilisateur (nom_complet, identifiant, mot_de_passe_hash, role_id) " +
            "VALUES (@nom, @identifiant, 'hash-essai', (SELECT min(id) FROM role)) RETURNING id",
            ("nom", nomComplet ?? $"Employe Essai {n}"), ("identifiant", $"essai{n}-{Guid.NewGuid():N}"[..24]));
    }

    /// <summary>Patient d'essai avec le solde de crédit demandé (le plafond de crédit est relevé en conséquence).</summary>
    public Task<long> PatientAsync(decimal solde = 0m)
    {
        var n = Suivant();
        return InsererAsync(
            "INSERT INTO patient (nom, prenom, solde_credit, plafond_credit) VALUES (@nom, @prenom, @solde, @plafond) RETURNING id",
            ("nom", $"Patient Essai {n}"), ("prenom", "Fictif"), ("solde", solde), ("plafond", Math.Max(solde, 1000m)));
    }

    /// <summary>Produit d'essai ; <paramref name="psychotrope"/> : classe « psychotrope » en liste I (inscrit au registre).</summary>
    public Task<long> ProduitAsync(bool psychotrope = false, decimal prixPublic = 100m)
    {
        var n = Suivant();
        return InsererAsync(
            "INSERT INTO produit (nom_commercial, prix_public, classe, liste_psychotrope) VALUES (@nom, @prix, @classe, @liste) RETURNING id",
            ("nom", $"Produit Essai {n}"), ("prix", prixPublic),
            ("classe", psychotrope ? "psychotrope" : "libre"), ("liste", psychotrope ? "liste_i" : null));
    }

    public Task<long> FournisseurAsync()
        => InsererAsync("INSERT INTO fournisseur (raison_sociale) VALUES (@nom) RETURNING id", ("nom", $"Fournisseur Essai {Suivant()}"));

    /// <summary>Réception d'essai (en-tête seul) : exigée par les colonnes reception_id et fournisseur_id d'un lot.</summary>
    public Task<long> ReceptionAsync(long fournisseurId)
        => InsererAsync("INSERT INTO reception (fournisseur_id, numero_bl) VALUES (@f, @bl) RETURNING id", ("f", fournisseurId), ("bl", $"BL-ESSAI-{Suivant()}"));

    /// <summary>
    /// Lot d'essai sur un produit. <paramref name="peremption"/> : date de péremption, par défaut dans un an (calculée par la base).
    /// Crée au passage son fournisseur et sa réception.
    /// </summary>
    public async Task<long> LotAsync(long produitId, int quantite, DateOnly? peremption = null, bool bloque = false)
    {
        var fournisseur = await FournisseurAsync();
        var reception = await ReceptionAsync(fournisseur);
        return await InsererAsync(
            "INSERT INTO lot (produit_id, numero_lot, date_peremption, quantite_disponible, reception_id, fournisseur_id, bloque, motif_blocage) " +
            "VALUES (@p, @num, COALESCE(@per, current_date + 365), @q, @r, @f, @bloque, @motif) RETURNING id",
            ("p", produitId), ("num", $"LOT-ESSAI-{Suivant()}"), ("per", peremption), ("q", quantite), ("r", reception), ("f", fournisseur),
            ("bloque", bloque), ("motif", bloque ? "Blocage d'essai" : null));
    }

    // À faire dans les lots suivants (colonnes non encore relevées) : VenteValideeAsync, PrestationAsync, InventaireAsync, FactureAsync,
    // ReglementAsync, AvoirAsync, PanierAttenteAsync.
}
