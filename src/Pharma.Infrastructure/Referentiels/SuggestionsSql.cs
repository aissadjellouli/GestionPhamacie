using Npgsql;
using Pharma.Application.Commun;

namespace Pharma.Infrastructure.Referentiels;

/// <summary>
/// Valeurs déjà saisies d'un champ de la liste blanche (ChampsSuggestions) : table, colonne et condition viennent du code, la saisie
/// est toujours un paramètre lié. Comparaison sans accents (sans_accent) ni majuscules (ILIKE), valeurs vides exclues, valeurs
/// distinctes sans majuscules ni accents, les plus fréquentes d'abord (ou par ordre alphabétique pour un champ marqué Alphabetique).
/// </summary>
public sealed class SuggestionsSql(NpgsqlDataSource db) : ISuggestionsDepot
{
    public async Task<IReadOnlyList<string>> ValeursAsync(ChampSuggestion champ, string texte, int limite, CancellationToken ct = default)
    {
        // Garde : seul un champ de la liste blanche est lu (jamais un identifiant venu d'ailleurs)
        if (!ChampsSuggestions.Liste.TryGetValue(champ.Cle, out var connu) || connu != champ)
            throw new ArgumentException("Champ hors de la liste blanche.", nameof(champ));
        var condition = connu.Condition is null ? "" : $" AND ({connu.Condition})";
        // Ordre fixé par le code (jamais par le poste) : alphabétique sans majuscules ni accents, ou les plus fréquentes d'abord
        var ordre = connu.Alphabetique ? $"lower(sans_accent(btrim({connu.Colonne}))), 1" : "count(*) DESC, 1";
        // Valeurs distinctes sans tenir compte des majuscules ni des accents (« Pédiatrie » et « pediatrie » : une seule proposition,
        // l'écriture la plus fréquente), les plus fréquentes d'abord
        await using var cmd = db.CreateCommand($"""
            SELECT mode() WITHIN GROUP (ORDER BY btrim({connu.Colonne})) AS valeur
            FROM {connu.Table}
            WHERE {connu.Colonne} IS NOT NULL AND btrim({connu.Colonne}) <> ''{condition}
              AND sans_accent({connu.Colonne}) ILIKE '%' || sans_accent($1) || '%' ESCAPE '\'
            GROUP BY lower(sans_accent(btrim({connu.Colonne})))
            ORDER BY {ordre}
            LIMIT $2
            """);
        cmd.Parameters.AddWithValue(MotifRecherche.Echapper(texte));   // \, % et _ tapés pris à la lettre
        cmd.Parameters.AddWithValue(limite);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var liste = new List<string>();
        while (await r.ReadAsync(ct)) liste.Add(r.GetString(0));
        return liste;
    }
}
