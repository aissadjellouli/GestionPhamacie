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

### 6.1 Étude P4 (2026-11-27, « go étude P4 », aucun code)

Méthode : lecture des projets, comptage dans les sources de l'API (`Pharma.Api`, `Pharma.Application`, `Pharma.Domain`,
`Pharma.Infrastructure`, `Pharma.Shared` : 521 fichiers), lecture de la marque de compatibilité inscrite dans chaque bibliothèque publiée,
et **essai de publication native** de l'API dans un dossier temporaire (`dotnet publish -p:PublishAot=true`, dépôt non modifié).

**Ce que NativeAOT protégerait.** Le poste ne livre que `Pharma.Desktop.dll` et `Pharma.Shared.dll` ; les règles (`Pharma.Domain`,
`Pharma.Application`, `Pharma.Infrastructure`, `Pharma.Api` : 9,7 Mo de `.dll` sur 11,4 Mo côté serveur) ne sont livrées **qu'avec l'API**.
Compilées en code machine, elles ne seraient plus lisibles par un décompilateur C#. Resteraient en `.dll` : le poste et `Pharma.Shared`
(objets échangés, pas les règles) : c'est le périmètre de l'obfuscation (P2).

**Résultat de l'essai de publication native (constaté).**
- Arrêt à la dernière étape : « Platform linker not found » : **les outils C++ de Visual Studio ne sont pas installés** sur ce PC
  (charge « Développement Desktop en C++ »). Les avertissements de nos sources sont produits avant cet arrêt ; ceux des bibliothèques
  (analyse du compilateur natif) **ne le sont pas** : ils restent à relever une fois les outils installés.
- **69 appels à corriger dans 35 fichiers**, tous de la même famille (JSON par réflexion) : 29 `JsonSerializer.Serialize`,
  15 `Deserialize`, 7 `Results.Json`, 6 `WriteAsJsonAsync`, 3 `ReadFromJsonAsync`, 3 `JsonArray.Add`, 2 `JsonContent.Create`,
  2 `SerializeToUtf8Bytes`, 1 `PostAsJsonAsync`, 1 `SerializeToNode` (36 dans Infrastructure, 21 dans l'API, 8 dans Application, 4 dans Shared).
- 1 création de tâche de fond par type (`TacheSuspendueSiEcarte.cs`, ligne 38) à annoter.
- **Aucun avertissement** pour la lecture de la configuration (5 lectures par `Get<T>` / `Configure<T>` : prises en charge par le
  générateur) ni pour les 488 déclarations de routes. **Attention** : l'absence d'avertissement sur les routes ne prouve rien pour leur
  JSON ; un objet de requête ou de réponse non déclaré échoue **à l'exécution**, pas à la compilation.

**Comptages dans les sources (constatés).** Aucune réflexion libre (`Activator`, `GetProperties`, `MakeGenericType`, chargement
d'assembly : 0), aucun `dynamic`, aucune sérialisation XML, aucun appel natif direct, aucune option JSON globale, aucun contexte JSON
généré (0 `JsonSerializerContext`) ; 8 types anonymes renvoyés ou sérialisés (6 dans des routes, 2 ailleurs) à remplacer par des types
nommés. Types candidats au contexte JSON : **plusieurs centaines** (614 classes ou enregistrements publics dans `Pharma.Shared`, 593
enregistrements dans l'API et Application ; le nombre réellement échangé n'est pas mesuré).

**Bibliothèques : marque de compatibilité lue dans l'assembly publié.** Une bibliothèque non marquée n'est pas forcément incompatible :
elle n'est simplement **pas garantie** par son éditeur ; seul un essai le dira.

| Bibliothèque (version publiée) | Marquée compatible AOT | Usage chez nous | Risque |
|---|---|---|---|
| Npgsql 10.0.0 | Oui | Toute la base | Faible |
| MailKit 4.18.2, MimeKit 4.18.1 | Oui | E-mails | Faible |
| BouncyCastle.Cryptography 2.7.0 (amené par MimeKit) | Non | Indirect | À essayer |
| PdfSharp 6.2.4 | Oui | PDF | Faible |
| **MigraDoc 6.2.4** (modèle et rendu), PdfSharp.Charting | **Non** | **Tous les PDF** (tickets, factures, rapports) | **Élevé** : point à essayer en premier |
| BCrypt.Net-Next 4.0.3 | Non | Mots de passe | À essayer (calcul pur) |
| JwtBearer 10.0.0, Microsoft.IdentityModel 8.0.1 | Non (découpage seulement) | Jetons de connexion | À essayer ; la documentation Microsoft classe l'authentification JWT « prise en charge » |
| **System.Data.Odbc 10.0.13** | **Non** | Reprise de données (3 fichiers) | **Élevé** : à sortir de l'API ou à remplacer |
| System.IO.Ports 10.0.13 | Oui | Modem GSM | Faible |
| ProtectedData 10.0.13 | Oui | Secrets locaux (DPAPI) | Faible |
| Hébergement en service Windows 10.0.0 | Oui | Service de l'API | Faible |

Documentation Microsoft (lue le 2026-11-27, « ASP.NET Core support for Native AOT », .NET 10) : routes simples **partiellement** prises
en charge (tout objet de requête ou de réponse doit figurer dans un contexte JSON généré), authentification JWT prise en charge, MVC non.

**Chantier, s'il est décidé (lots proposés).**
1. **P4-0, faisabilité (court, à faire avant tout engagement)** : installer les outils C++ sur le PC de fabrication ; publier l'API en
   natif telle quelle ; relever les avertissements des bibliothèques ; lancer l'exécutable et essayer un PDF (MigraDoc), une connexion
   (jeton, BCrypt), un e-mail, la reprise ODBC. **Si MigraDoc échoue, P4 s'arrête là** ou impose de changer de bibliothèque PDF.
2. **P4-1, JSON** : contexte(s) JSON générés listant tous les objets échangés ou enregistrés, branchés sur les routes et sur les 69 appels ;
   8 types anonymes remplacés ; test d'architecture qui échoue si un objet d'une route manque au contexte. Les noms JSON ne changent pas
   (données déjà enregistrées et échanges avec le poste et le central inchangés) : à prouver par test.
3. **P4-2, bibliothèques à risque** selon P4-0 (ODBC, PDF).
4. **P4-3, recette sur l'exécutable natif** : les tests unitaires tournent sur les `.dll`, pas sur l'exécutable ; une erreur propre au
   natif n'apparaît qu'à l'exécution. Il faut rejouer la recette par l'API (routes GET, scénarios de caisse, synchronisation, PDF,
   sauvegarde) sur l'exécutable natif, sur base jetable.
5. **P4-4, fabrication** : `creer_livraison.ps1` publie l'API en natif (le poste reste en `.dll`), manuels à jour.

**Autres conséquences.** Compilation plus longue ; un exécutable par système (Windows x64 ; la démonstration sous Linux demanderait sa
propre compilation) ; traces d'erreur sans numéro de ligne (déjà le cas depuis P1) mais **noms de méthodes gardés** par défaut, donc
journaux plus lisibles qu'après obfuscation ; les commandes de console de l'API (`--autoriser-poste`, `--creer-cle-serveur`…) restent
dans le même exécutable. Les textes (messages, SQL, noms JSON) restent lisibles dans l'exécutable.

**Avis.** Le code est favorable (aucune réflexion, tout le blocage est du JSON, mécanique) ; l'incertitude est dans deux bibliothèques
(MigraDoc, ODBC). Le lot **P4-0** lève cette incertitude sans rien changer au dépôt. P4 protège les règles mieux que P2 ; P2 reste
nécessaire pour le poste et `Pharma.Shared`.

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
   **Lot P1 bis — fait le 2026-11-27** (« go P1 bis ») : la livraison porte son propre dossier `scripts\`, limité aux **17 scripts utiles
   en officine** (liste fermée `$scriptsLivres` de `creer_livraison.ps1` : certificat HTTPS, sauvegarde, restauration, PC de secours,
   `mep\` pour la base de l'officine, `second-serveur\`) ; jamais `dev\`, `paquet\`, les scripts du PC des sources ni
   `creer_base_centrale.ps1`. `nettoyer_livraison.ps1` retire les commentaires des `.ps1` par l'analyse du langage PowerShell (un « # »
   dans un texte n'est pas touché), en gardant `#Requires` et le bloc d'aide de tête (`Get-Help`), et **refuse le script si la suite de
   ses instructions change** ou si un second nettoyage le modifie ; signature UTF-8 gardée (accents sous Windows PowerShell 5.1). Les
   manuels font copier le `scripts` de la livraison, plus celui du dépôt. Essai réel (Windows PowerShell 5.1) : livraison fabriquée,
   17 scripts 10 % plus légers, aucun `.ps1` ailleurs dans la livraison, `creer_base_officine.ps1 -WhatIf` lancé depuis la copie nettoyée
   avec les scripts SQL nettoyés. Tests : `ScriptsLivraisonTests` (tout fichier de `scripts\` classé livré ou non livré, aucun script livré
   n'appelle un script non livré, nettoyage des 17 scripts). **Limites** : la logique des scripts reste lisible (comme le SQL) ; l'aide de
   tête est gardée et décrit le rôle de chaque script ; `sauvegarde_pharmacie.ps1` et `outils_mep.ps1` n'ont pas de bloc d'aide, leur
   en-tête en commentaires part donc avec le reste (le manuel de MEP reste la référence) ; signature des scripts : lot P3.
2. **Lot P2, essai** avec **.NET Reactor** (version d'essai d'abord, puis licence perpétuelle à 199 USD si l'essai est concluant) :
   obfusquer une copie du paquet avec le réglage du § 4 ; lancer tous les tests sur le paquet obfusqué ; essai réel complet (caisse, écrans,
   synchronisation avec le central, codes d'activation, données JSON déjà enregistrées) ; mesurer les temps de la caisse. Repli gratuit :
   Obfuscar (protection plus faible). Le résultat dira si Blazor Hybrid fonctionne obfusqué (aucun éditeur ne le confirme).
   **Essai P2 en cours (2026-11-27, « go P2 », version d'essai 7.7.2.0 ; rien dans le dépôt, tout dans un dossier temporaire).**
   - Outil : installateur officiel (signature « Eziriz e.K. » valide), ligne de commande `dotNET_Reactor.Console.exe`. La version d'essai
     affiche : « The protected file(s) will stop working after 14 days » : ses fichiers ne sont **jamais livrables**.
   - Nos 7 assemblys (5 de l'API, 2 du poste) obfusqués un par un, sans erreur, en 3 à 20 s chacun ; taille multipliée par 1,4 à 2 ;
     un fichier de correspondance `.nrmap` par assembly (à archiver par version, hors paquet).
   - Réglage 1 (`-obfuscation 1 -obfuscate_public_types 0 -control_flow_obfuscation 1 -stringencryption 1 -mapping_file 1`) : l'API
     démarre, la connexion fonctionne ; 115 routes GET rejouées en titulaire sur l'API obfusquée et sur l'API témoin (même base
     jetable) : **7 erreurs 500, une seule cause** : l'enregistrement privé `Contenu` de `DatePlancherFichier` (date plancher de
     l'offre, JSON en fichier), dont les propriétés sont renommées (fichier écrit `{}` puis illisible).
   - Réglage 2 (réglage 1 + `-exclude_properties 1`, propriétés jamais renommées) : **115 réponses identiques** au témoin (écarts
     restants dus au montage : dossiers, clé de scellement, lignes de journal) ; il reste **une** erreur au démarrage sur ce même
     enregistrement privé : à exclure nommément ou à fixer dans le code (noms JSON explicites) avant toute livraison obfusquée.
   - **Poste (Blazor Hybrid) obfusqué** : il démarre, la fenêtre s'ouvre et l'écran de connexion s'affiche (code d'autorisation,
     « Connexion du titulaire », « Connexion super admin »), sans bandeau d'erreur. Rien d'autre n'est encore essayé à l'écran.
   - **Journaux** : constaté : les méthodes privées sortent brouillées dans les traces (`DatePlancherFichier.cf2e1Vd0wI()`), les classes
     et méthodes publiques restent lisibles, les catégories des journaux (`Pharma.Api.Taches.EnvoiNotifications`) aussi.
   - **Reste à essayer** : écrans et parcours du poste (vente, retour, impression PDF), écritures par l'API, code d'activation,
     JSON déjà enregistré (ventes en attente, profils de reprise), synchronisation avec le central, temps de la caisse (premier
     relevé : première passe des 115 routes en 3,9 s contre 1,3 s pour le témoin, seconde passe 1,2 s contre 0,8 s ; à mesurer
     proprement), décodage d'une trace avec le fichier de correspondance, anti-modification et NecroBit (non activés).
   - **Prix relevés le 2026-11-27 sur la page de commande de l'éditeur** : 249 USD (un développeur nommé, 2 machines, serveur de
     fabrication non compris) et 549 USD (toute la société, une entité juridique, serveur de fabrication compris), hors taxes ;
     licence perpétuelle, 12 mois de mises à jour. Ces prix **diffèrent** des 199 et 349 USD notés plus haut le même jour : à
     revérifier au moment de l'achat.
3. **Lot P3** dès que le certificat est disponible : signature après obfuscation, et contrôle des signatures au démarrage de l'API.
4. **Lot P4** : étude faite le 2026-11-27 (§ 6.1) ; suite possible : **P4-0, essai de faisabilité** (outils C++ à installer), sur go.

## 9. .NET Reactor : installation, licence, activation, usage (constaté et lu le 2026-11-27)

> Ce qui est « constaté » a été fait sur le PC de développement ; ce qui est « lu » vient du site et de l'aide de l'éditeur
> (eziriz.com, aide installée avec l'outil) ; « non indiqué » : l'éditeur ne le dit pas, à lui demander avant l'achat.

### 9.1 Installation (constaté)

- Téléchargement : page `https://www.eziriz.com/reactor_download.htm`, installateur Windows `dotnet_reactor_setup_7_7_2_0.exe`
  (version 7.7.2.0 du 18 novembre 2026, 41 Mo, interface et ligne de commande). L'éditeur ne publie pas d'empreinte du fichier.
- Contrôle fait avant de lancer : signature Authenticode **valide**, signataire « Eziriz e.K. » (Allemagne), horodatage DigiCert
  (`Get-AuthenticodeSignature`). SHA-256 relevé : `170EA712EAA7DB22430E003FCAE1A013A0A3D3C19425FF23EAB9497983C1420C`.
- Installation silencieuse (installateur Inno Setup) : `/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /DIR="<dossier>"` ; elle demande
  les droits d'administrateur. Dossier retenu sur le PC de développement : `%LOCALAPPDATA%\Programs\dotNET_Reactor`.
- Programmes : `dotNET_Reactor.exe` (interface), `dotNET_Reactor.Console.exe` (ligne de commande, la seule utilisée par nos scripts),
  aide complète dans `Help\`. Désinstallation : `unins000.exe` du même dossier.
- **Version d'essai** : toutes les fonctions sont utilisables, mais l'outil affiche à chaque protection « The protected file(s) will
  stop working after 14 days ». Un fichier obfusqué par la version d'essai **ne se livre jamais**.

### 9.2 Licences et prix (lu sur la page de commande, 2026-11-27)

| Licence | Prix hors taxes | Couvre |
|---|---|---|
| Single Developer | 249 USD | un seul développeur nommé, activation sur 2 machines au plus, **serveur de fabrication non compris** |
| Company | 549 USD | toute la société (une seule entité juridique : ni filiale ni maison mère), plusieurs développeurs, **serveur de fabrication compris** |

- **Paiement en une fois, licence perpétuelle** (« Perpetual license with 1 year update support ») : pas d'abonnement. La version
  achetée et toutes celles sorties pendant les 12 mois suivants restent utilisables sans limite de durée.
- **Après 12 mois** : plus de nouvelles versions ni d'assistance par e-mail, sauf maintenance annuelle **facultative** (prix non
  indiqué). Elle ne devient utile que pour suivre une nouvelle version de .NET (l'outil prend en charge .NET 10 aujourd'hui).
- **« Serveur de fabrication »** (*build server*) : machine qui fabrique le logiciel seule, sans personne devant (Azure DevOps,
  GitHub Actions…). Nous n'en avons pas : la livraison se fabrique à la main sur le PC des sources (`creer_livraison.ps1`). Tant
  qu'une seule personne fabrique, sur son PC activé, la licence Single Developer suffit sur ce point. La licence Company devient
  nécessaire si plusieurs personnes lancent l'obfuscation ou si une fabrication automatique est mise en place (l'éditeur fournit
  alors, sur demande à son assistance, une licence pour le serveur).
- **Achat** : `https://www.eziriz.com/order.htm` ; le paiement et la facture sont faits par le revendeur **PayPro Global**. Taxes
  ajoutées au paiement selon le pays. **Non indiqué** : moyens de paiement acceptés, restriction par pays (paiement depuis l'Algérie à
  vérifier sur la page de paiement avant de valider), passage ultérieur de Single Developer à Company en payant la différence.
- Le contrat (lu le 2026-11-27) : nombre de logiciels protégés illimité, sans redevance par logiciel ni par officine.
- Écart à lever : 199 et 349 USD relevés le matin du 2026-11-27 (§ 5), 249 et 549 USD sur la page de commande le soir.

### 9.3 Activation (aide de l'éditeur ; faite sur le PC de développement le 2026-11-27)

**Fait le 2026-11-27 (constaté)** : licence achetée par le titulaire ; `license.license` et `license.v3lic` rangés hors dépôt
(`C:\MesAtlassian\NetReactor`). Voie sans l'interface, qui a fonctionné : (1) `dotNET_Reactor.Console.exe -gethardwareid` affiche le
Hardware ID du PC ; (2) le titulaire demande le `license.v3lic` sur le site d'activation (étapes 4 et 5 ci-dessous) ; (3) copie de
`license.v3lic` **à côté de `dotNET_Reactor.Console.exe`** ; (4) contrôle : une protection lancée avec `-licensed` rend le code 0 et
le message « Demo Version … 14 days » n'apparaît plus. Les sept assemblys ont été refaits en version complète en 54 s au total.

Procédure par l'interface, telle que l'éditeur la décrit :

1. À l'achat, un fichier **`license.license`** arrive par e-mail (autoriser `eziriz.com` dans la messagerie ; délai non indiqué).
   **Le garder en lieu sûr, hors dépôt** : il sert à toute activation future.
2. Ouvrir .NET Reactor, menu **Help > Registration**, choisir `license.license`, valider, relancer l'outil.
3. L'outil affiche le **Hardware ID** du PC.
4. Sur `https://www.reactoractivation.com/` : envoyer `license.license`, saisir le Hardware ID et l'adresse e-mail.
5. Un fichier **`license.v3lic`**, lié à ce PC, arrive par e-mail (en général en quelques minutes).
6. **Help > Registration**, choisir `license.v3lic`, relancer ; **Help > About** doit indiquer la version complète.

- Les deux fichiers ne sont pas interchangeables : `license.license` prouve l'achat, `license.v3lic` active un PC précis.
- Autre PC ou matériel changé : redemander un `license.v3lic` avec le même `license.license` et le nouveau Hardware ID.
- **Non indiqué** : s'il faut désinstaller la version d'essai avant ; pendant combien de temps le site d'activation reste garanti.
- Ni `license.license` ni `license.v3lic` ne vont dans le dépôt ni dans une livraison.

### 9.4 Ce qu'une nouvelle version change

- **Nouvelle version de .NET Reactor** : aucune obligation. Ce qui est déjà en officine continue de fonctionner : l'outil ne sert
  qu'à la fabrication, les fichiers obfusqués sont autonomes (constaté : l'API obfusquée tourne sans l'outil ; l'éditeur ne l'écrit
  pas en toutes lettres). Avec la version payante, pas de date de fin (la limite de 14 jours est celle de la version d'essai).
  Avant de fabriquer avec une nouvelle version de l'outil : **refaire l'essai du § 9.6**.
- **Nouvelle version de notre logiciel** : on refabrique et on redéploie comme aujourd'hui ; l'obfuscation est une étape de plus de
  `creer_livraison.ps1` (environ une minute pour les sept assemblys d'après l'essai).
- **Fichier de correspondance** (`.nrmap`, un par assembly) : les noms brouillés changent à chaque fabrication ; l'archiver **avec
  chaque version livrée**, hors paquet, sinon les traces d'erreur de cette version ne se décodent plus.

### 9.5 Commande d'obfuscation retenue (constaté) ; intégrée à la fabrication le 2026-11-28

**Intégration (« go intégration P2 », 2026-11-28)** : `scripts\mep\creer_livraison.ps1` obfusque lui-même les sept assemblys après la
publication et avant le nettoyage, avec la commande ci-dessous et `-licensed`. Il cherche l'outil avant de publier (paramètre
`-Obfuscateur`, sinon `%LOCALAPPDATA%\Programs\dotNET_Reactor` puis `Program Files`), **refuse la livraison** si l'outil manque, s'il
n'est pas activé (code 101), si un assembly n'a pas changé, si sa correspondance manque, ou si un `Pharma.*.dll` publié n'est pas dans
sa liste (projet ajouté). Les correspondances vont dans `<Sortie>-correspondances\<date-heure>\` (paramètre `-Correspondances`), hors
de la livraison ; `nettoyer_livraison.ps1` refuse toute livraison contenant un `.nrmap`, `.license`, `.v3lic` ou `.nrproj`.
`-SansObfuscation` fabrique une livraison en clair pour un essai interne (témoin du § 9.6), avec un avertissement. Tests :
`ScriptsLivraisonTests` (liste = tous les projets référencés par l'API et le poste ; `-licensed` et réglages présents). Depuis le « go paquet d'essai » du 2026-11-28, l'obfuscation est dans un script commun, `scripts\mep\obfusquer_livraison.ps1` (liste des assemblys, réglage, contrôles, correspondances), appelé par `creer_livraison.ps1` **et** par `creer_paquet_essai.ps1` : le paquet d'essai remis à un pharmacien est obfusqué de la même façon (fabrication réelle faite : 7 assemblys, 7 correspondances hors paquet ; paquet non installé ni lancé).

```powershell
dotNET_Reactor.Console.exe -file "<livraison>\Api\Pharma.Application.dll" -targetfile "<sortie>\Pharma.Application.dll" `
    -obfuscation 1 -obfuscate_public_types 0 -exclude_properties 1 -control_flow_obfuscation 1 -stringencryption 1 `
    -mapping_file 1 -necrobit 0 -antitamp 0 -suppressildasm 1 -q
```

- Un appel par assembly : `Pharma.Api.dll`, `Pharma.Application.dll`, `Pharma.Domain.dll`, `Pharma.Infrastructure.dll`,
  `Pharma.Shared.dll` (API) ; `Pharma.Desktop.dll`, `Pharma.Shared.dll` (poste). Jamais les assemblys de .NET ni des bibliothèques.
- `-obfuscate_public_types 0` : le public garde son nom (objets échangés en JSON, composants d'écran). `-exclude_properties 1` : aucune
  propriété renommée (sans lui, le JSON d'un type privé casse : § 8, essai P2).
- En fabrication réelle, ajouter **`-licensed`** : la commande échoue (code 101) si l'outil n'est pas en version complète ; aucun
  fichier « 14 jours » ne peut alors partir par erreur.
- Pour `Pharma.Api.dll` et `Pharma.Desktop.dll`, l'outil recopie aussi l'`.exe`, le `.deps.json` et le `.runtimeconfig.json` dans le
  dossier de sortie ; seul le `.dll` est à reprendre.
- Non activés à ce stade : NecroBit, anti-modification, virtualisation (coût à l'exécution, à mesurer avant).

### 9.6 Procédure d'essai d'une livraison obfusquée (constaté, à refaire à chaque changement d'outil ou de réglage)

1. Fabriquer une livraison dans un dossier d'essai (`creer_livraison.ps1 -Sortie <essai>\livraison`), la copier (`<essai>\obf`),
   obfusquer les sept assemblys de la copie (§ 9.5), ranger les `.nrmap` à part.
2. Base **jetable** (jamais `pharmacie_dev` ni `pharmacie_test`), créée avec le compte applicatif puis
   `initialiser_base_locale.ps1 -MiseAJour -Bases <base jetable>`.
3. Lancer **deux API** sur cette base, chacune avec ses dossiers (`Donnees__Racine`, `Journaux__Dossier`, `Ged__Dossier`) et son port
   (`Urls` et `--urls`) : l'API **témoin** (livraison non obfusquée) et l'API **obfusquée**. Vérifier les ports avant
   ([poste-de-developpement.md](poste-de-developpement.md)).
4. Rejouer les mêmes requêtes sur les deux et comparer les réponses (dates masquées). Essai du 2026-11-27 : 115 routes GET sans
   paramètre, connecté en titulaire. Une seule session par employé : une API après l'autre.
5. Lire le journal de l'API obfusquée : toute exception absente du témoin est un défaut d'obfuscation.
6. Lancer le poste obfusqué sur l'API obfusquée, avec ses propres dossiers (`PHARMA_API`, `PHARMA_POSTE_REGLAGES`,
   `PHARMA_POSTE_IDENTITE`, `WEBVIEW2_USER_DATA_FOLDER`), et dérouler les parcours (autorisation du poste, connexion, vente, retour,
   PDF, code d'activation).
7. En fin d'essai : arrêter les instances lancées, supprimer la base jetable et le dossier d'essai.

### 9.7 Objets relus depuis du JSON : la règle (correctif du 2026-11-27, « go correctif JSON »)

**Constaté par une expérience isolée** (petit programme obfusqué avec nos réglages, relecture de JSON) :

| Forme de l'objet | Sans `-exclude_properties` | Avec `-exclude_properties 1` (notre réglage) |
|---|---|---|
| Enregistrement **public** positionnel | fonctionne | fonctionne |
| Enregistrement **privé ou interne** positionnel (`record X(…)`) | **échec à la relecture** | **échec à la relecture** |
| Le même avec `[Obfuscation(Exclude = true, ApplyToMembers = true)]` | fonctionne | fonctionne |
| Type privé à propriétés (`{ get; set; }` ou `{ get; init; }`) | écrit `{}` | fonctionne |
| Type interne placé dans un objet public | écrit `{}` | fonctionne |
| Type anonyme (`new { … }`) écrit en JSON | fonctionne | fonctionne |

Cause : l'outil renomme les paramètres du constructeur des types non publics ; `System.Text.Json` ne retrouve plus la propriété de
chaque paramètre (« Each parameter in the deserialization constructor … must bind to an object property »). Effet réel constaté
avant le correctif : fichier de la date plancher de l'offre illisible, donc lecture de l'offre en erreur, autorisation d'un poste
impossible et routes gardées par un pack en erreur 500.

**Règle** : dans un fichier source livré qui lit ou écrit du JSON, tout enregistrement positionnel **non public** porte
`[System.Reflection.Obfuscation(Exclude = true, ApplyToMembers = true)]` (attribut standard de .NET, sans effet hors obfuscation ;
l'outil le respecte même sans son option « Declarative Protection »). Les types publics n'en ont pas besoin (jamais renommés).
17 objets marqués le 2026-11-27 : 1 dans l'API, 2 dans Application, 4 dans Infrastructure (dont la date plancher, les écarts de
réception, les profils de reprise, la copie de secours), 10 dans le poste (réception mise de côté, retour laissé, facture en
préparation, panier capturé, tâches laissées). Figé par le test d'architecture `ObjetsJsonObfuscationTests` : il échoue dès qu'un
nouvel enregistrement non public apparaît sans l'attribut dans un fichier qui manie du JSON.

**Limites de la règle** : elle ne voit que les enregistrements déclarés en début de ligne ; une classe non publique à constructeur
paramétré relue depuis du JSON n'est pas détectée (aucune trouvée à ce jour) ; seul l'essai du § 9.6 prouve le résultat.

### 9.8 Résultat de l'essai après correctif (2026-11-27 au soir, outil en version complète)

- Livraison refabriquée, 7 assemblys obfusqués avec `-licensed` (54 s au total), API témoin et API obfusquée sur la même base jetable,
  en gardant le fichier de date plancher déjà écrit (cas d'une mise à jour).
- **API** : `--autoriser-poste` réussit sur l'API obfusquée (il échouait) ; le maximum de postes de l'offre est bien refusé au 3e ;
  **115 routes GET sur 115 identiques** au témoin (4 écarts dus au montage : dossiers, clé de scellement, journal des connexions) ;
  journaux des deux API identiques (seulement les mêmes refus métier 422).
- **Temps** (115 routes, connecté en titulaire) : première passe 3,8 s contre 2,5 s pour le témoin (démarrage à froid) ; passes
  suivantes 1,03 s contre 1,02 à 1,09 s : **aucun écart mesurable** à chaud sur ces lectures. Écritures et caisse non mesurées.
- **Poste (Blazor Hybrid) obfusqué relié à l'API obfusquée** : autorisation du poste par son code (preuve signée du poste acceptée),
  connexion du titulaire, tableau de bord affiché ; **57 écrans sans paramètre balayés : aucun bandeau d'erreur, aucun écran vide** ;
  les 2 écrans du super admin renvoient à l'accueil avec leur message (comportement normal pour le titulaire).
- **Fichier de correspondance** (constaté) : fichier texte lisible, un bloc par type, puis des paires « nom brouillé, nom réel »
  (exemple relevé : `noqQqBe6na` → `LireContenu` dans `Pharma.Infrastructure.Offre.DatePlancherFichier`). Une trace se décode donc
  par simple recherche dans le fichier de la **même fabrication** ; l'outil fournit aussi un décodeur (« Stack Trace Deobfuscator »,
  interface et bibliothèque, non essayés). **Ce fichier contient tous les noms réels : il ne part jamais en officine.**
- **Essai à la main par le titulaire** (2026-11-28, vers 00 h 10) : caisse ouverte et produit mis au panier sur le poste obfusqué ;
  à ce moment, aucune erreur au journal de l'API obfusquée ni au journal Windows. Résultat de cet essai à noter par le titulaire.
- **Parcours de caisse sur le poste obfusqué relié à l'API obfusquée (2026-11-28, piloté par l'assistant)** : lot périmé refusé et
  lot suivant proposé (règle normale) ; **vente libre T-000001 encaissée** (352,50 DA, espèces) ; **ticket PDF** demandé aux deux API :
  PDF valide, même taille (74 080 octets) et mêmes données de ticket que le témoin ; **retour partiel** de la boîte enregistré
  (« 1 boîte remise en stock… À rendre au patient : 352,50 DA ») ; aucun bandeau d'erreur à l'écran, aucune erreur ni trace au
  journal de l'API obfusquée.
- **Non fait** : impression réelle sur imprimante, tiers payant, code d'activation de l'offre, relecture des JSON déjà enregistrés
  par une version non obfusquée (réception mise de côté, vente en attente, profil de reprise), synchronisation avec le central,
  connexion super admin, écrans à paramètre (fiches), mesure des temps en écriture.

**Décisions attendues du titulaire** : « go P1 » ; accord pour l'essai de .NET Reactor (version d'essai, puis achat éventuel) ou choix d'un
autre outil ; démarche juridique.
