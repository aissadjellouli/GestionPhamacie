using System.Text.Json;
using Pharma.Domain.Reglages;

namespace Pharma.Domain.Tests;

/// <summary>
/// Reste du lot S7.3 : message du titulaire à distance affiché sur les postes (message-v1). Mêmes règles que la base centrale
/// (cloud_message_poste_controle, script central 038) : texte simple de 1 à 200 caractères sur une ligne, ni balise, ni caractère
/// invisible, ni lien ; fin d'affichage 1 seconde à 7 jours après l'envoi, en UTC.
/// </summary>
public class MessagePostesTests
{
    private static readonly DateTime Cree = new(2026, 11, 21, 10, 0, 0, DateTimeKind.Utc);

    private static string Json(string texte, string fin, string format = "message-v1") =>
        JsonSerializer.Serialize(new Dictionary<string, string> { ["format"] = format, ["texte"] = texte, ["jusqu_au"] = fin });

    [Theory]
    [InlineData("Fermeture à 18 h samedi", null)]
    [InlineData("Livraison vers 10 h. Merci !", null)]
    [InlineData("   ", "vide")]
    [InlineData("<b>Urgent</b>", "texte")]
    [InlineData("Ligne 1\nLigne 2", "texte")]
    [InlineData("Caché‮txt", "texte")]
    [InlineData("Espace​invisible", "texte")]
    [InlineData("Voir https://exemple.invalid", "lien")]
    [InlineData("Voir WWW.exemple", "lien")]
    [InlineData("Écrire à contact@pharma.dz demain", "lien")]
    [InlineData("javascript:alert(1)", "lien")]
    [InlineData("site exemple.com", "lien")]
    public void Texte_simple_sans_lien(string texte, string? code) => Assert.Equal(code, MessagePostes.ControlerTexte(texte));

    [Fact]
    public void Deux_cents_caracteres_au_plus_comptes_comme_la_base()
    {
        Assert.Null(MessagePostes.ControlerTexte(new string('é', 200)));
        Assert.Equal("trop_long", MessagePostes.ControlerTexte(new string('é', 201)));
        // Un caractère hors du plan de base compte pour un (comme length() de PostgreSQL), pas pour deux
        Assert.Null(MessagePostes.ControlerTexte(string.Concat(Enumerable.Repeat("😀", 200))));
        Assert.Equal("trop_long", MessagePostes.ControlerTexte(string.Concat(Enumerable.Repeat("😀", 201))));
    }

    [Fact]
    public void Lecture_des_donnees_format_et_duree()
    {
        var (texte, fin, code) = MessagePostes.Lire(Json("Inventaire lundi", "2026-11-24T10:00:00Z"), Cree);
        Assert.Null(code);
        Assert.Equal("Inventaire lundi", texte);
        Assert.Equal(new DateTime(2026, 11, 24, 10, 0, 0, DateTimeKind.Utc), fin);
        Assert.Equal(DateTimeKind.Utc, fin!.Value.Kind);
        Assert.Null(MessagePostes.Lire(Json("x", "2026-11-28T10:00:00Z"), Cree).Code);                // 7 jours tout juste
        Assert.Null(MessagePostes.Lire(Json("x", "2026-11-24T11:00:00+01:00"), Cree).Code);           // décalage explicite
        Assert.Equal("duree", MessagePostes.Lire(Json("x", "2026-11-28T10:00:01Z"), Cree).Code);
        Assert.Equal("duree", MessagePostes.Lire(Json("x", "2026-11-21T10:00:00Z"), Cree).Code);
        Assert.Equal("format", MessagePostes.Lire(Json("x", "2026-11-24 10:00:00"), Cree).Code);      // heure ambiguë
        Assert.Equal("format", MessagePostes.Lire(Json("x", "2026-13-24T10:00:00Z"), Cree).Code);
        Assert.Equal("format", MessagePostes.Lire(Json("x", "2026-11-24T10:00:00Z", "message-v2"), Cree).Code);
        Assert.Equal("format", MessagePostes.Lire("""{"format":"message-v1","texte":"x","jusqu_au":"2026-11-24T10:00:00Z","lien":"y"}""", Cree).Code);
        Assert.Equal("format", MessagePostes.Lire("""{"format":"message-v1","texte":12,"jusqu_au":"2026-11-24T10:00:00Z"}""", Cree).Code);
        Assert.Equal("format", MessagePostes.Lire("pas du json", Cree).Code);
        Assert.Equal("texte", MessagePostes.Lire(Json(" non rogné", "2026-11-24T10:00:00Z"), Cree).Code);
        Assert.Equal("lien", MessagePostes.Lire(Json("www.exemple", "2026-11-24T10:00:00Z"), Cree).Code);
        Assert.True(MessagePostes.JoursValides(1) && MessagePostes.JoursValides(7));
        Assert.False(MessagePostes.JoursValides(0) || MessagePostes.JoursValides(8));
        Assert.All(new[] { "format", "vide", "trop_long", "texte", "lien", "duree" }, c => Assert.True(MessagePostes.Refus.ContainsKey(c)));
    }
}
