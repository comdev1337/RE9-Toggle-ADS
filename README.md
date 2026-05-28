# RE9 Toggle ADS

REFramework.NET mod to **toggle** ADS on RMB instead of **holding RMB down all the time like a retard that Capcom thinks you are**

<img width="1209" height="704" alt="image" src="https://github.com/user-attachments/assets/c1d4beb7-0548-44a1-8bed-7f53caf3d9cf" />

## Features

* Toggle ~~AIDS~~ ADS on RMB
* It might work

## Installation

1. Install all [prerequisites](https://cursey.github.io/reframework-book/api_cs/general/index.html#prerequisites) for a REFramework build with REFramework.NET support. If installed right, you'll likely observe a cmd generating a ton of .NET assemblies when starting the game.
2. Put [RE9ToggleADS.cs](RE9ToggleADS.cs) in `reframework\plugins\source`

## Notes

- The script learns the internal ADS hold button automatically per session.
- Loading a different save may rebuild the underlying input objects; the script detects this and relearns automatically.
- Tested with REFramework `v1.5.9.1+479-2130fe73`, game version `1.3.0.0`.
