# Tester et vérifier Pulse

Ce guide dit **quoi lancer, dans quel ordre, et quel résultat attendre**. Trois niveaux, du plus rapide au plus réaliste :

| Niveau | Quoi | Durée | Ce qu'il prouve |
|---|---|---|---|
| 1 | Build + tests automatiques | 30 s | La logique est correcte, le code compile sans avertissement |
| 2 | `scripts/verify.ps1` : vrais serveurs, vrai réseau | 5 min | L'application tourne de bout en bout et résiste aux attaques courantes |
| 3 | Parcours manuel dans le navigateur | 15 min | L'interface est utilisable et lisible |

Un échec au niveau 1 rend les suivants inutiles. Toutes les commandes se lancent **à la racine du dépôt**.

## 0. Prérequis

- SDK .NET 10 (`dotnet --version` affiche `10.x`).
- Windows PowerShell 5.1 ou PowerShell 7 pour le niveau 2.
- Accès Internet pour les contrôles « vrai site public » (sinon ajoute `-SkipInternet`).
- Ports libres sur `127.0.0.1` : 5180 à 5183, 5189, 5190, 9190 et 2590 pour le niveau 2 ; 5080, 5090, 9100 et 2525 pour le niveau 3.
- Après une modification du code, **arrête puis relance** les programmes du niveau 3 : un programme déjà lancé garde l'ancienne version.
- Le niveau 2 lance plusieurs processus .NET en même temps : ferme les programmes lourds avant, sinon la machine peut manquer de mémoire.

## 1. Niveau 1 : build et tests automatiques

```bash
dotnet build -c Release
dotnet test --project tests/Pulse.Tests -c Release
dotnet format --verify-no-changes
```

**Attendu** : `0 Avertissement(s)`, `0 Erreur(s)` (les avertissements sont des erreurs) ; `total : 357`, `échec : 0` ; `dotnet format` ne dit rien et sort avec le code 0.

**Couverture** (cible : 80 % minimum) :

```bash
dotnet test --project tests/Pulse.Tests -c Release --coverage --coverage-output-format cobertura --results-directory TestResults
```

Le rapport `*.cobertura.xml` est dans `TestResults/`. Le site Blazor (`Pulse.Web`) n'est pas couvert par les tests automatiques : c'est le rôle des niveaux 2 et 3.

**Ce que les tests couvrent**

| Fichier | Vérifie |
|---|---|
| `MonitoredServiceTests` | Seuil d'échecs, ouverture/fermeture d'incident, délai avant prochain contrôle, bornes des réglages |
| `IpAddressPolicyTests`, `TargetValidatorTests` | Adresses publiques acceptées ; privées, loopback, métadonnées cloud, formes détournées, ports interdits, identifiants dans l'URL refusés |
| `AccountServiceTests` | Inscription, connexion, verrouillage, jetons à usage unique, mot de passe, formules, suppression de compte |
| `TenantIsolationTests` | Un client ne lit, ne modifie et ne supprime rien d'un autre, au niveau du stockage et de l'API |
| `SecurityAdapterTests` | Hachage des mots de passe, chiffrement AES-GCM des secrets (falsification détectée) |
| `StartupValidationTests` | Une clé de chiffrement ou une adresse de site invalide arrête le démarrage |
| `MigrationTests` | Une base de l'ancienne version migre vers « Espace historique » sans perte |
| `ApiTests` | L'API complète par HTTP : authentification, validation, cycle de vie, quotas, formules, en-têtes, adresse jamais révélée |
| `CheckRunnerTests`, `CheckRunnerRaceTests` | Contrôles sur vraie base SQLite, incident ouvert puis fermé, purge, service supprimé pendant son contrôle |
| `HttpServiceProbeTests` | Vraie sonde contre un vrai serveur local : succès, mauvais code, redirection non suivie, délai dépassé, adresse privée refusée |
| `AlertPublishingTests`, `AlertDispatcherTests` | Quand une alerte part (une à l'ouverture, une à la résolution), nouvelles tentatives, un canal en panne ne bloque pas l'autre |
| `WebhookAlertChannelTests`, `MailTests` | Vrai webhook signé et vrai envoi SMTP contre des serveurs locaux |
| `FrenchTextTests` | Chaque message de l'API a sa traduction française |

## 2. Niveau 2 : vérification réelle de bout en bout

```bash
powershell -ExecutionPolicy Bypass -File scripts/verify.ps1
```

Options : `-SkipInternet` (sans réseau), `-SkipBuild` (si déjà compilé), `-Dotnet <chemin>`.

Le script **démarre les vrais programmes** (l'API, le site, et `tools/FlakyTarget` qui joue une cible capricieuse, un récepteur de webhooks et un faux serveur SMTP), joue les scénarios, puis arrête tout et supprime ses bases temporaires. Le code de sortie est le nombre d'échecs : `0` = tout fonctionne.

**A. Configuration dangereuse refusée.** L'API s'arrête vite, avec `Invalid configuration: …`, si la clé de chiffrement manque, a la mauvaise taille ou n'est pas en base64, si l'adresse du site est invalide, si le serveur de mail n'a pas d'expéditeur valide, ou si un réglage est hors bornes.

**B. API en mode production** (cibles privées interdites, liens d'e-mail écrits dans le journal).
- Validation de l'inscription : mot de passe court ou égal à l'e-mail, e-mail invalide, adresse de page invalide, réservée (`admin`, `demo`) ou prise, e-mail déjà utilisé.
- Authentification : 401 sans jeton, avec un faux jeton, avec le bon jeton amputé d'un caractère ; même réponse pour un mauvais mot de passe et un e-mail inconnu.
- Confirmation d'e-mail et réinitialisation du mot de passe, avec le lien lu dans le journal ; jetons à usage unique ; la réinitialisation ferme toutes les sessions.
- **SSRF** : une vingtaine d'adresses d'attaque refusées pour les services, et quatre pour les webhooks (`127.0.0.1`, `localhost`, `169.254.169.254`, `0x7f.1`, `[::ffff:127.0.0.1]`, réseaux privés, ports 22 et 6379, `ftp://`, `file://`…), aucune enregistrée.
- Limites de la formule Gratuite (5 services, pas de contrôle plus rapide que 5 min), passage en Pro, retour refusé si l'usage dépasse.
- **Isolation entre deux clients** : liste, lecture, modification, suppression, historique et webhooks d'un autre client répondent 404 ; chaque page publique ne montre que ses services ; un service masqué n'apparaît pas.
- Secret de webhook : montré une fois, absent de toute liste suivante, **absent du fichier de base de données**.
- Vrai site public (`https://example.com/`) : vrai DNS, vrai TLS, « Opérationnel », latence positive, disponibilité 100 %.
- Redémarrage : sessions et services toujours là. Verrouillage après 5 échecs. Suppression de compte : sessions fermées, page publique en 404, adresse et e-mail réutilisables.
- Le journal ne contient ni erreur non gérée, ni trace de pile, ni mot de passe, ni secret.

**D. Vrai site web.** Accueil, inscription, connexion, mot de passe oublié et confidentialité s'affichent ; le tableau de bord, le compte et les alertes ne montrent rien sans connexion ; la page publique d'un client s'affiche sans les adresses surveillées et sans fuite d'un autre client ; page inconnue en 404 ; en-têtes de sécurité (CSP, nosniff) ; aucun jeton ni mot de passe dans le HTML ; **API arrêtée, la page publique reste en ligne avec un message, sans trace de pile**.

**E. Limitation de débit.** Au-delà de la limite globale, 429 ; les tentatives de connexion répétées sont coupées par la limite d'authentification sans ralentir le reste de l'API.

**C. Incidents et alertes par client** (laboratoire : cibles privées autorisées, 2 échecs = panne, e-mails reçus par le faux serveur SMTP).
- Deux clients, l'un avec un webhook et un e-mail, l'autre avec l'e-mail seulement : l'alerte de test atteint chaque canal ; la signature du webhook est **recalculée** par le script.
- Une panne simultanée des deux : chaque page publique montre son propre incident, le webhook ne reçoit que celui de son propriétaire, **une seule fois** malgré un premier appel forcé en erreur (nouvelle tentative), et chaque propriétaire reçoit l'e-mail de son service seulement.
- Au rétablissement : une alerte « résolu » par canal, sans doublon, un seul incident par panne, disponibilité sous 100 %.
- Cible trop lente (`No answer within 1 s`), redirection jamais suivie (302), 15 services de 2 s contrôlés en parallèle en moins de 20 s, service ou compte supprimé pendant son contrôle sans effet sur les autres.

### Lire le résultat

```
[PASS] Refused for a service: http://169.254.169.254/latest/meta-data/
[FAIL] After 2 failures in a row both services are Down
Result: N passed, 1 failed, 0 skipped
```

Pour une ligne `[FAIL]`, regarde les journaux de l'API et corrige. Un échec **de temps** (un « Down » qui n'arrive pas) peut venir d'une machine très chargée : relance une fois avant de conclure.

## 3. Niveau 3 : parcours manuel dans le navigateur

### 3.1 Démarrer

```bash
dotnet run --project src/Pulse.Api --launch-profile http     # API sur 5080
dotnet run --project src/Pulse.Web --launch-profile http     # site sur 5090
dotnet run --project tools/FlakyTarget                       # cible capricieuse (9100) et faux SMTP (2525)
```

Pour que les alertes e-mail partent vers le faux serveur, définis dans le terminal de l'API, avant `dotnet run` :

```powershell
$env:Pulse__Mail__Host = "127.0.0.1"
$env:Pulse__Mail__Port = "2525"
$env:Pulse__Mail__Security = "None"
$env:Pulse__Mail__From = "pulse@example.com"
```

Les messages reçus se lisent sur <http://localhost:9100/hook/received> et <http://localhost:9100/smtp/received>.

### 3.2 Parcours à dérouler

| # | Action | Résultat attendu |
|---|---|---|
| 1 | Ouvrir <http://localhost:5090/> | Accueil : battement de cœur, exemple de page de statut, boutons « Créer un compte gratuit » et « Voir un exemple de page » |
| 2 | Cliquer sur « Exemple » (`/s/demo`) | Trois services avec leur frise de 90 jours, encadré « Données de démonstration », incidents passés |
| 3 | « Créer un compte » avec un mot de passe de 5 caractères | Message d'erreur en français sous le champ, rien n'est créé |
| 4 | Créer le compte (mot de passe de 10 caractères ou plus, adresse de page `ma-boutique`) | Arrivée sur `/app`, connecté, message de bienvenue |
| 5 | « Ajouter un service » : nom `Site de démo`, adresse `http://localhost:9100/flaky`, fréquence 30 s (à réserver à Pro : passer d'abord par « Compte » → Pro) | Le service apparaît ; dans la minute, « Opérationnel » et un premier contrôle |
| 6 | Ajouter `ftp://exemple.fr/` avec un nom vide | Deux messages en français sous les champs, rien n'est ajouté |
| 7 | Ouvrir « Ma page » (`/s/ma-boutique`) | État global vert, le service avec sa frise, aucune adresse visible |
| 8 | « Alertes » : ajouter un webhook `http://localhost:9100/hook` | Le secret `whsec_…` est affiché **une seule fois** ; il n'apparaît plus après rechargement |
| 9 | « Envoyer une alerte de test » | Une ligne verte par canal ; l'événement `test` est visible sur `/hook/received` |
| 10 | Panne : `curl -X POST http://localhost:9100/control/500` | Après 3 contrôles : « En panne » dans `/app`, page publique en rouge avec l'incident et sa cause, `incident.opened` reçu par le webhook |
| 11 | Rétablissement : `curl -X POST http://localhost:9100/control/200` | Retour au vert, incident « résolu », `incident.resolved` reçu, pas d'alerte à chaque contrôle |
| 12 | « Compte » : changer le mot de passe, se déconnecter, se reconnecter | Connexion avec le nouveau mot de passe seulement |
| 13 | `/forgot-password` avec l'adresse du compte | Message identique pour une adresse inconnue ; le lien de réinitialisation est dans la console de l'API |
| 14 | Ouvrir `/app` déconnecté | Redirection vers la connexion |
| 15 | « Compte » → supprimer le compte (mot de passe demandé) | Retour à l'accueil ; `/s/ma-boutique` répond « Cette page de statut n'existe pas » |
| 16 | Arrêter l'API, recharger `/s/demo` | « Le statut n'est pas disponible pour le moment », pas de page d'erreur technique ; l'accueil s'affiche toujours |

### 3.3 Lisibilité et accessibilité (contrôle rapide)

- Réduire la fenêtre à 390 px : pas de défilement horizontal, formulaires sur une colonne.
- Passer le système en mode sombre : couleurs lisibles, badges compris.
- Naviguer au clavier (`Tab`) : chaque champ et chaque bouton reçoit un contour visible.
- Les états ne reposent pas sur la couleur seule : chaque badge porte un texte (« Opérationnel », « En panne »).

## 4. Contrôles de sécurité avant toute mise en ligne

- [ ] `Pulse__Accounts__EncryptionKey` est définie par variable d'environnement ou secret de l'hébergeur (32 octets aléatoires en base64), sauvegardée, absente du dépôt. La valeur de `appsettings.Development.json` n'est jamais utilisée en production.
- [ ] `Pulse:AllowPrivateTargets`, `Pulse:SeedDemoData`, `Pulse:Accounts:AutoVerifyEmail` et `Pulse:Accounts:LogEmailsToConsole` valent `false`. `appsettings.Development.json` n'est jamais déployé.
- [ ] `ASPNETCORE_ENVIRONMENT` vaut `Production`.
- [ ] Un serveur SMTP est configuré (`Pulse:Mail:*`) et `Pulse:Accounts:WebBaseUrl` est l'adresse publique du site.
- [ ] Le site et l'API sont derrière HTTPS (proxy inverse) ; `Web:SecureCookies=true`. Le site ajoute HSTS en production, pas l'API : le proxy doit le faire.
- [ ] Derrière un proxy, `Pulse:TrustForwardedHeaders` et `Web:TrustForwardedHeaders` sont activés et le proxy est le seul à joindre l'application : sans cela, tous les visiteurs partagent la même limite de débit.
- [ ] La base SQLite est sur un disque persistant et sauvegardée avec la clé de chiffrement.
- [ ] Les champs de `/confidentialite` (éditeur, contact) sont complétés.
- [ ] `git ls-files | grep -E "\.db|\.env|secret"` ne retourne rien.
- [ ] `dotnet list package --vulnerable --include-transitive` ne signale rien.
- [ ] `scripts/verify.ps1` rend `0`.

## 5. Ce qui n'est PAS vérifié

- **Tenue dans la durée** : aucun essai de plusieurs heures ou jours (fuites mémoire, croissance de la base).
- **Charge** : 15 services en parallèle testés, pas 500. `ListDueAsync` charge tous les services à chaque tick (voir ADR 0001).
- **Interface** : aucun test automatisé du site Blazor, aucun audit d'accessibilité formel (Lighthouse, lecteur d'écran).
- **Navigateurs** : seul Chrome a été utilisé.
- **Production réelle** : pas de test derrière un proxy inverse, ni en HTTPS, ni en conteneur, ni sous Linux.
- **Alertes** : testées contre un faux serveur SMTP et un faux récepteur de webhook, **pas contre un vrai fournisseur** (Gmail, SendGrid, Slack, Discord) : STARTTLS/TLS, authentification SMTP et les formats Slack et Discord n'ont été vérifiés que par des tests unitaires ou localement sans chiffrement. Une alerte en attente d'envoi est perdue si le processus s'arrête (ADR 0003).
- **Comptes** : pas d'authentification à deux facteurs, pas d'équipes ; la rotation de la clé de chiffrement n'est pas automatisée (ADR 0004).

## 6. Dépannage

| Symptôme | Cause probable | Remède |
|---|---|---|
| `Invalid configuration: … EncryptionKey …` au démarrage | Clé absente, pas en base64 ou pas de 32 octets | Définir `Pulse__Accounts__EncryptionKey` (`openssl rand -base64 32`) |
| `Address already in use` | Un ancien processus tient le port | `Get-Process dotnet`, puis `Stop-Process` sur le bon identifiant |
| Un service reste « En attente » | Aucun contrôle encore passé (délai = intervalle), ou planificateur désactivé | Attendre ; vérifier `Pulse:SchedulerEnabled` |
| Ajout refusé « doit pointer vers un serveur public » | Cible locale en mode production | Normal en production ; en démo locale, utiliser le profil Development |
| Pas d'e-mail de confirmation | Aucun SMTP configuré | En développement, lire le lien dans la console de l'API ; sinon configurer `Pulse:Mail:*` |
| `429 Too Many Requests` | Limite de débit atteinte | Attendre une minute ou relever `Pulse:RateLimitPerMinute` / `Pulse:AuthRateLimitPerMinute` |
| Le site affiche « statut non disponible » | L'API est arrêtée ou `Api:BaseUrl` est faux | Démarrer l'API ; vérifier `Api:BaseUrl` |
| `dotnet test` : « VSTest target is no longer supported » | Ancienne syntaxe | Utiliser `dotnet test --project tests/Pulse.Tests` |
