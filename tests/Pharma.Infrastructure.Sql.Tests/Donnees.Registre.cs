namespace Pharma.Infrastructure.Sql.Tests;

/// <summary>Fabrique du lot 5 (registre des psychotropes).</summary>
public sealed partial class Donnees
{
    /// <summary>Ordonnance d'essai (exigée par toute sortie du registre).</summary>
    public Task<long> OrdonnanceAsync(long patientId, DateOnly? datePrescription = null)
        => InsererAsync("INSERT INTO ordonnance (patient_id, date_prescription, numero) VALUES (@p, COALESCE(@d, current_date), @n) RETURNING id",
            ("p", patientId), ("d", datePrescription), ("n", $"ORD-ESSAI-{Suivant()}"));

    /// <summary>
    /// Inscription au registre à l'instant demandé (<paramref name="creeLe"/>). <paramref name="soldeLot"/> : solde du lot après le mouvement.
    /// Une sortie reçoit son patient et son ordonnance (obligatoires). Les numéros d'ordre suivent l'ordre des appels.
    /// </summary>
    public async Task<long> InscrireRegistreAsync(long lotId, long utilisateurId, string type, int quantite, int soldeLot, DateTimeOffset creeLe)
    {
        long? patient = null, ordonnance = null;
        if (type == "sortie")
        {
            patient = await PatientAsync();
            ordonnance = await OrdonnanceAsync(patient.Value);
        }
        return await InsererAsync(
            "INSERT INTO registre_psychotrope (lot_id, type_mouvement, quantite, solde_apres, patient_id, ordonnance_id, utilisateur_id, cree_le) " +
            "VALUES (@lot, @type, @q, @solde, @pat, @ord, @u, @le) RETURNING id",
            ("lot", lotId), ("type", type), ("q", quantite), ("solde", soldeLot), ("pat", patient), ("ord", ordonnance), ("u", utilisateurId), ("le", creeLe));
    }
}
