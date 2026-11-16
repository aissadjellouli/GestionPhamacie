using System.Text.RegularExpressions;

namespace Pharma.Application.Catalogue;

/// <summary>
/// Contenance d'un produit (unités par boîte, règle du 2026-11-16) : la saisie du pharmacien (produit.contenance) prime ;
/// la nomenclature nationale importée n'est utilisée que si la saisie est vide, et seulement quand son conditionnement
/// s'analyse sans ambiguïté (« B/30 », « B/030 », « BOITE DE 30 »), éventuellement suivi d'une unité de délivrance comptée par
/// le nombre (règle du 2026-11-16, script 134) : sachets (« SACHETS », « SACH. », « SCH », « SACHETS-DOSES »), ampoules
/// (« AMP », « AMP. », « AMPOULES », « AMP BUV »), comprimés (« CP », « CPR », « COMPRIMÉS EFFERVESCENTS »), gélules,
/// suppositoires, ovules, capsules, patchs, dispositifs transdermiques, unidoses. Cette unité peut être suivie de sa taille unitaire
/// (« B/5 AMP. DE 2ML », « B/30 SACHETS DE 5 G », « BOITE DE 05 AMPOULES DE 01ML », règle du 2026-11-16, script 137) : seul le premier
/// nombre est une quantité ; la taille (ML, MG, G, L) n'est jamais acceptée sans unité de délivrance. Règle du 2026-11-16, script 139 : les contenants
/// flacons (« FL », « FL. », « FLC », « FLACON(S) »), seringues (préremplies ou non), stylos, cartouches (« CART. »), poches et tubes sont aussi des unités comptées
/// par le nombre (« B/01 FL » = 1, « B/10FL. DE 20ML » = 10, « B/50CART. DE 1,8ML » = 50), et la taille peut s'écrire « /5ML » ou « (5ML) » (« B/20AMP./5ML » = 20).
/// Tout autre texte (« B/1+1 », « B/30 ET B/90 »,
/// « FL/10ML », « T/15G », « B/5 AMPOULES EN POLYPROPYLENE », « B/10 DE 2ML », « B/1FL. DE PDRE. », « B/FL 15 ML »…) est considéré comme vide : rien n'est deviné.
/// Le motif est recopié tel quel dans database/migration/139_contenance_flacons_seringues_stylos.sql (fonction contenance_depuis_conditionnement ; test d'alignement).
/// </summary>
public static partial class ContenanceProduit
{
    public const int Minimum = 1;
    public const int Maximum = 10000;

    [GeneratedRegex(@"^\s*(?:B|BTE|BOITE|BOÎTE)\s*(?:/|\s+DE\s+)\s*([0-9]{1,5})(?:\s*(?:SACHETS?(?:(?:\s+|-)DOSES?)?|SACH|SCH|AMPOULES?|AMPS?|AMP\.?\s+BUV|CPR?S?|CP\.?\s+EFF|COMPRIM[EÉ]S?(?:\s+EFFERVESCENTS?)?|G[EÉ]LULES?|SUPPOSITOIRES?|SUPPO|OVULES?|CAPSULES?|PATCH(?:E?S)?|DISPOSITIFS?\s+TRANSDERMIQUES?|UNIDOSES?|R[EÉ]CIPIENTS\s+UNIDOSES|FLACONS?|FLC|FL|(?:SERINGUES?|SERING\.?|SERIN|SER)(?:\s*PR[EÉ][\s-]*REMPL[A-ZÉÈ]*\.?)?|STYLOS?(?:\s*PR[EÉ][\s-]*REMPL[A-ZÉÈ]*\.?)?|CART(?:OUCHES?)?|POCHES?|TUBES?)\.?(?:\s*(?:DE\s*|/\s*)?[0-9]+(?:[.,][0-9]+)?\s*(?:ML|MG|G|L)|\s*\(\s*[0-9]+(?:[.,][0-9]+)?\s*(?:ML|MG|G|L)\s*\))?)?\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Boite();

    /// <summary>Nombre d'unités lu dans un conditionnement de la nomenclature, ou null si le texte n'est pas analysable.</summary>
    public static int? DepuisConditionnement(string? conditionnement)
    {
        if (string.IsNullOrWhiteSpace(conditionnement)) return null;
        var m = Boite().Match(conditionnement);
        if (!m.Success || !int.TryParse(m.Groups[1].Value, out var n)) return null;
        return n is >= Minimum and <= Maximum ? n : null;
    }

    /// <summary>
    /// Contenance tirée de plusieurs lignes de la nomenclature pour un même n° d'enregistrement : retenue seulement si
    /// toutes les lignes donnent la même valeur analysable ; sinon (désaccord ou texte non analysable) : null.
    /// </summary>
    public static int? DepuisNomenclature(IEnumerable<string?> conditionnements)
    {
        int? retenue = null;
        foreach (var c in conditionnements)
        {
            if (DepuisConditionnement(c) is not { } n) return null;
            if (retenue is { } r && r != n) return null;
            retenue = n;
        }
        return retenue;
    }

    /// <summary>La saisie du pharmacien prime ; la nomenclature ne sert que si la saisie est vide.</summary>
    public static int? Retenue(int? saisie, int? nomenclature) => saisie ?? nomenclature;

    /// <summary>Saisie valide : vide, ou entier de 1 à 10000.</summary>
    public static bool SaisieValide(int? saisie) => saisie is null or (>= Minimum and <= Maximum);
}
