using Pharma.Application.Securite;

namespace Pharma.Application.Pilotage;

public sealed record CaisseOuverte(string Poste, string OuvertePar, DateTime OuverteLe, decimal FondCaisse, decimal EspecesAttendues, int Ventes);

public sealed record ClotureDuJour(string Poste, DateTime FermeeLe, decimal Attendu, decimal? Compte, decimal? Ecart);

public sealed record AlerteResume(string Type, string Niveau, string Message);

public sealed record SoldeCompte(string Nom, decimal Solde);

/// <summary>Tableau de bord du titulaire (téléphone) : journée, caisses, alertes, décisions en attente, argent.</summary>
public sealed record TableauPilotage(DateTime GenereLe, decimal ChiffreAffaires, int NombreVentes, decimal PartOrganismes, decimal CreditsAccordes,
    decimal ChiffreAffairesHier, IReadOnlyList<CaisseOuverte> Caisses, IReadOnlyList<ClotureDuJour> Clotures, int AlertesUrgentes,
    IReadOnlyList<AlerteResume> Alertes, int EcartsInventaire, IReadOnlyList<SoldeCompte> Comptes, decimal FacturesAEncaisser,
    string? InventaireComplet);

public interface IPilotage
{
    Task<TableauPilotage> TableauAsync(CancellationToken ct = default);
}

/// <summary>Réservé au titulaire : chiffres de la journée et décisions qui l'attendent.</summary>
public sealed class ObtenirPilotage(IPilotage pilotage, IPermissionService droits)
{
    public async Task<TableauPilotage> ExecuterAsync(long utilisateurId, CancellationToken ct = default)
    {
        await droits.ExigerTitulaireAsync(utilisateurId, "consulter le tableau de bord", ct);
        return await pilotage.TableauAsync(ct);
    }
}
