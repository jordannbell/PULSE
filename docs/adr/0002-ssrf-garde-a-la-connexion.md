# ADR 0002 : défense contre le SSRF au moment de la connexion

- **Statut** : accepté
- **Contexte** : les utilisateurs choisissent les adresses que Pulse appelle. Sans contrôle, quelqu'un peut viser le réseau interne ou les métadonnées du cloud (`169.254.169.254`). Vérifier l'adresse une seule fois à la création ne suffit pas : un nom de domaine peut répondre avec une IP publique au moment du contrôle, puis avec une IP privée plus tard (« DNS rebinding »).
- **Décision** :
  1. `TargetValidator` refuse à la création toute adresse non http(s), avec identifiants, sur un port hors liste, ou qui résout vers au moins une IP non publique ;
  2. `HttpServiceProbe` résout le nom lui-même dans le `ConnectCallback` du `SocketsHttpHandler`, contrôle chaque réponse avec `IpAddressPolicy`, puis connecte le socket **à l'adresse contrôlée** ;
  3. les redirections ne sont jamais suivies : une redirection est un résultat (échec si le code attendu diffère), pas un saut ;
  4. seules des listes autorisées (global unicast IPv6 `2000::/3`, IPv4 hors plages réservées) passent, plutôt qu'une liste noire.
- **Conséquences** :
  - (+) fermé contre le rebinding, les écritures IP détournées (`2130706433`, `0x7f.1`, `::ffff:127.0.0.1`) et les redirections vers l'interne ;
  - (−) pas de cible sur ports non standard en production ;
  - (−) un service interne légitime ne peut pas être surveillé : cela nécessiterait un agent de sonde déployé dans le réseau du client.
- **Vérifié par** : `IpAddressPolicyTests`, `TargetValidatorTests`, `HttpServiceProbeTests`, `ApiTests`.
