# ValBridgeServer — AI Player Tool Design

## Goal

Enable an AI model to play Valheim by exposing a set of primitive tools that mirror the actions available to a human player. These tools should be low-level enough to give the AI fine-grained control, while compound/helper tools can be layered on top for common sequences (e.g. "chop this tree", "navigate to point").

## Design Principles

- **Match human capabilities**: Each tool should correspond to something a human player can do with keyboard/mouse.
- **Camera-based perception**: Use Unity's camera and raycasting to give the AI a view of the world that matches what a player sees — not omniscient sphere queries.
- **Atomic actions**: Prefer single-press / single-frame actions over long-running loops. Let the AI decide when to swing again rather than auto-repeating.
- **Compound tools are optional helpers**: Tools like `navigate_to_position` and `attack_target` combine primitives for convenience, but the AI should be able to do everything with primitives alone.

---

## Existing Tools

| Tool | Type | Description |
|------|------|-------------|
| `player_get_health` | Perception | Health, max health, percentage |
| `player_get_position` | Perception | World position coordinates |
| `find_nearby_prefabs` | Perception | Physics overlap sphere search by name |
| `navigate_to_position` | Compound | Pathfind and walk to a world position |
| `get_visible_objects` | Perception | Camera frustum + raycast visibility check |
| `primary_attack` | Combat | Single primary attack (left click) |
| `secondary_attack` | Combat | Single secondary attack (right click) |
| `block` | Combat | Start/stop blocking (parry if timed within 0.25s) |
| `look_at_position` | Look | Face toward a world coordinate |
| `look_at_object` | Look | Face toward an object by instanceId |
| `get_player_state` | State | Full snapshot: health, stamina, biome, weapon, effects, food |
| `get_inventory` | State | All items with name, quantity, slot, equipped, durability |
| `interact` | Interaction | Press E on hovered object |
| `pickup_nearby` | Interaction | Pick up loose items within range |
| `equip_item` | Equipment | Equip item from inventory by name |
| `unequip_item` | Equipment | Unequip currently equipped item by name |
| `use_item` | Equipment | Consume food/potion by name |
| `drop_item` | Equipment | Drop item into world |
| `move_direction` | Movement | Walk/run in world-space direction for duration |
| `jump` | Movement | Single jump input |
| `dodge` | Movement | Dodge roll in direction (i-frames) |
| `set_crouch` | Movement | Toggle crouch/sneak on or off |
| `get_available_recipes` | Crafting | List craftable recipes at current station |
| `craft_item` | Crafting | Craft item by name, consumes materials |
| `repair_item` | Crafting | Repair damaged inventory item |
| `place_piece` | Building | Place building piece at position/rotation |
| `remove_piece` | Building | Demolish building piece by instanceId |
| `run_command` | Utility | Execute console commands |
| `get_steam_session_state` | Identity | Authoritative in-process Steamworks readiness/identity probe (SteamID64, BLoggedOn, Valheim 892970 subscription, app owner, family-sharing). Fails closed. Menu + in-world. |

---

## Planned Tools

### Perception / State

#### `get_player_state`
Full player snapshot: health, stamina, position, look direction, status effects, food buffs, current biome, equipped items.

#### `get_inventory`
List all items in inventory with name, quantity, slot, equipped status, and durability.

#### `get_visible_objects`
Camera-based vision system:
1. Gather candidate objects near the player
2. Filter to objects within the camera frustum (`Camera.WorldToViewportPoint`)
3. Raycast from camera to each candidate to check line-of-sight (occlusion)
4. Return visible objects with: name, type, instanceId, distance, screen-space position

This replaces omnidirectional sphere queries with perception that matches what the player actually sees.

#### `get_hovered_object`
What the player's crosshair is pointing at — the object that pressing E would interact with. Includes name, type, instanceId, available actions.

---

### Movement

#### `move_direction`
Walk or run in a direction for a specified duration. Parameters: direction vector (or cardinal: forward/back/left/right), run (bool), duration (seconds).

#### `jump`
Single jump input.

#### `dodge`
Dodge roll in a direction.

#### `set_crouch`
Toggle sneak/crouch on or off.

---

### Look / Aim

#### `look_at_position`
Face the player toward a world coordinate (XZ plane).

#### `look_at_object`
Face the player toward a specific object by instanceId.

---

### Combat (Melee)

#### `primary_attack`
Single primary attack input (left click equivalent). Swing with current weapon. Returns when the attack animation starts.

#### `secondary_attack`
Single secondary attack input (right click equivalent). Block, parry, or alt-attack depending on equipped weapon/shield.

#### `block`
Start or stop holding block. Parameters: active (bool).

---

### Interaction

#### `interact`
Press the interact key (E) on whatever is currently hovered. Opens doors, picks up items, activates crafting stations, reads signs, talks to NPCs.

#### `pickup_nearby`
Auto-pickup loose items within range.

---

### Inventory / Equipment

#### `equip_item`
Equip an item by name from inventory.

#### `unequip_item`
Unequip currently equipped item in a slot.

#### `use_item`
Consume a food or potion item by name.

#### `drop_item`
Drop an item from inventory.

---

### Building

#### `place_piece`
Place a building piece at a position/rotation. Requires hammer equipped. Parameters: piece name, position, rotation.

#### `remove_piece`
Demolish a building piece by instanceId.

---

### Crafting

#### `get_available_recipes`
List what can be crafted at the nearest crafting station.

#### `craft_item`
Craft an item by name at the nearest station.

#### `repair_item`
Repair an item at the nearest station.

---

## Implementation Priority

1. ~~**Vision** — `get_visible_objects` (foundational for all decision-making)~~ DONE
2. ~~**Combat** — `primary_attack`, `secondary_attack`, `block`~~ DONE
3. ~~**Look** — `look_at_position`, `look_at_object`~~ DONE
4. ~~**State** — `get_player_state`, `get_inventory`~~ DONE
5. ~~**Interaction** — `interact`, `pickup_nearby`~~ DONE
6. ~~**Equipment** — `equip_item`, `unequip_item`, `use_item`, `drop_item`~~ DONE
7. ~~**Movement** — `move_direction`, `jump`, `dodge`, `set_crouch`~~ DONE
8. ~~**Building & Crafting** — `get_available_recipes`, `craft_item`, `repair_item`, `place_piece`, `remove_piece`~~ DONE

## Future Work

- Ranged combat (bows/crossbows): draw-aim-release flow
- Boat and cart control
- Map/minimap awareness
- Multiplayer communication
