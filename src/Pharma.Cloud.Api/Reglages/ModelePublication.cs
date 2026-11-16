using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Pharma.Domain.Reglages;
using Pharma.Shared;

namespace Pharma.Cloud.Api.Reglages;

/// <summary>Modèle prêt à publier : valeur sous forme canonique (octets identiques pour un même modèle).</summary>
public sealed record ModelePrepare(string Table, string Mode, bool? AjoutsLocaux, JsonNode Valeur, string ValeurJson, int Taille, int Elements)
{
    public bool PorteAjoutsLocaux => AjoutsLocaux is not null;
}

/// <summary>
/// Contrôle d'un fichier de modèle mutu-v1 avant publication (D12) : validation complète par le domaine (mêmes règles que la saisie locale,
/// remises terminées refusées avec la date du jour), puis mise en forme canonique du format. Aucune connexion, aucune écriture.
/// Le numéro de version, l'auteur et la date du fichier sont indicatifs : le central les remplace à la publication (version +1, auteur donné par --auteur).
/// </summary>
public static class ModelePublication
{
    /// <summary>Limite de la valeur stockée au central (cloud_publier_reglage) : l'élément descendant complet reste sous 64 Kio.</summary>
    public const int ValeurMaxOctets = 60000;

    public static (ModelePrepare? Modele, IReadOnlyList<string> Erreurs) Preparer(string table, string json, DateOnly aujourdhui)
    {
        var erreurs = ValidationModele.ValiderElement(table, json, aujourdhui).ToList();
        if (erreurs.Count > 0) return (null, erreurs);

        using (var doc = JsonDocument.Parse(json))
        {
            if (doc.RootElement.GetProperty("mode").GetString() == ContratMutu.ModeLocal)
                return (null, ["Mode « local » : ce n'est pas une publication. Pour qu'une officine redevienne maîtresse de ses réglages : --liberer-reglages <code officine>."]);
        }

        try
        {
            var element = SerialisationMutuV1.Lire(table, Encoding.UTF8.GetBytes(json));
            var canonique = SerialisationMutuV1.Serialiser(table, element);
            var racine = JsonNode.Parse(canonique)!.AsObject();
            var valeur = racine["valeur"]!;
            var valeurJson = valeur.ToJsonString(new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
            if (Encoding.UTF8.GetByteCount(valeurJson) > ValeurMaxOctets)
                return (null, [$"La valeur dépasse {ValeurMaxOctets} octets : réduisez le modèle (l'élément descendant est limité à 64 Kio)."]);
            var ajouts = racine["ajouts_locaux"]?.GetValue<bool>();
            var elements = valeur.AsObject().FirstOrDefault(p => p.Value is JsonArray).Value is JsonArray a ? a.Count : valeur.AsObject().Count;
            return (new ModelePrepare(table, element.Mode, ajouts, valeur, valeurJson, canonique.Length, elements), []);
        }
        catch (FormatMutuException e)
        {
            return (null, [e.Message]);
        }
    }
}
