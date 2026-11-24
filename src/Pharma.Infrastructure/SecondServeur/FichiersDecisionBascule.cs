using System.Text.Json;
using Pharma.Application.SecondServeur;

namespace Pharma.Infrastructure.SecondServeur;

/// <summary>
/// Fichiers de décision de bascule : « bascule-AAAAMMJJTHHMMSSZ.json » dans le dossier des journaux (noms de postes seulement). Écrits AVANT la promotion
/// (la base du 2e serveur est alors en lecture seule), repris au démarrage en principal puis renommés en « .enregistre » (ou « .annule » si la promotion n'a pas eu lieu).
/// </summary>
public sealed class FichiersDecisionBascule(string dossier) : IFichiersDecisionBascule
{
    public const string Motif = "bascule-*.json";
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public async Task<string> EcrireAsync(DecisionBascule decision, CancellationToken ct = default)
    {
        Directory.CreateDirectory(dossier);
        var chemin = Path.Combine(dossier, $"bascule-{decision.DecideLeUtc.ToUniversalTime():yyyyMMdd'T'HHmmss'Z'}.json");
        var temporaire = chemin + ".tmp";
        await File.WriteAllTextAsync(temporaire, JsonSerializer.Serialize(decision, Options), ct);
        File.Move(temporaire, chemin, overwrite: true);   // jamais un fichier à moitié écrit sous le nom définitif
        return chemin;
    }

    public async Task<IReadOnlyList<FichierDecision>> NonEnregistresAsync(CancellationToken ct = default)
    {
        var liste = new List<FichierDecision>();
        if (!Directory.Exists(dossier)) return liste;
        foreach (var chemin in Directory.GetFiles(dossier, Motif).OrderBy(x => x, StringComparer.Ordinal))
        {
            DecisionBascule? decision = null;
            try { decision = JsonSerializer.Deserialize<DecisionBascule>(await File.ReadAllTextAsync(chemin, ct), Options); }
            catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException) { }
            liste.Add(new FichierDecision(chemin, decision));
        }
        return liste;
    }

    public Task MarquerEnregistreAsync(string chemin, CancellationToken ct = default) => Renommer(chemin, ".enregistre");

    public Task AnnulerAsync(string chemin, CancellationToken ct = default) => Renommer(chemin, ".annule");

    private static Task Renommer(string chemin, string extension)
    {
        File.Move(chemin, Path.ChangeExtension(chemin, extension), overwrite: true);
        return Task.CompletedTask;
    }
}
