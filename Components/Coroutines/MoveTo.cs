﻿using System;
using Trinity.Framework;
using System.Threading.Tasks;
using Buddy.Coroutines;
using Trinity.DbProvider;
using Trinity.Framework.Reference;
using Zeta.Bot.Navigation;
using Zeta.Common;
using Zeta.Game;


namespace Trinity.Components.Coroutines
{
    public class MoveTo
    {
        private static int _startingWorldId;

        /// <summary>
        /// Moves to somewhere.
        /// </summary>
        /// <param name="location">where to move to</param>
        /// <param name="destinationName">name of location for debugging purposes</param>
        /// <param name="range">how close it should get</param>
        /// <param name="stopCondition"></param>
        public static async Task<bool> Execute(Vector3 location, string destinationName = "", float range = 10f, Func<bool> stopCondition = null)
        {
            if (string.IsNullOrEmpty(destinationName))
                destinationName = location.ToString();

            _startingWorldId = ZetaDia.Globals.WorldSnoId;

            if (Core.Player.IsInTown)
            {
                GameUI.CloseVendorWindow();
            }

            await Coroutine.Wait(TimeSpan.MaxValue, () => !(ZetaDia.Me.LoopingAnimationEndTime > 0));
            
            Navigator.PlayerMover.MoveTowards(location);

            while (ZetaDia.IsInGame && location.Distance2D(ZetaDia.Me.Position) >= range && !ZetaDia.Me.IsDead)
            {
                if (Navigator.StuckHandler.IsStuck)
                {
                    await Navigator.StuckHandler.DoUnstick();
                    Core.Logger.Verbose("MoveTo Finished. (Stuck)", _startingWorldId, ZetaDia.Globals.WorldSnoId);
                    break;
                }

                if (stopCondition != null && stopCondition())
                {
                    Core.Logger.Verbose("MoveTo Finished. (Stop Condition)", _startingWorldId, ZetaDia.Globals.WorldSnoId);
                    return false;
                }

                if (_startingWorldId != ZetaDia.Globals.WorldSnoId)
                {
                    Core.Logger.Verbose("MoveTo Finished. World Changed from {0} to {1}", _startingWorldId, ZetaDia.Globals.WorldSnoId);
                    return false;
                }

                Core.Logger.Verbose("Moving to " + destinationName);
                await Coroutine.Wait(1000, () => PlayerMover.MoveTo(location) == MoveResult.ReachedDestination);
            }

            var distance = location.Distance(ZetaDia.Me.Position);
            if (distance <= range)
                Navigator.PlayerMover.MoveStop();

            Core.Logger.Verbose("MoveTo Finished. Distance={0}", distance);
            return true;
        }
    }
}
