using System.Text.Json;
using Pharma.Application.Commun;
using Pharma.Domain.Commun;

namespace Pharma.Application.Synchronisation;

// ---- Référentiels, lots 3 à 5 (docs/conception-synchronisation.md § 28) : prestations, indications, DCI envoyées par le central ----
// Prestations par code : ajouts et corrections (libellé, prix, TVA, remboursement ; une valeur absente garde celle de l'officine ;
// l'état actif n'est jamais changé ; une prestation nouvelle sans prix est créée à 0 DA, jamais vendue tant que son prix n'est pas fixé).
// Indications par libellé et DCI par nom : AJOUTS seulement. Rien n'est supprimé ni désactivé. Prestations et indications passent par
// les cas d'usage des écrans (mêmes contrôles, même journal) ; DCI par le dépôt des ordres (une transaction, un journal).

/// <summary>Changements d'une liste : éléments ajoutés et corrigés.</summary>
public sealed record ChangementsListe(IReadOnlyList<string> Ajouts, IReadOnlyList<string> Corrections)
{
    private static string Liste(IReadOnlyList<string> n) => n.Count == 0 ? "" : $" ({string.Join(", ", n.Take(5))}{(n.Count > 5 ? ", …" : "")})";

    public bool Vide => Ajouts.Count + Corrections.Count == 0;

    public string Texte() => Vide ? "Rien ne change : déjà à jour."
        : string.Join(", ", new[]
        {
            Ajouts.Count > 0 ? $"{Ajouts.Count} ajout(s){Liste(Ajouts)}" : null,
            Corrections.Count > 0 ? $"{Corrections.Count} correction(s){Liste(Corrections)}" : null
        }.Where(x => x is not null)) + ".";
}

public interface IListesCentrales
{
    /// <summary>Changements prévus (prestations ou indications), sans rien écrire ; données invalides : OrdreInapplicableException.</summary>
    Task<ChangementsListe> CalculerAsync(string type, JsonElement donnees, CancellationToken ct = default);
    /// <summary>Application au nom du titulaire qui confirme.</summary>
    Task<ChangementsListe> AppliquerAsync(string type, JsonElement donnees, long titulaireId, CancellationToken ct = default);
}

public sealed class ListesCentrales(Caisse.ITypesPrestation prestations, Caisse.TypesPrestation gestionPrestations,
    Catalogue.IIndications indications, Catalogue.Indications gestionIndications) : IListesCentrales
{
    private sealed record PrestationRecue(string Code, string Libelle, decimal? Prix, decimal? TauxTva, bool? Remboursable, decimal? Tarif);
    private sealed record PrestationPrevue(long? Id, string Code, string Libelle, decimal Prix, decimal TauxTva, bool Actif, bool Remboursable, decimal? Tarif);

    public async Task<ChangementsListe> CalculerAsync(string type, JsonElement donnees, CancellationToken ct = default) => type switch
    {
        TypesOrdre.Prestations => (await PlanPrestationsAsync(donnees, ct)).Changements,
        TypesOrdre.Indications => (await PlanIndicationsAsync(donnees, ct)).Changements,
        _ => throw new OrdreInapplicableException($"Type de liste non pris en charge : {type}.")
    };

    public async Task<ChangementsListe> AppliquerAsync(string type, JsonElement donnees, long titulaireId, CancellationToken ct = default)
    {
        if (type == TypesOrdre.Prestations)
        {
            var (prevues, changements) = await PlanPrestationsAsync(donnees, ct);
            foreach (var p in prevues)
                await gestionPrestations.EnregistrerAsync(p.Id, p.Code, p.Libelle, p.Prix, p.TauxTva, p.Actif, titulaireId, ct, p.Remboursable, p.Tarif);
            return changements;
        }
        if (type == TypesOrdre.Indications)
        {
            var (nouvelles, changements) = await PlanIndicationsAsync(donnees, ct);
            foreach (var l in nouvelles) await gestionIndications.EnregistrerAsync(null, l, true, titulaireId, ct);
            return changements;
        }
        throw new OrdreInapplicableException($"Type de liste non pris en charge : {type}.");
    }

    private async Task<(List<PrestationPrevue> Prevues, ChangementsListe Changements)> PlanPrestationsAsync(JsonElement d, CancellationToken ct)
    {
        List<PrestationRecue> recues;
        try
        {
            static decimal? N(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDecimal() : null;
            recues = d.GetProperty("prestations").EnumerateArray().Select(e => new PrestationRecue(
                e.GetProperty("code").GetString() ?? "", e.GetProperty("libelle").GetString() ?? "", N(e, "prix"), N(e, "taux_tva"),
                e.TryGetProperty("remboursable", out var r) && r.ValueKind is JsonValueKind.True or JsonValueKind.False ? r.GetBoolean() : null,
                N(e, "tarif_reference"))).ToList();
        }
        catch (Exception e) when (e is KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new OrdreInapplicableException("Liste de prestations mal formée.");
        }
        if (recues.Count == 0) throw new OrdreInapplicableException("Aucune prestation dans l’ordre.");
        var existantes = await ToutesAsync((p, c) => prestations.ListerAsync(null, null, p, c), ct);
        var prevues = new List<PrestationPrevue>();
        var (ajouts, corrections) = (new List<string>(), new List<string>());
        try
        {
            foreach (var r in recues)
            {
                var ex = existantes.FirstOrDefault(x => string.Equals(x.Code.Trim(), r.Code.Trim(), StringComparison.OrdinalIgnoreCase));
                // Valeur absente de l'ordre : celle de l'officine (ou, pour une prestation nouvelle : 0 DA, TVA 0, non remboursable)
                var prix = r.Prix ?? ex?.Prix ?? 0m;
                var tva = r.TauxTva ?? ex?.TauxTva ?? 0m;
                var remboursable = r.Remboursable ?? ex?.Remboursable ?? false;
                var tarif = r.Remboursable is null && r.Tarif is null ? ex?.TarifReference : r.Tarif;
                // Mêmes contrôles que l'écran (TypesPrestation.EnregistrerAsync), faits ici avant toute écriture
                var (code, libelle) = Pharma.Domain.Ventes.RegleTypePrestation.Verifier(r.Code, r.Libelle, prix, tva);
                tarif = Pharma.Domain.Ventes.RegleTypePrestation.VerifierRemboursement(remboursable, tarif);
                if (ex is null)
                {
                    prevues.Add(new PrestationPrevue(null, code, libelle, prix, tva, true, remboursable, tarif));
                    ajouts.Add(code);
                }
                else if (ex.Libelle != libelle || ex.Prix != prix || ex.TauxTva != tva || ex.Remboursable != remboursable || ex.TarifReference != tarif)
                {
                    prevues.Add(new PrestationPrevue(ex.Id, ex.Code, libelle, prix, tva, ex.Actif, remboursable, tarif));
                    corrections.Add(ex.Code);
                }
            }
        }
        catch (RegleMetierException e)
        {
            throw new OrdreInapplicableException($"Prestations refusées, rien n’a été changé : {e.Message}");
        }
        return (prevues, new ChangementsListe(ajouts, corrections));
    }

    private async Task<(List<string> Nouvelles, ChangementsListe Changements)> PlanIndicationsAsync(JsonElement d, CancellationToken ct)
    {
        List<string> recues;
        try
        {
            recues = d.GetProperty("indications").EnumerateArray().Select(e => e.GetString() ?? "").ToList();
        }
        catch (Exception e) when (e is KeyNotFoundException or InvalidOperationException)
        {
            throw new OrdreInapplicableException("Liste d’indications mal formée.");
        }
        if (recues.Count == 0) throw new OrdreInapplicableException("Aucune indication dans l’ordre.");
        var existantes = await ToutesAsync((p, c) => indications.ListerAsync(null, null, p, c), ct);
        var nouvelles = new List<string>();
        try
        {
            foreach (var r in recues)
            {
                var l = Pharma.Domain.Catalogue.RegleIndication.VerifierLibelle(r);
                // Libellé unique sans tenir compte de la casse : une indication existante (même désactivée) n'est jamais recréée
                if (!existantes.Any(x => string.Equals(x.Libelle.Trim(), l, StringComparison.OrdinalIgnoreCase))
                    && !nouvelles.Contains(l, StringComparer.OrdinalIgnoreCase))
                    nouvelles.Add(l);
            }
        }
        catch (RegleMetierException e)
        {
            throw new OrdreInapplicableException($"Indications refusées, rien n’a été changé : {e.Message}");
        }
        return (nouvelles, new ChangementsListe(nouvelles, []));
    }

    private static async Task<List<T>> ToutesAsync<T>(Func<DemandePage, CancellationToken, Task<Page<T>>> lister, CancellationToken ct)
    {
        var tous = new List<T>();
        for (var page = 1; ; page++)
        {
            var p = await lister(new DemandePage(page, DemandePage.TailleMax), ct);
            tous.AddRange(p.Lignes);
            if (p.Lignes.Count < DemandePage.TailleMax || tous.Count >= p.Total) return tous;
        }
    }
}
