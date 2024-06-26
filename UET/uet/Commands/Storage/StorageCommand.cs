﻿namespace UET.Commands.Storage
{
    using System.Collections.Generic;
    using System.CommandLine;
    using UET.Commands.Storage.List;
    using UET.Commands.Storage.Purge;

    internal sealed class StorageCommand
    {
        public static Command CreateStorageCommand(HashSet<Command> globalCommands)
        {
            var subcommands = new List<Command>
            {
                StorageListCommand.CreateListCommand(),
                StoragePurgeCommand.CreatePurgeCommand(),
                StorageAutoPurgeCommand.CreateAutoPurgeCommand(),
            };

            var command = new Command("storage", "View or remove storage used by UET.");
            foreach (var subcommand in subcommands)
            {
                globalCommands.Add(subcommand);
                command.AddCommand(subcommand);
            }
            globalCommands.Add(command);
            return command;
        }
    }
}
