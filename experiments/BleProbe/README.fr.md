[English](README.md) · **Français**

# BleProbe — archive d'un résultat négatif

Banc d'essai conservé **exprès**, et volontairement **hors de
`LuminaMonitor.sln`** : il ne fait pas partie de l'application et n'est pas
compilé avec elle.

Il a servi à répondre à une seule question, avant que la voie USB ne soit
choisie : Windows 11 peut-il piloter un iPhone en publiant un clavier/souris
**Bluetooth LE** (HOGP) depuis l'espace utilisateur ? Trois profils HID sont
essayés — pointeur relatif, pointeur absolu, digitaliseur tactile —
`LuminaMonitor.BleProbe <relative|absolute|digitizer>`.

**Réponse : non**, et pour deux raisons vérifiées sur une machine réelle :

1. l'appairage dual-mode est instable — iOS fusionne les identités Classic et
   LE et démolit le lien HID neuf ;
2. iOS n'accepte le **pointeur absolu qu'en Bluetooth Classic**, un rôle que
   Windows n'offre pas sans pilote noyau tiers — ce que la règle du projet
   interdit.

C'est ce mur qui a envoyé le projet sur le câble USB. Le code reste ici pour
que la démonstration soit vérifiable plutôt que sur parole ; il ne reçoit
aucune maintenance.

Pour le compiler malgré tout :
`dotnet build experiments/BleProbe/LuminaMonitor.BleProbe.csproj`.
