namespace Pharma.Application.Chifa;

public sealed record FactureChifa(string Numero, string Organisme, DateTime Date, IReadOnlyList<LigneFactureChifa> Lignes);
public sealed record LigneFactureChifa(string CodeProduit, int Quantite, decimal Montant);

/// <summary>Lecture des factures signées exportées par Chifa_Officine (format à confirmer avec la CNAS).</summary>
public interface IChifaConnecteur
{
    Task<IReadOnlyList<FactureChifa>> LireFacturesSigneesAsync(DateOnly depuis, CancellationToken ct = default);
}
