using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Lib.GAB.Tools;
using UnityEngine;
using ValBridgeServer;

namespace ValBridgeServer.Tools
{
    public class PlayerTools
    {
        [Tool("player_get_health", Description = "Get player's current health statistics")]
        public object GetHealth()
        {
            var player = Player.m_localPlayer;
            if (player == null)
                return new { success = false, error = "No local player found" };

            return new
            {
                success = true,
                health = player.GetHealth(),
                maxHealth = player.GetMaxHealth(),
                healthPercentage = player.GetHealthPercentage() * 100f
            };
        }

        [Tool("get_player_state", Description = "Get a full snapshot of the player: health, stamina, position, biome, equipped weapon, status effects, and food buffs.")]
        public object GetPlayerState()
        {
            var player = Player.m_localPlayer;
            if (player == null)
                return new { success = false, error = "No local player found" };

            var pos = player.transform.position;
            var lookDir = player.GetLookDir();

            // Status effects
            var effects = player.GetSEMan().GetStatusEffects();
            var effectList = effects.Select(se => new
            {
                name = se.m_name,
                timeRemaining = se.m_ttl > 0 ? (float?)Math.Round(se.GetRemaningTime(), 1) : null
            }).ToList();

            // Food
            var foods = player.GetFoods();
            var foodList = foods.Select(f => new
            {
                name = f.m_name,
                health = (float)Math.Round(f.m_health, 1),
                stamina = (float)Math.Round(f.m_stamina, 1),
                timeRemaining = (float)Math.Round(f.m_time, 1)
            }).ToList();

            // Equipped weapon
            var weapon = player.GetCurrentWeapon();
            object? weaponInfo = null;
            if (weapon != null && weapon.m_dropPrefab != null)
            {
                weaponInfo = new
                {
                    name = weapon.m_dropPrefab.name,
                    durability = (float)Math.Round(weapon.GetDurabilityPercentage() * 100f, 1)
                };
            }

            return new
            {
                success = true,
                health = (float)Math.Round(player.GetHealth(), 1),
                maxHealth = (float)Math.Round(player.GetMaxHealth(), 1),
                stamina = (float)Math.Round(player.GetStamina(), 1),
                maxStamina = (float)Math.Round(player.GetMaxStamina(), 1),
                position = new { x = pos.x, y = pos.y, z = pos.z },
                lookDirection = new { x = lookDir.x, y = lookDir.y, z = lookDir.z },
                biome = player.GetCurrentBiome().ToString(),
                weapon = weaponInfo,
                statusEffects = effectList,
                foods = foodList
            };
        }

        [Tool("get_inventory", Description = "List all items in the player's inventory with name, quantity, slot, equipped status, and durability.")]
        public object GetInventory()
        {
            var player = Player.m_localPlayer;
            if (player == null)
                return new { success = false, error = "No local player found" };

            var inv = player.GetInventory();
            var items = inv.GetAllItems();

            var itemList = items.Select(item => new
            {
                name = item.m_dropPrefab?.name ?? item.m_shared.m_name,
                quantity = item.m_stack,
                slot = new { x = item.m_gridPos.x, y = item.m_gridPos.y },
                equipped = item.m_equipped,
                durability = item.m_shared.m_maxDurability > 0
                    ? (float?)Math.Round(item.GetDurabilityPercentage() * 100f, 1)
                    : null,
                type = item.m_shared.m_itemType.ToString()
            }).ToList();

            return new
            {
                success = true,
                count = itemList.Count,
                totalWeight = (float)Math.Round(inv.GetTotalWeight(), 1),
                emptySlots = inv.GetEmptySlots(),
                items = itemList
            };
        }

        [Tool("player_get_position", Description = "Get player's world position coordinates")]
        public object GetPosition()
        {
            var player = Player.m_localPlayer;
            if (player == null)
                return new { success = false, error = "No local player found" };

            var pos = player.transform.position;
            return new
            {
                success = true,
                position = new { x = pos.x, y = pos.y, z = pos.z }
            };
        }

        [Tool("get_visible_objects", Description = "Get objects visible to the player using camera frustum and line-of-sight raycasting. Returns what the player can actually see.")]
        public object GetVisibleObjects(
            [ToolParameter(Description = "Max distance to check (default 50)")] float range = 50f,
            [ToolParameter(Description = "Max objects to return (default 25)")] int limit = 25)
        {
            var player = Player.m_localPlayer;
            if (player == null)
                return new { success = false, error = "No local player found" };

            var tcs = new TaskCompletionSource<object>();

            MainThreadDispatcher.Instance.Enqueue(() =>
            {
                try
                {
                    var cam = Camera.main;
                    if (cam == null)
                    {
                        tcs.SetResult(new { success = false, error = "No main camera found" });
                        return;
                    }

                    var playerPos = player.transform.position;
                    var colliders = Physics.OverlapSphere(playerPos, range);
                    var seen = new HashSet<int>();
                    var results = new List<(float dist, object data)>();

                    foreach (var col in colliders)
                    {
                        if (col == null) continue;

                        var go = col.gameObject;
                        var root = go.transform.root.gameObject;

                        var id = root.GetInstanceID();
                        if (!seen.Add(id)) continue;

                        // Skip the player themselves
                        if (root == player.gameObject) continue;

                        var objPos = root.transform.position;

                        // Check if object is within camera frustum
                        var viewportPoint = cam.WorldToViewportPoint(objPos);
                        if (viewportPoint.z < 0f || viewportPoint.x < 0f || viewportPoint.x > 1f || viewportPoint.y < 0f || viewportPoint.y > 1f)
                            continue;

                        // Raycast from camera to object to check line-of-sight
                        var camPos = cam.transform.position;
                        var dir = objPos - camPos;
                        var dist = dir.magnitude;
                        if (Physics.Raycast(camPos, dir.normalized, out var hit, dist))
                        {
                            // Check if we hit the target object or one of its children
                            if (hit.collider.gameObject.transform.root.gameObject != root)
                                continue; // Occluded by something else
                        }

                        var objDist = Vector3.Distance(playerPos, objPos);

                        // Detect type
                        string? type = null;
                        if (root.GetComponent<TreeBase>() != null)
                            type = "TreeBase";
                        else if (root.GetComponent<Destructible>() != null)
                            type = "Destructible";
                        else if (root.GetComponent<TreeLog>() != null)
                            type = "TreeLog";
                        else if (root.GetComponent<MineRock>() != null)
                            type = "MineRock";
                        else if (root.GetComponent<Character>() != null)
                            type = "Character";
                        else if (root.GetComponent<Piece>() != null)
                            type = "Piece";
                        else if (root.GetComponent<ItemDrop>() != null)
                            type = "ItemDrop";

                        results.Add((objDist, new
                        {
                            name = root.name,
                            instanceId = id,
                            type,
                            position = new { x = objPos.x, y = objPos.y, z = objPos.z },
                            distance = (float)Math.Round(objDist, 2),
                            screenPosition = new { x = (float)Math.Round(viewportPoint.x, 3), y = (float)Math.Round(viewportPoint.y, 3) }
                        }));
                    }

                    var sorted = results
                        .OrderBy(r => r.dist)
                        .Take(limit)
                        .Select(r => r.data)
                        .ToList();

                    tcs.SetResult(new
                    {
                        success = true,
                        count = sorted.Count,
                        playerPosition = new { x = playerPos.x, y = playerPos.y, z = playerPos.z },
                        cameraForward = new { x = cam.transform.forward.x, y = cam.transform.forward.y, z = cam.transform.forward.z },
                        range,
                        objects = sorted
                    });
                }
                catch (Exception ex)
                {
                    tcs.SetResult(new { success = false, error = ex.Message });
                }
            });

            return tcs.Task.Result;
        }

        [Tool("primary_attack", Description = "Perform a single primary attack (left click) with the current weapon. Returns immediately after starting the attack.")]
        public object PrimaryAttack()
        {
            var player = Player.m_localPlayer;
            if (player == null)
                return new { success = false, error = "No local player found" };

            var tcs = new TaskCompletionSource<object>();

            MainThreadDispatcher.Instance.Enqueue(() =>
            {
                try
                {
                    var result = player.StartAttack(null, false);
                    tcs.SetResult(new
                    {
                        success = result,
                        message = result ? "Primary attack started" : "Cannot attack (no weapon, mid-animation, or stunned)"
                    });
                }
                catch (Exception ex)
                {
                    tcs.SetResult(new { success = false, error = ex.Message });
                }
            });

            return tcs.Task.Result;
        }

        [Tool("secondary_attack", Description = "Perform a single secondary attack (right click) with the current weapon. Alt-attack, parry, or special depending on weapon.")]
        public object SecondaryAttack()
        {
            var player = Player.m_localPlayer;
            if (player == null)
                return new { success = false, error = "No local player found" };

            var tcs = new TaskCompletionSource<object>();

            MainThreadDispatcher.Instance.Enqueue(() =>
            {
                try
                {
                    var result = player.StartAttack(null, true);
                    tcs.SetResult(new
                    {
                        success = result,
                        message = result ? "Secondary attack started" : "Cannot attack (no weapon, no secondary attack, mid-animation, or stunned)"
                    });
                }
                catch (Exception ex)
                {
                    tcs.SetResult(new { success = false, error = ex.Message });
                }
            });

            return tcs.Task.Result;
        }

        [Tool("block", Description = "Start or stop blocking with the current shield/weapon. Blocking within 0.25s of an incoming hit triggers a parry.")]
        public object Block(
            [ToolParameter(Description = "true to start blocking, false to stop")] bool active)
        {
            var player = Player.m_localPlayer;
            if (player == null)
                return new { success = false, error = "No local player found" };

            var tcs = new TaskCompletionSource<object>();

            MainThreadDispatcher.Instance.Enqueue(() =>
            {
                try
                {
                    player.SetControls(
                        Vector3.zero,
                        attack: false, attackHold: false,
                        secondaryAttack: false, secondaryAttackHold: false,
                        block: false, blockHold: active,
                        jump: false, crouch: false, run: false, autoRun: false);

                    tcs.SetResult(new
                    {
                        success = true,
                        message = active ? "Blocking started" : "Blocking stopped",
                        isBlocking = player.IsBlocking()
                    });
                }
                catch (Exception ex)
                {
                    tcs.SetResult(new { success = false, error = ex.Message });
                }
            });

            return tcs.Task.Result;
        }

        [Tool("look_at_position", Description = "Face the player toward a world position (XZ plane).")]
        public object LookAtPosition(
            [ToolParameter(Description = "X coordinate to look at")] float x,
            [ToolParameter(Description = "Z coordinate to look at")] float z)
        {
            var player = Player.m_localPlayer;
            if (player == null)
                return new { success = false, error = "No local player found" };

            var tcs = new TaskCompletionSource<object>();

            MainThreadDispatcher.Instance.Enqueue(() =>
            {
                try
                {
                    var pos = player.transform.position;
                    var dir = new Vector3(x - pos.x, 0f, z - pos.z);
                    if (dir.magnitude < 0.01f)
                    {
                        tcs.SetResult(new { success = false, error = "Target position is too close to player" });
                        return;
                    }
                    dir.Normalize();
                    player.SetLookDir(dir);
                    player.FaceLookDirection();
                    tcs.SetResult(new
                    {
                        success = true,
                        message = $"Now facing ({x:F1}, {z:F1})"
                    });
                }
                catch (Exception ex)
                {
                    tcs.SetResult(new { success = false, error = ex.Message });
                }
            });

            return tcs.Task.Result;
        }

        [Tool("look_at_object", Description = "Face the player toward a specific object by instanceId.")]
        public object LookAtObject(
            [ToolParameter(Description = "Instance ID of the target object (from find_nearby_prefabs or get_visible_objects)")] int instanceId)
        {
            var player = Player.m_localPlayer;
            if (player == null)
                return new { success = false, error = "No local player found" };

            var tcs = new TaskCompletionSource<object>();

            MainThreadDispatcher.Instance.Enqueue(() =>
            {
                try
                {
                    var playerPos = player.transform.position;
                    var colliders = Physics.OverlapSphere(playerPos, 100f);
                    var seen = new HashSet<int>();
                    GameObject? target = null;

                    foreach (var col in colliders)
                    {
                        if (col == null) continue;
                        var root = col.gameObject.transform.root.gameObject;
                        if (seen.Add(root.GetInstanceID()) && root.GetInstanceID() == instanceId)
                        {
                            target = root;
                            break;
                        }
                    }

                    if (target == null)
                    {
                        tcs.SetResult(new { success = false, error = $"No GameObject found with instanceId {instanceId} within range" });
                        return;
                    }

                    var dir = target.transform.position - playerPos;
                    dir.y = 0f;
                    if (dir.magnitude < 0.01f)
                    {
                        tcs.SetResult(new { success = false, error = "Target is too close to player" });
                        return;
                    }
                    dir.Normalize();
                    player.SetLookDir(dir);
                    player.FaceLookDirection();
                    tcs.SetResult(new
                    {
                        success = true,
                        message = $"Now facing {target.name}",
                        targetPosition = new { x = target.transform.position.x, y = target.transform.position.y, z = target.transform.position.z }
                    });
                }
                catch (Exception ex)
                {
                    tcs.SetResult(new { success = false, error = ex.Message });
                }
            });

            return tcs.Task.Result;
        }

        [Tool("interact", Description = "Interact with the object the player is looking at (E key). Opens doors, picks up items, activates crafting stations, etc.")]
        public object Interact(
            [ToolParameter(Description = "Hold interact instead of tap (default false)")] bool hold = false)
        {
            var player = Player.m_localPlayer;
            if (player == null)
                return new { success = false, error = "No local player found" };

            var tcs = new TaskCompletionSource<object>();

            MainThreadDispatcher.Instance.Enqueue(() =>
            {
                try
                {
                    var hover = player.GetHoverObject();
                    if (hover == null)
                    {
                        tcs.SetResult(new { success = false, error = "Nothing in range to interact with" });
                        return;
                    }

                    var interactable = hover.GetComponentInParent<Interactable>();
                    if (interactable == null)
                    {
                        tcs.SetResult(new { success = false, error = $"Object '{hover.name}' is not interactable" });
                        return;
                    }

                    var result = interactable.Interact(player, hold, false);
                    tcs.SetResult(new
                    {
                        success = result,
                        message = result ? $"Interacted with {hover.name}" : $"Interaction with {hover.name} failed",
                        target = hover.name
                    });
                }
                catch (Exception ex)
                {
                    tcs.SetResult(new { success = false, error = ex.Message });
                }
            });

            return tcs.Task.Result;
        }

        [Tool("pickup_nearby", Description = "Pick up loose item drops within range of the player.")]
        public object PickupNearby(
            [ToolParameter(Description = "Pickup radius in meters (default 5)")] float range = 5f)
        {
            var player = Player.m_localPlayer;
            if (player == null)
                return new { success = false, error = "No local player found" };

            var tcs = new TaskCompletionSource<object>();

            MainThreadDispatcher.Instance.Enqueue(() =>
            {
                try
                {
                    var origin = player.transform.position + Vector3.up;
                    var colliders = Physics.OverlapSphere(origin, range, LayerMask.GetMask("item"));
                    var pickedUp = new List<string>();

                    foreach (var col in colliders)
                    {
                        if (col == null || col.attachedRigidbody == null) continue;

                        var itemDrop = col.attachedRigidbody.GetComponent<ItemDrop>();
                        if (itemDrop == null) continue;

                        var znv = itemDrop.GetComponent<ZNetView>();
                        if (znv == null || !znv.IsValid()) continue;

                        itemDrop.Load();
                        if (player.GetInventory().CanAddItem(itemDrop.m_itemData))
                        {
                            if (player.Pickup(itemDrop.gameObject, true, false))
                            {
                                pickedUp.Add(itemDrop.m_itemData.m_dropPrefab?.name ?? itemDrop.m_itemData.m_shared.m_name);
                            }
                        }
                    }

                    tcs.SetResult(new
                    {
                        success = true,
                        pickedUpCount = pickedUp.Count,
                        items = pickedUp,
                        message = pickedUp.Count > 0 ? $"Picked up {pickedUp.Count} items" : "No items to pick up"
                    });
                }
                catch (Exception ex)
                {
                    tcs.SetResult(new { success = false, error = ex.Message });
                }
            });

            return tcs.Task.Result;
        }

        [Tool("find_nearby_prefabs", Description = "Find prefab instances near the player by name. Useful for locating trees, rocks, enemies, and other world objects within range.")]
        public object FindNearbyPrefabs(
            [ToolParameter(Description = "Prefab name to search for (partial match, case-insensitive). E.g. 'Beech', 'Rock', 'Boar'")] string prefabName,
            [ToolParameter(Description = "Search radius in meters (default 30)")] float range = 30f)
        {
            var player = Player.m_localPlayer;
            if (player == null)
                return new { success = false, error = "No local player found" };

            var tcs = new TaskCompletionSource<object>();
            var playerPos = player.transform.position;

            MainThreadDispatcher.Instance.Enqueue(() =>
            {
                try
                {
                    var colliders = Physics.OverlapSphere(playerPos, range);
                    var filter = prefabName.ToLowerInvariant();
                    var seen = new HashSet<int>();
                    var results = new List<(float dist, object data)>();

                    foreach (var col in colliders)
                    {
                        if (col == null) continue;

                        // Walk up to find the root prefab instance
                        var go = col.gameObject;
                        var root = go.transform.root.gameObject;

                        // Check both the hit object and its root
                        GameObject? match = null;
                        if (root.name.ToLowerInvariant().Contains(filter))
                            match = root;
                        else if (go.name.ToLowerInvariant().Contains(filter))
                            match = go;

                        if (match == null) continue;

                        var id = match.GetInstanceID();
                        if (!seen.Add(id)) continue;

                        var pos = match.transform.position;
                        var dist = Vector3.Distance(playerPos, pos);

                        // Detect interactable type
                        string? type = null;
                        if (match.GetComponent<TreeBase>() != null)
                            type = "TreeBase";
                        else if (match.GetComponent<Destructible>() != null)
                            type = "Destructible";
                        else if (match.GetComponent<TreeLog>() != null)
                            type = "TreeLog";
                        else if (match.GetComponent<MineRock>() != null)
                            type = "MineRock";
                        else if (match.GetComponent<Character>() != null)
                            type = "Character";

                        results.Add((dist, new
                        {
                            name = match.name,
                            instanceId = id,
                            position = new { x = pos.x, y = pos.y, z = pos.z },
                            distance = (float)Math.Round(dist, 2),
                            type
                        }));
                    }

                    var sorted = results
                        .OrderBy(r => r.dist)
                        .Select(r => r.data)
                        .ToList();

                    tcs.SetResult(new
                    {
                        success = true,
                        count = sorted.Count,
                        playerPosition = new { x = playerPos.x, y = playerPos.y, z = playerPos.z },
                        range,
                        prefabs = sorted
                    });
                }
                catch (Exception ex)
                {
                    tcs.SetResult(new { success = false, error = ex.Message });
                }
            });

            return tcs.Task.Result;
        }

        [Tool("attack_target", Description = "Attack a target with the current weapon until destroyed. Use find_nearby_prefabs to get instanceId.")]
        public object AttackTarget(
            [ToolParameter(Description = "Instance ID of the target GameObject (from find_nearby_prefabs)")] int instanceId,
            [ToolParameter(Description = "Timeout in seconds (default 30)")] float timeout = 30f)
        {
            var player = Player.m_localPlayer;
            if (player == null)
                return new { success = false, error = "No local player found" };

            var tcs = new TaskCompletionSource<object>();

            MainThreadDispatcher.Instance.Enqueue(() =>
            {
                try
                {
                    // Find the target GameObject by instance ID using physics overlap
                    var playerPos = player.transform.position;
                    var colliders = Physics.OverlapSphere(playerPos, 100f);
                    var seen = new HashSet<int>();
                    GameObject? target = null;

                    foreach (var col in colliders)
                    {
                        if (col == null) continue;
                        var root = col.gameObject.transform.root.gameObject;
                        if (seen.Add(root.GetInstanceID()) && root.GetInstanceID() == instanceId)
                        {
                            target = root;
                            break;
                        }
                    }

                    if (target == null)
                    {
                        tcs.SetResult(new { success = false, error = $"No GameObject found with instanceId {instanceId} within range" });
                        return;
                    }

                    // Start attacking on the main thread, then bridge the result
                    var attackTask = AttackManager.Instance.StartAttacking(target, timeout);
                    attackTask.ContinueWith(t => tcs.TrySetResult(t.Result));
                }
                catch (Exception ex)
                {
                    tcs.SetResult(new { success = false, error = ex.Message });
                }
            });

            return tcs.Task.Result;
        }

        [Tool("navigate_to_position", Description = "Walk the player to a world position using pathfinding. Returns when arrived or on failure.")]
        public object NavigateToPosition(
            [ToolParameter(Description = "X coordinate")] float x,
            [ToolParameter(Description = "Z coordinate")] float z,
            [ToolParameter(Description = "Timeout in seconds (default 60)")] float timeout = 60f)
        {
            var player = Player.m_localPlayer;
            if (player == null)
                return new { success = false, error = "No local player found" };

            var target = new Vector3(x, player.transform.position.y, z);

            var task = NavigationManager.Instance.StartNavigation(target, timeout);
            return task.Result;
        }
    }
}
