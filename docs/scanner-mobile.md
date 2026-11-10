# Scanner mobile

Le préparateur scanne en rayon avec un téléphone ou un terminal Android à laser. Il n'y a rien à installer depuis un store : c'est une page web servie par l'API du PC serveur, sur le Wi-Fi de la pharmacie, à l'adresse `https://<adresse du PC>:5443/mobile/`.

## Usages

| Onglet | Ce qu'on fait | Droit |
| --- | --- | --- |
| **Contrôle** | Scanner une boîte : stock, prix, rayon et réserve, lots avec péremption et niveau d'alerte (seuils de l'Administration). Alertes : lot bloqué (rappel), périmé, boîte déjà vendue, date à vérifier. Confirmer la date d'un lot « date à vérifier » (reprise). Ranger le produit (rayon de vente et réserve). | `stock.consulter` ; `stock.ajuster` pour confirmer une date et ranger |
| **Inventaire** | Inventaire tournant d'un rayon, à l'aveugle : scan de chaque boîte (étiquette interne ou Data Matrix) ou saisie du nombre, date lue pour les lots à vérifier, puis « Terminer ». Les écarts suivent les mêmes règles que sur le poste. | `stock.ajuster` |
| **Réception** | Scanner chaque boîte au déballage. Data Matrix : produit, lot, péremption et série sont lus ; une même boîte n'est acceptée qu'une fois. Boîte sans Data Matrix : le code-barres du fabricant donne le produit. Sur le poste, Réceptions, « Importer les scans du mobile » remplit les lignes ; on complète le prix (et le lot pour les boîtes sans Data Matrix), puis on valide sur le poste. Retirer une ligne importée ou abandonner la réception remet ses boîtes en attente sur le mobile. | `achats.receptionner` |

Le code-barres du fabricant ne désigne pas un lot : en Contrôle, la page signale alors tous les lots du produit à retirer (bloqués ou périmés) ou à dater.

## Appareils

| Appareil | Lecture |
| --- | --- |
| Téléphone Android ou iPhone, tout navigateur récent | Caméra (bouton « Caméra ») ou douchette Bluetooth appairée |
| Terminal Android à laser intégré (Zebra, Honeywell, Urovo…) | Laser : il tape le code dans le champ de scan, suivi de Entrée (réglage « clavier » ou « keystroke » du terminal) |

La caméra utilise le détecteur de codes du navigateur quand il lit les Data Matrix (Chrome sur Android) ; sinon (iPhone, Firefox…) la page charge la bibliothèque open source **ZXing** (`@zxing/library` 0.23.0, licence Apache 2.0), servie par le serveur de la pharmacie, sans internet : `src/Pharma.Api/wwwroot/mobile/lib/zxing-0.23.0/` (fichier, licence et provenance). Seule la zone du viseur est analysée.

Les Data Matrix contiennent un séparateur invisible (GS) entre le lot et la série. La caméra le transmet ; pour une douchette ou un laser, il faut activer la transmission des caractères de contrôle GS1 dans son réglage.

## Installation (une fois, sur le PC serveur)

1. En administrateur, lancer :
   ```powershell
   .\scripts\certificat_mobile.ps1 -DossierApi "<dossier de l'API>" -Adresses <adresse IP du PC> -PareFeu
   ```
   Le script crée une autorité de certification propre à la pharmacie (clé non exportable, magasin Windows de la machine), un certificat pour l'adresse du PC, le fichier `appsettings.Https.json` (http sur 5080, https sur 5443, aucun secret), publie le certificat **public** de l'autorité dans la page mobile et ouvre le port 5443 sur le réseau **privé** uniquement.
2. Redémarrer le service « PharmaOfficine API ».
3. Donner une **adresse IP fixe** au PC serveur dans la box ; sinon relancer le script quand elle change.

## Installation (une fois, sur chaque téléphone)

1. Ouvrir `http://<adresse du PC>:5080/mobile/autorite-pharmacie.crt` et installer le certificat :
   - Android : Paramètres, Sécurité, Chiffrement et identifiants, Installer un certificat, Certificat CA ;
   - iPhone : installer le profil téléchargé, puis Réglages, Général, Informations, Réglages des certificats : activer la confiance totale.
2. Ouvrir `https://<adresse du PC>:5443/mobile/` et l'ajouter à l'écran d'accueil.

## Sécurité

- Connexion avec le compte de l'employé ; chaque action est revérifiée par le serveur avec ses droits. Le jeton est gardé pour l'onglet seulement (`sessionStorage`) et disparaît à sa fermeture.
- La page n'affiche aucune donnée de patient. Le motif d'un blocage de lot, saisi librement, peut citer un patient : il n'est jamais envoyé au téléphone (seulement « bloqué (rappel) »). Un test le vérifie.
- Le port https n'est ouvert que sur le réseau privé ; rien n'est exposé sur internet.
- Les données affichées sont insérées comme du texte, jamais comme du HTML.

## Limites connues

- À vérifier avec le vrai matériel : réglage GS1 des douchettes et terminaux, qualité de lecture des petits Data Matrix par la caméra selon les téléphones. La lecture par ZXing a été testée avec une caméra simulée (Data Matrix GS1 lu et rapproché du lot), pas encore sur un vrai iPhone.
