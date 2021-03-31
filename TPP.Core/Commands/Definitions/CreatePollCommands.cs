using System.Collections.Generic;
using System.Threading.Tasks;
using TPP.ArgsParsing.Types;
using TPP.Persistence.Repos;

namespace TPP.Core.Commands.Definitions
{
    class CreatePollCommands : ICommandCollection
    {
        public IEnumerable<Command> Commands => new[]
        {
            new Command("poll", StartPoll)
            {
                Aliases = new[] { "poll" },
                Description = "Starts a poll with single choice. " +
                              "Argument: <PollName> <PollCode> <Option1> <Option2> <OptionX> (optional)"
            },

            new Command("multipoll", StartMultiPoll)
            {
                Aliases = new[] { "multipoll" },
                Description = "Starts a poll with multiple choice. " +
                              "Argument: <PollName> <PollCode> <Option1> <Option2> <OptionX> (optional)"
            },
        };

        private readonly IPollRepo _pollRepo;

        public CreatePollCommands(IPollRepo pollRepo)
        {
            _pollRepo = pollRepo;
        }

        public async Task<CommandResult> StartPoll(CommandContext context)
        {
            (string pollName, string pollCode, ManyOf<string> options) = await context.ParseArgs<string, string, ManyOf<string>>();
            if (options.Values.Count < 2) return new CommandResult { Response = "must specify at least 2 options" };

            await _pollRepo.CreatePoll(pollName, pollCode, false, options.Values);
            return new CommandResult { Response = "Single option poll created" };
        }

        public async Task<CommandResult> StartMultiPoll(CommandContext context)
        {
            (string pollName, string pollCode, ManyOf<string> options) = await context.ParseArgs<string, string, ManyOf<string>>();
            if (options.Values.Count < 2) return new CommandResult { Response = "must specify at least 2 options" };

            await _pollRepo.CreatePoll(pollName, pollCode, true, options.Values);
            return new CommandResult { Response = "Multi option poll created" };
        }
    }
}
