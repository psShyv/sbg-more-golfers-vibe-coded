# AI-Generated More Golfers Update
#### _A BepInEx5 mod to increase the player limit of Super Battle Golf_

Tested against Super Battle Golf **v1.2.2-657**.

## Installation

- [Install BepInEx 5](https://github.com/BepInEx/BepInEx/releases) 
- Download and drop the MoreGolfers.dll file in the \BepInEx\plugins folder of your Super Battle Golf Installation
- After opening the game for the first time, you can adjust the custom player limit in \BepInEx\config MoreGolfers.cfg

## Multiplayer note

`MaxPlayers` is read independently from each participant's own local config file - it is not
transmitted over the network. For a match to look and behave consistently for everyone, **every
participant (host and clients) should run the same MoreGolfers version with the same `MaxPlayers`
value.** A mismatched client isn't at risk of crashing - the game's own tee-layout code only ever
runs server-side - but its locally-rendered UI pools (name tags, hole-progress entries, popups,
and so on) will be sized for its own configured value rather than the match's actual player count,
which reintroduces the destroy/reinstantiate churn this mod exists to remove, just for that one
participant.

## Building for source
*set the env variable ``SUPER_BATTLE_GOLF_PATH`` to your Super Battle Golf installation directory.*
```sh
dotnet build
```
