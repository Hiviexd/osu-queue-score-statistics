// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using MySqlConnector;
using osu.Framework.IO.Network;
using osu.Game.Beatmaps;
using osu.Game.Beatmaps.Legacy;
using osu.Game.Online.API.Requests.Responses;
using osu.Game.Rulesets;
using osu.Game.Rulesets.Difficulty;
using osu.Game.Rulesets.Mods;
using osu.Server.Queues.ScoreStatisticsProcessor.Helpers;
using osu.Server.Queues.ScoreStatisticsProcessor.Models;
using Beatmap = osu.Server.Queues.ScoreStatisticsProcessor.Models.Beatmap;

namespace osu.Server.Queues.ScoreStatisticsProcessor.Stores
{
    /// <summary>
    /// A store for retrieving <see cref="Models.Beatmap"/>s.
    /// </summary>
    public class BeatmapStore
    {
        private static readonly bool use_realtime_difficulty_calculation = Environment.GetEnvironmentVariable("REALTIME_DIFFICULTY") != "0";
        private static readonly string beatmap_download_path = Environment.GetEnvironmentVariable("BEATMAP_DOWNLOAD_PATH") ?? "https://osu.ppy.sh/osu/{0}";

        private readonly ConcurrentDictionary<int, Beatmap?> beatmapCache = new ConcurrentDictionary<int, Beatmap?>();
        private readonly ConcurrentDictionary<DifficultyAttributeKey, BeatmapDifficultyAttribute[]?> attributeCache = new ConcurrentDictionary<DifficultyAttributeKey, BeatmapDifficultyAttribute[]?>();
        private readonly IReadOnlyDictionary<BlacklistEntry, byte> blacklist;

        private BeatmapStore(IEnumerable<KeyValuePair<BlacklistEntry, byte>> blacklist)
        {
            this.blacklist = new Dictionary<BlacklistEntry, byte>(blacklist);
        }

        /// <summary>
        /// Creates a new <see cref="BeatmapStore"/>.
        /// </summary>
        /// <param name="connection">The <see cref="MySqlConnection"/>.</param>
        /// <param name="transaction">An existing transaction.</param>
        /// <returns>The created <see cref="BeatmapStore"/>.</returns>
        public static async Task<BeatmapStore> CreateAsync(MySqlConnection connection, MySqlTransaction? transaction = null)
        {
            var dbBlacklist = await connection.QueryAsync<PerformanceBlacklistEntry>("SELECT * FROM osu_beatmap_performance_blacklist", transaction: transaction);

            return new BeatmapStore
            (
                dbBlacklist.Select(b => new KeyValuePair<BlacklistEntry, byte>(new BlacklistEntry(b.beatmap_id, b.mode), 1))
            );
        }

        /// <summary>
        /// Retrieves difficulty attributes from the database.
        /// </summary>
        /// <param name="beatmap">The beatmap.</param>
        /// <param name="ruleset">The score's ruleset.</param>
        /// <param name="mods">The score's mods.</param>
        /// <param name="connection">The <see cref="MySqlConnection"/>.</param>
        /// <param name="transaction">An existing transaction.</param>
        /// <returns>The difficulty attributes or <c>null</c> if not existing.</returns>
        public async Task<DifficultyAttributes?> GetDifficultyAttributesAsync(APIBeatmap beatmap, Ruleset ruleset, Mod[] mods, MySqlConnection connection, MySqlTransaction? transaction = null)
        {
            if (use_realtime_difficulty_calculation)
            {
                using var req = new WebRequest(string.Format(beatmap_download_path, beatmap.OnlineID))
                {
                    AllowInsecureRequests = true
                };

                await req.PerformAsync().ConfigureAwait(false);

                if (req.ResponseStream.Length == 0)
                    throw new Exception($"Retrieved zero-length beatmap ({beatmap.OnlineID})!");

                var workingBeatmap = new StreamedWorkingBeatmap(req.ResponseStream);
                var calculator = ruleset.CreateDifficultyCalculator(workingBeatmap);

                return calculator.Calculate(mods);
            }

            BeatmapDifficultyAttribute[]? rawDifficultyAttributes;

            // Todo: We shouldn't be using legacy mods, but this requires difficulty calculation to be performed in-line.
            LegacyMods legacyModValue = LegacyModsHelper.MaskRelevantMods(ruleset.ConvertToLegacyMods(mods), ruleset.RulesetInfo.OnlineID != beatmap.RulesetID, ruleset.RulesetInfo.OnlineID);
            DifficultyAttributeKey key = new DifficultyAttributeKey((uint)beatmap.OnlineID, ruleset.RulesetInfo.OnlineID, (uint)legacyModValue);

            if (!attributeCache.TryGetValue(key, out rawDifficultyAttributes))
            {
                rawDifficultyAttributes = attributeCache[key] = (await connection.QueryAsync<BeatmapDifficultyAttribute>(
                    "SELECT * FROM osu_beatmap_difficulty_attribs WHERE `beatmap_id` = @BeatmapId AND `mode` = @RulesetId AND `mods` = @ModValue", new
                    {
                        key.BeatmapId,
                        key.RulesetId,
                        key.ModValue
                    }, transaction: transaction)).ToArray();
            }

            if (rawDifficultyAttributes == null || rawDifficultyAttributes.Length == 0)
                return null;

            DifficultyAttributes difficultyAttributes = LegacyRulesetHelper.CreateDifficultyAttributes(ruleset.RulesetInfo.OnlineID);
            difficultyAttributes.FromDatabaseAttributes(rawDifficultyAttributes.ToDictionary(a => (int)a.attrib_id, a => (double)a.value), beatmap);

            return difficultyAttributes;
        }

        /// <summary>
        /// Retrieves a beatmap from the database.
        /// </summary>
        /// <param name="beatmapId">The beatmap's ID.</param>
        /// <param name="connection">The <see cref="MySqlConnection"/>.</param>
        /// <param name="transaction">An existing transaction.</param>
        /// <returns>The retrieved beatmap, or <c>null</c> if not existing.</returns>
        public async Task<Beatmap?> GetBeatmapAsync(int beatmapId, MySqlConnection connection, MySqlTransaction? transaction = null)
        {
            if (beatmapCache.TryGetValue(beatmapId, out var beatmap))
                return beatmap;

            return beatmapCache[beatmapId] = await connection.QuerySingleOrDefaultAsync<Beatmap?>("SELECT * FROM osu_beatmaps WHERE `beatmap_id` = @BeatmapId", new
            {
                BeatmapId = beatmapId
            }, transaction: transaction);
        }

        /// <summary>
        /// Whether performance points may be awarded for the given beatmap and ruleset combination.
        /// </summary>
        /// <param name="beatmap">The beatmap.</param>
        /// <param name="rulesetId">The ruleset.</param>
        public bool IsBeatmapValidForPerformance(Beatmap beatmap, int rulesetId)
        {
            if (blacklist.ContainsKey(new BlacklistEntry(beatmap.beatmap_id, rulesetId)))
                return false;

            switch (beatmap.approved)
            {
                case BeatmapOnlineStatus.Ranked:
                case BeatmapOnlineStatus.Approved:
                    return true;

                default:
                    return false;
            }
        }

        private record struct DifficultyAttributeKey(uint BeatmapId, int RulesetId, uint ModValue);

        private record struct BlacklistEntry(int BeatmapId, int RulesetId);
    }
}
