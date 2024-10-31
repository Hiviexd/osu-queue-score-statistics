// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Linq;
using JetBrains.Annotations;
using MySqlConnector;
using osu.Game.Beatmaps;
using osu.Game.Rulesets;
using osu.Game.Rulesets.Mods;
using osu.Server.Queues.ScoreStatisticsProcessor.Models;

namespace osu.Server.Queues.ScoreStatisticsProcessor.Processors
{
    /// <summary>
    /// Adjust max combo (if required) for the user.
    /// </summary>
    [UsedImplicitly]
    public class MaxComboProcessor : IProcessor
    {
        public bool RunOnFailedScores => false;

        public bool RunOnLegacyScores => false;

        public void RevertFromUserStats(SoloScore score, UserStats userStats, int previousVersion, MySqlConnection conn, MySqlTransaction transaction)
        {
            // TODO: this will require access to stable scores to be implemented correctly.
        }

        public void ApplyToUserStats(SoloScore score, UserStats userStats, MySqlConnection conn, MySqlTransaction transaction)
        {
            Ruleset ruleset = ScoreStatisticsQueueProcessor.AVAILABLE_RULESETS.Single(r => r.RulesetInfo.OnlineID == score.ruleset_id);

            // Automation mods should not count towards max combo statistic.
            if (score.ScoreData.Mods.Select(m => m.ToMod(ruleset)).Any(m => m.Type == ModType.Automation))
                return;

            if (score.beatmap == null || score.beatmap.approved < BeatmapOnlineStatus.Ranked)
                return;

            // TODO: assert the user's score is not higher than the max combo for the beatmap.
            userStats.max_combo = Math.Max(userStats.max_combo, (ushort)score.max_combo);
        }

        public void ApplyGlobal(SoloScore score, MySqlConnection conn)
        {
        }
    }
}
