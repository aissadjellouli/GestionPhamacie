using System.Net;
using Microsoft.AspNetCore.HttpOverrides;

namespace Pharma.Api.Securite;

/// <summary>
/// En-têtes X-Forwarded-For et X-Forwarded-Proto d'un proxy inverse (hébergement derrière Nginx, Caddy…) : pris en compte
/// seulement s'ils viennent d'un proxy déclaré dans le réglage ProxysConnus (adresses IP ou réseaux CIDR, ex. « 10.0.0.2 »,
/// « 172.18.0.0/16 »). Sans réglage : désactivé, comportement inchangé (adresse du client = adresse de la connexion).
/// L'adresse ainsi rétablie sert à la limite de débit des connexions et aux contrôles « poste local ».
/// </summary>
public static class ProxysInverses
{
    public const string Reglage = "ProxysConnus";

    /// <summary>Options à appliquer, ou null si aucun proxy n'est déclaré. Une valeur illisible arrête le démarrage (message clair).</summary>
    public static ForwardedHeadersOptions? Options(IConfiguration config)
    {
        var valeurs = Valeurs(config);
        if (valeurs.Count == 0) return null;
        var options = new ForwardedHeadersOptions { ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto };
        // Seuls les proxys déclarés : les valeurs par défaut (boucle locale) sont retirées
        options.KnownProxies.Clear();
        options.KnownIPNetworks.Clear();
        foreach (var v in valeurs)
        {
            if (v.Contains('/') && System.Net.IPNetwork.TryParse(v, out var reseau)) options.KnownIPNetworks.Add(reseau);
            else if (!v.Contains('/') && IPAddress.TryParse(v, out var ip)) options.KnownProxies.Add(ip);
            else throw new InvalidOperationException($"Réglage {Reglage} : « {v} » n'est ni une adresse IP ni un réseau CIDR.");
        }
        return options;
    }

    /// <summary>Liste (ProxysConnus:0, ProxysConnus:1… ou variables ProxysConnus__0…) ou valeur unique séparée par « ; » ou « , ».</summary>
    private static List<string> Valeurs(IConfiguration config)
    {
        var section = config.GetSection(Reglage);
        var brutes = section.GetChildren().Select(c => c.Value).OfType<string>().ToList();
        if (section.Value is { } unique) brutes.Add(unique);
        return brutes.SelectMany(b => b.Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).Distinct().ToList();
    }
}
