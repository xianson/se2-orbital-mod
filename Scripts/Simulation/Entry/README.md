# Entry/ - reentry and heat

- `EntryHost.cs` - reentry in game (the rules are `Core/Entry/Reentry`). Server: `ServerBeforeLock` (hooks the aero mod's
  entry source - never under FramesLock), `ServerTick` (under the lock: railed frames braked through a body's band, heat
  kept, ballistic coefficients and the border's air asked for), `ServerAfterLock` (those aero calls made, after the lock).
  Client: `ClientTick` (your next pass predicted: the card, the map, warp stopping ahead). The aero contract numbers are
  checked by `tools/check_contract.sh`.
- `EntryDamage.cs` - heat past a grid's tolerance wears its forward layer (server, gentle by rule).
- `DelfosHeat.cs` - the brown dwarf's killing field about our Delfos (the game's own damage / effect / warning, moved).
