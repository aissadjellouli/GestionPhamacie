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

## Solutions possibles (à choisir avec le prestataire informatique)

| Solution | Installation | À savoir |
| --- | --- | --- |
| **WireGuard** (logiciel libre) | Serveur WireGuard sur le routeur, s'il le permet, ou sur le PC serveur ; application WireGuard sur le téléphone | Ouvre un seul port UDP vers le serveur VPN, pas vers l'API. Il faut une adresse IP fixe ou un nom dynamique (DDNS) pour la box. |
| **Tailscale** (service basé sur WireGuard) | Application Tailscale sur le PC serveur et sur le téléphone, même compte | Aucun port à ouvrir. Le service gère les clés ; vérifier ses conditions d'utilisation et activer la double authentification du compte. |
| **VPN du routeur** (OpenVPN, IPsec selon le modèle) | Réglage dans l'interface de la box ou du routeur | Dépend du matériel. |

Rien n'est installé par le logiciel : ces réglages relèvent du prestataire informatique de la pharmacie.

## Étapes, une fois le VPN en place

1. Le certificat HTTPS de la page mobile doit aussi couvrir l'adresse VPN du serveur. Relancer le script en administrateur, avec les deux adresses :
   ```powershell
   .\scripts\certificat_mobile.ps1 -DossierApi "<dossier de l'API>" -Adresses <adresse IP locale>,<adresse VPN du serveur> -PareFeu
   ```
2. Installer le certificat racine sur le téléphone du titulaire, comme pour le scanner mobile : voir `scanner-mobile.md`.
3. Sur le téléphone, activer le VPN, puis ouvrir `https://<adresse VPN du serveur>:5443/mobile/`. Se connecter avec le compte du titulaire, puis « Ajouter à l'écran d'accueil ».

## Sécurité

- Un mot de passe solide pour le compte titulaire. La session mobile se ferme à la fermeture de l'onglet.
- Si le téléphone est perdu : retirer l'appareil du VPN (console Tailscale ou clé WireGuard supprimée) et changer le mot de passe du titulaire.
- Ne jamais rediriger le port 5443 (ou 5081) du routeur vers le serveur.
