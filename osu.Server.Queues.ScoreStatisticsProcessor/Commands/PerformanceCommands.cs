// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Threading;
using System.Threading.Tasks;
using McMaster.Extensions.CommandLineUtils;
using osu.Server.Queues.ScoreStatisticsProcessor.Commands.Performance;

namespace osu.Server.Queues.ScoreStatisticsProcessor.Commands
{
    [Command(Name = "performance", Description = "Runs batch processing on pp scores / user totals.")]
    [Subcommand(typeof(UpdateScoresCommands))]
    [Subcommand(typeof(UpdateUserTotalsCommands))]
    public sealed class PerformanceCommands
    {
        public Task<int> OnExecuteAsync(CommandLineApplication app, CancellationToken cancellationToken)
        {
            app.ShowHelp(false);
            return Task.FromResult(1);
        }
    }
}
