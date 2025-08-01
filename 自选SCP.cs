using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Exiled.API.Enums;
using Exiled.API.Features;
using Exiled.API.Features.Items;
using Exiled.API.Features.Pickups;
using Exiled.API.Interfaces;
using Exiled.Events.EventArgs.Player;
using Exiled.Events.EventArgs.Server;
using MEC;
using PlayerRoles;
using UnityEngine;

namespace ClassDRebellionIndicator
{
    public class RebellionSystem : Plugin<RebellionSystem.PluginConfig>
    {
        public static RebellionSystem Instance { get; private set; }
        public override string Name => "ClassDRebellionIndicator";
        public override string Author => "YourName";
        public override Version Version => new Version(1, 2, 1); // 版本更新
        public override PluginPriority Priority => PluginPriority.Medium;

        private Dictionary<Player, RebellionData> _rebellionData = new Dictionary<Player, RebellionData>();
        private Dictionary<Player, CoroutineHandle> _statusHandles = new Dictionary<Player, CoroutineHandle>();

        private HashSet<ItemType> _contrabandItems = new HashSet<ItemType>
        {
            ItemType.GunCOM15,
            ItemType.GunCOM18,
            ItemType.GunE11SR,
            ItemType.GunCrossvec,
            ItemType.GunFSP9,
            ItemType.GunLogicer,
            ItemType.GunRevolver,
            ItemType.GunShotgun,
            ItemType.GunAK,
            ItemType.GunCom45
        };

        public class PluginConfig : IConfig
        {
            [Description("是否启用插件")]
            public bool IsEnabled { get; set; } = true;

            [Description("调试模式")]
            public bool Debug { get; set; } = false;

            [Description("造反所需点数")]
            public int RebellionThreshold { get; set; } = 200;

            [Description("造反嫌疑阈值")]
            public int SuspicionThreshold { get; set; } = 100;

            [Description("拾取违禁品点数")]
            public int PickupContrabandPoints { get; set; } = 100;

            [Description("持有违禁品每秒增加点数")]
            public int HoldingContrabandPerSecond { get; set; } = 2;

            [Description("附近有人持有违禁品每秒增加点数")]
            public int NearbyContrabandPerSecond { get; set; } = 2;

            [Description("附近D级/混沌伤害基金会阵营每人获得点数")]
            public int NearbyRebelDamagePoints { get; set; } = 20;

            [Description("对基金会阵营造成伤害的D级获得点数")]
            public int DealDamageToFoundationPoints { get; set; } = 999;

            [Description("无违禁品时每秒减少点数")]
            public int NoContrabandReductionPerSecond { get; set; } = 2;
        }

        public class RebellionData
        {
            public int RebellionPoints { get; set; } = 0;
            public bool HasRebelled { get; set; } = false;
            public string OriginalNickname { get; set; } = string.Empty;
            public bool IsClassD { get; set; } = false;
            public int LastDisplayedPoints { get; set; } = -1;
            public string LastSuffix { get; set; } = string.Empty; // 跟踪上次的后缀
        }

        public override void OnEnabled()
        {
            Instance = this;

            Exiled.Events.Handlers.Player.Verified += OnPlayerVerified;
            Exiled.Events.Handlers.Player.ChangingRole += OnPlayerChangingRole;
            Exiled.Events.Handlers.Player.Destroying += OnPlayerDestroying;
            Exiled.Events.Handlers.Player.PickingUpItem += OnPickingUpItem;
            Exiled.Events.Handlers.Player.DroppingItem += OnDroppingItem;
            Exiled.Events.Handlers.Player.Hurting += OnHurting;
            Exiled.Events.Handlers.Server.RoundEnded += OnRoundEnded;
            Exiled.Events.Handlers.Server.RestartingRound += OnRoundRestarting;

            Log.Info("Class-D造反指示器已启用！");
        }

        public override void OnDisabled()
        {
            Exiled.Events.Handlers.Player.Verified -= OnPlayerVerified;
            Exiled.Events.Handlers.Player.ChangingRole -= OnPlayerChangingRole;
            Exiled.Events.Handlers.Player.Destroying -= OnPlayerDestroying;
            Exiled.Events.Handlers.Player.PickingUpItem -= OnPickingUpItem;
            Exiled.Events.Handlers.Player.DroppingItem -= OnDroppingItem;
            Exiled.Events.Handlers.Player.Hurting -= OnHurting;
            Exiled.Events.Handlers.Server.RoundEnded -= OnRoundEnded;
            Exiled.Events.Handlers.Server.RestartingRound -= OnRoundRestarting;

            foreach (var handle in _statusHandles.Values)
            {
                Timing.KillCoroutines(handle);
            }
            _statusHandles.Clear();

            // 还原所有玩家的昵称
            foreach (var player in Player.List)
            {
                if (_rebellionData.TryGetValue(player, out var data) && !string.IsNullOrEmpty(data.OriginalNickname))
                {
                    player.DisplayNickname = data.OriginalNickname;
                }
            }

            _rebellionData.Clear();
            Instance = null;
            Log.Info("Class-D造反指示器已禁用！");
        }

        private void OnPlayerVerified(VerifiedEventArgs ev)
        {
            // 保存玩家原始昵称
            _rebellionData[ev.Player] = new RebellionData
            {
                OriginalNickname = ev.Player.Nickname
            };
        }

        private void OnPlayerChangingRole(ChangingRoleEventArgs ev)
        {
            if (!_rebellionData.TryGetValue(ev.Player, out var data)) return;

            bool isClassD = ev.NewRole == RoleTypeId.ClassD;
            data.IsClassD = isClassD;

            if (isClassD)
            {
                // 确保记录原始昵称
                if (string.IsNullOrEmpty(data.OriginalNickname))
                {
                    data.OriginalNickname = ev.Player.Nickname;
                }

                if (!_statusHandles.ContainsKey(ev.Player))
                {
                    var handle = Timing.RunCoroutine(UpdateRebellionStatus(ev.Player));
                    _statusHandles[ev.Player] = handle;
                }
                UpdateNicknameSuffix(ev.Player, data);
            }
            else
            {
                // 玩家不再是D级，还原昵称
                if (_statusHandles.TryGetValue(ev.Player, out var handle))
                {
                    Timing.KillCoroutines(handle);
                    _statusHandles.Remove(ev.Player);
                }

                if (!string.IsNullOrEmpty(data.OriginalNickname))
                {
                    ev.Player.DisplayNickname = data.OriginalNickname;
                }

                // 重置数据
                data.RebellionPoints = 0;
                data.HasRebelled = false;
                data.LastSuffix = string.Empty;
            }
        }

        private void OnPlayerDestroying(DestroyingEventArgs ev)
        {
            if (_statusHandles.TryGetValue(ev.Player, out var handle))
            {
                Timing.KillCoroutines(handle);
                _statusHandles.Remove(ev.Player);
            }

            if (_rebellionData.TryGetValue(ev.Player, out var data) && !string.IsNullOrEmpty(data.OriginalNickname))
            {
                ev.Player.DisplayNickname = data.OriginalNickname;
            }

            _rebellionData.Remove(ev.Player);
        }

        private void OnPickingUpItem(PickingUpItemEventArgs ev)
        {
            if (!_rebellionData.TryGetValue(ev.Player, out var data) || !data.IsClassD) return;

            if (_contrabandItems.Contains(ev.Pickup.Type))
            {
                data.RebellionPoints += Instance.Config.PickupContrabandPoints;
                CheckRebellion(ev.Player, data);
                UpdateNicknameSuffix(ev.Player, data);
            }
        }

        private void OnDroppingItem(DroppingItemEventArgs ev)
        {
            if (!_rebellionData.TryGetValue(ev.Player, out var data) || !data.IsClassD) return;

            if (_contrabandItems.Contains(ev.Item.Type))
            {
                // 丢弃违禁品不扣分
            }
        }

        private void OnHurting(HurtingEventArgs ev)
        {
            if (ev.Attacker == null || ev.Attacker == ev.Player) return;
            if (!_rebellionData.TryGetValue(ev.Attacker, out var attackerData)) return;

            bool isRebel = attackerData.IsClassD ||
                          ev.Attacker.Role.Team == Team.ChaosInsurgency;

            bool isFoundation = ev.Player.Role.Team == Team.FoundationForces ||
                               ev.Player.Role.Team == Team.Scientists;

            if (isRebel && isFoundation)
            {
                if (attackerData.IsClassD)
                {
                    attackerData.RebellionPoints += Instance.Config.DealDamageToFoundationPoints;
                    CheckRebellion(ev.Attacker, attackerData);
                    UpdateNicknameSuffix(ev.Attacker, attackerData);
                }

                foreach (var player in Player.List)
                {
                    if (player == ev.Attacker) continue;
                    if (!_rebellionData.TryGetValue(player, out var nearbyData)) continue;

                    if (nearbyData.IsClassD && Vector3.Distance(player.Position, ev.Attacker.Position) <= 10f)
                    {
                        nearbyData.RebellionPoints += Instance.Config.NearbyRebelDamagePoints;
                        CheckRebellion(player, nearbyData);
                        UpdateNicknameSuffix(player, nearbyData);
                    }
                }
            }
        }

        private void OnRoundEnded(RoundEndedEventArgs ev)
        {
            // 还原所有玩家的昵称
            foreach (var player in Player.List)
            {
                if (_rebellionData.TryGetValue(player, out var data) && !string.IsNullOrEmpty(data.OriginalNickname))
                {
                    player.DisplayNickname = data.OriginalNickname;
                }
            }

            // 重置数据
            foreach (var data in _rebellionData.Values)
            {
                data.RebellionPoints = 0;
                data.HasRebelled = false;
                data.LastSuffix = string.Empty;
            }
        }

        private void OnRoundRestarting()
        {
            foreach (var handle in _statusHandles.Values)
            {
                Timing.KillCoroutines(handle);
            }
            _statusHandles.Clear();
        }

        private IEnumerator<float> UpdateRebellionStatus(Player player)
        {
            while (player.IsConnected)
            {
                if (!_rebellionData.TryGetValue(player, out var data))
                {
                    yield return Timing.WaitForSeconds(1f);
                    continue;
                }

                if (!data.HasRebelled)
                {
                    bool hasContraband = player.Items.Any(item => _contrabandItems.Contains(item.Type));

                    bool nearbyContraband = Player.List
                        .Where(p => p != player && p.IsAlive &&
                                  (p.Role.Team == Team.ClassD ||
                                   p.Role.Team == Team.ChaosInsurgency))
                        .Any(p => Vector3.Distance(p.Position, player.Position) <= 10f &&
                                 p.Items.Any(item => _contrabandItems.Contains(item.Type)));

                    if (hasContraband)
                    {
                        data.RebellionPoints += Instance.Config.HoldingContrabandPerSecond;
                    }
                    else if (nearbyContraband)
                    {
                        data.RebellionPoints += Instance.Config.NearbyContrabandPerSecond;
                    }
                    else
                    {
                        data.RebellionPoints = Math.Max(0, data.RebellionPoints - Instance.Config.NoContrabandReductionPerSecond);
                    }

                    CheckRebellion(player, data);
                    UpdateNicknameSuffix(player, data);
                }

                yield return Timing.WaitForSeconds(0.5f);
            }
        }

        private void UpdateNicknameSuffix(Player player, RebellionData data)
        {
            // 生成新的后缀
            string newSuffix;
            if (data.HasRebelled)
            {
                newSuffix = "[已造反]";
            }
            else if (data.RebellionPoints >= Config.RebellionThreshold)
            {
                data.HasRebelled = true;
                newSuffix = "[已造反]";
                player.Broadcast(3, $"基金会工作人员请注意: {player.Nickname} 作为D级人员已造反！");
            }
            else if (data.RebellionPoints < Config.SuspicionThreshold)
            {
                newSuffix = $"({data.RebellionPoints}/200)[守法好DD]";
            }
            else
            {
                newSuffix = $"({data.RebellionPoints}/200)[造反嫌疑]";
            }

            // 如果后缀没有变化，不更新昵称
            if (data.LastSuffix == newSuffix)
            {
                return;
            }

            // 更新昵称
            data.LastSuffix = newSuffix;

            // 确保不超过最大长度限制
            string newNickname = $"{data.OriginalNickname}{newSuffix}";
            if (newNickname.Length > 31) // SCP:SL昵称最大长度限制
            {
                int maxBaseLength = 31 - newSuffix.Length;
                if (maxBaseLength > 0)
                {
                    newNickname = $"{data.OriginalNickname.Substring(0, maxBaseLength)}{newSuffix}";
                }
                else
                {
                    newNickname = newSuffix;
                }
            }

            player.DisplayNickname = newNickname;
        }

        private void CheckRebellion(Player player, RebellionData data)
        {
            if (data.HasRebelled) return;

            if (data.RebellionPoints >= Config.RebellionThreshold)
            {
                data.HasRebelled = true;
                UpdateNicknameSuffix(player, data);

                if (Instance.Config.Debug)
                {
                    Log.Debug($"{player.Nickname} 已造反！");
                }
            }
        }
    }
}