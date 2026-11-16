# Accès du titulaire hors de la pharmacie (VPN)

Le titulaire suit sa pharmacie depuis son téléphone, onglet **Pilotage** de la page mobile :
- chiffre du jour et comparaison avec la veille ;
- caisses ouvertes, espèces attendues, clôtures et écarts du jour ;
- alertes ;
- soldes de trésorerie et factures à encaisser ;
- **validation ou refus des gros écarts d'inventaire**.

Hors de la pharmacie, le téléphone passe par un **VPN**. Le serveur n'est jamais publié sur internet : aucun port du routeur n'est ouvert vers l'API, et les données de santé restent sur le réseau de la pharmacie.

## Principe

```
Téléphone du titulaire ──(tunnel VPN chiffré)──► PC serveur de la pharmacie ──► https://<adresse VPN du serveur>:5443/mobile/
```

Seuls les appareils inscrits au VPN (le téléphone du titulaire) joignent le serveur. L'accès à la page demande en plus l'identifiant et le mot de passe du titulaire. L'onglet Pilotage n'apparaît qu'au titulaire.

## Où installer le VPN

- **Côté pharmacie** : le serveur VPN, sur la box ou le routeur s'il le permet, sinon sur le PC serveur.
- **Côté extérieur** : l'application VPN sur le téléphone (ou le PC portable) du titulaire seulement.
- **Jamais sur les postes de caisse** : ils sont dans la pharmacie et joignent le serveur par le réseau local. Un client VPN sur une
  caisse n'apporte rien et ouvre une porte de plus vers le PC qui manipule l'argent et les données de santé.
- Plusieurs pharmacies reliées à un même serveur (cas non prévu à ce jour) : relier les routeurs des sites entre eux (VPN « site à
  site »), pas les postes un par un.

## Solutions possibles (à choisir avec le prestataire informatique)

| Solution | Installation | Coût et conditions | À savoir |
| --- | --- | --- | --- |
| **WireGuard** (logiciel libre) — **recommandé** | Serveur WireGuard sur le routeur, s'il le permet, ou sur le PC serveur ; application WireGuard sur le téléphone | Gratuit, usage commercial autorisé | Ouvre un seul port UDP vers le serveur VPN, pas vers l'API. Il faut une adresse IP fixe ou un nom dynamique (DDNS) pour la box. Aucune donnée ne passe par un service tiers. |
| **VPN du routeur** (WireGuard, OpenVPN, IPsec selon le modèle) | Réglage dans l'interface de la box ou du routeur | Inclus dans le matériel | Dépend du modèle ; souvent le plus simple si la box le propose. |
| **OpenVPN** (édition communautaire) | Serveur sur le routeur ou le PC serveur ; application sur le téléphone | Gratuit, usage commercial autorisé | Plus lourd à régler que WireGuard. |
| **Tailscale** (service basé sur WireGuard) | Application Tailscale sur le PC serveur et sur le téléphone, même compte | **Le plan gratuit est réservé à un usage non commercial** : une pharmacie doit prendre un plan payant (par utilisateur et par mois) | Aucun port à ouvrir. Le service gère les clés et les accès ; activer la double authentification du compte. |

Conditions et tarifs relevés en novembre 2026 sur des sources secondaires et la documentation de Tailscale : les vérifier sur les
pages officielles au moment du choix. D'autres services du même genre (NetBird, ZeroTier) existent ; leurs conditions d'usage
commercial sont à vérifier de la même façon.

Rien n'est installé par le logiciel : ces réglages relèvent du prestataire informatique de la pharmacie.

## Étapes, une fois le VPN en place

1. Le certificat HTTPS de la page mobile doit aussi couvrir l'adresse VPN du serveur. Relancer le script en administrateur en ajoutant l'adresse VPN (les noms et adresses par défaut du PC sont gardés ; ne renseigner que l'adresse réellement utilisée, rien n'est deviné) :
   ```powershell
   .\scripts\certificat_https.ps1 -DossierApi "<dossier de l'API>" -DossierExport <clé USB> -AdressesSupplementaires <adresse VPN du serveur> -PareFeu
   ```
2. Installer le certificat racine sur le téléphone du titulaire, comme pour le scanner mobile : voir `scanner-mobile.md`.
3. Sur le téléphone, activer le VPN, puis ouvrir `https://<adresse VPN du serveur>:5443/mobile/`. Se connecter avec le compte du titulaire, puis « Ajouter à l'écran d'accueil ».

## Sécurité

- Un mot de passe solide pour le compte titulaire. La session mobile se ferme à la fermeture de l'onglet.
- Si le téléphone est perdu : retirer l'appareil du VPN (console Tailscale ou clé WireGuard supprimée) et changer le mot de passe du titulaire.
- Ne jamais rediriger les ports de l'API (5443 en https, 5080 en http) du routeur vers le serveur.
