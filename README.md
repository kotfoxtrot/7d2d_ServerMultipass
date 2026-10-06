# Server Multipass

One mod with the everyday features of a public 7 Days to Die server: homes, teleport requests,
blood moon reminders, scheduled restarts, chest sorting, land claim, backpack and quest location
protection, item giving, chat tags, a welcome message and locks on newly placed boxes, doors and
vehicles. Every feature is a module that you switch on or off in one file.

> Server-side mod · 7 Days to Die dedicated server 3.2 · no client download

[Русская версия](README.ru.md)

## What it does

| module | what it gives | on after install |
|---|---|---|
| [Home](#home) | `/home`, `/sethome`, `/delhome`: personal teleport points | yes |
| [Tpa](#tpa) | `/tp <player>`, `/tpa`, `/tpd`: teleport to another player with their consent | yes |
| [BloodMoon](#bloodmoon) | `/bm` and a daily reminder of the days left until the blood moon | yes |
| [Welcome](#welcome) | greets a newcomer in chat and welcomes back a returning player | yes |
| [ChestSort](#chestsort) | a storage box labelled `sort` spreads its items over the boxes of the base | yes |
| [Give](#give) | `mp-give` console command: items straight into a player's inventory | yes |
| [Shutdown](#shutdown) | restart on a schedule with a countdown in chat | no |
| [ClaimGuard](#claimguard) | only the owner and friends can open boxes and workstations inside a land claim | no |
| [ClaimLimit](#claimlimit) | a land claim over the limit is given back instead of the oldest claim being switched off | yes |
| [BackpackGuard](#backpackguard) | the backpack dropped on death belongs to its owner for 30 minutes | no |
| [PoiGuard](#poiguard) | nobody can walk into a location while another player is doing a quest there | no |
| [ChatDecor](#chatdecor) | chat tag and colours for chosen players, for example VIP | no |
| [Autolock](#autolock) | boxes, doors and vehicles a player places start locked to them | no |

- Modules are switched on and off in `ServerMultipass.xml`. A change applies a few seconds after
  the file is saved, no restart needed.
- The mod comes with ready settings and text files for every module, with an explanation above
  each line. It does not create these files itself: a module whose file is missing stays off.
- All messages to players come in English and Russian. Players pick a language with `/lang`, and a
  newcomer gets the language of their country automatically. Any text can be changed and new
  languages can be added.
- If a module breaks, it switches itself off and writes the reason to the log. The other modules
  keep working.
- Players do not need to install anything.

## Requirements

| | |
|---|---|
| game | 7 Days to Die dedicated server **3.2** |
| dependency | `0_TFP_Harmony` (ships with the server) |
| clients | nothing to download, server-side only |
| optional | a free GeoIP database from MaxMind (`GeoLite2-Country.mmdb` or `GeoLite2-City.mmdb`) to pick the language by country, download it from [github.com/P3TERX/GeoLite.mmdb](https://github.com/P3TERX/GeoLite.mmdb) |

## Install

### From a release

1. Unpack the archive into `<server>/Mods/` so that you end up with
   `<server>/Mods/1_ServerMultipass/` containing `ServerMultipass.dll`, `ModInfo.xml`,
   `ServerMultipass.xml` and the `Settings` and `Lang` folders.
2. Optional: download `GeoLite2-Country.mmdb` from [github.com/P3TERX/GeoLite.mmdb](https://github.com/P3TERX/GeoLite.mmdb) and put it into
   `<server>/Mods/1_ServerMultipass/Data/` (create the folder if it is not there yet).
3. Look through `ServerMultipass.xml` and the `Settings` files, see [Configuration](#configuration),
   then restart the server.

### From source

```bash
dotnet build -c Release -p:GameRoot=/path/to/server
```

`GameRoot` is the dedicated server root, the folder holding `7DaysToDieServer_Data/Managed` and
`Mods/0_TFP_Harmony`. Omit `-p:GameRoot` and the path baked into the `.csproj` is used.

The build puts the ready mod folder, with the DLL, `ServerMultipass.xml`, `Settings` and `Lang`,
into `bin/1_ServerMultipass/`; `-p:PackageDir=/some/folder` puts it elsewhere. Add
`-p:Deploy=true` to install it into `<GameRoot>/Mods/1_ServerMultipass/`: the DLL, `ModInfo.xml`
and the READMEs are replaced, your `ServerMultipass.xml`, `Settings` and `Lang` files are kept and
only missing ones are added.

### Updating

Replace `ServerMultipass.dll` and `ModInfo.xml`, keep your `ServerMultipass.xml`, `Settings` and
`Lang`, then restart the server. The mod itself never writes into these files, except
`mp enable` and `mp disable`, which switch a module in `ServerMultipass.xml`.

If a new version adds an option, a module or a text, the log says what your files are missing,
and such a module stays off until you add it: copy the line from the files of the new version.

### Folder name

The folder must start with `1_`. Mods are loaded in alphabetical order: `0_TFP_Harmony` has to come
first, and Server Multipass should see chat messages before other mods do, so that its chat commands
never show up in the chat.

## Configuration

Everything you configure ships with the mod, ready to use. The mod never creates or restores these
files: what you see in the folder is exactly what it works with.

```
Mods/1_ServerMultipass/
├── ModInfo.xml
├── ServerMultipass.dll
├── ServerMultipass.xml      shipped: modules on or off, general options
├── Settings/<Module>.xml    shipped: options of each module
├── Lang/<Module>.csv        shipped: every text players see
└── Data/
    ├── Players.json         written by the mod: languages that players picked with /lang
    └── GeoLite2-City.mmdb   GeoIP database, optional, put it here yourself
```

Data that belongs to a world, like the homes of players, is kept next to the save in
`Saves/<World>/<Game>/ServerMultipass/`. It goes away together with the world on a wipe and gets
into your backups together with the save.

A module starts only when its files are in place and correct: `Settings/<Module>.xml` for a module
with options and `Lang/<Module>.csv` for a module with texts. Every option has to be in the file
with a valid value. If something is missing or wrong, the module stays off and the log says exactly
what, for example `Settings/Home.xml: property Cooldown is missing`. The mod has no built-in values
and fills in nothing by itself: everything comes from these files. If you deleted or broke a file,
take it from the archive of your version.

Every file is read again a few seconds after you save it. If the new version of a file has a
mistake, it is not applied, the log tells what is wrong and the previous values stay in use. All log
lines of the mod start with `[Multipass]`.

### ServerMultipass.xml

```xml
<ServerMultipass>
  <property name="ChatPrefix" value="/" />
  <property name="DefaultLanguage" value="english" />
  <property name="GeoIp" value="true" />
  <property name="GeoIpDatabase" value="" />
  <property name="CountryLanguages" value="RU:russian,BY:russian,KZ:russian" />
  <modules>
    <module name="Home" enabled="true" />
    <module name="Shutdown" enabled="false" />
    ...
  </modules>
</ServerMultipass>
```

| option | shipped value | meaning |
|---|---|---|
| `ChatPrefix` | `/` | character that starts a chat command. If you change it, change the command hints in the `Lang` files too |
| `DefaultLanguage` | `english` | language for players who did not pick one and were not recognised by their country |
| `GeoIp` | `true` | pick the language of a newcomer by their country |
| `GeoIpDatabase` | empty | name of the GeoIP file in the `Data` folder of the mod. Empty means the first `.mmdb` file found there |
| `CountryLanguages` | `RU:russian,BY:russian,KZ:russian` | which language the players of a country get |

After you add or replace the GeoIP database, run `mp reload` or restart the server.

`enabled="true"` turns a module on, `enabled="false"` turns it off. The console commands
`mp enable <module>` and `mp disable <module>` do the same and save the change into this file.

### Who may use chat commands

Chat commands are open to everyone. To limit one, add a line to the `<commands>` section of
`serveradmin.xml`, with `mp.` in front of the command name:

```xml
<permission cmd="mp.home" permission_level="100" />
```

Now only players with permission level 100 or lower can use `/home` (0 is the server owner).

### Texts and languages

`Lang/<Module>.csv` is a table: the first column is the key, then one column per language. It opens
in any text editor or spreadsheet (UTF-8, comma separated). Colours use the game codes
`[RRGGBB]text[-]`, and `{0}`, `{1}` are filled in by the mod. The `Tag` line is the coloured prefix
of all messages of that module.

To add a language, add a column named after it (`german`, `french`, ...) and fill in the texts.
A text missing in some language is taken from `DefaultLanguage`, then from the first language
column that has it.

Players manage their language with:

- `/lang`: shows the current language and the available ones
- `/lang ru` or `/lang russian`: picks a language
- `/lang auto`: goes back to the automatic choice

## Home

Players save named teleport points and return to them with a cooldown.

- `/sethome <name>` saves the current spot; the same name again moves the point
- `/home` lists your homes, `/home <name>` teleports you there
- `/delhome <name>` removes a home

Teleport is refused while the player sits in a vehicle. Homes belong to the world and are wiped
together with it.

| option | shipped value | meaning |
|---|---|---|
| `Limit` | `3` | how many homes one player can save |
| `Cooldown` | `900` | seconds between two teleports home, 0 turns the wait off |

## Tpa

A player asks to be teleported to another player, who accepts or refuses.

- `/tp <name>` sends a request, part of the name is enough
- `/tpa` accepts the request, `/tpd` refuses it

| option | shipped value | meaning |
|---|---|---|
| `RequestTimeout` | `60` | seconds a request waits for an answer |
| `Cooldown` | `1800` | seconds between two teleports of the same player, 0 turns the wait off |

## BloodMoon

`/bm` shows how many days are left until the blood moon. Every in-game day at `NotifyHour` everyone
online gets a reminder, and on the blood moon day a warning that they are coming tonight.

| option | shipped value | meaning |
|---|---|---|
| `NotifyHour` | `18` | in-game hour of the daily reminder |
| `NotifyEveryDays` | `1` | remind when the days left divide by this number: 1 every day, 7 once a week, 0 only on the blood moon day |

## Welcome

When a player joins the server for the first time, everyone online sees a greeting. On every later
join the player gets a private welcome back message. The texts are in `Lang/Welcome.csv`.

| option | shipped value | meaning |
|---|---|---|
| `GreetNewPlayers` | `true` | greet a newcomer in chat for everyone online |
| `GreetReturningPlayers` | `true` | send a returning player a private welcome back |

## ChestSort

Put a storage box with a writable label into your base, write `sort` on it, drop items into it and
close it. Every item that already lies in another box of the same base moves there: first onto
unfinished stacks, then into free slots of that box. Items that no other box holds stay in the sort
box.

A base is your land claim together with your claims nearby. Boxes that someone has open right now
and slots locked by the player are never touched.

| option | shipped value | meaning |
|---|---|---|
| `ChestName` | `sort` | label text of the sort box |
| `MergeDistance` | `5` | your claims closer than the claim size plus this many blocks count as one base |

## Give

A console command for admins and shop panels: the item goes straight into the inventory of a player,
who gets a message about it. Every use is written to the log.

```
mp-give <player|all> <item> [count] [quality] [durability]
```

- `player`: name, entity id, EOS or Steam id; `all` gives to everyone online
- `count`: 1 by default; `quality`: 1 to 6, 1 by default; `durability`: percent, 100 by default

Examples: `mp-give Alice drinkJarBoiledWater 5`, `mp-give all gunHandgunT1Pistol 1 6`.

| option | shipped value | meaning |
|---|---|---|
| `Aliases` | empty | more names for the same command, for example `st-GiveItem,gi` for a shop panel. Applied after a server restart |
| `MaxCount` | `1000000` | largest amount one command can give |

If you still have the standalone GiveItemMod with the same command names, remove it first.

## Shutdown

Stops the server on a schedule so that your host brings it back up. Before that, everyone online
gets warnings in chat, during the last minute a warning that progress may not be saved, then all
players are kicked with a message and the server stops. During a blood moon the countdown waits.

**The mod only stops the server.** Something has to start it again: LinuxGSM monitor, a systemd
service with `Restart=always`, or your hosting panel.

- `/rc` in chat shows the time left
- `mp-shutdown` in the console shows the next restart, `mp-shutdown 10` moves it to 10 minutes from
  now, `mp-shutdown 03:00` to 3 o'clock, `mp-shutdown off` cancels it until the next start

| option | shipped value | meaning |
|---|---|---|
| `Schedule` | `240` | minutes after the server start (`240`), a time of day (`03:00`) or several times (`03:00,15:00`), server local time |
| `AlertMinutes` | `15,10,5,4,3,2,1` | minutes before the restart when everyone gets a warning |
| `AlertOnLogin` | `false` | tell every player who joins how long is left |
| `JoinAlertMinutes` | `5` | tell a joining player anyway when the restart is this close |
| `FinalWarningMinutes` | `1` | from this minute on, warn that progress may not be saved |
| `KickLeadSeconds` | `5` | seconds before the restart when all players are kicked |
| `ShutdownGraceSeconds` | `3` | shortest pause between the kick and the stop |
| `ShutdownTimeoutSeconds` | `15` | longest wait for players to disconnect |
| `FreezeDuringBloodMoon` | `true` | pause the countdown during a blood moon |
| `ChatCommands` | `rc` | chat commands that show the time left |

## ClaimGuard

Inside a land claim only its owner and the owner's friends can open storage boxes, workstations,
generators and loot bags. Loot bags also open for players in the owner's party, so after a blood
moon at someone's base the loot can be shared; the owner has to be online. A stranger hears the deny sound and gets a message. Vending machines stay
open for trade, admins are not limited, and dropped backpacks are left to
[BackpackGuard](#backpackguard). A claim whose owner has been away too long stops protecting, as in
the game itself. No settings.

## ClaimLimit

In the game itself a player who places one land claim more than `LandClaimCount` in
`serverconfig.xml` allows silently loses the oldest one: it stops protecting, and many players never
notice. With this module such a claim is not placed at all: the block goes back into the inventory,
and the player hears the deny sound and reads `Land claim limit reached: 3/3!`. Every claim that is
placed shows how many of the limit are in use: `Land claim blocks placed: 2/3!`.

The limit is `LandClaimCount` from `serverconfig.xml`; `setgamepref LandClaimCount` applies within
a second. The player's game applies the limit on its own the moment the block is placed, before the
server sees it, so while the module is on, the game's own limit is kept one above `LandClaimCount`:
neither the server nor the player's game switches off an old claim, and the module turns the extra
one away. When the module is turned off, the game's limit goes back to `LandClaimCount`. Do not
change it with `setgamestat LandClaimCount`, the module sets it back within a second.

If you lower `LandClaimCount` below what some players already have, their old claims stay, but they
cannot place new ones until they are under the limit. The rule covers admins too, as the game's own
limit does. A refused claim is written to the log. No settings.

## BackpackGuard

When a player dies, only they can open the backpack they dropped, for a set time. Anyone else is told
whose backpack it is and how long it stays protected. Admins are not limited.

| option | shipped value | meaning |
|---|---|---|
| `ProtectionMinutes` | `30` | real-time minutes of protection after death |
| `ExemptFriends` | `true` | friends of the owner can open the backpack too |

## PoiGuard

While a player is doing a quest in a location, other players who walk in are teleported back out,
to where they stood before entering. The protection lasts while the quest runs and during the loot
time after it (about two in-game hours, the game's own rule). The quest owner, their party, their
friends and admins are never moved. Every move is written to the log.

A land claim that would cover part of a location, or a bedroll placed inside one, is removed right
away and given back to the player.

| option | shipped value | meaning |
|---|---|---|
| `EvictStrangers` | `true` | teleport other players out of a quest location |
| `BlockClaims` | `true` | forbid land claims that cover part of a location |
| `BlockBedrolls` | `true` | forbid bedrolls inside locations |

## ChatDecor

Gives chosen players a tag and colours in chat. List them in `Settings/ChatDecor.xml`, one line per
player:

```xml
<player id="EOS_..." tag="[00CC00]VIP[-]" nameColor="FFD700" messageColor="FFFFFF" />
```

`id` is the EOS or Steam id of the player, the `lp` console command shows it. Colours are `RRGGBB`.

With `TagBeforeName="false"`, as shipped, the tag goes right after the name: `Name: [VIP] message`.
This way the game's block list keeps working: whoever blocked this player still does not see the
messages. With `TagBeforeName="true"` you get the classic look `[VIP] Name: message` with a
coloured name, but then the block list no longer hides these messages.

| option | shipped value | meaning |
|---|---|---|
| `TagBeforeName` | `false` | classic look with the tag and coloured name in front, see above |

## Autolock

A storage crate, door or vehicle that a player places starts locked to that player. In the game
itself it starts unlocked, and anyone can open it until the owner remembers to lock it.

- Only what a player places gets locked. Locations, loot and everything already standing in the
  world are left alone.
- What the owner unlocked stays unlocked after a restart, and an upgraded block keeps the lock state
  it had.
- The owner unlocks, locks and sets a keypad code as usual. Nobody else, friends included, gets
  into a locked box, door or vehicle without the code: this is how every lock in the game works. For
  a friend to ride along, unlock the vehicle or give them its code.
- Bandits open only unlocked doors, so a locked door keeps them out as well.

Covered are the Wood, Iron and Steel Storage Crates and every door, hatch, gate, shutter and
drawbridge that can be locked in the game. Safes, gun safes, lockers and powered doors have no lock
in the game and are left alone. The lock is the game's own, exactly as if the owner had locked it by
hand.

| option | shipped value | meaning |
|---|---|---|
| `LockStorage` | `true` | lock storage crates |
| `LockDoors` | `true` | lock doors, hatches, gates, shutters and drawbridges |
| `LockVehicles` | `true` | lock vehicles |

The separate LockStorageOnPlace and LockVehicleOnPlace mods are not needed with this module.

## Console commands

| command | what it does |
|---|---|
| `mp` | list of modules: on or off, their chat commands, errors |
| `mp enable <module>` | turns a module on and saves it in `ServerMultipass.xml` |
| `mp disable <module>` | turns a module off and saves it in `ServerMultipass.xml` |
| `mp reload` | reads all files again right now |
| `mp-give ...` | see [Give](#give) |
| `mp-shutdown ...` | see [Shutdown](#shutdown) |

Console commands are for admins only (permission level 0) unless `serveradmin.xml` says otherwise.

## Failure behaviour

* Modules do not depend on each other. If a module fails 10 times within a minute, it switches
  itself off, writes the reason to the log and tells how to switch it back on. The others keep
  working.
* A module whose settings or text file is missing or wrong stays off, and the log names the file
  and the problem. The other modules start as usual.
* A mistake in a file edited while the server runs never stops anything: the log names the file and
  the problem, and the previous values stay.
* A module that is off does not touch the game at all.
* A world data file that cannot be read is kept as a `.broken` copy next to it, and the module
  starts empty instead of losing it silently.

## Related mods

Server Multipass is about admin features. Engine fixes stay separate mods, so that an update of one
never takes down the other:

| mod | what it is for |
|---|---|
| [MaxChunkAgeDeadlockFix](https://github.com/kotfoxtrot/7d2d_MaxChunkAgeDeadlockFix) | makes `MaxChunkAge` chunk reset safe by breaking a lock-order deadlock |
| [CullExpiredFix](https://github.com/kotfoxtrot/7d2d_CullExpiredFix) | makes that same reset cheap |
| [ProfLog](https://github.com/kotfoxtrot/7d2d_ProfLog) | read-only profiler for the dedicated server |
