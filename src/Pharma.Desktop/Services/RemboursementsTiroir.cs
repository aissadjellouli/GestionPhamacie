namespace Pharma.Desktop.Services;

/// <summary>
/// « Rembourser » le trop-payé d'une régularisation Chifa (recette du 2026-11-15, D4) : l'écran qui l'affiche (rattachement à la caisse,
/// Finances › Chifa › Volets avec écart) dépose la demande ; la colonne d'actions de la caisse ouvre la sortie de tiroir « Remboursement
/// d'un client » pré-remplie (patient, montant, raison) et liée à la régularisation, puis annonce le bon (montant marqué rendu).
/// </summary>
public sealed class RemboursementsTiroir
{
    public sealed record Demande(long VenteId, string Vente, long PatientId, string Patient, decimal Montant)
    {
        public string Raison => $"Régularisation Chifa {Vente}";
    }

    public Demande? EnAttente { get; private set; }
    /// <summary>Une demande attend la colonne d'actions de la caisse.</summary>
    public event Action? Deposee;
    /// <summary>Trop-payé rendu : vente et n° du bon.</summary>
    public event Action<long, string>? Rendu;

    public void Deposer(Demande d)
    {
        EnAttente = d;
        Deposee?.Invoke();
    }

    /// <summary>Prend la demande en attente (une seule fois).</summary>
    public Demande? Prendre()
    {
        var d = EnAttente;
        EnAttente = null;
        return d;
    }

    public void Annoncer(long venteId, string bon) => Rendu?.Invoke(venteId, bon);
}
