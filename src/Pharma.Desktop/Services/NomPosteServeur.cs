using Pharma.Shared;

namespace Pharma.Desktop.Services;

/// <summary>
/// Nom du poste renommé par le titulaire (Administration › Postes autorisés › « Renommer ») : le serveur utilise aussitôt le nouveau nom,
/// mais le poste affichait l'ancien, écrit dans poste-identite.json à l'autorisation et jamais relu (constaté le 2026-11-26). Le poste prend
/// désormais le nom que le serveur lui donne quand il prouve son identité (GET /api/postes/etat).
/// </summary>
public static class NomPosteServeur
{
    /// <summary>
    /// Nouveau nom à garder, ou null s'il n'y a rien à changer : seulement pour un poste enrôlé, prouvé par le serveur sous le MÊME identifiant
    /// (jamais le nom d'un autre poste, ni une réponse d'un serveur qui ne connaît pas ce poste), nom non vide et différent du nom local.
    /// </summary>
    public static string? NouveauNom(long? posteIdLocal, string? nomLocal, EtatPosteAutoriseDto? etat)
    {
        if (posteIdLocal is not { } id || etat is not { Prouve: true, PosteId: { } prouve } || prouve != id) return null;
        var nom = etat.Nom?.Trim();
        return string.IsNullOrEmpty(nom) || string.Equals(nom, nomLocal, StringComparison.Ordinal) ? null : nom;
    }
}
