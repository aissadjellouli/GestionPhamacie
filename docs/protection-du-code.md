# Protection du code livré en officine (étude du 2026-11-27)

> Demande du titulaire (2026-11-27) : le logiciel livré ne doit être ni lisible ni copiable par un tiers. **Étude seulement, aucun code**
> (« étude P2 ») ; lots et décisions dans [reste-a-faire-synthese.md](reste-a-faire-synthese.md). Recherches documentaires faites le
> 2026-11-27 : chaque fait sur un outil est daté et sourcé ; « non vérifié » signifie qu'aucune source fiable ne l'a confirmé.

## 1. Objectif réaliste

Aucune protection n'est absolue contre un administrateur du serveur de l'officine. Le but est que **lire, comprendre ou reprendre le
logiciel coûte plus cher que de l'écrire**, et que l'éditeur **puisse agir en justice** (volet juridique, § 7).

## 2. Ce qui est livré et lisible (constaté dans `scripts/creer_paquet_essai.ps1`)

| Livré | Lisible aujourd'hui | Risque |
|---|---|---|
| Nos `.dll` .NET (API, poste, règles) | Oui : un décompilateur gratuit restitue un C# presque identique | Élevé |
| Fichiers `.pdb` (symboles) | Oui ; ils aident la décompilation | Inutiles en officine |
| Scripts SQL 001 à 174, **avec commentaires** | Oui, en clair | Élevé (schéma, fonctions, règles expliquées) |
| Base installée (tables, fonctions, déclencheurs) | Oui, pour qui a accès à PostgreSQL sur le serveur | Moyen ; ne peut pas être caché au propriétaire de la base |
| Écrans (CSS, images) | Oui | Faible |
| Serveur central, outil de l'éditeur | Non livrés | Aucun |

Déjà protégé : aucun secret dans le programme (seulement les clés publiques de l'éditeur) ; clés des serveurs et des postes dans le TPM ;
mots de passe en configuration et en base.

## 3. Signature des fichiers (rappel)

- **Signature Authenticode** (certificat de signature de code acheté à une autorité, au nom de la société, clé sur support matériel,
  `signtool.exe` gratuit du SDK Windows, avec horodatage) : prouve l'éditeur, détecte un fichier modifié ; **ne protège pas de la lecture**.
  Aucune édition de Visual Studio ne fournit ce certificat ; Visual Studio peut seulement appeler `signtool` à la compilation ou à la
  publication. Un certificat « auto-signé » de test n'est pas reconnu par Windows.
- **Signature de nom fort** (onglet « Signature » d'un projet, fichier `.snk`) : identité technique de l'assembly, **non vérifiée par
  .NET depuis .NET Core**, n'affiche pas l'éditeur : **sans intérêt** pour ce besoin.
- Ordre de fabrication du paquet : `dotnet publish` → **obfuscation** → signature Authenticode (la signature scelle le fichier final).

## 4. Ce que l'obfuscation ne doit pas renommer dans notre code (constaté le 2026-11-27)

| Élément | Quantité | Pourquoi |
|---|---|---|
| Paramètres des composants d'écran (`[Parameter]`, `[CascadingParameter]`) | 264 | Blazor les remplit par leur nom |
| Services injectés dans les écrans (`@inject`) | 354 | Liés par nom de propriété |
| Méthodes appelées depuis JavaScript (`[JSInvokable]`) | 4 | Appelées par leur nom |
| Objets échangés en JSON (poste ↔ API ↔ central) | 80 fichiers ; aucune génération de source JSON | Les noms des propriétés sont le format d'échange |
| JSON enregistré en base ou en fichier (écarts de réception, profils de reprise, état intermédiaire de caisse, ventes en attente, date plancher, copie de secours) | ~10 endroits, dont 2 types anonymes | Données déjà enregistrées illisibles après mise à jour |
| Réglages lus depuis la configuration (`GetSection`, `Bind`) | 7 | Liés aux noms des propriétés |
| Lecture d'ordonnance (`dynamic`, composant Windows) | 1 | Appel par nom |

Sans risque : contenu des codes d'activation (noms JSON fixés par `JsonPropertyName`, signatures inchangées) ; lecture SQL (713 lectures
par position, 93 par nom de **colonne**, chaînes SQL non touchées) ; aucun chargement dynamique d'assembly ni `Reflection.Emit` ; pas d'EF Core.

**Réglage retenu pour le premier essai** : brouillage du flux de contrôle et chiffrement des chaînes **partout** ; renommage **seulement du
privé et de l'interne**, en excluant les types échangés ou enregistrés en JSON (dont `Pharma.Shared`) et les composants d'écran. Les
projets non livrés (serveur central, outil de l'éditeur, tests) ne sont pas obfusqués ; seuls **nos** assemblys le sont, jamais ceux de .NET.

## 5. Comparaison des outils (recherche du 2026-11-27)

| Outil | .NET 10 | Blazor / WPF | Ligne de commande | Techniques | Prix / licence | Désobfuscateur public | Verdict |
|---|---|---|---|---|---|---|---|
| **Obfuscar** (gratuit, MIT) | Outil global .NET 10 « en préversion » depuis 2.2.42 ; stable 2.2.50 (2025-12-15) | Non mentionnés | Oui (configuration XML) | Renommage, masquage des chaînes ; **pas** de flux de contrôle ni d'anti-modification | Gratuit | Oui : de4dot annule le renommage | Ralentisseur seulement |
| **ConfuserEx** et dérivés (gratuit) | **Non** (jusqu'à .NET Core 2.2) | — | Oui | Complet mais ancien | Gratuit | Oui, nombreux | **À écarter** |
| **.NET Reactor** | **Oui** : 7.5.0.0 (2025-11-12) | **Blazor : oui** (annoncé) ; WPF non nommé | Oui (générateur de ligne de commande, intégration continue) | NecroBit (corps de méthodes chiffrés), virtualisation, anti-modification, chiffrement | **199 USD** (1 développeur nommé, 2 machines) ou 349 USD (toute la société, machines illimitées, une seule entité juridique), **perpétuel**, 1 an de mises à jour puis maintenance annuelle facultative ; **nombre de logiciels protégés illimité, sans redevance** (contrat de licence lu le 2026-11-27) | Aucun universel confirmé (non vérifié) | **Meilleur rapport coût / effet** |
| **Dotfuscator Pro** | Oui : 7.5.0 (2025-12-22) ; 7.8.0 (2026-07-28) | Blazor WebAssembly / WebApp nommés ; **Hybrid non nommé** ; bureau : oui | Oui (ligne de commande, MSBuild) | Renommage, chaînes, flux de contrôle, protection à l'exécution (anti-modification, anti-débogage) | **Sur devis** (non public) | Non vérifié | Haut de gamme, coût inconnu |
| **Babel Obfuscator 12** | Oui | Non nommés | Oui (ligne de commande, MSBuild) | Renommage, chaînes, flux de contrôle, machine virtuelle pour les corps de méthodes, anti-modification | **Non public** (devis) | Non vérifié | Alternative à .NET Reactor |
| **Eazfuscator.NET** | **Non vérifié** (.NET 9 préliminaire en 2024) | Non mentionnés | Non vérifié | Virtualisation, autres non vérifiées | ~399 USD (source secondaire, non vérifié) | Non vérifié | Incertain |
| **ArmDot** | Annoncé, **non vérifié** | Non confirmés | Oui (ligne de commande, MSBuild) | Flux de contrôle, virtualisation | 499 USD (1 développeur) | Non vérifié | À tester avant tout achat |
| **SmartAssembly** | **Non vérifié** | Non confirmés | Historiquement oui | Renommage, chaînes, flux de contrôle | Par serveur et développeur, dès ~710 USD/an | Non vérifié | Incertain |

Sources principales : eziriz.com (history.htm, dotnet_reactor.htm, order.htm), preemptive.com (blog 7.5.0 et 7.8.0, pricing),
babelfor.net, github.com/obfuscar/obfuscar, github.com/mkaring/ConfuserEx, github.com/de4dot/de4dot, softanics.com/armdot,
gapotchenko.com, red-gate.com.

**Points communs à tous les outils** :
- **Traces d'erreurs** illisibles après renommage : garder, pour chaque version livrée, le **fichier de correspondance** (hors paquet, comme
  une clé) pour décoder une erreur remontée d'une officine.
- **Performances** : la virtualisation et le chiffrement des corps de méthodes coûtent du temps d'exécution : les réserver aux parties
  sensibles (contrôle de l'offre, preuve des postes, règles fiscales), jamais à la caisse entière sans mesure.
- **Anti-débogage** : peut gêner un diagnostic sur site ; garder une version interne non protégée.

## 6. Compilation native de l'API (NativeAOT), alternative pour l'API seulement

- **Possible en principe** (documentation Microsoft) : les routes simples (minimal APIs, notre cas : 412 routes, aucun contrôleur) sont
  prises en charge ; l'injection de dépendances aussi ; **Npgsql est compatible depuis la version 8** (sans EF Core, notre cas).
- **Travail important** : tout le JSON doit passer par la **génération de source** (contexte JSON listant tous les objets échangés) ; aucune
  réflexion libre.
- **Gain** : code machine sans métadonnées .NET ; les décompilateurs C# ne s'appliquent plus ; nettement plus dur à lire qu'un `.dll`
  obfusqué. Les noms des objets JSON restent visibles.
- **Impossible pour le poste** (WPF + WebView2). Stratégie cohérente à terme : **NativeAOT pour l'API + obfuscation pour le poste et les
  bibliothèques communes**. À étudier après P2 (lot P4).

## 6 bis. Zipper, hacher ou chiffrer les scripts livrés : ce que cela protège (question du titulaire, 2026-11-27)

Idée examinée : mettre les scripts (`.sql`, `.ps1`) dans une archive avec une empreinte, que le logiciel rouvrirait à l'installation.

- **Un hash (empreinte) protège l'intégrité, pas le secret.** Il sert à détecter une modification ; le fichier haché reste lisible. Pour
  empêcher la lecture, il faudrait **chiffrer**, pas hacher.
- **Le logiciel qui rouvre l'archive contient la clé.** Le programme est livré sur le serveur de l'officine : qui décompile nos `.dll` y
  trouve la clé de déchiffrement et rouvre l'archive. Un chiffrement des scripts **n'a donc de sens qu'après l'obfuscation des `.dll` (P2)** ;
  sans elle, c'est un simple ralentisseur.
- **Deux limites subsistent même après P2** : (1) PostgreSQL exige du SQL **en clair** pour l'exécuter — à l'installation, les scripts
  déchiffrés passent en clair vers la base, captables par un administrateur du serveur (journal des requêtes, interception) ; (2) une fois
  installées, les **fonctions SQL se lisent dans la base** par qui a accès à PostgreSQL : chiffrer les fichiers ne cache pas la base.
- **Gain réel** : un paquet **signé** (lot P3, § 3) vaut mieux qu'un simple hash — le logiciel **refuse d'installer un paquet modifié** et
  détecte tout remplacement de script. Le chiffrement des scripts, après P2, gêne la lecture sans la rendre impossible.
- **Seul vrai gain sur le fond** : déplacer en **C#** (obfusqué) les règles SQL qui ont de la valeur, au lieu de les laisser en clair dans un
  `.sql`. Chantier à cibler sur les règles sensibles, après inventaire (lot P5 éventuel).
- **Ordre d'efficacité** : signature du paquet (P3) > obfuscation des `.dll` (P2) > chiffrement des scripts (après P2) > réécriture en C# des
  règles sensibles. Le zip + hash seul **n'empêche ni la lecture ni la copie**.

## 7. Volet juridique (titulaire, hors code)

Dépôt du logiciel pour dater la création (droit d'auteur ; organisme et démarche **à confirmer avec un juriste**) ; contrat de licence signé
par chaque officine (interdiction de copier, décompiler, céder) ; historique du dépôt et codes d'activation signés comme preuves
d'antériorité et d'usage.

## 8. Recommandation et suite

1. **Lot P1 — fait le 2026-11-27** (« go P1, script nettoyer à la livraison, pas dans git ») : `scripts\mep\creer_livraison.ps1` publie
   l'API et le poste sans symboles de débogage puis lance `scripts\mep\nettoyer_livraison.ps1` (aussi appelé par
   `creer_paquet_essai.ps1`) : plus de `.pdb` ni de `appsettings.Development.json`, scripts SQL **sans commentaires** (analyse du SQL comme
   PostgreSQL : textes, `E'…'`, commandes psql, corps `$…$` et en-têtes « 001 à NNN » respectés ; `NettoyageSql.cs`), contrôles (aucun
   `.pdb`, nettoyage stable, en-têtes présents, aucun fichier de clé). Le dépôt garde tout. Essai réel (Windows PowerShell 5.1) : 178 scripts
   nettoyés, 26 % plus légers. Tests : `NettoyageLivraisonTests` (cas limites, tous les scripts livrés, et **base créée puis mise à jour avec
   les scripts nettoyés identique** à celle des scripts du dépôt : structure et données). Les `COMMENT ON` (descriptions stockées dans la
   base) restent : ce sont des instructions, pas des commentaires.
2. **Lot P2, essai** avec **.NET Reactor** (version d'essai d'abord, puis licence perpétuelle à 199 USD si l'essai est concluant) :
   obfusquer une copie du paquet avec le réglage du § 4 ; lancer tous les tests sur le paquet obfusqué ; essai réel complet (caisse, écrans,
   synchronisation avec le central, codes d'activation, données JSON déjà enregistrées) ; mesurer les temps de la caisse. Repli gratuit :
   Obfuscar (protection plus faible). Le résultat dira si Blazor Hybrid fonctionne obfusqué (aucun éditeur ne le confirme).
3. **Lot P3** dès que le certificat est disponible : signature après obfuscation, et contrôle des signatures au démarrage de l'API.
4. **Lot P4** (étude) : NativeAOT de l'API.

**Décisions attendues du titulaire** : « go P1 » ; accord pour l'essai de .NET Reactor (version d'essai, puis achat éventuel) ou choix d'un
autre outil ; démarche juridique.
